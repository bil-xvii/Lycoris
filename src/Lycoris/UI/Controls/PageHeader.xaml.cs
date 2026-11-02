using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lycoris.UI.Controls;

/// <summary>
/// Standard page header (one page anatomy): condensed title + subtitle on the left; on the right «проверено HH:MM»
/// (<see cref="Checked"/>), the ONE word «ОБНОВИТЬ» (<see cref="RefreshCommand"/>), secondary <see cref="Actions"/>
/// and the page's ONE main button (<see cref="Primary"/>, a Btn.Primary).
/// Inside a host page (<see cref="IsEmbeddedProperty"/> set on an ancestor) only the right side is shown.
/// <code>
/// &lt;c:PageHeader Title="{Binding Title}" Subtitle="{Binding Subtitle}" Checked="{Binding CheckedText}"
///               RefreshCommand="{Binding RefreshCommand}"&gt;
///     &lt;c:PageHeader.Primary&gt;&lt;Button Style="{StaticResource Btn.Primary}" …/&gt;&lt;/c:PageHeader.Primary&gt;
/// &lt;/c:PageHeader&gt;
/// </code>
/// </summary>
public partial class PageHeader : UserControl, System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Kept so existing page bindings stay valid; the index is no longer displayed.</summary>
    public static readonly DependencyProperty IndexProperty = DependencyProperty.Register(nameof(Index), typeof(string), typeof(PageHeader), new("00"));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader),
        new PropertyMetadata("", null, (_, v) => Loc.Sink(v as string))); // safety net: a page title the tables know is shown translated
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader),
        new PropertyMetadata("", null, (_, v) => Loc.Sink(v as string)));
    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageHeader), new(null));
    public static readonly DependencyProperty PrimaryProperty = DependencyProperty.Register(nameof(Primary), typeof(object), typeof(PageHeader),
        new(null, (d, _) => ((PageHeader)d).Raise(nameof(HasPrimary))));
    public static readonly DependencyProperty CheckedProperty = DependencyProperty.Register(nameof(Checked), typeof(string), typeof(PageHeader),
        new(null, (d, _) => ((PageHeader)d).Raise(nameof(HasChecked))));
    public static readonly DependencyProperty RefreshCommandProperty = DependencyProperty.Register(nameof(RefreshCommand), typeof(ICommand), typeof(PageHeader),
        new(null, (d, _) => ((PageHeader)d).Raise(nameof(HasRefresh))));
    public static readonly DependencyProperty RefreshTextProperty = DependencyProperty.Register(nameof(RefreshText), typeof(string), typeof(PageHeader), new(Loc.T("ОБНОВИТЬ")));

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
    /// <summary>Secondary outlined buttons (a horizontal StackPanel).</summary>
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    /// <summary>The ONE main button of the page (Btn.Primary); hidden when null.</summary>
    public object? Primary { get => GetValue(PrimaryProperty); set => SetValue(PrimaryProperty, value); }
    /// <summary>«проверено 15:40» — when the page's data was last read; hidden when empty.</summary>
    public string? Checked { get => (string?)GetValue(CheckedProperty); set => SetValue(CheckedProperty, value); }
    /// <summary>The «ОБНОВИТЬ» button's command; the button is hidden when null.</summary>
    public ICommand? RefreshCommand { get => (ICommand?)GetValue(RefreshCommandProperty); set => SetValue(RefreshCommandProperty, value); }
    /// <summary>Caption of the refresh button — «ОБНОВИТЬ» everywhere (one word for one thing).</summary>
    public string RefreshText { get => (string)GetValue(RefreshTextProperty); set => SetValue(RefreshTextProperty, value); }

    public bool HasPrimary => Primary is not null;
    public bool HasChecked => !string.IsNullOrWhiteSpace(Checked);
    public bool HasRefresh => RefreshCommand is not null;

    /// <summary>«проверено HH:MM» for <see cref="Checked"/>, or null before the first read.</summary>
    public static string? CheckedText(DateTime? time) => time is { } t ? Loc.Tf("проверено {0:HH:mm}", t) : null;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new(name));

    public PageHeader()
    {
        InitializeComponent();
    }
}
