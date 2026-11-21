using System.Globalization;
using WinNetworkManager.Core.Units;

namespace WinNetworkManager.Core.Settings;

/// <summary>Result of validating a settings document.</summary>
/// <param name="Settings">Normalized settings: every value inside its allowed range.</param>
/// <param name="Issues">Human-readable descriptions of every value that had to be corrected.</param>
public sealed record SettingsValidationResult(AppSettings Settings, IReadOnlyList<string> Issues)
{
    /// <summary>True when nothing had to be corrected.</summary>
    public bool IsValid => Issues.Count == 0;
}

/// <summary>Validates and normalizes <see cref="AppSettings"/>. Never throws for bad values; clamps and reports.</summary>
public static class SettingsValidator
{
    /// <summary>Allowed refresh intervals (ms) offered in the UI.</summary>
    public static IReadOnlyList<int> RefreshIntervals { get; } = [250, 500, 1000, 2000, 5000];

    /// <summary>Validates <paramref name="settings"/>.</summary>
    public static SettingsValidationResult Validate(AppSettings? settings)
    {
        var issues = new List<string>();
        settings ??= new AppSettings();

        var monitoring = settings.Monitoring ?? new MonitoringSettings();
        monitoring = monitoring with
        {
            RefreshIntervalMs = Clamp(monitoring.RefreshIntervalMs, 250, 10_000, "Monitoring.RefreshIntervalMs", issues),
            ConnectionPollIntervalMs = Clamp(monitoring.ConnectionPollIntervalMs, 250, 10_000, "Monitoring.ConnectionPollIntervalMs", issues),
            ProcessPollIntervalMs = Clamp(monitoring.ProcessPollIntervalMs, 500, 30_000, "Monitoring.ProcessPollIntervalMs", issues),
            ExitedProcessRetentionSeconds = Clamp(monitoring.ExitedProcessRetentionSeconds, 0, 3600, "Monitoring.ExitedProcessRetentionSeconds", issues),
            CollectorStartMode = Defined(monitoring.CollectorStartMode, CollectorStartMode.Manual, "Monitoring.CollectorStartMode", issues),
        };

        var appearance = settings.Appearance ?? new AppearanceSettings();
        string? accent = appearance.AccentColor;
        if (accent is not null && !IsHexColor(accent))
        {
            issues.Add($"Appearance.AccentColor '{accent}' is not a #RRGGBB colour; the Windows accent colour is used.");
            accent = null;
        }

        appearance = appearance with
        {
            Theme = Defined(appearance.Theme, ThemePreference.System, "Appearance.Theme", issues),
            Units = Defined(appearance.Units, DataUnitSystem.DecimalBytes, "Appearance.Units", issues),
            Density = Defined(appearance.Density, TableDensity.Comfortable, "Appearance.Density", issues),
            AccentColor = accent,
            TextScale = Clamp(double.IsNaN(appearance.TextScale) ? 1.0 : appearance.TextScale, 0.8, 2.0, "Appearance.TextScale", issues),
        };

        var processes = settings.Processes ?? new ProcessListSettings();
        processes = processes with
        {
            HiddenProcessNames = (processes.HiddenProcessNames ?? [])
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };

        var history = settings.History ?? new HistorySettings();
        history = history with
        {
            RetentionDays = Clamp(history.RetentionDays, 1, 3650, "History.RetentionDays", issues),
            InMemoryMinutes = Clamp(history.InMemoryMinutes, 1, 120, "History.InMemoryMinutes", issues),
            MaxTrackedProcessSeries = Clamp(history.MaxTrackedProcessSeries, 16, 4096, "History.MaxTrackedProcessSeries", issues),
        };

        var notifications = settings.Notifications ?? new NotificationSettings();
        notifications = notifications with
        {
            RateThresholdBytesPerSecond = Clamp(notifications.RateThresholdBytesPerSecond, 1_000L, 1_000_000_000_000L, "Notifications.RateThresholdBytesPerSecond", issues),
            RateThresholdSeconds = Clamp(notifications.RateThresholdSeconds, 1, 3600, "Notifications.RateThresholdSeconds", issues),
        };

        var diagnostics = settings.Diagnostics ?? new DiagnosticsSettings();
        diagnostics = diagnostics with
        {
            TimeoutMs = Clamp(diagnostics.TimeoutMs, 100, 120_000, "Diagnostics.TimeoutMs", issues),
            PingCount = Clamp(diagnostics.PingCount, 1, 10_000, "Diagnostics.PingCount", issues),
            PingIntervalMs = Clamp(diagnostics.PingIntervalMs, 200, 60_000, "Diagnostics.PingIntervalMs", issues),
            TracerouteMaxHops = Clamp(diagnostics.TracerouteMaxHops, 1, 64, "Diagnostics.TracerouteMaxHops", issues),
        };

        var export = settings.Export ?? new ExportSettings();
        if (export.CsvDelimiter is not (',' or ';' or '\t'))
        {
            issues.Add($"Export.CsvDelimiter '{export.CsvDelimiter}' is not supported; ',' is used.");
            export = export with { CsvDelimiter = ',' };
        }

        export = export with { DefaultFormat = Defined(export.DefaultFormat, ExportFormat.Csv, "Export.DefaultFormat", issues) };

        var logging = settings.Logging ?? new LoggingSettings();
        logging = logging with
        {
            MinimumLevel = Defined(logging.MinimumLevel, LogLevelSetting.Information, "Logging.MinimumLevel", issues),
            RetainedFiles = Clamp(logging.RetainedFiles, 1, 90, "Logging.RetainedFiles", issues),
            MaxFileSizeMB = Clamp(logging.MaxFileSizeMB, 1, 500, "Logging.MaxFileSizeMB", issues),
        };

        var general = settings.General ?? new GeneralSettings();
        general = general with { CloseBehavior = Defined(general.CloseBehavior, CloseBehavior.Exit, "General.CloseBehavior", issues) };

        var tables = new Dictionary<string, TableLayout>(StringComparer.Ordinal);
        foreach (var (id, layout) in settings.Tables ?? new Dictionary<string, TableLayout>())
        {
            if (string.IsNullOrWhiteSpace(id) || layout is null)
            {
                continue;
            }

            var columns = (layout.Columns ?? [])
                .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id))
                .GroupBy(c => c.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .Select(c => c.Width is { } w && (double.IsNaN(w) || w < 24 || w > 2000) ? c with { Width = null } : c)
                .ToArray();
            tables[id] = layout with { Columns = columns };
        }

