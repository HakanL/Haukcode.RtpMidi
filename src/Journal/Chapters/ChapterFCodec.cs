namespace Haukcode.RtpMidi.Journal.Chapters;

/// <summary>
/// Chapter F — System Common (RFC 6295 §B.3). Variable length.
///
///   Byte 0:  S | D(=0) | V | Q | F | X(=0) | P(=0) | C(=0)
///     D=1 → MTC Quarter Frame data byte follows (1 byte)
///     V=1 → Song Position Pointer follows (2 bytes: LSB, MSB)
///     Q=1 → Song Select follows (1 byte)
///
/// System-scoped: the <c>channel</c> parameter on <see cref="Decode"/> is
/// accepted for interface uniformity and ignored.
/// </summary>
internal sealed class ChapterFCodec : IChapterCodec
{
    private readonly SystemMidiState _state;

    public ChapterFCodec(SystemMidiState state) => _state = state;

    public char ChapterId => 'F';
    public bool HasData   => _state.HasAnyData;

    public byte[] Encode(bool isLastChapterInJournal)
    {
        int size = 1
            + (_state.HasTimeCode     ? 1 : 0)
            + (_state.HasSongPosition ? 2 : 0)
            + (_state.HasSongSelect   ? 1 : 0);

        var buf = new byte[size];
        buf[0] = (byte)(
            (isLastChapterInJournal  ? 0x80 : 0) |
            (_state.HasTimeCode      ? 0x40 : 0) |  // D flag
            (_state.HasSongPosition  ? 0x20 : 0) |  // V flag
            (_state.HasSongSelect    ? 0x10 : 0));  // Q flag

        int offset = 1;
        if (_state.HasTimeCode)     { buf[offset++] = _state.TimeCode; }
        if (_state.HasSongPosition) { buf[offset++] = _state.SongPositionLsb;
                                      buf[offset++] = _state.SongPositionMsb; }
        if (_state.HasSongSelect)   { buf[offset++] = _state.SongSelect; }

        return buf;
    }

    public int Decode(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => DecodeStatic(data, recovered);

    /// <summary>
    /// Stateless decoder. Returns bytes consumed on success or -1 on
    /// structurally invalid input. The channel parameter is not applicable
    /// to system-common messages (0xF1/F2/F3 carry no channel nibble).
    /// </summary>
    public static int DecodeStatic(ReadOnlySpan<byte> data, List<byte[]> recovered)
    {
        if (data.IsEmpty) return -1;

        byte header = data[0];
        bool hasD = (header & 0x40) != 0;
        bool hasV = (header & 0x20) != 0;
        bool hasQ = (header & 0x10) != 0;

        int required = 1
            + (hasD ? 1 : 0)
            + (hasV ? 2 : 0)
            + (hasQ ? 1 : 0);

        if (data.Length < required) return -1;

        int offset = 1;
        if (hasD) { recovered.Add([0xF1, (byte)(data[offset++] & 0x7F)]); }
        if (hasV) { recovered.Add([0xF2, data[offset++], data[offset++]]); }
        if (hasQ) { recovered.Add([0xF3, (byte)(data[offset++] & 0x7F)]); }

        return required;
    }

    /// <summary>
    /// Returns the total byte size of a Chapter F block starting at
    /// <paramref name="data"/>[0], or -1 if the data is too short. Used by
    /// parsers that need to skip over Chapter F without emitting recovered
    /// MIDI (e.g. when locating Chapter X in a legacy-compat code path).
    /// </summary>
    public static int GetSize(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return -1;
        byte hdr = data[0];
        bool hasD = (hdr & 0x40) != 0;
        bool hasV = (hdr & 0x20) != 0;
        bool hasQ = (hdr & 0x10) != 0;
        int size = 1 + (hasD ? 1 : 0) + (hasV ? 2 : 0) + (hasQ ? 1 : 0);
        return data.Length >= size ? size : -1;
    }

    public void Reset() { }
}
