using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNetworkManager.App.Services;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Engine;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Platform.Windows.Processes;
using WinNetworkManager.UI.Controls;

namespace WinNetworkManager.App.ViewModels;

/// <summary>Key/value line of the inspector.</summary>
/// <param name="Key">Label.</param>
/// <param name="Value">Value (selectable).</param>
/// <param name="Hint">Tooltip explaining the value or why it is missing.</param>
public sealed record InfoLine(string Key, string Value, string? Hint = null);

/// <summary>A socket of the inspected process.</summary>
/// <param name="Protocol">TCP or UDP.</param>
/// <param name="Local">Local endpoint.</param>
/// <param name="Remote">Remote endpoint or peers.</param>
/// <param name="State">State.</param>
/// <param name="Down">Received.</param>
/// <param name="Up">Sent.</param>
/// <param name="Age">Age.</param>
public sealed record SocketLine(string Protocol, string Local, string Remote, string State, string Down, string Up, string Age);

/// <summary>Process Inspector: everything known about one process instance.</summary>
public sealed partial class ProcessInspectorViewModel : PageViewModel
{
    private readonly MonitoringEngine _engine;
    private readonly IconService _icons;
    private readonly Func<Shell.ShellViewModel> _shell;
    private readonly long _frequency = Stopwatch.Frequency;
    private (TimeSpan Cpu, long Timestamp)? _lastCpu;
    private DateTimeOffset _lastChart;
    private ProcessView? _view;

