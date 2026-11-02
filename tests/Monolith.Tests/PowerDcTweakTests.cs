using Monolith.Core;
using Monolith.Tweaks;

namespace Monolith.Tests;

/// <summary>
/// The battery (DC) side of the power catalog and the screen/sleep timeouts (general_tweaks.md §6 sys.timeouts, §11 DC rule;
/// gaming_tweaks.md §2 C). Pure: command shapes, the «only shorten» rule, applicability and profile membership — nothing is
/// applied. The DC items themselves are verified by the owner on a real laptop.
/// </summary>
public sealed class PowerDcTweakTests
{
    private static readonly string[] DcIds = ["power-mode-best-efficiency-dc", "battery-screen-off-3min", "battery-sleep-10min"];
    private static readonly string[] AcTimeoutIds = ["screen-off-ac-10m", "sleep-ac-30m"];

    private static SystemProfile Stub(bool laptop, int build = 26100, string cpu = "13th Gen Intel(R) Core(TM) i7-1360P", int cores = 12)
    {
        var sp = new SystemProfile();
        void Set(string name, object value) => typeof(SystemProfile).GetProperty(name)!.SetValue(sp, value);
        Set(nameof(SystemProfile.OsBuild), build);
        Set(nameof(SystemProfile.OsEdition), "Professional");
        Set(nameof(SystemProfile.CpuName), cpu);
        Set(nameof(SystemProfile.CpuCores), cores);
        Set(nameof(SystemProfile.HasBattery), laptop);
        return sp;
    }

    [Theory]
    [InlineData(0u, 180u, true)]     // «никогда» → write
    [InlineData(900u, 180u, true)]   // longer → write
    [InlineData(120u, 180u, false)]  // already shorter → skip
    [InlineData(180u, 180u, false)]  // already the target → skip
    [InlineData(1800u, 1800u, false)]
    [InlineData(0u, 1800u, true)]
    public void Only_shorten_writes_when_the_timeout_is_off_or_longer(uint current, uint target, bool writes)
    {
        Assert.Equal(writes, TweakCatalog.TimeoutNeedsShortening(current, target));
        // The detection rule is the complement: a shorter (non-zero) value counts as applied, never lengthened.
        var satisfied = TweakCatalog.Satisfied(null, onlyShorten: true, target)!;
        Assert.Equal(!writes, TweakCatalog.PowerSettingState(current, target, satisfied) == TweakState.Applied);
    }

    [Fact]
    public void Only_shorten_composes_with_the_callers_own_rule_and_is_absent_otherwise()
    {
        Func<uint, bool> own = v => v == 7;
        Assert.Same(own, TweakCatalog.Satisfied(own, onlyShorten: false, 180));
        Assert.Null(TweakCatalog.Satisfied(null, onlyShorten: false, 180));
        var both = TweakCatalog.Satisfied(own, onlyShorten: true, 180)!;
        Assert.True(both(7));
        Assert.True(both(60));
        Assert.False(both(0));
        Assert.False(both(900));
    }

    [Theory]
    [InlineData(0u, "никогда")]
    [InlineData(180u, "через 3 мин")]
    [InlineData(600u, "через 10 мин")]
    [InlineData(1800u, "через 30 мин")]
    [InlineData(90u, "через 1 мин 30 с")]
    [InlineData(45u, "через 45 с")]
    public void Timeouts_are_described_in_minutes(uint seconds, string expected) => Assert.Equal(expected, TweakCatalog.DescribeTimeout(seconds));

    [Fact]
    public void Dc_power_setting_command_writes_the_dc_index_and_rolls_itself_back()
    {
        var scheme = PowerSchemes.Balanced; var sub = TweakCatalog.SubVideo; var setting = TweakCatalog.VideoIdle;
        var plain = TweakCatalog.PowerSettingCommandDc(scheme, sub, setting, 180);
        Assert.Contains($"/setdcvalueindex {scheme:D} {sub:D} {setting:D} 180 && ", plain);
        Assert.DoesNotContain("/setacvalueindex", plain);
        Assert.EndsWith("/setactive SCHEME_CURRENT", plain);
        Assert.DoesNotContain("||", plain);

        var forward = TweakCatalog.PowerSettingCommandDc(scheme, sub, setting, 180, rollbackTo: 0);
        var parts = forward.Split(" || ");
        Assert.Equal(2, parts.Length);
        Assert.Contains($"/setdcvalueindex {scheme:D} {sub:D} {setting:D} 180 && ", parts[0]);
        Assert.Contains($"/setdcvalueindex {scheme:D} {sub:D} {setting:D} 0 & ", parts[1]);
        Assert.EndsWith("exit /b 1)", parts[1]);

        // The AC twin is byte-for-byte the same line with the other verb: one code path, two sides.
        Assert.Equal(TweakCatalog.PowerSettingCommand(scheme, sub, setting, 180, rollbackTo: 0),
            forward.Replace("/setdcvalueindex", "/setacvalueindex"));
        Assert.Equal($"powercfg:{scheme:D}:{setting:D}:dc", TweakCatalog.PowercfgTarget(dc: true, scheme, setting));
        Assert.Equal($"powercfg:{scheme:D}:{setting:D}:ac", TweakCatalog.PowercfgTarget(dc: false, scheme, setting));
    }

