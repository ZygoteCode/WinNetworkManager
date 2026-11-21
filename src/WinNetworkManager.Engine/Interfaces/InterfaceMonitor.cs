using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Traffic;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Platform.Windows.Connectivity;
using WinNetworkManager.Platform.Windows.IpHelper;

namespace WinNetworkManager.Engine.Interfaces;

/// <summary>Static configuration of an interface (from GetAdaptersAddresses via System.Net.NetworkInformation).</summary>
internal sealed record InterfaceConfiguration(
    Guid Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    IReadOnlyList<InterfaceAddressView> Addresses,
    IReadOnlyList<IpAddressValue> Gateways,
    IReadOnlyList<IpAddressValue> DnsServers,
    string? DnsSuffix,
    bool DhcpEnabled,
    IReadOnlyList<IpAddressValue> DhcpServers,
    int? Mtu,
    string? Mac);

/// <summary>Provides interface configuration (abstracted for tests).</summary>
internal interface IInterfaceConfigurationSource
{
    IReadOnlyList<InterfaceConfiguration> Read();
}

/// <summary>Reads configuration with <see cref="NetworkInterface"/>.</summary>
internal sealed class NetworkInterfaceConfigurationSource : IInterfaceConfigurationSource
{
    public IReadOnlyList<InterfaceConfiguration> Read()
    {
        var result = new List<InterfaceConfiguration>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!Guid.TryParse(nic.Id, out var id))
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var addresses = properties.UnicastAddresses
                .Select(u => new InterfaceAddressView(IpAddressValue.FromIPAddress(u.Address), u.PrefixLength, Origin(u)))
                .ToArray();
            int? mtu = null;
            bool dhcp = false;
            try
            {
                var v4 = nic.Supports(NetworkInterfaceComponent.IPv4) ? properties.GetIPv4Properties() : null;
                mtu = v4?.Mtu;
                dhcp = v4?.IsDhcpEnabled ?? false;
            }
            catch (NetworkInformationException)
            {
                // IPv4 not bound.
            }

            byte[] mac = nic.GetPhysicalAddress().GetAddressBytes();
            result.Add(new InterfaceConfiguration(
                id,
                nic.Name,
                nic.Description,
                nic.NetworkInterfaceType,
                addresses,
                properties.GatewayAddresses.Select(g => IpAddressValue.FromIPAddress(g.Address)).Where(a => !a.IsUnspecified).ToArray(),
                properties.DnsAddresses.Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).Select(IpAddressValue.FromIPAddress).ToArray(),
                string.IsNullOrEmpty(properties.DnsSuffix) ? null : properties.DnsSuffix,
                dhcp,
                properties.DhcpServerAddresses.Select(IpAddressValue.FromIPAddress).ToArray(),
                mtu,
                mac.Length == 0 ? null : string.Join('-', mac.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)))));
        }

        return result;
    }

    private static string Origin(UnicastIPAddressInformation address)
    {
        try
        {
            return address.PrefixOrigin switch
            {
                PrefixOrigin.Dhcp => "DHCP",
                PrefixOrigin.Manual => "Manual",
                PrefixOrigin.RouterAdvertisement => address.SuffixOrigin == SuffixOrigin.Random ? "SLAAC (temporary)" : "SLAAC",
                PrefixOrigin.WellKnown => address.Address.IsIPv6LinkLocal || address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal) ? "Link-local" : "Well-known",
                _ => "Other",
            };
        }
        catch (PlatformNotSupportedException)
        {
            return "Other";
        }
    }
}

/// <summary>
/// Samples interface counters every tick (rates from real elapsed time; resets and gaps never produce rates),
/// refreshes configuration on network changes, and determines the interfaces carrying the default routes.
/// Engine-thread only, except <see cref="MarkConfigurationDirty"/>.
/// </summary>
internal sealed class InterfaceMonitor : IDisposable
{
    private static readonly IpAddressValue ProbeIPv4 = IpAddressValue.FromIPv4(8, 8, 8, 8);
    private static readonly IpAddressValue ProbeIPv6 = IpAddressValue.FromIPv6(0x2001486048600000, 0x8888);

