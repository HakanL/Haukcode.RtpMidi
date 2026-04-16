namespace Haukcode.RtpMidi;

/// <summary>
/// Apple MIDI session-control packet types.
/// Packets begin with 0xFF 0xFF followed by the 2-byte ASCII command.
/// </summary>
public enum AppleSessionCommand : ushort
{
    Invitation        = 0x494E,  // 'I','N'
    InvitationAccepted = 0x4F4B, // 'O','K'
    InvitationRefused  = 0x4E4F, // 'N','O'
    EndSession         = 0x4259, // 'B','Y'
    ClockSync          = 0x434B, // 'C','K'
    ReceiverFeedback   = 0x5253, // 'R','S'
}

/// <summary>Payload for IN / OK / NO / BY packets.</summary>
public record SessionPacket(
    AppleSessionCommand Command,
    uint ProtocolVersion,
    uint InitiatorToken,
    uint Ssrc,
    string? Name);

/// <summary>
/// Payload for CK packets.
/// The <see cref="Count"/> field indicates how many timestamps are populated:
///   0 = CK0 (initiator → responder, only Timestamp1 set)
///   1 = CK1 (responder → initiator, Timestamp1+2 set)
///   2 = CK2 (initiator → responder, all three set)
/// </summary>
public record ClockSyncPacket(
    uint Ssrc,
    byte Count,
    ulong Timestamp1,
    ulong Timestamp2,
    ulong Timestamp3);

/// <summary>
/// Codec for the Apple MIDI session-control protocol (control port N).
/// All values are big-endian on the wire (network byte order).
/// </summary>
public static class AppleSessionProtocol
{
    private const byte SignatureByte = 0xFF;
    public const uint ProtocolVersion = 2;

    // Minimum sizes
    private const int MinSessionPacketSize = 16; // sig(2)+cmd(2)+ver(4)+token(4)+ssrc(4)
    private const int ClockSyncPacketSize  = 36; // sig(2)+cmd(2)+ssrc(4)+count(1)+pad(3)+ts1(8)+ts2(8)+ts3(8)

    // -------------------------------------------------------------------------
    // Parse
    // -------------------------------------------------------------------------

    public static bool TryParse(ReadOnlySpan<byte> data, out SessionPacket? packet, out ClockSyncPacket? clockPacket)
    {
        packet = null;
        clockPacket = null;

        if (data.Length < 4 || data[0] != SignatureByte || data[1] != SignatureByte)
            return false;

        var cmd = (AppleSessionCommand)BinaryPrimitives.ReadUInt16BigEndian(data[2..]);

        if (cmd == AppleSessionCommand.ClockSync)
            return TryParseClock(data, out clockPacket);

        return TryParseSession(data, cmd, out packet);
    }

    private static bool TryParseSession(ReadOnlySpan<byte> data, AppleSessionCommand cmd, out SessionPacket? packet)
    {
        packet = null;
        if (data.Length < MinSessionPacketSize)
            return false;

        var version = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var token   = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        var ssrc    = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);

        string? name = null;
        if (data.Length > 16)
        {
            // Null-terminated name string starts at offset 16
            var nameSpan = data[16..];
            int nullIdx = nameSpan.IndexOf((byte)0);
            name = Encoding.UTF8.GetString(nullIdx >= 0 ? nameSpan[..nullIdx] : nameSpan);
        }

        packet = new SessionPacket(cmd, version, token, ssrc, name);
        return true;
    }

    private static bool TryParseClock(ReadOnlySpan<byte> data, out ClockSyncPacket? packet)
    {
        packet = null;
        if (data.Length < ClockSyncPacketSize)
            return false;

        var ssrc  = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var count = data[8];
        // bytes 9-11 are padding
        var ts1 = BinaryPrimitives.ReadUInt64BigEndian(data[12..]);
        var ts2 = BinaryPrimitives.ReadUInt64BigEndian(data[20..]);
        var ts3 = BinaryPrimitives.ReadUInt64BigEndian(data[28..]);

        packet = new ClockSyncPacket(ssrc, count, ts1, ts2, ts3);
        return true;
    }

    // -------------------------------------------------------------------------
    // Encode
    // -------------------------------------------------------------------------

    public static byte[] Encode(SessionPacket packet)
    {
        var nameBytes = packet.Name != null
            ? Encoding.UTF8.GetBytes(packet.Name)
            : [];

        // header(16) + name + NUL terminator
        var buf = new byte[16 + nameBytes.Length + 1];

        buf[0] = SignatureByte;
        buf[1] = SignatureByte;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)packet.Command);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), packet.ProtocolVersion);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), packet.InitiatorToken);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12), packet.Ssrc);

        if (nameBytes.Length > 0)
            nameBytes.CopyTo(buf, 16);
        // buf[16 + nameBytes.Length] = 0x00  (already zero from new byte[])

        return buf;
    }

    public static byte[] EncodeClock(ClockSyncPacket packet)
    {
        var buf = new byte[ClockSyncPacketSize];

        buf[0] = SignatureByte;
        buf[1] = SignatureByte;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), (ushort)AppleSessionCommand.ClockSync);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), packet.Ssrc);
        buf[8] = packet.Count;
        // bytes 9-11: padding, already zero
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(12), packet.Timestamp1);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(20), packet.Timestamp2);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(28), packet.Timestamp3);

        return buf;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a random 32-bit SSRC suitable for a new local session.
    /// </summary>
    public static uint GenerateSsrc()
        => (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    /// <summary>
    /// Returns a random 32-bit initiator token for an outgoing invitation.
    /// </summary>
    public static uint GenerateInitiatorToken()
        => (uint)Random.Shared.NextInt64(1, uint.MaxValue);
}
