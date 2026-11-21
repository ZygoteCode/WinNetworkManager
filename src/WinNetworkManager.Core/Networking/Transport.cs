namespace WinNetworkManager.Core.Networking;

/// <summary>Transport protocol.</summary>
public enum TransportProtocol : byte
{
    /// <summary>Transmission Control Protocol (connection oriented).</summary>
    Tcp = 6,

    /// <summary>User Datagram Protocol (connectionless).</summary>
    Udp = 17,
}

/// <summary>
/// TCP connection state as reported by the IP Helper API (<c>MIB_TCP_STATE</c>). Numeric values match Windows.
/// </summary>
public enum TcpState : byte
{
    /// <summary>Unknown/not applicable (e.g. UDP).</summary>
    Unknown = 0,

    /// <summary>CLOSED.</summary>
    Closed = 1,

    /// <summary>LISTENING.</summary>
    Listen = 2,

    /// <summary>SYN_SENT.</summary>
    SynSent = 3,

    /// <summary>SYN_RECEIVED.</summary>
    SynReceived = 4,

    /// <summary>ESTABLISHED.</summary>
    Established = 5,

    /// <summary>FIN_WAIT_1.</summary>
    FinWait1 = 6,

    /// <summary>FIN_WAIT_2.</summary>
    FinWait2 = 7,

    /// <summary>CLOSE_WAIT.</summary>
    CloseWait = 8,

    /// <summary>CLOSING.</summary>
    Closing = 9,

    /// <summary>LAST_ACK.</summary>
    LastAck = 10,

    /// <summary>TIME_WAIT.</summary>
    TimeWait = 11,

    /// <summary>DELETE_TCB.</summary>
    DeleteTcb = 12,
}

/// <summary>Helpers for <see cref="TcpState"/>.</summary>
public static class TcpStateInfo
{
    /// <summary>Converts a raw <c>MIB_TCP_STATE</c> value; unknown values map to <see cref="TcpState.Unknown"/>.</summary>
    public static TcpState FromNative(uint value) => value is >= 1 and <= 12 ? (TcpState)value : TcpState.Unknown;

    /// <summary>Conventional upper-case name (as used by netstat).</summary>
    public static string DisplayName(TcpState state) => state switch
    {
        TcpState.Closed => "CLOSED",
        TcpState.Listen => "LISTENING",
        TcpState.SynSent => "SYN_SENT",
        TcpState.SynReceived => "SYN_RECEIVED",
        TcpState.Established => "ESTABLISHED",
        TcpState.FinWait1 => "FIN_WAIT_1",
        TcpState.FinWait2 => "FIN_WAIT_2",
        TcpState.CloseWait => "CLOSE_WAIT",
        TcpState.Closing => "CLOSING",
        TcpState.LastAck => "LAST_ACK",
        TcpState.TimeWait => "TIME_WAIT",
        TcpState.DeleteTcb => "DELETE_TCB",
        _ => "—",
    };

    /// <summary>Plain-language explanation of a state, used in tooltips.</summary>
    public static string Description(TcpState state) => state switch
    {
        TcpState.Closed => "The connection is closed.",
        TcpState.Listen => "Waiting for incoming connection requests.",
        TcpState.SynSent => "Connection request sent, waiting for the remote side to answer.",
        TcpState.SynReceived => "Connection request received and acknowledged, waiting for the final handshake step.",
        TcpState.Established => "Open connection; data can flow in both directions.",
        TcpState.FinWait1 => "Local side asked to close; waiting for the remote acknowledgement.",
        TcpState.FinWait2 => "Close acknowledged; waiting for the remote side to close too.",
        TcpState.CloseWait => "Remote side closed; waiting for the local application to close.",
        TcpState.Closing => "Both sides are closing simultaneously.",
        TcpState.LastAck => "Waiting for the final acknowledgement of the close.",
        TcpState.TimeWait => "Closed; kept briefly so late packets are not mistaken for a new connection. No owning process.",
        TcpState.DeleteTcb => "The connection control block is being deleted.",
        _ => "State not available.",
    };

    /// <summary>True for states in which data can be exchanged or a close is in progress.</summary>
    public static bool IsActive(TcpState state) =>
        state is TcpState.Established or TcpState.SynSent or TcpState.SynReceived or TcpState.FinWait1
            or TcpState.FinWait2 or TcpState.CloseWait or TcpState.Closing or TcpState.LastAck;
}
