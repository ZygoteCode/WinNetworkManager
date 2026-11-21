using System.Windows.Threading;
using WinNetworkManager.Engine;
using WinNetworkManager.Engine.Model;

namespace WinNetworkManager.App.Services;

/// <summary>
/// Bridges engine snapshots (published on the engine thread) to the UI thread with "latest wins" coalescing:
/// if the UI is busy, intermediate snapshots are skipped rather than queued (backpressure).
/// </summary>
public sealed class SnapshotHub : IDisposable
{
    private readonly MonitoringEngine _engine;
    private readonly Dispatcher _dispatcher;
    private EngineSnapshot _pending;
    private int _scheduled;
    private bool _paused;

    /// <summary>Creates the hub.</summary>
    public SnapshotHub(MonitoringEngine engine, Dispatcher dispatcher)
    {
        _engine = engine;
        _dispatcher = dispatcher;
        _pending = engine.Latest;
        Current = engine.Latest;
        _engine.SnapshotPublished += OnSnapshot;
    }

    /// <summary>Raised on the UI thread with each delivered snapshot.</summary>
    public event EventHandler<EngineSnapshot>? Updated;

    /// <summary>Last snapshot delivered to the UI.</summary>
    public EngineSnapshot Current { get; private set; }

    /// <summary>Snapshots skipped because the UI was busy.</summary>
    public long Skipped { get; private set; }

    /// <summary>When paused the display freezes (the engine keeps collecting).</summary>
    public bool IsPaused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value)
            {
                Schedule();
            }
        }
    }

    /// <summary>Re-delivers the current snapshot (after a settings change that affects formatting).</summary>
    public void Refresh() => Updated?.Invoke(this, Current);

    /// <inheritdoc />
    public void Dispose() => _engine.SnapshotPublished -= OnSnapshot;

    private void OnSnapshot(object? sender, EngineSnapshot snapshot)
    {
        Volatile.Write(ref _pending, snapshot);
        Schedule();
    }

    private void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
        {
            Skipped++;
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Background, Deliver);
    }

    private void Deliver()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        if (_paused)
        {
            return;
        }

        var snapshot = Volatile.Read(ref _pending);
        if (ReferenceEquals(snapshot, Current))
        {
            return;
        }

        Current = snapshot;
        Updated?.Invoke(this, snapshot);
    }
}
