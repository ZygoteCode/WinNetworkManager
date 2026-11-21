using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinNetworkManager.Platform.Windows.Security;
using WinNetworkManager.Platform.Windows.Shell;
using WinNetworkManager.Telemetry.Model;
using WinNetworkManager.Telemetry.Sources;

namespace WinNetworkManager.Telemetry.Ipc;

/// <summary>The user declined the UAC prompt.</summary>
public sealed class ElevationDeclinedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ElevationDeclinedException()
        : base("The administrator permission request was declined.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ElevationDeclinedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public ElevationDeclinedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Starts the elevated helper (one UAC prompt), connects to its pipe after verifying that the server is the process
/// just launched, and relays frames/status/log records to the sink.
/// </summary>
public sealed class ElevatedHelperTelemetrySource : ITelemetrySource
{
    /// <summary>File name of the helper executable, expected next to the application.</summary>
    public const string HelperFileName = "WinNetworkManager.Collector.exe";

    private readonly string _helperPath;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NamedPipeClientStream? _pipe;
    private Process? _helper;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;

    /// <summary>Creates the source.</summary>
    public ElevatedHelperTelemetrySource(string? helperPath = null, ILogger? logger = null)
    {
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, HelperFileName);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public TelemetrySourceKind Kind => TelemetrySourceKind.ElevatedHelper;

    /// <inheritdoc />
    public Task Completion => _completion.Task;

    /// <summary>True when the helper executable is present.</summary>
    public bool IsHelperAvailable => File.Exists(_helperPath);

    /// <inheritdoc />
    public async Task StartAsync(ITelemetrySink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!IsHelperAvailable)
        {
            throw new FileNotFoundException("The collector helper is missing from the installation.", _helperPath);
        }

        var options = new CollectorHostOptions(PipeIdentity.NewPipeName(), Environment.ProcessId, PrivilegeInfo.CurrentUserSid());
        var result = ElevatedLauncher.TryLaunch(_helperPath, options.ToArguments(), out var helper, out string? error);
        switch (result)
        {
            case ElevationRequestResult.Cancelled:
                throw new ElevationDeclinedException();
            case ElevationRequestResult.Failed:
                throw new InvalidOperationException("The collector helper could not be started: " + error);
        }

        _helper = helper!;
        var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

            int? serverPid = PipeIdentity.GetServerProcessId(pipe.SafePipeHandle);
            if (serverPid != _helper.Id)
            {
                throw new InvalidOperationException($"Pipe server PID {serverPid} does not match the launched helper ({_helper.Id}).");
            }

            var hello = await TelemetryProtocol.ReadAsync(pipe, connectTimeout.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The helper closed the pipe before greeting.");
            if (hello.Type != MessageType.Hello)
            {
                throw new InvalidDataException("Unexpected first message from the helper.");
            }

            var greeting = TelemetryProtocol.DecodeHello(hello.Payload);
            if (greeting.ProtocolVersion != TelemetryProtocol.Version || greeting.TimestampFrequency != Stopwatch.Frequency)
            {
                throw new InvalidDataException($"Helper protocol {greeting.ProtocolVersion} (QPC {greeting.TimestampFrequency}) is not compatible.");
            }
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            TryKillHelper();
            _completion.TrySetResult();
            throw;
        }

        _pipe = pipe;
        _readCts = new CancellationTokenSource();
        _readLoop = ReadLoopAsync(pipe, sink, _readCts.Token);
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (_pipe is { IsConnected: true } pipe)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await TelemetryProtocol.WriteAsync(pipe, MessageType.Shutdown, ReadOnlyMemory<byte>.Empty, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Helper already gone.
            }
        }

        if (_helper is not null)
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _helper.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Collector helper did not exit in time; it will exit when the pipe closes.");
            }
        }

        _readCts?.Cancel();
        if (_readLoop is not null)
        {
            await _readLoop.ConfigureAwait(false);
        }

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }

        _helper?.Dispose();
        _helper = null;
        _readCts?.Dispose();
        _readCts = null;
        _completion.TrySetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task ReadLoopAsync(Stream pipe, ITelemetrySink sink, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await TelemetryProtocol.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                switch (message.Value.Type)
                {
                    case MessageType.Frame:
                        sink.OnFrame(TelemetryProtocol.DecodeFrame(message.Value.Payload));
                        break;
                    case MessageType.Status:
                        sink.OnStatus(TelemetryProtocol.DecodeStatus(message.Value.Payload));
                        break;
                    case MessageType.Log:
                        var log = TelemetryProtocol.DecodeLog(message.Value.Payload);
                        _logger.Log((LogLevel)Math.Clamp(log.Level, 0, 6), "[collector] {Message}", log.Message);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Pipe closed.
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError(ex, "Protocol violation from the collector helper; disconnecting.");
        }
        finally
        {
            _completion.TrySetResult();
        }
    }

    private void TryKillHelper()
    {
        try
        {
            if (_helper is { HasExited: false })
            {
                _helper.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Elevated process: the unelevated UI may not be allowed to terminate it; it exits on its own.
        }
    }
}