    /// <summary>Creates the inspector for <paramref name="key"/>.</summary>
    public ProcessInspectorViewModel(ProcessKey key, MonitoringEngine engine, IconService icons, Func<Shell.ShellViewModel> shell)
    {
        Key = key;
        _engine = engine;
        _icons = icons;
        _shell = shell;
        PidText = key.Pid.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Inspected instance.</summary>
    public ProcessKey Key { get; }

    /// <inheritdoc />
    public override string Title => Name;

    /// <inheritdoc />
    public override PageKind Kind => PageKind.Inspector;

    /// <summary>General information.</summary>
    public ObservableCollection<InfoLine> General { get; } = [];

    /// <summary>Network figures.</summary>
    public ObservableCollection<InfoLine> Network { get; } = [];

    /// <summary>Sockets of the process.</summary>
    public ObservableCollection<SocketLine> Sockets { get; } = [];

    [ObservableProperty] public partial string Name { get; set; } = "Process";
    [ObservableProperty] public partial string PidText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PathText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SignatureText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool SignatureValid { get; set; }
    [ObservableProperty] public partial bool HasSignature { get; set; }
    [ObservableProperty] public partial ImageSource? Icon { get; set; }
    [ObservableProperty] public partial string CpuText { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string MemoryText { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string PrivateText { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string HandlesText { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string ResourceHint { get; set; } = string.Empty;
    [ObservableProperty] public partial string DownRate { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string UpRate { get; set; } = Fmt.NotAvailable;
    [ObservableProperty] public partial string CommandLine { get; set; } = "Not loaded — reading it queries WMI.";
    [ObservableProperty] public partial bool CommandLineLoaded { get; set; }
    [ObservableProperty] public partial IReadOnlyList<ChartSeries> HistorySeries { get; set; } = [];
    [ObservableProperty] public partial bool StackProtocols { get; set; }
    [ObservableProperty] public partial bool Gone { get; set; }
    [ObservableProperty] public partial Core.Units.DataUnitSystem Units { get; set; }
    [ObservableProperty] public partial string SocketSummary { get; set; } = string.Empty;

    partial void OnStackProtocolsChanged(bool value) => _lastChart = default;

    [RelayCommand]
    private void CopyPath() => ShellActions.Copy(_view?.Path.Value);

    [RelayCommand]
    private void OpenLocation() => ShellActions.ShowInExplorer(_view?.Path.Value);

    [RelayCommand]
    private void ShowConnections()
    {
        if (_shell().Pages.TryGetValue(PageKind.Connections, out var page) && page is ConnectionsViewModel connections)
        {
            connections.SearchText = $"pid={Key.Pid}";
            _shell().Navigate(PageKind.Connections);
        }
    }

    [RelayCommand]
    private async Task LoadCommandLineAsync()
    {
        CommandLine = "Loading…";
        var result = await Task.Run(() => CommandLineReader.Read(Key.Pid, TimeSpan.FromSeconds(5))).ConfigureAwait(true);
        CommandLine = result.HasValue
            ? result.Value!
            : result.Status switch
            {
                InfoStatus.AccessDenied => "Not available: Windows only reveals command lines of other users' or protected processes to administrators.",
                InfoStatus.NotFound => "The process has exited.",
                InfoStatus.NotApplicable => "Not applicable to kernel processes.",
                _ => "Could not be read (WMI query failed).",
            };
        CommandLineLoaded = result.HasValue;
    }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        Units = Fmt.Units;
        var view = snapshot.Processes.FirstOrDefault(p => p.Key == Key)
            ?? (Key.HasCreateTime ? null : snapshot.Processes.FirstOrDefault(p => p.Key.Pid == Key.Pid));
        if (view is null)
        {
            Gone = true;
            StateText = "Exited — no longer tracked";
            return;
        }

        _view = view;
        Name = view.Name;
        OnPropertyChanged(nameof(Title));
        PathText = view.Path.HasValue ? view.Path.Value! : Probe<string>.Explain(view.Path.Status);
        StateText = view.State == ProcessState.Exited
            ? $"Exited{(view.ExitCode is { } c ? $" with code {c}" : string.Empty)} at {Fmt.Time(view.ExitTime)}"
            : "Running";
        HasSignature = view.Signature is not null;
        SignatureValid = view.Signature?.Status == SignatureStatus.Valid;
        SignatureText = view.Signature is { } s ? $"{s.Description}{(s.Publisher is null ? string.Empty : " · " + s.Publisher)}" : "Signature not checked yet";
        if (Icon is null && view.Path.HasValue)
        {
            Icon = _icons.Get(view.Path.Value, image => Icon = image);
        }

        bool traffic = view.Traffic is TrafficAvailability.Available or TrafficAvailability.Partial;
        DownRate = Fmt.Rate(view.Rates.Received, traffic);
        UpRate = Fmt.Rate(view.Rates.Sent, traffic);
        UpdateGeneral(view, snapshot);
        UpdateNetwork(view, snapshot, traffic);
        UpdateResources(view);
        UpdateSockets(snapshot);
        if (snapshot.Timestamp - _lastChart > TimeSpan.FromSeconds(4))
        {
            _lastChart = snapshot.Timestamp;
            _ = LoadChartAsync();
        }
    }

    private void UpdateGeneral(ProcessView v, EngineSnapshot snapshot)
    {
        var parent = snapshot.Processes.FirstOrDefault(p => p.Key.Pid == v.ParentPid && p.State == ProcessState.Running && (p.StartTime ?? DateTimeOffset.MaxValue) <= (v.StartTime ?? DateTimeOffset.MaxValue));
        var lines = new List<InfoLine>
        {
            new("Name", v.Name),
            new("Process ID", v.Key.Pid.ToString(CultureInfo.InvariantCulture), "Windows reuses PIDs; this instance is identified by PID and creation time."),
            new("Identity", v.Identity == IdentityConfidence.Exact ? $"PID + creation time ({v.Key.CreateTimeUtc?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)})" : "PID + name (creation time not readable)"),
            new("Parent", v.ParentPid == 0 ? Fmt.NotAvailable : parent is null ? $"{v.ParentPid} (exited or reused)" : $"{parent.Name} ({v.ParentPid})", "The parent PID recorded at creation; the parent may have exited since."),
            new("Path", v.Path.HasValue ? v.Path.Value! : Probe<string>.Explain(v.Path.Status)),
            new("Description", v.Description ?? Fmt.NotAvailable),
            new("Product", v.Product ?? Fmt.NotAvailable),
            new("Company", v.Company ?? Fmt.NotAvailable),
            new("File version", v.Version ?? Fmt.NotAvailable),
            new("Signature", v.Signature?.Description ?? "Not checked yet", "Authenticode (embedded or catalog). Revocation is not checked online."),
            new("Publisher", v.Signature?.Publisher ?? Fmt.NotAvailable),
            new("User", v.UserName ?? "Not readable", v.UserName is null ? "The token of processes owned by other users or SYSTEM can only be read with administrator rights." : null),
            new("Session", v.SessionId < 0 ? Fmt.NotAvailable : v.SessionId == 0 ? "0 (services)" : v.SessionId.ToString(CultureInfo.InvariantCulture)),
            new("Started", Fmt.DateTime(v.StartTime)),
            new("State", StateText),
        };
        if (v.Services.Count > 0)
        {
            lines.Add(new InfoLine("Services", string.Join(", ", v.Services)));
        }

        Replace(General, lines);
    }

    private void UpdateNetwork(ProcessView v, EngineSnapshot snapshot, bool traffic)
    {
        string scope = snapshot.Collector.State == Telemetry.Model.CollectorState.Running ? string.Empty : " (collector stopped)";
        var interfaces = InvolvedInterfaces(snapshot);
        var lines = new List<InfoLine>
        {
            new("Download now", $"{Fmt.Rate(v.Rates.Received, traffic)}  (TCP {Fmt.Rate(v.Rates.TcpReceived, traffic)} · UDP {Fmt.Rate(v.Rates.UdpReceived, traffic)})" + scope),
            new("Upload now", $"{Fmt.Rate(v.Rates.Sent, traffic)}  (TCP {Fmt.Rate(v.Rates.TcpSent, traffic)} · UDP {Fmt.Rate(v.Rates.UdpSent, traffic)})"),
            new("Downloaded", v.HasTraffic || traffic ? $"{Fmt.Bytes(v.Totals.Received)}  (TCP {Fmt.Bytes(v.Totals.TcpReceived)} · UDP {Fmt.Bytes(v.Totals.UdpReceived)})" : Fmt.NotAvailable, "Payload received since monitoring started. Earlier traffic is not known."),
            new("Uploaded", v.HasTraffic || traffic ? $"{Fmt.Bytes(v.Totals.Sent)}  (TCP {Fmt.Bytes(v.Totals.TcpSent)} · UDP {Fmt.Bytes(v.Totals.UdpSent)})" : Fmt.NotAvailable),
            new("Peak (1 s)", v.HasTraffic ? $"↓ {Fmt.Rate(v.PeakReceiveRate)}   ↑ {Fmt.Rate(v.PeakSendRate)}" : Fmt.NotAvailable),
            new("Average", v.MonitoredDuration.TotalSeconds >= 1 ? $"↓ {Fmt.Rate(v.AverageReceiveRate)}   ↑ {Fmt.Rate(v.AverageSendRate)}" : Fmt.NotAvailable, "Total divided by the monitored duration."),
            new("Monitored for", v.MonitoredDuration > TimeSpan.Zero ? Fmt.Duration(v.MonitoredDuration) : Fmt.NotAvailable),
            new("Loopback (local)", v.LoopbackTotals.Total > 0 ? $"↓ {Fmt.Bytes(v.LoopbackTotals.Received)}   ↑ {Fmt.Bytes(v.LoopbackTotals.Sent)}" : "None", "Traffic with other processes on this computer; excluded from the totals above unless enabled in Settings."),
            new("Retransmitted", v.RetransmittedBytes > 0 ? Fmt.Bytes(v.RetransmittedBytes) : "None", "TCP data sent again after loss; a sign of congestion or a poor link."),
            new("Sockets", $"{v.TcpConnections} TCP connections · {v.TcpListeners} listening · {v.UdpEndpoints} UDP"),
            new("Remote addresses", v.RemoteAddresses.ToString(CultureInfo.CurrentCulture)),
            new("Interfaces", interfaces, "Derived from the local addresses of the process's connections; wildcard binds cannot be attributed."),
            new("Last activity", Fmt.Time(v.LastActivity)),
        };
        Replace(Network, lines);
    }

    private string InvolvedInterfaces(EngineSnapshot snapshot)
    {
        var locals = snapshot.Connections.Where(c => c.Owner == Key && !c.IsListener && c.ClosedAt is null).Select(c => c.Local.Address.WithoutScope().UnmapIPv4())
            .Concat(snapshot.UdpEndpoints.Where(u => u.Owner == Key && !u.Local.Address.IsUnspecified).Select(u => u.Local.Address.WithoutScope().UnmapIPv4()))
            .ToHashSet();
        if (locals.Count == 0)
        {
            return "Not known (no open connection or bound address)";
        }

        var names = snapshot.Interfaces
            .Where(i => i.Addresses.Any(a => locals.Contains(a.Address.WithoutScope())))
            .Select(i => i.Name)
            .ToList();
        if (locals.Any(AddressClassifier.IsLoopback))
        {
            names.Add("Loopback");
        }

        return names.Count == 0 ? "Not known (no open connection or bound address)" : string.Join(", ", names.Distinct());
    }

    private void UpdateResources(ProcessView view)
    {
        if (view.State == ProcessState.Exited)
        {
            CpuText = MemoryText = PrivateText = HandlesText = Fmt.NotAvailable;
            ResourceHint = "The process has exited.";
            return;
        }

        var usage = ProcessDetailsReader.GetResourceUsage(Key.Pid);
        if (!usage.HasValue)
        {
            CpuText = MemoryText = PrivateText = HandlesText = Fmt.NotAvailable;
            ResourceHint = usage.Status == InfoStatus.AccessDenied
                ? "Windows denies access to this process's counters (protected or other user's process)."
                : Probe<string>.Explain(usage.Status);
            return;
        }

        var u = usage.Value;
        long now = Stopwatch.GetTimestamp();
        if (_lastCpu is { } last && now > last.Timestamp)
        {
            double wall = (now - last.Timestamp) / (double)_frequency;
            double cpu = (u.TotalCpuTime - last.Cpu).TotalSeconds / wall / Environment.ProcessorCount * 100;
            CpuText = Math.Clamp(cpu, 0, 100).ToString("0.0", CultureInfo.CurrentCulture) + " %";
        }

        _lastCpu = (u.TotalCpuTime, now);
        MemoryText = u.WorkingSetBytes >= 0 ? DataFormat(u.WorkingSetBytes) : Fmt.NotAvailable;
        PrivateText = u.PrivateBytes >= 0 ? DataFormat(u.PrivateBytes) : Fmt.NotAvailable;
        HandlesText = u.HandleCount?.ToString("N0", CultureInfo.CurrentCulture) ?? Fmt.NotAvailable;
        ResourceHint = $"CPU time {u.TotalCpuTime:hh\\:mm\\:ss} · CPU % is relative to all {Environment.ProcessorCount} logical processors.";
    }

    private static string DataFormat(long bytes) => Core.Units.DataFormatter.FormatBytes(bytes, Core.Units.DataUnitSystem.BinaryBytes);

    private void UpdateSockets(EngineSnapshot snapshot)
    {
        var lines = snapshot.Connections
            .Where(c => c.Owner == Key)
            .OrderBy(c => c.ClosedAt is not null).ThenBy(c => c.IsListener).ThenByDescending(c => c.ReceiveRate + c.SendRate)
            .Select(c => new SocketLine(
                "TCP",
                IpEndpointValue.Format(c.Local.Address, c.Local.Port),
                c.IsListener ? "(listening)" : IpEndpointValue.Format(c.Remote.Address, c.Remote.Port) + (c.RemoteHostName is { } h ? $"  {h}" : string.Empty),
                TcpStateInfo.DisplayName(c.State),
                c.IsListener ? string.Empty : Fmt.Bytes(c.ReceivedBytes, c.BytesAvailable),
                c.IsListener ? string.Empty : Fmt.Bytes(c.SentBytes, c.BytesAvailable),
                Fmt.Duration(c.Age(snapshot.Timestamp))))
            .Concat(snapshot.UdpEndpoints.Where(u => u.Owner == Key).Select(u => new SocketLine(
                "UDP",
                IpEndpointValue.Format(u.Local.Address, u.Local.Port),
                u.PeerCount == 0 ? "(no datagrams seen)" : $"{u.PeerCount} peers: " + string.Join(", ", u.Peers.Take(3).Select(p => p.Remote.ToString())),
                u.ClosedAt is null ? "BOUND" : "CLOSED",
                Fmt.Bytes(u.ReceivedBytes, u.BytesAvailable),
                Fmt.Bytes(u.SentBytes, u.BytesAvailable),
                Fmt.Duration((u.ClosedAt ?? snapshot.Timestamp) - (u.CreatedAt ?? u.FirstSeen)))))
            .Take(500)
            .ToList();
        Replace(Sockets, lines);
        SocketSummary = lines.Count == 0 ? "No sockets." : $"{lines.Count(l => l.Protocol == "TCP")} TCP · {lines.Count(l => l.Protocol == "UDP")} UDP";
    }

    private async Task LoadChartAsync()
    {
        var series = await _engine.GetProcessSeriesAsync(Key, 300).ConfigureAwait(true);
        if (series is null)
        {
            HistorySeries = [];
            return;
        }

        var down = Application.Current.TryFindResource("Brush.Data.Download") as Brush;
        var up = Application.Current.TryFindResource("Brush.Data.Upload") as Brush;
        var tcp = Application.Current.TryFindResource("Brush.Data.Tcp") as Brush;
        var udp = Application.Current.TryFindResource("Brush.Data.Udp") as Brush;
        HistorySeries = StackProtocols
            ?
            [
                new ChartSeries { Name = "TCP", Values = series.Select(v => v.TcpReceived + v.TcpSent), Stroke = tcp, Fill = tcp is SolidColorBrush t ? new SolidColorBrush(Color.FromArgb(0x40, t.Color.R, t.Color.G, t.Color.B)) : null },
                new ChartSeries { Name = "UDP", Values = series.Select(v => v.UdpReceived + v.UdpSent), Stroke = udp, Fill = udp is SolidColorBrush u ? new SolidColorBrush(Color.FromArgb(0x40, u.Color.R, u.Color.G, u.Color.B)) : null, Dashed = true },
            ]
            :
            [
                new ChartSeries { Name = "↓ Download", Values = series.Select(v => v.Received), Stroke = down, Fill = Application.Current.TryFindResource("Brush.Data.DownloadFill") as Brush },
                new ChartSeries { Name = "↑ Upload", Values = series.Select(v => v.Sent), Stroke = up, Dashed = true },
            ];
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i < target.Count)
            {
                if (!Equals(target[i], items[i]))
                {
                    target[i] = items[i];
                }
            }
            else
            {
                target.Add(items[i]);
            }
        }

        while (target.Count > items.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
