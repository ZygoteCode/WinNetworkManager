using WinNetworkManager.Core.Processes;

namespace WinNetworkManager.Telemetry.Collection;

/// <summary>
/// Resolves the payload PID of a network event to the process instance that owned it at the event's timestamp.
/// Lifetimes come from Kernel-Process start/stop events in the same ordered stream, so PID reuse never merges two
/// processes. The previous instance of each PID is kept to resolve events delivered slightly out of order across CPUs.
/// Used only on the consumer thread.
/// </summary>
internal sealed class ProcessInstanceTracker
{
    private const int MaxSlots = 65_536;
    private readonly Dictionary<int, Slot> _slots = [];
    private readonly Func<int, long> _lookupCreateTime;

    /// <param name="lookupCreateTime">Reads the creation time of a PID never seen before (0 when unknown).</param>
    public ProcessInstanceTracker(Func<int, long> lookupCreateTime)
    {
        _lookupCreateTime = lookupCreateTime;
    }

    /// <summary>Number of tracked PIDs.</summary>
    public int Count => _slots.Count;

    /// <summary>Seeds processes that existed before the session started.</summary>
    public void Seed(IEnumerable<ProcessKey> processes)
    {
        foreach (var key in processes)
        {
            _slots[key.Pid] = new Slot { Current = key, CurrentSince = long.MinValue };
        }
    }

    /// <summary>A process started at <paramref name="timestamp"/>.</summary>
    public void OnStarted(ProcessKey key, long timestamp)
    {
        if (_slots.TryGetValue(key.Pid, out var slot))
        {
            if (slot.Current == key)
            {
                return; // seeded instance re-announced (start raced the initial snapshot)
            }

            slot.Previous = slot.Current;
            slot.HasPrevious = true;
        }

        slot.Current = key;
        slot.CurrentSince = timestamp;
        slot.Exited = false;
        _slots[key.Pid] = slot;
        PruneIfNeeded();
    }

    /// <summary>A process exited. The mapping is kept until the PID is reused (late events still resolve).</summary>
    public void OnExited(ProcessKey key)
    {
        if (_slots.TryGetValue(key.Pid, out var slot) && slot.Current == key)
        {
            slot.Exited = true;
            _slots[key.Pid] = slot;
        }
    }

    /// <summary>Instance that owned <paramref name="pid"/> at <paramref name="timestamp"/>.</summary>
    public ProcessKey Resolve(int pid, long timestamp)
    {
        if (pid == 0)
        {
            return ProcessKey.Idle;
        }

        if (_slots.TryGetValue(pid, out var slot))
        {
            return slot.HasPrevious && timestamp < slot.CurrentSince ? slot.Previous : slot.Current;
        }

        // Unknown PID (started between enumeration and enable, or snapshot denied): look it up once.
        var key = new ProcessKey(pid, _lookupCreateTime(pid));
        _slots[pid] = new Slot { Current = key, CurrentSince = long.MinValue };
        PruneIfNeeded();
        return key;
    }

    private void PruneIfNeeded()
    {
        if (_slots.Count <= MaxSlots)
        {
            return;
        }

        foreach (int pid in _slots.Where(kv => kv.Value.Exited).Select(kv => kv.Key).ToList())
        {
            _slots.Remove(pid);
        }
    }

    private struct Slot
    {
        public ProcessKey Current;
        public long CurrentSince;
        public ProcessKey Previous;
        public bool HasPrevious;
        public bool Exited;
    }
}
