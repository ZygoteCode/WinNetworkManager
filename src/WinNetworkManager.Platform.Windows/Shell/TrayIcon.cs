using System.Runtime.InteropServices;

namespace WinNetworkManager.Platform.Windows.Shell;

/// <summary>What happened on the notification-area icon.</summary>
public enum TrayIconEvent
{
    /// <summary>Nothing relevant.</summary>
    None,

    /// <summary>Left click, or Enter/Space on the focused icon.</summary>
    Activate,

    /// <summary>Right click, Shift+F10 or the context-menu key.</summary>
    ContextMenu,

    /// <summary>The user clicked a notification shown by this icon.</summary>
    NotificationClicked,
}

/// <summary>Severity of a notification.</summary>
public enum TrayNotificationKind
{
    /// <summary>Information.</summary>
    Information = 1,

    /// <summary>Warning.</summary>
    Warning = 2,

    /// <summary>Error.</summary>
    Error = 3,
}

/// <summary>
/// Notification-area icon through <c>Shell_NotifyIcon</c> (version 4 behaviour) owned by a window the caller provides;
/// the caller forwards that window's messages to <see cref="Translate"/>. After Explorer restarts, the caller re-adds the
/// icon when it receives <see cref="TaskbarCreatedMessage"/>.
/// </summary>
public sealed unsafe partial class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint NIIF_RESPECT_QUIET_TIME = 0x80;
    private const int WM_LBUTTONUP = 0x0202, WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;

    private readonly nint _window;
    private readonly uint _callbackMessage;
    private nint _icon;
    private string _tooltip = string.Empty;
    private bool _added;

    /// <summary>Creates the icon (not yet shown) for <paramref name="window"/>, using the first icon of <paramref name="iconFile"/>.</summary>
    public TrayIcon(nint window, uint callbackMessage, string iconFile)
    {
        _window = window;
        _callbackMessage = callbackMessage;
        nint small = 0;
        if (!string.IsNullOrEmpty(iconFile) && ExtractIconEx(iconFile, 0, null, &small, 1) > 0 && small != 0)
        {
            _icon = small;
        }
    }

    /// <summary>Message Explorer broadcasts when the taskbar is (re)created.</summary>
    public static uint TaskbarCreatedMessage { get; } = RegisterWindowMessage("TaskbarCreated");

    /// <summary>Shows the icon (also after Explorer restarted).</summary>
    public bool Show(string tooltip)
    {
        _tooltip = tooltip;
        var data = Create(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Copy(tooltip, data.szTip, 128);
        if (_added)
        {
            Shell_NotifyIcon(NIM_DELETE, &data);
        }

        _added = Shell_NotifyIcon(NIM_ADD, &data);
        if (_added)
        {
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, &data);
        }

        return _added;
    }

    /// <summary>Updates the tooltip (at most 127 characters).</summary>
    public void SetTooltip(string tooltip)
    {
        if (!_added || tooltip == _tooltip)
        {
            return;
        }

        _tooltip = tooltip;
        var data = Create(NIF_TIP | NIF_SHOWTIP);
        Copy(tooltip, data.szTip, 128);
        Shell_NotifyIcon(NIM_MODIFY, &data);
    }

    /// <summary>Shows a notification (Windows displays it as a toast; quiet hours are respected).</summary>
    public void Notify(string title, string text, TrayNotificationKind kind)
    {
        if (!_added)
        {
            return;
        }

        var data = Create(NIF_INFO);
        Copy(title, data.szInfoTitle, 64);
        Copy(text, data.szInfo, 256);
        data.dwInfoFlags = (uint)kind | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIcon(NIM_MODIFY, &data);
    }

    /// <summary>Maps a callback message (version 4 layout) to an event.</summary>
    public static TrayIconEvent Translate(nint lParam) => (int)(lParam & 0xFFFF) switch
    {
        WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT => TrayIconEvent.Activate,
        WM_CONTEXTMENU => TrayIconEvent.ContextMenu,
        NIN_BALLOONUSERCLICK => TrayIconEvent.NotificationClicked,
        _ => TrayIconEvent.None,
    };

    /// <summary>Brings the owner window to the foreground (required before showing a context menu).</summary>
    public void FocusOwner() => SetForegroundWindow(_window);

    /// <summary>Removes the icon.</summary>
    public void Hide()
    {
        if (_added)
        {
            var data = Create(0);
            Shell_NotifyIcon(NIM_DELETE, &data);
            _added = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Hide();
        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }
    }

    /// <summary>Size of the native structure (layout check).</summary>
    internal static int NativeSize => sizeof(NOTIFYICONDATAW);

    private NOTIFYICONDATAW Create(uint flags) => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _window,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = _callbackMessage,
        hIcon = _icon,
    };

    private static void Copy(string text, char* destination, int capacity)
    {
        int length = Math.Min(text.Length, capacity - 1);
        for (int i = 0; i < length; i++)
        {
            destination[i] = text[i];
        }

        destination[length] = '\0';
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIcon(uint message, NOTIFYICONDATAW* data);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconEx(string file, int index, nint* large, nint* small, uint count);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
}
