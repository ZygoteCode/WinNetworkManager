using Microsoft.Extensions.Logging;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Engine;
using WinNetworkManager.Infrastructure.Logging;
using WinNetworkManager.Infrastructure.Settings;
using WinNetworkManager.Platform.Windows.Shell;
using WinNetworkManager.UI;

namespace WinNetworkManager.App.Services;

/// <summary>Single entry point for reading and changing settings; applies every change to all subsystems.</summary>
public sealed class SettingsService
{
    private readonly JsonSettingsStore _store;
    private readonly MonitoringEngine _engine;
    private readonly ThemeManager _theme;
    private readonly JsonLinesFileLoggerProvider? _fileLogger;
    private readonly ILogger<SettingsService> _logger;

    /// <summary>Creates the service.</summary>
    public SettingsService(JsonSettingsStore store, MonitoringEngine engine, ThemeManager theme, ILogger<SettingsService> logger, JsonLinesFileLoggerProvider? fileLogger = null)
    {
        _store = store;
        _engine = engine;
        _theme = theme;
        _logger = logger;
        _fileLogger = fileLogger;
    }

    /// <summary>Raised on the UI thread after settings were applied.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>Current settings.</summary>
    public AppSettings Current => _store.Current;

    /// <summary>Problems found when loading (shown once in Settings).</summary>
    public IReadOnlyList<string> LoadIssues { get; set; } = [];

    /// <summary>Applies the current settings to every subsystem (startup).</summary>
    public void ApplyCurrent() => Apply(Current, previous: null);

    /// <summary>Changes and persists settings.</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var previous = Current;
        AppSettings next;
        try
        {
            next = _store.Update(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Settings could not be saved.");
            throw;
        }

        Apply(next, previous);
    }

    private void Apply(AppSettings settings, AppSettings? previous)
    {
        Fmt.Units = settings.Appearance.Units;
        _engine.ApplySettings(settings);
        _theme.Apply(settings.Appearance.Theme, settings.Appearance.AccentColor, settings.Appearance.TextScale, settings.Appearance.ReduceMotion, settings.Appearance.Density);
        _fileLogger?.SetMinimumLevel(settings.Logging.Enabled ? Map(settings.Logging.MinimumLevel) : LogLevel.None);

        if (previous is null || previous.General.StartWithWindows != settings.General.StartWithWindows)
        {
            try
            {
                if (settings.General.StartWithWindows || StartupRegistration.IsRegistered())
                {
                    StartupRegistration.Set(settings.General.StartWithWindows, Environment.ProcessPath ?? string.Empty);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                _logger.LogWarning(ex, "Could not update the start-with-Windows registration.");
            }
        }

        Changed?.Invoke(this, settings);
    }

    private static LogLevel Map(LogLevelSetting level) => level switch
    {
        LogLevelSetting.Error => LogLevel.Error,
        LogLevelSetting.Warning => LogLevel.Warning,
        LogLevelSetting.Debug => LogLevel.Debug,
        _ => LogLevel.Information,
    };
}
