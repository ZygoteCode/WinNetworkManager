using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Traffic;
using WinNetworkManager.Platform.Windows.Connectivity;
using WinNetworkManager.Platform.Windows.IpHelper;
using WinNetworkManager.Platform.Windows.Processes;
using WinNetworkManager.Platform.Windows.Security;
using WinNetworkManager.Telemetry.Model;
using WinNetworkManager.Telemetry.Sources;

namespace WinNetworkManager.Engine.Model;

/// <summary>Lifecycle of a process as seen by the engine.</summary>
public enum ProcessState : byte
{
    /// <summary>Running.</summary>
    Running,

    /// <summary>Exited (kept visible for a while).</summary>
    Exited,
}

/// <summary>Whether per-process traffic figures are available, and how trustworthy they are.</summary>
public enum TrafficAvailability : byte
{
    /// <summary>No collector: per-process bytes cannot be measured (never shown as zero).</summary>
    RequiresCollector,

    /// <summary>Collector starting; figures not available yet.</summary>
    Starting,

    /// <summary>Measured.</summary>
    Available,

    /// <summary>Measured, but events were lost in the current window.</summary>
    Partial,
}

/// <summary>How certain the process identity is.</summary>
public enum IdentityConfidence : byte
{
    /// <summary>PID + creation time known: PID reuse cannot be confused.</summary>
    Exact,

    /// <summary>Creation time unreadable (access denied): identity by PID and image name.</summary>
    PidAndName,
}

/// <summary>Signature summary.</summary>
/// <param name="Status">Verification status.</param>
/// <param name="Publisher">Signer subject.</param>
/// <param name="IsCatalogSigned">Catalog signature.</param>
/// <param name="Description">User-facing text.</param>
public sealed record SignatureView(SignatureStatus Status, string? Publisher, bool IsCatalogSigned, string Description);

/// <summary>A process in the live list.</summary>
public sealed record ProcessView
{
    /// <summary>Instance identity.</summary>
    public required ProcessKey Key { get; init; }

    /// <summary>Image name (e.g. chrome.exe).</summary>
    public required string Name { get; init; }

    /// <summary>Full path, when readable.</summary>
    public required Probe<string> Path { get; init; }

    /// <summary>File description from version info.</summary>
    public string? Description { get; init; }

    /// <summary>Product name from version info.</summary>
    public string? Product { get; init; }

    /// <summary>Company name from version info.</summary>
    public string? Company { get; init; }

    /// <summary>File version.</summary>
    public string? Version { get; init; }

    /// <summary>Signature, once verified.</summary>
    public SignatureView? Signature { get; init; }

    /// <summary>Services hosted (svchost).</summary>
    public IReadOnlyList<string> Services { get; init; } = [];

    /// <summary>Application id used for grouping and history (normalized path or name).</summary>
    public required string AppId { get; init; }

    /// <summary>Kernel/session-0/service-account process.</summary>
    public bool IsSystem { get; init; }

    /// <summary>Session id, or -1.</summary>
    public int SessionId { get; init; } = -1;

    /// <summary>Owner account, when readable.</summary>
    public string? UserName { get; init; }

    /// <summary>Parent PID.</summary>
    public int ParentPid { get; init; }

    /// <summary>State.</summary>
    public ProcessState State { get; init; }

    /// <summary>Start time (UTC), when known.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>Exit time (UTC), when exited.</summary>
    public DateTimeOffset? ExitTime { get; init; }

    /// <summary>Exit code, when reported by the collector.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Identity confidence.</summary>
    public IdentityConfidence Identity { get; init; }

    /// <summary>Availability of the traffic columns.</summary>
    public TrafficAvailability Traffic { get; init; }

    /// <summary>Current rates over the rate window (external traffic, plus loopback when configured).</summary>
    public TrafficRates Rates { get; init; }

    /// <summary>Totals since monitoring started (same scope as <see cref="Rates"/>).</summary>
    public ProtocolBytes Totals { get; init; }

