# Interoperability Testing

## Why interoperability tests matter

RTP-MIDI (RFC 6295) is a network protocol implemented by multiple independent software stacks and hardware devices. Correct operation requires that every implementation speaks exactly the same wire format and follows the same session-lifecycle rules as every other implementation.

The only reliable way to know your implementation is correct is to connect it to *other* implementations and verify that things actually work — not just that your own encoder and decoder agree with each other (which is easier to achieve but much weaker as a guarantee).

At the same time, requiring a physical hardware bridge or a second developer machine to run tests creates high friction:

- New contributors cannot run tests without specialist kit.
- CI pipelines cannot spin up a hardware MIDI interface.
- Test outcomes are hard to reproduce and slow to iterate on.

The solution used in this project is a **self-contained CLI interop tool** that can act as *both* the server (a reference RTP-MIDI peer) and the client (a structured test runner). One invocation of the tool becomes the peer; another invocation exercises it with a defined checklist. The pair runs entirely on localhost, in any CI runner, with no hardware and no manual steps.

## CI integration

The interop tests run automatically in GitHub Actions on every pull request and push to `main`. The CI pipeline is split into two workflows.

### `main.yml` — mandatory per-PR checks

The `interop-self` job runs the full 14-check loopback suite on all three platforms. NuGet publishing is blocked if any check fails.

| Job | Runner | Trigger | Purpose |
|-----|--------|---------|---------|
| `Interop (self) on ubuntu-latest` | `ubuntu-latest` | every PR / push | 14 loopback checks on Linux |
| `Interop (self) on macos-latest` | `macos-latest` | every PR / push | 14 loopback checks on macOS |
| `Interop (self) on windows-latest` | `windows-latest` | every PR / push | 14 loopback checks on Windows |

### `interop-extended.yml` — nightly and manual

Additional jobs that are too slow or environment-specific for per-PR runs are in `interop-extended.yml`. It is triggered automatically on a nightly schedule (02:00 UTC) and can also be dispatched manually via the GitHub Actions UI.

