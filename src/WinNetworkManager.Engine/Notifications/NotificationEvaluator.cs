using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Core.Units;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Platform.Windows.Connectivity;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Engine.Notifications;

/// <summary>Kind of notification.</summary>
public enum NotificationKind : byte
{
    /// <summary>The collector stopped unexpectedly.</summary>
    CollectorFailure,

    /// <summary>Internet connectivity changed.</summary>
    Connectivity,

    /// <summary>A process exceeded the configured rate for the configured time.</summary>
    RateThreshold,

    /// <summary>An application used the network for the first time in this session.</summary>
    NewApplication,
}

/// <summary>A notification raised by the engine.</summary>
/// <param name="Kind">Kind.</param>
/// <param name="Title">Short title.</param>
/// <param name="Message">Body.</param>
/// <param name="Process">Related process, when any.</param>
public sealed record EngineNotification(NotificationKind Kind, string Title, string Message, ProcessKey? Process = null);

/// <summary>Evaluates notification rules on consecutive snapshots. Engine-thread only.</summary>
internal sealed class NotificationEvaluator
{
    private readonly Dictionary<ProcessKey, int> _secondsAbove = [];
    private readonly Dictionary<ProcessKey, DateTimeOffset> _lastRateNotification = [];
    private readonly HashSet<string> _knownApps = new(StringComparer.Ordinal);
    private ConnectivityLevel? _lastConnectivity;
    private CollectorState? _lastCollectorState;
    private bool _primed;

    public IEnumerable<EngineNotification> Evaluate(EngineSnapshot snapshot, NotificationSettings settings, DataUnitSystem units, bool collectorStopRequested)
    {
        var results = new List<EngineNotification>();
        var level = snapshot.System.Connectivity.Level;
        if (_lastConnectivity is { } previous && previous != level && level != ConnectivityLevel.Unknown && settings.ConnectivityChanges)
        {
            results.Add(new EngineNotification(
                NotificationKind.Connectivity,
                level == ConnectivityLevel.Internet ? "Internet connection restored" : "Internet connection lost",
                level switch
                {
                    ConnectivityLevel.Internet => "Windows reports Internet access again.",
                    ConnectivityLevel.LocalOnly => "Windows reports local network access only (no Internet).",
                    _ => "No network connection.",
                }));
        }

        _lastConnectivity = level;

        var state = snapshot.Collector.State;
        if (_lastCollectorState == CollectorState.Running && state is CollectorState.Faulted or CollectorState.Stopped && !collectorStopRequested && settings.CollectorFailure)
        {
            results.Add(new EngineNotification(
                NotificationKind.CollectorFailure,
                "Per-process monitoring stopped",
                snapshot.Collector.Error ?? "The traffic collector stopped unexpectedly. Per-process traffic is unavailable until it is restarted."));
        }

        _lastCollectorState = state;

        foreach (var process in snapshot.Processes)
        {
            if (!process.HasTraffic)
            {
                continue;
            }

            if (_knownApps.Add(process.AppId) && _primed && settings.NewNetworkApplications && !process.IsSystem)
            {
                results.Add(new EngineNotification(
                    NotificationKind.NewApplication,
                    "New application using the network",
                    $"{process.Description ?? process.Name} (PID {process.Key.Pid}) started using the network.",
                    process.Key));
            }

            if (!settings.RateThresholdEnabled)
            {
                continue;
            }

            bool above = process.Rates.Received >= settings.RateThresholdBytesPerSecond || process.Rates.Sent >= settings.RateThresholdBytesPerSecond;
            int seconds = above ? _secondsAbove.GetValueOrDefault(process.Key) + (int)Math.Max(1, Math.Round(snapshot.RateWindow.TotalSeconds)) : 0;
            _secondsAbove[process.Key] = seconds;
            if (seconds >= settings.RateThresholdSeconds &&
                (!_lastRateNotification.TryGetValue(process.Key, out var last) || snapshot.Timestamp - last > TimeSpan.FromMinutes(10)))
            {
                _lastRateNotification[process.Key] = snapshot.Timestamp;
                string rate = DataFormatter.FormatRate(Math.Max(process.Rates.Received, process.Rates.Sent), units);
                results.Add(new EngineNotification(
                    NotificationKind.RateThreshold,
                    "High network usage",
                    $"{process.Name} (PID {process.Key.Pid}) has been above {DataFormatter.FormatRate(settings.RateThresholdBytesPerSecond, units)} for {seconds} s (now {rate}).",
                    process.Key));
            }
        }

        // The first evaluation only learns which applications already use the network.
        _primed = true;
        if (_secondsAbove.Count > 4096)
        {
            _secondsAbove.Clear();
        }

        return settings.Enabled ? results : [];
    }
}
