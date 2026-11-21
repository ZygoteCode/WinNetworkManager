using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNetworkManager.App.Common;
using WinNetworkManager.App.Services;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Querying;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Infrastructure.Export;
using WinNetworkManager.Telemetry.Model;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.App.ViewModels;

/// <summary>Which socket model is displayed.</summary>
public enum SocketTab
{
    /// <summary>TCP connections and listeners.</summary>
    Tcp,

    /// <summary>UDP endpoints.</summary>
    Udp,
}

/// <summary>Connection Explorer: TCP connections/listeners and UDP endpoints (distinct models), with filters and export.</summary>
public sealed partial class ConnectionsViewModel : PageViewModel, ISortableTable
{
    private static readonly HashSet<string> FilterKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "pid", "process", "ip", "lip", "rip", "port", "lport", "rport", "state", "scope", "host", "service",
    };

    private readonly RowSynchronizer<long, TcpRow> _tcp = new();
    private readonly RowSynchronizer<long, UdpRow> _udp = new();
    private readonly IconService _icons;
    private readonly SettingsService _settings;
    private readonly Func<Shell.ShellViewModel> _shell;
    private EngineSnapshot? _snapshot;
    private SearchQuery _query = SearchQuery.Empty;
    private bool _initializing = true;

    /// <summary>Creates the view model.</summary>
    public ConnectionsViewModel(IconService icons, SettingsService settings, Func<Shell.ShellViewModel> shell)
    {
        _icons = icons;
        _settings = settings;
        _shell = shell;
        ResolveNames = settings.Current.Monitoring.ResolveRemoteHostNames;
        _initializing = false;
        settings.Changed += (_, s) =>
        {
            _initializing = true;
            ResolveNames = s.Monitoring.ResolveRemoteHostNames;
            _initializing = false;
            if (_snapshot is not null)
            {
                OnSnapshot(_snapshot);
            }
        };
    }

    /// <inheritdoc />
    public override string Title => "Connections";

    /// <inheritdoc />
    public override PageKind Kind => PageKind.Connections;

    /// <summary>TCP rows.</summary>
    public BulkObservableCollection<TcpRow> TcpRows => _tcp.Items;

    /// <summary>UDP rows.</summary>
    public BulkObservableCollection<UdpRow> UdpRows => _udp.Items;

    /// <inheritdoc />
    public string SortColumn { get; private set; } = "state";

    /// <inheritdoc />
    public bool SortDescending { get; private set; }

    [ObservableProperty] public partial SocketTab Tab { get; set; } = SocketTab.Tcp;
    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShowListening { get; set; } = true;
    [ObservableProperty] public partial bool ShowClosed { get; set; } = true;
    [ObservableProperty] public partial bool ShowLoopback { get; set; } = true;
    [ObservableProperty] public partial bool ResolveNames { get; set; }
    [ObservableProperty] public partial TcpRow? SelectedTcp { get; set; }
    [ObservableProperty] public partial UdpRow? SelectedUdp { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = string.Empty;
    [ObservableProperty] public partial string? ActionMessage { get; set; }

    partial void OnTabChanged(SocketTab value)
    {
        SortColumn = value == SocketTab.Tcp ? "state" : "process";
        SortDescending = false;
        Rebuild();
    }

    partial void OnSearchTextChanged(string value)
    {
        _query = SearchQueryParser.Parse(value, FilterKeys);
        Rebuild();
    }

    partial void OnShowListeningChanged(bool value) => Rebuild();

    partial void OnShowClosedChanged(bool value) => Rebuild();

    partial void OnShowLoopbackChanged(bool value) => Rebuild();

    partial void OnResolveNamesChanged(bool value)
    {
        if (_initializing)
        {
            return;
        }

        try
        {
            _settings.Update(s => s with { Monitoring = s.Monitoring with { ResolveRemoteHostNames = value } });
        }
        catch (IOException)
        {
            // The engine still receives the in-memory change.
        }
    }

    /// <inheritdoc />
    public void SortBy(string column, bool? descending = null)
    {
        if (descending is { } d)
        {
            SortColumn = column;
            SortDescending = d;
        }
        else if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = column is "down" or "up" or "downTotal" or "upTotal" or "age" or "retrans" or "peers";
        }

        Rebuild();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void CopyLocalAddress() => Copy(Tab == SocketTab.Tcp ? SelectedTcp?.View?.Local.Address.ToString() : SelectedUdp?.View?.Local.Address.ToString());

    [RelayCommand]
    private void CopyLocalEndpoint() => Copy(Tab == SocketTab.Tcp ? SelectedTcp?.LocalEndpoint : SelectedUdp?.LocalEndpoint);

    [RelayCommand]
    private void CopyRemoteAddress() => Copy(SelectedTcp?.View is { IsListener: false } v ? v.Remote.Address.ToString() : null);

    [RelayCommand]
    private void CopyRemoteEndpoint() => Copy(SelectedTcp?.RemoteEndpoint);

    [RelayCommand]
    private void GoToProcess()
    {
        var owner = Tab == SocketTab.Tcp ? SelectedTcp?.Owner : SelectedUdp?.Owner;
        if (owner is { } key)
        {
            _shell().OpenInspector(key);
        }
    }

    [RelayCommand]
    private void FilterProcess()
    {
        int pid = Tab == SocketTab.Tcp ? SelectedTcp?.Pid ?? 0 : SelectedUdp?.Pid ?? 0;
        if (pid != 0)
        {
            SearchText = $"pid={pid}";
        }
    }

    [RelayCommand]
    private void FilterRemote()
    {
        if (SelectedTcp?.View is { IsListener: false } v)
        {
            SearchText = $"rip:{v.Remote.Address}";
        }
    }

    [RelayCommand]
    private void FilterPort()
    {
        if (Tab == SocketTab.Tcp && SelectedTcp?.View is { } t)
        {
            SearchText = t.IsListener ? $"lport={t.Local.Port}" : $"rport={t.Remote.Port}";
        }
        else if (SelectedUdp?.View is { } u)
        {
            SearchText = $"lport={u.Local.Port}";
        }
    }

    [RelayCommand]
    private void Diagnose(string? tool)
    {
        if (SelectedTcp?.View is not { IsListener: false } v || v.Remote.Address.IsUnspecified)
        {
            return;
        }

        if (_shell().Pages.TryGetValue(PageKind.Diagnostics, out var page) && page is DiagnosticsViewModel diagnostics)
        {
            diagnostics.Prefill(tool ?? "ping", v.Remote.Address.ToString(), v.Remote.Port);
            _shell().Navigate(PageKind.Diagnostics);
        }
    }

    /// <summary>Exports the rows shown in the current tab.</summary>
    [RelayCommand]
    private void Export(string? format)
    {
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        string? path = ShellActions.AskSavePath(
            $"{(Tab == SocketTab.Tcp ? "tcp" : "udp")}-{DateTime.Now:yyyyMMdd-HHmmss}.{(json ? "json" : "csv")}",
            json ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv",
            _settings.Current.Export.LastFolder);
        if (path is null || _snapshot is null)
        {
            return;
        }

        try
        {
            var table = Tab == SocketTab.Tcp ? BuildTcpExport(_snapshot) : BuildUdpExport(_snapshot);
            if (json)
            {
                TableExporter.WriteJson(path, table);
            }
            else
            {
                TableExporter.WriteCsv(path, table, _settings.Current.Export.CsvDelimiter);
            }

            ActionMessage = $"Exported {table.Rows.Count} rows to {System.IO.Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActionMessage = "Export failed: " + ErrorText.Describe(ex);
        }
    }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        _snapshot = snapshot;
        bool collector = snapshot.Collector.State == CollectorState.Running;
        var paths = new Dictionary<Core.Processes.ProcessKey, string>();
        foreach (var process in snapshot.Processes)
        {
            if (process.Path.HasValue)
            {
                paths[process.Key] = process.Path.Value!;
            }
        }

        var aliveTcp = new HashSet<long>();
        foreach (var view in snapshot.Connections)
        {
            aliveTcp.Add(view.Id);
            var row = _tcp.GetOrCreate(view.Id, () => new TcpRow(view.Id));
            row.Update(view, snapshot.Timestamp, collector);
            LoadIcon(paths, view.Owner, i => row.Icon = i, row.Icon);
        }

        _tcp.Retain(aliveTcp);
        var aliveUdp = new HashSet<long>();
        foreach (var view in snapshot.UdpEndpoints)
        {
            aliveUdp.Add(view.Id);
            var row = _udp.GetOrCreate(view.Id, () => new UdpRow(view.Id));
            row.Update(view, snapshot.Timestamp, collector);
            LoadIcon(paths, view.Owner, i => row.Icon = i, row.Icon);
        }

        _udp.Retain(aliveUdp);
        Rebuild();
    }

    private void LoadIcon(Dictionary<Core.Processes.ProcessKey, string> paths, Core.Processes.ProcessKey? owner, Action<System.Windows.Media.ImageSource?> set, System.Windows.Media.ImageSource? current)
    {
        if (current is not null || owner is not { } key || !paths.TryGetValue(key, out string? path))
        {
            return;
        }

        var icon = _icons.Get(path, set);
        if (icon is not null)
        {
            set(icon);
        }
    }

    private void Rebuild()
    {
        if (_snapshot is null || !IsActive)
        {
            return;
        }

        int sign = SortDescending ? -1 : 1;
        if (Tab == SocketTab.Tcp)
        {
            var rows = _tcp.Rows.Values.Where(r => r.View is not null && IncludeTcp(r)).ToList();
            Func<TcpRow, IComparable?> key = SortColumn switch
            {
                "process" => r => r.ProcessName,
                "pid" => r => r.Pid,
                "lip" => r => r.View!.Local.Address,
                "lport" => r => r.View!.Local.Port,
                "rip" => r => r.View!.Remote.Address,
                "rport" => r => r.View!.Remote.Port,
                "host" => r => r.RemoteHost,
                "scope" => r => r.ScopeText,
                "age" => r => r.Age,
                "down" => r => r.DownRate,
                "up" => r => r.UpRate,
                "downTotal" => r => r.DownTotal,
                "upTotal" => r => r.UpTotal,
                "retrans" => r => r.View!.Retransmissions,
                "service" => r => r.ServiceText,
                _ => r => StateOrder(r),
            };
            rows.Sort((a, b) =>
            {
                int c = Comparer<IComparable?>.Default.Compare(key(a), key(b)) * sign;
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });
            _tcp.Apply(rows);
        }
        else
        {
            var rows = _udp.Rows.Values.Where(r => r.View is not null && IncludeUdp(r)).ToList();
            Func<UdpRow, IComparable?> key = SortColumn switch
            {
                "pid" => r => r.Pid,
                "lip" => r => r.View!.Local.Address,
                "lport" => r => r.View!.Local.Port,
                "peers" => r => r.PeerCount,
                "down" => r => r.DownRate,
                "up" => r => r.UpRate,
                "downTotal" => r => r.DownTotal,
                "upTotal" => r => r.UpTotal,
                "age" => r => r.Age,
                "service" => r => r.ServiceText,
                _ => r => r.ProcessName,
            };
            rows.Sort((a, b) =>
            {
                int c = Comparer<IComparable?>.Default.Compare(key(a), key(b)) * sign;
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });
            _udp.Apply(rows);
        }

        var s = _snapshot.System;
        Summary = string.Create(CultureInfo.CurrentCulture,
            $"{s.TcpConnections} established · {s.TcpListeners} listening · {s.TcpTimeWait} TIME_WAIT · {s.UdpEndpoints} UDP endpoints · showing {(Tab == SocketTab.Tcp ? TcpRows.Count : UdpRows.Count)}");
    }

    private static int StateOrder(TcpRow row) => row.View!.State switch
    {
        TcpState.Established => 0,
        TcpState.SynSent or TcpState.SynReceived => 1,
        TcpState.CloseWait or TcpState.FinWait1 or TcpState.FinWait2 or TcpState.Closing or TcpState.LastAck => 2,
        TcpState.Listen => 3,
        TcpState.TimeWait => 4,
        _ => 5,
    };

    private bool IncludeTcp(TcpRow row)
    {
        var v = row.View!;
        if (!ShowListening && v.IsListener)
        {
            return false;
        }

        if (!ShowClosed && (v.ClosedAt is not null || v.State is TcpState.TimeWait or TcpState.Closed))
        {
            return false;
        }

        if (!ShowLoopback && v.IsLoopback)
        {
            return false;
        }

        return _query.IsEmpty || _query.Evaluate(
            text => Contains(row.ProcessName, text) || Contains(row.LocalAddress, text) || Contains(row.RemoteAddress, text) ||
                    row.LocalPort == text || row.RemotePort == text || Contains(row.RemoteHost, text) || Contains(row.StateText, text) || Contains(row.ServiceText, text),
            f => f.Key switch
            {
                "pid" => int.TryParse(f.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && (v.OwnerPid == pid || v.Owner?.Pid == pid),
                "process" => Contains(row.ProcessName, f.Value),
                "ip" => Contains(row.LocalAddress, f.Value) || Contains(row.RemoteAddress, f.Value),
                "lip" => Contains(row.LocalAddress, f.Value),
                "rip" => Contains(row.RemoteAddress, f.Value),
                "port" => row.LocalPort == f.Value || row.RemotePort == f.Value,
                "lport" => row.LocalPort == f.Value,
                "rport" => row.RemotePort == f.Value,
                "state" => Contains(row.StateText, f.Value),
                "scope" => Contains(row.ScopeText, f.Value),
                "host" => Contains(row.RemoteHost, f.Value),
                "service" => Contains(row.ServiceText, f.Value),
                _ => null,
            });
    }

    private bool IncludeUdp(UdpRow row)
    {
        var v = row.View!;
        if (!ShowClosed && v.ClosedAt is not null)
        {
            return false;
        }

        if (!ShowLoopback && AddressClassifier.IsLoopback(v.Local.Address))
        {
            return false;
        }

        return _query.IsEmpty || _query.Evaluate(
            text => Contains(row.ProcessName, text) || Contains(row.LocalAddress, text) || row.LocalPort == text || Contains(row.ServiceText, text) ||
                    v.Peers.Any(p => p.Remote.Address.ToString().Contains(text, StringComparison.OrdinalIgnoreCase)),
            f => f.Key switch
            {
                "pid" => int.TryParse(f.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && v.OwnerPid == pid,
                "process" => Contains(row.ProcessName, f.Value),
                "ip" or "lip" => Contains(row.LocalAddress, f.Value),
                "rip" => v.Peers.Any(p => p.Remote.Address.ToString().Contains(f.Value, StringComparison.OrdinalIgnoreCase)),
                "port" or "lport" => row.LocalPort == f.Value,
                "service" => Contains(row.ServiceText, f.Value),
                _ => null,
            });
    }

    private static bool Contains(string? haystack, string needle) => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void Copy(string? text)
    {
        if (ShellActions.Copy(text))
        {
            ActionMessage = $"Copied {text}";
        }
    }

    private ExportTable BuildTcpExport(EngineSnapshot snapshot)
    {
        var columns = new ExportColumn[]
        {
            new("snapshot_utc", "Time of the snapshot (UTC)"),
            new("protocol", "TCP"),
            new("pid", "Owner PID reported by Windows (0 for TIME_WAIT)"),
            new("process", "Owner process (last known owner for TIME_WAIT)"),
            new("local_address", "Local IP address"),
            new("local_port", "Local port"),
            new("remote_address", "Remote IP address (unspecified for listeners)"),
            new("remote_port", "Remote port"),
            new("remote_host", "Reverse DNS name, when resolution is enabled"),
            new("remote_scope", "Loopback / private / public / …"),
            new("state", "TCP state"),
            new("created_utc", "Creation time reported by Windows, when available"),
            new("first_seen_utc", "First time this application saw the connection"),
            new("closed_utc", "Time the connection disappeared from the table"),
            new("received_bytes", "Payload received while monitored"),
            new("sent_bytes", "Payload sent while monitored"),
            new("receive_bytes_per_s", "Receive rate over the rate window"),
            new("send_bytes_per_s", "Send rate over the rate window"),
            new("retransmissions", "TCP retransmission events"),
            new("service", "Owning service/module, when resolved"),
            new("bytes_measured", "False when bytes could not be measured (no collector)"),
        };
        var rows = TcpRows.Select(r =>
        {
            var v = r.View!;
            return (IReadOnlyList<object?>)new object?[]
            {
                snapshot.Timestamp, "TCP", v.OwnerPid, v.OwnerName, v.Local.Address.ToString(), (int)v.Local.Port,
                v.IsListener ? null : v.Remote.Address.ToString(), v.IsListener ? null : (int)v.Remote.Port, v.RemoteHostName,
                v.IsListener ? "listening" : AddressClassifier.DisplayName(v.RemoteScope), TcpStateInfo.DisplayName(v.State),
                v.CreatedAt, v.FirstSeen, v.ClosedAt, v.BytesAvailable ? v.ReceivedBytes : null, v.BytesAvailable ? v.SentBytes : null,
                v.BytesAvailable ? v.ReceiveRate : null, v.BytesAvailable ? v.SendRate : null, v.Retransmissions, v.OwnerModule, v.BytesAvailable,
            };
        }).ToList();
        return new ExportTable("WinNetworkManager — TCP connections", columns, rows, ExportMetadata.Create(_settings.Current, snapshot, "Connections (TCP)"));
    }

    private ExportTable BuildUdpExport(EngineSnapshot snapshot)
    {
        var columns = new ExportColumn[]
        {
            new("snapshot_utc", "Time of the snapshot (UTC)"),
            new("protocol", "UDP"),
            new("pid", "Owner PID"),
            new("process", "Owner process"),
            new("local_address", "Local IP address (* = all addresses)"),
            new("local_port", "Local port"),
            new("observed_peers", "Distinct remote endpoints seen in datagrams (UDP has no connections)"),
            new("peer_endpoints", "Most recent observed peers, ';'-separated"),
            new("received_bytes", "Payload received while monitored"),
            new("sent_bytes", "Payload sent while monitored"),
            new("created_utc", "Bind time reported by Windows"),
            new("first_seen_utc", "First seen by this application"),
            new("service", "Owning service/module"),
            new("bytes_measured", "False when bytes could not be measured"),
        };
        var rows = UdpRows.Select(r =>
        {
            var v = r.View!;
            return (IReadOnlyList<object?>)new object?[]
            {
                snapshot.Timestamp, "UDP", v.OwnerPid, v.OwnerName, v.Local.Address.ToString(), (int)v.Local.Port, v.PeerCount,
                string.Join(";", v.Peers.Select(p => p.Remote.ToString())), v.BytesAvailable ? v.ReceivedBytes : null, v.BytesAvailable ? v.SentBytes : null,
                v.CreatedAt, v.FirstSeen, v.OwnerModule, v.BytesAvailable,
            };
        }).ToList();
        return new ExportTable("WinNetworkManager — UDP endpoints", columns, rows, ExportMetadata.Create(_settings.Current, snapshot, "Connections (UDP)"));
    }
}
