namespace Haukcode.RtpMidi.Journal.State;

/// <summary>
/// Per-channel RPN/NRPN parameter-system state for Chapter M (RFC 6295 §A.4).
///
/// Tracks the log list of selected parameters — most-recently-selected first —
/// along with a pending parameter-MSB when an RPN/NRPN selection has started
/// (CC 99 or 101) but the matching LSB (CC 98 or 100) has not yet arrived.
///
/// A second selection of a parameter number already in the log removes the
/// existing entry and inserts a fresh one at the front with cleared data-entry
/// fields ("re-selection resets data" per §A.4).
/// </summary>
internal sealed class ParameterLog
{
    /// <summary>
    /// One log entry: the parameter address plus the most-recent data-entry
    /// values seen for it since selection.
    /// </summary>
    internal struct Entry
    {
        public bool IsNrpn;
        public byte ParamMsb, ParamLsb;
        public bool HasDataMsb, HasDataLsb;
        public byte DataMsb, DataLsb;
    }

    private readonly List<Entry> _entries = new();
    private bool _hasPendingMsb;
    private bool _pendingIsNrpn;
    private byte _pendingMsb;

    /// <summary>Log entries, most-recently-selected first.</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>True when a parameter MSB has been received without a matching LSB.</summary>
    public bool HasPendingMsb => _hasPendingMsb;

    /// <summary>True when the pending MSB is for an NRPN selection (CC 99).</summary>
    public bool PendingIsNrpn => _pendingIsNrpn;

    /// <summary>The pending MSB value, only meaningful when <see cref="HasPendingMsb"/>.</summary>
    public byte PendingMsb => _pendingMsb;

    /// <summary>True when any parameter-system state has been accumulated.</summary>
    public bool HasAnyData => _entries.Count > 0 || _hasPendingMsb;

    /// <summary>Discards all accumulated parameter-system state.</summary>
    public void Reset()
    {
        _entries.Clear();
        _hasPendingMsb = false;
        _pendingIsNrpn = false;
        _pendingMsb    = 0;
    }

    /// <summary>
    /// Processes a Control Change that may participate in the parameter
    /// system (CCs 6, 38, 98, 99, 100, 101). Other controller numbers are
    /// ignored and the caller need not filter them out.
    /// </summary>
    public void ProcessControlChange(byte cc, byte value)
    {
        switch (cc)
        {
            case 99:  // NRPN MSB — begin NRPN parameter selection
                _hasPendingMsb = true;
                _pendingIsNrpn = true;
                _pendingMsb    = value;
                break;

            case 101: // RPN MSB — begin RPN parameter selection
                _hasPendingMsb = true;
                _pendingIsNrpn = false;
                _pendingMsb    = value;
                break;

            case 98:  // NRPN LSB — complete NRPN selection
                if (_hasPendingMsb && _pendingIsNrpn)
                {
                    Finalize(isNrpn: true, msb: _pendingMsb, lsb: value);
                    _hasPendingMsb = false;
                }
                break;

            case 100: // RPN LSB — complete RPN selection
                if (_hasPendingMsb && !_pendingIsNrpn)
                {
                    Finalize(isNrpn: false, msb: _pendingMsb, lsb: value);
                    _hasPendingMsb = false;
                }
                break;

            case 6:   // Data Entry MSB — update front log entry
                if (_entries.Count > 0)
                {
                    var e = _entries[0];
                    e.HasDataMsb = true;
                    e.DataMsb    = value;
                    _entries[0]  = e;
                }
                break;

            case 38:  // Data Entry LSB — update front log entry
                if (_entries.Count > 0)
                {
                    var e = _entries[0];
                    e.HasDataLsb = true;
                    e.DataLsb    = value;
                    _entries[0]  = e;
                }
                break;
        }
    }

    /// <summary>
    /// Inserts a fresh entry for (isNrpn, msb, lsb) at the front of the log
    /// after removing any existing entry with the same address ("re-selection
    /// resets data" — RFC 6295 §A.4).
    /// </summary>
    private void Finalize(bool isNrpn, byte msb, byte lsb)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (e.IsNrpn == isNrpn && e.ParamMsb == msb && e.ParamLsb == lsb)
            {
                _entries.RemoveAt(i);
                break;
            }
        }
        _entries.Insert(0, new Entry { IsNrpn = isNrpn, ParamMsb = msb, ParamLsb = lsb });
    }

    /// <summary>
    /// Appends a new entry at the front without merging. Used only by decoders
    /// that rebuild a log from wire bytes and must NOT apply re-selection
    /// semantics (which would be lossy).
    /// </summary>
    internal void AppendFromDecode(Entry entry) => _entries.Insert(0, entry);
}
