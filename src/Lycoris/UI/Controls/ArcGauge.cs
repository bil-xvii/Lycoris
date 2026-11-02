using System.Windows;
using System.Windows.Media;

namespace Lycoris.UI.Controls;

/// <summary>
/// 270° tick gauge (0–100) used for the system health score. Lit ticks are white.
/// Put the number in the middle with any content on top of it.
/// </summary>
public sealed class ArcGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TicksProperty = DependencyProperty.Register(
        nameof(Ticks), typeof(int), typeof(ArcGauge), new FrameworkPropertyMetadata(54, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickLengthProperty = DependencyProperty.Register(
        nameof(TickLength), typeof(double), typeof(ArcGauge), new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Ticks { get => (int)GetValue(TicksProperty); set => SetValue(TicksProperty, value); }
    public double TickLength { get => (double)GetValue(TickLengthProperty); set => SetValue(TickLengthProperty, value); }

    // Ticks have round caps (the rounded shape language); they are shortened by the cap so the outer extent is unchanged.
    private static readonly Pen LitPen = Freeze(RoundCaps(new Pen(Brushes.White, 3)));
    private static readonly Pen DimPen = Freeze(RoundCaps(new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), 3)));
    private static readonly Pen MajorPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x47, 0x47, 0x47)), 1));

    private static Pen Freeze(Pen p) { p.Freeze(); return p; }

    private static Pen RoundCaps(Pen p)
    {
        p.StartLineCap = PenLineCap.Round;
        p.EndLineCap = PenLineCap.Round;
        return p;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var rOuter = size / 2 - 2;
        var rInner = rOuter - TickLength;
        var n = Math.Max(2, Ticks);
        var lit = (int)Math.Round(Math.Clamp(Value, 0, 100) / 100d * n);

        const double start = 135, sweep = 270;
        var cap = LitPen.Thickness / 2;
        for (var i = 0; i < n; i++)
        {
            var a = (start + sweep * i / (n - 1)) * Math.PI / 180;
            var p1 = new Point(c.X + Math.Cos(a) * (rInner + cap), c.Y + Math.Sin(a) * (rInner + cap));
            var p2 = new Point(c.X + Math.Cos(a) * (rOuter - cap), c.Y + Math.Sin(a) * (rOuter - cap));
            dc.DrawLine(i < lit ? LitPen : DimPen, p1, p2);
        }

        // Thin inner ring
        var rRing = rInner - 8;
        if (rRing > 4) dc.DrawEllipse(null, MajorPen, c, rRing, rRing);
    }
}
