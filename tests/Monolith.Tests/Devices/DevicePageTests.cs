using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Devices;
using Monolith.Shell;
using Monolith.Tweaks;

namespace Monolith.Tests.Devices;

/// <summary>The page shell of «Устройства и сеть»: plan rules, model rules, scan, revert rule, focus, catalogs.</summary>
[Collection("Registry")] // DeviceProviders.OverrideForTests, Elevation and UserScope overrides are process-wide
public sealed class DevicePageTests : IDisposable
{
    public void Dispose()
    {
        UserScope.CurrentSidForTests = null;
        DeviceScan.Timeout = TimeSpan.FromSeconds(10);
    }

    private static Task<ApplyOutcome> Noop(ChangeSession s, DeviceValue v, CancellationToken ct) => Task.FromResult(ApplyOutcome.Applied);

    private static DeviceSetting Setting(string id, long current, long recommended, RecommendPolicy policy, bool perUser = false,
        bool countdown = false, bool readOnly = false, bool measure = false) => new()
    {
        Id = id, Title = id, Hint = "x", Control = SettingControl.Slider, Min = 0, Max = 100, Current = DeviceValue.Of(current), PerUser = perUser,
        NeedsCountdown = countdown, Apply = readOnly ? null : Noop,
        Recommended = new Recommendation(DeviceValue.Of(recommended), "почему", policy) { NeedsMeasurement = measure },
    };

    private static DeviceCard Card(string key, DeviceKind kind, int order, params DeviceSetting[] settings) =>
        new() { Key = key, Kind = kind, Title = key, Order = order, Settings = [.. settings] };

    // ───────────────────────────── Plan ─────────────────────────────

    [Fact]
    public void Plan_policies_preselect_include_only_and_findings_are_not_selectable()
    {
        var card = Card("mouse", DeviceKind.Mouse, 10,
            Setting("include", 1, 2, RecommendPolicy.Include),
            Setting("suggest", 1, 2, RecommendPolicy.Suggest),
            Setting("advice", 1, 1, RecommendPolicy.Advice),
            Setting("none", 1, 2, RecommendPolicy.None),
            Setting("equal", 2, 2, RecommendPolicy.Include));

        var plan = DevicePlan.Build([card], interactiveUser: true);

        Assert.True(plan.Single(i => i.Setting.Id == "include") is { Selectable: true, IsSelected: true, Kind: PlanItemKind.Setting });
        Assert.True(plan.Single(i => i.Setting.Id == "suggest") is { Selectable: true, IsSelected: false });
        Assert.True(plan.Single(i => i.Setting.Id == "advice") is { Selectable: false, Kind: PlanItemKind.Advice }); // always, even when equal
        Assert.DoesNotContain(plan, i => i.Setting.Id is "none" or "equal");
        Assert.Equal("1", plan.Single(i => i.Setting.Id == "include").From);
        Assert.Equal("2", plan.Single(i => i.Setting.Id == "include").To);
    }

    [Fact]
    public void Plan_disables_per_user_rows_for_another_account_and_read_only_rows()
    {
        var card = Card("mouse", DeviceKind.Mouse, 10,
            Setting("per-user", 1, 2, RecommendPolicy.Include, perUser: true),
            Setting("read-only", 1, 2, RecommendPolicy.Include, readOnly: true));

        var plan = DevicePlan.Build([card], interactiveUser: false);

        Assert.All(plan, i => Assert.False(i.Selectable));
        Assert.All(plan, i => Assert.False(i.IsSelected));
        Assert.All(plan, i => Assert.NotNull(i.DisabledReason));
        Assert.True(DevicePlan.Build([card], interactiveUser: true).Single(i => i.Setting.Id == "per-user").Selectable);
    }

