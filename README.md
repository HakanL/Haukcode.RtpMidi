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
- Full RFC 6295 recovery journal — system chapters X (SysEx) and F (System Common), plus all channel chapters (Program Change, Control Change, Pitch Wheel, Note On/Off, Channel Pressure, Poly Key Pressure, RPN/NRPN Parameter System)
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

### Recovery Journal (RFC 6295 §4 / §A)

The library implements the full RFC 6295 recovery journal on both the send and receive sides.

**Covered chapters:**

| Scope | Chapter | Content |
|-------|---------|---------|
| System | X | System Exclusive (SysEx) |
| System | F | System Common messages |
| Channel | P | Program Change + Bank Select |
| Channel | C | Control Change (all 128 controllers) |
| Channel | W | Pitch Wheel |
| Channel | M | RPN/NRPN Parameter System |
| Channel | N | Note Off |
| Channel | Q | Note On |
| Channel | T | Channel Pressure |
| Channel | A | Poly Key Pressure |

On the send side, each outgoing packet carries a journal encoding the most-recent state for every active channel and any buffered SysEx, so a receiver who missed a packet can reconstruct the lost events from the next packet it receives.

On the receive side, sequence numbers are tracked; when a gap is detected the incoming packet's journal is consulted and any recovered events are emitted to `MidiReceived` subscribers before the current packet's MIDI data.

**Configuration:**

```csharp
// Enabled by default; disable only on strictly loss-free paths
session.EnableRecoveryJournal = false;
```

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

A dedicated CLI tool lives in `tests/RtpMidi.InteropTest`.
It can run in two modes and is useful for validating changes against real implementations.

### Mode 1 — Client (connect to a known peer and run checks)

```
dotnet run --project tests/RtpMidi.InteropTest -- client --host <ip> --port 5004 [--name InteropTest] [--loopback]
```

Runs the following checks in order and prints PASS / FAIL / SKIP for each.
Exit code 0 means all non-skipped checks passed.

| # | Check | Notes |
|---|-------|-------|
| 1 | Session handshake (IN → OK, control + data) | Always runs |
| 2 | Clock sync (CK0 → CK1 → CK2) | Always runs |
| 3 | MIDI round-trip (Note On / Note Off) | Requires `--loopback` |
| 4 | SysEx fragmentation / reassembly (>128 bytes) | Requires `--loopback` |
| 5 | Recovery journal enabled (Chapter X) | Always runs |
| 6 | Clean disconnection (BY) | Always runs |

Pass `--loopback` when the peer is configured to echo all received MIDI back (e.g. server mode below, or a DAW in MIDI-thru mode).

A Wireshark display filter for the chosen port is printed at startup:

```
Wireshark filter: udp.port == 5004 || udp.port == 5005
```

### Mode 2 — Server (act as a reference peer for other implementations)

```
dotnet run --project tests/RtpMidi.InteropTest -- server [--port 5004] [--name InteropTest]
```

- Accepts incoming connections (IN → OK)
- Completes clock sync
- **Echoes all received MIDI back** to the sender (loopback mode)
- Reports each received packet to stdout
- Advertises via mDNS (`_apple-midi._udp`) so the session appears automatically in macOS Audio MIDI Setup and rtpMIDI

### Setting up known-good peers

| Peer | Platform | Setup |
|------|----------|-------|
| macOS CoreMIDI | macOS | Open **Audio MIDI Setup** → **Window → Show MIDI Studio** → double-click **Network** → add a session and enable the "My Sessions" checkbox |
| [rtpMIDI](https://www.tobias-erichsen.de/software/rtpmidi.html) | Windows | Install, create a session on port 5004, connect to the host running the interop tool |
| [rtpmidid](https://github.com/davidmoreno/rtpmidid) | Linux | `rtpmidid --port 5004` — note: no recovery journal support |

### Example: full interop run against the built-in server

In one terminal start the server:

```
dotnet run --project tests/RtpMidi.InteropTest -- server --port 5004
```

In a second terminal run the client with loopback enabled:

```
dotnet run --project tests/RtpMidi.InteropTest -- client --host 127.0.0.1 --port 5004 --loopback
```

Expected output (all checks pass):

```
Wireshark filter: udp.port == 5004 || udp.port == 5005

Client mode → 127.0.0.1:5004  (name=InteropTest)

  [1/6] Session handshake (IN → OK)…       PASS  (connected to 'InteropTest')
  [2/6] Clock sync (CK0 → CK1 → CK2)…     PASS  (session still connected after 500 ms)
  [3/6] MIDI round-trip (Note On/Off)…     PASS  (Note On + Note Off echoed back)
  [4/6] SysEx fragmentation/reassembly…   PASS  (200-byte SysEx echoed back intact)
  [5/6] Recovery journal (Chapter X)…     PASS  (EnableRecoveryJournal=true …)
  [6/6] Clean disconnection (BY)…         PASS  (DisconnectAsync completed without error)

─────────────────────────────────────────────────────────
  PASS  handshake               connected to 'InteropTest'
  PASS  clock-sync              session still connected after 500 ms
  PASS  midi-roundtrip          Note On + Note Off echoed back
  PASS  sysex-frag              200-byte SysEx echoed back intact
  PASS  recovery-journal        EnableRecoveryJournal=true …
  PASS  disconnect              DisconnectAsync completed without error
─────────────────────────────────────────────────────────
  6 passed  /  0 failed  /  0 skipped

All checks passed. ✓
```
