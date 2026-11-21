using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNetworkManager.App.Services;
using WinNetworkManager.Core.Units;
using WinNetworkManager.Engine;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Infrastructure.Export;
using WinNetworkManager.Infrastructure.History;
using WinNetworkManager.UI.Controls;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.App.ViewModels;

/// <summary>Time range choices.</summary>
public enum HistoryRange
{
    /// <summary>Last 60 seconds.</summary>
    Minute,

    /// <summary>Last 5 minutes.</summary>
    FiveMinutes,

    /// <summary>Last 15 minutes.</summary>
    FifteenMinutes,

    /// <summary>Last hour.</summary>
    Hour,

    /// <summary>Last 24 hours.</summary>
    Day,

    /// <summary>Last 7 days.</summary>
    Week,

    /// <summary>Last 30 days.</summary>
    Month,

    /// <summary>User-defined.</summary>
    Custom,
}

/// <summary>Application line in the history table.</summary>
public sealed record HistoryAppRow(string AppKey, string Name, string? Path, long Received, long Sent, long TcpBytes, long UdpBytes)
{
    /// <summary>Total bytes.</summary>
    public long Total => Received + Sent;

    /// <summary>Formatted download.</summary>
    public string DownText => Fmt.Bytes(Received);

    /// <summary>Formatted upload.</summary>
    public string UpText => Fmt.Bytes(Sent);

    /// <summary>Formatted total.</summary>
    public string TotalText => Fmt.Bytes(Total);

    /// <summary>Formatted TCP bytes.</summary>
    public string TcpText => Fmt.Bytes(TcpBytes);

    /// <summary>Formatted UDP bytes.</summary>
    public string UdpText => Fmt.Bytes(UdpBytes);
}

/// <summary>An interface that can be charted.</summary>
public sealed record InterfaceChoice(Guid Id, string Name);

/// <summary>Traffic history: ranges from 60 s to 30 days, charts, top applications, export.</summary>
public sealed partial class HistoryViewModel : PageViewModel
{
    private const int MaxChartPoints = 900;

    private readonly MonitoringEngine _engine;
    private readonly HistoryService _history;
    private readonly SettingsService _settings;
    private DateTimeOffset _lastLoad;
    private IReadOnlyList<SystemSample> _samples = [];
    private int _resolution = 1;
    private (DateTimeOffset From, DateTimeOffset To) _window;
    private bool _loading;
    private bool _reloadRequested;

