using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Robot.Dashboard.Wpf;

public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double?[]), typeof(TrendChart),
        new FrameworkPropertyMetadata(Array.Empty<double?>(), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(TrendChart),
        new FrameworkPropertyMetadata(Brushes.Turquoise, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FixedMaximumProperty = DependencyProperty.Register(
        nameof(FixedMaximum), typeof(double), typeof(TrendChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double?[] Values { get => (double?[])GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double FixedMaximum { get => (double)GetValue(FixedMaximumProperty); set => SetValue(FixedMaximumProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth - 8, height = ActualHeight - 22;
        if (width <= 0 || height <= 0) return;
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(43, 57, 78)), 1);
        for (int i = 0; i < 3; i++)
        {
            double y = 4 + height * i / 2;
            dc.DrawLine(gridPen, new Point(4, y), new Point(width + 4, y));
        }
        double maximum = FixedMaximum > 0 ? FixedMaximum :
            Math.Max(100, Values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max() * 1.1);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Label(string text, Point at) => dc.DrawText(
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, Brushes.SlateGray, dpi), at);
        Label("30s ago", new Point(6, height + 6));
        Label($"scale 0 - {maximum:F0}", new Point(width / 2 - 35, height + 6));
        Label("now", new Point(width - 22, height + 6));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            bool segment = false;
            for (int i = 0; i < Values.Length; i++)
            {
                if (Values[i] is not double value) { segment = false; continue; }
                double x = 4 + width * (299 - Values.Length + 1 + i) / 299;
                double y = 4 + height * (1 - Math.Clamp(value / maximum, 0, 1));
                var point = new Point(x, y);
                if (!segment) { context.BeginFigure(point, false, false); segment = true; }
                else context.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 2), geometry);
    }
}
