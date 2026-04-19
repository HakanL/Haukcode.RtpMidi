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
    private uint   localSsrc;

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

    // --- Recovery journal state ---
    private byte[]? lastSysExPayload;
    private ushort  lastSysExSeqNum;

    // --- Per-channel MIDI state for recovery journal chapters P, C, W, N, Q, T, A ---
    private readonly ChannelMidiState[] channelStates = new ChannelMidiState[16];

    // --- System-level MIDI state for recovery journal chapter F ---
    private readonly SystemMidiState systemMidiState = new();

    // --- Inbound sequence tracking for gap detection ---
    private ushort? expectedSeqNum;

    // --- RS (Receiver Feedback) rate limiting ---
    private int rsPacketCount;
    private const int RsEveryNPackets = 10;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <param name="localName">Name announced to peers (e.g. "DMX Core 100").</param>
    public RtpMidiSession(string localName)
    {
        this.localName = localName;
        localSsrc = AppleSessionProtocol.GenerateSsrc();
        for (int i = 0; i < 16; i++)
            channelStates[i] = new ChannelMidiState();
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

    /// <summary>
    /// When <c>true</c> (the default), the session appends an RFC 6295 recovery journal
    /// to every outgoing RTP-MIDI packet.  The journal encodes the most-recent MIDI state
    /// across all chapters: Chapter X (SysEx), Chapter F (System Common), and channel
    /// chapters V+P+C+W+N+Q+T+A (Program Change, Control Change, Pitch Wheel, Note Off,
    /// Note On, Channel Pressure, Poly Key Pressure).
    ///
    /// This allows the remote peer to reconstruct dropped packets from the next received
    /// packet, preventing hard session drops from strict implementations (e.g. Apple CoreMIDI).
    ///
    /// Set to <c>false</c> only on strictly loss-free environments where the extra bytes
    /// are unwanted.
    /// </summary>
    public bool EnableRecoveryJournal { get; set; } = true;

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
        lastSysExPayload = null;
        expectedSeqNum   = null;
        for (int i = 0; i < 16; i++) channelStates[i].Reset();
        systemMidiState.Reset();
        rsPacketCount = 0;

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

        const int MaxSsrcRetries = 3;
        for (int ssrcAttempt = 0; ssrcAttempt < MaxSsrcRetries; ssrcAttempt++)
        {
            // Step 1: Handshake control port
            await HandshakePortAsync(controlSocket, remoteControlEp, timeoutCts.Token);
            State = SessionState.ConnectingData;

            // Step 2: Handshake data port
            await HandshakePortAsync(dataSocket, remoteDataEp, timeoutCts.Token);

            // SSRC collision detection (RFC 3550 §8.2): if local and remote chose the same SSRC,
            // regenerate the local one and transparently retry the handshake.
            if (!DetectAndResolveSsrcCollision())
                break;

            if (ssrcAttempt == MaxSsrcRetries - 1)
                throw new InvalidOperationException(
                    $"SSRC collision persisted after {MaxSsrcRetries} retries (remoteSsrc={remoteSsrc:X8}).");

            State = SessionState.ConnectingControl;
            initiatorToken = AppleSessionProtocol.GenerateInitiatorToken();
        }

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
        sequenceNumber   = (ushort)Random.Shared.Next(0, ushort.MaxValue);
        lastSysExPayload = null;
        expectedSeqNum   = null;
        for (int i = 0; i < 16; i++) channelStates[i].Reset();
        systemMidiState.Reset();
        rsPacketCount = 0;

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

        // SSRC collision detection (RFC 3550 §8.2): if local and remote chose the same SSRC,
        // regenerate the local one. The peer will re-initiate after receiving our BY.
        if (DetectAndResolveSsrcCollision())
        {
            if (TraceHook != null)
                TraceHook($"[{localName}] SSRC collision on listen; sending BY and waiting for peer to re-initiate");
            await SendByAsync(controlSocket, remoteControlEp);
            await SendByAsync(dataSocket,    remoteDataEp);

            // Wait for the peer to re-initiate with a fresh handshake.
            State = SessionState.ConnectingControl;
            remoteControlEp = await AcceptPortAsync(controlSocket, ct);

            State = SessionState.ConnectingData;
            using var retryDataCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            retryDataCts.CancelAfter(HandshakeTimeoutMs);
            try
            {
                remoteDataEp = await AcceptPortAsync(dataSocket, retryDataCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"RTP-MIDI data port handshake timed out on SSRC-collision retry (port {dataPort}).");
            }
            controlSocket.Connect(remoteControlEp);
            dataSocket.Connect(remoteDataEp);

            if (DetectAndResolveSsrcCollision())
                throw new InvalidOperationException(
                    $"SSRC collision persisted after retry (remoteSsrc={remoteSsrc:X8}).");
        }

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

        // Update journal state BEFORE async operations — copy to array to avoid
        // holding a ReadOnlySpan across await points (not permitted in async methods).
        if (EnableRecoveryJournal && midiBytes.Length >= 1)
        {
            var data = midiBytes.ToArray();

            // Track the last complete SysEx for the recovery journal BEFORE fragmenting.
            if (data.Length >= 2 && data[0] == 0xF0 && data[data.Length - 1] == 0xF7)
            {
                lastSysExPayload = data;
                lastSysExSeqNum  = sequenceNumber; // checkpoint = first fragment's seq num
            }
            else
            {
                byte status = data[0];
                if (status < 0xF0)
                {
                    // Channel message — update per-channel state
                    byte channel = (byte)(status & 0x0F);
                    channelStates[channel].ProcessMidi(data);
                }
                else if (status == 0xF1 || status == 0xF2 || status == 0xF3)
                {
                    // System Common — update system state for Chapter F
                    systemMidiState.ProcessMidi(data);
                }
            }
        }

        int fragmentIndex = 0;
        foreach (var segment in BuildSysExFragments(midiBytes))
        {
            // Build the recovery journal combining all accumulated state.
            // The checkpoint is (sequenceNumber - 1): one before the current packet.
            // This ensures that if the current packet is dropped, the NEXT packet's
            // journal (with checkpoint = current sequenceNumber) will satisfy IsInGap.
            byte[]? journal = null;
            if (EnableRecoveryJournal)
            {
                var j = RtpMidiJournal.EncodeFullJournal(
                    (ushort)(sequenceNumber - 1), lastSysExPayload, channelStates, systemMidiState);
                if (j.Length > 0) journal = j;
            }

            var ts  = RtpMidiPacket.CurrentTimestamp(sessionStart);
            var pkt = journal != null
                ? RtpMidiPacket.Encode(localSsrc, sequenceNumber, ts, segment.Span, journal)
                : RtpMidiPacket.Encode(localSsrc, sequenceNumber, ts, segment.Span);

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

            if (!AppleSessionProtocol.TryParse(result.Buffer, out var packet, out _, out _) || packet == null)
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
        if (!AppleSessionProtocol.TryParse(buffer, out var p, out _, out _))
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

                if (AppleSessionProtocol.TryParse(buf, out var sessionPkt, out var clockPkt, out var feedbackPkt))
                {
                    if (clockPkt != null)
                    {
                        if (TraceHook != null)
                            TraceHook($"[{localName}] RX clock ({(isData ? "data" : "control")}) from {result.RemoteEndPoint}");
                        await HandleClockPacketAsync(socket, clockPkt, ct);
                    }
                    else if (feedbackPkt != null)
                    {
                        if (TraceHook != null)
                            TraceHook($"[{localName}] RX session RS from {result.RemoteEndPoint} ssrc={feedbackPkt.Ssrc:X8} lastSeq={feedbackPkt.LastReceivedSequence}");

                        // Advance recovery journal checkpoint: if the remote has confirmed
                        // receiving our SysEx packet (or any packet after it), clear the
                        // journal so we stop carrying stale data.
                        if (lastSysExPayload != null
                            && (short)(feedbackPkt.LastReceivedSequence - lastSysExSeqNum) >= 0)
                        {
                            lastSysExPayload = null;
                        }
                    }
                    else if (sessionPkt != null)
                    {
                        if (TraceHook != null)
                            TraceHook($"[{localName}] RX session {sessionPkt.Command} ({(isData ? "data" : "control")}) from {result.RemoteEndPoint} remote='{sessionPkt.Name}' ssrc={sessionPkt.Ssrc:X8}");

                        // Apple MIDI spec: when already connected, refuse new invitations on the
                        // control port with NO so the initiator gives up immediately rather than
                        // waiting for a timeout.
                        if (!isData
                            && sessionPkt.Command == AppleSessionCommand.Invitation
                            && State == SessionState.Connected)
                        {
                            var no = new SessionPacket(
                                AppleSessionCommand.InvitationRefused,
                                AppleSessionProtocol.ProtocolVersion,
                                sessionPkt.InitiatorToken,
                                localSsrc,
                                localName);
                            if (TraceHook != null)
                                TraceHook($"[{localName}] TX session InvitationRefused (NO) to {result.RemoteEndPoint} (already connected)");
                            await socket.SendAsync(AppleSessionProtocol.Encode(no), ct);
                        }
                        else
                        {
                            HandleSessionControlPacket(sessionPkt);
                        }
                    }
                }
                else if (isData && RtpMidiPacket.TryParse(buf, out var midiPkt) && midiPkt != null)
                {
                    // Discard RTP packets from unexpected SSRCs (RFC 3550 §8.2)
                    if (midiPkt.Ssrc != remoteSsrc)
                    {
                        if (TraceHook != null)
                            TraceHook($"[{localName}] RX discarding RTP from unexpected SSRC={midiPkt.Ssrc:X8} (expected {remoteSsrc:X8})");
                        continue;
                    }

                    // Check for sequence-number gap and attempt journal recovery
                    if (expectedSeqNum.HasValue && midiPkt.SequenceNumber != expectedSeqNum.Value)
                    {
                        // One or more packets were lost — consult the recovery journal
                        if (!midiPkt.JournalBytes.IsEmpty
                            && RtpMidiJournal.TryParseFullJournal(
                                midiPkt.JournalBytes.Span,
                                out var checkpointSeq,
                                out var recoveredMessages)
                            && recoveredMessages != null
                            && IsInGap(checkpointSeq, expectedSeqNum.Value, midiPkt.SequenceNumber))
                        {
                            foreach (var msg in recoveredMessages)
                                midiSubject.OnNext(msg);
                        }
                    }

                    expectedSeqNum = (ushort)(midiPkt.SequenceNumber + 1);

                    // Send RS (Receiver Feedback) every RsEveryNPackets packets so the remote
                    // peer can advance its recovery journal checkpoint without flooding
                    // the control socket on high-throughput streams.
                    rsPacketCount++;
                    if (controlSocket != null && rsPacketCount >= RsEveryNPackets)
                    {
                        rsPacketCount = 0;
                        var rs = new ReceiverFeedbackPacket(localSsrc, midiPkt.SequenceNumber);
                        var encoded = AppleSessionProtocol.EncodeReceiverFeedback(rs);
                        if (TraceHook != null)
                            TraceHook($"[{localName}] TX RS lastSeq={midiPkt.SequenceNumber}");
                        await controlSocket.SendAsync(encoded, ct);
                    }

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

    /// <summary>
    /// Returns true if <paramref name="checkpointSeq"/> falls within the gap
    /// [<paramref name="gapStart"/>, <paramref name="receivedSeq"/> - 1] using
    /// unsigned 16-bit wraparound arithmetic.
    /// </summary>
    private static bool IsInGap(ushort checkpointSeq, ushort gapStart, ushort receivedSeq)
        => (ushort)(checkpointSeq - gapStart) < (ushort)(receivedSeq - gapStart);

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

    /// <summary>
    /// Checks whether <see cref="localSsrc"/> collides with <see cref="remoteSsrc"/>.
    /// Per RFC 3550 §8.2, if a collision is detected a new SSRC is generated iteratively
    /// until it no longer matches the remote's SSRC.
    /// </summary>
    /// <returns><c>true</c> if a collision was detected (and a new SSRC has been assigned).</returns>
    private bool DetectAndResolveSsrcCollision()
    {
        if (localSsrc != remoteSsrc)
            return false;

        // Regenerate until unique (extremely unlikely to need more than one iteration).
        do
        {
            localSsrc = AppleSessionProtocol.GenerateSsrc();
        }
        while (localSsrc == remoteSsrc);

        if (TraceHook != null)
            TraceHook($"[{localName}] SSRC collision detected (remoteSsrc={remoteSsrc:X8}); new localSsrc={localSsrc:X8}");

        return true;
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
                initiatorToken,
                localSsrc,
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
