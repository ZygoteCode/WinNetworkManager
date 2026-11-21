using Microsoft.Extensions.Logging;
using WinNetworkManager.Platform.Windows.Security;
using WinNetworkManager.Telemetry.Collection;
using WinNetworkManager.Telemetry.Ipc;
using WinNetworkManager.Telemetry.Model;
using WinNetworkManager.Telemetry.Sources;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Engine.Collector;

/// <summary>Result of a collector start request.</summary>
public enum CollectorStartResult : byte
{
    /// <summary>Started in this process.</summary>
    StartedInProcess,

    /// <summary>Started in the elevated helper.</summary>
    StartedElevatedHelper,

    /// <summary>Already running.</summary>
    AlreadyRunning,

    /// <summary>Elevation is required and the caller did not allow a prompt.</summary>
    ElevationRequired,

    /// <summary>The user declined the UAC prompt.</summary>
    ElevationDeclined,

    /// <summary>The helper executable is missing.</summary>
    HelperMissing,

    /// <summary>Starting failed.</summary>
    Failed,
}

/// <summary>
/// Chooses and owns the telemetry source: in-process when the UI already has ETW rights, otherwise the elevated
/// helper (only when the user asks, because it shows a UAC prompt). Thread-safe.
/// </summary>
public sealed class CollectorController : IAsyncDisposable
{
    private readonly ITelemetrySink _sink;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NetworkCollectorOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITelemetrySource? _source;

    /// <summary>Creates the controller.</summary>
    public CollectorController(ITelemetrySink sink, NetworkCollectorOptions options, ILoggerFactory loggerFactory)
    {
        _sink = sink;
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CollectorController>();
        Privileges = PrivilegeInfo.ReadCurrent();
    }

    /// <summary>Privileges of this process.</summary>
    public PrivilegeInfo Privileges { get; }

    /// <summary>Active source kind.</summary>
    public TelemetrySourceKind Kind { get; private set; } = TelemetrySourceKind.None;

    /// <summary>Last error (start failure, helper exit).</summary>
    public string? LastError { get; private set; }

    /// <summary>True after the user declined the UAC prompt (until the next successful start).</summary>
    public bool ElevationDeclined { get; private set; }

    /// <summary>True when the elevated helper is installed next to the application.</summary>
    public bool HelperAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, ElevatedHelperTelemetrySource.HelperFileName));

    /// <summary>Raised when the source stops unexpectedly (helper exited, pipe broken).</summary>
    public event EventHandler? SourceStopped;

    /// <summary>Starts a collector.</summary>
    /// <param name="allowElevationPrompt">Allow showing the UAC prompt to start the helper.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<CollectorStartResult> StartAsync(bool allowElevationPrompt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_source is not null)
            {
                return CollectorStartResult.AlreadyRunning;
            }

            if (Privileges.CanLikelyControlEtw)
            {
                var inProcess = new InProcessTelemetrySource(_options, _loggerFactory.CreateLogger<NetworkCollector>());
                try
                {
                    await inProcess.StartAsync(_sink, cancellationToken).ConfigureAwait(false);
                    Attach(inProcess);
                    return CollectorStartResult.StartedInProcess;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LastError = ErrorText.Describe(ex);
                    _logger.LogWarning(ex, "In-process collector failed to start.");
                    if (Privileges.IsElevated)
                    {
                        return CollectorStartResult.Failed;
                    }
                }
            }

            if (!allowElevationPrompt)
            {
                return CollectorStartResult.ElevationRequired;
            }

            var helper = new ElevatedHelperTelemetrySource(logger: _loggerFactory.CreateLogger<ElevatedHelperTelemetrySource>());
            if (!helper.IsHelperAvailable)
            {
                LastError = "The collector helper is not installed.";
                return CollectorStartResult.HelperMissing;
            }

            try
            {
                await helper.StartAsync(_sink, cancellationToken).ConfigureAwait(false);
                ElevationDeclined = false;
                LastError = null;
                Attach(helper);
                return CollectorStartResult.StartedElevatedHelper;
            }
            catch (ElevationDeclinedException)
            {
                ElevationDeclined = true;
                return CollectorStartResult.ElevationDeclined;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LastError = ErrorText.Describe(ex);
                _logger.LogError(ex, "Elevated collector failed to start.");
                return CollectorStartResult.Failed;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the collector.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var source = _source;
            _source = null;
            Kind = TelemetrySourceKind.None;
            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
                _sink.OnStatus(CollectorStatus.NotRunning(_options.SessionName));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private void Attach(ITelemetrySource source)
    {
        _source = source;
        Kind = source.Kind;
        _ = source.Completion.ContinueWith(
            _ =>
            {
                if (ReferenceEquals(_source, source))
                {
                    _logger.LogWarning("Telemetry source {Kind} stopped.", source.Kind);
                    _source = null;
                    Kind = TelemetrySourceKind.None;
                    LastError ??= "The collector stopped.";
                    _sink.OnStatus(CollectorStatus.NotRunning(_options.SessionName, LastError));
                    SourceStopped?.Invoke(this, EventArgs.Empty);
                }
            },
            TaskScheduler.Default);
    }
}
