using System.Runtime.InteropServices;

namespace WinNetworkManager.Platform.Windows.Connectivity;

/// <summary>Connectivity as determined by Windows (NCSI probes), from the Network List Manager.</summary>
[Flags]
public enum ConnectivityFlags
{
    /// <summary>No connectivity.</summary>
    Disconnected = 0,

    /// <summary>IPv4: connected, no traffic seen.</summary>
    IPv4NoTraffic = 0x1,

    /// <summary>IPv6: connected, no traffic seen.</summary>
    IPv6NoTraffic = 0x2,

    /// <summary>IPv4 subnet reachable.</summary>
    IPv4Subnet = 0x10,

    /// <summary>IPv4 local network reachable.</summary>
    IPv4LocalNetwork = 0x20,

    /// <summary>IPv4 Internet reachable.</summary>
    IPv4Internet = 0x40,

    /// <summary>IPv6 subnet reachable.</summary>
    IPv6Subnet = 0x100,

    /// <summary>IPv6 local network reachable.</summary>
    IPv6LocalNetwork = 0x200,

    /// <summary>IPv6 Internet reachable.</summary>
    IPv6Internet = 0x400,
}

/// <summary>Summarized connectivity level.</summary>
public enum ConnectivityLevel : byte
{
    /// <summary>Could not be determined.</summary>
    Unknown,

    /// <summary>No network.</summary>
    None,

    /// <summary>Local network only (no Internet, or captive portal).</summary>
    LocalOnly,

    /// <summary>Internet access.</summary>
    Internet,
}

/// <summary>Result of a connectivity query.</summary>
/// <param name="Level">Summary.</param>
/// <param name="Flags">Raw flags.</param>
public readonly record struct ConnectivityStatus(ConnectivityLevel Level, ConnectivityFlags Flags)
{
    /// <summary>IPv4 Internet reachable.</summary>
    public bool HasIPv4Internet => (Flags & ConnectivityFlags.IPv4Internet) != 0;

    /// <summary>IPv6 Internet reachable.</summary>
    public bool HasIPv6Internet => (Flags & ConnectivityFlags.IPv6Internet) != 0;

    /// <summary>Converts raw flags.</summary>
    public static ConnectivityStatus FromFlags(ConnectivityFlags flags)
    {
        var level = (flags & (ConnectivityFlags.IPv4Internet | ConnectivityFlags.IPv6Internet)) != 0 ? ConnectivityLevel.Internet
            : (flags & (ConnectivityFlags.IPv4LocalNetwork | ConnectivityFlags.IPv6LocalNetwork | ConnectivityFlags.IPv4Subnet | ConnectivityFlags.IPv6Subnet)) != 0 ? ConnectivityLevel.LocalOnly
            : (flags & (ConnectivityFlags.IPv4NoTraffic | ConnectivityFlags.IPv6NoTraffic)) != 0 ? ConnectivityLevel.LocalOnly
            : ConnectivityLevel.None;
        return new ConnectivityStatus(level, flags);
    }
}

/// <summary>Queries <c>INetworkListManager::GetConnectivity</c>.</summary>
public static class NetworkListManager
{
    private static readonly Guid ClsidNetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    /// <summary>Reads the current connectivity (COM call; fast, safe from any thread).</summary>
    public static ConnectivityStatus GetConnectivity()
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidNetworkListManager, throwOnError: false);
            if (type is null)
            {
                return new ConnectivityStatus(ConnectivityLevel.Unknown, ConnectivityFlags.Disconnected);
            }

            instance = Activator.CreateInstance(type);
            if (instance is not INetworkListManager manager)
            {
                return new ConnectivityStatus(ConnectivityLevel.Unknown, ConnectivityFlags.Disconnected);
            }

            return ConnectivityStatus.FromFlags((ConnectivityFlags)manager.GetConnectivity());
        }
        catch (COMException)
        {
            return new ConnectivityStatus(ConnectivityLevel.Unknown, ConnectivityFlags.Disconnected);
        }
        catch (InvalidCastException)
        {
            return new ConnectivityStatus(ConnectivityLevel.Unknown, ConnectivityFlags.Disconnected);
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.ReleaseComObject(instance);
            }
        }
    }

    [ComImport]
    [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworks(int flags);

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetwork(Guid networkId);

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworkConnections();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworkConnection(Guid networkConnectionId);

        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }

        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }

        int GetConnectivity();
    }
}
