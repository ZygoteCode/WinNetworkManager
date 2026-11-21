using System.Runtime.InteropServices;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;

namespace WinNetworkManager.Telemetry.Model;

/// <summary>Kind of a decoded Kernel-Network event.</summary>
public enum NetworkEventKind : byte
{
    /// <summary>Not a recognised event.</summary>
    Unknown = 0,

    /// <summary>TCP payload sent (counted as upload).</summary>
    TcpSend,

    /// <summary>TCP payload received (counted as download).</summary>
    TcpReceive,

    /// <summary>UDP datagram sent (counted as upload).</summary>
    UdpSend,

    /// <summary>UDP datagram received (counted as download).</summary>
    UdpReceive,

    /// <summary>TCP retransmission (quality signal, not part of totals).</summary>
    TcpRetransmit,

    /// <summary>Outgoing TCP connection attempt.</summary>
    TcpConnect,

    /// <summary>Incoming TCP connection accepted.</summary>
    TcpAccept,

    /// <summary>TCP connection closed.</summary>
    TcpDisconnect,

    /// <summary>TCP reconnect attempt.</summary>
    TcpReconnect,

    /// <summary>"Copied in protocol on behalf of user" — duplicates receive data; never added to totals.</summary>
    TcpCopy,

    /// <summary>TCP connection attempt failed.</summary>
    TcpFail,

    /// <summary>UDP failure.</summary>
    UdpFail,
}

/// <summary>A decoded Kernel-Network event. Value type; decoded without allocations.</summary>
[StructLayout(LayoutKind.Auto)]
public struct NetworkEvent
{
    /// <summary>Raw QPC timestamp of the event.</summary>
    public long Timestamp;

    /// <summary>Owner PID from the payload (the header PID is unreliable for network events).</summary>
    public int Pid;

    /// <summary>Payload size in bytes.</summary>
    public int Size;

    /// <summary>Kind.</summary>
    public NetworkEventKind Kind;

    /// <summary>Local endpoint.</summary>
    public IpEndpointValue Local;

    /// <summary>Remote endpoint.</summary>
    public IpEndpointValue Remote;

    /// <summary>Connection identifier reported by the stack (TCP), 0 when absent.</summary>
    public uint ConnectionId;

    /// <summary>Transport protocol of the event.</summary>
    public readonly TransportProtocol Protocol => Kind is NetworkEventKind.UdpSend or NetworkEventKind.UdpReceive or NetworkEventKind.UdpFail
        ? TransportProtocol.Udp
        : TransportProtocol.Tcp;
}

/// <summary>Identity of a flow: a process instance exchanging data between two endpoints over one protocol.</summary>
/// <param name="Protocol">TCP or UDP.</param>
/// <param name="Process">Process instance.</param>
/// <param name="Local">Local endpoint (IPv6 scope removed).</param>
/// <param name="Remote">Remote endpoint (IPv6 scope removed).</param>
public readonly record struct FlowKey(TransportProtocol Protocol, ProcessKey Process, IpEndpointValue Local, IpEndpointValue Remote);

/// <summary>Byte and event counters of one flow during one interval.</summary>
public struct FlowCounters : IEquatable<FlowCounters>
{
    /// <summary>Payload bytes received.</summary>
    public long ReceivedBytes;

    /// <summary>Payload bytes sent.</summary>
    public long SentBytes;

    /// <summary>Receive events (≈ segments/datagrams delivered; coalesced segments count once).</summary>
    public int ReceiveEvents;

    /// <summary>Send events.</summary>
    public int SendEvents;

    /// <summary>Retransmitted bytes.</summary>
    public long RetransmittedBytes;

    /// <summary>Retransmission events.</summary>
    public int Retransmissions;

    /// <summary>Adds another value.</summary>
    public void Add(in FlowCounters other)
    {
        ReceivedBytes += other.ReceivedBytes;
        SentBytes += other.SentBytes;
        ReceiveEvents += other.ReceiveEvents;
        SendEvents += other.SendEvents;
        RetransmittedBytes += other.RetransmittedBytes;
        Retransmissions += other.Retransmissions;
    }

    /// <inheritdoc />
    public readonly bool Equals(FlowCounters other) =>
        ReceivedBytes == other.ReceivedBytes && SentBytes == other.SentBytes && ReceiveEvents == other.ReceiveEvents &&
        SendEvents == other.SendEvents && RetransmittedBytes == other.RetransmittedBytes && Retransmissions == other.Retransmissions;

    /// <inheritdoc />
    public override readonly bool Equals(object? obj) => obj is FlowCounters other && Equals(other);

    /// <inheritdoc />
    public override readonly int GetHashCode() => HashCode.Combine(ReceivedBytes, SentBytes, ReceiveEvents, SendEvents, RetransmittedBytes, Retransmissions);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(FlowCounters left, FlowCounters right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(FlowCounters left, FlowCounters right) => !left.Equals(right);
}
