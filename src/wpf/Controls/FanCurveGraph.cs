using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PearlRigCore.Controls;

/// <summary>Renders the 3-point piecewise-linear Smart Fan Curve with live GPU effective temperature markers.</summary>
public sealed class FanCurveGraph : FrameworkElement
{
    public static readonly DependencyProperty TempLowProperty = DependencyProperty.Register(nameof(TempLow), typeof(double), typeof(FanCurveGraph), new FrameworkPropertyMetadata(50d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SpeedLowProperty = DependencyProperty.Register(nameof(SpeedLow), typeof(double), typeof(FanCurveGraph), new FrameworkPropertyMetadata(45d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TempMidProperty = DependencyProperty.Register(nameof(TempMid), typeof(double), typeof(FanCurveGraph), new FrameworkPropertyMetadata(65d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SpeedMidProperty = DependencyProperty.Register(nameof(SpeedMid), typeof(double), typeof(FanCurveGraph), new FrameworkPropertyMetadata(68d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TempHighProperty = DependencyProperty.Register(nameof(TempHigh), typeof(double), typeof(FanCurveGraph), new FrameworkPropertyMetadata(75d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SpeedHighProperty = DependencyProperty.Register(nameof(SpeedHigh), typeof(double), typeof(FanCurveGraph), new FrameworkPropertyMetadata(95d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Gpu0EffectiveTempProperty = DependencyProperty.Register(nameof(Gpu0EffectiveTemp), typeof(double?), typeof(FanCurveGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Gpu1EffectiveTempProperty = DependencyProperty.Register(nameof(Gpu1EffectiveTemp), typeof(double?), typeof(FanCurveGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double TempLow { get => (double)GetValue(TempLowProperty); set => SetValue(TempLowProperty, value); }
    public double SpeedLow { get => (double)GetValue(SpeedLowProperty); set => SetValue(SpeedLowProperty, value); }
    public double TempMid { get => (double)GetValue(TempMidProperty); set => SetValue(TempMidProperty, value); }
    public double SpeedMid { get => (double)GetValue(SpeedMidProperty); set => SetValue(SpeedMidProperty, value); }
    public double TempHigh { get => (double)GetValue(TempHighProperty); set => SetValue(TempHighProperty, value); }
    public double SpeedHigh { get => (double)GetValue(SpeedHighProperty); set => SetValue(SpeedHighProperty, value); }
    public double? Gpu0EffectiveTemp { get => (double?)GetValue(Gpu0EffectiveTempProperty); set => SetValue(Gpu0EffectiveTempProperty, value); }
    public double? Gpu1EffectiveTemp { get => (double?)GetValue(Gpu1EffectiveTempProperty); set => SetValue(Gpu1EffectiveTempProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 175 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 80 || height < 60) return;

        var bgBrush = new SolidColorBrush(Color.FromRgb(10, 15, 22));
        var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(32, 46, 62)), 1);
        dc.DrawRoundedRectangle(bgBrush, borderPen, new Rect(0.5, 0.5, width - 1, height - 1), 8, 8);

        const double left = 34;
        const double right = 14;
        const double top = 14;
        const double bottom = 22;
        var plotW = Math.Max(20, width - left - right);
        var plotH = Math.Max(20, height - top - bottom);

        const double minT = 30d;
        const double maxT = 95d;
        const double minS = 20d;
        const double maxS = 100d;

        Point Map(double tempC, double speedPct)
        {
            var nx = Math.Clamp((tempC - minT) / (maxT - minT), 0d, 1d);
            var ny = Math.Clamp((speedPct - minS) / (maxS - minS), 0d, 1d);
            return new Point(left + nx * plotW, top + plotH - ny * plotH);
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 139, 152, 170)), 1);
        var labelBrush = new SolidColorBrush(Color.FromRgb(139, 152, 170));

        foreach (var pct in new[] { 40, 60, 80, 100 })
        {
            var p = Map(minT, pct);
            dc.DrawLine(gridPen, new Point(left, p.Y), new Point(left + plotW, p.Y));
            var ft = new FormattedText($"{pct}%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 9, labelBrush, dpi);
            dc.DrawText(ft, new Point(4, p.Y - 6));
        }

        foreach (var t in new[] { 40, 50, 60, 70, 80, 90 })
        {
            var p = Map(t, minS);
            dc.DrawLine(gridPen, new Point(p.X, top), new Point(p.X, top + plotH));
            var ft = new FormattedText($"{t}°", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 9, labelBrush, dpi);
            dc.DrawText(ft, new Point(p.X - 9, top + plotH + 4));
        }

        var t1 = Math.Clamp(TempLow, 30, 80);
        var s1 = Math.Clamp(SpeedLow, 30, 100);
        var t2 = Math.Clamp(Math.Max(t1 + 1, TempMid), 35, 90);
        var s2 = Math.Clamp(Math.Max(s1, SpeedMid), 30, 100);
        var t3 = Math.Clamp(Math.Max(t2 + 1, TempHigh), 45, 95);
        var s3 = Math.Clamp(Math.Max(s2, SpeedHigh), 30, 100);

        var p0 = Map(minT, s1);
        var p1 = Map(t1, s1);
        var p2 = Map(t2, s2);
        var p3 = Map(t3, s3);
        var pEnd = Map(maxT, s3);

        var fillGeom = new StreamGeometry();
        var lineGeom = new StreamGeometry();
        using (var fc = fillGeom.Open())
        using (var lc = lineGeom.Open())
        {
            lc.BeginFigure(p0, false, false);
            lc.LineTo(p1, true, true);
            lc.LineTo(p2, true, true);
            lc.LineTo(p3, true, true);
            lc.LineTo(pEnd, true, true);

            fc.BeginFigure(new Point(p0.X, top + plotH), true, true);
            fc.LineTo(p0, false, false);
            fc.LineTo(p1, false, false);
            fc.LineTo(p2, false, false);
            fc.LineTo(p3, false, false);
            fc.LineTo(pEnd, false, false);
            fc.LineTo(new Point(pEnd.X, top + plotH), false, false);
        }
        fillGeom.Freeze();
        lineGeom.Freeze();

        var fillBrush = new LinearGradientBrush(
            Color.FromArgb(75, 0, 229, 192),
            Color.FromArgb(6, 0, 229, 192),
            new Point(0, 0),
            new Point(0, 1));
        dc.DrawGeometry(fillBrush, null, fillGeom);

        var curvePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 229, 192)), 2.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        dc.DrawGeometry(null, curvePen, lineGeom);

        var nodeFill = new SolidColorBrush(Color.FromRgb(12, 20, 29));
        var nodePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 229, 192)), 2);
        foreach (var pt in new[] { p1, p2, p3 })
            dc.DrawEllipse(nodeFill, nodePen, pt, 4.2, 4.2);

        double EvalSpeed(double tc)
        {
            if (tc <= t1) return s1;
            if (tc >= t3) return s3;
            if (tc <= t2) return s1 + ((tc - t1) / (t2 - t1)) * (s2 - s1);
            return s2 + ((tc - t2) / (t3 - t2)) * (s3 - s2);
        }

        void DrawLiveGpuMarker(double? temp, Color color, string tag)
        {
            if (temp is not double tVal || !double.IsFinite(tVal) || tVal <= 0) return;
            var sVal = EvalSpeed(tVal);
            var markerPt = Map(tVal, sVal);
            var brush = new SolidColorBrush(color);
            var pen = new Pen(brush, 1.2) { DashStyle = DashStyles.Dash };
            dc.DrawLine(pen, new Point(markerPt.X, top), new Point(markerPt.X, top + plotH));
            dc.DrawEllipse(brush, new Pen(Brushes.White, 1), markerPt, 4.5, 4.5);
            var lbl = new FormattedText($"{tag}: {tVal:0}°→{sVal:0}%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 9, brush, dpi);
            var lblX = markerPt.X + 6 + lbl.Width > width - right ? markerPt.X - lbl.Width - 6 : markerPt.X + 6;
            var lblY = Math.Max(top + 2, markerPt.Y - 14);
            dc.DrawText(lbl, new Point(lblX, lblY));
        }

        DrawLiveGpuMarker(Gpu0EffectiveTemp, Color.FromRgb(251, 191, 36), "G0");
        DrawLiveGpuMarker(Gpu1EffectiveTemp, Color.FromRgb(96, 165, 250), "G1");
    }
}
