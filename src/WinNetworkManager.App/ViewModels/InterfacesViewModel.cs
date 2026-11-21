using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNetworkManager.App.Services;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Platform.Windows.IpHelper;
using WinNetworkManager.UI.Controls;

namespace WinNetworkManager.App.ViewModels;

/// <summary>One address line of the interface details.</summary>
/// <param name="Address">Address with prefix.</param>
/// <param name="Kind">Scope and assignment.</param>
public sealed record AddressLine(string Address, string Kind);

/// <summary>An interface in the list, with its details.</summary>
public sealed partial class InterfaceItem : ObservableObject
{
    /// <summary>Creates an item.</summary>
    public InterfaceItem(Guid id) => Id = id;

    /// <summary>Interface GUID.</summary>
    public Guid Id { get; }

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Description { get; set; } = string.Empty;
    [ObservableProperty] public partial string TypeName { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsUp { get; set; }
    [ObservableProperty] public partial bool IsDefault { get; set; }
    [ObservableProperty] public partial string DefaultText { get; set; } = string.Empty;
    [ObservableProperty] public partial string Glyph { get; set; } = UI.Glyphs.Ethernet;
    [ObservableProperty] public partial string RateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DownRate { get; set; } = string.Empty;
    [ObservableProperty] public partial string UpRate { get; set; } = string.Empty;
    [ObservableProperty] public partial string PrimaryAddress { get; set; } = string.Empty;
    [ObservableProperty] public partial IReadOnlyList<float> Spark { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ChartSeries> Series { get; set; } = [];
    [ObservableProperty] public partial string Mac { get; set; } = string.Empty;
    [ObservableProperty] public partial string Mtu { get; set; } = string.Empty;
    [ObservableProperty] public partial string LinkSpeed { get; set; } = string.Empty;
    [ObservableProperty] public partial string Kind { get; set; } = string.Empty;
    [ObservableProperty] public partial IReadOnlyList<AddressLine> Addresses { get; set; } = [];
    [ObservableProperty] public partial string Gateways { get; set; } = string.Empty;
    [ObservableProperty] public partial string DnsServers { get; set; } = string.Empty;
    [ObservableProperty] public partial string DnsSuffix { get; set; } = string.Empty;
    [ObservableProperty] public partial string Dhcp { get; set; } = string.Empty;
    [ObservableProperty] public partial string SessionTotals { get; set; } = string.Empty;
    [ObservableProperty] public partial string Errors { get; set; } = string.Empty;
    [ObservableProperty] public partial string IndexText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CounterReset { get; set; }

    /// <summary>Accessible summary for the list item.</summary>
    public string AccessibleName => $"{Name}, {TypeName}, {StatusText}{(IsDefault ? ", default route" : string.Empty)}, {RateText}";

    /// <summary>Raw view.</summary>
    public InterfaceView? View { get; private set; }

    /// <summary>Updates from a view.</summary>
    public void Update(InterfaceView v)
    {
        View = v;
        Name = v.Name;
        Description = v.Description;
        TypeName = v.TypeName + (v.IsHardware ? string.Empty : " (virtual)");
        IsUp = v.Status == InterfaceOperationalStatus.Up;
        StatusText = v.Status switch
        {
            InterfaceOperationalStatus.Up => "Connected",
            InterfaceOperationalStatus.Down => "Disconnected",
            InterfaceOperationalStatus.Dormant => "Dormant",
            InterfaceOperationalStatus.LowerLayerDown => "Cable/radio down",
            InterfaceOperationalStatus.NotPresent => "Not present",
            InterfaceOperationalStatus.Testing => "Testing",
            _ => "Unknown",
        };
        IsDefault = v.IsDefaultIPv4 || v.IsDefaultIPv6;
        DefaultText = (v.IsDefaultIPv4, v.IsDefaultIPv6) switch
        {
            (true, true) => "Default route (IPv4 + IPv6)",
            (true, false) => "Default route (IPv4)",
            (false, true) => "Default route (IPv6)",
            _ => string.Empty,
        };
        Glyph = v.TypeName.StartsWith("Wi-Fi", StringComparison.Ordinal) ? UI.Glyphs.Wifi : UI.Glyphs.Ethernet;
        DownRate = v.ReceiveRate is { } rx ? Fmt.Rate(rx) : Fmt.NotAvailable;
        UpRate = v.SendRate is { } tx ? Fmt.Rate(tx) : Fmt.NotAvailable;
        RateText = $"↓ {DownRate}  ↑ {UpRate}";
        PrimaryAddress = v.Addresses.FirstOrDefault(a => a.Address.IsIPv4)?.Address.ToString()
            ?? v.Addresses.FirstOrDefault(a => AddressClassifier.Classify(a.Address) != AddressScope.LinkLocal)?.Address.ToString()
            ?? string.Empty;
        var combined = new float[Math.Max(v.ReceiveHistory.Count, v.SendHistory.Count)];
        for (int i = 0; i < combined.Length; i++)
        {
            float r = i < v.ReceiveHistory.Count ? v.ReceiveHistory[i] : float.NaN;
            float s = i < v.SendHistory.Count ? v.SendHistory[i] : float.NaN;
            combined[i] = float.IsNaN(r) && float.IsNaN(s) ? float.NaN : (float.IsNaN(r) ? 0 : r) + (float.IsNaN(s) ? 0 : s);
        }

        Spark = combined;
        Series =
        [
            new ChartSeries { Name = "↓ Received", Values = v.ReceiveHistory.Select(x => (double)x).ToArray(), Stroke = Brush("Brush.Data.Download"), Fill = Brush("Brush.Data.DownloadFill") },
            new ChartSeries { Name = "↑ Sent", Values = v.SendHistory.Select(x => (double)x).ToArray(), Stroke = Brush("Brush.Data.Upload"), Fill = Brush("Brush.Data.UploadFill"), Dashed = true },
        ];
        Mac = v.MacAddress ?? Fmt.NotAvailable;
        Mtu = v.Mtu is { } mtu ? $"{mtu} bytes" : Fmt.NotAvailable;
        LinkSpeed = v.ReceiveLinkSpeed == v.TransmitLinkSpeed
            ? Fmt.LinkSpeed(v.ReceiveLinkSpeed)
            : $"↓ {Fmt.LinkSpeed(v.ReceiveLinkSpeed)} / ↑ {Fmt.LinkSpeed(v.TransmitLinkSpeed)}";
        Kind = v.IsHardware ? "Hardware adapter" : "Virtual / software adapter";
        Addresses = v.Addresses
            .OrderBy(a => a.Address.IsIPv6)
            .Select(a => new AddressLine($"{a.Address}/{a.PrefixLength}", $"{AddressClassifier.DisplayName(AddressClassifier.Classify(a.Address))} · {a.Origin}"))
            .ToArray();
        Gateways = Join(v.Gateways);
        DnsServers = Join(v.DnsServers);
        DnsSuffix = string.IsNullOrEmpty(v.DnsSuffix) ? Fmt.NotAvailable : v.DnsSuffix;
        Dhcp = v.DhcpEnabled ? (v.DhcpServers.Count > 0 ? $"Enabled · server {Join(v.DhcpServers)}" : "Enabled") : "Disabled (static configuration)";
        SessionTotals = $"↓ {Fmt.Bytes(v.SessionReceived)}   ↑ {Fmt.Bytes(v.SessionSent)}";
        Errors = $"In {Fmt.Count(v.InErrors)} · Out {Fmt.Count(v.OutErrors)} (errors + discards since boot)";
        IndexText = $"Index {v.Index} · LUID 0x{v.Luid:X}";
        CounterReset = v.CounterReset;
        OnPropertyChanged(nameof(AccessibleName));
    }

    private static string Join(IReadOnlyList<IpAddressValue> addresses) =>
        addresses.Count == 0 ? Fmt.NotAvailable : string.Join(", ", addresses.Select(a => a.ToString()));

    private static Brush? Brush(string key) => Application.Current.TryFindResource(key) as Brush;
}

/// <summary>Network interfaces page.</summary>
public sealed partial class InterfacesViewModel : PageViewModel
{
    private readonly Dictionary<Guid, InterfaceItem> _items = [];

    /// <inheritdoc />
    public override string Title => "Interfaces";

    /// <inheritdoc />
    public override PageKind Kind => PageKind.Interfaces;

    /// <summary>Interfaces shown.</summary>
    public ObservableCollection<InterfaceItem> Items { get; } = [];

    [ObservableProperty] public partial InterfaceItem? Selected { get; set; }
    [ObservableProperty] public partial bool ShowInactive { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = string.Empty;
    [ObservableProperty] public partial Core.Units.DataUnitSystem Units { get; set; }

    private EngineSnapshot? _snapshot;

    partial void OnShowInactiveChanged(bool value)
    {
        if (_snapshot is not null)
        {
            OnSnapshot(_snapshot);
        }
    }

    [RelayCommand]
    private void CopyDetails()
    {
        if (Selected is not { } s)
        {
            return;
        }

        var text = $"{s.Name} — {s.Description}\nType: {s.TypeName}\nStatus: {s.StatusText}\nMAC: {s.Mac}\nMTU: {s.Mtu}\nLink speed: {s.LinkSpeed}\n" +
                   string.Join('\n', s.Addresses.Select(a => $"Address: {a.Address} ({a.Kind})")) +
                   $"\nGateway: {s.Gateways}\nDNS: {s.DnsServers}\nDNS suffix: {s.DnsSuffix}\nDHCP: {s.Dhcp}";
        ShellActions.Copy(text);
    }

    /// <inheritdoc />
    protected override void OnSnapshot(EngineSnapshot snapshot)
    {
        _snapshot = snapshot;
        Units = Fmt.Units;
        var visible = snapshot.Interfaces.Where(i => ShowInactive || IsShownByDefault(i)).ToList();
        var ids = visible.Select(v => v.Id).ToHashSet();
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Items[i].Id))
            {
                Items.RemoveAt(i);
            }
        }

        for (int i = 0; i < visible.Count; i++)
        {
            var view = visible[i];
            if (!_items.TryGetValue(view.Id, out var item))
            {
                item = new InterfaceItem(view.Id);
                _items[view.Id] = item;
            }

            item.Update(view);
            int current = Items.IndexOf(item);
            if (current < 0)
            {
                Items.Insert(Math.Min(i, Items.Count), item);
            }
            else if (current != i && i < Items.Count)
            {
                Items.Move(current, i);
            }
        }

        Selected ??= Items.FirstOrDefault();
        int up = snapshot.Interfaces.Count(i => i.Status == InterfaceOperationalStatus.Up);
        Summary = $"{snapshot.Interfaces.Count} interfaces · {up} connected · showing {Items.Count}";
    }

    /// <summary>
    /// Default list: connected interfaces that carry IP traffic. Loopback, disconnected adapters and virtual helper
    /// interfaces without any IP address (WAN miniports, virtual switch ports, kernel debugger) appear only with
    /// "Show all interfaces", like the hidden adapters of the Windows network settings.
    /// </summary>
    internal static bool IsShownByDefault(InterfaceView view) =>
        view.Status == InterfaceOperationalStatus.Up &&
        view.TypeName != "Loopback" &&
        (view.IsHardware || view.Addresses.Count > 0);
}
