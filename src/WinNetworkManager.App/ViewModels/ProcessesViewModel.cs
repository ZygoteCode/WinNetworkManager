using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNetworkManager.App.Common;
using WinNetworkManager.App.Services;
using WinNetworkManager.Core.Querying;
using WinNetworkManager.Core.Units;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Infrastructure.Export;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.App.ViewModels;

/// <summary>Processes page: live per-process traffic with search, filters, grouping, sorting and export.</summary>
public sealed partial class ProcessesViewModel : PageViewModel, ISortableTable
{
    private static readonly HashSet<string> FilterKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "pid", "path", "publisher", "user", "state", "rate", "down", "up", "total", "conn", "system",
    };

    private readonly RowSynchronizer<string, ProcessRowBase> _rows = new();
    private readonly SettingsService _settings;
    private readonly IconService _icons;
    private readonly Func<Shell.ShellViewModel> _shell;
    private EngineSnapshot? _snapshot;
    private SearchQuery _query = SearchQuery.Empty;
    private bool _initializing = true;

    /// <summary>Creates the view model.</summary>
    public ProcessesViewModel(SettingsService settings, IconService icons, Func<Shell.ShellViewModel> shell)
    {
        _settings = settings;
        _icons = icons;
        _shell = shell;
        var s = settings.Current.Processes;
        GroupByApplication = s.GroupByApplication;
        HideSystem = s.HideSystemProcesses;
        ShowIdle = s.ShowIdleProcesses;
        _initializing = false;
        settings.Changed += (_, _) =>
        {
            foreach (var row in _rows.Rows.Values)
            {
                row.Reformat();
            }

            Rebuild();
        };
    }

    /// <inheritdoc />
    public override string Title => "Processes";

    /// <inheritdoc />
    public override PageKind Kind => PageKind.Processes;

    /// <summary>Rows shown in the table.</summary>
    public BulkObservableCollection<ProcessRowBase> Rows => _rows.Items;

    /// <inheritdoc />
    public string SortColumn { get; private set; } = "rate";

    /// <inheritdoc />
    public bool SortDescending { get; private set; } = true;

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool GroupByApplication { get; set; }
    [ObservableProperty] public partial bool HideSystem { get; set; }
    [ObservableProperty] public partial bool ShowIdle { get; set; } = true;
    [ObservableProperty] public partial ProcessRowBase? SelectedRow { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = string.Empty;
    [ObservableProperty] public partial bool TrafficUnavailable { get; set; }
    [ObservableProperty] public partial string? ExportMessage { get; set; }

    partial void OnSearchTextChanged(string value)
    {
        _query = SearchQueryParser.Parse(value, FilterKeys);
        Rebuild();
    }

    partial void OnGroupByApplicationChanged(bool value)
    {
        if (_initializing)
        {
            return;
        }

        Persist(s => s with { Processes = s.Processes with { GroupByApplication = value } });
        Rebuild();
    }

    partial void OnHideSystemChanged(bool value)
    {
        if (_initializing)
        {
            return;
        }

        Persist(s => s with { Processes = s.Processes with { HideSystemProcesses = value } });
        Rebuild();
    }

    partial void OnShowIdleChanged(bool value)
    {
        if (_initializing)
        {
            return;
        }

        Persist(s => s with { Processes = s.Processes with { ShowIdleProcesses = value } });
        Rebuild();
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
            SortDescending = column is not ("name" or "publisher" or "description" or "path" or "user" or "status");
        }

        Rebuild();
    }

    /// <summary>Opens the inspector for the selected (or given) row.</summary>
    [RelayCommand]
    private void OpenDetails(ProcessRowBase? row)
    {
        row ??= SelectedRow;
        switch (row)
        {
            case ProcessRow process:
                _shell().OpenInspector(process.Key);
                break;
            case AppGroupRow group:
                group.IsExpanded = !group.IsExpanded;
                Rebuild();
                break;
        }
    }

    /// <summary>Toggles a group.</summary>
    [RelayCommand]
    private void ToggleGroup(ProcessRowBase? row)
    {
        // The expander button exists in every row template (collapsed for processes), so the parameter can be any row.
        if (row is AppGroupRow group)
        {
            group.IsExpanded = !group.IsExpanded;
            Rebuild();
        }
    }

    /// <summary>Shows the connections of the selected process.</summary>
    [RelayCommand]
    private void ShowConnections()
    {
        if (SelectedRow is ProcessRow row && _shell().Pages.TryGetValue(PageKind.Connections, out var page) && page is ConnectionsViewModel connections)
        {
            connections.SearchText = $"pid={row.Key.Pid}";
            _shell().Navigate(PageKind.Connections);
        }
    }

    [RelayCommand]
    private void CopyName() => ShellActions.Copy(SelectedRow?.Name);

    [RelayCommand]
    private void CopyPid() => ShellActions.Copy(SelectedRow is ProcessRow r ? r.PidText : null);

    [RelayCommand]
    private void CopyPath() => ShellActions.Copy((SelectedRow as ProcessRow)?.Path ?? (SelectedRow as AppGroupRow)?.Path);

    [RelayCommand]
    private void OpenLocation() => ShellActions.ShowInExplorer((SelectedRow as ProcessRow)?.Path ?? (SelectedRow as AppGroupRow)?.Path);

    [RelayCommand]
    private void HideProcess()
    {
        string? name = (SelectedRow as ProcessRow)?.Name ?? (SelectedRow as AppGroupRow)?.Children.FirstOrDefault()?.Name;
        if (name is null)
        {
            return;
        }

        Persist(s => s with { Processes = s.Processes with { HiddenProcessNames = [.. s.Processes.HiddenProcessNames, name] } });
        Rebuild();
    }

    /// <summary>
    /// Ends the selected process — or every running instance of a selected group — after confirmation. Each instance is
    /// identified by PID and creation time, so a PID reused since the row was shown is never ended.
    /// </summary>
    [RelayCommand]
    private async Task EndProcessAsync()
    {
        var targets = (SelectedRow switch
        {
            ProcessRow p => [p],
            AppGroupRow g => g.Children,
            _ => [],
        }).Where(r => !r.IsExited).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var first = targets[0];
        string what = targets.Count == 1 ? $"{first.Name} (PID {first.PidText})" : $"all {targets.Count} running instances of {first.Name}";
        var message = new System.Text.StringBuilder()
            .Append(CultureInfo.CurrentCulture, $"End {what}?")
            .AppendLine().AppendLine()
            .Append("The program is closed immediately, without asking it to save: unsaved work in it is lost.");
        var services = targets.SelectMany(t => t.View?.Services ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (services.Count > 0)
        {
            message.AppendLine().AppendLine().Append(CultureInfo.CurrentCulture, $"It hosts Windows services ({string.Join(", ", services.Take(6))}{(services.Count > 6 ? ", …" : string.Empty)}); they stop with it.");
        }

        if (targets.Any(t => t.IsSystem))
        {
            message.AppendLine().AppendLine().Append("This is a system process: ending it can make Windows unstable until you restart.");
        }

        if (!Shell.Dialogs.Confirm(targets.Count == 1 ? "End process" : "End processes", message.ToString(), "End process", kind: Shell.DialogKind.Warning, destructive: true))
        {
            return;
        }

        var keys = targets.Select(t => t.Key).ToList();
        var results = await Task.Run(() => keys.Select(k => Platform.Windows.Processes.ProcessTerminator.Terminate(k, TimeSpan.FromSeconds(3))).ToList()).ConfigureAwait(true);
        var problems = results.Where(r => !r.IsGone).ToList();
        if (problems.Count > 0)
        {
            string text = problems.Count == 1 && results.Count == 1
                ? problems[0].Message
                : $"{results.Count - problems.Count} of {results.Count} processes were ended." + Environment.NewLine + Environment.NewLine +
                  string.Join(Environment.NewLine, problems.Select(p => p.Message).Distinct());
            Shell.Dialogs.Inform("End process", text, problems.Any(p => p.Outcome is Platform.Windows.Processes.TerminateOutcome.Failed) ? Shell.DialogKind.Error : Shell.DialogKind.Warning);
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>Exports the visible rows.</summary>
    [RelayCommand]
    private void Export(string? format)
    {
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        var settings = _settings.Current;
        string? path = ShellActions.AskSavePath(
            $"processes-{DateTime.Now:yyyyMMdd-HHmmss}.{(json ? "json" : "csv")}",
            json ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv",
            settings.Export.LastFolder);
        if (path is null)
        {
            return;
        }

        try
        {
            var table = BuildExport();
            if (json)
            {
                TableExporter.WriteJson(path, table);
            }
            else
            {
                TableExporter.WriteCsv(path, table, settings.Export.CsvDelimiter);
            }

            Persist(s => s with { Export = s.Export with { LastFolder = System.IO.Path.GetDirectoryName(path) } });
            ExportMessage = $"Exported {table.Rows.Count} rows to {System.IO.Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportMessage = "Export failed: " + ErrorText.Describe(ex);
        }
    }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        _snapshot = snapshot;
        var alive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in snapshot.Processes)
        {
            string key = "p:" + view.Key;
            alive.Add(key);
            var row = (ProcessRow)_rows.GetOrCreate(key, () => new ProcessRow(view.Key));
            row.Update(view, 0);
            if (row.Icon is null && row.Path is { } path)
            {
                var target = row;
                row.Icon = _icons.Get(path, image => target.Icon = image);
            }
        }

        foreach (var key in _rows.Rows.Keys.Where(k => k.StartsWith("g:", StringComparison.Ordinal)))
        {
            alive.Add(key);
        }

        _rows.Retain(alive);
        TrafficUnavailable = snapshot.System.Traffic is TrafficAvailability.RequiresCollector or TrafficAvailability.Starting;
        Rebuild();
    }

    private void Rebuild()
    {
        if (_snapshot is null || !IsActive)
        {
            return;
        }

        var hidden = new HashSet<string>(_settings.Current.Processes.HiddenProcessNames, StringComparer.OrdinalIgnoreCase);
        var processes = _rows.Rows.Values.OfType<ProcessRow>()
            .Where(r => r.View is not null)
            .Where(r => !hidden.Contains(r.Name))
            .Where(r => !HideSystem || !r.IsSystem)
            .Where(r => ShowIdle || r.Total > 0 || r.IsActive || r.Connections > 0)
            .Where(Matches)
            .ToList();

        var comparison = Comparison();
        var desired = new List<ProcessRowBase>(processes.Count + 16);
        if (GroupByApplication)
        {
            var groups = processes.GroupBy(p => p.AppId).ToList();
            var top = new List<ProcessRowBase>();
            var aliveGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in groups)
            {
                if (g.Count() == 1)
                {
                    var single = g.First();
                    single.Update(single.View!, 0);
                    top.Add(single);
                    continue;
                }

                var group = (AppGroupRow)_rows.GetOrCreate("g:" + g.Key, () => new AppGroupRow(g.Key));
                group.Children.Clear();
                group.Children.AddRange(g);
                group.Aggregate();
                if (group.Icon is null && group.Path is { } path)
                {
                    var target = group;
                    group.Icon = _icons.Get(path, image => target.Icon = image);
                }

                aliveGroups.Add("g:" + g.Key);
                top.Add(group);
            }

            top.Sort(comparison);
            foreach (var row in top)
            {
                desired.Add(row);
                if (row is AppGroupRow { IsExpanded: true } group)
                {
                    var children = group.Children.ToList();
                    children.Sort(comparison);
                    foreach (var child in children)
                    {
                        child.Update(child.View!, 1);
                        desired.Add(child);
                    }
                }
            }
        }
        else
        {
            foreach (var p in processes)
            {
                p.Update(p.View!, 0);
            }

            processes.Sort(comparison);
            desired.AddRange(processes);
        }

        _rows.Apply(desired);
        int total = _snapshot.Processes.Count;
        int withTraffic = _snapshot.Processes.Count(p => p.Rates.Total > 0);
        Summary = string.Create(CultureInfo.CurrentCulture, $"{total} processes · {withTraffic} transferring now · {processes.Count} shown");
    }

    private bool Matches(ProcessRow row)
    {
        if (_query.IsEmpty)
        {
            return true;
        }

        return _query.Evaluate(
            text => Contains(row.Name, text) || Contains(row.PathText, text) || Contains(row.Publisher, text) || Contains(row.Description, text) || row.PidText == text,
            filter => filter.Key switch
            {
                "name" => Contains(row.Name, filter.Value),
                "pid" => int.TryParse(filter.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && row.Pid == pid,
                "path" => Contains(row.PathText, filter.Value),
                "publisher" => Contains(row.Publisher, filter.Value),
                "user" => Contains(row.UserText, filter.Value),
                "state" => Contains(row.StatusText, filter.Value),
                "system" => filter.Value is "yes" or "true" ? row.IsSystem : !row.IsSystem,
                "rate" => CompareBytes(row.Rate, filter),
                "down" => CompareBytes(row.Down, filter),
                "up" => CompareBytes(row.Up, filter),
                "total" => CompareBytes(row.Total, filter),
                "conn" => CompareNumber(row.Connections, filter),
                _ => null,
            });
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

    private static bool? CompareBytes(double value, SearchFilter filter) =>
        DataFormatter.TryParseBytes(filter.Value, out double threshold) ? Compare(value, threshold, filter.Operator) : null;

    private static bool? CompareNumber(double value, SearchFilter filter) =>
        double.TryParse(filter.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold) ? Compare(value, threshold, filter.Operator) : null;

    private static bool Compare(double value, double threshold, FilterOperator op) => op switch
    {
        FilterOperator.GreaterThan => value > threshold,
        FilterOperator.GreaterOrEqual or FilterOperator.Matches => value >= threshold,
        FilterOperator.LessThan => value < threshold,
        FilterOperator.LessOrEqual => value <= threshold,
        _ => Math.Abs(value - threshold) < 0.5,
    };

    private Comparison<ProcessRowBase> Comparison()
    {
        Func<ProcessRowBase, IComparable?> key = SortColumn switch
        {
            "name" => r => r.Name,
            "pid" => r => r.Pid,
            "down" => r => r.Down,
            "up" => r => r.Up,
            "tcpDown" => r => r.TcpDown,
            "tcpUp" => r => r.TcpUp,
            "udpDown" => r => r.UdpDown,
            "udpUp" => r => r.UdpUp,
            "totalDown" => r => r.TotalDown,
            "totalUp" => r => r.TotalUp,
            "total" => r => r.Total,
            "avgDown" => r => r.AvgDown,
            "avgUp" => r => r.AvgUp,
            "peakDown" => r => r.PeakDown,
            "peakUp" => r => r.PeakUp,
            "conn" => r => r.Connections,
            "remotes" => r => r.Remotes,
            "retrans" => r => r.Retransmitted,
            "publisher" => r => r.Publisher,
            "description" => r => r.Description,
            "path" => r => r.PathText,
            "user" => r => r.UserText,
            "started" => r => r.Started,
            "monitored" => r => r.Monitored,
            "status" => r => r.StatusText,
            _ => r => r.Rate,
        };
        int sign = SortDescending ? -1 : 1;
        return (a, b) =>
        {
            int c = Comparer<IComparable?>.Default.Compare(key(a), key(b));
            if (c == 0)
            {
                // Stable, deterministic tie-breaks: busiest first, then name, then PID.
                c = -a.Rate.CompareTo(b.Rate) * sign;
                if (c == 0)
                {
                    c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                }

                if (c == 0)
                {
                    c = a.Pid.CompareTo(b.Pid);
                }

                return c;
            }

            return c * sign;
        };
    }

    private ExportTable BuildExport()
    {
        var snapshot = _snapshot!;
        var columns = new List<ExportColumn>
        {
            new("snapshot_utc", "Time the values were computed (UTC)"),
            new("pid", "Process id"),
            new("process_start_utc", "Process creation time (UTC); identifies the instance together with the PID"),
            new("name", "Image name"),
            new("path", "Executable path, when readable"),
            new("publisher", "Signer / company"),
            new("state", "Running or exited"),
            new("download_bytes_per_s", "TCP+UDP payload received, averaged over the rate window"),
            new("upload_bytes_per_s", "TCP+UDP payload sent, averaged over the rate window"),
            new("tcp_download_bytes_per_s", "TCP payload received per second"),
            new("tcp_upload_bytes_per_s", "TCP payload sent per second"),
            new("udp_download_bytes_per_s", "UDP payload received per second"),
            new("udp_upload_bytes_per_s", "UDP payload sent per second"),
            new("total_download_bytes", "Payload received since monitoring started"),
            new("total_upload_bytes", "Payload sent since monitoring started"),
            new("peak_download_bytes_per_s", "Highest 1-second download rate observed"),
            new("peak_upload_bytes_per_s", "Highest 1-second upload rate observed"),
            new("average_download_bytes_per_s", "Total received / monitored duration"),
            new("average_upload_bytes_per_s", "Total sent / monitored duration"),
            new("tcp_retransmitted_bytes", "TCP bytes retransmitted (not included in upload)"),
            new("sockets", "Active TCP connections + UDP endpoints"),
            new("remote_addresses", "Distinct remote addresses"),
            new("monitored_seconds", "Time covered by the collector for this process"),
            new("traffic_available", "False when per-process traffic could not be measured (no collector)"),
        };
        var rows = new List<IReadOnlyList<object?>>();
        foreach (var row in Rows.OfType<ProcessRow>())
        {
            var v = row.View!;
            bool available = row.TrafficAvailable;
            rows.Add(
            [
                snapshot.Timestamp, v.Key.Pid, v.StartTime, v.Name, v.Path.Value, row.Publisher, v.State.ToString(),
                available ? v.Rates.Received : null, available ? v.Rates.Sent : null,
                available ? v.Rates.TcpReceived : null, available ? v.Rates.TcpSent : null,
                available ? v.Rates.UdpReceived : null, available ? v.Rates.UdpSent : null,
                v.Totals.Received, v.Totals.Sent, v.PeakReceiveRate, v.PeakSendRate, v.AverageReceiveRate, v.AverageSendRate,
                v.RetransmittedBytes, v.TcpConnections + v.UdpEndpoints, v.RemoteAddresses, Math.Round(v.MonitoredDuration.TotalSeconds, 1), available,
            ]);
        }

        var metadata = ExportMetadata.Create(_settings.Current, snapshot, "Processes");
        return new ExportTable("WinNetworkManager — processes", columns, rows, metadata);
    }

    private void Persist(Func<Core.Settings.AppSettings, Core.Settings.AppSettings> change)
    {
        try
        {
            _settings.Update(change);
        }
        catch (IOException)
        {
            // Preferences are best effort; the in-memory value still applies.
        }
    }
}
