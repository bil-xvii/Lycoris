using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Lycoris.UI;

/// <summary>
/// Two app-wide, view-independent polish rules, installed once by the main window (no per-view hacks):
///  1. UI Automation names — a button whose content is a StackPanel (glyph + text) exposes no name to UIA / screen readers.
///     When a Button / ToggleButton / CheckBox / RadioButton / ComboBox / TextBox has no <see cref="AutomationProperties.NameProperty"/>,
///     it gets the visible text of its content (icon glyphs skipped); a switch without text takes the title of its row.
///  2. A text cut by <see cref="TextBlock.TextTrimming"/> gets its full text as a tooltip on hover (paths, long titles) — the tooltip
///     only repeats what the page already says where it can (Obvious UI rule 5).
/// </summary>
public static class UiA11y
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnControlLoaded));
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnControlLoaded));
        EventManager.RegisterClassHandler(typeof(TextBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnControlLoaded));
        EventManager.RegisterClassHandler(typeof(TextBlock), UIElement.MouseEnterEvent, new System.Windows.Input.MouseEventHandler(OnTextEnter));
    }

    // ───────────────────────────── UIA names ─────────────────────────────

    private static void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        Apply(fe);
        // A button's text is often bound («ПРИМЕНИТЬ · 2»): refresh when the item gets a new data context or is shown again.
        fe.DataContextChanged -= OnChanged;
        fe.DataContextChanged += OnChanged;
        fe.IsVisibleChanged -= OnChanged2;
        fe.IsVisibleChanged += OnChanged2;
    }

    private static void OnChanged(object sender, DependencyPropertyChangedEventArgs e) => Apply((FrameworkElement)sender, refresh: true);
    private static void OnChanged2(object sender, DependencyPropertyChangedEventArgs e) { if (e.NewValue is true) Apply((FrameworkElement)sender, refresh: true); }

    /// <summary>Marker: the name was derived by us, so it may be derived again; a name set in XAML is never overwritten.</summary>
    private static readonly DependencyProperty DerivedProperty = DependencyProperty.RegisterAttached("Derived", typeof(bool), typeof(UiA11y));

    private static void Apply(FrameworkElement fe, bool refresh = false)
    {
        try
        {
            var current = AutomationProperties.GetName(fe);
            if (!string.IsNullOrEmpty(current) && !(refresh && (bool)fe.GetValue(DerivedProperty))) return;
            var name = Derive(fe);
            if (string.IsNullOrWhiteSpace(name) || name == current) return;
            AutomationProperties.SetName(fe, name);
            fe.SetValue(DerivedProperty, true);
        }
        catch
        {
            // Naming is a nicety — never let it break a page.
        }
    }

    /// <summary>The visible words of a control: the string content, else the non-icon TextBlocks inside it, else its row's title.</summary>
    public static string? Derive(FrameworkElement fe)
    {
        if (fe is ContentControl { Content: string s } && !string.IsNullOrWhiteSpace(s)) return Clean(s);
        if (fe is TextBox tb)
        {
            if (tb.Tag is string tag && !string.IsNullOrWhiteSpace(tag)) return Clean(tag);
            if (fe.ToolTip is string tt0) return Clean(tt0);
            return RowTitle(fe);
        }
        var own = fe is ContentControl ? TextInside(fe) : null;
        if (!string.IsNullOrWhiteSpace(own)) return own;
        if (fe.ToolTip is string tt && !string.IsNullOrWhiteSpace(tt)) return Clean(tt);
        // A switch / checkbox / combo box without words: the title of the row it sits in.
        return RowTitle(fe);
    }

    private static string Clean(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Visible, non-icon text of the subtree (in tree order), joined with spaces.</summary>
    private static string? TextInside(DependencyObject root)
    {
        var parts = new List<string>();
        void Walk(DependencyObject n)
        {
            if (n is TextBlock t)
            {
                if (!IsIcon(t) && !string.IsNullOrWhiteSpace(t.Text)) parts.Add(Clean(t.Text));
                return;
            }
            if (n is UIElement { Visibility: not Visibility.Visible }) return;
            var count = n is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(n) : 0;
            for (var i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(n, i));
        }
        Walk(root);
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static bool IsIcon(TextBlock t) =>
        t.FontFamily.Source.Contains("Icons", StringComparison.OrdinalIgnoreCase) || t.FontFamily.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase);

    private static string? RowTitle(FrameworkElement fe)
    {
        DependencyObject? up = fe;
        for (var level = 0; level < 5 && up is not null; level++)
        {
            up = VisualTreeHelper.GetParent(up);
            if (up is null) break;
            // The first non-icon text of the nearest ancestor that has some outside this control.
            string? found = null;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(up);
            while (queue.Count > 0 && found is null)
            {
                var n = queue.Dequeue();
                if (ReferenceEquals(n, fe)) continue;
                if (n is TextBlock t) { if (!IsIcon(t) && !string.IsNullOrWhiteSpace(t.Text)) found = Clean(t.Text); continue; }
                var c = n is Visual ? VisualTreeHelper.GetChildrenCount(n) : 0;
                for (var i = 0; i < c; i++) queue.Enqueue(VisualTreeHelper.GetChild(n, i));
            }
            if (found is not null) return found;
            if (up is ScrollViewer or Window) break;
        }
        return null;
    }

    // ───────────────────────────── Tooltip for cut text ─────────────────────────────

    private static void OnTextEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not TextBlock t || t.TextTrimming == TextTrimming.None || t.ToolTip is not null) return;
        if (string.IsNullOrEmpty(t.Text) || t.ActualWidth <= 0) return;
        try
        {
            var ft = new FormattedText(t.Text, CultureInfo.CurrentUICulture, t.FlowDirection,
                new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(t).PixelsPerDip);
            var single = t.TextWrapping == TextWrapping.NoWrap;
            var cut = single ? ft.WidthIncludingTrailingWhitespace > t.ActualWidth + 0.5
                             : t.MaxHeight < double.PositiveInfinity && t.ActualHeight >= t.MaxHeight - 0.5;
            if (cut) t.ToolTip = t.Text;
        }
        catch
        {
            // A tooltip is a nicety.
        }
    }
}
