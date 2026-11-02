using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lycoris.UI;

/// <summary>
/// Clips a rounded <see cref="Border"/>'s child to the inside of its corners, so content painted to the edge
/// (hatched bands, inverted headers, progress bars, selection bars, first/last row backgrounds) follows the radius
/// instead of poking out.
/// Usage: <c>&lt;Border Style="{StaticResource Card.Flush}" ui:RoundClip.Content="True"&gt;</c>. The Card and Callout
/// styles already set it.
/// The clip is the border's inner area (inside BorderThickness, ignoring Padding) expressed in the child's coordinates,
/// so a padded child is never cut — only what reaches into the rounded corners is. Per-corner radii are honoured; the
/// inner radius is the outer one minus half the border thickness, exactly as <see cref="Border"/> paints its background.
/// </summary>
public static class RoundClip
{
    public static readonly DependencyProperty ContentProperty = DependencyProperty.RegisterAttached(
        "Content", typeof(bool), typeof(RoundClip), new PropertyMetadata(false, OnContentChanged));

    public static bool GetContent(DependencyObject d) => (bool)d.GetValue(ContentProperty);
    public static void SetContent(DependencyObject d, bool value) => d.SetValue(ContentProperty, value);

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        border.SizeChanged -= OnBorderSizeChanged;
        if (e.NewValue is true)
        {
            border.SizeChanged += OnBorderSizeChanged;
            Apply(border);
        }
        else if (border.Child is UIElement child)
        {
            child.ClearValue(UIElement.ClipProperty);
        }
    }

    private static void OnBorderSizeChanged(object sender, SizeChangedEventArgs e) => Apply((Border)sender);

    /// <summary>Inner corner radius for one corner: outer radius minus half the thicker adjacent edge (never negative).</summary>
    public static double InnerRadius(double outer, double edgeA, double edgeB) =>
        Math.Max(0, outer - 0.5 * Math.Max(edgeA, edgeB));

    /// <summary>Inner (top-left) corner radius for a border.</summary>
    public static double InnerRadius(CornerRadius outer, Thickness thickness) =>
        InnerRadius(outer.TopLeft, thickness.Left, thickness.Top);

    private static void Apply(Border border)
    {
        if (border.Child is not FrameworkElement child) return;
        if (child.ActualWidth <= 0 || child.ActualHeight <= 0) return;
        var t = border.BorderThickness;
        var w = border.ActualWidth - t.Left - t.Right;
        var h = border.ActualHeight - t.Top - t.Bottom;
        if (w <= 0 || h <= 0) return;

        // The child sits inside Padding/Margin; express the border's inner rectangle in the child's own coordinates.
        var offset = VisualTreeHelper.GetOffset(child);
        var inner = new Rect(t.Left - offset.X, t.Top - offset.Y, w, h);

        var r = border.CornerRadius;
        var tl = InnerRadius(r.TopLeft, t.Left, t.Top);
        var tr = InnerRadius(r.TopRight, t.Right, t.Top);
        var br = InnerRadius(r.BottomRight, t.Right, t.Bottom);
        var bl = InnerRadius(r.BottomLeft, t.Left, t.Bottom);

        var clip = tl == tr && tr == br && br == bl
            ? new RectangleGeometry(inner, Math.Min(tl, Math.Min(w, h) / 2), Math.Min(tl, Math.Min(w, h) / 2))
            : RoundedRect(inner, tl, tr, br, bl);
        clip.Freeze();
        child.Clip = clip;
    }

    /// <summary>Rectangle with an individual radius per corner; radii that overflow an edge are scaled down together.</summary>
    private static Geometry RoundedRect(Rect r, double tl, double tr, double br, double bl)
    {
        var f = 1.0;
        if (tl + tr > r.Width) f = Math.Min(f, r.Width / (tl + tr));
        if (bl + br > r.Width) f = Math.Min(f, r.Width / (bl + br));
        if (tl + bl > r.Height) f = Math.Min(f, r.Height / (tl + bl));
        if (tr + br > r.Height) f = Math.Min(f, r.Height / (tr + br));
        tl *= f; tr *= f; br *= f; bl *= f;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.Left + tl, r.Top), true, true);
            ctx.LineTo(new Point(r.Right - tr, r.Top), false, false);
            if (tr > 0) ctx.ArcTo(new Point(r.Right, r.Top + tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r.Right, r.Bottom - br), false, false);
            if (br > 0) ctx.ArcTo(new Point(r.Right - br, r.Bottom), new Size(br, br), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r.Left + bl, r.Bottom), false, false);
            if (bl > 0) ctx.ArcTo(new Point(r.Left, r.Bottom - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(r.Left, r.Top + tl), false, false);
            if (tl > 0) ctx.ArcTo(new Point(r.Left + tl, r.Top), new Size(tl, tl), 0, false, SweepDirection.Clockwise, false, false);
        }
        return g;
    }
}
