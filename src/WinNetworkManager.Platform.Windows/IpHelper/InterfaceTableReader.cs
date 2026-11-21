using WinNetworkManager.Platform.Windows.Interop;
using static WinNetworkManager.Platform.Windows.Interop.IpHlpNative;

namespace WinNetworkManager.Platform.Windows.IpHelper;

/// <summary>Operational status (IF-MIB ifOperStatus).</summary>
public enum InterfaceOperationalStatus : byte
{
    /// <summary>Unknown.</summary>
    Unknown = 0,

    /// <summary>Up: can pass packets.</summary>
    Up = 1,

    /// <summary>Down.</summary>
    Down = 2,

    /// <summary>Testing.</summary>
    Testing = 3,

    /// <summary>Status unknown (IF-MIB 4).</summary>
    StatusUnknown = 4,

    /// <summary>Dormant (waiting for an external event).</summary>
    Dormant = 5,

    /// <summary>A required component is missing.</summary>
    NotPresent = 6,

    /// <summary>Lower layer down.</summary>
    LowerLayerDown = 7,
}

/// <summary>Counters and identity of one interface from <c>GetIfTable2</c> (all 64-bit IF-MIB counters).</summary>
public sealed record InterfaceRow
{
    /// <summary>Locally unique identifier (stable while the interface exists).</summary>
    public required ulong Luid { get; init; }

    /// <summary>Interface index.</summary>
    public required uint Index { get; init; }

    /// <summary>Interface GUID (matches NetworkInterface.Id).</summary>
    public required Guid Guid { get; init; }

    /// <summary>Friendly name (alias), e.g. "Ethernet".</summary>
    public required string Alias { get; init; }

    /// <summary>Adapter description.</summary>
    public required string Description { get; init; }

    /// <summary>IANA ifType (6 ethernet, 71 Wi-Fi, 24 loopback, 131 tunnel, …).</summary>
    public required uint IfType { get; init; }

    /// <summary>NDIS physical medium.</summary>
    public required uint PhysicalMediumType { get; init; }

    /// <summary>Backed by hardware.</summary>
    public required bool IsHardware { get; init; }

    /// <summary>An NDIS filter interface (duplicates the counters of the interface it filters).</summary>
    public required bool IsFilter { get; init; }

    /// <summary>Connector present.</summary>
    public required bool ConnectorPresent { get; init; }

    /// <summary>Operational status.</summary>
    public required InterfaceOperationalStatus OperStatus { get; init; }

    /// <summary>True when the media is connected.</summary>
    public required bool MediaConnected { get; init; }

    /// <summary>MTU in bytes.</summary>
    public required uint Mtu { get; init; }

    /// <summary>Transmit link speed in bits per second.</summary>
    public required ulong TransmitLinkSpeed { get; init; }

    /// <summary>Receive link speed in bits per second.</summary>
    public required ulong ReceiveLinkSpeed { get; init; }

    /// <summary>Octets received, including framing.</summary>
    public required ulong InOctets { get; init; }

    /// <summary>Octets sent, including framing.</summary>
    public required ulong OutOctets { get; init; }

    /// <summary>Packets received (unicast + non-unicast).</summary>
    public required ulong InPackets { get; init; }

    /// <summary>Packets sent (unicast + non-unicast).</summary>
    public required ulong OutPackets { get; init; }

    /// <summary>Inbound packets discarded.</summary>
    public required ulong InDiscards { get; init; }

    /// <summary>Inbound packets with errors.</summary>
    public required ulong InErrors { get; init; }

    /// <summary>Outbound packets discarded.</summary>
    public required ulong OutDiscards { get; init; }

    /// <summary>Outbound packets with errors.</summary>
    public required ulong OutErrors { get; init; }

    /// <summary>MAC address bytes (may be empty).</summary>
    public required byte[] PhysicalAddress { get; init; }

    /// <summary>True for the software loopback pseudo-interface.</summary>
    public bool IsLoopback => IfType == 24;

    /// <summary>True for tunnel interfaces (Teredo, IP-HTTPS, 6to4, ISATAP, many VPNs).</summary>
    public bool IsTunnel => IfType == 131;
}

/// <summary>Source of interface rows (abstracted for engine tests).</summary>
public interface IInterfaceTableSource
{
    /// <summary>Reads every interface known to the stack.</summary>
    IReadOnlyList<InterfaceRow> ReadInterfaces();
}

/// <summary>Reads <c>GetIfTable2</c>.</summary>
public sealed unsafe class InterfaceTableReader : IInterfaceTableSource
{
    /// <inheritdoc />
    public IReadOnlyList<InterfaceRow> ReadInterfaces()
    {
        void* table = null;
        uint error = GetIfTable2(&table);
        if (error != Win32Error.ERROR_SUCCESS)
        {
            throw new NativeCallException("GetIfTable2", error);
        }

        try
        {
            uint count = *(uint*)table;
            var rows = (MIB_IF_ROW2*)((byte*)table + 8);
            var result = new InterfaceRow[count];
            for (int i = 0; i < count; i++)
            {
                MIB_IF_ROW2* row = &rows[i];
                byte flags = row->InterfaceAndOperStatusFlags;
                int macLength = (int)Math.Min(row->PhysicalAddressLength, 32u);
                result[i] = new InterfaceRow
                {
                    Luid = row->InterfaceLuid,
                    Index = row->InterfaceIndex,
                    Guid = row->InterfaceGuid,
                    Alias = new string(row->Alias),
                    Description = new string(row->Description),
                    IfType = row->Type,
                    PhysicalMediumType = row->PhysicalMediumType,
                    IsHardware = (flags & 0x01) != 0,
                    IsFilter = (flags & 0x02) != 0,
                    ConnectorPresent = (flags & 0x04) != 0,
                    MediaConnected = (flags & 0x10) == 0 && row->MediaConnectState == 1,
                    OperStatus = row->OperStatus is >= 1 and <= 7 ? (InterfaceOperationalStatus)row->OperStatus : InterfaceOperationalStatus.Unknown,
                    Mtu = row->Mtu,
                    TransmitLinkSpeed = row->TransmitLinkSpeed,
                    ReceiveLinkSpeed = row->ReceiveLinkSpeed,
                    InOctets = row->InOctets,
                    OutOctets = row->OutOctets,
                    InPackets = row->InUcastPkts + row->InNUcastPkts,
                    OutPackets = row->OutUcastPkts + row->OutNUcastPkts,
                    InDiscards = row->InDiscards,
                    InErrors = row->InErrors,
                    OutDiscards = row->OutDiscards,
                    OutErrors = row->OutErrors,
                    PhysicalAddress = new ReadOnlySpan<byte>(row->PhysicalAddress, macLength).ToArray(),
                };
            }

            return result;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    internal static int RowSize => sizeof(MIB_IF_ROW2);
}
