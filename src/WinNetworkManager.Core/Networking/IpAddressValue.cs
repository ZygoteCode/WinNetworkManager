using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WinNetworkManager.Core.Networking;

/// <summary>IP protocol version of an address.</summary>
public enum IpVersion : byte
{
    /// <summary>No address (default value).</summary>
    None = 0,

    /// <summary>IPv4.</summary>
    IPv4 = 4,

    /// <summary>IPv6.</summary>
    IPv6 = 6,
}

/// <summary>
/// Compact, allocation-free IPv4/IPv6 address used on hot paths (ETW decoding, connection tables).
/// The address is stored big-endian in two 64-bit halves; IPv4 occupies the low 32 bits of the low half.
/// </summary>
public readonly struct IpAddressValue : IEquatable<IpAddressValue>, IComparable<IpAddressValue>, IComparable
{
    private readonly ulong _hi;
    private readonly ulong _lo;
    private readonly uint _scopeId;
    private readonly IpVersion _version;

    private IpAddressValue(IpVersion version, ulong hi, ulong lo, uint scopeId)
    {
        _version = version;
        _hi = hi;
        _lo = lo;
        _scopeId = scopeId;
    }

    /// <summary>IPv4 0.0.0.0.</summary>
    public static IpAddressValue IPv4Any => new(IpVersion.IPv4, 0, 0, 0);

    /// <summary>IPv6 ::.</summary>
    public static IpAddressValue IPv6Any => new(IpVersion.IPv6, 0, 0, 0);

    /// <summary>IPv4 127.0.0.1.</summary>
    public static IpAddressValue IPv4Loopback => FromIPv4(127, 0, 0, 1);

    /// <summary>IPv6 ::1.</summary>
    public static IpAddressValue IPv6Loopback => new(IpVersion.IPv6, 0, 1, 0);

    /// <summary>Protocol version; <see cref="IpVersion.None"/> for the default value.</summary>
    public IpVersion Version => _version;

    /// <summary>IPv6 scope (zone) identifier; 0 for IPv4 or global scope.</summary>
    public uint ScopeId => _scopeId;

    /// <summary>True for IPv4.</summary>
    public bool IsIPv4 => _version == IpVersion.IPv4;

    /// <summary>True for IPv6.</summary>
    public bool IsIPv6 => _version == IpVersion.IPv6;

    /// <summary>True for the default (empty) value.</summary>
    public bool IsEmpty => _version == IpVersion.None;

    /// <summary>True for 0.0.0.0 or ::.</summary>
    public bool IsUnspecified => _version != IpVersion.None && _hi == 0 && _lo == 0;

    /// <summary>The IPv4 address as a host-order integer (a.b.c.d → a&lt;&lt;24 | …). Only meaningful for IPv4.</summary>
    public uint IPv4HostOrder => (uint)_lo;

    /// <summary>Upper 64 bits of an IPv6 address (big-endian).</summary>
    public ulong High => _hi;

    /// <summary>Lower 64 bits of an IPv6 address (big-endian) or the IPv4 address in the low 32 bits.</summary>
    public ulong Low => _lo;

    /// <summary>True when this is an IPv6 IPv4-mapped address (::ffff:a.b.c.d).</summary>
    public bool IsIPv4MappedToIPv6 => _version == IpVersion.IPv6 && _hi == 0 && (_lo >> 32) == 0xFFFF;

    /// <summary>Creates an IPv4 address from its host-order integer form.</summary>
    public static IpAddressValue FromIPv4HostOrder(uint hostOrder) => new(IpVersion.IPv4, 0, hostOrder, 0);

    /// <summary>Creates an IPv4 address from a value in network byte order, as found in Win32 structures.</summary>
    public static IpAddressValue FromIPv4NetworkOrder(uint networkOrder) =>
        FromIPv4HostOrder(BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(networkOrder) : networkOrder);

    /// <summary>Creates an IPv4 address from four octets.</summary>
    public static IpAddressValue FromIPv4(byte a, byte b, byte c, byte d) =>
        FromIPv4HostOrder(((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d);

    /// <summary>Creates an IPv6 address from its two big-endian halves.</summary>
    public static IpAddressValue FromIPv6(ulong high, ulong low, uint scopeId = 0) => new(IpVersion.IPv6, high, low, scopeId);

    /// <summary>Creates an IPv6 address from 16 network-order bytes.</summary>
    public static IpAddressValue FromIPv6(ReadOnlySpan<byte> bytes, uint scopeId = 0)
    {
        if (bytes.Length != 16)
        {
            throw new ArgumentException("An IPv6 address must be exactly 16 bytes.", nameof(bytes));
        }

        return new IpAddressValue(
            IpVersion.IPv6,
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
            scopeId);
    }

    /// <summary>Converts from <see cref="IPAddress"/>.</summary>
    public static IpAddressValue FromIPAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        Span<byte> buffer = stackalloc byte[16];
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            address.TryWriteBytes(buffer, out _);
            return FromIPv4(buffer[0], buffer[1], buffer[2], buffer[3]);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            address.TryWriteBytes(buffer, out _);
            return FromIPv6(buffer, (uint)address.ScopeId);
        }

        throw new ArgumentException($"Unsupported address family {address.AddressFamily}.", nameof(address));
    }

    /// <summary>Parses an IPv4 or IPv6 literal (IPv6 may carry a %zone suffix).</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out IpAddressValue value)
    {
        text = text.Trim();
        if (!text.IsEmpty && IPAddress.TryParse(text, out var address) &&
            address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
        {
            // IPAddress.TryParse accepts shorthand such as "1" or "1.2"; require canonical dotted quads for IPv4.
            if (address.AddressFamily == AddressFamily.InterNetwork && text.Count('.') != 3)
            {
                value = default;
                return false;
            }

            value = FromIPAddress(address);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Returns the embedded IPv4 address for IPv4-mapped IPv6 addresses, otherwise this value.</summary>
    public IpAddressValue UnmapIPv4() => IsIPv4MappedToIPv6 ? FromIPv4HostOrder((uint)_lo) : this;

    /// <summary>Returns the same address without IPv6 scope identifier.</summary>
    public IpAddressValue WithoutScope() => new(_version, _hi, _lo, 0);

    /// <summary>Writes the network-order bytes (4 for IPv4, 16 for IPv6) and returns the count.</summary>
    public int WriteBytes(Span<byte> destination)
    {
        switch (_version)
        {
            case IpVersion.IPv4:
                BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)_lo);
                return 4;
            case IpVersion.IPv6:
                BinaryPrimitives.WriteUInt64BigEndian(destination, _hi);
                BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _lo);
                return 16;
            default:
                return 0;
        }
    }

    /// <summary>Converts to <see cref="IPAddress"/> (allocates).</summary>
    public IPAddress ToIPAddress()
    {
        Span<byte> buffer = stackalloc byte[16];
        int length = WriteBytes(buffer);
        return _version switch
        {
            IpVersion.IPv4 => new IPAddress(buffer[..length]),
            IpVersion.IPv6 => new IPAddress(buffer[..length], _scopeId),
            _ => IPAddress.None,
        };
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (_version == IpVersion.IPv4)
        {
            uint v = (uint)_lo;
            return string.Create(CultureInfo.InvariantCulture, $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}");
        }

        return _version == IpVersion.IPv6 ? ToIPAddress().ToString() : string.Empty;
    }

    /// <inheritdoc />
    public bool Equals(IpAddressValue other) =>
        _version == other._version && _hi == other._hi && _lo == other._lo && _scopeId == other._scopeId;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is IpAddressValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_version, _hi, _lo, _scopeId);

    /// <summary>Orders IPv4 before IPv6, then numerically.</summary>
    public int CompareTo(IpAddressValue other)
    {
        int c = _version.CompareTo(other._version);
        if (c != 0)
        {
            return c;
        }

        c = _hi.CompareTo(other._hi);
        if (c != 0)
        {
            return c;
        }

        c = _lo.CompareTo(other._lo);
        return c != 0 ? c : _scopeId.CompareTo(other._scopeId);
    }

    /// <summary>Non-generic comparison (used by sort helpers); null sorts first.</summary>
    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        IpAddressValue other => CompareTo(other),
        _ => throw new ArgumentException("The object is not an IpAddressValue.", nameof(obj)),
    };

    /// <summary>Equality operator.</summary>
    public static bool operator ==(IpAddressValue left, IpAddressValue right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(IpAddressValue left, IpAddressValue right) => !left.Equals(right);

    /// <summary>Ordering operator.</summary>
    public static bool operator <(IpAddressValue left, IpAddressValue right) => left.CompareTo(right) < 0;

    /// <summary>Ordering operator.</summary>
    public static bool operator >(IpAddressValue left, IpAddressValue right) => left.CompareTo(right) > 0;

    /// <summary>Ordering operator.</summary>
    public static bool operator <=(IpAddressValue left, IpAddressValue right) => left.CompareTo(right) <= 0;

    /// <summary>Ordering operator.</summary>
    public static bool operator >=(IpAddressValue left, IpAddressValue right) => left.CompareTo(right) >= 0;
}
