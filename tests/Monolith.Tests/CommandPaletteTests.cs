using Monolith.Modules.Services;
using Monolith.Shell;
using Monolith.Tweaks;

namespace Monolith.Tests;

/// <summary>Command palette search and ranking (pure logic, no UI).</summary>
public sealed class CommandPaletteTests
{
    private static PaletteEntry E(PaletteKind kind, string title, string description = "", string keywords = "", TweakRisk? risk = null) =>
        new() { Kind = kind, Title = title, Description = description, Keywords = keywords, Risk = risk, PageKey = "optimize" };

    [Theory]
    [InlineData("Ёлка", "елка")]
    [InlineData("  СБРОС  DNS-КЭША ", "сброс dns кэша")]
    [InlineData("«Игровой режим»", "игровой режим")]
    [InlineData("", "")]
    public void Normalize_lowercases_folds_yo_and_strips_punctuation(string input, string expected) =>
        Assert.Equal(expected, CommandPaletteSearch.Normalize(input));

    [Fact]
    public void Empty_query_returns_only_pages()
    {
        var index = new[] { E(PaletteKind.Tweak, "Игровой режим"), E(PaletteKind.Page, "ПАНЕЛЬ"), E(PaletteKind.Page, "ИГРЫ") };
        var r = CommandPaletteSearch.Search(index, "   ");
        Assert.Equal(["ПАНЕЛЬ", "ИГРЫ"], r.Select(e => e.Title));
    }

    [Fact]
    public void Search_is_case_insensitive_and_treats_yo_as_ye()
    {
        var index = new[] { E(PaletteKind.Tweak, "Ускорение отклика всплывающих окон"), E(PaletteKind.Tweak, "Чёрный список") };
        Assert.Equal("Чёрный список", Assert.Single(CommandPaletteSearch.Search(index, "ЧЕРНЫЙ")).Title);
        Assert.Single(CommandPaletteSearch.Search(index, "черный"));
    }

    [Fact]
    public void Title_match_ranks_above_description_and_keywords()
    {
        var index = new[]
        {
            E(PaletteKind.Tweak, "Ускорение меню", description: "Отключает анимацию мыши."),
            E(PaletteKind.Tweak, "Тонкая настройка", keywords: "мышь"),
            E(PaletteKind.Tweak, "Ускорение мыши"),
        };
        var r = CommandPaletteSearch.Search(index, "мыш");
        Assert.Equal(["Ускорение мыши", "Тонкая настройка", "Ускорение меню"], r.Select(e => e.Title));
    }

    [Fact]
    public void All_words_must_match()
    {
        var index = new[] { E(PaletteKind.Tweak, "Игровой режим"), E(PaletteKind.Tweak, "Режим питания") };
        Assert.Equal("Игровой режим", Assert.Single(CommandPaletteSearch.Search(index, "режим игр")).Title);
        Assert.Empty(CommandPaletteSearch.Search(index, "режим сеть"));
    }

    [Fact]
    public void Word_start_beats_inner_match()
    {
        var index = new[] { E(PaletteKind.Tweak, "Перезапуск служб"), E(PaletteKind.Tweak, "Запуск игр") };
        Assert.Equal("Запуск игр", CommandPaletteSearch.Search(index, "запуск")[0].Title);
    }

    [Fact]
    public void Results_are_capped()
    {
        var index = Enumerable.Range(0, 30).Select(i => E(PaletteKind.Tweak, $"Настройка {i}")).ToList();
        Assert.Equal(CommandPaletteSearch.DefaultMax, CommandPaletteSearch.Search(index, "настройка").Count);
    }

    [Fact]
    public void Risk_tag_only_on_tweaks()
    {
        Assert.Equal("ЭКСПЕРТ", E(PaletteKind.Tweak, "a", risk: TweakRisk.Advanced).RiskText);
        Assert.Equal("ПО ЖЕЛАНИЮ", E(PaletteKind.Tweak, "a", risk: TweakRisk.Optional).RiskText);
        Assert.Equal("БЕЗОПАСНО", E(PaletteKind.Tweak, "a", risk: TweakRisk.Safe).RiskText);
        Assert.Null(E(PaletteKind.Service, "a", risk: TweakRisk.Advanced).RiskText);
        Assert.Equal("НАСТРОЙКА", E(PaletteKind.Tweak, "a").KindText);
        Assert.Equal("РАЗДЕЛ", E(PaletteKind.Page, "a").KindText);
        Assert.Equal("ИНСТРУМЕНТ", E(PaletteKind.Tool, "a").KindText);
    }

    [Fact]
    public void Index_contains_every_tweak_with_focus_id_and_every_curated_service()
    {
        var index = CommandPaletteIndex.Build([("optimize", "ОПТИМИЗАЦИЯ", "")], TweakCatalog.All, [], [], ServiceCatalog.Services);
        foreach (var t in TweakCatalog.All)
            Assert.Contains(index, e => e.Kind == PaletteKind.Tweak && e.FocusId == t.Id && e.PageKey == "optimize" && e.Risk == t.Risk);
        Assert.Equal(ServiceCatalog.Services.Count, index.Count(e => e.Kind == PaletteKind.Service && e.PageKey == "services"));
        Assert.Single(index, e => e.Kind == PaletteKind.Page);
    }

    [Fact]
    public void Every_tweak_is_found_by_its_own_title()
    {
        var index = CommandPaletteIndex.Build([], TweakCatalog.All, [], [], []);
        foreach (var t in TweakCatalog.All)
            Assert.Contains(CommandPaletteSearch.Search(index, t.Title, max: 50), e => e.FocusId == t.Id);
    }

    [Fact]
    public void Tweak_is_found_by_its_id()
    {
        var index = CommandPaletteIndex.Build([], TweakCatalog.All, [], [], []);
        Assert.Contains(CommandPaletteSearch.Search(index, "game-mode"), e => e.FocusId == "game-mode");
    }
}
