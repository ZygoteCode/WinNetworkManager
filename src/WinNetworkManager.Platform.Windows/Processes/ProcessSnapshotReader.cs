using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Platform.Windows.Interop;
using static WinNetworkManager.Platform.Windows.Interop.ProcessNative;

namespace WinNetworkManager.Platform.Windows.Processes;

/// <summary>One process from a system snapshot.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="ParentPid">Parent process id at creation (the parent may have exited or the PID may be reused).</param>
/// <param name="ImageName">Executable file name (e.g. "chrome.exe"); "System"/"[System Process]" for kernel entries.</param>
/// <param name="ThreadCount">Number of threads.</param>
/// <param name="CreateTime">Creation time (FILETIME UTC), or 0 when it could not be read.</param>
/// <param name="CreateTimeStatus">Why <paramref name="CreateTime"/> is missing, if it is.</param>
public sealed record ProcessSnapshotEntry(
    int Pid,
    int ParentPid,
    string ImageName,
    int ThreadCount,
    long CreateTime,
    InfoStatus CreateTimeStatus)
{
    /// <summary>Identity key of this instance.</summary>
    public ProcessKey Key => new(Pid, CreateTime);
}

/// <summary>Source of process snapshots (abstracted for engine tests).</summary>
public interface IProcessSnapshotSource
{
    /// <summary>Enumerates running processes.</summary>
    IReadOnlyList<ProcessSnapshotEntry> ReadProcesses();
}

/// <summary>
/// Enumerates processes with Toolhelp32 and reads each creation time with
/// <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> + <c>GetProcessTimes</c>. Creation times of known PIDs are
/// re-read every pass so a recycled PID is always detected.
/// </summary>
public sealed unsafe class ProcessSnapshotReader : IProcessSnapshotSource
{
    /// <summary>Approximate boot time (FILETIME UTC), for display only — never used in identity keys.</summary>
    public static long ApproximateBootTimeFileTime { get; } = ComputeBootTime();

    /// <inheritdoc />
    public IReadOnlyList<ProcessSnapshotEntry> ReadProcesses()
    {
        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            throw NativeCallException.FromLastError("CreateToolhelp32Snapshot");
        }

        var result = new List<ProcessSnapshotEntry>(512);
        var entry = default(PROCESSENTRY32W);
        entry.dwSize = (uint)sizeof(PROCESSENTRY32W);
        if (!Process32First(snapshot, &entry))
        {
            uint error = (uint)Marshal.GetLastPInvokeError();
            if (error == Win32Error.ERROR_NO_MORE_FILES)
            {
                return result;
            }

            throw new NativeCallException("Process32First", error);
        }

        do
        {
            int pid = (int)entry.th32ProcessID;
            string name = new(entry.szExeFile);
            // The Idle pseudo-process has no creation time. Every other PID (System included) is read the same way in
            // every process of the product, so collector and UI derive identical keys whenever access allows.
            (long createTime, InfoStatus status) = pid == WellKnownPids.Idle
                ? (0L, InfoStatus.NotApplicable)
                : ReadCreateTime(pid);

            result.Add(new ProcessSnapshotEntry(pid, (int)entry.th32ParentProcessID, name, (int)entry.cntThreads, createTime, status));
            entry.dwSize = (uint)sizeof(PROCESSENTRY32W);
        }
        while (Process32Next(snapshot, &entry));

        return result;
    }

    /// <summary>Reads the creation time of a process.</summary>
    public static (long CreateTime, InfoStatus Status) ReadCreateTime(int pid)
    {
        using SafeProcessHandle handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsInvalid)
        {
            uint error = (uint)Marshal.GetLastPInvokeError();
            return (0, error switch
            {
                Win32Error.ERROR_ACCESS_DENIED => InfoStatus.AccessDenied,
                Win32Error.ERROR_INVALID_PARAMETER => InfoStatus.NotFound,
                _ => InfoStatus.Failed,
            });
        }

        long creation, exit, kernel, user;
        if (!GetProcessTimes(handle, &creation, &exit, &kernel, &user))
        {
            return (0, InfoStatus.Failed);
        }

        return (creation, InfoStatus.Available);
    }

    private static long ComputeBootTime()
    {
        ulong uptimeMs = GetTickCount64();
        return DateTime.UtcNow.ToFileTimeUtc() - ((long)uptimeMs * TimeSpan.TicksPerMillisecond);
    }

    internal static int EntrySize => sizeof(PROCESSENTRY32W);
}
