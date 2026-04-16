using System.Net;
using Haukcode.Mdns;

namespace Haukcode.RtpMidi.Mdns;

/// <summary>
/// Advertises an RTP-MIDI session on the local network via mDNS (_apple-midi._udp)
/// so that other devices (macOS Network MIDI, rtpMIDI, mioXM, etc.) can discover it
/// without requiring manual IP entry.
///
/// Usage:
///   1. Create an <see cref="RtpMidiAdvertiser"/> with your session name and control port.
///   2. Call <see cref="Start"/> — the device becomes visible on the network immediately.
///   3. Call <see cref="Dispose"/> to send goodbye packets and remove the advertisement.
/// </summary>
public sealed class RtpMidiAdvertiser : IDisposable
{
    private readonly MdnsAdvertiser advertiser;
    private bool disposed;

    /// <param name="sessionName">
    /// Human-readable name for the session, e.g. "DMX Core 100".
    /// This is what appears in macOS Audio MIDI Setup and other clients.
    /// </param>
    /// <param name="controlPort">
    /// The RTP-MIDI control port (N). The data port is implicitly N+1.
    /// The standard Apple MIDI port is 5004.
    /// </param>
    /// <param name="localAddress">
    /// Local IPv4 address to include in the A record.
    /// If null, the best available address is selected automatically
    /// (prefers wired Ethernet over Wi-Fi).
    /// </param>
    /// <param name="properties">
    /// Optional TXT record properties. Apple MIDI does not require any specific
    /// TXT properties, but you may include informational entries.
    /// </param>
    public RtpMidiAdvertiser(
        string sessionName,
        ushort controlPort = 5004,
        IPAddress? localAddress = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        var profile = new ServiceProfile(
            sessionName,
            "_apple-midi._udp",
            controlPort,
            properties);

        advertiser = new MdnsAdvertiser(profile, localAddress);
    }

    /// <summary>Start advertising the session on the local network.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        advertiser.Start();
    }

    /// <summary>
    /// Stop advertising — sends mDNS goodbye packets so other devices
    /// remove this session from their lists promptly.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        advertiser.Dispose();
    }
}
