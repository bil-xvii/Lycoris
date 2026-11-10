using Lycoris.Modules.Optimize;
using Xunit;

namespace Lycoris.Tests;

/// <summary>Profile tiles: one row while each tile is wide enough for its title, otherwise two rows (1024 px screen, 10.11).</summary>
public class OptimizeTileColumnsTests
{
    [Fact] public void Wide_row_keeps_all_six_in_one_row() => Assert.Equal(6, OptimizeView.ColumnsFor(1100, 6));

    [Fact] public void Narrow_row_splits_into_two_rows_of_three() => Assert.Equal(3, OptimizeView.ColumnsFor(776, 6));

    [Fact] public void Odd_count_rounds_up() => Assert.Equal(3, OptimizeView.ColumnsFor(500, 5));

    [Fact] public void Exactly_the_minimum_width_stays_in_one_row() => Assert.Equal(6, OptimizeView.ColumnsFor(OptimizeView.MinTileWidth * 6, 6));

    [Fact] public void Before_layout_or_without_tiles_nothing_breaks()
    {
        Assert.Equal(1, OptimizeView.ColumnsFor(0, 0));
        Assert.Equal(1, OptimizeView.ColumnsFor(50, 1));
    }
}
