using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNetworkManager.App.Services;
using WinNetworkManager.App.ViewModels;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Engine;
using WinNetworkManager.Engine.Collector;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Platform.Windows.Security;
using WinNetworkManager.Telemetry.Model;
using WinNetworkManager.Telemetry.Sources;
using WinNetworkManager.UI;

namespace WinNetworkManager.App.Shell;

/// <summary>Severity of a status indicator (also conveyed by text and glyph, never colour alone).</summary>
public enum StatusLevel
{
    /// <summary>Everything fine.</summary>
    Ok,

    /// <summary>Degraded.</summary>
    Warning,

    /// <summary>Failed.</summary>
    Error,

    /// <summary>Not running.</summary>
    Inactive,
}

/// <summary>An entry of the navigation rail.</summary>
/// <param name="Kind">Target page.</param>
/// <param name="Label">Label.</param>
/// <param name="Glyph">Icon.</param>
/// <param name="Shortcut">Keyboard shortcut text.</param>
public sealed record NavItem(PageKind Kind, string Label, string Glyph, string Shortcut);

/// <summary>Shell: navigation, collector control, status bar.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly MonitoringEngine _engine;
    private readonly SnapshotHub _hub;
    private readonly Dictionary<PageKind, PageViewModel> _pages;
    private readonly Func<ProcessKey, ProcessInspectorViewModel> _inspectorFactory;
    private readonly Stack<PageViewModel> _back = new();

    /// <summary>Creates the shell.</summary>
    public ShellViewModel(MonitoringEngine engine, SnapshotHub hub, IEnumerable<PageViewModel> pages, Func<ProcessKey, ProcessInspectorViewModel> inspectorFactory)
    {
        _engine = engine;
        _hub = hub;
        _inspectorFactory = inspectorFactory;
        _pages = pages.ToDictionary(p => p.Kind);
        NavItems =
        [
            new(PageKind.Dashboard, "Dashboard", Glyphs.Dashboard, "Ctrl+1"),
            new(PageKind.Processes, "Processes", Glyphs.Processes, "Ctrl+2"),
            new(PageKind.Connections, "Connections", Glyphs.Connections, "Ctrl+3"),
            new(PageKind.Interfaces, "Interfaces", Glyphs.Interfaces, "Ctrl+4"),
            new(PageKind.Diagnostics, "Diagnostics", Glyphs.Diagnostics, "Ctrl+5"),
            new(PageKind.History, "History", Glyphs.History, "Ctrl+6"),
        ];
        FooterItems = [new(PageKind.Settings, "Settings", Glyphs.Settings, "Ctrl+,")];
        _hub.Updated += (_, snapshot) => OnSnapshot(snapshot);
        Navigate(PageKind.Dashboard);
        OnSnapshot(_hub.Current);
    }

    /// <summary>Main navigation entries.</summary>
    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>Footer navigation entries.</summary>
    public IReadOnlyList<NavItem> FooterItems { get; }

    /// <summary>Displayed page.</summary>
    [ObservableProperty]
    public partial PageViewModel? CurrentPage { get; set; }

    /// <summary>Highlighted navigation entry.</summary>
    [ObservableProperty]
    public partial NavItem? SelectedNav { get; set; }

    /// <summary>Navigation rail expanded (labels visible).</summary>
    [ObservableProperty]
    public partial bool IsNavExpanded { get; set; } = true;

    /// <summary>Display frozen.</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>True when a back target exists.</summary>
    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    /// <summary>Collector summary.</summary>
    [ObservableProperty]
    public partial string CollectorText { get; set; } = "Per-process monitoring off";

    /// <summary>Collector severity.</summary>
    [ObservableProperty]
    public partial StatusLevel CollectorLevel { get; set; } = StatusLevel.Inactive;

    /// <summary>Privilege description.</summary>
    [ObservableProperty]
    public partial string PrivilegeText { get; set; } = string.Empty;

    /// <summary>Events per second / losses.</summary>
    [ObservableProperty]
    public partial string EventsText { get; set; } = string.Empty;

    /// <summary>Age of the final data.</summary>
    [ObservableProperty]
    public partial string DataAgeText { get; set; } = string.Empty;

    /// <summary>Refresh interval text.</summary>
    [ObservableProperty]
    public partial string RefreshText { get; set; } = string.Empty;

    /// <summary>True when the collector can be started from the UI.</summary>
    [ObservableProperty]
    public partial bool CanEnableCollector { get; set; }

    /// <summary>True when the collector runs.</summary>
    [ObservableProperty]
    public partial bool IsCollectorRunning { get; set; }

    /// <summary>Message after a collector start attempt.</summary>
    [ObservableProperty]
    public partial string? CollectorMessage { get; set; }

    /// <summary>Page instances (for keyboard focus requests).</summary>
    public IReadOnlyDictionary<PageKind, PageViewModel> Pages => _pages;

    /// <summary>Selection of the main navigation list (null while a footer page is shown).</summary>
    public NavItem? SelectedMainNav
    {
        get => SelectedNav is { } nav && NavItems.Contains(nav) ? nav : null;
        set
        {
            if (value is not null)
            {
                SelectedNav = value;
            }
        }
    }

    /// <summary>Selection of the footer navigation list (null while a main page is shown).</summary>
    public NavItem? SelectedFooterNav
    {
        get => SelectedNav is { } nav && FooterItems.Contains(nav) ? nav : null;
        set
        {
            if (value is not null)
            {
                SelectedNav = value;
            }
        }
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        OnPropertyChanged(nameof(SelectedMainNav));
        OnPropertyChanged(nameof(SelectedFooterNav));
        if (value is not null && CurrentPage?.Kind != value.Kind)
        {
            Navigate(value.Kind);
        }
    }

    partial void OnIsPausedChanged(bool value) => _hub.IsPaused = value;

    /// <summary>Navigates to a page.</summary>
    [RelayCommand]
    public void Navigate(PageKind kind)
    {
        if (!_pages.TryGetValue(kind, out var page))
        {
            return;
        }

        Show(page, remember: CurrentPage?.Kind == PageKind.Inspector ? false : true);
    }

    /// <summary>Opens the inspector of a process.</summary>
    public void OpenInspector(ProcessKey key)
    {
        var inspector = _inspectorFactory(key);
        Show(inspector, remember: true);
    }

    /// <summary>Returns to the previous page.</summary>
    [RelayCommand]
    public void GoBack()
    {
        if (_back.Count == 0)
        {
            return;
        }

        Show(_back.Pop(), remember: false);
    }

    /// <summary>Toggles the navigation rail.</summary>
    [RelayCommand]
    public void ToggleNav() => IsNavExpanded = !IsNavExpanded;

    /// <summary>Toggles pause.</summary>
    [RelayCommand]
    public void TogglePause() => IsPaused = !IsPaused;

    /// <summary>Starts the collector (shows the UAC prompt when needed).</summary>
    [RelayCommand]
    public async Task EnableCollectorAsync()
    {
        CollectorMessage = "Starting the collector…";
        var result = await _engine.StartCollectorAsync(allowElevationPrompt: true).ConfigureAwait(true);
        CollectorMessage = result switch
        {
            CollectorStartResult.StartedInProcess or CollectorStartResult.StartedElevatedHelper or CollectorStartResult.AlreadyRunning => null,
            CollectorStartResult.ElevationDeclined => "Administrator permission was declined. Per-process traffic stays unavailable; everything else keeps working.",
            CollectorStartResult.HelperMissing => "The collector helper (WinNetworkManager.Collector.exe) is missing from the installation folder.",
            _ => "The collector could not start: " + (_engine.Collector.LastError ?? "unknown error"),
        };
    }

    /// <summary>Stops the collector.</summary>
    [RelayCommand]
    public Task DisableCollectorAsync() => _engine.StopCollectorAsync();

    private void Show(PageViewModel page, bool remember)
    {
        if (ReferenceEquals(page, CurrentPage))
        {
            return;
        }

        if (CurrentPage is not null)
        {
            if (remember)
            {
                _back.Push(CurrentPage);
                if (_back.Count > 20)
                {
                    var keep = _back.Take(20).Reverse().ToList();
                    _back.Clear();
                    foreach (var p in keep)
                    {
                        _back.Push(p);
                    }
                }
            }

            CurrentPage.Deactivate();
        }

        CurrentPage = page;
        SelectedNav = NavItems.Concat(FooterItems).FirstOrDefault(n => n.Kind == page.Kind);
        CanGoBack = _back.Count > 0;
        page.Activate(_hub.Current);
    }

    private void OnSnapshot(EngineSnapshot snapshot)
    {
        CurrentPage?.Deliver(snapshot);
        var c = snapshot.Collector;
        IsCollectorRunning = c.State == CollectorState.Running;
        CanEnableCollector = c.State is CollectorState.Stopped or CollectorState.Faulted && (c.Privileges.CanLikelyControlEtw || c.HelperAvailable);
        (CollectorText, CollectorLevel) = c.State switch
        {
            CollectorState.Running when c.PartialData => ("Per-process monitoring active · some events lost", StatusLevel.Warning),
            CollectorState.Running => (c.Kind == TelemetrySourceKind.ElevatedHelper ? "Per-process monitoring active (elevated helper)" : "Per-process monitoring active", StatusLevel.Ok),
            CollectorState.Starting => ("Starting per-process monitoring…", StatusLevel.Warning),
            CollectorState.Retrying => ("Per-process monitoring restarting…", StatusLevel.Warning),
            CollectorState.Faulted => ("Per-process monitoring failed", StatusLevel.Error),
            _ => ("Per-process monitoring off", StatusLevel.Inactive),
        };

        PrivilegeText = c.Privileges.Kind switch
        {
            ElevationKind.Elevated => "Running as administrator",
            ElevationKind.AdministratorNotElevated => "Standard rights (administrator account)",
            _ => "Standard user",
        };
        EventsText = IsCollectorRunning
            ? $"{c.EventsPerSecond:0} events/s" + (c.EventsLost > 0 ? $" · {Fmt.Count(c.EventsLost)} lost" : string.Empty)
            : string.Empty;
        DataAgeText = IsCollectorRunning && c.DataAsOf is { } asOf
            ? $"Traffic data {(snapshot.Timestamp - asOf).TotalSeconds:0.0} s behind real time"
            : string.Empty;
        RefreshText = $"Refresh {snapshot.RateWindow.TotalSeconds:0.##} s";
    }
}
