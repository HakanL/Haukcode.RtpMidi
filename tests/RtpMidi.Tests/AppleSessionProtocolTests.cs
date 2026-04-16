using Haukcode.RtpMidi;

namespace RtpMidi.Tests;

public class AppleSessionProtocolTests
{
    // -------------------------------------------------------------------------
    // Encode → Parse round-trips
    // -------------------------------------------------------------------------

    [Fact]
    public void Invitation_RoundTrip()
    {
        var original = new SessionPacket(
            AppleSessionCommand.Invitation,
            AppleSessionProtocol.ProtocolVersion,
            InitiatorToken: 0xDEADBEEF,
            Ssrc: 0x12345678,
            Name: "Test Session");

        var encoded = AppleSessionProtocol.Encode(original);
        var ok = AppleSessionProtocol.TryParse(encoded, out var parsed, out var clock);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Null(clock);
        Assert.Equal(AppleSessionCommand.Invitation, parsed.Command);
        Assert.Equal(0xDEADBEEFu, parsed.InitiatorToken);
        Assert.Equal(0x12345678u, parsed.Ssrc);
        Assert.Equal("Test Session", parsed.Name);
    }

    [Fact]
    public void InvitationAccepted_RoundTrip()
    {
        var original = new SessionPacket(
            AppleSessionCommand.InvitationAccepted,
            AppleSessionProtocol.ProtocolVersion,
            InitiatorToken: 0xABCDEF01,
            Ssrc: 0x99887766,
            Name: "Bridge");

        var encoded = AppleSessionProtocol.Encode(original);
        Assert.True(AppleSessionProtocol.TryParse(encoded, out var parsed, out _));
        Assert.Equal(AppleSessionCommand.InvitationAccepted, parsed!.Command);
        Assert.Equal("Bridge", parsed.Name);
    }

    [Fact]
    public void EndSession_RoundTrip_NoName()
    {
        var original = new SessionPacket(
            AppleSessionCommand.EndSession,
            AppleSessionProtocol.ProtocolVersion,
            InitiatorToken: 0,
            Ssrc: 0,
            Name: null);

        var encoded = AppleSessionProtocol.Encode(original);
        Assert.True(AppleSessionProtocol.TryParse(encoded, out var parsed, out _));
        Assert.Equal(AppleSessionCommand.EndSession, parsed!.Command);
    }

    // -------------------------------------------------------------------------
    // Clock sync
    // -------------------------------------------------------------------------

    [Fact]
    public void ClockSync_Ck0_RoundTrip()
    {
        var original = new ClockSyncPacket(
            Ssrc: 0x11223344,
            Count: 0,
            Timestamp1: 0xAABBCCDD_EEFF0011,
            Timestamp2: 0,
            Timestamp3: 0);

        var encoded = AppleSessionProtocol.EncodeClock(original);
        Assert.True(AppleSessionProtocol.TryParse(encoded, out _, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(0, parsed.Count);
        Assert.Equal(0x11223344u, parsed.Ssrc);
        Assert.Equal(0xAABBCCDD_EEFF0011ul, parsed.Timestamp1);
        Assert.Equal(0ul, parsed.Timestamp2);
    }

    [Fact]
    public void ClockSync_Ck2_AllTimestamps_RoundTrip()
    {
        var original = new ClockSyncPacket(
            Ssrc: 0xFEDCBA98,
            Count: 2,
            Timestamp1: 100_000,
            Timestamp2: 200_000,
            Timestamp3: 300_000);

        var encoded = AppleSessionProtocol.EncodeClock(original);
        Assert.True(AppleSessionProtocol.TryParse(encoded, out _, out var parsed));
        Assert.Equal(2, parsed!.Count);
        Assert.Equal(100_000ul, parsed.Timestamp1);
        Assert.Equal(200_000ul, parsed.Timestamp2);
        Assert.Equal(300_000ul, parsed.Timestamp3);
    }

    // -------------------------------------------------------------------------
    // Parse rejection
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_RejectsShortBuffer()
    {
        var buf = new byte[] { 0xFF, 0xFF, 0x49 }; // truncated IN header
        Assert.False(AppleSessionProtocol.TryParse(buf, out _, out _));
    }

    [Fact]
    public void Parse_RejectsBadSignature()
    {
        var buf = new byte[20];
        buf[0] = 0x00; // wrong signature
        buf[1] = 0xFF;
        Assert.False(AppleSessionProtocol.TryParse(buf, out _, out _));
    }

    [Fact]
    public void ClockSync_RejectsShortBuffer()
    {
        // Valid signature + CK command but truncated body
        var buf = new byte[] { 0xFF, 0xFF, 0x43, 0x4B, 0x00, 0x00, 0x00, 0x00 };
        Assert.False(AppleSessionProtocol.TryParse(buf, out _, out _));
    }
}
