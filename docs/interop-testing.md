# Interoperability Testing

## Why interoperability tests matter

RTP-MIDI (RFC 6295) is a network protocol implemented by multiple independent software stacks and hardware devices. Correct operation requires that every implementation speaks exactly the same wire format and follows the same session-lifecycle rules as every other implementation.

The only reliable way to know your implementation is correct is to connect it to *other* implementations and verify that things actually work — not just that your own encoder and decoder agree with each other (which is easier to achieve but much weaker as a guarantee).

At the same time, requiring a physical hardware bridge or a second developer machine to run tests creates high friction:

- New contributors cannot run tests without specialist kit.
- CI pipelines cannot spin up a hardware MIDI interface.
- Test outcomes are hard to reproduce and slow to iterate on.

The solution used in this project is a **self-contained CLI interop tool** that can act as *both* the server (a reference RTP-MIDI peer) and the client (a structured test runner). One invocation of the tool becomes the peer; another invocation exercises it with a defined checklist. The pair runs entirely on localhost, in any CI runner, with no hardware and no manual steps.

## What the tests validate

Each run of the client checks the following properties in order:

| # | Check | What RFC/spec it covers |
|---|-------|------------------------|
| 1 | Session handshake (IN → OK, control + data ports) | Apple MIDI session protocol — invitation, acceptance |
| 2 | Clock sync (CK0 → CK1 → CK2) | Apple MIDI session protocol — mandatory 3-way exchange |
| 3 | MIDI round-trip (Note On / Note Off echoed back) | RFC 6295 §4 — RTP payload encoding/decoding |
| 4 | SysEx fragmentation and reassembly (>128 bytes) | RFC 6295 §4.3 — long SysEx spanning multiple packets |
| 5 | Recovery journal enabled (Chapter X present) | RFC 6295 §5 / §A — loss-recovery journal |
| 6 | Clean disconnection (BY on both ports) | Apple MIDI session protocol — graceful termination |

Together these checks exercise the full lifecycle of a session and the most important parts of the MIDI data path, including the edge cases (large SysEx, packet-loss recovery) that are easy to overlook and that hardware or DAWs are known to rely on.

## How the tool works

The interop tool lives in `tests/RtpMidi.InteropTest` and is a plain .NET console application. It has two modes.

### Server mode — reference peer

```
dotnet run --project tests/RtpMidi.InteropTest -- server [--port 5004] [--name InteropTest]
```

The server:

- Listens for incoming connections and accepts them (IN → OK handshake).
- Completes the clock sync exchange.
- **Echoes every received MIDI message back to the sender.** This is what makes MIDI round-trip and SysEx tests possible without any external hardware.
- Prints a human-readable decode of each packet to stdout.
- Advertises via mDNS (`_apple-midi._udp`) so the session appears automatically in macOS Audio MIDI Setup and Windows rtpMIDI.

### Client mode — structured test runner

```
dotnet run --project tests/RtpMidi.InteropTest -- client --host <ip> --port 5004 [--name InteropTest] [--loopback]
```

The client connects to any RTP-MIDI peer (the built-in server, or a third-party implementation) and runs the six checks listed above. Each check prints `PASS`, `FAIL`, or `SKIP` with a reason. The exit code is `0` if all non-skipped checks passed and `2` if any check failed, making it machine-readable for CI.

Pass `--loopback` when the peer is known to echo MIDI back (server mode always does this). Without `--loopback` the round-trip and SysEx checks are skipped rather than failing.

## Running the full self-test locally

Open two terminals in the repository root.

**Terminal 1 — start the reference server:**

```
dotnet run --project tests/RtpMidi.InteropTest -- server --port 5004
```

**Terminal 2 — run the client against it:**

```
dotnet run --project tests/RtpMidi.InteropTest -- client --host 127.0.0.1 --port 5004 --loopback
```

Expected output (all six checks pass):

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

A Wireshark display filter for the chosen ports is printed at startup so you can capture and inspect the exact bytes on the wire while the test runs.

## Testing against real third-party implementations

The interop tool is also designed to run against real software and hardware, not just against itself. Verifying against independent implementations is the strongest possible compliance signal.

### Known-good peers

| Peer | Platform | Notes |
|------|----------|-------|
| macOS CoreMIDI (Audio MIDI Setup) | macOS | Built into every Mac. Open **Audio MIDI Setup → Window → Show MIDI Studio**, double-click **Network**, add a session on port 5004, enable "My Sessions". |
| [rtpMIDI](https://www.tobias-erichsen.de/software/rtpmidi.html) | Windows | Free. Create a session on port 5004 and connect to the host running the interop tool. |
| [rtpmidid](https://github.com/davidmoreno/rtpmidid) | Linux | `rtpmidid --port 5004`. Note: does not implement the recovery journal, so check 5 behaviour differs. |

To test against macOS CoreMIDI, run the server on the machine you want to test from and let CoreMIDI connect to it, then run the client against the CoreMIDI session's IP:

```
# Machine under test — start the server
dotnet run --project tests/RtpMidi.InteropTest -- server --port 5004

# Add the machine in Audio MIDI Setup, then run the client in the other direction
dotnet run --project tests/RtpMidi.InteropTest -- client --host <mac-ip> --port 5004
```

## Why this approach is better than manual testing

| Concern | Manual / hardware testing | Automated interop tool |
|---------|--------------------------|------------------------|
| Requires hardware | Yes — physical bridge, controller, or second machine | No — runs entirely on localhost |
| Reproducible | No — depends on hardware state, firmware versions | Yes — same result every run |
| CI-friendly | No | Yes — exit code signals pass/fail |
| Checks documented | Implicitly, in a human runbook | Explicitly, in code and this document |
| Covers edge cases (SysEx >128 bytes, packet loss) | Easy to forget | Always included in the checklist |
| Detects regressions automatically | No — must run manually after each change | Yes — run in CI on every pull request |
| Can still test real hardware | N/A | Yes — point the client at any real peer |

The automated approach gives us a high, reproducible level of confidence that the library is compatible with both the RFC 6295 specification and the real-world implementations that users will connect to — without requiring anyone to own or configure physical hardware.
