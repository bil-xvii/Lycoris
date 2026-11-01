using Monolith.Core;
using Monolith.Modules.Optimize;
using Monolith.Tweaks;

namespace Monolith.Tests;

/// <summary>
/// Selection rules of the Optimize page: exclusive groups (one system target → one checked member), dependencies
/// («Requires»: a row applies after the tweak it needs) and the apply order of a batch. Pure — synthetic tweaks only,
/// nothing is written. The catalog invariants pass trivially until the scenarios of wave 3 assign the groups.
/// </summary>
public sealed class TweakSelectionTests
{
    private static CustomTweak Tweak(string id, string? group = null, IReadOnlyList<string>? requires = null,
        Func<TweakState>? detect = null, TweakCategory category = TweakCategory.Power, Func<SystemProfile, bool>? appliesTo = null,
        string? notApplicableReason = null) => new()
    {
        Id = id,
        Title = "Пункт " + id.ToUpperInvariant(),
        Description = "",
        Category = category,
        ExclusiveGroup = group,
        Requires = requires ?? [],
        AppliesTo = appliesTo,
        NotApplicableReason = notApplicableReason,
        DetectFunc = detect ?? (() => TweakState.NotApplied),
        ApplyAction = _ => { },
    };

    private static string[] Ids(IEnumerable<Tweak> tweaks) => tweaks.Select(t => t.Id).ToArray();

    // ───────────────────────────── Apply order ─────────────────────────────

    [Fact]
    public void Order_puts_power_plan_switches_first_and_keeps_catalog_order_otherwise()
    {
        var batch = new List<Tweak>
        {
            Tweak("a"), Tweak("plan-1", TweakGroups.PowerPlan), Tweak("b"), Tweak("mode", TweakGroups.PowerModeAc),
            Tweak("plan-2", TweakGroups.PowerPlan), Tweak("c"),
        };
        Assert.Equal(["plan-1", "plan-2", "a", "b", "mode", "c"], Ids(TweakEngine.OrderForApply(batch)));
    }

    [Fact]
    public void Order_moves_a_dependent_right_after_the_last_of_its_dependencies()
    {
        var batch = new List<Tweak> { Tweak("dep", requires: ["x", "y"]), Tweak("a"), Tweak("x"), Tweak("b"), Tweak("y"), Tweak("c") };
        Assert.Equal(["a", "x", "b", "y", "dep", "c"], Ids(TweakEngine.OrderForApply(batch)));
    }

    [Fact]
    public void Order_resolves_chains_and_dependencies_already_in_place_stay_put()
    {
        var chain = new List<Tweak> { Tweak("c", requires: ["b"]), Tweak("b", requires: ["a"]), Tweak("a") };
        Assert.Equal(["a", "b", "c"], Ids(TweakEngine.OrderForApply(chain)));

        var fine = new List<Tweak> { Tweak("a"), Tweak("b", requires: ["a"]), Tweak("c") };
        Assert.Equal(["a", "b", "c"], Ids(TweakEngine.OrderForApply(fine)));

        // A dependency outside the batch (already applied, or not selected) changes nothing.
        var outside = new List<Tweak> { Tweak("m", requires: ["not-here"]), Tweak("n") };
        Assert.Equal(["m", "n"], Ids(TweakEngine.OrderForApply(outside)));
    }

    [Fact]
    public void Order_is_pure_and_survives_a_cycle()
    {
        var input = new List<Tweak> { Tweak("a", requires: ["b"]), Tweak("b", requires: ["a"]), Tweak("c") };
        var before = Ids(input);
        var ordered = TweakEngine.OrderForApply(input);
        Assert.Equal(before, Ids(input)); // input untouched
        Assert.Equal(3, ordered.Count);
        Assert.Equal(before.Order(), Ids(ordered).Order());
        Assert.Empty(TweakEngine.OrderForApply([]));
    }

