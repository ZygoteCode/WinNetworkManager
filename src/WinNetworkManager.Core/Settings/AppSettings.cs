using WinNetworkManager.Core.Units;

namespace WinNetworkManager.Core.Settings;

/// <summary>Colour theme.</summary>
public enum ThemePreference : byte
{
    /// <summary>Follow the Windows app mode setting.</summary>
    System,

    /// <summary>Light.</summary>
    Light,

    /// <summary>Dark.</summary>
    Dark,
}

/// <summary>What the close button does.</summary>
public enum CloseBehavior : byte
{
    /// <summary>Exit the application.</summary>
    Exit,

    /// <summary>Hide to the notification area and keep monitoring.</summary>
    MinimizeToTray,
}

/// <summary>When the privileged collector is started.</summary>
public enum CollectorStartMode : byte
{
    /// <summary>Offer it on the dashboard; start only on user action.</summary>
    Manual,

    /// <summary>Start it at application start (shows a UAC prompt when not elevated).</summary>
    Automatic,
}

/// <summary>Table row density.</summary>
public enum TableDensity : byte
{
    /// <summary>Comfortable row height.</summary>
    Comfortable,

    /// <summary>Compact row height.</summary>
    Compact,
}

/// <summary>Minimum level written to the log file.</summary>
public enum LogLevelSetting : byte
{
    /// <summary>Errors only.</summary>
    Error,

    /// <summary>Warnings and errors.</summary>
    Warning,

    /// <summary>Informational and above (default).</summary>
    Information,

    /// <summary>Verbose diagnostics.</summary>
    Debug,
}

/// <summary>Export file format.</summary>
public enum ExportFormat : byte
{
    /// <summary>Comma (or semicolon) separated values with a metadata preamble.</summary>
    Csv,

    /// <summary>JSON document with a metadata object.</summary>
    Json,
}

/// <summary>
/// Root settings document (persisted as JSON). Every property has a safe default. Properties are settable (not
/// init-only) because the JSON source generator would otherwise reset properties missing from a hand-edited file to
/// default(T) instead of their initializer; treat instances as immutable and use <c>with</c> to change them.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Current schema version written by this build.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of the document.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>General behaviour.</summary>
    public GeneralSettings General { get; set; } = new();

    /// <summary>Acquisition parameters.</summary>
    public MonitoringSettings Monitoring { get; set; } = new();

    /// <summary>Theme, units, density.</summary>
    public AppearanceSettings Appearance { get; set; } = new();

    /// <summary>Process list behaviour.</summary>
    public ProcessListSettings Processes { get; set; } = new();

    /// <summary>History retention.</summary>
    public HistorySettings History { get; set; } = new();

    /// <summary>Notifications.</summary>
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>Diagnostics defaults.</summary>
    public DiagnosticsSettings Diagnostics { get; set; } = new();

    /// <summary>Export preferences.</summary>
    public ExportSettings Export { get; set; } = new();

    /// <summary>Logging.</summary>
    public LoggingSettings Logging { get; set; } = new();

    /// <summary>Per-table column layouts keyed by table id.</summary>
    public IReadOnlyDictionary<string, TableLayout> Tables { get; set; } = new Dictionary<string, TableLayout>();
}

/// <summary>General behaviour.</summary>
public sealed record GeneralSettings
{
    /// <summary>Register in the current user's Run key (only ever set by the user).</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Start hidden in the notification area.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Close button behaviour.</summary>
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Exit;
}

/// <summary>Acquisition parameters.</summary>
public sealed record MonitoringSettings
{
    /// <summary>UI refresh and rate averaging window, in milliseconds.</summary>
    public int RefreshIntervalMs { get; set; } = 1000;

    /// <summary>Connection table polling interval, in milliseconds.</summary>
    public int ConnectionPollIntervalMs { get; set; } = 1000;

    /// <summary>Process snapshot polling interval, in milliseconds.</summary>
    public int ProcessPollIntervalMs { get; set; } = 2000;

    /// <summary>Include loopback traffic in network totals and rates.</summary>
    public bool IncludeLoopbackInTotals { get; set; }

    /// <summary>Seconds an exited process stays visible in the live list.</summary>
    public int ExitedProcessRetentionSeconds { get; set; } = 30;

    /// <summary>Reverse-resolve remote addresses (asynchronous, cached; off by default).</summary>
    public bool ResolveRemoteHostNames { get; set; }

    /// <summary>When the privileged collector starts.</summary>
    public CollectorStartMode CollectorStartMode { get; set; } = CollectorStartMode.Manual;
}

/// <summary>Visual preferences.</summary>
public sealed record AppearanceSettings
{
    /// <summary>Theme.</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>Accent colour as #RRGGBB, or null for the Windows accent colour.</summary>
    public string? AccentColor { get; set; }

