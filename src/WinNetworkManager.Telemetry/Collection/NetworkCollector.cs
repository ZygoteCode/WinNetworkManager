using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinNetworkManager.Core.Time;
using WinNetworkManager.Platform.Windows.Etw;
using WinNetworkManager.Platform.Windows.Interop;
using WinNetworkManager.Platform.Windows.Processes;
using WinNetworkManager.Telemetry.Decoding;
using WinNetworkManager.Telemetry.Model;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Telemetry.Collection;

/// <summary>Collector configuration.</summary>
public sealed record NetworkCollectorOptions
{
    /// <summary>
    /// Default ETW session name, fixed per Windows logon session ("WinNetworkManager-Collector-1"). ETW session names are
    /// global to the machine: a fixed name lets the next start replace a session left behind by a crash (real-time
    /// sessions outlive their process), and the logon-session suffix keeps users signed in at the same time from stopping
    /// each other's collector.
    /// </summary>
    public static string DefaultSessionName { get; } = SessionNameFor(CurrentWindowsSessionId());

    /// <summary>Session name used for Windows logon session <paramref name="windowsSessionId"/>.</summary>
    public static string SessionNameFor(int windowsSessionId) =>
        "WinNetworkManager-Collector-" + windowsSessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static int CurrentWindowsSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    /// <summary>ETW session name.</summary>
    public string SessionName { get; init; } = DefaultSessionName;

    /// <summary>Bucket duration (resolution of rates).</summary>
    public TimeSpan BucketDuration { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long after a bucket ends it is considered complete.</summary>
    public TimeSpan FinalizationDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>ETW buffer flush interval.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Maximum flows tracked individually per bucket (memory bound).</summary>
    public int MaxFlowsPerBucket { get; init; } = 20_000;

    /// <summary>Status publication interval.</summary>
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Automatic restarts before giving up.</summary>
    public int MaxRestarts { get; init; } = 20;
}

/// <summary>
/// Real-time per-process network collector: one ETW session with Kernel-Network (IPv4 + IPv6) and Kernel-Process,
/// decoded without allocations on the consumer thread, attributed to process instances, bucketed by event time and
/// emitted as <see cref="TelemetryFrame"/>s. Restarts automatically when the session is stopped externally; reports
/// lost events. Requires an elevated token (or Performance Log Users membership).
/// </summary>
public sealed class NetworkCollector : IAsyncDisposable, IEtwEventSink
{
    private readonly NetworkCollectorOptions _options;
    private readonly ITelemetrySink _sink;
    private readonly ILogger _logger;
    private readonly BucketTimeline _timeline;
    private readonly TrafficBucketer _bucketer;
    private readonly KernelNetworkDecoder _networkDecoder = new();
    private readonly KernelProcessDecoder _processDecoder = new();
    private readonly LocalAddressSet _localAddresses = new();
    private readonly object _lifecycleGate = new();
    private ProcessInstanceTracker _tracker;
    private EtwRealTimeSession? _session;
    private EtwRealTimeConsumer? _consumer;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private volatile CollectorState _state = CollectorState.Stopped;
    private long _totalEvents;
    private long _totalLost;
    private long _totalLate;
    private long _decodeFailures;
    private int _restarts;
    private string? _lastError;
    private bool _accessDenied;
    private EtwSessionStatistics _lastStats;

    /// <summary>Creates a collector writing to <paramref name="sink"/>.</summary>
    public NetworkCollector(NetworkCollectorOptions options, ITelemetrySink sink, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _logger = logger ?? NullLogger.Instance;
        _timeline = new BucketTimeline(Stopwatch.Frequency, options.BucketDuration);
        _bucketer = new TrafficBucketer(_timeline, options.FinalizationDelay, options.MaxFlowsPerBucket);
        _tracker = NewTracker();
    }

    /// <summary>Bucket timeline used for frames.</summary>
    public BucketTimeline Timeline => _timeline;

    /// <summary>Current state.</summary>
    public CollectorState State => _state;

    /// <summary>Current status.</summary>
    public CollectorStatus Status => BuildStatus(0);

