using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNetworkManager.App.Services;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Platform.Windows.Connectivity;
using WinNetworkManager.Platform.Windows.Security;
using WinNetworkManager.Telemetry.Model;
using WinNetworkManager.UI.Controls;

namespace WinNetworkManager.App.ViewModels;

/// <summary>A row of the "top applications" card.</summary>
public sealed partial class TopProcessItem : ObservableObject
{
    /// <summary>Process key.</summary>
    public required ProcessKey Key { get; init; }

    /// <summary>Display name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Secondary text (description or path).</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>Download rate text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Down { get; set; } = string.Empty;

    /// <summary>Upload rate text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Up { get; set; } = string.Empty;

    /// <summary>Share of the busiest row (0–1) for the bar.</summary>
    [ObservableProperty]
    public partial double Share { get; set; }

    /// <summary>Icon.</summary>
    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    /// <summary>Accessible summary.</summary>
    public string AccessibleName => $"{Name}: download {Down}, upload {Up}";
}

/// <summary>Dashboard: immediate overview of throughput, applications, connections and connectivity.</summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly IconService _icons;
    private readonly Func<Shell.ShellViewModel> _shell;

    /// <summary>Creates the view model.</summary>
    public DashboardViewModel(IconService icons, Func<Shell.ShellViewModel> shell)
    {
        _icons = icons;
        _shell = shell;
    }

    /// <inheritdoc />
    public override string Title => "Dashboard";

    /// <inheritdoc />
    public override PageKind Kind => PageKind.Dashboard;

    /// <summary>Top applications by current rate.</summary>
    public ObservableCollection<TopProcessItem> TopProcesses { get; } = [];

    [ObservableProperty] public partial string DownloadRate { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string UploadRate { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string DownloadTotal { get; set; } = string.Empty;
    [ObservableProperty] public partial string UploadTotal { get; set; } = string.Empty;
    [ObservableProperty] public partial string AttributedDown { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string AttributedUp { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string AttributedCaption { get; set; } = string.Empty;
    [ObservableProperty] public partial string ActiveApps { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string ActiveAppsCaption { get; set; } = string.Empty;
    [ObservableProperty] public partial string TcpConnections { get; set; } = "0";
    [ObservableProperty] public partial string ConnectionsCaption { get; set; } = string.Empty;
    [ObservableProperty] public partial IReadOnlyList<float> DownloadSpark { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<float> UploadSpark { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ChartSeries> ThroughputSeries { get; set; } = [];
    [ObservableProperty] public partial bool ShowAttributedSeries { get; set; } = true;
    [ObservableProperty] public partial Core.Units.DataUnitSystem Units { get; set; }

    // Collector banner
    [ObservableProperty] public partial bool ShowCollectorBanner { get; set; }
    [ObservableProperty] public partial string CollectorBannerTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string CollectorBannerText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CollectorBannerHasAction { get; set; }
    [ObservableProperty] public partial bool CollectorBannerIsError { get; set; }

    // Network card
    [ObservableProperty] public partial string ConnectivityText { get; set; } = "Checking…";
    [ObservableProperty] public partial string ConnectivityGlyph { get; set; } = UI.Glyphs.Globe;
    [ObservableProperty] public partial string InterfaceName { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string InterfaceDetail { get; set; } = string.Empty;
    [ObservableProperty] public partial string IPv4Addresses { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string IPv6Addresses { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string Gateways { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string DnsServers { get; set; } = Fmt.NotAvailable;

    // Breakdown card
    [ObservableProperty] public partial string PhysicalSession { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string AttributedSession { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string LoopbackSession { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string UnattributedSession { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string TcpUdpSplit { get; set; } = Fmt.NotAvailable;

    /// <summary>Opens the inspector of a top process.</summary>
    [RelayCommand]
    private void OpenProcess(TopProcessItem? item)
    {
        if (item is not null)
        {
            _shell().OpenInspector(item.Key);
        }
    }

    /// <summary>Starts the collector.</summary>
    [RelayCommand]
    private Task EnableCollectorAsync() => _shell().EnableCollectorAsync();

    partial void OnShowAttributedSeriesChanged(bool value) => OnSnapshot(LastSnapshot ?? EngineSnapshot.Empty(PrivilegeInfo.ReadCurrent()));

    private EngineSnapshot? LastSnapshot { get; set; }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        LastSnapshot = snapshot;
        Units = Fmt.Units;
        var system = snapshot.System;
        bool attributed = system.Traffic is TrafficAvailability.Available or TrafficAvailability.Partial;

        DownloadRate = system.PhysicalReceiveRate is { } rx ? Fmt.Rate(rx) : Fmt.NotAvailable;
        UploadRate = system.PhysicalSendRate is { } tx ? Fmt.Rate(tx) : Fmt.NotAvailable;
        DownloadTotal = $"{Fmt.Bytes(system.PhysicalReceived)} this session";
        UploadTotal = $"{Fmt.Bytes(system.PhysicalSent)} this session";
        DownloadSpark = system.PhysicalReceiveHistory;
        UploadSpark = system.PhysicalSendHistory;

        AttributedDown = Fmt.Rate(system.AttributedRates.Received, attributed);
        AttributedUp = Fmt.Rate(system.AttributedRates.Sent, attributed);
        AttributedCaption = attributed
            ? (system.Traffic == TrafficAvailability.Partial ? "Some events were lost in this window" : "TCP/UDP payload measured per process")
            : "Requires per-process monitoring";
        ActiveApps = attributed ? Fmt.Count(system.ActiveProcesses) : Fmt.Count(system.ProcessesWithSockets);
        ActiveAppsCaption = attributed
            ? $"with traffic now · {Fmt.Count(system.ProcessesWithSockets)} with open sockets"
            : "processes with open sockets";
        TcpConnections = Fmt.Count(system.TcpConnections);
        ConnectionsCaption = $"TCP established · {Fmt.Count(system.TcpListeners)} listening · {Fmt.Count(system.UdpEndpoints)} UDP";

        BuildChart(system, attributed);
        UpdateBanner(snapshot.Collector);
        UpdateNetwork(snapshot);
        UpdateBreakdown(system, attributed);
        UpdateTop(snapshot, attributed);
    }

    private void BuildChart(SystemTrafficView system, bool attributed)
    {
        var series = new List<ChartSeries>
        {
            new() { Name = "↓ Adapters", Values = ToDoubles(system.PhysicalReceiveHistory), Stroke = Brush("Brush.Data.Download"), Fill = Brush("Brush.Data.DownloadFill") },
            new() { Name = "↑ Adapters", Values = ToDoubles(system.PhysicalSendHistory), Stroke = Brush("Brush.Data.Upload"), Fill = Brush("Brush.Data.UploadFill"), Dashed = true },
        };
        if (attributed && ShowAttributedSeries)
        {
            series.Add(new ChartSeries { Name = "↓ Apps", Values = ToDoubles(system.AttributedReceiveHistory), Stroke = Brush("Brush.Data.Tcp"), Thickness = 1.25 });
            series.Add(new ChartSeries { Name = "↑ Apps", Values = ToDoubles(system.AttributedSendHistory), Stroke = Brush("Brush.Data.Udp"), Thickness = 1.25, Dashed = true });
        }

        ThroughputSeries = series;
    }

    private void UpdateBanner(CollectorView collector)
    {
        ShowCollectorBanner = collector.State != CollectorState.Running;
        CollectorBannerIsError = collector.State == CollectorState.Faulted;
        CollectorBannerHasAction = collector.State is CollectorState.Stopped or CollectorState.Faulted &&
            (collector.Privileges.CanLikelyControlEtw || collector.HelperAvailable);
        (CollectorBannerTitle, CollectorBannerText) = collector.State switch
        {
            CollectorState.Starting or CollectorState.Retrying => ("Starting per-process monitoring…", "Connecting to Windows event tracing."),
            CollectorState.Faulted => ("Per-process monitoring failed", collector.Error ?? "The collector stopped. Adapters, connections and diagnostics keep working."),
            _ when collector.ElevationDeclined => ("Per-process traffic is off", "Administrator permission was declined. Adapter totals, connections, processes and diagnostics work without it."),
            _ when !collector.HelperAvailable && !collector.Privileges.CanLikelyControlEtw => ("Per-process traffic unavailable", "The collector helper is missing from this installation. Reinstall WinNetworkManager or run it as administrator."),
            _ => ("See which apps use your network", "Measuring bytes per process uses Windows kernel event tracing, which needs administrator rights. WinNetworkManager starts a small helper with a single permission prompt; the main window stays unprivileged."),
        };
    }

    private void UpdateNetwork(EngineSnapshot snapshot)
    {
        var connectivity = snapshot.System.Connectivity;
        (ConnectivityText, ConnectivityGlyph) = connectivity.Level switch
        {
            ConnectivityLevel.Internet => ($"Connected to the Internet ({(connectivity.HasIPv4Internet ? "IPv4" : string.Empty)}{(connectivity.HasIPv4Internet && connectivity.HasIPv6Internet ? " + " : string.Empty)}{(connectivity.HasIPv6Internet ? "IPv6" : string.Empty)})", UI.Glyphs.Globe),
            ConnectivityLevel.LocalOnly => ("Local network only — no Internet access", UI.Glyphs.Warning),
            ConnectivityLevel.None => ("Not connected", UI.Glyphs.Error),
            _ => ("Connectivity unknown", UI.Glyphs.Info),
        };

        var primary = snapshot.Interfaces.FirstOrDefault(i => i.IsDefaultIPv4 || i.IsDefaultIPv6);
        if (primary is null)
        {
            InterfaceName = "No default route";
            InterfaceDetail = string.Empty;
            IPv4Addresses = IPv6Addresses = Gateways = DnsServers = Fmt.NotAvailable;
            return;
        }

        InterfaceName = primary.Name;
        InterfaceDetail = $"{primary.TypeName} · {Fmt.LinkSpeed(primary.ReceiveLinkSpeed)} · {primary.Description}";
        IPv4Addresses = Join(primary.Addresses.Where(a => a.Address.IsIPv4).Select(a => $"{a.Address}/{a.PrefixLength}"));
        IPv6Addresses = Join(primary.Addresses.Where(a => a.Address.IsIPv6 && AddressClassifier.Classify(a.Address) != AddressScope.LinkLocal).Select(a => a.Address.ToString()));
        Gateways = Join(primary.Gateways.Select(g => g.ToString()));
        DnsServers = Join(primary.DnsServers.Select(d => d.ToString()));
    }

    private void UpdateBreakdown(SystemTrafficView system, bool attributed)
    {
        PhysicalSession = $"↓ {Fmt.Bytes(system.PhysicalReceived)}   ↑ {Fmt.Bytes(system.PhysicalSent)}";
        AttributedSession = attributed || system.AttributedTotals.Total > 0
            ? $"↓ {Fmt.Bytes(system.AttributedTotals.Received)}   ↑ {Fmt.Bytes(system.AttributedTotals.Sent)}"
            : Fmt.NotAvailable;
        LoopbackSession = attributed || system.LoopbackTotals.Total > 0
            ? $"↓ {Fmt.Bytes(system.LoopbackTotals.Received)}   ↑ {Fmt.Bytes(system.LoopbackTotals.Sent)}"
            : Fmt.NotAvailable;
        TcpUdpSplit = attributed || system.AttributedTotals.Total > 0
            ? $"TCP {Fmt.Bytes(system.AttributedTotals.TcpReceived + system.AttributedTotals.TcpSent)} · UDP {Fmt.Bytes(system.AttributedTotals.UdpReceived + system.AttributedTotals.UdpSent)}"
            : Fmt.NotAvailable;
        UnattributedSession = attributed
            ? $"↓ ≈{Fmt.Bytes(Math.Max(0, system.PhysicalReceived - system.AttributedTotals.Received))}   ↑ ≈{Fmt.Bytes(Math.Max(0, system.PhysicalSent - system.AttributedTotals.Sent))}"
            : Fmt.NotAvailable;
    }

    private void UpdateTop(EngineSnapshot snapshot, bool attributed)
    {
        IEnumerable<ProcessView> candidates = attributed
            ? snapshot.Processes.Where(p => p.Rates.Total > 0).OrderByDescending(p => p.Rates.Total)
            : snapshot.Processes.Where(p => p.TcpConnections > 0).OrderByDescending(p => p.TcpConnections);
        var top = candidates.Take(6).ToList();
        double max = attributed ? Math.Max(1, top.Select(p => p.Rates.Total).DefaultIfEmpty(1).Max()) : Math.Max(1, top.Select(p => (double)p.TcpConnections).DefaultIfEmpty(1).Max());

        while (TopProcesses.Count > top.Count)
        {
            TopProcesses.RemoveAt(TopProcesses.Count - 1);
        }

        for (int i = 0; i < top.Count; i++)
        {
            var p = top[i];
            if (i >= TopProcesses.Count || TopProcesses[i].Key != p.Key)
            {
                var item = new TopProcessItem { Key = p.Key };
                if (i < TopProcesses.Count)
                {
                    TopProcesses[i] = item;
                }
                else
                {
                    TopProcesses.Add(item);
                }
            }

            var row = TopProcesses[i];
            row.Name = p.Name;
            row.Detail = p.Description ?? (p.Path.HasValue ? p.Path.Value! : $"PID {p.Key.Pid}");
            row.Down = attributed ? "↓ " + Fmt.Rate(p.Rates.Received) : $"{p.TcpConnections} connections";
            row.Up = attributed ? "↑ " + Fmt.Rate(p.Rates.Sent) : string.Empty;
            row.Share = (attributed ? p.Rates.Total : p.TcpConnections) / max;
            if (row.Icon is null && p.Path.HasValue)
            {
                var target = row;
                row.Icon = _icons.Get(p.Path.Value, image => target.Icon = image);
            }
        }
    }

    private static IReadOnlyList<double> ToDoubles(IReadOnlyList<float> values)
    {
        var result = new double[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            result[i] = values[i];
        }

        return result;
    }

    private static Brush? Brush(string key) => Application.Current.TryFindResource(key) as Brush;

    private static string Join(IEnumerable<string> values)
    {
        string text = string.Join(", ", values);
        return text.Length == 0 ? Fmt.NotAvailable : text;
    }
}