    /// <summary>Loopback/local-machine totals (always reported separately).</summary>
    public ProtocolBytes LoopbackTotals { get; init; }

    /// <summary>Retransmitted TCP bytes.</summary>
    public long RetransmittedBytes { get; init; }

    /// <summary>Peak 1-second download rate.</summary>
    public double PeakReceiveRate { get; init; }

    /// <summary>Peak 1-second upload rate.</summary>
    public double PeakSendRate { get; init; }

    /// <summary>Average download rate over the monitored duration.</summary>
    public double AverageReceiveRate { get; init; }

    /// <summary>Average upload rate over the monitored duration.</summary>
    public double AverageSendRate { get; init; }

    /// <summary>Time the process has been observed with the collector running.</summary>
    public TimeSpan MonitoredDuration { get; init; }

    /// <summary>Established/active TCP connections.</summary>
    public int TcpConnections { get; init; }

    /// <summary>TCP listeners.</summary>
    public int TcpListeners { get; init; }

    /// <summary>Bound UDP endpoints.</summary>
    public int UdpEndpoints { get; init; }

    /// <summary>Distinct remote addresses (TCP connections + observed UDP peers).</summary>
    public int RemoteAddresses { get; init; }

    /// <summary>Last time traffic was attributed to the process.</summary>
    public DateTimeOffset? LastActivity { get; init; }

    /// <summary>Total rate per second over the last 60 s (oldest first), for sparklines; empty without collector.</summary>
    public IReadOnlyList<float> Sparkline { get; init; } = [];

    /// <summary>True when any traffic was ever attributed.</summary>
    public bool HasTraffic => Totals.Total > 0 || LoopbackTotals.Total > 0;
}

/// <summary>Where a connection record comes from.</summary>
public enum ConnectionOrigin : byte
{
    /// <summary>Seen in the IP Helper table.</summary>
    Table,

    /// <summary>Only observed through ETW (opened and closed between two table polls).</summary>
    ObservedOnly,
}

/// <summary>A TCP connection or listener.</summary>
public sealed record ConnectionView
{
    /// <summary>Stable id for the lifetime of the record.</summary>
    public required long Id { get; init; }

    /// <summary>Local endpoint.</summary>
    public required IpEndpointValue Local { get; init; }

    /// <summary>Remote endpoint (unspecified for listeners).</summary>
    public required IpEndpointValue Remote { get; init; }

    /// <summary>State (CLOSED once the row disappeared).</summary>
    public required TcpState State { get; init; }

    /// <summary>Owning process instance (last known owner for TIME_WAIT rows).</summary>
    public ProcessKey? Owner { get; init; }

    /// <summary>Owner PID as reported by the table (0 for TIME_WAIT).</summary>
    public int OwnerPid { get; init; }

    /// <summary>Owner display name.</summary>
    public string OwnerName { get; init; } = string.Empty;

    /// <summary>Owning service/module (svchost).</summary>
    public string? OwnerModule { get; init; }

    /// <summary>First time this record was seen.</summary>
    public required DateTimeOffset FirstSeen { get; init; }

    /// <summary>Creation time reported by Windows, when available.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Time the row disappeared.</summary>
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>Remote address scope.</summary>
    public AddressScope RemoteScope { get; init; }

    /// <summary>True for loopback/local-machine connections.</summary>
    public bool IsLoopback { get; init; }

    /// <summary>Bytes received on this connection while monitored.</summary>
    public long ReceivedBytes { get; init; }

    /// <summary>Bytes sent on this connection while monitored.</summary>
    public long SentBytes { get; init; }

    /// <summary>Receive rate over the rate window.</summary>
    public double ReceiveRate { get; init; }

    /// <summary>Send rate over the rate window.</summary>
    public double SendRate { get; init; }

    /// <summary>TCP retransmissions observed.</summary>
    public long Retransmissions { get; init; }

    /// <summary>True when byte figures are measured (collector running since the connection was seen).</summary>
    public bool BytesAvailable { get; init; }

