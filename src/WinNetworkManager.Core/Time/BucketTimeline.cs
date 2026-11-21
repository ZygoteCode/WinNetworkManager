namespace WinNetworkManager.Core.Time;

/// <summary>
/// Maps monotonic QPC timestamps (system-wide, shared by every process on the machine) to fixed-duration bucket
/// indices, and bucket boundaries back to wall-clock time through an anchor. Index 0 starts at QPC 0 (boot), so the
/// collector process and the UI process agree on bucket numbers without negotiation.
/// </summary>
public sealed class BucketTimeline
{
    private readonly object _anchorGate = new();
    private long _anchorTimestamp;
    private DateTimeOffset _anchorUtc;

    /// <summary>Creates a timeline.</summary>
    /// <param name="frequency">Timestamp ticks per second (QPC frequency).</param>
    /// <param name="bucketDuration">Bucket duration; must be at least 1 ms.</param>
    public BucketTimeline(long frequency, TimeSpan bucketDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frequency, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketDuration, TimeSpan.FromMilliseconds(1));
        Frequency = frequency;
        BucketDuration = bucketDuration;
        BucketTicks = (long)Math.Round(frequency * bucketDuration.TotalSeconds);
    }

    /// <summary>Ticks per second.</summary>
    public long Frequency { get; }

    /// <summary>Bucket duration.</summary>
    public TimeSpan BucketDuration { get; }

    /// <summary>Bucket duration in timestamp ticks.</summary>
    public long BucketTicks { get; }

    /// <summary>Number of buckets per second (may be fractional for unusual durations).</summary>
    public double BucketsPerSecond => 1.0 / BucketDuration.TotalSeconds;

    /// <summary>Bucket that contains <paramref name="timestamp"/>.</summary>
    public long IndexOf(long timestamp) => timestamp <= 0 ? 0 : timestamp / BucketTicks;

    /// <summary>First timestamp of a bucket.</summary>
    public long StartOf(long index) => index * BucketTicks;

    /// <summary>First timestamp after a bucket.</summary>
    public long EndOf(long index) => (index + 1) * BucketTicks;

    /// <summary>Converts a tick interval to a <see cref="TimeSpan"/>.</summary>
    public TimeSpan ToTimeSpan(long ticks) => TimeSpan.FromSeconds(ticks / (double)Frequency);

    /// <summary>Converts a <see cref="TimeSpan"/> to ticks.</summary>
    public long ToTicks(TimeSpan span) => (long)Math.Round(span.TotalSeconds * Frequency);

    /// <summary>Associates a monotonic timestamp with a wall-clock instant (call periodically to follow clock changes).</summary>
    public void Anchor(long timestamp, DateTimeOffset utcNow)
    {
        lock (_anchorGate)
        {
            _anchorTimestamp = timestamp;
            _anchorUtc = utcNow;
        }
    }

    /// <summary>True once <see cref="Anchor"/> has been called.</summary>
    public bool IsAnchored
    {
        get
        {
            lock (_anchorGate)
            {
                return _anchorUtc != default;
            }
        }
    }

    /// <summary>Wall-clock time of a monotonic timestamp, using the latest anchor.</summary>
    public DateTimeOffset ToUtc(long timestamp)
    {
        lock (_anchorGate)
        {
            return _anchorUtc + TimeSpan.FromSeconds((timestamp - _anchorTimestamp) / (double)Frequency);
        }
    }
}