    [Fact]
    public void Plan_switch_before_a_plan_scoped_setting_that_needs_it()
    {
        // The real shape of a scenario: the dependent power mode after the Balanced plan, both after a plan-scoped setting
        // placed earlier in the catalog would still be wrong — the plan comes first.
        var batch = new List<Tweak>
        {
            Tweak("powercfg-setting"), Tweak("power-mode", TweakGroups.PowerModeAc, ["power-plan-balanced"]),
            Tweak("power-plan-balanced", TweakGroups.PowerPlan),
        };
        Assert.Equal(["power-plan-balanced", "powercfg-setting", "power-mode"], Ids(TweakEngine.OrderForApply(batch)));
    }

    [Fact]
    public void Order_is_idempotent_so_the_shared_apply_primitive_may_order_again()
    {
        // ApplyInSession orders every batch itself (a page collecting tweaks in its own order gets the same guarantees as
        // the Optimize page); the Optimize batch passes through it already ordered and must not move again.
        var batch = new List<Tweak>
        {
            Tweak("powercfg-setting"), Tweak("power-mode", TweakGroups.PowerModeAc, ["power-plan-balanced"]),
            Tweak("power-plan-balanced", TweakGroups.PowerPlan), Tweak("dep", requires: ["z"]), Tweak("z"),
        };
        var once = TweakEngine.OrderForApply(batch);
        Assert.Equal(["power-plan-balanced", "powercfg-setting", "power-mode", "z", "dep"], Ids(once));
        Assert.Equal(Ids(once), Ids(TweakEngine.OrderForApply(once)));
    }

    [Fact]
    public void Requires_cycles_are_found_and_an_acyclic_graph_has_none()
    {
        Assert.Equal(["a", "b"], TweakEngine.RequiresCycles([Tweak("a", requires: ["b"]), Tweak("b", requires: ["a"]), Tweak("c", requires: ["a"])]));
        Assert.Equal(["x", "y", "z"], TweakEngine.RequiresCycles([Tweak("x", requires: ["y"]), Tweak("y", requires: ["z"]), Tweak("z", requires: ["x"])]));
        Assert.Equal(["s"], TweakEngine.RequiresCycles([Tweak("s", requires: ["s"])]));
        Assert.Empty(TweakEngine.RequiresCycles([Tweak("c", requires: ["b", "a"]), Tweak("b", requires: ["a"]), Tweak("a"), Tweak("m", requires: ["ghost"])]));
        Assert.Empty(TweakEngine.RequiresCycles([]));
    }

    [Fact]
    public void Skip_reason_names_the_dependency_from_the_batch()
    {
        var plan = Tweak("power-plan-balanced", TweakGroups.PowerPlan);
        var mode = Tweak("mode", requires: ["power-plan-balanced-x3d", "power-plan-balanced"]);
        Assert.Equal("Пункт MODE: зависит от «Пункт POWER-PLAN-BALANCED», которое не применилось",
            TweakEngine.DependencySkipReason(mode, [plan, mode]));
        Assert.Equal("Пункт MODE: «Пункт POWER-PLAN-BALANCED» применено, но пункт по-прежнему неприменим на этом ПК",
            TweakEngine.DependencySkipReason(mode, [plan, mode], dependencyApplied: true));
        // Not in the batch: the real catalog title of the first dependency that exists, else the id itself.
        Assert.Contains("«Схема питания для Ryzen X3D", TweakEngine.DependencySkipReason(mode, [mode]));
        Assert.Equal("Пункт Z: зависит от «ghost», которое не применилось", TweakEngine.DependencySkipReason(Tweak("z", requires: ["ghost"]), []));
    }

    [Fact]
    public void Run_result_reports_no_dependency_skips_by_default()
    {
        var r = new TweakRunResult(0, 0, [], null, RestartRequirement.None);
        Assert.Empty(r.SkippedReasons);
        Assert.Empty(r.SkippedIds);
    }

    // ───────────────────────────── Exclusive groups (pure) ─────────────────────────────

