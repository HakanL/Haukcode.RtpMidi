namespace Haukcode.RtpMidi;

/// <summary>
/// A single MIDI command with an optional delta-time prefix, as used in the
/// RFC 6295 MIDI command list (§3.1).
/// </summary>
/// <param name="DeltaTime">
/// Time delta from the previous command (or from the packet timestamp if this is the
/// first command), expressed in RTP-MIDI timestamp units (100 µs ticks).
/// Zero means "simultaneous with the previous command" or "no delay".
/// </param>
/// <param name="Data">
/// Complete MIDI bytes for this command, always including the status byte at index 0
/// (e.g. 0x90 for Note On channel 1, followed by key and velocity bytes).
/// </param>
public readonly record struct MidiCommand(uint DeltaTime, ReadOnlyMemory<byte> Data);
