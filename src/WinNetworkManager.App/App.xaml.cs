using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinNetworkManager.App.Services;
using WinNetworkManager.App.Shell;
using WinNetworkManager.App.ViewModels;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Engine;
using WinNetworkManager.Infrastructure;
using WinNetworkManager.Infrastructure.Logging;
using WinNetworkManager.Infrastructure.Settings;
using WinNetworkManager.Platform.Windows.Shell;
using WinNetworkManager.UI;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.App;

/// <summary>Composition root and application lifetime.</summary>
public partial class App : Application
{
    static App()
    {
        // The interface is English. Built-in WPF text (TextBox and scroll bar menus, for example) comes from framework
        // resources localized into the Windows display language; use the English ones. Numbers and dates keep following
        // the regional format chosen in Windows (CurrentCulture is not changed).
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = english;
        Thread.CurrentThread.CurrentUICulture = english;
    }

    private const string MutexName = @"Local\WinNetworkManager.SingleInstance";
    private const string ActivateEventName = @"Local\WinNetworkManager.Activate";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private IHost? _host;
    private JsonLinesFileLoggerProvider? _fileLogger;
    private ILogger<App>? _logger;
    private int _shutdownStarted;
    private bool _errorDialogOpen;

    /// <summary>Service provider (available after startup).</summary>
    public static IServiceProvider Services => ((App)Current)._host?.Services ?? throw new InvalidOperationException("The application is not started.");

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--cleanup", StringComparer.OrdinalIgnoreCase))
        {
            // Run by the uninstaller for the user who uninstalls: remove what WinNetworkManager wrote outside its data
            // folder (the start-with-Windows entry). No window, no single-instance handshake.
            try
            {
                StartupRegistration.Set(false, string.Empty);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // Nothing else to do: the entry stays, Windows shows it as an app that cannot be found.
            }

            Shutdown(0);
            return;
        }

