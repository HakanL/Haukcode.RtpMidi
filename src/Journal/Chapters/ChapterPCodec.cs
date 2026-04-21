namespace Haukcode.RtpMidi.Journal.Chapters;

/// <summary>
/// Chapter P — Program Change (RFC 6295 §A.2). Fixed 3 bytes.
///
///   Byte 0:  S | PROGRAM[6:0]
///   Byte 1:  B | BANK-MSB[6:0]   B = 1 when bank coarse (CC 0) was sent before this PC
///   Byte 2:  X | BANK-LSB[6:0]   X = 1 when bank fine (CC 32) was sent between coarse and PC
/// </summary>
internal sealed class ChapterPCodec : IChapterCodec
{
    private readonly ChannelMidiState _state;

    public ChapterPCodec(ChannelMidiState state) => _state = state;

    public char ChapterId => 'P';
    public bool HasData   => _state.HasProgram;

    public byte[] Encode(bool isLastChapterInJournal)
    {
        byte prog = (byte)(_state.Program    & 0x7F);
        byte bc   = (byte)(_state.BankCoarse & 0x7F);
        byte bf   = (byte)(_state.BankFine   & 0x7F);

        return
        [
            (byte)((isLastChapterInJournal ? 0x80 : 0) | prog),
            (byte)((_state.HasBankCoarse   ? 0x80 : 0) | bc),
            (byte)((_state.HasBankFine     ? 0x80 : 0) | bf),
        ];
    }

    public int Decode(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => DecodeStatic(data, channel, recovered);

    /// <summary>
    /// Stateless decoder. Returns 3 on success or -1 on short input.
    /// </summary>
    public static int DecodeStatic(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.Length < 3) return -1;

        byte b0 = data[0], b1 = data[1], b2 = data[2];
        byte prog  = (byte)(b0 & 0x7F);
        bool hasBC = (b1 & 0x80) != 0;
        byte bc    = (byte)(b1 & 0x7F);
        bool hasBF = (b2 & 0x80) != 0;
        byte bf    = (byte)(b2 & 0x7F);

        if (hasBC) recovered.Add([(byte)(0xB0 | (channel & 0x0F)),  0, bc]);
        if (hasBF) recovered.Add([(byte)(0xB0 | (channel & 0x0F)), 32, bf]);
        recovered.Add([(byte)(0xC0 | (channel & 0x0F)), prog]);

        return 3;
    }

    public void Reset()
    {
        // State lifecycle is managed by ChannelMidiState.Reset(); this codec
        // is a thin view. Explicit per-codec reset is a no-op here.
    }
}
