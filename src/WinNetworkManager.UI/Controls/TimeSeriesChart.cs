using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using WinNetworkManager.Core.Units;

namespace WinNetworkManager.UI.Controls;

/// <summary>What the values of a chart represent.</summary>
public enum ChartValueKind
{
    /// <summary>Bytes per second.</summary>
    Rate,

    /// <summary>Bytes.</summary>
    Bytes,

    /// <summary>Milliseconds (latency).</summary>
    Milliseconds,

    /// <summary>Unitless numbers.</summary>
    Plain,
}

/// <summary>One line/area of a <see cref="TimeSeriesChart"/>.</summary>
public sealed class ChartSeries
{
    /// <summary>Display name (legend, crosshair readout, accessibility summary).</summary>
    public required string Name { get; init; }

    /// <summary>Values at a fixed step; NaN = gap.</summary>
    public required IReadOnlyList<double> Values { get; init; }

    /// <summary>Line brush.</summary>
    public Brush? Stroke { get; init; }

    /// <summary>Area fill under the line (optional).</summary>
    public Brush? Fill { get; init; }

    /// <summary>Dashed line (second visual cue besides colour).</summary>
    public bool Dashed { get; init; }

    /// <summary>Line thickness.</summary>
    public double Thickness { get; init; } = 1.75;
}

