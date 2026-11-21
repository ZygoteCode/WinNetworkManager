using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Platform.Windows.Interop;
using static WinNetworkManager.Platform.Windows.Interop.ProcessNative;

namespace WinNetworkManager.Platform.Windows.Processes;

/// <summary>CPU and memory usage of a process at one instant.</summary>
/// <param name="KernelTime">Cumulative kernel CPU time.</param>
/// <param name="UserTime">Cumulative user CPU time.</param>
/// <param name="WorkingSetBytes">Working set.</param>
/// <param name="PrivateBytes">Private committed bytes.</param>
/// <param name="HandleCount">Open handles, if readable.</param>
public readonly record struct ProcessResourceUsage(TimeSpan KernelTime, TimeSpan UserTime, long WorkingSetBytes, long PrivateBytes, int? HandleCount)
{
    /// <summary>Total CPU time.</summary>
    public TimeSpan TotalCpuTime => KernelTime + UserTime;
}

/// <summary>Identity information from the process token.</summary>
/// <param name="UserName">DOMAIN\User.</param>
/// <param name="UserSid">User SID string.</param>
/// <param name="IsElevated">Token is elevated.</param>
/// <param name="IntegrityLevel">Integrity level name (Low, Medium, High, System, …).</param>
public sealed record ProcessTokenInfo(string UserName, string UserSid, bool IsElevated, string IntegrityLevel)
{
    /// <summary>True for LocalSystem, LocalService and NetworkService.</summary>
    public bool IsServiceAccount => UserSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20";
}

/// <summary>Reads details of a single process. Every method reports why a value is missing.</summary>
public static unsafe class ProcessDetailsReader
{
    /// <summary>Full Win32 path of the executable.</summary>
    public static Probe<string> GetImagePath(int pid)
    {
        if (WellKnownPids.IsKernel(pid))
        {
            return Probe<string>.Missing(InfoStatus.NotApplicable);
        }

        using var handle = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION, out InfoStatus status);
        if (handle is null)
        {
            return Probe<string>.Missing(status);
        }

        const int capacity = 32_768;
        char* buffer = stackalloc char[capacity];
        uint size = capacity;
        if (!QueryFullProcessImageName(handle, 0, buffer, &size))
        {
            // Minimal processes (Registry, Memory Compression, Secure System) have no image file.
            uint error = (uint)Marshal.GetLastPInvokeError();
            return Probe<string>.Missing(error == Win32Error.ERROR_ACCESS_DENIED ? InfoStatus.AccessDenied : InfoStatus.NotApplicable);
        }

