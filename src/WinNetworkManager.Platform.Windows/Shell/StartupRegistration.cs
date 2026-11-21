using Microsoft.Win32;

namespace WinNetworkManager.Platform.Windows.Shell;

/// <summary>
/// Per-user "start with Windows" registration in <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
/// Only ever changed on explicit user request; never requires elevation.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name used in the Run key.</summary>
    public const string ValueName = "WinNetworkManager";

    /// <summary>True when a Run entry exists for this application.</summary>
    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Adds or removes the Run entry.</summary>
    public static void Set(bool enabled, string executablePath, string arguments = "--minimized")
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{executablePath}\" {arguments}".TrimEnd(), RegistryValueKind.String);
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
