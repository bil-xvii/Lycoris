using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Monolith.UI;

/// <summary>
/// Clips a rounded <see cref="Border"/>'s child to the inside of its corners, so content painted to the edge
/// (hatched bands, inverted headers, progress bars, selection bars) follows the radius instead of poking out.
/// Usage: <c>&lt;Border Style="{StaticResource Card.Flush}" ui:RoundClip.Content="True"&gt;</c>.
/// Uses the top-left radius of the border; the inner radius is reduced by the border thickness.
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

    /// <summary>Inner corner radius for a border: outer radius minus the thickest adjacent edge (never negative).</summary>
    public static double InnerRadius(CornerRadius outer, Thickness thickness) =>
        Math.Max(0, outer.TopLeft - Math.Max(thickness.Left, thickness.Top));

    private static void Apply(Border border)
    {
        if (border.Child is not FrameworkElement child) return;
        var w = child.ActualWidth;
        var h = child.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var r = InnerRadius(border.CornerRadius, border.BorderThickness);
        var clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        clip.Freeze();
        child.Clip = clip;
    }
}
