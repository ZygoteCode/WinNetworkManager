using System.Buffers.Binary;
using System.Runtime.InteropServices;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Platform.Windows.Interop;
using static WinNetworkManager.Platform.Windows.Interop.IpHlpNative;

namespace WinNetworkManager.Platform.Windows.IpHelper;

/// <summary>One TCP endpoint/connection from the IP Helper table.</summary>
/// <param name="Local">Local endpoint.</param>
/// <param name="Remote">Remote endpoint (0.0.0.0:0 / [::]:0 for listeners).</param>
/// <param name="State">TCP state.</param>
/// <param name="OwningPid">Owner PID (0 for TIME_WAIT rows, which have no owner).</param>
/// <param name="CreateTimestamp">FILETIME when the socket context was bound; 0 when unknown.</param>
/// <param name="OwnerModule">Owning module/service name when resolved (svchost services), otherwise null.</param>
public readonly record struct TcpConnectionRow(
    IpEndpointValue Local,
    IpEndpointValue Remote,
    TcpState State,
    int OwningPid,
    long CreateTimestamp,
    string? OwnerModule);

/// <summary>One UDP endpoint (bound socket) from the IP Helper table. UDP has no remote endpoint.</summary>
/// <param name="Local">Local endpoint.</param>
/// <param name="OwningPid">Owner PID.</param>
/// <param name="CreateTimestamp">FILETIME when the socket was bound; 0 when unknown.</param>
/// <param name="SpecificPortBind">True when bound with SO_EXCLUSIVEADDRUSE-like specific port semantics.</param>
/// <param name="OwnerModule">Owning module/service name when resolved, otherwise null.</param>
public readonly record struct UdpEndpointRow(
    IpEndpointValue Local,
    int OwningPid,
    long CreateTimestamp,
    bool SpecificPortBind,
    string? OwnerModule);

/// <summary>Source of TCP/UDP tables (abstracted for engine tests).</summary>
public interface IConnectionTableSource
{
    /// <summary>Reads IPv4 and IPv6 TCP rows into <paramref name="destination"/> (cleared first).</summary>
    void ReadTcp(List<TcpConnectionRow> destination);

    /// <summary>Reads IPv4 and IPv6 UDP rows into <paramref name="destination"/> (cleared first).</summary>
    void ReadUdp(List<UdpEndpointRow> destination);
}

/// <summary>
/// Reads TCP/UDP tables with owner PID, creation timestamp and (optionally) owning service, for IPv4 and IPv6.
/// Handles table growth between the size query and the fill (grow-and-retry) and validates row counts against
/// the returned size. Not thread-safe: use one instance per polling loop.
/// </summary>
public sealed unsafe class ConnectionTableReader : IConnectionTableSource, IDisposable
{
    private const int MaxAttempts = 8;
    private readonly NativeBuffer _buffer = new(64 * 1024);
    private readonly Func<int, bool>? _resolveOwnerModule;
    private readonly Dictionary<(TransportProtocol, IpEndpointValue, IpEndpointValue, int, long), string?> _moduleCache = [];
    private int _moduleCachePass;

    /// <summary>Creates a reader.</summary>
    /// <param name="resolveOwnerModule">
    /// Optional predicate: rows owned by PIDs for which it returns true get their owning module resolved
    /// (useful for svchost.exe processes that host several services). Results are cached per connection.
    /// </param>
    public ConnectionTableReader(Func<int, bool>? resolveOwnerModule = null)
    {
        _resolveOwnerModule = resolveOwnerModule;
    }

    /// <inheritdoc />
    public void ReadTcp(List<TcpConnectionRow> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        TrimModuleCache();
        Fill(AF_INET, isTcp: true);
        ParseTcp4(destination);
        Fill(AF_INET6, isTcp: true);
        ParseTcp6(destination);
    }

    /// <inheritdoc />
    public void ReadUdp(List<UdpEndpointRow> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        Fill(AF_INET, isTcp: false);
        ParseUdp4(destination);
        Fill(AF_INET6, isTcp: false);
        ParseUdp6(destination);
    }

    /// <inheritdoc />
    public void Dispose() => _buffer.Dispose();

    /// <summary>Converts the DWORD port field of IP Helper rows (network order in the low 16 bits).</summary>
    public static ushort PortFromNative(uint value) => BinaryPrimitives.ReverseEndianness((ushort)(value & 0xFFFF));

