using System.Buffers.Binary;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Platform.Windows.Etw;
using WinNetworkManager.Platform.Windows.Interop;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Telemetry.Decoding;

/// <summary>Field offsets of one Kernel-Network event version, resolved from its TDH schema.</summary>
internal sealed record NetworkEventLayout(
    NetworkEventKind Kind,
    int PidOffset,
    int SizeOffset,
    int DaddrOffset,
    int SaddrOffset,
    int DportOffset,
    int SportOffset,
    int ConnIdOffset,
    int AddressLength,
    int MinimumLength)
{
    /// <summary>True when the layout carries endpoints.</summary>
    public bool HasEndpoints => DaddrOffset >= 0 && SaddrOffset >= 0 && DportOffset >= 0 && SportOffset >= 0 && AddressLength is 4 or 16;

    /// <summary>Builds a layout from a schema; returns null when a required field is missing or variable-length.</summary>
    public static NetworkEventLayout? FromSchema(NetworkEventKind kind, TdhSchema schema, bool pointerIs64Bit)
    {
        int[] offsets = schema.ComputeFixedOffsets(pointerIs64Bit);
        int Off(string name)
        {
            int index = schema.IndexOf(name);
            return index < 0 ? -1 : offsets[index];
        }

        int Size(string name)
        {
            int index = schema.IndexOf(name);
            return index < 0 ? -1 : TdhSchema.FixedSizeOf(schema.Properties[index], pointerIs64Bit);
        }

        int pid = Off("PID");
        if (pid < 0 || Size("PID") != 4)
        {
            return null;
        }

        int size = Size("size") == 4 ? Off("size") : -1;
        int daddr = Off("daddr");
        int saddr = Off("saddr");
        int addressLength = Size("daddr");
        if (addressLength != Size("saddr"))
        {
            daddr = saddr = -1;
        }

        int dport = Size("dport") == 2 ? Off("dport") : -1;
        int sport = Size("sport") == 2 ? Off("sport") : -1;
        int connId = Size("connid") == 4 ? Off("connid") : -1;

        int minimum = new[] { pid + 4, size + 4, daddr + addressLength, saddr + addressLength, dport + 2, sport + 2 }.Max();
        bool needsEndpoints = kind is NetworkEventKind.TcpSend or NetworkEventKind.TcpReceive or NetworkEventKind.UdpSend or NetworkEventKind.UdpReceive or NetworkEventKind.TcpRetransmit;
        var layout = new NetworkEventLayout(kind, pid, size, daddr, saddr, dport, sport, connId, addressLength, minimum);
        if (needsEndpoints && (!layout.HasEndpoints || size < 0))
        {
            return null;
        }

        return layout;
    }
}

/// <summary>
/// Decodes Microsoft-Windows-Kernel-Network events without allocations. Field offsets come from the TDH schema of
/// each (id, version), resolved once. Address semantics (see docs/research/ETW-KERNEL-NETWORK.md):
/// TCP: daddr = remote, saddr = local. UDP send: daddr = remote. UDP receive: daddr = local, saddr = remote.
/// Not thread-safe: used only on the consumer thread.
/// </summary>
internal sealed unsafe class KernelNetworkDecoder
{
    /// <summary>Provider GUID.</summary>
    public static readonly Guid ProviderId = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    /// <summary>IPv4 keyword.</summary>
    public const ulong KeywordIPv4 = 0x10;

    /// <summary>IPv6 keyword.</summary>
    public const ulong KeywordIPv6 = 0x20;

    private readonly Dictionary<int, NetworkEventLayout?> _layouts = [];

    /// <summary>Events whose id is not a known Kernel-Network event.</summary>
    public long UnknownEvents { get; private set; }

    /// <summary>Event versions whose schema lacked required fields.</summary>
    public int UnusableLayouts { get; private set; }

    /// <summary>Maps an event id to its kind.</summary>
    public static NetworkEventKind KindOf(ushort id) => id switch
    {
        10 or 26 => NetworkEventKind.TcpSend,
        11 or 27 => NetworkEventKind.TcpReceive,
        12 or 28 => NetworkEventKind.TcpConnect,
        13 or 29 => NetworkEventKind.TcpDisconnect,
        14 or 30 => NetworkEventKind.TcpRetransmit,
        15 or 31 => NetworkEventKind.TcpAccept,
        16 or 32 => NetworkEventKind.TcpReconnect,
        17 => NetworkEventKind.TcpFail,
        18 or 34 => NetworkEventKind.TcpCopy,
        42 or 58 => NetworkEventKind.UdpSend,
        43 or 59 => NetworkEventKind.UdpReceive,
        49 => NetworkEventKind.UdpFail,
        _ => NetworkEventKind.Unknown,
    };

