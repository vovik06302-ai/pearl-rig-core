using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PearlRigCore.Controls;

/// <summary>Hardware-accelerated WPF sparkline chart rendered from real telemetry ring buffers with optional Min/Max scale labels.</summary>
public sealed class TelemetrySparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeColorProperty = DependencyProperty.Register(
        nameof(StrokeColor), typeof(Color), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(Color.FromRgb(0, 229, 192), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowScaleLabelsProperty = DependencyProperty.Register(
        nameof(ShowScaleLabels), typeof(bool), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitSuffixProperty = DependencyProperty.Register(
        nameof(UnitSuffix), typeof(string), typeof(TelemetrySparkline),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Color StrokeColor
    {
        get => (Color)GetValue(StrokeColorProperty);
        set => SetValue(StrokeColorProperty, value);
    }

    public bool ShowScaleLabels
    {
        get => (bool)GetValue(ShowScaleLabelsProperty);
        set => SetValue(ShowScaleLabelsProperty, value);
    }

    public string UnitSuffix
    {
        get => (string)GetValue(UnitSuffixProperty);
        set => SetValue(UnitSuffixProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 140 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 28 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 12 || height < 8) return;

        var bounds = new Rect(0.5, 0.5, width - 1, height - 1);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(11, 17, 24)),
            new Pen(new SolidColorBrush(Color.FromRgb(28, 41, 55)), 1),
            bounds, 5, 5);

        var samples = Values;
        if (samples is null || samples.Count < 2)
            return;

        var min = double.MaxValue;
        var max = double.MinValue;
        for (var i = 0; i < samples.Count; i++)
        {
            var v = samples[i];
            if (!double.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (min > max) return;

        var rawMin = min;
        var rawMax = max;
        var span = max - min;
        if (span < 0.01)
        {
            min -= 0.5;
            max += 0.5;
            span = 1.0;
        }

        var padX = 4d;
        var padY = ShowScaleLabels && height >= 36 ? 11d : 4d;
        var plotWidth = Math.Max(4, width - padX * 2);
        var plotHeight = Math.Max(4, height - padY * 2);

        Point MapPoint(int index, double value)
        {
            var x = padX + (index / (double)(samples.Count - 1)) * plotWidth;
            var normalized = Math.Clamp((value - min) / span, 0d, 1d);
            var y = padY + plotHeight - normalized * plotHeight;
            return new Point(x, y);
        }

        var lineGeometry = new StreamGeometry();
        var fillGeometry = new StreamGeometry();
        var first = MapPoint(0, samples[0]);
        var last = first;

        using (var lineCtx = lineGeometry.Open())
        using (var fillCtx = fillGeometry.Open())
        {
            lineCtx.BeginFigure(first, false, false);
            fillCtx.BeginFigure(new Point(first.X, height - 2), true, true);
            fillCtx.LineTo(first, false, false);

            for (var i = 1; i < samples.Count; i++)
            {
                var pt = MapPoint(i, samples[i]);
                lineCtx.LineTo(pt, true, true);
                fillCtx.LineTo(pt, false, false);
                last = pt;
            }

            fillCtx.LineTo(new Point(last.X, height - 2), false, false);
        }

        lineGeometry.Freeze();
        fillGeometry.Freeze();

        var fillBrush = new LinearGradientBrush(
            Color.FromArgb(72, StrokeColor.R, StrokeColor.G, StrokeColor.B),
            Color.FromArgb(4, StrokeColor.R, StrokeColor.G, StrokeColor.B),
            new Point(0, 0),
            new Point(0, 1));
        fillBrush.Freeze();

        var strokeBrush = new SolidColorBrush(StrokeColor);
        strokeBrush.Freeze();
        var pen = new Pen(strokeBrush, 1.6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        drawingContext.DrawGeometry(fillBrush, null, fillGeometry);
        drawingContext.DrawGeometry(null, pen, lineGeometry);
        drawingContext.DrawEllipse(strokeBrush, null, last, 2.4, 2.4);

        if (ShowScaleLabels && height >= 36)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var labelBrush = new SolidColorBrush(Color.FromArgb(195, 139, 152, 170));
            labelBrush.Freeze();
            var suffix = string.IsNullOrWhiteSpace(UnitSuffix) ? string.Empty : $" {UnitSuffix}";

            var maxText = new FormattedText(
                $"{rawMax:0.##}{suffix}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                8.5,
                labelBrush,
                dpi);
            drawingContext.DrawText(maxText, new Point(4, 1.5));

            var minText = new FormattedText(
                $"{rawMin:0.##}{suffix}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                8.5,
                labelBrush,
                dpi);
            drawingContext.DrawText(minText, new Point(4, Math.Max(11, height - 11)));
        }
    }
}