    /// <summary>Reverse DNS name, when resolution is enabled and finished.</summary>
    public string? RemoteHostName { get; init; }

    /// <summary>Origin of the record.</summary>
    public ConnectionOrigin Origin { get; init; }

    /// <summary>True for listening sockets.</summary>
    public bool IsListener => State == TcpState.Listen;

    /// <summary>Age, when the creation time is known; otherwise time since first seen.</summary>
    public TimeSpan Age(DateTimeOffset now) => (ClosedAt ?? now) - (CreatedAt ?? FirstSeen);
}

/// <summary>A remote peer observed on a UDP endpoint.</summary>
/// <param name="Remote">Remote endpoint.</param>
/// <param name="ReceivedBytes">Bytes received from it.</param>
/// <param name="SentBytes">Bytes sent to it.</param>
/// <param name="LastSeen">Last datagram.</param>
/// <param name="Scope">Remote scope.</param>
public readonly record struct UdpPeerView(IpEndpointValue Remote, long ReceivedBytes, long SentBytes, DateTimeOffset LastSeen, AddressScope Scope);

/// <summary>A bound UDP endpoint. UDP has no connection: remote peers exist only as observed datagrams.</summary>
public sealed record UdpEndpointView
{
    /// <summary>Stable id.</summary>
    public required long Id { get; init; }

    /// <summary>Local endpoint (often a wildcard address).</summary>
    public required IpEndpointValue Local { get; init; }

    /// <summary>Owner.</summary>
    public ProcessKey? Owner { get; init; }

    /// <summary>Owner PID.</summary>
    public int OwnerPid { get; init; }

    /// <summary>Owner name.</summary>
    public string OwnerName { get; init; } = string.Empty;

    /// <summary>Owning service/module.</summary>
    public string? OwnerModule { get; init; }

    /// <summary>First seen.</summary>
    public required DateTimeOffset FirstSeen { get; init; }

    /// <summary>Bind time reported by Windows.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Time the socket disappeared.</summary>
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>Bytes received.</summary>
    public long ReceivedBytes { get; init; }

    /// <summary>Bytes sent.</summary>
    public long SentBytes { get; init; }

    /// <summary>Receive rate.</summary>
    public double ReceiveRate { get; init; }

    /// <summary>Send rate.</summary>
    public double SendRate { get; init; }

    /// <summary>Observed peers (most recent first, bounded).</summary>
    public IReadOnlyList<UdpPeerView> Peers { get; init; } = [];

    /// <summary>Total distinct peers observed (may exceed <see cref="Peers"/>).</summary>
    public int PeerCount { get; init; }

    /// <summary>True when byte figures are measured.</summary>
    public bool BytesAvailable { get; init; }
}

/// <summary>Unicast address of an interface.</summary>
/// <param name="Address">Address.</param>
/// <param name="PrefixLength">Prefix length.</param>
/// <param name="Origin">How it was assigned (Dhcp, Manual, LinkLayer, RouterAdvertisement, Random, WellKnown, Other).</param>
public sealed record InterfaceAddressView(IpAddressValue Address, int PrefixLength, string Origin);

/// <summary>A network interface with configuration and live counters.</summary>
public sealed record InterfaceView
{
    /// <summary>Interface GUID.</summary>
    public required Guid Id { get; init; }

    /// <summary>Interface index.</summary>
    public uint Index { get; init; }

    /// <summary>LUID.</summary>
    public ulong Luid { get; init; }

    /// <summary>Friendly name.</summary>
    public required string Name { get; init; }

    /// <summary>Adapter description.</summary>
    public required string Description { get; init; }

    /// <summary>Type text (Ethernet, Wi-Fi, Loopback, Tunnel, …).</summary>
    public required string TypeName { get; init; }

    /// <summary>Hardware adapter (as opposed to virtual/tunnel).</summary>
    public bool IsHardware { get; init; }

    /// <summary>Status.</summary>
    public InterfaceOperationalStatus Status { get; init; }

