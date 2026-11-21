using System.Runtime.InteropServices;

namespace WinNetworkManager.Platform.Windows.Interop;

#pragma warning disable IDE1006 // Native names are kept verbatim.

/// <summary>IP Helper API (iphlpapi.dll). Layouts verified by tests.</summary>
internal static unsafe partial class IpHlpNative
{
    public const int AF_UNSPEC = 0;
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;

    public const int TCP_TABLE_OWNER_MODULE_ALL = 8;
    public const int UDP_TABLE_OWNER_MODULE = 2;
    public const int TCPIP_OWNING_MODULE_SIZE = 16;

    public const int TCPIP_OWNER_MODULE_INFO_BASIC = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW_OWNER_MODULE
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
        public long liCreateTimestamp;
        public fixed ulong OwningModuleInfo[TCPIP_OWNING_MODULE_SIZE];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCP6ROW_OWNER_MODULE
    {
        public fixed byte ucLocalAddr[16];
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public fixed byte ucRemoteAddr[16];
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
        public long liCreateTimestamp;
        public fixed ulong OwningModuleInfo[TCPIP_OWNING_MODULE_SIZE];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_UDPROW_OWNER_MODULE
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
        public long liCreateTimestamp;
        public int dwFlags;
        public fixed ulong OwningModuleInfo[TCPIP_OWNING_MODULE_SIZE];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_UDP6ROW_OWNER_MODULE
    {
        public fixed byte ucLocalAddr[16];
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
        public long liCreateTimestamp;
        public int dwFlags;
        public fixed ulong OwningModuleInfo[TCPIP_OWNING_MODULE_SIZE];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_IF_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        public fixed char Alias[257];
        public fixed char Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }

    /// <summary>SOCKADDR_INET: union of SOCKADDR_IN and SOCKADDR_IN6 (28 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SOCKADDR_INET
    {
        public ushort si_family;
        public ushort sin_port;           // network byte order
        public uint sin6_flowinfo;        // IPv4: sin_addr (network order)
        public fixed byte sin6_addr[16];  // IPv4: sin_zero
        public uint sin6_scope_id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IP_ADDRESS_PREFIX
    {
        public SOCKADDR_INET Prefix;
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_IPFORWARD_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IP_ADDRESS_PREFIX DestinationPrefix;
        public SOCKADDR_INET NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public uint Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public uint Origin;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_IPINTERFACE_ROW
    {
        public ushort Family;
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public uint MaxReassemblySize;
        public ulong InterfaceIdentifier;
        public uint MinRouterAdvertisementInterval;
        public uint MaxRouterAdvertisementInterval;
        public byte AdvertisingEnabled;
        public byte ForwardingEnabled;
        public byte WeakHostSend;
        public byte WeakHostReceive;
        public byte UseAutomaticMetric;
        public byte UseNeighborUnreachabilityDetection;
        public byte ManagedAddressConfigurationSupported;
        public byte OtherStatefulConfigurationSupported;
        public byte AdvertiseDefaultRoute;
        public uint RouterDiscoveryBehavior;
        public uint DadTransmits;
        public uint BaseReachableTime;
        public uint RetransmitTime;
        public uint PathMtuDiscoveryTimeout;
        public uint LinkLocalAddressBehavior;
        public uint LinkLocalAddressTimeout;
        public fixed uint ZoneIndices[16];
        public uint SitePrefixLength;
        public uint Metric;
        public uint NlMtu;
        public byte Connected;
        public byte SupportsWakeUpPatterns;
        public byte SupportsNeighborDiscovery;
        public byte SupportsRouterDiscovery;
        public uint ReachableTime;
        public byte TransmitOffload;
        public byte ReceiveOffload;
        public byte DisableDefaultRoutes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TCPIP_OWNER_MODULE_BASIC_INFO
    {
        public char* pModuleName;
        public char* pModulePath;
    }

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedTcpTable(void* pTcpTable, uint* pdwSize, int bOrder, uint ulAf, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetExtendedUdpTable(void* pUdpTable, uint* pdwSize, int bOrder, uint ulAf, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetIfTable2(void** table);

    [LibraryImport("iphlpapi.dll")]
    public static partial void FreeMibTable(void* memory);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetIpForwardTable2(ushort family, void** table);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetIpInterfaceEntry(MIB_IPINTERFACE_ROW* row);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetBestRoute2(
        ulong* interfaceLuid,
        uint interfaceIndex,
        SOCKADDR_INET* sourceAddress,
        SOCKADDR_INET* destinationAddress,
        uint addressSortOptions,
        MIB_IPFORWARD_ROW2* bestRoute,
        SOCKADDR_INET* bestSourceAddress);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetOwnerModuleFromTcpEntry(MIB_TCPROW_OWNER_MODULE* pTcpEntry, int infoClass, void* pBuffer, uint* pdwSize);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetOwnerModuleFromTcp6Entry(MIB_TCP6ROW_OWNER_MODULE* pTcpEntry, int infoClass, void* pBuffer, uint* pdwSize);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetOwnerModuleFromUdpEntry(MIB_UDPROW_OWNER_MODULE* pUdpEntry, int infoClass, void* pBuffer, uint* pdwSize);

    [LibraryImport("iphlpapi.dll")]
    public static partial uint GetOwnerModuleFromUdp6Entry(MIB_UDP6ROW_OWNER_MODULE* pUdpEntry, int infoClass, void* pBuffer, uint* pdwSize);
}
