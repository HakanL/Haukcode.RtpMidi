namespace Haukcode.RtpMidi.Journal;

/// <summary>
/// Encode/decode contract for a single RTP-MIDI recovery-journal chapter
/// (RFC 6295 Appendix A/B).
///
/// Each concrete codec owns its mutable state (what has been sent since the
/// last reset) and knows its own wire format. The orchestrator
/// (<see cref="RtpMidiJournal"/>) iterates chapters in TOC order and dispatches
/// uniformly through this interface, rather than unrolling per-chapter calls.
///
/// <para>
/// <b>Design note:</b> <c>Decode</c> is an instance method for uniform dispatch,
/// but decoding does not mutate codec state — the same codec instance can be
/// used both to accumulate outgoing state (via the chapter's own MIDI-input
/// hooks) and to parse incoming journal bytes. A "prototype" codec with no
/// accumulated state is also a valid decoder. Channel codecs honour the
/// <c>channel</c> parameter; system codecs (F, X) ignore it.
/// </para>
/// </summary>
internal interface IChapterCodec
{
    /// <summary>RFC 6295 chapter identifier (e.g. 'P', 'C', 'N', 'F', 'X').</summary>
    char ChapterId { get; }

    /// <summary>
    /// True when this codec has accumulated any outgoing state since the last
    /// <see cref="Reset"/>. Drives the TOC-bit / system-journal flag when
    /// building the enclosing journal.
    /// </summary>
    bool HasData { get; }

    /// <summary>
    /// Encodes the chapter body (no enclosing journal header, no TOC byte).
    /// </summary>
    /// <param name="isLastChapterInJournal">
    /// True when this chapter is the last one present in its enclosing
    /// journal. Drives the per-chapter S bit (RFC 6295 §A.1). Chapters that
    /// use byte-0 bit-7 for a different purpose (notably Chapter N's B bit,
    /// §A.6.1) accept the parameter but may ignore it.
    /// </param>
    byte[] Encode(bool isLastChapterInJournal);

    /// <summary>
    /// Parses this chapter starting at <paramref name="data"/>[0], appends
    /// recovered MIDI messages to <paramref name="recovered"/>, and returns
    /// the number of bytes consumed. Returns -1 on structurally invalid input.
    /// </summary>
    /// <param name="channel">
    /// MIDI channel (0–15) for channel chapters; ignored by system chapters.
    /// </param>
    int Decode(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered);

    /// <summary>Discards all accumulated outgoing state (session reset).</summary>
    void Reset();
}
