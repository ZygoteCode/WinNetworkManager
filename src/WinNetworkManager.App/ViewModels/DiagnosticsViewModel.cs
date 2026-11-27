using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNetworkManager.App.Services;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Diagnostics;
using WinNetworkManager.Engine;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Platform.Windows.Interop;
using WinNetworkManager.Platform.Windows.IpHelper;
using WinNetworkManager.UI.Controls;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.App.ViewModels;

/// <summary>Diagnostic tools, in tab order.</summary>
public enum DiagnosticTool
{
    /// <summary>ICMP echo.</summary>
    Ping,

    /// <summary>Route tracing.</summary>
    Traceroute,

    /// <summary>TCP port test.</summary>
    Tcp,

    /// <summary>DNS lookups.</summary>
    Dns,

    /// <summary>HTTP and TLS.</summary>
    Http,

    /// <summary>Path MTU.</summary>
    PathMtu,

    /// <summary>Routing table and route lookup.</summary>
    Routes,

    /// <summary>DNS client cache.</summary>
    DnsCache,

    /// <summary>File checksum calculator and checker.</summary>
    FileHash,
}

/// <summary>Computed checksum row.</summary>
/// <param name="Value">Digest.</param>
/// <param name="IsMatch">True when this digest equals the expected value.</param>
public sealed record HashRow(FileHashValue Value, bool IsMatch)
{
    /// <summary>Algorithm name.</summary>
    public string Name => Value.Name;

    /// <summary>Digest (lowercase hex).</summary>
    public string Hex => Value.Hex;

    /// <summary>Note shown next to weak algorithms.</summary>
    public string? Note => Value.IsWeak ? "Weak: only for old published checksums" : null;
}

/// <summary>How a DNS lookup is performed.</summary>
public enum DnsLookupMode
{
    /// <summary>Through the Windows resolver, like applications.</summary>
    System,

    /// <summary>Direct query to one DNS server.</summary>
    Server,
}

/// <summary>Ping result row.</summary>
public sealed record PingRow(PingReplyInfo Reply)
{
    /// <summary>Local time.</summary>
    public string Time => Reply.Timestamp.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
}

/// <summary>HTTP probe step with formatted fields.</summary>
public sealed record HttpStepView(HttpProbeStep Step)
{
    /// <summary>URL.</summary>
    public string Url => Step.Url.ToString();

    /// <summary>Status ("200 OK" or the failure).</summary>
    public string Status => Step.StatusCode is { } code ? $"{code} {Step.ReasonPhrase}" : "Failed at " + Step.FailedStage;

    /// <summary>Server endpoint.</summary>
    public string Remote => Step.Remote?.ToString() ?? "—";

    /// <summary>DNS time.</summary>
    public string Dns => DiagnosticsViewModel.Ms(Step.DnsMs, "cached / literal");

    /// <summary>TCP connect time.</summary>
    public string Connect => DiagnosticsViewModel.Ms(Step.ConnectMs);

    /// <summary>TLS time.</summary>
    public string Tls => Step.Url.Scheme == "https" ? DiagnosticsViewModel.Ms(Step.TlsMs) : "not used";

    /// <summary>Server wait time.</summary>
    public string Wait => DiagnosticsViewModel.Ms(Step.WaitMs);

    /// <summary>Total.</summary>
    public string Total => DiagnosticsViewModel.Ms(Step.TotalMs);

    /// <summary>TLS summary.</summary>
    public string TlsSummary => Step.TlsProtocol is null
        ? (Step.Url.Scheme == "https" ? "No TLS session was established." : "Plain HTTP: the connection is not encrypted.")
        : $"{Step.TlsProtocolText} · {Step.CipherSuite} · ALPN {Step.ApplicationProtocol ?? "none"}";

    /// <summary>Certificate validity text.</summary>
    public string? CertificateValidity
    {
        get
        {
            if (Step.Certificate is not { } c)
            {
                return null;
            }

            var now = DateTimeOffset.Now;
            string range = $"{c.NotBefore.LocalDateTime:d} → {c.NotAfter.LocalDateTime:d}";
            if (now > c.NotAfter)
            {
                return $"{range} · expired {(now - c.NotAfter).TotalDays:0} days ago";
            }

            return now < c.NotBefore ? $"{range} · not yet valid" : $"{range} · expires in {(c.NotAfter - now).TotalDays:0} days";
        }
    }