    /// <summary>Starts the session. Throws <see cref="NativeCallException"/> when the session cannot be created.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleGate)
        {
            if (_state is CollectorState.Running or CollectorState.Starting or CollectorState.Retrying)
            {
                return Task.CompletedTask;
            }

            _state = CollectorState.Starting;
            try
            {
                StartCore();
            }
            catch (Exception ex)
            {
                _accessDenied = EtwRealTimeSession.IsAccessDenied(ex);
                _lastError = ErrorText.Describe(ex);
                _state = CollectorState.Faulted;
                StopCore();
                _sink.OnStatus(BuildStatus(0));
                throw;
            }

            _loopCts = new CancellationTokenSource();
            _loop = Task.Factory.StartNew(() => RunLoopAsync(_loopCts.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }

        return Task.CompletedTask;
    }

    /// <summary>Stops the session and emits the remaining buckets.</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_lifecycleGate)
        {
            if (_state == CollectorState.Stopped)
            {
                return;
            }

            _loopCts?.Cancel();
            loop = _loop;
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        lock (_lifecycleGate)
        {
            StopCore();
            foreach (var frame in _bucketer.FinalizeAll(Stopwatch.GetTimestamp()))
            {
                _sink.OnFrame(frame);
            }

            _state = CollectorState.Stopped;
            _loopCts?.Dispose();
            _loopCts = null;
            _loop = null;
            _sink.OnStatus(BuildStatus(0));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _localAddresses.Dispose();
    }

    unsafe void IEtwEventSink.OnEvent(EtwNative.EVENT_RECORD* record)
    {
        _totalEvents++;
        Guid provider = record->EventHeader.ProviderId;
        if (provider == KernelNetworkDecoder.ProviderId)
        {
            if (_networkDecoder.TryDecode(record, out NetworkEvent ev))
            {
                if (ev.Kind is NetworkEventKind.TcpSend or NetworkEventKind.TcpReceive or NetworkEventKind.UdpSend
                    or NetworkEventKind.UdpReceive or NetworkEventKind.TcpRetransmit)
                {
                    var process = _tracker.Resolve(ev.Pid, ev.Timestamp);
                    _bucketer.AddNetworkEvent(ev, process, _localAddresses.IsLocalMachine(ev.Remote.Address));
                }
                else
                {
                    _bucketer.CountEvent(ev.Timestamp);
                }
            }
            else if (KernelNetworkDecoder.KindOf(record->EventHeader.EventDescriptor.Id) != NetworkEventKind.Unknown)
            {
                _decodeFailures++;
                _bucketer.AddDecodeFailure(record->EventHeader.TimeStamp);
            }

            return;
        }

        if (provider == KernelProcessDecoder.ProviderId)
        {
            var ev = _processDecoder.TryDecode(record);
            if (ev is null)
            {
                return;
            }

            if (ev.Kind == ProcessLifecycleKind.Started)
            {
                _tracker.OnStarted(ev.Process, ev.Timestamp);
                ev = ev with { ImageName = ev.ImageName is null ? null : DevicePathConverter.ToWin32Path(ev.ImageName) };
            }
            else
            {
                _tracker.OnExited(ev.Process);
            }

            _bucketer.AddProcessEvent(ev);
        }
    }

    private void StartCore()
    {
        _session = EtwRealTimeSession.Start(new EtwSessionOptions(_options.SessionName, FlushInterval: _options.FlushInterval));
        _session.EnableProvider(KernelNetworkDecoder.ProviderId, 4, KernelNetworkDecoder.KeywordIPv4 | KernelNetworkDecoder.KeywordIPv6);
        _session.EnableProvider(KernelProcessDecoder.ProviderId, 4, KernelProcessDecoder.KeywordProcess);
        _lastStats = default;

        // Seed instances that already exist. Events raised meanwhile wait in the session buffers.
        _tracker = NewTracker();
        _tracker.Seed(new ProcessSnapshotReader().ReadProcesses().Select(p => p.Key));
        _localAddresses.Start();
        _bucketer.Start(Stopwatch.GetTimestamp());

        _consumer = new EtwRealTimeConsumer(_options.SessionName, this);
        _consumer.Start();
        _state = CollectorState.Running;
        _lastError = null;
        _accessDenied = false;
        _logger.LogInformation("ETW collector started (session {Session}, millisecond flush timer: {MsFlush}).", _options.SessionName, _session.UsesMillisecondFlushTimer);
    }

    private void StopCore()
    {
        try
        {
            _session?.Stop();
        }
        finally
        {
            _consumer?.Dispose();
            _consumer = null;
            _session = null;
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Clamp(_options.BucketDuration.TotalMilliseconds, 50, 1000));
        using var timer = new PeriodicTimer(tick);
        long lastStatus = Stopwatch.GetTimestamp();
        long lastEvents = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (_consumer?.Completion.IsCompleted == true && _state == CollectorState.Running)
                {
                    await RestartAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (Stopwatch.GetElapsedTime(lastStatus) >= _options.StatusInterval)
                {
                    double seconds = Stopwatch.GetElapsedTime(lastStatus).TotalSeconds;
                    lastStatus = Stopwatch.GetTimestamp();
                    QueryLosses();
                    long events = Interlocked.Read(ref _totalEvents);
                    _sink.OnStatus(BuildStatus((events - lastEvents) / seconds));
                    lastEvents = events;
                }

                foreach (var frame in _bucketer.Finalize(Stopwatch.GetTimestamp()))
                {
                    _totalLate += frame.Health.LateEvents;
                    _sink.OnFrame(frame);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Collector loop iteration failed.");
                _lastError = ErrorText.Describe(ex);
            }
        }
    }

    private void QueryLosses()
    {
        var session = _session;
        if (session is null || !session.IsRunning)
        {
            return;
        }

        try
        {
            var stats = session.QueryStatistics();
            long lost = (stats.EventsLost - (long)_lastStats.EventsLost) + (stats.RealTimeBuffersLost - (long)_lastStats.RealTimeBuffersLost);
            _lastStats = stats;
            if (lost > 0)
            {
                _totalLost += lost;
                _bucketer.AddLostEvents(lost);
                _logger.LogWarning("ETW reported {Lost} lost events/buffers; affected intervals are marked partial.", lost);
            }
        }
        catch (NativeCallException ex)
        {
            _logger.LogDebug(ex, "Session statistics query failed.");
        }
    }

    private async Task RestartAsync(CancellationToken cancellationToken)
    {
        uint code = _consumer!.Completion.IsCompletedSuccessfully ? _consumer.Completion.Result : uint.MaxValue;
        _logger.LogWarning("ETW consumer stopped unexpectedly (ProcessTrace returned {Code}); restarting.", code);
        lock (_lifecycleGate)
        {
            _state = CollectorState.Retrying;
            StopCore();
        }

        _sink.OnStatus(BuildStatus(0));
        while (!cancellationToken.IsCancellationRequested)
        {
            _restarts++;
            if (_restarts > _options.MaxRestarts)
            {
                _state = CollectorState.Faulted;
                _lastError = "The ETW session kept stopping; giving up after repeated restarts.";
                _sink.OnStatus(BuildStatus(0));
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(_restarts, 5)))), cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_lifecycleGate)
                {
                    StartCore();
                }

                _sink.OnStatus(BuildStatus(0));
                return;
            }
            catch (Exception ex) when (ex is NativeCallException or InvalidOperationException)
            {
                _lastError = ErrorText.Describe(ex);
                _accessDenied = EtwRealTimeSession.IsAccessDenied(ex);
                _logger.LogWarning(ex, "Collector restart attempt {Attempt} failed.", _restarts);
                lock (_lifecycleGate)
                {
                    StopCore();
                }

                if (_accessDenied)
                {
                    _state = CollectorState.Faulted;
                    _sink.OnStatus(BuildStatus(0));
                    return;
                }
            }
        }
    }

    private CollectorStatus BuildStatus(double eventsPerSecond) => new(
        _state,
        _options.SessionName,
        eventsPerSecond,
        Interlocked.Read(ref _totalEvents),
        _totalLost,
        _totalLate,
        _decodeFailures,
        _restarts,
        _session?.UsesMillisecondFlushTimer ?? false,
        _lastError,
        _accessDenied);

    private static ProcessInstanceTracker NewTracker() =>
        new(pid => ProcessSnapshotReader.ReadCreateTime(pid).CreateTime);
}
