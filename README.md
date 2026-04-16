# Haukcode.RtpMidi

RTP-MIDI (RFC 6295) implementation in modern C# with full Apple MIDI session protocol support.

Enables bidirectional MIDI over IP — receive notes, CC, program changes, and send LED feedback to hardware controllers via any standard network MIDI bridge.

[![NuGet](https://img.shields.io/nuget/v/Haukcode.RtpMidi.svg)](https://www.nuget.org/packages/Haukcode.RtpMidi)
[![Build](https://github.com/HakanL/Haukcode.RtpMidi/actions/workflows/main.yml/badge.svg)](https://github.com/HakanL/Haukcode.RtpMidi/actions)

---

## Features

- Full Apple MIDI session protocol (IN / OK / NO / BY / CK)
- Both **initiator** and **responder** roles
- Clock sync (3-way CK exchange) — required by hardware bridges
- RTP-MIDI packet encoding/decoding (RFC 6295)
- `IObservable<T>` streams via **System.Reactive** for received MIDI and state changes
- Cross-platform: Windows, Linux (including ARM64), macOS
- Zero platform-specific code — pure managed C#
- Optional mDNS discovery via the companion **Haukcode.RtpMidi.Mdns** package

## Compatible bridges

| Bridge | Platform | Notes |
|--------|----------|-------|
| macOS Network MIDI (Audio MIDI Setup) | macOS | Built-in |
| [rtpMIDI](https://www.tobias-erichsen.de/software/rtpmidi.html) | Windows | Free |
| [raveloxmidi](https://github.com/ravelox/pimidi) | Linux | Headless/RPi |
| iConnectivity mioXM | Hardware | Multi-port, PoE |

---

## Installation

```
dotnet add package Haukcode.RtpMidi
```

For mDNS peer discovery:

```
dotnet add package Haukcode.RtpMidi.Mdns
```

---

## Quick Start

### Connect to a known peer (static IP)

```csharp
await using var session = new RtpMidiSession("My App");

// Subscribe before connecting
session.MidiReceived.Subscribe(midiBytes =>
{
    // midiBytes is the raw MIDI payload — parse as needed
    Console.WriteLine($"MIDI: {BitConverter.ToString(midiBytes.ToArray())}");
});

session.StateChanges.Subscribe(state =>
    Console.WriteLine($"State: {state}"));

// Control port is N; data port N+1 is derived automatically
await session.ConnectAsync(new IPEndPoint(IPAddress.Parse("192.168.1.50"), 5004));

// Send LED feedback SysEx to hardware (e.g. Akai LPD8 mk2 pad color)
byte[] sysex = [0xF0, 0x47, 0x7F, 0x30, 0x2C, 0x01, 0x00, 0xF7];
await session.SendMidiAsync(sysex);
```

### Listen for incoming connections

```csharp
await using var session = new RtpMidiSession("My App");

session.MidiReceived.Subscribe(HandleMidi);

// Listen on control port 5004; data port 5005 opened automatically
await session.ListenAsync(controlPort: 5004);
```

### Discover peers via mDNS (requires Haukcode.RtpMidi.Mdns)

```csharp
// One-shot scan
var peers = await RtpMidiDiscovery.ResolveAsync();
foreach (var peer in peers)
    Console.WriteLine($"{peer.Name} @ {peer.ControlEndPoint}");

// Continuous monitoring
using var discovery = new RtpMidiDiscovery();
discovery.PeersFound.Subscribe(peer => Console.WriteLine($"Found: {peer.Name}"));
discovery.PeersLost.Subscribe(peer  => Console.WriteLine($"Lost:  {peer.Name}"));
discovery.StartMonitoring();
```

---

## Architecture

```
Haukcode.RtpMidi          (core — System.Reactive only)
├── RtpMidiSession        Main session class (initiator + responder)
├── IRtpMidiSession       Public interface
├── AppleSessionProtocol  IN/OK/NO/BY/CK packet codec
├── RtpMidiPacket         RFC 6295 RTP header + MIDI command section codec
├── ClockSync             3-way CK exchange, latency/offset estimation
└── SessionState          Idle / ConnectingControl / ConnectingData / Connected / Disconnecting

Haukcode.RtpMidi.Mdns     (optional — adds Zeroconf)
└── RtpMidiDiscovery      _apple-midi._udp mDNS browse (one-shot + continuous)
```

---

## Protocol notes

RTP-MIDI uses two UDP ports per session:

| Port | Purpose |
|------|---------|
| N (control) | Apple MIDI session handshake (IN/OK/BY) and clock sync (CK) |
| N+1 (data)  | RTP packets carrying MIDI payload |

The clock sync exchange (CK0 → CK1 → CK2) is **mandatory** — hardware bridges will not confirm the connection until it completes. This library implements the full 3-way exchange and repeats it every ~10 seconds to maintain the session.

### Journal (RFC 6295 §4)

Journal-based packet loss recovery is not implemented in v1.0. This is safe for LAN use where UDP loss is negligible. Journal support is planned for a future release.

---

## Platform support

| Platform | Tested |
|----------|--------|
| Windows 10/11 | Yes |
| Linux x64 | Yes |
| Linux ARM64 (Raspberry Pi) | Yes |
| macOS | Yes |

---

## Contributing

Bug reports and pull requests welcome at https://github.com/HakanL/Haukcode.RtpMidi.

When contributing protocol-level changes, please reference the relevant section of [RFC 6295](https://datatracker.ietf.org/doc/html/rfc6295) or the Apple MIDI session protocol documentation.
