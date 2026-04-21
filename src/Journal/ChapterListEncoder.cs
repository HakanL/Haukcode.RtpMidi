namespace Haukcode.RtpMidi.Journal;

/// <summary>
/// Shared encoder for recovery-journal chapters whose wire format is
/// <c>S | LEN[6:0]</c> header followed by LEN+1 two-byte log entries where:
///
///   byte0:  S(last entry) | FIRST[6:0]
///   byte1:  FLAG          | SECOND[6:0]
///
/// This layout is shared by Chapter C (Control Change, §A.3) and Chapter A
/// (Poly Key Pressure, §A.9) of RFC 6295. The meaning of the FLAG bit on
/// byte1 differs per chapter (A-bit for C, X-bit for A) but this library
/// always emits it as 0; the helper accepts a mask so callers that need
/// a non-zero flag keep a single source for the rest of the layout.
///
/// The helper does NOT handle Chapter N's list — Chapter N uses
/// <c>LEN = count</c> (not count-1) and has a distinct 2-byte header.
/// </summary>
internal static class ChapterListEncoder
{
    /// <summary>
    /// Encodes a chapter consisting of a 1-byte header plus
    /// <paramref name="entries"/>.Count two-byte log entries.
    /// </summary>
    /// <param name="isLast">
    /// True when this chapter is the last present in its channel journal,
    /// controlling the header byte's S bit (RFC 6295 §A.1).
    /// </param>
    /// <param name="entries">
    /// Log entries in encode order. Count MUST be at least 1; the empty-list
    /// case is illegal per RFC 6295 (the chapter must be omitted instead).
    /// </param>
    /// <param name="secondByteFlagMask">
    /// Bit(s) to OR into byte1 of every entry. Defaults to 0 (library never
    /// sets the A or X bits). Low 7 bits are ignored; typical value is 0x80.
    /// </param>
    public static byte[] EncodeCountMinusOneList(
        bool isLast,
        ReadOnlySpan<(byte first, byte second)> entries,
        byte secondByteFlagMask = 0x00)
    {
        int count = entries.Length;
        var buf = new byte[1 + count * 2];

        // Header: S | LEN where LEN = count - 1 (7-bit field, max 128 entries).
        buf[0] = (byte)((isLast ? 0x80 : 0) | ((count - 1) & 0x7F));

        for (int i = 0; i < count; i++)
        {
            bool lastEntry = i == count - 1;
            buf[1 + i * 2]     = (byte)((lastEntry ? 0x80 : 0) | (entries[i].first  & 0x7F));
            buf[1 + i * 2 + 1] = (byte)((secondByteFlagMask    & 0x80) | (entries[i].second & 0x7F));
        }

        return buf;
    }
}
