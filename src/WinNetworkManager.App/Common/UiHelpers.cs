using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace WinNetworkManager.App.Common;

/// <summary>Application-wide routed commands.</summary>
public static class FocusCommands
{
    /// <summary>Moves keyboard focus to the page's search box (Ctrl+F).</summary>
    public static RoutedUICommand FocusSearch { get; } = new("Search", nameof(FocusSearch), typeof(FocusCommands));
}

/// <summary>double → left margin (tree indentation).</summary>
public sealed class IndentConverter : IValueConverter
{
    /// <summary>Shared instance.</summary>
    public static IndentConverter Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is double d ? d : 0, 0, 0, 0);

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool → one of two strings (e.g. chevron glyphs).</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    /// <summary>Text for true.</summary>
    public string TrueText { get; set; } = string.Empty;

    /// <summary>Text for false.</summary>
    public string FalseText { get; set; } = string.Empty;

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? TrueText : FalseText;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Enum value → Visible when it equals the parameter.</summary>
public sealed class EnumVisibility : IValueConverter
{
    /// <summary>Shared instance.</summary>
    public static EnumVisibility Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Shows the UDP peer panel only on the UDP tab with a selection.</summary>
public sealed class UdpDetailVisibility : IMultiValueConverter
{
    /// <summary>Shared instance.</summary>
    public static UdpDetailVisibility Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is ViewModels.SocketTab.Udp && values[1] is ViewModels.UdpRow ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}
