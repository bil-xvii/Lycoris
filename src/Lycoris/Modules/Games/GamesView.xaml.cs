using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Lycoris.Modules.Games;

public partial class GamesView : UserControl
{
    private const double MinCardWidth = 232;
    private const double CardGap = 12;

    /// <summary>Number of card columns that fit the library width (bound by the UniformGrid).</summary>
    public static readonly DependencyProperty CardColumnsProperty = DependencyProperty.Register(
        nameof(CardColumns), typeof(int), typeof(GamesView), new PropertyMetadata(3));

    public int CardColumns
    {
        get => (int)GetValue(CardColumnsProperty);
        set => SetValue(CardColumnsProperty, value);
    }

    public GamesView()
    {
        InitializeComponent();
    }

    /// <summary>Each card carries a 12 px right/bottom gap; the vertical scrollbar overlays the last column's gap.</summary>
    private void CardsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        if (width <= 0) return;
        var columns = Math.Max(1, (int)Math.Floor(width / (MinCardWidth + CardGap)));
        if (columns != CardColumns) CardColumns = columns;
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not GamesViewModel vm) return;
        vm.CancelEditorCommand.Execute(null);
        e.Handled = true;
    }
}

/// <summary>True only when every bound value is true (e.g. game available AND boost allowed).</summary>
public sealed class AllTrueConverter : IMultiValueConverter
{
    public static AllTrueConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
