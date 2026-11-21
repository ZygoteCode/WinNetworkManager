using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Core.Time;
using WinNetworkManager.Core.Traffic;
using WinNetworkManager.Engine.Collector;
using WinNetworkManager.Engine.Connections;
using WinNetworkManager.Engine.History;
using WinNetworkManager.Engine.Interfaces;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Engine.Notifications;
using WinNetworkManager.Engine.Processes;
using WinNetworkManager.Engine.Traffic;
using WinNetworkManager.Infrastructure.History;
using WinNetworkManager.Platform.Windows.IpHelper;
using WinNetworkManager.Platform.Windows.Processes;
using WinNetworkManager.Telemetry.Collection;
using WinNetworkManager.Telemetry.Model;
using WinNetworkManager.Telemetry.Sources;

namespace WinNetworkManager.Engine;

/// <summary>Data sources of the engine (replaceable in tests).</summary>
public sealed class MonitoringEngineSources
{
    /// <summary>Process snapshots.</summary>
    public IProcessSnapshotSource Processes { get; init; } = new ProcessSnapshotReader();

    /// <summary>Connection tables; null = default reader with svchost owner-module resolution.</summary>
    public IConnectionTableSource? Connections { get; init; }

    /// <summary>Interface counters.</summary>
    public IInterfaceTableSource Interfaces { get; init; } = new InterfaceTableReader();

    /// <summary>Read per-process services (svchost) — disabled in tests.</summary>
    public bool ReadServices { get; init; } = true;

    /// <summary>Enrich processes (path, version, signature) — disabled in tests.</summary>
    public bool Enrich { get; init; } = true;

    internal IInterfaceConfigurationSource Configuration { get; init; } = new NetworkInterfaceConfigurationSource();
}

/// <summary>Per-second series of one process or of the system, for charts.</summary>
/// <param name="Start">Start of the first second (UTC).</param>
/// <param name="Values">Per-second bytes (TCP/UDP × receive/send).</param>
public sealed record TrafficSeries(DateTimeOffset Start, IReadOnlyList<ProtocolBytes> Values, IReadOnlyList<bool> Covered)
{
    /// <summary>Per-second values as doubles, NaN where the collector did not cover the second (unknown, not zero).</summary>
    public double[] Select(Func<ProtocolBytes, long> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new double[Values.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = i < Covered.Count && Covered[i] ? value(Values[i]) : double.NaN;
        }

        return result;
    }
}

