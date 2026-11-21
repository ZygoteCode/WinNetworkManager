using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using WinNetworkManager.Core.Settings;

namespace WinNetworkManager.UI;

/// <summary>Effective theme variant.</summary>
public enum ThemeVariant
{
    /// <summary>Dark palette.</summary>
    Dark,

    /// <summary>Light palette.</summary>
    Light,

    /// <summary>Windows contrast theme (system colours).</summary>
    HighContrast,
}

/// <summary>
/// Applies the design system to the application: palette (dark/light/high contrast), accent colour (Windows accent
/// or user choice, adjusted for contrast), text scale (Windows "Text size" × user scale), row density and motion.
/// Follows Windows changes while the preference is "System". UI thread only.
/// </summary>
public sealed class ThemeManager : IDisposable
{
    private const string PaletteMarker = "WNM.Palette";
    private static readonly Dictionary<string, double> BaseFontSizes = new()
    {
        ["FontSize.Caption"] = 12,
        ["FontSize.Body"] = 14,
        ["FontSize.BodyLarge"] = 15,
        ["FontSize.Subtitle"] = 18,
        ["FontSize.Title"] = 24,
        ["FontSize.Metric"] = 28,
        ["FontSize.Display"] = 34,
        ["FontSize.Icon"] = 16,
        ["FontSize.IconSmall"] = 12,
        ["FontSize.IconLarge"] = 20,
    };

    private readonly Application _app;
    private ThemePreference _preference = ThemePreference.System;
    private string? _accent;
    private double _userScale = 1;
    private bool? _reduceMotion;
    private TableDensity _density = TableDensity.Comfortable;
    private bool _subscribed;

    /// <summary>Creates the manager for <paramref name="app"/>.</summary>
    public ThemeManager(Application app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
    }

    /// <summary>Raised after the theme changed (variant, accent or scale).</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>Current variant.</summary>
    public ThemeVariant Variant { get; private set; } = ThemeVariant.Dark;

    /// <summary>True when the dark palette is active.</summary>
    public bool IsDark => Variant == ThemeVariant.Dark || (Variant == ThemeVariant.HighContrast && Luminance(SystemColors.WindowColor) < 0.5);

    /// <summary>True when animations may run.</summary>
    public bool MotionEnabled { get; private set; } = true;

    /// <summary>Effective text scale.</summary>
    public double TextScale { get; private set; } = 1;

    /// <summary>Applies preferences.</summary>
    public void Apply(ThemePreference preference, string? accentHex, double userTextScale, bool? reduceMotion, TableDensity density)
    {
        _preference = preference;
        _accent = accentHex;
        _userScale = double.IsNaN(userTextScale) ? 1 : Math.Clamp(userTextScale, 0.8, 2.0);
        _reduceMotion = reduceMotion;
        _density = density;
        if (!_subscribed)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
            _subscribed = true;
        }