    [Fact]
    public void Group_membership_is_by_name_and_never_includes_the_tweak_itself()
    {
        Tweak a = Tweak("a", "g"), b = Tweak("b", "g"), c = Tweak("c", "other"), d = Tweak("d");
        Assert.True(a.SharesGroupWith(b));
        Assert.False(a.SharesGroupWith(a));
        Assert.False(a.SharesGroupWith(c));
        Assert.False(d.SharesGroupWith(a));
        Assert.False(a.SharesGroupWith(d));
        Assert.Equal(["b"], Ids(OptimizeLogic.ExclusivePeers(a, [a, b, c, d])));
        Assert.Empty(OptimizeLogic.ExclusivePeers(d, [a, b, c, d]));
    }

    [Fact]
    public void Surplus_members_are_every_group_member_after_the_first()
    {
        Tweak a = Tweak("a", "g"), b = Tweak("b", "g"), c = Tweak("c", "g"), h1 = Tweak("h1", "h"), h2 = Tweak("h2", "h"), n = Tweak("n");
        Assert.Equal(["b", "c", "h2"], Ids(OptimizeLogic.ExclusiveSurplus([a, b, n, h1, c, h2])));
        Assert.Empty(OptimizeLogic.ExclusiveSurplus([a, h1, n]));
    }

    [Fact]
    public void Checking_a_member_unchecks_exactly_the_checked_peers()
    {
        Tweak a = Tweak("a", "g"), b = Tweak("b", "g"), c = Tweak("c", "g"), h = Tweak("h", "h"), n = Tweak("n");
        var rows = new List<(Tweak, bool)> { (a, true), (b, false), (c, true), (h, true), (n, true) };
        Assert.Equal(["a", "c"], Ids(OptimizeLogic.MembersToUncheck(b, rows)));
        Assert.Empty(OptimizeLogic.MembersToUncheck(n, rows));
        Assert.Empty(OptimizeLogic.MembersToUncheck(h, rows));
        // Nothing else of the group checked: nothing to uncheck.
        Assert.Empty(OptimizeLogic.MembersToUncheck(a, [(a, true), (b, false), (c, false)]));
    }

    [Fact]
    public void Section_is_all_selected_when_each_group_has_one_checked_member()
    {
        Tweak p1 = Tweak("p1", "plan"), p2 = Tweak("p2", "plan"), p3 = Tweak("p3", "plan"), n = Tweak("n"), m = Tweak("m");
        Assert.True(OptimizeLogic.AllSelected([(p1, true), (p2, false), (p3, false), (n, true), (m, true)]));
        Assert.True(OptimizeLogic.AllSelected([(p1, false), (p2, false), (p3, true), (n, true)]));
        Assert.False(OptimizeLogic.AllSelected([(p1, true), (p2, false), (p3, false), (n, false), (m, true)]), "a plain row is unchecked");
        Assert.False(OptimizeLogic.AllSelected([(p1, false), (p2, false), (p3, false), (n, true)]), "no member of the group is checked");
        Assert.False(OptimizeLogic.AllSelected([(n, false)]));
        Assert.False(OptimizeLogic.AllSelected([]), "an empty section is never «all selected»");
        Assert.True(OptimizeLogic.AllSelected([(n, true), (m, true)]));
        // Two header clicks: select (one member stays after the group rule) → «all selected» → the second click clears.
        var afterFirstClick = new List<(Tweak, bool)> { (p1, true), (p2, false), (p3, false), (n, true) };
        Assert.True(OptimizeLogic.AllSelected(afterFirstClick));
    }

    [Fact]
    public void Replaces_note_lists_the_applied_members()
    {
        Assert.Equal("заменит: «Схема A»", OptimizeLogic.ReplacesNote(["Схема A"]));
        Assert.Equal("заменит: «A», «B»", OptimizeLogic.ReplacesNote(["A", "", "B"]));
        Assert.Null(OptimizeLogic.ReplacesNote([]));
    }

