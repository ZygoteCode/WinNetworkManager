using Microsoft.Extensions.Logging;
using WinNetworkManager.Telemetry.Collection;
using WinNetworkManager.Telemetry.Ipc;

namespace WinNetworkManager.Collector;

/// <summary>
/// Elevated helper of WinNetworkManager. It hosts the kernel event tracing collector for a WinNetworkManager window that
/// is not elevated and streams the measurements to it over a private named pipe. It has no window, accepts exactly one
/// client (the parent process, verified by process id and user), and exits when the parent exits or asks it to.
/// Arguments: --pipe &lt;name&gt; --parent &lt;pid&gt; --client-sid &lt;sid&gt; (passed by WinNetworkManager).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = CollectorHostOptions.Parse(args);
        if (options is null)
        {
            return CollectorExitCodes.InvalidArguments;
        }

        var logger = new PipeLogger();
        var host = new CollectorPipeHost(options, logger);
        logger.Target = host;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.Log(LogLevel.Critical, default, e.ExceptionObject?.ToString() ?? "Unhandled exception.", null, static (s, _) => s);

        try
        {
            return await host.RunAsync(new NetworkCollectorOptions(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogCritical(ex, "The collector helper failed.");
            return CollectorExitCodes.Crashed;
        }
    }

    /// <summary>Forwards log entries to WinNetworkManager through the pipe (the helper writes no files of its own).</summary>
    private sealed class PipeLogger : ILogger
    {
        public CollectorPipeHost? Target { get; set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || Target is not { } target)
            {
                return;
            }

            string message = formatter(state, exception);
            if (exception is not null)
            {
                message += Environment.NewLine + exception;
            }

            target.Forward(logLevel, message);
        }
    }
}