/// <summary>
/// The monitoring engine: a single loop that ingests collector frames, polls processes, connections and interfaces,
/// and publishes immutable <see cref="EngineSnapshot"/>s at the refresh interval. All mutable state is owned by the
/// loop; other threads interact through the frame channel, settings swap and <see cref="InvokeAsync{T}"/>.
/// </summary>
public sealed class MonitoringEngine : ITelemetrySink, IAsyncDisposable
{
    private static readonly TimeSpan ClosedConnectionRetention = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleExitedRetention = TimeSpan.FromSeconds(3);
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly MonitoringEngineSources _sources;
    private readonly IConnectionTableSource _connectionSource;
    private readonly ConnectionTableReader? _ownedConnectionReader;
    private readonly BucketTimeline _timeline;
    private readonly int _bucketsPerSecond;
    private readonly Channel<TelemetryFrame> _frames = Channel.CreateBounded<TelemetryFrame>(new BoundedChannelOptions(16_384) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Channel<Action> _work = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ProcessCatalog _catalog;
    private readonly ProcessEnricher? _enricher;
    private readonly ConnectionTracker _connections = new();
    private readonly InterfaceMonitor _interfaces;
    private readonly HostNameResolver _resolver = new();
    private readonly NotificationEvaluator _notifications = new();
    private readonly TrafficAccumulator _system;
    private readonly BucketRing<Coverage> _coverage = new(2048); // 512 s: covers the 5-minute series
    private readonly HashSet<int> _serviceHosts = [];
    private readonly HistoryRecorder _history;
    private SqliteHistoryStore? _historyStore;
    private DateTimeOffset _nextPrune;
    private AppSettings _settings;
    private CollectorStatus? _collectorStatus;
    private long _lastBucket = -1;
    private DateTimeOffset? _dataAsOf;
    private DateTimeOffset? _coverageStart;
    private DateTimeOffset _nextProcessPoll;
    private DateTimeOffset _nextConnectionPoll;
    private DateTimeOffset _nextServices;
    private long _sequence;
    private long _droppedFrames;
    private bool _collectorStopRequested;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private EngineSnapshot _latest;

    /// <summary>Creates the engine.</summary>
    public MonitoringEngine(
        AppSettings settings,
        ILoggerFactory loggerFactory,
        TimeProvider? time = null,
        MonitoringEngineSources? sources = null,
        NetworkCollectorOptions? collectorOptions = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _settings = SettingsValidator.Validate(settings).Settings;
        _logger = loggerFactory.CreateLogger<MonitoringEngine>();
        _time = time ?? TimeProvider.System;
        _sources = sources ?? new MonitoringEngineSources();
        var options = collectorOptions ?? new NetworkCollectorOptions();
        _timeline = new BucketTimeline(_time.TimestampFrequency, options.BucketDuration);
        _bucketsPerSecond = Math.Max(1, (int)Math.Round(1 / options.BucketDuration.TotalSeconds));
        _system = new TrafficAccumulator(_bucketsPerSecond, _settings.History.InMemoryMinutes * 60);
        _catalog = new ProcessCatalog(_logger);
        _enricher = _sources.Enrich ? new ProcessEnricher(_logger) : null;
        if (_sources.Connections is null)
        {
            _ownedConnectionReader = new ConnectionTableReader(pid => _serviceHosts.Contains(pid));
            _connectionSource = _ownedConnectionReader;
        }
        else
        {
            _connectionSource = _sources.Connections;
        }

        _interfaces = new InterfaceMonitor(_sources.Interfaces, _sources.Configuration, _time.TimestampFrequency, _logger);
        _history = new HistoryRecorder(_timeline, _bucketsPerSecond, _settings.History.InMemoryMinutes * 60, _logger);
        _interfaces.DeltaObserver = (id, name, previous, current, rx, tx, physical) => _history.OnInterface(id, name, previous, current, rx, tx, physical);
        Collector = new CollectorController(this, options, loggerFactory);
        _latest = EngineSnapshot.Empty(Collector.Privileges);
    }

    /// <summary>Collector management.</summary>
    public CollectorController Collector { get; }

    /// <summary>Most recent snapshot.</summary>
    public EngineSnapshot Latest => Volatile.Read(ref _latest);

    /// <summary>Frames dropped because the engine could not keep up (should stay 0).</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>Raised on the engine thread after each snapshot. Handlers must not block.</summary>
    public event EventHandler<EngineSnapshot>? SnapshotPublished;

    /// <summary>Raised on the engine thread for notifications.</summary>
    public event EventHandler<EngineNotification>? NotificationRaised;

    /// <summary>Swaps settings (thread-safe; applied at the next cycle).</summary>
    public void ApplySettings(AppSettings settings)
    {
        var normalized = SettingsValidator.Validate(settings).Settings;
        Volatile.Write(ref _settings, normalized);
        _resolver.Enabled = normalized.Monitoring.ResolveRemoteHostNames;
        _history.InMemorySeconds = normalized.History.InMemoryMinutes * 60;
    }

    /// <summary>Attaches (or detaches with null) the persistent history store.</summary>
    public void AttachHistoryStore(SqliteHistoryStore? store)
    {
        Volatile.Write(ref _historyStore, store);
        _history.Sink = store;
        _nextPrune = default;
    }

    /// <summary>Recent per-second system samples kept in memory (engine thread).</summary>
    public Task<IReadOnlyList<SystemSample>> GetRecentSystemSamplesAsync(TimeSpan range) => InvokeAsync(() => _history.Recent(range));

    /// <summary>History batches dropped (writer failure or overload).</summary>
    public long DroppedHistoryBatches => _history.DroppedBatches;

    /// <summary>Starts the loop.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _resolver.Enabled = _settings.Monitoring.ResolveRemoteHostNames;
        _interfaces.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Factory.StartNew(() => RunAsync(_cts.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        return Task.CompletedTask;
    }

    /// <summary>Starts the collector (see <see cref="CollectorController.StartAsync"/>).</summary>
    public Task<CollectorStartResult> StartCollectorAsync(bool allowElevationPrompt, CancellationToken cancellationToken = default)
    {
        _collectorStopRequested = false;
        return Collector.StartAsync(allowElevationPrompt, cancellationToken);
    }

    /// <summary>Stops the collector at the user's request.</summary>
    public Task StopCollectorAsync()
    {
        _collectorStopRequested = true;
        return Collector.StopAsync();
    }

    private long _lastCycleTimestamp;

    /// <summary>Stops the loop and the collector.</summary>
    public async Task StopAsync()
    {
        _collectorStopRequested = true;
        await Collector.StopAsync().ConfigureAwait(false);
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _loop = null;
        _cts?.Dispose();
        _cts = null;

        // The loop has ended, so the recorder can be used from this thread: keep the partial current minute.
        if (_lastCycleTimestamp != 0)
        {
            _history.Flush(_lastCycleTimestamp, collectorRunning: false, final: true);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await Collector.DisposeAsync().ConfigureAwait(false);
        if (_enricher is not null)
        {
            await _enricher.DisposeAsync().ConfigureAwait(false);
        }

        _interfaces.Dispose();
        _resolver.Dispose();
        await _history.DisposeAsync().ConfigureAwait(false);
        _ownedConnectionReader?.Dispose();
    }

    /// <summary>Runs <paramref name="query"/> on the engine thread (between cycles) and returns its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_loop is null)
        {
            // Not running (tests): execute inline.
            try
            {
                tcs.SetResult(query());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }

            return tcs.Task;
        }

        _work.Writer.TryWrite(() =>
        {
            try
            {
                tcs.TrySetResult(query());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>Per-second series of one process for the last <paramref name="seconds"/> seconds (engine thread).</summary>
    public Task<TrafficSeries?> GetProcessSeriesAsync(ProcessKey key, int seconds) => InvokeAsync(() =>
    {
        var record = _catalog.Find(key);
        if (record?.Traffic is null || _lastBucket < 0)
        {
            return null;
        }

        long lastSecond = _lastBucket / _bucketsPerSecond;
        var (values, first) = record.Traffic.Seconds(lastSecond, seconds, Volatile.Read(ref _settings).Monitoring.IncludeLoopbackInTotals);
        return new TrafficSeries(_timeline.ToUtc(_timeline.StartOf(first * _bucketsPerSecond)), values, CoverageMask(first, values.Length));
    });

    /// <summary>Per-second attributed system series (engine thread).</summary>
    public Task<TrafficSeries?> GetSystemSeriesAsync(int seconds) => InvokeAsync(() =>
    {
        if (_lastBucket < 0)
        {
            return null;
        }

        long lastSecond = _lastBucket / _bucketsPerSecond;
        var (values, first) = _system.Seconds(lastSecond, seconds, Volatile.Read(ref _settings).Monitoring.IncludeLoopbackInTotals);
        return (TrafficSeries?)new TrafficSeries(_timeline.ToUtc(_timeline.StartOf(first * _bucketsPerSecond)), values, CoverageMask(first, values.Length));
    });

    void ITelemetrySink.OnFrame(TelemetryFrame frame)
    {
        if (!_frames.Writer.TryWrite(frame))
        {
            Interlocked.Increment(ref _droppedFrames);
        }
    }

    void ITelemetrySink.OnStatus(CollectorStatus status) => Volatile.Write(ref _collectorStatus, status);

    /// <summary>Runs one acquisition + publication cycle (the loop calls this; tests call it directly).</summary>
    internal EngineSnapshot RunCycle()
    {
        var settings = Volatile.Read(ref _settings);
        var now = _time.GetUtcNow();
        long timestamp = _time.GetTimestamp();
        _timeline.Anchor(timestamp, now);

        Stage("frames", () =>
        {
            while (_frames.Reader.TryRead(out var frame))
            {
                Ingest(frame);
            }
        });

        Stage("processes", () =>
        {
            if (now >= _nextProcessPoll)
            {
                _catalog.ApplySnapshot(_sources.Processes.ReadProcesses(), now);
                _nextProcessPoll = now + TimeSpan.FromMilliseconds(settings.Monitoring.ProcessPollIntervalMs);
            }

            if (_sources.ReadServices && now >= _nextServices)
            {
                var services = ServiceMap.Read();
                _catalog.ApplyServices(services);
                _serviceHosts.Clear();
                foreach (var (pid, list) in services)
                {
                    if (list.Count > 0)
                    {
                        _serviceHosts.Add(pid);
                    }
                }

                _nextServices = now + TimeSpan.FromSeconds(30);
            }

            Enrich();
        });

        Stage("connections", () =>
        {
            if (now >= _nextConnectionPoll)
            {
                _connections.Poll(_connectionSource, _catalog, now, IsCollectorActive());
                _nextConnectionPoll = now + TimeSpan.FromMilliseconds(settings.Monitoring.ConnectionPollIntervalMs) - TimeSpan.FromMilliseconds(50);
            }
        });

        Stage("interfaces", () => _interfaces.Sample(timestamp, now));

        Stage("history", () =>
        {
            _history.Flush(timestamp, IsCollectorActive());
            _lastCycleTimestamp = timestamp;
            if (Volatile.Read(ref _historyStore) is { } store && now >= _nextPrune)
            {
                _nextPrune = now + TimeSpan.FromHours(1);
                int removed = store.Prune(now - TimeSpan.FromHours(24), now - TimeSpan.FromDays(settings.History.RetentionDays));
                _logger.LogDebug("History retention removed {Rows} rows.", removed);
            }
        });

        Stage("prune", () =>
        {
            _catalog.Prune(now, TimeSpan.FromSeconds(settings.Monitoring.ExitedProcessRetentionSeconds), IdleExitedRetention);
            _connections.Prune(now, ClosedConnectionRetention);
        });

        var snapshot = BuildSnapshot(settings, now, timestamp);
        Volatile.Write(ref _latest, snapshot);
        try
        {
            SnapshotPublished?.Invoke(this, snapshot);
            foreach (var notification in _notifications.Evaluate(snapshot, settings.Notifications, settings.Appearance.Units, _collectorStopRequested))
            {
                NotificationRaised?.Invoke(this, notification);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "A snapshot subscriber failed.");
        }

        return snapshot;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        int period = Volatile.Read(ref _settings).Monitoring.RefreshIntervalMs;
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(period), _time);
        try
        {
            RunCycleSafely();
            Task<bool> tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
            while (!cancellationToken.IsCancellationRequested)
            {
                var work = _work.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var completed = await Task.WhenAny(tick, work).ConfigureAwait(false);
                if (completed == tick)
                {
                    if (!await tick.ConfigureAwait(false))
                    {
                        break;
                    }

                    RunCycleSafely();
                    int desired = Volatile.Read(ref _settings).Monitoring.RefreshIntervalMs;
                    if (desired != period)
                    {
                        period = desired;
                        timer.Dispose();
                        timer = new PeriodicTimer(TimeSpan.FromMilliseconds(period), _time);
                    }

                    tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                }

                while (_work.Reader.TryRead(out var action))
                {
                    action();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        finally
        {
            timer.Dispose();
        }
    }

    private void RunCycleSafely()
    {
        try
        {
            RunCycle();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Engine cycle failed.");
        }
    }

    private void Stage(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Engine stage {Stage} failed.", name);
        }
    }

    // Every source reports NotRunning when it stops (see CollectorController.Attach), so the status alone is authoritative.
    private bool IsCollectorActive() => Volatile.Read(ref _collectorStatus)?.State == CollectorState.Running;

    private void Ingest(TelemetryFrame frame)
    {
        if (_lastBucket >= 0 && frame.BucketIndex <= _lastBucket)
        {
            return; // duplicate or reordered after a restart
        }

        var when = _timeline.ToUtc(frame.EndTimestamp);
        _coverageStart ??= _timeline.ToUtc(frame.StartTimestamp);
        _coverage.Add(frame.BucketIndex, new Coverage(1, frame.Health.IsPartial ? 1 : 0));

        foreach (var ev in frame.ProcessEvents.OrderBy(e => e.Timestamp))
        {
            _catalog.ApplyLifecycle(ev, _timeline.ToUtc(ev.Timestamp));
        }

        var historySeconds = Volatile.Read(ref _settings).History.InMemoryMinutes * 60;
        TrafficCounters frameTotal = default;
        foreach (var traffic in frame.Processes)
        {
            var record = _catalog.ResolveForTraffic(traffic.Process, when);
            record.Traffic ??= new TrafficAccumulator(_bucketsPerSecond, historySeconds);
            record.Traffic.Add(frame.BucketIndex, traffic.Counters);
            frameTotal += traffic.Counters;
            _history.OnApp(frame.BucketIndex, record.AppId, record.Name, record.Path.HasValue ? record.Path.Value : null, traffic.Counters.External);
            record.MonitoredSince ??= Max(_coverageStart.Value, record.StartTime ?? record.FirstSeen);
            if (!traffic.Counters.IsZero)
            {
                record.LastActivity = when;
            }

            _system.Add(frame.BucketIndex, traffic.Counters);
        }

        _history.OnBucket(frame.BucketIndex, frameTotal, frame.Health.IsPartial);
        foreach (var flow in frame.Flows)
        {
            var owner = _catalog.ResolveForTraffic(flow.Key.Process, when);
            _connections.ApplyFlow(flow, frame.BucketIndex, owner, when);
        }

        _lastBucket = frame.BucketIndex;
        _dataAsOf = when;
    }

    private void Enrich()
    {
        if (_enricher is null)
        {
            return;
        }

        foreach (var record in _catalog.Records)
        {
            if (!record.BasicEnrichmentRequested && record.State == ProcessState.Running)
            {
                record.BasicEnrichmentRequested = true;
                _enricher.Request(record.Key, record.Path.HasValue ? record.Path.Value : null);
            }
        }

        while (_enricher.TryTake(out var result) && result is not null)
        {
            var record = _catalog.Find(result.Key);
            if (record is null)
            {
                continue;
            }

            if (result.Path is { } path && (path.HasValue || !record.Path.HasValue))
            {
                record.Path = path;
            }

            if (result.SessionId is { } session)
            {
                record.SessionId = session;
            }

            record.UserName ??= result.UserName;
            if (result.IsServiceAccount is { } service)
            {
                record.IsServiceAccount = service;
            }
            else if (result.Path?.Status == InfoStatus.AccessDenied)
            {
                record.IsServiceAccount = true; // unreadable token with a denied path: almost always a system service
            }

            if (result.File is { } file)
            {
                record.Description = file.Description;
                record.Product = file.Product;
                record.Company = file.Company;
                record.FileVersion = file.Version;
                record.Signature = file.Signature;
            }
        }
    }

    private EngineSnapshot BuildSnapshot(AppSettings settings, DateTimeOffset now, long timestamp)
    {
        bool includeLoopback = settings.Monitoring.IncludeLoopbackInTotals;
        int windowBuckets = Math.Max(1, (int)Math.Round(settings.Monitoring.RefreshIntervalMs / _timeline.BucketDuration.TotalMilliseconds));
        double windowSeconds = windowBuckets * _timeline.BucketDuration.TotalSeconds;
        var status = Volatile.Read(ref _collectorStatus);
        bool active = IsCollectorActive() && _lastBucket >= 0;
        bool partial = active && WindowPartial(windowBuckets);
        var availability = active
            ? (partial ? TrafficAvailability.Partial : TrafficAvailability.Available)
            : status?.State is CollectorState.Starting or CollectorState.Retrying ? TrafficAvailability.Starting : TrafficAvailability.RequiresCollector;

        long lastSecond = _lastBucket < 0 ? 0 : _lastBucket / _bucketsPerSecond;
        var sockets = _connections.CountByProcess();
        var processes = new List<ProcessView>(_catalog.Records.Count);
        int activeProcesses = 0;
        foreach (var record in _catalog.Records)
        {
            var traffic = record.Traffic;
            TrafficRates rates = default;
            ProtocolBytes totals = default;
            ProtocolBytes loopback = default;
            double peakRx = 0, peakTx = 0;
            IReadOnlyList<float> spark = [];
            long retransmitted = 0;
            if (traffic is not null)
            {
                if (active)
                {
                    rates = TrafficRates.FromBytes(traffic.Window(_lastBucket, windowBuckets).Select(includeLoopback), windowSeconds);
                }

                totals = traffic.Totals.Select(includeLoopback);
                loopback = traffic.Totals.Loopback;
                retransmitted = traffic.Totals.TcpRetransmittedBytes;
                peakRx = includeLoopback ? traffic.PeakAllReceive : traffic.PeakExternalReceive;
                peakTx = includeLoopback ? traffic.PeakAllSend : traffic.PeakExternalSend;
                spark = Covered(traffic.History(lastSecond, 60, includeLoopback, receive: true, send: true), lastSecond);
            }

            if (rates.Total > 0)
            {
                activeProcesses++;
            }

            var monitored = record.MonitoredSince is { } since ? (record.ExitTime ?? now) - since : TimeSpan.Zero;
            if (monitored < TimeSpan.Zero)
            {
                monitored = TimeSpan.Zero;
            }

            sockets.TryGetValue(record, out var counts);
            processes.Add(new ProcessView
            {
                Key = record.Key,
                Name = record.Name,
                Path = record.Path,
                Description = record.Description,
                Product = record.Product,
                Company = record.Company,
                Version = record.FileVersion,
                Signature = record.Signature,
                Services = record.Services,
                AppId = record.AppId,
                IsSystem = record.IsSystem,
                SessionId = record.SessionId,
                UserName = record.UserName,
                ParentPid = record.ParentPid,
                State = record.State,
                StartTime = record.StartTime,
                ExitTime = record.ExitTime,
                ExitCode = record.ExitCode,
                Identity = record.Key.HasCreateTime ? IdentityConfidence.Exact : IdentityConfidence.PidAndName,
                Traffic = traffic is null && active ? TrafficAvailability.Available : availability,
                Rates = rates,
                Totals = totals,
                LoopbackTotals = loopback,
                RetransmittedBytes = retransmitted,
                PeakReceiveRate = peakRx,
                PeakSendRate = peakTx,
                AverageReceiveRate = monitored.TotalSeconds >= 1 ? totals.Received / monitored.TotalSeconds : 0,
                AverageSendRate = monitored.TotalSeconds >= 1 ? totals.Sent / monitored.TotalSeconds : 0,
                MonitoredDuration = monitored,
                TcpConnections = counts.Tcp,
                TcpListeners = counts.Listeners,
                UdpEndpoints = counts.Udp,
                RemoteAddresses = counts.Remotes?.Count(a => !a.IsUnspecified) ?? 0,
                LastActivity = record.LastActivity,
                Sparkline = spark,
            });
        }

        var connections = BuildConnections(now, windowBuckets, windowSeconds, active);
        var udp = BuildUdp(windowBuckets, windowSeconds, active);
        var interfaces = _interfaces.BuildViews(timestamp);
        var physical = _interfaces.Physical;
        long physicalSecond = timestamp / _time.TimestampFrequency;
        var systemWindow = active ? _system.Window(_lastBucket, windowBuckets) : default;

        var system = new SystemTrafficView
        {
            AttributedRates = TrafficRates.FromBytes(systemWindow.Select(includeLoopback), windowSeconds),
            LoopbackRates = TrafficRates.FromBytes(systemWindow.Loopback, windowSeconds),
            AttributedTotals = _system.Totals.Select(includeLoopback),
            LoopbackTotals = _system.Totals.Loopback,
            PhysicalReceiveRate = physical.ReceiveRate,
            PhysicalSendRate = physical.SendRate,
            PhysicalReceived = physical.Received,
            PhysicalSent = physical.Sent,
            Traffic = availability,
            ActiveProcesses = activeProcesses,
            ProcessesWithSockets = sockets.Count(kv => kv.Value.Tcp + kv.Value.Udp > 0),
            TcpConnections = connections.Count(c => c.ClosedAt is null && TcpStateInfo.IsActive(c.State)),
            TcpListeners = connections.Count(c => c.ClosedAt is null && c.State == TcpState.Listen),
            TcpTimeWait = connections.Count(c => c.ClosedAt is null && c.State == TcpState.TimeWait),
            UdpEndpoints = udp.Count(u => u.ClosedAt is null),
            Connectivity = _interfaces.Connectivity,
            PhysicalReceiveHistory = physical.History.Rates(physicalSecond, 60, receive: true),
            PhysicalSendHistory = physical.History.Rates(physicalSecond, 60, receive: false),
            AttributedReceiveHistory = active ? Covered(_system.History(lastSecond, 60, includeLoopback, receive: true, send: false), lastSecond) : [],
            AttributedSendHistory = active ? Covered(_system.History(lastSecond, 60, includeLoopback, receive: false, send: true), lastSecond) : [],
        };

        var collector = new CollectorView
        {
            Kind = Collector.Kind,
            State = status?.State ?? CollectorState.Stopped,
            EventsPerSecond = status?.EventsPerSecond ?? 0,
            EventsLost = status?.TotalEventsLost ?? 0,
            LateEvents = status?.TotalLateEvents ?? 0,
            Error = status?.LastError ?? Collector.LastError,
            AccessDenied = status?.IsAccessDenied ?? false,
            DataAsOf = _dataAsOf,
            Privileges = Collector.Privileges,
            HelperAvailable = Collector.HelperAvailable,
            PartialData = partial,
            ElevationDeclined = Collector.ElevationDeclined,
        };

        return new EngineSnapshot
        {
            Sequence = ++_sequence,
            Timestamp = now,
            RateWindow = TimeSpan.FromSeconds(windowSeconds),
            Collector = collector,
            System = system,
            Processes = processes,
            Connections = connections,
            UdpEndpoints = udp,
            Interfaces = interfaces,
        };
    }

    private List<ConnectionView> BuildConnections(DateTimeOffset now, int windowBuckets, double windowSeconds, bool active)
    {
        var result = new List<ConnectionView>();
        foreach (var record in _connections.TcpRecords)
        {
            bool quiet = !active || record.LastBytesBucket < _lastBucket - windowBuckets;
            string? host = _resolver.Enabled && !record.Remote.Address.IsUnspecified ? _resolver.TryGet(record.Remote.Address) : null;
            if (quiet && record.Cached is { } cached && record.CachedVersion == record.Version && cached.RemoteHostName == host && cached.ReceiveRate == 0 && cached.SendRate == 0)
            {
                result.Add(cached);
                continue;
            }

            var window = active ? record.Window(_lastBucket, windowBuckets) : default;
            var scope = AddressClassifier.Classify(record.Remote.Address);
            var view = new ConnectionView
            {
                Id = record.Id,
                Local = record.Local,
                Remote = record.Remote,
                State = record.State,
                Owner = record.Owner?.Key,
                OwnerPid = record.OwnerPid,
                OwnerName = OwnerName(record.Owner, record.OwnerPid),
                OwnerModule = record.OwnerModule,
                FirstSeen = record.FirstSeen,
                CreatedAt = record.CreatedAt,
                ClosedAt = record.ClosedAt,
                RemoteScope = scope,
                IsLoopback = scope == AddressScope.Loopback || AddressClassifier.IsLoopback(record.Local.Address),
                ReceivedBytes = record.Received,
                SentBytes = record.Sent,
                ReceiveRate = window.Received / windowSeconds,
                SendRate = window.Sent / windowSeconds,
                Retransmissions = record.Retransmissions,
                BytesAvailable = record.BytesAvailable,
                RemoteHostName = host,
                Origin = record.Origin,
            };
            record.Cached = view;
            record.CachedVersion = record.Version;
            result.Add(view);
        }

        return result;
    }

    private List<UdpEndpointView> BuildUdp(int windowBuckets, double windowSeconds, bool active)
    {
        var result = new List<UdpEndpointView>();
        foreach (var record in _connections.UdpRecords)
        {
            bool quiet = !active || record.LastBytesBucket < _lastBucket - windowBuckets;
            if (quiet && record.Cached is { } cached && record.CachedVersion == record.Version && cached.ReceiveRate == 0 && cached.SendRate == 0)
            {
                result.Add(cached);
                continue;
            }

            var window = active ? record.Window(_lastBucket, windowBuckets) : default;
            var view = new UdpEndpointView
            {
                Id = record.Id,
                Local = record.Local,
                Owner = record.Owner?.Key,
                OwnerPid = record.OwnerPid,
                OwnerName = OwnerName(record.Owner, record.OwnerPid),
                OwnerModule = record.OwnerModule,
                FirstSeen = record.FirstSeen,
                CreatedAt = record.CreatedAt,
                ClosedAt = record.ClosedAt,
                ReceivedBytes = record.Received,
                SentBytes = record.Sent,
                ReceiveRate = window.Received / windowSeconds,
                SendRate = window.Sent / windowSeconds,
                Peers = record.Peers
                    .OrderByDescending(p => p.Value.LastSeen)
                    .Take(32)
                    .Select(p => new UdpPeerView(p.Key, p.Value.Received, p.Value.Sent, p.Value.LastSeen, AddressClassifier.Classify(p.Key.Address)))
                    .ToArray(),
                PeerCount = record.PeerCount,
                BytesAvailable = record.BytesAvailable,
            };
            record.Cached = view;
            record.CachedVersion = record.Version;
            result.Add(view);
        }

        return result;
    }

    /// <summary>
    /// True for each second (from <paramref name="firstSecond"/>) for which at least one bucket reached the engine — the
    /// same rule as the history (a partly covered second, at collector start or after dropped frames, is a lower bound
    /// flagged elsewhere as partial). Seconds without any bucket are unknown and must be drawn as gaps, never as zero.
    /// </summary>
    private bool[] CoverageMask(long firstSecond, int count)
    {
        var mask = new bool[count];
        for (int i = 0; i < count; i++)
        {
            mask[i] = IsCovered(firstSecond + i);
        }

        return mask;
    }

    /// <summary>Replaces seconds that were not covered by the collector (before it started, or lost frames) with NaN.</summary>
    private float[] Covered(float[] perSecond, long lastSecond)
    {
        long first = lastSecond - perSecond.Length + 1;
        for (int i = 0; i < perSecond.Length; i++)
        {
            if (!IsCovered(first + i))
            {
                perSecond[i] = float.NaN;
            }
        }

        return perSecond;
    }

    private bool IsCovered(long second)
    {
        for (int b = 0; b < _bucketsPerSecond; b++)
        {
            if (_coverage.Get((second * _bucketsPerSecond) + b).Frames > 0)
            {
                return true;
            }
        }

        return false;
    }

    private bool WindowPartial(int windowBuckets)
    {
        for (long i = _lastBucket - windowBuckets + 1; i <= _lastBucket; i++)
        {
            var c = _coverage.Get(i);
            if (c.Frames == 0 || c.Partial > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string OwnerName(ProcessRecord? owner, int pid) => owner is null
        ? (pid == 0 ? "—" : $"PID {pid}")
        : pid == 0 && owner.Key.Pid != 0 ? owner.Name + " (closed)" : owner.Name;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private readonly record struct Coverage(int Frames, int Partial) : System.Numerics.IAdditionOperators<Coverage, Coverage, Coverage>
    {
        public static Coverage operator +(Coverage left, Coverage right) => new(left.Frames + right.Frames, left.Partial + right.Partial);
    }
}
