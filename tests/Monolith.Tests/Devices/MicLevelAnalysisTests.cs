using Monolith.Modules.Devices.Audio;

namespace Monolith.Tests.Devices;

/// <summary>The level test's analysis, the level suggestion (target −9 dBFS, true peak ≤ −1 dBFS) and the sample conversion.</summary>
public sealed class MicLevelAnalysisTests
{
    private const int Rate = 48000;

    private static double Amp(double dbfs) => Math.Pow(10, dbfs / 20);

    private static float[] Sine(double dbfs, double seconds, double hz = 1000)
    {
        var a = Amp(dbfs);
        var n = (int)(Rate * seconds);
        var s = new float[n];
        for (var i = 0; i < n; i++) s[i] = (float)(a * Math.Sin(2 * Math.PI * hz * i / Rate));
        return s;
    }

    /// <summary>Speech-like: 150-ms bursts at <paramref name="burstDbfs"/> with 100-ms pauses of room noise (−60 dBFS).</summary>
    private static float[] Bursts(double burstDbfs, double seconds)
    {
        var n = (int)(Rate * seconds);
        var s = new float[n];
        var burst = Amp(burstDbfs);
        var noise = Amp(-60);
        for (var i = 0; i < n; i++)
        {
            var inBurst = i % (Rate / 4) < Rate * 0.15;
            s[i] = (float)((inBurst ? burst : noise) * Math.Sin(2 * Math.PI * 300 * i / Rate));
        }
        return s;
    }

    private static MicLevelStats Stats(double p95, double peak, int clipped = 0, double speechSeconds = 3) =>
        new(peak, p95, p95 - 10, clipped, (int)Math.Round(speechSeconds / MicLevelAnalysis.WindowSeconds));

    // ───────────────────────────── Analysis ─────────────────────────────

    [Fact]
    public void A_minus_20_dbfs_sine_peaks_at_minus_20()
    {
        var s = MicLevelAnalysis.Analyze(Sine(-20, 2), Rate);
        Assert.InRange(s.PeakDbfs, -20.1, -19.9);
        Assert.InRange(s.P95WindowPeakDbfs, -20.1, -19.9);
        Assert.InRange(s.RmsDbfs, -23.2, -22.8); // a sine's RMS is 3 dB under its peak
        Assert.Equal(0, s.ClippedSamples);
        Assert.InRange(s.SpeechSeconds, 1.9, 2.1);
    }

    [Fact]
    public void Full_scale_counts_clipped_samples()
    {
        var samples = Sine(-20, 2);
        for (var i = 1000; i < 1100; i++) samples[i] = i % 2 == 0 ? 1f : -1f;
        var s = MicLevelAnalysis.Analyze(samples, Rate);
        Assert.Equal(100, s.ClippedSamples);
        Assert.Equal(MicLevelVerdict.Clipping, MicLevelAnalysis.Classify(s));
        Assert.Equal("перегруз: 100 отсчётов на пределе", MicLevelAnalysis.Verdict(s));
    }

    [Fact]
    public void Silence_suggests_nothing()
    {
        var s = MicLevelAnalysis.Analyze(new float[Rate * 5], Rate);
        Assert.Equal(0, s.SpeechWindows);
        Assert.Equal(-120, s.PeakDbfs);
        Assert.Equal(MicLevelVerdict.NoSpeech, MicLevelAnalysis.Classify(s));
        Assert.Null(MicLevelAnalysis.SuggestDb(5.6, s, -16, 15));
        Assert.Equal(-120, MicLevelAnalysis.Analyze([], Rate).PeakDbfs);
    }

    [Fact]
    public void Speech_like_bursts_are_measured_by_their_loud_windows()
    {
        var s = MicLevelAnalysis.Analyze(Bursts(-24, 5), Rate);
        Assert.InRange(s.P95WindowPeakDbfs, -24.2, -23.8);
        Assert.InRange(s.SpeechSeconds, 2.5, 3.5); // the pauses are below −45 dBFS
        Assert.Equal("тихо: пики −24 дБFS", MicLevelAnalysis.Verdict(s));
    }

