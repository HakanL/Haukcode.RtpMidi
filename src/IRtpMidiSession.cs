using System.Reactive;

namespace Haukcode.RtpMidi;

public interface IRtpMidiSession : IAsyncDisposable
{
    /// <summary>
    /// Connect to a remote RTP-MIDI peer.
    /// <paramref name="controlEndPoint"/> is port N; the data port N+1 is derived automatically.
    /// Returns once the handshake is complete; call <see cref="DisconnectAsync"/> or dispose to close.
    /// </summary>
    Task ConnectAsync(IPEndPoint controlEndPoint, CancellationToken ct = default);

    /// <summary>
    /// Connect and automatically reconnect whenever the session drops.
    /// Loops until <paramref name="ct"/> is cancelled.
    /// Use <see cref="StateChanges"/> to observe connect/disconnect transitions.
    /// </summary>
    Task ConnectWithReconnectAsync(IPEndPoint controlEndPoint, TimeSpan reconnectDelay, CancellationToken ct = default);

    /// <summary>
    /// Listen for an incoming connection on <paramref name="controlPort"/> (N).
    /// Data port N+1 is opened automatically.
    /// Returns once the handshake is complete.
    /// </summary>
    Task ListenAsync(int controlPort, CancellationToken ct = default);

    /// <summary>
    /// Listen and automatically re-listen after each session ends.
    /// Loops until <paramref name="ct"/> is cancelled.
    /// </summary>
    Task ListenWithReconnectAsync(int controlPort, TimeSpan reconnectDelay, CancellationToken ct = default);

    /// <summary>Send a BY packet and close the session gracefully.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Send raw MIDI bytes to the remote peer.
    /// Use this for outbound MIDI, including LED-feedback SysEx to hardware controllers.
    /// </summary>
    Task SendMidiAsync(ReadOnlyMemory<byte> midiBytes, CancellationToken ct = default);

    /// <summary>
    /// Stream of raw MIDI byte payloads received from the remote peer.
    /// Subscribe to parse note-on, CC, program change, SysEx, etc.
    /// Completes when the session is disposed.
    /// </summary>
    IObservable<ReadOnlyMemory<byte>> MidiReceived { get; }

    /// <summary>
    /// Stream of session state transitions.
    /// Completes when the session is disposed.
    /// </summary>
    IObservable<SessionState> StateChanges { get; }

    SessionState State { get; }

    /// <summary>Name advertised by the remote peer, populated after connection.</summary>
    string? RemoteName { get; }
}
