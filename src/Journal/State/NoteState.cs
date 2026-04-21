namespace Haukcode.RtpMidi.Journal.State;

/// <summary>
/// Per-channel Note On / Note Off state for Chapter N (RFC 6295 §A.6).
///
/// A given note (0–127) is either "on" (most-recent event was a Note On
/// with velocity > 0) or "off" (most-recent event was a Note Off, or a
/// Note On with velocity 0). A note MUST NOT appear in both the note log
/// list and the OFFBITS bitfield simultaneously — most-recent-wins.
///
/// <para>
/// Velocities are stored as the strike velocity of the most recent
/// active Note On (never 0). A Note On with velocity 0 is treated as a
/// Note Off per §A.6, and clears any previously stored strike velocity.
/// </para>
/// </summary>
internal sealed class NoteState
{
    private readonly bool[] _noteOnActive  = new bool[128];
    private readonly byte[] _noteOnVel     = new byte[128];
    private readonly bool[] _noteOffActive = new bool[128];

    /// <summary>True when any note has a pending Note On in the log list.</summary>
    public bool HasNoteOn  { get; private set; }

    /// <summary>True when any note has a pending Note Off in OFFBITS.</summary>
    public bool HasNoteOff { get; private set; }

    /// <summary>True when any Chapter N data is present (log entries OR OFFBITS bits).</summary>
    public bool HasAnyData => HasNoteOn || HasNoteOff;

    /// <summary>Read-only view of the per-note "most recent event was Note On" flags.</summary>
    internal ReadOnlySpan<bool> NoteOnActive => _noteOnActive;

    /// <summary>Read-only view of the per-note strike velocities (valid where NoteOnActive is true).</summary>
    internal ReadOnlySpan<byte> NoteOnVelocities => _noteOnVel;

    /// <summary>Read-only view of the per-note "most recent event was Note Off" flags.</summary>
    internal ReadOnlySpan<bool> NoteOffActive => _noteOffActive;

    /// <summary>
    /// Processes a Note On (0x9n). Velocity 0 is treated as Note Off per §A.6.
    /// </summary>
    public void ProcessNoteOn(byte note, byte velocity)
    {
        note = (byte)(note & 0x7F);
        byte vel = (byte)(velocity & 0x7F);

        if (vel == 0)
        {
            _noteOnActive[note]  = false;
            _noteOffActive[note] = true;
        }
        else
        {
            _noteOnActive[note]  = true;
            _noteOnVel[note]     = vel;
            _noteOffActive[note] = false;
        }
        RefreshFlags();
    }

    /// <summary>
    /// Processes a Note Off (0x8n). The note moves from the log list to OFFBITS.
    /// </summary>
    public void ProcessNoteOff(byte note)
    {
        note = (byte)(note & 0x7F);
        _noteOnActive[note]  = false;
        _noteOffActive[note] = true;
        RefreshFlags();
    }

    /// <summary>Discards all accumulated note state.</summary>
    public void Reset()
    {
        Array.Clear(_noteOnActive,  0, _noteOnActive.Length);
        Array.Clear(_noteOnVel,     0, _noteOnVel.Length);
        Array.Clear(_noteOffActive, 0, _noteOffActive.Length);
        HasNoteOn  = false;
        HasNoteOff = false;
    }

    private void RefreshFlags()
    {
        bool anyOn = false, anyOff = false;
        for (int i = 0; i < 128; i++)
        {
            if (_noteOnActive[i])  anyOn  = true;
            if (_noteOffActive[i]) anyOff = true;
            if (anyOn && anyOff) break;
        }
        HasNoteOn  = anyOn;
        HasNoteOff = anyOff;
    }
}