    [Fact]
    public void Plan_puts_countdown_rows_last_then_measurements_then_findings()
    {
        var card = Card("monitor", DeviceKind.Monitor, 30,
            Setting("finding", 1, 1, RecommendPolicy.Advice),
            Setting("measure", 1, 2, RecommendPolicy.Suggest, measure: true),
            Setting("mode", 1, 2, RecommendPolicy.Include, countdown: true),
            Setting("plain", 1, 2, RecommendPolicy.Include));

        var plan = DevicePlan.Build([card], true);

        Assert.Equal(["plain", "mode", "measure", "finding"], plan.Select(i => i.Setting.Id));
        Assert.Equal(PlanItemKind.Measurement, plan[2].Kind);
        Assert.False(plan[2].Selectable);
        Assert.Equal("С ПОДТВЕРЖДЕНИЕМ", plan[1].TagText);
    }

    [Fact]
    public void Plan_keeps_the_users_checkboxes_and_dedupes_one_tweak()
    {
        var a = Card("display:a", DeviceKind.Monitor, 30, Setting("mode", 1, 2, RecommendPolicy.Include));
        var tweakA = DeviceSetting.ForTweak("vrr-optimization", "VRR", "x");
        var tweakB = DeviceSetting.ForTweak("vrr-optimization", "VRR", "x");
        foreach (var t in new[] { tweakA, tweakB })
        {
            t.Current = DeviceValue.Of(false);
            t.Recommended = new Recommendation(DeviceValue.Of(true), "почему", RecommendPolicy.Include);
        }
        a.Settings.Add(tweakA);
        var b = Card("display:b", DeviceKind.Monitor, 31, tweakB);

        var plan = DevicePlan.Build([a, b], true, new Dictionary<string, bool> { ["display:a|mode"] = false });

        Assert.False(plan.Single(i => i.Setting.Id == "mode").IsSelected);
        Assert.Single(plan, i => i.TweakId == "vrr-optimization");
        Assert.Equal(PlanItemKind.OptimizeTweak, plan.Single(i => i.TweakId is not null).Kind);
    }

    // ───────────────────────────── Model ─────────────────────────────

    [Fact]
    public void DeviceValue_equality_and_dual_values()
    {
        Assert.Equal(DeviceValue.Of(10), DeviceValue.Of(10));
        Assert.NotEqual(DeviceValue.Of(10), DeviceValue.Of("10"));
        Assert.Equal(DeviceValue.Of(true), DeviceValue.Of(1));
        var dual = DeviceValue.Dual(DeviceValue.Of("2560x1440"), DeviceValue.Of(240));
        Assert.Equal("2560x1440@240", dual.Text);
        Assert.Equal(("2560x1440", "240"), dual.SplitDual());
        Assert.Null(DeviceValue.Of(5).SplitDual());
    }

    [Fact]
    public void Toggle_uses_the_settings_own_on_and_off()
    {
        var s = new DeviceSetting { Id = "wheel-routing", Title = "x", Control = SettingControl.Toggle, On = DeviceValue.Of(2), Off = DeviceValue.Of(0), Current = DeviceValue.Of(0) };
        s.ToggleValue = true;
        Assert.Equal(DeviceValue.Of(2), s.Staged);
        Assert.True(s.IsDirty);
        Assert.Equal("вкл", s.FormatValue(DeviceValue.Of(2)));
    }

    [Fact]
    public void Dual_choice_resets_a_refresh_the_new_resolution_does_not_offer()
    {
        var s = new DeviceSetting
        {
            Id = "display-mode", Title = "Разрешение и частота", Control = SettingControl.DualChoice,
            Options = [new(DeviceValue.Of("2560x1440"), "2560×1440"), new(DeviceValue.Of("1920x1080"), "1920×1080")],
            SecondaryOptions = p => p.Text == "2560x1440" ? [new(DeviceValue.Of(240), "240 Гц"), new(DeviceValue.Of(144), "144 Гц")] : [new(DeviceValue.Of(60), "60 Гц")],
            Current = DeviceValue.Of("2560x1440@144"),
        };
        Assert.Equal("144 Гц", s.SelectedSecondary!.Label);
        s.SelectedPrimary = s.Options[1];
        Assert.Equal("1920x1080@60", s.Staged!.Value.Text);
        s.SelectedPrimary = s.Options[0];
        Assert.Equal("2560x1440@240", s.Staged!.Value.Text); // 60 is not offered at 1440p: the best one
        Assert.Equal(DeviceValue.Of(240), s.StagedSecondary);
    }

