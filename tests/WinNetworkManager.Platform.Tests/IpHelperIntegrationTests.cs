using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Platform.Windows.IpHelper;
using TcpState = WinNetworkManager.Core.Networking.TcpState;

namespace WinNetworkManager.Platform.Tests;

/// <summary>Integration tests against the live IP Helper API of the test machine.</summary>
public sealed class IpHelperIntegrationTests
{
    [Fact]
    public async Task Tcp_table_contains_our_listener_and_connection_with_correct_owner_ipv4_and_ipv6()
    {
        using var listener4 = new TcpListener(IPAddress.Loopback, 0);
        using var listener6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener4.Start();
        listener6.Start();
        int port4 = ((IPEndPoint)listener4.LocalEndpoint).Port;
        int port6 = ((IPEndPoint)listener6.LocalEndpoint).Port;

        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port4, TestContext.Current.CancellationToken);
        using var accepted = await listener4.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        using var reader = new ConnectionTableReader();
        var rows = new List<TcpConnectionRow>();
        reader.ReadTcp(rows);

        int me = Environment.ProcessId;
        var l4 = Assert.Single(rows, r => r.Local.Port == port4 && r.State == TcpState.Listen && r.Local.Address.IsIPv4);
        Assert.Equal(me, l4.OwningPid);
        Assert.Equal("127.0.0.1", l4.Local.Address.ToString());
        Assert.True(l4.Remote.Address.IsUnspecified);

        var l6 = Assert.Single(rows, r => r.Local.Port == port6 && r.State == TcpState.Listen && r.Local.Address.IsIPv6);
        Assert.Equal(me, l6.OwningPid);
        Assert.Equal("::1", l6.Local.Address.ToString());

        var established = Assert.Single(rows, r => r.Local.Port == clientPort && r.Remote.Port == port4);
        Assert.Equal(TcpState.Established, established.State);
        Assert.Equal(me, established.OwningPid);
        Assert.True(established.CreateTimestamp > 0, "liCreateTimestamp should be populated for live connections.");
        var created = DateTime.FromFileTimeUtc(established.CreateTimestamp);
        Assert.InRange(created, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void Tcp_table_matches_dotnet_view_of_listeners()
    {
        using var reader = new ConnectionTableReader();
        var rows = new List<TcpConnectionRow>();
        reader.ReadTcp(rows);
        var ours = rows.Where(r => r.State == TcpState.Listen)
            .Select(r => (r.Local.Address.WithoutScope().ToString(), (int)r.Local.Port))
            .ToHashSet();
        var dotnet = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Select(e => (IpAddressValue.FromIPAddress(e.Address).WithoutScope().ToString(), e.Port))
            .ToHashSet();

        // The tables are read at slightly different instants: allow a little churn but require near equality.
        int missing = dotnet.Count(e => !ours.Contains(e));
        Assert.True(missing <= 2, $"{missing} listeners known to .NET were not reported (of {dotnet.Count}).");
    }

    [Fact]
    public void Udp_table_contains_our_sockets_ipv4_and_ipv6()
    {
        using var u4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var u6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        int p4 = ((IPEndPoint)u4.Client.LocalEndPoint!).Port;
        int p6 = ((IPEndPoint)u6.Client.LocalEndPoint!).Port;

        using var reader = new ConnectionTableReader();
        var rows = new List<UdpEndpointRow>();
        reader.ReadUdp(rows);
        Assert.Contains(rows, r => r.Local.Port == p4 && r.Local.Address.IsIPv4 && r.OwningPid == Environment.ProcessId);
        Assert.Contains(rows, r => r.Local.Port == p6 && r.Local.Address.IsIPv6 && r.OwningPid == Environment.ProcessId);
    }

    [Fact]
    public void Reader_is_reusable_and_stable_across_many_polls()
    {
        using var reader = new ConnectionTableReader();
        var tcp = new List<TcpConnectionRow>();
        var udp = new List<UdpEndpointRow>();
        for (int i = 0; i < 50; i++)
        {
            reader.ReadTcp(tcp);
            reader.ReadUdp(udp);
        }

        Assert.NotEmpty(tcp);
        Assert.NotEmpty(udp);
    }

    [Fact]
    public void Owner_module_resolution_names_svchost_services()
    {
        var services = Windows.Processes.ServiceMap.Read();
        var multiHost = services.Where(kv => kv.Value.Count >= 1).Select(kv => kv.Key).ToHashSet();
        using var reader = new ConnectionTableReader(pid => multiHost.Contains(pid));
        var rows = new List<TcpConnectionRow>();
        reader.ReadTcp(rows);
        var withModule = rows.Where(r => r.OwnerModule is not null).ToList();
        // Every Windows install has service-owned listeners (e.g. RPC endpoint mapper on 135 owned by RpcSs).
        Assert.NotEmpty(withModule);
    }

    [Fact]
    public void Interface_table_matches_network_interfaces()
    {
        var rows = new InterfaceTableReader().ReadInterfaces();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.IsLoopback);
        var byGuid = rows.ToDictionary(r => r.Guid);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!Guid.TryParse(nic.Id, out var guid) || !byGuid.TryGetValue(guid, out var row))
            {
                continue;
            }

            Assert.Equal(nic.Name, row.Alias);
            if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            {
                // Same underlying counters (GetIfEntry2); ours is read first so it can only be smaller or equal.
                var stats = nic.GetIPStatistics();
                Assert.True((ulong)stats.BytesReceived + (1 << 20) >= row.InOctets, $"{nic.Name}: rx mismatch");
            }
        }
    }

    [Fact]
    public void Routes_include_loopback_and_best_route_resolves()
    {
        var routes = RouteTableReader.ReadRoutes();
        Assert.Contains(routes, r => r.Destination.IsIPv4 && r.Destination.ToString() == "127.0.0.0" && r.PrefixLength == 8);
        Assert.All(routes, r => Assert.InRange(r.PrefixLength, 0, 128));

        var best = RouteTableReader.FindBestRoute(IpAddressValue.IPv4Loopback);
        Assert.NotNull(best);
    }
}