    /// <summary>Decodes a live event record.</summary>
    public bool TryDecode(EtwNative.EVENT_RECORD* record, out NetworkEvent ev)
    {
        ushort id = record->EventHeader.EventDescriptor.Id;
        byte version = record->EventHeader.EventDescriptor.Version;
        int key = (id << 8) | version;
        if (!_layouts.TryGetValue(key, out var layout))
        {
            layout = ResolveLayout(id, record);
            _layouts[key] = layout;
        }

        if (layout is null)
        {
            ev = default;
            return false;
        }

        var payload = new ReadOnlySpan<byte>((void*)record->UserData, record->UserDataLength);
        return Decode(layout, record->EventHeader.TimeStamp, payload, out ev);
    }

    /// <summary>Decodes a payload with an explicit schema (tests and replay).</summary>
    public bool TryDecode(ushort id, byte version, long timestamp, ReadOnlySpan<byte> payload, TdhSchema schema, out NetworkEvent ev)
    {
        int key = (id << 8) | version;
        if (!_layouts.TryGetValue(key, out var layout))
        {
            var kind = KindOf(id);
            layout = kind == NetworkEventKind.Unknown ? null : NetworkEventLayout.FromSchema(kind, schema, pointerIs64Bit: true);
            if (kind == NetworkEventKind.Unknown)
            {
                UnknownEvents++;
            }
            else if (layout is null)
            {
                UnusableLayouts++;
            }

            _layouts[key] = layout;
        }

        if (layout is null)
        {
            ev = default;
            return false;
        }

        return Decode(layout, timestamp, payload, out ev);
    }

    private NetworkEventLayout? ResolveLayout(ushort id, EtwNative.EVENT_RECORD* record)
    {
        var kind = KindOf(id);
        if (kind == NetworkEventKind.Unknown)
        {
            UnknownEvents++;
            return null;
        }

        if (!TdhSchema.TryRead(record, out var schema, out _) || schema is null)
        {
            UnusableLayouts++;
            return null;
        }

        bool is64 = (record->EventHeader.Flags & EtwNative.EVENT_HEADER_FLAG_32_BIT_HEADER) == 0;
        var layout = NetworkEventLayout.FromSchema(kind, schema, is64);
        if (layout is null)
        {
            UnusableLayouts++;
        }

        return layout;
    }

    internal static bool Decode(NetworkEventLayout layout, long timestamp, ReadOnlySpan<byte> payload, out NetworkEvent ev)
    {
        ev = default;
        if (payload.Length < layout.MinimumLength)
        {
            return false;
        }

        ev.Timestamp = timestamp;
        ev.Kind = layout.Kind;
        ev.Pid = BinaryPrimitives.ReadInt32LittleEndian(payload[layout.PidOffset..]);
        ev.Size = layout.SizeOffset >= 0 ? BinaryPrimitives.ReadInt32LittleEndian(payload[layout.SizeOffset..]) : 0;
        if (ev.Size < 0)
        {
            return false;
        }

        if (layout.ConnIdOffset >= 0 && layout.ConnIdOffset + 4 <= payload.Length)
        {
            ev.ConnectionId = BinaryPrimitives.ReadUInt32LittleEndian(payload[layout.ConnIdOffset..]);
        }

        if (!layout.HasEndpoints)
        {
            return true;
        }

        IpAddressValue daddr = ReadAddress(payload, layout.DaddrOffset, layout.AddressLength);
        IpAddressValue saddr = ReadAddress(payload, layout.SaddrOffset, layout.AddressLength);
        ushort dport = BinaryPrimitives.ReadUInt16BigEndian(payload[layout.DportOffset..]);
        ushort sport = BinaryPrimitives.ReadUInt16BigEndian(payload[layout.SportOffset..]);

        if (layout.Kind == NetworkEventKind.UdpReceive)
        {
            ev.Local = new IpEndpointValue(daddr, dport);
            ev.Remote = new IpEndpointValue(saddr, sport);
        }
        else
        {
            ev.Local = new IpEndpointValue(saddr, sport);
            ev.Remote = new IpEndpointValue(daddr, dport);
        }

        return true;
    }

    private static IpAddressValue ReadAddress(ReadOnlySpan<byte> payload, int offset, int length) => length == 4
        ? IpAddressValue.FromIPv4NetworkOrder(BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]))
        : IpAddressValue.FromIPv6(payload.Slice(offset, 16));
}
