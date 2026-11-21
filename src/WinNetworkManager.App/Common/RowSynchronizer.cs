using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace WinNetworkManager.App.Common;

/// <summary>ObservableCollection with a single-notification bulk replace.</summary>
/// <typeparam name="T">Item type.</typeparam>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces all items with one Reset notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// Keeps an observable collection of row view models in sync with immutable snapshot data: rows are matched by key
/// and updated in place (only changed values notify), new rows are inserted, missing rows removed, and the order is
/// adjusted with a minimal number of moves — so selection, focus and scroll position survive refreshes. When most of
/// the order changes, a single bulk reset is cheaper than many moves and is used instead.
/// </summary>
/// <typeparam name="TKey">Row identity.</typeparam>
/// <typeparam name="TRow">Row view model.</typeparam>
public sealed class RowSynchronizer<TKey, TRow>
    where TKey : notnull
    where TRow : class
{
    private readonly Dictionary<TKey, TRow> _rows = [];
    private readonly HashSet<TRow> _desiredSet = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TRow, int> _shownPosition = new(ReferenceEqualityComparer.Instance);
    private readonly List<int> _positions = [];
    private readonly List<int> _tails = [];

    /// <summary>Bound collection.</summary>
    public BulkObservableCollection<TRow> Items { get; } = [];

    /// <summary>All known rows (including filtered-out ones), by key.</summary>
    public IReadOnlyDictionary<TKey, TRow> Rows => _rows;

    /// <summary>
    /// Synchronizes. <paramref name="desired"/> is the final ordered list of rows to show (already filtered and sorted)
    /// built by the caller with <see cref="GetOrCreate"/>; rows created but not desired stay cached.
    /// </summary>
    public void Apply(IReadOnlyList<TRow> desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        _desiredSet.Clear();
        foreach (var row in desired)
        {
            _desiredSet.Add(row);
        }

        // Cost of an in-place update: removals + insertions + the minimum number of moves, which is the number of kept
        // rows outside the longest subsequence already in the right relative order. (Counting rows "out of place" would
        // make a single insertion near the top look like a complete reorder.)
        _shownPosition.Clear();
        int kept = 0;
        int removals = 0;
        foreach (var item in Items)
        {
            if (_desiredSet.Contains(item))
            {
                _shownPosition[item] = kept++;
            }
            else
            {
                removals++;
            }
        }

        _positions.Clear();
        int insertions = 0;
        foreach (var row in desired)
        {
            if (_shownPosition.TryGetValue(row, out int position))
            {
                _positions.Add(position);
            }
            else
            {
                insertions++;
            }
        }

        int moves = _positions.Count - LongestIncreasingRun(_positions);
        int threshold = Math.Max(24, desired.Count / 6);
        if (Items.Count == 0 || removals + insertions + moves > threshold)
        {
            Items.ReplaceAll(desired);
            return;
        }

        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!_desiredSet.Contains(Items[i]))
            {
                Items.RemoveAt(i);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            if (i < Items.Count && ReferenceEquals(Items[i], row))
            {
                continue;
            }

            int current = IndexOf(row, i);
            if (current < 0)
            {
                Items.Insert(i, row);
            }
            else
            {
                Items.Move(current, i);
            }
        }

        while (Items.Count > desired.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }

    /// <summary>Returns the row for <paramref name="key"/>, creating it when missing.</summary>
    public TRow GetOrCreate(TKey key, Func<TRow> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        if (!_rows.TryGetValue(key, out var row))
        {
            row = create();
            _rows[key] = row;
        }

        return row;
    }

    /// <summary>Drops cached rows whose key is not in <paramref name="alive"/>.</summary>
    public void Retain(IReadOnlySet<TKey> alive)
    {
        ArgumentNullException.ThrowIfNull(alive);
        if (_rows.Count == alive.Count)
        {
            return;
        }

        foreach (var key in _rows.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _rows.Remove(key);
        }
    }

    /// <summary>Length of the longest strictly increasing subsequence (patience sorting, O(n log n)).</summary>
    private int LongestIncreasingRun(List<int> values)
    {
        _tails.Clear();
        foreach (int value in values)
        {
            int index = _tails.BinarySearch(value);
            if (index < 0)
            {
                index = ~index;
            }

            if (index == _tails.Count)
            {
                _tails.Add(value);
            }
            else
            {
                _tails[index] = value;
            }
        }

        return _tails.Count;
    }

    private int IndexOf(TRow row, int from)
    {
        for (int i = from; i < Items.Count; i++)
        {
            if (ReferenceEquals(Items[i], row))
            {
                return i;
            }
        }

        return -1;
    }
}
