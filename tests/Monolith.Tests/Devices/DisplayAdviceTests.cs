using Monolith.Core.Devices;
using Monolith.Modules.Devices;
using Monolith.Modules.Devices.Displays;

namespace Monolith.Tests.Devices;

/// <summary>Monitor recommendations: native size at max refresh, the 1 Hz slack, laptops/battery, never a downgrade, honest notes.</summary>
public sealed class DisplayAdviceTests
{
    private static DisplayModeSpec Spec(int w, int h, int hz, uint num = 0, uint den = 0, int orientation = 0) => new(w, h, hz, num, den, 32, orientation, 0, 0);

    private static List<OfferedMode> Modes(params (int W, int H, int[] Rates)[] sizes) =>
        sizes.SelectMany(s => s.Rates.Select(r => new OfferedMode(s.W, s.H, r))).ToList();

    private static readonly List<OfferedMode> X27uModes = Modes((2560, 1440, [240, 180, 144, 120, 60, 59]), (1920, 1080, [240, 144, 60]), (1280, 720, [60]));
    private static readonly DisplaySize Qhd = new(2560, 1440);

    [Fact]
    public void The_monitors_own_239_97_hz_timing_is_not_flagged()
    {
        Assert.Null(DisplayAdvice.RecommendedMode(X27uModes, Qhd, Qhd, Spec(2560, 1440, 240, 239970, 1000)));
        Assert.Null(DisplayAdvice.RecommendedMode(X27uModes, Qhd, Qhd, Spec(2560, 1440, 239, 239970, 1000))); // a driver that rounds down
    }

    [Fact]
    public void Below_max_refresh_includes_the_max_at_the_same_size()
    {
        var a = DisplayAdvice.RecommendedMode(X27uModes, Qhd, Qhd, Spec(2560, 1440, 144))!;

        Assert.Equal(new OfferedMode(2560, 1440, 240), a.Target);
        Assert.Equal(RecommendPolicy.Include, a.Policy);
        Assert.Contains("240 Гц", a.Reason);
    }

    [Fact]
    public void A_non_native_size_gets_the_native_size_at_max_refresh()
    {
        var a = DisplayAdvice.RecommendedMode(X27uModes, Qhd, Qhd, Spec(1920, 1080, 60))!;

        Assert.Equal(new OfferedMode(2560, 1440, 240), a.Target);
        Assert.Equal(RecommendPolicy.Include, a.Policy);
        Assert.Contains("Родное разрешение", a.Reason);
    }

    [Fact]
    public void A_portrait_monitor_keeps_its_orientation()
    {
        var portrait = Modes((1440, 2560, [180, 165, 120, 60]), (1080, 1920, [180, 60]));
        var a = DisplayAdvice.RecommendedMode(portrait, new DisplaySize(1440, 2560), new DisplaySize(1440, 2560), Spec(1440, 2560, 60, orientation: 3))!;

        Assert.Equal(new OfferedMode(1440, 2560, 180), a.Target);
    }

    [Fact]
    public void Never_preselects_a_smaller_size_and_suggests_only_when_sources_disagree()
    {
        // A TV whose preferred timing is 1080p although it is 4K: the native size is uncertain → at most Suggest.
        var tv = Modes((3840, 2160, [60, 30]), (2560, 1440, [60]), (1920, 1080, [120, 60]));
        var fromLower = DisplayAdvice.RecommendedMode(tv, new DisplaySize(3840, 2160), new DisplaySize(1920, 1080), Spec(2560, 1440, 60))!;
        Assert.Equal(new OfferedMode(3840, 2160, 60), fromLower.Target);
        Assert.Equal(RecommendPolicy.Suggest, fromLower.Policy);

        // Preferred 1080p on that TV while running 4K: never a downgrade (no size change at all).
        Assert.Null(DisplayAdvice.RecommendedMode(tv, new DisplaySize(1920, 1080), new DisplaySize(1920, 1080), Spec(3840, 2160, 60)));

        // Running above native (driver super resolution): no downgrade either.
        var dsr = Modes((3840, 2160, [144]), (2560, 1440, [144]));
        Assert.Null(DisplayAdvice.RecommendedMode(dsr, Qhd, Qhd, Spec(3840, 2160, 144)));
    }

    [Fact]
    public void Laptop_panels_and_battery_follow_the_research()
    {
        var panel = Modes((2560, 1600, [165, 60]));
        var size = new DisplaySize(2560, 1600);
        var current = Spec(2560, 1600, 60);

        // On battery: no refresh recommendation, internal or external.
        Assert.Null(DisplayAdvice.RecommendedMode(panel, size, size, current, new ModeContext(IsInternal: true, OnBattery: true)));
        Assert.Null(DisplayAdvice.RecommendedMode(X27uModes, Qhd, Qhd, Spec(2560, 1440, 144), new ModeContext(OnBattery: true)));
        // A panel with Windows' dynamic refresh rate: left to Windows.
        Assert.Null(DisplayAdvice.RecommendedMode(panel, size, size, current, new ModeContext(IsInternal: true, DrrLikely: true)));
        // On AC without DRR: offered, unchecked.
        var ac = DisplayAdvice.RecommendedMode(panel, size, size, current, new ModeContext(IsInternal: true))!;
        Assert.Equal(new OfferedMode(2560, 1600, 165), ac.Target);
        Assert.Equal(RecommendPolicy.Suggest, ac.Policy);
        // On battery a size fix still comes, at the current rate.
        var fix = DisplayAdvice.RecommendedMode(panel.Concat(Modes((1920, 1200, [165, 60]))).ToList(), size, size, Spec(1920, 1200, 60),
            new ModeContext(IsInternal: true, OnBattery: true))!;
        Assert.Equal(new OfferedMode(2560, 1600, 60), fix.Target);
    }

