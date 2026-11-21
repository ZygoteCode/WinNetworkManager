using Microsoft.Extensions.Logging;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Engine.Traffic;
using WinNetworkManager.Platform.Windows.Processes;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Engine.Processes;

/// <summary>Mutable engine-side state of a process instance.</summary>
internal sealed class ProcessRecord
{
    public ProcessRecord(ProcessKey key, string name, DateTimeOffset firstSeen)
    {
        Key = key;
        Name = name;
        FirstSeen = firstSeen;
    }

    public ProcessKey Key { get; set; }

    public string Name { get; set; }

    public Probe<string> Path { get; set; } = Probe<string>.Missing(InfoStatus.Pending);

    public string? Description { get; set; }

    public string? Product { get; set; }

    public string? Company { get; set; }

    public string? FileVersion { get; set; }

    public SignatureView? Signature { get; set; }

    public IReadOnlyList<string> Services { get; set; } = [];

    public int ParentPid { get; set; }

    public int SessionId { get; set; } = -1;

    public string? UserName { get; set; }

    public bool IsServiceAccount { get; set; }

    public ProcessState State { get; set; }

    public DateTimeOffset? ExitTime { get; set; }

    public int? ExitCode { get; set; }

    public DateTimeOffset FirstSeen { get; }

    /// <summary>Time from which the collector covered this process (for averages).</summary>
    public DateTimeOffset? MonitoredSince { get; set; }

    public bool BasicEnrichmentRequested { get; set; }

    public bool FileEnrichmentRequested { get; set; }

    public bool SeenInLastSnapshot { get; set; }

    public TrafficAccumulator? Traffic { get; set; }

    /// <summary>Last bucket in which traffic was attributed.</summary>
    public DateTimeOffset? LastActivity { get; set; }

    public DateTimeOffset? StartTime => Key.CreateTimeUtc;

    public bool IsSystem =>
        WellKnownPids.IsKernel(Key.Pid) || SessionId == 0 || IsServiceAccount ||
        Name is "Registry" or "Memory Compression" or "Secure System" or "System" or "smss.exe" or "csrss.exe" or "wininit.exe" or "services.exe" or "lsass.exe";

    public string AppId => Path.HasValue ? Path.Value!.ToLowerInvariant() : "name:" + Name.ToLowerInvariant();
}

/// <summary>
/// Authoritative registry of process instances. Merges periodic snapshots (works without privileges) with exact
/// lifecycle events from the collector, detects PID reuse through creation times, and upgrades instances whose
/// creation time was unreadable when a better source reports it. Engine-thread only.
/// </summary>
internal sealed class ProcessCatalog
{
    private readonly Dictionary<ProcessKey, ProcessRecord> _byKey = [];
    private readonly Dictionary<int, ProcessRecord> _alive = [];
    private readonly ILogger _logger;
    private long _syntheticSequence;