    [Fact]
    public void Slider_stages_snapped_values_and_the_special_option()
    {
        var s = new DeviceSetting
        {
            Id = "wheel-lines", Title = "x", Control = SettingControl.Slider, Min = 1, Max = 100, Current = DeviceValue.Of(3),
            SpecialOption = new(DeviceValue.Of(SpiCatalog.PageScroll), "экран целиком"),
        };
        s.SliderValue = 7.4;
        Assert.Equal(DeviceValue.Of(7), s.Staged);
        s.SpecialSelected = true;
        Assert.Equal(DeviceValue.Of(-1), s.Staged);
        Assert.Equal("экран целиком", s.SliderText);
        s.SpecialSelected = false;
        Assert.Equal(DeviceValue.Of(3), s.Staged);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void Custom_equality_decides_dirty_and_plan_membership()
    {
        var s = Setting("mic", 80, 81, RecommendPolicy.Suggest);
        s = new DeviceSetting
        {
            Id = "mic", Title = "mic", Hint = "x", Control = SettingControl.Slider, Current = DeviceValue.Of(80), Apply = Noop,
            Same = (a, b) => Math.Abs(a.Number - b.Number) <= 1,
            Recommended = new Recommendation(DeviceValue.Of(81), "почему", RecommendPolicy.Suggest),
        };
        Assert.Empty(DevicePlan.Build([Card("mic", DeviceKind.AudioInput, 60, s)], true));
        s.Staged = DeviceValue.Of(81);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void Every_changeable_setting_explains_itself_briefly()
    {
        Assert.Null(DeviceScan.HintProblem(new DeviceSetting { Id = "info", Title = "x" }));
        Assert.NotNull(DeviceScan.HintProblem(new DeviceSetting { Id = "a", Title = "x", Apply = Noop }));
        Assert.NotNull(DeviceScan.HintProblem(new DeviceSetting { Id = "a", Title = "x", Apply = Noop, Hint = new string('я', 121) }));
        Assert.Null(DeviceScan.HintProblem(new DeviceSetting { Id = "a", Title = "x", Apply = Noop, Hint = "Колёсико прокручивает окно под курсором." }));
    }

    // ───────────────────────────── Links ─────────────────────────────

    [Theory]
    [InlineData(DeviceLinkKind.Settings, "ms-settings:mousetouchpad", true)]
    [InlineData(DeviceLinkKind.Settings, "https://example.com", false)]
    [InlineData(DeviceLinkKind.Settings, "ms-settings:display & calc.exe", false)]
    [InlineData(DeviceLinkKind.Settings, "ms-settings:", false)]
    [InlineData(DeviceLinkKind.SoundPanel, "3", true)]
    [InlineData(DeviceLinkKind.SoundPanel, "4", false)]
    [InlineData(DeviceLinkKind.SoundPanel, "1 & x", false)]
    [InlineData(DeviceLinkKind.FocusTweak, "mouse-acceleration-off", true)]
    [InlineData(DeviceLinkKind.FocusTweak, "../x", false)]
    [InlineData(DeviceLinkKind.Navigate, "drivers", true)]
    [InlineData(DeviceLinkKind.Navigate, "C:\\x", false)]
    [InlineData(DeviceLinkKind.Tool, "mouse", true)]
    [InlineData(DeviceLinkKind.VendorApp, "", false)]
    public void Links_are_validated(DeviceLinkKind kind, string target, bool ok) =>
        Assert.Equal(ok, DeviceApply.ValidateLink(new DeviceLink(kind, "x", target)) is null);

    // ───────────────────────────── Scan ─────────────────────────────

    private sealed class Provider(string name, Func<IReadOnlyList<DeviceCard>> cards, TimeSpan delay = default, bool fail = false) : IDeviceProvider
    {
        public string Name => name;
        public async Task<IReadOnlyList<DeviceCard>> DetectAsync(DeviceScanContext ctx, CancellationToken ct)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, CancellationToken.None);
            if (fail) throw new InvalidOperationException("нет устройства");
            return cards();
        }
    }

