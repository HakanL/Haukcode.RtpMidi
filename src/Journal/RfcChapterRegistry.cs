namespace Haukcode.RtpMidi.Journal;

/// <summary>
/// Single source of truth for RTP-MIDI (RFC 6295) recovery-journal chapters:
/// RFC section, TOC bit, implementation status, and known quirks.
///
/// Used by the journal encoder/decoder to drive chapter dispatch in TOC order,
/// and as a compliance checklist for reviewers investigating spec deviations.
/// </summary>
internal static class RfcChapterRegistry
{
    /// <summary>
    /// Implementation status for a chapter relative to the RFC 6295 normative format.
    /// </summary>
    public enum Status
    {
        /// <summary>Fully implemented per the RFC with full round-trip fidelity.</summary>
        FullyImplemented,

        /// <summary>Reserved by the RFC; not emitted by this library.</summary>
        Reserved,

        /// <summary>Not implemented (neither encoded nor decoded).</summary>
        NotImplemented,
    }

    /// <summary>
    /// Scope of a chapter: part of the channel journal (per-channel, one of 16)
    /// or the system journal (global, one instance).
    /// </summary>
    public enum ChapterScope
    {
        Channel,
        System,
    }

    /// <summary>
    /// Record describing a single RFC 6295 recovery-journal chapter.
    /// </summary>
    /// <param name="Id">Chapter identifier (e.g. 'P', 'C', 'N', 'F', 'X').</param>
    /// <param name="RfcSection">RFC 6295 section reference.</param>
    /// <param name="DisplayName">Human-readable chapter name.</param>
    /// <param name="TocBit">
    /// Channel journal TOC byte bit (§5 Figure 9) for channel chapters;
    /// 0 for system chapters (which use the system-journal header byte instead).
    /// </param>
    /// <param name="Scope">Whether this is a channel or system chapter.</param>
    /// <param name="Status">Implementation status in this library.</param>
    /// <param name="Notes">Known quirks, compliance caveats, or spec references.</param>
    public record ChapterInfo(
        char         Id,
        string       RfcSection,
        string       DisplayName,
        byte         TocBit,
        ChapterScope Scope,
        Status       Status,
        string       Notes);

    /// <summary>
    /// Channel-journal TOC byte bits, in the order they appear left-to-right in §5 Figure 9.
    /// </summary>
    public const byte TocP = 0x80; // §A.2 Program Change
    public const byte TocC = 0x40; // §A.3 Control Change
    public const byte TocM = 0x20; // §A.4 Parameter System (RPN/NRPN)
    public const byte TocW = 0x10; // §A.5 Pitch Wheel
    public const byte TocN = 0x08; // §A.6 Note (On + Off)
    public const byte TocE = 0x04; // §A.7 Note Command Extras (reserved)
    public const byte TocT = 0x02; // §A.8 Channel Aftertouch
    public const byte TocA = 0x01; // §A.9 Poly Key Pressure

    /// <summary>
    /// System-journal header byte flags (§5.2, §B.2, §B.3).
    /// </summary>
    public const byte SysFlagF = 0x40; // §B.3 System Common
    public const byte SysFlagX = 0x04; // §B.2 SysEx

    /// <summary>
    /// All chapters this library is aware of, keyed by chapter ID.
    /// Iteration order is TOC (channel chapters first, system chapters after).
    /// </summary>
    public static readonly IReadOnlyDictionary<char, ChapterInfo> All =
        new Dictionary<char, ChapterInfo>
        {
            ['P'] = new('P', "§A.2", "Program Change",
                TocP, ChapterScope.Channel, Status.FullyImplemented,
                "3-byte fixed. Bank coarse (CC 0) and fine (CC 32) are mirrored "
                + "into the chapter when they precede the Program Change."),

            ['C'] = new('C', "§A.3", "Control Change",
                TocC, ChapterScope.Channel, Status.FullyImplemented,
                "LEN = count-1 list of 2-byte entries (value tool, A=0). "
                + "Uses the shared ChapterListEncoder."),

            ['M'] = new('M', "§A.4", "Parameter System (RPN/NRPN)",
                TocM, ChapterScope.Channel, Status.FullyImplemented,
                "2-byte header (S|P|E|U|W|Z|LENGTH[9:0]) with most-recently-"
                + "selected parameter at the front. Z optimisation is parsed "
                + "but not emitted (encoder always sets Z=0)."),

            ['W'] = new('W', "§A.5", "Pitch Wheel",
                TocW, ChapterScope.Channel, Status.FullyImplemented,
                "Fixed 2 bytes (LSB, MSB)."),

            ['N'] = new('N', "§A.6", "Note (On + Off)",
                TocN, ChapterScope.Channel, Status.FullyImplemented,
                "2-byte header (B | LEN=count, LOW|HIGH) plus 2-byte note-log "
                + "entries and OFFBITS bitfield. B-bit is always 1 per §A.6.1 "
                + "guidance; the encoder's isLast parameter is not used for "
                + "this chapter. Special sentinel LEN=127,LOW=15,HIGH=0 codes "
                + "a 128-entry note log with empty OFFBITS."),

            ['E'] = new('E', "§A.7", "Note Command Extras",
                TocE, ChapterScope.Channel, Status.Reserved,
                "Not emitted. On decode, if a remote sets the E bit we skip "
                + "the rest of THAT channel journal (T and A cannot be parsed "
                + "without knowing Chapter E's size) but continue with the "
                + "next channel journal."),

            ['T'] = new('T', "§A.8", "Channel Aftertouch",
                TocT, ChapterScope.Channel, Status.FullyImplemented,
                "Fixed 1 byte."),

            ['A'] = new('A', "§A.9", "Poly Key Pressure",
                TocA, ChapterScope.Channel, Status.FullyImplemented,
                "LEN = count-1 list of 2-byte entries. Uses the shared "
                + "ChapterListEncoder."),

            ['F'] = new('F', "§B.3", "System Common",
                0, ChapterScope.System, Status.FullyImplemented,
                "Variable-length: 1-byte header (D|V|Q flags) plus optional "
                + "MTC quarter frame, Song Position Pointer, Song Select."),

            ['X'] = new('X', "§B.2", "SysEx",
                0, ChapterScope.System, Status.FullyImplemented,
                "2-byte header (S|F|LENGTH[13:0]) followed by the raw SysEx "
                + "payload (including F0/F7 framing). F bit indicates a "
                + "complete message (trailing F7 observed)."),
        };

    /// <summary>
    /// Channel chapters in TOC wire order (P, C, M, W, N, T, A). Excludes E
    /// (reserved) and system chapters. Use this to iterate chapters when
    /// building/parsing a channel journal so the wire format is driven by
    /// the registry rather than hard-coded lists.
    /// </summary>
    public static readonly char[] ChannelChapterOrder = ['P', 'C', 'M', 'W', 'N', 'T', 'A'];

    /// <summary>
    /// Looks up a chapter by its channel-journal TOC bit. Returns null for
    /// bits that do not correspond to a defined chapter (currently none in
    /// the 8-bit TOC byte — all positions are defined or reserved).
    /// </summary>
    public static ChapterInfo? ForChannelJournalBit(byte bit)
    {
        foreach (var info in All.Values)
        {
            if (info.Scope == ChapterScope.Channel && info.TocBit == bit)
                return info;
        }
        return null;
    }
}
