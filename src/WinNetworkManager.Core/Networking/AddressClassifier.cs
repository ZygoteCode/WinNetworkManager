namespace WinNetworkManager.Core.Networking;

/// <summary>Reachability scope of an IP address, derived from IANA special-purpose registries.</summary>
public enum AddressScope : byte
{
    /// <summary>No address.</summary>
    None = 0,

    /// <summary>0.0.0.0 / :: (wildcard bind).</summary>
    Unspecified,

    /// <summary>127.0.0.0/8, ::1.</summary>
    Loopback,

    /// <summary>169.254.0.0/16, fe80::/10.</summary>
    LinkLocal,

    /// <summary>RFC 1918 (10/8, 172.16/12, 192.168/16), unique local fc00::/7, deprecated site-local fec0::/10.</summary>
    Private,

    /// <summary>RFC 6598 carrier-grade NAT space 100.64.0.0/10.</summary>
    SharedAddressSpace,

    /// <summary>224.0.0.0/4, ff00::/8.</summary>
    Multicast,

    /// <summary>255.255.255.255.</summary>
    Broadcast,

    /// <summary>Documentation ranges (192.0.2/24, 198.51.100/24, 203.0.113/24, 2001:db8::/32).</summary>
    Documentation,

    /// <summary>Other reserved/special-purpose ranges (benchmarking, 240/4, IETF assignments, discard-only, …).</summary>
    Reserved,

    /// <summary>Globally routable unicast.</summary>
    Public,
}

/// <summary>Classifies IP addresses into <see cref="AddressScope"/>.</summary>
public static class AddressClassifier
{
    /// <summary>Returns the scope of an address. IPv4-mapped IPv6 addresses are classified by their IPv4 part.</summary>
    public static AddressScope Classify(IpAddressValue address)
    {
        return address.Version switch
        {
            IpVersion.IPv4 => ClassifyIPv4(address.IPv4HostOrder),
            IpVersion.IPv6 => address.IsIPv4MappedToIPv6
                ? ClassifyIPv4((uint)address.Low)
                : ClassifyIPv6(address.High, address.Low),
            _ => AddressScope.None,
        };
    }

    /// <summary>True for loopback addresses (including IPv4-mapped loopback).</summary>
    public static bool IsLoopback(IpAddressValue address) => Classify(address) == AddressScope.Loopback;

    /// <summary>True when traffic with this remote address never leaves the local network segment/site.</summary>
    public static bool IsLocalNetwork(AddressScope scope) =>
        scope is AddressScope.LinkLocal or AddressScope.Private or AddressScope.SharedAddressSpace
            or AddressScope.Multicast or AddressScope.Broadcast;

    /// <summary>Human readable name for a scope.</summary>
    public static string DisplayName(AddressScope scope) => scope switch
    {
        AddressScope.None => "—",
        AddressScope.Unspecified => "Unspecified",
        AddressScope.Loopback => "Loopback",
        AddressScope.LinkLocal => "Link-local",
        AddressScope.Private => "Private",
        AddressScope.SharedAddressSpace => "Shared (CGNAT)",
        AddressScope.Multicast => "Multicast",
        AddressScope.Broadcast => "Broadcast",
        AddressScope.Documentation => "Documentation",
        AddressScope.Reserved => "Reserved",
        AddressScope.Public => "Public",
        _ => scope.ToString(),
    };

    private static AddressScope ClassifyIPv4(uint a)
    {
        if (a == 0)
        {
            return AddressScope.Unspecified;
        }

        if (a == 0xFFFFFFFF)
        {
            return AddressScope.Broadcast;
        }

        byte first = (byte)(a >> 24);
        return first switch
        {
            0 => AddressScope.Reserved,                       // 0.0.0.0/8 "this network"
            10 => AddressScope.Private,
            127 => AddressScope.Loopback,
            100 when (a & 0xFFC00000) == 0x64400000 => AddressScope.SharedAddressSpace, // 100.64/10
            169 when (a & 0xFFFF0000) == 0xA9FE0000 => AddressScope.LinkLocal,          // 169.254/16
            172 when (a & 0xFFF00000) == 0xAC100000 => AddressScope.Private,            // 172.16/12
            192 when (a & 0xFFFF0000) == 0xC0A80000 => AddressScope.Private,            // 192.168/16
            192 when (a & 0xFFFFFF00) == 0xC0000200 => AddressScope.Documentation,      // 192.0.2/24
            192 when (a & 0xFFFFFF00) == 0xC0000000 => AddressScope.Reserved,           // 192.0.0/24
            198 when (a & 0xFFFE0000) == 0xC6120000 => AddressScope.Reserved,           // 198.18/15 benchmarking
            198 when (a & 0xFFFFFF00) == 0xC6336400 => AddressScope.Documentation,      // 198.51.100/24
            203 when (a & 0xFFFFFF00) == 0xCB007100 => AddressScope.Documentation,      // 203.0.113/24
            >= 224 and <= 239 => AddressScope.Multicast,
            >= 240 => AddressScope.Reserved,
            _ => AddressScope.Public,
        };
    }

    private static AddressScope ClassifyIPv6(ulong hi, ulong lo)
    {
        if (hi == 0)
        {
            if (lo == 0)
            {
                return AddressScope.Unspecified;
            }

            if (lo == 1)
            {
                return AddressScope.Loopback;
            }

            return AddressScope.Reserved; // IPv4-compatible (deprecated) and other ::/96 space
        }

        ushort first16 = (ushort)(hi >> 48);
        if ((first16 & 0xFF00) == 0xFF00)
        {
            return AddressScope.Multicast;
        }

        if ((first16 & 0xFFC0) == 0xFE80)
        {
            return AddressScope.LinkLocal;
        }

        if ((first16 & 0xFFC0) == 0xFEC0 || (first16 & 0xFE00) == 0xFC00)
        {
            return AddressScope.Private; // deprecated site-local, unique local addresses
        }

        if ((hi >> 32) == 0x20010DB8)
        {
            return AddressScope.Documentation;
        }

        if (hi == 0x0100000000000000)
        {
            return AddressScope.Reserved; // 100::/64 discard-only
        }

        if ((first16 & 0xE000) == 0x2000)
        {
            return AddressScope.Public; // 2000::/3 global unicast (includes Teredo 2001::/32 and 6to4 2002::/16)
        }

        if (hi == 0x0064FF9B00000000 && (lo >> 32) == 0)
        {
            return AddressScope.Public; // 64:ff9b::/96 NAT64 well-known prefix
        }

        return AddressScope.Reserved;
    }
}