    private sealed class Enricher(Action<List<DeviceCard>> body) : IDeviceEnricher
    {
        public string Name => "производители";
        public Task EnrichAsync(List<DeviceCard> cards, DeviceScanContext ctx, CancellationToken ct)
        {
            body(cards);
            return Task.CompletedTask;
        }
    }

    private static DeviceScanContext Ctx() => new() { Profile = new SystemProfile(), Results = new DeviceResultStore(), InteractiveUser = true };

    [Fact]
    public async Task Scan_skips_slow_and_failing_providers_runs_enrichers_after_and_sorts()
    {
        DeviceScan.Timeout = TimeSpan.FromMilliseconds(300);
        var seenByEnricher = 0;
        using var _ = DeviceProviders.OverrideForTests(
        [
            new Provider("мониторы", () => [Card("display:a", DeviceKind.Monitor, 30)]),
            new Provider("мышь", () => [Card("mouse", DeviceKind.Mouse, 10)]),
            new Provider("звук", () => [Card("audio:x", DeviceKind.AudioOutput, 50)], delay: TimeSpan.FromSeconds(3)),
            new Provider("сеть", () => [], fail: true),
        ], [new Enricher(cards => seenByEnricher = cards.Count)]);

        var result = await DeviceScan.RunAsync(Ctx());

        Assert.Equal(["mouse", "display:a"], result.Cards.Select(c => c.Key));
        Assert.Equal(2, seenByEnricher);
        Assert.Equal(["звук", "сеть"], result.Failed.OrderBy(x => x));
    }

    [Fact]
    public async Task Tweak_rows_take_the_tweaks_state_and_policy()
    {
        var safe = TweakCatalog.All.First(t => t.AppliesTo is null && t.Risk == TweakRisk.Safe);
        var advanced = TweakCatalog.All.First(t => t.AppliesTo is null && t.Risk == TweakRisk.Advanced);
        var card = Card("net", DeviceKind.Network, 70,
            DeviceSetting.ForTweak(safe.Id, "a", "x"), DeviceSetting.ForTweak(advanced.Id, "b", "x"), DeviceSetting.ForTweak("no-such-tweak", "c", "x"));
        using var _ = DeviceProviders.OverrideForTests([new Provider("сеть", () => [card])]);

        await DeviceScan.RunAsync(Ctx());

        Assert.Equal(RecommendPolicy.Include, card.Settings[0].Recommended!.Policy);
        Assert.Equal(DeviceLinkKind.FocusTweak, card.Settings[0].Recommended!.Link!.Kind);
        Assert.Equal(RecommendPolicy.None, card.Settings[1].Recommended!.Policy); // advanced tweaks are never planned
        Assert.NotNull(card.Settings[2].DisabledReason);
        Assert.True(card.Settings.All(s => s.IsReadOnly)); // never a second toggle of an Optimize tweak
    }

    // ───────────────────────────── Revert rule ─────────────────────────────

