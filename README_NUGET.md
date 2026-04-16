# Haukcode.RtpMidi

RTP-MIDI (RFC 6295) for .NET — bidirectional MIDI over IP with full Apple MIDI session protocol support.

## Key Features

- Apple MIDI session protocol (IN/OK/NO/BY/CK) — both initiator and responder roles
- Mandatory clock sync (CK0→CK1→CK2) — compatible with hardware bridges
- `IObservable<T>` streams for received MIDI and state changes (System.Reactive)
- Cross-platform: Windows, Linux ARM64, macOS — pure managed C#

## Installation

```
dotnet add package Haukcode.RtpMidi
```

For mDNS peer discovery:

```
dotnet add package Haukcode.RtpMidi.Mdns
```

## Quick Start

```csharp
await using var session = new RtpMidiSession("My App");

session.MidiReceived.Subscribe(midiBytes =>
    Console.WriteLine($"MIDI: {BitConverter.ToString(midiBytes.ToArray())}"));

// Control port N; data port N+1 derived automatically
await session.ConnectAsync(new IPEndPoint(IPAddress.Parse("192.168.1.50"), 5004));

// Send LED feedback to hardware controller
await session.SendMidiAsync(new byte[] { 0xF0, 0x47, 0x7F, 0x30, 0x2C, 0x01, 0x00, 0xF7 });
```

## Compatible Bridges

- **macOS** — Built-in Network MIDI (Audio MIDI Setup)
- **Windows** — [rtpMIDI](https://www.tobias-erichsen.de/software/rtpmidi.html) (free)
- **Linux** — raveloxmidi
- **Hardware** — iConnectivity mioXM and similar

## Links

- [GitHub](https://github.com/HakanL/Haukcode.RtpMidi)
- [RFC 6295](https://datatracker.ietf.org/doc/html/rfc6295)