    // ───────────────────────────── Suggestion ─────────────────────────────

    [Fact]
    public void Quiet_speech_at_plus_5_6_db_goes_to_the_top_of_the_range()
    {
        var s = MicLevelAnalysis.Analyze(Bursts(-24, 5), Rate);
        Assert.Equal<double?>(15, MicLevelAnalysis.SuggestDb(5.6, s, -16, 15)); // +15 dB wanted → 20.6, clamped to the device's +15
    }

    [Fact]
    public void Speech_already_at_minus_9_suggests_nothing()
    {
        Assert.Null(MicLevelAnalysis.SuggestDb(5.6, Stats(p95: -9, peak: -7), -16, 15));
        Assert.Equal("в норме: пики −9 дБFS", MicLevelAnalysis.Verdict(Stats(p95: -9, peak: -7)));
        Assert.Null(MicLevelAnalysis.SuggestDb(5.6, Stats(p95: -10, peak: -7), -16, 15)); // under 1,5 dB: not worth a change
    }

    [Fact]
    public void Suggestions_are_rounded_to_half_a_decibel()
    {
        Assert.Equal<double?>(6.5, MicLevelAnalysis.SuggestDb(0, Stats(p95: -15.3, peak: -12), -16, 15)); // 6,3 → 6,5
        Assert.Equal<double?>(3.0, MicLevelAnalysis.SuggestDb(1.2, Stats(p95: -10.9, peak: -8), -16, 15)); // 3,1 → 3,0
        Assert.Equal<double?>(-2.5, MicLevelAnalysis.SuggestDb(0, Stats(p95: -6.6, peak: -4), -16, 15)); // −2,4 → −2,5
    }

    [Fact]
    public void The_true_peak_never_goes_above_minus_1_dbfs()
    {
        // p95 −20 would ask for +11 dB, but one sample already peaks at −3 dBFS: at most +2 dB.
        Assert.Equal<double?>(2.0, MicLevelAnalysis.SuggestDb(0, Stats(p95: -20, peak: -3), -16, 15));
        // A peak already near the limit never lets a quiet p95 raise the level.
        Assert.Null(MicLevelAnalysis.SuggestDb(0, Stats(p95: -20, peak: -0.5), -16, 15));
    }

    [Fact]
    public void Clipping_only_ever_lowers_the_level()
    {
        var suggestion = MicLevelAnalysis.SuggestDb(5, Stats(p95: -20, peak: 0, clipped: 12), -16, 15);
        Assert.NotNull(suggestion);
        Assert.True(suggestion <= 2, $"{suggestion}");
    }

    [Fact]
    public void Too_little_speech_suggests_nothing()
    {
        Assert.Null(MicLevelAnalysis.SuggestDb(0, Stats(p95: -30, peak: -28, speechSeconds: 0.5), -16, 15));
        Assert.Equal(MicLevelVerdict.NoSpeech, MicLevelAnalysis.Classify(Stats(p95: -30, peak: -28, speechSeconds: 0.5)));
    }

    [Fact]
    public void The_suggestion_stays_inside_the_device_range()
    {
        Assert.Equal<double?>(-16, MicLevelAnalysis.SuggestDb(-12, Stats(p95: -1.5, peak: -1.2), -16, 15));
        Assert.Null(MicLevelAnalysis.SuggestDb(15, Stats(p95: -30, peak: -28), -16, 15)); // already at the top
        Assert.Null(MicLevelAnalysis.SuggestDb(double.NaN, Stats(p95: -30, peak: -28), -16, 15));
    }

    // ───────────────────────────── The setting value of a level ─────────────────────────────

    [Fact]
    public void A_level_value_compares_in_decibels_within_half_a_decibel()
    {
        var live = MicLevelValue.Of(0.75f, 5.6f);
        Assert.Equal(75, live.Number);
        Assert.True(MicLevelValue.TryDb(live, out var db) && Math.Abs(db - 5.6) < 1e-9);
        var target = MicLevelValue.Target(15, 100);
        Assert.True(MicLevelValue.IsEstimate(target));
        Assert.False(MicLevelValue.Same(live, target));
        Assert.True(MicLevelValue.Same(MicLevelValue.Of(0.99f, 14.7f), target));
        Assert.True(MicLevelValue.Same(Monolith.Modules.Devices.DeviceValue.Of(75), live)); // a slider value has no dB: by percent
        Assert.Equal("≈100 %", MicLevelValue.Format(target));
        Assert.Equal("75 %", MicLevelValue.Format(live));
    }

