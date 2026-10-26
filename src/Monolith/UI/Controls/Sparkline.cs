using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace Monolith.UI.Controls;

/// <summary>
/// Minimal line chart for live history (CPU %, GPU % ...). Values are 0..Maximum.
/// Bind <see cref="Values"/> to an ObservableCollection&lt;double&gt;; it redraws on every change.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AreaFillProperty = DependencyProperty.Register(
        nameof(AreaFill), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(Sparkline), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    /// <summary>How many points fit the full width.</summary>
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? AreaFill { get => (Brush?)GetValue(AreaFillProperty); set => SetValue(AreaFillProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)), 1));

    private static Pen Freeze(Pen p) { p.Freeze(); return p; }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged oldC) oldC.CollectionChanged -= s.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newC) newC.CollectionChanged += s.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (ShowGrid)
        {
            for (var i = 1; i < 4; i++)
            {
                var y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
            }
        }

        if (Values is null) return;
        var points = new List<double>();
        foreach (var v in Values)
            points.Add(v is double d ? d : Convert.ToDouble(v));
        if (points.Count < 2) return;

        var cap = Math.Max(2, Capacity);
        if (points.Count > cap) points = points.GetRange(points.Count - cap, cap);
        var step = w / (cap - 1);
        var max = Maximum <= 0 ? 1 : Maximum;
        var startX = w - step * (points.Count - 1);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (var i = 0; i < points.Count; i++)
            {
                var x = startX + i * step;
                var y = h - Math.Clamp(points[i] / max, 0, 1) * (h - 2) - 1;
                if (i == 0) ctx.BeginFigure(new Point(x, y), false, false);
                else ctx.LineTo(new Point(x, y), true, false);
            }
        }
        geo.Freeze();

        if (AreaFill is not null)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(startX, h), true, true);
                for (var i = 0; i < points.Count; i++)
                {
                    var x = startX + i * step;
                    var y = h - Math.Clamp(points[i] / max, 0, 1) * (h - 2) - 1;
                    ctx.LineTo(new Point(x, y), false, false);
                }
                ctx.LineTo(new Point(w, h), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(AreaFill, null, area);
        }

        var pen = new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
