using System.Windows;
using System.Windows.Controls;

namespace Monolith.UI.Controls;

/// <summary>
/// Standard page header: huge condensed title, subtitle, optional actions on the right.
/// Inside a host page (<see cref="IsEmbeddedProperty"/> set on an ancestor) only the actions are shown.
/// </summary>
public partial class PageHeader : UserControl
{
    /// <summary>Kept so existing page bindings stay valid; the index is no longer displayed.</summary>
    public static readonly DependencyProperty IndexProperty = DependencyProperty.Register(nameof(Index), typeof(string), typeof(PageHeader), new("00"));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new(""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new(""));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader), new(null));

    /// <summary>
    /// Inherited: set on a host element (e.g. the maintenance page's tab content) to hide the title and subtitle of
    /// every page header inside it; the actions stay visible.
    /// </summary>
    public static readonly DependencyProperty IsEmbeddedProperty = DependencyProperty.RegisterAttached(
        "IsEmbedded", typeof(bool), typeof(PageHeader), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsEmbedded(DependencyObject d) => (bool)d.GetValue(IsEmbeddedProperty);
    public static void SetIsEmbedded(DependencyObject d, bool value) => d.SetValue(IsEmbeddedProperty, value);

    public string Index { get => (string)GetValue(IndexProperty); set => SetValue(IndexProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public PageHeader()
    {
        InitializeComponent();
    }
}