    /// <summary>Key text.</summary>
    public string? CertificateKey => Step.Certificate is { } c ? $"{c.KeyAlgorithm} {c.KeySize?.ToString(CultureInfo.CurrentCulture) ?? "?"} bit · signed with {c.SignatureAlgorithm}" : null;

    /// <summary>Alternative names, one per line.</summary>
    public string? CertificateNames => Step.Certificate is { } c ? string.Join(Environment.NewLine, c.SubjectAlternativeNames) : null;

    /// <summary>Chain, one per line.</summary>
    public string? CertificateChain => Step.Certificate is { } c ? string.Join(Environment.NewLine, c.Chain.Select((s, i) => new string(' ', i * 2) + s)) : null;

    /// <summary>Chain problems, one per line.</summary>
    public string? CertificateChainStatus => Step.Certificate is { ChainStatus.Count: > 0 } c ? string.Join(Environment.NewLine, c.ChainStatus) : null;
}

/// <summary>DNS record row (TTL unknown for system-resolver results).</summary>
public sealed record DnsRecordRow(string Section, string Name, string Type, string Ttl, string Data)
{
    /// <summary>Row for a record of a direct query.</summary>
    public static DnsRecordRow From(DnsAnswer answer) => new(answer.Section.ToString(), answer.Name, answer.TypeText, answer.TimeToLiveText, answer.Data);
}

/// <summary>Routing table row.</summary>
public sealed record RouteView(string Family, string Prefix, string NextHop, string Interface, string RouteMetric, string InterfaceMetric, string EffectiveMetric, string Origin, bool IsDefault, uint SortMetric);

