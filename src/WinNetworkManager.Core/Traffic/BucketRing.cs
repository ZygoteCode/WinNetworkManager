using System.Numerics;

namespace WinNetworkManager.Core.Traffic;

/// <summary>
/// Fixed-capacity ring of fixed-duration buckets addressed by absolute, monotonically increasing bucket indices.
/// Buckets that were never written read as <c>default</c>. Values older than the ring capacity are rejected,
/// so memory is bounded regardless of how long the application runs.
/// </summary>
/// <typeparam name="T">Additive value type (counters).</typeparam>
public sealed class BucketRing<T>
    where T : struct, IAdditionOperators<T, T, T>
{
    private readonly T[] _items;
    private long _newest = -1;

    /// <summary>Creates a ring holding <paramref name="capacity"/> buckets.</summary>
    public BucketRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    /// <summary>Number of buckets retained.</summary>
    public int Capacity => _items.Length;

    /// <summary>Absolute index of the newest bucket, or -1 when empty.</summary>
    public long Newest => _newest;

    /// <summary>Absolute index of the oldest bucket still retained, or -1 when empty.</summary>
    public long Oldest => _newest < 0 ? -1 : Math.Max(0, _newest - _items.Length + 1);

    /// <summary>Moves the head forward to <paramref name="index"/>, zero-filling skipped buckets.</summary>
    public void AdvanceTo(long index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (_newest < 0)
        {
            _newest = index;
            _items[Slot(index)] = default;
            return;
        }

        if (index <= _newest)
        {
            return;
        }

        long steps = Math.Min(index - _newest, _items.Length);
        for (long i = 1; i <= steps; i++)
        {
            _items[Slot(index - steps + i)] = default;
        }

        _newest = index;
    }

    /// <summary>Adds a value to a bucket. Returns false when the bucket is older than the retained window.</summary>
    public bool Add(long index, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        AdvanceTo(index);
        if (index < Oldest)
        {
            return false;
        }

        ref T slot = ref _items[Slot(index)];
        slot += value;
        return true;
    }

    /// <summary>Returns the value of one bucket (default when outside the retained window).</summary>
    public T Get(long index) => index < 0 || _newest < 0 || index > _newest || index < Oldest ? default : _items[Slot(index)];

    /// <summary>Sums buckets in [<paramref name="fromInclusive"/>, <paramref name="toExclusive"/>), clamped to the retained window.</summary>
    public T Sum(long fromInclusive, long toExclusive)
    {
        T sum = default;
        if (_newest < 0)
        {
            return sum;
        }

        long from = Math.Max(fromInclusive, Oldest);
        long to = Math.Min(toExclusive, _newest + 1);
        for (long i = from; i < to; i++)
        {
            sum += _items[Slot(i)];
        }

        return sum;
    }

    /// <summary>Removes all data.</summary>
    public void Clear()
    {
        Array.Clear(_items);
        _newest = -1;
    }

    private int Slot(long index) => (int)(index % _items.Length);
}
