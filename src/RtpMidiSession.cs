using System.Reactive.Subjects;
using System.Reactive.Linq;

namespace Haukcode.RtpMidi;

/// <summary>
/// Production RTP-MIDI session (RFC 6295 + Apple MIDI session protocol).
///
/// Supports both initiator and responder roles:
///   - Initiator: call <see cref="ConnectAsync"/> with a known peer endpoint.
///   - Responder: call <see cref="ListenAsync"/> to accept incoming connections.
///
/// Architecture:
///   Control port (N)  — Apple session control packets (IN/OK/NO/BY/CK)
///   Data port   (N+1) — RTP-MIDI MIDI payload packets
///
/// Each port completes the Apple session handshake independently before
/// MIDI data flows, matching the macOS CoreMIDI reference implementation.
/// </summary>
public sealed class RtpMidiSession : IRtpMidiSession
{
    // --- Configuration defaults ---
    private const int    DefaultClockSyncIntervalMs = 10_000;
    private const int    HandshakeTimeoutMs         = 5_000;
    private const int    MaxUdpPayload              = 65_507;

    // --- Local identity ---
    private readonly string localName;
    private readonly uint   localSsrc;

    // --- Session state ---
    private readonly Subject<ReadOnlyMemory<byte>> midiSubject  = new();
    private readonly Subject<SessionState>          stateSubject = new();
    private SessionState _state = SessionState.Idle;

    private DateTime sessionStart;
    private ClockSync? clockSync;
    private uint remoteSsrc;
    private uint initiatorToken;

    // --- Sockets ---
    private UdpClient? controlSocket;
    private UdpClient? dataSocket;
    private IPEndPoint? remoteControlEp;
    private IPEndPoint? remoteDataEp;

    // --- Background loop cancellation ---
    private CancellationTokenSource? loopCts;
    private Task? receiveControlTask;
    private Task? receiveDataTask;
    private Task? clockSyncTask;

    // --- Sequence counter for outbound RTP packets ---
    private ushort sequenceNumber;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <param name="localName">Name announced to peers (e.g. "DMX Core 100").</param>
    public RtpMidiSession(string localName)
    {
        this.localName = localName;
        localSsrc = AppleSessionProtocol.GenerateSsrc();
    }

    // -------------------------------------------------------------------------
    // IRtpMidiSession
    // -------------------------------------------------------------------------

    public IObservable<ReadOnlyMemory<byte>> MidiReceived
        => midiSubject.AsObservable();

    public IObservable<SessionState> StateChanges
        => stateSubject.AsObservable();

