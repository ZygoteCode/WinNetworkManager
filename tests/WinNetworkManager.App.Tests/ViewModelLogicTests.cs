using System.Collections.Specialized;
using System.Globalization;
using WinNetworkManager.App.Common;
using WinNetworkManager.App.ViewModels;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Platform.Windows.IpHelper;

namespace WinNetworkManager.App.Tests;

public sealed class RowSynchronizerTests
{
    private sealed class Row(string id)
    {
        public string Id { get; } = id;
    }

    private static (RowSynchronizer<string, Row> Sync, List<Row> Rows) Create(int count)
    {
        var sync = new RowSynchronizer<string, Row>();
        var rows = Enumerable.Range(0, count).Select(i => sync.GetOrCreate("r" + i, () => new Row("r" + i))).ToList();
        sync.Apply(rows);
        return (sync, rows);
    }

    private static Dictionary<NotifyCollectionChangedAction, int> Record(RowSynchronizer<string, Row> sync)
    {
        var counts = new Dictionary<NotifyCollectionChangedAction, int>();
        sync.Items.CollectionChanged += (_, e) => counts[e.Action] = counts.GetValueOrDefault(e.Action) + 1;
        return counts;
    }

    [Fact]
    public void Small_changes_are_applied_in_place_so_selection_and_scroll_survive()
    {
        var (sync, rows) = Create(100);
        var events = Record(sync);
        var desired = rows.ToList();
        (desired[3], desired[7]) = (desired[7], desired[3]);
        desired.RemoveAt(50);
        desired.Insert(10, sync.GetOrCreate("new", () => new Row("new")));

        sync.Apply(desired);

        Assert.Equal(desired, sync.Items);
        Assert.False(events.ContainsKey(NotifyCollectionChangedAction.Reset));
        Assert.True(events.Values.Sum() <= 8, $"{events.Values.Sum()} change notifications");
    }

    [Fact]
    public void A_full_reorder_uses_one_reset_instead_of_many_moves()
    {
        var (sync, rows) = Create(100);
        var events = Record(sync);
        var reversed = Enumerable.Reverse(rows).ToList();

        sync.Apply(reversed);

        Assert.Equal(reversed, sync.Items);
        Assert.Equal(1, events.GetValueOrDefault(NotifyCollectionChangedAction.Reset));
        Assert.Single(events);
    }

    [Fact]
    public void Rows_keep_their_identity_and_retain_forgets_dead_keys()
    {
        var (sync, rows) = Create(10);
        Assert.Same(rows[4], sync.GetOrCreate("r4", () => new Row("other")));
        sync.Retain(new HashSet<string> { "r1", "r2" });
        Assert.Equal(2, sync.Rows.Count);
        Assert.NotSame(rows[4], sync.GetOrCreate("r4", () => new Row("r4")));
    }
}

public sealed class PageLogicTests
{
    [Fact]
    public void History_averaging_ignores_gaps_and_keeps_empty_groups_as_gaps()
    {
        double[] values = [1, 3, double.NaN, double.NaN, 5, double.NaN, 7];
        var averaged = HistoryViewModel.Average(values, 2);
        Assert.Equal(4, averaged.Length);
        Assert.Equal(2, averaged[0]);
        Assert.True(double.IsNaN(averaged[1]));
        Assert.Equal(5, averaged[2]);
        Assert.Equal(7, averaged[3]);
        Assert.Same(values, HistoryViewModel.Average(values, 1));
    }

    private static InterfaceView Nic(string name, InterfaceOperationalStatus status, bool hardware, string type = "Ethernet", params string[] addresses) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = name,
        TypeName = type,
        IsHardware = hardware,
        Status = status,
        Addresses = addresses.Select(a => new InterfaceAddressView(IpAddressValue.TryParse(a, out var ip) ? ip : default, 24, "Dhcp")).ToArray(),
    };

    [Fact]
    public void Interface_list_hides_helper_interfaces_by_default()
    {
        Assert.True(InterfacesViewModel.IsShownByDefault(Nic("Wi-Fi", InterfaceOperationalStatus.Up, hardware: true)));
        Assert.True(InterfacesViewModel.IsShownByDefault(Nic("vEthernet (Default Switch)", InterfaceOperationalStatus.Up, hardware: false, "Ethernet", "172.25.32.1")));
        Assert.False(InterfacesViewModel.IsShownByDefault(Nic("WAN Miniport (IP)", InterfaceOperationalStatus.Up, hardware: false)));
        Assert.False(InterfacesViewModel.IsShownByDefault(Nic("Ethernet", InterfaceOperationalStatus.Down, hardware: true)));
        Assert.False(InterfacesViewModel.IsShownByDefault(Nic("Loopback Pseudo-Interface 1", InterfaceOperationalStatus.Up, hardware: false, "Loopback", "127.0.0.1")));
    }

    [Fact]
    public void Millisecond_text_uses_one_decimal_below_ten()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal("4.2 ms", DiagnosticsViewModel.Ms(4.24));
            Assert.Equal("37 ms", DiagnosticsViewModel.Ms(37.4));
            Assert.Equal("—", DiagnosticsViewModel.Ms(null));
            Assert.Equal("cached", DiagnosticsViewModel.Ms(null, "cached"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
