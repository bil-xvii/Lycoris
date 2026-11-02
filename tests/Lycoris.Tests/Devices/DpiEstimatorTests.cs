using Lycoris.Modules.Devices.Input;

namespace Lycoris.Tests.Devices;

/// <summary>DPI from counts over a known distance (pure).</summary>
public sealed class DpiEstimatorTests
{
    private static long Counts(double dpi, double cm) => (long)Math.Round(dpi * cm / 2.54);

    [Fact]
    public void Three_runs_at_1600_over_10_cm()
    {
        var runs = new[] { new DpiRun(Counts(1600, 10), 0, 10), new DpiRun(Counts(1600, 10) - 3, 40, 10), new DpiRun(Counts(1600, 10) + 2, -25, 10) };
        Assert.All(runs, r => Assert.True(DpiEstimator.IsValid(r)));
        var e = DpiEstimator.Combine(runs.Select(r => r.Dpi))!;
        Assert.InRange(e.Dpi, 1599, 1601);
        Assert.True(e.SpreadPercent < 1);
        Assert.False(e.Inconsistent);
        Assert.Equal("≈ 1600 DPI (±1 %)", DpiEstimator.ResultText(e));
    }

    [Fact]
    public void Diagonal_motion_counts_the_straight_line()
    {
        // 3 cm right and 4 cm down at 800 DPI over a 5-cm line.
        var run = new DpiRun(Counts(800, 3), Counts(800, 4), 5);
        Assert.InRange(run.Dpi, 799, 801);
    }

    [Fact]
    public void Median_is_robust_to_an_outlier()
    {
        var e = DpiEstimator.Combine([1600, 1612, 2450])!;
        Assert.Equal(1612, e.Dpi);
        Assert.True(e.Inconsistent);
    }

    [Fact]
    public void Bank_card_distance()
    {
        var run = new DpiRun(Counts(800, DpiEstimator.CardCm), 0, DpiEstimator.CardCm);
        Assert.InRange(run.Dpi, 799, 801);
        Assert.Equal(8.56, DpiEstimator.CardCm);
    }

    [Fact]
    public void Too_short_and_too_long_runs_are_rejected()
    {
        var click = new DpiRun(3, 1, 10);
        Assert.False(DpiEstimator.IsValid(click));
        Assert.Contains("слишком короткий", DpiEstimator.RunProblem(click));
        var half = new DpiRun(Counts(150, 10), 0, 10); // below any real sensor's DPI
        Assert.True(DpiEstimator.IsValid(half));
        Assert.False(DpiEstimator.IsValid(new DpiRun(Counts(90, 10), 0, 10)));
        Assert.False(DpiEstimator.IsValid(new DpiRun(Counts(60000, 10), 0, 10)));
        Assert.Null(DpiEstimator.Combine([20, 30]));
        Assert.Equal(1600, DpiEstimator.Combine([20, 1600])!.Dpi); // the short one is left out
    }
}
