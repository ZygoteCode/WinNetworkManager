using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNetworkManager.App.Services;
using WinNetworkManager.App.Shell;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Core.Units;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Infrastructure;
using WinNetworkManager.Telemetry.Model;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.App.ViewModels;

/// <summary>A labelled choice for combo boxes.</summary>
/// <typeparam name="T">Value type.</typeparam>
/// <param name="Value">Value.</param>
/// <param name="Label">Text shown.</param>
public sealed record Choice<T>(T Value, string Label);

/// <summary>
/// Settings page. Every control writes through <see cref="SettingsService.Update"/>, which validates, persists and
/// applies the change immediately (no "Apply" button, no restart).
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly AppPaths _paths;
    private readonly Func<ShellViewModel> _shell;

    /// <summary>Creates the view model.</summary>
    public SettingsViewModel(SettingsService settings, HistoryService history, AppPaths paths, Func<ShellViewModel> shell)
    {
        _settings = settings;
        _history = history;
        _paths = paths;
        _shell = shell;
        _settings.Changed += (_, _) => OnPropertyChanged(string.Empty);
        LoadIssues = string.Join(Environment.NewLine, settings.LoadIssues);
    }

    /// <inheritdoc />
    public override string Title => "Settings";

    /// <inheritdoc />
    public override PageKind Kind => PageKind.Settings;

    private AppSettings S => _settings.Current;

    /// <summary>Result of the last action, or a save error.</summary>
    [ObservableProperty] public partial string? Message { get; set; }

    /// <summary>Problems found in the settings file at startup.</summary>
    public string LoadIssues { get; }

    // ── Choices ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Refresh intervals.</summary>
    public IReadOnlyList<Choice<int>> RefreshIntervals { get; } = SettingsValidator.RefreshIntervals.Select(ms => new Choice<int>(ms, ms < 1000 ? $"{ms} ms" : $"{ms / 1000} s")).ToList();

    /// <summary>Polling intervals.</summary>
    public IReadOnlyList<Choice<int>> PollIntervals { get; } = [new(500, "0.5 s"), new(1000, "1 s"), new(2000, "2 s"), new(5000, "5 s"), new(10000, "10 s")];

    /// <summary>Themes.</summary>
    public IReadOnlyList<Choice<ThemePreference>> Themes { get; } = [new(ThemePreference.System, "Use Windows setting"), new(ThemePreference.Light, "Light"), new(ThemePreference.Dark, "Dark")];

    /// <summary>Units.</summary>
    public IReadOnlyList<Choice<DataUnitSystem>> UnitSystems { get; } =
    [
        new(DataUnitSystem.DecimalBytes, "Bytes, decimal (1 kB = 1000 B)"),
        new(DataUnitSystem.BinaryBytes, "Bytes, binary (1 KiB = 1024 B)"),
        new(DataUnitSystem.DecimalBits, "Bits (1 Mbps = 1,000,000 bit/s)"),
    ];

    /// <summary>Densities.</summary>
    public IReadOnlyList<Choice<TableDensity>> Densities { get; } = [new(TableDensity.Comfortable, "Comfortable"), new(TableDensity.Compact, "Compact")];

    /// <summary>Motion choices (null = follow Windows).</summary>
    public IReadOnlyList<Choice<string>> MotionChoices { get; } = [new("system", "Use Windows setting"), new("on", "Reduce animations"), new("off", "Show animations")];

    /// <summary>Close behaviours.</summary>
    public IReadOnlyList<Choice<CloseBehavior>> CloseBehaviors { get; } = [new(CloseBehavior.Exit, "Exit WinNetworkManager"), new(CloseBehavior.MinimizeToTray, "Keep monitoring in the notification area")];

    /// <summary>Collector start modes.</summary>
    public IReadOnlyList<Choice<CollectorStartMode>> CollectorStartModes { get; } =
    [
        new(CollectorStartMode.Manual, "Only when I turn it on"),
        new(CollectorStartMode.Automatic, "At startup (shows an administrator prompt when needed)"),
    ];

    /// <summary>Log levels.</summary>
    public IReadOnlyList<Choice<LogLevelSetting>> LogLevels { get; } = [new(LogLevelSetting.Error, "Errors"), new(LogLevelSetting.Warning, "Warnings"), new(LogLevelSetting.Information, "Information"), new(LogLevelSetting.Debug, "Debug (verbose)")];

    /// <summary>Export formats.</summary>
    public IReadOnlyList<Choice<ExportFormat>> ExportFormats { get; } = [new(ExportFormat.Csv, "CSV"), new(ExportFormat.Json, "JSON")];

    /// <summary>CSV delimiters.</summary>
    public IReadOnlyList<Choice<char>> Delimiters { get; } = [new(',', "Comma ( , )"), new(';', "Semicolon ( ; ) — for Excel with a comma decimal separator")];

    // ── General ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Start with Windows (current user's Run key; never enabled automatically).</summary>
    public bool StartWithWindows { get => S.General.StartWithWindows; set => Set(s => s with { General = s.General with { StartWithWindows = value } }); }

    /// <summary>Start minimized to the notification area.</summary>
    public bool StartMinimized { get => S.General.StartMinimized; set => Set(s => s with { General = s.General with { StartMinimized = value } }); }

    /// <summary>Close button behaviour.</summary>
    public CloseBehavior CloseBehavior { get => S.General.CloseBehavior; set => Set(s => s with { General = s.General with { CloseBehavior = value } }); }

    // ── Monitoring ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Refresh interval.</summary>
    public int RefreshIntervalMs { get => S.Monitoring.RefreshIntervalMs; set => Set(s => s with { Monitoring = s.Monitoring with { RefreshIntervalMs = value } }); }

    /// <summary>Connection polling interval.</summary>
    public int ConnectionPollIntervalMs { get => S.Monitoring.ConnectionPollIntervalMs; set => Set(s => s with { Monitoring = s.Monitoring with { ConnectionPollIntervalMs = value } }); }

    /// <summary>Process polling interval.</summary>
    public int ProcessPollIntervalMs { get => S.Monitoring.ProcessPollIntervalMs; set => Set(s => s with { Monitoring = s.Monitoring with { ProcessPollIntervalMs = value } }); }

    /// <summary>Include loopback in totals.</summary>
    public bool IncludeLoopbackInTotals { get => S.Monitoring.IncludeLoopbackInTotals; set => Set(s => s with { Monitoring = s.Monitoring with { IncludeLoopbackInTotals = value } }); }

    /// <summary>Seconds exited processes stay listed.</summary>
    public int ExitedProcessRetentionSeconds { get => S.Monitoring.ExitedProcessRetentionSeconds; set => Set(s => s with { Monitoring = s.Monitoring with { ExitedProcessRetentionSeconds = value } }); }

    /// <summary>Reverse-resolve remote addresses.</summary>
    public bool ResolveRemoteHostNames { get => S.Monitoring.ResolveRemoteHostNames; set => Set(s => s with { Monitoring = s.Monitoring with { ResolveRemoteHostNames = value } }); }

    /// <summary>Collector start mode.</summary>
    public CollectorStartMode CollectorStartMode { get => S.Monitoring.CollectorStartMode; set => Set(s => s with { Monitoring = s.Monitoring with { CollectorStartMode = value } }); }

    /// <summary>Collector state text.</summary>
    [ObservableProperty] public partial string CollectorState { get; set; } = string.Empty;

    /// <summary>Privilege text.</summary>
    [ObservableProperty] public partial string PrivilegeState { get; set; } = string.Empty;

    /// <summary>True while the collector runs.</summary>
    [ObservableProperty] public partial bool CollectorRunning { get; set; }

    // ── Appearance ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Theme.</summary>
    public ThemePreference Theme { get => S.Appearance.Theme; set => Set(s => s with { Appearance = s.Appearance with { Theme = value } }); }

    /// <summary>Use the Windows accent colour.</summary>
    public bool UseSystemAccent
    {
        get => S.Appearance.AccentColor is null;
        set => Set(s => s with { Appearance = s.Appearance with { AccentColor = value ? null : (s.Appearance.AccentColor ?? "#2B88D8") } });
    }

    /// <summary>Custom accent colour (#RRGGBB).</summary>
    public string AccentColor
    {
        get => S.Appearance.AccentColor ?? string.Empty;
        set
        {
            string text = value.Trim();
            if (!text.StartsWith('#'))
            {
                text = "#" + text;
            }

            if (!SettingsValidator.IsHexColor(text))
            {
                Message = "Enter the accent colour as #RRGGBB, for example #2B88D8.";
                OnPropertyChanged();
                return;
            }

            Set(s => s with { Appearance = s.Appearance with { AccentColor = text.ToUpperInvariant() } });
        }
    }

    /// <summary>Units.</summary>
    public DataUnitSystem Units { get => S.Appearance.Units; set => Set(s => s with { Appearance = s.Appearance with { Units = value } }); }

    /// <summary>Density.</summary>
    public TableDensity Density { get => S.Appearance.Density; set => Set(s => s with { Appearance = s.Appearance with { Density = value } }); }

    /// <summary>Motion preference.</summary>
    public string Motion
    {
        get => S.Appearance.ReduceMotion switch { null => "system", true => "on", false => "off" };
        set => Set(s => s with { Appearance = s.Appearance with { ReduceMotion = value switch { "on" => true, "off" => false, _ => null } } });
    }

    /// <summary>Text scale.</summary>
    public double TextScale { get => S.Appearance.TextScale; set => Set(s => s with { Appearance = s.Appearance with { TextScale = Math.Round(value, 2) } }); }

    /// <summary>Text scale as percent text.</summary>
    public string TextScaleText => (S.Appearance.TextScale * 100).ToString("0", CultureInfo.CurrentCulture) + " %";

    // ── Processes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Hide system processes.</summary>
    public bool HideSystemProcesses { get => S.Processes.HideSystemProcesses; set => Set(s => s with { Processes = s.Processes with { HideSystemProcesses = value } }); }

    /// <summary>Show processes without network activity.</summary>
    public bool ShowIdleProcesses { get => S.Processes.ShowIdleProcesses; set => Set(s => s with { Processes = s.Processes with { ShowIdleProcesses = value } }); }

    /// <summary>Group by application.</summary>
    public bool GroupByApplication { get => S.Processes.GroupByApplication; set => Set(s => s with { Processes = s.Processes with { GroupByApplication = value } }); }

    /// <summary>Hidden process names, one per line.</summary>
    public string HiddenProcessNames
    {
        get => string.Join(Environment.NewLine, S.Processes.HiddenProcessNames);
        set
        {
            var names = value.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Set(s => s with { Processes = s.Processes with { HiddenProcessNames = names } });
        }
    }

    // ── History ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Persist history.</summary>
    public bool PersistHistory { get => S.History.PersistToDisk; set => Set(s => s with { History = s.History with { PersistToDisk = value } }); }

    /// <summary>Retention in days.</summary>
    public int RetentionDays { get => S.History.RetentionDays; set => Set(s => s with { History = s.History with { RetentionDays = value } }); }

    /// <summary>In-memory per-second history in minutes.</summary>
    public int InMemoryMinutes { get => S.History.InMemoryMinutes; set => Set(s => s with { History = s.History with { InMemoryMinutes = value } }); }

    /// <summary>Database location.</summary>
    public string HistoryDatabase => _history.DatabasePath;

    /// <summary>Database size and state.</summary>
    public string HistoryState => _history.Store is { } store
        ? $"{Fmt.Bytes(store.SizeBytes)} on disk"
        : _history.Error is { } error ? "Not available: " + error : "Not kept (history stays in memory only)";

    // ── Notifications ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Master switch.</summary>
    public bool NotificationsEnabled { get => S.Notifications.Enabled; set => Set(s => s with { Notifications = s.Notifications with { Enabled = value } }); }

    /// <summary>Collector failures.</summary>
    public bool NotifyCollectorFailure { get => S.Notifications.CollectorFailure; set => Set(s => s with { Notifications = s.Notifications with { CollectorFailure = value } }); }

    /// <summary>Connectivity changes.</summary>
    public bool NotifyConnectivity { get => S.Notifications.ConnectivityChanges; set => Set(s => s with { Notifications = s.Notifications with { ConnectivityChanges = value } }); }

    /// <summary>New network applications.</summary>
    public bool NotifyNewApplications { get => S.Notifications.NewNetworkApplications; set => Set(s => s with { Notifications = s.Notifications with { NewNetworkApplications = value } }); }

    /// <summary>Rate threshold.</summary>
    public bool NotifyRateThreshold { get => S.Notifications.RateThresholdEnabled; set => Set(s => s with { Notifications = s.Notifications with { RateThresholdEnabled = value } }); }

    /// <summary>Threshold in megabytes per second (decimal).</summary>
    public double RateThresholdMegabytes
    {
        get => S.Notifications.RateThresholdBytesPerSecond / 1_000_000.0;
        set => Set(s => s with { Notifications = s.Notifications with { RateThresholdBytesPerSecond = (long)Math.Round(Math.Max(0.01, value) * 1_000_000) } });
    }

    /// <summary>Seconds above the threshold before notifying.</summary>
    public int RateThresholdSeconds { get => S.Notifications.RateThresholdSeconds; set => Set(s => s with { Notifications = s.Notifications with { RateThresholdSeconds = value } }); }

    // ── Diagnostics ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Timeout.</summary>
    public int DiagnosticsTimeoutMs { get => S.Diagnostics.TimeoutMs; set => Set(s => s with { Diagnostics = s.Diagnostics with { TimeoutMs = value } }); }

    /// <summary>Ping count.</summary>
    public int PingCount { get => S.Diagnostics.PingCount; set => Set(s => s with { Diagnostics = s.Diagnostics with { PingCount = value } }); }

    /// <summary>Ping interval.</summary>
    public int PingIntervalMs { get => S.Diagnostics.PingIntervalMs; set => Set(s => s with { Diagnostics = s.Diagnostics with { PingIntervalMs = value } }); }

    /// <summary>Traceroute hops.</summary>
    public int TracerouteMaxHops { get => S.Diagnostics.TracerouteMaxHops; set => Set(s => s with { Diagnostics = s.Diagnostics with { TracerouteMaxHops = value } }); }

    /// <summary>Follow redirects by default.</summary>
    public bool HttpFollowRedirects { get => S.Diagnostics.HttpFollowRedirects; set => Set(s => s with { Diagnostics = s.Diagnostics with { HttpFollowRedirects = value } }); }

    // ── Export ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Default export format.</summary>
    public ExportFormat DefaultExportFormat { get => S.Export.DefaultFormat; set => Set(s => s with { Export = s.Export with { DefaultFormat = value } }); }

    /// <summary>CSV delimiter.</summary>
    public char CsvDelimiter { get => S.Export.CsvDelimiter; set => Set(s => s with { Export = s.Export with { CsvDelimiter = value } }); }

    // ── Logging ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Write a log file.</summary>
    public bool LoggingEnabled { get => S.Logging.Enabled; set => Set(s => s with { Logging = s.Logging with { Enabled = value } }); }

    /// <summary>Log level.</summary>
    public LogLevelSetting LogLevel { get => S.Logging.MinimumLevel; set => Set(s => s with { Logging = s.Logging with { MinimumLevel = value } }); }

    /// <summary>Retained log files.</summary>
    public int RetainedLogFiles { get => S.Logging.RetainedFiles; set => Set(s => s with { Logging = s.Logging with { RetainedFiles = value } }); }

    // ── About ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Version line.</summary>
    public string VersionText { get; } = $"WinNetworkManager {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)} · {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture}";

    /// <summary>OS line.</summary>
    public string OsText { get; } = $"{RuntimeInformation.OSDescription} (build {Environment.OSVersion.Version.Build})";

    /// <summary>Data folder.</summary>
    public string DataDirectory => _paths.DataDirectory + (_paths.IsPortable ? "  (portable mode)" : string.Empty);

    /// <summary>Log folder.</summary>
    public string LogDirectory => _paths.LogDirectory;

    /// <inheritdoc />
    protected override void OnActivated() => OnPropertyChanged(string.Empty);

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        var c = snapshot.Collector;
        CollectorRunning = c.State is Telemetry.Model.CollectorState.Running or Telemetry.Model.CollectorState.Starting or Telemetry.Model.CollectorState.Retrying;
        CollectorState = _shell().CollectorText;
        PrivilegeState = _shell().PrivilegeText;
        OnPropertyChanged(nameof(HistoryState));
    }

    [RelayCommand]
    private Task EnableCollectorAsync() => _shell().EnableCollectorAsync();

    [RelayCommand]
    private Task DisableCollectorAsync() => _shell().DisableCollectorAsync();

    [RelayCommand]
    private void ClearHistory()
    {
        if (!Dialogs.Confirm(
                "Clear history",
                "Delete all traffic history stored on this PC?\n\nThis cannot be undone. Monitoring continues and new history is recorded from now on.",
                "Delete history",
                kind: DialogKind.Warning,
                destructive: true))
        {
            return;
        }

        try
        {
            _history.Clear();
            Message = "Stored history deleted.";
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            Message = "History could not be deleted: " + ErrorText.Describe(ex);
        }

        OnPropertyChanged(nameof(HistoryState));
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        if (Dialogs.Confirm(
                "Reset settings",
                "Restore every setting to its default value?\n\nStored history and column layouts are kept. Start with Windows is turned off.",
                "Reset"))
        {
            Set(s => new AppSettings { Tables = s.Tables }, nameof(Message));
            Message = "Settings restored to defaults.";
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => ShellActions.OpenFolder(_paths.DataDirectory);

    [RelayCommand]
    private void OpenLogFolder() => ShellActions.OpenFolder(_paths.LogDirectory);

    private void Set(Func<AppSettings, AppSettings> change, [CallerMemberName] string? property = null)
    {
        try
        {
            _settings.Update(change);
            Message = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = "The settings could not be saved: " + ErrorText.Describe(ex);
        }

        // The stored value may have been clamped by validation: re-read it.
        OnPropertyChanged(property);
        if (property == nameof(TextScale))
        {
            OnPropertyChanged(nameof(TextScaleText));
        }
    }
}
