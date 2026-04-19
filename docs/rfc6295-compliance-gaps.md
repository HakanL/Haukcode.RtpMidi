# Fix remaining RFC 6295 / Apple MIDI compliance gaps

## Background

A compliance audit against RFC 6295 and the Apple MIDI Session Protocol identified several gaps beyond Chapter M (tracked separately). This issue covers the remaining items in priority order.

---

## 1. BY packet carries SSRC=0 instead of `localSsrc` (Apple MIDI spec)

`SendByAsync` builds the EndSession packet with both token and SSRC set to 0:

```csharp
new SessionPacket(AppleSessionCommand.EndSession, ProtocolVersion, 0, 0, null)
```

The Apple MIDI spec requires the BY packet to carry the sender's SSRC. Most implementations are tolerant in practice, but a strict receiver can ignore a zeroed BY and leave the session open until timeout.

**Fix:** pass `localSsrc` (and optionally `initiatorToken`) to the BY packet.

---

## 2. RTP header extension bit (X) and CSRC count (CC) not handled (RFC 6295 §4.1)

The parser validates `V=2` but does not skip:
- **CC > 0**: When `CC` is non-zero, `4*CC` bytes of CSRC data appear at offset 12, pushing the MIDI command section start forward. The parser always reads from offset 12, producing garbage.
- **X = 1**: When the extension bit is set, a 4-byte extension header appears *after* the CSRC list and before the payload. The parser would interpret it as MIDI data.

Both are rare in Apple MIDI sessions, but a conforming receiver must handle them gracefully (skip / discard rather than corrupt).

**Fix:** In `RtpMidiPacket.TryParse`, read `CC` from bits 3–0 of byte 0, advance past CSRC entries, and handle the X flag.

---

## 3. Channel journal state not reset between sessions

`ConnectAsync` and `ListenAsync` reset `lastSysExPayload` and `expectedSeqNum` but not the 16 `ChannelMidiState` objects or `systemMidiState`. A new peer would immediately receive recovery messages for events it never missed, potentially producing unexpected notes/program changes.

**Fix:** Reset all `channelStates[i]` and `systemMidiState` in the session-setup path.

---

## 4. Chapter N B=1 (extended bitfield mode) aborts all remaining channel chapters (RFC 6295 §A.5)

`DecodeChapterN` returns `-1` when the `B` flag is set, and the caller's `break` discards all subsequent chapters for that channel. The RFC defines a fixed-size bitfield alternative that must be skipped over even if not interpreted, so subsequent chapters can still be parsed.

**Fix:** When `B=1`, calculate the fixed bitfield size (16 bytes per §A.5), advance the position by that amount, and return the consumed byte count rather than -1.

---

## 5. SSRC collision detected but throws instead of retrying (RFC 3550 §8.2)

When an SSRC collision is detected at the end of the handshake, the implementation generates a new local SSRC but then throws `InvalidOperationException`, leaving the session in a partially-connected state. The caller must catch and manually retry `ConnectAsync`.

Per RFC 3550 §8.2, the implementation should transparently assign a new SSRC and retry the connection internally.

**Fix:** After collision detection, restart the handshake internally (up to a small retry limit) before surfacing an error.

---

## 6. RS (Receiver Feedback) sent for every packet with no rate limiting

An RS packet is sent for every received RTP-MIDI packet. Under high-throughput scenarios this creates a burst of RS traffic equal to the inbound packet rate. Reference implementations (rtpmidid, Apple CoreMIDI) typically throttle RS to one per ~10 packets or on a timer.

**Fix:** Add a simple per-N-packet or per-interval RS send gate. This is a quality-of-implementation improvement rather than a strict RFC requirement.

---

## References

- RFC 6295 §4.1 — RTP Header
- RFC 6295 §5 — Recovery Journal
- RFC 6295 §A.5 — Chapter N (Note Off)
- RFC 3550 §8.2 — SSRC Collision Detection and Resolution
- Apple MIDI Session Protocol documentation
