using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using Haukcode.RtpMidi;

namespace RtpMidi.Tests;

/// <summary>
/// Exercises the session-level watchdog behaviors added alongside the
/// RFC-compliance rework:
///
///   1) <c>PeerLivenessTimeout</c> — a dedicated watchdog task on each
///      connected session tears the session down after configured silence.
///   2) Same-endpoint / different-SSRC reinvite — a Connected listener
///      accepts a reinvite from the same remote endpoint by disconnecting
///      the stale session so the rebooted peer can reconnect immediately
///      instead of waiting out the liveness watchdog.
///
/// Both tests drive the real socket path on loopback with short timeouts
/// (≤ 2 s) so the suite stays fast without relying on fragile timing.
/// </summary>
[Trait("Category", "Session")]
public class SessionLivenessTests
{
    private static int FreeUdpPortPair()
    {
        // Reserve an (N, N+1) pair: loop until we bind two consecutive
        // ports without conflict.
        for (int attempt = 0; attempt < 32; attempt++)
        {
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            try
            {
                using var pair = new UdpClient(new IPEndPoint(IPAddress.Loopback, port + 1));
                return port;
            }
            catch (SocketException)
            {
                // Neighbour port taken; pick another seed.
            }
        }
        throw new IOException("Could not find a free consecutive UDP port pair on loopback.");
    }

    [Fact]
    public async Task PeerLivenessWatchdog_FiresAfterConfiguredTimeout()
    {
        int port = FreeUdpPortPair();

        // Short window so the test completes in well under a second.
        await using var listener = new RtpMidiSession("listener")
        {
            PeerLivenessTimeout = TimeSpan.FromMilliseconds(400),
        };
        await using var initiator = new RtpMidiSession("initiator");

        // Observe state transitions so we can wait for Idle.
        var idleReached = new TaskCompletionSource();
        using var sub = listener.StateChanges
            .Where(s => s == SessionState.Idle)
            .Subscribe(_ => idleReached.TrySetResult());

        using var listenCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listenTask = listener.ListenAsync(port, listenCts.Token);
        await Task.Delay(100); // let the listener bind

        await initiator.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        await listenTask; // both ports handshaken, listener is Connected

        Assert.Equal(SessionState.Connected, listener.State);

        // Simulate a dead peer: kill sockets WITHOUT sending BY. From the
        // listener's perspective, packets just stop arriving.
        initiator.SimulateAbruptDisconnect();

        // Watchdog ticks at ~100 ms (PeerLivenessTimeout / 4). Detection
        // latency should be well under 2 s even on a loaded CI runner.
        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await idleReached.Task.WaitAsync(waitCts.Token);

        Assert.Equal(SessionState.Idle, listener.State);
    }

