using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinNetworkManager.Core.Errors;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Platform.Windows.Interop;
using static WinNetworkManager.Platform.Windows.Interop.ProcessNative;

namespace WinNetworkManager.Platform.Windows.Processes;

/// <summary>Outcome of a request to end a process.</summary>
public enum TerminateOutcome
{
    /// <summary>The process was ended and has exited.</summary>
    Terminated,

    /// <summary>The process had already exited; nothing was done.</summary>
    AlreadyExited,

    /// <summary>The PID now belongs to a different process (Windows reused it); nothing was done.</summary>
    Replaced,

    /// <summary>Refused by WinNetworkManager: ending it would crash Windows, or it is this program or a kernel process.</summary>
    Refused,

    /// <summary>Windows denied access (another user's, a service's, an elevated or a protected process).</summary>
    AccessDenied,

    /// <summary>Termination was requested but the process had not exited within the wait.</summary>
    Pending,

    /// <summary>Other failure.</summary>
    Failed,
}

/// <summary>Result of <see cref="ProcessTerminator.Terminate"/>.</summary>
/// <param name="Outcome">Outcome.</param>
/// <param name="Message">User-facing explanation.</param>
public sealed record TerminateResult(TerminateOutcome Outcome, string Message)
{
    /// <summary>True when the process is gone (ended now or before).</summary>
    public bool IsGone => Outcome is TerminateOutcome.Terminated or TerminateOutcome.AlreadyExited;
}

/// <summary>
/// Ends a process instance safely. The process is opened once and its creation time is checked on that same handle
/// before <c>TerminateProcess</c>: while the handle is open Windows cannot reuse the PID, so a recycled PID can never
/// cause a different program to be ended. Critical system processes (whose termination crashes Windows) are refused.
/// </summary>
public static class ProcessTerminator
{
    /// <summary>Exit code given to ended processes (as Task Manager does).</summary>
    public const uint ExitCode = 1;

    /// <summary>Ends the process identified by <paramref name="key"/> and waits up to <paramref name="wait"/> for it to exit.</summary>
    public static unsafe TerminateResult Terminate(ProcessKey key, TimeSpan wait)
    {
        if (key.Pid is 0 or 4)
        {
            return new(TerminateOutcome.Refused, "The System and System Idle processes are part of the Windows kernel and cannot be ended.");
        }

        if (key.Pid == Environment.ProcessId)
        {
            return new(TerminateOutcome.Refused, "This is WinNetworkManager itself. Close it from the window or the notification area instead.");
        }

        if (!key.HasCreateTime)
        {
            return new(TerminateOutcome.Refused, "The identity of this process could not be verified (its creation time is not readable), so it is not ended: the PID might already belong to another program. Start WinNetworkManager as administrator to manage it.");
        }

        using SafeProcessHandle handle = OpenProcess(PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, (uint)key.Pid);
        if (handle.IsInvalid)
        {
            uint error = (uint)Marshal.GetLastPInvokeError();
            return error switch
            {
                Win32Error.ERROR_INVALID_PARAMETER => new(TerminateOutcome.AlreadyExited, "The process has already exited."),
                Win32Error.ERROR_ACCESS_DENIED => new(TerminateOutcome.AccessDenied, AccessDeniedText),
                _ => new(TerminateOutcome.Failed, "The process could not be opened: " + ErrorText.Win32((int)error)),
            };
        }

        long creation, exit, kernel, user;
        if (!GetProcessTimes(handle, &creation, &exit, &kernel, &user))
        {
            return new(TerminateOutcome.Failed, "The identity of the process could not be verified: " + ErrorText.Win32(Marshal.GetLastPInvokeError()));
        }

        if (creation != key.CreateTimeTicks)
        {
            return new(TerminateOutcome.Replaced, "The process has already exited and Windows has given its PID to another program, which was left running.");
        }

        if (WaitForSingleObject(handle, 0) == WAIT_OBJECT_0)
        {
            return new(TerminateOutcome.AlreadyExited, "The process has already exited.");
        }

        if (IsProcessCritical(handle, out bool critical) && critical)
        {
            return new(TerminateOutcome.Refused, "This is a critical Windows process: ending it would stop Windows immediately (blue screen). It was left running.");
        }

        if (!TerminateProcess(handle, ExitCode))
        {
            uint error = (uint)Marshal.GetLastPInvokeError();
            if (WaitForSingleObject(handle, 0) == WAIT_OBJECT_0)
            {
                return new(TerminateOutcome.AlreadyExited, "The process has already exited.");
            }

            return error == Win32Error.ERROR_ACCESS_DENIED
                ? new(TerminateOutcome.AccessDenied, AccessDeniedText)
                : new(TerminateOutcome.Failed, "The process could not be ended: " + ErrorText.Win32((int)error));
        }

        uint waited = WaitForSingleObject(handle, (uint)Math.Clamp(wait.TotalMilliseconds, 0, int.MaxValue));
        return waited == WAIT_OBJECT_0
            ? new(TerminateOutcome.Terminated, "The process was ended.")
            : new(TerminateOutcome.Pending, "Windows accepted the request, but the process has not exited yet (it may be waiting for a driver or a pending I/O). Check again in a moment.");
    }

    private const string AccessDeniedText =
        "Windows denied access. Processes of other users, services, elevated programs and protected processes can only be ended by an administrator: start WinNetworkManager as administrator to end it.";
}