| Job | Runner | Purpose |
|-----|--------|---------|
| `Extended self-interop on <os>` | ubuntu / macos / windows | Repeat of the 14-check suite with a longer timeout |
| `Extended interop (rtpmidid, Ubuntu)` | `ubuntu-22.04` | Our library as RTP-MIDI client, [rtpmidid](https://github.com/davidmoreno/rtpmidid) as the server peer |
| `Extended interop against <peer>` | `ubuntu-latest` | Connect to an external peer supplied via `workflow_dispatch` inputs |

> **Note on rtpmidid**: The rtpmidid job requires ALSA kernel modules (`snd-seq` etc.) which are not available on GitHub-hosted Azure cloud runners. It is kept in the extended workflow for use on self-hosted runners with real ALSA support, or in local testing.

## What the tests validate

Each `--loopback` run of the client checks all 14 properties below in order. Checks that require the peer to echo MIDI back are marked **loopback**; they are skipped (not failed) when `--loopback` is absent.

| # | Check | Mode | What RFC/spec it covers |
|---|-------|------|------------------------|
| 1 | Session handshake (IN → OK, control + data ports) | always | Apple MIDI session protocol — invitation, acceptance |
| 2 | Clock sync (CK0 → CK1 → CK2) | always | Apple MIDI session protocol — mandatory 3-way exchange |
| 3 | MIDI round-trip (Note On / Note Off echoed back) | loopback | RFC 6295 §4 — RTP payload encoding/decoding |
| 4 | SysEx fragmentation and reassembly (>128 bytes) | loopback | RFC 6295 §4.3 — long SysEx spanning multiple packets |
| 5 | Recovery journal enabled (Chapter X present) | always | RFC 6295 §5 / §A — loss-recovery journal |
| 6 | Control Change (CC #7 Volume, CC #10 Pan) | loopback | RFC 6295 §4 — channel voice messages |
| 7 | Program Change | loopback | RFC 6295 §4 — channel voice messages |
| 8 | Pitch Wheel | loopback | RFC 6295 §4 — channel voice messages |
| 9 | Polyphony + Note Off (velocity=0 variant) | loopback | RFC 6295 §4 — running status, Note Off encoding |
| 10 | Channel Aftertouch | loopback | RFC 6295 §4 — channel voice messages |
| 11 | Poly Key Pressure (Note Aftertouch) | loopback | RFC 6295 §4 — channel voice messages |
| 12 | MTC Quarter Frame (System Common) | loopback | RFC 6295 §4 — system common messages |
| 13 | Sequence-number gap + recovery journal | loopback | RFC 6295 §5 — packet loss recovery via journal |
| 14 | Clean disconnection (BY on both ports) | always | Apple MIDI session protocol — graceful termination |

Together these checks exercise the full session lifecycle, every major MIDI message category, and the two most important edge cases — large SysEx reassembly and packet-loss journal recovery — that hardware devices and DAWs are known to rely on.

## How the tool works

The interop tool lives in `tests/RtpMidi.InteropTest` and is a plain .NET console application. It has two modes.

### Server mode — reference peer

```
dotnet run --project tests/RtpMidi.InteropTest -- server [--port 5004] [--name InteropTest]
```

The server:

- Listens for incoming connections and accepts them (IN → OK handshake).
- Completes the clock sync exchange.
- **Echoes every received MIDI message back to the sender.** This is what enables the loopback checks (MIDI round-trip, SysEx, CC, PC, etc.) without any external hardware.
- Prints a human-readable decode of each packet to stdout.
- Advertises via mDNS (`_apple-midi._udp`) so the session appears automatically in macOS Audio MIDI Setup and Windows rtpMIDI.

### Client mode — structured test runner

```
dotnet run --project tests/RtpMidi.InteropTest -- client --host <ip> --port 5004 [--name InteropTest] [--loopback] [--alsa-verify]
```

The client connects to any RTP-MIDI peer (the built-in server, or a third-party implementation) and runs the checks listed above. Each check prints `PASS`, `FAIL`, or `SKIP` with a reason. The exit code is `0` if all non-skipped checks passed and `2` if any check failed, making it machine-readable for CI.

**`--loopback`** — pass when the peer echoes MIDI back to the sender (the built-in server always does this). Without `--loopback`, the 11 echo-dependent checks are skipped rather than failed. CI always passes `--loopback` when testing against our own server.

**`--alsa-verify`** — Linux only; for use with rtpmidid on a machine that has real ALSA kernel support (self-hosted runner or local dev box). Adds a 15th check that verifies a sent MIDI note arrives on rtpmidid's ALSA sequencer port as detected by DryWetMidi. This flag is **not** used in GitHub-hosted CI because Azure cloud kernels do not include ALSA sound modules.

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

Expected output (all 14 checks pass):

```
Wireshark filter: udp.port == 5004 || udp.port == 5005

Client mode → 127.0.0.1:5004  (name=InteropTest, loopback=True, alsa-verify=False)

  [1/14] Session handshake (IN → OK)…                PASS  (connected to 'InteropTest')
  [2/14] Clock sync (CK0 → CK1 → CK2)…              PASS  (session still Connected after 500 ms)
  [3/14] MIDI round-trip (Note On/Off)…              PASS  (Note On + Note Off echoed back)
  [4/14] SysEx fragmentation/reassembly…             PASS  (200-byte SysEx echoed back intact)
  [5/14] Recovery journal (Chapter X)…               PASS  (EnableRecoveryJournal=true)
  [6/14] Control Change (CC #7 Vol, #10 Pan)…        PASS  (CC #7 and #10 echoed back)
  [7/14] Program Change…                             PASS  (Program Change #25 echoed back)
  [8/14] Pitch Wheel…                                PASS  (Pitch Bend value=10000 echoed back)
  [9/14] Polyphony + Note Off (vel=0)…               PASS  (C4+E4 poly on, C4 released via vel=0 — all echoed back)
  [10/14] Channel Aftertouch…                        PASS  (Channel Aftertouch pressure=64 echoed back)
  [11/14] Poly Key Pressure…                         PASS  (Poly Key Pressure note=60 pres=40 echoed back)
  [12/14] MTC Quarter Frame (System Common)…         PASS  (MTC Quarter Frame (F1 05) echoed back)
  [13/14] Seq-number gap + journal recovery…         PASS  (A4 recovered from journal across 1-packet gap; B4 echoed directly)
  [14/14] Clean disconnection (BY)…                  PASS  (DisconnectAsync completed without error)

─────────────────────────────────────────────────────────
  PASS  handshake                 connected to 'InteropTest'
  PASS  clock-sync                session still Connected after 500 ms
  PASS  midi-roundtrip            Note On + Note Off echoed back
  PASS  sysex-frag                200-byte SysEx echoed back intact
  PASS  recovery-journal          EnableRecoveryJournal=true
  PASS  cc                        CC #7 and #10 echoed back
  PASS  program-change            Program Change #25 echoed back
  PASS  pitch-wheel               Pitch Bend value=10000 echoed back
  PASS  polyphony                 C4+E4 poly on, C4 released via vel=0 — all echoed back
  PASS  channel-at                Channel Aftertouch pressure=64 echoed back
  PASS  poly-kp                   Poly Key Pressure note=60 pres=40 echoed back
  PASS  mtc-qf                    MTC Quarter Frame (F1 05) echoed back
  PASS  seq-gap-recovery          A4 recovered from journal across 1-packet gap; B4 echoed directly
  PASS  disconnect                DisconnectAsync completed without error
─────────────────────────────────────────────────────────
  14 passed  /  0 failed  /  0 skipped

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

To run the extended rtpmidid test on a Linux machine with ALSA support:

```
# Start rtpmidid (built from source or installed)
rtpmidid --port 5004 &

# Run the client against it — add --alsa-verify to also check the ALSA port
dotnet run --project tests/RtpMidi.InteropTest -- client --host 127.0.0.1 --port 5004 --alsa-verify
```

The `interop-extended.yml` workflow can also be triggered manually from the GitHub Actions UI and pointed at any reachable external peer via the `peer_host`, `peer_port`, and `peer_label` inputs.

## Why this approach is better than manual testing

| Concern | Manual / hardware testing | Automated interop tool |
|---------|--------------------------|------------------------|
| Requires hardware | Yes — physical bridge, controller, or second machine | No — runs entirely on localhost |
| Reproducible | No — depends on hardware state, firmware versions | Yes — same result every run |
| CI-friendly | No | Yes — exit code signals pass/fail; NuGet publish blocked on failure |
| Checks documented | Implicitly, in a human runbook | Explicitly, in code and this document |
| Covers edge cases (SysEx >128 bytes, packet loss) | Easy to forget | Always included in the checklist |
| Detects regressions automatically | No — must run manually after each change | Yes — 14 checks run on every pull request across Ubuntu, macOS, and Windows |
| Can still test real hardware | N/A | Yes — point the client at any real peer |

The automated approach gives us a high, reproducible level of confidence that the library is compatible with both the RFC 6295 specification and the real-world implementations that users will connect to — without requiring anyone to own or configure physical hardware.