    // ───────────────────────────── Dependencies (pure) ─────────────────────────────

    [Fact]
    public void Dependency_is_satisfied_by_any_pending_required_tweak()
    {
        var t = Tweak("mode", requires: ["plan-x3d", "plan"]);
        Assert.Equal("plan", OptimizeLogic.PendingDependency(t, id => id == "plan"));
        Assert.Equal("plan-x3d", OptimizeLogic.PendingDependency(t, _ => true));
        Assert.Null(OptimizeLogic.PendingDependency(t, _ => false));
        Assert.Null(OptimizeLogic.PendingDependency(Tweak("plain"), _ => true));
    }

    [Fact]
    public void Row_is_applicable_now_by_its_own_state_or_through_a_pending_dependency()
    {
        var mode = Tweak("mode", requires: ["plan"]);
        Assert.True(OptimizeLogic.IsApplicableNow(TweakState.NotApplied, mode, _ => false));
        Assert.True(OptimizeLogic.IsApplicableNow(null, mode, _ => false), "unknown yet: not refused");
        Assert.False(OptimizeLogic.IsApplicableNow(TweakState.NotApplicable, mode, _ => false));
        Assert.True(OptimizeLogic.IsApplicableNow(TweakState.NotApplicable, mode, id => id == "plan"));
        Assert.False(OptimizeLogic.IsApplicableNow(TweakState.NotApplicable, mode, id => id == "other"));
        Assert.False(OptimizeLogic.IsApplicableNow(TweakState.NotApplicable, Tweak("plain"), _ => true), "no dependency to lean on");
    }

    [Fact]
    public void Profile_tile_counts_a_dependent_default_when_its_dependency_will_be_pending()
    {
        var defaults = new HashSet<string>(StringComparer.Ordinal) { "plan", "mode", "other" };
        var mode = Tweak("mode", requires: ["plan"]);
        Assert.True(OptimizeLogic.IsProfileMember(Tweak("plan"), true, defaults, _ => true));
        Assert.False(OptimizeLogic.IsProfileMember(Tweak("stranger"), true, defaults, _ => true), "not a default of this profile");
        Assert.True(OptimizeLogic.IsProfileMember(mode, false, defaults, id => id == "plan"), "not applicable itself, the Balanced plan is about to be switched on");
        Assert.False(OptimizeLogic.IsProfileMember(mode, false, defaults, id => id == "other"), "the plan is already active or not applicable: the row's own detection decides");
        Assert.False(OptimizeLogic.IsProfileMember(Tweak("mode", requires: ["plan-x3d"]), false, defaults, _ => true), "its dependency is not a default of the profile");
        Assert.True(OptimizeLogic.IsProfileMember(mode, true, defaults, _ => false), "applicable on its own");
    }

    [Fact]
    public void Status_note_depends_on_state_and_pending_dependency()
    {
        var t = Tweak("mode", requires: ["plan"], notApplicableReason: "Только на сбалансированной схеме.");
        Assert.Null(OptimizeLogic.StatusNote(t, TweakState.NotApplied, "Сбалансированная схема"));
        Assert.Null(OptimizeLogic.StatusNote(t, null, null));
        Assert.Equal("Применится после «Сбалансированная схема».", OptimizeLogic.StatusNote(t, TweakState.NotApplicable, "Сбалансированная схема"));
        Assert.Equal("Только на сбалансированной схеме.", OptimizeLogic.StatusNote(t, TweakState.NotApplicable, null));
        Assert.StartsWith("Не подходит для этого компьютера", OptimizeLogic.StatusNote(Tweak("x"), TweakState.NotApplicable, null));
    }

    // ───────────────────────────── View-model behaviour (synthetic catalog) ─────────────────────────────
    // Windows only: OptimizeViewModel owns WPF DispatcherTimers, so its constructor cannot load on Linux
    // (BadImageFormatException there is by design, like the other WPF-bound tests).

