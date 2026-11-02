using Monolith.Modules.Games;
using Monolith.Tweaks;

namespace Monolith.Tests;

/// <summary>
/// Game Boost power rules (docs/research/gaming_tweaks.md §1, §2 C): the plan switch on desktops, the Windows 11 AC power
/// mode where the plan cannot be switched (laptops, Modern Standby, no High performance plan — desktops included), and the
/// texts shown for every decision. Pure logic; nothing here touches the system.
/// </summary>
public sealed class BoostPolicyTests
{
    private static readonly PowerPlanAction[] All = Enum.GetValues<PowerPlanAction>();

    // ───────────────────────────── Hardware rules ─────────────────────────────

    [Theory]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", 16, true)]
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor", 12, true)]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", 8, false)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", 8, false)]
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor", 12, false)]
    [InlineData("Intel(R) Core(TM) i9-14900K", 24, false)]
    [InlineData("", 16, false)]
    public void Dual_ccd_x3d_detection(string cpu, int cores, bool dual) => Assert.Equal(dual, BoostPolicy.IsDualCcdX3D(cpu, cores));

    // ───────────────────────────── Plan ─────────────────────────────

    [Fact]
    public void Power_plan_switches_only_when_every_rule_allows_it()
    {
        Assert.Equal(PowerPlanAction.Switch, BoostPolicy.DecidePowerPlan(true, false, false, false, false, false, true, false));
        Assert.Equal(PowerPlanAction.SkipDisabled, BoostPolicy.DecidePowerPlan(false, false, false, false, false, false, true, false));
        Assert.Equal(PowerPlanAction.SkipDualCcdX3D, BoostPolicy.DecidePowerPlan(true, true, false, false, false, false, true, false));
        Assert.Equal(PowerPlanAction.SkipOnBattery, BoostPolicy.DecidePowerPlan(true, false, true, false, false, false, true, false));
        Assert.Equal(PowerPlanAction.SkipLaptop, BoostPolicy.DecidePowerPlan(true, false, false, true, false, false, true, false));
        Assert.Equal(PowerPlanAction.SkipModernStandby, BoostPolicy.DecidePowerPlan(true, false, false, false, true, false, true, false));
        Assert.Equal(PowerPlanAction.SkipAlreadyFast, BoostPolicy.DecidePowerPlan(true, false, false, false, false, false, true, true));
        Assert.Equal(PowerPlanAction.SkipManagedElsewhere, BoostPolicy.DecidePowerPlan(true, false, false, false, false, true, true, false));
        Assert.Equal(PowerPlanAction.SkipUnavailable, BoostPolicy.DecidePowerPlan(true, false, false, false, false, false, false, false));
    }

    [Fact]
    public void Dual_ccd_x3d_wins_over_every_other_reason()
    {
        // Even on battery and with the setting on, the X3D rule is the one reported — for the plan and for the mode.
        Assert.Equal(PowerPlanAction.SkipDualCcdX3D, BoostPolicy.DecidePowerPlan(true, true, true, true, true, true, false, true));
        Assert.Equal(PowerPlanAction.SkipDualCcdX3D, BoostPolicy.DecidePowerMode(true, true, true, true, true, true, true, true));
    }

    // ───────────────────────────── Windows 11 power mode ─────────────────────────────

    /// <summary>enabled, not X3D, on AC, Windows 11, Balanced-personality plan, energy saver off, mode not Best performance, no other record.</summary>
    private static PowerPlanAction Mode(bool enabled = true, bool dualCcd = false, bool onBattery = false, bool windows11 = true,
        bool balanced = true, bool energySaver = false, bool alreadyBest = false, bool managed = false) =>
        BoostPolicy.DecidePowerMode(enabled, dualCcd, onBattery, windows11, balanced, energySaver, alreadyBest, managed);

    [Fact]
    public void Power_mode_decision_table()
    {
        Assert.Equal(PowerPlanAction.SwitchPowerMode, Mode());
        Assert.Equal(PowerPlanAction.SkipDisabled, Mode(enabled: false));
        Assert.Equal(PowerPlanAction.SkipDualCcdX3D, Mode(dualCcd: true));
        Assert.Equal(PowerPlanAction.SkipOnBattery, Mode(onBattery: true));
        // No such mode on Windows 10 or outside a Balanced-personality plan: "unavailable" — Decide() turns it into the plan's reason.
        Assert.Equal(PowerPlanAction.SkipUnavailable, Mode(windows11: false));
        Assert.Equal(PowerPlanAction.SkipUnavailable, Mode(balanced: false));
        Assert.Equal(PowerPlanAction.SkipEnergySaver, Mode(energySaver: true));
        Assert.Equal(PowerPlanAction.SkipModeAlreadyFast, Mode(alreadyBest: true));
        Assert.Equal(PowerPlanAction.SkipModeManagedElsewhere, Mode(managed: true));
        // Energy saver is reported before "already fast": Windows ignores the mode while it is on.
        Assert.Equal(PowerPlanAction.SkipEnergySaver, Mode(energySaver: true, alreadyBest: true, managed: true));
    }

    [Fact]
    public void Laptop_on_ac_with_balanced_plan_on_windows_11_gets_the_power_mode()
    {
        var plan = BoostPolicy.DecidePowerPlan(true, false, false, laptop: true, false, false, true, false);
        Assert.Equal(PowerPlanAction.SkipLaptop, plan);
        Assert.Equal(PowerPlanAction.SwitchPowerMode, BoostPolicy.Decide(plan, Mode()));
        // On battery the more specific reason is reported by both rules.
        var battery = BoostPolicy.DecidePowerPlan(true, false, true, true, false, false, true, false);
        Assert.Equal(PowerPlanAction.SkipOnBattery, BoostPolicy.Decide(battery, Mode(onBattery: true)));
    }

    [Fact]
    public void Desktops_get_the_power_mode_whenever_the_plan_cannot_be_switched()
    {
        // Modern Standby desktop: only Balanced exists → the mode.
        var standby = BoostPolicy.DecidePowerPlan(true, false, false, laptop: false, modernStandby: true, false, true, false);
        Assert.Equal(PowerPlanAction.SkipModernStandby, standby);
        Assert.Equal(PowerPlanAction.SwitchPowerMode, BoostPolicy.Decide(standby, Mode()));

        // Desktop without the High performance plan → the mode.
        var noPlan = BoostPolicy.DecidePowerPlan(true, false, false, false, false, false, highPerformanceExists: false, false);
        Assert.Equal(PowerPlanAction.SkipUnavailable, noPlan);
        Assert.Equal(PowerPlanAction.SwitchPowerMode, BoostPolicy.Decide(noPlan, Mode()));

        // Desktop with the plan available: the plan switch, unchanged.
        var desktop = BoostPolicy.DecidePowerPlan(true, false, false, false, false, false, true, false);
        Assert.Equal(PowerPlanAction.Switch, BoostPolicy.Decide(desktop, Mode()));
    }

    [Fact]
    public void Plan_skips_that_mean_must_not_never_fall_back_to_the_mode()
    {
        foreach (var plan in new[] { PowerPlanAction.SkipDisabled, PowerPlanAction.SkipDualCcdX3D, PowerPlanAction.SkipOnBattery,
                     PowerPlanAction.SkipAlreadyFast, PowerPlanAction.SkipManagedElsewhere, PowerPlanAction.SkipUnknownHardware, PowerPlanAction.Switch })
        {
            Assert.False(BoostPolicy.PlanFallsBackToMode(plan));
            Assert.Equal(plan, BoostPolicy.Decide(plan, PowerPlanAction.SwitchPowerMode));
        }
    }

    [Fact]
    public void When_the_mode_does_not_exist_the_plans_own_reason_is_reported()
    {
        // Windows 10 laptop → the laptop reason; a Windows 10 Modern Standby desktop → its own reason, never «на ноутбуке».
        Assert.Equal(PowerPlanAction.SkipLaptop, BoostPolicy.Decide(PowerPlanAction.SkipLaptop, Mode(windows11: false)));
        Assert.Equal(PowerPlanAction.SkipModernStandby, BoostPolicy.Decide(PowerPlanAction.SkipModernStandby, Mode(windows11: false)));
        Assert.Equal(PowerPlanAction.SkipUnavailable, BoostPolicy.Decide(PowerPlanAction.SkipUnavailable, Mode(balanced: false)));
        // Energy saver etc. are reported as such — the mode exists, Windows just refuses it now.
        Assert.Equal(PowerPlanAction.SkipEnergySaver, BoostPolicy.Decide(PowerPlanAction.SkipLaptop, Mode(energySaver: true)));
        Assert.Equal(PowerPlanAction.SkipModeAlreadyFast, BoostPolicy.Decide(PowerPlanAction.SkipModernStandby, Mode(alreadyBest: true)));
        Assert.Equal(PowerPlanAction.SkipModeManagedElsewhere, BoostPolicy.Decide(PowerPlanAction.SkipUnavailable, Mode(managed: true)));
    }

    [Fact]
    public void Mode_is_switched_only_from_a_documented_current_mode()
    {
        Assert.Null(BoostPolicy.ModeProblem(TweakCatalog.PowerModeBalanced));
        Assert.Null(BoostPolicy.ModeProblem(TweakCatalog.PowerModeBestEfficiency));
        Assert.Equal(PowerPlanAction.SkipModeUnreadable, BoostPolicy.ModeProblem(null));
        Assert.Equal(PowerPlanAction.SkipModeUnknown, BoostPolicy.ModeProblem(Guid.NewGuid()));

        // Laptop on Windows 11 / Balanced: the mode — but only when the current mode was read and is documented.
        var laptop = BoostPolicy.DecidePowerPlan(true, false, false, laptop: true, false, false, true, false);
        Assert.Equal(PowerPlanAction.SwitchPowerMode, BoostPolicy.Decide(laptop, Mode(), TweakCatalog.PowerModeBalanced));
        Assert.Equal(PowerPlanAction.SkipModeUnreadable, BoostPolicy.Decide(laptop, Mode(), null));
        Assert.Equal(PowerPlanAction.SkipModeUnknown, BoostPolicy.Decide(laptop, Mode(), Guid.NewGuid()));
        // The other skips keep their own, more useful reason; the plan switch never looks at the mode.
        Assert.Equal(PowerPlanAction.SkipEnergySaver, BoostPolicy.Decide(laptop, Mode(energySaver: true), null));
        Assert.Equal(PowerPlanAction.SkipLaptop, BoostPolicy.Decide(laptop, Mode(windows11: false), Guid.NewGuid()));
        var desktop = BoostPolicy.DecidePowerPlan(true, false, false, false, false, false, true, false);
        Assert.Equal(PowerPlanAction.Switch, BoostPolicy.Decide(desktop, Mode(), null));
    }

    // ───────────────────────────── Texts ─────────────────────────────

    [Fact]
    public void Power_descriptions_are_specific()
    {
        Assert.Contains("«Сбалансированная» → «Высокая производительность»", BoostPolicy.Describe(PowerPlanAction.Switch, "Сбалансированная", ""));
        Assert.Contains("Ryzen 9 7950X3D 16-Core", BoostPolicy.Describe(PowerPlanAction.SkipDualCcdX3D, "x", "AMD Ryzen 9 7950X3D 16-Core Processor"));
        Assert.Contains("батареи", BoostPolicy.Describe(PowerPlanAction.SkipOnBattery, "x", ""));
        Assert.Contains("энергосбережение", BoostPolicy.Describe(PowerPlanAction.SkipEnergySaver, "x", ""));
        Assert.Contains("уже «Максимальная производительность»", BoostPolicy.Describe(PowerPlanAction.SkipModeAlreadyFast, "x", ""));
        Assert.Contains("«Оптимизация»", BoostPolicy.Describe(PowerPlanAction.SkipModeManagedElsewhere, "x", ""));
        Assert.Contains("не удалось прочитать текущий режим питания", BoostPolicy.Describe(PowerPlanAction.SkipModeUnreadable, "x", ""));
        Assert.Contains("неизвестный режим питания — Lycoris его не меняет", BoostPolicy.Describe(PowerPlanAction.SkipModeUnknown, "x", ""));
        Assert.Contains("журнал Lycoris не принял бы отмену", BoostPolicy.Describe(PowerPlanAction.SkipModeUntrusted, "x", ""));
    }

    [Fact]
    public void Power_mode_text_names_the_current_mode_and_keeps_the_scheme()
    {
        var laptop = BoostPolicy.Describe(PowerPlanAction.SwitchPowerMode, "Сбалансированная", "", "сбалансированный", laptop: true);
        Assert.StartsWith(BoostPolicy.SwitchModePrefix + "«Сбалансированный» → «Максимальная производительность» (схема остаётся сбалансированной).", laptop);
        Assert.Contains("Возвращается после игры", laptop);
        Assert.Contains("от батареи ничего не меняется", laptop);

        // A desktop (Modern Standby or without the High performance plan) reads nothing about batteries or laptops.
        var desktop = BoostPolicy.Describe(PowerPlanAction.SwitchPowerMode, "Сбалансированная", "", "максимальная эффективность", laptop: false);
        Assert.Contains("«Максимальная эффективность» → «Максимальная производительность»", desktop);
        Assert.DoesNotContain("батаре", desktop);
        Assert.DoesNotContain("ноутбук", desktop);
        Assert.EndsWith("Возвращается после игры.", desktop);
    }

    [Fact]
    public void Laptop_text_is_used_only_for_laptops_and_names_the_way_out()
    {
        // Windows 11, a plan that is not Balanced-personality: the way out is the Balanced scheme in Optimize.
        var laptop = BoostPolicy.Describe(PowerPlanAction.SkipLaptop, "x", "", windows11: true);
        Assert.Contains("на ноутбуке", laptop);
        Assert.Contains("Windows 11", laptop);
        Assert.Contains("«Оптимизации»", laptop);
        // Windows 10: the mode does not exist — the user is not sent to enable a scheme they may already be on.
        var windows10 = BoostPolicy.Describe(PowerPlanAction.SkipLaptop, "x", "", windows11: false);
        Assert.Contains("на ноутбуке", windows10);
        Assert.Contains("есть только в Windows 11", windows10);
        Assert.DoesNotContain("Оптимизаци", windows10);
        Assert.DoesNotContain("сбалансированной", windows10);
        // Desktop-only reasons never say «на ноутбуке».
        foreach (var a in new[] { PowerPlanAction.SkipModernStandby, PowerPlanAction.SkipUnavailable, PowerPlanAction.SkipUnknownHardware,
                     PowerPlanAction.SkipEnergySaver, PowerPlanAction.SkipModeAlreadyFast, PowerPlanAction.SkipModeManagedElsewhere,
                     PowerPlanAction.SkipModeUnreadable, PowerPlanAction.SkipModeUnknown, PowerPlanAction.SkipModeUntrusted })
            Assert.DoesNotContain("на ноутбуке", BoostPolicy.Describe(a, "x", ""));
        Assert.Contains("не удалось определить", BoostPolicy.Describe(PowerPlanAction.SkipUnknownHardware, "x", ""));
        Assert.Contains("Windows 11", BoostPolicy.Describe(PowerPlanAction.SkipModernStandby, "x", ""));
    }

    [Fact]
    public void Every_text_has_a_known_prefix_and_promises_no_fps()
    {
        foreach (var a in All)
        {
            var text = BoostPolicy.Describe(a, "Сбалансированная", "AMD Ryzen 9 7950X3D 16-Core Processor", "сбалансированный", laptop: true);
            Assert.DoesNotContain("FPS", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("кадр", text, StringComparison.OrdinalIgnoreCase);
            if (a == PowerPlanAction.Switch) Assert.StartsWith(BoostPolicy.SwitchPrefix, text);
            else if (a == PowerPlanAction.SwitchPowerMode) Assert.StartsWith(BoostPolicy.SwitchModePrefix, text);
            else if (a == PowerPlanAction.SkipDisabled) Assert.StartsWith("План питания не меняется (выключено)", text);
            else Assert.StartsWith(BoostPolicy.SkipPrefix, text);
            Assert.Equal(BoostPolicy.IsSwitch(a), a is PowerPlanAction.Switch or PowerPlanAction.SwitchPowerMode);
        }
    }

    [Fact]
    public void Boost_plan_lists_the_power_line_first_for_plan_and_mode()
    {
        var game = new GameEntry { Id = "steam:730", Name = "Counter-Strike 2", Platform = GamePlatform.Steam, InstallDir = @"D:\cs2" };
        var plan = new BoostPlan(PowerPlanAction.Switch, "План питания: A → B.",
            [new RunningApp("Discord", "", 3, 0, true), new RunningApp("chrome", "", 1, 0, true)], [], true, false);
        var lines = plan.Describe(game);
        Assert.Equal("План питания: A → B.", lines[0]);
        Assert.Contains(lines, l => l.Contains("Discord (3), chrome") && l.Contains("принудительное завершение — только после отдельного вопроса"));
        Assert.Contains(lines, l => l.Contains("«Высокий»") && l.Contains("никогда «Реального времени»"));
        Assert.DoesNotContain(lines, l => l.Contains("standby"));
        Assert.Contains(lines, l => l.Contains("«Counter-Strike 2»"));

        var modeText = BoostPolicy.Describe(PowerPlanAction.SwitchPowerMode, "Сбалансированная", "", "сбалансированный", laptop: true);
        var mode = new BoostPlan(PowerPlanAction.SwitchPowerMode, modeText, [], [], false, false);
        Assert.Equal(modeText, mode.Describe(game)[0]);
    }

    [Fact]
    public void Manual_session_plan_without_game_mentions_stop_button()
    {
        var plan = new BoostPlan(PowerPlanAction.SkipDisabled, "План питания не меняется (выключено).", [], [], false, true);
        var lines = plan.Describe(null);
        Assert.Contains(lines, l => l.Contains("список закрытия пуст"));
        Assert.Contains(lines, l => l.Contains("standby"));
        Assert.Contains(lines, l => l.Contains("«СТОП»"));
        Assert.DoesNotContain(lines, l => l.Contains("Приоритет"));
    }

    // ───────────────────────────── Readiness hint ─────────────────────────────

    [Fact]
    public void Balanced_plan_hint_says_what_the_session_does_here()
    {
        var desktop = GamingReadiness.BalancedHint(laptop: false, windows11: true, modernStandby: false, highPerformanceExists: true);
        Assert.Contains("переключает схему", desktop);
        Assert.DoesNotContain("ноутбук", desktop);

        var laptop = GamingReadiness.BalancedHint(laptop: true, windows11: true, modernStandby: false, highPerformanceExists: true);
        Assert.Contains("На ноутбуке игровой сеанс Lycoris включает режим питания «Максимальная производительность» (от сети)", laptop);

        // A desktop with Modern Standby or without the plan gets the same lever, described without «ноутбук».
        foreach (var hint in new[]
                 {
                     GamingReadiness.BalancedHint(false, true, modernStandby: true, highPerformanceExists: true),
                     GamingReadiness.BalancedHint(false, true, modernStandby: false, highPerformanceExists: false),
                 })
        {
            Assert.Contains("включает режим питания «Максимальная производительность» (от сети)", hint);
            Assert.DoesNotContain("ноутбук", hint);
        }

        var windows10 = GamingReadiness.BalancedHint(true, windows11: false, false, true);
        Assert.Contains("только в Windows 11", windows10);
        Assert.DoesNotContain("включает режим", windows10);

        // The difference is stated as a typical range, never as a promised gain.
        foreach (var hint in new[] { desktop, laptop, windows10 })
            Assert.Contains("обычно 0–2 % FPS", hint);
    }
}
