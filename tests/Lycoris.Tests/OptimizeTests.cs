using System.Collections.ObjectModel;
using System.ServiceProcess;
using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.Modules.Optimize;
using Lycoris.Tweaks;

namespace Lycoris.Tests;

/// <summary>Pure logic of the Optimize page. Nothing here touches the real system.</summary>
public sealed class OptimizeTests
{
    [Theory]
    [InlineData(TweakRisk.Safe, false, true)]
    [InlineData(TweakRisk.Optional, false, true)]
    [InlineData(TweakRisk.Advanced, false, false)]
    [InlineData(TweakRisk.Advanced, true, true)]
    public void Applying_a_tweak_depends_on_mode_and_risk(TweakRisk risk, bool expertMode, bool expected) =>
        Assert.Equal(expected, OptimizeLogic.CanApplyRisk(risk, expertMode));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reverting_a_journaled_tweak_is_available_in_both_modes(bool expertMode) =>
        Assert.True(OptimizeLogic.CanRevert(hasJournalEntry: true, expertMode));

    [Fact]
    public void Applying_and_importing_filter_advanced_items_only_in_simple_mode()
    {
        var tweaks = new[] { Tweak("safe", "Safe"), Tweak("advanced", "Advanced", risk: TweakRisk.Advanced) };

        Assert.Equal(["safe"], OptimizeLogic.FilterApplicable(tweaks, t => t.Risk, expertMode: false).Select(t => t.Id));
        Assert.Equal(["safe", "advanced"], OptimizeLogic.FilterApplicable(tweaks, t => t.Risk, expertMode: true).Select(t => t.Id));
    }

    private static CustomTweak Tweak(string id, string title, string description = "", string details = "",
        TweakCategory category = TweakCategory.Gaming, TweakRisk risk = TweakRisk.Safe) => new()
    {
        Id = id, Title = title, Description = description, Details = details, Category = category, Risk = risk,
        DetectFunc = () => TweakState.NotApplied,
        ApplyAction = _ => { },
    };

    // ───────────────────────────── Profiles ─────────────────────────────

    [Theory]
    [InlineData("Normal", OptimizeProfile.Normal)]
    [InlineData("gaming", OptimizeProfile.Gaming)]
    [InlineData("Recommended", OptimizeProfile.Recommended)]
    [InlineData(" Custom ", OptimizeProfile.Custom)]
    [InlineData(null, OptimizeProfile.Recommended)]
    [InlineData("", OptimizeProfile.Recommended)]
    [InlineData("Turbo", OptimizeProfile.Recommended)]
    [InlineData("1", OptimizeProfile.Recommended)]
    [InlineData("42", OptimizeProfile.Recommended)]
    public void Last_profile_is_parsed_safely(string? stored, OptimizeProfile expected) =>
        Assert.Equal(expected, OptimizeLogic.ParseProfile(stored));

    [Theory]
    [InlineData("Streaming", "Streaming")]
    [InlineData("efficiency", "Efficiency")]
    [InlineData(" Efficiency ", "Efficiency")]
    [InlineData("Normal", "Normal")]
    [InlineData("Custom", "Custom")]
    [InlineData("8", "Recommended")]
    [InlineData("16", "Recommended")]
    [InlineData("Quiet", "Recommended")]
    public void New_scenario_names_are_parsed_and_older_or_unknown_names_stay_compatible(string stored, string expected) =>
        Assert.Equal(Enum.Parse<OptimizeProfile>(expected), OptimizeLogic.ParseProfile(stored));

