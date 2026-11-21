using WinNetworkManager.Core.Networking;

namespace WinNetworkManager.Core.Tests.Networking;

public sealed class AddressClassifierTests
{
    [Theory]
    [InlineData("0.0.0.0", AddressScope.Unspecified)]
    [InlineData("0.1.2.3", AddressScope.Reserved)]
    [InlineData("127.0.0.1", AddressScope.Loopback)]
    [InlineData("127.255.255.254", AddressScope.Loopback)]
    [InlineData("10.20.30.40", AddressScope.Private)]
    [InlineData("172.16.0.1", AddressScope.Private)]
    [InlineData("172.31.255.255", AddressScope.Private)]
    [InlineData("172.32.0.1", AddressScope.Public)]
    [InlineData("172.15.255.255", AddressScope.Public)]
    [InlineData("192.168.0.1", AddressScope.Private)]
    [InlineData("100.64.0.1", AddressScope.SharedAddressSpace)]
    [InlineData("100.127.255.255", AddressScope.SharedAddressSpace)]
    [InlineData("100.128.0.1", AddressScope.Public)]
    [InlineData("169.254.10.10", AddressScope.LinkLocal)]
    [InlineData("192.0.2.5", AddressScope.Documentation)]
    [InlineData("198.51.100.7", AddressScope.Documentation)]
    [InlineData("203.0.113.9", AddressScope.Documentation)]
    [InlineData("192.0.0.8", AddressScope.Reserved)]
    [InlineData("198.18.0.1", AddressScope.Reserved)]
    [InlineData("198.19.255.255", AddressScope.Reserved)]
    [InlineData("224.0.0.251", AddressScope.Multicast)]
    [InlineData("239.255.255.250", AddressScope.Multicast)]
    [InlineData("240.0.0.1", AddressScope.Reserved)]
    [InlineData("255.255.255.255", AddressScope.Broadcast)]
    [InlineData("8.8.8.8", AddressScope.Public)]
    [InlineData("1.1.1.1", AddressScope.Public)]
    public void Classifies_ipv4(string address, AddressScope expected)
    {
        Assert.True(IpAddressValue.TryParse(address, out var value));
        Assert.Equal(expected, AddressClassifier.Classify(value));
    }

    [Theory]
    [InlineData("::", AddressScope.Unspecified)]
    [InlineData("::1", AddressScope.Loopback)]
    [InlineData("::ffff:127.0.0.1", AddressScope.Loopback)]
    [InlineData("::ffff:192.168.1.1", AddressScope.Private)]
    [InlineData("::ffff:8.8.8.8", AddressScope.Public)]
    [InlineData("fe80::1", AddressScope.LinkLocal)]
    [InlineData("febf::1", AddressScope.LinkLocal)]
    [InlineData("fc00::1", AddressScope.Private)]
    [InlineData("fd12:3456::1", AddressScope.Private)]
    [InlineData("fec0::1", AddressScope.Private)]
    [InlineData("ff02::1", AddressScope.Multicast)]
    [InlineData("ff05::1:3", AddressScope.Multicast)]
    [InlineData("2001:db8::1", AddressScope.Documentation)]
    [InlineData("2001:4860:4860::8888", AddressScope.Public)]
    [InlineData("2606:4700:4700::1111", AddressScope.Public)]
    [InlineData("2001:0:4136:e378::1", AddressScope.Public)]
    [InlineData("2002:c000:204::1", AddressScope.Public)]
    [InlineData("64:ff9b::808:808", AddressScope.Public)]
    [InlineData("100::1", AddressScope.Reserved)]
    [InlineData("::2", AddressScope.Reserved)]
    [InlineData("4000::1", AddressScope.Reserved)]
    public void Classifies_ipv6(string address, AddressScope expected)
    {
        Assert.True(IpAddressValue.TryParse(address, out var value));
        Assert.Equal(expected, AddressClassifier.Classify(value));
    }

    [Fact]
    public void Default_address_has_no_scope()
    {
        Assert.Equal(AddressScope.None, AddressClassifier.Classify(default));
    }

    [Fact]
    public void Local_network_scopes()
    {
        Assert.True(AddressClassifier.IsLocalNetwork(AddressScope.Private));
        Assert.True(AddressClassifier.IsLocalNetwork(AddressScope.LinkLocal));
        Assert.False(AddressClassifier.IsLocalNetwork(AddressScope.Public));
        Assert.False(AddressClassifier.IsLocalNetwork(AddressScope.Loopback));
    }
}