    [Fact]
    public void Dc_power_mode_script_targets_the_dc_setter_and_refuses_undocumented_modes()
    {
        var script = TweakCatalog.PowerModeScriptDc(TweakCatalog.PowerModeBestEfficiency);
        Assert.Contains("EntryPoint=\"PowerSetUserConfiguredDCPowerMode\"", script);
        Assert.Contains($"$g=[Guid]'{TweakCatalog.PowerModeBestEfficiency:D}';", script);
        Assert.Contains("[Monolith.PowerModeDc]::SetDc([ref]$g)", script);
        Assert.DoesNotContain("PowerSetUserConfiguredACPowerMode", script);
        Assert.Throws<ArgumentException>(() => TweakCatalog.PowerModeScriptDc(Guid.NewGuid()));
        Assert.Equal(script, TweakCatalog.PowerModeScript(TweakCatalog.PowerModeBestEfficiency, dc: true));
        Assert.Equal(TweakCatalog.PowerModeScript(TweakCatalog.PowerModeBestEfficiency), TweakCatalog.PowerModeScript(TweakCatalog.PowerModeBestEfficiency, dc: false));
    }

    [Fact]
    public async Task Dc_power_mode_scripts_parse_cleanly()
    {
        // Windows only. Both DC scripts the item can issue (apply = best efficiency, undo = the recorded mode, e.g. balanced),
        // parsed exactly as powershell.exe receives them (wrapper included) — mirrors Generated_powershell_scripts_parse_cleanly.
        string[] scripts =
        [
            TweakCatalog.PowerModeScriptDc(TweakCatalog.PowerModeBestEfficiency),
            TweakCatalog.PowerModeScriptDc(TweakCatalog.PowerModeBalanced),
            TweakCatalog.PowerModeScriptDc(TweakCatalog.PowerModeBestPerformance),
        ];
        foreach (var script in scripts)
        {
            Assert.True(TweakCatalog.PowerShellArgs(script).Length < 8000);
            var encoded = TweakCatalog.PowerShellArgs(script).Split(' ').Last();
            var checker = $"$s=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{encoded}'));$e=$null;$tk=$null;" +
                          "[void][System.Management.Automation.Language.Parser]::ParseInput($s,[ref]$tk,[ref]$e);" +
                          "if($e.Count -gt 0){Write-Output ($e|Out-String)};Write-Output ('PARSE-ERRORS=' + $e.Count)";
            var r = await ProcessRunner.RunPowerShellAsync(checker, new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromSeconds(60) });
            Assert.True(r.Success, r.Combined);
            Assert.Contains("PARSE-ERRORS=0", r.StdOut);
        }
    }

    [Fact]
    public async Task Dc_power_mode_interop_type_compiles()
    {
        // Windows only. Only declares the P/Invoke type (Add-Type compiles the MemberDefinition); the power mode is not changed.
        var r = await ProcessRunner.RunPowerShellAsync(TweakCatalog.PowerModeTypeDefinitionDc + "Write-Output ('PMDC-TYPE=' + [bool]('Monolith.PowerModeDc' -as [type]))",
            new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromSeconds(90) });
        Assert.True(r.Success, r.Combined);
        Assert.Contains("PMDC-TYPE=True", r.StdOut);
    }

    [Fact]
    public void Dc_and_timeout_items_exist_as_optional_power_items_outside_every_profile()
    {
        foreach (var id in DcIds.Concat(AcTimeoutIds))
        {
            var t = TweakCatalog.Find(id);
            Assert.NotNull(t);
            Assert.Equal(TweakCategory.Power, t!.Category);
            Assert.Equal(TweakRisk.Optional, t.Risk);
            // Wave 3: only the Efficiency scenario may pre-check the DC items (laptop battery); never another scenario.
            Assert.True(t.DefaultIn is ProfileSet.None or ProfileSet.Efficiency, $"{id}: {t.DefaultIn}");
            foreach (var kind in Enum.GetValues<ProfileKind>().Where(k => k != ProfileKind.Efficiency))
                Assert.DoesNotContain(TweakCatalog.DefaultsFor(kind), d => d.Id == id);
        }
        Assert.Equal(TweakGroups.PowerModeDc, TweakCatalog.Find("power-mode-best-efficiency-dc")!.ExclusiveGroup);
    }

    [Fact]
    public void Dc_items_are_for_laptops_and_name_the_desktop_counterpart()
    {
        var laptop = Stub(laptop: true);
        var desktop = Stub(laptop: false, cpu: "13th Gen Intel(R) Core(TM) i7-13700K", cores: 16);
        foreach (var id in DcIds)
        {
            var t = TweakCatalog.Find(id)!;
            Assert.True(t.IsApplicable(laptop), id);
            Assert.False(t.IsApplicable(desktop), id);
            // The owner's rule: every battery item says what the desktop equivalent is.
            Assert.Contains("настольн", t.NotApplicableReason!, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("от сети", TweakCatalog.Find("power-mode-best-efficiency-dc")!.NotApplicableReason!);
        Assert.Contains("10 минут от сети", TweakCatalog.Find("battery-screen-off-3min")!.NotApplicableReason!);
        Assert.Contains("30 минут от сети", TweakCatalog.Find("battery-sleep-10min")!.NotApplicableReason!);

        // The AC timeouts serve every PC, desktops included.
        foreach (var id in AcTimeoutIds)
        {
            var t = TweakCatalog.Find(id)!;
            Assert.Null(t.AppliesTo);
            Assert.True(t.IsApplicable(desktop));
            Assert.True(t.IsApplicable(laptop));
        }
    }

    [Fact]
    public void Dc_power_mode_needs_windows_11_and_is_not_offered_on_dual_ccd_x3d()
    {
        var t = TweakCatalog.Find("power-mode-best-efficiency-dc")!;
        Assert.True(t.IsApplicable(Stub(laptop: true, cpu: "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", cores: 8)));
        Assert.True(t.IsApplicable(Stub(laptop: true, cpu: "AMD Ryzen 9 7945HX with Radeon Graphics", cores: 16)));
        Assert.False(t.IsApplicable(Stub(laptop: true, build: 19045)));
        Assert.False(t.IsApplicable(Stub(laptop: true, cpu: "AMD Ryzen 9 7945HX3D with Radeon Graphics", cores: 16)));
        // A UPS battery is not a laptop (SystemProfile.BatteryFromCaps): the hardware facts decide, not a raw battery count.
        Assert.False(SystemProfile.BatteryFromCaps(present: true, shortTerm: true));
    }

    [Fact]
    public void Battery_items_say_the_numbers_are_monoliths_choice_and_only_shorten()
    {
        foreach (var id in new[] { "battery-screen-off-3min", "battery-sleep-10min", "screen-off-ac-10m", "sleep-ac-30m" })
        {
            var t = TweakCatalog.Find(id)!;
            Assert.Contains("3 или 10 минут", t.Details);
            Assert.Contains("выбор Lycoris", t.Details);
            Assert.Contains("только сокращает", t.Details);
            Assert.Contains(TweakCatalog.PowerSettingUndoNote, t.Details);
        }
        Assert.Contains("«от батареи»", TweakCatalog.Find("battery-sleep-10min")!.Details);
        Assert.Contains("«от сети»", TweakCatalog.Find("sleep-ac-30m")!.Details);
        Assert.Contains("игры на батарее станут медленнее", TweakCatalog.Find("power-mode-best-efficiency-dc")!.Details);
    }

    [Fact]
    public void Timeout_targets_follow_the_research_numbers()
    {
        Assert.Equal(180u, TweakCatalog.BatteryScreenOffSeconds);
        Assert.Equal(600u, TweakCatalog.BatterySleepSeconds);
        Assert.Equal(600u, TweakCatalog.AcScreenOffSeconds);
        Assert.Equal(1800u, TweakCatalog.AcSleepSeconds);
        Assert.Equal(Guid.Parse("7516b95f-f776-4464-8c53-06167f40cc99"), TweakCatalog.SubVideo);
        Assert.Equal(Guid.Parse("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"), TweakCatalog.VideoIdle);
        Assert.Equal(Guid.Parse("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"), TweakCatalog.StandbyIdle);
    }
}
