namespace Haukcode.RtpMidi;

/// <summary>
/// Tracks the per-channel MIDI state required to encode recovery journal
/// chapters P, C, M, W, N, T, and A of RFC 6295.
///
/// ── RFC 6295 compliance notes ───────────────────────────────────────────────
/// The implementation in this file follows the normative wire formats from
/// Appendix A of RFC 6295 exactly. Where the older code (pre-April 2026) had
/// quirks that were internally consistent but incompatible with strict-spec
/// implementations, each fix is called out in a comment block at the
/// relevant encoder/decoder.
///
/// RFC term usage:
///   "LEN"    — chapter-specific count field; semantics differ per chapter
///              (count minus one for C/E/A; count as-is for N; etc.).
///   "LENGTH" — generic "bytes-including-header" length (channel journal
///              header uses this, NOT LEN).
///   "R bit"  — reserved; senders MUST set to 0, receivers MUST ignore.
///   "S bit"  — single-packet loss indicator; MUST be 1 unless the chapter
///              codes data from the previous packet, in which case 0.
/// ────────────────────────────────────────────────────────────────────────────
/// </summary>
internal sealed class ChannelMidiState
{
    // -----------------------------------------------------------------------
    // Chapter P — Program Change
    // -----------------------------------------------------------------------

    /// <summary>True when a Program Change has been sent on this channel.</summary>
    public bool HasProgram { get; private set; }

    /// <summary>Most-recent program number (0–127).</summary>
    public byte Program { get; private set; }

    /// <summary>True when Bank Select Coarse (CC 0) has been sent before the current Program Change.</summary>
    public bool HasBankCoarse { get; private set; }

    /// <summary>Most-recent Bank Select Coarse value (CC 0, 0–127).</summary>
    public byte BankCoarse { get; private set; }

    /// <summary>True when Bank Select Fine (CC 32) was sent after Bank Coarse and before the current PC.</summary>
    public bool HasBankFine { get; private set; }

    /// <summary>Most-recent Bank Select Fine value (CC 32, 0–127).</summary>
    public byte BankFine { get; private set; }

    // -----------------------------------------------------------------------
    // Chapter C — Control Change (all 128 controllers)
    // -----------------------------------------------------------------------

    private readonly byte[] ccValues  = new byte[128];
    private readonly bool[] ccActive  = new bool[128];

    /// <summary>Read-only view of the per-controller last-value array (indexed 0–127).</summary>
    internal ReadOnlySpan<byte> CcValues => ccValues;

    /// <summary>Read-only view of the per-controller "has been set" flags.</summary>
    internal ReadOnlySpan<bool> CcActive => ccActive;

    /// <summary>True when at least one Control Change has been sent on this channel.</summary>
    public bool HasControlChange { get; private set; }

    // -----------------------------------------------------------------------
    // Chapter W — Pitch Wheel
    // -----------------------------------------------------------------------

    /// <summary>True when a Pitch Wheel message has been sent on this channel.</summary>
    public bool HasPitchWheel { get; private set; }

    /// <summary>First data byte (LSB) of the most-recent Pitch Wheel message (0–127).</summary>
    public byte PitchLsb { get; private set; }

    /// <summary>Second data byte (MSB) of the most-recent Pitch Wheel message (0–127).</summary>
    public byte PitchMsb { get; private set; }

    // -----------------------------------------------------------------------
    // Chapter N — MIDI NoteOn and NoteOff (RFC 6295 §A.6)
    // -----------------------------------------------------------------------
    //
    // RFC 6295 §A.6 specifies ONE chapter (N) covering BOTH NoteOn and NoteOff
    // events. Per-note, the most recent N-active event dictates where the note
    // goes in the chapter:
    //   - NoteOn         → entry in the note log list (carries strike velocity)
    //   - NoteOff (or NoteOn vel=0) → set bit in the OFFBITS bitfield
    // A note MUST NOT appear in both structures simultaneously. Earlier
    // versions of this library split these into separate chapters N and Q;
    // Q is not defined by RFC 6295 and is now removed.