        return Probe<string>.Of(new string(buffer, 0, (int)size));
    }

    /// <summary>CPU times and memory counters.</summary>
    public static Probe<ProcessResourceUsage> GetResourceUsage(int pid)
    {
        using var handle = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION, out InfoStatus status);
        if (handle is null)
        {
            return Probe<ProcessResourceUsage>.Missing(status);
        }

        long creation, exit, kernel, user;
        if (!GetProcessTimes(handle, &creation, &exit, &kernel, &user))
        {
            return Probe<ProcessResourceUsage>.Missing(InfoStatus.Failed);
        }

        var memory = default(PROCESS_MEMORY_COUNTERS_EX);
        memory.cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX);
        bool hasMemory = GetProcessMemoryInfo(handle, &memory, memory.cb);
        uint handles;
        int? handleCount = GetProcessHandleCount(handle, &handles) ? (int)handles : null;

        return Probe<ProcessResourceUsage>.Of(new ProcessResourceUsage(
            TimeSpan.FromTicks(kernel),
            TimeSpan.FromTicks(user),
            hasMemory ? (long)memory.WorkingSetSize : -1,
            hasMemory ? (long)memory.PrivateUsage : -1,
            handleCount));
    }

    /// <summary>Terminal Services session of the process (0 = services).</summary>
    public static Probe<int> GetSessionId(int pid)
    {
        uint session;
        if (ProcessIdToSessionId((uint)pid, &session))
        {
            return Probe<int>.Of((int)session);
        }

        uint error = (uint)Marshal.GetLastPInvokeError();
        return Probe<int>.Missing(error == Win32Error.ERROR_ACCESS_DENIED ? InfoStatus.AccessDenied : InfoStatus.Failed);
    }

    /// <summary>User, elevation and integrity of the process token.</summary>
    public static Probe<ProcessTokenInfo> GetTokenInfo(int pid)
    {
        using var handle = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION, out InfoStatus status);
        if (handle is null)
        {
            return Probe<ProcessTokenInfo>.Missing(status);
        }

        if (!OpenProcessToken(handle, TOKEN_QUERY, out SafeAccessTokenHandleEx token))
        {
            token.Dispose();
            uint error = (uint)Marshal.GetLastPInvokeError();
            return Probe<ProcessTokenInfo>.Missing(error == Win32Error.ERROR_ACCESS_DENIED ? InfoStatus.AccessDenied : InfoStatus.Failed);
        }

        using (token)
        {
            byte* buffer = stackalloc byte[512];
            uint length;
            string sid = string.Empty;
            string user = string.Empty;
            if (GetTokenInformation(token, TokenUser, buffer, 512, &length))
            {
                nint sidPointer = *(nint*)buffer; // TOKEN_USER.User.Sid
                var identifier = new SecurityIdentifier(sidPointer);
                sid = identifier.Value;
                user = TranslateSid(identifier);
            }

            bool elevated = false;
            uint elevation;
            if (GetTokenInformation(token, TokenElevation, &elevation, sizeof(uint), &length))
            {
                elevated = elevation != 0;
            }

            string integrity = "Unknown";
            if (GetTokenInformation(token, TokenIntegrityLevel, buffer, 512, &length))
            {
                nint labelSid = *(nint*)buffer; // TOKEN_MANDATORY_LABEL.Label.Sid
                integrity = IntegrityName(new SecurityIdentifier(labelSid).Value);
            }

            return Probe<ProcessTokenInfo>.Of(new ProcessTokenInfo(user, sid, elevated, integrity));
        }
    }

    private static SafeProcessHandle? Open(int pid, uint access, out InfoStatus status)
    {
        var handle = OpenProcess(access, false, (uint)pid);
        if (!handle.IsInvalid)
        {
            status = InfoStatus.Available;
            return handle;
        }

        uint error = (uint)Marshal.GetLastPInvokeError();
        handle.Dispose();
        status = error switch
        {
            Win32Error.ERROR_ACCESS_DENIED => InfoStatus.AccessDenied,
            Win32Error.ERROR_INVALID_PARAMETER => InfoStatus.NotFound,
            _ => InfoStatus.Failed,
        };
        return null;
    }

    /// <summary>
    /// Account name of <paramref name="sid"/>. Windows localizes the names of built-in accounts (for example
    /// "NT AUTHORITY\SERVIZIO DI RETE" on Italian systems), so the well-known service accounts use their English names.
    /// </summary>
    internal static string TranslateSid(SecurityIdentifier sid)
    {
        switch (sid.Value)
        {
            case "S-1-5-18":
                return @"NT AUTHORITY\SYSTEM";
            case "S-1-5-19":
                return @"NT AUTHORITY\LOCAL SERVICE";
            case "S-1-5-20":
                return @"NT AUTHORITY\NETWORK SERVICE";
        }

        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
        catch (SystemException)
        {
            return sid.Value;
        }
    }

    private static string IntegrityName(string sid) => sid switch
    {
        "S-1-16-0" => "Untrusted",
        "S-1-16-4096" => "Low",
        "S-1-16-8192" => "Medium",
        "S-1-16-8448" => "Medium Plus",
        "S-1-16-12288" => "High",
        "S-1-16-16384" => "System",
        "S-1-16-20480" => "Protected",
        _ => sid,
    };
}
