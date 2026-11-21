using System.Buffers.Binary;
using System.Net;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Platform.Windows.Etw;
using WinNetworkManager.Telemetry.Decoding;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Telemetry.Tests;

/// <summary>
/// Decoder tests with payloads built exactly like the Windows 11 26200 manifest (see docs/research). The schemas are
/// constructed explicitly, so these tests also prove the decoder relies on field names rather than fixed offsets.
/// </summary>
public sealed class DecoderTests
{
    private static TdhProperty U32(string name) => new(name, TdhInType.UInt32, 8, TdhPropertyFlags.None, 4, 1);

    private static TdhProperty U16(string name) => new(name, TdhInType.UInt16, 22, TdhPropertyFlags.None, 2, 1);

    private static TdhProperty V6(string name) => new(name, TdhInType.Binary, 24, TdhPropertyFlags.ParamFixedLength, 16, 1);

    private static TdhSchema RecvV4() => TdhSchema.FromProperties("TCPIP", "Recv", U32("PID"), U32("size"), U32("daddr"), U32("saddr"), U16("dport"), U16("sport"), U32("seqnum"), U32("connid"));

    private static TdhSchema SendV4() => TdhSchema.FromProperties("TCPIP", "Send", U32("PID"), U32("size"), U32("daddr"), U32("saddr"), U16("dport"), U16("sport"), U32("startime"), U32("endtime"), U32("seqnum"), U32("connid"));

    private static TdhSchema RecvV6() => TdhSchema.FromProperties("TCPIP", "Recv", U32("PID"), U32("size"), V6("daddr"), V6("saddr"), U16("dport"), U16("sport"), U32("seqnum"), U32("connid"));

