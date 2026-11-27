using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinNetworkManager.UI.Controls;

/// <summary>
/// Application icon with a fallback: shows <see cref="Source"/> when available, otherwise a generic application glyph
/// (processes without an accessible executable, protected system processes, icons still loading). Decorative: the
/// owning row provides the accessible name.
/// </summary>
public sealed class AppIcon : Control
{
    /// <summary>Icon image; null shows the generic application glyph.</summary>
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(AppIcon), new FrameworkPropertyMetadata(null));

    static AppIcon()
    {
        FocusableProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(false));
    }

    /// <summary>Icon image.</summary>
    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    private sealed class DecorativePeer(AppIcon owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
