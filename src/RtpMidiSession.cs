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

    /// <summary>
    /// Optional diagnostic hook. When set, every outbound / inbound packet
    /// and every session-protocol event (IN/OK/NO/BY/CK) is routed here so
    /// callers can correlate wire traffic with peer disconnections. No-op
    /// when null (default). Assign from application startup, e.g.
    /// <c>RtpMidiSession.TraceHook = msg =&gt; logger.LogTrace(msg);</c>.
    ///
    /// Call sites MUST guard with <c>if (TraceHook != null)</c> before
    /// building the message string — the interpolation cost is otherwise
    /// paid on every packet even when no hook is attached.
    /// </summary>
    public static Action<string>? TraceHook;

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

    // --- SysEx reassembly state (receive side) ---
    private List<byte>? sysExBuffer;

    // --- SysEx fragmentation threshold (send side) ---
    private const int MaxSysExBytesPerPacket = 128;

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

        if (TraceHook != null)
            TraceHook($"[{localName}] ConnectAsync target={controlEndPoint} localSsrc={localSsrc:X8} initiatorToken={initiatorToken:X8} seqStart={sequenceNumber}");

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

        if (TraceHook != null)
            TraceHook($"[{localName}] ListenAsync controlPort={controlPort} dataPort={dataPort} localSsrc={localSsrc:X8} seqStart={sequenceNumber}");

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

        int fragmentIndex = 0;
        foreach (var segment in BuildSysExFragments(midiBytes))
        {
            var ts  = RtpMidiPacket.CurrentTimestamp(sessionStart);
            var pkt = RtpMidiPacket.Encode(localSsrc, sequenceNumber, ts, segment.Span);

            if (TraceHook != null)
                TraceHook($"[{localName}] TX seq={sequenceNumber} ts={ts} frag={fragmentIndex} midi={segment.Length}B pkt={pkt.Length}B firstMidi={Preview(segment.Span, 12)} pkt={Preview(pkt, 24)}");
            sequenceNumber++;
            fragmentIndex++;

            await dataSocket!.SendAsync(pkt, ct);
        }
    }

    /// <summary>
    /// Splits a large SysEx message into segments per RFC 6295 §3.3.
    /// Non-SysEx messages or SysEx that fits within <see cref="MaxSysExBytesPerPacket"/>
    /// are returned as-is in a single-element enumeration.
    ///
    /// Segment markers:
    ///   First segment:  F0 … F0  (trailing F0 = "continuation follows")
    ///   Middle segment: F7 … F0
    ///   Last segment:   F7 … F7
    ///   Unfragmented:   F0 … F7  (passed through unchanged)
    /// </summary>
    internal static IEnumerable<ReadOnlyMemory<byte>> BuildSysExFragments(ReadOnlyMemory<byte> midiBytes)
    {
        var span = midiBytes.Span;

        // Only fragment when: it's a SysEx AND it exceeds the per-packet limit.
        if (span.Length <= MaxSysExBytesPerPacket
            || span[0] != 0xF0
            || span[span.Length - 1] != 0xF7)
        {
            yield return midiBytes;
            yield break;
        }

        // Inner payload: strip the outer F0 and F7.
        int innerStart  = 1;
        int innerLength = midiBytes.Length - 2;

        // Each fragment: [opening] [inner chunk] [closing] ≤ MaxSysExBytesPerPacket bytes.
        int maxInnerPerFragment = MaxSysExBytesPerPacket - 2;

        int offset  = 0;
        bool isFirst = true;

        while (offset < innerLength)
        {
            int remaining = innerLength - offset;
            bool isLast   = remaining <= maxInnerPerFragment;
            int chunkSize = isLast ? remaining : maxInnerPerFragment;

            byte opening = isFirst ? (byte)0xF0 : (byte)0xF7;
            byte closing = isLast  ? (byte)0xF7 : (byte)0xF0;

            var seg = new byte[1 + chunkSize + 1];
            seg[0] = opening;
            midiBytes.Slice(innerStart + offset, chunkSize).Span.CopyTo(seg.AsSpan(1));
            seg[seg.Length - 1] = closing;

            yield return seg;

            offset  += chunkSize;
            isFirst  = false;
        }
    }

    private static string Preview(ReadOnlySpan<byte> bytes, int maxBytes)
    {
        int count = Math.Min(bytes.Length, maxBytes);
        var sb = new System.Text.StringBuilder(count * 3);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        if (bytes.Length > maxBytes) sb.Append(" …");
        return sb.ToString();
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
            if (TraceHook != null)
                TraceHook($"[{localName}] TX session Invitation to {remote} (attempt {attempt + 1}/3) token={initiatorToken:X8} ssrc={localSsrc:X8}");
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
            if (TraceHook != null)
                TraceHook($"[{localName}] TX session InvitationAccepted to {result.RemoteEndPoint} remote='{packet.Name}' remoteSsrc={remoteSsrc:X8} localSsrc={localSsrc:X8}");
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
                    {
                        if (TraceHook != null)
                            TraceHook($"[{localName}] RX clock ({(isData ? "data" : "control")}) from {result.RemoteEndPoint}");
                        await HandleClockPacketAsync(socket, clockPkt, ct);
                    }
                    else if (sessionPkt != null)
                    {
                        if (TraceHook != null)
                            TraceHook($"[{localName}] RX session {sessionPkt.Command} ({(isData ? "data" : "control")}) from {result.RemoteEndPoint} remote='{sessionPkt.Name}' ssrc={sessionPkt.Ssrc:X8}");
                        HandleSessionControlPacket(sessionPkt);
                    }
                }
                else if (isData && RtpMidiPacket.TryParse(buf, out var midiPkt) && midiPkt != null)
                {
                    if (!midiPkt.MidiBytes.IsEmpty)
                    {
                        if (TraceHook != null)
                            TraceHook($"[{localName}] RX seq={midiPkt.SequenceNumber} ts={midiPkt.Timestamp} midi={midiPkt.MidiBytes.Length}B firstMidi={Preview(midiPkt.MidiBytes.Span, 12)}");

                        var assembled = AssembleSysExFragment(midiPkt.MidiBytes);
                        if (assembled.HasValue)
                            midiSubject.OnNext(assembled.Value);
                    }
                }
                else
                {
                    if (TraceHook != null)
                        TraceHook($"[{localName}] RX unrecognised {buf.Length}B from {result.RemoteEndPoint} first={Preview(buf, 16)}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException)
        {
            // If the socket died while we were still connected (not a planned shutdown),
            // trigger a clean disconnect so reconnect logic can kick in.
            if (State == SessionState.Connected)
                _ = DisconnectAsync();
        }
    }

    // -------------------------------------------------------------------------
    // SysEx reassembly
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reassembles a potentially fragmented SysEx from a single RTP-MIDI packet's MIDI bytes.
    ///
    /// Per RFC 6295 §3.3, segments are identified by their opening and closing bytes:
    ///   F0 … F0  → first fragment  (buffer, wait for more)
    ///   F7 … F0  → middle fragment (buffer, wait for more)
    ///   F7 … F7  → last fragment   (emit assembled SysEx)
    ///   F0 … F7  → complete SysEx  (emit immediately)
    ///   Anything else → emit immediately (non-SysEx or malformed; reset buffer)
    /// </summary>
    internal ReadOnlyMemory<byte>? AssembleSysExFragment(ReadOnlyMemory<byte> midiBytes)
    {
        var span  = midiBytes.Span;
        byte first = span[0];
        byte last  = span[span.Length - 1];

        // First fragment of a fragmented SysEx: F0 … F0
        if (first == 0xF0 && last == 0xF0)
        {
            // Buffer everything except the trailing continuation F0.
            // Use a generous initial capacity since we expect more fragments to follow.
            sysExBuffer = new List<byte>(512);
            sysExBuffer.AddRange(span[..^1].ToArray()); // F0 [data...]
            return null;
        }

        // Middle or last fragment: opens with F7 and we have an active buffer.
        if (first == 0xF7 && sysExBuffer != null)
        {
            if (last == 0xF7)
            {
                // Last fragment — append [data... F7] (skip the opening F7), then emit.
                sysExBuffer.AddRange(span[1..].ToArray());
                var result = sysExBuffer.ToArray();
                sysExBuffer = null;
                return result;
            }
            else
            {
                // Middle fragment (ends with F0) — append inner bytes only, skipping both markers.
                sysExBuffer.AddRange(span[1..^1].ToArray());
                return null;
            }
        }

        // Complete SysEx (F0 … F7), non-SysEx, or orphan F7 without a prior buffer
        // — emit as-is and reset any stale buffer.
        sysExBuffer = null;
        return midiBytes;
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

    private async Task SendByAsync(UdpClient? socket, IPEndPoint? remote)
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
            if (TraceHook != null)
                TraceHook($"[{localName}] TX session EndSession (BY) to {remote}");
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
    // Reconnect loops
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task ConnectWithReconnectAsync(IPEndPoint controlEndPoint, TimeSpan reconnectDelay, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            // Subscribe before connecting so we cannot miss a rapid disconnect.
            var sessionEndedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = StateChanges
                .Where(s => s == SessionState.Idle)
                .Subscribe(_ => sessionEndedTcs.TrySetResult());

            try
            {
                await ConnectAsync(controlEndPoint, ct);
                // Wait until the session falls back to Idle (BY from remote, socket error, etc.)
                await sessionEndedTcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Handshake failed or unexpected error — ensure clean state before retrying.
                await DisconnectAsync();
            }

            if (ct.IsCancellationRequested) return;

            try { await Task.Delay(reconnectDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <inheritdoc/>
    public async Task ListenWithReconnectAsync(int controlPort, TimeSpan reconnectDelay, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var sessionEndedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = StateChanges
                .Where(s => s == SessionState.Idle)
                .Subscribe(_ => sessionEndedTcs.TrySetResult());

            try
            {
                await ListenAsync(controlPort, ct);
                await sessionEndedTcs.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await DisconnectAsync();
            }

            if (ct.IsCancellationRequested) return;

            try { await Task.Delay(reconnectDelay, ct); }
            catch (OperationCanceledException) { return; }
        }
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