    private static byte[] PayloadV4(int pid, int size, string daddr, string saddr, ushort dport, ushort sport, int trailingUInt32s)
    {
        var buffer = new byte[20 + (4 * trailingUInt32s)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, pid);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), size);
        IPAddress.Parse(daddr).GetAddressBytes().CopyTo(buffer, 8);
        IPAddress.Parse(saddr).GetAddressBytes().CopyTo(buffer, 12);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(16), dport);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(18), sport);
        for (int i = 0; i < trailingUInt32s; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20 + (4 * i)), (uint)(0xA0 + i));
        }

        return buffer;
    }

    [Fact]
    public void Tcp_receive_uses_daddr_as_remote()
    {
        var decoder = new KernelNetworkDecoder();
        byte[] payload = PayloadV4(1234, 1460, "93.184.216.34", "192.168.1.10", 443, 50123, 2);
        Assert.True(decoder.TryDecode(11, 0, 999, payload, RecvV4(), out NetworkEvent ev));
        Assert.Equal(NetworkEventKind.TcpReceive, ev.Kind);
        Assert.Equal(TransportProtocol.Tcp, ev.Protocol);
        Assert.Equal(1234, ev.Pid);
        Assert.Equal(1460, ev.Size);
        Assert.Equal(999, ev.Timestamp);
        Assert.Equal("93.184.216.34:443", ev.Remote.ToString());
        Assert.Equal("192.168.1.10:50123", ev.Local.ToString());
        Assert.Equal(0xA1u, ev.ConnectionId);
    }

    [Fact]
    public void Tcp_send_uses_daddr_as_remote_and_skips_extra_fields()
    {
        var decoder = new KernelNetworkDecoder();
        byte[] payload = PayloadV4(55, 512, "1.1.1.1", "10.0.0.2", 853, 60000, 4);
        Assert.True(decoder.TryDecode(10, 0, 1, payload, SendV4(), out NetworkEvent ev));
        Assert.Equal(NetworkEventKind.TcpSend, ev.Kind);
        Assert.Equal("1.1.1.1:853", ev.Remote.ToString());
        Assert.Equal("10.0.0.2:60000", ev.Local.ToString());
        Assert.Equal(0xA3u, ev.ConnectionId);
    }

    [Fact]
    public void Udp_receive_swaps_addresses()
    {
        // UDP receive: daddr is the local (destination) address, saddr the remote sender (verified empirically).
        var decoder = new KernelNetworkDecoder();
        byte[] payload = PayloadV4(77, 61, "10.252.203.8", "1.1.1.1", 57814, 53, 2);
        Assert.True(decoder.TryDecode(43, 0, 1, payload, RecvV4(), out NetworkEvent ev));
        Assert.Equal(NetworkEventKind.UdpReceive, ev.Kind);
        Assert.Equal(TransportProtocol.Udp, ev.Protocol);
        Assert.Equal("1.1.1.1:53", ev.Remote.ToString());
        Assert.Equal("10.252.203.8:57814", ev.Local.ToString());
    }

    [Fact]
    public void Udp_send_uses_daddr_as_remote()
    {
        var decoder = new KernelNetworkDecoder();
        byte[] payload = PayloadV4(77, 29, "1.1.1.1", "10.252.203.8", 53, 57814, 2);
        Assert.True(decoder.TryDecode(42, 0, 1, payload, RecvV4(), out NetworkEvent ev));
        Assert.Equal(NetworkEventKind.UdpSend, ev.Kind);
        Assert.Equal("1.1.1.1:53", ev.Remote.ToString());
    }

    [Fact]
    public void Tcp_ipv6_receive()
    {
        var payload = new byte[52];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 4321);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 65475);
        IPAddress.Parse("2606:4700::6810:84e5").GetAddressBytes().CopyTo(payload, 8);
        IPAddress.Parse("2a01:db8:1::5").GetAddressBytes().CopyTo(payload, 24);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(40), 443);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(42), 49999);

        var decoder = new KernelNetworkDecoder();
        Assert.True(decoder.TryDecode(27, 0, 1, payload, RecvV6(), out NetworkEvent ev));
        Assert.Equal("[2606:4700::6810:84e5]:443", ev.Remote.ToString());
        Assert.Equal("[2a01:db8:1::5]:49999", ev.Local.ToString());
        Assert.Equal(65475, ev.Size);
    }

    [Fact]
    public void Truncated_payload_is_rejected()
    {
        var decoder = new KernelNetworkDecoder();
        byte[] payload = PayloadV4(1, 100, "1.2.3.4", "5.6.7.8", 1, 2, 0)[..18];
        Assert.False(decoder.TryDecode(11, 0, 1, payload, RecvV4(), out _));
    }

    [Fact]
    public void Negative_size_is_rejected()
    {
        var decoder = new KernelNetworkDecoder();
        byte[] payload = PayloadV4(1, -5, "1.2.3.4", "5.6.7.8", 1, 2, 2);
        Assert.False(decoder.TryDecode(11, 0, 1, payload, RecvV4(), out _));
    }

    [Fact]
    public void Unknown_event_ids_are_counted_and_ignored()
    {
        var decoder = new KernelNetworkDecoder();
        Assert.False(decoder.TryDecode(99, 0, 1, new byte[64], RecvV4(), out _));
        Assert.Equal(1, decoder.UnknownEvents);
    }

    [Fact]
    public void Schema_missing_required_fields_is_unusable_not_misread()
    {
        var decoder = new KernelNetworkDecoder();
        var broken = TdhSchema.FromProperties("TCPIP", "Recv", U32("PID"), U32("size"), U32("daddr"));
        Assert.False(decoder.TryDecode(11, 0, 1, new byte[64], broken, out _));
        Assert.Equal(1, decoder.UnusableLayouts);
    }

    [Fact]
    public void Tcp_copy_events_are_classified_separately()
    {
        Assert.Equal(NetworkEventKind.TcpCopy, KernelNetworkDecoder.KindOf(18));
        Assert.Equal(NetworkEventKind.TcpCopy, KernelNetworkDecoder.KindOf(34));
        Assert.Equal(NetworkEventKind.TcpRetransmit, KernelNetworkDecoder.KindOf(14));
        Assert.Equal(NetworkEventKind.UdpReceive, KernelNetworkDecoder.KindOf(59));
    }

    [Fact]
    public void Process_start_with_sid_and_strings_is_decoded()
    {
        var schema = TdhSchema.FromProperties("ProcessStart", "Start",
            U32("ProcessID"),
            new TdhProperty("ProcessSequenceNumber", TdhInType.UInt64, 10, TdhPropertyFlags.None, 8, 1),
            new TdhProperty("CreateTime", TdhInType.FileTime, 2, TdhPropertyFlags.None, 8, 1),
            U32("ParentProcessID"),
            new TdhProperty("ParentProcessSequenceNumber", TdhInType.UInt64, 10, TdhPropertyFlags.None, 8, 1),
            U32("SessionID"),
            U32("Flags"),
            new TdhProperty("MandatoryLabel", TdhInType.Sid, 1, TdhPropertyFlags.None, 0, 1),
            new TdhProperty("ImageName", TdhInType.UnicodeString, 1, TdhPropertyFlags.None, 0, 1),
            U32("ImageChecksum"));

        var payload = new List<byte>();
        void Add32(uint v) => payload.AddRange(BitConverter.GetBytes(v));
        void Add64(ulong v) => payload.AddRange(BitConverter.GetBytes(v));
        Add32(4242);
        Add64(777);
        Add64(133_000_000_000_000_000);
        Add32(100);
        Add64(10);
        Add32(1);
        Add32(0);
        payload.AddRange([1, 1, 0, 0, 0, 0, 0, 16, 0x00, 0x20, 0x00, 0x00]); // S-1-16-8192 (medium)
        payload.AddRange(System.Text.Encoding.Unicode.GetBytes(@"\Device\HarddiskVolume3\Windows\notepad.exe" + "\0"));
        Add32(0xBEEF);

        var ev = KernelProcessDecoder.Decode(1, 555, payload.ToArray(), schema, pointerIs64Bit: true);
        Assert.NotNull(ev);
        Assert.Equal(ProcessLifecycleKind.Started, ev.Kind);
        Assert.Equal(4242, ev.Process.Pid);
        Assert.Equal(133_000_000_000_000_000, ev.Process.CreateTimeTicks);
        Assert.Equal(100, ev.ParentPid);
        Assert.Equal(1, ev.SessionId);
        Assert.Equal(777UL, ev.SequenceNumber);
        Assert.Equal(@"\Device\HarddiskVolume3\Windows\notepad.exe", ev.ImageName);
        Assert.Equal(555, ev.Timestamp);
    }

    [Fact]
    public void Process_payload_truncated_inside_string_is_rejected_gracefully()
    {
        var schema = TdhSchema.FromProperties("ProcessStart", "Start", U32("ProcessID"), new TdhProperty("CreateTime", TdhInType.FileTime, 2, TdhPropertyFlags.None, 8, 1), U32("Count"),
            new TdhProperty("Blob", TdhInType.Binary, 0, TdhPropertyFlags.ParamLength, 2, 1));
        var payload = new byte[4 + 8 + 4 + 3];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 1000); // claims 1000 bytes follow, only 3 do
        Assert.Null(KernelProcessDecoder.Decode(1, 0, payload, schema, true));
    }
}