    [Fact]
    public void Revert_needs_admin_rights_and_the_pages_own_top_layer()
    {
        UserScope.CurrentSidForTests = () => "S-1-5-21-1-2-3-1001";
        var mine = new ChangeRecord { Source = DeviceTargets.Source, Kind = ChangeKind.UserParameter, Target = "spi:mouse-speed", UserSid = "S-1-5-21-1-2-3-1001" };
        var other = new ChangeRecord { Source = DeviceTargets.Source, Kind = ChangeKind.UserParameter, Target = "spi:mouse-speed", UserSid = "S-1-5-21-1-2-3-1002" };
        var tweak = new ChangeRecord { Source = "tweak:x", Kind = ChangeKind.Registry, Target = "spi:mouse-speed" };

        Assert.Same(mine, DeviceApply.RevertTarget([mine], elevated: true));
        Assert.Null(DeviceApply.RevertTarget([mine], elevated: false)); // read-only mode never reverts
        Assert.Null(DeviceApply.RevertTarget([mine, other], elevated: true));
        Assert.Null(DeviceApply.RevertTarget([mine, tweak], elevated: true));
        Assert.Null(DeviceApply.RevertTarget([], elevated: true));
    }

    // ───────────────────────────── Page: focus, merge ─────────────────────────────

    private static DevicesViewModel PageWith(params DeviceCard[] cards)
    {
        var vm = new DevicesViewModel();
        vm.Merge(cards);
        vm.RebuildPlan();
        return vm;
    }

    [Fact]
    public void Focus_kind_monitor_resolves_to_its_plan_row_or_its_mode_row()
    {
        var mode = Setting("display-mode", 144, 240, RecommendPolicy.Include, countdown: true);
        var vm = PageWith(Card("mouse", DeviceKind.Mouse, 10), Card("display:a", DeviceKind.Monitor, 30), Card("display:b", DeviceKind.Monitor, 31, mode));

        Assert.Same(mode, Assert.IsType<PlanItem>(vm.Resolve("kind:monitor", autoTab: true)).Setting);
        Assert.Same(mode, vm.Resolve("kind:monitor", autoTab: false)); // the card that has a plan row, at that row
        Assert.Equal("mouse", Assert.IsType<DeviceCard>(vm.Resolve("kind:mouse", autoTab: false)).Key);
        Assert.Null(vm.Resolve("kind:keyboard", autoTab: true));
    }

    [Fact]
    public void Focus_by_card_key_setting_journal_target_and_tool()
    {
        var speed = new DeviceSetting { Id = "mouse-speed", Title = "x", JournalTarget = DeviceTargets.Spi("mouse-speed") };
        var tool = new DeviceTool { Id = "dpi", Title = "Проверка DPI", Run = (_, _) => Task.CompletedTask };
        var mouse = Card("mouse", DeviceKind.Mouse, 10, speed);
        mouse.Tools.Add(tool);
        var vm = PageWith(mouse);

        Assert.Same(mouse, vm.Resolve("mouse", false));
        Assert.Same(speed, vm.Resolve("mouse-speed", false));
        Assert.Same(speed, vm.Resolve("spi:mouse-speed", false));
        Assert.Same(tool, vm.Resolve("tool:dpi", false));
        Assert.Same(mouse, vm.Resolve("spi:wheel-lines", false)); // no such row: the device
    }

    [Fact]
    public void Tab_focus_ids_switch_tabs()
    {
        var vm = PageWith(Card("mouse", DeviceKind.Mouse, 10));
        vm.ApplyFocus("tab:manual");
        Assert.True(vm.IsManualMode);
        vm.ApplyFocus("tab:auto");
        Assert.True(vm.IsAutoMode);
    }

    [Fact]
    public void Rescan_keeps_staged_values_and_card_instances()
    {
        var speed = Setting("mouse-speed", 14, 10, RecommendPolicy.Suggest);
        var vm = PageWith(Card("mouse", DeviceKind.Mouse, 10, speed));
        speed.Staged = DeviceValue.Of(12);
        vm.OptionalItems.Single().IsSelected = true;

        var fresh = Setting("mouse-speed", 13, 10, RecommendPolicy.Suggest);
        vm.Merge([Card("mouse", DeviceKind.Mouse, 10, fresh)]);
        vm.RebuildPlan();

        var live = vm.Cards.Single().Settings.Single();
        Assert.Same(speed, live);                       // same instance: the view keeps its place
        Assert.Equal(DeviceValue.Of(13), live.Current);  // new live value
        Assert.Equal(DeviceValue.Of(12), live.Staged);   // the user's unapplied value stays
        Assert.True(vm.OptionalItems.Single().IsSelected);
    }