    /// <summary>Carries the default IPv4 route used for Internet traffic.</summary>
    public bool IsDefaultIPv4 { get; init; }

    /// <summary>Carries the default IPv6 route used for Internet traffic.</summary>
    public bool IsDefaultIPv6 { get; init; }

    /// <summary>Link speed (bits per second), receive direction.</summary>
    public long ReceiveLinkSpeed { get; init; }

    /// <summary>Link speed (bits per second), transmit direction.</summary>
    public long TransmitLinkSpeed { get; init; }

    /// <summary>MAC address (AA-BB-…), when present.</summary>
    public string? MacAddress { get; init; }

    /// <summary>Unicast addresses.</summary>
    public IReadOnlyList<InterfaceAddressView> Addresses { get; init; } = [];

    /// <summary>Gateways.</summary>
    public IReadOnlyList<IpAddressValue> Gateways { get; init; } = [];

    /// <summary>DNS servers.</summary>
    public IReadOnlyList<IpAddressValue> DnsServers { get; init; } = [];

    /// <summary>DNS suffix.</summary>
    public string? DnsSuffix { get; init; }

    /// <summary>DHCP enabled (IPv4).</summary>
    public bool DhcpEnabled { get; init; }

    /// <summary>DHCP servers.</summary>
    public IReadOnlyList<IpAddressValue> DhcpServers { get; init; } = [];

    /// <summary>IPv4 MTU.</summary>
    public int? Mtu { get; init; }

    /// <summary>Receive rate (bytes/s, link-layer octets), null when not computable (first sample, reset, gap).</summary>
    public double? ReceiveRate { get; init; }

    /// <summary>Send rate (bytes/s).</summary>
    public double? SendRate { get; init; }

    /// <summary>Bytes received since monitoring started.</summary>
    public long SessionReceived { get; init; }

    /// <summary>Bytes sent since monitoring started.</summary>
    public long SessionSent { get; init; }

    /// <summary>Inbound errors + discards (cumulative, from the OS).</summary>
    public long InErrors { get; init; }

    /// <summary>Outbound errors + discards.</summary>
    public long OutErrors { get; init; }

    /// <summary>True when the last counter sample was a reset.</summary>
    public bool CounterReset { get; init; }

    /// <summary>Recent receive rate history (1 s, last 60 s, oldest first; NaN = gap).</summary>
    public IReadOnlyList<float> ReceiveHistory { get; init; } = [];

    /// <summary>Recent send rate history.</summary>
    public IReadOnlyList<float> SendHistory { get; init; } = [];
}

/// <summary>System-wide traffic, keeping the three sources apart.</summary>
public sealed record SystemTrafficView
{
    /// <summary>Attributed process traffic (external, plus loopback when configured) over the rate window.</summary>
    public TrafficRates AttributedRates { get; init; }

    /// <summary>Loopback/local-machine traffic rates.</summary>
    public TrafficRates LoopbackRates { get; init; }

    /// <summary>Attributed totals since monitoring started.</summary>
    public ProtocolBytes AttributedTotals { get; init; }

    /// <summary>Loopback totals.</summary>
    public ProtocolBytes LoopbackTotals { get; init; }

    /// <summary>Physical interfaces receive rate (link-layer, all protocols).</summary>
    public double? PhysicalReceiveRate { get; init; }

    /// <summary>Physical interfaces send rate.</summary>
    public double? PhysicalSendRate { get; init; }

    /// <summary>Physical bytes received since monitoring started.</summary>
    public long PhysicalReceived { get; init; }

    /// <summary>Physical bytes sent since monitoring started.</summary>
    public long PhysicalSent { get; init; }

    /// <summary>Availability of attributed figures.</summary>
    public TrafficAvailability Traffic { get; init; }

    /// <summary>Processes with traffic in the rate window.</summary>
    public int ActiveProcesses { get; init; }

    /// <summary>Processes owning at least one established TCP connection or UDP endpoint.</summary>
    public int ProcessesWithSockets { get; init; }

