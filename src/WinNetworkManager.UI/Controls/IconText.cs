using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace WinNetworkManager.UI.Controls;

/// <summary>
/// Icon glyph (Segoe Fluent Icons). Decorative: excluded from the UI Automation control and content views so screen
/// readers never announce private-use glyph characters; the owning control provides the accessible name.
/// </summary>
public sealed class IconText : TextBlock
{
    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    private sealed class DecorativePeer(IconText owner) : TextBlockAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
