using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Zeroconf;

namespace Haukcode.RtpMidi.Mdns;

/// <summary>
/// Discovers RTP-MIDI peers on the local network via mDNS (_apple-midi._udp).
///
/// Compatible with macOS Network MIDI, Tobias Erichsen's rtpMIDI (Windows),
/// raveloxmidi (Linux), and hardware bridges such as the iConnectivity mioXM.
/// </summary>
public sealed class RtpMidiDiscovery : IDisposable
{
    private const string ServiceType = "_apple-midi._udp.local.";

    private readonly Subject<RtpMidiPeer> foundSubject = new();
    private readonly Subject<RtpMidiPeer> lostSubject = new();
    private readonly Dictionary<string, RtpMidiPeer> knownPeers = new();
    private readonly object knownPeersLock = new();

    private IDisposable? monitorSubscription;
    private bool disposed;

    /// <summary>Emits each peer the moment it is first discovered.</summary>
    public IObservable<RtpMidiPeer> PeersFound => foundSubject.AsObservable();

    /// <summary>
    /// Emits a peer when it disappears from subsequent scans.
    /// Note: mDNS departure detection is inherently poll-based when using
    /// ResolveContinuous; true goodbye packets require a lower-level listener.
    /// </summary>
    public IObservable<RtpMidiPeer> PeersLost => lostSubject.AsObservable();

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
        var hosts = await ZeroconfResolver.ResolveAsync(
            ServiceType,
            scanTime: scanTime ?? TimeSpan.FromSeconds(2),
            cancellationToken: ct);

        return hosts.SelectMany(ToPeers).ToList();
    }

    // -------------------------------------------------------------------------
    // Continuous monitoring
    // -------------------------------------------------------------------------

    /// <summary>
    /// Start continuous mDNS monitoring. <see cref="PeersFound"/> emits as new
    /// devices appear. Call <see cref="Dispose"/> to stop.
    /// </summary>
    public void StartMonitoring(TimeSpan? scanInterval = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        monitorSubscription = ZeroconfResolver
            .ResolveContinuous(ServiceType, scanTime: scanInterval ?? TimeSpan.FromSeconds(2))
            .Subscribe(
                onNext: OnHostSeen,
                onError: _ => { /* surface via error observable in a future release */ },
                onCompleted: () => { });
    }

    // -------------------------------------------------------------------------
    // Internal
    // -------------------------------------------------------------------------

    private void OnHostSeen(IZeroconfHost host)
    {
        foreach (var peer in ToPeers(host))
        {
            lock (knownPeersLock)
            {
                if (!knownPeers.ContainsKey(peer.Name))
                {
                    knownPeers[peer.Name] = peer;
                    foundSubject.OnNext(peer);
                }
            }
        }
    }

    private static IEnumerable<RtpMidiPeer> ToPeers(IZeroconfHost host)
    {
        if (!IPAddress.TryParse(host.IPAddress, out var addr))
            yield break;

        foreach (var svc in host.Services.Values)
        {
            var name = host.DisplayName ?? host.Id;
            yield return new RtpMidiPeer(name, new IPEndPoint(addr, svc.Port));
            break; // one peer per service entry
        }
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        monitorSubscription?.Dispose();

        foundSubject.OnCompleted();
        foundSubject.Dispose();
        lostSubject.OnCompleted();
        lostSubject.Dispose();
    }
}