    private static OptimizeViewModel Page(params Tweak[] tweaks)
    {
        var vm = new OptimizeViewModel();
        vm.BuildCatalog(tweaks);
        return vm;
    }

    private static TweakItemViewModel Row(OptimizeViewModel vm, string id) => vm.Items.Single(i => i.Id == id);

    [Fact]
    public void Checking_a_group_member_unchecks_the_other_members()
    {
        var vm = Page(Tweak("a", "g"), Tweak("b", "g"), Tweak("c", "g"), Tweak("n"));
        foreach (var i in vm.Items) i.State = TweakState.NotApplied;
        Row(vm, "a").IsChecked = true;
        Row(vm, "n").IsChecked = true;
        Assert.True(Row(vm, "a").IsChecked);

        Row(vm, "b").IsChecked = true;

        Assert.False(Row(vm, "a").IsChecked);
        Assert.True(Row(vm, "b").IsChecked);
        Assert.False(Row(vm, "c").IsChecked);
        Assert.True(Row(vm, "n").IsChecked, "members of other groups and plain tweaks are not touched");

        Row(vm, "b").IsChecked = false; // unchecking never checks anything else
        Assert.All(vm.Items.Where(i => i.Tweak.ExclusiveGroup == "g"), i => Assert.False(i.IsChecked));
    }

    [Fact]
    public void Exclusive_enforcement_notifies_the_unchecked_row()
    {
        var vm = Page(Tweak("a", "g"), Tweak("b", "g"));
        foreach (var i in vm.Items) i.State = TweakState.NotApplied;
        Row(vm, "a").IsChecked = true;
        var raised = new List<string?>();
        Row(vm, "a").PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Row(vm, "b").IsChecked = true;
        Assert.Contains(nameof(TweakItemViewModel.IsChecked), raised);
        Assert.Contains(nameof(TweakItemViewModel.IsPending), raised);
    }

    [Fact]
    public void Dependent_row_is_applicable_only_while_its_dependency_is_pending()
    {
        var vm = Page(Tweak("plan", TweakGroups.PowerPlan), Tweak("mode", TweakGroups.PowerModeAc, ["plan"], notApplicableReason: "Только на сбалансированной схеме."));
        var plan = Row(vm, "plan");
        var mode = Row(vm, "mode");
        plan.State = TweakState.NotApplied;
        mode.State = TweakState.NotApplicable;

        Assert.False(mode.IsApplicable);
        Assert.False(mode.IsApplicableNow);
        Assert.Equal("Только на сбалансированной схеме.", mode.StatusNote);
        mode.IsChecked = true;
        Assert.False(mode.IsChecked, "a not-applicable row cannot be checked without a pending dependency");

        plan.IsChecked = true;
        Assert.True(mode.IsApplicableNow);
        Assert.True(mode.DependencySatisfiedByPending);
        Assert.Equal("Применится после «Пункт PLAN».", mode.StatusNote);
        mode.IsChecked = true;
        Assert.True(mode.IsChecked);
        Assert.True(mode.IsPending, "the dependent row is part of the apply");
        Assert.Equal(2, vm.ChangeCount);

        plan.IsChecked = false;
        Assert.False(mode.IsChecked, "no dependency pending any more: the dependent row is unchecked again");
        Assert.False(mode.IsApplicableNow);
        Assert.Equal("Только на сбалансированной схеме.", mode.StatusNote);
    }

    [Fact]
    public void Two_header_clicks_on_a_section_with_a_group_end_with_nothing_checked()
    {
        var vm = Page(Tweak("p1", "plan"), Tweak("p2", "plan"), Tweak("p3", "plan"), Tweak("p4", "plan"), Tweak("n"));
        foreach (var i in vm.Items) i.State = TweakState.NotApplied;
        vm.SelectCategory(null);
        var section = vm.Rows.OfType<SectionRow>().Single(s => s.Category == TweakCategory.Power);

        vm.ToggleSection(section);
        Assert.Equal(["p1", "n"], Ids(vm.Items.Where(i => i.IsChecked).Select(i => i.Tweak)));
        Assert.True(section.CheckState, "one member per group: the header shows «all selected», not indeterminate");

        vm.ToggleSection(section);
        Assert.DoesNotContain(vm.Items, i => i.IsChecked);
        Assert.False(section.CheckState);
    }

