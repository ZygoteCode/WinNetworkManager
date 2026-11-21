using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WinNetworkManager.App.Services;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Engine.Model;

namespace WinNetworkManager.App.ViewModels;

/// <summary>A TCP connection or listener row.</summary>
public sealed partial class TcpRow : ObservableObject
{
    private bool _collectorRunning;

    /// <summary>Creates a row.</summary>
    public TcpRow(long id) => Id = id;

    /// <summary>Record id.</summary>
    public long Id { get; }

    /// <summary>Latest view.</summary>
    public ConnectionView? View { get; private set; }

    [ObservableProperty] public partial string ProcessName { get; set; } = string.Empty;
    [ObservableProperty] public partial string PidText { get; set; } = string.Empty;
    [ObservableProperty] public partial ImageSource? Icon { get; set; }
    [ObservableProperty] public partial string LocalAddress { get; set; } = string.Empty;
    [ObservableProperty] public partial string LocalPort { get; set; } = string.Empty;
    [ObservableProperty] public partial string RemoteAddress { get; set; } = string.Empty;
    [ObservableProperty] public partial string RemotePort { get; set; } = string.Empty;
    [ObservableProperty] public partial string RemoteHost { get; set; } = string.Empty;
    [ObservableProperty] public partial string ScopeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StateDescription { get; set; } = string.Empty;
    [ObservableProperty] public partial string AgeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AgeToolTip { get; set; } = string.Empty;
    [ObservableProperty] public partial string DownRateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UpRateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DownTotalText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UpTotalText { get; set; } = string.Empty;
    [ObservableProperty] public partial string RetransText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ServiceText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsClosed { get; set; }
    [ObservableProperty] public partial bool IsListener { get; set; }

    /// <summary>Owning process (for navigation).</summary>
    public ProcessKey? Owner => View?.Owner;

    /// <summary>Numeric values for sorting.</summary>
    public int Pid => View?.OwnerPid ?? 0;

    public double DownRate => View?.ReceiveRate ?? 0;

    public double UpRate => View?.SendRate ?? 0;

    public long DownTotal => View?.ReceivedBytes ?? 0;

    public long UpTotal => View?.SentBytes ?? 0;

    public TimeSpan Age { get; private set; }

    /// <summary>Accessible summary.</summary>
    public string AccessibleName => $"{ProcessName} {LocalAddress}:{LocalPort} to {RemoteAddress}:{RemotePort}, {StateText}";

    /// <summary>Updates from a view.</summary>
    public void Update(ConnectionView view, DateTimeOffset now, bool collectorRunning)
    {
        bool changed = !ReferenceEquals(View, view) || _collectorRunning != collectorRunning;
        _collectorRunning = collectorRunning;
        View = view;
        Age = view.Age(now);
        AgeText = Fmt.Duration(Age) + (view.CreatedAt is null ? "+" : string.Empty);
        AgeToolTip = view.CreatedAt is { } created
            ? $"Created {Fmt.DateTime(created)} (reported by Windows)" + (view.ClosedAt is { } c ? $"\nClosed {Fmt.Time(c)}" : string.Empty)
            : $"First seen {Fmt.Time(view.FirstSeen)}; Windows did not report the creation time, so the real age may be longer.";
        if (!changed)
        {
            return;
        }

        ProcessName = view.OwnerName;
        PidText = view.OwnerPid == 0 ? "—" : view.OwnerPid.ToString(CultureInfo.InvariantCulture);
        LocalAddress = Fmt.Address(view.Local.Address);
        LocalPort = Fmt.Port(view.Local.Port);
        IsListener = view.IsListener;
        RemoteAddress = view.IsListener ? string.Empty : Fmt.Address(view.Remote.Address);
        RemotePort = view.IsListener ? string.Empty : Fmt.Port(view.Remote.Port);
        RemoteHost = view.RemoteHostName ?? string.Empty;
        ScopeText = view.IsListener ? "Listening" : AddressClassifier.DisplayName(view.RemoteScope);
        StateText = view.Origin == ConnectionOrigin.ObservedOnly ? "CLOSED (observed)" : TcpStateInfo.DisplayName(view.State);
        StateDescription = view.Origin == ConnectionOrigin.ObservedOnly
            ? "This connection opened and closed between two table polls; it is known only from kernel events."
            : TcpStateInfo.Description(view.State);
        IsClosed = view.ClosedAt is not null;
        bool bytes = view.BytesAvailable && collectorRunning;
        DownRateText = view.IsListener ? string.Empty : Fmt.Rate(view.ReceiveRate, bytes);
        UpRateText = view.IsListener ? string.Empty : Fmt.Rate(view.SendRate, bytes);
        DownTotalText = view.IsListener ? string.Empty : Fmt.Bytes(view.ReceivedBytes, view.BytesAvailable);
        UpTotalText = view.IsListener ? string.Empty : Fmt.Bytes(view.SentBytes, view.BytesAvailable);
        RetransText = view.Retransmissions > 0 ? Fmt.Count(view.Retransmissions) : string.Empty;
        ServiceText = view.OwnerModule ?? string.Empty;
        OnPropertyChanged(nameof(AccessibleName));
    }

