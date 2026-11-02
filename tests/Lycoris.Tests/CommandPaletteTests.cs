using Lycoris.Modules.Services;
using Lycoris.Shell;
using Lycoris.Tweaks;

namespace Lycoris.Tests;

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
    public void Former_pages_are_searchable_sections_but_not_listed_for_an_empty_query()
    {
        var index = CommandPaletteIndex.Build([("services", "СЛУЖБЫ", ""), ("maintenance", "ОБСЛУЖИВАНИЕ", "")], [], [], [], [],
            CommandPaletteIndex.Sections);
        Assert.Equal(["СЛУЖБЫ", "ОБСЛУЖИВАНИЕ"], CommandPaletteSearch.Search(index, "").Select(e => e.Title));
        Assert.Equal("startup", CommandPaletteSearch.Search(index, "автозапуск")[0].PageKey);
        Assert.Equal("cleaner", CommandPaletteSearch.Search(index, "очистка")[0].PageKey);
        Assert.Equal("tools", CommandPaletteSearch.Search(index, "инструменты")[0].PageKey);
    }

    [Theory]
    [InlineData("cleaner", "maintenance")]
    [InlineData("tools", "maintenance")]
    [InlineData("startup", "services")]
    [InlineData("optimize", "optimize")]
    [InlineData("settings", "settings")]
    public void Old_page_keys_resolve_to_the_new_pages(string key, string page) =>
        Assert.Equal(page, MainViewModel.ResolvePageKey(key));

    [Fact]
    public void Every_section_key_resolves_to_a_top_level_page()
    {
        string[] pages = ["dashboard", "optimize", "games", "services", "maintenance", "drivers", "apps", "restore", "settings"];
        foreach (var (key, _, _, _) in CommandPaletteIndex.Sections)
            Assert.Contains(MainViewModel.ResolvePageKey(key), pages);
    }

    private static IReadOnlyList<PaletteEntry> FullIndex() =>
        CommandPaletteIndex.Build([("optimize", "ОПТИМИЗАЦИЯ", ""), ("settings", "НАСТРОЙКИ", "")], TweakCatalog.All, [], [], ServiceCatalog.Services,
            CommandPaletteIndex.Sections, CommandPaletteIndex.DeviceEntries, RejectedTweaks.All);

    [Theory]
    [InlineData("HPET")]
    [InlineData("таймер")]
    [InlineData("парковка ядер")]
    [InlineData("файл подкачки")]
    [InlineData("C-states")]
    [InlineData("сжатие памяти")]
    [InlineData("NVMe")]
    [InlineData("зарезервированное")]
    [InlineData("чистка реестра")]
    [InlineData("GlobalTimerResolutionRequests")]
    public void Popular_rejected_tweaks_are_found_as_myths(string query)
    {
        var hit = CommandPaletteSearch.Search(FullIndex(), query).FirstOrDefault(e => e.Kind == PaletteKind.Myth);
        Assert.NotNull(hit);
        Assert.Equal("settings", hit.PageKey);
        Assert.StartsWith(RejectedTweaks.FocusPrefix, hit.FocusId);
        Assert.NotNull(RejectedTweaks.Find(hit.FocusId![RejectedTweaks.FocusPrefix.Length..]));
    }

    [Fact]
    public void Myth_entries_have_no_risk_tag_and_one_per_rejected_tweak()
    {
        var myths = FullIndex().Where(e => e.Kind == PaletteKind.Myth).ToList();
        Assert.Equal(RejectedTweaks.All.Count, myths.Count);
        Assert.All(myths, e => { Assert.False(e.HasRisk); Assert.Null(e.RiskText); Assert.Equal("НЕ ДЕЛАЕМ", e.KindText); Assert.NotEmpty(e.Description); });
        Assert.Equal(RejectedTweaks.All.Select(r => RejectedTweaks.FocusPrefix + r.Id), myths.Select(e => e.FocusId));
        // Myth entries route to the settings page, which NavigateTo resolves to itself.
        Assert.Equal("settings", MainViewModel.ResolvePageKey("settings"));
    }

    [Fact]
    public void Every_rejected_tweak_is_found_by_its_own_title()
    {
        var index = FullIndex();
        foreach (var r in RejectedTweaks.All)
            Assert.Contains(CommandPaletteSearch.Search(index, r.Title, max: 50), e => e.Kind == PaletteKind.Myth && e.FocusId == RejectedTweaks.FocusPrefix + r.Id);
    }

    [Fact]
    public void Tweak_is_found_by_its_id()
    {
        var index = CommandPaletteIndex.Build([], TweakCatalog.All, [], [], []);
        Assert.Contains(CommandPaletteSearch.Search(index, "game-mode"), e => e.FocusId == "game-mode");
    }

    // ───────────── Obvious UI: the search knows programs, cleanup items, autostart, Settings rows, Drivers tabs, Restore ─────────────

    private static IReadOnlyList<PaletteEntry> ExtendedIndex(IEnumerable<CommandPaletteIndex.Entry>? extra = null) =>
        CommandPaletteIndex.Build([("apps", "ПРИЛОЖЕНИЯ", ""), ("restore", "ВОССТАНОВЛЕНИЕ", "")], [], [], [], [],
            CommandPaletteIndex.Sections, null, null, extra ?? CommandPaletteIndex.StaticExtras);

    [Fact]
    public void Every_catalog_app_is_found_by_name_and_opens_its_card()
    {
        var index = ExtendedIndex();
        foreach (var a in Lycoris.Modules.Apps.AppCatalog.All)
            Assert.Contains(CommandPaletteSearch.Search(index, a.Name, max: 50),
                e => e.Kind == PaletteKind.App && e.PageKey == "apps" && e.FocusId == CommandPaletteIndex.AppFocusPrefix + a.Id);
        Assert.Equal("ПРОГРАММА", index.First(e => e.Kind == PaletteKind.App).KindText);
    }

    [Fact]
    public void Every_cleanup_item_is_found_and_opens_the_cleaner_tab()
    {
        var index = ExtendedIndex();
        foreach (var c in Lycoris.Modules.Cleaner.CleanCatalog.All)
            Assert.Contains(CommandPaletteSearch.Search(index, c.Name, max: 50),
                e => e.Kind == PaletteKind.Clean && e.PageKey == "cleaner" && e.FocusId == CommandPaletteIndex.CleanFocusPrefix + c.Id);
        Assert.Equal("maintenance", MainViewModel.ResolvePageKey("cleaner"));
    }

    [Fact]
    public void Settings_rows_driver_tabs_and_restore_sections_are_searchable_but_not_listed_for_an_empty_query()
    {
        var index = ExtendedIndex();
        Assert.Equal(["ПРИЛОЖЕНИЯ", "ВОССТАНОВЛЕНИЕ"], CommandPaletteSearch.Search(index, "").Select(e => e.Title));
        var rights = CommandPaletteSearch.Search(index, "права администратора")[0];
        Assert.Equal(PaletteKind.Setting, rights.Kind);
        Assert.Equal("settings", rights.PageKey);
        Assert.StartsWith(CommandPaletteIndex.SettingFocusPrefix, rights.FocusId);
        var updates = CommandPaletteSearch.Search(index, "обновления драйверов").First(e => e.PageKey == "drivers");
        Assert.Equal("tab:Updates", updates.FocusId);
        Assert.True(updates.IsSection);
        var last = CommandPaletteSearch.Search(index, "откатить последний").First(e => e.PageKey == "restore");
        Assert.Equal(CommandPaletteIndex.RestoreSectionPrefix + "last-batch", last.FocusId);
        Assert.Equal("ПАРАМЕТР", rights.KindText);
    }

    [Fact]
    public void Driver_tab_focus_ids_name_real_tabs()
    {
        foreach (var t in CommandPaletteIndex.DriverTabEntries)
        {
            Assert.Equal("drivers", t.PageKey);
            Assert.StartsWith("tab:", t.FocusId);
            Assert.True(Enum.TryParse<Lycoris.Modules.Drivers.DriversTab>(t.FocusId![4..], true, out _), t.FocusId);
        }
    }

    [Fact]
    public void Autostart_entries_come_from_the_live_scan_and_open_the_startup_tab()
    {
        var index = ExtendedIndex(CommandPaletteIndex.StartupEntries([("Run|Discord", "Discord", "Discord Inc.", @"C:\Discord\Update.exe --processStart Discord.exe")]));
        var hit = Assert.Single(CommandPaletteSearch.Search(index, "discord"), e => e.Kind == PaletteKind.Startup);
        Assert.Equal("services", hit.PageKey);
        Assert.Equal(CommandPaletteIndex.StartupFocusPrefix + "Run|Discord", hit.FocusId);
        Assert.Equal("АВТОЗАПУСК", hit.KindText);
        Assert.Contains("Discord Inc.", hit.Description);
        // Found by the publisher too (keywords).
        Assert.Contains(CommandPaletteSearch.Search(index, "discord inc"), e => e.Kind == PaletteKind.Startup);
    }
}
