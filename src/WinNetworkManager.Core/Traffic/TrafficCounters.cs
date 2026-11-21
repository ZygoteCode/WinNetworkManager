using System.Numerics;

namespace WinNetworkManager.Core.Traffic;

/// <summary>TCP/UDP payload byte counters split by direction.</summary>
public struct ProtocolBytes : IEquatable<ProtocolBytes>, IAdditionOperators<ProtocolBytes, ProtocolBytes, ProtocolBytes>
{
    /// <summary>TCP payload received (download).</summary>
    public long TcpReceived;

    /// <summary>TCP payload sent (upload), excluding retransmissions.</summary>
    public long TcpSent;

    /// <summary>UDP payload received (download).</summary>
    public long UdpReceived;

    /// <summary>UDP payload sent (upload).</summary>
    public long UdpSent;

    /// <summary>Creates a value.</summary>
    public ProtocolBytes(long tcpReceived, long tcpSent, long udpReceived, long udpSent)
    {
        TcpReceived = tcpReceived;
        TcpSent = tcpSent;
        UdpReceived = udpReceived;
        UdpSent = udpSent;
    }

    /// <summary>TCP + UDP received.</summary>
    public readonly long Received => TcpReceived + UdpReceived;

    /// <summary>TCP + UDP sent.</summary>
    public readonly long Sent => TcpSent + UdpSent;

    /// <summary>All bytes in both directions.</summary>
    public readonly long Total => Received + Sent;

    /// <summary>True when every counter is zero.</summary>
    public readonly bool IsZero => TcpReceived == 0 && TcpSent == 0 && UdpReceived == 0 && UdpSent == 0;

    /// <summary>Adds two values.</summary>
    public static ProtocolBytes operator +(ProtocolBytes left, ProtocolBytes right) => new(
        left.TcpReceived + right.TcpReceived,
        left.TcpSent + right.TcpSent,
        left.UdpReceived + right.UdpReceived,
        left.UdpSent + right.UdpSent);

    /// <summary>Subtracts two values (used for unattributed/difference computations).</summary>
    public static ProtocolBytes operator -(ProtocolBytes left, ProtocolBytes right) => new(
        left.TcpReceived - right.TcpReceived,
        left.TcpSent - right.TcpSent,
        left.UdpReceived - right.UdpReceived,
        left.UdpSent - right.UdpSent);

    /// <summary>Adds two values (named alternative to the operator).</summary>
    public static ProtocolBytes Add(ProtocolBytes left, ProtocolBytes right) => left + right;

    /// <summary>Subtracts two values (named alternative to the operator).</summary>
    public static ProtocolBytes Subtract(ProtocolBytes left, ProtocolBytes right) => left - right;

    /// <inheritdoc />
    public readonly bool Equals(ProtocolBytes other) =>
        TcpReceived == other.TcpReceived && TcpSent == other.TcpSent &&
        UdpReceived == other.UdpReceived && UdpSent == other.UdpSent;

    /// <inheritdoc />
    public override readonly bool Equals(object? obj) => obj is ProtocolBytes other && Equals(other);

    /// <inheritdoc />
    public override readonly int GetHashCode() => HashCode.Combine(TcpReceived, TcpSent, UdpReceived, UdpSent);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(ProtocolBytes left, ProtocolBytes right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(ProtocolBytes left, ProtocolBytes right) => !left.Equals(right);
}

/// <summary>
/// Traffic attributed to one entity (process, flow, system) during an interval or cumulatively.
/// External and loopback traffic are kept apart so loopback can be excluded from "network" figures without
/// losing it.
/// </summary>
public struct TrafficCounters : IEquatable<TrafficCounters>, IAdditionOperators<TrafficCounters, TrafficCounters, TrafficCounters>
{
    /// <summary>Traffic whose remote endpoint is not a loopback address.</summary>
    public ProtocolBytes External;

    /// <summary>Traffic exchanged with loopback addresses (127.0.0.0/8, ::1).</summary>
    public ProtocolBytes Loopback;

    /// <summary>TCP bytes retransmitted (quality signal; not part of <see cref="ProtocolBytes.TcpSent"/>).</summary>
    public long TcpRetransmittedBytes;

    /// <summary>Number of TCP retransmission events.</summary>
    public long TcpRetransmissions;

    /// <summary>Creates a value.</summary>
    public TrafficCounters(ProtocolBytes external, ProtocolBytes loopback, long tcpRetransmittedBytes = 0, long tcpRetransmissions = 0)
    {
        External = external;
        Loopback = loopback;
        TcpRetransmittedBytes = tcpRetransmittedBytes;
        TcpRetransmissions = tcpRetransmissions;
    }

    /// <summary>Returns external traffic, plus loopback when <paramref name="includeLoopback"/> is set.</summary>
    public readonly ProtocolBytes Select(bool includeLoopback) => includeLoopback ? External + Loopback : External;

    /// <summary>True when nothing was counted.</summary>
    public readonly bool IsZero => External.IsZero && Loopback.IsZero && TcpRetransmittedBytes == 0 && TcpRetransmissions == 0;

    /// <summary>Adds two values.</summary>
    public static TrafficCounters operator +(TrafficCounters left, TrafficCounters right) => new(
        left.External + right.External,
        left.Loopback + right.Loopback,
        left.TcpRetransmittedBytes + right.TcpRetransmittedBytes,
        left.TcpRetransmissions + right.TcpRetransmissions);

    /// <summary>Adds two values (named alternative to the operator).</summary>
    public static TrafficCounters Add(TrafficCounters left, TrafficCounters right) => left + right;

    /// <inheritdoc />
    public readonly bool Equals(TrafficCounters other) =>
        External == other.External && Loopback == other.Loopback &&
        TcpRetransmittedBytes == other.TcpRetransmittedBytes && TcpRetransmissions == other.TcpRetransmissions;

    /// <inheritdoc />
    public override readonly bool Equals(object? obj) => obj is TrafficCounters other && Equals(other);

    /// <inheritdoc />
    public override readonly int GetHashCode() => HashCode.Combine(External, Loopback, TcpRetransmittedBytes, TcpRetransmissions);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(TrafficCounters left, TrafficCounters right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(TrafficCounters left, TrafficCounters right) => !left.Equals(right);
}

/// <summary>Rates in bytes per second, split like <see cref="ProtocolBytes"/>.</summary>
public readonly record struct TrafficRates(double TcpReceived, double TcpSent, double UdpReceived, double UdpSent)
{
    /// <summary>Zero rates.</summary>
    public static TrafficRates Zero => default;

    /// <summary>Download rate (TCP + UDP).</summary>
    public double Received => TcpReceived + UdpReceived;

    /// <summary>Upload rate (TCP + UDP).</summary>
    public double Sent => TcpSent + UdpSent;

    /// <summary>Combined rate.</summary>
    public double Total => Received + Sent;

    /// <summary>Rates from a byte quantity accumulated over <paramref name="seconds"/>.</summary>
    public static TrafficRates FromBytes(ProtocolBytes bytes, double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds))
        {
            return Zero;
        }

        return new TrafficRates(bytes.TcpReceived / seconds, bytes.TcpSent / seconds, bytes.UdpReceived / seconds, bytes.UdpSent / seconds);
    }
}
