using Lycoris.Core;
using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Modules.Games;

namespace Lycoris.Tests.Localization;

/// <summary>English output of the Dashboard and Games modules (the Russian output is covered by DashboardTests / GamesTests).</summary>
public sealed class DashboardGamesLocalizationTests
{
    [Fact]
    public void Dashboard_formatting_follows_the_language()
    {
        using (Loc.UseLanguage("en"))
        {
            Assert.Equal("12.4 GB", DashFormat.Size(12.4 * 1024 * 1024 * 1024));
            Assert.Equal("1.2 MB/s", DashFormat.Rate(1.2 * 1024 * 1024));
            Assert.Equal("3 d 04 h 12 min", DashFormat.Duration(new TimeSpan(3, 4, 12, 0)));
            Assert.Equal("7%", DashFormat.Share(0.07));
            Assert.Equal("5.08 GHz", DashFormat.Clock(5080));
        }
        Assert.Equal("12,4 ГБ", DashFormat.Size(12.4 * 1024 * 1024 * 1024));
        Assert.Equal("7 %", DashFormat.Share(0.07));
    }

    [Fact]
    public void Dashboard_age_uses_english_plurals()
    {
        using var _ = Loc.UseLanguage("en");
        var now = new DateTime(2026, 11, 2);
        Assert.Equal("1 day", DashFormat.Age(now.AddDays(-1), now));
        Assert.Equal("3 days", DashFormat.Age(now.AddDays(-3), now));
        Assert.Equal("2 months", DashFormat.Age(now.AddDays(-60), now));
        Assert.Equal("1 year", DashFormat.Age(now.AddDays(-400), now));
    }

    [Fact]
    public void Launchpad_tiles_are_english()
    {
        using var _ = Loc.UseLanguage("en");
        var on = LaunchpadLogic.Game(true);
        Assert.Equal("Running", on.Value);
        Assert.Equal("STOP", on.Button);
        Assert.Equal("START", LaunchpadLogic.Game(false).Button);
        Assert.Equal("APPLY · 3", LaunchpadLogic.Optimize(3, 28).Button);
        Assert.Equal("3 to change", LaunchpadLogic.Optimize(3, 28).Value);
        Assert.Equal("FIX ALL SAFE ITEMS · 2", LaunchpadLogic.FixSafeText(2));
        // the cleaner may hand over "654.8" or "654,8": both parse
        Assert.Equal(654.8, LaunchpadLogic.ParseAmount("654.8"));
        Assert.Equal(654.8, LaunchpadLogic.ParseAmount("654,8"));
    }

    [Fact]
    public void Health_checks_are_english_and_keep_their_facts()
    {
        using var _ = Loc.UseLanguage("en");
        var many = HealthRules.CheckStartup(new SystemSnapshot { StartupCount = 14 })!;
        Assert.Equal("TOO MANY STARTUP PROGRAMS", many.Title);
        Assert.Contains("14 programs", many.Detail);
        Assert.Equal("DISABLE THE EXTRAS", many.Action!.Label);
        Assert.Equal("1 program", Loc.N(1, "{0} программа", "{0} программы", "{0} программ"));
        Assert.Equal("EXCELLENT", HealthScore.Verdict(95));
        Assert.Equal("NEEDS ATTENTION", HealthScore.Verdict(40));
    }

    [Fact]
    public void Boost_texts_are_english_and_the_prefixes_still_strip()
    {
        using var _ = Loc.UseLanguage("en");
        var text = BoostPolicy.Describe(PowerPlanAction.SkipOnBattery, "Balanced", "");
        Assert.StartsWith(BoostPolicy.SkipPrefix, text);
        Assert.Equal("Power plan not changed: ", BoostPolicy.SkipPrefix);
        Assert.Equal("the laptop is running on battery.", text[BoostPolicy.SkipPrefix.Length..]);
        Assert.Equal("Realtime", GamesNative.PriorityName(GamesNative.REALTIME_PRIORITY_CLASS));
        Assert.Equal("5 GAMES", Loc.N(5, "{0} ИГРА", "{0} ИГРЫ", "{0} ИГР"));
        Assert.Equal("1 GAME", Loc.N(1, "{0} ИГРА", "{0} ИГРЫ", "{0} ИГР"));
    }

    [Fact]
    public void Gpu_preference_phrases_are_english()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("the discrete graphics card (RTX 4070)", GpuPreference.DiscreteCardPhrase("RTX 4070"));
        Assert.Equal("the discrete graphics card", GpuPreference.DiscreteCardPhrase(null));
        Assert.Equal("high performance", GpuPreference.Describe(GpuPreference.HighPerformance));
    }
}
