using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WinNetworkManager.Diagnostics.Tests;

public sealed class TargetTests
{
    [Theory]
    [InlineData("  example.com ", "example.com")]
    [InlineData("https://Example.com/path?q=1", "example.com")]
    [InlineData("[::1]", "::1")]
    [InlineData("192.168.1.1", "192.168.1.1")]
    [InlineData("router", "router")]
    public void Valid_destinations_are_normalized(string input, string expected)
    {
        Assert.True(DiagnosticTarget.TryNormalize(input, out string host, out string? error), error);
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.0/8")]
    [InlineData("10.0.0.1,10.0.0.2")]
    [InlineData("*.example.com")]
    [InlineData("999.1.1.1")]
    [InlineData("123")]
    [InlineData("10.1")]
    public void Ranges_lists_and_invalid_input_are_rejected(string input)
    {
        Assert.False(DiagnosticTarget.TryNormalize(input, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task Localhost_resolves_with_family_preference()
    {
        var ct = TestContext.Current.CancellationToken;
        var v4 = await DiagnosticTarget.ResolveAsync("localhost", AddressPreference.IPv4, ct);
        Assert.All(v4.Addresses, a => Assert.Equal(AddressFamily.InterNetwork, a.AddressFamily));
        var literal = await DiagnosticTarget.ResolveAsync("127.0.0.1", AddressPreference.System, ct);
        Assert.True(literal.IsLiteral);
        Assert.Null(literal.ResolveMs);
        await Assert.ThrowsAsync<DiagnosticsException>(() => DiagnosticTarget.ResolveAsync("127.0.0.1", AddressPreference.IPv6, ct));
    }
}

public sealed class PingTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Loopback_answers_every_request(string address)
    {
        var replies = new List<PingReplyInfo>();
        await foreach (var reply in PingTool.RunAsync(IPAddress.Parse(address), new PingSettings { Count = 3, Interval = TimeSpan.FromMilliseconds(50) }, TestContext.Current.CancellationToken))
        {
            replies.Add(reply);
        }

        Assert.Equal([1, 2, 3], replies.Select(r => r.Sequence));
        Assert.All(replies, r =>
        {
            Assert.Equal(IPStatus.Success, r.Status);
            Assert.NotNull(r.RoundTripMs);
        });
        var summary = PingTool.Summarize(replies);
        Assert.Equal(3, summary.Received);
        Assert.Equal(0, summary.LossPercent);
    }

    [Fact]
    public void Summary_computes_loss_extremes_and_jitter()
    {
        var target = IPAddress.Loopback;
        PingReplyInfo R(int seq, long? rtt) => new(seq, DateTimeOffset.UtcNow, target, rtt is null ? null : target, rtt is null ? IPStatus.TimedOut : IPStatus.Success, rtt, 64, 32);
        var summary = PingTool.Summarize([R(1, 10), R(2, 20), R(3, null), R(4, 40)]);
        Assert.Equal(4, summary.Sent);
        Assert.Equal(3, summary.Received);
        Assert.Equal(25, summary.LossPercent);
        Assert.Equal(10, summary.MinimumMs);
        Assert.Equal(40, summary.MaximumMs);
        Assert.Equal(70 / 3.0, summary.AverageMs!.Value, 6);
        Assert.Equal(15, summary.JitterMs!.Value, 6); // (|20-10| + |40-20|) / 2
        Assert.Equal(PingSummary.Empty, PingTool.Summarize([]));
    }

    [Fact]
    public async Task Invalid_settings_are_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in PingTool.RunAsync(IPAddress.Loopback, new PingSettings { Ttl = 0 }, TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public void Round_trip_text_shows_sub_millisecond_replies()
    {
        var reply = new PingReplyInfo(1, DateTimeOffset.UtcNow, IPAddress.Loopback, IPAddress.Loopback, IPStatus.Success, 0, 128, 32);
        Assert.Equal("<1 ms", reply.RoundTripText);
    }
}

public sealed class TracerouteTests
{
    [Fact]
    public async Task Loopback_is_reached_at_the_first_hop()
    {
        var hops = new List<TraceHop>();
        await foreach (var hop in TracerouteTool.RunAsync(IPAddress.Loopback, new TracerouteSettings { ProbesPerHop = 2 }, TestContext.Current.CancellationToken))
        {
            hops.Add(hop);
        }

        var only = Assert.Single(hops);
        Assert.True(only.IsDestination);
        Assert.Equal(IPAddress.Loopback, only.Address);
        Assert.Equal(2, only.RoundTripsMs.Count);
        Assert.All(only.RoundTripsMs, r => Assert.NotNull(r));
    }
}

public sealed class TcpConnectTests
{
    [Fact]
    public async Task Listening_port_connects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var result = await TcpConnectTool.ProbeAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(TcpProbeOutcome.Connected, result.Outcome);
            Assert.NotNull(result.ConnectMs);
            Assert.NotNull(result.Local);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Closed_port_is_refused()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await TcpConnectTool.ProbeAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(TcpProbeOutcome.Refused, result.Outcome);
        Assert.Null(result.ConnectMs); // Windows retries refused connects: the elapsed time is not a latency
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, TcpProbeOutcome.Refused)]
    [InlineData(SocketError.TimedOut, TcpProbeOutcome.TimedOut)]
    [InlineData(SocketError.HostUnreachable, TcpProbeOutcome.Unreachable)]
    [InlineData(SocketError.NetworkUnreachable, TcpProbeOutcome.Unreachable)]
    [InlineData(SocketError.AccessDenied, TcpProbeOutcome.Failed)]
    public void Socket_errors_are_classified(SocketError error, TcpProbeOutcome outcome) => Assert.Equal(outcome, TcpConnectTool.Classify(error));
}

public sealed class PathMtuTests
{
    [Fact]
    public async Task Loopback_mtu_is_at_least_ethernet_size()
    {
        var result = await PathMtuTool.DiscoverAsync(IPAddress.Loopback, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.NotNull(result.Mtu);
        Assert.True(result.Mtu >= 1500, result.Message);
    }

    [Fact]
    public async Task Ipv6_is_reported_as_unsupported()
    {
        var result = await PathMtuTool.DiscoverAsync(IPAddress.IPv6Loopback, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Null(result.Mtu);
        Assert.Equal(0, result.Probes);
    }
}
