using Microsoft.Extensions.Logging;
using WinNetworkManager.Telemetry.Collection;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Telemetry.Sources;

/// <summary>Where per-process telemetry comes from.</summary>
public enum TelemetrySourceKind : byte
{
    /// <summary>No collector: per-process traffic unavailable.</summary>
    None,

    /// <summary>Collector running inside the (elevated) application process.</summary>
    InProcess,

    /// <summary>Collector running in the elevated helper process, streamed over a secured named pipe.</summary>
    ElevatedHelper,
}

/// <summary>A running source of telemetry frames.</summary>
public interface ITelemetrySource : IAsyncDisposable
{
    /// <summary>Kind of source.</summary>
    TelemetrySourceKind Kind { get; }

    /// <summary>Completes when the source stops for any reason (helper exit, pipe broken, stop requested).</summary>
    Task Completion { get; }

    /// <summary>Starts delivering frames and status to <paramref name="sink"/>.</summary>
    Task StartAsync(ITelemetrySink sink, CancellationToken cancellationToken);

    /// <summary>Stops the source.</summary>
    Task StopAsync();
}

/// <summary>Runs the collector inside the current process (requires ETW rights).</summary>
public sealed class InProcessTelemetrySource : ITelemetrySource
{
    private readonly NetworkCollectorOptions _options;
    private readonly ILogger? _logger;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NetworkCollector? _collector;

    /// <summary>Creates the source.</summary>
    public InProcessTelemetrySource(NetworkCollectorOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public TelemetrySourceKind Kind => TelemetrySourceKind.InProcess;

    /// <inheritdoc />
    public Task Completion => _completion.Task;

    /// <inheritdoc />
    public async Task StartAsync(ITelemetrySink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _collector = new NetworkCollector(_options, sink, _logger);
        try
        {
            await _collector.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _collector.DisposeAsync().ConfigureAwait(false);
            _collector = null;
            _completion.TrySetResult();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (_collector is not null)
        {
            await _collector.DisposeAsync().ConfigureAwait(false);
            _collector = null;
        }

        _completion.TrySetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
