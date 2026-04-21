namespace Haukcode.RtpMidi.Journal.Chapters;

/// <summary>
/// Chapter C — Control Change (RFC 6295 §A.3).
///
///   Header byte: S | LEN[6:0]
///     LEN = (number of controller log entries) − 1  (7-bit, max 128 entries)
///   Each log entry (2 bytes): S | NUMBER[6:0] | A | VALUE[6:0]
///     NUMBER = controller number (0–127)
///     A = 0 → "value tool"; VALUE codes the controller data value
///         (A = 1 "toggle / count tool" is defined by the RFC but not emitted here)
///
/// The list must contain at least one entry; if no controllers are active,
/// the chapter MUST NOT be present at all (the orchestrator honors
/// <see cref="HasData"/> to enforce this).
/// </summary>
internal sealed class ChapterCCodec : IChapterCodec
{
    private readonly ChannelMidiState _state;

    public ChapterCCodec(ChannelMidiState state) => _state = state;

    public char ChapterId => 'C';
    public bool HasData   => _state.HasControlChange;

    public byte[] Encode(bool isLastChapterInJournal)
    {
        // Collect active controllers (up to 128 — 7-bit LEN field codes count-1)
        Span<(byte cc, byte val)> entries = stackalloc (byte, byte)[128];
        int count = 0;
        ReadOnlySpan<bool> active = _state.CcActive;
        ReadOnlySpan<byte> values = _state.CcValues;
        for (int i = 0; i < 128; i++)
        {
            if (active[i])
                entries[count++] = ((byte)i, values[i]);
        }

        // A = 0 (value tool): bit 7 of byte1 clear for all emitted entries.
        return ChapterListEncoder.EncodeCountMinusOneList(
            isLastChapterInJournal, entries.Slice(0, count), secondByteFlagMask: 0x00);
    }

    public int Decode(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => DecodeStatic(data, channel, recovered);

    /// <summary>
    /// Stateless decoder. LEN codes count-1 (min 1 entry). Returns bytes
    /// consumed (1 + count×2) or -1 on short input.
    /// </summary>
    public static int DecodeStatic(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.IsEmpty) return -1;

        byte header = data[0];
        int count = (header & 0x7F) + 1;
        int required = 1 + count * 2;
        if (data.Length < required) return -1;

        for (int i = 0; i < count; i++)
        {
            byte cc  = (byte)(data[1 + i * 2] & 0x7F);
            byte val = (byte)(data[1 + i * 2 + 1] & 0x7F);
            // The A (alt-tool) bit is ignored for recovery: emit a plain CC
            // regardless of whether the sender used the value tool or the
            // toggle/count tool.
            recovered.Add([(byte)(0xB0 | (channel & 0x0F)), cc, val]);
        }

        return required;
    }

    public void Reset() { }
}