    private void Fill(int family, bool isTcp)
    {
        uint size = _buffer.Size;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            uint error = isTcp
                ? GetExtendedTcpTable(_buffer.Pointer, &size, 0, (uint)family, TCP_TABLE_OWNER_MODULE_ALL, 0)
                : GetExtendedUdpTable(_buffer.Pointer, &size, 0, (uint)family, UDP_TABLE_OWNER_MODULE, 0);
            if (error == Win32Error.ERROR_SUCCESS)
            {
                return;
            }

            if (error != Win32Error.ERROR_INSUFFICIENT_BUFFER)
            {
                throw new NativeCallException(isTcp ? "GetExtendedTcpTable" : "GetExtendedUdpTable", error);
            }

            // The table can grow between calls: over-allocate by 25 % + 8 KB.
            _buffer.EnsureCapacity(size + (size / 4) + 8192);
            size = _buffer.Size;
        }

        throw new NativeCallException(isTcp ? "GetExtendedTcpTable" : "GetExtendedUdpTable", Win32Error.ERROR_INSUFFICIENT_BUFFER);
    }

    private uint RowCount(int rowSize)
    {
        uint count = *(uint*)_buffer.Pointer;
        ulong needed = 8 + ((ulong)count * (ulong)rowSize);
        if (needed > _buffer.Size)
        {
            throw new InvalidDataException($"IP Helper table reports {count} rows but the buffer holds {_buffer.Size} bytes.");
        }

        return count;
    }

    private void ParseTcp4(List<TcpConnectionRow> destination)
    {
        uint count = RowCount(sizeof(MIB_TCPROW_OWNER_MODULE));
        var rows = (MIB_TCPROW_OWNER_MODULE*)(_buffer.Pointer + 8);
        for (uint i = 0; i < count; i++)
        {
            MIB_TCPROW_OWNER_MODULE* row = &rows[i];
            var local = new IpEndpointValue(IpAddressValue.FromIPv4NetworkOrder(row->dwLocalAddr), PortFromNative(row->dwLocalPort));
            var state = TcpStateInfo.FromNative(row->dwState);
            var remote = new IpEndpointValue(
                IpAddressValue.FromIPv4NetworkOrder(row->dwRemoteAddr),
                state == TcpState.Listen ? (ushort)0 : PortFromNative(row->dwRemotePort));
            int pid = (int)row->dwOwningPid;
            string? module = null;
            if (pid != 0 && _resolveOwnerModule?.Invoke(pid) == true)
            {
                module = ResolveModule(TransportProtocol.Tcp, local, remote, pid, row->liCreateTimestamp, row, null, null, null);
            }

            destination.Add(new TcpConnectionRow(local, remote, state, pid, row->liCreateTimestamp, module));
        }
    }

    private void ParseTcp6(List<TcpConnectionRow> destination)
    {
        uint count = RowCount(sizeof(MIB_TCP6ROW_OWNER_MODULE));
        var rows = (MIB_TCP6ROW_OWNER_MODULE*)(_buffer.Pointer + 8);
        for (uint i = 0; i < count; i++)
        {
            MIB_TCP6ROW_OWNER_MODULE* row = &rows[i];
            var local = new IpEndpointValue(
                IpAddressValue.FromIPv6(new ReadOnlySpan<byte>(row->ucLocalAddr, 16), row->dwLocalScopeId),
                PortFromNative(row->dwLocalPort));
            var state = TcpStateInfo.FromNative(row->dwState);
            var remote = new IpEndpointValue(
                IpAddressValue.FromIPv6(new ReadOnlySpan<byte>(row->ucRemoteAddr, 16), row->dwRemoteScopeId),
                state == TcpState.Listen ? (ushort)0 : PortFromNative(row->dwRemotePort));
            int pid = (int)row->dwOwningPid;
            string? module = null;
            if (pid != 0 && _resolveOwnerModule?.Invoke(pid) == true)
            {
                module = ResolveModule(TransportProtocol.Tcp, local, remote, pid, row->liCreateTimestamp, null, row, null, null);
            }

            destination.Add(new TcpConnectionRow(local, remote, state, pid, row->liCreateTimestamp, module));
        }
    }

    private void ParseUdp4(List<UdpEndpointRow> destination)
    {
        uint count = RowCount(sizeof(MIB_UDPROW_OWNER_MODULE));
        var rows = (MIB_UDPROW_OWNER_MODULE*)(_buffer.Pointer + 8);
        for (uint i = 0; i < count; i++)
        {
            MIB_UDPROW_OWNER_MODULE* row = &rows[i];
            var local = new IpEndpointValue(IpAddressValue.FromIPv4NetworkOrder(row->dwLocalAddr), PortFromNative(row->dwLocalPort));
            int pid = (int)row->dwOwningPid;
            string? module = null;
            if (pid != 0 && _resolveOwnerModule?.Invoke(pid) == true)
            {
                module = ResolveModule(TransportProtocol.Udp, local, default, pid, row->liCreateTimestamp, null, null, row, null);
            }

            destination.Add(new UdpEndpointRow(local, pid, row->liCreateTimestamp, (row->dwFlags & 1) != 0, module));
        }
    }

    private void ParseUdp6(List<UdpEndpointRow> destination)
    {
        uint count = RowCount(sizeof(MIB_UDP6ROW_OWNER_MODULE));
        var rows = (MIB_UDP6ROW_OWNER_MODULE*)(_buffer.Pointer + 8);
        for (uint i = 0; i < count; i++)
        {
            MIB_UDP6ROW_OWNER_MODULE* row = &rows[i];
            var local = new IpEndpointValue(
                IpAddressValue.FromIPv6(new ReadOnlySpan<byte>(row->ucLocalAddr, 16), row->dwLocalScopeId),
                PortFromNative(row->dwLocalPort));
            int pid = (int)row->dwOwningPid;
            string? module = null;
            if (pid != 0 && _resolveOwnerModule?.Invoke(pid) == true)
            {
                module = ResolveModule(TransportProtocol.Udp, local, default, pid, row->liCreateTimestamp, null, null, null, row);
            }

            destination.Add(new UdpEndpointRow(local, pid, row->liCreateTimestamp, (row->dwFlags & 1) != 0, module));
        }
    }

    private string? ResolveModule(
        TransportProtocol protocol,
        IpEndpointValue local,
        IpEndpointValue remote,
        int pid,
        long created,
        MIB_TCPROW_OWNER_MODULE* tcp4,
        MIB_TCP6ROW_OWNER_MODULE* tcp6,
        MIB_UDPROW_OWNER_MODULE* udp4,
        MIB_UDP6ROW_OWNER_MODULE* udp6)
    {
        var key = (protocol, local, remote, pid, created);
        if (_moduleCache.TryGetValue(key, out string? cached))
        {
            return cached;
        }

        const uint bufferSize = 2048;
        byte* info = stackalloc byte[(int)bufferSize];
        uint size = bufferSize;
        uint error = tcp4 != null ? GetOwnerModuleFromTcpEntry(tcp4, TCPIP_OWNER_MODULE_INFO_BASIC, info, &size)
            : tcp6 != null ? GetOwnerModuleFromTcp6Entry(tcp6, TCPIP_OWNER_MODULE_INFO_BASIC, info, &size)
            : udp4 != null ? GetOwnerModuleFromUdpEntry(udp4, TCPIP_OWNER_MODULE_INFO_BASIC, info, &size)
            : GetOwnerModuleFromUdp6Entry(udp6, TCPIP_OWNER_MODULE_INFO_BASIC, info, &size);

        string? name = null;
        if (error == Win32Error.ERROR_SUCCESS)
        {
            var basic = (TCPIP_OWNER_MODULE_BASIC_INFO*)info;
            if (basic->pModuleName != null)
            {
                name = new string(basic->pModuleName);
            }
        }

        _moduleCache[key] = name;
        return name;
    }

    private void TrimModuleCache()
    {
        // Bounded cache: reset periodically; connections are re-resolved lazily.
        if (++_moduleCachePass >= 600 || _moduleCache.Count > 20_000)
        {
            _moduleCache.Clear();
            _moduleCachePass = 0;
        }
    }

    internal static int TableRowOffset => 8;

    internal static (int Tcp4, int Tcp6, int Udp4, int Udp6) RowSizes =>
        (sizeof(MIB_TCPROW_OWNER_MODULE), sizeof(MIB_TCP6ROW_OWNER_MODULE), sizeof(MIB_UDPROW_OWNER_MODULE), sizeof(MIB_UDP6ROW_OWNER_MODULE));

    internal static int SockAddrInetSize => Marshal.SizeOf<SOCKADDR_INET>();
}
