using System.Globalization;

namespace WinNetworkManager.Core.Networking;

/// <summary>An IP address and a port (host byte order).</summary>
public readonly record struct IpEndpointValue(IpAddressValue Address, ushort Port) : IComparable<IpEndpointValue>
{
    /// <summary>True when no address is set.</summary>
    public bool IsEmpty => Address.IsEmpty;

    /// <summary>Formats as <c>1.2.3.4:443</c> or <c>[2001:db8::1]:443</c>.</summary>
    public override string ToString() => Format(Address, Port);

    /// <summary>Formats an address and port using bracket notation for IPv6.</summary>
    public static string Format(IpAddressValue address, ushort port)
    {
        if (address.IsEmpty)
        {
            return string.Empty;
        }

        string host = address.ToString();
        return address.IsIPv6
            ? string.Create(CultureInfo.InvariantCulture, $"[{host}]:{port}")
            : string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");
    }

    /// <summary>Parses <c>host:port</c> or <c>[v6]:port</c>.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out IpEndpointValue endpoint)
    {
        endpoint = default;
        text = text.Trim();
        if (text.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<char> host;
        ReadOnlySpan<char> port;
        if (text[0] == '[')
        {
            int close = text.IndexOf(']');
            if (close < 0 || close + 2 > text.Length || text[close + 1] != ':')
            {
                return false;
            }

            host = text[1..close];
            port = text[(close + 2)..];
        }
        else
        {
            int colon = text.LastIndexOf(':');
            if (colon <= 0 || text[..colon].Contains(':'))
            {
                return false;
            }

            host = text[..colon];
            port = text[(colon + 1)..];
        }

        if (!ushort.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out ushort p) ||
            !IpAddressValue.TryParse(host, out var address))
        {
            return false;
        }

        endpoint = new IpEndpointValue(address, p);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(IpEndpointValue other)
    {
        int c = Address.CompareTo(other.Address);
        return c != 0 ? c : Port.CompareTo(other.Port);
    }

    /// <summary>Ordering operator.</summary>
    public static bool operator <(IpEndpointValue left, IpEndpointValue right) => left.CompareTo(right) < 0;

    /// <summary>Ordering operator.</summary>
    public static bool operator >(IpEndpointValue left, IpEndpointValue right) => left.CompareTo(right) > 0;

    /// <summary>Ordering operator.</summary>
    public static bool operator <=(IpEndpointValue left, IpEndpointValue right) => left.CompareTo(right) <= 0;

    /// <summary>Ordering operator.</summary>
    public static bool operator >=(IpEndpointValue left, IpEndpointValue right) => left.CompareTo(right) >= 0;
}