    public ProcessCatalog(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>All records (running and retained exited).</summary>
    public IReadOnlyCollection<ProcessRecord> Records => _byKey.Values;

    /// <summary>Number of running processes.</summary>
    public int RunningCount => _alive.Count;

    /// <summary>The running instance with <paramref name="pid"/>.</summary>
    public ProcessRecord? FindAlive(int pid) => _alive.GetValueOrDefault(pid);

    /// <summary>Finds a record by key.</summary>
    public ProcessRecord? Find(ProcessKey key) => _byKey.GetValueOrDefault(key);

    /// <summary>Applies a process snapshot.</summary>
    public void ApplySnapshot(IReadOnlyList<ProcessSnapshotEntry> entries, DateTimeOffset now)
    {
        foreach (var record in _alive.Values)
        {
            record.SeenInLastSnapshot = false;
        }

        foreach (var entry in entries)
        {
            string name = DisplayName(entry);
            ProcessRecord record;
            if (entry.CreateTimeStatus == InfoStatus.Available && entry.CreateTime > 0)
            {
                if (_byKey.TryGetValue(entry.Key, out var existing))
                {
                    record = existing;
                    if (record.State == ProcessState.Exited && record.ExitCode is null)
                    {
                        // Absent from one enumeration but still alive: undo the inferred exit.
                        record.State = ProcessState.Running;
                        record.ExitTime = null;
                        _alive[record.Key.Pid] = record;
                    }
                }
                else if (_alive.TryGetValue(entry.Pid, out var current))
                {
                    if (!current.Key.HasCreateTime && (current.Name.StartsWith('<') || string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        record = Rekey(current, entry.Key);
                    }
                    else
                    {
                        MarkExited(current, now, null, "PID reused");
                        record = Add(entry.Key, name, now);
                    }
                }
                else
                {
                    record = Add(entry.Key, name, now);
                }
            }
            else if (_alive.TryGetValue(entry.Pid, out var current) && string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                record = current;
            }
            else
            {
                if (current is not null)
                {
                    MarkExited(current, now, null, "PID reused");
                }

                record = Add(NewSyntheticKey(entry.Pid), name, now);
            }

            record.ParentPid = entry.ParentPid;
            if (string.IsNullOrEmpty(record.Name) || record.Name.StartsWith('<'))
            {
                record.Name = name;
            }

            record.SeenInLastSnapshot = true;
        }

        foreach (var record in _alive.Values.Where(r => !r.SeenInLastSnapshot).ToList())
        {
            MarkExited(record, now, null, "absent from snapshot");
        }
    }

    /// <summary>Applies a lifecycle event from the collector.</summary>
    public void ApplyLifecycle(ProcessLifecycleEvent ev, DateTimeOffset when)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (ev.Kind == ProcessLifecycleKind.Started)
        {
            var record = ResolveForTraffic(ev.Process, when);
            record.ParentPid = ev.ParentPid;
            if (ev.SessionId >= 0)
            {
                record.SessionId = ev.SessionId;
            }

            if (!string.IsNullOrEmpty(ev.ImageName) && !record.Path.HasValue && System.IO.Path.IsPathRooted(ev.ImageName))
            {
                record.Path = Probe<string>.Of(ev.ImageName);
                record.Name = System.IO.Path.GetFileName(ev.ImageName);
            }

            return;
        }

        if (_byKey.TryGetValue(ev.Process, out var exited))
        {
            MarkExited(exited, when, ev.ExitCode, "collector exit event");
        }
    }

    /// <summary>
    /// Returns the record traffic for <paramref name="key"/> belongs to, creating or upgrading one when needed.
    /// </summary>
    public ProcessRecord ResolveForTraffic(ProcessKey key, DateTimeOffset now)
    {
        if (_byKey.TryGetValue(key, out var record))
        {
            return record;
        }

        _alive.TryGetValue(key.Pid, out var current);
        if (!key.HasCreateTime)
        {
            // The collector could not read the creation time: the running instance is the best attribution.
            return current ?? Add(NewSyntheticKey(key.Pid), Placeholder(key.Pid), now);
        }

        if (current is not null)
        {
            if (!current.Key.HasCreateTime)
            {
                return Rekey(current, key);
            }

            if (current.Key.CreateTimeTicks < key.CreateTimeTicks)
            {
                MarkExited(current, now, null, "newer instance reported by the collector");
            }
            else
            {
                // Traffic of an older instance we no longer track (exited before we saw it).
                var old = Add(key, Placeholder(key.Pid), now, makeAlive: false);
                old.State = ProcessState.Exited;
                old.ExitTime = now;
                return old;
            }
        }

        return Add(key, Placeholder(key.Pid), now);
    }

    /// <summary>
    /// Removes exited records: after <paramref name="retention"/> when they had network traffic, after
    /// <paramref name="idleRetention"/> otherwise (the collector reveals many short-lived processes — compilers,
    /// shells, conhost — that never touch the network and would only clutter the list).
    /// </summary>
    public void Prune(DateTimeOffset now, TimeSpan retention, TimeSpan idleRetention)
    {
        foreach (var record in _byKey.Values.Where(r => r.State == ProcessState.Exited && r.ExitTime is { } t &&
                     now - t > (r.Traffic is null ? TimeSpan.FromTicks(Math.Min(idleRetention.Ticks, retention.Ticks)) : retention)).ToList())
        {
            _byKey.Remove(record.Key);
        }
    }

    /// <summary>Assigns hosted services.</summary>
    public void ApplyServices(IReadOnlyDictionary<int, IReadOnlyList<ServiceInfo>> services)
    {
        foreach (var record in _alive.Values)
        {
            record.Services = services.TryGetValue(record.Key.Pid, out var list) ? list.Select(s => s.Name).ToArray() : [];
        }
    }

    private ProcessRecord Add(ProcessKey key, string name, DateTimeOffset now, bool makeAlive = true)
    {
        var record = new ProcessRecord(key, name, now);
        _byKey[key] = record;
        if (makeAlive)
        {
            _alive[key.Pid] = record;
        }

        return record;
    }

    private ProcessRecord Rekey(ProcessRecord record, ProcessKey key)
    {
        _byKey.Remove(record.Key);
        record.Key = key;
        _byKey[key] = record;
        if (record.State == ProcessState.Running)
        {
            _alive[key.Pid] = record;
        }

        return record;
    }

    private void MarkExited(ProcessRecord record, DateTimeOffset when, int? exitCode, string reason)
    {
        if (record.State != ProcessState.Exited)
        {
            record.State = ProcessState.Exited;
            record.ExitTime = when;
            _logger.LogTrace("Process {Name} ({Key}) exited: {Reason}.", record.Name, record.Key, reason);
        }

        if (exitCode is not null)
        {
            record.ExitCode = exitCode;
        }

        if (_alive.TryGetValue(record.Key.Pid, out var alive) && ReferenceEquals(alive, record))
        {
            _alive.Remove(record.Key.Pid);
        }
    }

    private ProcessKey NewSyntheticKey(int pid) => new(pid, -(++_syntheticSequence));

    private static string Placeholder(int pid) => pid switch
    {
        WellKnownPids.Idle => "System Idle Process",
        WellKnownPids.System => "System",
        _ => $"<PID {pid}>",
    };

    private static string DisplayName(ProcessSnapshotEntry entry) => entry.Pid switch
    {
        WellKnownPids.Idle => "System Idle Process",
        WellKnownPids.System => "System",
        _ => entry.ImageName,
    };
}
