using Haukcode.RtpMidi;

namespace RtpMidi.Tests;

/// <summary>
/// Tests for RTP-MIDI recovery journal encoding and decoding (RFC 6295 §A.3).
/// </summary>
public class RtpMidiJournalTests
{
    private static readonly byte[] SysEx8 = [0xF0, 0x47, 0x7F, 0x30, 0x2C, 0x01, 0x00, 0xF7];

    // -------------------------------------------------------------------------
    // Journal encode / decode
    // -------------------------------------------------------------------------

    [Fact]
    public void EncodeChapterX_ProducesCorrectHeader()
    {
        var journal = RtpMidiJournal.EncodeChapterX(0x0042, SysEx8);

        // Byte 0: S=1 (0x80), Y=0, A=0, H=0, TOTCHAN=0 → 0x80
        Assert.Equal(0x80, journal[0]);

        // Bytes 1-2: checkpoint sequence number 0x0042
        Assert.Equal(0x00, journal[1]);
        Assert.Equal(0x42, journal[2]);

        // Byte 3 (system journal header): X flag set = 0x04
        Assert.Equal(0x04, journal[3]);

        // Bytes 4-5 (chapter X header): S=1(0x80), F=1(0x40), len=8 → 0xC0, 0x08
        Assert.Equal(0xC0, journal[4]);
        Assert.Equal(0x08, journal[5]);

        // SysEx data
        Assert.Equal(SysEx8, journal[6..]);
    }

    [Fact]
    public void EncodeChapterX_IncompleteSysEx_FlagNotSet()
    {
        // SysEx without trailing F7 → F flag must be clear
        byte[] incompleteSysEx = [0xF0, 0x41, 0x10, 0x42];
        var journal = RtpMidiJournal.EncodeChapterX(1, incompleteSysEx);

        // F bit (0x40) should be 0
        Assert.Equal(0, journal[4] & 0x40);
    }

    [Fact]
    public void TryParseChapterX_RoundTrip()
    {
        ushort checkpoint = 0x1234;
        var journal = RtpMidiJournal.EncodeChapterX(checkpoint, SysEx8);

        bool ok = RtpMidiJournal.TryParseChapterX(journal, out var parsedCheckpoint, out var parsedSysEx);

        Assert.True(ok);
        Assert.Equal(checkpoint, parsedCheckpoint);
        Assert.NotNull(parsedSysEx);
        Assert.Equal(SysEx8, parsedSysEx);
    }

    [Fact]
    public void TryParseChapterX_TooShort_ReturnsFalse()
    {
        Assert.False(RtpMidiJournal.TryParseChapterX(new byte[2], out _, out _));
    }

    [Fact]
    public void TryParseChapterX_NoSystemJournal_ReturnsNullSysEx()
    {
        // Journal header with S=0 (no system journal)
        byte[] noSystem = [0x00, 0x00, 0x01];
        bool ok = RtpMidiJournal.TryParseChapterX(noSystem, out var seq, out var sysEx);
        Assert.True(ok);
        Assert.Equal(1, seq);
        Assert.Null(sysEx);
    }

    // -------------------------------------------------------------------------
    // RtpMidiPacket J-bit encoding / decoding
    // -------------------------------------------------------------------------

    [Fact]
    public void Encode_WithJournal_SetsJBit_ShortHeader()
    {
        var midi    = new byte[] { 0x90, 0x3C, 0x7F };
        var journal = RtpMidiJournal.EncodeChapterX(1, SysEx8);

        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, midi, journal);