    // ───────────────────────────── Catalogs ─────────────────────────────

    [Fact]
    public void SpiCatalog_has_eleven_consistent_entries()
    {
        Assert.Equal(11, SpiCatalog.All.Count);
        Assert.Equal(11, SpiCatalog.All.Select(e => e.Id).Distinct().Count());
        foreach (var e in SpiCatalog.All)
        {
            Assert.True(e.ValidMin <= e.UiMin && e.UiMax <= e.ValidMax, e.Id);
            if (e.Persisted is { } p) Assert.StartsWith(@"Control Panel\", p.Path);
            Assert.False(string.IsNullOrWhiteSpace(e.Title));
        }
        Assert.Null(SpiCatalog.Find("mouse-sonar")!.Persisted);
        Assert.Null(SpiCatalog.Find("mouse-vanish")!.Persisted);
        Assert.False(SpiCatalog.Find("wheel-routing")!.IsAllowedTarget(1));
        Assert.True(SpiCatalog.Find("wheel-lines")!.IsAllowedTarget(SpiCatalog.PageScroll));
    }

    [Fact]
    public void Device_settings_are_in_the_command_palette()
    {
        var index = CommandPaletteIndex.Build([], [], [], [], [], null, CommandPaletteIndex.DeviceEntries);
        Assert.Contains(CommandPaletteSearch.Search(index, "скорость указателя"), e => e.PageKey == "devices" && e.FocusId == "spi:mouse-speed");
        Assert.Contains(CommandPaletteSearch.Search(index, "hdr"), e => e.FocusId == "kind:monitor");
        Assert.Contains(CommandPaletteSearch.Search(index, "микрофон"), e => e.FocusId == "kind:audioinput");
        Assert.Contains(CommandPaletteSearch.Search(index, "пинг"), e => e.FocusId == "kind:network");
    }

    [Fact]
    public void Devices_page_sits_between_drivers_and_apps()
    {
        Assert.Equal("devices", new DevicesViewModel().Key);
        Assert.Equal(["auto", "manual"], DevicesViewModel.TabKeys);
    }

    [Fact]
    public void PnpTree_virtual_chain_and_redaction()
    {
        Assert.True(PnpTree.IsVirtualChain([@"HID\VID_046D&PID_C231\1&2", @"LGHUBDevice\VID_046D&PID_C231\1"]));
        Assert.False(PnpTree.IsVirtualChain([@"HID\VID_046D&PID_C54D&MI_01\7&3", @"USB\VID_046D&PID_C54D\5&2bd"]));
        Assert.False(PnpTree.IsVirtualChain([@"BTHLEDevice\{guid}_Dev_VID&02046d\8&1"]));
        Assert.Equal(@"USB\VID_046D&PID_C54D\…", PnpTree.Redact(@"USB\VID_046D&PID_C54D\5&2BD4E1F2&0&3"));
    }

    [Fact]
    public void Device_kinds_group_into_the_manual_tab_sections()
    {
        Assert.Equal("Звук", DeviceKinds.Group(DeviceKind.AudioInput).Title);
        Assert.Equal("Сеть", DeviceKinds.Group(DeviceKind.Network).Title);
        Assert.Equal("Другие устройства", DeviceKinds.Group(DeviceKind.Lighting).Title);
        Assert.True(DeviceKinds.Group(DeviceKind.Network).Order < DeviceKinds.Group(DeviceKind.Peripheral).Order);
        Assert.Equal("kind:audioinput", DeviceKinds.FocusId(DeviceKind.AudioInput));
    }
}
