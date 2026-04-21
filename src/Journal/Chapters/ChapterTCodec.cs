namespace Haukcode.RtpMidi.Journal.Chapters;

/// <summary>
/// Chapter T — Channel Aftertouch (RFC 6295 §A.8). Fixed 1 byte.
///
///   Byte 0: S | PRESSURE[6:0]
/// </summary>
internal sealed class ChapterTCodec : IChapterCodec
{
    private readonly ChannelMidiState _state;

    public ChapterTCodec(ChannelMidiState state) => _state = state;

    public char ChapterId => 'T';
    public bool HasData   => _state.HasChannelPressure;

    public byte[] Encode(bool isLastChapterInJournal)
    {
        return [(byte)((isLastChapterInJournal ? 0x80 : 0) | (_state.ChannelPressure & 0x7F))];
    }

    public int Decode(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => DecodeStatic(data, channel, recovered);

    public static int DecodeStatic(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.IsEmpty) return -1;

        byte pressure = (byte)(data[0] & 0x7F);
        recovered.Add([(byte)(0xD0 | (channel & 0x0F)), pressure]);

        return 1;
    }

    public void Reset() { }
}
