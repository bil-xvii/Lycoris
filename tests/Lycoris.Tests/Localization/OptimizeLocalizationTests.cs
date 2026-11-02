using Lycoris.Modules.Optimize;
using Lycoris.Tweaks;

namespace Lycoris.Tests.Localization;

/// <summary>English for Optimize and the tweak catalog; the journal-bound texts stay Russian in every language.</summary>
public sealed class OptimizeLocalizationTests
{
    [Fact]
    public void ScenarioNames_AreEnglish()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("Games", OptimizeLogic.ScenarioName(OptimizeProfile.Gaming, false));
        Assert.Equal("Quiet PC", OptimizeLogic.ScenarioName(OptimizeProfile.Efficiency, false));
        Assert.Equal("LAPTOP: BATTERY LIFE", OptimizeLogic.ScenarioTitle(OptimizeProfile.Efficiency, true));
    }

    [Fact]
    public void BatchTitle_GoesToTheJournalInRussian_WhateverTheLanguage()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("Оптимизация: сценарий «Игры»", OptimizeLogic.BatchTitle(OptimizeProfile.Gaming, false));
        Assert.Equal("Оптимизация: свой набор", OptimizeLogic.BatchTitle(OptimizeProfile.Custom, false));
        // ...and the Restore page translates it from the table when it shows it.
        Assert.Equal("Optimize: “Games” scenario", Loc.Sink("Оптимизация: сценарий «Игры»"));
    }

    [Fact]
    public void TweakTitle_IsShownInEnglish_ButJournaledInRussian()
    {
        var tweak = TweakCatalog.Find("game-mode")!;
        Assert.Equal("Игровой режим Windows", tweak.TitleRu);
        Assert.Equal("Игровой режим Windows", tweak.Title);
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("Windows Game Mode", tweak.Title);
        Assert.Equal("Игровой режим Windows", tweak.TitleRu);
    }

    [Fact]
    public void Plurals_AndTimeouts_AreEnglish()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("never", TweakCatalog.DescribeTimeout(0));
        Assert.Equal("after 1 min 30 s", TweakCatalog.DescribeTimeout(90));
        Assert.Equal("1 ITEM", Loc.N(1, "{0} ПУНКТ", "{0} ПУНКТА", "{0} ПУНКТОВ"));
        Assert.Equal("5 ITEMS", Loc.N(5, "{0} ПУНКТ", "{0} ПУНКТА", "{0} ПУНКТОВ"));
    }

    [Fact]
    public void RiskAndRestartTexts_AreEnglish()
    {
        var tweak = TweakCatalog.Find("hags")!;
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("SAFE", tweak.RiskText);
        Assert.Equal("restart required", tweak.RestartText);
    }
}