        if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            issues.Add(string.Create(CultureInfo.InvariantCulture,
                $"Settings were written by a newer version (schema {settings.SchemaVersion}); unknown values were ignored."));
        }

        var normalized = settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            General = general,
            Monitoring = monitoring,
            Appearance = appearance,
            Processes = processes,
            History = history,
            Notifications = notifications,
            Diagnostics = diagnostics,
            Export = export,
            Logging = logging,
            Tables = tables,
        };

        return new SettingsValidationResult(normalized, issues);
    }

    /// <summary>True for "#RRGGBB".</summary>
    public static bool IsHexColor(string value) =>
        value.Length == 7 && value[0] == '#' && value.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF") == false;

    private static int Clamp(int value, int min, int max, string name, List<string> issues)
    {
        if (value < min || value > max)
        {
            int clamped = Math.Clamp(value, min, max);
            issues.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {value} is outside {min}–{max}; {clamped} is used."));
            return clamped;
        }

        return value;
    }

    private static long Clamp(long value, long min, long max, string name, List<string> issues)
    {
        if (value < min || value > max)
        {
            long clamped = Math.Clamp(value, min, max);
            issues.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {value} is outside {min}–{max}; {clamped} is used."));
            return clamped;
        }

        return value;
    }

    private static double Clamp(double value, double min, double max, string name, List<string> issues)
    {
        if (value < min || value > max)
        {
            double clamped = Math.Clamp(value, min, max);
            issues.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {value} is outside {min}–{max}; {clamped} is used."));
            return clamped;
        }

        return value;
    }

    private static T Defined<T>(T value, T fallback, string name, List<string> issues)
        where T : struct, Enum
    {
        if (Enum.IsDefined(value))
        {
            return value;
        }

        issues.Add($"{name} has an unknown value; {fallback} is used.");
        return fallback;
    }
}