    private readonly IInterfaceTableSource _counters;
    private readonly IInterfaceConfigurationSource _configuration;
    private readonly ILogger _logger;
    private readonly Dictionary<ulong, State> _states = [];
    private readonly long _frequency;
    private IReadOnlyList<InterfaceConfiguration> _config = [];
    private IReadOnlyList<InterfaceRow> _rows = [];
    private int _configDirty = 1;
    private DateTimeOffset _lastConfigRead;
    private DateTimeOffset _lastRouteRead;
    private uint? _defaultV4;
    private uint? _defaultV6;
    private bool _subscribed;

    public InterfaceMonitor(IInterfaceTableSource counters, IInterfaceConfigurationSource configuration, long timestampFrequency, ILogger logger)
    {
        _counters = counters;
        _configuration = configuration;
        _frequency = timestampFrequency;
        _logger = logger;
    }

    /// <summary>Physical traffic accumulator (hardware, non-filter, non-loopback interfaces).</summary>
    public PhysicalTotals Physical { get; } = new();

    /// <summary>Current connectivity.</summary>
    public ConnectivityStatus Connectivity { get; private set; }

    /// <summary>Raised (on a thread-pool thread) when the network configuration changes.</summary>
    public event EventHandler? ConfigurationChanged;

    /// <summary>
    /// Receives every counter delta (interface id, name, previous timestamp, timestamp, received, sent, physical) on
    /// the engine thread; used by the history recorder.
    /// </summary>
    public Action<Guid, string, long, long, long, long, bool>? DeltaObserver { get; set; }

    /// <summary>Subscribes to system notifications.</summary>
    public void Start()
    {
        if (_subscribed)
        {
            return;
        }

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        _subscribed = true;
    }

    /// <summary>Forces a configuration refresh at the next sample.</summary>
    public void MarkConfigurationDirty() => Interlocked.Exchange(ref _configDirty, 1);

