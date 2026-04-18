using Haukcode.RtpMidi;

namespace RtpMidi.Tests;

public class MidiCommandTests
{
    // -------------------------------------------------------------------------
    // VLQ helpers
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0u,         new byte[] { 0x00 })]
    [InlineData(1u,         new byte[] { 0x01 })]
    [InlineData(0x7Fu,      new byte[] { 0x7F })]
    [InlineData(0x80u,      new byte[] { 0x81, 0x00 })]
    [InlineData(300u,       new byte[] { 0x82, 0x2C })]
    [InlineData(0x3FFFu,    new byte[] { 0xFF, 0x7F })]
    [InlineData(0x4000u,    new byte[] { 0x81, 0x80, 0x00 })]
    [InlineData(0x1FFFFFu,  new byte[] { 0xFF, 0xFF, 0x7F })]
    public void WriteVlq_CorrectBytes(uint value, byte[] expected)
    {
        Span<byte> buf = stackalloc byte[4];
        int len = RtpMidiPacket.WriteVlq(value, buf);
        Assert.Equal(expected.Length, len);
        Assert.Equal(expected, buf[..len].ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0x00 },             0u,        1)]
    [InlineData(new byte[] { 0x7F },             0x7Fu,     1)]
    [InlineData(new byte[] { 0x81, 0x00 },       0x80u,     2)]
    [InlineData(new byte[] { 0x82, 0x2C },       300u,      2)]
    [InlineData(new byte[] { 0xFF, 0x7F },       0x3FFFu,   2)]
    [InlineData(new byte[] { 0x81, 0x80, 0x00 }, 0x4000u,   3)]
    public void TryReadVlq_CorrectValue(byte[] input, uint expectedValue, int expectedConsumed)
    {
        bool ok = RtpMidiPacket.TryReadVlq(input, out uint value, out int consumed);
        Assert.True(ok);
        Assert.Equal(expectedValue, value);
        Assert.Equal(expectedConsumed, consumed);
    }

    [Fact]
    public void TryReadVlq_EmptySpan_ReturnsFalse()
    {
        Assert.False(RtpMidiPacket.TryReadVlq(ReadOnlySpan<byte>.Empty, out _, out _));
    }

    // -------------------------------------------------------------------------
    // GetStatusDataLength
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0x90, 2)] // Note On
    [InlineData(0x80, 2)] // Note Off
    [InlineData(0xB0, 2)] // Control Change
    [InlineData(0xC0, 1)] // Program Change
    [InlineData(0xD0, 1)] // Channel Pressure
    [InlineData(0xE0, 2)] // Pitch Bend
    [InlineData(0xF1, 1)] // MTC Quarter Frame
    [InlineData(0xF2, 2)] // Song Position Pointer
    [InlineData(0xF3, 1)] // Song Select
    [InlineData(0xF6, 0)] // Tune Request
    [InlineData(0xF8, 0)] // Timing Clock (real-time)
    [InlineData(0xFA, 0)] // Start (real-time)
    [InlineData(0xFE, 0)] // Active Sensing (real-time)
    public void GetStatusDataLength_KnownMessages(byte status, int expectedDataBytes)
    {
        Assert.Equal(expectedDataBytes, RtpMidiPacket.GetStatusDataLength(status));
    }

    [Theory]
    [InlineData(0xF0)] // SysEx start
    [InlineData(0xF7)] // SysEx end
    public void GetStatusDataLength_SysEx_ReturnsMinusOne(byte status)
    {
        Assert.Equal(-1, RtpMidiPacket.GetStatusDataLength(status));
    }

    // -------------------------------------------------------------------------
    // Z flag (delta-time) — encode/decode round-trips
    // -------------------------------------------------------------------------

    [Fact]
    public void SingleCommand_NoDeltaTime_NoZFlag()
    {
        // A single command with delta=0 should produce Z=0 (no VLQ prefix).
        var commands = new List<MidiCommand>
        {
            new(0u, new byte[] { 0x90, 0x3C, 0x7F })
        };

        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, commands);

        // Z flag is bit 5 of the MIDI section header byte (offset 12).
        Assert.True((encoded[12] & 0x20) == 0, "Z flag should not be set for single command with delta=0");

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.False(pkt!.HasDeltaTime);

        var decoded = pkt.DecodeCommands();
        Assert.Single(decoded);
        Assert.Equal(0u, decoded[0].DeltaTime);
        Assert.Equal(new byte[] { 0x90, 0x3C, 0x7F }, decoded[0].Data.ToArray());
    }

    [Fact]
    public void SingleCommand_WithDeltaTime_ZFlagSet()
    {
        // A single command with delta > 0 should set Z=1 so the delta is encoded.
        var commands = new List<MidiCommand>
        {
            new(100u, new byte[] { 0x90, 0x3C, 0x7F })
        };

        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, commands);
        Assert.True((encoded[12] & 0x20) != 0, "Z flag should be set when delta > 0");

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.True(pkt!.HasDeltaTime);

        var decoded = pkt.DecodeCommands();
        Assert.Single(decoded);
        Assert.Equal(100u, decoded[0].DeltaTime);
        Assert.Equal(new byte[] { 0x90, 0x3C, 0x7F }, decoded[0].Data.ToArray());
    }

    [Fact]
    public void MultipleCommands_ZFlagSet_DeltaTimesRoundTrip()
    {
        // Two simultaneous Note On events (chord): delta=0 for both.
        var commands = new List<MidiCommand>
        {
            new(0u,   new byte[] { 0x90, 0x3C, 0x7F }),  // C4 vel=127
            new(0u,   new byte[] { 0x90, 0x40, 0x7F }),  // E4 vel=127
        };

        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, commands);
        Assert.True((encoded[12] & 0x20) != 0, "Z flag should be set for multiple commands");

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.True(pkt!.HasDeltaTime);

        var decoded = pkt.DecodeCommands();
        Assert.Equal(2, decoded.Count);
        Assert.Equal(0u, decoded[0].DeltaTime);
        Assert.Equal(new byte[] { 0x90, 0x3C, 0x7F }, decoded[0].Data.ToArray());
        Assert.Equal(0u, decoded[1].DeltaTime);
        Assert.Equal(new byte[] { 0x90, 0x40, 0x7F }, decoded[1].Data.ToArray());
    }

    [Fact]
    public void MultipleCommands_VariousDeltaTimes_RoundTrip()
    {
        // Three commands with distinct delta-times (including a 2-byte VLQ value).
        var commands = new List<MidiCommand>
        {
            new(0u,   new byte[] { 0x90, 0x3C, 0x64 }),
            new(100u, new byte[] { 0x90, 0x40, 0x64 }),
            new(500u, new byte[] { 0x80, 0x3C, 0x00 }),
        };

        var encoded = RtpMidiPacket.Encode(0x01, 7, 999, commands);

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.True(pkt!.HasDeltaTime);

        var decoded = pkt.DecodeCommands();
        Assert.Equal(3, decoded.Count);
        Assert.Equal(0u,   decoded[0].DeltaTime);
        Assert.Equal(100u, decoded[1].DeltaTime);
        Assert.Equal(500u, decoded[2].DeltaTime);
        Assert.Equal(new byte[] { 0x90, 0x3C, 0x64 }, decoded[0].Data.ToArray());
        Assert.Equal(new byte[] { 0x90, 0x40, 0x64 }, decoded[1].Data.ToArray());
        Assert.Equal(new byte[] { 0x80, 0x3C, 0x00 }, decoded[2].Data.ToArray());
    }

    [Fact]
    public void LargeVlqDeltaTime_RoundTrip()
    {
        // delta = 0x3FFF (two-byte VLQ) to verify multi-byte VLQ round-trips.
        var commands = new List<MidiCommand>
        {
            new(0x3FFFu, new byte[] { 0xB0, 0x07, 0x40 }),
            new(0x3FFFu, new byte[] { 0xB0, 0x0A, 0x60 }),
        };

        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, commands);
        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.True(pkt!.HasDeltaTime);

        var decoded = pkt.DecodeCommands();
        Assert.Equal(2, decoded.Count);
        Assert.Equal(0x3FFFu, decoded[0].DeltaTime);
        Assert.Equal(0x3FFFu, decoded[1].DeltaTime);
    }

    // -------------------------------------------------------------------------
    // P flag (phantom status / running status)
    // -------------------------------------------------------------------------

    [Fact]
    public void PhantomStatus_OmitsFirstStatusByte_PFlagSet()
    {
        // Single Note On — if the previous packet ended with status 0x90, P flag should be set
        // and the status byte should be omitted from the encoded payload.
        byte phantom = 0x90;
        var commands = new List<MidiCommand>
        {
            new(0u, new byte[] { 0x90, 0x3C, 0x7F })
        };

        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, commands, phantomStatus: phantom);

        // P flag is bit 4 of the MIDI section header byte.
        Assert.True((encoded[12] & 0x10) != 0, "P flag should be set when first command matches phantomStatus");

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.True(pkt!.HasPhantomStatus);

        // Decode with the same phantom status — status byte should be restored.
        var decoded = pkt.DecodeCommands(phantomStatus: phantom);
        Assert.Single(decoded);
        Assert.Equal(new byte[] { 0x90, 0x3C, 0x7F }, decoded[0].Data.ToArray());
    }

    [Fact]
    public void PhantomStatus_NoMatch_PFlagNotSet()
    {
        // If the phantom status doesn't match the first command's status, P flag must be 0.
        var commands = new List<MidiCommand>
        {
            new(0u, new byte[] { 0x90, 0x3C, 0x7F })
        };

        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, commands, phantomStatus: 0x80);

        Assert.True((encoded[12] & 0x10) == 0, "P flag should not be set when phantomStatus doesn't match");

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.False(pkt!.HasPhantomStatus);
    }

    [Fact]
    public void WithinPacket_RunningStatus_StatusOmittedForSubsequentCommands()
    {
        // Two consecutive Note On commands with the same status byte: the second
        // command's status byte should be omitted in the encoded MIDI section.
        var commands = new List<MidiCommand>
        {
            new(0u, new byte[] { 0x90, 0x3C, 0x7F }),
            new(0u, new byte[] { 0x90, 0x40, 0x64 }),
        };

        var (midiSection, _, _) = RtpMidiPacket.EncodeCommandSection(commands, null);

        // With Z=1: [VLQ(0)][status][key][vel][VLQ(0)][key][vel]
        // = [0x00][0x90][0x3C][0x7F][0x00][0x40][0x64] → 7 bytes
        // Without running-status compression it would be 9 bytes.
        Assert.Equal(7, midiSection.Length);

        // Verify round-trip: decode should restore both commands with status bytes.
        var decoded = RtpMidiPacket.DecodeCommandSection(midiSection, hasDelta: true, hasPhantom: false, phantomStatus: null);
        Assert.Equal(2, decoded.Count);
        Assert.Equal(new byte[] { 0x90, 0x3C, 0x7F }, decoded[0].Data.ToArray());
        Assert.Equal(new byte[] { 0x90, 0x40, 0x64 }, decoded[1].Data.ToArray());
    }

    [Fact]
    public void WithinPacket_DifferentStatusBytes_NoCompression()
    {
        // Note On followed by Control Change: different status bytes, no running-status compression.
        var commands = new List<MidiCommand>
        {
            new(0u, new byte[] { 0x90, 0x3C, 0x7F }),
            new(0u, new byte[] { 0xB0, 0x07, 0x40 }),
        };

        var (midiSection, _, _) = RtpMidiPacket.EncodeCommandSection(commands, null);

        // [0x00][0x90][0x3C][0x7F][0x00][0xB0][0x07][0x40] → 8 bytes
        Assert.Equal(8, midiSection.Length);

        var decoded = RtpMidiPacket.DecodeCommandSection(midiSection, hasDelta: true, hasPhantom: false, phantomStatus: null);
        Assert.Equal(2, decoded.Count);
        Assert.Equal(new byte[] { 0x90, 0x3C, 0x7F }, decoded[0].Data.ToArray());
        Assert.Equal(new byte[] { 0xB0, 0x07, 0x40 }, decoded[1].Data.ToArray());
    }

    [Fact]
    public void PhantomAndRunningStatus_Combined_RoundTrip()
    {
        // Three Note On commands; phantomStatus=0x90 so the first omits its status byte (P=1),
        // and the remaining two use within-packet running status.
        byte phantom = 0x90;
        var commands = new List<MidiCommand>
        {
            new(0u, new byte[] { 0x90, 0x3C, 0x7F }),
            new(0u, new byte[] { 0x90, 0x40, 0x64 }),
            new(0u, new byte[] { 0x90, 0x43, 0x50 }),
        };

        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, commands, phantomStatus: phantom);

        Assert.True((encoded[12] & 0x10) != 0, "P flag should be set");
        Assert.True((encoded[12] & 0x20) != 0, "Z flag should be set");

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.True(pkt!.HasPhantomStatus);
        Assert.True(pkt.HasDeltaTime);

        var decoded = pkt.DecodeCommands(phantomStatus: phantom);
        Assert.Equal(3, decoded.Count);
        Assert.Equal(new byte[] { 0x90, 0x3C, 0x7F }, decoded[0].Data.ToArray());
        Assert.Equal(new byte[] { 0x90, 0x40, 0x64 }, decoded[1].Data.ToArray());
        Assert.Equal(new byte[] { 0x90, 0x43, 0x50 }, decoded[2].Data.ToArray());
    }

    // -------------------------------------------------------------------------
    // Mixed message types
    // -------------------------------------------------------------------------

    [Fact]
    public void MixedMessages_NoteOn_CC_ProgramChange_RoundTrip()
    {
        var commands = new List<MidiCommand>
        {
            new(0u,   new byte[] { 0x90, 0x3C, 0x7F }), // Note On
            new(10u,  new byte[] { 0xB0, 0x07, 0x64 }), // CC Volume
            new(20u,  new byte[] { 0xC0, 0x05 }),        // Program Change
        };

        var encoded = RtpMidiPacket.Encode(0x01, 5, 2000, commands);
        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.True(pkt!.HasDeltaTime);

        var decoded = pkt.DecodeCommands();
        Assert.Equal(3, decoded.Count);
        Assert.Equal(0u,  decoded[0].DeltaTime);
        Assert.Equal(10u, decoded[1].DeltaTime);
        Assert.Equal(20u, decoded[2].DeltaTime);
        Assert.Equal(new byte[] { 0x90, 0x3C, 0x7F }, decoded[0].Data.ToArray());
        Assert.Equal(new byte[] { 0xB0, 0x07, 0x64 }, decoded[1].Data.ToArray());
        Assert.Equal(new byte[] { 0xC0, 0x05 },        decoded[2].Data.ToArray());
    }

    [Fact]
    public void RealTimeMessage_NoRunningStatusChange_RoundTrip()
    {
        // Real-time messages (≥ 0xF8) must not participate in running status tracking.
        var commands = new List<MidiCommand>
        {
            new(0u, new byte[] { 0x90, 0x3C, 0x7F }), // Note On
            new(0u, new byte[] { 0xF8 }),              // Timing Clock (real-time)
            new(0u, new byte[] { 0x90, 0x40, 0x64 }), // Note On — should still use running status
        };

        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, commands);
        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));

        var decoded = pkt!.DecodeCommands();
        Assert.Equal(3, decoded.Count);
        Assert.Equal(new byte[] { 0x90, 0x3C, 0x7F }, decoded[0].Data.ToArray());
        Assert.Equal(new byte[] { 0xF8 },              decoded[1].Data.ToArray());
        Assert.Equal(new byte[] { 0x90, 0x40, 0x64 }, decoded[2].Data.ToArray());
    }

    // -------------------------------------------------------------------------
    // Empty / edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public void EmptyCommandList_ProducesEmptyMidiSection()
    {
        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, new List<MidiCommand>());
        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.Equal(0, pkt!.MidiBytes.Length);
        Assert.Empty(pkt.DecodeCommands());
    }

    [Fact]
    public void HasDeltaTime_And_HasPhantomStatus_FalseForLegacyRawBytes()
    {
        // Packets encoded via the raw-bytes API should have both flags clear.
        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, new byte[] { 0x90, 0x3C, 0x7F });
        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.False(pkt!.HasDeltaTime);
        Assert.False(pkt.HasPhantomStatus);
    }
}