    [Fact]
    public void Profile_tile_and_change_count_agree_on_a_dependent_row()
    {
        // The real wave-3 shape: Balanced plan + a power mode that is applicable only on the Balanced plan; the PC is on
        // High performance right now, so the mode detects as not applicable.
        var vm = Page(Tweak("plan", TweakGroups.PowerPlan), Tweak("mode", TweakGroups.PowerModeAc, ["plan"]));
        vm.SetProfileDefaults(OptimizeProfile.Gaming, ["plan", "mode"]);
        Row(vm, "plan").State = TweakState.NotApplied;
        Row(vm, "mode").State = TweakState.NotApplicable;
        vm.MarkDetected();
        var tile = vm.Profiles.Single(t => t.Kind == OptimizeProfile.Gaming);
        Assert.Equal(2, tile.Total);
        Assert.Equal(2, tile.ToChange);

        vm.SelectProfile(OptimizeProfile.Gaming);
        Assert.True(Row(vm, "mode").IsChecked);
        Assert.Equal(2, vm.ChangeCount);
        Assert.Equal(2, tile.ToChange);
    }

    [Fact]
    public void Efficiency_tile_joins_the_row_only_with_a_member_beyond_a_plan_switch_and_takes_the_laptop_title()
    {
        var vm = Page(Tweak("plan", TweakGroups.PowerPlan), Tweak("mode", TweakGroups.PowerModeAc, ["plan"]), Tweak("n"));
        vm.SetProfileDefaults(OptimizeProfile.Efficiency, ["plan", "mode"]);
        Assert.False(vm.IsEfficiencyTileVisible, "before detection the hardware facts are unknown");
        Assert.Equal(5, vm.Profiles.Count);

        // Windows 10 shape: the plan is already Balanced, the power mode does not exist → nothing would get quieter.
        Row(vm, "plan").State = TweakState.Applied;
        Row(vm, "mode").State = TweakState.NotApplicable;
        Row(vm, "n").State = TweakState.NotApplied;
        vm.MarkDetected();
        Assert.False(vm.IsEfficiencyTileVisible);

        // The mode is applicable (Windows 11 on Balanced): the tile appears in catalog order, between «Стриминг» and «Свой набор».
        Row(vm, "mode").State = TweakState.NotApplied;
        vm.MarkDetected();
        Assert.True(vm.IsEfficiencyTileVisible);
        Assert.Equal([OptimizeProfile.Recommended, OptimizeProfile.Gaming, OptimizeProfile.Normal, OptimizeProfile.Streaming, OptimizeProfile.Efficiency, OptimizeProfile.Custom],
            vm.Profiles.Select(t => t.Kind).ToArray());
        var tile = vm.Profiles.Single(t => t.Kind == OptimizeProfile.Efficiency);
        Assert.Equal("ТИХИЙ ПК", tile.Title);
        Assert.Equal(2, tile.Total);
        Assert.Equal(1, tile.ToChange);

        // The mode on High performance (not applicable on its own) still counts through its pending dependency.
        Row(vm, "plan").State = TweakState.NotApplied;
        Row(vm, "mode").State = TweakState.NotApplicable;
        vm.MarkDetected(laptop: true);
        Assert.True(vm.IsEfficiencyTileVisible);
        Assert.Equal("НОУТБУК: АВТОНОМНОСТЬ", tile.Title);
        Assert.Equal("Ноутбук: автономность", tile.SentenceName);
        Assert.Equal(2, tile.ToChange);

        // Unknown hardware facts (a WMI failure) hide it again, and a selected efficiency scenario falls back to the default one.
        vm.SelectProfile(OptimizeProfile.Efficiency);
        Assert.Equal(OptimizeProfile.Efficiency, vm.Profile);
        vm.MarkDetected(hardwareFactsKnown: false);
        Assert.False(vm.IsEfficiencyTileVisible);
        Assert.Equal(OptimizeProfile.Recommended, vm.Profile);
    }