    /// <summary>Set for each note whose most recent N-active event is a NoteOn (strike).</summary>
    private readonly bool[] noteOnActive = new bool[128];

    /// <summary>Strike velocity (1–127) of the most recent NoteOn, per note.</summary>
    private readonly byte[] noteOnVel    = new byte[128];

    /// <summary>Set for each note whose most recent N-active event is a NoteOff.</summary>
    private readonly bool[] noteOffActive = new bool[128];

    /// <summary>True when the Chapter N note list is non-empty.</summary>
    public bool HasNoteOn  { get; private set; }

    /// <summary>True when the Chapter N OFFBITS structure is non-empty.</summary>
    public bool HasNoteOff { get; private set; }

    /// <summary>True when any Chapter N data (either NoteOn log entries or OFFBITS bits) is present.</summary>
    public bool HasNotes => HasNoteOn || HasNoteOff;

    // -----------------------------------------------------------------------
    // Chapter T — Channel Pressure (Aftertouch)
    // -----------------------------------------------------------------------

    /// <summary>True when a Channel Pressure message has been sent on this channel.</summary>
    public bool HasChannelPressure { get; private set; }

    /// <summary>Most-recent channel pressure value (0–127).</summary>
    public byte ChannelPressure { get; private set; }

    // -----------------------------------------------------------------------
    // Chapter A — Poly Key Pressure
    // -----------------------------------------------------------------------

    private readonly byte[] polyPressure       = new byte[128];
    private readonly bool[] polyPressureActive = new bool[128];

    /// <summary>Read-only view of the per-note last-pressure array (indexed 0–127).</summary>
    internal ReadOnlySpan<byte> PolyPressure => polyPressure;

    /// <summary>Read-only view of the per-note "has been set" flags.</summary>
    internal ReadOnlySpan<bool> PolyPressureActive => polyPressureActive;

    /// <summary>True when at least one Poly Key Pressure message has been sent.</summary>
    public bool HasPolyPressure { get; private set; }

    // -----------------------------------------------------------------------
    // Chapter M — Parameter System (RPN/NRPN)
    // -----------------------------------------------------------------------

    /// <summary>
    /// RPN/NRPN parameter-system log (RFC 6295 §A.4). Owns its own accumulation
    /// logic; this class forwards relevant Control Change events to it.
    /// </summary>
    private readonly Journal.State.ParameterLog paramLog = new();

    /// <summary>Exposes the parameter log to the Chapter M codec.</summary>
    internal Journal.State.ParameterLog ParamLog => paramLog;

    /// <summary>True when RPN/NRPN parameter-system state has been accumulated on this channel.</summary>
    public bool HasParameterSystem => paramLog.HasAnyData;

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Returns true if any chapter data has been accumulated since the last reset.</summary>
    public bool HasAnyData =>
        HasProgram || HasControlChange || HasParameterSystem || HasPitchWheel ||
        HasNotes || HasChannelPressure || HasPolyPressure;

    /// <summary>
    /// Clears all accumulated channel state.  Call at the start of each new session so that
    /// recovery journals do not carry over state from a previous peer.
    /// </summary>
    public void Reset()
    {
        HasProgram       = false;
        Program          = 0;
        HasBankCoarse    = false;
        BankCoarse       = 0;
        HasBankFine      = false;
        BankFine         = 0;
        HasControlChange = false;
        Array.Clear(ccValues,  0, ccValues.Length);
        Array.Clear(ccActive,  0, ccActive.Length);
        HasPitchWheel    = false;
        PitchLsb         = 0;
        PitchMsb         = 0;
        HasNoteOn        = false;
        HasNoteOff       = false;
        Array.Clear(noteOnActive,  0, noteOnActive.Length);
        Array.Clear(noteOnVel,     0, noteOnVel.Length);
        Array.Clear(noteOffActive, 0, noteOffActive.Length);
        HasChannelPressure = false;
        ChannelPressure  = 0;
        HasPolyPressure  = false;
        Array.Clear(polyPressure,       0, polyPressure.Length);
        Array.Clear(polyPressureActive, 0, polyPressureActive.Length);
        paramLog.Reset();
    }

