using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WinNetworkManager.App.Shell;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Engine;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Engine.Notifications;
using WinNetworkManager.Platform.Windows.Shell;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.App.Services;

/// <summary>
/// Notification-area icon: live rates in the tooltip, open/exit menu, and the engine's notifications. The icon lives on a
/// hidden window of its own so it keeps working while the main window is hidden.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1

    private readonly MonitoringEngine _engine;
    private readonly SnapshotHub _hub;
    private readonly SettingsService _settings;
    private readonly Func<ShellViewModel> _shell;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<TrayIconService> _logger;
    private HwndSource? _host;
    private TrayIcon? _icon;
    private MainWindow? _window;
    private ProcessKey? _lastNotificationProcess;
    private DateTimeOffset _lastTooltip;
    private bool _toldAboutTray;

    /// <summary>Creates the service.</summary>
    public TrayIconService(MonitoringEngine engine, SnapshotHub hub, SettingsService settings, Func<ShellViewModel> shell, Dispatcher dispatcher, ILogger<TrayIconService> logger)
    {
        _engine = engine;
        _hub = hub;
        _settings = settings;
        _shell = shell;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Creates the icon for <paramref name="window"/> (UI thread).</summary>
    public void Start(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        var parameters = new HwndSourceParameters("WinNetworkManager.NotificationArea")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = 0x00000080,        // WS_EX_TOOLWINDOW
        };
        _host = new HwndSource(parameters);
        _host.AddHook(WndProc);
        _icon = new TrayIcon(_host.Handle, CallbackMessage, Environment.ProcessPath ?? string.Empty);
        if (!_icon.Show("WinNetworkManager"))
        {
            _logger.LogWarning("The notification-area icon could not be added.");
        }

        _hub.Updated += (_, snapshot) => UpdateTooltip(snapshot);
        _engine.NotificationRaised += (_, notification) => _dispatcher.BeginInvoke(() => Notify(notification));
        window.HiddenToTray += (_, _) => OnHiddenToTray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
        _host?.Dispose();
        _host = null;
    }

    private void UpdateTooltip(EngineSnapshot snapshot)
    {
        if (_icon is null || snapshot.Timestamp - _lastTooltip < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastTooltip = snapshot.Timestamp;
        var s = snapshot.System;
        string rates = s.PhysicalReceiveRate is { } rx && s.PhysicalSendRate is { } tx
            ? $"↓ {Fmt.Rate(rx)}   ↑ {Fmt.Rate(tx)}"
            : "Measuring…";
        string collector = snapshot.Collector.State == CollectorState.Running ? "Per-process monitoring on" : "Per-process monitoring off";
        _icon.SetTooltip($"WinNetworkManager\n{rates}\n{collector}");
    }

    private void Notify(EngineNotification notification)
    {
        if (_icon is null || !_settings.Current.Notifications.Enabled)
        {
            return;
        }

        _lastNotificationProcess = notification.Process;
        var kind = notification.Kind == NotificationKind.CollectorFailure ? TrayNotificationKind.Warning : TrayNotificationKind.Information;
        _icon.Notify(notification.Title, notification.Message, kind);
    }

    private void OnHiddenToTray()
    {
        if (_toldAboutTray || _icon is null)
        {
            return;
        }

        _toldAboutTray = true;
        _icon.Notify("WinNetworkManager is still running", "Monitoring continues in the notification area. Right-click the icon to open or exit.", TrayNotificationKind.Information);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            handled = true;
            switch (TrayIcon.Translate(lParam))
            {
                case TrayIconEvent.Activate:
                    Open();
                    break;
                case TrayIconEvent.ContextMenu:
                    ShowMenu();
                    break;
                case TrayIconEvent.NotificationClicked:
                    Open();
                    if (_lastNotificationProcess is { } key)
                    {
                        _shell().OpenInspector(key);
                    }

                    break;
            }
        }
        else if (msg == (int)TrayIcon.TaskbarCreatedMessage)
        {
            _icon?.Show("WinNetworkManager"); // Explorer restarted: the icon must be added again.
        }

        return 0;
    }

    private void Open()
    {
        if (_window is not null)
        {
            App.BringToFront(_window);
        }
    }

    private void ShowMenu()
    {
        var shell = _shell();
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        var open = new MenuItem { Header = "Open WinNetworkManager", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => Open();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        bool running = _hub.Current.Collector.State is CollectorState.Running or CollectorState.Starting or CollectorState.Retrying;
        if (running)
        {
            var off = new MenuItem { Header = "Turn off per-process monitoring" };
            off.Click += async (_, _) => await shell.DisableCollectorAsync().ConfigureAwait(true);
            menu.Items.Add(off);
        }
        else if (shell.CanEnableCollector)
        {
            var on = new MenuItem { Header = "Turn on per-process monitoring" };
            on.Click += async (_, _) => await shell.EnableCollectorAsync().ConfigureAwait(true);
            menu.Items.Add(on);
        }

        var pause = new MenuItem { Header = shell.IsPaused ? "Resume display" : "Pause display" };
        pause.Click += (_, _) => shell.IsPaused = !shell.IsPaused;
        menu.Items.Add(pause);
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Exit();
        menu.Items.Add(exit);

        _icon?.FocusOwner(); // lets the menu close when the user clicks elsewhere
        menu.IsOpen = true;
    }

    private void Exit()
    {
        if (_window is null)
        {
            Application.Current.Shutdown();
            return;
        }

        _window.IsExiting = true;
        _window.Close();
    }
}
