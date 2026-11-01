using Monolith.Modules.Devices.Input;

namespace Monolith.Tests.Devices;

/// <summary>Polling rate from synthetic report streams (no raw input is ever registered in tests).</summary>
public sealed class PollingRateEstimatorTests
{
    private const long Freq = 10_000_000; // 100 ns ticks, like Stopwatch on Windows

    /// <summary>Reports at <paramref name="hz"/> for <paramref name="seconds"/>, each interval ±<paramref name="jitter"/>.</summary>
    private static List<MotionSample> Stream(double hz, double seconds, double jitter = 0, int seed = 1, long start = 1_000_000, int d = 5)
    {
        var rng = new Random(seed);
        var list = new List<MotionSample>();
        var t = (double)start;
        var interval = Freq / hz;
        var end = start + seconds * Freq;
        while (t < end)
        {
            list.Add(new MotionSample((long)t, d, 0));
            t += interval * (1 + jitter * (rng.NextDouble() * 2 - 1));
        }
        return list;
    }

    [Fact]
    public void Thousand_hz_with_jitter_is_stable()
    {
        var e = PollingRateEstimator.Estimate(Stream(1000, 3, jitter: 0.05), Freq);
        Assert.Equal(1000, e.Hz);
        Assert.True(e.Stability > 0.9, $"stability {e.Stability}");
        Assert.True(e.Jitter is > 1 and < 1.2, $"jitter {e.Jitter}");
        Assert.False(e.Saturated);
        Assert.True(e.EnoughMotion);
    }

    [Theory]
    [InlineData(125)]
    [InlineData(250)]
    [InlineData(500)]
    [InlineData(2000)]
    [InlineData(4000)]
    [InlineData(8000)]
    public void Standard_rates_snap(int hz)
    {
        var e = PollingRateEstimator.Estimate(Stream(hz, 3, jitter: 0.03, seed: hz), Freq);
        Assert.Equal(hz, e.Hz);
        Assert.True(e.Stability > 0.9, $"{hz} Hz: stability {e.Stability}");
    }

    [Fact]
    public void Two_bursts_are_segmented_and_the_gap_is_not_counted()
    {
        var first = Stream(1000, 1.5, jitter: 0.02, seed: 2);
        var second = Stream(1000, 1.5, jitter: 0.02, seed: 3, start: first[^1].Ticks + 2_000_000); // 200 ms later
        var e = PollingRateEstimator.Estimate([.. first, .. second], Freq);
        Assert.Equal(1000, e.Hz);
        Assert.True(e.Stability > 0.95, $"stability {e.Stability}");
        Assert.True(e.Bins <= 60, $"bins {e.Bins}"); // 2 × 1,5 s of 50-ms bins, nothing from the gap
        Assert.True(e.Jitter < 1.2, $"jitter {e.Jitter}"); // the 200-ms gap is not an interval
    }

    [Fact]
    public void Too_little_motion_gives_no_rate()
    {
        var e = PollingRateEstimator.Estimate(Stream(1000, 0.5), Freq);
        Assert.Null(e.Hz);
        Assert.False(e.EnoughMotion);
        Assert.Contains("Недостаточно движения", PollingRateEstimator.ResultText(e));
        Assert.Null(PollingRateEstimator.Estimate([], Freq).Hz);
    }

    [Fact]
    public void Odd_rate_does_not_snap()
    {
        var e = PollingRateEstimator.Estimate(Stream(1500, 3), Freq);
        Assert.Null(e.Hz);
        Assert.True(e.EnoughMotion);
        Assert.InRange(e.RawHz, 1450, 1550);
        Assert.Contains("нестабильно", PollingRateEstimator.ResultText(e));
    }

    [Fact]
    public void Mostly_tiny_reports_mean_saturation()
    {
        var samples = Stream(1000, 3).Select((s, i) => i % 10 < 7 ? s with { Dx = 1, Dy = 0 } : s).ToList();
        Assert.True(PollingRateEstimator.Estimate(samples, Freq).Saturated);
        Assert.False(PollingRateEstimator.Estimate(Stream(1000, 3), Freq).Saturated);
    }

    [Fact]
    public void The_busiest_device_wins()
    {
        var devices = new Dictionary<int, IReadOnlyList<MotionSample>>
        {
            [1] = Stream(125, 1),
            [2] = Stream(1000, 3),
            [3] = [],
        };
        var best = PollingRateEstimator.EstimateBusiest(devices, Freq);
        Assert.NotNull(best);
        Assert.Equal(2, best.Value.Device);
        Assert.Equal(1000, best.Value.Estimate.Hz);
        Assert.Null(PollingRateEstimator.EstimateBusiest(new Dictionary<int, IReadOnlyList<MotionSample>>(), Freq));
    }

    [Fact]
    public void Result_text()
    {
        var e = new PollingEstimate(1000, 1003, 0.98, 1.3, false, 60, 3000);
        Assert.Equal("≈ 1000 Гц · стабильность 98 % · разброс ×1,3", PollingRateEstimator.ResultText(e));
    }
}
