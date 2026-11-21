using System.Collections.Frozen;
using System.Net.NetworkInformation;
using WinNetworkManager.Core.Networking;

namespace WinNetworkManager.Telemetry.Collection;

/// <summary>
/// The machine's own unicast addresses. Traffic whose remote address is one of them never leaves the machine
/// (Windows loops it back internally), so it is accounted as loopback. Thread-safe: readers see an immutable set.
/// </summary>
internal sealed class LocalAddressSet : IDisposable
{
    private FrozenSet<IpAddressValue> _addresses = FrozenSet<IpAddressValue>.Empty;
    private bool _subscribed;

    /// <summary>Current number of addresses.</summary>
    public int Count => Volatile.Read(ref _addresses).Count;

    /// <summary>Loads the addresses and follows address changes.</summary>
    public void Start()
    {
        Refresh();
        if (!_subscribed)
        {
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
            _subscribed = true;
        }
    }

    /// <summary>Replaces the set (tests).</summary>
    public void Set(IEnumerable<IpAddressValue> addresses) =>
        Volatile.Write(ref _addresses, addresses.Select(a => a.WithoutScope()).ToFrozenSet());

    /// <summary>True when <paramref name="remote"/> is a loopback address or one of this machine's addresses.</summary>
    public bool IsLocalMachine(IpAddressValue remote)
    {
        if (AddressClassifier.IsLoopback(remote))
        {
            return true;
        }

        var normalized = remote.UnmapIPv4().WithoutScope();
        return !normalized.IsUnspecified && Volatile.Read(ref _addresses).Contains(normalized);
    }

    /// <summary>Re-reads the addresses.</summary>
    public void Refresh()
    {
        var set = new HashSet<IpAddressValue>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    set.Add(IpAddressValue.FromIPAddress(unicast.Address).WithoutScope());
                }
            }
        }
        catch (NetworkInformationException)
        {
            return; // keep the previous set
        }

        Volatile.Write(ref _addresses, set.ToFrozenSet());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_subscribed)
        {
            NetworkChange.NetworkAddressChanged -= OnAddressChanged;
            _subscribed = false;
        }
    }

    private void OnAddressChanged(object? sender, EventArgs e) => Refresh();
}