    // -----------------------------------------------------------------------
    // State update
    // -----------------------------------------------------------------------

    /// <summary>
    /// Processes a single MIDI message (status + data bytes) and updates the
    /// tracked state for the appropriate chapter.  Only channel messages
    /// (status bytes 0x80–0xEF) are processed; system messages are ignored.
    /// </summary>
    public void ProcessMidi(ReadOnlySpan<byte> midiData)
    {
        if (midiData.IsEmpty) return;

        byte status = midiData[0];
        if (status >= 0xF0) return; // system message — not a channel chapter

        switch (status & 0xF0)
        {
            case 0x80: // Note Off
                if (midiData.Length >= 3)
                {
                    byte note = (byte)(midiData[1] & 0x7F);
                    // Most-recent-wins: move note from log list to OFFBITS.
                    noteOnActive[note]  = false;
                    noteOffActive[note] = true;
                    RefreshNoteFlags();
                }
                break;

            case 0x90: // Note On (velocity 0 = Note Off per RFC 6295 §A.6)
                if (midiData.Length >= 3)
                {
                    byte note = (byte)(midiData[1] & 0x7F);
                    byte vel  = (byte)(midiData[2] & 0x7F);
                    if (vel == 0)
                    {
                        noteOnActive[note]  = false;
                        noteOffActive[note] = true;
                    }
                    else
                    {
                        noteOnActive[note]  = true;
                        noteOnVel[note]     = vel; // strike velocity (always 1-127)
                        noteOffActive[note] = false;
                    }
                    RefreshNoteFlags();
                }
                break;

            case 0xA0: // Poly Key Pressure
                if (midiData.Length >= 3)
                {
                    byte note     = (byte)(midiData[1] & 0x7F);
                    byte pressure = (byte)(midiData[2] & 0x7F);
                    polyPressure[note]       = pressure;
                    polyPressureActive[note] = true;
                    HasPolyPressure = true;
                }
                break;

            case 0xB0: // Control Change
                if (midiData.Length >= 3)
                {
                    byte cc  = (byte)(midiData[1] & 0x7F);
                    byte val = (byte)(midiData[2] & 0x7F);
                    ccValues[cc] = val;
                    ccActive[cc] = true;
                    HasControlChange = true;
                    // Mirror bank select into Chapter P fields
                    if (cc == 0)  { BankCoarse = val; HasBankCoarse = true; }
                    if (cc == 32) { BankFine   = val; HasBankFine   = true; }
                    // Parameter system tracking (Chapter M) — ParameterLog filters
                    // for the CCs it cares about (6, 38, 98, 99, 100, 101).
                    paramLog.ProcessControlChange(cc, val);
                }
                break;

            case 0xC0: // Program Change
                if (midiData.Length >= 2)
                {
                    Program    = (byte)(midiData[1] & 0x7F);
                    HasProgram = true;
                }
                break;

            case 0xD0: // Channel Pressure
                if (midiData.Length >= 2)
                {
                    ChannelPressure    = (byte)(midiData[1] & 0x7F);
                    HasChannelPressure = true;
                }
                break;

            case 0xE0: // Pitch Wheel
                if (midiData.Length >= 3)
                {
                    PitchLsb     = (byte)(midiData[1] & 0x7F);
                    PitchMsb     = (byte)(midiData[2] & 0x7F);
                    HasPitchWheel = true;
                }
                break;
        }
    }

    /// <summary>Recompute the "any notes in log/offbits" flags after a per-note update.</summary>
    private void RefreshNoteFlags()
    {
        bool anyOn = false, anyOff = false;
        for (int i = 0; i < 128; i++)
        {
            if (noteOnActive[i])  anyOn  = true;
            if (noteOffActive[i]) anyOff = true;
            if (anyOn && anyOff) break;
        }
        HasNoteOn  = anyOn;
        HasNoteOff = anyOff;
    }

    // -----------------------------------------------------------------------
    // Chapter encoding
    // -----------------------------------------------------------------------

    /// <summary>Encodes Chapter P. Delegates to <see cref="Journal.Chapters.ChapterPCodec"/>.</summary>
    public byte[] EncodeChapterP(bool isLast)
        => new Journal.Chapters.ChapterPCodec(this).Encode(isLast);