        Reapply();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_subscribed)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
            _subscribed = false;
        }
    }

    /// <summary>Reads whether Windows apps use the light theme.</summary>
    public static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    /// <summary>Reads the Windows accent colour.</summary>
    public static Color? SystemAccentColor()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is int abgr)
        {
            uint v = unchecked((uint)abgr);
            return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
        }

        return null;
    }

    /// <summary>Reads the Windows "Text size" accessibility factor.</summary>
    public static double SystemTextScale()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
        return key?.GetValue("TextScaleFactor") is int percent && percent is >= 100 and <= 225 ? percent / 100.0 : 1.0;
    }

    /// <summary>WCAG contrast ratio of two colours.</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a) + 0.05;
        double lb = Luminance(b) + 0.05;
        return la > lb ? la / lb : lb / la;
    }

    /// <summary>Relative luminance (WCAG 2.x).</summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private void Reapply()
    {
        Variant = SystemParameters.HighContrast ? ThemeVariant.HighContrast
            : _preference switch
            {
                ThemePreference.Dark => ThemeVariant.Dark,
                ThemePreference.Light => ThemeVariant.Light,
                _ => SystemUsesLightTheme() ? ThemeVariant.Light : ThemeVariant.Dark,
            };

        var palette = Variant switch
        {
            ThemeVariant.Light => Load("Theme.Light.xaml"),
            ThemeVariant.HighContrast => BuildHighContrast(),
            _ => Load("Theme.Dark.xaml"),
        };
        palette[PaletteMarker] = true;

        var merged = _app.Resources.MergedDictionaries;
        int index = merged.ToList().FindIndex(d => d.Contains(PaletteMarker));
        if (index >= 0)
        {
            merged[index] = palette;
        }
        else
        {
            merged.Insert(0, palette);
        }

        ApplyAccent(palette);
        TextScale = Math.Clamp(SystemTextScale() * _userScale, 0.8, 2.5);
        foreach (var (key, size) in BaseFontSizes)
        {
            _app.Resources[key] = Math.Round(size * TextScale, 1);
        }

        double row = (_density == TableDensity.Compact ? 26 : 32) * Math.Max(1, TextScale * 0.9);
        _app.Resources["Height.Row"] = Math.Round(row);
        _app.Resources["Height.Control"] = Math.Round(32 * Math.Max(1, TextScale * 0.9));
        MotionEnabled = _reduceMotion is { } reduce ? !reduce : SystemParameters.ClientAreaAnimation;
        _app.Resources["MotionEnabled"] = MotionEnabled;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAccent(ResourceDictionary palette)
    {
        var resources = _app.Resources;
        if (Variant == ThemeVariant.HighContrast)
        {
            resources["Brush.Accent"] = Frozen(SystemColors.HighlightColor);
            resources["Brush.AccentHover"] = Frozen(SystemColors.HighlightColor);
            resources["Brush.AccentPressed"] = Frozen(SystemColors.HighlightColor);
            resources["Brush.AccentText"] = Frozen(SystemColors.HotTrackColor);
            resources["Brush.OnAccent"] = Frozen(SystemColors.HighlightTextColor);
            resources["Brush.AccentSubtle"] = Frozen(SystemColors.HighlightColor);
            resources["Brush.AccentSubtleStrong"] = Frozen(SystemColors.HighlightColor);
            return;
        }

        bool dark = Variant == ThemeVariant.Dark;
        Color surface = ((SolidColorBrush)palette["Brush.Bg.Surface"]).Color;
        Color baseColor = TryParse(_accent) ?? SystemAccentColor() ?? Color.FromRgb(0x3B, 0x82, 0xF6);

        // Fill colour: light enough on dark surfaces, dark enough on light ones (≥ 3:1 for UI components).
        Color accent = EnsureContrast(baseColor, surface, 3.0, lighten: dark);
        Color hover = Shift(accent, dark ? 0.08 : -0.08);
        Color pressed = Shift(accent, dark ? -0.06 : -0.14);
        Color text = EnsureContrast(baseColor, surface, 4.5, lighten: dark);
        Color onAccent = Contrast(accent, Colors.White) >= Contrast(accent, Color.FromRgb(0x10, 0x12, 0x16)) ? Colors.White : Color.FromRgb(0x10, 0x12, 0x16);

        resources["Color.Accent"] = accent;
        resources["Brush.Accent"] = Frozen(accent);
        resources["Brush.AccentHover"] = Frozen(hover);
        resources["Brush.AccentPressed"] = Frozen(pressed);
        resources["Brush.AccentText"] = Frozen(text);
        resources["Brush.OnAccent"] = Frozen(onAccent);
        resources["Brush.AccentSubtle"] = Frozen(Color.FromArgb(dark ? (byte)0x2E : (byte)0x24, accent.R, accent.G, accent.B));
        resources["Brush.AccentSubtleStrong"] = Frozen(Color.FromArgb(dark ? (byte)0x47 : (byte)0x3A, accent.R, accent.G, accent.B));
    }

    private static ResourceDictionary Load(string file) =>
        new() { Source = new Uri($"pack://application:,,,/WinNetworkManager.UI;component/Themes/{file}", UriKind.Absolute) };

    private static ResourceDictionary BuildHighContrast()
    {
        var d = new ResourceDictionary();
        var window = SystemColors.WindowColor;
        var text = SystemColors.WindowTextColor;
        var highlight = SystemColors.HighlightColor;
        var hot = SystemColors.HotTrackColor;
        var gray = SystemColors.GrayTextColor;
        var highlightText = SystemColors.HighlightTextColor;
        void Set(string key, Color c) => d[key] = Frozen(c);

        foreach (string key in new[] { "Window", "Chrome", "Layer", "Surface", "SurfaceHover", "SurfacePressed", "Elevated", "Input", "RowHover", "RowAlternate", "Overlay" })
        {
            Set("Brush.Bg." + key, window);
        }

        foreach (string key in new[] { "Default", "Subtle", "Strong" })
        {
            Set("Brush.Border." + key, text);
        }

        Set("Brush.Border.Divider", gray);
        Set("Brush.Text.Primary", text);
        Set("Brush.Text.Secondary", text);
        Set("Brush.Text.Tertiary", text);
        Set("Brush.Text.Disabled", gray);
        Set("Brush.Text.Inverse", highlightText);
        Set("Brush.Data.Download", highlight);
        Set("Brush.Data.Upload", hot);
        Set("Brush.Data.Tcp", highlight);
        Set("Brush.Data.Udp", hot);
        Set("Brush.Data.Loopback", gray);
        Set("Brush.Data.Neutral", gray);
        d["Brush.Data.DownloadFill"] = Brushes.Transparent;
        d["Brush.Data.UploadFill"] = Brushes.Transparent;
        Set("Brush.Chart.Grid", gray);
        Set("Brush.Chart.Axis", text);
        Set("Brush.Chart.Crosshair", text);
        Set("Brush.Status.Success", text);
        Set("Brush.Status.Warning", hot);
        Set("Brush.Status.Danger", hot);
        Set("Brush.Status.Info", text);
        foreach (string key in new[] { "SuccessBg", "WarningBg", "DangerBg", "InfoBg" })
        {
            Set("Brush.Status." + key, window);
        }

        Set("Brush.ScrollBar.Thumb", text);
        Set("Brush.ScrollBar.ThumbHover", highlight);
        d["Color.Bg.Window"] = window;
        d["Color.Data.Download"] = highlight;
        d["Color.Data.Upload"] = hot;
        d["Color.Data.Tcp"] = highlight;
        d["Color.Data.Udp"] = hot;
        d["Color.Data.Loopback"] = gray;
        d["Color.Data.Neutral"] = gray;
        return d;
    }

    private static Color EnsureContrast(Color color, Color background, double ratio, bool lighten)
    {
        var c = color;
        for (int i = 0; i < 40 && Contrast(c, background) < ratio; i++)
        {
            c = Shift(c, lighten ? 0.04 : -0.04);
        }

        return c;
    }

    /// <summary>Moves a colour towards white (positive) or black (negative).</summary>
    private static Color Shift(Color c, double amount)
    {
        static byte Mix(byte v, double a) => (byte)Math.Clamp(Math.Round(a >= 0 ? v + ((255 - v) * a) : v * (1 + a)), 0, 255);
        return Color.FromRgb(Mix(c.R, amount), Mix(c.G, amount), Mix(c.B, amount));
    }

    private static Color? TryParse(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#')
        {
            return null;
        }

        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
        {
            _app.Dispatcher.BeginInvoke(Reapply);
        }
    }

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
        {
            _app.Dispatcher.BeginInvoke(Reapply);
        }
    }
}
