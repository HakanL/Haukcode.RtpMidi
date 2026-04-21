namespace Haukcode.RtpMidi.Journal.Chapters;

/// <summary>
/// Chapter X — SysEx (RFC 6295 §B.2).
///
///   Byte 0:  S | F | LEN[13:8]   S = last chapter in system journal
///                                F = 1 when SysEx ends with F7 (complete)
///   Byte 1:  LEN[7:0]
///   Bytes 2..(2+LEN-1):  raw SysEx payload including F0/F7 framing
///
/// Unlike the channel chapters, Chapter X's state is a single most-recent
/// SysEx payload (not accumulated bitfields). Set <see cref="SysExPayload"/>
/// via the constructor or <see cref="SetPayload"/> before calling
/// <see cref="Encode"/>; leave it <c>null</c> to indicate "no SysEx".
/// </summary>
internal sealed class ChapterXCodec : IChapterCodec
{
    private const byte LastChapterBit = 0x80;
    private const byte CompleteBit    = 0x40;

    private byte[]? _payload;

    public ChapterXCodec() { }

    public ChapterXCodec(byte[]? payload)
    {
        _payload = payload;
    }

    public char ChapterId => 'X';
    public bool HasData   => _payload != null;

    /// <summary>
    /// The SysEx payload to emit on the next <see cref="Encode"/>, or
    /// <c>null</c> to skip Chapter X.
    /// </summary>
    public byte[]? SysExPayload
    {
        get => _payload;
        set => _payload = value;
    }

    public void SetPayload(byte[]? payload) => _payload = payload;

    public byte[] Encode(bool isLastChapterInJournal)
    {
        byte[] payload = _payload ?? Array.Empty<byte>();
        int len = payload.Length;

        bool isComplete = len > 0 && payload[len - 1] == 0xF7;

        var buf = new byte[2 + len];
        // S bit on Chapter X: the caller passes the same isLastChapterInJournal
        // signal as other chapters. Historically the standalone
        // RtpMidiJournal.EncodeChapterX() legacy API hard-coded S=1 because it
        // emitted ONLY Chapter X; new callers should pass isLast appropriately.
        buf[0] = (byte)(
            (isLastChapterInJournal ? LastChapterBit : 0) |
            (isComplete             ? CompleteBit    : 0) |
            ((len >> 8) & 0x3F));
        buf[1] = (byte)(len & 0xFF);
        payload.CopyTo(buf, 2);

        return buf;
    }

    public int Decode(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => DecodeStatic(data, recovered);

    /// <summary>
    /// Parses a Chapter X block starting at <paramref name="data"/>[0] and
    /// appends the raw SysEx bytes to <paramref name="recovered"/>. Returns
    /// bytes consumed (2 + LEN) or -1 on short input.
    /// </summary>
    public static int DecodeStatic(ReadOnlySpan<byte> data, List<byte[]> recovered)
    {
        if (data.Length < 2) return -1;

        int len = ((data[0] & 0x3F) << 8) | data[1];
        int required = 2 + len;
        if (data.Length < required) return -1;

        if (len > 0)
            recovered.Add(data.Slice(2, len).ToArray());

        return required;
    }

    /// <summary>
    /// Returns the raw LEN value from the 2-byte Chapter X header, or -1 if
    /// the data is too short. Used by the legacy <c>TryParseChapterX</c> API
    /// which wants the payload span rather than copying into a list.
    /// </summary>
    public static int PeekLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return -1;
        return ((data[0] & 0x3F) << 8) | data[1];
    }

    public void Reset() => _payload = null;
}