    [Fact]
    public void Profiles_map_to_catalog_kinds()
    {
        Assert.Equal(ProfileKind.Normal, OptimizeLogic.ToKind(OptimizeProfile.Normal));
        Assert.Equal(ProfileKind.Gaming, OptimizeLogic.ToKind(OptimizeProfile.Gaming));
        Assert.Equal(ProfileKind.Recommended, OptimizeLogic.ToKind(OptimizeProfile.Recommended));
        Assert.Equal(ProfileKind.Streaming, OptimizeLogic.ToKind(OptimizeProfile.Streaming));
        Assert.Equal(ProfileKind.Efficiency, OptimizeLogic.ToKind(OptimizeProfile.Efficiency));
        Assert.Null(OptimizeLogic.ToKind(OptimizeProfile.Custom));
        // Every scenario with a catalog set round-trips through ToKind/ToSet; «Свой набор» is the only one without.
        Assert.Equal(Enum.GetValues<OptimizeProfile>().Where(p => p != OptimizeProfile.Custom), OptimizeLogic.CatalogProfiles);
        foreach (var p in OptimizeLogic.CatalogProfiles)
            Assert.Equal(p.ToString(), OptimizeLogic.ToKind(p)!.Value.ToString());
    }

    [Fact]
    public void Services_recommendations_follow_the_scenario()
    {
        Assert.Equal(ProfileSet.Normal, OptimizeLogic.ServicesProfileFor(OptimizeProfile.Normal));
        Assert.Equal(ProfileSet.Gaming, OptimizeLogic.ServicesProfileFor(OptimizeProfile.Gaming));
        Assert.Equal(ProfileSet.Recommended, OptimizeLogic.ServicesProfileFor(OptimizeProfile.Recommended));
        Assert.Equal(ProfileSet.Gaming, OptimizeLogic.ServicesProfileFor(OptimizeProfile.Streaming));
        Assert.Equal(ProfileSet.Recommended, OptimizeLogic.ServicesProfileFor(OptimizeProfile.Efficiency));
        Assert.Equal(ProfileSet.Recommended, OptimizeLogic.ServicesProfileFor(OptimizeProfile.Custom));
        // Only the three sets ServiceCatalog knows.
        Assert.All(Enum.GetValues<OptimizeProfile>(), p => Assert.True(ProfileSet.All.HasFlag(OptimizeLogic.ServicesProfileFor(p))));
    }

    [Fact]
    public void Scenario_texts_adapt_only_the_efficiency_tile_to_laptops()
    {
        Assert.Equal("ТИХИЙ ПК", OptimizeLogic.ScenarioTitle(OptimizeProfile.Efficiency, laptop: false));
        Assert.Equal("НОУТБУК: АВТОНОМНОСТЬ", OptimizeLogic.ScenarioTitle(OptimizeProfile.Efficiency, laptop: true));
        Assert.Equal("Тихий ПК", OptimizeLogic.ScenarioName(OptimizeProfile.Efficiency, laptop: false));
        Assert.Equal("Ноутбук: автономность", OptimizeLogic.ScenarioName(OptimizeProfile.Efficiency, laptop: true));
        Assert.Equal("ИГРЫ", OptimizeLogic.ScenarioTitle(OptimizeProfile.Gaming, laptop: false));
        Assert.Equal("РАБОТА И УЧЁБА", OptimizeLogic.ScenarioTitle(OptimizeProfile.Normal, laptop: true));
        Assert.Equal("СТРИМИНГ И ЗАПИСЬ", OptimizeLogic.ScenarioTitle(OptimizeProfile.Streaming, laptop: false));
        Assert.Equal("РЕКОМЕНДУЕМЫЙ", OptimizeLogic.ScenarioTitle(OptimizeProfile.Recommended, laptop: true));
        Assert.Equal("СВОЙ НАБОР", OptimizeLogic.ScenarioTitle(OptimizeProfile.Custom, laptop: false));
        foreach (var p in Enum.GetValues<OptimizeProfile>())
        {
            if (p == OptimizeProfile.Efficiency) continue;
            Assert.Equal(OptimizeLogic.ScenarioTitle(p, false), OptimizeLogic.ScenarioTitle(p, true));
            Assert.Equal(OptimizeLogic.ScenarioHint(p, false), OptimizeLogic.ScenarioHint(p, true));
            Assert.Matches("[А-Яа-я]", OptimizeLogic.ScenarioDescription(p, false));
        }
        // The tooltips are honest about what a scenario does NOT touch.
        Assert.Contains("Не трогает HAGS", OptimizeLogic.ScenarioHint(OptimizeProfile.Streaming, false));
        Assert.Contains("Не трогает: телеметрию", OptimizeLogic.ScenarioHint(OptimizeProfile.Normal, false));
        Assert.Contains("измерьте", OptimizeLogic.ScenarioHint(OptimizeProfile.Efficiency, false));
        Assert.Contains("дольше от батареи", OptimizeLogic.ScenarioHint(OptimizeProfile.Efficiency, true));
        Assert.DoesNotContain("батареи", OptimizeLogic.ScenarioHint(OptimizeProfile.Efficiency, false));
    }

