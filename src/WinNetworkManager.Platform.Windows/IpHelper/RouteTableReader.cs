using System.Buffers.Binary;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Platform.Windows.Interop;
using static WinNetworkManager.Platform.Windows.Interop.IpHlpNative;

namespace WinNetworkManager.Platform.Windows.IpHelper;

/// <summary>Origin of a route (NL_ROUTE_PROTOCOL subset).</summary>
public enum RouteProtocol : byte
{
    /// <summary>Other/unknown.</summary>
    Other = 0,

    /// <summary>Local interface (connected network).</summary>
    Local,

    /// <summary>Static (manually configured or by DHCP option).</summary>
    Static,

    /// <summary>Learned from router advertisements / ICMP redirects.</summary>
    Discovered,

    /// <summary>Dynamic routing protocol.</summary>
    Dynamic,
}

/// <summary>One entry of the IP forwarding table.</summary>
/// <param name="Destination">Destination prefix address.</param>
/// <param name="PrefixLength">Destination prefix length.</param>
/// <param name="NextHop">Gateway (unspecified for on-link routes).</param>
/// <param name="InterfaceIndex">Outgoing interface index.</param>
/// <param name="InterfaceLuid">Outgoing interface LUID.</param>
/// <param name="RouteMetric">Route metric.</param>
/// <param name="InterfaceMetric">Interface metric (effective metric = route + interface), null if unknown.</param>
/// <param name="Protocol">Route origin.</param>
/// <param name="IsLoopback">Loopback route.</param>
public sealed record RouteRow(
    IpAddressValue Destination,
    byte PrefixLength,
    IpAddressValue NextHop,
    uint InterfaceIndex,
    ulong InterfaceLuid,
    uint RouteMetric,
    uint? InterfaceMetric,
    RouteProtocol Protocol,
    bool IsLoopback)
{
    /// <summary>Route metric + interface metric.</summary>
    public uint? EffectiveMetric => InterfaceMetric is { } m ? RouteMetric + m : null;

    /// <summary>True for 0.0.0.0/0 or ::/0.</summary>
    public bool IsDefaultRoute => PrefixLength == 0;

    /// <summary>Prefix in CIDR notation.</summary>
    public string Prefix => $"{Destination}/{PrefixLength}";
}

/// <summary>Result of a best-route lookup.</summary>
/// <param name="InterfaceIndex">Interface that would carry traffic to the destination.</param>
/// <param name="InterfaceLuid">Its LUID.</param>
/// <param name="NextHop">Gateway or unspecified for on-link.</param>
/// <param name="SourceAddress">Source address that would be used.</param>
public sealed record BestRoute(uint InterfaceIndex, ulong InterfaceLuid, IpAddressValue NextHop, IpAddressValue SourceAddress);

/// <summary>Reads the routing table and performs best-route lookups (no packets are sent).</summary>
public static unsafe class RouteTableReader
{
    /// <summary>Reads IPv4 and IPv6 routes.</summary>
    public static IReadOnlyList<RouteRow> ReadRoutes()
    {
        void* table = null;
        uint error = GetIpForwardTable2(AF_UNSPEC, &table);
        if (error != Win32Error.ERROR_SUCCESS)
        {
            throw new NativeCallException("GetIpForwardTable2", error);
        }

        var metrics = new Dictionary<(ushort, ulong), uint?>();
        try
        {
            uint count = *(uint*)table;
            var rows = (MIB_IPFORWARD_ROW2*)((byte*)table + 8);
            var result = new List<RouteRow>((int)count);
            for (int i = 0; i < count; i++)
            {
                MIB_IPFORWARD_ROW2* row = &rows[i];
                ushort family = row->DestinationPrefix.Prefix.si_family;
                var key = (family, row->InterfaceLuid);
                if (!metrics.TryGetValue(key, out uint? interfaceMetric))
                {
                    interfaceMetric = GetInterfaceMetric(family, row->InterfaceLuid);
                    metrics[key] = interfaceMetric;
                }

                result.Add(new RouteRow(
                    ToAddress(&row->DestinationPrefix.Prefix),
                    row->DestinationPrefix.PrefixLength,
                    ToAddress(&row->NextHop),
                    row->InterfaceIndex,
                    row->InterfaceLuid,
                    row->Metric,
                    interfaceMetric,
                    MapProtocol(row->Protocol),
                    row->Loopback != 0));
            }

            return result;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    /// <summary>Finds the route Windows would use to reach <paramref name="destination"/>.</summary>
    public static BestRoute? FindBestRoute(IpAddressValue destination)
    {
        SOCKADDR_INET dest = ToSockAddr(destination);
        MIB_IPFORWARD_ROW2 route;
        SOCKADDR_INET source;
        uint error = GetBestRoute2(null, 0, null, &dest, 0, &route, &source);
        if (error != Win32Error.ERROR_SUCCESS)
        {
            return null;
        }

        return new BestRoute(route.InterfaceIndex, route.InterfaceLuid, ToAddress(&route.NextHop), ToAddress(&source));
    }

    private static uint? GetInterfaceMetric(ushort family, ulong luid)
    {
        var row = default(MIB_IPINTERFACE_ROW);
        row.Family = family;
        row.InterfaceLuid = luid;
        return GetIpInterfaceEntry(&row) == Win32Error.ERROR_SUCCESS ? row.Metric : null;
    }

    internal static IpAddressValue ToAddress(SOCKADDR_INET* address)
    {
        if (address->si_family == AF_INET)
        {
            // SOCKADDR_IN: family(2) port(2) sin_addr(4) → sin_addr overlays sin6_flowinfo.
            return IpAddressValue.FromIPv4NetworkOrder(address->sin6_flowinfo);
        }

        if (address->si_family == AF_INET6)
        {
            return IpAddressValue.FromIPv6(new ReadOnlySpan<byte>(address->sin6_addr, 16), address->sin6_scope_id);
        }

        return default;
    }

    internal static SOCKADDR_INET ToSockAddr(IpAddressValue address)
    {
        var result = default(SOCKADDR_INET);
        if (address.IsIPv4)
        {
            result.si_family = AF_INET;
            result.sin6_flowinfo = BinaryPrimitives.ReverseEndianness(address.IPv4HostOrder);
        }
        else if (address.IsIPv6)
        {
            result.si_family = AF_INET6;
            Span<byte> bytes = stackalloc byte[16];
            address.WriteBytes(bytes);
            bytes.CopyTo(new Span<byte>(result.sin6_addr, 16));
            result.sin6_scope_id = address.ScopeId;
        }

        return result;
    }

    private static RouteProtocol MapProtocol(uint protocol) => protocol switch
    {
        2 => RouteProtocol.Local,          // MIB_IPPROTO_LOCAL
        3 => RouteProtocol.Static,         // MIB_IPPROTO_NETMGMT
        4 => RouteProtocol.Discovered,     // MIB_IPPROTO_ICMP
        10002 or 10006 => RouteProtocol.Static, // MIB_IPPROTO_NT_STATIC / NT_STATIC_NON_DOD
        10007 => RouteProtocol.Discovered, // RA / autostatic
        8 or 13 or 14 => RouteProtocol.Dynamic,  // RIP, OSPF, BGP
        _ => RouteProtocol.Other,
    };

    internal static int RowSize => sizeof(MIB_IPFORWARD_ROW2);
}
