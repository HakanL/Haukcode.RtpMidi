namespace Haukcode.RtpMidi.Journal.Chapters;

/// <summary>
/// Chapter A — Poly Key Pressure (RFC 6295 §A.9).
///
///   Header byte: S | LEN[6:0]
///     LEN = (number of note logs) − 1
///   Each log (2 bytes): S | NOTENUM[6:0] | X | PRESSURE[6:0]
///     X = 0 by default (set to 1 if the command appears before All-Notes-Off /
///     All-Sound-Off; not emitted by this library).
/// </summary>
internal sealed class ChapterACodec : IChapterCodec
{
    private readonly ChannelMidiState _state;

    public ChapterACodec(ChannelMidiState state) => _state = state;

    public char ChapterId => 'A';
    public bool HasData   => _state.HasPolyPressure;

    public byte[] Encode(bool isLastChapterInJournal)
    {
        Span<(byte note, byte pressure)> entries = stackalloc (byte, byte)[128];
        int count = 0;
        ReadOnlySpan<bool> active   = _state.PolyPressureActive;
        ReadOnlySpan<byte> pressure = _state.PolyPressure;
        for (int i = 0; i < 128; i++)
        {
            if (active[i])
                entries[count++] = ((byte)i, pressure[i]);
        }

        // X = 0: bit 7 of byte1 clear for all emitted entries.
        return ChapterListEncoder.EncodeCountMinusOneList(
            isLastChapterInJournal, entries.Slice(0, count), secondByteFlagMask: 0x00);
    }

    public int Decode(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => DecodeStatic(data, channel, recovered);

    /// <summary>
    /// Stateless decoder. LEN codes count-1 (min 1 entry).
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
            byte note     = (byte)(data[1 + i * 2] & 0x7F);
            byte pressure = (byte)(data[1 + i * 2 + 1] & 0x7F);
            recovered.Add([(byte)(0xA0 | (channel & 0x0F)), note, pressure]);
        }

        return required;
    }

    public void Reset() { }
}
