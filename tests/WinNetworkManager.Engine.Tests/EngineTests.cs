using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Engine.Interfaces;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Engine.Notifications;
using WinNetworkManager.Platform.Windows.IpHelper;
using WinNetworkManager.Telemetry.Model;
using static WinNetworkManager.Engine.Tests.EngineHarness;

namespace WinNetworkManager.Engine.Tests;

public sealed class EngineTests
{
    [Fact]
    public async Task Pid_reuse_creates_a_new_instance_and_never_merges_traffic()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(100, 1_000, "first.exe"));
        h.Cycle();
        h.FramesForLastSecond(_ => [new ProcessTraffic(new ProcessKey(100, 1_000), Bytes(tcpRx: 4_000))]);
        var s1 = h.Cycle();
        Assert.Equal(16_000, s1.Processes.Single(p => p.Key.Pid == 100).Totals.TcpReceived);

        // Same PID, new creation time: Windows recycled the PID.
        h.Processes.Entries.Clear();
        h.Processes.Entries.Add(Proc(100, 2_000, "second.exe"));
        var s2 = h.Cycle();
        var first = s2.Processes.Single(p => p.Key == new ProcessKey(100, 1_000));
        var second = s2.Processes.Single(p => p.Key == new ProcessKey(100, 2_000));
        Assert.Equal(ProcessState.Exited, first.State);
        Assert.Equal(ProcessState.Running, second.State);
        Assert.Equal("second.exe", second.Name);
        Assert.Equal(0, second.Totals.Total);
        Assert.Equal(0, second.Rates.Total);
    }

    [Fact]
    public async Task Rates_use_the_window_of_finalized_buckets()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(200, 5, "app.exe"));
        h.Cycle();
        // 4 buckets of 250 ms with 2 500 B each = 10 000 B in 1 s.
        h.FramesForLastSecond(_ => [new ProcessTraffic(new ProcessKey(200, 5), Bytes(tcpRx: 2_500, udpTx: 250))]);
        var s = h.Cycle(TimeSpan.Zero);
        var p = s.Processes.Single(x => x.Key.Pid == 200);
        Assert.Equal(10_000, p.Rates.TcpReceived, 3);
        Assert.Equal(1_000, p.Rates.UdpSent, 3);
        Assert.Equal(TrafficAvailability.Available, p.Traffic);
        Assert.Equal(10_000, p.PeakReceiveRate, 3);
        Assert.Equal(10_000, s.System.AttributedRates.TcpReceived, 3);
        Assert.Equal(1, s.System.ActiveProcesses);
    }

    [Fact]
    public async Task Without_collector_traffic_is_unavailable_not_zero()
    {
        await using var h = new EngineHarness();
        h.Processes.Entries.Add(Proc(300, 7, "app.exe"));
        var s = h.Cycle();
        var p = s.Processes.Single(x => x.Key.Pid == 300);
        Assert.Equal(TrafficAvailability.RequiresCollector, p.Traffic);
        Assert.Equal(TrafficAvailability.RequiresCollector, s.System.Traffic);
    }

    [Fact]
    public async Task Lost_events_mark_the_window_partial()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(400, 7, "app.exe"));
        h.Cycle();
        h.FramesForLastSecond(_ => [new ProcessTraffic(new ProcessKey(400, 7), Bytes(tcpRx: 1))], lostInFirst: 5);
        var s = h.Cycle(TimeSpan.Zero);
        Assert.True(s.Collector.PartialData);
        Assert.Equal(TrafficAvailability.Partial, s.Processes.Single(p => p.Key.Pid == 400).Traffic);
    }

    [Fact]
    public async Task Unreadable_create_time_is_upgraded_when_the_collector_reports_it()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(500, 0, "protected.exe")); // access denied when unelevated
        var s1 = h.Cycle();
        var before = s1.Processes.Single(p => p.Key.Pid == 500);
        Assert.Equal(IdentityConfidence.PidAndName, before.Identity);

        h.FramesForLastSecond(_ => [new ProcessTraffic(new ProcessKey(500, 9_999), Bytes(tcpTx: 100))]);
        var s2 = h.Cycle(TimeSpan.Zero);
        var after = Assert.Single(s2.Processes, p => p.Key.Pid == 500);
        Assert.Equal(new ProcessKey(500, 9_999), after.Key);
        Assert.Equal("protected.exe", after.Name);
        Assert.Equal(IdentityConfidence.Exact, after.Identity);
        Assert.Equal(400, after.Totals.TcpSent);
    }

    [Fact]
    public async Task Exited_processes_stay_visible_for_the_retention_then_disappear()
    {
        await using var h = new EngineHarness();
        h.Processes.Entries.Add(Proc(600, 1, "short.exe"));
        h.Cycle();
        h.Processes.Entries.Clear();
        var s1 = h.Cycle();
        Assert.Equal(ProcessState.Exited, s1.Processes.Single(p => p.Key.Pid == 600).State);
        var s2 = h.Cycle(TimeSpan.FromSeconds(31));
        Assert.DoesNotContain(s2.Processes, p => p.Key.Pid == 600);
    }

    [Fact]
    public async Task Exited_processes_without_traffic_disappear_quickly_those_with_traffic_stay()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(610, 1, "idle.exe"));
        h.Processes.Entries.Add(Proc(611, 1, "talker.exe"));
        h.Cycle();
        h.FramesForLastSecond(_ => [new ProcessTraffic(new ProcessKey(611, 1), Bytes(tcpRx: 1))]);
        h.Cycle(TimeSpan.Zero);
        h.Processes.Entries.Clear();
        h.Cycle();
        var s = h.Cycle(TimeSpan.FromSeconds(4));
        Assert.DoesNotContain(s.Processes, p => p.Key.Pid == 610);
        Assert.Contains(s.Processes, p => p.Key.Pid == 611 && p.State == ProcessState.Exited);
    }

    [Fact]
    public async Task Collector_lifecycle_events_set_exit_code()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(700, 70, "tool.exe"));
        h.Cycle();
        long b = h.CurrentBucket - 4;
        h.Frame(b, events: [new ProcessLifecycleEvent(ProcessLifecycleKind.Exited, b * BucketTicks, new ProcessKey(700, 70), 0, -1, "tool.exe", 3, 0)]);
        var s = h.Cycle(TimeSpan.Zero);
        var p = s.Processes.Single(x => x.Key.Pid == 700);
        Assert.Equal(ProcessState.Exited, p.State);
        Assert.Equal(3, p.ExitCode);
    }

    [Fact]
    public async Task Connections_open_close_and_keep_owner_through_time_wait()
    {
        await using var h = new EngineHarness();
        h.Processes.Entries.Add(Proc(800, 80, "browser.exe"));
        var local = Ep("192.168.1.5:50001");
        var remote = Ep("93.184.216.34:443");
        h.Connections.Tcp.Add(new TcpConnectionRow(local, remote, TcpState.Established, 800, 0, null));
        h.Connections.Tcp.Add(new TcpConnectionRow(Ep("0.0.0.0:8080"), Ep("0.0.0.0:0"), TcpState.Listen, 800, 0, null));
        var s1 = h.Cycle();
        var c1 = s1.Connections.Single(c => c.Remote == remote);
        Assert.Equal(TcpState.Established, c1.State);
        Assert.Equal("browser.exe", c1.OwnerName);
        Assert.Equal(AddressScope.Public, c1.RemoteScope);
        var proc = s1.Processes.Single(p => p.Key.Pid == 800);
        Assert.Equal(1, proc.TcpConnections);
        Assert.Equal(1, proc.TcpListeners);
        Assert.Equal(1, proc.RemoteAddresses);
        Assert.Equal(1, s1.System.TcpConnections);
        Assert.Equal(1, s1.System.TcpListeners);

        // Closed: TIME_WAIT row owned by PID 0.
        h.Connections.Tcp[0] = new TcpConnectionRow(local, remote, TcpState.TimeWait, 0, 0, null);
        var s2 = h.Cycle();
        var c2 = s2.Connections.Single(c => c.Remote == remote);
        Assert.Equal(c1.Id, c2.Id);
        Assert.Equal(TcpState.TimeWait, c2.State);
        Assert.Equal(0, c2.OwnerPid);
        Assert.Equal("browser.exe (closed)", c2.OwnerName);
        Assert.Equal(new ProcessKey(800, 80), c2.Owner);

        // Gone from the table: closed, kept 10 s, then removed.
        h.Connections.Tcp.RemoveAt(0);
        var s3 = h.Cycle();
        var c3 = s3.Connections.Single(c => c.Remote == remote);
        Assert.Equal(TcpState.Closed, c3.State);
        Assert.NotNull(c3.ClosedAt);
        var s4 = h.Cycle(TimeSpan.FromSeconds(11));
        Assert.DoesNotContain(s4.Connections, c => c.Remote == remote);
    }

    [Fact]
    public async Task Flows_add_bytes_to_connections_and_udp_peers_to_wildcard_endpoints()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        var key = new ProcessKey(900, 90);
        h.Processes.Entries.Add(Proc(900, 90, "game.exe"));
        var local = Ep("10.0.0.2:50010");
        var remote = Ep("1.2.3.4:443");
        h.Connections.Tcp.Add(new TcpConnectionRow(local, remote, TcpState.Established, 900, 0, null));
        h.Connections.Udp.Add(new UdpEndpointRow(Ep("0.0.0.0:3074"), 900, 0, false, null));
        h.Cycle();

        long b = h.CurrentBucket - 4;
        h.Frame(b, flows:
        [
            new FlowTraffic(new FlowKey(TransportProtocol.Tcp, key, local, remote), new FlowCounters { ReceivedBytes = 1000, SentBytes = 200 }),
            new FlowTraffic(new FlowKey(TransportProtocol.Udp, key, Ep("10.0.0.2:3074"), Ep("5.6.7.8:3478")), new FlowCounters { ReceivedBytes = 50, SentBytes = 60 }),
            new FlowTraffic(new FlowKey(TransportProtocol.Udp, key, Ep("10.0.0.2:3074"), Ep("9.9.9.9:3478")), new FlowCounters { ReceivedBytes = 5, SentBytes = 6 }),
        ]);
        var s = h.Cycle(TimeSpan.Zero);
        var tcp = s.Connections.Single(c => c.Remote == remote);
        Assert.Equal(1000, tcp.ReceivedBytes);
        Assert.Equal(200, tcp.SentBytes);
        Assert.True(tcp.BytesAvailable);
        var udp = s.UdpEndpoints.Single(u => u.Local.Port == 3074);
        Assert.Equal(55, udp.ReceivedBytes);
        Assert.Equal(66, udp.SentBytes);
        Assert.Equal(2, udp.PeerCount);
        Assert.Contains(udp.Peers, p => p.Remote == Ep("5.6.7.8:3478") && p.ReceivedBytes == 50);
        Assert.Equal(3, s.Processes.Single(p => p.Key.Pid == 900).RemoteAddresses);
    }

    [Fact]
    public async Task Short_lived_connection_seen_only_by_etw_is_reported()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(950, 95, "curl.exe"));
        h.Cycle();
        long b = h.CurrentBucket - 4;
        h.Frame(b, flows: [new FlowTraffic(new FlowKey(TransportProtocol.Tcp, new ProcessKey(950, 95), Ep("10.0.0.2:50020"), Ep("8.8.8.8:443")), new FlowCounters { ReceivedBytes = 700 })]);
        var s = h.Cycle(TimeSpan.Zero);
        var c = s.Connections.Single(x => x.Remote == Ep("8.8.8.8:443"));
        Assert.Equal(ConnectionOrigin.ObservedOnly, c.Origin);
        Assert.Equal(700, c.ReceivedBytes);
        Assert.Equal("curl.exe", c.OwnerName);
    }

    [Fact]
    public async Task Interface_counter_reset_never_produces_a_negative_or_wrapped_rate()
    {
        await using var h = new EngineHarness();
        var guid = Guid.NewGuid();
        h.Configuration.Items.Add(new InterfaceConfiguration(guid, "Ethernet", "NIC", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, [], [], [], null, true, [], 1500, "00-01-02-03-04-05"));
        h.Interfaces.Rows.Add(Nic(1, guid, 1_000_000, 500_000));
        h.Cycle();
        h.Interfaces.Rows[0] = Nic(1, guid, 1_250_000, 600_000);
        var s1 = h.Cycle(TimeSpan.FromSeconds(2));
        var nic = s1.Interfaces.Single();
        Assert.Equal(125_000, nic.ReceiveRate!.Value, 3); // 250 000 B over 2 s of real elapsed time
        Assert.Equal(50_000, nic.SendRate!.Value, 3);
        Assert.Equal(125_000, s1.System.PhysicalReceiveRate!.Value, 3);

        h.Interfaces.Rows[0] = Nic(1, guid, 100, 50); // adapter re-enabled: counters restart
        var s2 = h.Cycle();
        nic = s2.Interfaces.Single();
        Assert.Null(nic.ReceiveRate);
        Assert.True(nic.CounterReset);
        Assert.Equal(250_000, nic.SessionReceived);
        Assert.Null(s2.System.PhysicalReceiveRate);
    }

    [Fact]
    public async Task Filter_and_virtual_interfaces_are_not_summed_into_physical_traffic()
    {
        await using var h = new EngineHarness();
        h.Interfaces.Rows.Add(Nic(1, Guid.NewGuid(), 0, 0));
        h.Interfaces.Rows.Add(Nic(2, Guid.NewGuid(), 0, 0, hardware: true, filter: true));
        h.Interfaces.Rows.Add(Nic(3, Guid.NewGuid(), 0, 0, hardware: false));
        h.Cycle();
        h.Interfaces.Rows[0] = Nic(1, h.Interfaces.Rows[0].Guid, 1000, 0);
        h.Interfaces.Rows[1] = Nic(2, h.Interfaces.Rows[1].Guid, 1000, 0, hardware: true, filter: true);
        h.Interfaces.Rows[2] = Nic(3, h.Interfaces.Rows[2].Guid, 1000, 0, hardware: false);
        var s = h.Cycle();
        Assert.Equal(1000, s.System.PhysicalReceiveRate!.Value, 3);
        Assert.Equal(1000, s.System.PhysicalReceived);
    }

    [Fact]
    public async Task Loopback_is_excluded_by_default_and_included_on_request()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(1000, 10, "server.exe"));
        h.Cycle();
        h.FramesForLastSecond(_ => [new ProcessTraffic(new ProcessKey(1000, 10), Bytes(tcpRx: 10, loopRx: 1000))]);
        var s1 = h.Cycle(TimeSpan.Zero);
        var p1 = s1.Processes.Single(p => p.Key.Pid == 1000);
        Assert.Equal(40, p1.Totals.TcpReceived);
        Assert.Equal(4000, p1.LoopbackTotals.TcpReceived);
        Assert.Equal(4000, s1.System.LoopbackRates.TcpReceived, 3);

        h.Engine.ApplySettings(h.Settings with { Monitoring = h.Settings.Monitoring with { IncludeLoopbackInTotals = true } });
        var s2 = h.Cycle(TimeSpan.Zero);
        Assert.Equal(4040, s2.Processes.Single(p => p.Key.Pid == 1000).Totals.TcpReceived);
    }

    [Fact]
    public async Task Gap_in_frames_marks_partial_data()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        h.Cycle();
        long end = h.CurrentBucket - 4;
        h.Frame(end - 3);
        h.Frame(end); // buckets end-2 and end-1 missing (e.g. frames dropped)
        var s = h.Cycle(TimeSpan.Zero);
        Assert.True(s.Collector.PartialData);
    }

    [Fact]
    public async Task Collector_stop_makes_rates_unavailable_but_keeps_totals()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(1100, 11, "app.exe"));
        h.Cycle();
        h.FramesForLastSecond(_ => [new ProcessTraffic(new ProcessKey(1100, 11), Bytes(tcpRx: 100))]);
        h.Cycle(TimeSpan.Zero);
        h.CollectorStopped();
        var s = h.Cycle();
        var p = s.Processes.Single(x => x.Key.Pid == 1100);
        Assert.Equal(TrafficAvailability.RequiresCollector, p.Traffic);
        Assert.Equal(400, p.Totals.TcpReceived);
        Assert.Equal(0, p.Rates.Total);
    }

    [Fact]
    public async Task Rate_threshold_notification_fires_once()
    {
        var settings = new AppSettings
        {
            Monitoring = new MonitoringSettings { RefreshIntervalMs = 1000, ConnectionPollIntervalMs = 1000, ProcessPollIntervalMs = 1000 },
            Notifications = new NotificationSettings { RateThresholdEnabled = true, RateThresholdBytesPerSecond = 1_000, RateThresholdSeconds = 2 },
        };
        await using var h = new EngineHarness(settings);
        var raised = new List<EngineNotification>();
        h.Engine.NotificationRaised += (_, n) => raised.Add(n);
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(1200, 12, "uploader.exe"));
        h.Cycle();
        for (int i = 0; i < 5; i++)
        {
            h.FramesForLastSecond(_ => [new ProcessTraffic(new ProcessKey(1200, 12), Bytes(tcpTx: 10_000))]);
            h.Cycle();
        }

        Assert.Single(raised, n => n.Kind == NotificationKind.RateThreshold);
    }

    [Fact]
    public async Task Queries_return_per_second_series()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(1300, 13, "app.exe"));
        h.Cycle();
        h.FramesForLastSecond(_ => [new ProcessTraffic(new ProcessKey(1300, 13), Bytes(udpRx: 25))]);
        h.Cycle(TimeSpan.Zero);
        var series = await h.Engine.GetProcessSeriesAsync(new ProcessKey(1300, 13), 10);
        Assert.NotNull(series);
        Assert.Equal(10, series.Values.Count);
        Assert.Equal(100, series.Values.Sum(v => v.UdpReceived));
        Assert.Contains(true, series.Covered);   // the second with frames
        Assert.False(series.Covered[0]);         // before the collector delivered anything: unknown, drawn as a gap
        var udp = series.Select(v => v.UdpReceived);
        Assert.True(double.IsNaN(udp[0]));
        Assert.Equal(100, udp.Where(v => !double.IsNaN(v)).Sum()); // nothing measured is hidden by the mask
        var system = await h.Engine.GetSystemSeriesAsync(10);
        Assert.Equal(100, system!.Values.Sum(v => v.UdpReceived));
    }
}
