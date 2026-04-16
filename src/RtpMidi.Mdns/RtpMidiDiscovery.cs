using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Haukcode.Mdns;

namespace Haukcode.RtpMidi.Mdns;

/// <summary>
/// Discovers RTP-MIDI peers on the local network via mDNS (_apple-midi._udp).
///
/// Compatible with macOS Network MIDI, Tobias Erichsen's rtpMIDI (Windows),
/// raveloxmidi (Linux), and hardware bridges such as the iConnectivity mioXM.
/// </summary>
public sealed class RtpMidiDiscovery : IDisposable
{
    private const string ServiceType = "_apple-midi._udp";

    private readonly Subject<RtpMidiPeer> foundSubject = new();
    private readonly Subject<RtpMidiPeer> lostSubject = new();
    private readonly MdnsBrowser browser;
    private bool disposed;

    /// <summary>Emits each peer the moment it is first discovered.</summary>
    public IObservable<RtpMidiPeer> PeersFound => foundSubject.AsObservable();

    /// <summary>Emits a peer when its PTR TTL expires and it is not refreshed.</summary>
    public IObservable<RtpMidiPeer> PeersLost => lostSubject.AsObservable();

    public RtpMidiDiscovery()
    {
        browser = new MdnsBrowser(ServiceType);
        browser.ServiceFound += OnServiceFound;
        browser.ServiceLost  += OnServiceLost;
    }

    // -------------------------------------------------------------------------
    // One-shot resolve
    // -------------------------------------------------------------------------

    /// <summary>
    /// Perform a one-shot mDNS scan and return all currently-advertising peers.
    /// </summary>
    public static async Task<IReadOnlyList<RtpMidiPeer>> ResolveAsync(
        TimeSpan? scanTime = null,
        CancellationToken ct = default)
    {
        using var browser = new MdnsBrowser(ServiceType);
        var found = new List<RtpMidiPeer>();

        browser.ServiceFound += svc =>
        {
            lock (found)
                found.Add(ToPeer(svc));
        };

        browser.Start();
        await Task.Delay(scanTime ?? TimeSpan.FromSeconds(2), ct);

        return found.ToList();
    }

    // -------------------------------------------------------------------------
    // Continuous monitoring
    // -------------------------------------------------------------------------

    /// <summary>
    /// Start continuous mDNS monitoring. <see cref="PeersFound"/> emits as new
    /// devices appear; <see cref="PeersLost"/> emits when their TTL expires.
    /// Call <see cref="Dispose"/> to stop.
    /// </summary>
    public void StartMonitoring()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        browser.Start();
    }

    // -------------------------------------------------------------------------
    // Internal
    // -------------------------------------------------------------------------

    private void OnServiceFound(ServiceProfile profile)
        => foundSubject.OnNext(ToPeer(profile));

    private void OnServiceLost(ServiceProfile profile)
        => lostSubject.OnNext(ToPeer(profile));

    private static RtpMidiPeer ToPeer(ServiceProfile profile)
    {
        var address = profile.Address ?? IPAddress.Loopback;
        return new RtpMidiPeer(profile.InstanceName, new IPEndPoint(address, profile.Port));
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        browser.ServiceFound -= OnServiceFound;
        browser.ServiceLost  -= OnServiceLost;
        browser.Dispose();

        foundSubject.OnCompleted();
        foundSubject.Dispose();
        lostSubject.OnCompleted();
        lostSubject.Dispose();
    }
}
