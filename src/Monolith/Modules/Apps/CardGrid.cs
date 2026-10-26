using System.Windows;
using System.Windows.Controls;

namespace Monolith.Modules.Apps;

/// <summary>
/// Responsive card grid: as many equal columns as fit <see cref="MinColumnWidth"/>, every card in a row gets the
/// height of the tallest one (aligned edges), collapsed children take no cell.
/// </summary>
public sealed class CardGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(CardGrid),
        new FrameworkPropertyMetadata(300d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(CardGrid),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(CardGrid),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    private readonly List<double> _rowHeights = [];
    private int _measuredColumns;

    /// <summary>Number of columns for a given width (pure; exposed for tests).</summary>
    public static int ColumnsFor(double width, double minColumnWidth, double gap, int maxColumns)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return 1;
        var cols = (int)Math.Floor((width + gap) / (Math.Max(1, minColumnWidth) + gap));
        return Math.Clamp(cols, 1, Math.Max(1, maxColumns));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? MinColumnWidth * 3 + Gap * 2 : availableSize.Width;
        var cols = ColumnsFor(width, MinColumnWidth, Gap, MaxColumns);
        var colWidth = Math.Max(0, (width - Gap * (cols - 1)) / cols);

        _rowHeights.Clear();
        _measuredColumns = cols;
        var index = 0;
        double rowHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(colWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            index++;
            if (index % cols == 0)
            {
                _rowHeights.Add(rowHeight);
                rowHeight = 0;
            }
        }
        if (index % cols != 0) _rowHeights.Add(rowHeight);

        var height = _rowHeights.Sum() + Gap * Math.Max(0, _rowHeights.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cols = ColumnsFor(finalSize.Width, MinColumnWidth, Gap, MaxColumns);
        var colWidth = Math.Max(0, (finalSize.Width - Gap * (cols - 1)) / cols);
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

        // Row heights from the measure pass; recomputed if the final width gives a different column count.
        var rows = (visible.Count + cols - 1) / cols;
        var heights = _measuredColumns == cols && _rowHeights.Count == rows ? _rowHeights.ToList()
            : Enumerable.Range(0, rows).Select(r => visible.Skip(r * cols).Take(cols).Max(c => c.DesiredSize.Height)).ToList();

        foreach (UIElement child in InternalChildren)
            if (child.Visibility == Visibility.Collapsed) child.Arrange(new Rect(0, 0, 0, 0));

        double y = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            var row = i / cols;
            var col = i % cols;
            if (col == 0 && row > 0) y += heights[row - 1] + Gap;
            visible[i].Arrange(new Rect(col * (colWidth + Gap), y, colWidth, heights[row]));
        }
        return finalSize;
    }
}
