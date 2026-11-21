using System.Runtime.InteropServices;
using WinNetworkManager.Platform.Windows.Interop;
using static WinNetworkManager.Platform.Windows.Interop.ProcessNative;

namespace WinNetworkManager.Platform.Windows.Processes;

/// <summary>A running Win32 service.</summary>
/// <param name="Name">Service (key) name, e.g. "Dnscache".</param>
/// <param name="DisplayName">Display name, e.g. "DNS Client".</param>
public sealed record ServiceInfo(string Name, string DisplayName);

/// <summary>Maps PIDs to the services they host (svchost.exe groups) using <c>EnumServicesStatusEx</c>.</summary>
public static unsafe class ServiceMap
{
    /// <summary>Reads running services grouped by hosting PID. Returns an empty map when access is denied.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<ServiceInfo>> Read()
    {
        nint manager = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (manager == 0)
        {
            return new Dictionary<int, IReadOnlyList<ServiceInfo>>();
        }

        try
        {
            uint needed = 0;
            uint returned = 0;
            uint resume = 0;
            uint size = 256 * 1024;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                byte* buffer = (byte*)NativeMemory.Alloc(size);
                try
                {
                    resume = 0;
                    bool ok = EnumServicesStatusEx(manager, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, buffer, size, &needed, &returned, &resume, null);
                    uint error = ok ? 0 : (uint)Marshal.GetLastPInvokeError();
                    if (!ok && error == Win32Error.ERROR_MORE_DATA)
                    {
                        size = needed + 16 * 1024;
                        continue;
                    }

                    if (!ok)
                    {
                        return new Dictionary<int, IReadOnlyList<ServiceInfo>>();
                    }

                    var map = new Dictionary<int, List<ServiceInfo>>();
                    var entries = (ENUM_SERVICE_STATUS_PROCESSW*)buffer;
                    for (int i = 0; i < returned; i++)
                    {
                        int pid = (int)entries[i].ServiceStatusProcess.dwProcessId;
                        if (pid == 0)
                        {
                            continue;
                        }

                        var info = new ServiceInfo(new string(entries[i].lpServiceName), new string(entries[i].lpDisplayName));
                        if (!map.TryGetValue(pid, out var list))
                        {
                            map[pid] = list = [];
                        }

                        list.Add(info);
                    }

                    return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ServiceInfo>)kv.Value.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray());
                }
                finally
                {
                    NativeMemory.Free(buffer);
                }
            }

            return new Dictionary<int, IReadOnlyList<ServiceInfo>>();
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }
}
