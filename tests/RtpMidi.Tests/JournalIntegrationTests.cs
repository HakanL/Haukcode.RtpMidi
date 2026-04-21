using Haukcode.RtpMidi;

namespace RtpMidi.Tests;

/// <summary>
/// Multi-chapter integration tests for the recovery journal.
///
/// Per-chapter tests in <see cref="ChannelJournalTests"/> pin each chapter's
/// own byte layout. These tests pin the *interactions* that span multiple
/// chapters or channels — the parts most likely to regress during the
/// journal codec refactor:
///
///   • Channel-journal TOC byte ordering (P|C|M|W|N|E|T|A).
///   • LENGTH field semantics (total incl. 3-byte header, RFC §A.1).
///   • "Last chapter" S-bit placement across chapters within one channel.
///   • "Last channel" S-bit placement across channel journals.
///   • System-journal + channel-journal coexistence (journal header S/A bits).
///   • Full round-trip recovery for a realistic mixed-state journal.
///   • A golden-vector byte-for-byte match so any wire-format drift fails loud.
/// </summary>
public class JournalIntegrationTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ChannelMidiState[] EmptyStates()
    {
        var s = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) s[i] = new ChannelMidiState();
        return s;
    }

    // Channel-journal TOC bit positions (mirrors RtpMidiJournal private consts).
    private const byte TocP = 0x80, TocC = 0x40, TocM = 0x20, TocW = 0x10,
                       TocN = 0x08, TocE = 0x04, TocT = 0x02, TocA = 0x01;

    private const byte JournalHeaderSystemPresent  = 0x80;
    private const byte JournalHeaderChannelPresent = 0x20;

    /// <summary>
    /// Parses a full journal and returns a dictionary of channel index → TOC bits
    /// from the encoded channel journal headers.
    /// </summary>
    private static (byte[] toc, int[] lengths, bool[] sBits, byte[] chans) ReadChannelHeaders(byte[] journal)
    {
        // Journal header is 3 bytes; assume no system journal for this helper.
        int totChan = (journal[0] & 0x0F) + 1;
        var toc      = new byte[totChan];
        var lengths  = new int[totChan];
        var sBits    = new bool[totChan];
        var chans    = new byte[totChan];

        int pos = 3;
        for (int i = 0; i < totChan; i++)
        {
            sBits[i]   = (journal[pos] & 0x80) != 0;
            chans[i]   = (byte)((journal[pos] >> 3) & 0x0F);
            lengths[i] = ((journal[pos] & 0x03) << 8) | journal[pos + 1];
            toc[i]     = journal[pos + 2];
            pos += lengths[i];
        }

        return (toc, lengths, sBits, chans);
    }

    // -------------------------------------------------------------------------
    // TOC byte — presence flag order (RFC 6295 §5 Figure 9: P|C|M|W|N|E|T|A)
    // -------------------------------------------------------------------------

    [Fact]
    public void ChannelJournal_AllChapters_TocByteMatchesRfcOrder()
    {
        // Accumulate one of each chapter's state on channel 5.
        var states = EmptyStates();
        var s = states[5];
        s.ProcessMidi([0xC5, 42]);           // P: Program Change
        s.ProcessMidi([0xB5,  7, 100]);      // C: Control Change (vol)
        s.ProcessMidi([0xB5, 101, 0]);       // M: RPN MSB (pending — triggers HasParameterSystem)
        s.ProcessMidi([0xB5, 100, 0]);       // M: RPN LSB — finalize
        s.ProcessMidi([0xE5,  0, 64]);       // W: Pitch Wheel
        s.ProcessMidi([0x95, 60, 100]);      // N: Note On
        s.ProcessMidi([0xD5, 90]);           // T: Channel Pressure
        s.ProcessMidi([0xA5, 72, 50]);       // A: Poly Key Pressure

        var journal = RtpMidiJournal.EncodeFullJournal(0x1234, null, states, null);

        var (toc, _, _, chans) = ReadChannelHeaders(journal);

        // Exactly one channel journal, channel 5.
        Assert.Single(toc);
        Assert.Equal(5, chans[0]);

        // All seven implemented chapter bits set; E (reserved) clear.
        byte expected = TocP | TocC | TocM | TocW | TocN | TocT | TocA;
        Assert.Equal(expected, toc[0]);
        Assert.Equal(0, toc[0] & TocE);
    }

    [Fact]
    public void ChannelJournal_SparseChapters_TocByteOnlyMarksPresent()
    {
        // Only Chapter P and Chapter T populated → TOC = TocP | TocT.
        var states = EmptyStates();
        states[3].ProcessMidi([0xC3, 10]);
        states[3].ProcessMidi([0xD3, 64]);

        var journal = RtpMidiJournal.EncodeFullJournal(0, null, states, null);

        var (toc, _, _, _) = ReadChannelHeaders(journal);
        Assert.Equal(TocP | TocT, toc[0]);
        Assert.Equal(0, toc[0] & (TocC | TocM | TocW | TocN | TocE | TocA));
    }

    // -------------------------------------------------------------------------
    // LENGTH field — RFC 6295 §A.1: total bytes INCLUDING the 3-byte header.
    // -------------------------------------------------------------------------

    [Fact]
    public void ChannelJournal_LengthField_IncludesThreeByteHeader()
    {
        // Chapter P only (3-byte body) → LENGTH should be 3 (header) + 3 (P) = 6.
        var states = EmptyStates();
        states[0].ProcessMidi([0xC0, 5]);

        var journal = RtpMidiJournal.EncodeFullJournal(0, null, states, null);

        var (_, lengths, _, _) = ReadChannelHeaders(journal);
        Assert.Equal(6, lengths[0]);

        // Verify the LENGTH matches the actual size of the channel journal slice
        // in the wire bytes: journal is [3-byte header] + [lengths[0] bytes].
        Assert.Equal(3 + lengths[0], journal.Length);
    }

    // -------------------------------------------------------------------------
    // "Last chapter" S-bit placement within a channel journal.
    //
    // RFC 6295 §A.1: each chapter header carries an S bit which is 1 iff this
    // chapter is the last one present in the channel journal. Chapter N uses
    // its byte-0 top bit as the "B" bit (§A.6.1), not the generic S bit, and is
    // always 1 regardless of position.
    // -------------------------------------------------------------------------

    [Fact]
    public void ChannelJournal_TwoChapters_SBitOnlyOnLastPresent()
    {
        // P followed by T: P is not last → byte 0 bit 7 = 0; T is last → bit 7 = 1.
        var states = EmptyStates();
        states[0].ProcessMidi([0xC0, 10]);    // Chapter P, program 10
        states[0].ProcessMidi([0xD0, 90]);    // Chapter T, pressure 90

        var journal = RtpMidiJournal.EncodeFullJournal(0, null, states, null);

        // Journal layout: [3 journal hdr][3 chan hdr][3 Chapter P][1 Chapter T]
        byte chapterP_byte0 = journal[6];
        byte chapterT_byte0 = journal[9];

        Assert.Equal(0, chapterP_byte0 & 0x80);     // P: S=0 (not last)
        Assert.Equal(10, chapterP_byte0 & 0x7F);    // P: program in low 7 bits
        Assert.Equal(0x80 | 90, chapterT_byte0);    // T: S=1 (last) | pressure
    }

    [Fact]
    public void ChannelJournal_ThreeChapters_SBitOnlyOnLastPresent()
    {
        // W present in the middle of P and T — W should NOT carry the S bit.
        var states = EmptyStates();
        states[0].ProcessMidi([0xC0, 10]);
        states[0].ProcessMidi([0xE0,  0, 64]);
        states[0].ProcessMidi([0xD0, 50]);

        var journal = RtpMidiJournal.EncodeFullJournal(0, null, states, null);

        // Layout: [3 journal hdr][3 chan hdr][3 P][2 W][1 T] = 12 bytes total.
        byte p0 = journal[6];
        byte w0 = journal[9];
        byte t0 = journal[11];

        Assert.Equal(0, p0 & 0x80);       // P: S=0
        Assert.Equal(0, w0 & 0x80);       // W: S=0
        Assert.Equal(0x80, t0 & 0x80);    // T: S=1
    }

    [Fact]
    public void ChannelJournal_ChapterN_BBitAlwaysSet_RegardlessOfPosition()
    {
        // Chapter N in the middle (followed by T) — B bit must still be 1.
        var states = EmptyStates();
        states[0].ProcessMidi([0xC0, 10]);         // P (first, not last)
        states[0].ProcessMidi([0x90, 60, 100]);    // N (middle, not last)
        states[0].ProcessMidi([0xD0, 50]);         // T (last)

        var journal = RtpMidiJournal.EncodeFullJournal(0, null, states, null);

        // Layout: [3 jh][3 ch][3 P][4 N (2 hdr + 2 log)][1 T].
        byte n_byte0 = journal[9];
        Assert.Equal(0x80, n_byte0 & 0x80);  // B = 1 always
        Assert.Equal(1,    n_byte0 & 0x7F);  // LEN = 1 note in log

        // And byte 0 of the chapter following N (T) carries the S bit.
        byte t_byte0 = journal[13];
        Assert.Equal(0x80, t_byte0 & 0x80);
    }

    [Fact]
    public void ChannelJournal_ChapterN_WhenActuallyLast_BBitStillSet()
    {
        // N is the only chapter and therefore last — B bit must still be 1
        // (confirms the discarded `isLast` parameter does not influence B).
        var states = EmptyStates();
        states[0].ProcessMidi([0x90, 60, 100]);

        var journal = RtpMidiJournal.EncodeFullJournal(0, null, states, null);

        byte n_byte0 = journal[6];
        Assert.Equal(0x80, n_byte0 & 0x80);  // B = 1
    }

    // -------------------------------------------------------------------------
    // "Last channel" S-bit placement across channel journals.
    // -------------------------------------------------------------------------

    [Fact]
    public void MultiChannelJournal_SBitOnlyOnLastChannel()
    {
        // Two channels, each with just Chapter P. Only the second channel
        // journal's byte-0 bit-7 should be set.
        var states = EmptyStates();
        states[2].ProcessMidi([0xC2, 10]);
        states[9].ProcessMidi([0xC9, 20]);

        var journal = RtpMidiJournal.EncodeFullJournal(0, null, states, null);

        var (_, lengths, sBits, chans) = ReadChannelHeaders(journal);

        Assert.Equal(2, lengths.Length);
        Assert.Equal(2, chans[0]);
        Assert.Equal(9, chans[1]);
        Assert.False(sBits[0]);  // first channel: S = 0
        Assert.True(sBits[1]);   // last channel:  S = 1

        // Journal header TOTCHAN = count - 1
        Assert.Equal(2 - 1, journal[0] & 0x0F);
        Assert.Equal(JournalHeaderChannelPresent, journal[0] & 0xE0); // A=1, S=0
    }

    // -------------------------------------------------------------------------
    // System + channel coexistence (journal header S and A bits both set).
    // -------------------------------------------------------------------------

    [Fact]
    public void JournalHeader_SystemPlusChannel_BothFlagsSet()
    {
        var sysState = new SystemMidiState();
        sysState.ProcessMidi([0xF1, 0x12]);          // MTC Quarter Frame

        var states = EmptyStates();
        states[0].ProcessMidi([0xC0, 42]);           // Chapter P on channel 0

        var journal = RtpMidiJournal.EncodeFullJournal(0x4242, null, states, sysState);

        // Journal header byte 0: S=1 (system present), A=1 (channel present),
        // TOTCHAN = 0 (1 channel).
        Assert.Equal(JournalHeaderSystemPresent | JournalHeaderChannelPresent,
                     journal[0] & 0xE0);
        Assert.Equal(0, journal[0] & 0x0F);
        Assert.Equal(0x42, journal[1]);
        Assert.Equal(0x42, journal[2]);

        // Byte 3 = system journal header with Chapter F flag set.
        Assert.Equal(0x40, journal[3] & 0x40);
    }

    [Fact]
    public void SystemJournal_ChapterF_LastBitCleared_WhenSysExFollows()
    {
        // When Chapter F is followed by Chapter X in the system journal, F's
        // S bit must be 0 (X is the last chapter). When X is absent, F's S = 1.
        var sysState = new SystemMidiState();
        sysState.ProcessMidi([0xF3, 7]); // Song Select

        byte[] sysex = [0xF0, 0x7E, 0x00, 0xF7];

        // Case 1: F + X → F.S = 0
        var withX    = RtpMidiJournal.EncodeFullJournal(0, sysex, EmptyStates(), sysState);
        // Case 2: F only → F.S = 1
        var withoutX = RtpMidiJournal.EncodeFullJournal(0, null,  EmptyStates(), sysState);

        // Layout: [3 jh][1 sys hdr][Chapter F: 1 hdr + 1 song select] [ Chapter X: 2 hdr + 4 data ]
        byte fByte0_withX    = withX[4];
        byte fByte0_withoutX = withoutX[4];

        Assert.Equal(0, fByte0_withX & 0x80);
        Assert.Equal(0x80, fByte0_withoutX & 0x80);
    }

    // -------------------------------------------------------------------------
    // Round-trip recovery — the ultimate integration assertion.
    // -------------------------------------------------------------------------

    [Fact]
    public void FullJournal_RoundTrip_RecoversAllChapterMessages()
    {
        var sysState = new SystemMidiState();
        sysState.ProcessMidi([0xF1, 0x0F]);             // MTC Quarter Frame
        sysState.ProcessMidi([0xF3, 3]);                // Song Select

        var states = EmptyStates();
        states[5].ProcessMidi([0xB5,  0, 3]);           // CC 0 (bank coarse)
        states[5].ProcessMidi([0xB5, 32, 7]);           // CC 32 (bank fine)
        states[5].ProcessMidi([0xC5, 42]);              // Program Change
        states[5].ProcessMidi([0xB5,  7, 100]);         // CC 7 (volume)
        states[5].ProcessMidi([0xE5,  0, 64]);          // Pitch Wheel center
        states[5].ProcessMidi([0x95, 60, 100]);         // Note On
        states[5].ProcessMidi([0x85, 62, 0]);           // Note Off
        states[5].ProcessMidi([0xD5, 77]);              // Channel Pressure
        states[5].ProcessMidi([0xA5, 72, 50]);          // Poly Pressure

        var journal = RtpMidiJournal.EncodeFullJournal(0x1234, null, states, sysState);

        Assert.True(RtpMidiJournal.TryParseFullJournal(journal, out var seq, out var recovered));
        Assert.Equal(0x1234, seq);
        Assert.NotNull(recovered);

        // System Common recovered
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xF1, 0x0F }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xF3, 3 }));

        // Channel 5 recovery
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xB5, 0,  3 }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xB5, 32, 7 }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xC5, 42 }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xB5, 7, 100 }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xE5, 0, 64 }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0x95, 60, 100 }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0x85, 62,  0 }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xD5, 77 }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xA5, 72, 50 }));
    }

    [Fact]
    public void FullJournal_TwoChannels_RoundTripsBothIndependently()
    {
        var states = EmptyStates();
        states[1].ProcessMidi([0xC1, 10]);             // ch 1: Program 10
        states[1].ProcessMidi([0x91, 60, 100]);        // ch 1: Note On
        states[7].ProcessMidi([0xB7, 11, 127]);        // ch 7: CC 11
        states[7].ProcessMidi([0xD7, 64]);             // ch 7: Channel Pressure

        var journal = RtpMidiJournal.EncodeFullJournal(0, null, states, null);

        Assert.True(RtpMidiJournal.TryParseFullJournal(journal, out _, out var recovered));

        // Channel 1 messages recovered with correct status nibble
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xC1, 10 }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0x91, 60, 100 }));

        // Channel 7 messages recovered with correct status nibble
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xB7, 11, 127 }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xD7, 64 }));
    }

    // -------------------------------------------------------------------------
    // Golden vector — exact byte sequence for a mixed-state journal.
    //
    // Any refactor that changes the wire format for this specific input will
    // fail this test. Derived from RFC 6295 Appendix A by hand; see inline
    // commentary byte-by-byte.
    // -------------------------------------------------------------------------

    [Fact]
    public void GoldenVector_MixedChannelJournal_ByteForByteMatch()
    {
        var states = EmptyStates();
        var s = states[5];
        s.ProcessMidi([0xC5, 10]);            // Program Change → P
        s.ProcessMidi([0xB5,  7, 100]);       // CC 7 = 100     → C
        s.ProcessMidi([0xE5,  0, 64]);        // Pitch wheel    → W
        s.ProcessMidi([0x95, 60, 100]);       // Note On 60/100 → N

        var journal = RtpMidiJournal.EncodeFullJournal(0x0001, null, states, null);

        // Expected layout:
        //   [3 journal hdr] [3 channel hdr] [3 P] [3 C] [2 W] [4 N] = 18 bytes
        byte[] expected =
        [
            // ── Recovery journal header (3 bytes, §5.1) ──
            0x20,                              // S=0, A=1, TOTCHAN=0
            0x00, 0x01,                        // checkpoint = 0x0001

            // ── Channel journal header (3 bytes, §5 Fig 9) ──
            // byte 0: S=1 (last channel) | CHAN=5 (0101) << 3 | LEN[9:8]=00
            //         = 0x80 | 0x28 | 0x00 = 0xA8
            0xA8,
            // byte 1: LEN[7:0] = 18 bytes total - 3 journal hdr = 15 channel bytes
            0x0F,
            // byte 2: TOC = P | C | W | N = 0x80 | 0x40 | 0x10 | 0x08 = 0xD8
            0xD8,

            // ── Chapter P (3 bytes, §A.2) — S=0 (not last) ──
            0x0A,  // S=0 | program 10
            0x00,  // B=0 | bank MSB 0
            0x00,  // X=0 | bank LSB 0

            // ── Chapter C (3 bytes, §A.3) — S=0, 1 entry ──
            0x00,  // S=0 | LEN=(count-1)=0
            0x87,  // S=1 (last entry) | CC 7
            0x64,  // A=0 | value 100

            // ── Chapter W (2 bytes, §A.5) — S=0 ──
            0x00,  // S=0 | LSB 0
            0x40,  // R=0 | MSB 64

            // ── Chapter N (4 bytes, §A.6) — B=1 always, logCount=1, no offbits ──
            0x81,  // B=1 | LEN=1
            0xF0,  // LOW=15, HIGH=0  (empty OFFBITS sentinel)
            0xBC,  // S=1 (last log entry) | note 60
            0x64   // Y=0 | velocity 100
        ];

        Assert.Equal(expected, journal);
    }
}