    [Fact]
    public void A_row_the_hardware_rules_out_is_never_made_applicable_by_a_pending_dependency()
    {
        // Windows 10 desktop on High performance: the Balanced plan is pending, but the Windows 11 power mode stays out —
        // not checked, not counted on the tile, and the efficiency tile (only a plan switch left) is hidden.
        var vm = Page(Tweak("plan", TweakGroups.PowerPlan), Tweak("mode", TweakGroups.PowerModeAc, ["plan"]));
        vm.SetProfileDefaults(OptimizeProfile.Efficiency, ["plan", "mode"]);
        vm.SetHardwareExcluded("mode");
        Row(vm, "plan").State = TweakState.NotApplied;
        Row(vm, "mode").State = TweakState.NotApplicable;
        vm.MarkDetected();
        Assert.False(vm.IsEfficiencyTileVisible);

        Row(vm, "plan").IsChecked = true;
        Row(vm, "mode").IsChecked = true;
        Assert.False(Row(vm, "mode").IsApplicableNow);
        Assert.False(Row(vm, "mode").DependencySatisfiedByPending);
        vm.SyncDependents();
        Assert.False(Row(vm, "mode").IsChecked);
    }

    [Fact]
    public void A_requires_cycle_does_not_overflow_the_view_model()
    {
        var vm = Page(Tweak("a", requires: ["b"]), Tweak("b", requires: ["a"]));
        foreach (var i in vm.Items) { i.State = TweakState.NotApplied; i.SetChecked(true); }
        foreach (var i in vm.Items) i.State = TweakState.NotApplicable;
        Assert.All(vm.Items, i => Assert.False(i.IsApplicableNow));
        Assert.All(vm.Items, i => Assert.Null(i.PendingDependency));
        vm.SyncDependents();
        Assert.All(vm.Items, i => Assert.False(i.IsChecked));
    }

    [Fact]
    public void Dependency_already_applied_is_not_pending_and_the_row_follows_its_own_detection()
    {
        var vm = Page(Tweak("plan"), Tweak("mode", requires: ["plan"]));
        Row(vm, "plan").State = TweakState.Applied;
        Row(vm, "plan").SetChecked(true);
        Row(vm, "mode").State = TweakState.NotApplicable;
        // Applied + checked is not «pending»: the dependent row's own detection decides (here: still not applicable).
        Assert.False(Row(vm, "mode").IsApplicableNow);
        Row(vm, "mode").State = TweakState.NotApplied;
        Assert.True(Row(vm, "mode").IsApplicableNow);
        Assert.Null(Row(vm, "mode").StatusNote);
    }

    [Fact]
    public void Dependent_rows_are_unchecked_when_detection_shows_the_dependency_is_gone()
    {
        var vm = Page(Tweak("plan"), Tweak("mode", requires: ["plan"]));
        var plan = Row(vm, "plan");
        var mode = Row(vm, "mode");
        plan.State = TweakState.NotApplied;
        mode.State = TweakState.NotApplicable;
        plan.IsChecked = true;
        mode.IsChecked = true;
        Assert.True(mode.IsChecked);

        // The dependency turns out to be not applicable on this PC: the row that leaned on it must not stay selected.
        plan.State = TweakState.NotApplicable;
        plan.SetChecked(false);
        vm.OnSelectionEditedByUser();
        Assert.False(mode.IsChecked);
    }

    // ───────────────────────────── Catalog invariants (guard wave 3) ─────────────────────────────

