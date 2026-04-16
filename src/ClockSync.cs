namespace Haukcode.RtpMidi;

/// <summary>
/// Handles the 3-way Apple MIDI clock-sync exchange (CK0 → CK1 → CK2).
///
/// The exchange runs on a ~10 s interval to keep the session alive and to
/// refine the latency estimate.  Hardware bridges (e.g. iConnectivity mioXM)
/// require this exchange to complete before trusting the connection.
///
/// Timestamp unit: 100 µs (10 000 ticks per second).
/// </summary>
public sealed class ClockSync
{
    private readonly Func<ulong> getTimestamp;
    private ulong latestTimestamp1;
    private readonly DateTime sessionStart;

    /// <summary>
    /// Estimated one-way latency to the remote peer, in 100 µs ticks.
    /// Updated after each completed CK2 exchange.
    /// </summary>
    public ulong LatencyTicks { get; private set; }

    /// <summary>
    /// Estimated clock offset between local and remote, in 100 µs ticks.
    /// Add this to a remote timestamp to get the equivalent local timestamp.
    /// </summary>
    public long OffsetTicks { get; private set; }

    public ClockSync(DateTime sessionStart, Func<ulong>? getTimestamp = null)
    {
        this.sessionStart = sessionStart;
        this.getTimestamp = getTimestamp ?? (() => SessionTimestamp(sessionStart));
    }

    // -------------------------------------------------------------------------
    // Initiator side (we initiate the exchange)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the CK0 packet to send to the remote (count=0, only Timestamp1 set).
    /// Call this to start a new clock-sync cycle.
    /// </summary>
    public ClockSyncPacket BuildCk0(uint ssrc)
    {
        latestTimestamp1 = getTimestamp();
        return new ClockSyncPacket(ssrc, Count: 0, latestTimestamp1, 0, 0);
    }

    /// <summary>
    /// Processes an incoming CK1 packet (count=1) and builds the CK2 reply.
    /// Also updates <see cref="LatencyTicks"/> and <see cref="OffsetTicks"/>.
    /// </summary>
    public ClockSyncPacket HandleCk1AndBuildCk2(uint ssrc, ClockSyncPacket ck1)
    {
        var ts3 = getTimestamp();

        // Latency = (ts3 - ts1) / 2
        LatencyTicks = (ts3 - ck1.Timestamp1) / 2;

        // Offset: remote time at ts2 minus the expected local time (ts1 + latency)
        OffsetTicks = (long)ck1.Timestamp2 - (long)(ck1.Timestamp1 + LatencyTicks);

        return new ClockSyncPacket(ssrc, Count: 2, ck1.Timestamp1, ck1.Timestamp2, ts3);
    }

    // -------------------------------------------------------------------------
    // Responder side (remote initiates, we respond)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Processes an incoming CK0 packet and builds the CK1 reply.
    /// </summary>
    public ClockSyncPacket HandleCk0AndBuildCk1(uint ssrc, ClockSyncPacket ck0)
    {
        var ts2 = getTimestamp();
        return new ClockSyncPacket(ssrc, Count: 1, ck0.Timestamp1, ts2, 0);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Current timestamp in 100 µs ticks relative to <paramref name="sessionStart"/>.
    /// Apple MIDI uses session-relative timestamps, not Unix epoch.
    /// </summary>
    public static ulong SessionTimestamp(DateTime sessionStart)
        => (ulong)((DateTime.UtcNow - sessionStart).TotalMilliseconds * 10);
}