    [Fact]
    public void Drr_is_assumed_only_for_windows_11_laptop_panels_with_vrr_from_120_hz()
    {
        var vrr = EdidParser.Parse(new EdidParserTests.EdidBuilder().Dtd(2560, 1440, 160, 41, 24150).Range(48, 165).Build());
        var slow = EdidParser.Parse(new EdidParserTests.EdidBuilder().Dtd(2560, 1440, 160, 41, 24150).Range(48, 75).Build());

        Assert.True(DisplayAdvice.DrrLikely(true, true, vrr));
        Assert.False(DisplayAdvice.DrrLikely(false, true, vrr));
        Assert.False(DisplayAdvice.DrrLikely(true, false, vrr));
        Assert.False(DisplayAdvice.DrrLikely(true, true, slow));
        Assert.False(DisplayAdvice.DrrLikely(true, true, null));
    }

    [Fact]
    public void The_bandwidth_note_appears_only_when_the_edid_lists_more_than_windows_offers()
    {
        var edid = EdidParser.Parse(EdidParserTests.X27u())!; // 239,97 Гц at 2560×1440

        Assert.Null(DisplayAdvice.Bottleneck(edid, Qhd, 240, 10, onIntegratedGpu: false)); // 239,97 vs 240
        Assert.Null(DisplayAdvice.Bottleneck(null, Qhd, 144, 10, false));

        var dp = DisplayAdvice.Bottleneck(edid, Qhd, 144, 10, onIntegratedGpu: false)!;
        Assert.Contains("240 Гц", dp.Text);
        Assert.Contains("144 Гц", dp.Text);
        Assert.Contains("DisplayPort 1.4", dp.Text);
        Assert.DoesNotContain("материнской", dp.Text); // the GPU port only when the monitor really is on the iGPU
        Assert.False(dp.OnMotherboard);

        var hdmi = DisplayAdvice.Bottleneck(edid, Qhd, 144, 5, onIntegratedGpu: true)!;
        Assert.Contains("материнской", hdmi.Text);
        Assert.Contains("HDMI 2.0", hdmi.Text);
        Assert.Contains("25,4", hdmi.Text); // 1056,75 МГц × 24 бит
        Assert.True(hdmi.OnMotherboard);
    }

    [Fact]
    public void Bandwidth_is_pixel_clock_times_three_channels()
    {
        Assert.Equal(25.36, DisplayAdvice.Bandwidth(1_056_750_000, 8), 2);
        Assert.Equal(31.70, DisplayAdvice.Bandwidth(1_056_750_000, 10), 2);
    }

    [Fact]
    public void Fps_cap_is_three_below_the_refresh()
    {
        Assert.Contains("237", DisplayAdvice.FpsCapText(240));
        Assert.Contains("177", DisplayAdvice.FpsCapText(180));
        Assert.Contains("237", DisplayAdvice.VrrHint(240));
        Assert.Equal("48–240 Гц", DisplayAdvice.VrrRangeText((48, 240)));
    }

    [Fact]
    public void Hdr_hint_is_neutral_short_and_shows_the_peak_luminance()
    {
        var hint = DisplayAdvice.HdrHint(EdidParser.Parse(EdidParserTests.X27u()));

        Assert.Contains("нит", hint);
        Assert.Contains("456", hint);
        Assert.Contains("OLED", hint);
        Assert.Contains("HDR400", hint);
        Assert.DoesNotContain("рекоменд", hint, StringComparison.OrdinalIgnoreCase);
        Assert.True(hint.Length <= 120, hint.Length.ToString());
        Assert.DoesNotContain("нит", DisplayAdvice.HdrHint(null));
    }

    [Fact]
    public void Native_size_is_certain_only_when_every_source_agrees()
    {
        Assert.Equal((Qhd, true), DisplayAdvice.NativeSize(X27uModes, Qhd, Qhd));
        Assert.Equal((Qhd, false), DisplayAdvice.NativeSize(X27uModes, Qhd, new DisplaySize(1920, 1080)));
        Assert.Equal((Qhd, false), DisplayAdvice.NativeSize(X27uModes, null, null)); // the largest offered, unconfirmed
        Assert.Null(DisplayAdvice.NativeSize([], Qhd, Qhd));
    }

    [Fact]
    public void The_fastest_external_monitor_should_be_primary()
    {
        const string fast = "fast", slow = "slow";
        Assert.Equal(fast, DisplayAdvice.FastestShouldBePrimary([(slow, true, false, 60), (fast, false, false, 240)]));
        Assert.Null(DisplayAdvice.FastestShouldBePrimary([(fast, true, false, 240), (slow, false, false, 60)]));
        Assert.Null(DisplayAdvice.FastestShouldBePrimary([(slow, true, true, 60), (fast, false, false, 240)])); // laptop panel primary
        Assert.Null(DisplayAdvice.FastestShouldBePrimary([(slow, true, false, 240), (fast, false, false, 240)]));
        Assert.Null(DisplayAdvice.FastestShouldBePrimary([(fast, true, false, 240)]));
    }
}
