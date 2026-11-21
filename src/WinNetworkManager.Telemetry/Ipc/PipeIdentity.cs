using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WinNetworkManager.Telemetry.Ipc;

/// <summary>Pipe peer verification and pipe security descriptors.</summary>
internal static partial class PipeIdentity
{
    /// <summary>Prefix of collector pipe names (a random suffix is generated per launch).</summary>
    public const string PipeNamePrefix = "WinNetworkManager.Collector.";

    /// <summary>Generates an unguessable pipe name.</summary>
    public static string NewPipeName() => PipeNamePrefix + Guid.NewGuid().ToString("N");

    /// <summary>True for names produced by <see cref="NewPipeName"/>.</summary>
    public static bool IsValidPipeName(string name) =>
        name.StartsWith(PipeNamePrefix, StringComparison.Ordinal) &&
        name.Length == PipeNamePrefix.Length + 32 &&
        name.AsSpan(PipeNamePrefix.Length).ContainsAnyExcept("0123456789abcdef") == false;

    /// <summary>PID of the process at the other end of a server pipe.</summary>
    public static int? GetClientProcessId(SafePipeHandle handle) =>
        GetNamedPipeClientProcessId(handle, out uint pid) ? (int)pid : null;

    /// <summary>PID of the server of a client pipe.</summary>
    public static int? GetServerProcessId(SafePipeHandle handle) =>
        GetNamedPipeServerProcessId(handle, out uint pid) ? (int)pid : null;

    /// <summary>
    /// Security for the collector pipe: the launching user (who may differ from the elevated account) can read and
    /// write; the helper's own account has full control; network logons are denied.
    /// </summary>
    public static PipeSecurity CreateSecurity(SecurityIdentifier clientUser)
    {
        var security = new PipeSecurity();
        using var self = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(clientUser, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        if (self.User is not null)
        {
            security.AddAccessRule(new PipeAccessRule(self.User, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
