namespace Haukcode.RtpMidi;

/// <summary>
/// Encoder and decoder for the RTP-MIDI recovery journal (RFC 6295 §4–§5, §A.3).
///
/// This implementation supports Chapter X (System Exclusive) only, which is
/// sufficient to maintain interoperability with Apple CoreMIDI and other strict
/// implementations that send BY on sessions transporting SysEx without a journal.
///
/// Wire layout of the journal appended after the MIDI command section:
///
///   [Recovery Journal Header: 3 bytes]  (RFC 6295 §5.1)
///     Byte 0:  S|Y|A|H|TOTCHAN  — S=1 (system journal), Y=A=H=0, TOTCHAN=0
///     Bytes 1-2: checkpoint packet sequence number (big-endian)
///
///   [System Journal Header: 1 byte]  (RFC 6295 §5.2)
///     Byte 0:  chapter flags — X=1 (bit 2; chapter X present)
///
///   [Chapter X: 2-byte header + SysEx data]  (RFC 6295 §A.3)
///     Byte 0:  S|F|len_hi6   — S=1 (last chapter), F=1 (complete SysEx)
///     Byte 1:  len_lo8
///     Followed by the SysEx bytes (including leading F0 and trailing F7).
/// </summary>
internal static class RtpMidiJournal
{
    // Recovery journal header (§5.1) byte 0: S=1 (system journal present)
    private const byte JournalHeaderSystemPresent = 0x80;

    // System journal header (§5.2) byte: X flag = bit 2
    private const byte SystemJournalHeaderXOnly = 0x04;

    // Chapter X (§A.3) header byte 0 base: S=1 (last chapter in system journal)
    private const byte ChapterXLastChapterBit = 0x80;

    // Chapter X (§A.3) header byte 0 bit: F=1 (SysEx is complete, ends with F7)
    private const byte ChapterXCompleteBit = 0x40;

    // -------------------------------------------------------------------------
    // Encode
    // -------------------------------------------------------------------------

    /// <summary>
    /// Encodes a recovery journal containing only Chapter X (SysEx).
    /// </summary>
    /// <param name="checkpointSeqNum">
    /// Sequence number of the packet whose SysEx events this journal covers.
    /// Receivers use this to decide whether the journal applies to a detected gap.
    /// </param>
    /// <param name="sysExPayload">
    /// Complete SysEx payload including the leading F0 and trailing F7 bytes.
    /// </param>
    /// <returns>Journal bytes ready to be appended after the MIDI command section.</returns>
    public static byte[] EncodeChapterX(ushort checkpointSeqNum, ReadOnlySpan<byte> sysExPayload)
    {
        int len = sysExPayload.Length;

        // 3 (journal header) + 1 (system journal header) + 2 (chapter X header) + len (data)
        var buf = new byte[6 + len];

        // Recovery journal header (3 bytes, §5.1)
        buf[0] = JournalHeaderSystemPresent;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(1), checkpointSeqNum);

        // System journal header (1 byte, §5.2) — only chapter X present
        buf[3] = SystemJournalHeaderXOnly;

        // Chapter X header (2 bytes, §A.3)
        // S=1 (last/only chapter), F=1 if SysEx ends with F7 (complete message)
        bool isComplete = len > 0 && sysExPayload[len - 1] == 0xF7;
        buf[4] = (byte)(ChapterXLastChapterBit
                      | (isComplete ? ChapterXCompleteBit : 0)
                      | ((len >> 8) & 0x3F));
        buf[5] = (byte)(len & 0xFF);

        sysExPayload.CopyTo(buf.AsSpan(6));

        return buf;
    }

    // -------------------------------------------------------------------------
    // Parse
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses a recovery journal and extracts the Chapter X (SysEx) payload if present.
    /// </summary>
    /// <param name="data">The raw journal bytes (everything after the MIDI command section).</param>
    /// <param name="checkpointSeqNum">The checkpoint sequence number from the journal header.</param>
    /// <param name="sysExPayload">
    /// The recovered SysEx bytes, or <c>null</c> if Chapter X is absent.
    /// </param>
    /// <returns>
    /// <c>true</c> if the journal header was parsed successfully (even when Chapter X is absent);
    /// <c>false</c> if the data is too short or structurally invalid.
    /// </returns>
    public static bool TryParseChapterX(
        ReadOnlySpan<byte> data,
        out ushort checkpointSeqNum,
        out byte[]? sysExPayload)
    {
        checkpointSeqNum = 0;
        sysExPayload = null;

        // Need at least 3 bytes for the recovery journal header
        if (data.Length < 3)
            return false;

        bool systemJournalPresent = (data[0] & 0x80) != 0;
        checkpointSeqNum = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);

        if (!systemJournalPresent)
            return true; // no system journal — nothing to extract

        // System journal header (1 byte)
        if (data.Length < 4)
            return true;

        byte sysJournalHeader = data[3];
        bool chapterXPresent = (sysJournalHeader & SystemJournalHeaderXOnly) != 0;

        if (!chapterXPresent)
            return true; // system journal present but no chapter X

        // Variable-length chapters S, D, V, Q, F (bits 7..3) precede chapter X.
        // We only encode journals with chapter X alone, so bail if any precede it.
        bool precedingChapters = (sysJournalHeader & 0xF8) != 0;
        if (precedingChapters)
            return true; // cannot determine chapter X offset — skip silently

        // Chapter X header (2 bytes)
        if (data.Length < 6)
            return false;

        int xLen = ((data[4] & 0x3F) << 8) | data[5];

        if (data.Length < 6 + xLen)
            return false;

        sysExPayload = data.Slice(6, xLen).ToArray();
        return true;
    }
}