/// <summary>
/// Diagnostics page: ping, traceroute, TCP port test, DNS (system resolver or a chosen server), HTTP/TLS, path MTU,
/// routing table and DNS cache. Every tool acts on one destination typed or chosen by the user, only when the user
/// starts it, and can be stopped at any time.
/// </summary>
public sealed partial class DiagnosticsViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly MonitoringEngine _engine;
    private readonly List<DnsCacheEntry> _cache = [];
    private DateTimeOffset _pingStart;

    /// <summary>Creates the view model.</summary>
    public DiagnosticsViewModel(SettingsService settings, MonitoringEngine engine)
    {
        _settings = settings;
        _engine = engine;
        var d = settings.Current.Diagnostics;
        PingCount = d.PingCount;
        TracerouteMaxHops = d.TracerouteMaxHops;
        HttpFollowRedirects = d.HttpFollowRedirects;
    }

    /// <inheritdoc />
    public override string Title => "Diagnostics";

    /// <inheritdoc />
    public override PageKind Kind => PageKind.Diagnostics;

    /// <summary>Selected tool.</summary>
    [ObservableProperty] public partial DiagnosticTool Tool { get; set; }

    /// <summary>Tool as tab index.</summary>
    public int ToolIndex
    {
        get => (int)Tool;
        set => Tool = (DiagnosticTool)Math.Clamp(value, 0, (int)DiagnosticTool.FileHash);
    }

    /// <summary>Destination for ping, traceroute, TCP and path MTU.</summary>
    [ObservableProperty] public partial string Target { get; set; } = string.Empty;

    /// <summary>Address family preference.</summary>
    [ObservableProperty] public partial AddressPreference Family { get; set; }

    /// <summary>Family choices.</summary>
    public IReadOnlyList<Choice<AddressPreference>> Families { get; } =
        [new(AddressPreference.System, "IPv4 or IPv6"), new(AddressPreference.IPv4, "IPv4 only"), new(AddressPreference.IPv6, "IPv6 only")];

    // ── Ping ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Requests to send.</summary>
    [ObservableProperty] public partial int PingCount { get; set; }

    /// <summary>Ping until stopped.</summary>
    [ObservableProperty] public partial bool PingContinuous { get; set; }

    /// <summary>Payload size.</summary>
    [ObservableProperty] public partial int PingPayloadSize { get; set; } = 32;

    /// <summary>Don't-fragment flag (IPv4).</summary>
    [ObservableProperty] public partial bool PingDontFragment { get; set; }

    /// <summary>Replies.</summary>
    public ObservableCollection<PingRow> PingReplies { get; } = [];

    /// <summary>Summary line.</summary>
    [ObservableProperty] public partial string? PingSummaryText { get; set; }

    /// <summary>Round-trip chart.</summary>
    [ObservableProperty] public partial IReadOnlyList<ChartSeries> PingSeries { get; set; } = [];

    /// <summary>Chart start.</summary>
    [ObservableProperty] public partial DateTimeOffset PingChartStart { get; set; }

    /// <summary>Chart step.</summary>
    [ObservableProperty] public partial TimeSpan PingChartStep { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Status or error.</summary>
    [ObservableProperty] public partial string? PingMessage { get; set; }

    // ── Traceroute ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Maximum hops.</summary>
    [ObservableProperty] public partial int TracerouteMaxHops { get; set; }

    /// <summary>Resolve hop names.</summary>
    [ObservableProperty] public partial bool TracerouteResolveNames { get; set; }

    /// <summary>Hops.</summary>
    public ObservableCollection<TraceHop> Hops { get; } = [];

    /// <summary>Status or error.</summary>
    [ObservableProperty] public partial string? TracerouteMessage { get; set; }

    // ── TCP ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Port to test.</summary>
    [ObservableProperty] public partial int TcpPort { get; set; } = 443;

    /// <summary>Test every resolved address instead of the first.</summary>
    [ObservableProperty] public partial bool TcpAllAddresses { get; set; } = true;

    /// <summary>Results.</summary>
    public ObservableCollection<TcpProbeResult> TcpResults { get; } = [];

    /// <summary>Status or error.</summary>
    [ObservableProperty] public partial string? TcpMessage { get; set; }

    // ── DNS ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Name (or address for reverse lookups).</summary>
    [ObservableProperty] public partial string DnsName { get; set; } = string.Empty;

    /// <summary>Record type for direct queries.</summary>
    [ObservableProperty] public partial DnsRecordType DnsType { get; set; } = DnsRecordType.A;

    /// <summary>Lookup mode.</summary>
    [ObservableProperty] public partial DnsLookupMode DnsMode { get; set; }

    /// <summary>Server address text for direct queries.</summary>
    [ObservableProperty] public partial string DnsServer { get; set; } = string.Empty;

    /// <summary>Record types offered.</summary>
    public IReadOnlyList<DnsRecordType> DnsTypes { get; } =
        [DnsRecordType.A, DnsRecordType.AAAA, DnsRecordType.CNAME, DnsRecordType.MX, DnsRecordType.TXT, DnsRecordType.NS, DnsRecordType.SOA, DnsRecordType.SRV, DnsRecordType.CAA, DnsRecordType.PTR];

    /// <summary>Configured servers.</summary>
    public ObservableCollection<DnsServerInfo> DnsServers { get; } = [];

    /// <summary>Records of the last answer.</summary>
    public ObservableCollection<DnsRecordRow> DnsRecords { get; } = [];

    /// <summary>Summary of the last answer.</summary>
    [ObservableProperty] public partial string? DnsSummary { get; set; }

    /// <summary>Status or error.</summary>
    [ObservableProperty] public partial string? DnsMessage { get; set; }

    // ── HTTP ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>URL.</summary>
    [ObservableProperty] public partial string HttpUrl { get; set; } = "https://";

    /// <summary>Method.</summary>
    [ObservableProperty] public partial string HttpMethod { get; set; } = "HEAD";

    /// <summary>Methods offered.</summary>
    public IReadOnlyList<string> HttpMethods { get; } = ["HEAD", "GET"];

    /// <summary>Follow redirects.</summary>
    [ObservableProperty] public partial bool HttpFollowRedirects { get; set; }

    /// <summary>Online revocation check.</summary>
    [ObservableProperty] public partial bool HttpCheckRevocation { get; set; }

    /// <summary>Requests made.</summary>
    public ObservableCollection<HttpStepView> HttpSteps { get; } = [];

    /// <summary>Selected request.</summary>
    [ObservableProperty] public partial HttpStepView? SelectedHttpStep { get; set; }

    /// <summary>Status or error.</summary>
    [ObservableProperty] public partial string? HttpMessage { get; set; }

    // ── Path MTU ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Result text.</summary>
    [ObservableProperty] public partial string? MtuMessage { get; set; }

    // ── Routes ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Routing table.</summary>
    public ObservableCollection<RouteView> Routes { get; } = [];

    /// <summary>Destination for a route lookup.</summary>
    [ObservableProperty] public partial string RouteTarget { get; set; } = string.Empty;

    /// <summary>Lookup result or error.</summary>
    [ObservableProperty] public partial string? RouteMessage { get; set; }

    // ── DNS cache ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Cache entries matching the filter.</summary>
    public ObservableCollection<DnsCacheEntry> CacheEntries { get; } = [];

    /// <summary>Filter text.</summary>
    [ObservableProperty] public partial string CacheFilter { get; set; } = string.Empty;

    /// <summary>Status or error.</summary>
    [ObservableProperty] public partial string? CacheMessage { get; set; }

    // ── File hash ─────────────────────────────────────────────────────────────────────────────────

    private FileHashResult? _hashResult;

    /// <summary>File to hash.</summary>
    [ObservableProperty] public partial string HashPath { get; set; } = string.Empty;

    /// <summary>Expected checksum pasted by the user (any supported algorithm, detected from the length).</summary>
    [ObservableProperty] public partial string HashExpected { get; set; } = string.Empty;

    /// <summary>Computed digests.</summary>
    public ObservableCollection<HashRow> HashRows { get; } = [];

    /// <summary>Progress, 0 to 1.</summary>
    [ObservableProperty] public partial double HashProgress { get; set; }

    /// <summary>File size and timing of the last computation.</summary>
    [ObservableProperty] public partial string? HashSummary { get; set; }

    /// <summary>Status or error.</summary>
    [ObservableProperty] public partial string? HashMessage { get; set; }

    /// <summary>Outcome of the comparison with <see cref="HashExpected"/>.</summary>
    [ObservableProperty] public partial HashMatchKind HashVerdict { get; set; }

    /// <summary>Explanation of <see cref="HashVerdict"/>; null when there is nothing to say.</summary>
    [ObservableProperty] public partial string? HashVerdictText { get; set; }

    private TimeSpan Timeout => TimeSpan.FromMilliseconds(Math.Clamp(_settings.Current.Diagnostics.TimeoutMs, 200, 60_000));

    /// <summary>Opens a tool for a destination chosen elsewhere (e.g. a connection's context menu) and starts it.</summary>
    public void Prefill(string tool, string target, ushort port)
    {
        ArgumentNullException.ThrowIfNull(tool);
        switch (tool)
        {
            case "traceroute":
                Tool = DiagnosticTool.Traceroute;
                Target = target;
                StartIfIdle(RunTracerouteCommand);
                break;
            case "tcp":
                Tool = DiagnosticTool.Tcp;
                Target = target;
                TcpPort = port == 0 ? TcpPort : port;
                StartIfIdle(RunTcpCommand);
                break;
            case "hash":
                Tool = DiagnosticTool.FileHash;
                HashPath = target;
                HashExpected = string.Empty; // a value left from another file would report a false mismatch
                StartIfIdle(RunHashCommand);
                break;
            case "dns":
                Tool = DiagnosticTool.Dns;
                DnsName = target;
                DnsType = DnsRecordType.PTR;
                DnsMode = DnsLookupMode.System;
                StartIfIdle(RunDnsCommand);
                break;
            default:
                Tool = DiagnosticTool.Ping;
                Target = target;
                StartIfIdle(RunPingCommand);
                break;
        }
    }

    /// <summary>Formats milliseconds.</summary>
    internal static string Ms(double? value, string missing = "—") =>
        value is { } v ? v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms" : missing;

    partial void OnToolChanged(DiagnosticTool value)
    {
        OnPropertyChanged(nameof(ToolIndex));
        if (value == DiagnosticTool.Routes && Routes.Count == 0)
        {
            LoadRoutes();
        }

        if (value == DiagnosticTool.Dns && DnsServers.Count == 0)
        {
            LoadDnsServers();
        }
    }

    partial void OnCacheFilterChanged(string value) => ApplyCacheFilter();

    partial void OnHashExpectedChanged(string value) => UpdateHashVerdict();

    partial void OnHashPathChanged(string value)
    {
        if (_hashResult is not null && !string.Equals(_hashResult.Path, value.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase))
        {
            _hashResult = null;
            HashRows.Clear();
            HashSummary = null;
            HashProgress = 0;
            UpdateHashVerdict();
        }
    }

    /// <summary>Uses a file dropped on the page and computes its checksums.</summary>
    public void HashDroppedFile(string path)
    {
        Tool = DiagnosticTool.FileHash;
        HashPath = path;
        StartIfIdle(RunHashCommand);
    }

    [RelayCommand]
    private void BrowseHashFile()
    {
        if (ShellActions.AskOpenPath("Choose a file to hash", HashPath) is { } path)
        {
            HashPath = path;
            StartIfIdle(RunHashCommand);
        }
    }

    [RelayCommand]
    private static void CopyHash(HashRow? row) => ShellActions.Copy(row?.Hex);

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunHashAsync(CancellationToken token)
    {
        _hashResult = null;
        HashRows.Clear();
        HashSummary = null;
        HashProgress = 0;
        HashMessage = null;
        UpdateHashVerdict();
        if (string.IsNullOrWhiteSpace(HashPath))
        {
            HashMessage = "Choose a file first: type its path, browse for it or drop it on this page.";
            return;
        }

        try
        {
            var progress = new Progress<double>(p => HashProgress = p);
            var result = await Task.Run(() => FileHashTool.ComputeAsync(HashPath, FileHashTool.All, progress, token), token).ConfigureAwait(true);
            _hashResult = result;
            HashProgress = 1;
            HashSummary = $"{Fmt.Bytes(result.Length)} ({result.Length.ToString("N0", CultureInfo.CurrentCulture)} bytes) · read in {Ms(result.ElapsedMs)}";
            UpdateHashVerdict();
        }
        catch (OperationCanceledException)
        {
            HashMessage = "Stopped.";
        }
        catch (DiagnosticsException ex)
        {
            HashMessage = ex.Message;
        }
    }

    private void UpdateHashVerdict()
    {
        var computed = _hashResult?.Hashes ?? [];
        var match = FileHashTool.Compare(HashExpected, computed);
        HashVerdict = match.Kind;
        string name = match.Algorithm is { } a ? FileHashTool.DisplayName(a) : string.Empty;
        HashVerdictText = match.Kind switch
        {
            HashMatchKind.Invalid => "The expected value is not a SHA-256, SHA-512, SHA-1 or MD5 checksum (hexadecimal digits only).",
            HashMatchKind.NotComputed when _hashResult is null => $"{name} checksum recognised. Compute the file's checksums to compare.",
            HashMatchKind.NotComputed => $"The {name} checksum was not computed.",
            HashMatchKind.Match => $"Match: the file's {name} checksum equals the expected value." +
                (match.Algorithm is FileHashAlgorithm.Sha1 or FileHashAlgorithm.Md5 ? $" {name} only detects accidental corruption, not deliberate tampering." : string.Empty),
            HashMatchKind.Mismatch => $"Mismatch: the file's {name} checksum differs from the expected value. The file is not the published one (corrupted, incomplete or modified).",
            _ => null,
        };

        HashRows.Clear();
        foreach (var value in computed)
        {
            HashRows.Add(new HashRow(value, match.Kind == HashMatchKind.Match && match.Algorithm == value.Algorithm));
        }
    }

    /// <inheritdoc />
    protected override void OnActivated()
    {
        if (DnsServers.Count == 0)
        {
            LoadDnsServers();
        }
    }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunPingAsync(CancellationToken token)
    {
        PingReplies.Clear();
        PingSeries = [];
        PingSummaryText = null;
        PingMessage = null;
        try
        {
            var target = await DiagnosticTarget.ResolveAsync(Target, Family, token).ConfigureAwait(true);
            var address = target.Addresses[0];
            var d = _settings.Current.Diagnostics;
            var settings = new PingSettings
            {
                Count = PingContinuous ? 0 : Math.Clamp(PingCount, 1, 1000),
                Interval = TimeSpan.FromMilliseconds(Math.Clamp(d.PingIntervalMs, 100, 60_000)),
                Timeout = Timeout,
                PayloadSize = Math.Clamp(PingPayloadSize, 0, 65500),
                DontFragment = PingDontFragment,
            };
            PingMessage = Describe(target, address) + $" with {settings.PayloadSize} bytes of data";
            _pingStart = DateTimeOffset.UtcNow;
            PingChartStart = _pingStart;
            PingChartStep = settings.Interval;
            await foreach (var reply in PingTool.RunAsync(address, settings, token).ConfigureAwait(true))
            {
                PingReplies.Add(new PingRow(reply));
                if (PingReplies.Count > 5000)
                {
                    PingReplies.RemoveAt(0);
                }

                UpdatePingSummary();
            }
        }
        catch (OperationCanceledException)
        {
            PingMessage = "Stopped.";
        }
        catch (DiagnosticsException ex)
        {
            PingMessage = ex.Message;
        }

        UpdatePingSummary();
    }

    private void UpdatePingSummary()
    {
        var replies = PingReplies.Select(r => r.Reply).ToList();
        var s = PingTool.Summarize(replies);
        if (s.Sent == 0)
        {
            return;
        }

        string rtt = s.Received == 0 ? "no replies"
            : string.Create(CultureInfo.CurrentCulture, $"min {s.MinimumMs} ms · avg {s.AverageMs:0.0} ms · max {s.MaximumMs} ms") +
              (s.JitterMs is { } j ? string.Create(CultureInfo.CurrentCulture, $" · jitter {j:0.0} ms") : string.Empty);
        PingSummaryText = string.Create(CultureInfo.CurrentCulture, $"Sent {s.Sent} · received {s.Received} · lost {s.Sent - s.Received} ({s.LossPercent:0.#} %) · {rtt}");
        PingSeries =
        [
            new ChartSeries
            {
                Name = "Round trip",
                Values = replies.Select(r => r.RoundTripMs is { } v ? (double)v : double.NaN).ToArray(),
                Stroke = Application.Current?.TryFindResource("Brush.Data.Tcp") as Brush,
            },
        ];
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunTracerouteAsync(CancellationToken token)
    {
        Hops.Clear();
        TracerouteMessage = null;
        try
        {
            var target = await DiagnosticTarget.ResolveAsync(Target, Family, token).ConfigureAwait(true);
            var address = target.Addresses[0];
            TracerouteMessage = Describe(target, address) + $", at most {Math.Clamp(TracerouteMaxHops, 1, 64)} hops";
            var settings = new TracerouteSettings
            {
                MaxHops = Math.Clamp(TracerouteMaxHops, 1, 64),
                Timeout = Timeout,
                ResolveNames = TracerouteResolveNames,
            };
            await foreach (var hop in TracerouteTool.RunAsync(address, settings, token).ConfigureAwait(true))
            {
                Hops.Add(hop);
            }

            var last = Hops.LastOrDefault();
            TracerouteMessage = last is { IsDestination: true }
                ? $"Destination {address} reached in {last.Hop} hops. Round trips are measured by WinNetworkManager around each probe."
                : $"The destination did not answer within {settings.MaxHops} hops. Routers or the destination may drop ICMP; that does not mean other traffic is blocked.";
        }
        catch (OperationCanceledException)
        {
            TracerouteMessage = "Stopped.";
        }
        catch (DiagnosticsException ex)
        {
            TracerouteMessage = ex.Message;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunTcpAsync(CancellationToken token)
    {
        TcpResults.Clear();
        TcpMessage = null;
        if (TcpPort is < 1 or > 65535)
        {
            TcpMessage = "Enter a port between 1 and 65535.";
            return;
        }

        try
        {
            var target = await DiagnosticTarget.ResolveAsync(Target, Family, token).ConfigureAwait(true);
            var addresses = TcpAllAddresses ? target.Addresses.Take(8).ToList() : [target.Addresses[0]];
            TcpMessage = $"Testing {target.Host} port {TcpPort} on {addresses.Count} address(es)…";
            foreach (var address in addresses)
            {
                TcpResults.Add(await TcpConnectTool.ProbeAsync(address, TcpPort, Timeout, token).ConfigureAwait(true));
            }

            int ok = TcpResults.Count(r => r.Outcome == TcpProbeOutcome.Connected);
            TcpMessage = ok == TcpResults.Count
                ? $"Port {TcpPort} accepts connections on every address tested."
                : ok == 0 ? $"Port {TcpPort} did not accept a connection on any address tested." : $"Port {TcpPort} accepts connections on {ok} of {TcpResults.Count} addresses.";
        }
        catch (OperationCanceledException)
        {
            TcpMessage = "Stopped.";
        }
        catch (DiagnosticsException ex)
        {
            TcpMessage = ex.Message;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunDnsAsync(CancellationToken token)
    {
        DnsRecords.Clear();
        DnsSummary = null;
        DnsMessage = null;
        string name = DnsName.Trim();
        if (name.Length == 0)
        {
            DnsMessage = "Enter a name (or an IP address for a reverse lookup).";
            return;
        }

        try
        {
            bool isAddress = IPAddress.TryParse(name.Trim('[', ']'), out var address);
            if (DnsMode == DnsLookupMode.System)
            {
                if (isAddress)
                {
                    var (host, ms) = await DnsTool.ReverseWithSystemAsync(address!, token).ConfigureAwait(true);
                    DnsSummary = host is null
                        ? $"No name is registered for {address} (no PTR record). {Ms(ms)}."
                        : $"{address} → {host}   ({Ms(ms)})";
                    if (host is not null)
                    {
                        DnsRecords.Add(new DnsRecordRow("Answer", DnsTool.ReverseName(address!), "PTR", "—", host));
                    }

                    return;
                }

                var result = await DnsTool.ResolveWithSystemAsync(name, token).ConfigureAwait(true);
                foreach (var a in result.Addresses)
                {
                    DnsRecords.Add(new DnsRecordRow("Answer", result.Name, a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "A" : "AAAA", "—", a.ToString()));
                }

                DnsSummary = result.Error ?? $"{result.Addresses.Count} address(es) in {Ms(result.ElapsedMs)} from the Windows resolver (hosts file, cache, configured DNS servers). TTLs are not exposed by this API.";
                return;
            }

            if (!IPAddress.TryParse(DnsServer.Trim().Trim('[', ']'), out var server))
            {
                DnsMessage = "Enter the IP address of the DNS server to query (or pick a configured one).";
                return;
            }

            string queryName = isAddress ? DnsTool.ReverseName(address!) : name;
            var type = isAddress ? DnsRecordType.PTR : DnsType;
            var answer = await DnsTool.QueryAsync(queryName, type, server, Timeout, token).ConfigureAwait(true);
            foreach (var record in answer.Records)
            {
                DnsRecords.Add(DnsRecordRow.From(record));
            }

            var flags = new List<string>();
            if (answer.Authoritative)
            {
                flags.Add("authoritative");
            }

            if (answer.RecursionAvailable)
            {
                flags.Add("recursion available");
            }

            if (answer.AuthenticData)
            {
                flags.Add("DNSSEC-validated by the server (AD)");
            }

            int answers = answer.Answers.Count();
            string empty = answer.ResponseCode == DnsResponseCode.NoError && answers == 0 ? " The name exists but has no records of this type." : string.Empty;
            DnsSummary = $"{answer.ResponseCodeText}.{empty} {answers} answer(s) from {answer.Server} over {answer.Transport} in {Ms(answer.ElapsedMs)}, {answer.ResponseBytes} bytes" +
                         (flags.Count > 0 ? " · " + string.Join(", ", flags) : string.Empty) + ".";
        }
        catch (OperationCanceledException)
        {
            DnsMessage = "Stopped.";
        }
        catch (DiagnosticsException ex)
        {
            DnsMessage = ex.Message;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunHttpAsync(CancellationToken token)
    {
        HttpSteps.Clear();
        SelectedHttpStep = null;
        HttpMessage = null;
        string text = HttpUrl.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host))
        {
            HttpMessage = "Enter an http:// or https:// address, for example https://example.com/.";
            return;
        }

        HttpUrl = url.ToString();
        try
        {
            var result = await HttpProbeTool.ProbeAsync(new HttpProbeSettings
            {
                Url = url,
                Method = HttpMethod,
                Timeout = TimeSpan.FromMilliseconds(Math.Max(Timeout.TotalMilliseconds, 10_000)),
                FollowRedirects = HttpFollowRedirects,
                CheckRevocation = HttpCheckRevocation,
            }, token).ConfigureAwait(true);
            foreach (var step in result.Steps)
            {
                HttpSteps.Add(new HttpStepView(step));
            }

            SelectedHttpStep = HttpSteps[^1];
            var final = result.Final;
            HttpMessage = final.Succeeded
                ? $"{final.StatusCode} {final.ReasonPhrase} in {Ms(final.TotalMs)}" + (result.Steps.Count > 1 ? $" after {result.Steps.Count - 1} redirect(s)." : ".") + " Only the response header was read."
                : final.Error;
        }
        catch (OperationCanceledException)
        {
            HttpMessage = "Stopped.";
        }
        catch (DiagnosticsException ex)
        {
            HttpMessage = ex.Message;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunMtuAsync(CancellationToken token)
    {
        MtuMessage = null;
        try
        {
            var target = await DiagnosticTarget.ResolveAsync(Target, AddressPreference.IPv4, token).ConfigureAwait(true);
            var address = target.Addresses[0];
            MtuMessage = $"Measuring the path to {address}…";
            var result = await PathMtuTool.DiscoverAsync(address, Timeout, token).ConfigureAwait(true);
            MtuMessage = result.Message + $" ({result.Probes} echo requests.)";
        }
        catch (OperationCanceledException)
        {
            MtuMessage = "Stopped.";
        }
        catch (DiagnosticsException ex)
        {
            MtuMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void LoadRoutes()
    {
        Routes.Clear();
        try
        {
            var names = new Dictionary<uint, string>();
            foreach (var nic in _engine.Latest.Interfaces)
            {
                names.TryAdd(nic.Index, nic.Name);
            }

            foreach (var r in RouteTableReader.ReadRoutes()
                         .OrderBy(r => r.Destination.Version)
                         .ThenBy(r => r.PrefixLength == 0 ? 0 : 1)
                         .ThenByDescending(r => r.PrefixLength)
                         .ThenBy(r => r.EffectiveMetric ?? uint.MaxValue))
            {
                Routes.Add(new RouteView(
                    r.Destination.Version == IpVersion.IPv4 ? "IPv4" : "IPv6",
                    r.Prefix,
                    r.NextHop.IsUnspecified || r.NextHop.IsEmpty ? "On-link" : r.NextHop.ToString(),
                    names.TryGetValue(r.InterfaceIndex, out var n) ? $"{n} ({r.InterfaceIndex})" : r.InterfaceIndex.ToString(CultureInfo.CurrentCulture),
                    r.RouteMetric.ToString(CultureInfo.CurrentCulture),
                    r.InterfaceMetric?.ToString(CultureInfo.CurrentCulture) ?? "—",
                    r.EffectiveMetric?.ToString(CultureInfo.CurrentCulture) ?? "—",
                    r.Protocol.ToString(),
                    r.IsDefaultRoute,
                    r.EffectiveMetric ?? uint.MaxValue));
            }
        }
        catch (NativeCallException ex)
        {
            RouteMessage = "The routing table could not be read: " + ex.Message;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task LookupRouteAsync(CancellationToken token)
    {
        RouteMessage = null;
        try
        {
            var target = await DiagnosticTarget.ResolveAsync(RouteTarget, AddressPreference.System, token).ConfigureAwait(true);
            var lines = new List<string>();
            foreach (var address in target.Addresses.Take(4))
            {
                var best = RouteTableReader.FindBestRoute(IpAddressValue.FromIPAddress(address));
                if (best is null)
                {
                    lines.Add($"{address}: no route (the destination is unreachable from this computer).");
                    continue;
                }

                string nic = _engine.Latest.Interfaces.FirstOrDefault(i => i.Index == best.InterfaceIndex)?.Name ?? $"interface {best.InterfaceIndex}";
                string via = best.NextHop.IsUnspecified || best.NextHop.IsEmpty ? "directly (on-link)" : $"via gateway {best.NextHop}";
                lines.Add($"{address}: leaves through {nic} {via}, source address {best.SourceAddress}.");
            }

            RouteMessage = string.Join(Environment.NewLine, lines) + Environment.NewLine + "Computed from the routing table; no packets were sent.";
        }
        catch (OperationCanceledException)
        {
            RouteMessage = "Stopped.";
        }
        catch (DiagnosticsException ex)
        {
            RouteMessage = ex.Message;
        }
        catch (NativeCallException ex)
        {
            RouteMessage = "The route lookup failed: " + ex.Message;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ReadCacheAsync(CancellationToken token)
    {
        CacheMessage = "Reading the DNS client cache…";
        _cache.Clear();
        CacheEntries.Clear();
        try
        {
            _cache.AddRange((await DnsCacheReader.ReadAsync(token).ConfigureAwait(true)).OrderBy(e => e.Entry, StringComparer.OrdinalIgnoreCase));
            ApplyCacheFilter();
            CacheMessage = $"{_cache.Count} cached records. The list is read on request only and is not saved.";
        }
        catch (OperationCanceledException)
        {
            CacheMessage = "Stopped.";
        }
        catch (DiagnosticsException ex)
        {
            CacheMessage = ex.Message;
        }
    }

    private void ApplyCacheFilter()
    {
        string filter = CacheFilter.Trim();
        CacheEntries.Clear();
        foreach (var entry in _cache)
        {
            if (filter.Length == 0 ||
                entry.Entry.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (entry.Data?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                CacheEntries.Add(entry);
            }
        }
    }

    private void LoadDnsServers()
    {
        DnsServers.Clear();
        try
        {
            foreach (var server in DnsTool.ConfiguredServers())
            {
                DnsServers.Add(server);
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException ex)
        {
            DnsMessage = "The configured DNS servers could not be read: " + ErrorText.Describe(ex);
        }

        if (DnsServer.Length == 0 && DnsServers.Count > 0)
        {
            DnsServer = DnsServers[0].Address.ToString();
        }
    }

    private static void StartIfIdle(IAsyncRelayCommand command)
    {
        if (!command.IsRunning && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private static string Describe(ResolvedTarget target, IPAddress address) =>
        target.IsLiteral
            ? $"Destination {address}"
            : $"Destination {target.Host} → {address}" + (target.Addresses.Count > 1 ? $" (1 of {target.Addresses.Count} addresses)" : string.Empty) + $", resolved in {Ms(target.ResolveMs)}";
}