    [Fact]
    public void The_estimated_slider_position_passes_through_the_live_point()
    {
        Assert.Equal(75, MicLevelValue.EstimatePercent(5.6, 5.6, 75, -16, 15));
        Assert.Equal(100, MicLevelValue.EstimatePercent(15, 5.6, 75, -16, 15));
        Assert.Equal(0, MicLevelValue.EstimatePercent(-16, 5.6, 75, -16, 15));
        Assert.InRange(MicLevelValue.EstimatePercent(10, 5.6, 75, -16, 15), 80, 90);
    }

    [Fact]
    public void A_measurement_goes_stale_when_the_level_moves()
    {
        var m = new MicMeasurement(0.75f, 5.6, Stats(-24, -22), 15, "тихо", null);
        Assert.False(m.IsStale(new EndpointLevel(0.75f, 5.9f, -16, 15, false)));
        Assert.True(m.IsStale(new EndpointLevel(0.99f, 15f, -16, 15, false)));
        var noDb = new MicMeasurement(0.75f, null, Stats(-24, -22), null, "тихо", null);
        Assert.True(noDb.IsStale(new EndpointLevel(0.80f, null, null, null, false)));
    }

    // ───────────────────────────── Sample conversion ─────────────────────────────

    [Fact]
    public void Integer_samples_convert_to_floats()
    {
        Assert.Equal(-1f, MicSamples.FromInt16(short.MinValue));
        Assert.Equal(0.5f, MicSamples.FromInt16(16384));
        Assert.Equal(-1f, MicSamples.FromInt24(0x00, 0x00, 0x80));
        Assert.Equal(0.5f, MicSamples.FromInt24(0x00, 0x00, 0x40));
        Assert.InRange(MicSamples.FromInt24(0xFF, 0xFF, 0x7F), 0.9999f, 1f);
        Assert.Equal(-1f, MicSamples.FromInt32(int.MinValue));
    }

    [Fact]
    public void Frames_keep_the_loudest_channel_with_its_sign()
    {
        var output = new List<float>();
        var data = new List<byte>();
        foreach (var (l, r) in new[] { (0.1f, -0.6f), (0.3f, 0.2f) })
        {
            data.AddRange(BitConverter.GetBytes(l));
            data.AddRange(BitConverter.GetBytes(r));
        }
        MicSamples.Convert(data.ToArray(), 2, 8, 2, 4, true, output);
        Assert.Equal(new[] { -0.6f, 0.3f }, output);

        output.Clear();
        MicSamples.Convert(new byte[] { 0x00, 0x00, 0x40, 0x00, 0x00, 0xC0 }, 2, 3, 1, 3, false, output); // packed 24-bit mono
        Assert.Equal(new[] { 0.5f, -0.5f }, output);

        output.Clear();
        MicSamples.Convert(BitConverter.GetBytes(0x40000000), 1, 4, 1, 4, false, output); // 24 valid bits in a 32-bit container
        Assert.Equal(new[] { 0.5f }, output);

        output.Clear();
        MicSamples.Convert(BitConverter.GetBytes((short)-16384), 1, 2, 1, 2, false, output);
        Assert.Equal(new[] { -0.5f }, output);
    }

    [Theory]
    [InlineData(1, "отсчёт")]
    [InlineData(3, "отсчёта")]
    [InlineData(11, "отсчётов")]
    [InlineData(22, "отсчёта")]
    [InlineData(25, "отсчётов")]
    public void Clipped_counts_use_russian_plurals(int n, string word) =>
        Assert.Equal(word, MicLevelAnalysis.Plural(n, "отсчёт", "отсчёта", "отсчётов"));
}