    /// <summary>Creates the view model.</summary>
    public HistoryViewModel(MonitoringEngine engine, HistoryService history, SettingsService settings)
    {
        _engine = engine;
        _history = history;
        _settings = settings;
        Units = Fmt.Units;
        CustomFrom = DateTime.Now.AddHours(-6).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        CustomTo = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override string Title => "History";

    /// <inheritdoc />
    public override PageKind Kind => PageKind.History;

    /// <summary>Top applications in the range.</summary>
    public ObservableCollection<HistoryAppRow> Apps { get; } = [];

    /// <summary>Interfaces available for the per-interface chart.</summary>
    public ObservableCollection<InterfaceChoice> InterfaceChoices { get; } = [];

    /// <summary>Selected range.</summary>
    [ObservableProperty] public partial HistoryRange Range { get; set; } = HistoryRange.FiveMinutes;

    /// <summary>Custom range start (local time text).</summary>
    [ObservableProperty] public partial string CustomFrom { get; set; }

    /// <summary>Custom range end (local time text).</summary>
    [ObservableProperty] public partial string CustomTo { get; set; }

    /// <summary>Adapters vs applications.</summary>
    [ObservableProperty] public partial IReadOnlyList<ChartSeries> ThroughputSeries { get; set; } = [];

    /// <summary>TCP vs UDP (stacked).</summary>
    [ObservableProperty] public partial IReadOnlyList<ChartSeries> ProtocolSeries { get; set; } = [];

    /// <summary>Selected application.</summary>
    [ObservableProperty] public partial IReadOnlyList<ChartSeries> AppSeries { get; set; } = [];

    /// <summary>Selected interface.</summary>
    [ObservableProperty] public partial IReadOnlyList<ChartSeries> InterfaceSeries { get; set; } = [];

    /// <summary>Start of the system charts.</summary>
    [ObservableProperty] public partial DateTimeOffset ChartStart { get; set; }

    /// <summary>Step of the system charts.</summary>
    [ObservableProperty] public partial TimeSpan ChartStep { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Start of the per-minute charts.</summary>
    [ObservableProperty] public partial DateTimeOffset MinuteChartStart { get; set; }

    /// <summary>Step of the per-minute charts.</summary>
    [ObservableProperty] public partial TimeSpan MinuteChartStep { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Relative time labels for short ranges.</summary>
    [ObservableProperty] public partial bool RelativeTime { get; set; } = true;

    /// <summary>Range, resolution and interval count.</summary>
    [ObservableProperty] public partial string RangeSummary { get; set; } = string.Empty;

    /// <summary>Totals in the range.</summary>
    [ObservableProperty] public partial string TotalsText { get; set; } = string.Empty;

    /// <summary>Collector coverage in the range.</summary>
    [ObservableProperty] public partial string CoverageText { get; set; } = string.Empty;

    /// <summary>Information or problem to show above the charts.</summary>
    [ObservableProperty] public partial string? Notice { get; set; }

    /// <summary>Selected application row.</summary>
    [ObservableProperty] public partial HistoryAppRow? SelectedApp { get; set; }

    /// <summary>Selected interface.</summary>
    [ObservableProperty] public partial InterfaceChoice? SelectedInterface { get; set; }

    /// <summary>Result of the last action.</summary>
    [ObservableProperty] public partial string? ActionMessage { get; set; }

    /// <summary>Unit system for the charts.</summary>
    [ObservableProperty] public partial DataUnitSystem Units { get; set; }

    /// <summary>True while the per-application section has data.</summary>
    public bool HasApps => Apps.Count > 0;

    partial void OnRangeChanged(HistoryRange value) => _ = LoadAsync();

    partial void OnSelectedAppChanged(HistoryAppRow? value) => LoadAppSeries();

    partial void OnSelectedInterfaceChanged(InterfaceChoice? value) => LoadInterfaceSeries();

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private Task ApplyCustomAsync()
    {
        if (Range != HistoryRange.Custom)
        {
            Range = HistoryRange.Custom; // triggers the load
            return Task.CompletedTask;
        }

        return LoadAsync();
    }

    [RelayCommand]
    private void Export(string? format)
    {
        bool json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
        string? path = ShellActions.AskSavePath(
            $"traffic-history-{DateTime.Now:yyyyMMdd-HHmmss}.{(json ? "json" : "csv")}",
            json ? "JSON (*.json)|*.json" : "CSV (*.csv)|*.csv",
            _settings.Current.Export.LastFolder);
        if (path is null)
        {
            return;
        }

        var columns = new ExportColumn[]
        {
            new("interval_start_utc", "Start of the interval (UTC)"),
            new("interval_seconds", "Interval length in seconds"),
            new("adapter_received_bytes", "Octets received on physical adapters (link layer, headers included); empty = not sampled"),
            new("adapter_sent_bytes", "Octets sent on physical adapters"),
            new("tcp_received_bytes", "TCP payload received by applications (external); empty = not measured"),
            new("tcp_sent_bytes", "TCP payload sent by applications"),
            new("udp_received_bytes", "UDP payload received by applications"),
            new("udp_sent_bytes", "UDP payload sent by applications"),
            new("loopback_received_bytes", "Loopback payload received"),
            new("loopback_sent_bytes", "Loopback payload sent"),
            new("collector_running", "False when per-process values were not measured"),
            new("partial", "True when events were lost (values are lower bounds)"),
        };
        var rows = _samples.Select(s => (IReadOnlyList<object?>)
        [
            s.Start, s.ResolutionSeconds, s.PhysicalReceived, s.PhysicalSent, s.TcpReceived, s.TcpSent, s.UdpReceived, s.UdpSent,
            s.LoopbackReceived, s.LoopbackSent, !s.Flags.HasFlag(SampleFlags.NoCollector), s.Flags.HasFlag(SampleFlags.Partial),
        ]).ToList();
        var metadata = new Dictionary<string, string>(ExportMetadata.Create(_settings.Current, _engine.Latest, "History"))
        {
            ["range_from_utc"] = _window.From.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["range_to_utc"] = _window.To.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["resolution_seconds"] = _resolution.ToString(CultureInfo.InvariantCulture),
            ["source_history"] = "Aggregates recorded by WinNetworkManager while it was running (no remote addresses are stored)",
        };
        try
        {
            var table = new ExportTable("WinNetworkManager — traffic history", columns, rows, metadata);
            if (json)
            {
                TableExporter.WriteJson(path, table);
            }
            else
            {
                TableExporter.WriteCsv(path, table, _settings.Current.Export.CsvDelimiter);
            }

            string? folder = System.IO.Path.GetDirectoryName(path);
            _settings.Update(s => s with { Export = s.Export with { LastFolder = folder } });
            ActionMessage = $"Exported {rows.Count} intervals to {System.IO.Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActionMessage = "Export failed: " + ErrorText.Describe(ex);
        }
    }

    /// <inheritdoc />
    protected override void OnActivated() => _ = LoadAsync();

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        Units = Fmt.Units;
        foreach (var nic in snapshot.Interfaces.Where(i => i.IsHardware))
        {
            if (InterfaceChoices.All(c => c.Id != nic.Id))
            {
                InterfaceChoices.Add(new InterfaceChoice(nic.Id, nic.Name));
            }
        }

        SelectedInterface ??= InterfaceChoices.Count > 0 ? InterfaceChoices[0] : null;
        bool live = Range is HistoryRange.Minute or HistoryRange.FiveMinutes or HistoryRange.FifteenMinutes or HistoryRange.Hour;
        var every = TimeSpan.FromSeconds(Range == HistoryRange.Hour ? 15 : Range == HistoryRange.Minute ? 1 : 3);
        if (live && snapshot.Timestamp - _lastLoad >= every)
        {
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            _reloadRequested = true;
            return;
        }

        _loading = true;
        try
        {
            do
            {
                _reloadRequested = false;
                await LoadOnceAsync().ConfigureAwait(true);
            }
            while (_reloadRequested);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            Notice = "History could not be read: " + ErrorText.Describe(ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadOnceAsync()
    {
        _lastLoad = _engine.Latest.Timestamp;
        Notice = null;
        var now = DateTimeOffset.UtcNow;
        var (from, to) = Range switch
        {
            HistoryRange.Minute => (now.AddSeconds(-60), now),
            HistoryRange.FiveMinutes => (now.AddMinutes(-5), now),
            HistoryRange.FifteenMinutes => (now.AddMinutes(-15), now),
            HistoryRange.Hour => (now.AddHours(-1), now),
            HistoryRange.Day => (now.AddDays(-1), now),
            HistoryRange.Week => (now.AddDays(-7), now),
            HistoryRange.Month => (now.AddDays(-30), now),
            _ => ParseCustom(now),
        };
        _window = (from, to);
        var span = to - from;
        var memory = TimeSpan.FromMinutes(_settings.Current.History.InMemoryMinutes);
        var store = _history.Store;
        bool endsNow = to >= now.AddSeconds(-5);
        if (span <= memory && endsNow)
        {
            _resolution = 1;
            _samples = await _engine.GetRecentSystemSamplesAsync(span).ConfigureAwait(true);
        }
        else if (store is null)
        {
            // No database: show what the in-memory window still has and say why the rest is missing.
            _resolution = 1;
            _samples = endsNow ? await _engine.GetRecentSystemSamplesAsync(memory).ConfigureAwait(true) : [];
            Notice = _history.Error is { } error
                ? $"The history database could not be opened ({error}). Only the last {memory.TotalMinutes:0} minutes kept in memory are available."
                : $"Only the last {memory.TotalMinutes:0} minutes are kept in memory. Turn on “Keep traffic history on this PC” in Settings → History to keep older data.";
        }
        else
        {
            _resolution = span <= TimeSpan.FromHours(24) && from >= now.AddHours(-24).AddMinutes(-1) ? 1 : 60;
            int resolution = _resolution;
            _samples = await Task.Run(() => store.ReadSystem(from, to, resolution)).ConfigureAwait(true);
            if (_resolution == 1 && _samples.Count == 0)
            {
                _resolution = 60;
                _samples = await Task.Run(() => store.ReadSystem(from, to, 60)).ConfigureAwait(true);
            }
        }

        BuildCharts(from, to);
        await LoadAppsAsync(store, from, to).ConfigureAwait(true);
        LoadAppSeries();
        LoadInterfaceSeries();
    }

    private (DateTimeOffset, DateTimeOffset) ParseCustom(DateTimeOffset now)
    {
        if (TryParseLocal(CustomFrom, out var from) && TryParseLocal(CustomTo, out var to) && to > from)
        {
            return (from, to > now ? now : to);
        }

        Notice = "Enter a valid custom range, for example 2026-11-21 08:00 → 2026-11-21 12:00. Showing the last hour.";
        return (now.AddHours(-1), now);
    }

    private static bool TryParseLocal(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var local) ||
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out local))
        {
            value = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUniversalTime();
            return true;
        }

        return false;
    }

    private void BuildCharts(DateTimeOffset from, DateTimeOffset to)
    {
        long first = Floor(from.ToUnixTimeSeconds(), _resolution);
        int count = (int)Math.Clamp((to.ToUnixTimeSeconds() - first) / _resolution, 1, 200_000);
        var physRx = Gaps(count);
        var physTx = Gaps(count);
        var appRx = Gaps(count);
        var appTx = Gaps(count);
        var tcp = Gaps(count);
        var udp = Gaps(count);
        long totalPhysRx = 0, totalPhysTx = 0, totalAppRx = 0, totalAppTx = 0, totalTcp = 0, totalUdp = 0;
        int withCollector = 0, partial = 0;
        foreach (var s in _samples)
        {
            long index = (s.Start.ToUnixTimeSeconds() - first) / _resolution;
            if (index < 0 || index >= count)
            {
                continue;
            }

            double seconds = s.ResolutionSeconds;
            if (s.PhysicalReceived is { } prx)
            {
                long ptx = s.PhysicalSent ?? 0;
                physRx[index] = prx / seconds;
                physTx[index] = ptx / seconds;
                totalPhysRx += prx;
                totalPhysTx += ptx;
            }

            if (s.TcpReceived is { } trx)
            {
                long ttx = s.TcpSent ?? 0, urx = s.UdpReceived ?? 0, utx = s.UdpSent ?? 0;
                appRx[index] = (trx + urx) / seconds;
                appTx[index] = (ttx + utx) / seconds;
                tcp[index] = (trx + ttx) / seconds;
                udp[index] = (urx + utx) / seconds;
                totalAppRx += trx + urx;
                totalAppTx += ttx + utx;
                totalTcp += trx + ttx;
                totalUdp += urx + utx;
                withCollector++;
            }

            if (s.Flags.HasFlag(SampleFlags.Partial))
            {
                partial++;
            }
        }

        int group = Math.Max(1, (int)Math.Ceiling(count / (double)MaxChartPoints));
        ChartStart = DateTimeOffset.FromUnixTimeSeconds(first);
        ChartStep = TimeSpan.FromSeconds((double)_resolution * group);
        RelativeTime = Range is HistoryRange.Minute or HistoryRange.FiveMinutes or HistoryRange.FifteenMinutes;

        ThroughputSeries =
        [
            new ChartSeries { Name = "↓ Adapters", Values = Average(physRx, group), Stroke = Res("Brush.Data.Download"), Fill = Res("Brush.Data.DownloadFill") },
            new ChartSeries { Name = "↑ Adapters", Values = Average(physTx, group), Stroke = Res("Brush.Data.Upload"), Dashed = true },
            new ChartSeries { Name = "↓ Apps", Values = Average(appRx, group), Stroke = Res("Brush.Data.Tcp"), Thickness = 1.25 },
            new ChartSeries { Name = "↑ Apps", Values = Average(appTx, group), Stroke = Res("Brush.Data.Udp"), Thickness = 1.25, Dashed = true },
        ];
        ProtocolSeries =
        [
            new ChartSeries { Name = "TCP", Values = Average(tcp, group), Stroke = Res("Brush.Data.Tcp"), Fill = Translucent("Brush.Data.Tcp") },
            new ChartSeries { Name = "UDP", Values = Average(udp, group), Stroke = Res("Brush.Data.Udp"), Fill = Translucent("Brush.Data.Udp"), Dashed = true },
        ];

        string resolution = _resolution == 1 ? "1-second" : "1-minute";
        string averaged = group > 1 ? $", charts averaged over {Fmt.Duration(ChartStep)}" : string.Empty;
        RangeSummary = $"{from.ToLocalTime():g} → {to.ToLocalTime():g} · {resolution} data{averaged} · {_samples.Count:N0} recorded intervals";
        TotalsText = $"Adapters ↓ {Fmt.Bytes(totalPhysRx)}  ↑ {Fmt.Bytes(totalPhysTx)}     Apps ↓ {Fmt.Bytes(totalAppRx)}  ↑ {Fmt.Bytes(totalAppTx)}     TCP {Fmt.Bytes(totalTcp)} · UDP {Fmt.Bytes(totalUdp)}";
        if (_samples.Count == 0)
        {
            CoverageText = "Nothing was recorded in this range (WinNetworkManager was not running, or history was turned off).";
        }
        else
        {
            string coverage = withCollector == _samples.Count
                ? "Per-application traffic was measured for the whole recorded range."
                : $"Per-application traffic was measured for {withCollector * 100.0 / _samples.Count:0.#} % of the recorded intervals; the rest are gaps, not zero.";
            string lost = partial == 1 ? "1 interval lost events (its values are lower bounds)." : $"{partial:N0} intervals lost events (their values are lower bounds).";
            CoverageText = partial > 0 ? coverage + " " + lost : coverage;
        }
    }

    private async Task LoadAppsAsync(SqliteHistoryStore? store, DateTimeOffset from, DateTimeOffset to)
    {
        string? selected = SelectedApp?.AppKey;
        Apps.Clear();
        if (store is not null)
        {
            var top = await Task.Run(() => store.ReadTopApps(from, to, 50)).ConfigureAwait(true);
            foreach (var app in top)
            {
                Apps.Add(new HistoryAppRow(app.AppKey, app.Name, app.Path, app.Received, app.Sent, app.TcpBytes, app.UdpBytes));
            }

            if (Apps.Count == 0 && to - from <= TimeSpan.FromMinutes(5))
            {
                Notice ??= "Per-application history is stored per minute: it appears about a minute after the traffic.";
            }
        }

        SelectedApp = Apps.FirstOrDefault(a => a.AppKey == selected) ?? Apps.FirstOrDefault();
        OnPropertyChanged(nameof(HasApps));
    }

    private void LoadAppSeries()
    {
        var store = _history.Store;
        if (store is null || SelectedApp is not { } app)
        {
            AppSeries = [];
            return;
        }

        var (from, to) = _window;
        var (start, count) = MinuteGrid(from, to);
        var rx = new double[count];
        var tx = new double[count];
        foreach (var s in store.ReadApp(app.AppKey, from, to))
        {
            long i = (s.Start.ToUnixTimeSeconds() - start) / 60;
            if (i >= 0 && i < count)
            {
                rx[i] = (s.TcpReceived + s.UdpReceived) / 60.0;
                tx[i] = (s.TcpSent + s.UdpSent) / 60.0;
            }
        }

        int group = Math.Max(1, (int)Math.Ceiling(count / (double)MaxChartPoints));
        MinuteChartStart = DateTimeOffset.FromUnixTimeSeconds(start);
        MinuteChartStep = TimeSpan.FromMinutes(group);
        AppSeries =
        [
            new ChartSeries { Name = $"↓ {app.Name}", Values = Average(rx, group), Stroke = Res("Brush.Data.Download"), Fill = Res("Brush.Data.DownloadFill") },
            new ChartSeries { Name = $"↑ {app.Name}", Values = Average(tx, group), Stroke = Res("Brush.Data.Upload"), Dashed = true },
        ];
    }

    private void LoadInterfaceSeries()
    {
        var store = _history.Store;
        if (store is null || SelectedInterface is not { } nic)
        {
            InterfaceSeries = [];
            return;
        }

        var (from, to) = _window;
        var (start, count) = MinuteGrid(from, to);
        var rx = Gaps(count);
        var tx = Gaps(count);
        foreach (var s in store.ReadInterfaces(from, to))
        {
            if (s.InterfaceId != nic.Id)
            {
                continue;
            }

            long i = (s.Start.ToUnixTimeSeconds() - start) / 60;
            if (i >= 0 && i < count)
            {
                rx[i] = s.Received / 60.0;
                tx[i] = s.Sent / 60.0;
            }
        }

        int group = Math.Max(1, (int)Math.Ceiling(count / (double)MaxChartPoints));
        MinuteChartStart = DateTimeOffset.FromUnixTimeSeconds(start);
        MinuteChartStep = TimeSpan.FromMinutes(group);
        InterfaceSeries =
        [
            new ChartSeries { Name = "↓ Received", Values = Average(rx, group), Stroke = Res("Brush.Data.Download"), Fill = Res("Brush.Data.DownloadFill") },
            new ChartSeries { Name = "↑ Sent", Values = Average(tx, group), Stroke = Res("Brush.Data.Upload"), Dashed = true },
        ];
    }

    private static (long Start, int Count) MinuteGrid(DateTimeOffset from, DateTimeOffset to)
    {
        long start = Floor(from.ToUnixTimeSeconds(), 60);
        int count = (int)Math.Clamp((to.ToUnixTimeSeconds() - start + 59) / 60, 1, 50_000);
        return (start, count);
    }

    private static long Floor(long value, int step) => value - (((value % step) + step) % step);

    /// <summary>Averages groups of <paramref name="group"/> values, ignoring gaps (NaN); a group of gaps stays a gap.</summary>
    internal static double[] Average(double[] values, int group)
    {
        if (group <= 1)
        {
            return values;
        }

        var result = new double[(values.Length + group - 1) / group];
        for (int o = 0; o < result.Length; o++)
        {
            double sum = 0;
            int n = 0;
            int end = Math.Min(values.Length, (o + 1) * group);
            for (int i = o * group; i < end; i++)
            {
                if (!double.IsNaN(values[i]))
                {
                    sum += values[i];
                    n++;
                }
            }

            result[o] = n == 0 ? double.NaN : sum / n;
        }

        return result;
    }

    private static double[] Gaps(int count)
    {
        var values = new double[count];
        Array.Fill(values, double.NaN);
        return values;
    }

    private static Brush? Res(string key) => Application.Current?.TryFindResource(key) as Brush;

    private static Brush? Translucent(string key) =>
        Application.Current?.TryFindResource(key) is SolidColorBrush b ? Frozen(new SolidColorBrush(Color.FromArgb(0x40, b.Color.R, b.Color.G, b.Color.B))) : null;

    private static SolidColorBrush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
