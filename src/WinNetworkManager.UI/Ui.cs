using System.Windows;
using System.Windows.Media;

namespace WinNetworkManager.UI;

/// <summary>Attached properties used by the control templates of the design system.</summary>
public static class Ui
{
    /// <summary>Placeholder text shown by TextBox/ComboBox templates when empty.</summary>
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(string.Empty));

    /// <summary>Icon glyph (Segoe Fluent Icons code point) shown by button and text box templates.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(string.Empty));

    /// <summary>Corner radius override for templates that support it.</summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached("CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(4)));

    /// <summary>Brush of a status indicator (badges, row markers).</summary>
    public static readonly DependencyProperty IndicatorBrushProperty =
        DependencyProperty.RegisterAttached("IndicatorBrush", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>Gets the placeholder.</summary>
    public static string GetPlaceholder(DependencyObject element) => (string)element.GetValue(PlaceholderProperty);

    /// <summary>Sets the placeholder.</summary>
    public static void SetPlaceholder(DependencyObject element, string value) => element.SetValue(PlaceholderProperty, value);

    /// <summary>Gets the icon glyph.</summary>
    public static string GetIcon(DependencyObject element) => (string)element.GetValue(IconProperty);

    /// <summary>Sets the icon glyph.</summary>
    public static void SetIcon(DependencyObject element, string value) => element.SetValue(IconProperty, value);

    /// <summary>Gets the corner radius.</summary>
    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);

    /// <summary>Sets the corner radius.</summary>
    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);

    /// <summary>Gets the indicator brush.</summary>
    public static Brush? GetIndicatorBrush(DependencyObject element) => (Brush?)element.GetValue(IndicatorBrushProperty);

    /// <summary>Sets the indicator brush.</summary>
    public static void SetIndicatorBrush(DependencyObject element, Brush? value) => element.SetValue(IndicatorBrushProperty, value);
}

/// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code points used by the application (shared by both fonts).</summary>
public static class Glyphs
{
#pragma warning disable CS1591 // Names are self-describing.
    public const string Menu = "";
    public const string Dashboard = "";
    public const string Processes = "";
    public const string Connections = "";
    public const string Interfaces = "";
    public const string Diagnostics = "";
    public const string History = "";
    public const string Settings = "";
    public const string Search = "";
    public const string Clear = "";
    public const string Refresh = "";
    public const string Pause = "";
    public const string Play = "";
    public const string Copy = "";
    public const string Export = "";
    public const string Download = "";
    public const string Upload = "";
    public const string Shield = "";
    public const string Admin = "";
    public const string Info = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Check = "";
    public const string ChevronDown = "";
    public const string ChevronUp = "";
    public const string ChevronRight = "";
    public const string ChevronLeft = "";
    public const string ArrowUp = "";
    public const string ArrowDown = "";
    public const string Filter = "";
    public const string Columns = "";
    public const string FolderOpen = "";
    public const string Clock = "";
    public const string Globe = "";
    public const string Lock = "";
    public const string Certificate = "";
    public const string Back = "";
    public const string More = "";
    public const string Wifi = "";
    public const string Ethernet = "";
    public const string Play2 = "";
    public const string Stop = "";
    public const string Group = "";
    public const string Delete = "";
#pragma warning restore CS1591
}
