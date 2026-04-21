using Haukcode.RtpMidi.Journal.State;

namespace Haukcode.RtpMidi.Journal.Chapters;

/// <summary>
/// Chapter N — MIDI NoteOn and NoteOff (RFC 6295 §A.6).
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
/// <c>isLastChapterInJournal</c> parameter is accepted for interface
/// uniformity with other chapter encoders but is not used by this encoder.
///
/// Special sentinel: LEN=127 with LOW=15, HIGH=0 codes a 128-entry note log
/// with empty OFFBITS (the only way to express count=128 in a 7-bit field).
/// </remarks>
internal sealed class ChapterNCodec : IChapterCodec
{
    private readonly NoteState _state;

    public ChapterNCodec(NoteState state) => _state = state;

    public char ChapterId => 'N';
    public bool HasData   => _state.HasAnyData;

    public byte[] Encode(bool isLastChapterInJournal)
    {
        ReadOnlySpan<bool> onActive  = _state.NoteOnActive;
        ReadOnlySpan<byte> onVel     = _state.NoteOnVelocities;
        ReadOnlySpan<bool> offActive = _state.NoteOffActive;

        // ── Gather the note log list (notes whose most-recent event is Note On) ──
        Span<(byte note, byte vel)> logEntries = stackalloc (byte, byte)[128];
        int logCount = 0;
        for (int i = 0; i < 128; i++)
        {
            if (onActive[i])
                logEntries[logCount++] = ((byte)i, onVel[i]);
        }

        // ── Compute OFFBITS coverage (octet range containing any pending Note Offs) ──
        int lowOct = 16, highOct = -1;
        for (int oct = 0; oct < 16; oct++)
        {
            bool any = false;
            for (int b = 0; b < 8; b++)
            {
                if (offActive[oct * 8 + b]) { any = true; break; }
            }
            if (any)
            {
                if (oct < lowOct)  lowOct  = oct;
                if (oct > highOct) highOct = oct;
            }
        }

        bool hasOffbits   = highOct >= lowOct;
        int  offbitsBytes = hasOffbits ? (highOct - lowOct + 1) : 0;

        // Header byte 1 encodes the OFFBITS range. §A.6.1 "empty OFFBITS"
        // sentinels: LOW=15 with HIGH=0 or HIGH=1.
        byte low, high;
        if (hasOffbits) { low = (byte)lowOct; high = (byte)highOct; }
        else            { low = 15;           high = 0; }

        // Count=128 can only be expressed by the LEN=127,LOW=15,HIGH=0 sentinel,
        // which also forces empty OFFBITS. (We can still have ≤128 log entries
        // + OFFBITS for counts ≤127.)
        byte lenField;
        if (logCount == 128)
        {
            lenField     = 127;
            low          = 15;
            high         = 0;
            hasOffbits   = false;
            offbitsBytes = 0;
        }
        else
        {
            lenField = (byte)logCount;
        }

        int total = 2 + logCount * 2 + offbitsBytes;
        var buf = new byte[total];

        // Header (2 octets). B (byte 0 bit 7) is always 1 per §A.6.1; isLast is
        // intentionally ignored — see the <remarks> on this class.
        buf[0] = (byte)(0x80 | (lenField & 0x7F));
        buf[1] = (byte)(((low & 0x0F) << 4) | (high & 0x0F));

        int pos = 2;
        for (int i = 0; i < logCount; i++)
        {
            bool lastEntry = i == logCount - 1;
            buf[pos++] = (byte)((lastEntry ? 0x80 : 0) | (logEntries[i].note & 0x7F));
            // Y = 0 (no playback hint); velocity in low 7 bits, never 0.
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
                    if (offActive[oct * 8 + bit])
                        b |= (byte)(0x80 >> bit);
                }
                buf[pos++] = b;
            }
        }

        return buf;
    }

    public int Decode(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => DecodeStatic(data, channel, recovered);

    public static int DecodeStatic(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.Length < 2) return -1;

        byte header0 = data[0];
        byte header1 = data[1];
        // B bit (0x80 of byte 0) is informational; ignore for recovery.
        int logCount = header0 & 0x7F;
        int low      = (header1 >> 4) & 0x0F;
        int high     =  header1       & 0x0F;

        // Special sentinel: LEN=127 && LOW=15 && HIGH=0 codes logCount=128 and empty OFFBITS.
        bool logCount128Special = (logCount == 127) && (low == 15) && (high == 0);
        int  effectiveLogCount  = logCount128Special ? 128 : logCount;

        // Determine OFFBITS octet count. Sentinels (LOW=15,HIGH=0) and
        // (LOW=15,HIGH=1) code "no OFFBITS". Any other LOW > HIGH is illegal
        // per §A.6.1 but we treat it defensively as no OFFBITS.
        int offbitsBytes;
        if (logCount128Special)          offbitsBytes = 0;
        else if (low == 15 && high <= 1) offbitsBytes = 0;
        else if (low <= high)            offbitsBytes = (high - low + 1);
        else                             offbitsBytes = 0;

        int required = 2 + effectiveLogCount * 2 + offbitsBytes;
        if (data.Length < required) return -1;

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
                        recovered.Add([status80, (byte)note, 0]);
                }
            }
        }

        return required;
    }

    public void Reset() { }
}
