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

    /// <summary>True when at least one Poly Key Pressure message has been sent.</summary>
    public bool HasPolyPressure { get; private set; }

    // -----------------------------------------------------------------------
    // Chapter M — Parameter System (RPN/NRPN)
    // -----------------------------------------------------------------------
    //
    // RFC 6295 §A.4 requires a full log list of parameter entries, most-recently-
    // selected first. Each entry carries the parameter number and the most-recent
    // data-entry values (CC6/CC38). A second selection of the same parameter number
    // moves the entry to the front with cleared data (re-selection semantics).

    private struct ParamEntry
    {
        public bool IsNrpn;
        public byte ParamMsb, ParamLsb;
        public bool HasDataMsb, HasDataLsb;
        public byte DataMsb, DataLsb;
    }

    /// <summary>Log of selected parameters, most-recently-selected first (RFC 6295 §A.4).</summary>
    private readonly List<ParamEntry> paramLog = new();

    /// <summary>True when a parameter-number MSB CC was received but the LSB has not yet arrived.</summary>
    private bool hasPendingParamMsb;
    private bool pendingIsNrpn;
    private byte pendingParamMsb;

    /// <summary>True when RPN/NRPN parameter-system state has been accumulated on this channel.</summary>
    public bool HasParameterSystem => paramLog.Count > 0 || hasPendingParamMsb;

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
        paramLog.Clear();
        hasPendingParamMsb = false;
        pendingIsNrpn      = false;
        pendingParamMsb    = 0;
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
                    // Parameter system tracking (Chapter M)
                    switch (cc)
                    {
                        case 99:  // NRPN MSB — begin NRPN parameter selection
                            hasPendingParamMsb = true;
                            pendingIsNrpn      = true;
                            pendingParamMsb    = val;
                            break;
                        case 101: // RPN MSB — begin RPN parameter selection
                            hasPendingParamMsb = true;
                            pendingIsNrpn      = false;
                            pendingParamMsb    = val;
                            break;
                        case 98:  // NRPN LSB — complete NRPN selection
                            if (hasPendingParamMsb && pendingIsNrpn)
                            {
                                FinalizeParamSelection(pendingIsNrpn, pendingParamMsb, val);
                                hasPendingParamMsb = false;
                            }
                            break;
                        case 100: // RPN LSB — complete RPN selection
                            if (hasPendingParamMsb && !pendingIsNrpn)
                            {
                                FinalizeParamSelection(pendingIsNrpn, pendingParamMsb, val);
                                hasPendingParamMsb = false;
                            }
                            break;
                        case 6:   // Data Entry MSB — update front log entry
                            if (paramLog.Count > 0)
                            {
                                var e = paramLog[0];
                                e.HasDataMsb = true;
                                e.DataMsb    = val;
                                paramLog[0]  = e;
                            }
                            break;
                        case 38:  // Data Entry LSB — update front log entry
                            if (paramLog.Count > 0)
                            {
                                var e = paramLog[0];
                                e.HasDataLsb = true;
                                e.DataLsb    = val;
                                paramLog[0]  = e;
                            }
                            break;
                    }
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

    /// <summary>
    /// Completes parameter selection: removes any existing entry for the same
    /// (isNrpn, msb, lsb) address then inserts a fresh entry at the front of
    /// <see cref="paramLog"/> with cleared data-entry fields. Data resets on
    /// re-selection per RFC 6295 §A.4.
    /// </summary>
    private void FinalizeParamSelection(bool isNrpn, byte msb, byte lsb)
    {
        for (int i = 0; i < paramLog.Count; i++)
        {
            var e = paramLog[i];
            if (e.IsNrpn == isNrpn && e.ParamMsb == msb && e.ParamLsb == lsb)
            {
                paramLog.RemoveAt(i);
                break;
            }
        }
        paramLog.Insert(0, new ParamEntry { IsNrpn = isNrpn, ParamMsb = msb, ParamLsb = lsb });
    }

    // -----------------------------------------------------------------------
    // Chapter encoding
    // -----------------------------------------------------------------------

    /// <summary>
    /// Encodes Chapter P (Program Change) — fixed 3 bytes (RFC 6295 §A.2).
    ///
    ///   Byte 0:  S | PROGRAM[6:0]
    ///   Byte 1:  B | BANK-MSB[6:0]   B = 1 when bank coarse (CC 0) was sent before this PC
    ///   Byte 2:  X | BANK-LSB[6:0]   X = 1 when bank fine (CC 32) was sent between coarse and PC
    /// </summary>
    public byte[] EncodeChapterP(bool isLast)
    {
        byte prog = (byte)(Program    & 0x7F);
        byte bc   = (byte)(BankCoarse & 0x7F);
        byte bf   = (byte)(BankFine   & 0x7F);

        return
        [
            (byte)((isLast        ? 0x80 : 0) | prog),
            (byte)((HasBankCoarse ? 0x80 : 0) | bc),
            (byte)((HasBankFine   ? 0x80 : 0) | bf)
        ];
    }

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
    {
        // Collect active controllers (up to 128 — 7-bit LEN field codes count-1)
        Span<(byte cc, byte val)> entries = stackalloc (byte, byte)[128];
        int count = 0;
        for (int i = 0; i < 128; i++)
        {
            if (ccActive[i])
                entries[count++] = ((byte)i, ccValues[i]);
        }

        // A = 0 (value tool): bit 7 of byte1 clear for all emitted entries.
        return Journal.ChapterListEncoder.EncodeCountMinusOneList(
            isLast, entries.Slice(0, count), secondByteFlagMask: 0x00);
    }

    /// <summary>
    /// Encodes Chapter W (Pitch Wheel) — fixed 2 bytes (RFC 6295 §A.5).
    ///
    ///   Byte 0:  S | FIRST[6:0]    FIRST  = LSB of the most-recent pitch wheel data
    ///   Byte 1:  R | SECOND[6:0]   SECOND = MSB. R is reserved (MUST be 0).
    /// </summary>
    public byte[] EncodeChapterW(bool isLast)
    {
        return
        [
            (byte)((isLast ? 0x80 : 0) | (PitchLsb & 0x7F)),
            (byte)(PitchMsb & 0x7F),  // R = 0; SECOND in low 7 bits
        ];
    }

    /// <summary>
    /// Encodes Chapter N (MIDI NoteOn and NoteOff) — RFC 6295 §A.6.
    ///
    ///   Header (2 octets, always):
    ///     Byte 0:  B | LEN[6:0]         B = 1 by default (see §A.6.1)
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

        // Header (2 octets)
        // B-bit default is 1 per §A.6.1 (the NoteOff bitfield's S-equivalent).
        // isLast is carried by higher-level chapter ordering; the S bit for the
        // *chapter overall* lives in byte 0 of chapter W / chapter C etc., but
        // Chapter N uses B in its place. We treat isLast only as a TODO hook;
        // for compliance with strict receivers we set B=1 always.
        _ = isLast;
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

    /// <summary>
    /// Encodes Chapter T (Channel Aftertouch) — fixed 1 byte (RFC 6295 §A.8).
    ///
    ///   Byte 0: S | PRESSURE[6:0]
    /// </summary>
    public byte[] EncodeChapterT(bool isLast)
    {
        return [(byte)((isLast ? 0x80 : 0) | (ChannelPressure & 0x7F))];
    }

    /// <summary>
    /// Encodes Chapter A (Poly Key Pressure) — RFC 6295 §A.9.
    ///
    ///   Header byte: S | LEN[6:0]
    ///     LEN = (number of note logs) − 1  (7-bit field)
    ///   Each log (2 bytes): S | NOTENUM[6:0] | X | PRESSURE[6:0]
    ///     X = 0 by default (set to 1 if the command appears before All-Notes-Off/All-Sound-Off).
    /// </summary>
    public byte[] EncodeChapterA(bool isLast)
    {
        Span<(byte note, byte pressure)> entries = stackalloc (byte, byte)[128];
        int count = 0;
        for (int i = 0; i < 128; i++)
        {
            if (polyPressureActive[i])
                entries[count++] = ((byte)i, polyPressure[i]);
        }

        // X = 0 (not before All-Notes-Off): bit 7 of byte1 clear for all entries.
        return Journal.ChapterListEncoder.EncodeCountMinusOneList(
            isLast, entries.Slice(0, count), secondByteFlagMask: 0x00);
    }

    /// <summary>
    /// Encodes Chapter M (Parameter System: RPN/NRPN) — RFC 6295 §A.4.
    ///
    /// Wire layout:
    ///   2-byte header: S(last) | P(pending MSB) | E(0) | U(all-RPN) | W(all-NRPN) | Z(0) |
    ///                  10-bit total chapter length
    ///   Optional pending byte (when P=1): Q(NRPN) | PNUM_MSB[6:0]
    ///   Log items (most-recent-selected first), each:
    ///     PNUM_LSB byte: S(last item) | PNUM_LSB[6:0]
    ///     PNUM_MSB byte: Q(NRPN)     | PNUM_MSB[6:0]
    ///     flags byte:    J(data MSB) | K(data LSB) | …
    ///     optional: data-entry MSB (CC6) when J=1
    ///     optional: data-entry LSB (CC38) when K=1
    /// </summary>
    public byte[] EncodeChapterM(bool isLast)
    {
        // Compute total chapter size for the header length field
        int pendingSize = hasPendingParamMsb ? 1 : 0;
        int itemsSize = 0;
        foreach (var e in paramLog)
            itemsSize += 3 + (e.HasDataMsb ? 1 : 0) + (e.HasDataLsb ? 1 : 0);
        int totalSize = 2 + pendingSize + itemsSize;

        // U/W are hints (used with Z optimisation to omit PNUM_MSB per entry);
        // we set them correctly for informational purposes even though Z=0 here.
        bool allRpn  = paramLog.Count > 0 && !paramLog.Any(e => e.IsNrpn);
        bool allNrpn = paramLog.Count > 0 && !paramLog.Any(e => !e.IsNrpn);

        var buf = new byte[totalSize];

        ushort hdr = (ushort)(
            (isLast              ? 0x8000 : 0)  // S
            | (hasPendingParamMsb ? 0x4000 : 0) // P
            | (allRpn            ? 0x1000 : 0)  // U
            | (allNrpn           ? 0x0800 : 0)  // W
            | (totalSize & 0x03FF));             // Length
        buf[0] = (byte)(hdr >> 8);
        buf[1] = (byte)(hdr & 0xFF);

        int off = 2;

        if (hasPendingParamMsb)
            buf[off++] = (byte)((pendingIsNrpn ? 0x80 : 0) | (pendingParamMsb & 0x7F));

        for (int i = 0; i < paramLog.Count; i++)
        {
            var  e        = paramLog[i];
            bool lastItem = i == paramLog.Count - 1;

            buf[off++] = (byte)((lastItem   ? 0x80 : 0) | (e.ParamLsb & 0x7F)); // PNUM_LSB
            buf[off++] = (byte)((e.IsNrpn   ? 0x80 : 0) | (e.ParamMsb & 0x7F)); // PNUM_MSB (Q=NRPN)

            byte flags = 0;
            if (e.HasDataMsb) flags |= 0x80; // J
            if (e.HasDataLsb) flags |= 0x40; // K
            buf[off++] = flags;

            if (e.HasDataMsb) buf[off++] = (byte)(e.DataMsb & 0x7F);
            if (e.HasDataLsb) buf[off++] = (byte)(e.DataLsb & 0x7F);
        }

        return buf;
    }

    // -----------------------------------------------------------------------
    // Chapter decoding  (static: parse bytes → MIDI messages)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Decodes Chapter P (RFC 6295 §A.2) and appends recovered Program Change
    /// (and optional bank-select Control Changes) to <paramref name="recovered"/>.
    /// Returns bytes consumed (always 3) or -1 on error.
    /// </summary>
    public static int DecodeChapterP(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.Length < 3) return -1;

        byte b0 = data[0], b1 = data[1], b2 = data[2];
        byte prog  = (byte)(b0 & 0x7F);
        bool hasBC = (b1 & 0x80) != 0;
        byte bc    = (byte)(b1 & 0x7F);
        bool hasBF = (b2 & 0x80) != 0;
        byte bf    = (byte)(b2 & 0x7F);

        if (hasBC)
            recovered.Add([(byte)(0xB0 | (channel & 0x0F)), 0, bc]);
        if (hasBF)
            recovered.Add([(byte)(0xB0 | (channel & 0x0F)), 32, bf]);
        recovered.Add([(byte)(0xC0 | (channel & 0x0F)), prog]);

        return 3;
    }

    /// <summary>
    /// Decodes Chapter C (RFC 6295 §A.3): LEN = (count − 1) in 7 bits, then
    /// LEN+1 two-byte log entries. Appends recovered Control Changes to
    /// <paramref name="recovered"/>. Returns bytes consumed or -1 on error.
    /// </summary>
    public static int DecodeChapterC(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.IsEmpty) return -1;

        byte header = data[0];
        int count = (header & 0x7F) + 1; // LEN codes count-1; min 1 entry
        int required = 1 + count * 2;
        if (data.Length < required) return -1;

        for (int i = 0; i < count; i++)
        {
            byte cc  = (byte)(data[1 + i * 2] & 0x7F);
            byte v   = data[1 + i * 2 + 1];
            byte val = (byte)(v & 0x7F);
            // Ignore the A (alt-tool) bit for recovery purposes; we always emit
            // a plain Control Change regardless of whether the sender used the
            // value tool or the toggle/count tool.
            recovered.Add([(byte)(0xB0 | (channel & 0x0F)), cc, val]);
        }

        return required;
    }

    /// <summary>
    /// Decodes Chapter W (RFC 6295 §A.5). Returns bytes consumed (2) or -1.
    /// </summary>
    public static int DecodeChapterW(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.Length < 2) return -1;

        byte lsb = (byte)(data[0] & 0x7F);
        byte msb = (byte)(data[1] & 0x7F); // R bit in position 7 is ignored
        recovered.Add([(byte)(0xE0 | (channel & 0x0F)), lsb, msb]);

        return 2;
    }

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

    /// <summary>
    /// Decodes Chapter T (RFC 6295 §A.8). Returns bytes consumed (1) or -1.
    /// </summary>
    public static int DecodeChapterT(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.IsEmpty) return -1;

        byte pressure = (byte)(data[0] & 0x7F);
        recovered.Add([(byte)(0xD0 | (channel & 0x0F)), pressure]);

        return 1;
    }

    /// <summary>
    /// Decodes Chapter A (RFC 6295 §A.9): LEN = (count − 1) in 7 bits.
    /// </summary>
    public static int DecodeChapterA(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.IsEmpty) return -1;

        byte header = data[0];
        int count = (header & 0x7F) + 1;
        int required = 1 + count * 2;
        if (data.Length < required) return -1;

        for (int i = 0; i < count; i++)
        {
            byte note     = (byte)(data[1 + i * 2] & 0x7F);
            byte pressure = (byte)(data[1 + i * 2 + 1] & 0x7F);
            recovered.Add([(byte)(0xA0 | (channel & 0x0F)), note, pressure]);
        }

        return required;
    }

    /// <summary>
    /// Decodes Chapter M (Parameter System: RPN/NRPN) — RFC 6295 §A.4.
    /// </summary>
    public static int DecodeChapterM(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        // Need at least the 2-byte chapter header
        if (data.Length < 2) return -1;

        ushort header  = (ushort)((data[0] << 8) | data[1]);
        bool   pending = (header & 0x4000) != 0;
        bool   hasZ    = (header & 0x0400) != 0;
        bool   hasU    = (header & 0x1000) != 0; // all-RPN hint
        bool   hasW    = (header & 0x0800) != 0; // all-NRPN hint
        int    length  = header & 0x03FF;        // total chapter size including header

        if (length < 2 || data.Length < length) return -1;

        int pos = 2;

        // Skip the optional pending byte
        if (pending)
        {
            if (pos >= length) return length;
            pos++;
        }

        // Z optimization: PNUM_MSB is omitted from each log item when all entries
        // share the same PNUM_MSB (RFC 6295 §A.1). Our encoder always sets Z=0 so
        // we never generate this format, but we parse it defensively for interoperability.
        bool noPnumMsb = hasZ && (hasU || hasW);

        byte status = (byte)(0xB0 | (channel & 0x0F));

        // Walk the log list
        while (pos < length)
        {
            // Log item: PNUM_LSB byte (bit 7 = S flag "last item")
            byte pnumLsb = (byte)(data[pos] & 0x7F);
            pos++;

            // Log item: PNUM_MSB byte (bit 7 = Q flag "NRPN")
            byte pnumMsb = 0;
            bool itemIsNrpn = hasW; // default from chapter-level W hint
            if (!noPnumMsb)
            {
                if (pos >= length) return -1;
                itemIsNrpn = (data[pos] & 0x80) != 0;
                pnumMsb    = (byte)(data[pos] & 0x7F);
                pos++;
            }

            // Log item: flags byte (J=0x80, K=0x40, L=0x20, M=0x10, N=0x08, T=0x04, V=0x02, R=0x01)
            if (pos >= length) return -1;
            byte flags = data[pos++];
            bool flagJ = (flags & 0x80) != 0; // data-entry MSB (CC6)
            bool flagK = (flags & 0x40) != 0; // data-entry LSB (CC38)
            bool flagL = (flags & 0x20) != 0; // a-button (2 bytes)
            bool flagM = (flags & 0x10) != 0; // c-button (2 bytes)
            bool flagN = (flags & 0x08) != 0; // count    (1 byte)

            // Optional data-entry MSB
            byte entryMsb = 0;
            if (flagJ)
            {
                if (pos >= length) return -1;
                entryMsb = (byte)(data[pos++] & 0x7F);
            }

            // Optional data-entry LSB
            byte entryLsb = 0;
            if (flagK)
            {
                if (pos >= length) return -1;
                entryLsb = (byte)(data[pos++] & 0x7F);
            }

            // Skip a-button (L flag, 2 bytes)
            if (flagL) { pos += 2; }

            // Skip c-button (M flag, 2 bytes)
            if (flagM) { pos += 2; }

            // Skip count (N flag, 1 byte)
            if (flagN) { pos += 1; }

            // Reconstruct the CC sequence:
            //   CC99+CC98 (NRPN) or CC101+CC100 (RPN), then CC6 and/or CC38
            if (itemIsNrpn)
            {
                recovered.Add([status, 99,  pnumMsb]);  // NRPN MSB
                recovered.Add([status, 98,  pnumLsb]);  // NRPN LSB
            }
            else
            {
                recovered.Add([status, 101, pnumMsb]);  // RPN MSB
                recovered.Add([status, 100, pnumLsb]);  // RPN LSB
            }

            if (flagJ) recovered.Add([status, 6,  entryMsb]); // Data Entry MSB
            if (flagK) recovered.Add([status, 38, entryLsb]); // Data Entry LSB
        }

        return length;
    }
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

    /// <summary>
    /// Encodes Chapter F (System Common) — variable length (RFC 6295 §B.3).
    ///
    ///   Byte 0:  S | D(=0) | V | Q | F | X(=0) | P(=0) | C(=0)
    ///     D=1 → MTC Quarter Frame data byte follows (1 byte)
    ///     V=1 → Song Position Pointer follows (2 bytes: LSB, MSB)
    ///     Q=1 → Song Select follows (1 byte)
    /// </summary>
    public byte[] EncodeChapterF(bool isLast)
    {
        int size = 1
            + (HasTimeCode    ? 1 : 0)
            + (HasSongPosition ? 2 : 0)
            + (HasSongSelect  ? 1 : 0);

        var buf = new byte[size];
        buf[0] = (byte)(
            (isLast         ? 0x80 : 0) |
            (HasTimeCode    ? 0x40 : 0) |  // D flag
            (HasSongPosition ? 0x20 : 0) | // V flag
            (HasSongSelect  ? 0x10 : 0));  // Q flag

        int offset = 1;
        if (HasTimeCode)    { buf[offset++] = TimeCode; }
        if (HasSongPosition) { buf[offset++] = SongPositionLsb; buf[offset++] = SongPositionMsb; }
        if (HasSongSelect)  { buf[offset++] = SongSelect; }

        return buf;
    }

    // -----------------------------------------------------------------------
    // Decoding
    // -----------------------------------------------------------------------

    /// <summary>
    /// Decodes Chapter F and appends the recovered system-common messages
    /// to <paramref name="recovered"/>.
    /// Returns the number of bytes consumed, or -1 on error.
    /// </summary>
    public static int DecodeChapterF(ReadOnlySpan<byte> data, List<byte[]> recovered)
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
}
