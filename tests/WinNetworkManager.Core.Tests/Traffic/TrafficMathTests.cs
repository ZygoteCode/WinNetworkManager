using WinNetworkManager.Core.Time;
using WinNetworkManager.Core.Traffic;

namespace WinNetworkManager.Core.Tests.Traffic;

public sealed class TrafficMathTests
{
    private const long Freq = 10_000_000; // 10 MHz, like QPC on the reference machine

    [Fact]
    public void BucketRing_accumulates_and_sums_ranges()
    {
        var ring = new BucketRing<ProtocolBytes>(8);
        ring.Add(100, new ProtocolBytes(10, 1, 0, 0));
        ring.Add(100, new ProtocolBytes(5, 0, 0, 0));
        ring.Add(102, new ProtocolBytes(7, 0, 3, 0));
        Assert.Equal(102, ring.Newest);
        Assert.Equal(15, ring.Get(100).TcpReceived);
        Assert.True(ring.Get(101).IsZero);
        var sum = ring.Sum(100, 103);
        Assert.Equal(22, sum.TcpReceived);
        Assert.Equal(3, sum.UdpReceived);
        Assert.Equal(1, sum.TcpSent);
    }

    [Fact]
    public void BucketRing_rejects_values_older_than_capacity_and_zero_fills_on_advance()
    {
        var ring = new BucketRing<ProtocolBytes>(4);
        ring.Add(10, new ProtocolBytes(1, 0, 0, 0));
        ring.Add(20, new ProtocolBytes(2, 0, 0, 0)); // jump larger than capacity: old data must vanish
        Assert.Equal(17, ring.Oldest);
        Assert.False(ring.Add(10, new ProtocolBytes(100, 0, 0, 0)));
        Assert.True(ring.Get(10).IsZero);
        Assert.Equal(2, ring.Sum(0, 100).TcpReceived);
        Assert.True(ring.Add(18, new ProtocolBytes(3, 0, 0, 0)));
        Assert.Equal(5, ring.Sum(17, 21).TcpReceived);
    }

    [Fact]
    public void BucketRing_slot_reuse_does_not_leak_previous_lap_values()
    {
        var ring = new BucketRing<ProtocolBytes>(3);
        for (long i = 0; i < 3; i++)
        {
            ring.Add(i, new ProtocolBytes(100, 0, 0, 0));
        }

        ring.AdvanceTo(4); // buckets 3 and 4 must be zero even though slots were used by 0 and 1
        Assert.True(ring.Get(3).IsZero);
        Assert.True(ring.Get(4).IsZero);
        Assert.Equal(100, ring.Get(2).TcpReceived);
    }

    [Fact]
    public void CumulativeCounter_produces_rates_from_real_elapsed_time()
    {
        var counter = new CumulativeCounter();
        var max = TimeSpan.FromSeconds(5);
        Assert.Equal(CounterSampleKind.Baseline, counter.Update(1_000, 0, Freq, max).Kind);

        // Irregular interval: 1.5 s, 3 000 bytes → 2 000 B/s (not 3 000 B/s as a naive "per tick" computation).
        var d = counter.Update(4_000, 15_000_000, Freq, max);
        Assert.Equal(CounterSampleKind.Valid, d.Kind);
        Assert.Equal(3_000UL, d.Delta);
        Assert.Equal(2_000, d.RatePerSecond!.Value, 6);
    }

    [Fact]
    public void CumulativeCounter_never_reports_negative_deltas_on_reset()
    {
        var counter = new CumulativeCounter();
        var max = TimeSpan.FromSeconds(5);
        counter.Update(50_000, 0, Freq, max);
        var reset = counter.Update(100, Freq, Freq, max);
        Assert.Equal(CounterSampleKind.Reset, reset.Kind);
        Assert.Equal(0UL, reset.Delta);
        Assert.Null(reset.RatePerSecond);

        var after = counter.Update(1_100, 2 * Freq, Freq, max);
        Assert.Equal(CounterSampleKind.Valid, after.Kind);
        Assert.Equal(1_000UL, after.Delta);
    }

    [Fact]
    public void CumulativeCounter_reports_gaps_without_rate()
    {
        var counter = new CumulativeCounter();
        var max = TimeSpan.FromSeconds(3);
        counter.Update(0, 0, Freq, max);
        var gap = counter.Update(10_000, 60 * Freq, Freq, max); // e.g. suspend/resume
        Assert.Equal(CounterSampleKind.Gap, gap.Kind);
        Assert.Equal(10_000UL, gap.Delta);
        Assert.Null(gap.RatePerSecond);
    }

    [Fact]
    public void CumulativeCounter_ignores_non_increasing_timestamps()
    {
        var counter = new CumulativeCounter();
        var max = TimeSpan.FromSeconds(3);
        counter.Update(0, 100, Freq, max);
        Assert.Equal(CounterSampleKind.Ignored, counter.Update(10, 100, Freq, max).Kind);
        Assert.Equal(CounterSampleKind.Ignored, counter.Update(10, 50, Freq, max).Kind);
    }

    [Fact]
    public void TrafficRates_from_bytes()
    {
        var rates = TrafficRates.FromBytes(new ProtocolBytes(4000, 2000, 1000, 0), 2.0);
        Assert.Equal(2000, rates.TcpReceived);
        Assert.Equal(2500, rates.Received);
        Assert.Equal(1000, rates.Sent);
        Assert.Equal(TrafficRates.Zero, TrafficRates.FromBytes(new ProtocolBytes(1, 1, 1, 1), 0));
    }

    [Fact]
    public void TrafficCounters_select_excludes_loopback_by_default()
    {
        var counters = new TrafficCounters(new ProtocolBytes(100, 10, 0, 0), new ProtocolBytes(1000, 1000, 0, 0));
        Assert.Equal(100, counters.Select(includeLoopback: false).TcpReceived);
        Assert.Equal(1100, counters.Select(includeLoopback: true).TcpReceived);
    }

    [Fact]
    public void BucketTimeline_maps_timestamps_to_buckets_and_wall_clock()
    {
        var timeline = new BucketTimeline(Freq, TimeSpan.FromMilliseconds(250));
        Assert.Equal(2_500_000, timeline.BucketTicks);
        Assert.Equal(0, timeline.IndexOf(2_499_999));
        Assert.Equal(1, timeline.IndexOf(2_500_000));
        Assert.Equal(5_000_000, timeline.EndOf(1));

        var anchor = new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
        timeline.Anchor(100 * Freq, anchor);
        Assert.Equal(anchor.AddSeconds(2), timeline.ToUtc(102 * Freq));
        Assert.Equal(anchor.AddSeconds(-1), timeline.ToUtc(99 * Freq));
    }

    [Fact]
    public void UniformSeries_downsampling_averages_and_preserves_gaps()
    {
        var start = DateTimeOffset.UnixEpoch;
        var series = new UniformSeries("x", start, TimeSpan.FromSeconds(1), [1, 3, double.NaN, double.NaN, 4, double.NaN]);
        var down = series.Downsample(3);
        Assert.Equal(3, down.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), down.Step);
        Assert.Equal(2, down.Values[0]);
        Assert.True(double.IsNaN(down.Values[1]));
        Assert.Equal(4, down.Values[2]);
        Assert.Equal(4, series.Max());
        Assert.Same(series, series.Downsample(10));
    }
}