    /// <summary>Units for quantities and rates.</summary>
    public DataUnitSystem Units { get; set; } = DataUnitSystem.DecimalBytes;

    /// <summary>Table density.</summary>
    public TableDensity Density { get; set; } = TableDensity.Comfortable;

    /// <summary>Null follows the system "show animations" setting; true/false overrides it.</summary>
    public bool? ReduceMotion { get; set; }

    /// <summary>Additional UI text scale on top of the Windows text-size setting (0.8–2.0).</summary>
    public double TextScale { get; set; } = 1.0;
}

/// <summary>Process list behaviour.</summary>
public sealed record ProcessListSettings
{
    /// <summary>Hide kernel pseudo-processes and processes owned by system accounts.</summary>
    public bool HideSystemProcesses { get; set; }

    /// <summary>Show processes that never used the network.</summary>
    public bool ShowIdleProcesses { get; set; } = true;

    /// <summary>Group rows by application (executable path).</summary>
    public bool GroupByApplication { get; set; }

    /// <summary>Process image names hidden from the list (case-insensitive, e.g. "svchost.exe").</summary>
    public IReadOnlyList<string> HiddenProcessNames { get; set; } = [];
}

/// <summary>History retention.</summary>
public sealed record HistorySettings
{
    /// <summary>Persist aggregated history to the local database.</summary>
    public bool PersistToDisk { get; set; } = true;

    /// <summary>Days of per-minute history kept on disk.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Minutes of per-second history kept in memory.</summary>
    public int InMemoryMinutes { get; set; } = 15;

    /// <summary>Maximum number of processes with an in-memory per-second series.</summary>
    public int MaxTrackedProcessSeries { get; set; } = 256;
}

/// <summary>Notifications.</summary>
public sealed record NotificationSettings
{
    /// <summary>Master switch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Notify when the collector stops unexpectedly.</summary>
    public bool CollectorFailure { get; set; } = true;

    /// <summary>Notify when Internet connectivity changes.</summary>
    public bool ConnectivityChanges { get; set; } = true;

    /// <summary>Notify the first time an application uses the network in a session.</summary>
    public bool NewNetworkApplications { get; set; }

    /// <summary>Notify when a process exceeds a rate threshold.</summary>
    public bool RateThresholdEnabled { get; set; }

    /// <summary>Threshold in bytes per second (download or upload).</summary>
    public long RateThresholdBytesPerSecond { get; set; } = 10_000_000;

    /// <summary>Seconds the threshold must be exceeded before notifying.</summary>
    public int RateThresholdSeconds { get; set; } = 10;
}

/// <summary>Diagnostics defaults.</summary>
public sealed record DiagnosticsSettings
{
    /// <summary>Default per-operation timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>Default number of echo requests.</summary>
    public int PingCount { get; set; } = 4;

    /// <summary>Interval between echo requests in milliseconds.</summary>
    public int PingIntervalMs { get; set; } = 1000;

    /// <summary>Maximum traceroute hops.</summary>
    public int TracerouteMaxHops { get; set; } = 30;

    /// <summary>Follow HTTP redirects in the HTTP probe.</summary>
    public bool HttpFollowRedirects { get; set; }
}

/// <summary>Export preferences.</summary>
public sealed record ExportSettings
{
    /// <summary>Default format.</summary>
    public ExportFormat DefaultFormat { get; set; } = ExportFormat.Csv;

    /// <summary>CSV delimiter: ',' or ';'.</summary>
    public char CsvDelimiter { get; set; } = ',';

    /// <summary>Last folder used.</summary>
    public string? LastFolder { get; set; }
}

/// <summary>Logging.</summary>
public sealed record LoggingSettings
{
    /// <summary>Write a log file.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minimum level.</summary>
    public LogLevelSetting MinimumLevel { get; set; } = LogLevelSetting.Information;

    /// <summary>Number of daily files kept.</summary>
    public int RetainedFiles { get; set; } = 7;

    /// <summary>Size cap per file in megabytes.</summary>
    public int MaxFileSizeMB { get; set; } = 10;
}

/// <summary>Column layout of one table.</summary>
public sealed record TableLayout
{
    /// <summary>Columns in display order.</summary>
    public IReadOnlyList<ColumnLayout> Columns { get; set; } = [];

    /// <summary>Id of the sort column, or null.</summary>
    public string? SortColumn { get; set; }

    /// <summary>Sort direction.</summary>
    public bool SortDescending { get; set; } = true;
}

/// <summary>Layout of one column.</summary>
/// <param name="Id">Stable column id.</param>
/// <param name="Visible">Whether the column is shown.</param>
/// <param name="Width">Width in device-independent pixels; null = automatic.</param>
public sealed record ColumnLayout(string Id, bool Visible, double? Width);
