using System.Security.Principal;

namespace WinNetworkManager.Platform.Windows.Security;

/// <summary>How the current token relates to the Administrators group.</summary>
public enum ElevationKind : byte
{
    /// <summary>Standard user (or UAC disabled): elevation needs different credentials.</summary>
    StandardUser,

    /// <summary>Member of Administrators running with a filtered token: elevation is a consent prompt away.</summary>
    AdministratorNotElevated,

    /// <summary>Running elevated.</summary>
    Elevated,
}

/// <summary>Privilege facts about the current process, used to explain what is available and why.</summary>
/// <param name="Kind">Elevation state.</param>
/// <param name="IsPerformanceLogUser">Member of "Performance Log Users" (may control ETW sessions without elevation).</param>
/// <param name="UserName">DOMAIN\User of the current token.</param>
public sealed record PrivilegeInfo(ElevationKind Kind, bool IsPerformanceLogUser, string UserName)
{
    /// <summary>SID of the built-in "Performance Log Users" group.</summary>
    public const string PerformanceLogUsersSid = "S-1-5-32-559";

    /// <summary>True when the current process is elevated.</summary>
    public bool IsElevated => Kind == ElevationKind.Elevated;

    /// <summary>True when the process may be able to create ETW sessions itself.</summary>
    public bool CanLikelyControlEtw => IsElevated || IsPerformanceLogUser;

    /// <summary>Reads the current process token.</summary>
    public static PrivilegeInfo ReadCurrent()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        bool elevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
        bool adminSplitToken = !elevated && identity.Claims.Any(c =>
            c.Type == ClaimTypes.DenyOnlySid && c.Value == "S-1-5-32-544");
        bool perfLog = principal.IsInRole(new SecurityIdentifier(PerformanceLogUsersSid));

        var kind = elevated ? ElevationKind.Elevated
            : adminSplitToken ? ElevationKind.AdministratorNotElevated
            : ElevationKind.StandardUser;
        return new PrivilegeInfo(kind, perfLog, identity.Name);
    }

    /// <summary>SID string of the current user (used to secure the collector pipe).</summary>
    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current token has no user SID.");
    }

    private static class ClaimTypes
    {
        public const string DenyOnlySid = "http://schemas.microsoft.com/ws/2008/06/identity/claims/denyonlysid";
    }
}