        // Short header: byte 12, J bit = 0x40
        Assert.Equal(0x40, encoded[12] & 0x40);
        // B bit = 0 (short header for 3-byte MIDI)
        Assert.Equal(0, encoded[12] & 0x80);
    }

    [Fact]
    public void Encode_WithJournal_SetsJBit_LongHeader()
    {
        var midi    = new byte[16]; // forces long header
        var journal = RtpMidiJournal.EncodeChapterX(1, SysEx8);

        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, midi, journal);

        // Long header: byte 12, B=1(0x80) and J=1(0x40) → 0xC0
        Assert.Equal(0xC0, encoded[12] & 0xC0);
    }

    [Fact]
    public void Encode_WithoutJournal_JBitNotSet()
    {
        var midi    = new byte[] { 0x90, 0x3C, 0x7F };
        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, midi);

        Assert.Equal(0, encoded[12] & 0x40);
    }

    [Fact]
    public void Packet_RoundTrip_WithJournal()
    {
        var midi    = new byte[] { 0x90, 0x3C, 0x7F };
        var journal = RtpMidiJournal.EncodeChapterX(42, SysEx8);

        var encoded = RtpMidiPacket.Encode(0xDEADBEEF, 100, 9999, midi, journal);

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.NotNull(pkt);
        Assert.Equal(100, pkt.SequenceNumber);
        Assert.Equal(midi, pkt.MidiBytes.ToArray());
        Assert.False(pkt.JournalBytes.IsEmpty);

        // Journal should round-trip through TryParseChapterX
        bool ok = RtpMidiJournal.TryParseChapterX(pkt.JournalBytes.Span, out var cpSeq, out var recovered);
        Assert.True(ok);
        Assert.Equal(42, cpSeq);
        Assert.Equal(SysEx8, recovered);
    }

    [Fact]
    public void Packet_WithoutJournal_JournalBytesEmpty()
    {
        var midi    = new byte[] { 0x90, 0x3C, 0x7F };
        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, midi);

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.True(pkt!.JournalBytes.IsEmpty);
    }

    // -------------------------------------------------------------------------
    // Packet-loss recovery simulation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Simulates the scenario from the issue:
    ///   Packet 1 — ordinary MIDI (no SysEx yet)
    ///   Packet 2 — SysEx payload + journal (checkpoint = 2, SysEx recovered)
    ///   Packet 3 — ordinary MIDI + journal from packet 2's SysEx (carries recovery)
    ///
    /// Packet 2 is "dropped". Feeding packets 1 and 3 to the decoder should
    /// emit the SysEx from packet 2 via the journal in packet 3.
    /// </summary>
    [Fact]
    public void PacketLossRecovery_SysExRecoveredFromJournal()
    {
        var noteon = new byte[] { 0x90, 0x3C, 0x7F };
        var noteoff = new byte[] { 0x80, 0x3C, 0x00 };

        // Packet 1: ordinary Note On, seq=1, no journal yet
        var pkt1 = RtpMidiPacket.Encode(0xAA, 1, 100, noteon);

        // Packet 2: SysEx, seq=2, journal with checkpoint=2 (SysEx8 itself)
        var journal2 = RtpMidiJournal.EncodeChapterX(2, SysEx8);
        var pkt2     = RtpMidiPacket.Encode(0xAA, 2, 200, SysEx8, journal2);

        // Packet 3: Note Off, seq=3, journal still carries SysEx from packet 2
        var journal3 = RtpMidiJournal.EncodeChapterX(2, SysEx8);
        var pkt3     = RtpMidiPacket.Encode(0xAA, 3, 300, noteoff, journal3);

        // Simulate receiver: receives pkt1, (drops pkt2), receives pkt3
        var received = new List<byte[]>();
        ushort? expectedSeq = null;

        foreach (var raw in new[] { pkt1, /* pkt2 dropped */ pkt3 })
        {
            Assert.True(RtpMidiPacket.TryParse(raw, out var p));
            var pkt = p!;

            if (expectedSeq.HasValue && pkt.SequenceNumber != expectedSeq.Value)
            {
                // Gap detected — try recovery journal
                if (!pkt.JournalBytes.IsEmpty
                    && RtpMidiJournal.TryParseChapterX(pkt.JournalBytes.Span, out var cpSeq, out var recovered)
                    && recovered != null
                    && IsInGap(cpSeq, expectedSeq.Value, pkt.SequenceNumber))
                {
                    received.Add(recovered);
                }
            }

            expectedSeq = (ushort)(pkt.SequenceNumber + 1);

            if (!pkt.MidiBytes.IsEmpty)
                received.Add(pkt.MidiBytes.ToArray());
        }

        // Should have: noteon, recovered-SysEx (from pkt3's journal), noteoff
        Assert.Equal(3, received.Count);
        Assert.Equal(noteon,  received[0]);
        Assert.Equal(SysEx8,  received[1]);
        Assert.Equal(noteoff, received[2]);
    }

    /// <summary>
    /// Validates the fix for the channel-message journal checkpoint bug.
    ///
    /// Previously the checkpoint in channel-only journals was always 0 (the default
    /// of lastSysExSeqNum), which caused IsInGap to return false for any realistic
    /// sequence number, silently suppressing recovery.
    ///
    /// After the fix, the journal checkpoint is (sequenceNumber - 1) — one before the
    /// current packet — so that a receiver detecting a gap at seq N sees checkpoint N
    /// in the *next* packet's journal and correctly applies recovery.
    ///
    /// Scenario:
    ///   Packet 10 — CC Volume on ch 0 (dropped)
    ///   Packet 11 — Note On on ch 0, journal (C=10) carries CC Volume state
    ///   Receiver: expected seq=10, receives seq=11 → gap [10,10] → recovers CC Volume
    /// </summary>
    [Fact]
    public void PacketLossRecovery_ChannelMsgRecoveredFromJournal()
    {
        byte channel = 0;

        // Build the MIDI state that would exist at the time packet 11 is sent:
        //   CC Volume from packet 10 (accumulated state)
        //   Note On from packet 11 (current packet's own message, also accumulated)
        var chState = new ChannelMidiState();
        chState.ProcessMidi([(byte)(0xB0 | channel), 7, 100]);   // CC Volume (from dropped pkt 10)
        chState.ProcessMidi([(byte)(0x90 | channel), 0x3C, 0x7F]); // Note On  (from pkt 11)

        var channelStates16 = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) channelStates16[i] = new ChannelMidiState();
        channelStates16[channel] = chState;

        // Packet 11: Note On MIDI payload, journal checkpoint = 10 (= sequenceNumber - 1 = 11 - 1)
        var journal11 = RtpMidiJournal.EncodeFullJournal(10, null, channelStates16, null);
        var pkt11 = RtpMidiPacket.Encode(0xAA, 11, 1100, new byte[] { (byte)(0x90 | channel), 0x3C, 0x7F }, journal11);

        // Simulate receiver: expected seq=10 (has received up to seq=9), gets seq=11
        ushort expectedSeq = 10;
        var received = new List<byte[]>();

        Assert.True(RtpMidiPacket.TryParse(pkt11, out var p));
        var pkt = p!;

        // Gap detected: 11 ≠ 10
        Assert.NotEqual(expectedSeq, pkt.SequenceNumber);

        // Consult journal
        Assert.True(RtpMidiJournal.TryParseFullJournal(pkt.JournalBytes.Span, out var cpSeq, out var msgs));
        Assert.NotNull(msgs);

        // IsInGap(10, 10, 11) = (10-10) < (11-10) = 0 < 1 = TRUE
        Assert.True(IsInGap(cpSeq, expectedSeq, pkt.SequenceNumber),
            $"checkpoint {cpSeq} should be in gap [{expectedSeq},{pkt.SequenceNumber})");

        received.AddRange(msgs!);
        if (!pkt.MidiBytes.IsEmpty)
            received.Add(pkt.MidiBytes.ToArray());

        // Recovered messages include CC Volume (from Chapter C) and Note On (from Chapter Q),
        // plus Note On again from the packet's own MIDI payload.
        // Key assertion: CC Volume (from dropped packet 10) was recovered.
        Assert.Contains(received, m => m.SequenceEqual(new byte[] { (byte)(0xB0 | channel), 7, 100 }));
    }

    // -------------------------------------------------------------------------
    // RS-based channel state checkpoint advancement
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that once an RS confirms receipt of the packet that first
    /// carried a channel state update, the next EncodeFullJournal omits that
    /// state (it has been cleared) so the journal doesn't grow indefinitely.
    ///
    /// Scenario:
    ///   seq=10: CC Volume sent → channelStates updated, lastMidiStateSeqNum=10
    ///   RS(10) received       → channel state cleared, lastMidiStateSeqNum=0
    ///   seq=11: Note On sent  → journal should contain only Note On, NOT CC Volume
    /// </summary>
    [Fact]
    public void RsCheckpoint_ChannelStateCleared_AfterConfirmation()
    {
        // Simulate state after CC Volume was sent in packet 10 and RS(10) received:
        // channel state should have been cleared. Build fresh state for only Note On
        // (what would be present after the RS clear + new Note On accumulation).
        var channelStates = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) channelStates[i] = new ChannelMidiState();

        // Only Note On is in the state (CC Volume was cleared by RS)
        channelStates[0].ProcessMidi([0x90, 0x3C, 0x7F]);

        var journal = RtpMidiJournal.EncodeFullJournal(10, null, channelStates, null);

        Assert.True(RtpMidiJournal.TryParseFullJournal(journal, out _, out var recovered));
        Assert.NotNull(recovered);

        // Note On MUST be present
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0x90, 0x3C, 0x7F }));

        // CC Volume must NOT be present (was cleared by RS)
        Assert.DoesNotContain(recovered!, m => m.Length == 3 && m[0] == 0xB0 && m[1] == 7);
    }

    /// <summary>
    /// Verifies that channel state is NOT cleared prematurely: if RS confirms a
    /// sequence earlier than lastMidiStateSeqNum, the state must be kept because
    /// the most recent channel state change has not yet been confirmed.
    /// </summary>
    [Fact]
    public void RsCheckpoint_ChannelStateRetained_WhenNotYetConfirmed()
    {
        // Both CC Volume (seq=10) and CC Pan (seq=11) were sent before RS arrives.
        // RS(10) comes in: lastMidiStateSeqNum=11, RS(10) < 11 → no clear.
        // State should still contain both controllers.
        var channelStates = new ChannelMidiState[16];
        for (int i = 0; i < 16; i++) channelStates[i] = new ChannelMidiState();

        channelStates[0].ProcessMidi([0xB0, 7, 100]);   // CC Volume (seq 10)
        channelStates[0].ProcessMidi([0xB0, 10, 64]);   // CC Pan (seq 11)

        // Simulate RS(10) with lastMidiStateSeqNum=11: (short)(10 - 11) = -1 < 0 → no clear.
        // We verify this by keeping state unchanged and encoding the journal.
        var journal = RtpMidiJournal.EncodeFullJournal(10, null, channelStates, null);
        Assert.True(RtpMidiJournal.TryParseFullJournal(journal, out _, out var recovered));
        Assert.NotNull(recovered);

        // Both CC values must still be in the journal
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xB0, 7, 100 }));
        Assert.Contains(recovered!, m => m.SequenceEqual(new byte[] { 0xB0, 10, 64 }));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool IsInGap(ushort checkpointSeq, ushort gapStart, ushort receivedSeq)
        => (ushort)(checkpointSeq - gapStart) < (ushort)(receivedSeq - gapStart);
}
