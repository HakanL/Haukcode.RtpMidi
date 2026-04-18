using Haukcode.RtpMidi;

namespace RtpMidi.Tests;

/// <summary>
/// Tests for recovery journal channel chapters V, P, C, W, N, Q, T, A
/// and system chapter F (RFC 6295 Appendix A).
/// </summary>
public class ChannelJournalTests
{
    // =========================================================================
    // Chapter P — Program Change
    // =========================================================================

    [Fact]
    public void ChapterP_Encode_RoundTrip_ProgramOnly()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0xC5, 42]); // Program Change, channel 5, program 42

        Assert.True(state.HasProgram);
        Assert.Equal(42, state.Program);

        var bytes = state.EncodeChapterP(isLast: true);
        Assert.Equal(3, bytes.Length);

        // S=1 (isLast), B=0 (no bank), prog=42
        // Byte 0: 1_0_prog[6:1] = 0x80 | (42 >> 1) = 0x80 | 0x15 = 0x95
        Assert.Equal(0x95, bytes[0]);

        // Byte 1: prog[0]_0_bc[6:1] = ((42 & 1) << 7) | 0 = 0 (prog=42 even)
        Assert.Equal(0x00, bytes[1]);

        // Byte 2: bc[0]_bf[6:0] = 0
        Assert.Equal(0x00, bytes[2]);
    }

    [Fact]
    public void ChapterP_Encode_RoundTrip_WithBank()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0xB0, 0, 3]);    // CC 0 (bank coarse) = 3
        state.ProcessMidi([0xB0, 32, 7]);   // CC 32 (bank fine) = 7
        state.ProcessMidi([0xC0, 60]);      // Program Change, program 60

        var bytes = state.EncodeChapterP(isLast: false);
        Assert.Equal(3, bytes.Length);

        // B=1, X=1 (bank coarse active), prog=60
        Assert.Equal(0, bytes[0] & 0x80); // S=0 (not last)
        Assert.NotEqual(0, bytes[0] & 0x40); // B=1

        // Decode and verify
        var recovered = new List<byte[]>();
        int consumed = ChannelMidiState.DecodeChapterP(bytes, 0, recovered);
        Assert.Equal(3, consumed);
        // Should emit: CC 0, CC 32, Program Change
        Assert.Equal(3, recovered.Count);
        Assert.Equal(new byte[] { 0xB0, 0, 3 }, recovered[0]);
        Assert.Equal(new byte[] { 0xB0, 32, 7 }, recovered[1]);
        Assert.Equal(new byte[] { 0xC0, 60 }, recovered[2]);
    }

    [Fact]
    public void ChapterP_Decode_ProgramOnly_NoBank()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0xC3, 127]); // Program 127, channel 3
        var bytes = state.EncodeChapterP(isLast: true);

        var recovered = new List<byte[]>();
        int consumed = ChannelMidiState.DecodeChapterP(bytes, 3, recovered);

        Assert.Equal(3, consumed);
        Assert.Single(recovered); // only program change
        Assert.Equal(new byte[] { 0xC3, 127 }, recovered[0]);
    }

    [Fact]
    public void ChapterP_Decode_TooShort_ReturnsMinusOne()
    {
        var recovered = new List<byte[]>();
        Assert.Equal(-1, ChannelMidiState.DecodeChapterP(new byte[2], 0, recovered));
    }

    [Fact]
    public void ChapterP_Program127_BitEncodingCorrect()
    {
        // prog=127 = 0b1111111; bits 6:1 = 0b111111 = 63, bit 0 = 1
        var state = new ChannelMidiState();
        state.ProcessMidi([0xC0, 127]);
        var bytes = state.EncodeChapterP(isLast: true);

        // Byte 0: S=1 | B=0 | prog[6:1]=0x3F → 0x80 | 0x3F = 0xBF
        Assert.Equal(0xBF, bytes[0]);
        // Byte 1: prog[0]=1 | X=0 | bc[6:1]=0 → 0x80
        Assert.Equal(0x80, bytes[1]);

        var recovered = new List<byte[]>();
        ChannelMidiState.DecodeChapterP(bytes, 0, recovered);
        Assert.Contains(recovered, m => m.SequenceEqual(new byte[] { 0xC0, 127 }));
    }

    // =========================================================================
    // Chapter C — Control Change
    // =========================================================================

    [Fact]
    public void ChapterC_Encode_SingleController_RoundTrip()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0xB2, 7, 64]); // CC 7 (volume) = 64, channel 2

        var bytes = state.EncodeChapterC(isLast: true);
        // 1 header + 2 bytes per entry = 3 bytes
        Assert.Equal(3, bytes.Length);

        // Header: S=1, ALT=0, LEN=1
        Assert.Equal(0x81, bytes[0]);
        // Entry: SFLAG=1 (last), NUM=7 → 0x80|7=0x87
        Assert.Equal(0x87, bytes[1]);
        Assert.Equal(64, bytes[2]);
    }

    [Fact]
    public void ChapterC_Decode_MultipleControllers_RoundTrip()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0xB0, 7, 100]);   // volume
        state.ProcessMidi([0xB0, 10, 64]);   // pan
        state.ProcessMidi([0xB0, 11, 80]);   // expression

        var bytes = state.EncodeChapterC(isLast: false);
        // Header + 3 entries * 2 bytes = 7
        Assert.Equal(7, bytes.Length);

        var recovered = new List<byte[]>();
        int consumed = ChannelMidiState.DecodeChapterC(bytes, 0, recovered);
        Assert.Equal(7, consumed);
        Assert.Equal(3, recovered.Count);

        Assert.Contains(recovered, m => m.SequenceEqual(new byte[] { 0xB0, 7, 100 }));
        Assert.Contains(recovered, m => m.SequenceEqual(new byte[] { 0xB0, 10, 64 }));
        Assert.Contains(recovered, m => m.SequenceEqual(new byte[] { 0xB0, 11, 80 }));
    }

    [Fact]
    public void ChapterC_Decode_TooShort_ReturnsMinusOne()
    {
        // Header says 2 entries but only 3 bytes available (should be 5)
        var data = new byte[] { 0x02, 0x87, 64 };
        var recovered = new List<byte[]>();
        Assert.Equal(-1, ChannelMidiState.DecodeChapterC(data, 0, recovered));
    }

    [Fact]
    public void ChapterC_SFlagSet_OnLastEntry()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0xB0, 1, 50]);
        state.ProcessMidi([0xB0, 2, 60]);

        var bytes = state.EncodeChapterC(isLast: true);
        // Entry 1: SFLAG=0 (not last)
        Assert.Equal(0, bytes[1] & 0x80);
        // Entry 2: SFLAG=1 (last)
        Assert.NotEqual(0, bytes[3] & 0x80);
    }

    // =========================================================================
    // Chapter W — Pitch Wheel
    // =========================================================================

    [Fact]
    public void ChapterW_Encode_RoundTrip()
    {
        var state = new ChannelMidiState();
        // Pitch wheel center: lsb=0, msb=64
        state.ProcessMidi([0xE0, 0x00, 0x40]);

        var bytes = state.EncodeChapterW(isLast: true);
        Assert.Equal(2, bytes.Length);

        // S=1, FIRST=0 (lsb) → 0x80
        Assert.Equal(0x80, bytes[0]);
        // SECOND=0x40 (msb)
        Assert.Equal(0x40, bytes[1]);

        var recovered = new List<byte[]>();
        int consumed = ChannelMidiState.DecodeChapterW(bytes, 0, recovered);
        Assert.Equal(2, consumed);
        Assert.Single(recovered);
        Assert.Equal(new byte[] { 0xE0, 0x00, 0x40 }, recovered[0]);
    }

    [Fact]
    public void ChapterW_Decode_Channel4_CorrectStatus()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0xE4, 0x12, 0x34]);
        var bytes = state.EncodeChapterW(isLast: true);

        var recovered = new List<byte[]>();
        ChannelMidiState.DecodeChapterW(bytes, 4, recovered);

        Assert.Single(recovered);
        Assert.Equal((byte)(0xE0 | 4), recovered[0][0]);
    }

    [Fact]
    public void ChapterW_Decode_TooShort_ReturnsMinusOne()
    {
        var recovered = new List<byte[]>();
        Assert.Equal(-1, ChannelMidiState.DecodeChapterW(new byte[1], 0, recovered));
    }

    // =========================================================================
    // Chapter N — Note Off
    // =========================================================================

    [Fact]
    public void ChapterN_Encode_SingleNote_RoundTrip()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0x80, 60, 0]); // Note Off, note 60, velocity 0

        var bytes = state.EncodeChapterN(isLast: true);
        Assert.Equal(3, bytes.Length);

        // S=1, B=0, LEN=1
        Assert.Equal(0x81, bytes[0]);

        var recovered = new List<byte[]>();
        int consumed = ChannelMidiState.DecodeChapterN(bytes, 0, recovered);
        Assert.Equal(3, consumed);
        Assert.Single(recovered);
        Assert.Equal(new byte[] { 0x80, 60, 0 }, recovered[0]);
    }

    [Fact]
    public void ChapterN_NoteOn_WithVelocity0_TrackedAsNoteOff()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0x90, 64, 0]); // Note On velocity 0 = Note Off

        Assert.True(state.HasNoteOff);
        Assert.False(state.HasNoteOn);
    }

    [Fact]
    public void ChapterN_Decode_MultipleNotes()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0x80, 60, 32]);
        state.ProcessMidi([0x80, 62, 0]);

        var bytes = state.EncodeChapterN(isLast: false);
        var recovered = new List<byte[]>();
        int consumed = ChannelMidiState.DecodeChapterN(bytes, 0, recovered);

        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(2, recovered.Count);
    }

    // =========================================================================
    // Chapter Q — Note On
    // =========================================================================

    [Fact]
    public void ChapterQ_Encode_SingleNote_RoundTrip()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0x90, 60, 100]); // Note On, note 60, velocity 100

        Assert.True(state.HasNoteOn);

        var bytes = state.EncodeChapterQ(isLast: true);
        Assert.Equal(3, bytes.Length);

        var recovered = new List<byte[]>();
        int consumed = ChannelMidiState.DecodeChapterQ(bytes, 0, recovered);
        Assert.Equal(3, consumed);
        Assert.Single(recovered);
        Assert.Equal(new byte[] { 0x90, 60, 100 }, recovered[0]);
    }

    [Fact]
    public void ChapterQ_NoteOnThenOff_RemovedFromOnList()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0x90, 60, 100]); // Note On
        state.ProcessMidi([0x80, 60, 0]);   // Note Off — should remove from on-list

        Assert.True(state.HasNoteOff);
        // Note 60 should no longer be "on"
        var bytes = state.EncodeChapterQ(isLast: true);
        // Header only (LEN=0) if no notes are on
        Assert.Single(bytes);
    }

    [Fact]
    public void ChapterQ_Decode_PolyphonicNotes()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0x90, 60, 80]);
        state.ProcessMidi([0x90, 64, 90]);
        state.ProcessMidi([0x90, 67, 70]);

        var bytes = state.EncodeChapterQ(isLast: true);
        var recovered = new List<byte[]>();
        ChannelMidiState.DecodeChapterQ(bytes, 0, recovered);

        Assert.Equal(3, recovered.Count);
        Assert.Contains(recovered, m => m.SequenceEqual(new byte[] { 0x90, 60, 80 }));
        Assert.Contains(recovered, m => m.SequenceEqual(new byte[] { 0x90, 64, 90 }));
        Assert.Contains(recovered, m => m.SequenceEqual(new byte[] { 0x90, 67, 70 }));
    }

    // =========================================================================
    // Chapter T — Channel Pressure
    // =========================================================================

    [Fact]
    public void ChapterT_Encode_RoundTrip()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0xD9, 72]); // Channel Pressure, channel 9, value 72

        Assert.True(state.HasChannelPressure);
        Assert.Equal(72, state.ChannelPressure);

        var bytes = state.EncodeChapterT(isLast: true);
        Assert.Single(bytes);
        // S=1 (0x80) | 72 = 0xC8
        Assert.Equal(0xC8, bytes[0]);

        var recovered = new List<byte[]>();
        int consumed = ChannelMidiState.DecodeChapterT(bytes, 9, recovered);
        Assert.Equal(1, consumed);
        Assert.Single(recovered);
        Assert.Equal(new byte[] { 0xD9, 72 }, recovered[0]);
    }

    [Fact]
    public void ChapterT_Decode_TooShort_ReturnsMinusOne()
    {
        var recovered = new List<byte[]>();
        Assert.Equal(-1, ChannelMidiState.DecodeChapterT(ReadOnlySpan<byte>.Empty, 0, recovered));
    }

    // =========================================================================
    // Chapter A — Poly Key Pressure
    // =========================================================================

    [Fact]
    public void ChapterA_Encode_SingleNote_RoundTrip()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0xA0, 60, 50]); // Poly pressure, note 60, pressure 50

        Assert.True(state.HasPolyPressure);

        var bytes = state.EncodeChapterA(isLast: true);
        Assert.Equal(3, bytes.Length);

        var recovered = new List<byte[]>();
        int consumed = ChannelMidiState.DecodeChapterA(bytes, 0, recovered);
        Assert.Equal(3, consumed);
        Assert.Single(recovered);
        Assert.Equal(new byte[] { 0xA0, 60, 50 }, recovered[0]);
    }

    [Fact]
    public void ChapterA_Decode_MultipleNotes()
    {
        var state = new ChannelMidiState();
        state.ProcessMidi([0xA3, 60, 40]);
        state.ProcessMidi([0xA3, 64, 60]);

        var bytes = state.EncodeChapterA(isLast: true);
        var recovered = new List<byte[]>();
        ChannelMidiState.DecodeChapterA(bytes, 3, recovered);

        Assert.Equal(2, recovered.Count);
        Assert.All(recovered, m => Assert.Equal(0xA3, m[0]));
    }

    // =========================================================================
    // Chapter F — System Common
    // =========================================================================

    [Fact]
    public void ChapterF_Encode_SongPosition_RoundTrip()
    {
        var state = new SystemMidiState();
        state.ProcessMidi([0xF2, 0x10, 0x20]); // Song Position Pointer

        Assert.True(state.HasSongPosition);

        var bytes = state.EncodeChapterF(isLast: true);
        // Header (1) + 2 bytes for song position = 3
        Assert.Equal(3, bytes.Length);
        // Header: S=1, V=1 (song position) → 0x80 | 0x20 = 0xA0
        Assert.Equal(0xA0, bytes[0]);

        var recovered = new List<byte[]>();
        int consumed = SystemMidiState.DecodeChapterF(bytes, recovered);
        Assert.Equal(3, consumed);
        Assert.Single(recovered);
        Assert.Equal(new byte[] { 0xF2, 0x10, 0x20 }, recovered[0]);
    }

    [Fact]
    public void ChapterF_Encode_AllThreeMessages_RoundTrip()
    {
        var state = new SystemMidiState();
        state.ProcessMidi([0xF1, 0x55]); // MTC Quarter Frame
        state.ProcessMidi([0xF2, 0x08, 0x04]); // Song Position
        state.ProcessMidi([0xF3, 3]);    // Song Select

        Assert.True(state.HasTimeCode);
        Assert.True(state.HasSongPosition);
        Assert.True(state.HasSongSelect);

        var bytes = state.EncodeChapterF(isLast: false);
        // Header(1) + 1(TC) + 2(SPP) + 1(SS) = 5
        Assert.Equal(5, bytes.Length);
        // Header byte: S=0(isLast=false), D=1(0x40), V=1(0x20), Q=1(0x10) → 0x70
        Assert.Equal(0x70, bytes[0]);

        var recovered = new List<byte[]>();
        int consumed = SystemMidiState.DecodeChapterF(bytes, recovered);
        Assert.Equal(5, consumed);
        Assert.Equal(3, recovered.Count);

        Assert.Contains(recovered, m => m.SequenceEqual(new byte[] { 0xF1, 0x55 }));
        Assert.Contains(recovered, m => m.SequenceEqual(new byte[] { 0xF2, 0x08, 0x04 }));
        Assert.Contains(recovered, m => m.SequenceEqual(new byte[] { 0xF3, 3 }));
    }

    [Fact]
    public void ChapterF_Decode_TooShort_ReturnsMinusOne()
    {
        // Header says D=1 (1 byte TC follows) but no byte available
        var data = new byte[] { 0x40 };
        var recovered = new List<byte[]>();
        Assert.Equal(-1, SystemMidiState.DecodeChapterF(data, recovered));
    }

    // =========================================================================
    // Full journal encode / decode (EncodeFullJournal + TryParseFullJournal)
    // =========================================================================

    [Fact]
    public void FullJournal_EmptyState_ReturnsEmptyArray()
    {
        var states = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) states[i] = new ChannelMidiState();

        var journal = RtpMidiJournal.EncodeFullJournal(1, null, states, null);
        Assert.Empty(journal);
    }

    [Fact]
    public void FullJournal_SysExOnly_RoundTrip()
    {
        var sysEx = new byte[] { 0xF0, 0x41, 0x10, 0x42, 0xF7 };
        var states = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) states[i] = new ChannelMidiState();

        var journal = RtpMidiJournal.EncodeFullJournal(42, sysEx, states, null);
        Assert.NotEmpty(journal);

        bool ok = RtpMidiJournal.TryParseFullJournal(journal, out var cp, out var msgs);
        Assert.True(ok);
        Assert.Equal(42, cp);
        Assert.NotNull(msgs);
        Assert.Single(msgs!);
        Assert.Equal(sysEx, msgs![0]);
    }

    [Fact]
    public void FullJournal_ChannelProgramChange_RoundTrip()
    {
        var states = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) states[i] = new ChannelMidiState();

        states[5].ProcessMidi([0xC5, 42]); // Program 42 on channel 5

        var journal = RtpMidiJournal.EncodeFullJournal(10, null, states, null);
        Assert.NotEmpty(journal);

        // Journal header: A=1 (channel journals), TOTCHAN=0 (1 channel)
        Assert.Equal(0x20, journal[0] & 0x20); // A flag

        bool ok = RtpMidiJournal.TryParseFullJournal(journal, out var cp, out var msgs);
        Assert.True(ok);
        Assert.Equal(10, cp);
        Assert.NotNull(msgs);
        Assert.Contains(msgs!, m => m.SequenceEqual(new byte[] { 0xC5, 42 }));
    }

    [Fact]
    public void FullJournal_MultipleChannels_AllRecovered()
    {
        var states = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) states[i] = new ChannelMidiState();

        states[0].ProcessMidi([0xC0, 10]); // Program 10, ch 0
        states[1].ProcessMidi([0xB1, 7, 100]); // CC 7 vol, ch 1
        states[9].ProcessMidi([0xD9, 80]); // Aftertouch ch 9

        var journal = RtpMidiJournal.EncodeFullJournal(5, null, states, null);

        bool ok = RtpMidiJournal.TryParseFullJournal(journal, out _, out var msgs);
        Assert.True(ok);
        Assert.NotNull(msgs);
        Assert.Contains(msgs!, m => m.SequenceEqual(new byte[] { 0xC0, 10 }));
        Assert.Contains(msgs!, m => m.SequenceEqual(new byte[] { 0xB1, 7, 100 }));
        Assert.Contains(msgs!, m => m.SequenceEqual(new byte[] { 0xD9, 80 }));
    }

    [Fact]
    public void FullJournal_SysExPlusChannelChapters_RoundTrip()
    {
        var sysEx = new byte[] { 0xF0, 0x47, 0x7F, 0xF7 };
        var states = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) states[i] = new ChannelMidiState();

        states[0].ProcessMidi([0xE0, 0x00, 0x40]); // Pitch wheel center, ch 0
        states[0].ProcessMidi([0x90, 60, 127]);     // Note On 60, ch 0

        var journal = RtpMidiJournal.EncodeFullJournal(99, sysEx, states, null);

        bool ok = RtpMidiJournal.TryParseFullJournal(journal, out var cp, out var msgs);
        Assert.True(ok);
        Assert.Equal(99, cp);
        Assert.NotNull(msgs);

        Assert.Contains(msgs!, m => m.SequenceEqual(sysEx));
        Assert.Contains(msgs!, m => m.SequenceEqual(new byte[] { 0xE0, 0x00, 0x40 }));
        Assert.Contains(msgs!, m => m.SequenceEqual(new byte[] { 0x90, 60, 127 }));
    }

    [Fact]
    public void FullJournal_NoteOnOff_BothTracked()
    {
        var states = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) states[i] = new ChannelMidiState();

        states[0].ProcessMidi([0x90, 60, 100]); // Note On 60
        states[0].ProcessMidi([0x90, 62, 90]);  // Note On 62
        states[0].ProcessMidi([0x80, 64, 0]);   // Note Off 64

        var journal = RtpMidiJournal.EncodeFullJournal(1, null, states, null);
        bool ok = RtpMidiJournal.TryParseFullJournal(journal, out _, out var msgs);

        Assert.True(ok);
        Assert.NotNull(msgs);
        // Should have Note Ons for 60 and 62, and Note Off for 64
        Assert.Contains(msgs!, m => m.SequenceEqual(new byte[] { 0x90, 60, 100 }));
        Assert.Contains(msgs!, m => m.SequenceEqual(new byte[] { 0x90, 62, 90 }));
        Assert.Contains(msgs!, m => m.SequenceEqual(new byte[] { 0x80, 64, 0 }));
    }

    [Fact]
    public void FullJournal_SystemF_RoundTrip()
    {
        var states = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) states[i] = new ChannelMidiState();

        var sysState = new SystemMidiState();
        sysState.ProcessMidi([0xF2, 0x10, 0x00]); // Song Position

        var journal = RtpMidiJournal.EncodeFullJournal(1, null, states, sysState);
        Assert.NotEmpty(journal);

        bool ok = RtpMidiJournal.TryParseFullJournal(journal, out _, out var msgs);
        Assert.True(ok);
        Assert.NotNull(msgs);
        Assert.Contains(msgs!, m => m.SequenceEqual(new byte[] { 0xF2, 0x10, 0x00 }));
    }

    [Fact]
    public void TryParseFullJournal_TooShort_ReturnsFalse()
    {
        Assert.False(RtpMidiJournal.TryParseFullJournal(new byte[2], out _, out _));
    }

    [Fact]
    public void TryParseFullJournal_NullResult_WhenNoMessages()
    {
        // Journal header S=0, A=0 → no journals at all (pathological but valid)
        var data = new byte[] { 0x00, 0x00, 0x01 };
        bool ok = RtpMidiJournal.TryParseFullJournal(data, out var cp, out var msgs);
        Assert.True(ok);
        Assert.Equal(1, cp);
        Assert.Null(msgs);
    }

    // =========================================================================
    // Legacy TryParseChapterX compatibility — still works when F precedes X
    // =========================================================================

    [Fact]
    public void LegacyTryParseChapterX_WorksWithChapterFPresent()
    {
        // Build a journal with Chapter F + Chapter X
        var sysEx = new byte[] { 0xF0, 0x47, 0xF7 };
        var states = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) states[i] = new ChannelMidiState();

        var sysState = new SystemMidiState();
        sysState.ProcessMidi([0xF3, 2]); // Song Select 2

        var journal = RtpMidiJournal.EncodeFullJournal(55, sysEx, states, sysState);

        // The legacy parser should still find Chapter X even though F precedes it
        bool ok = RtpMidiJournal.TryParseChapterX(journal, out var cp, out var recovered);
        Assert.True(ok);
        Assert.Equal(55, cp);
        Assert.NotNull(recovered);
        Assert.Equal(sysEx, recovered);
    }

    // =========================================================================
    // Packet-loss recovery simulation — channel messages
    // =========================================================================

    [Fact]
    public void PacketLossRecovery_ProgramChange_RestoredFromJournal()
    {
        // Packet 0: Note, no journal yet — establishes expected sequence
        // Packet 1: Program Change + journal (checkpoint=1) — DROPPED
        // Packet 2: Note On + journal (checkpoint=1) — received
        // Gap detection on pkt2 should recover the Program Change from its journal.

        var pkt0 = RtpMidiPacket.Encode(0xAA, 0, 0, new byte[] { 0x90, 48, 50 });

        var states = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) states[i] = new ChannelMidiState();
        states[0].ProcessMidi([0xC0, 42]); // Program 42, ch 0

        var journal = RtpMidiJournal.EncodeFullJournal(1, null, states, null);
        // pkt1 (dropped): Program Change + journal
        // var pkt1 = ...

        // pkt2: Note On + same journal (still carries prog change, checkpoint=1)
        var pkt2 = RtpMidiPacket.Encode(0xAA, 2, 200, new byte[] { 0x90, 60, 100 }, journal);

        // Simulate: receive pkt0, drop pkt1, receive pkt2
        var received = new List<byte[]>();
        ushort? expectedSeq = null;

        foreach (var raw in new[] { pkt0, /* pkt1 dropped */ pkt2 })
        {
            Assert.True(RtpMidiPacket.TryParse(raw, out var pkt));
            Assert.NotNull(pkt);

            if (expectedSeq.HasValue && pkt!.SequenceNumber != expectedSeq.Value)
            {
                // Gap detected
                if (!pkt.JournalBytes.IsEmpty
                    && RtpMidiJournal.TryParseFullJournal(
                        pkt.JournalBytes.Span,
                        out var cpSeq,
                        out var recovered)
                    && recovered != null
                    && IsInGap(cpSeq, expectedSeq.Value, pkt.SequenceNumber))
                {
                    received.AddRange(recovered);
                }
            }

            expectedSeq = (ushort)(pkt!.SequenceNumber + 1);
            if (!pkt.MidiBytes.IsEmpty)
                received.Add(pkt.MidiBytes.ToArray());
        }

        // Should have: Note from pkt0, recovered Program Change, then Note On from pkt2
        Assert.Contains(received, m => m.SequenceEqual(new byte[] { 0xC0, 42 }));
        Assert.Contains(received, m => m.SequenceEqual(new byte[] { 0x90, 60, 100 }));
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static bool IsInGap(ushort checkpoint, ushort gapStart, ushort received)
        => (ushort)(checkpoint - gapStart) < (ushort)(received - gapStart);
}
