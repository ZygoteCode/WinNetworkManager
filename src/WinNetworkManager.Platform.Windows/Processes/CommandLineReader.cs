using System.Management;
using WinNetworkManager.Core.Processes;

namespace WinNetworkManager.Platform.Windows.Processes;

/// <summary>
/// Reads a process command line through WMI (<c>Win32_Process.CommandLine</c>), the documented way to obtain it.
/// Windows only returns command lines of processes the caller is allowed to query (other users' and protected
/// processes need elevation); in that case the status is <see cref="InfoStatus.AccessDenied"/>.
/// </summary>
public static class CommandLineReader
{
    /// <summary>Reads the command line of <paramref name="pid"/> (blocking; call from a background thread).</summary>
    public static Probe<string> Read(int pid, TimeSpan timeout)
    {
        if (WellKnownPids.IsKernel(pid))
        {
            return Probe<string>.Missing(InfoStatus.NotApplicable);
        }

        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = timeout, ReturnImmediately = false, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}"),
                options);
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    return item["CommandLine"] is string line && line.Length > 0
                        ? Probe<string>.Of(line)
                        : Probe<string>.Missing(InfoStatus.AccessDenied);
                }
            }

            return Probe<string>.Missing(InfoStatus.NotFound);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return Probe<string>.Missing(InfoStatus.AccessDenied);
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or TimeoutException)
        {
            return Probe<string>.Missing(InfoStatus.Failed);
        }
    }
}