    /// <summary>Established (active) TCP connections.</summary>
    public int TcpConnections { get; init; }

    /// <summary>TCP listeners.</summary>
    public int TcpListeners { get; init; }

    /// <summary>TIME_WAIT rows.</summary>
    public int TcpTimeWait { get; init; }

    /// <summary>Bound UDP endpoints.</summary>
    public int UdpEndpoints { get; init; }

    /// <summary>Connectivity.</summary>
    public ConnectivityStatus Connectivity { get; init; }

    /// <summary>Per-second download history of physical interfaces (last 60 s).</summary>
    public IReadOnlyList<float> PhysicalReceiveHistory { get; init; } = [];

    /// <summary>Per-second upload history of physical interfaces.</summary>
    public IReadOnlyList<float> PhysicalSendHistory { get; init; } = [];

    /// <summary>Per-second attributed download history.</summary>
    public IReadOnlyList<float> AttributedReceiveHistory { get; init; } = [];

    /// <summary>Per-second attributed upload history.</summary>
    public IReadOnlyList<float> AttributedSendHistory { get; init; } = [];
}

/// <summary>Collector and privilege state for the status bar and the dashboard.</summary>
public sealed record CollectorView
{
    /// <summary>Source kind.</summary>
    public TelemetrySourceKind Kind { get; init; }

    /// <summary>State.</summary>
    public CollectorState State { get; init; }

    /// <summary>Events per second.</summary>
    public double EventsPerSecond { get; init; }

    /// <summary>Events lost since start.</summary>
    public long EventsLost { get; init; }

    /// <summary>Late events since start.</summary>
    public long LateEvents { get; init; }

    /// <summary>Last error.</summary>
    public string? Error { get; init; }

    /// <summary>True when ETW access was denied.</summary>
    public bool AccessDenied { get; init; }

    /// <summary>Time up to which traffic data is final.</summary>
    public DateTimeOffset? DataAsOf { get; init; }

    /// <summary>Privileges of the UI process.</summary>
    public required PrivilegeInfo Privileges { get; init; }

    /// <summary>True when the elevated helper executable is installed.</summary>
    public bool HelperAvailable { get; init; }

    /// <summary>True when the rate window contains intervals with lost events.</summary>
    public bool PartialData { get; init; }

    /// <summary>Last start attempt was declined at the UAC prompt.</summary>
    public bool ElevationDeclined { get; init; }
}

/// <summary>Immutable snapshot of everything the UI shows, published at the refresh interval.</summary>
public sealed record EngineSnapshot
{
    /// <summary>Monotonic sequence number.</summary>
    public required long Sequence { get; init; }

    /// <summary>Time the snapshot was built.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Rate averaging window.</summary>
    public required TimeSpan RateWindow { get; init; }

    /// <summary>Collector state.</summary>
    public required CollectorView Collector { get; init; }

    /// <summary>System totals.</summary>
    public required SystemTrafficView System { get; init; }

    /// <summary>Processes (running + recently exited).</summary>
    public required IReadOnlyList<ProcessView> Processes { get; init; }

    /// <summary>TCP connections and listeners (current + recently closed).</summary>
    public required IReadOnlyList<ConnectionView> Connections { get; init; }

    /// <summary>UDP endpoints.</summary>
    public required IReadOnlyList<UdpEndpointView> UdpEndpoints { get; init; }

    /// <summary>Interfaces.</summary>
    public required IReadOnlyList<InterfaceView> Interfaces { get; init; }

    /// <summary>An empty snapshot.</summary>
    public static EngineSnapshot Empty(PrivilegeInfo privileges) => new()
    {
        Sequence = 0,
        Timestamp = DateTimeOffset.UtcNow,
        RateWindow = TimeSpan.FromSeconds(1),
        Collector = new CollectorView { Privileges = privileges },
        System = new SystemTrafficView(),
        Processes = [],
        Connections = [],
        UdpEndpoints = [],
        Interfaces = [],
    };
}
