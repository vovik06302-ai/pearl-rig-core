using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PearlRigCore.Controls;

/// <summary>A live, animated speedometer whose scale is supplied from observed GPU hashrates.</summary>
public sealed class SpeedometerGauge : FrameworkElement
{
    private const double StartAngleDegrees = 135;
    private const double SweepDegrees = 270;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SpeedometerGauge), new FrameworkPropertyMetadata(0d, OnValueChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(SpeedometerGauge), new FrameworkPropertyMetadata(1d, OnVisualChanged));
    public static readonly DependencyProperty HasDataProperty = DependencyProperty.Register(
        nameof(HasData), typeof(bool), typeof(SpeedometerGauge), new FrameworkPropertyMetadata(false, OnVisualChanged));
    private static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.Register(
        nameof(AnimatedValue), typeof(double), typeof(SpeedometerGauge), new FrameworkPropertyMetadata(0d, OnVisualChanged));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool HasData { get => (bool)GetValue(HasDataProperty); set => SetValue(HasDataProperty, value); }
    private double AnimatedValue { get => (double)GetValue(AnimatedValueProperty); set => SetValue(AnimatedValueProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 138 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 100 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20) return;

        var center = new Point(width / 2, height * 0.59);
        var radius = Math.Max(8, Math.Min(width / 2 - 10, height * 0.50));
        var track = new Pen(new SolidColorBrush(Color.FromRgb(43, 57, 73)), 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var accent = new Pen(new SolidColorBrush(Color.FromRgb(0, 229, 192)), 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawingContext.DrawGeometry(null, track, CreateArc(center, radius, StartAngleDegrees, SweepDegrees));

        for (var tick = 0; tick <= 10; tick++)
        {
            var angle = DegreesToRadians(StartAngleDegrees + SweepDegrees * tick / 10d);
            var inner = PointOnCircle(center, radius - (tick % 5 == 0 ? 8 : 4), angle);
            var outer = PointOnCircle(center, radius - 1, angle);
            drawingContext.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(111, 130, 151)), tick % 5 == 0 ? 1.5 : 1), inner, outer);
        }

        var max = double.IsFinite(Maximum) && Maximum > 0 ? Maximum : 1;
        var ratio = HasData ? Math.Clamp(AnimatedValue / max, 0, 1) : 0;
        if (HasData && ratio > 0.002)
            drawingContext.DrawGeometry(null, accent, CreateArc(center, radius, StartAngleDegrees, SweepDegrees * ratio));

        var labelBrush = new SolidColorBrush(Color.FromRgb(139, 152, 170));
        DrawLabel(drawingContext, "0", labelBrush, new Point(3, height - 15), 9, TextAlignment.Left);
        DrawLabel(drawingContext, $"{max:0.##} TH/s", labelBrush, new Point(width - 3, height - 15), 9, TextAlignment.Right);

        if (HasData)
        {
            var needleAngle = DegreesToRadians(StartAngleDegrees + SweepDegrees * ratio);
            var needleEnd = PointOnCircle(center, radius - 13, needleAngle);
            drawingContext.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(242, 247, 251)), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, center, needleEnd);
            drawingContext.DrawEllipse(new SolidColorBrush(Color.FromRgb(0, 229, 192)), null, center, 4.2, 4.2);
        }
        else
        {
            DrawLabel(drawingContext, "нет данных", labelBrush, new Point(width / 2, center.Y + 7), 9, TextAlignment.Center);
        }
    }

    private static void OnValueChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var gauge = (SpeedometerGauge)owner;
        var target = args.NewValue is double value && double.IsFinite(value) && value >= 0 ? value : 0;
        var start = gauge.AnimatedValue;
        gauge.BeginAnimation(AnimatedValueProperty, new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(520))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnVisualChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) => ((SpeedometerGauge)owner).InvalidateVisual();

    private static StreamGeometry CreateArc(Point center, double radius, double startAngleDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();
        var start = PointOnCircle(center, radius, DegreesToRadians(startAngleDegrees));
        var end = PointOnCircle(center, radius, DegreesToRadians(startAngleDegrees + sweepDegrees));
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angle) =>
        new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private void DrawLabel(DrawingContext drawingContext, string text, Brush brush, Point point, double size, TextAlignment alignment)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { TextAlignment = alignment };
        drawingContext.DrawText(formatted, point);
    }
}