    [Fact]
    public void Batch_titles_name_the_scenario()
    {
        Assert.Equal("Оптимизация: сценарий «Игры»", OptimizeLogic.BatchTitle(OptimizeProfile.Gaming, laptop: false));
        Assert.Equal("Оптимизация: сценарий «Стриминг и запись»", OptimizeLogic.BatchTitle(OptimizeProfile.Streaming, laptop: false));
        Assert.Equal("Оптимизация: сценарий «Тихий ПК»", OptimizeLogic.BatchTitle(OptimizeProfile.Efficiency, laptop: false));
        Assert.Equal("Оптимизация: сценарий «Ноутбук: автономность»", OptimizeLogic.BatchTitle(OptimizeProfile.Efficiency, laptop: true));
        Assert.Equal("Оптимизация: сценарий «Работа и учёба»", OptimizeLogic.BatchTitle(OptimizeProfile.Normal, laptop: false));
        Assert.Equal("Оптимизация: свой набор", OptimizeLogic.BatchTitle(OptimizeProfile.Custom, laptop: false));
    }

    [Fact]
    public void Manual_steps_exist_only_for_the_scenario_applied_and_name_no_vendor()
    {
        Assert.Empty(OptimizeLogic.ManualSteps(OptimizeProfile.Gaming, false));
        Assert.Empty(OptimizeLogic.ManualSteps(OptimizeProfile.Normal, false));
        Assert.Empty(OptimizeLogic.ManualSteps(OptimizeProfile.Recommended, false));
        Assert.Empty(OptimizeLogic.ManualSteps(OptimizeProfile.Custom, false));
        var streaming = Assert.Single(OptimizeLogic.ManualSteps(OptimizeProfile.Streaming, false));
        Assert.Contains("Не беспокоить", streaming);
        Assert.Contains("Lycoris не пишет", streaming);
        var quiet = Assert.Single(OptimizeLogic.ManualSteps(OptimizeProfile.Efficiency, false));
        Assert.Contains("FPS", quiet);
        Assert.Contains("вентиляторов", quiet);
        var battery = Assert.Single(OptimizeLogic.ManualSteps(OptimizeProfile.Efficiency, true));
        Assert.Contains("заряда", battery);
        Assert.Contains("динамическую частоту", battery);
        foreach (var line in new[] { streaming, quiet, battery })
        {
            Assert.DoesNotContain("Armoury", line);
            Assert.DoesNotContain("Vantage", line);
            Assert.DoesNotContain("Устройства", line);
            Assert.True(line.Length <= 160, "one sentence per line");
        }
        Assert.Equal("ВРУЧНУЮ", OptimizeLogic.ManualStepsHeader);
    }