    private static SystemProfile Stub(string cpu, int cores, bool battery)
    {
        var sp = new SystemProfile();
        void Set(string name, object value) => typeof(SystemProfile).GetProperty(name)!.SetValue(sp, value);
        Set(nameof(SystemProfile.OsBuild), 26100);
        Set(nameof(SystemProfile.OsEdition), "Professional");
        Set(nameof(SystemProfile.CpuName), cpu);
        Set(nameof(SystemProfile.CpuCores), cores);
        Set(nameof(SystemProfile.HasBattery), battery);
        return sp;
    }

    public static TheoryData<string, string, int, bool> Hardware => new()
    {
        { "desktop", "13th Gen Intel(R) Core(TM) i7-13700K", 16, false },
        { "dual-ccd-x3d", "AMD Ryzen 9 7950X3D 16-Core Processor", 16, false },
        { "laptop", "13th Gen Intel(R) Core(TM) i7-1360P", 12, true },
    };

    [Theory]
    [MemberData(nameof(Hardware))]
    public void Profiles_never_preselect_two_members_of_one_exclusive_group_for_the_same_hardware(string name, string cpu, int cores, bool battery)
    {
        var sp = Stub(cpu, cores, battery);
        Assert.Equal(name == "laptop", sp.IsLaptop);
        Assert.Equal(name == "dual-ccd-x3d", TweakCatalog.IsDualCcdX3D(sp));
        foreach (var kind in Enum.GetValues<ProfileKind>())
        {
            var applicable = TweakCatalog.DefaultsFor(kind).Where(t => t.IsApplicable(sp)).ToList();
            foreach (var group in applicable.Where(t => t.ExclusiveGroup is not null).GroupBy(t => t.ExclusiveGroup))
                Assert.True(group.Count() == 1, $"{name}/{kind}: group «{group.Key}» has {string.Join(", ", Ids(group))}");

            // Power plans today (groups are assigned in wave 3): the same rule by id prefix.
            var plans = applicable.Where(t => t.Id.StartsWith("power-plan-", StringComparison.Ordinal)).ToList();
            Assert.True(plans.Count <= 1, $"{name}/{kind}: plans {string.Join(", ", Ids(plans))}");
        }
    }

    [Fact]
    public void Every_dependency_in_the_catalog_exists_and_is_not_the_tweak_itself()
    {
        var ids = TweakCatalog.All.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var t in TweakCatalog.All)
        {
            Assert.All(t.Requires, r => Assert.True(ids.Contains(r) && r != t.Id, $"{t.Id} requires «{r}»"));
            // A dependency is never a member of the same exclusive group (it would uncheck itself).
            Assert.All(t.Requires, r => Assert.False(t.SharesGroupWith(TweakCatalog.Find(r)!), $"{t.Id} requires its group peer «{r}»"));
        }
    }

    [Fact]
    public void Catalog_dependencies_are_acyclic()
    {
        // The Optimize page follows the Requires chain to decide whether a dependent row is pending; a cycle would make
        // that walk endless (the view model guards it, but the catalog must never need the guard).
        Assert.Empty(TweakEngine.RequiresCycles(TweakCatalog.All));
    }

    [Fact]
    public void Catalog_applies_plan_switches_before_every_plan_scoped_power_setting()
    {
        // PowerSettingTweak items write to the ACTIVE plan: a plan switch later in the batch would leave them on the old plan.
        var ordered = TweakEngine.OrderForApply(TweakCatalog.All);
        var lastPlan = ordered.FindLastIndex(t => t.Id.StartsWith("power-plan-", StringComparison.Ordinal) || t.ExclusiveGroup == TweakGroups.PowerPlan);
        var firstSetting = ordered.FindIndex(t => t.Details.Contains(TweakCatalog.PowerSettingUndoNote, StringComparison.Ordinal));
        Assert.True(lastPlan >= 0 && firstSetting >= 0, "the catalog has plan switches and plan-scoped settings");
        Assert.True(lastPlan < firstSetting, $"plan switch at {lastPlan} ({ordered[lastPlan].Id}) after setting at {firstSetting} ({ordered[firstSetting].Id})");
    }
}