    public SessionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            stateSubject.OnNext(value);
        }
    }

    public string? RemoteName { get; private set; }

    // -------------------------------------------------------------------------
    // Connect (initiator role)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Connect to a remote peer as the initiator.
    /// <paramref name="controlEndPoint"/> is the peer's control port N;
    /// the data port N+1 is derived automatically.
    /// </summary>
    public async Task ConnectAsync(IPEndPoint controlEndPoint, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(midiSubject.IsDisposed, this);
        if (State != SessionState.Idle)
            throw new InvalidOperationException($"Session is not idle (current state: {State}).");

        remoteControlEp = controlEndPoint;
        remoteDataEp    = new IPEndPoint(controlEndPoint.Address, controlEndPoint.Port + 1);

        sessionStart     = DateTime.UtcNow;
        clockSync        = new ClockSync(sessionStart);
        initiatorToken   = AppleSessionProtocol.GenerateInitiatorToken();
        sequenceNumber   = (ushort)Random.Shared.Next(0, ushort.MaxValue);

        // Bind local sockets on ephemeral ports (OS assigns)
        controlSocket = new UdpClient(0);
        dataSocket    = new UdpClient(0);

        // Connect so we can use Send/Receive directly
        controlSocket.Connect(remoteControlEp);
        dataSocket.Connect(remoteDataEp);

        State = SessionState.ConnectingControl;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HandshakeTimeoutMs);

        // Step 1: Handshake control port
        await HandshakePortAsync(controlSocket, remoteControlEp, timeoutCts.Token);
        State = SessionState.ConnectingData;

        // Step 2: Handshake data port
        await HandshakePortAsync(dataSocket, remoteDataEp, timeoutCts.Token);
        State = SessionState.Connected;

        // Start receive loops and clock sync
        loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        receiveControlTask = ReceiveLoopAsync(controlSocket, isData: false, loopCts.Token);
        receiveDataTask    = ReceiveLoopAsync(dataSocket,    isData: true,  loopCts.Token);
        clockSyncTask      = ClockSyncLoopAsync(loopCts.Token);
    }

    // -------------------------------------------------------------------------
    // Listen (responder role)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Listen for an incoming connection on <paramref name="controlPort"/> (N).
    /// The data port N+1 is opened automatically.
    /// Returns when a peer has completed the handshake on both ports.
    /// </summary>
    public async Task ListenAsync(int controlPort, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(midiSubject.IsDisposed, this);
        if (State != SessionState.Idle)
            throw new InvalidOperationException($"Session is not idle (current state: {State}).");

        int dataPort = controlPort + 1;

        controlSocket = new UdpClient(controlPort);
        dataSocket    = new UdpClient(dataPort);

        sessionStart = DateTime.UtcNow;
        clockSync    = new ClockSync(sessionStart);
        sequenceNumber = (ushort)Random.Shared.Next(0, ushort.MaxValue);

        State = SessionState.ConnectingControl;

        // Wait indefinitely for the peer to initiate — only ct (Ctrl+C) can cancel this.
        remoteControlEp = await AcceptPortAsync(controlSocket, ct);

        // Peer has started connecting. Now the data port IN should arrive promptly —
        // apply a timeout to catch half-finished handshakes.
        State = SessionState.ConnectingData;

        using var dataTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dataTimeoutCts.CancelAfter(HandshakeTimeoutMs);

        try
        {
            remoteDataEp = await AcceptPortAsync(dataSocket, dataTimeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"RTP-MIDI data port handshake timed out waiting for IN on port {dataPort}. " +
                $"Ensure UDP port {dataPort} is reachable (check firewall rules).");
        }

        // Connect sockets so receive loops work symmetrically
        controlSocket.Connect(remoteControlEp);
        dataSocket.Connect(remoteDataEp);

        State = SessionState.Connected;

        loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        receiveControlTask = ReceiveLoopAsync(controlSocket, isData: false, loopCts.Token);
        receiveDataTask    = ReceiveLoopAsync(dataSocket,    isData: true,  loopCts.Token);
        clockSyncTask      = ClockSyncLoopAsync(loopCts.Token);
    }

    // -------------------------------------------------------------------------
    // Send MIDI
    // -------------------------------------------------------------------------

    public async Task SendMidiAsync(ReadOnlyMemory<byte> midiBytes, CancellationToken ct = default)
    {
        if (State != SessionState.Connected)
            throw new InvalidOperationException("Not connected.");

        var ts  = RtpMidiPacket.CurrentTimestamp(sessionStart);
        var pkt = RtpMidiPacket.Encode(localSsrc, sequenceNumber++, ts, midiBytes.Span);

        await dataSocket!.SendAsync(pkt, ct);
    }

    // -------------------------------------------------------------------------
    // Disconnect
    // -------------------------------------------------------------------------

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (State == SessionState.Idle || State == SessionState.Disconnecting)
            return;

        var wasConnected = State == SessionState.Connected;
        State = SessionState.Disconnecting;

        // Only send BY if we completed the full handshake — no point notifying
        // the peer about a session that was never established.
        if (wasConnected)
        {
            await SendByAsync(controlSocket, remoteControlEp);
            await SendByAsync(dataSocket,    remoteDataEp);
        }

        await StopLoopsAsync();
        CloseAndNullSockets();

        State = SessionState.Idle;
    }

    // -------------------------------------------------------------------------
    // Handshake helpers
    // -------------------------------------------------------------------------

    /// <summary>Send IN, wait for OK (initiator role).</summary>
    private async Task HandshakePortAsync(UdpClient socket, IPEndPoint remote, CancellationToken ct)
    {
        var invite = new SessionPacket(
            AppleSessionCommand.Invitation,
            AppleSessionProtocol.ProtocolVersion,
            initiatorToken,
            localSsrc,
            localName);

        var encoded = AppleSessionProtocol.Encode(invite);

        // Retry up to 3 times with 1 s gap (matches Apple reference behavior)
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await socket.SendAsync(encoded, ct);

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delayCts.CancelAfter(1_000);

            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(delayCts.Token);
                    if (TryHandleSessionPacket(result.Buffer, out var response) && response != null)
                    {
                        if (response.Command == AppleSessionCommand.InvitationAccepted)
                        {
                            remoteSsrc  = response.Ssrc;
                            RemoteName  = response.Name;
                            return;
                        }
                        if (response.Command == AppleSessionCommand.InvitationRefused)
                            throw new InvalidOperationException($"Remote refused invitation (NO). Remote: {remote}");
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Inner timeout expired — retry
            }
        }

        throw new TimeoutException($"RTP-MIDI handshake timed out for {remote}.");
    }

    /// <summary>Wait for IN, reply OK (responder role). Returns the remote endpoint.</summary>
    private async Task<IPEndPoint> AcceptPortAsync(UdpClient socket, CancellationToken ct)
    {
        while (true)
        {
            var result = await socket.ReceiveAsync(ct);

            if (!AppleSessionProtocol.TryParse(result.Buffer, out var packet, out _) || packet == null)
                continue;

            if (packet.Command != AppleSessionCommand.Invitation)
                continue;

            remoteSsrc = packet.Ssrc;
            RemoteName = packet.Name;
            initiatorToken = packet.InitiatorToken;

            var ok = new SessionPacket(
                AppleSessionCommand.InvitationAccepted,
                AppleSessionProtocol.ProtocolVersion,
                packet.InitiatorToken,
                localSsrc,
                localName);

            var encoded = AppleSessionProtocol.Encode(ok);
            await socket.SendAsync(encoded, result.RemoteEndPoint, ct);

            return result.RemoteEndPoint;
        }
    }

    private static bool TryHandleSessionPacket(byte[] buffer, out SessionPacket? packet)
    {
        packet = null;
        if (!AppleSessionProtocol.TryParse(buffer, out var p, out _))
            return false;
        packet = p;
        return packet != null;
    }

    // -------------------------------------------------------------------------
    // Receive loops
    // -------------------------------------------------------------------------

    private async Task ReceiveLoopAsync(UdpClient socket, bool isData, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(ct);
                var buf    = result.Buffer;

                if (AppleSessionProtocol.TryParse(buf, out var sessionPkt, out var clockPkt))
                {
                    if (clockPkt != null)
                        await HandleClockPacketAsync(socket, clockPkt, ct);
                    else if (sessionPkt != null)
                        HandleSessionControlPacket(sessionPkt);
                }
                else if (isData && RtpMidiPacket.TryParse(buf, out var midiPkt) && midiPkt != null)
                {
                    if (!midiPkt.MidiBytes.IsEmpty)
                        midiSubject.OnNext(midiPkt.MidiBytes);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { /* socket closed during shutdown */ }
    }

    // -------------------------------------------------------------------------
    // Clock sync
    // -------------------------------------------------------------------------

    private async Task ClockSyncLoopAsync(CancellationToken ct)
    {
        try
        {
            // Initial sync immediately after connecting
            await RunClockSyncAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(DefaultClockSyncIntervalMs, ct);
                await RunClockSyncAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunClockSyncAsync(CancellationToken ct)
    {
        if (clockSync == null || controlSocket == null) return;

        var ck0 = clockSync.BuildCk0(localSsrc);
        await controlSocket.SendAsync(AppleSessionProtocol.EncodeClock(ck0), ct);
        // CK1 arrives in ReceiveLoopAsync → HandleClockPacketAsync → sends CK2
    }

    private async Task HandleClockPacketAsync(UdpClient socket, ClockSyncPacket pkt, CancellationToken ct)
    {
        if (clockSync == null) return;

        switch (pkt.Count)
        {
            case 0:
                // Remote initiated CK0 — we are responder, reply with CK1
                var ck1 = clockSync.HandleCk0AndBuildCk1(localSsrc, pkt);
                await socket.SendAsync(AppleSessionProtocol.EncodeClock(ck1), ct);
                break;

            case 1:
                // We initiated, remote sent CK1 — complete with CK2
                var ck2 = clockSync.HandleCk1AndBuildCk2(localSsrc, pkt);
                await socket.SendAsync(AppleSessionProtocol.EncodeClock(ck2), ct);
                break;

            case 2:
                // CK2 received (we were responder) — exchange complete, no reply needed
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Session control packet handling
    // -------------------------------------------------------------------------

    private void HandleSessionControlPacket(SessionPacket packet)
    {
        if (packet.Command == AppleSessionCommand.EndSession)
        {
            // Remote sent BY — tear down asynchronously
            _ = DisconnectAsync();
        }
    }

    // -------------------------------------------------------------------------
    // Shutdown helpers
    // -------------------------------------------------------------------------

    private static async Task SendByAsync(UdpClient? socket, IPEndPoint? remote)
    {
        if (socket == null || remote == null) return;
        try
        {
            var by = new SessionPacket(
                AppleSessionCommand.EndSession,
                AppleSessionProtocol.ProtocolVersion,
                0,
                0,
                null);
            await socket.SendAsync(AppleSessionProtocol.Encode(by));
        }
        catch { /* best-effort */ }
    }

    private async Task StopLoopsAsync()
    {
        loopCts?.Cancel();
        var tasks = new[] { receiveControlTask, receiveDataTask, clockSyncTask }
            .Where(t => t != null)
            .Select(t => t!);
        try { await Task.WhenAll(tasks); } catch { }
        loopCts?.Dispose();
        loopCts = null;
    }

    private void CloseAndNullSockets()
    {
        controlSocket?.Close();
        dataSocket?.Close();
        controlSocket = null;
        dataSocket = null;
    }

    // -------------------------------------------------------------------------
    // IAsyncDisposable
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        midiSubject.OnCompleted();
        midiSubject.Dispose();
        stateSubject.OnCompleted();
        stateSubject.Dispose();
    }
}