    [Fact]
    public async Task ReinviteFromSameEndpointWithNewSsrc_DisconnectsStaleSession()
    {
        int port = FreeUdpPortPair();

        // Long watchdog: if the reinvite path works we should go Idle
        // within milliseconds; if it doesn't, the watchdog wouldn't save us
        // in the test's 3 s budget.
        await using var listener = new RtpMidiSession("listener")
        {
            PeerLivenessTimeout = TimeSpan.FromMinutes(5),
        };
        await using var initiator1 = new RtpMidiSession("initiator-1");

        var idleReached = new TaskCompletionSource();
        using var sub = listener.StateChanges
            .Where(s => s == SessionState.Idle)
            .Subscribe(_ => idleReached.TrySetResult());

        using var listenCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listenTask = listener.ListenAsync(port, listenCts.Token);
        await Task.Delay(100);

        await initiator1.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        await listenTask;

        Assert.Equal(SessionState.Connected, listener.State);

        // Capture the first initiator's local ctrl-port endpoint BEFORE it
        // dies. We'll rebind a raw UDP socket there to fake a "same app,
        // rebooted with a new SSRC" invitation — which is exactly the
        // pattern the new reinvite path is designed to handle.
        var firstLocalCtrl = GetPrivateUdpClientLocalEndpoint(initiator1, "controlSocket");
        Assert.NotNull(firstLocalCtrl);

        initiator1.SimulateAbruptDisconnect();
        // Give the OS a moment to release the bound port.
        await Task.Delay(50);

        // Craft an IN packet with a NEW SSRC (different from initiator-1's)
        // and send it from the same source port the listener already knows.
        using var reinviteSock = new UdpClient(firstLocalCtrl!);
        uint newSsrc = 0xDEADBEEFu;
        byte[] inviteBytes = BuildInvitationPacket(
            token: 0x12345678u, ssrc: newSsrc, name: "initiator-2");
        await reinviteSock.SendAsync(inviteBytes, inviteBytes.Length,
                                     new IPEndPoint(IPAddress.Loopback, port));

        // Listener should recognise the same-endpoint / new-SSRC pattern
        // and tear down the stale session (firing StateChanges → Idle).
        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await idleReached.Task.WaitAsync(waitCts.Token);

        Assert.Equal(SessionState.Idle, listener.State);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Peek at a private <c>UdpClient</c> field on an RtpMidiSession. Used
    /// by the reinvite test to learn initiator-1's local ctrl-port so we
    /// can re-bind a raw socket there.
    /// </summary>
    private static IPEndPoint? GetPrivateUdpClientLocalEndpoint(RtpMidiSession sess, string fieldName)
    {
        var f = typeof(RtpMidiSession).GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (f == null) return null;
        if (f.GetValue(sess) is not UdpClient u) return null;
        return u.Client.LocalEndPoint as IPEndPoint;
    }

    [Fact]
    public async Task InvitationWithUnsupportedVersion_IsRefusedWithNO()
    {
        // Raw UDP peer sending a malformed IN with protocolVersion = 99.
        // Haukcode's AcceptPortAsync should reply AM_NO and continue listening
        // rather than promoting the session. Without the version gate, it
        // would quietly accept and attempt a handshake over a dialect it
        // doesn't actually speak.
        int port = FreeUdpPortPair();

        await using var listener = new RtpMidiSession("listener");
        using var listenCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var listenTask = listener.ListenAsync(port, listenCts.Token);
        await Task.Delay(100);

        using var rawPeer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] badInvite = BuildInvitationPacket(
            token: 0xCAFEBABEu, ssrc: 0x11223344u, name: "bad-version", version: 99);
        await rawPeer.SendAsync(badInvite, badInvite.Length,
                                new IPEndPoint(IPAddress.Loopback, port));

        // Expect an AM_NO back.
        using var recvCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var reply = await rawPeer.ReceiveAsync(recvCts.Token);

        // First two bytes = 0xFFFF magic; bytes 2-3 = 'N','O'.
        Assert.True(reply.Buffer.Length >= 4);
        Assert.Equal(0xFF, reply.Buffer[0]);
        Assert.Equal(0xFF, reply.Buffer[1]);
        Assert.Equal((byte)'N', reply.Buffer[2]);
        Assert.Equal((byte)'O', reply.Buffer[3]);

        // Listener should still be listening (no session established).
        Assert.Equal(SessionState.ConnectingControl, listener.State);

        listenCts.Cancel();
        try { await listenTask; } catch { /* expected */ }
    }

    /// <summary>Hand-build an AppleMIDI Invitation (IN) packet.</summary>
    private static byte[] BuildInvitationPacket(uint token, uint ssrc, string name, uint version = 2)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        byte[] buf = new byte[16 + nameBytes.Length + 1];
        // 0xFFFF magic
        buf[0] = 0xFF; buf[1] = 0xFF;
        // "IN"
        buf[2] = (byte)'I'; buf[3] = (byte)'N';
        // protocol version, big-endian 32-bit
        buf[4] = (byte)(version >> 24);
        buf[5] = (byte)(version >> 16);
        buf[6] = (byte)(version >> 8);
        buf[7] = (byte)version;
        // initiator token
        buf[8]  = (byte)(token >> 24);
        buf[9]  = (byte)(token >> 16);
        buf[10] = (byte)(token >> 8);
        buf[11] = (byte)token;
        // SSRC
        buf[12] = (byte)(ssrc >> 24);
        buf[13] = (byte)(ssrc >> 16);
        buf[14] = (byte)(ssrc >> 8);
        buf[15] = (byte)ssrc;
        // Name + trailing NUL
        Buffer.BlockCopy(nameBytes, 0, buf, 16, nameBytes.Length);
        buf[^1] = 0;
        return buf;
    }
}