    [Fact]
    public void Efficiency_tile_needs_known_hardware_and_a_member_beyond_a_plan_switch()
    {
        var plan = new CustomTweak { Id = "plan", Title = "П", Description = "", ExclusiveGroup = TweakGroups.PowerPlan, DetectFunc = () => TweakState.NotApplied, ApplyAction = _ => { } };
        var mode = new CustomTweak { Id = "mode", Title = "М", Description = "", ExclusiveGroup = TweakGroups.PowerModeAc, DetectFunc = () => TweakState.NotApplied, ApplyAction = _ => { } };
        Assert.False(OptimizeLogic.EfficiencyTileVisible(hardwareFactsKnown: false, [plan, mode]));
        Assert.False(OptimizeLogic.EfficiencyTileVisible(hardwareFactsKnown: true, [plan]), "Windows 10 / dual-CCD X3D: a plan switch makes nothing quieter");
        Assert.False(OptimizeLogic.EfficiencyTileVisible(hardwareFactsKnown: true, []));
        Assert.True(OptimizeLogic.EfficiencyTileVisible(hardwareFactsKnown: true, [plan, mode]));
    }

    [Fact]
    public void Profile_names_round_trip_through_settings()
    {
        foreach (var p in Enum.GetValues<OptimizeProfile>())
            Assert.Equal(p, OptimizeLogic.ParseProfile(p.ToString()));
    }

    // ───────────────────────────── States ─────────────────────────────

    [Theory]
    [InlineData(TweakState.NotApplied, true)]
    [InlineData(TweakState.Partial, true)]
    [InlineData(TweakState.Unknown, true)]
    [InlineData(TweakState.Applied, false)]
    [InlineData(TweakState.NotApplicable, false)]
    public void Only_states_that_differ_count_as_a_change(TweakState state, bool willChange) =>
        Assert.Equal(willChange, OptimizeLogic.WillChange(state));

    [Fact]
    public void Every_state_has_a_russian_label()
    {
        Assert.Equal("ПРОВЕРКА…", OptimizeLogic.StateText(null));
        Assert.Equal("ПРИМЕНЕНО", OptimizeLogic.StateText(TweakState.Applied));
        Assert.Equal("НЕ ПРИМЕНЕНО", OptimizeLogic.StateText(TweakState.NotApplied));
        Assert.Equal("ЧАСТИЧНО", OptimizeLogic.StateText(TweakState.Partial));
        Assert.Equal("НЕПРИМЕНИМО", OptimizeLogic.StateText(TweakState.NotApplicable));
        Assert.Equal("НЕИЗВЕСТНО", OptimizeLogic.StateText(TweakState.Unknown));
    }

    [Fact]
    public void Status_note_is_the_dependency_sentence_or_the_not_applicable_reason()
    {
        var plain = Tweak("x", "X");
        var reasoned = new CustomTweak
        {
            Id = "mode", Title = "Режим", Description = "", Requires = ["plan"],
            NotApplicableReason = "Только для Windows 11 на сбалансированной схеме питания.",
            DetectFunc = () => TweakState.NotApplicable, ApplyAction = _ => { },
        };

        // Applicable (or still checking): no note at all.
        foreach (var s in new TweakState?[] { null, TweakState.Applied, TweakState.NotApplied, TweakState.Partial, TweakState.Unknown })
        {
            Assert.Null(OptimizeLogic.StatusNote(plain, s, null));
            Assert.Null(OptimizeLogic.StatusNote(reasoned, s, "Сбалансированная схема питания"));
        }

        // Not applicable: the tweak's own reason, or the generic sentence.
        Assert.Equal("Только для Windows 11 на сбалансированной схеме питания.", OptimizeLogic.StatusNote(reasoned, TweakState.NotApplicable, null));
        Assert.Equal("Не подходит для этого компьютера или этой версии Windows: нужного компонента нет в системе.",
            OptimizeLogic.StatusNote(plain, TweakState.NotApplicable, null));

        // Not applicable, but a required tweak is checked and pending in the same apply: the row will apply after it.
        Assert.Equal("Применится после «Сбалансированная схема питания».",
            OptimizeLogic.StatusNote(reasoned, TweakState.NotApplicable, "Сбалансированная схема питания"));
        Assert.Equal("Применится после «Сбалансированная схема питания».", OptimizeLogic.DependencyNote("Сбалансированная схема питания"));
    }

