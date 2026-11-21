using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Time;
using WinNetworkManager.Telemetry.Collection;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Telemetry.Tests;

public sealed class AggregationTests
{
    private const long Freq = 10_000_000;
    private static readonly BucketTimeline Timeline = new(Freq, TimeSpan.FromMilliseconds(250));
    private static readonly long B = Timeline.BucketTicks; // 2 500 000 ticks

    private static NetworkEvent Ev(NetworkEventKind kind, long ts, int size, string remote = "8.8.8.8:443", int pid = 10)
    {
        IpEndpointValue.TryParse(remote, out var r);
        IpEndpointValue.TryParse("192.168.1.2:50000", out var l);
        return new NetworkEvent { Kind = kind, Timestamp = ts, Size = size, Pid = pid, Local = l, Remote = r };
    }

    [Fact]
    public void Tracker_resolves_pid_reuse_by_timestamp()
    {
        var tracker = new ProcessInstanceTracker(_ => 0);
        var first = new ProcessKey(500, 1000);
        var second = new ProcessKey(500, 2000);
        tracker.Seed([first]);
        Assert.Equal(first, tracker.Resolve(500, 10));

        tracker.OnExited(first);
        Assert.Equal(first, tracker.Resolve(500, 20)); // late event of the dead instance

        tracker.OnStarted(second, 100);
        Assert.Equal(second, tracker.Resolve(500, 150));
        Assert.Equal(first, tracker.Resolve(500, 90)); // delivered after the start event but older than it
    }

    [Fact]
    public void Tracker_looks_up_unknown_pids_once()
    {
        int lookups = 0;
        var tracker = new ProcessInstanceTracker(pid => { lookups++; return 42; });
        Assert.Equal(new ProcessKey(9, 42), tracker.Resolve(9, 1));
        Assert.Equal(new ProcessKey(9, 42), tracker.Resolve(9, 2));
        Assert.Equal(1, lookups);
        Assert.Equal(ProcessKey.Idle, tracker.Resolve(0, 3));
    }

    [Fact]
    public void Tracker_ignores_duplicate_start_of_seeded_instance()
    {
        var tracker = new ProcessInstanceTracker(_ => 0);
        var key = new ProcessKey(7, 70);
        tracker.Seed([key]);
        tracker.OnStarted(key, 500);
        Assert.Equal(key, tracker.Resolve(7, 1)); // no "previous" fabricated
    }

    [Fact]
    public void Bucketer_aggregates_by_event_time_and_finalizes_after_delay()
    {
        var bucketer = new TrafficBucketer(Timeline, TimeSpan.FromSeconds(1), 1000);
        long t0 = 100 * B;
        bucketer.Start(t0);
        var p = new ProcessKey(10, 1);
        bucketer.AddNetworkEvent(Ev(NetworkEventKind.TcpReceive, t0 + 10, 1000), p, loopback: false);
        bucketer.AddNetworkEvent(Ev(NetworkEventKind.TcpReceive, t0 + B + 10, 500), p, loopback: false);
        bucketer.AddNetworkEvent(Ev(NetworkEventKind.TcpSend, t0 + 20, 100), p, loopback: false);
        bucketer.AddNetworkEvent(Ev(NetworkEventKind.UdpReceive, t0 + 30, 64), p, loopback: true);

        // Not yet final: bucket 100 ends at t0 + B, needs + 1 s (4 B).
        Assert.Empty(bucketer.Finalize(t0 + B + (4 * B) - 1));
        var frames = bucketer.Finalize(t0 + (2 * B) + (4 * B));
        Assert.Equal([100L, 101L], frames.Select(f => f.BucketIndex));
        var c0 = Assert.Single(frames[0].Processes).Counters;
        Assert.Equal(1000, c0.External.TcpReceived);
        Assert.Equal(100, c0.External.TcpSent);
        Assert.Equal(64, c0.Loopback.UdpReceived);
        Assert.Equal(0, c0.External.UdpReceived);
        Assert.Equal(500, Assert.Single(frames[1].Processes).Counters.External.TcpReceived);
        Assert.Equal(3, frames[0].Health.Events); // the 500-byte event belongs to bucket 101
    }

    [Fact]
    public void Late_events_are_kept_in_the_oldest_open_bucket()
    {
        var bucketer = new TrafficBucketer(Timeline, TimeSpan.FromSeconds(1), 1000);
        long t0 = 50 * B;
        bucketer.Start(t0);
        var p = new ProcessKey(10, 1);
        bucketer.AddNetworkEvent(Ev(NetworkEventKind.TcpReceive, t0, 1), p, false);
        var first = bucketer.Finalize(t0 + (5 * B));
        Assert.Single(first);

        // An event timestamped in already-finalized bucket 50 arrives now.
        bucketer.AddNetworkEvent(Ev(NetworkEventKind.TcpReceive, t0 + 5, 999), p, false);
        var next = bucketer.Finalize(t0 + (20 * B));
        long total = next.SelectMany(f => f.Processes).Sum(x => x.Counters.External.TcpReceived);
        Assert.Equal(999, total);
        Assert.Equal(1, next.Sum(f => f.Health.LateEvents));
    }