    /// <summary>Formats an endpoint for copying.</summary>
    public string LocalEndpoint => View is { } v ? IpEndpointValue.Format(v.Local.Address, v.Local.Port) : string.Empty;

    /// <summary>Formats an endpoint for copying.</summary>
    public string RemoteEndpoint => View is { IsListener: false } v ? IpEndpointValue.Format(v.Remote.Address, v.Remote.Port) : string.Empty;
}

/// <summary>A UDP endpoint row.</summary>
public sealed partial class UdpRow : ObservableObject
{
    private bool _collectorRunning;

    /// <summary>Creates a row.</summary>
    public UdpRow(long id) => Id = id;

    /// <summary>Record id.</summary>
    public long Id { get; }

    /// <summary>Latest view.</summary>
    public UdpEndpointView? View { get; private set; }

    [ObservableProperty] public partial string ProcessName { get; set; } = string.Empty;
    [ObservableProperty] public partial string PidText { get; set; } = string.Empty;
    [ObservableProperty] public partial ImageSource? Icon { get; set; }
    [ObservableProperty] public partial string LocalAddress { get; set; } = string.Empty;
    [ObservableProperty] public partial string LocalPort { get; set; } = string.Empty;
    [ObservableProperty] public partial string PeersText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DownRateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UpRateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DownTotalText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UpTotalText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AgeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ServiceText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsClosed { get; set; }
    [ObservableProperty] public partial IReadOnlyList<UdpPeerItem> Peers { get; set; } = [];

    /// <summary>Owning process.</summary>
    public ProcessKey? Owner => View?.Owner;

    public int Pid => View?.OwnerPid ?? 0;

    public double DownRate => View?.ReceiveRate ?? 0;

    public double UpRate => View?.SendRate ?? 0;

    public long DownTotal => View?.ReceivedBytes ?? 0;

    public long UpTotal => View?.SentBytes ?? 0;

    public int PeerCount => View?.PeerCount ?? 0;

    public TimeSpan Age { get; private set; }

    /// <summary>Accessible summary.</summary>
    public string AccessibleName => $"{ProcessName} UDP {LocalAddress}:{LocalPort}, {PeersText}";

    /// <summary>Local endpoint text.</summary>
    public string LocalEndpoint => View is { } v ? IpEndpointValue.Format(v.Local.Address, v.Local.Port) : string.Empty;

    /// <summary>Updates from a view.</summary>
    public void Update(UdpEndpointView view, DateTimeOffset now, bool collectorRunning)
    {
        bool changed = !ReferenceEquals(View, view) || _collectorRunning != collectorRunning;
        _collectorRunning = collectorRunning;
        View = view;
        Age = (view.ClosedAt ?? now) - (view.CreatedAt ?? view.FirstSeen);
        AgeText = Fmt.Duration(Age) + (view.CreatedAt is null ? "+" : string.Empty);
        if (!changed)
        {
            return;
        }

        ProcessName = view.OwnerName;
        PidText = view.OwnerPid == 0 ? "—" : view.OwnerPid.ToString(CultureInfo.InvariantCulture);
        LocalAddress = Fmt.Address(view.Local.Address);
        LocalPort = Fmt.Port(view.Local.Port);
        PeersText = view.BytesAvailable ? (view.PeerCount == 0 ? "no datagrams seen" : $"{view.PeerCount} peer{(view.PeerCount == 1 ? string.Empty : "s")}") : "needs collector";
        bool bytes = view.BytesAvailable && collectorRunning;
        DownRateText = Fmt.Rate(view.ReceiveRate, bytes);
        UpRateText = Fmt.Rate(view.SendRate, bytes);
        DownTotalText = Fmt.Bytes(view.ReceivedBytes, view.BytesAvailable);
        UpTotalText = Fmt.Bytes(view.SentBytes, view.BytesAvailable);
        ServiceText = view.OwnerModule ?? string.Empty;
        IsClosed = view.ClosedAt is not null;
        Peers = view.Peers.Select(p => new UdpPeerItem(
            IpEndpointValue.Format(p.Remote.Address, p.Remote.Port),
            AddressClassifier.DisplayName(p.Scope),
            Fmt.Bytes(p.ReceivedBytes),
            Fmt.Bytes(p.SentBytes),
            Fmt.Time(p.LastSeen))).ToArray();
        OnPropertyChanged(nameof(AccessibleName));
    }
}

/// <summary>Observed UDP peer (display).</summary>
/// <param name="Endpoint">Remote endpoint.</param>
/// <param name="Scope">Scope.</param>
/// <param name="Received">Bytes received from it.</param>
/// <param name="Sent">Bytes sent to it.</param>
/// <param name="LastSeen">Last datagram time.</param>
public sealed record UdpPeerItem(string Endpoint, string Scope, string Received, string Sent, string LastSeen);
