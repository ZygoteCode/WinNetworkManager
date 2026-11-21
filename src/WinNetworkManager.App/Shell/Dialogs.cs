using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WinNetworkManager.Platform.Windows.Shell;
using WinNetworkManager.UI;
using WinNetworkManager.UI.Controls;

namespace WinNetworkManager.App.Shell;

/// <summary>Icon and tone of a dialog.</summary>
public enum DialogKind
{
    /// <summary>Information.</summary>
    Information,

    /// <summary>Question.</summary>
    Question,

    /// <summary>Warning (for destructive actions).</summary>
    Warning,

    /// <summary>Error.</summary>
    Error,
}

/// <summary>
/// Themed message and confirmation dialogs. Used instead of <see cref="MessageBox"/>, whose buttons Windows labels in
/// the system display language, so every word of the interface stays English and matches the theme.
/// </summary>
public static class Dialogs
{
    /// <summary>
    /// Asks for confirmation; returns true when <paramref name="confirmText"/> was chosen. For a
    /// <paramref name="destructive"/> action the cancel button is the default one (Enter does not delete).
    /// </summary>
    public static bool Confirm(string title, string message, string confirmText, string cancelText = "Cancel", DialogKind kind = DialogKind.Question, bool destructive = false) =>
        Show(title, message, confirmText, cancelText, kind, destructive);

    /// <summary>Shows a message with a single button.</summary>
    public static void Inform(string title, string message, DialogKind kind = DialogKind.Information) =>
        Show(title, message, "OK", null, kind, destructive: false);

    private static bool Show(string title, string message, string primaryText, string? secondaryText, DialogKind kind, bool destructive)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        if (owner is { IsVisible: false })
        {
            owner = null;
        }

        var window = new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            MinWidth = 400,
            MaxWidth = 600,
            UseLayoutRounding = true,
        };
        window.SetResourceReference(Control.BackgroundProperty, "Brush.Bg.Layer");
        window.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Primary");
        window.SetResourceReference(Control.FontFamilyProperty, "Font.Text");
        window.SetResourceReference(Control.FontSizeProperty, "FontSize.Body");

        var icon = new IconText
        {
            Text = kind switch
            {
                DialogKind.Warning => Glyphs.Warning,
                DialogKind.Error => Glyphs.Error,
                DialogKind.Question => Glyphs.Info,
                _ => Glyphs.Info,
            },
            FontSize = 28,
            Margin = new Thickness(0, 2, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(FrameworkElement.StyleProperty, "Icon");
        icon.SetResourceReference(TextBlock.ForegroundProperty, kind switch
        {
            DialogKind.Warning => "Brush.Status.Warning",
            DialogKind.Error => "Brush.Status.Danger",
            _ => "Brush.Accent",
        });

        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "Text.Subtitle");
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        body.SetResourceReference(FrameworkElement.StyleProperty, "Text.Body");
        var text = new StackPanel();
        text.Children.Add(heading);
        text.Children.Add(body);

        var content = new DockPanel { Margin = new Thickness(24, 24, 24, 20) };
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);
        content.Children.Add(text);

        var primary = new Button { Content = primaryText, IsDefault = true, MinWidth = 96 };
        primary.SetResourceReference(FrameworkElement.StyleProperty, destructive ? "Button.Standard" : "Button.Accent");
        primary.Click += (_, _) => window.DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(primary);
        if (secondaryText is not null)
        {
            var secondary = new Button { Content = secondaryText, IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
            secondary.SetResourceReference(FrameworkElement.StyleProperty, destructive ? "Button.Accent" : "Button.Standard");
            buttons.Children.Add(secondary);
            if (destructive)
            {
                // Destructive confirmation: the safe choice is the default one.
                primary.IsDefault = false;
                secondary.IsDefault = true;
            }
        }
        else
        {
            primary.IsCancel = true;
        }

        var footer = new Border { Padding = new Thickness(24, 14, 24, 14), Child = buttons, BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Chrome");
        footer.SetResourceReference(Border.BorderBrushProperty, "Brush.Border.Divider");

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(content);
        window.Content = root;
        window.SourceInitialized += (_, _) =>
        {
            bool dark = window.TryFindResource("Brush.Bg.Layer") is SolidColorBrush { Color: var c } && (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255 < 0.5;
            WindowEffects.SetDarkTitleBar(new WindowInteropHelper(window).Handle, dark);
        };
        window.Loaded += (_, _) => (destructive && secondaryText is not null ? (UIElement)buttons.Children[1] : primary).Focus();
        return window.ShowDialog() == true;
    }
}