    /// <summary>
    /// Encodes Chapter C (Control Change) — RFC 6295 §A.3.
    ///
    ///   Header byte: S | LEN[6:0]
    ///     LEN = (number of controller log entries) − 1  (7-bit field, max 128 entries)
    ///   Each log entry (2 bytes): S | NUMBER[6:0] | A | VALUE[6:0]
    ///     NUMBER = controller number (0–127)
    ///     A = 0 → "value tool"; VALUE codes the controller data value
    ///         (A = 1 "toggle / count tool" is defined by the RFC but not emitted here)
    ///
    /// The list must contain at least one entry; if no controllers are active,
    /// the chapter MUST NOT be present at all (HasControlChange governs this).
    /// </summary>
    public byte[] EncodeChapterC(bool isLast)
        => new Journal.Chapters.ChapterCCodec(this).Encode(isLast);

    /// <summary>Encodes Chapter W. Delegates to <see cref="Journal.Chapters.ChapterWCodec"/>.</summary>
    public byte[] EncodeChapterW(bool isLast)
        => new Journal.Chapters.ChapterWCodec(this).Encode(isLast);

    /// <summary>
    /// Encodes Chapter N (MIDI NoteOn and NoteOff) — RFC 6295 §A.6.
    ///
    ///   Header (2 octets, always):
    ///     Byte 0:  B | LEN[6:0]         B = 1 always, per §A.6.1 guidance
    ///                                   LEN = number of note log entries (7-bit, not count-1)
    ///     Byte 1:  LOW[3:0] | HIGH[3:0]
    ///       If LOW ≤ HIGH, OFFBITS occupies (HIGH − LOW + 1) octets after the log list.
    ///       If LOW = 15 and HIGH = 0 or 1, the OFFBITS structure is empty.
    ///
    ///   Each note log (2 bytes): S | NOTENUM[6:0] | Y | VELOCITY[6:0]
    ///     NOTENUM  = note number (0–127)
    ///     VELOCITY = strike velocity of the most recent N-active NoteOn (never 0)
    ///     Y        = sender hint: 1 = play the recovered NoteOn, 0 = skip it
    ///
    ///   OFFBITS: packed 8-bit octets where the MSB of the first octet represents
    ///   note 8·LOW and successive bits advance by one note number. A set bit
    ///   codes a NoteOff command for that note.
    /// </summary>
    /// <remarks>
    /// Unlike other chapters, Chapter N uses byte-0 bit-7 as the <c>B</c> bit
    /// (§A.6.1 — a per-chapter semantic indicating NoteOff-log presence hint),
    /// NOT the generic per-chapter S "last in journal" bit. RFC §A.6.1 guidance
    /// is to set B=1 and we follow that unconditionally. The
    /// <paramref name="isLast"/> parameter is accepted for interface uniformity
    /// with other chapter encoders (so the orchestrator can dispatch chapters
    /// generically) but is not used by this encoder.
    /// </remarks>
    public byte[] EncodeChapterN(bool isLast)
    {
        // ── Gather the note log list (notes whose most recent event is NoteOn) ──
        var logEntries = new List<(byte note, byte vel)>(128);
        for (int i = 0; i < 128; i++)
        {
            if (noteOnActive[i])
                logEntries.Add(((byte)i, noteOnVel[i]));
        }

        int logCount = logEntries.Count; // 0-128

        // ── Compute OFFBITS coverage (octet range of pending NoteOffs) ──
        int lowOct = 16, highOct = -1;
        for (int oct = 0; oct < 16; oct++)
        {
            bool any = false;
            for (int b = 0; b < 8; b++)
            {
                if (noteOffActive[oct * 8 + b]) { any = true; break; }
            }
            if (any)
            {
                if (oct < lowOct)  lowOct  = oct;
                if (oct > highOct) highOct = oct;
            }
        }

        bool hasOffbits = highOct >= lowOct;
        int  offbitsBytes = hasOffbits ? (highOct - lowOct + 1) : 0;

        // Header-byte-1 encodes the OFFBITS range. RFC §A.6.1 specifies the
        // "empty OFFBITS" sentinels: LOW=15 with HIGH=0 or HIGH=1.
        byte low, high;
        if (hasOffbits) { low = (byte)lowOct; high = (byte)highOct; }
        else            { low = 15;           high = 0; }

        // LEN=127 is the special value that codes count=127 OR count=128 depending
        // on LOW/HIGH: if LEN=127, LOW=15, HIGH=0, the note list is 128 entries
        // long AND there is no OFFBITS structure. For any count ≤ 127 we can
        // encode it directly. For count=128 we MUST use the sentinel combination.
        byte lenField;
        if (logCount == 128)
        {
            // Force the special sentinel; cannot coexist with any OFFBITS.
            lenField = 127;
            low  = 15;
            high = 0;
            hasOffbits = false;
            offbitsBytes = 0;
        }
        else
        {
            lenField = (byte)logCount;
        }

        // Build the chapter.
        int total = 2 + logCount * 2 + offbitsBytes;
        var buf = new byte[total];

        // Header (2 octets). B (byte 0 bit 7) is always 1 per §A.6.1; see the
        // <remarks> section on this method for why isLast is accepted but
        // intentionally unused.
        buf[0] = (byte)(0x80 | (lenField & 0x7F));
        buf[1] = (byte)(((low & 0x0F) << 4) | (high & 0x0F));

        int pos = 2;
        for (int i = 0; i < logCount; i++)
        {
            bool lastEntry = i == logCount - 1;
            buf[pos++] = (byte)((lastEntry ? 0x80 : 0) | (logEntries[i].note & 0x7F));
            // Y = 0 (no playback hint); VELOCITY in low 7 bits, never 0.
            byte vel = logEntries[i].vel;
            if (vel == 0) vel = 1; // defensive — RFC forbids zero-velocity log entries
            buf[pos++] = (byte)(vel & 0x7F);
        }

        if (hasOffbits)
        {
            for (int oct = lowOct; oct <= highOct; oct++)
            {
                byte b = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    // MSB of each octet represents the lowest note in its group.
                    if (noteOffActive[oct * 8 + bit])
                        b |= (byte)(0x80 >> bit);
                }
                buf[pos++] = b;
            }
        }

