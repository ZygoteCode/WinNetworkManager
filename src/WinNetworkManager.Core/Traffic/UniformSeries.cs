namespace WinNetworkManager.Core.Traffic;

/// <summary>
/// Immutable series of samples at a fixed step, as consumed by charts and exports.
/// <see cref="double.NaN"/> marks a gap (no data: app not running, collector stopped, interval lost).
/// </summary>
public sealed class UniformSeries
{
    /// <summary>Creates a series.</summary>
    public UniformSeries(string name, DateTimeOffset start, TimeSpan step, double[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);
        Name = name;
        Start = start;
        Step = step;
        Values = values;
    }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Time of the first sample (start of its interval).</summary>
    public DateTimeOffset Start { get; }

    /// <summary>Distance between samples.</summary>
    public TimeSpan Step { get; }

    /// <summary>Samples; NaN = gap.</summary>
    public IReadOnlyList<double> Values { get; }

    /// <summary>Number of samples.</summary>
    public int Count => Values.Count;

    /// <summary>End of the last interval.</summary>
    public DateTimeOffset End => Start + (Step * Count);

    /// <summary>Time of sample <paramref name="index"/>.</summary>
    public DateTimeOffset TimeAt(int index) => Start + (Step * index);

    /// <summary>Maximum finite value, or 0.</summary>
    public double Max()
    {
        double max = 0;
        foreach (double v in Values)
        {
            if (!double.IsNaN(v) && v > max)
            {
                max = v;
            }
        }

        return max;
    }

    /// <summary>
    /// Re-samples to at most <paramref name="maxPoints"/> points by averaging groups of consecutive samples
    /// (correct for rates: the average of equal-length intervals is the rate of the combined interval).
    /// Groups that are entirely gaps stay gaps; partially covered groups average only their finite samples.
    /// </summary>
    public UniformSeries Downsample(int maxPoints)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 1);
        if (Count <= maxPoints)
        {
            return this;
        }

        int group = (int)Math.Ceiling(Count / (double)maxPoints);
        int outCount = (int)Math.Ceiling(Count / (double)group);
        var result = new double[outCount];
        for (int o = 0; o < outCount; o++)
        {
            double sum = 0;
            int n = 0;
            int end = Math.Min(Count, (o + 1) * group);
            for (int i = o * group; i < end; i++)
            {
                double v = Values[i];
                if (!double.IsNaN(v))
                {
                    sum += v;
                    n++;
                }
            }

            result[o] = n == 0 ? double.NaN : sum / n;
        }

        return new UniformSeries(Name, Start, Step * group, result);
    }
}