/// <summary>
/// Lightweight, theme-aware time-series chart rendered with <see cref="StreamGeometry"/>: nice Y ticks with unit-aware
/// labels, gaps for missing data, stacked areas, a crosshair readout driven by mouse or keyboard (Left/Right/Home/End),
/// and an automation peer that exposes a textual summary to screen readers.
/// </summary>
public sealed class TimeSeriesChart : FrameworkElement
{
    /// <summary>Series to draw.</summary>
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(TimeSeriesChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    /// <summary>Time of the first sample.</summary>
    public static readonly DependencyProperty StartTimeProperty = DependencyProperty.Register(
        nameof(StartTime), typeof(DateTimeOffset), typeof(TimeSeriesChart), new FrameworkPropertyMetadata(default(DateTimeOffset), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Sample step.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(TimeSpan), typeof(TimeSeriesChart), new FrameworkPropertyMetadata(TimeSpan.FromSeconds(1), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Value kind (formatting).</summary>
    public static readonly DependencyProperty ValueKindProperty = DependencyProperty.Register(
        nameof(ValueKind), typeof(ChartValueKind), typeof(TimeSeriesChart), new FrameworkPropertyMetadata(ChartValueKind.Rate, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Unit system for byte values.</summary>
    public static readonly DependencyProperty UnitsProperty = DependencyProperty.Register(
        nameof(Units), typeof(DataUnitSystem), typeof(TimeSeriesChart), new FrameworkPropertyMetadata(DataUnitSystem.DecimalBytes, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Stack series (areas add up).</summary>
    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(
        nameof(Stacked), typeof(bool), typeof(TimeSeriesChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Show the time axis labels.</summary>
    public static readonly DependencyProperty ShowTimeAxisProperty = DependencyProperty.Register(
        nameof(ShowTimeAxis), typeof(bool), typeof(TimeSeriesChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Label times relative to the end ("−30 s") instead of clock times.</summary>
    public static readonly DependencyProperty RelativeTimeProperty = DependencyProperty.Register(
        nameof(RelativeTime), typeof(bool), typeof(TimeSeriesChart), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Accessible title, e.g. "Network throughput, last 60 seconds".</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(TimeSeriesChart), new FrameworkPropertyMetadata(string.Empty));

    private int _cursor = -1;

    static TimeSeriesChart()
    {
        FocusableProperty.OverrideMetadata(typeof(TimeSeriesChart), new FrameworkPropertyMetadata(true));
        FocusVisualStyleProperty.OverrideMetadata(typeof(TimeSeriesChart), new FrameworkPropertyMetadata(null));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(TimeSeriesChart), new FrameworkPropertyMetadata(true));
    }

    /// <summary>Creates the chart.</summary>
    public TimeSeriesChart()
    {
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    /// <summary>Series to draw.</summary>
    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>Time of the first sample.</summary>
    public DateTimeOffset StartTime
    {
        get => (DateTimeOffset)GetValue(StartTimeProperty);
        set => SetValue(StartTimeProperty, value);
    }

    /// <summary>Sample step.</summary>
    public TimeSpan Step
    {
        get => (TimeSpan)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>Value kind.</summary>
    public ChartValueKind ValueKind
    {
        get => (ChartValueKind)GetValue(ValueKindProperty);
        set => SetValue(ValueKindProperty, value);
    }

    /// <summary>Unit system.</summary>
    public DataUnitSystem Units
    {
        get => (DataUnitSystem)GetValue(UnitsProperty);
        set => SetValue(UnitsProperty, value);
    }

    /// <summary>Stacked areas.</summary>
    public bool Stacked
    {
        get => (bool)GetValue(StackedProperty);
        set => SetValue(StackedProperty, value);
    }

    /// <summary>Show time axis.</summary>
    public bool ShowTimeAxis
    {
        get => (bool)GetValue(ShowTimeAxisProperty);
        set => SetValue(ShowTimeAxisProperty, value);
    }

    /// <summary>Relative time labels.</summary>
    public bool RelativeTime
    {
        get => (bool)GetValue(RelativeTimeProperty);
        set => SetValue(RelativeTimeProperty, value);
    }

    /// <summary>Accessible title.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Formats a value according to <see cref="ValueKind"/> and <see cref="Units"/>.</summary>
    public string Format(double value) => ValueKind switch
    {
        _ when double.IsNaN(value) => "—",
        ChartValueKind.Rate => DataFormatter.FormatRate(value, Units),
        ChartValueKind.Bytes => DataFormatter.FormatBytes((long)value, Units),
        ChartValueKind.Milliseconds => value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms",
        _ => value.ToString("0.##", CultureInfo.CurrentCulture),
    };

    /// <summary>Text summary used by assistive technologies.</summary>
    public string Summary()
    {
        var series = Series;
        if (series is null || series.Count == 0)
        {
            return Title + ": no data";
        }

        var sb = new StringBuilder(Title);
        foreach (var s in series)
        {
            double last = s.Values.LastOrDefault(v => !double.IsNaN(v), double.NaN);
            double max = s.Values.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
            sb.Append(CultureInfo.CurrentCulture, $". {s.Name}: now {Format(last)}, peak {Format(max)}");
        }

        return sb.ToString();
    }

    /// <summary>Readout at the cursor, or null.</summary>
    public string? CursorReadout()
    {
        var series = Series;
        if (series is null || _cursor < 0)
        {
            return null;
        }

        var sb = new StringBuilder(TimeLabel(_cursor, Count(series)));
        foreach (var s in series)
        {
            double v = _cursor < s.Values.Count ? s.Values[_cursor] : double.NaN;
            sb.Append(CultureInfo.CurrentCulture, $"  {s.Name} {Format(v)}");
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this);

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var series = Series;
        int count = series is null ? 0 : Count(series);
        if (count < 2)
        {
            return;
        }

        var plot = PlotArea(series!);
        double x = e.GetPosition(this).X;
        int index = (int)Math.Round((x - plot.Left) / plot.Width * (count - 1));
        SetCursor(Math.Clamp(index, 0, count - 1));
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsKeyboardFocused)
        {
            SetCursor(-1);
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var series = Series;
        int count = series is null ? 0 : Count(series);
        if (count == 0)
        {
            return;
        }

        int start = _cursor < 0 ? count - 1 : _cursor;
        int? next = e.Key switch
        {
            Key.Left => Math.Max(0, start - 1),
            Key.Right => Math.Min(count - 1, start + 1),
            Key.PageUp => Math.Max(0, start - 10),
            Key.PageDown => Math.Min(count - 1, start + 10),
            Key.Home => 0,
            Key.End => count - 1,
            Key.Escape => -1,
            _ => null,
        };
        if (next is { } value)
        {
            SetCursor(value);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        SetCursor(-1);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit testing everywhere
        var series = Series;
        Brush grid = Resource("Brush.Chart.Grid", Brushes.DimGray);
        Brush axis = Resource("Brush.Chart.Axis", Brushes.Gray);
        if (IsKeyboardFocused)
        {
            dc.DrawRoundedRectangle(null, new Pen(Resource("Brush.Accent", Brushes.DodgerBlue), 2), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)), 6, 6);
        }

        if (series is null || series.Count == 0 || Count(series) < 2 || ActualWidth < 40 || ActualHeight < 30)
        {
            return;
        }

        int count = Count(series);
        var plot = PlotArea(series);
        double max = NiceMax(MaxValue(series));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(Resource<FontFamily>("Font.Text") ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double fontSize = Resource<object>("FontSize.Caption") is double fs ? fs - 1 : 11;
        var gridPen = new Pen(grid, 1);
        gridPen.Freeze();

        // Horizontal grid + Y labels (4 intervals).
        for (int i = 0; i <= 4; i++)
        {
            double value = max * i / 4;
            double y = Math.Round(plot.Bottom - (plot.Height * i / 4)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = new FormattedText(Format(value), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, axis, dpi);
            dc.DrawText(label, new Point(Math.Max(0, plot.Left - label.Width - 6), y - (label.Height / 2)));
        }

        if (ShowTimeAxis)
        {
            for (int i = 0; i <= 4; i++)
            {
                int index = (int)Math.Round((count - 1) * i / 4.0);
                double x = plot.Left + (plot.Width * index / (count - 1));
                var label = new FormattedText(TimeLabel(index, count), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, axis, dpi);
                double lx = Math.Clamp(x - (label.Width / 2), plot.Left - 4, Math.Max(plot.Left, ActualWidth - label.Width));
                dc.DrawText(label, new Point(lx, plot.Bottom + 4));
            }
        }

        double[]? baseline = Stacked ? new double[count] : null;
        foreach (var s in series)
        {
            var points = new Point?[count];
            for (int i = 0; i < count; i++)
            {
                double v = i < s.Values.Count ? s.Values[i] : double.NaN;
                if (double.IsNaN(v))
                {
                    continue;
                }

                double stackedValue = baseline is null ? v : baseline[i] + v;
                double x = plot.Left + (plot.Width * i / (count - 1));
                double y = plot.Bottom - (plot.Height * Math.Min(stackedValue, max) / max);
                points[i] = new Point(x, y);
            }

            if (s.Fill is not null)
            {
                dc.DrawGeometry(s.Fill, null, AreaGeometry(points, baseline, plot, max));
            }

            var pen = new Pen(s.Stroke ?? axis, s.Thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (s.Dashed)
            {
                pen.DashStyle = new DashStyle([3, 2], 0);
            }

            pen.Freeze();
            dc.DrawGeometry(null, pen, LineGeometry(points));
            if (baseline is not null)
            {
                for (int i = 0; i < count; i++)
                {
                    double v = i < s.Values.Count ? s.Values[i] : double.NaN;
                    if (!double.IsNaN(v))
                    {
                        baseline[i] += v;
                    }
                }
            }
        }

        if (_cursor >= 0 && _cursor < count)
        {
            DrawCursor(dc, series, plot, max, count, typeface, fontSize, dpi);
        }
    }

    private void DrawCursor(DrawingContext dc, IReadOnlyList<ChartSeries> series, Rect plot, double max, int count, Typeface typeface, double fontSize, double dpi)
    {
        double x = plot.Left + (plot.Width * _cursor / (count - 1));
        var crossPen = new Pen(Resource("Brush.Chart.Crosshair", Brushes.Gray), 1) { DashStyle = DashStyles.Dash };
        dc.DrawLine(crossPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        double stacked = 0;
        var lines = new List<(string Text, Brush Brush)> { (TimeLabel(_cursor, count), Resource("Brush.Text.Secondary", Brushes.Gray)) };
        foreach (var item in series)
        {
            double v = _cursor < item.Values.Count ? item.Values[_cursor] : double.NaN;
            lines.Add(($"{item.Name}  {Format(v)}", item.Stroke ?? Brushes.Gray));
            if (double.IsNaN(v))
            {
                continue;
            }

            stacked = Stacked ? stacked + v : v;
            double y = plot.Bottom - (plot.Height * Math.Min(stacked, max) / max);
            dc.DrawEllipse(Resource("Brush.Bg.Surface", Brushes.Black), new Pen(item.Stroke ?? Brushes.Gray, 2), new Point(x, y), 3.5, 3.5);
        }

        var texts = lines.Select(l => (Text: new FormattedText(l.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize + 1, l.Brush, dpi), l.Brush)).ToList();
        double width = texts.Max(t => t.Text.Width) + 20;
        double height = texts.Sum(t => t.Text.Height + 2) + 12;
        double bx = x + 12 + width > plot.Right ? x - 12 - width : x + 12;
        var box = new Rect(Math.Max(plot.Left, bx), plot.Top + 4, width, height);
        dc.DrawRoundedRectangle(Resource("Brush.Bg.Elevated", Brushes.Black), new Pen(Resource("Brush.Border.Strong", Brushes.Gray), 1), box, 6, 6);
        double ty = box.Top + 6;
        foreach (var (text, _) in texts)
        {
            dc.DrawText(text, new Point(box.Left + 10, ty));
            ty += text.Height + 2;
        }
    }

    private static StreamGeometry LineGeometry(Point?[] points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            bool open = false;
            foreach (var p in points)
            {
                if (p is null)
                {
                    open = false;
                    continue;
                }

                if (!open)
                {
                    ctx.BeginFigure(p.Value, false, false);
                    open = true;
                }
                else
                {
                    ctx.LineTo(p.Value, true, true);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry AreaGeometry(Point?[] points, double[]? baseline, Rect plot, double max)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            int i = 0;
            while (i < points.Length)
            {
                while (i < points.Length && points[i] is null)
                {
                    i++;
                }

                int start = i;
                while (i < points.Length && points[i] is not null)
                {
                    i++;
                }

                int end = i - 1;
                if (end <= start)
                {
                    continue;
                }

                ctx.BeginFigure(points[start]!.Value, true, true);
                for (int k = start + 1; k <= end; k++)
                {
                    ctx.LineTo(points[k]!.Value, false, false);
                }

                for (int k = end; k >= start; k--)
                {
                    double b = baseline is null ? 0 : baseline[k];
                    double y = plot.Bottom - (plot.Height * Math.Min(b, max) / max);
                    ctx.LineTo(new Point(points[k]!.Value.X, y), false, false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private Rect PlotArea(IReadOnlyList<ChartSeries> series)
    {
        double max = NiceMax(MaxValue(series));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        double fontSize = Resource<object>("FontSize.Caption") is double fs ? fs - 1 : 11;
        double labelWidth = new FormattedText(Format(max), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, dpi).Width;
        double left = Math.Ceiling(labelWidth + 10);
        double bottom = ShowTimeAxis ? fontSize + 10 : 4;
        return new Rect(left, 6, Math.Max(10, ActualWidth - left - 8), Math.Max(10, ActualHeight - bottom - 8));
    }

    private double MaxValue(IReadOnlyList<ChartSeries> series)
    {
        double max = 0;
        if (Stacked)
        {
            int count = Count(series);
            for (int i = 0; i < count; i++)
            {
                double sum = 0;
                foreach (var s in series)
                {
                    if (i < s.Values.Count && !double.IsNaN(s.Values[i]))
                    {
                        sum += s.Values[i];
                    }
                }

                max = Math.Max(max, sum);
            }

            return max;
        }

        foreach (var s in series)
        {
            foreach (double v in s.Values)
            {
                if (!double.IsNaN(v) && v > max)
                {
                    max = v;
                }
            }
        }

        return max;
    }

    private double NiceMax(double max)
    {
        double floor = ValueKind is ChartValueKind.Rate or ChartValueKind.Bytes ? 1024 : 1;
        max = Math.Max(max, floor);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        double normalized = max / magnitude;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private string TimeLabel(int index, int count)
    {
        if (RelativeTime)
        {
            var ago = Step * (count - 1 - index);
            return ago == TimeSpan.Zero ? "now"
                : ago.TotalMinutes >= 120 ? $"−{ago.TotalHours:0} h"
                : ago.TotalSeconds >= 120 ? $"−{ago.TotalMinutes:0} min"
                : $"−{ago.TotalSeconds:0} s";
        }

        var time = (StartTime + (Step * index)).ToLocalTime();
        return Step >= TimeSpan.FromHours(1) ? time.ToString("g", CultureInfo.CurrentCulture)
            : Step >= TimeSpan.FromMinutes(1) ? time.ToString("t", CultureInfo.CurrentCulture)
            : time.ToString("T", CultureInfo.CurrentCulture);
    }

    private static int Count(IReadOnlyList<ChartSeries> series) => series.Count == 0 ? 0 : series.Max(s => s.Values.Count);

    private void SetCursor(int index)
    {
        if (_cursor == index)
        {
            return;
        }

        _cursor = index;
        InvalidateVisual();
        if (UIElementAutomationPeer.FromElement(this) is ChartAutomationPeer peer)
        {
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.HelpTextProperty, null, CursorReadout() ?? string.Empty);
        }
    }

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private T? Resource<T>(string key)
        where T : class => TryFindResource(key) as T;

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TimeSeriesChart)d;
        if (chart._cursor >= 0 && e.NewValue is IReadOnlyList<ChartSeries> series && chart._cursor >= Count(series))
        {
            chart._cursor = -1;
        }
    }

    private sealed class ChartAutomationPeer(TimeSeriesChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(TimeSeriesChart);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetLocalizedControlTypeCore() => "chart";

        protected override string GetNameCore()
        {
            string name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? owner.Summary() : name;
        }

        protected override string GetHelpTextCore() => owner.CursorReadout() ?? "Use the left and right arrow keys to read values.";

        protected override bool IsKeyboardFocusableCore() => true;
    }
}
