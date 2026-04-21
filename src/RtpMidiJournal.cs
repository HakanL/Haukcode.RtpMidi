namespace Haukcode.RtpMidi;

/// <summary>
/// Encoder and decoder for the RTP-MIDI recovery journal (RFC 6295 §4–§5, Appendix A).
///
/// Supported chapters:
///   System journal: Chapter X (SysEx, §A.3), Chapter F (System Common, §A.11)
///   Channel journal: Chapter V (header, §A.1), P (§A.7), C (§A.4), M (§A.1), W (§A.6),
///                    N (§A.5), Q (§A.8), T (§A.9), A (§A.2)
///
/// Wire layout of the journal appended after the MIDI command section:
///
///   [Recovery Journal Header: 3 bytes]  (RFC 6295 §5.1)
///     Byte 0:  S|Y|A|H|TOTCHAN
///       S=1 → system journal present
///       A=1 → channel journals present (TOTCHAN+1 channels follow)
///     Bytes 1-2: checkpoint packet sequence number (big-endian)
///
///   [System Journal (if S=1)]
///     System Journal Header: 1 byte (chapter presence flags)
///       Bit 6: F (Chapter F present)
///       Bit 2: X (Chapter X present)
///     Chapter F (if present): variable length
///     Chapter X (if present): 2-byte header + SysEx data
///
///   [Channel Journals (if A=1, one per active channel)]
///     Channel Journal Header: 3 bytes (RFC 6295 §5 Figure 9)
///       Byte 0: S|CHAN[3:0]|H|LENGTH_HI[1:0]   S=1 on last channel journal
///       Byte 1: LENGTH_LO[7:0]
///       Byte 2: P|C|M|W|N|E|T|A  chapter presence (TOC) flags
///     LENGTH is 10 bits and codes the *total* channel-journal byte count,
///     **including** this 3-byte header (RFC 6295 §A.1 "LENGTH field").
///     Chapters in RFC bit-order: P, C, M, W, N, (E), T, A
///     Chapter E (Note Command Extras) is not currently emitted by this
///     library; the bit position is left reserved.
/// </summary>
internal static class RtpMidiJournal
{
    // Recovery journal header (§5.1) byte 0 flags
    private const byte JournalHeaderSystemPresent  = 0x80; // S
    private const byte JournalHeaderChannelPresent = 0x20; // A

    // System journal header (§5.2) chapter flags
    private const byte SysJournalFlagF = 0x40; // Chapter F (System Common)
    private const byte SysJournalFlagX = 0x04; // Chapter X (SysEx)

    // Keep the old name for backward-compat with existing tests
    private const byte SystemJournalHeaderXOnly = SysJournalFlagX;

    // Chapter X (§A.3) header flags
    private const byte ChapterXLastChapterBit = 0x80;
    private const byte ChapterXCompleteBit    = 0x40;

    // Channel journal TOC byte bit positions, per RFC 6295 §5 Figure 9.
    // Chapter N encodes BOTH NoteOn and NoteOff events (§A.6). Chapter E
    // (Note Command Extras, §A.7) is reserved but not currently emitted.
    private const byte ChapTocP = 0x80; // §A.2 Program Change
    private const byte ChapTocC = 0x40; // §A.3 Control Change
    private const byte ChapTocM = 0x20; // §A.4 Parameter System (RPN/NRPN)
    private const byte ChapTocW = 0x10; // §A.5 Pitch Wheel
    private const byte ChapTocN = 0x08; // §A.6 Note (On + Off)
    private const byte ChapTocE = 0x04; // §A.7 Note Command Extras (reserved)
    private const byte ChapTocT = 0x02; // §A.8 Channel Aftertouch
    private const byte ChapTocA = 0x01; // §A.9 Poly Key Pressure

    // -------------------------------------------------------------------------
    // Legacy API — Chapter X only (unchanged, existing callers still work)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Encodes a recovery journal containing only Chapter X (SysEx).
    /// </summary>
    /// <param name="checkpointSeqNum">
    /// Sequence number of the packet whose SysEx events this journal covers.
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

