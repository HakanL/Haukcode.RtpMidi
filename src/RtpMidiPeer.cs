namespace Haukcode.RtpMidi;

/// <summary>
/// A network MIDI peer discovered via mDNS (_apple-midi._udp).
/// <see cref="ControlEndPoint"/> is the control port (N); the data port is N+1.
/// </summary>
public record RtpMidiPeer(string Name, IPEndPoint ControlEndPoint);
