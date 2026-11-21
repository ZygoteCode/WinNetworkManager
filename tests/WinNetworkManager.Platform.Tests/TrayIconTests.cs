using WinNetworkManager.Platform.Windows.Shell;

namespace WinNetworkManager.Platform.Tests;

public sealed class TrayIconTests
{
    [Fact]
    public void Native_structure_matches_the_x64_layout() => Assert.Equal(976, TrayIcon.NativeSize);

    [Theory]
    [InlineData(0x0202, TrayIconEvent.Activate)]       // WM_LBUTTONUP
    [InlineData(0x0400, TrayIconEvent.Activate)]       // NIN_SELECT
    [InlineData(0x0401, TrayIconEvent.Activate)]       // NIN_KEYSELECT
    [InlineData(0x007B, TrayIconEvent.ContextMenu)]    // WM_CONTEXTMENU
    [InlineData(0x0405, TrayIconEvent.NotificationClicked)]
    [InlineData(0x0200, TrayIconEvent.None)]           // WM_MOUSEMOVE
    public void Callback_events_are_translated(int message, TrayIconEvent expected) =>
        Assert.Equal(expected, TrayIcon.Translate((nint)(0x0001_0000 | message))); // high word = icon id

    [Fact]
    public void Taskbar_created_message_is_registered() => Assert.NotEqual(0u, TrayIcon.TaskbarCreatedMessage);
}
