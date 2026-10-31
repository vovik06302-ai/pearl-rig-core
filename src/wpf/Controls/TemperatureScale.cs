using System.Windows;
using System.Windows.Media;

namespace PearlRigCore.Controls;

/// <summary>A compact vertical temperature scale: cool colors at the bottom, hot colors at the top.</summary>
public sealed class TemperatureScale : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(TemperatureScale),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HasDataProperty = DependencyProperty.Register(
        nameof(HasData), typeof(bool), typeof(TemperatureScale),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsOverheatedProperty = DependencyProperty.Register(
        nameof(IsOverheated), typeof(bool), typeof(TemperatureScale),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool HasData { get => (bool)GetValue(HasDataProperty); set => SetValue(HasDataProperty, value); }
    public bool IsOverheated { get => (bool)GetValue(IsOverheatedProperty); set => SetValue(IsOverheatedProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2) return;

        var track = new Rect(1, 1, width - 2, height - 2);
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(31, 43, 57)), null, track, 4, 4);
        if (!HasData) return;

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 1),
            EndPoint = new Point(0, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0, 194, 176), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(65, 204, 130), 0.34));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(250, 208, 75), 0.58));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(255, 133, 64), 0.78));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(255, 55, 75), 1));

        var ratio = Math.Clamp((Value - 35d) / (90d - 35d), 0d, 1d);
        var markerY = track.Bottom - ratio * track.Height;
        drawingContext.PushClip(new RectangleGeometry(new Rect(track.Left, markerY, track.Width, Math.Max(0.1, track.Bottom - markerY))));
        drawingContext.DrawRoundedRectangle(gradient, null, track, 4, 4);
        drawingContext.Pop();

        var color = IsOverheated ? Color.FromRgb(255, 62, 78) : Colors.White;
        if (IsOverheated)
        {
            DrawMarker(drawingContext, track, markerY, Color.FromArgb(45, 255, 35, 55), 9);
            DrawMarker(drawingContext, track, markerY, Color.FromArgb(95, 255, 35, 55), 5);
        }
        DrawMarker(drawingContext, track, markerY, color, 2);
    }

    private static void DrawMarker(DrawingContext drawingContext, Rect track, double y, Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawingContext.DrawLine(pen, new Point(track.Left - 1, y), new Point(track.Right + 1, y));
    }
}
