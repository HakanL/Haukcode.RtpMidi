# Haukcode.RtpMidi.Mdns

mDNS/Bonjour peer discovery and advertising for [Haukcode.RtpMidi](https://www.nuget.org/packages/Haukcode.RtpMidi).

Browses the local network for `_apple-midi._udp` services and exposes peers as `IObservable<RtpMidiPeer>` streams. Advertises your own session so macOS, rtpMIDI, and hardware bridges can find you without manual IP entry.

## Installation

```
dotnet add package Haukcode.RtpMidi.Mdns
```

## Discover peers

### One-shot scan

```csharp
var peers = await RtpMidiDiscovery.ResolveAsync();
foreach (var peer in peers)
    Console.WriteLine($"{peer.Name} @ {peer.ControlEndPoint}");
```

### Continuous monitoring

```csharp
using var discovery = new RtpMidiDiscovery();

discovery.PeersFound.Subscribe(peer =>
    Console.WriteLine($"Found: {peer.Name} @ {peer.ControlEndPoint}"));

discovery.PeersLost.Subscribe(peer =>
    Console.WriteLine($"Lost: {peer.Name}"));

discovery.StartMonitoring();
```

### Connect to a discovered peer

```csharp
var peers = await RtpMidiDiscovery.ResolveAsync();
var peer  = peers.First();

await using var session = new RtpMidiSession("My App");
session.MidiReceived.Subscribe(midi => /* handle */ );
await session.ConnectAsync(peer.ControlEndPoint);
```

## Advertise your session

Make your app visible in macOS Audio MIDI Setup, Tobias Erichsen's rtpMIDI, and hardware bridges (e.g. iConnectivity mioXM):

```csharp
using var advertiser = new RtpMidiAdvertiser("My App", controlPort: 5004);
advertiser.Start();

// Your session is now discoverable on the local network.
// Dispose to send goodbye packets and remove the advertisement.
```

## Links

- [GitHub](https://github.com/HakanL/Haukcode.RtpMidi)
- [Haukcode.RtpMidi (core package)](https://www.nuget.org/packages/Haukcode.RtpMidi)
- [Haukcode.Mdns (mDNS library)](https://www.nuget.org/packages/Haukcode.Mdns)
