using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinNetworkManager.Platform.Windows.Shell;

/// <summary>System backdrop materials (Windows 11 22H2+).</summary>
public enum BackdropKind
{
    /// <summary>No system backdrop.</summary>
    None = 1,

    /// <summary>Mica (main windows).</summary>
    Mica = 2,

    /// <summary>Acrylic (transient surfaces).</summary>
    Acrylic = 3,

    /// <summary>Mica Alt (tabbed windows).</summary>
    MicaAlt = 4,
}

/// <summary>DWM window attributes: dark title bar, rounded corners, system backdrop.</summary>
public static partial class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;

    /// <summary>True on Windows 11 22H2 (build 22621) and later, where system backdrops are supported.</summary>
    public static bool SupportsSystemBackdrop => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Switches the title bar between light and dark.</summary>
    public static void SetDarkTitleBar(nint hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    /// <summary>Requests rounded corners (Windows 11).</summary>
    public static void SetRoundedCorners(nint hwnd)
    {
        int value = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }

    /// <summary>Applies a system backdrop. Returns false when unsupported (caller keeps a solid background).</summary>
    public static bool TrySetBackdrop(nint hwnd, BackdropKind kind)
    {
        if (!SupportsSystemBackdrop)
        {
            return false;
        }

        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int value = (int)kind;
        return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int)) == 0;
    }

    /// <summary>Reads whether apps should use the light theme (Settings → Personalization → Colors).</summary>
    public static bool AppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    /// <summary>Reads the Windows accent colour as (R, G, B), or null when unavailable.</summary>
    public static (byte R, byte G, byte B)? ReadAccentColor()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is int abgr)
        {
            uint value = unchecked((uint)abgr);
            return ((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
        }

        return null;
    }

    /// <summary>Reads the Windows "Text size" accessibility factor (1.0–2.25).</summary>
    public static double ReadTextScaleFactor()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
        return key?.GetValue("TextScaleFactor") is int percent && percent is >= 100 and <= 225 ? percent / 100.0 : 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
