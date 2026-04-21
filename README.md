# Haukcode.RtpMidi

RTP-MIDI (RFC 6295) implementation in modern C# with full Apple MIDI session protocol support.

Enables bidirectional MIDI over IP — receive notes, CC, program changes, and send LED feedback to hardware controllers via any standard network MIDI bridge.

[![NuGet](https://img.shields.io/nuget/v/Haukcode.RtpMidi.svg)](https://www.nuget.org/packages/Haukcode.RtpMidi)
[![Build](https://github.com/HakanL/Haukcode.RtpMidi/actions/workflows/main.yml/badge.svg)](https://github.com/HakanL/Haukcode.RtpMidi/actions)

---

## Features

- Full Apple MIDI session protocol (IN / OK / NO / BY / CK)
- Both **initiator** and **responder** roles, with optional auto-reconnect
- Clock sync (3-way CK exchange) — required by hardware bridges
- RTP-MIDI packet encoding/decoding (RFC 6295)
- RFC 6295 recovery journal — system chapters X (SysEx) and F (System Common), plus channel chapters P/C/W/M/N/T/A (Program Change, Control Change, Pitch Wheel, RPN/NRPN, Note On/Off, Channel Pressure, Poly Key Pressure)
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

### Auto-reconnect

Both roles have reconnecting variants that loop until cancellation:

```csharp
using var cts = new CancellationTokenSource();

// Reconnects every 5 s if the session drops
await session.ConnectWithReconnectAsync(endpoint, TimeSpan.FromSeconds(5), cts.Token);

// Re-listens after each session ends
await session.ListenWithReconnectAsync(controlPort: 5004, TimeSpan.FromMilliseconds(500), cts.Token);
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

### Recovery Journal (RFC 6295 §5 / §A)

The library implements the RFC 6295 recovery journal on both the send and receive sides.

**Implemented chapters:**

| Scope | Chapter | Content | Notes |
|-------|---------|---------|-------|
| System | X | System Exclusive (SysEx) | Last complete SysEx retained until RS confirms receipt |
| System | F | System Common (MTC, Song Position, Song Select) | |
| Channel | P | Program Change + Bank Select (CC 0 / CC 32) | |
| Channel | C | Control Change (all 128 controllers) | |
| Channel | W | Pitch Wheel | |
| Channel | M | RPN/NRPN Parameter System | Full log list (most-recently-selected first, §A.4) |
| Channel | N | Note On + Note Off (unified, §A.6) | Note log list + OFFBITS bitfield |
| Channel | T | Channel Pressure | |
| Channel | A | Poly Key Pressure | |

**Out of scope / not implemented:**

| Chapter | Reason |
|---------|--------|
| E (Tone Map) | Encodes the duration between Note On and its matching Note Off, which is unknown at Note On send time. Implementing Chapter E would require buffering all outgoing Note Ons and only sending the journal after the corresponding Note Off arrives — incompatible with a real-time streaming API. Inbound Chapter E journals are detected and skipped safely. |
| Q (Note Off) | RFC 6295 defines only Chapter N (unified Note On/Off). Chapter Q was an informal pre-standard extension; it is not part of the specification and is not emitted or expected. |

On the send side, each outgoing packet carries a journal encoding the most-recent state for every active channel and any buffered SysEx. The journal checkpoint advances when the remote peer sends an RS (Receiver Feedback) packet confirming receipt, at which point accumulated state is cleared so the journal does not grow indefinitely.

On the receive side, sequence numbers are tracked; when a gap is detected the incoming packet's journal is consulted and any recovered events are emitted to `MidiReceived` subscribers before the current packet's MIDI data.

**Configuration:**

```csharp
// Enabled by default; disable only on strictly loss-free paths
session.EnableRecoveryJournal = false;
```

### MIDI command section (RFC 6295 §3)

| Feature | Status |
|---------|--------|
| Short header (4-bit length) | Implemented |
| Long header (12-bit length) | Implemented |
| Z flag: VLQ delta-times | Implemented (encode + decode) |
| J flag: recovery journal present | Implemented |
| P flag (phantom / cross-packet running status) — **receive** | Implemented — incoming P-flagged packets from peers (Apple CoreMIDI, hardware bridges) are correctly expanded using the running status from the previous packet |
| P flag (phantom status) — **send** | Not implemented — the library always includes the full status byte on every packet, which is correct and universally compatible. The send-side omission saves at most 1 byte per packet when consecutive messages share a status byte, which is negligible for typical use. |
| Within-packet running status — **receive** | Implemented via `DecodeCommands` |
| Within-packet running status — **send** | Not implemented — each `SendMidiAsync` call produces one MIDI message per packet, so within-packet RS compression has no opportunity to apply. |
| SysEx fragmentation (§3.3) | Implemented — large SysEx is split into ≤128-byte segments with F0/F7 continuation markers; fragments are reassembled on the receive side. |

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

---

## Interoperability testing

A dedicated CLI tool in `tests/RtpMidi.InteropTest` validates the library against real RTP-MIDI implementations without needing any physical hardware. It runs in two modes:

- **Server** — acts as a reference peer that accepts connections and echoes received MIDI back.
- **Client** — connects to any RTP-MIDI peer and runs a structured six-check compliance suite (handshake, clock sync, MIDI round-trip, SysEx fragmentation, recovery journal, clean disconnection).

The pair can run entirely on localhost, making it suitable for CI and for local development. The same client can also be pointed at real third-party implementations (macOS CoreMIDI, rtpMIDI, rtpmidid) to verify real-world compatibility.

See **[docs/interop-testing.md](docs/interop-testing.md)** for the full rationale, check descriptions, step-by-step instructions, and a comparison with manual/hardware testing approaches.
