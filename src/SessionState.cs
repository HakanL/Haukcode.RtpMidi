namespace Haukcode.RtpMidi;

public enum SessionState
{
    /// <summary>Not connected; no sockets open.</summary>
    Idle,

    /// <summary>Sent IN on control port, waiting for OK.</summary>
    ConnectingControl,

    /// <summary>Control port established; sent IN on data port, waiting for OK.</summary>
    ConnectingData,

    /// <summary>Both ports established and clock sync running.</summary>
    Connected,

    /// <summary>Sent BY on both ports; tearing down sockets.</summary>
    Disconnecting,
}
