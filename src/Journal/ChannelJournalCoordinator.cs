using Haukcode.RtpMidi.Journal.Chapters;

namespace Haukcode.RtpMidi.Journal;

/// <summary>
/// Per-channel view that exposes all channel-scoped chapter codecs in TOC wire
/// order (P, C, M, W, N, T, A). <see cref="RtpMidiJournal"/> iterates this list
/// to build and parse channel journals generically rather than unrolling a
/// chapter-by-chapter call sequence.
///
/// The coordinator wraps a <see cref="ChannelMidiState"/> during the transition
/// to the new codec-based architecture; after state extraction completes the
/// coordinator will own the per-chapter state classes directly.
/// </summary>
internal sealed class ChannelJournalCoordinator
{
    private readonly ChannelMidiState _state;
    private readonly IChapterCodec[]  _chapters;

    public ChannelJournalCoordinator(ChannelMidiState state)
    {
        _state = state;
        // TOC wire order (RFC 6295 §5 Figure 9): P, C, M, W, N, T, A.
        // Chapter E (reserved) is not emitted.
        _chapters =
        [
            new ChapterPCodec(state),
            new ChapterCCodec(state),
            new ChapterMCodec(state.ParamLog),
            new ChapterWCodec(state),
            new ChapterNCodec(state.NoteState),
            new ChapterTCodec(state),
            new ChapterACodec(state),
        ];
    }

    /// <summary>The underlying state (temporary accessor during refactor transition).</summary>
    public ChannelMidiState State => _state;

    /// <summary>Chapter codecs in TOC wire order.</summary>
    public IReadOnlyList<IChapterCodec> Chapters => _chapters;

    /// <summary>True when any chapter has accumulated outgoing state.</summary>
    public bool HasAnyData => _state.HasAnyData;

    /// <summary>
    /// Dispatches a single MIDI message to the per-chapter state accumulators.
    /// Kept delegating to <see cref="ChannelMidiState.ProcessMidi"/> for now;
    /// becomes the primary entry point once state is fully extracted.
    /// </summary>
    public void ProcessMidi(ReadOnlySpan<byte> midiData) => _state.ProcessMidi(midiData);

    /// <summary>Discards all accumulated state across every chapter.</summary>
    public void Reset() => _state.Reset();

    /// <summary>
    /// Parses a channel journal's chapter section (the bytes AFTER the 3-byte
    /// channel-journal header) driven by the TOC presence byte.
    ///
    /// Returns bytes consumed on success; stops at the first malformed chapter
    /// or at Chapter E (which is reserved and can't be safely skipped without
    /// knowing its size).
    /// </summary>
    /// <param name="chapData">Chapter section, starting at the first chapter byte.</param>
    /// <param name="tocByte">TOC presence byte from the channel-journal header.</param>
    /// <param name="channel">MIDI channel (0–15) for the recovered messages.</param>
    /// <param name="recovered">Destination list for recovered MIDI messages.</param>
    public static void DecodeChapters(
        ReadOnlySpan<byte> chapData,
        byte               tocByte,
        byte               channel,
        List<byte[]>       recovered)
    {
        int cpos = 0;

        // TOC wire order per §5 Figure 9: P, C, M, W, N, E, T, A.
        // Chapter E is reserved; if its bit is set we cannot parse T or A
        // without knowing E's size, so we stop processing this channel journal.
        foreach (var (bit, chapId) in s_channelTocOrder)
        {
            if ((tocByte & bit) == 0) continue;
            if (chapId == 'E') return; // reserved — stop here
            int n = DecodeChannelChapter(chapId, chapData[cpos..], channel, recovered);
            if (n < 0) return;
            cpos += n;
        }
    }

    /// <summary>
    /// TOC wire order with the Chapter E "reserved" slot included. Built from
    /// the registry to keep the ordering authoritative.
    /// </summary>
    private static readonly (byte bit, char id)[] s_channelTocOrder = new[]
    {
        (RfcChapterRegistry.TocP, 'P'),
        (RfcChapterRegistry.TocC, 'C'),
        (RfcChapterRegistry.TocM, 'M'),
        (RfcChapterRegistry.TocW, 'W'),
        (RfcChapterRegistry.TocN, 'N'),
        (RfcChapterRegistry.TocE, 'E'),
        (RfcChapterRegistry.TocT, 'T'),
        (RfcChapterRegistry.TocA, 'A'),
    };

    private static int DecodeChannelChapter(char chapId, ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => chapId switch
        {
            'P' => ChapterPCodec.DecodeStatic(data, channel, recovered),
            'C' => ChapterCCodec.DecodeStatic(data, channel, recovered),
            'M' => ChapterMCodec.DecodeStatic(data, channel, recovered),
            'W' => ChapterWCodec.DecodeStatic(data, channel, recovered),
            'N' => ChapterNCodec.DecodeStatic(data, channel, recovered),
            'T' => ChapterTCodec.DecodeStatic(data, channel, recovered),
            'A' => ChapterACodec.DecodeStatic(data, channel, recovered),
            _   => -1,
        };
}
