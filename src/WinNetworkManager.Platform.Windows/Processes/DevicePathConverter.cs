using System.Runtime.InteropServices;

namespace WinNetworkManager.Platform.Windows.Processes;

/// <summary>
/// Converts NT device paths (<c>\Device\HarddiskVolume3\Windows\explorer.exe</c>, as reported by kernel ETW
/// providers) to Win32 paths (<c>C:\Windows\explorer.exe</c>) using <c>QueryDosDevice</c>.
/// </summary>
public static partial class DevicePathConverter
{
    private static readonly object Gate = new();
    private static Dictionary<string, string> _map = Build();
    private static DateTime _builtAt = DateTime.UtcNow;

    /// <summary>Converts a path; returns the input unchanged when no drive maps to its device.</summary>
    public static string ToWin32Path(string ntPath)
    {
        ArgumentNullException.ThrowIfNull(ntPath);
        if (ntPath.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return ntPath[4..];
        }

        if (!ntPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return ntPath;
        }

        if (TryMap(Snapshot(), ntPath, out string? result))
        {
            return result;
        }

        // A volume may have been mounted since the map was built: rebuild at most every 5 seconds.
        lock (Gate)
        {
            if (DateTime.UtcNow - _builtAt > TimeSpan.FromSeconds(5))
            {
                _map = Build();
                _builtAt = DateTime.UtcNow;
            }
        }

        return TryMap(Snapshot(), ntPath, out result) ? result : ntPath;
    }

    private static Dictionary<string, string> Snapshot()
    {
        lock (Gate)
        {
            return _map;
        }
    }

    private static bool TryMap(Dictionary<string, string> map, string ntPath, out string result)
    {
        foreach (var (device, drive) in map)
        {
            if (ntPath.Length > device.Length && ntPath[device.Length] == '\\' &&
                ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            {
                result = drive + ntPath[device.Length..];
                return true;
            }
        }

        result = ntPath;
        return false;
    }

    private static unsafe Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        char* target = stackalloc char[1024];
        foreach (string drive in Environment.GetLogicalDrives())
        {
            string name = drive.TrimEnd('\\'); // "C:"
            if (QueryDosDevice(name, target, 1024) > 0)
            {
                string device = new(target); // first string of the multi-string
                if (!string.IsNullOrEmpty(device))
                {
                    map[device] = name;
                }
            }
        }

        return map;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint QueryDosDevice(string deviceName, char* targetPath, uint max);
}