        // Variable-length chapters precede chapter X; skip them so we can locate X.
        // We navigate by walking through the system journal using the F chapter first,
        // then finding Chapter X.
        int pos = 4; // position of system journal header — we already read it

        // Chapter F (bit 6 of system journal header) precedes Chapter X.
        if ((sysJournalHeader & SysJournalFlagF) != 0)
        {
            // Skip Chapter F: consume its variable-length body
            if (data.Length <= pos) return true;
            int consumed = GetChapterFSize(data[pos..]);
            if (consumed < 0) return true;
            pos += consumed;
        }

        // Now pos points at Chapter X
        if (data.Length < pos + 2)
            return false;

        int xLen = ((data[pos] & 0x3F) << 8) | data[pos + 1];

        if (data.Length < pos + 2 + xLen)
            return false;

        sysExPayload = data.Slice(pos + 2, xLen).ToArray();
        return true;
    }

    // -------------------------------------------------------------------------
    // Full journal encode (system + channel chapters)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Encodes a complete recovery journal combining system chapters (F, X) and
    /// channel chapters (V+P+C+M+W+N+Q+T+A) for all channels that have accumulated state.
    /// </summary>
    /// <param name="checkpointSeqNum">Checkpoint packet sequence number.</param>
    /// <param name="lastSysExPayload">
    /// The most-recent SysEx payload to encode in Chapter X, or <c>null</c> if none.
    /// </param>
    /// <param name="channelStates">
    /// Array of 16 per-channel states (index = MIDI channel 0–15).
    /// May be <c>null</c>; channels with no accumulated data are skipped.
    /// </param>
    /// <param name="systemState">
    /// System-level MIDI state for Chapter F, or <c>null</c> if not applicable.
    /// </param>
    /// <returns>
    /// Journal bytes ready to be appended after the MIDI command section,
    /// or an empty array if there is nothing to journal.
    /// </returns>
    public static byte[] EncodeFullJournal(
        ushort             checkpointSeqNum,
        byte[]?            lastSysExPayload,
        ChannelMidiState[] channelStates,
        SystemMidiState?   systemState)
    {
        bool hasSysEx    = lastSysExPayload != null;
        bool hasSysF     = systemState?.HasAnyData == true;
        bool hasSystem   = hasSysEx || hasSysF;

        // Collect active channels (those with any journal data)
        var activeChannels = new List<(byte ch, ChannelMidiState state)>(16);
        for (byte ch = 0; ch < 16; ch++)
        {
            if (channelStates[ch].HasAnyData)
                activeChannels.Add((ch, channelStates[ch]));
        }

        bool hasChannels = activeChannels.Count > 0;

        if (!hasSystem && !hasChannels)
            return Array.Empty<byte>();

        // Build system journal body
        byte[]? sysJournalBody = null;
        if (hasSystem)
            sysJournalBody = BuildSystemJournalBody(hasSysF, hasSysEx, systemState, lastSysExPayload);

        // Build channel journal bodies
        byte[][]? chanJournalBodies = null;
        if (hasChannels)
        {
            chanJournalBodies = new byte[activeChannels.Count][];
            for (int i = 0; i < activeChannels.Count; i++)
            {
                bool isLast = i == activeChannels.Count - 1;
                chanJournalBodies[i] = BuildChannelJournal(
                    activeChannels[i].ch, activeChannels[i].state, isLast);
            }
        }

        // Total size: 3 (journal header) + system journal body + all channel journal bodies
        int totalSize = 3;
        if (sysJournalBody   != null) totalSize += sysJournalBody.Length;
        if (chanJournalBodies != null)
            foreach (var cj in chanJournalBodies) totalSize += cj.Length;

        var buf = new byte[totalSize];

        // Journal header (3 bytes, §5.1):
        //   Byte 0: S|Y|A|H|TOTCHAN
        byte headerByte0 = 0;
        if (hasSystem)   headerByte0 |= 0x80; // S
        if (hasChannels) headerByte0 |= 0x20; // A
        if (hasChannels) headerByte0 |= (byte)((activeChannels.Count - 1) & 0x0F); // TOTCHAN
        buf[0] = headerByte0;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(1), checkpointSeqNum);

        int offset = 3;
        if (sysJournalBody != null)
        {
            sysJournalBody.CopyTo(buf, offset);
            offset += sysJournalBody.Length;
        }

        if (chanJournalBodies != null)
        {
            foreach (var cj in chanJournalBodies)
            {
                cj.CopyTo(buf, offset);
                offset += cj.Length;
            }
        }

        return buf;
    }

    // -------------------------------------------------------------------------
    // Full journal decode (all chapters → recovered MIDI messages)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses a complete recovery journal and returns all recovered MIDI messages.
    /// </summary>
    /// <param name="data">Raw journal bytes from a received RTP-MIDI packet.</param>
    /// <param name="checkpointSeqNum">Decoded checkpoint packet sequence number.</param>
    /// <param name="recoveredMessages">
    /// List of recovered MIDI byte arrays in recovery order, or <c>null</c> if
    /// parsing succeeded but there is nothing to recover.
    /// </param>
    /// <returns>
    /// <c>true</c> if the journal header was parsed successfully;
    /// <c>false</c> if the data is too short or structurally invalid.
    /// </returns>
    public static bool TryParseFullJournal(
        ReadOnlySpan<byte> data,
        out ushort checkpointSeqNum,
        out List<byte[]>? recoveredMessages)
    {
        checkpointSeqNum  = 0;
        recoveredMessages = null;

        if (data.Length < 3) return false;

        byte hdr0              = data[0];
        bool systemPresent     = (hdr0 & 0x80) != 0;
        bool channelsPresent   = (hdr0 & 0x20) != 0;
        int  totalChan         = (hdr0 & 0x0F) + 1; // TOTCHAN+1 channel journals

        checkpointSeqNum = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);

        int pos = 3;
        var recovered = new List<byte[]>();

        // ── System journal ──────────────────────────────────────────────────
        if (systemPresent)
        {
            if (data.Length <= pos) return false;
            byte sysHdr = data[pos++];

            if ((sysHdr & SysJournalFlagF) != 0)
            {
                // Chapter F
                int consumed = SystemMidiState.DecodeChapterF(data[pos..], recovered);
                if (consumed < 0) return false;
                pos += consumed;
            }

            if ((sysHdr & SysJournalFlagX) != 0)
            {
                // Chapter X
                if (data.Length < pos + 2) return false;
                int xLen = ((data[pos] & 0x3F) << 8) | data[pos + 1];
                pos += 2;
                if (data.Length < pos + xLen) return false;
                recovered.Add(data.Slice(pos, xLen).ToArray());
                pos += xLen;
            }
        }

        // ── Channel journals ────────────────────────────────────────────────
        if (channelsPresent)
        {
            for (int ci = 0; ci < totalChan; ci++)
            {
                // Channel journal header (3 bytes, RFC 6295 §5 Figure 9).
                if (data.Length < pos + 3) return false;

                byte vb0  = data[pos];
                byte vb1  = data[pos + 1];
                byte vb2  = data[pos + 2];

                byte channel = (byte)((vb0 >> 3) & 0x0F);
                // LENGTH is the *total* length including the 3-byte header.
                int totalLen = ((vb0 & 0x03) << 8) | vb1;
                if (totalLen < 3) return false;
                if (data.Length < pos + totalLen) return false;

                // Chapter data follows the 3-byte header.
                int chapLen = totalLen - 3;
                var chapData = data.Slice(pos + 3, chapLen);
                pos += totalLen;

                int cpos = 0;

                if ((vb2 & ChapTocP) != 0)
                {
                    int n = ChannelMidiState.DecodeChapterP(chapData[cpos..], channel, recovered);
                    if (n < 0) break;
                    cpos += n;
                }

                if ((vb2 & ChapTocC) != 0)
                {
                    int n = ChannelMidiState.DecodeChapterC(chapData[cpos..], channel, recovered);
                    if (n < 0) break;
                    cpos += n;
                }

                if ((vb2 & ChapTocM) != 0)
                {
                    int n = ChannelMidiState.DecodeChapterM(chapData[cpos..], channel, recovered);
                    if (n < 0) break;
                    cpos += n;
                }

                if ((vb2 & ChapTocW) != 0)
                {
                    int n = ChannelMidiState.DecodeChapterW(chapData[cpos..], channel, recovered);
                    if (n < 0) break;
                    cpos += n;
                }

                if ((vb2 & ChapTocN) != 0)
                {
                    int n = ChannelMidiState.DecodeChapterN(chapData[cpos..], channel, recovered);
                    if (n < 0) break;
                    cpos += n;
                }

                // Chapter E (§A.7) is not emitted by this library and not
                // decoded for recovery. If a remote sets the E bit we skip the
                // remaining chapters of THIS channel journal (T and A cannot be
                // safely parsed without knowing Chapter E's size) but MUST
                // continue processing subsequent channel journals — `pos` has
                // already been advanced by `totalLen` so the next ci iteration
                // starts at the correct offset.  A `break` here would silently
                // drop all remaining channel journals, which is a correctness bug.
                if ((vb2 & ChapTocE) != 0)
                {
                    continue;
                }

                if ((vb2 & ChapTocT) != 0)
                {
                    int n = ChannelMidiState.DecodeChapterT(chapData[cpos..], channel, recovered);
                    if (n < 0) break;
                    cpos += n;
                }

                if ((vb2 & ChapTocA) != 0)
                {
                    int n = ChannelMidiState.DecodeChapterA(chapData[cpos..], channel, recovered);
                    if (n < 0) break;
                }
            }
        }

        recoveredMessages = recovered.Count > 0 ? recovered : null;
        return true;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the system journal body (system journal header byte + chapters F and/or X).
    /// </summary>
    private static byte[] BuildSystemJournalBody(
        bool             hasSysF,
        bool             hasSysEx,
        SystemMidiState? systemState,
        byte[]?          lastSysExPayload)
    {
        // Chapter F data (if present)
        byte[]? chapterFBytes = hasSysF ? systemState!.EncodeChapterF(isLast: !hasSysEx) : null;

        // Chapter X data (if present)
        byte[]? chapterXBytes = null;
        if (hasSysEx && lastSysExPayload != null)
        {
            int xLen       = lastSysExPayload.Length;
            bool isComplete = xLen > 0 && lastSysExPayload[xLen - 1] == 0xF7;
            chapterXBytes = new byte[2 + xLen];
            chapterXBytes[0] = (byte)(ChapterXLastChapterBit
                                    | (isComplete ? ChapterXCompleteBit : 0)
                                    | ((xLen >> 8) & 0x3F));
            chapterXBytes[1] = (byte)(xLen & 0xFF);
            lastSysExPayload.CopyTo(chapterXBytes, 2);
        }

        // System journal header byte
        byte sysHdr = 0;
        if (hasSysF)  sysHdr |= SysJournalFlagF;
        if (hasSysEx) sysHdr |= SysJournalFlagX;

        int bodySize = 1 // system journal header
            + (chapterFBytes != null ? chapterFBytes.Length : 0)
            + (chapterXBytes != null ? chapterXBytes.Length : 0);

        var body = new byte[bodySize];
        body[0] = sysHdr;
        int off = 1;
        if (chapterFBytes != null) { chapterFBytes.CopyTo(body, off); off += chapterFBytes.Length; }
        if (chapterXBytes != null) { chapterXBytes.CopyTo(body, off); }

        return body;
    }

    /// <summary>
    /// Builds a single channel journal (Chapter V header + all active chapters).
    /// </summary>
    private static byte[] BuildChannelJournal(byte channel, ChannelMidiState state, bool isLastChannel)
    {
        // Determine which chapters are present and encode them. Chapter N now
        // carries both NoteOn and NoteOff per RFC 6295 §A.6 (the old separate
        // Chapter Q for NoteOn is not part of the RFC and has been removed).
        bool hasP = state.HasProgram;
        bool hasC = state.HasControlChange;
        bool hasM = state.HasParameterSystem;
        bool hasW = state.HasPitchWheel;
        bool hasN = state.HasNotes;
        bool hasT = state.HasChannelPressure;
        bool hasA = state.HasPolyPressure;

        // Chapter order in the wire format (P, C, M, W, N, E, T, A) matches
        // the TOC bit order. E is reserved; we never emit it.
        bool[] present = [hasP, hasC, hasM, hasW, hasN, hasT, hasA];
        int lastIdx = -1;
        for (int i = present.Length - 1; i >= 0; i--) { if (present[i]) { lastIdx = i; break; } }

        byte[]? pBytes = hasP ? state.EncodeChapterP(isLast: lastIdx == 0) : null;
        byte[]? cBytes = hasC ? state.EncodeChapterC(isLast: lastIdx == 1) : null;
        byte[]? mBytes = hasM ? state.EncodeChapterM(isLast: lastIdx == 2) : null;
        byte[]? wBytes = hasW ? state.EncodeChapterW(isLast: lastIdx == 3) : null;
        byte[]? nBytes = hasN ? state.EncodeChapterN(isLast: lastIdx == 4) : null;
        byte[]? tBytes = hasT ? state.EncodeChapterT(isLast: lastIdx == 5) : null;
        byte[]? aBytes = hasA ? state.EncodeChapterA(isLast: lastIdx == 6) : null;

        // Chapter payload size (header is 3 bytes, added below).
        int chapLen = 0;
        if (pBytes != null) chapLen += pBytes.Length;
        if (cBytes != null) chapLen += cBytes.Length;
        if (mBytes != null) chapLen += mBytes.Length;
        if (wBytes != null) chapLen += wBytes.Length;
        if (nBytes != null) chapLen += nBytes.Length;
        if (tBytes != null) chapLen += tBytes.Length;
        if (aBytes != null) chapLen += aBytes.Length;

        // TOC presence flags (byte 2 of the channel journal header).
        byte presence = 0;
        if (hasP) presence |= ChapTocP;
        if (hasC) presence |= ChapTocC;
        if (hasM) presence |= ChapTocM;
        if (hasW) presence |= ChapTocW;
        if (hasN) presence |= ChapTocN;
        if (hasT) presence |= ChapTocT;
        if (hasA) presence |= ChapTocA;

        // Channel journal header (3 bytes, RFC 6295 §5 Figure 9):
        //   Byte 0: S | CHAN[3:0] | H(=0) | LENGTH[9:8]
        //   Byte 1: LENGTH[7:0]
        //   Byte 2: TOC (P|C|M|W|N|E|T|A)
        // LENGTH is the **total** journal byte count *including* the 3-byte
        // header (RFC 6295 §A.1 "LENGTH field" definition).
        int totalLen = 3 + chapLen;
        var journal = new byte[totalLen];
        journal[0] = (byte)(
            (isLastChannel ? 0x80 : 0) |
            ((channel & 0x0F) << 3) |
            ((totalLen >> 8) & 0x03));
        journal[1] = (byte)(totalLen & 0xFF);
        journal[2] = presence;

        int off = 3;
        if (pBytes != null) { pBytes.CopyTo(journal, off); off += pBytes.Length; }
        if (cBytes != null) { cBytes.CopyTo(journal, off); off += cBytes.Length; }
        if (mBytes != null) { mBytes.CopyTo(journal, off); off += mBytes.Length; }
        if (wBytes != null) { wBytes.CopyTo(journal, off); off += wBytes.Length; }
        if (nBytes != null) { nBytes.CopyTo(journal, off); off += nBytes.Length; }
        if (tBytes != null) { tBytes.CopyTo(journal, off); off += tBytes.Length; }
        if (aBytes != null) { aBytes.CopyTo(journal, off); }

        return journal;
    }

    /// <summary>
    /// Returns the total byte size of a Chapter F block starting at <paramref name="data"/>,
    /// or -1 if the data is too short.
    /// </summary>
    private static int GetChapterFSize(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return -1;
        byte hdr = data[0];
        bool hasD = (hdr & 0x40) != 0;
        bool hasV = (hdr & 0x20) != 0;
        bool hasQ = (hdr & 0x10) != 0;
        int size = 1 + (hasD ? 1 : 0) + (hasV ? 2 : 0) + (hasQ ? 1 : 0);
        return data.Length >= size ? size : -1;
    }
}
