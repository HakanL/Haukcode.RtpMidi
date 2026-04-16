# Haukcode.RtpMidi.Mdns

mDNS/Bonjour peer discovery for [Haukcode.RtpMidi](https://www.nuget.org/packages/Haukcode.RtpMidi).

Browses the local network for `_apple-midi._udp` services and exposes found peers as `IObservable<RtpMidiPeer>` streams.

## Installation

```
dotnet add package Haukcode.RtpMidi.Mdns
```

## Usage

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

## Links

- [GitHub](https://github.com/HakanL/Haukcode.RtpMidi)
- [Haukcode.RtpMidi (core package)](https://www.nuget.org/packages/Haukcode.RtpMidi)