    /// <summary>Samples counters (and configuration when needed).</summary>
    public void Sample(long timestamp, DateTimeOffset now)
    {
        if (Interlocked.Exchange(ref _configDirty, 0) == 1 || now - _lastConfigRead > TimeSpan.FromSeconds(30))
        {
            try
            {
                _config = _configuration.Read();
                Connectivity = NetworkListManager.GetConnectivity();
            }
            catch (NetworkInformationException ex)
            {
                _logger.LogWarning(ex, "Reading interface configuration failed.");
            }

            _lastConfigRead = now;
            _lastRouteRead = DateTimeOffset.MinValue;
        }

        if (now - _lastRouteRead > TimeSpan.FromSeconds(10))
        {
            _defaultV4 = RouteTableReader.FindBestRoute(ProbeIPv4)?.InterfaceIndex;
            _defaultV6 = RouteTableReader.FindBestRoute(ProbeIPv6)?.InterfaceIndex;
            _lastRouteRead = now;
        }

        _rows = _counters.ReadInterfaces();
        var maxInterval = TimeSpan.FromSeconds(5);
        double? physicalRx = 0;
        double? physicalTx = 0;
        var seen = new HashSet<ulong>();
        foreach (var row in _rows)
        {
            seen.Add(row.Luid);
            if (!_states.TryGetValue(row.Luid, out var state))
            {
                state = new State();
                _states[row.Luid] = state;
            }

            long previousTimestamp = state.LastTimestamp;
            state.LastTimestamp = timestamp;
            var rx = state.Rx.Update(row.InOctets, timestamp, _frequency, maxInterval);
            var tx = state.Tx.Update(row.OutOctets, timestamp, _frequency, maxInterval);
            if (rx.Kind is CounterSampleKind.Valid or CounterSampleKind.Gap && tx.Kind is CounterSampleKind.Valid or CounterSampleKind.Gap)
            {
                DeltaObserver?.Invoke(row.Guid, row.Alias, previousTimestamp, timestamp, (long)rx.Delta, (long)tx.Delta, IsPhysical(row));
            }
            state.ReceiveRate = rx.RatePerSecond;
            state.SendRate = tx.RatePerSecond;
            state.CounterReset = rx.Kind == CounterSampleKind.Reset || tx.Kind == CounterSampleKind.Reset;
            if (rx.Kind is CounterSampleKind.Valid or CounterSampleKind.Gap)
            {
                state.SessionReceived += (long)rx.Delta;
            }

            if (tx.Kind is CounterSampleKind.Valid or CounterSampleKind.Gap)
            {
                state.SessionSent += (long)tx.Delta;
            }

            long second = timestamp / _frequency;
            state.History.AdvanceTo(second);
            if (state.ReceiveRate is { } r)
            {
                state.History.Add(second, new RatePair(r, state.SendRate ?? 0, 1));
            }

            if (IsPhysical(row))
            {
                physicalRx = state.ReceiveRate is { } prx && physicalRx is { } arx ? arx + prx : null;
                physicalTx = state.SendRate is { } ptx && physicalTx is { } atx ? atx + ptx : null;
                if (rx.Kind is CounterSampleKind.Valid or CounterSampleKind.Gap)
                {
                    Physical.Received += (long)rx.Delta;
                }

                if (tx.Kind is CounterSampleKind.Valid or CounterSampleKind.Gap)
                {
                    Physical.Sent += (long)tx.Delta;
                }
            }
        }

        foreach (ulong gone in _states.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _states.Remove(gone); // interface removed: its counters will start from a new baseline if it returns
        }

        Physical.ReceiveRate = physicalRx;
        Physical.SendRate = physicalTx;
        long physicalSecond = timestamp / _frequency;
        Physical.History.AdvanceTo(physicalSecond);
        if (physicalRx is { } prr)
        {
            Physical.History.Add(physicalSecond, new RatePair(prr, physicalTx ?? 0, 1));
        }
    }

    /// <summary>Builds views for the interfaces present in the configuration (IP-enabled adapters).</summary>
    public IReadOnlyList<InterfaceView> BuildViews(long timestamp)
    {
        var rowsByGuid = new Dictionary<Guid, InterfaceRow>();
        foreach (var row in _rows)
        {
            // Filter interfaces share the GUID of their base interface in some drivers; prefer the base row.
            if (!row.IsFilter || !rowsByGuid.ContainsKey(row.Guid))
            {
                rowsByGuid[row.Guid] = row;
            }
        }

        long second = timestamp / _frequency;
        var views = new List<InterfaceView>(_config.Count);
        foreach (var config in _config)
        {
            rowsByGuid.TryGetValue(config.Id, out var row);
            if (row is { IsFilter: true })
            {
                // NDIS filter layers ("…-WFP Native MAC Layer LightWeight Filter-0000") repeat the counters of the
                // adapter they sit on; Windows never lists them as connections.
                continue;
            }

            State? state = row is null ? null : _states.GetValueOrDefault(row.Luid);
            views.Add(new InterfaceView
            {
                Id = config.Id,
                Index = row?.Index ?? 0,
                Luid = row?.Luid ?? 0,
                Name = config.Name,
                Description = config.Description,
                TypeName = TypeName(config.Type, row),
                IsHardware = row?.IsHardware ?? false,
                Status = row?.OperStatus ?? InterfaceOperationalStatus.Unknown,
                IsDefaultIPv4 = row is not null && _defaultV4 == row.Index,
                IsDefaultIPv6 = row is not null && _defaultV6 == row.Index,
                ReceiveLinkSpeed = (long)Math.Min(row?.ReceiveLinkSpeed ?? 0, long.MaxValue),
                TransmitLinkSpeed = (long)Math.Min(row?.TransmitLinkSpeed ?? 0, long.MaxValue),
                MacAddress = config.Mac,
                Addresses = config.Addresses,
                Gateways = config.Gateways,
                DnsServers = config.DnsServers,
                DnsSuffix = config.DnsSuffix,
                DhcpEnabled = config.DhcpEnabled,
                DhcpServers = config.DhcpServers,
                Mtu = config.Mtu ?? (row is null ? null : (int)row.Mtu),
                ReceiveRate = state?.ReceiveRate,
                SendRate = state?.SendRate,
                SessionReceived = state?.SessionReceived ?? 0,
                SessionSent = state?.SessionSent ?? 0,
                InErrors = row is null ? 0 : (long)(row.InErrors + row.InDiscards),
                OutErrors = row is null ? 0 : (long)(row.OutErrors + row.OutDiscards),
                CounterReset = state?.CounterReset ?? false,
                ReceiveHistory = state?.History.Rates(second, 60, receive: true) ?? [],
                SendHistory = state?.History.Rates(second, 60, receive: false) ?? [],
            });
        }

        return views
            .OrderByDescending(v => v.IsDefaultIPv4 || v.IsDefaultIPv6)
            .ThenBy(v => v.Status == InterfaceOperationalStatus.Up ? 0 : 1)
            .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>True for interfaces counted in the "physical" total: hardware, not NDIS filters, not loopback.</summary>
    internal static bool IsPhysical(InterfaceRow row) => row.IsHardware && !row.IsFilter && !row.IsLoopback && row.OperStatus == InterfaceOperationalStatus.Up;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_subscribed)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
            _subscribed = false;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        MarkConfigurationDirty();
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkChanged(sender, e);

