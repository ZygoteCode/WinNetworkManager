namespace WinNetworkManager.Core.Traffic;

/// <summary>Outcome of feeding a sample to a <see cref="CumulativeCounter"/>.</summary>
public enum CounterSampleKind : byte
{
    /// <summary>First sample; establishes the baseline, no delta.</summary>
    Baseline,

    /// <summary>Valid delta over a normal interval: rate can be computed.</summary>
    Valid,

    /// <summary>The counter went backwards (reset, driver reload, adapter re-enabled). No delta, new baseline.</summary>
    Reset,

    /// <summary>The interval was abnormally long (suspend/resume, stall): delta is real but no rate is derived.</summary>
    Gap,

    /// <summary>Sample with a non-increasing timestamp; ignored.</summary>
    Ignored,
}

/// <summary>Result of <see cref="CumulativeCounter.Update"/>.</summary>
/// <param name="Kind">Classification of the sample.</param>
/// <param name="Delta">Increase since the previous sample (0 unless Valid or Gap).</param>
/// <param name="Elapsed">Measured time since the previous sample.</param>
public readonly record struct CounterDelta(CounterSampleKind Kind, ulong Delta, TimeSpan Elapsed)
{
    /// <summary>Rate per second, only for valid samples.</summary>
    public double? RatePerSecond => Kind == CounterSampleKind.Valid && Elapsed > TimeSpan.Zero
        ? Delta / Elapsed.TotalSeconds
        : null;
}

/// <summary>
/// Turns a monotonically increasing hardware/OS counter sampled at irregular times into safe deltas:
/// never negative, never wrapped, never averaged across gaps.
/// </summary>
public struct CumulativeCounter
{
    private ulong _last;
    private long _lastTimestamp;
    private bool _hasBaseline;

    /// <summary>Last raw value seen.</summary>
    public readonly ulong LastValue => _last;

    /// <summary>True after the first sample.</summary>
    public readonly bool HasBaseline => _hasBaseline;

    /// <summary>Feeds a sample.</summary>
    /// <param name="value">Raw cumulative counter value.</param>
    /// <param name="timestamp">Monotonic timestamp (e.g. <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>).</param>
    /// <param name="frequency">Ticks per second of <paramref name="timestamp"/>.</param>
    /// <param name="maxInterval">Intervals longer than this are reported as <see cref="CounterSampleKind.Gap"/>.</param>
    public CounterDelta Update(ulong value, long timestamp, long frequency, TimeSpan maxInterval)
    {
        if (!_hasBaseline)
        {
            _hasBaseline = true;
            _last = value;
            _lastTimestamp = timestamp;
            return new CounterDelta(CounterSampleKind.Baseline, 0, TimeSpan.Zero);
        }

        if (timestamp <= _lastTimestamp)
        {
            return new CounterDelta(CounterSampleKind.Ignored, 0, TimeSpan.Zero);
        }

        var elapsed = TimeSpan.FromSeconds((timestamp - _lastTimestamp) / (double)frequency);
        ulong previous = _last;
        _last = value;
        _lastTimestamp = timestamp;

        if (value < previous)
        {
            return new CounterDelta(CounterSampleKind.Reset, 0, elapsed);
        }

        ulong delta = value - previous;
        return elapsed > maxInterval
            ? new CounterDelta(CounterSampleKind.Gap, delta, elapsed)
            : new CounterDelta(CounterSampleKind.Valid, delta, elapsed);
    }

    /// <summary>Forgets the baseline (e.g. after resume from sleep).</summary>
    public void Reset()
    {
        _hasBaseline = false;
        _last = 0;
        _lastTimestamp = 0;
    }
}
