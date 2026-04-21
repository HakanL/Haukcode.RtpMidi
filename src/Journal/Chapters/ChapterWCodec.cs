namespace Haukcode.RtpMidi.Journal.Chapters;

/// <summary>
/// Chapter W — Pitch Wheel (RFC 6295 §A.5). Fixed 2 bytes.
///
///   Byte 0:  S | FIRST[6:0]    FIRST  = LSB of the most-recent pitch wheel data
///   Byte 1:  R | SECOND[6:0]   SECOND = MSB. R is reserved (MUST be 0).
/// </summary>
internal sealed class ChapterWCodec : IChapterCodec
{
    private readonly ChannelMidiState _state;

    public ChapterWCodec(ChannelMidiState state) => _state = state;

    public char ChapterId => 'W';
    public bool HasData   => _state.HasPitchWheel;

    public byte[] Encode(bool isLastChapterInJournal)
    {
        return
        [
            (byte)((isLastChapterInJournal ? 0x80 : 0) | (_state.PitchLsb & 0x7F)),
            (byte)(_state.PitchMsb & 0x7F),  // R = 0; SECOND in low 7 bits
        ];
    }

    public int Decode(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => DecodeStatic(data, channel, recovered);

    public static int DecodeStatic(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.Length < 2) return -1;

        byte lsb = (byte)(data[0] & 0x7F);
        byte msb = (byte)(data[1] & 0x7F); // R bit in position 7 is ignored
        recovered.Add([(byte)(0xE0 | (channel & 0x0F)), lsb, msb]);

        return 2;
    }

    public void Reset() { }
}
