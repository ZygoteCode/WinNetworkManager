using System.Runtime.InteropServices;
using WinNetworkManager.Platform.Windows.Interop;
using WinNetworkManager.Platform.Windows.IpHelper;
using WinNetworkManager.Platform.Windows.Processes;

namespace WinNetworkManager.Platform.Tests;

/// <summary>
/// Native structure sizes must match the Windows SDK headers exactly; a mismatch silently corrupts every row.
/// Expected values are the 64-bit sizes from the SDK (x64 and ARM64 share them).
/// </summary>
public sealed unsafe class NativeLayoutTests
{
    [Fact]
    public void Process_is_64_bit()
    {
        Assert.True(Environment.Is64BitProcess, "WinNetworkManager supports 64-bit processes only (x64, ARM64).");
    }

    [Fact]
    public void Etw_structures_match_sdk()
    {
        Assert.Equal(48, sizeof(EtwNative.WNODE_HEADER));
        Assert.Equal(120, sizeof(EtwNative.EVENT_TRACE_PROPERTIES));
        Assert.Equal(80, sizeof(EtwNative.EVENT_HEADER));
        Assert.Equal(112, sizeof(EtwNative.EVENT_RECORD));
        Assert.Equal(88, sizeof(EtwNative.EVENT_TRACE));
        Assert.Equal(172, sizeof(EtwNative.TIME_ZONE_INFORMATION));
        Assert.Equal(280, sizeof(EtwNative.TRACE_LOGFILE_HEADER));
        Assert.Equal(448, sizeof(EtwNative.EVENT_TRACE_LOGFILEW));
        Assert.Equal(104, Marshal.OffsetOf<EtwNative.EVENT_RECORD>(nameof(EtwNative.EVENT_RECORD.UserContext)).ToInt32());
        Assert.Equal(440, Marshal.OffsetOf<EtwNative.EVENT_TRACE_LOGFILEW>(nameof(EtwNative.EVENT_TRACE_LOGFILEW.Context)).ToInt32());
        Assert.Equal(424, Marshal.OffsetOf<EtwNative.EVENT_TRACE_LOGFILEW>(nameof(EtwNative.EVENT_TRACE_LOGFILEW.EventRecordCallback)).ToInt32());
    }

    [Fact]
    public void IpHelper_structures_match_sdk()
    {
        var (tcp4, tcp6, udp4, udp6) = ConnectionTableReader.RowSizes;
        Assert.Equal(160, tcp4);
        Assert.Equal(192, tcp6);
        Assert.Equal(160, udp4);
        Assert.Equal(176, udp6);
        Assert.Equal(1352, InterfaceTableReader.RowSize);
        Assert.Equal(28, ConnectionTableReader.SockAddrInetSize);
        Assert.Equal(104, RouteTableReader.RowSize);
        Assert.Equal(168, sizeof(IpHlpNative.MIB_IPINTERFACE_ROW));
        Assert.Equal(1208, Marshal.OffsetOf<IpHlpNative.MIB_IF_ROW2>(nameof(IpHlpNative.MIB_IF_ROW2.InOctets)).ToInt32());
        Assert.Equal(1280, Marshal.OffsetOf<IpHlpNative.MIB_IF_ROW2>(nameof(IpHlpNative.MIB_IF_ROW2.OutOctets)).ToInt32());
        Assert.Equal(148, Marshal.OffsetOf<IpHlpNative.MIB_IPINTERFACE_ROW>(nameof(IpHlpNative.MIB_IPINTERFACE_ROW.Metric)).ToInt32());
    }

    [Fact]
    public void Process_and_trust_structures_match_sdk()
    {
        Assert.Equal(568, ProcessSnapshotReader.EntrySize);
        Assert.Equal(80, sizeof(ProcessNative.PROCESS_MEMORY_COUNTERS_EX));
        var (data, file, catalog) = AuthenticodeVerifier.StructSizes;
        Assert.Equal(88, data);
        Assert.Equal(32, file);
        Assert.Equal(72, catalog);
        Assert.Equal(56, sizeof(ProcessNative.ENUM_SERVICE_STATUS_PROCESSW)); // 2 pointers + 9 DWORDs, padded to 8
    }

    [Theory]
    [InlineData(0x5000u, 80)]       // 0x0050 in network order → stored as 0x5000 in the low word
    [InlineData(0xBB01u, 443)]
    [InlineData(0x5BC3u, 50_011)]   // 50011 = 0xC35B; high port: must not become negative (v1 bug)
    [InlineData(0xFFFFu, 65_535)]
    [InlineData(0x0000u, 0)]
    public void Ports_convert_from_network_order(uint native, int expected)
    {
        Assert.Equal(expected, ConnectionTableReader.PortFromNative(native));
    }
}