        if (!Environment.Is64BitProcess)
        {
            MessageBox.Show("WinNetworkManager requires a 64-bit version of Windows.", "WinNetworkManager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The other instance is starting or exiting; nothing to activate.
            }

            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        var paths = AppPaths.Resolve();
        paths.EnsureCreated();
        var store = new JsonSettingsStore(paths.SettingsFile);
        var load = store.Load();
        var settings = load.Settings;
        _fileLogger = new JsonLinesFileLoggerProvider(new FileLoggerOptions(
            paths.LogDirectory,
            settings.Logging.Enabled ? Map(settings.Logging.MinimumLevel) : LogLevel.None,
            settings.Logging.RetainedFiles,
            settings.Logging.MaxFileSizeMB * 1024L * 1024L));

        var theme = new ThemeManager(this);
        theme.Apply(settings.Appearance.Theme, settings.Appearance.AccentColor, settings.Appearance.TextScale, settings.Appearance.ReduceMotion, settings.Appearance.Density);
        Fmt.Units = settings.Appearance.Units;

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(_fileLogger);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(theme);
        builder.Services.AddSingleton(_fileLogger);
        builder.Services.AddSingleton(Dispatcher);
        builder.Services.AddSingleton(sp => new MonitoringEngine(store.Current, sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton(sp => new SnapshotHub(sp.GetRequiredService<MonitoringEngine>(), Dispatcher));
        builder.Services.AddSingleton(sp => new SettingsService(store, sp.GetRequiredService<MonitoringEngine>(), theme, sp.GetRequiredService<ILogger<SettingsService>>(), _fileLogger) { LoadIssues = load.Issues });
        builder.Services.AddSingleton(sp => new IconService(Dispatcher));
        builder.Services.AddSingleton<HistoryService>();
        builder.Services.AddSingleton<TrayIconService>();
        ViewModelRegistration.Register(builder.Services);
        builder.Services.AddSingleton<Func<ProcessKey, ProcessInspectorViewModel>>(sp => key => ActivatorUtilities.CreateInstance<ProcessInspectorViewModel>(sp, key));
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        _host = builder.Build();
        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("WinNetworkManager {Version} starting (data: {Data}, portable: {Portable}).", typeof(App).Assembly.GetName().Version, paths.DataDirectory, paths.IsPortable);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        SessionEnding += (_, _) => ShutdownServices();

        _host.Start();
        var engine = _host.Services.GetRequiredService<MonitoringEngine>();
        var settingsService = _host.Services.GetRequiredService<SettingsService>();
        settingsService.ApplyCurrent();
        _ = _host.Services.GetRequiredService<HistoryService>(); // opens the database (when enabled) before the first cycle
        engine.StartAsync().GetAwaiter().GetResult();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        _host.Services.GetRequiredService<TrayIconService>().Start(window);
        window.CloseToTray = settings.General.CloseBehavior == CloseBehavior.MinimizeToTray;
        settingsService.Changed += (_, s) => window.CloseToTray = s.General.CloseBehavior == CloseBehavior.MinimizeToTray;
        window.Closed += (_, _) => Shutdown();
        bool startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) && settings.General.StartMinimized && window.CloseToTray;
        if (!startMinimized)
        {
            window.Show();
        }

        StartActivationListener(window);
        _ = StartCollectorAccordingToPolicyAsync(engine, settings);
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        ShutdownServices();
        base.OnExit(e);
    }

    private async Task StartCollectorAccordingToPolicyAsync(MonitoringEngine engine, AppSettings settings)
    {
        try
        {
            if (engine.Collector.Privileges.CanLikelyControlEtw)
            {
                // No prompt needed: the process already has the rights.
                await engine.StartCollectorAsync(allowElevationPrompt: false).ConfigureAwait(true);
            }
            else if (settings.Monitoring.CollectorStartMode == CollectorStartMode.Automatic)
            {
                await engine.StartCollectorAsync(allowElevationPrompt: true).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogError(ex, "Automatic collector start failed.");
        }
    }

    private void StartActivationListener(Window window)
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            try
            {
                while (_activateEvent.WaitOne())
                {
                    if (Volatile.Read(ref _shutdownStarted) == 1)
                    {
                        return;
                    }

                    Dispatcher.BeginInvoke(() => BringToFront(window));
                }
            }
            catch (ObjectDisposedException)
            {
                // Exiting.
            }
        })
        {
            IsBackground = true,
            Name = "Single-instance activation",
        };
        thread.Start();
    }

    /// <summary>Shows and activates the main window.</summary>
    public static void BringToFront(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private void ShutdownServices()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 1)
        {
            return;
        }

        try
        {
            if (_host is not null)
            {
                // Stopping the engine stops the ETW session (in-process) or tells the helper to exit.
                var engine = _host.Services.GetRequiredService<MonitoringEngine>();
                engine.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                _host.Services.GetRequiredService<TrayIconService>().Dispose();
                _host.Services.GetRequiredService<HistoryService>().Dispose();
                _host.Services.GetRequiredService<IconService>().Dispose();
                _host.StopAsync(TimeSpan.FromSeconds(2)).Wait(TimeSpan.FromSeconds(3));
                _host.Dispose();
            }
        }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException or InvalidOperationException)
        {
            _logger?.LogError(ex, "Error during shutdown.");
        }
        finally
        {
            _logger?.LogInformation("WinNetworkManager stopped.");
            _fileLogger?.Dispose();
            _activateEvent?.Set();
            _activateEvent?.Dispose();
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled UI exception.");
        if (_errorDialogOpen)
        {
            // The same failure repeating (for example on every layout pass) while the dialog is open: log it only.
            e.Handled = true;
            return;
        }

        string text = $"An unexpected error occurred:\n\n{ErrorText.Describe(e.Exception)}\n\nDetails were written to the log. You can keep working, but the application may be unstable.";
        bool keepRunning;
        _errorDialogOpen = true;
        try
        {
            keepRunning = Dialogs.Confirm("Unexpected error", text, "Keep running", "Exit", DialogKind.Error);
        }
        catch (Exception dialogError) when (dialogError is InvalidOperationException or XamlParseException)
        {
            keepRunning = false; // The themed dialog itself failed: exit safely.
        }
        finally
        {
            _errorDialogOpen = false;
        }

        e.Handled = keepRunning;
        if (!e.Handled)
        {
            ShutdownServices();
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating}).", e.IsTerminating);
        if (e.IsTerminating)
        {
            ShutdownServices();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    private static LogLevel Map(LogLevelSetting level) => level switch
    {
        LogLevelSetting.Error => LogLevel.Error,
        LogLevelSetting.Warning => LogLevel.Warning,
        LogLevelSetting.Debug => LogLevel.Debug,
        _ => LogLevel.Information,
    };
}
