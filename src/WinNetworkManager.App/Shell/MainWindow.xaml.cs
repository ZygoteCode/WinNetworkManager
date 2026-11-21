using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using WinNetworkManager.Platform.Windows.Shell;
using WinNetworkManager.UI;

namespace WinNetworkManager.App.Shell;

/// <summary>Main window: custom chrome with Windows 11 snap layouts, theme-aware title bar.</summary>
public partial class MainWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int HTMAXBUTTON = 9;

    private readonly ShellViewModel _shell;
    private readonly ThemeManager _theme;
    private bool _maxHover;

    /// <summary>Creates the window.</summary>
    public MainWindow(ShellViewModel shell, ThemeManager theme)
    {
        _shell = shell;
        _theme = theme;
        InitializeComponent();
        DataContext = shell;
        shell.PropertyChanged += OnShellPropertyChanged;
        theme.ThemeChanged += (_, _) => ApplyChromeTheme();
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        UpdateCollectorDot();
        UpdateNavWidth();
    }

    /// <summary>When true, closing hides the window instead of exiting.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>True once the application is really exiting.</summary>
    public bool IsExiting { get; set; }

    /// <summary>Raised when the close button hid the window instead of exiting.</summary>
    public event EventHandler? HiddenToTray;

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }

        ApplyChromeTheme();
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        if (CloseToTray && !IsExiting)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.OnClosing(e);
    }

    private void ApplyChromeTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }

        WindowEffects.SetDarkTitleBar(hwnd, _theme.IsDark);
        WindowEffects.SetRoundedCorners(hwnd);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.CollectorLevel):
                UpdateCollectorDot();
                break;
            case nameof(ShellViewModel.IsNavExpanded):
                UpdateNavWidth();
                break;
        }
    }

    private void UpdateCollectorDot()
    {
        string key = _shell.CollectorLevel switch
        {
            StatusLevel.Ok => "Brush.Status.Success",
            StatusLevel.Warning => "Brush.Status.Warning",
            StatusLevel.Error => "Brush.Status.Danger",
            _ => "Brush.Text.Tertiary",
        };
        CollectorDot.SetResourceReference(Shape.FillProperty, key);
    }

    private void UpdateNavWidth() => NavRail.Width = _shell.IsNavExpanded ? 220 : 64;

    private void UpdateMaximizeGlyph()
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        AutomationPropertiesHelper.SetName(MaximizeButton, WindowState == WindowState.Maximized ? "Restore" : "Maximize");
        // Keep content inside the screen when maximized (WindowChrome extends past the work area by the frame size).
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // Windows 11 snap layouts appear when the window reports HTMAXBUTTON for the maximize button area.
    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (IsOverMaximize(lParam))
                {
                    SetMaxHover(true);
                    handled = true;
                    return HTMAXBUTTON;
                }

                SetMaxHover(false);
                break;
            case WM_NCMOUSELEAVE:
                SetMaxHover(false);
                break;
            case WM_NCLBUTTONDOWN when wParam == HTMAXBUTTON:
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                handled = true;
                OnMaximize(this, new RoutedEventArgs());
                break;
        }

        return 0;
    }

    private bool IsOverMaximize(nint lParam)
    {
        int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        if (!MaximizeButton.IsVisible)
        {
            return false;
        }

        try
        {
            var point = MaximizeButton.PointFromScreen(new Point(x, y));
            return point.X >= 0 && point.Y >= 0 && point.X < MaximizeButton.ActualWidth && point.Y < MaximizeButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SetMaxHover(bool hover)
    {
        if (_maxHover == hover)
        {
            return;
        }

        _maxHover = hover;
        if (hover)
        {
            MaximizeButton.SetResourceReference(BackgroundProperty, "Brush.Bg.SurfaceHover");
        }
        else
        {
            MaximizeButton.ClearValue(BackgroundProperty);
        }
    }

    private static class AutomationPropertiesHelper
    {
        public static void SetName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
    }
}
