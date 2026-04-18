namespace Haukcode.RtpMidi;

/// <summary>
/// Tracks the per-channel MIDI state required to encode recovery journal chapters
/// P, C, W, N, Q, T, and A (RFC 6295 Appendix A).
///
/// Updated each time a MIDI message is sent on this channel.  The accumulated
/// state is serialised into a channel journal by
/// <see cref="RtpMidiJournal.EncodeChannelJournal"/>.
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

    /// <summary>True when Bank Select Coarse (CC 0) has been sent.</summary>
    public bool HasBankCoarse { get; private set; }

    /// <summary>Most-recent Bank Select Coarse value (CC 0, 0–127).</summary>
    public byte BankCoarse { get; private set; }

    /// <summary>True when Bank Select Fine (CC 32) has been sent.</summary>
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
    // Chapter N — Note Off
    // -----------------------------------------------------------------------

    private readonly byte[] noteOffVel    = new byte[128];
    private readonly bool[] noteOffActive = new bool[128];

    /// <summary>True when at least one Note Off has been tracked on this channel.</summary>
    public bool HasNoteOff { get; private set; }

    // -----------------------------------------------------------------------
    // Chapter Q — Note On
    // -----------------------------------------------------------------------

    private readonly byte[] noteOnVel    = new byte[128];
    private readonly bool[] noteOnActive = new bool[128];

    /// <summary>True when at least one note is currently "on" on this channel.</summary>
    public bool HasNoteOn { get; private set; }

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
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Returns true if any chapter data has been accumulated since the last reset.</summary>
    public bool HasAnyData =>
        HasProgram || HasControlChange || HasPitchWheel ||
        HasNoteOff || HasNoteOn || HasChannelPressure || HasPolyPressure;

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
                    byte vel  = (byte)(midiData[2] & 0x7F);
                    noteOffVel[note]    = vel;
                    noteOffActive[note] = true;
                    noteOnActive[note]  = false;
                    HasNoteOff = true;
                }
                break;

            case 0x90: // Note On (velocity 0 = Note Off)
                if (midiData.Length >= 3)
                {
                    byte note = (byte)(midiData[1] & 0x7F);
                    byte vel  = (byte)(midiData[2] & 0x7F);
                    if (vel == 0)
                    {
                        noteOffVel[note]    = 0;
                        noteOffActive[note] = true;
                        noteOnActive[note]  = false;
                        HasNoteOff = true;
                    }
                    else
                    {
                        noteOnVel[note]    = vel;
                        noteOnActive[note] = true;
                        noteOffActive[note] = false;
                        HasNoteOn = true;
                    }
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

    // -----------------------------------------------------------------------
    // Chapter encoding
    // -----------------------------------------------------------------------

    /// <summary>
    /// Encodes Chapter P (Program Change) — 3 bytes (RFC 6295 §A.7).
    ///
    ///   Byte 0:  S | B | PROG[6:1]
    ///   Byte 1:  PROG[0] | X | BC[6:1]
    ///   Byte 2:  BC[0] | BF[6:0]
    ///
    /// S=1 when this is the last chapter in the channel journal.
    /// B=1 when bank-select info is valid. X=1 when bank-coarse data is active.
    /// </summary>
    public byte[] EncodeChapterP(bool isLast)
    {
        bool bankValid  = HasBankCoarse || HasBankFine;
        byte prog = Program;
        byte bc   = BankCoarse;
        byte bf   = BankFine;

        return
        [
            (byte)((isLast ? 0x80 : 0) | (bankValid ? 0x40 : 0) | ((prog >> 1) & 0x3F)),
            (byte)(((prog & 1) << 7) | (HasBankCoarse ? 0x40 : 0) | ((bc >> 1) & 0x3F)),
            (byte)(((bc & 1) << 7) | (bf & 0x7F))
        ];
    }

    /// <summary>
    /// Encodes Chapter C (Control Change) — 1-byte header + 2 bytes per controller (RFC 6295 §A.4).
    ///
    ///   Header byte:  S | ALT(=0) | LEN[5:0]
    ///   Each entry:   SFLAG | NUM[6:0] | VALUE[7:0]
    ///
    /// LEN is the number of controller entries (max 63 to fit 6-bit field).
    /// SFLAG=1 on the last entry.  ALT=0 (standard log mode).
    /// </summary>
    public byte[] EncodeChapterC(bool isLast)
    {
        // Collect active controllers (up to 63 — 6-bit LEN field)
        var entries = new List<(byte cc, byte val)>(64);
        for (int i = 0; i < 128 && entries.Count < 63; i++)
        {
            if (ccActive[i])
                entries.Add(((byte)i, ccValues[i]));
        }

        int count = entries.Count;
        var buf = new byte[1 + count * 2];
        buf[0] = (byte)((isLast ? 0x80 : 0) | (count & 0x3F));

        for (int i = 0; i < count; i++)
        {
            bool lastEntry = i == count - 1;
            buf[1 + i * 2]     = (byte)((lastEntry ? 0x80 : 0) | (entries[i].cc & 0x7F));
            buf[1 + i * 2 + 1] = entries[i].val;
        }

        return buf;
    }

    /// <summary>
    /// Encodes Chapter W (Pitch Wheel) — 2 bytes (RFC 6295 §A.6).
    ///
    ///   Byte 0:  S | FIRST[6:0]   (FIRST = LSB of Pitch Wheel, data byte 1)
    ///   Byte 1:  SECOND[7:0]      (SECOND = MSB of Pitch Wheel, data byte 2)
    /// </summary>
    public byte[] EncodeChapterW(bool isLast)
    {
        return
        [
            (byte)((isLast ? 0x80 : 0) | (PitchLsb & 0x7F)),
            PitchMsb
        ];
    }

    /// <summary>
    /// Encodes Chapter N (Note Off) — 1-byte header + 2 bytes per note (RFC 6295 §A.5).
    ///
    ///   Header byte:  S | B(=0) | LEN[5:0]
    ///   Each entry:   SFLAG | NOTE[6:0] | VELOCITY[7:0]
    ///
    /// B=0 (list mode; extended history bitfield not used).
    /// SFLAG=1 on the last entry.
    /// </summary>
    public byte[] EncodeChapterN(bool isLast)
    {
        var entries = new List<(byte note, byte vel)>(32);
        for (int i = 0; i < 128 && entries.Count < 63; i++)
        {
            if (noteOffActive[i])
                entries.Add(((byte)i, noteOffVel[i]));
        }

        int count = entries.Count;
        var buf = new byte[1 + count * 2];
        buf[0] = (byte)((isLast ? 0x80 : 0) | (count & 0x3F));

        for (int i = 0; i < count; i++)
        {
            bool lastEntry = i == count - 1;
            buf[1 + i * 2]     = (byte)((lastEntry ? 0x80 : 0) | (entries[i].note & 0x7F));
            buf[1 + i * 2 + 1] = entries[i].vel;
        }

        return buf;
    }

    /// <summary>
    /// Encodes Chapter Q (Note On) — 1-byte header + 2 bytes per note (RFC 6295 §A.8).
    ///
    ///   Header byte:  S | Y(=0) | LEN[5:0]
    ///   Each entry:   SFLAG | NOTE[6:0] | OFFS(=0) | VEL[6:0]
    ///
    /// Y=0 (no low-velocity notes encoded with timing offset).
    /// SFLAG=1 on the last entry.
    /// </summary>
    public byte[] EncodeChapterQ(bool isLast)
    {
        var entries = new List<(byte note, byte vel)>(32);
        for (int i = 0; i < 128 && entries.Count < 63; i++)
        {
            if (noteOnActive[i])
                entries.Add(((byte)i, noteOnVel[i]));
        }

        int count = entries.Count;
        var buf = new byte[1 + count * 2];
        buf[0] = (byte)((isLast ? 0x80 : 0) | (count & 0x3F));

        for (int i = 0; i < count; i++)
        {
            bool lastEntry = i == count - 1;
            buf[1 + i * 2]     = (byte)((lastEntry ? 0x80 : 0) | (entries[i].note & 0x7F));
            buf[1 + i * 2 + 1] = (byte)(entries[i].vel & 0x7F); // OFFS=0, VEL in bits 6:0
        }

        return buf;
    }

    /// <summary>
    /// Encodes Chapter T (Channel Pressure) — 1 byte (RFC 6295 §A.9).
    ///
    ///   Byte 0:  S | PRESSURE[6:0]
    /// </summary>
    public byte[] EncodeChapterT(bool isLast)
    {
        return [(byte)((isLast ? 0x80 : 0) | (ChannelPressure & 0x7F))];
    }

    /// <summary>
    /// Encodes Chapter A (Poly Key Pressure) — 1-byte header + 2 bytes per note (RFC 6295 §A.2).
    ///
    ///   Header byte:  S | X(=0) | LEN[5:0]
    ///   Each entry:   SFLAG | NOTE[6:0] | PRESSURE[7:0]
    ///
    /// X=0 (standard list mode).  SFLAG=1 on the last entry.
    /// </summary>
    public byte[] EncodeChapterA(bool isLast)
    {
        var entries = new List<(byte note, byte pressure)>(32);
        for (int i = 0; i < 128 && entries.Count < 63; i++)
        {
            if (polyPressureActive[i])
                entries.Add(((byte)i, polyPressure[i]));
        }

        int count = entries.Count;
        var buf = new byte[1 + count * 2];
        buf[0] = (byte)((isLast ? 0x80 : 0) | (count & 0x3F));

        for (int i = 0; i < count; i++)
        {
            bool lastEntry = i == count - 1;
            buf[1 + i * 2]     = (byte)((lastEntry ? 0x80 : 0) | (entries[i].note & 0x7F));
            buf[1 + i * 2 + 1] = entries[i].pressure;
        }

        return buf;
    }

    // -----------------------------------------------------------------------
    // Chapter decoding  (static: parse bytes → MIDI messages)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Decodes Chapter P and appends the recovered Program Change (and optional
    /// bank-select Control Changes) to <paramref name="recovered"/>.
    /// Returns the number of bytes consumed, or -1 on error.
    /// </summary>
    public static int DecodeChapterP(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.Length < 3) return -1;

        byte b0 = data[0], b1 = data[1], b2 = data[2];
        bool bankValid  = (b0 & 0x40) != 0;
        byte prog       = (byte)(((b0 & 0x3F) << 1) | ((b1 >> 7) & 1));
        bool bankCoarseActive = (b1 & 0x40) != 0;
        byte bc         = (byte)(((b1 & 0x3F) << 1) | ((b2 >> 7) & 1));
        byte bf         = (byte)(b2 & 0x7F);

        if (bankValid && bankCoarseActive)
            recovered.Add([(byte)(0xB0 | (channel & 0x0F)), 0, bc]);   // CC 0

        if (bankValid && HasBankFineFromChapterP(b0, b1, b2))
            recovered.Add([(byte)(0xB0 | (channel & 0x0F)), 32, bf]);  // CC 32

        recovered.Add([(byte)(0xC0 | (channel & 0x0F)), prog]);        // Program Change

        return 3;
    }

    // Bank fine is always emitted together with bank coarse when B=1; the RFC does not
    // provide a separate "bank fine valid" bit, so we always include both CC 0 and CC 32.
    private static bool HasBankFineFromChapterP(byte b0, byte b1, byte b2)
        => (b0 & 0x40) != 0; // B flag set — bank info is present

    /// <summary>
    /// Decodes Chapter C and appends the recovered Control Change messages
    /// to <paramref name="recovered"/>.
    /// Returns the number of bytes consumed, or -1 on error.
    /// </summary>
    public static int DecodeChapterC(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.IsEmpty) return -1;

        byte header = data[0];
        int count = header & 0x3F;
        int required = 1 + count * 2;
        if (data.Length < required) return -1;

        for (int i = 0; i < count; i++)
        {
            byte cc  = (byte)(data[1 + i * 2] & 0x7F);
            byte val = data[1 + i * 2 + 1];
            recovered.Add([(byte)(0xB0 | (channel & 0x0F)), cc, val]);
        }

        return required;
    }

    /// <summary>
    /// Decodes Chapter W and appends the recovered Pitch Wheel message
    /// to <paramref name="recovered"/>.
    /// Returns the number of bytes consumed, or -1 on error.
    /// </summary>
    public static int DecodeChapterW(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.Length < 2) return -1;

        byte lsb = (byte)(data[0] & 0x7F);
        byte msb = data[1];
        recovered.Add([(byte)(0xE0 | (channel & 0x0F)), lsb, msb]);

        return 2;
    }

    /// <summary>
    /// Decodes Chapter N (Note Off) and appends the recovered Note Off messages
    /// to <paramref name="recovered"/>.
    /// Returns the number of bytes consumed, or -1 on error.
    /// </summary>
    public static int DecodeChapterN(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.IsEmpty) return -1;

        byte header = data[0];
        bool extendedBitfield = (header & 0x40) != 0; // B flag
        // Extended bitfield mode (B=1) is not yet implemented; return -1 so the
        // caller skips remaining chapters for this channel rather than misinterpreting
        // the bitfield bytes as chapter data.
        if (extendedBitfield) return -1;

        int count = header & 0x3F;
        int required = 1 + count * 2;
        if (data.Length < required) return -1;

        for (int i = 0; i < count; i++)
        {
            byte note = (byte)(data[1 + i * 2] & 0x7F);
            byte vel  = data[1 + i * 2 + 1];
            recovered.Add([(byte)(0x80 | (channel & 0x0F)), note, vel]);
        }

        return required;
    }

    /// <summary>
    /// Decodes Chapter Q (Note On) and appends the recovered Note On messages
    /// to <paramref name="recovered"/>.
    /// Returns the number of bytes consumed, or -1 on error.
    /// </summary>
    public static int DecodeChapterQ(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.IsEmpty) return -1;

        byte header = data[0];
        int count = header & 0x3F;
        int required = 1 + count * 2;
        if (data.Length < required) return -1;

        for (int i = 0; i < count; i++)
        {
            byte note = (byte)(data[1 + i * 2] & 0x7F);
            byte vel  = (byte)(data[1 + i * 2 + 1] & 0x7F); // strip OFFS flag
            recovered.Add([(byte)(0x90 | (channel & 0x0F)), note, vel]);
        }

        return required;
    }

    /// <summary>
    /// Decodes Chapter T (Channel Pressure) and appends the recovered message
    /// to <paramref name="recovered"/>.
    /// Returns the number of bytes consumed, or -1 on error.
    /// </summary>
    public static int DecodeChapterT(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.IsEmpty) return -1;

        byte pressure = (byte)(data[0] & 0x7F);
        recovered.Add([(byte)(0xD0 | (channel & 0x0F)), pressure]);

        return 1;
    }

    /// <summary>
    /// Decodes Chapter A (Poly Key Pressure) and appends the recovered messages
    /// to <paramref name="recovered"/>.
    /// Returns the number of bytes consumed, or -1 on error.
    /// </summary>
    public static int DecodeChapterA(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.IsEmpty) return -1;

        byte header = data[0];
        int count = header & 0x3F;
        int required = 1 + count * 2;
        if (data.Length < required) return -1;

        for (int i = 0; i < count; i++)
        {
            byte note     = (byte)(data[1 + i * 2] & 0x7F);
            byte pressure = data[1 + i * 2 + 1];
            recovered.Add([(byte)(0xA0 | (channel & 0x0F)), note, pressure]);
        }

        return required;
    }
}

/// <summary>
/// Tracks the system-level MIDI state required for the Chapter F recovery journal
/// (RFC 6295 §A.11).  Chapter F covers MIDI System Common messages:
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
    /// Encodes Chapter F (System Common) — variable length (RFC 6295 §A.11).
    ///
    ///   Byte 0:  S | D(=0) | V | Q | F | X(=0) | P(=0) | C(=0)
    ///     D=1 → MTC Quarter Frame data byte follows (1 byte)
    ///     V=1 → Song Position Pointer follows (2 bytes: LSB, MSB)
    ///     Q=1 → Song Select follows (1 byte)
    ///
    /// S and X are part of the system journal header — they are set externally;
    /// this method sets D, V, Q based on tracked state.
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
