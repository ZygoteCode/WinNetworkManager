using System.Net;
using WinNetworkManager.Core.Networking;

namespace WinNetworkManager.Core.Tests.Networking;

public sealed class IpAddressTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.254")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void RoundTrips_through_IPAddress(string text)
    {
        var expected = IPAddress.Parse(text);
        Assert.True(IpAddressValue.TryParse(text, out var value));
        Assert.Equal(expected, value.ToIPAddress());
        Assert.Equal(expected.ToString(), value.ToString());
    }

    [Fact]
    public void IPv4_from_network_order_matches_win32_layout()
    {
        // 192.168.1.2 in a MIB_TCPROW dwLocalAddr is stored as bytes C0 A8 01 02 (network order).
        uint networkOrder = BitConverter.ToUInt32([0xC0, 0xA8, 0x01, 0x02]);
        var value = IpAddressValue.FromIPv4NetworkOrder(networkOrder);
        Assert.Equal("192.168.1.2", value.ToString());
        Assert.Equal(0xC0A80102u, value.IPv4HostOrder);
    }

    [Fact]
    public void Rejects_shorthand_ipv4_literals()
    {
        Assert.False(IpAddressValue.TryParse("1", out _));
        Assert.False(IpAddressValue.TryParse("1.2", out _));
        Assert.False(IpAddressValue.TryParse("not-an-ip", out _));
        Assert.False(IpAddressValue.TryParse("", out _));
    }

    [Fact]
    public void IPv4_mapped_addresses_unmap_to_IPv4()
    {
        Assert.True(IpAddressValue.TryParse("::ffff:8.8.4.4", out var mapped));
        Assert.True(mapped.IsIPv4MappedToIPv6);
        var unmapped = mapped.UnmapIPv4();
        Assert.True(unmapped.IsIPv4);
        Assert.Equal("8.8.4.4", unmapped.ToString());
    }

    [Fact]
    public void Scope_id_participates_in_equality_and_can_be_removed()
    {
        var a = IpAddressValue.FromIPv6(IPAddress.Parse("fe80::1").GetAddressBytes(), 5);
        var b = IpAddressValue.FromIPv6(IPAddress.Parse("fe80::1").GetAddressBytes(), 7);
        Assert.NotEqual(a, b);
        Assert.Equal(a.WithoutScope(), b.WithoutScope());
        Assert.Equal("fe80::1%5", a.ToString());
    }

    [Fact]
    public void Ordering_puts_ipv4_before_ipv6_and_is_numeric()
    {
        Assert.True(IpAddressValue.TryParse("10.0.0.2", out var a));
        Assert.True(IpAddressValue.TryParse("10.0.0.10", out var b));
        Assert.True(IpAddressValue.TryParse("::1", out var c));
        Assert.True(a < b);
        Assert.True(b < c);
    }

    [Fact]
    public void Default_value_is_empty()
    {
        IpAddressValue value = default;
        Assert.True(value.IsEmpty);
        Assert.Equal(string.Empty, value.ToString());
        Assert.False(value.IsUnspecified);
    }

    [Theory]
    [InlineData("1.2.3.4:443", "1.2.3.4", 443)]
    [InlineData("[2001:db8::1]:8080", "2001:db8::1", 8080)]
    [InlineData("[::1]:0", "::1", 0)]
    [InlineData(" 10.0.0.1:65535 ", "10.0.0.1", 65535)]
    public void Endpoint_parse_and_format(string text, string address, int port)
    {
        Assert.True(IpEndpointValue.TryParse(text, out var endpoint));
        Assert.Equal(address, endpoint.Address.ToString());
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(text.Trim(), endpoint.ToString());
    }

    [Theory]
    [InlineData("2001:db8::1:443")]
    [InlineData("1.2.3.4:70000")]
    [InlineData("1.2.3.4:")]
    [InlineData("[::1]443")]
    [InlineData(":443")]
    public void Endpoint_parse_rejects_ambiguous_or_invalid_text(string text)
    {
        Assert.False(IpEndpointValue.TryParse(text, out _));
    }

    [Fact]
    public void High_ports_are_not_negative()
    {
        // Regression for v1 bug N1: ports >= 32768 became negative.
        var endpoint = new IpEndpointValue(IpAddressValue.FromIPv4(10, 0, 0, 1), 50123);
        Assert.Equal("10.0.0.1:50123", endpoint.ToString());
    }
}
