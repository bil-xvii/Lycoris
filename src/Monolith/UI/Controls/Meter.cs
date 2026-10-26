using System.Windows;
using System.Windows.Media;

namespace Monolith.UI.Controls;

/// <summary>
/// Segmented horizontal load meter (0–100). Lit segments are white, the rest are dark.
/// The industrial "LED bar" look of the dashboard.
/// </summary>
public sealed class Meter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Meter), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(int), typeof(Meter), new FrameworkPropertyMetadata(32, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(Meter), new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Meter), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(Meter),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Above this value the lit segments switch to the hatched "hot" look.</summary>
    public static readonly DependencyProperty HotThresholdProperty = DependencyProperty.Register(
        nameof(HotThreshold), typeof(double), typeof(Meter), new FrameworkPropertyMetadata(101d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Segments { get => (int)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double HotThreshold { get => (double)GetValue(HotThresholdProperty); set => SetValue(HotThresholdProperty, value); }

    private static readonly Pen HotPen = CreateHotPen();

    private static Pen CreateHotPen()
    {
        var p = new Pen(Brushes.White, 1);
        p.Freeze();
        return p;
    }

    public Meter()
    {
        SnapsToDevicePixels = true;
        Height = 10;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var n = Math.Max(1, Segments);
        if (w <= 0 || h <= 0) return;

        var segW = (w - Gap * (n - 1)) / n;
        if (segW < 1) segW = 1;
        var lit = (int)Math.Round(Math.Clamp(Value, 0, 100) / 100d * n);
        var hot = Value >= HotThreshold;

        for (var i = 0; i < n; i++)
        {
            var x = Math.Round(i * (segW + Gap));
            var rect = new Rect(x, 0, Math.Max(1, Math.Round(segW)), h);
            if (i < lit)
            {
                if (hot)
                    dc.DrawRectangle(null, HotPen, new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1));
                else
                    dc.DrawRectangle(Fill, null, rect);
            }
            else
            {
                dc.DrawRectangle(Track, null, rect);
            }
        }
    }
}
