using System.Collections.ObjectModel;
using System.ServiceProcess;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Modules.Optimize;
using Monolith.Tweaks;

namespace Monolith.Tests;

/// <summary>Pure logic of the Optimize page. Nothing here touches the real system.</summary>
public sealed class OptimizeTests
{
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

    [Fact]
    public void Profiles_map_to_catalog_kinds()
    {
        Assert.Equal(ProfileKind.Normal, OptimizeLogic.ToKind(OptimizeProfile.Normal));
        Assert.Equal(ProfileKind.Gaming, OptimizeLogic.ToKind(OptimizeProfile.Gaming));
        Assert.Equal(ProfileKind.Recommended, OptimizeLogic.ToKind(OptimizeProfile.Recommended));
        Assert.Null(OptimizeLogic.ToKind(OptimizeProfile.Custom));
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
}