    [Fact]
    public void Every_bucket_produces_a_frame_even_when_empty()
    {
        var bucketer = new TrafficBucketer(Timeline, TimeSpan.FromSeconds(1), 1000);
        bucketer.Start(10 * B);
        var frames = bucketer.Finalize((10 * B) + (12 * B));
        Assert.Equal(Enumerable.Range(10, 8).Select(i => (long)i), frames.Select(f => f.BucketIndex));
        Assert.All(frames, f => Assert.True(f.IsEmpty));
    }

    [Fact]
    public void Long_stall_produces_a_gap_not_thousands_of_empty_frames()
    {
        var bucketer = new TrafficBucketer(Timeline, TimeSpan.FromSeconds(1), 1000, ringCapacity: 64);
        bucketer.Start(10 * B);
        var frames = bucketer.Finalize((10 * B) + (10_000 * B));
        Assert.InRange(frames.Count, 1, 8);
    }

    [Fact]
    public void Lost_events_mark_the_next_frame_partial()
    {
        var bucketer = new TrafficBucketer(Timeline, TimeSpan.FromSeconds(1), 1000);
        bucketer.Start(0);
        bucketer.AddLostEvents(12);
        var frames = bucketer.Finalize(10 * B);
        Assert.True(frames[0].Health.IsPartial);
        Assert.Equal(12, frames[0].Health.EventsLost);
        Assert.All(frames.Skip(1), f => Assert.False(f.Health.IsPartial));
    }

    [Fact]
    public void Flow_cap_bounds_memory_and_counts_drops_but_process_totals_stay_exact()
    {
        var bucketer = new TrafficBucketer(Timeline, TimeSpan.FromSeconds(1), maxFlowsPerBucket: 3);
        bucketer.Start(0);
        var p = new ProcessKey(10, 1);
        for (int i = 0; i < 10; i++)
        {
            bucketer.AddNetworkEvent(Ev(NetworkEventKind.UdpSend, 5, 100, $"10.0.0.{i + 1}:53"), p, false);
        }

        var frame = bucketer.FinalizeAll(B * 2)[0];
        Assert.Equal(3, frame.Flows.Count);
        Assert.Equal(7, frame.Health.DroppedFlows);
        Assert.Equal(1000, Assert.Single(frame.Processes).Counters.External.UdpSent);
    }

    [Fact]
    public void Retransmissions_are_not_added_to_sent_bytes()
    {
        var bucketer = new TrafficBucketer(Timeline, TimeSpan.FromSeconds(1), 1000);
        bucketer.Start(0);
        var p = new ProcessKey(10, 1);
        bucketer.AddNetworkEvent(Ev(NetworkEventKind.TcpSend, 1, 1000), p, false);
        bucketer.AddNetworkEvent(Ev(NetworkEventKind.TcpRetransmit, 2, 1000), p, false);
        var frame = bucketer.FinalizeAll(B)[0];
        var counters = Assert.Single(frame.Processes).Counters;
        Assert.Equal(1000, counters.External.TcpSent);
        Assert.Equal(1000, counters.TcpRetransmittedBytes);
        Assert.Equal(1, counters.TcpRetransmissions);
        var flow = Assert.Single(frame.Flows).Counters;
        Assert.Equal(1000, flow.SentBytes);
        Assert.Equal(1, flow.Retransmissions);
    }

    [Fact]
    public void Connect_and_copy_events_create_no_traffic_entries()
    {
        var bucketer = new TrafficBucketer(Timeline, TimeSpan.FromSeconds(1), 1000);
        bucketer.Start(0);
        bucketer.AddNetworkEvent(Ev(NetworkEventKind.TcpConnect, 1, 0), new ProcessKey(10, 1), false);
        bucketer.AddNetworkEvent(Ev(NetworkEventKind.TcpCopy, 1, 5000), new ProcessKey(10, 1), false);
        var frame = bucketer.FinalizeAll(B)[0];
        Assert.Empty(frame.Processes);
        Assert.Empty(frame.Flows);
        Assert.Equal(2, frame.Health.Events);
    }

    [Fact]
    public void Ipv6_scope_is_removed_from_flow_keys()
    {
        var bucketer = new TrafficBucketer(Timeline, TimeSpan.FromSeconds(1), 1000);
        bucketer.Start(0);
        IpAddressValue.TryParse("fe80::1%7", out var scoped);
        var ev = Ev(NetworkEventKind.UdpReceive, 1, 10);
        ev.Remote = new IpEndpointValue(scoped, 5353);
        bucketer.AddNetworkEvent(ev, new ProcessKey(10, 1), false);
        var flow = Assert.Single(bucketer.FinalizeAll(B)[0].Flows);
        Assert.Equal(0u, flow.Key.Remote.Address.ScopeId);
    }

    [Fact]
    public void Local_address_set_classifies_own_addresses_as_local_machine()
    {
        using var set = new LocalAddressSet();
        IpAddressValue.TryParse("192.168.1.50", out var own);
        IpAddressValue.TryParse("192.168.1.51", out var other);
        set.Set([own]);
        Assert.True(set.IsLocalMachine(own));
        Assert.True(set.IsLocalMachine(IpAddressValue.IPv4Loopback));
        Assert.True(set.IsLocalMachine(IpAddressValue.IPv6Loopback));
        Assert.False(set.IsLocalMachine(other));
        IpAddressValue.TryParse("::ffff:192.168.1.50", out var mapped);
        Assert.True(set.IsLocalMachine(mapped));
    }
}
