using System.Windows;
using System.Windows.Controls;

namespace Monolith.UI.Controls;

/// <summary>Standard page header: index chip, huge condensed title, subtitle, optional actions on the right.</summary>
public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty IndexProperty = DependencyProperty.Register(nameof(Index), typeof(string), typeof(PageHeader), new("00"));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new(""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new(""));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader), new(null));

    public string Index { get => (string)GetValue(IndexProperty); set => SetValue(IndexProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }

    public PageHeader()
    {
        InitializeComponent();
    }
}