    [Fact]
    public void Category_titles_match_the_tweak_model()
    {
        foreach (var c in Enum.GetValues<TweakCategory>())
        {
            var t = Tweak("x", "x", category: c);
            Assert.Equal(t.CategoryText, OptimizeLogic.CategoryTitle(c));
            Assert.False(string.IsNullOrWhiteSpace(OptimizeLogic.CategoryDescription(c)));
        }
    }

    // ───────────────────────────── Plural ─────────────────────────────

    [Theory]
    [InlineData(0, "пунктов")]
    [InlineData(1, "пункт")]
    [InlineData(2, "пункта")]
    [InlineData(4, "пункта")]
    [InlineData(5, "пунктов")]
    [InlineData(11, "пунктов")]
    [InlineData(12, "пунктов")]
    [InlineData(14, "пунктов")]
    [InlineData(21, "пункт")]
    [InlineData(22, "пункта")]
    [InlineData(25, "пунктов")]
    [InlineData(101, "пункт")]
    [InlineData(111, "пунктов")]
    [InlineData(-3, "пункта")]
    public void Russian_plural_forms(int n, string expected) =>
        Assert.Equal(expected, OptimizeLogic.Plural(n, "пункт", "пункта", "пунктов"));

    // ───────────────────────────── Search ─────────────────────────────

    [Fact]
    public void Search_normalizes_case_whitespace_and_yo()
    {
        Assert.Equal("елка", OptimizeLogic.NormalizeSearch("  ЁЛКА "));
        Assert.Equal("", OptimizeLogic.NormalizeSearch(null));
        Assert.Equal("", OptimizeLogic.NormalizeSearch("   "));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("игровой", true)]
    [InlineData("ИГРОВОЙ WINDOWS", true)]
    [InlineData("windows игровой", true)]
    [InlineData("драйверов", true)]
    [InlineData("приоритет", true)]
    [InlineData("game-mode", true)]
    [InlineData("игры", true)]
    [InlineData("безопасно", true)]
    [InlineData("игровой ёлка", false)]
    [InlineData("телеметрия", false)]
    public void Search_requires_every_word(string query, bool expected)
    {
        var t = Tweak("game-mode", "Игровой режим Windows", "Windows не ставит обновления драйверов во время игры.",
            "Даёт приоритет запущенной игре.");
        Assert.Equal(expected, OptimizeLogic.Matches(t, query));
    }

    [Fact]
    public void Search_treats_yo_and_ye_as_equal()
    {
        var t = Tweak("x", "Отключить всё лишнее", "Ещё быстрее");
        Assert.True(OptimizeLogic.Matches(t, "все"));
        Assert.True(OptimizeLogic.Matches(t, "ЕЩЕ"));
        Assert.True(OptimizeLogic.Matches(t, "всё"));
    }

    // ───────────────────────────── Collection sync ─────────────────────────────

    private sealed record Row(string Name);

    [Fact]
    public void Sync_produces_the_desired_order_and_keeps_instances()
    {
        Row a = new("a"), b = new("b"), c = new("c"), d = new("d"), x = new("x");
        var target = new ObservableCollection<Row> { a, b, c, d };
        var desired = new List<Row> { d, a, x };

        OptimizeLogic.Sync(target, desired);

        Assert.Equal(desired, target);
        Assert.Same(d, target[0]);
        Assert.Same(a, target[1]);
    }

    [Fact]
    public void Sync_handles_empty_and_identical_lists()
    {
        Row a = new("a"), b = new("b");
        var target = new ObservableCollection<Row>();
        OptimizeLogic.Sync(target, [a, b]);
        Assert.Equal(new[] { a, b }, target);

        var events = 0;
        target.CollectionChanged += (_, _) => events++;
        OptimizeLogic.Sync(target, [a, b]);
        Assert.Equal(0, events);

        OptimizeLogic.Sync(target, []);
        Assert.Empty(target);
    }

