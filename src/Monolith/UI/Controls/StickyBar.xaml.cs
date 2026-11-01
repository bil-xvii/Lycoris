using System.Windows;
using System.Windows.Controls;

namespace Monolith.UI.Controls;

/// <summary>
/// The page's sticky action bar («ВЫБРАНО N · ПРИМЕНИТЬ · N»). Lives outside the page's ScrollViewer so the main
/// action is always visible. Usage:
/// <code>
/// &lt;c:StickyBar Grid.Row="2" Margin="0,12,0,0" Count="{Binding SelectedCount}" Detail="{Binding ChangeCountText}"
///              Text="Перед применением покажем точный список и создадим точку восстановления."&gt;
///     &lt;c:StickyBar.Actions&gt; … [СНЯТЬ ВЫБОР] [Btn.Hero ПРИМЕНИТЬ · N] … &lt;/c:StickyBar.Actions&gt;
/// &lt;/c:StickyBar&gt;
/// </code>
/// </summary>
public partial class StickyBar : UserControl
{
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(nameof(Count), typeof(int), typeof(StickyBar), new(0));
    public static readonly DependencyProperty CountLabelProperty = DependencyProperty.Register(nameof(CountLabel), typeof(string), typeof(StickyBar), new("ВЫБРАНО"));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(StickyBar), new(null));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(StickyBar), new(""));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(StickyBar), new(null));

    /// <summary>The number shown after <see cref="CountLabel"/>.</summary>
    public int Count { get => (int)GetValue(CountProperty); set => SetValue(CountProperty, value); }
    /// <summary>Upper-case label before the number («ВЫБРАНО», «К ОЧИСТКЕ»).</summary>
    public string CountLabel { get => (string)GetValue(CountLabelProperty); set => SetValue(CountLabelProperty, value); }
    /// <summary>Optional second upper-case fact («БУДЕТ ИЗМЕНЕНО 2»); hidden when empty.</summary>
    public string? Detail { get => (string?)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    /// <summary>One muted sentence (what happens next).</summary>
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>Buttons on the right; the main one is a Btn.Hero.</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public StickyBar()
    {
        InitializeComponent();
    }
}
