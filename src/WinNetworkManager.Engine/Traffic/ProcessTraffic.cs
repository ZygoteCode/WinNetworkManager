using WinNetworkManager.Core.Traffic;

namespace WinNetworkManager.Engine.Traffic;

/// <summary>
/// Traffic accounting of one process instance (or of the whole system): totals, a short ring of 250 ms buckets for
/// the current-rate window, 1-second peaks and a per-second history ring for sparklines and the inspector.
/// Allocated lazily on first traffic; updated only by the engine thread.
/// </summary>
internal sealed class TrafficAccumulator
{
    private readonly int _bucketsPerSecond;
    private readonly BucketRing<TrafficCounters> _buckets;
    private readonly BucketRing<ProtocolBytes> _secondsExternal;
    private readonly BucketRing<ProtocolBytes> _secondsLoopback;

    public TrafficAccumulator(int bucketsPerSecond, int historySeconds)
    {
        _bucketsPerSecond = Math.Max(1, bucketsPerSecond);
        _buckets = new BucketRing<TrafficCounters>(Math.Max(16, _bucketsPerSecond * 16));
        _secondsExternal = new BucketRing<ProtocolBytes>(Math.Max(60, historySeconds));
        _secondsLoopback = new BucketRing<ProtocolBytes>(Math.Max(60, historySeconds));
    }

    /// <summary>Cumulative counters since monitoring started.</summary>
    public TrafficCounters Totals { get; private set; }

    /// <summary>First bucket with traffic.</summary>
    public long FirstBucket { get; private set; } = -1;

    /// <summary>Last bucket with traffic.</summary>
    public long LastBucket { get; private set; } = -1;

    /// <summary>Peak 1-second receive rate (external only).</summary>
    public double PeakExternalReceive { get; private set; }

    /// <summary>Peak 1-second send rate (external only).</summary>
    public double PeakExternalSend { get; private set; }

    /// <summary>Peak 1-second receive rate including loopback.</summary>
    public double PeakAllReceive { get; private set; }

    /// <summary>Peak 1-second send rate including loopback.</summary>
    public double PeakAllSend { get; private set; }

    /// <summary>Adds the counters of one finalized bucket.</summary>
    public void Add(long bucketIndex, TrafficCounters counters)
    {
        Totals += counters;
        if (FirstBucket < 0)
        {
            FirstBucket = bucketIndex;
        }

        LastBucket = Math.Max(LastBucket, bucketIndex);
        _buckets.Add(bucketIndex, counters);
        long second = bucketIndex / _bucketsPerSecond;
        _secondsExternal.Add(second, counters.External);
        _secondsLoopback.Add(second, counters.Loopback);

        // Peak over the 1-second window ending at this bucket (the maximum of a sliding sum is reached at a bucket
        // that received traffic, so evaluating here is sufficient).
        var window = _buckets.Sum(bucketIndex - _bucketsPerSecond + 1, bucketIndex + 1);
        PeakExternalReceive = Math.Max(PeakExternalReceive, window.External.Received);
        PeakExternalSend = Math.Max(PeakExternalSend, window.External.Sent);
        var all = window.External + window.Loopback;
        PeakAllReceive = Math.Max(PeakAllReceive, all.Received);
        PeakAllSend = Math.Max(PeakAllSend, all.Sent);
    }

    /// <summary>Sum of buckets in (<paramref name="endInclusive"/> - <paramref name="count"/>, <paramref name="endInclusive"/>].</summary>
    public TrafficCounters Window(long endInclusive, int count) => _buckets.Sum(endInclusive - count + 1, endInclusive + 1);

    /// <summary>
    /// Per-second totals (bytes/s) for the <paramref name="seconds"/> seconds ending at <paramref name="lastSecond"/>,
    /// oldest first.
    /// </summary>
    public float[] History(long lastSecond, int seconds, bool includeLoopback, bool receive, bool send)
    {
        var result = new float[seconds];
        for (int i = 0; i < seconds; i++)
        {
            long s = lastSecond - seconds + 1 + i;
            var bytes = _secondsExternal.Get(s);
            if (includeLoopback)
            {
                bytes += _secondsLoopback.Get(s);
            }

            result[i] = (receive ? bytes.Received : 0) + (send ? bytes.Sent : 0);
        }

        return result;
    }

    /// <summary>Per-second series for charts: download and upload, TCP and UDP separately.</summary>
    public (ProtocolBytes[] Values, long FirstSecond) Seconds(long lastSecond, int seconds, bool includeLoopback)
    {
        var values = new ProtocolBytes[seconds];
        long first = lastSecond - seconds + 1;
        for (int i = 0; i < seconds; i++)
        {
            var bytes = _secondsExternal.Get(first + i);
            values[i] = includeLoopback ? bytes + _secondsLoopback.Get(first + i) : bytes;
        }

        return (values, first);
    }
}