    [Fact]
    public void Sync_only_inserts_what_is_missing()
    {
        Row head = new("head"), filter = new("filter"), s1 = new("s1"), r1 = new("r1"), r2 = new("r2"), r3 = new("r3");
        var target = new ObservableCollection<Row> { head, filter, s1, r1, r3 };
        var ops = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => ops.Add(e.Action);

        OptimizeLogic.Sync(target, [head, filter, s1, r1, r2, r3]);

        Assert.Equal(new[] { head, filter, s1, r1, r2, r3 }, target);
        Assert.Equal(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Add }, ops);
    }

    [Fact]
    public void Sync_handles_reversal()
    {
        var rows = Enumerable.Range(0, 8).Select(i => new Row(i.ToString())).ToList();
        var target = new ObservableCollection<Row>(rows);
        var reversed = Enumerable.Reverse(rows).ToList();
        OptimizeLogic.Sync(target, reversed);
        Assert.Equal(reversed, target);
    }

    // ───────────────────────────── Descriptions ─────────────────────────────

    [Theory]
    [InlineData(@"reg:HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled", @"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled")]
    [InlineData("svc:DiagTrack", "Служба DiagTrack")]
    [InlineData(@"task:\Microsoft\Windows\Maps\MapsUpdateTask", @"Задача \Microsoft\Windows\Maps\MapsUpdateTask")]
    [InlineData("power:active", "Активная схема питания")]
    [InlineData("cmd:hibernate", "Параметр hibernate")]
    [InlineData("something", "something")]
    [InlineData("odd:value", "odd:value")]
    [InlineData("", "—")]
    public void Journal_targets_are_readable(string target, string expected) =>
        Assert.Equal(expected, OptimizeLogic.DescribeTarget(target));

    [Fact]
    public void New_registry_values_are_formatted()
    {
        Assert.Equal("1 (DWORD)", OptimizeLogic.FormatNewValue(RegistryValueKind.DWord, 1));
        Assert.Equal("38 (0x26) (DWORD)", OptimizeLogic.FormatNewValue(RegistryValueKind.DWord, 38));
        Assert.Equal("4294967295 (0xFFFFFFFF) (DWORD)", OptimizeLogic.FormatNewValue(RegistryValueKind.DWord, -1));
        Assert.Equal("удалить значение", OptimizeLogic.FormatNewValue(RegistryValueKind.DWord, null));
        Assert.Equal("«SwapEffectUpgradeEnable=1;»", OptimizeLogic.FormatNewValue(RegistryValueKind.String, "SwapEffectUpgradeEnable=1;"));
        Assert.Equal("01 0A FF", OptimizeLogic.FormatNewValue(RegistryValueKind.Binary, new byte[] { 1, 10, 255 }));
        Assert.Equal("«a»; «b»", OptimizeLogic.FormatNewValue(RegistryValueKind.MultiString, new[] { "a", "b" }));
    }

    [Fact]
    public void Current_registry_values_are_formatted()
    {
        Assert.Equal("нет значения", OptimizeLogic.FormatCurrentValue(null));
        Assert.Equal("0", OptimizeLogic.FormatCurrentValue(0));
        Assert.Equal("4294967295 (0xFFFFFFFF)", OptimizeLogic.FormatCurrentValue(-1));
        Assert.Equal("«6»", OptimizeLogic.FormatCurrentValue("6"));
        Assert.Equal("пусто", OptimizeLogic.FormatCurrentValue(Array.Empty<byte>()));
    }

    [Fact]
    public void Planned_changes_describe_registry_tweaks()
    {
        var t = new RegistryTweak
        {
            Id = "r", Title = "R", Description = "",
            Values =
            [
                RegSet.Dword(RegHive.HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
                RegSet.Delete(RegHive.HKLM, @"SOFTWARE\Test", "Gone"),
                RegSet.Str(RegHive.HKCU, @"Control Panel\Mouse", "", "0"),
            ],
        };
        var lines = OptimizeLogic.PlannedChanges(t);
        Assert.Equal(3, lines.Count);
        Assert.Equal(@"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled", lines[0].Target);
        Assert.Equal("1 (DWORD)", lines[0].NewValue);
        Assert.Equal(@"HKLM\SOFTWARE\Test\Gone", lines[1].Target);
        Assert.Equal("удалить значение", lines[1].NewValue);
        Assert.Equal(@"HKCU\Control Panel\Mouse\(По умолчанию)", lines[2].Target);
        Assert.All(lines, l => Assert.NotNull(l.ReadCurrent));
    }

    [Fact]
    public void Planned_changes_describe_services_and_tasks()
    {
        var svc = new ServiceTweak
        {
            Id = "s", Title = "S", Description = "",
            Services = [("DiagTrack", ServiceStartMode.Manual), ("Fax", ServiceStartMode.Disabled)],
        };
        var svcLines = OptimizeLogic.PlannedChanges(svc);
        Assert.Equal("Служба DiagTrack · тип запуска", svcLines[0].Target);
        Assert.Equal("Вручную", svcLines[0].NewValue);
        Assert.Equal("Отключена", svcLines[1].NewValue);

        var task = new ScheduledTaskTweak { Id = "t", Title = "T", Description = "", Tasks = [@"\Microsoft\Windows\Maps\MapsUpdateTask"] };
        var taskLines = OptimizeLogic.PlannedChanges(task);
        Assert.Equal(@"Задача \Microsoft\Windows\Maps\MapsUpdateTask", taskLines[0].Target);
        Assert.Equal("отключена", taskLines[0].NewValue);

        Assert.Empty(OptimizeLogic.PlannedChanges(Tweak("c", "C")));
    }

    // ───────────────────────────── Custom selection store ─────────────────────────────

    [Fact]
    public void Custom_selection_parses_valid_json()
    {
        var ids = CustomSelectionStore.Parse("[\"game-mode\", \" hags \", \"\", null, \"game-mode\"]");
        Assert.NotNull(ids);
        Assert.True(ids.SetEquals(["game-mode", "hags"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2,3]")]
    public void Custom_selection_rejects_garbage(string? json) => Assert.Null(CustomSelectionStore.Parse(json));

    [Fact]
    public void Custom_selection_round_trips_sorted_and_distinct()
    {
        var json = CustomSelectionStore.Serialize(["b", "a", "b", "c"]);
        Assert.Equal("[\"a\",\"b\",\"c\"]", json);
        var parsed = CustomSelectionStore.Parse(json);
        Assert.NotNull(parsed);
        Assert.True(parsed.SetEquals(["a", "b", "c"]));
    }

    // ───────────────────────────── Data loss / merged records ─────────────────────────────

    [Fact]
    public void Recall_snapshot_tweak_is_marked_as_data_loss_and_exists_in_catalog()
    {
        Assert.Contains(TweakCatalog.All, t => t.Id == "recall-snapshots-off");
        Assert.NotNull(OptimizeLogic.DataLossNote("recall-snapshots-off"));
        Assert.Null(OptimizeLogic.DataLossNote("some-other-tweak"));
    }

    [Fact]
    public void Other_sources_of_merged_records_exclude_own_ids_and_dedupe()
    {
        var others = OptimizeLogic.OtherTweakSources(
            ["tweak:a", "tweak:b", "tweak:a", "services", "tweak:c", "tweak:b", ""], "tweak:", ["a"]);
        Assert.Equal(["b", "services", "c"], others);
        Assert.Empty(OptimizeLogic.OtherTweakSources(["tweak:a"], "tweak:", ["a"]));
    }
}
