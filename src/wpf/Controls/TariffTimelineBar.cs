using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PearlRigCore.Controls;

/// <summary>Renders a 24-hour visual timeline showing Day/Night electricity tariff zones and the current local time marker.</summary>
public sealed class TariffTimelineBar : FrameworkElement
{
    public static readonly DependencyProperty DayStartHourProperty = DependencyProperty.Register(nameof(DayStartHour), typeof(int), typeof(TariffTimelineBar), new FrameworkPropertyMetadata(7, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty NightStartHourProperty = DependencyProperty.Register(nameof(NightStartHour), typeof(int), typeof(TariffTimelineBar), new FrameworkPropertyMetadata(23, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DualTariffEnabledProperty = DependencyProperty.Register(nameof(DualTariffEnabled), typeof(bool), typeof(TariffTimelineBar), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public int DayStartHour { get => (int)GetValue(DayStartHourProperty); set => SetValue(DayStartHourProperty, value); }
    public int NightStartHour { get => (int)GetValue(NightStartHourProperty); set => SetValue(NightStartHourProperty, value); }
    public bool DualTariffEnabled { get => (bool)GetValue(DualTariffEnabledProperty); set => SetValue(DualTariffEnabledProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 42 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 60 || height < 24) return;

        const double padX = 4;
        var barW = Math.Max(20, width - padX * 2);
        const double barY = 4;
        const double barH = 16;

        var dayBrush = new SolidColorBrush(Color.FromRgb(29, 78, 72));
        var nightBrush = new SolidColorBrush(Color.FromRgb(49, 46, 129));
        var segmentPen = new Pen(new SolidColorBrush(Color.FromRgb(15, 23, 34)), 1);

        var dayStart = Math.Clamp(DayStartHour, 0, 23);
        var nightStart = Math.Clamp(NightStartHour, 0, 23);

        bool IsNightHour(int hour)
        {
            if (!DualTariffEnabled || dayStart == nightStart) return false;
            return nightStart > dayStart
                ? hour >= nightStart || hour < dayStart
                : hour >= nightStart && hour < dayStart;
        }

        var hourW = barW / 24d;
        for (var h = 0; h < 24; h++)
        {
            var rect = new Rect(padX + h * hourW, barY, Math.Max(1, hourW - 0.5), barH);
            dc.DrawRoundedRectangle(IsNightHour(h) ? nightBrush : dayBrush, segmentPen, rect, 2, 2);
        }

        var now = DateTime.Now;
        var currentFraction = (now.Hour + now.Minute / 60d) / 24d;
        var markerX = padX + currentFraction * barW;
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0, 229, 192)), 2), new Point(markerX, barY - 2), new Point(markerX, barY + barH + 2));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelBrush = new SolidColorBrush(Color.FromRgb(139, 152, 170));
        foreach (var tickHour in new[] { 0, 6, 12, 18, 24 })
        {
            var tx = padX + (tickHour / 24d) * barW;
            var text = tickHour == 24 ? "24:00" : $"{tickHour:00}:00";
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 9, labelBrush, dpi);
            var drawX = tickHour == 24 ? tx - ft.Width : tickHour == 0 ? tx : tx - ft.Width / 2;
            dc.DrawText(ft, new Point(Math.Max(0, drawX), barY + barH + 4));
        }
    }
}