    private static string TypeName(NetworkInterfaceType type, InterfaceRow? row) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit
            => row is { IsHardware: false } ? "Virtual Ethernet" : "Ethernet",
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Loopback => "Loopback",
        NetworkInterfaceType.Tunnel => "Tunnel",
        NetworkInterfaceType.Ppp => "PPP",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile broadband",
        _ => type.ToString(),
    };

    /// <summary>Physical traffic totals and rates.</summary>
    internal sealed class PhysicalTotals
    {
        public long Received { get; set; }

        public long Sent { get; set; }

        public double? ReceiveRate { get; set; }

        public double? SendRate { get; set; }

        public RateHistory History { get; } = new(120);
    }

    private sealed class State
    {
        public CumulativeCounter Rx;
        public CumulativeCounter Tx;

        public double? ReceiveRate { get; set; }

        public double? SendRate { get; set; }

        public long SessionReceived { get; set; }

        public long SessionSent { get; set; }

        public bool CounterReset { get; set; }

        public long LastTimestamp { get; set; }

        public RateHistory History { get; } = new(120);
    }
}

/// <summary>Per-second average of sampled rates (count used to average several samples in one second).</summary>
internal readonly record struct RatePair(double Receive, double Send, int Samples) : System.Numerics.IAdditionOperators<RatePair, RatePair, RatePair>
{
    public static RatePair operator +(RatePair left, RatePair right) => new(left.Receive + right.Receive, left.Send + right.Send, left.Samples + right.Samples);
}

/// <summary>Ring of per-second rate samples.</summary>
internal sealed class RateHistory
{
    private readonly BucketRing<RatePair> _ring;

    public RateHistory(int seconds)
    {
        _ring = new BucketRing<RatePair>(seconds);
    }

    public void AdvanceTo(long second) => _ring.AdvanceTo(second);

    public void Add(long second, RatePair sample) => _ring.Add(second, sample);

    /// <summary>Rates for the <paramref name="seconds"/> seconds ending at <paramref name="last"/>; NaN where no sample exists.</summary>
    public float[] Rates(long last, int seconds, bool receive)
    {
        var result = new float[seconds];
        for (int i = 0; i < seconds; i++)
        {
            var pair = _ring.Get(last - seconds + 1 + i);
            result[i] = pair.Samples == 0 ? float.NaN : (float)((receive ? pair.Receive : pair.Send) / pair.Samples);
        }

        return result;
    }
}
