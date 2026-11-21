using System.Windows;
using System.Windows.Media;

namespace WinNetworkManager.UI.Controls;

/// <summary>
/// Minimal inline trend line for table cells and tiles (no axes, no interaction). NaN values are gaps. Cheap enough
/// to render in hundreds of virtualized rows every second.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    /// <summary>Values (oldest first).</summary>
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<float>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Line brush.</summary>
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fill brush under the line.</summary>
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fixed maximum (0 = auto).</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Values.</summary>
    public IReadOnlyList<float>? Values
    {
        get => (IReadOnlyList<float>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Line brush.</summary>
    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Fill brush.</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Fixed maximum (0 = auto).</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 80 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 18 : availableSize.Height);

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        var values = Values;
        if (values is null || values.Count < 2 || ActualWidth <= 2 || ActualHeight <= 2)
        {
            return;
        }

        double max = Maximum;
        if (max <= 0)
        {
            foreach (float v in values)
            {
                if (!float.IsNaN(v) && v > max)
                {
                    max = v;
                }
            }
        }

        if (max <= 0)
        {
            var flat = new Pen(Stroke, 1);
            flat.Freeze();
            dc.DrawLine(flat, new Point(0, ActualHeight - 1.5), new Point(ActualWidth, ActualHeight - 1.5));
            return;
        }

        double w = ActualWidth;
        double h = ActualHeight - 2;
        var line = new StreamGeometry();
        var area = Fill is null ? null : new StreamGeometry();
        using (var ctx = line.Open())
        using (var actx = area?.Open())
        {
            bool open = false;
            Point first = default;
            Point last = default;
            for (int i = 0; i < values.Count; i++)
            {
                float v = values[i];
                if (float.IsNaN(v))
                {
                    if (open && actx is not null)
                    {
                        actx.LineTo(new Point(last.X, h + 1), false, false);
                        actx.LineTo(new Point(first.X, h + 1), false, false);
                    }

                    open = false;
                    continue;
                }

                var p = new Point(w * i / (values.Count - 1), 1 + (h - (h * Math.Min(v, max) / max)));
                if (!open)
                {
                    ctx.BeginFigure(p, false, false);
                    actx?.BeginFigure(p, true, true);
                    first = p;
                    open = true;
                }
                else
                {
                    ctx.LineTo(p, true, true);
                    actx?.LineTo(p, false, false);
                }

                last = p;
            }

            if (open && actx is not null)
            {
                actx.LineTo(new Point(last.X, h + 1), false, false);
                actx.LineTo(new Point(first.X, h + 1), false, false);
            }
        }

        line.Freeze();
        if (area is not null)
        {
            area.Freeze();
            dc.DrawGeometry(Fill, null, area);
        }

        var pen = new Pen(Stroke, 1.25) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