        return buf;
    }

    /// <summary>Encodes Chapter T. Delegates to <see cref="Journal.Chapters.ChapterTCodec"/>.</summary>
    public byte[] EncodeChapterT(bool isLast)
        => new Journal.Chapters.ChapterTCodec(this).Encode(isLast);

    /// <summary>
    /// Encodes Chapter A (Poly Key Pressure) — RFC 6295 §A.9.
    ///
    ///   Header byte: S | LEN[6:0]
    ///     LEN = (number of note logs) − 1  (7-bit field)
    ///   Each log (2 bytes): S | NOTENUM[6:0] | X | PRESSURE[6:0]
    ///     X = 0 by default (set to 1 if the command appears before All-Notes-Off/All-Sound-Off).
    /// </summary>
    public byte[] EncodeChapterA(bool isLast)
        => new Journal.Chapters.ChapterACodec(this).Encode(isLast);

    /// <summary>Encodes Chapter M. Delegates to <see cref="Journal.Chapters.ChapterMCodec"/>.</summary>
    public byte[] EncodeChapterM(bool isLast)
        => new Journal.Chapters.ChapterMCodec(paramLog).Encode(isLast);

    // -----------------------------------------------------------------------
    // Chapter decoding  (static: parse bytes → MIDI messages)
    // -----------------------------------------------------------------------

    /// <summary>Decodes Chapter P. Delegates to <see cref="Journal.Chapters.ChapterPCodec.DecodeStatic"/>.</summary>
    public static int DecodeChapterP(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => Journal.Chapters.ChapterPCodec.DecodeStatic(data, channel, recovered);

    /// <summary>Decodes Chapter C. Delegates to <see cref="Journal.Chapters.ChapterCCodec.DecodeStatic"/>.</summary>
    public static int DecodeChapterC(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => Journal.Chapters.ChapterCCodec.DecodeStatic(data, channel, recovered);

    /// <summary>Decodes Chapter W. Delegates to <see cref="Journal.Chapters.ChapterWCodec.DecodeStatic"/>.</summary>
    public static int DecodeChapterW(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => Journal.Chapters.ChapterWCodec.DecodeStatic(data, channel, recovered);

    /// <summary>
    /// Decodes Chapter N (RFC 6295 §A.6): 2-byte header with 7-bit LEN (= note
    /// log count directly, NOT count-1) plus LOW/HIGH nibbles, followed by
    /// LEN × 2-byte note-log entries (NoteOn with strike velocity) and
    /// (HIGH−LOW+1) OFFBITS octets when LOW ≤ HIGH. Appends recovered Note On
    /// and Note Off messages to <paramref name="recovered"/>.
    /// </summary>
    public static int DecodeChapterN(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.Length < 2) return -1;

        byte header0 = data[0];
        byte header1 = data[1];
        // B bit (0x80 of byte 0) is informational; ignore for recovery.
        int logCount = header0 & 0x7F;
        int low      = (header1 >> 4) & 0x0F;
        int high     =  header1        & 0x0F;

        // Special case: LEN=127 && LOW=15 && HIGH=0 codes logCount=128 and empty OFFBITS.
        bool logCount128Special = (logCount == 127) && (low == 15) && (high == 0);
        int  effectiveLogCount = logCount128Special ? 128 : logCount;

        // Determine OFFBITS octet count. Sentinels (LOW=15,HIGH=0) and (LOW=15,HIGH=1)
        // code "no OFFBITS". Any other LOW > HIGH is illegal per §A.6.1 but we
        // treat it defensively as no OFFBITS to remain compatible with strays.
        int offbitsBytes;
        if (logCount128Special)         offbitsBytes = 0;
        else if (low == 15 && high <= 1) offbitsBytes = 0;
        else if (low <= high)            offbitsBytes = (high - low + 1);
        else                             offbitsBytes = 0;

        int required = 2 + effectiveLogCount * 2 + offbitsBytes;
        if (data.Length < required) return -1;

        // ── Note log list → NoteOn (0x9n) recovery ──
        byte status90 = (byte)(0x90 | (channel & 0x0F));
        int pos = 2;
        for (int i = 0; i < effectiveLogCount; i++)
        {
            byte b0 = data[pos++];
            byte b1 = data[pos++];
            byte note = (byte)(b0 & 0x7F);
            byte vel  = (byte)(b1 & 0x7F); // Y bit at position 7 ignored for emit
            // Per §A.6.2 VELOCITY is never 0 in the note log list.
            if (vel == 0) vel = 1;
            recovered.Add([status90, note, vel]);
        }

        // ── OFFBITS bitfield → NoteOff (0x8n) recovery ──
        byte status80 = (byte)(0x80 | (channel & 0x0F));
        for (int oct = 0; oct < offbitsBytes; oct++)
        {
            byte b = data[pos++];
            if (b == 0) continue;
            int baseNote = 8 * (low + oct);
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (0x80 >> bit)) != 0)
                {
                    int note = baseNote + bit;
                    if (note < 128)
                        recovered.Add([status80, (byte)note, (byte)0]);
                }
            }
        }

        return required;
    }

    /// <summary>Decodes Chapter T. Delegates to <see cref="Journal.Chapters.ChapterTCodec.DecodeStatic"/>.</summary>
    public static int DecodeChapterT(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => Journal.Chapters.ChapterTCodec.DecodeStatic(data, channel, recovered);

    /// <summary>Decodes Chapter A. Delegates to <see cref="Journal.Chapters.ChapterACodec.DecodeStatic"/>.</summary>
    public static int DecodeChapterA(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => Journal.Chapters.ChapterACodec.DecodeStatic(data, channel, recovered);

    /// <summary>Decodes Chapter M. Delegates to <see cref="Journal.Chapters.ChapterMCodec.DecodeStatic"/>.</summary>
    public static int DecodeChapterM(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => Journal.Chapters.ChapterMCodec.DecodeStatic(data, channel, recovered);
}

/// <summary>
/// Tracks the system-level MIDI state required for the Chapter F recovery journal
/// (RFC 6295 §B.3).  Chapter F covers MIDI System Common messages:
/// MTC Quarter Frame (0xF1), Song Position Pointer (0xF2), and Song Select (0xF3).
/// </summary>
internal sealed class SystemMidiState
{
    // -----------------------------------------------------------------------
    // MIDI Time Code Quarter Frame (0xF1)
    // -----------------------------------------------------------------------

    /// <summary>True when an MTC Quarter Frame has been sent.</summary>
    public bool HasTimeCode { get; private set; }

    /// <summary>Data byte of the most-recent MTC Quarter Frame message (0–127).</summary>
    public byte TimeCode { get; private set; }

    // -----------------------------------------------------------------------
    // Song Position Pointer (0xF2)
    // -----------------------------------------------------------------------

    /// <summary>True when a Song Position Pointer has been sent.</summary>
    public bool HasSongPosition { get; private set; }

    /// <summary>LSB of the most-recent Song Position Pointer (data byte 1).</summary>
    public byte SongPositionLsb { get; private set; }

    /// <summary>MSB of the most-recent Song Position Pointer (data byte 2).</summary>
    public byte SongPositionMsb { get; private set; }

    // -----------------------------------------------------------------------
    // Song Select (0xF3)
    // -----------------------------------------------------------------------

    /// <summary>True when a Song Select has been sent.</summary>
    public bool HasSongSelect { get; private set; }

    /// <summary>Song number of the most-recent Song Select (0–127).</summary>
    public byte SongSelect { get; private set; }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Returns true if any Chapter F data has been accumulated.</summary>
    public bool HasAnyData => HasTimeCode || HasSongPosition || HasSongSelect;

    /// <summary>
    /// Clears all accumulated system state.  Call at the start of each new session so that
    /// recovery journals do not carry over state from a previous peer.
    /// </summary>
    public void Reset()
    {
        HasTimeCode     = false;
        TimeCode        = 0;
        HasSongPosition = false;
        SongPositionLsb = 0;
        SongPositionMsb = 0;
        HasSongSelect   = false;
        SongSelect      = 0;
    }

    // -----------------------------------------------------------------------
    // State update
    // -----------------------------------------------------------------------

    /// <summary>
    /// Processes a system common MIDI message and updates the tracked state.
    /// Only 0xF1, 0xF2, and 0xF3 are processed; other messages are ignored.
    /// </summary>
    public void ProcessMidi(ReadOnlySpan<byte> midiData)
    {
        if (midiData.IsEmpty) return;

        switch (midiData[0])
        {
            case 0xF1: // MTC Quarter Frame
                if (midiData.Length >= 2)
                {
                    TimeCode    = (byte)(midiData[1] & 0x7F);
                    HasTimeCode = true;
                }
                break;

            case 0xF2: // Song Position Pointer
                if (midiData.Length >= 3)
                {
                    SongPositionLsb = (byte)(midiData[1] & 0x7F);
                    SongPositionMsb = (byte)(midiData[2] & 0x7F);
                    HasSongPosition = true;
                }
                break;

            case 0xF3: // Song Select
                if (midiData.Length >= 2)
                {
                    SongSelect    = (byte)(midiData[1] & 0x7F);
                    HasSongSelect = true;
                }
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Encoding
    // -----------------------------------------------------------------------

    /// <summary>Encodes Chapter F. Delegates to <see cref="Journal.Chapters.ChapterFCodec"/>.</summary>
    public byte[] EncodeChapterF(bool isLast)
        => new Journal.Chapters.ChapterFCodec(this).Encode(isLast);

    // -----------------------------------------------------------------------
    // Decoding
    // -----------------------------------------------------------------------

    /// <summary>Decodes Chapter F. Delegates to <see cref="Journal.Chapters.ChapterFCodec.DecodeStatic"/>.</summary>
    public static int DecodeChapterF(ReadOnlySpan<byte> data, List<byte[]> recovered)
        => Journal.Chapters.ChapterFCodec.DecodeStatic(data, recovered);
}
