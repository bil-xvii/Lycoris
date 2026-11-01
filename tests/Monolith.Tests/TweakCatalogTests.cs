using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Tweaks;

namespace Monolith.Tests;

/// <summary>
/// Catalog integrity and the pure helpers behind it. Nothing here changes the system: detection is read-only,
/// generated scripts are only parsed (or run against HKCU\Software\MONOLITH-Tests), never applied for real.
/// </summary>
public sealed class TweakCatalogTests : IDisposable
{
    private const string TestRoot = @"Software\MONOLITH-Tests";
    private readonly string _testKey = $@"{TestRoot}\{Guid.NewGuid():N}";

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_testKey, false); } catch { }
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(TestRoot);
            if (root is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(TestRoot, false);
        }
        catch { }
    }

    public static readonly TheoryData<string> CanonicalIds = new()
    {
        "game-mode", "hags", "gamedvr-off", "power-plan-performance", "mouse-acceleration-off", "windowed-games-optimization",
        "visual-effects-performance", "ads-suggestions-off", "telemetry-required", "bing-start-search-off", "storage-sense-on",
        "delivery-optimization-local", "trim-on",
    };

    // ───────────────────────────── Catalog integrity ─────────────────────────────

    [Fact]
    public void Catalog_has_a_full_set_of_tweaks()
    {
        Assert.InRange(TweakCatalog.All.Count, 35, 140);
    }

    [Fact]
    public void Ids_are_unique_and_kebab_case()
    {
        var ids = TweakCatalog.All.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches(@"^[a-z0-9]+(-[a-z0-9]+)*$", id));
    }

    [Fact]
    public void Every_tweak_is_explained_in_russian()
    {
        var cyrillic = new Regex("[А-Яа-яЁё]");
        Assert.All(TweakCatalog.All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Title), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Description), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Details), t.Id);
            Assert.Matches(cyrillic, t.Title);
            Assert.Matches(cyrillic, t.Description);
            Assert.True(t.Title.Length <= 60, $"{t.Id}: title is too long for a list row");
        });
    }

    [Theory]
    [MemberData(nameof(CanonicalIds))]
    public void Canonical_ids_exist(string id) => Assert.NotNull(TweakCatalog.Find(id));

    [Fact]
    public void Advanced_tweaks_are_never_part_of_a_profile()
    {
        foreach (var kind in Enum.GetValues<ProfileKind>())
            Assert.DoesNotContain(TweakCatalog.DefaultsFor(kind), t => t.Risk == TweakRisk.Advanced);
        Assert.All(TweakCatalog.All.Where(t => t.Risk == TweakRisk.Advanced), t => Assert.Equal(ProfileSet.None, t.DefaultIn));
    }

    [Fact]
    public void Every_advanced_tweak_has_a_russian_warning()
    {
        var advanced = TweakCatalog.All.Where(t => t.Risk == TweakRisk.Advanced).ToList();
        Assert.NotEmpty(advanced);
        Assert.All(advanced, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Warning), t.Id);
            Assert.Matches("[А-Яа-я]", t.Warning!);
        });
    }

    [Fact]
    public void Conditional_tweaks_explain_why_they_do_not_apply()
    {
        Assert.All(TweakCatalog.All.Where(t => t.AppliesTo is not null), t =>
            Assert.False(string.IsNullOrWhiteSpace(t.NotApplicableReason), t.Id));
    }

    [Theory]
    [InlineData(ProfileKind.Normal)]
    [InlineData(ProfileKind.Gaming)]
    [InlineData(ProfileKind.Recommended)]
    public void Each_profile_has_a_reasonable_set(ProfileKind kind)
    {
        var count = TweakCatalog.DefaultsFor(kind).Count();
        Assert.InRange(count, 8, 40);
    }

    [Fact]
    public void Recommended_is_a_superset_of_normal()
    {
        var recommended = TweakCatalog.DefaultsFor(ProfileKind.Recommended).Select(t => t.Id).ToHashSet();
        Assert.All(TweakCatalog.DefaultsFor(ProfileKind.Normal), t => Assert.Contains(t.Id, recommended));
    }

    [Fact]
    public void Gaming_profile_contains_the_core_gaming_items()
    {
        var gaming = TweakCatalog.DefaultsFor(ProfileKind.Gaming).Select(t => t.Id).ToHashSet();
        foreach (var id in new[] { "game-mode", "hags", "gamedvr-off", "windowed-games-optimization", "mouse-acceleration-off",
                     "power-plan-performance", "power-plan-balanced-x3d" })
            Assert.Contains(id, gaming);
    }

    [Fact]
    public void Delivery_optimization_is_recommended_but_not_a_gaming_default()
    {
        // gaming_tweaks.md: SAFE-OPTIONAL for gaming (default DownloadMode=1 only uploads to the LAN).
        Assert.DoesNotContain(TweakCatalog.DefaultsFor(ProfileKind.Gaming), t => t.Id == "delivery-optimization-local");
        Assert.Contains(TweakCatalog.DefaultsFor(ProfileKind.Recommended), t => t.Id == "delivery-optimization-local");
    }

    [Fact]
    public void Hardware_rules_are_not_judged_when_the_profile_facts_are_missing()
    {
        var unknown = new SystemProfile(); // nothing collected: IsLaptop etc. are only defaults
        Assert.False(unknown.HardwareFactsKnown);
        Assert.False(unknown.IsComplete);
        Assert.All(TweakCatalog.All, t =>
            Assert.Equal(t.AppliesTo is null ? true : null, TweakEngine.Applicability(t, unknown)));
    }

    [Fact]
    public void Profiles_never_preselect_two_different_power_plans_for_the_same_hardware()
    {
        // Balanced (non-X3D) and Balanced-X3D are mutually exclusive by CPU; High performance never shares a profile with Balanced.
        foreach (var kind in Enum.GetValues<ProfileKind>())
        {
            var plans = TweakCatalog.DefaultsFor(kind).Select(t => t.Id).Where(id => id.StartsWith("power-plan-", StringComparison.Ordinal)).ToList();
            Assert.False(plans.Contains("power-plan-performance") && plans.Contains("power-plan-balanced"), kind.ToString());
            Assert.DoesNotContain("power-plan-ultimate", plans);
        }
    }

    [Fact]
    public void Registry_tweaks_have_values_and_stay_out_of_forbidden_areas()
    {
        var registryTweaks = TweakCatalog.All.OfType<RegistryTweak>().ToList();
        Assert.NotEmpty(registryTweaks);
        foreach (var t in registryTweaks)
        {
            Assert.NotEmpty(t.Values);
            foreach (var v in t.Values)
            {
                Assert.False(string.IsNullOrWhiteSpace(v.Path), t.Id);
                Assert.False(IsForbidden(v), $"{t.Id} touches a forbidden area: {v.Hive}\\{v.Path}\\{v.Name}");
            }
        }
    }

    [Fact]
    public void Page_file_is_never_disabled_and_delivery_optimization_never_uses_bypass_mode()
    {
        foreach (var v in TweakCatalog.All.OfType<RegistryTweak>().SelectMany(t => t.Values))
        {
            if (v.Name == "PagingFiles") Assert.True(v.Value is string[] { Length: > 0 } files && files.All(f => f.Length > 0));
            if (v.Name is "DownloadMode" or "DODownloadMode") Assert.NotEqual(100, v.Value);
        }
    }

    [Fact]
    public void Service_tweaks_never_touch_protected_services()
    {
        Assert.All(TweakCatalog.All.OfType<ServiceTweak>().SelectMany(t => t.Services), s =>
        {
            Assert.False(ServiceControl.IsProtected(s.Service), s.Service);
            Assert.NotEqual(System.ServiceProcess.ServiceStartMode.Disabled, s.Mode);
        });
    }

    [Fact]
    public async Task Detection_and_applicability_are_read_only_and_never_throw()
    {
        var sp = await SystemProfile.LoadAsync();
        foreach (var t in TweakCatalog.All)
        {
            var applicable = Record.Exception(() => t.IsApplicable(sp));
            Assert.Null(applicable);
            var detect = Record.Exception(() => t.Detect());
            Assert.Null(detect);
        }
        var states = await TweakEngine.DetectAllAsync(TweakCatalog.All);
        Assert.Equal(TweakCatalog.All.Count, states.Count);
    }

    // ───────────────────────────── Hardware rules ─────────────────────────────

    [Theory]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", 16, true)]
    [InlineData("AMD Ryzen 9 7900X3D 12-Core Processor", 12, true)]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", 16, true)]
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor", 12, true)]
    [InlineData("AMD Ryzen 9 9950X3D2 16-Core Processor", 16, true)]
    [InlineData("AMD Ryzen 9 7945HX3D with Radeon Graphics", 16, true)]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", 8, false)]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", 16, false)] // WMI fallback reported threads: the model wins
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", 8, false)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", 8, false)]
    [InlineData("AMD Ryzen 5 7600X3D 6-Core Processor", 6, false)]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", 16, false)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", 24, false)]
    [InlineData("", 0, false)]
    public void Dual_ccd_x3d_is_detected_from_the_cpu_name(string cpu, int cores, bool expected) =>
        Assert.Equal(expected, TweakCatalog.IsDualCcdX3D(cpu, cores));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", true)]
    [InlineData("NVIDIA GeForce RTX 4090", true)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", true)]
    [InlineData("NVIDIA GeForce RTX 5090 D", true)]
    [InlineData("NVIDIA GeForce RTX 3080", false)]
    [InlineData("NVIDIA RTX A4000", false)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", false)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    public void Rtx_40_and_50_are_recognized(string gpu, bool expected) => Assert.Equal(expected, TweakCatalog.IsRtx40Or50(gpu));

    [Theory]
    [InlineData("Core", true)]
    [InlineData("CoreSingleLanguage", true)]
    [InlineData("CoreCountrySpecific", true)]
    [InlineData("Professional", false)]
    [InlineData("Enterprise", false)]
    [InlineData("Education", false)]
    public void Home_edition_is_recognized(string edition, bool expected) => Assert.Equal(expected, TweakCatalog.IsHomeEdition(edition));

    [Fact]
    public void Gpu_scheduling_query_is_safe()
    {
        var ex = Record.Exception(() => TweakCatalog.QueryGpuScheduling());
        Assert.Null(ex);
    }

    // ───────────────────────────── DirectX settings string ─────────────────────────────

    [Theory]
    [InlineData(null, "SwapEffectUpgradeEnable", "1", "SwapEffectUpgradeEnable=1;")]
    [InlineData("", "SwapEffectUpgradeEnable", "1", "SwapEffectUpgradeEnable=1;")]
    [InlineData("VRROptimizeEnable=0;AutoHDREnable=1;", "VRROptimizeEnable", "1", "VRROptimizeEnable=1;AutoHDREnable=1;")]
    [InlineData("AutoHDREnable=1;", "SwapEffectUpgradeEnable", "1", "AutoHDREnable=1;SwapEffectUpgradeEnable=1;")]
    [InlineData("SwapEffectUpgradeEnable=1;", "SwapEffectUpgradeEnable", null, null)]
    [InlineData("AutoHDREnable=1;SwapEffectUpgradeEnable=1;VRROptimizeEnable=1;", "SwapEffectUpgradeEnable", null, "AutoHDREnable=1;VRROptimizeEnable=1;")]
    [InlineData("swapeffectupgradeenable=0; AutoHDREnable=1", "SwapEffectUpgradeEnable", "1", "SwapEffectUpgradeEnable=1;AutoHDREnable=1;")]
    public void DirectX_tokens_are_merged_without_touching_others(string? before, string key, string? value, string? expected) =>
        Assert.Equal(expected, TweakCatalog.SetDxToken(before, key, value));

    [Theory]
    [InlineData("SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;", "VRROptimizeEnable", "0")]
    [InlineData("SwapEffectUpgradeEnable=1;", "SwapEffectUpgradeEnable", "1")]
    [InlineData("AutoHDREnable=1;", "SwapEffectUpgradeEnable", null)]
    [InlineData(null, "SwapEffectUpgradeEnable", null)]
    public void DirectX_token_is_read(string? settings, string key, string? expected) =>
        Assert.Equal(expected, TweakCatalog.GetDxToken(settings, key));

    [Fact]
    public async Task DirectX_token_script_matches_the_managed_merge()
    {
        string?[][] cases =
        [
            [null, "SwapEffectUpgradeEnable", "1"],
            ["VRROptimizeEnable=0;AutoHDREnable=1;", "VRROptimizeEnable", "1"],
            ["AutoHDREnable=1;SwapEffectUpgradeEnable=1;", "SwapEffectUpgradeEnable", null],
            ["SwapEffectUpgradeEnable=1;", "SwapEffectUpgradeEnable", null],
        ];
        foreach (var c in cases)
        {
            var (before, key, value) = (c[0], c[1]!, c[2]);
            RegistryHelper.Write(RegHive.HKCU, _testKey, "Marker", RegistryValueKind.DWord, 1);
            if (before is null) RegistryHelper.DeleteValue(RegHive.HKCU, _testKey, "Settings");
            else RegistryHelper.Write(RegHive.HKCU, _testKey, "Settings", RegistryValueKind.String, before);

            var script = TweakCatalog.DxTokenScript(key, value, $@"HKCU:\{_testKey}", "Settings");
            var r = await ProcessRunner.RunAsync(TweakCatalog.PowerShellExe, TweakCatalog.PowerShellArgs(script),
                new ProcessRunOptions { Encoding = OutputEncoding.Utf8, LogCommand = false, Timeout = TimeSpan.FromSeconds(60) });

            Assert.True(r.Success, r.Combined);
            Assert.Equal(TweakCatalog.SetDxToken(before, key, value), RegistryHelper.ReadString(RegHive.HKCU, _testKey, "Settings"));
        }
    }

    // ───────────────────────────── Generated commands ─────────────────────────────

    [Fact]
    public async Task Generated_powershell_scripts_parse_cleanly()
    {
        var flagsOff = TweakCatalog.VisualEffectFlags.Select(f => (f.Set, false)).ToList();
        var flagsOn = TweakCatalog.VisualEffectFlags.Select(f => (f.Set, true)).ToList();
        string[] scripts =
        [
            TweakCatalog.SpiScript(flagsOff, 0),
            TweakCatalog.SpiScript(flagsOn, 1),
            TweakCatalog.SpiScript(flagsOff, 0, rollback: flagsOn, rollbackMinAnimate: 1),
            TweakCatalog.DnsScript("ipv4", DnsGuid, ["1.1.1.1", "1.0.0.1"], rollbackTo: []),
            TweakCatalog.DnsScript("ipv6", DnsGuid, [], rollbackTo: ["2606:4700:4700::1111"]),
            TweakCatalog.DnsScript("ipv4", DnsGuid, ["192.168.1.1"]),
            TweakCatalog.DxTokenScript("SwapEffectUpgradeEnable", "1"),
            TweakCatalog.DxTokenScript("VRROptimizeEnable", null),
            TweakCatalog.DxTokenScript("VRROptimizeEnable", null, createdKey: true),
            TweakCatalog.DxTokenScript("VRROptimizeEnable", "0", createdKey: true, createdParent: true),
            TweakCatalog.DnsScript("ipv4", DnsGuid, ["1.1.1.1"], keepRegistration: false),
            TweakCatalog.PowerModeScript(TweakCatalog.PowerModeBestPerformance),
            TweakCatalog.PowerModeScript(TweakCatalog.PowerModeBalanced),
        ];
        foreach (var script in scripts)
        {
            // Parse the full script exactly as powershell.exe receives it (wrapper included).
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
    public void Generated_command_lines_fit_the_windows_limit()
    {
        var flags = TweakCatalog.VisualEffectFlags.Select(f => (f.Set, true)).ToList();
        Assert.True(TweakCatalog.PowerShellArgs(TweakCatalog.SpiScript(flags, 1)).Length < 8000);
        var off = TweakCatalog.VisualEffectFlags.Select(f => (f.Set, false)).ToList();
        Assert.True(TweakCatalog.PowerShellArgs(TweakCatalog.SpiScript(off, 0, rollback: flags, rollbackMinAnimate: 1)).Length < 8000);
        Assert.True(TweakCatalog.PowerShellArgs(TweakCatalog.DnsScript("ipv6", DnsGuid,
            ["2606:4700:4700::1111", "2606:4700:4700::1001"], rollbackTo: ["fd00::1", "fd00::2", "fd00::3"])).Length < 8000);
        Assert.True(TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript("SwapEffectUpgradeEnable", "1")).Length < 8000);
        Assert.True(TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript("SwapEffectUpgradeEnable", null, createdKey: true, createdParent: true)).Length < 8000);
        Assert.True(TweakCatalog.PowerShellArgs(TweakCatalog.PowerModeScript(TweakCatalog.PowerModeBestEfficiency)).Length < 8000);
        // powercfg lines of the AC-only items (with the self-rollback form): cmd.exe's limit is 8191 characters.
        var sub = Guid.Parse("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        foreach (var setting in new[] { TweakCatalog.WakeTimers, TweakCatalog.ProcessorBoostMode, TweakCatalog.UsbSelectiveSuspend })
        {
            Assert.True(TweakCatalog.CmdArgs(TweakCatalog.PowerSettingCommand(PowerSchemes.HighPerformance, sub, setting, 2, rollbackTo: 1)).Length < 8000);
            Assert.True(TweakCatalog.CmdArgs(TweakCatalog.PowerSettingCommand(PowerSchemes.HighPerformance, sub, setting, 1)).Length < 8000);
        }
    }

    [Fact]
    public async Task SystemParametersInfo_interop_type_compiles()
    {
        // Only declares the P/Invoke type; no SystemParametersInfo call is made.
        var r = await ProcessRunner.RunPowerShellAsync(TweakCatalog.SpiTypeDefinition + "Write-Output ('SPI-TYPE=' + [bool]('Monolith.Spi' -as [type]))",
            new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromSeconds(90) });
        Assert.True(r.Success, r.Combined);
        Assert.Contains("SPI-TYPE=True", r.StdOut);
    }

    [Fact]
    public void Visual_effects_can_be_read()
    {
        var state = TweakCatalog.ReadVisualEffects();
        Assert.NotNull(state);
        Assert.Equal(TweakCatalog.VisualEffectFlags.Length, state!.Flags.Length);
    }

    [Fact]
    public void Spi_script_sets_every_flag_and_the_window_animation()
    {
        var script = TweakCatalog.SpiScript(TweakCatalog.VisualEffectFlags.Select(f => (f.Set, false)).ToList(), 0);
        foreach (var f in TweakCatalog.VisualEffectFlags) Assert.Contains($"0x{f.Set:X},0,[IntPtr]0,3", script);
        Assert.Contains("SetAnimation(0x49,8,[int[]]@(8,0),3)", script);
        Assert.DoesNotContain("0x1003,0,[IntPtr]1", script);
    }

    [Fact]
    public void Spi_script_with_rollback_restores_the_original_flags_and_rethrows()
    {
        var off = TweakCatalog.VisualEffectFlags.Select(f => (f.Set, false)).ToList();
        var on = TweakCatalog.VisualEffectFlags.Select(f => (f.Set, true)).ToList();
        var script = TweakCatalog.SpiScript(off, 0, rollback: on, rollbackMinAnimate: 1);
        var catchAt = script.IndexOf("}catch{$e=$_;", StringComparison.Ordinal);
        Assert.True(catchAt > 0);
        var rollback = script[catchAt..];
        foreach (var f in TweakCatalog.VisualEffectFlags) Assert.Contains($"0x{f.Set:X},0,[IntPtr]1,3", rollback);
        Assert.Contains("SetAnimation(0x49,8,[int[]]@(8,1),3)", rollback);
        Assert.Contains("throw $e", rollback);
    }

    [Fact]
    public void Power_setting_command_rolls_itself_back_on_failure()
    {
        var scheme = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        var sub = Guid.Parse("2a737441-1930-4402-8d77-b2bebba308a3");
        var setting = Guid.Parse("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        var plain = TweakCatalog.PowerSettingCommand(scheme, sub, setting, 1);
        Assert.Contains($"/setacvalueindex {scheme:D} {sub:D} {setting:D} 1 && ", plain);
        Assert.EndsWith("/setactive SCHEME_CURRENT", plain);
        Assert.DoesNotContain("||", plain);

        var forward = TweakCatalog.PowerSettingCommand(scheme, sub, setting, 0, rollbackTo: 1);
        var parts = forward.Split(" || ");
        Assert.Equal(2, parts.Length);
        Assert.Contains($"{setting:D} 0 && ", parts[0]);
        Assert.Contains($"{setting:D} 1 & ", parts[1]);
        Assert.EndsWith("exit /b 1)", parts[1]);
    }

    [Fact]
    public void Powershell_arguments_are_encoded_and_fail_fast()
    {
        var args = TweakCatalog.PowerShellArgs("Write-Output 1");
        Assert.StartsWith("-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", args);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(args.Split(' ').Last()));
        Assert.Contains("$ErrorActionPreference='Stop'", decoded);
        Assert.Contains("exit 1", decoded);
        Assert.Contains("Write-Output 1", decoded);
    }

    [Fact]
    public async Task Powershell_failure_exits_with_1_and_a_readable_message()
    {
        var r = await ProcessRunner.RunAsync(TweakCatalog.PowerShellExe, TweakCatalog.PowerShellArgs("throw 'проверка ошибки'"),
            new ProcessRunOptions { Encoding = OutputEncoding.Utf8, LogCommand = false, Timeout = TimeSpan.FromSeconds(60) });
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("проверка ошибки", r.Combined);
    }

    [Fact]
    public void Cmd_arguments_keep_inner_quotes()
    {
        Assert.Equal("/d /s /c \"\"C:\\x.exe\" a && \"C:\\y.exe\" b\"", TweakCatalog.CmdArgs("\"C:\\x.exe\" a && \"C:\\y.exe\" b"));
    }

    // ───────────────────────────── DNS ─────────────────────────────

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("1.1.1.1,1.0.0.1", new[] { "1.1.1.1", "1.0.0.1" })]
    [InlineData("8.8.8.8 8.8.4.4", new[] { "8.8.8.8", "8.8.4.4" })]
    [InlineData(" 2606:4700:4700::1111 , 2606:4700:4700::1001 ", new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" })]
    public void Name_servers_are_parsed(string? value, string[] expected) =>
        Assert.Equal(expected, TweakCatalog.ParseNameServers(value));

    [Fact]
    public void Server_lists_compare_by_order_and_normalized_spelling()
    {
        Assert.True(TweakCatalog.SameServers(["2606:4700:4700:0:0:0:0:1111"], ["2606:4700:4700::1111"]));
        Assert.True(TweakCatalog.SameServers(["1.1.1.1", "1.0.0.1"], ["1.1.1.1", "1.0.0.1"]));
        Assert.False(TweakCatalog.SameServers(["1.0.0.1", "1.1.1.1"], ["1.1.1.1", "1.0.0.1"]));
        Assert.False(TweakCatalog.SameServers([], ["1.1.1.1"]));
        Assert.True(TweakCatalog.SameServers([], []));
    }

    private const string DnsGuid = "{2D0A3F57-7F0E-4C2B-9B1D-6F3C4E5A6B7C}";

    [Fact]
    public void Dns_scripts_resolve_the_adapter_by_guid_at_run_time()
    {
        var dhcp = TweakCatalog.DnsScript("ipv4", DnsGuid, []);
        Assert.Contains("Get-NetAdapter -IncludeHidden", dhcp);
        Assert.Contains("$_.InterfaceGuid -eq '{2d0a3f57-7f0e-4c2b-9b1d-6f3c4e5a6b7c}'", dhcp);
        Assert.Contains("throw 'сетевой адаптер не найден", dhcp);
        Assert.Contains("Invoke-Netsh @('interface','ipv4','set','dnsservers',\"name=$i\",'source=dhcp');", dhcp);
        Assert.Contains("$LASTEXITCODE -ne 0", dhcp);
        Assert.DoesNotContain("catch", dhcp);

        var stat = TweakCatalog.DnsScript("ipv6", DnsGuid, ["2606:4700:4700::1111", "2606:4700:4700::1001"]);
        Assert.Contains("'set','dnsservers',\"name=$i\",'source=static','address=2606:4700:4700::1111','register=primary','validate=no'", stat);
        Assert.Contains("'add','dnsservers',\"name=$i\",'address=2606:4700:4700::1001','index=2','validate=no'", stat);
        Assert.Contains("netsh.exe'", stat);
        Assert.Contains("/flushdns", stat);
    }

    [Fact]
    public void Dns_script_with_rollback_puts_the_previous_list_back_and_rethrows()
    {
        var script = TweakCatalog.DnsScript("ipv4", DnsGuid, ["1.1.1.1"], rollbackTo: []);
        var catchAt = script.IndexOf("}catch{$e=$_;", StringComparison.Ordinal);
        Assert.True(catchAt > 0);
        Assert.Contains("'address=1.1.1.1'", script[..catchAt]);
        Assert.Contains("'source=dhcp'", script[catchAt..]);
        Assert.Contains("throw $e", script[catchAt..]);
    }

    [Fact]
    public void Dns_scripts_reject_anything_that_is_not_an_ip_address_or_a_guid()
    {
        Assert.Throws<InvalidOperationException>(() => TweakCatalog.DnsScript("ipv4", DnsGuid, ["1.1.1.1' ; calc ; '"]));
        Assert.Throws<InvalidOperationException>(() => TweakCatalog.DnsScript("ipv4", "{x'; calc}", ["1.1.1.1"]));
        Assert.Throws<InvalidOperationException>(() => TweakCatalog.DnsScript("ipv4", DnsGuid, ["8.8.8.8"], rollbackTo: ["evil"]));
    }

    // ───────────────────────────── Background apps ─────────────────────────────

    [Fact]
    public void Package_family_matches_installed_full_names()
    {
        string[] installed =
        [
            "Microsoft.BingNews_1.0.2.0_x64__8wekyb3d8bbwe",
            "Clipchamp.Clipchamp_4.7.10220.0_x64__yxz26nhyzhsrt",
            "Microsoft.BingWeatherPlus_1.0.0.0_x64__8wekyb3d8bbwe",
        ];
        Assert.True(TweakCatalog.IsPackageInstalled("Microsoft.BingNews_8wekyb3d8bbwe", installed));
        Assert.True(TweakCatalog.IsPackageInstalled("Clipchamp.Clipchamp_yxz26nhyzhsrt", installed));
        Assert.False(TweakCatalog.IsPackageInstalled("Microsoft.BingWeather_8wekyb3d8bbwe", installed));
        Assert.False(TweakCatalog.IsPackageInstalled("Microsoft.GetHelp_8wekyb3d8bbwe", installed));
    }

    [Fact]
    public void Background_app_list_never_includes_gaming_or_security_packages()
    {
        Assert.DoesNotContain(TweakCatalog.ConsumerBackgroundApps, p =>
            p.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("Gaming", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("SecHealth", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0, 0, TweakState.NotApplicable)]
    [InlineData(0, 3, TweakState.NotApplied)]
    [InlineData(2, 3, TweakState.Partial)]
    [InlineData(3, 3, TweakState.Applied)]
    public void State_is_derived_from_matching_checks(int matches, int total, TweakState expected) =>
        Assert.Equal(expected, TweakCatalog.StateOf(matches, total));

    // ───────────────────────────── New catalog items (research classification is binding) ─────────────────────────────

    public static readonly TheoryData<string, TweakRisk, ProfileSet, TweakCategory> NewTweaks = new()
    {
        { "accessibility-hotkeys-off", TweakRisk.Optional, ProfileSet.Gaming, TweakCategory.Input },
        { "edge-background-off", TweakRisk.Optional, ProfileSet.None, TweakCategory.Performance },
        { "trim-on", TweakRisk.Safe, ProfileSet.Recommended, TweakCategory.System },
        { "drive-optimize-schedule-on", TweakRisk.Optional, ProfileSet.None, TweakCategory.System },
        { "paint-ai-off", TweakRisk.Optional, ProfileSet.None, TweakCategory.Privacy },
        { "notepad-ai-off", TweakRisk.Optional, ProfileSet.None, TweakCategory.Privacy },
        { "start-web-recommendations-off", TweakRisk.Optional, ProfileSet.None, TweakCategory.Interface },
        { "start-phone-panel-off", TweakRisk.Optional, ProfileSet.None, TweakCategory.Interface },
        { "spotlight-third-party-off", TweakRisk.Optional, ProfileSet.None, TweakCategory.Interface },
        { "explorer-home-private", TweakRisk.Optional, ProfileSet.None, TweakCategory.Interface },
        { "news-interests-off", TweakRisk.Safe, ProfileSet.Recommended, TweakCategory.Interface },
        { "power-mode-best-performance", TweakRisk.Optional, ProfileSet.None, TweakCategory.Power },
        { "power-mode-best-efficiency-ac", TweakRisk.Optional, ProfileSet.None, TweakCategory.Power },
        { "wake-timers-important-only", TweakRisk.Optional, ProfileSet.None, TweakCategory.Power },
        { "cpu-boost-off-ac", TweakRisk.Optional, ProfileSet.None, TweakCategory.Power },
        { "tcp-autotuning-normal", TweakRisk.Safe, ProfileSet.Recommended, TweakCategory.Network },
    };

    [Theory]
    [MemberData(nameof(NewTweaks))]
    public void New_tweaks_have_the_researched_risk_profiles_and_category(string id, TweakRisk risk, ProfileSet profiles, TweakCategory category)
    {
        var t = TweakCatalog.Find(id);
        Assert.NotNull(t);
        Assert.Equal(risk, t!.Risk);
        Assert.Equal(profiles, t.DefaultIn);
        Assert.Equal(category, t.Category);
        Assert.Equal(1, TweakCatalog.All.Count(x => x.Id == id));
    }

    public static readonly TheoryData<string, RegHive, string, string, int> NewRegistryValues = new()
    {
        { "edge-background-off", RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0 },
        { "edge-background-off", RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0 },
        { "paint-ai-off", RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1 },
        { "paint-ai-off", RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1 },
        { "paint-ai-off", RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1 },
        { "notepad-ai-off", RegHive.HKLM, @"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1 },
        { "start-web-recommendations-off", RegHive.HKCU, @"Software\Policies\Microsoft\Windows\Explorer", "HideRecommendedPersonalizedSites", 1 },
        { "start-phone-panel-off", RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Start\Companions\Microsoft.YourPhone_8wekyb3d8bbwe", "IsEnabled", 0 },
        { "spotlight-third-party-off", RegHive.HKCU, @"Software\Policies\Microsoft\Windows\CloudContent", "DisableThirdPartySuggestions", 1 },
        { "explorer-home-private", RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowRecent", 0 },
        { "explorer-home-private", RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowCloudFilesInQuickAccess", 0 },
        { "explorer-home-private", RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer", "ShowRecommendations", 0 },
        { "news-interests-off", RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", 0 },
    };

    [Theory]
    [MemberData(nameof(NewRegistryValues))]
    public void New_registry_tweaks_write_exactly_the_documented_dword(string id, RegHive hive, string path, string name, int value)
    {
        // RegistryTweak, so TweakApplyRevertTests applies and reverts every value automatically.
        var t = Assert.IsType<RegistryTweak>(TweakCatalog.Find(id));
        var v = Assert.Single(t.Values, x => x.Name == name);
        Assert.Equal(hive, v.Hive);
        Assert.Equal(path, v.Path);
        Assert.Equal(RegistryValueKind.DWord, v.Kind);
        Assert.Equal(value, v.Value);
    }

    [Fact]
    public void New_registry_tweaks_write_nothing_else()
    {
        var expected = NewRegistryValues.Select(row => ((string)row[0], (string)row[3])).ToHashSet();
        foreach (var id in expected.Select(e => e.Item1).Distinct())
            Assert.All(((RegistryTweak)TweakCatalog.Find(id)!).Values, v => Assert.Contains((id, v.Name), expected));
        // ShowFrequent stays: with recent files, frequent folders and cloud files all off, Explorer Home renders blank.
        Assert.DoesNotContain(((RegistryTweak)TweakCatalog.Find("explorer-home-private")!).Values, v => v.Name == "ShowFrequent");
    }

    [Fact]
    public void Duplicate_proposals_are_merged_into_one_tweak()
    {
        string[] Writers(string name) => TweakCatalog.All.OfType<RegistryTweak>()
            .Where(t => t.Values.Any(v => v.Name == name)).Select(t => t.Id).ToArray();
        Assert.Equal(["edge-background-off"], Writers("StartupBoostEnabled"));
        Assert.Equal(["edge-background-off"], Writers("BackgroundModeEnabled"));
        Assert.Single(TweakCatalog.All, t => t.Id.Contains("accessibility", StringComparison.Ordinal) || t.Id.Contains("sticky", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejected_research_items_are_not_in_the_catalog()
    {
        string[] rejected =
        [
            "PowerThrottlingOff", "DisableUserPresenceQos", "NonBestEffortLimit", "ShellFeedsTaskbarViewMode", "HideAIActionsMenu",
            "DisableWindowsSpotlightFeatures", "DisableWindowsConsumerFeatures", "DisableSoftLanding", "DisableCloudOptimizedContent",
            "RefsDisableDeleteNotification", "DisableGraphRecentItems", "ActiveOverlayAcPowerScheme",
        ];
        Assert.DoesNotContain(TweakCatalog.All.OfType<RegistryTweak>().SelectMany(t => t.Values), v => rejected.Contains(v.Name));
    }

    private static SystemProfile Profile(int build, int ubr = 0, string edition = "Professional", string cpu = "AMD Ryzen 7 9800X3D 8-Core Processor", int cores = 8)
    {
        var sp = new SystemProfile();
        void Set(string name, object value) => typeof(SystemProfile).GetProperty(name)!.SetValue(sp, value);
        Set(nameof(SystemProfile.OsBuild), build);
        Set(nameof(SystemProfile.OsUbr), ubr);
        Set(nameof(SystemProfile.OsEdition), edition);
        Set(nameof(SystemProfile.CpuName), cpu);
        Set(nameof(SystemProfile.CpuCores), cores);
        return sp;
    }

    [Theory]
    [InlineData(19045, true, false)]
    [InlineData(22631, false, true)]
    [InlineData(26100, false, true)]
    public void News_and_interests_and_widgets_never_show_together(int build, bool news, bool widgets)
    {
        var sp = Profile(build);
        Assert.Equal(news, TweakCatalog.Find("news-interests-off")!.IsApplicable(sp));
        Assert.Equal(widgets, TweakCatalog.Find("widgets-off")!.IsApplicable(sp));
    }

    [Fact]
    public void Edition_and_build_gates_follow_the_policy_documentation()
    {
        var spotlight = TweakCatalog.Find("spotlight-third-party-off")!;
        Assert.False(spotlight.IsApplicable(Profile(26100, edition: "Core")));        // Home: the policy is ignored
        Assert.True(spotlight.IsApplicable(Profile(26100, edition: "Professional")));

        var web = TweakCatalog.Find("start-web-recommendations-off")!;
        Assert.False(web.IsApplicable(Profile(22621, ubr: 1927)));                     // before KB5027303
        Assert.True(web.IsApplicable(Profile(22621, ubr: 1928)));
        Assert.True(web.IsApplicable(Profile(26100)));
        Assert.False(web.IsApplicable(Profile(19045, ubr: 6000)));

        foreach (var id in new[] { "paint-ai-off", "notepad-ai-off" })
        {
            Assert.False(TweakCatalog.Find(id)!.IsApplicable(Profile(19045)), id);
            Assert.True(TweakCatalog.Find(id)!.IsApplicable(Profile(22621)), id);
        }

        var mode = TweakCatalog.Find("power-mode-best-performance")!;
        Assert.False(mode.IsApplicable(Profile(19045)));
        Assert.True(mode.IsApplicable(Profile(26100)));
        Assert.False(mode.IsApplicable(Profile(26100, cpu: "AMD Ryzen 9 9950X3D 16-Core Processor", cores: 16)));
    }

    [Fact]
    public void Power_setting_tweaks_say_that_undo_writes_the_value_explicitly()
    {
        foreach (var id in new[] { "cpu-min-state-100", "usb-selective-suspend-off", "pcie-aspm-off", "wifi-power-saving-off",
                     "wake-timers-important-only", "cpu-boost-off-ac" })
            Assert.Contains(TweakCatalog.PowerSettingUndoNote, TweakCatalog.Find(id)!.Details);
    }

    // ───────────────────────────── Power items (AC only): wake timers, CPU boost, efficiency mode, boost-plan follow-up ─────────────────────────────

    [Fact]
    public void Ac_power_items_of_the_power_wave_are_never_pre_selected_and_explain_themselves()
    {
        foreach (var id in new[] { "wake-timers-important-only", "cpu-boost-off-ac", "power-mode-best-efficiency-ac", "usb-selective-suspend-off" })
        {
            var t = TweakCatalog.Find(id);
            Assert.NotNull(t);
            Assert.Equal(ProfileSet.None, t!.DefaultIn);
            foreach (var kind in Enum.GetValues<ProfileKind>())
                Assert.DoesNotContain(TweakCatalog.DefaultsFor(kind), x => x.Id == id);
            Assert.Equal(TweakRisk.Optional, t.Risk);
        }
        // Details are honest about the trade-offs the research insists on.
        Assert.Contains("Пробуждать компьютер", TweakCatalog.Find("wake-timers-important-only")!.Details);
        Assert.Contains("от сети", TweakCatalog.Find("wake-timers-important-only")!.Details);
        Assert.Contains("однопоточной", TweakCatalog.Find("cpu-boost-off-ac")!.Details);
        Assert.Contains("Высокую производительность", TweakCatalog.Find("cpu-boost-off-ac")!.Details);
        Assert.Contains("не аналог", TweakCatalog.Find("power-mode-best-efficiency-ac")!.Details);
        Assert.Contains("Высокая производительность", TweakCatalog.Find("usb-selective-suspend-off")!.Details);
        Assert.Contains("отдельной записью", TweakCatalog.Find("usb-selective-suspend-off")!.Details);
        // The existing power-mode item kept its id and texts after the refactoring into the shared factory.
        var perf = TweakCatalog.Find("power-mode-best-performance")!;
        Assert.Equal("Режим питания «Максимальная производительность»", perf.Title);
        Assert.StartsWith("Штатный режим Windows 11 — «облегчённая схема» поверх сбалансированной", perf.Details);
        Assert.Equal(perf.NotApplicableReason, TweakCatalog.Find("power-mode-best-efficiency-ac")!.NotApplicableReason);
    }

    [Fact]
    public void Hardware_gates_of_the_ac_power_items()
    {
        var boost = TweakCatalog.Find("cpu-boost-off-ac")!;
        Assert.True(boost.IsApplicable(Profile(26100, cpu: "AMD Ryzen 7 9700X 8-Core Processor")));
        Assert.True(boost.IsApplicable(Profile(19045, cpu: "Intel(R) Core(TM) i7-14700K")));
        Assert.False(boost.IsApplicable(Profile(26100)));                                                     // 9800X3D (single CCD) — still X3D
        Assert.False(boost.IsApplicable(Profile(26100, cpu: "AMD Ryzen 9 9950X3D 16-Core Processor", cores: 16)));

        var efficiency = TweakCatalog.Find("power-mode-best-efficiency-ac")!;
        Assert.False(efficiency.IsApplicable(Profile(19045)));
        Assert.True(efficiency.IsApplicable(Profile(26100)));
        Assert.False(efficiency.IsApplicable(Profile(26100, cpu: "AMD Ryzen 9 9950X3D 16-Core Processor", cores: 16)));

        var wake = TweakCatalog.Find("wake-timers-important-only")!;
        Assert.True(wake.IsApplicable(Profile(26100)));
        var modern = Profile(26100);
        typeof(SystemProfile).GetProperty(nameof(SystemProfile.IsModernStandby))!.SetValue(modern, true);
        Assert.False(wake.IsApplicable(modern));
    }

    [Theory] // primitive data only: xUnit on Linux does not discover theories whose InlineData uses app-assembly enums
    [InlineData(0u, true)]   // «отключены» is stricter than «только важные»: never loosened
    [InlineData(1u, false)]  // the stock AC value: the only one the item changes
    [InlineData(2u, true)]
    [InlineData(7u, false)]
    public void Wake_timers_item_never_loosens_disabled_timers(uint current, bool applied)
    {
        var expected = applied ? TweakState.Applied : TweakState.NotApplied;
        Assert.Equal(expected, TweakCatalog.PowerSettingState(current, 2, TweakCatalog.WakeTimersAlreadyStrict));
        // Without the predicate only the target counts (the other PowerSettingTweaks).
        Assert.Equal(current == 2 ? TweakState.Applied : TweakState.NotApplied, TweakCatalog.PowerSettingState(current, 2));
    }

    [Fact]
    public void Extra_boost_plans_follow_the_game_session_decision()
    {
        var high = PowerSchemes.HighPerformance;
        // Desktop, option on, High exists: the session switches there (GameBoostService.StartAsync) → High only. An Ultimate
        // copy is never part of the list: the session never activates it (FindUltimateCopy only recognises it as already active).
        Assert.Equal([high], TweakCatalog.ExtraSchemesForBoost(true, false, false, false, true));
        // Every reason the session would not switch plans → nothing extra is written.
        Assert.Empty(TweakCatalog.ExtraSchemesForBoost(false, false, false, false, true)); // option off
        Assert.Empty(TweakCatalog.ExtraSchemesForBoost(true, true, false, false, true));   // laptop
        Assert.Empty(TweakCatalog.ExtraSchemesForBoost(true, false, true, false, true));   // dual-CCD X3D
        Assert.Empty(TweakCatalog.ExtraSchemesForBoost(true, false, false, true, true));   // Modern Standby
        Assert.Empty(TweakCatalog.ExtraSchemesForBoost(true, false, false, false, false)); // no High performance plan
    }

    [Fact]
    public void Extra_plan_records_use_the_journal_shape_the_undo_check_already_accepts()
    {
        // The same catalog code builds the record for the active plan and for the boost plans; only the scheme differs.
        var cmd = ProcessRunner.Sys("cmd.exe");
        foreach (var scheme in new[] { PowerSchemes.HighPerformance, Guid.NewGuid() })
        {
            foreach (var (sub, setting, target, original) in new[]
                     {
                         (TweakCatalog.SubUsb, TweakCatalog.UsbSelectiveSuspend, 0u, 1u),
                         (Guid.Parse("238c9fa8-0aad-41ed-83f4-97be242c8f20"), TweakCatalog.WakeTimers, 2u, 1u),
                         (Guid.Parse("54533251-82be-4824-96c1-47b60b740d00"), TweakCatalog.ProcessorBoostMode, 0u, 2u),
                     })
            {
                var record = new ChangeRecord
                {
                    Source = "tweak:usb-selective-suspend-off", Title = "t", Kind = ChangeKind.Command,
                    Target = $"cmd:powercfg:{scheme:D}:{setting:D}:ac",
                    RevertFile = cmd, RevertArgs = TweakCatalog.CmdArgs(TweakCatalog.PowerSettingCommand(scheme, sub, setting, original)),
                };
                Assert.Null(JournalTrust.Check(record));
                // The forward (self-rollback) form is never an undo line.
                record.RevertArgs = TweakCatalog.CmdArgs(TweakCatalog.PowerSettingCommand(scheme, sub, setting, target, rollbackTo: original));
                Assert.NotNull(JournalTrust.Check(record));
            }
        }
    }

    [Theory]
    [InlineData(true, false, true)]   // a laptop battery
    [InlineData(true, true, false)]   // a UPS (short-term) battery: not a laptop
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Battery_counts_only_when_it_is_not_a_ups(bool present, bool shortTerm, bool expected) =>
        Assert.Equal(expected, SystemProfile.BatteryFromCaps(present, shortTerm));

    [Fact]
    public void Personality_bytes_compare_with_the_balanced_guid()
    {
        Assert.True(PowerSchemes.PersonalityIsBalanced(PowerSchemes.Balanced.ToByteArray()));
        Assert.False(PowerSchemes.PersonalityIsBalanced(PowerSchemes.HighPerformance.ToByteArray()));
        Assert.False(PowerSchemes.PersonalityIsBalanced(PowerSchemes.PowerSaver.ToByteArray()));
        Assert.False(PowerSchemes.PersonalityIsBalanced(new byte[4]));
        Assert.False(PowerSchemes.PersonalityIsBalanced(null));
        Assert.True(PowerSchemes.IsBalancedPersonality(PowerSchemes.Balanced));   // the plan itself, no API call
        Assert.False(PowerSchemes.IsBalancedPersonality(Guid.Empty));
    }

    [Fact]
    public void Personality_index_resolves_through_the_possible_value_and_falls_back_to_the_documented_mapping()
    {
        // The per-scheme store keeps an index (powercfg /qh: 000 Power saver / 001 High performance / 002 Balanced); the possible
        // value behind it is the personality GUID and decides when Windows returns it.
        Assert.True(PowerSchemes.PersonalityIndexIsBalanced(2, PowerSchemes.Balanced.ToByteArray()));
        Assert.False(PowerSchemes.PersonalityIndexIsBalanced(1, PowerSchemes.HighPerformance.ToByteArray()));
        Assert.False(PowerSchemes.PersonalityIndexIsBalanced(0, PowerSchemes.PowerSaver.ToByteArray()));
        // A readable possible value wins over the index (an OEM store with reordered possible values).
        Assert.True(PowerSchemes.PersonalityIndexIsBalanced(0, PowerSchemes.Balanced.ToByteArray()));
        Assert.False(PowerSchemes.PersonalityIndexIsBalanced(2, PowerSchemes.HighPerformance.ToByteArray()));
        // No possible value (null / wrong size): the documented mapping, 2 = Balanced only.
        Assert.True(PowerSchemes.PersonalityIndexIsBalanced(2, null));
        Assert.True(PowerSchemes.PersonalityIndexIsBalanced(2, new byte[4]));
        Assert.False(PowerSchemes.PersonalityIndexIsBalanced(1, null));
        Assert.False(PowerSchemes.PersonalityIndexIsBalanced(0, null));
        Assert.False(PowerSchemes.PersonalityIndexIsBalanced(7, null));
        Assert.Equal(2u, PowerSchemes.BalancedPersonalityIndex);
    }

    [Fact]
    public void Hybrid_sleep_fact_names_the_state_the_record_keeps()
    {
        Assert.Equal("включён в текущей схеме", TweakCatalog.HybridSleepFact(1, fastSystemS4: true));
        Assert.Equal("выключен в текущей схеме", TweakCatalog.HybridSleepFact(0, fastSystemS4: true));
        Assert.Equal("неизвестно", TweakCatalog.HybridSleepFact(null, fastSystemS4: true));
        Assert.StartsWith("недоступен", TweakCatalog.HybridSleepFact(1, fastSystemS4: false));
        var hibernate = TweakCatalog.Find("hibernation-off")!;
        Assert.Contains("920730", hibernate.Details);
        Assert.Contains("гибридный сон", hibernate.Warning!);
        Assert.Contains("/type reduced", hibernate.Details);
    }

    [Theory]
    [InlineData(0u, "отключён")]
    [InlineData(1u, "включён")]
    [InlineData(2u, "агрессивный")]
    [InlineData(6u, "эффективный агрессивный при гарантии")]
    [InlineData(9u, "9")]
    public void Boost_mode_values_are_named(uint value, string expected) => Assert.Equal(expected, TweakCatalog.DescribeBoostMode(value));

    // ───────────────────────────── Accessibility shortcuts ─────────────────────────────

    [Theory]
    [InlineData("510", 510, 506)]  // Sticky Keys default → shortcut off
    [InlineData("126", 126, 122)]  // Filter Keys
    [InlineData("62", 62, 58)]     // Toggle Keys
    [InlineData("506", 510, 506)]  // already off: unchanged
    [InlineData(" 122 ", 126, 122)]
    public void Only_the_shortcut_bit_is_cleared(string raw, int defaultValue, int expected)
    {
        var flags = TweakCatalog.ChangeableAccessibilityFlags(raw, defaultValue);
        Assert.NotNull(flags);
        Assert.Equal(expected, TweakCatalog.ClearAccessibilityHotkey(flags!.Value));
    }

    [Fact]
    public void Accessibility_flags_are_read_like_windows_and_features_in_use_are_left_alone()
    {
        Assert.Equal(510, TweakCatalog.ChangeableAccessibilityFlags(null, 510));      // absent = Windows default
        Assert.Equal(506, TweakCatalog.ChangeableAccessibilityFlags(506, 510));       // DWORD written by other tools
        Assert.Null(TweakCatalog.ChangeableAccessibilityFlags("511", 510));           // Sticky Keys is ON: user relies on it
        Assert.Null(TweakCatalog.ChangeableAccessibilityFlags("63", 62));
        Assert.Null(TweakCatalog.ChangeableAccessibilityFlags("abc", 510));           // unreadable: never touched
        Assert.Null(TweakCatalog.ChangeableAccessibilityFlags("-5", 510));
        Assert.Null(TweakCatalog.ChangeableAccessibilityFlags(new byte[] { 1 }, 510));
        Assert.Equal(["StickyKeys", "Keyboard Response", "ToggleKeys"],
            TweakCatalog.AccessibilityHotkeyKeys.Select(k => k.Path.Split('\\').Last()).ToArray());
        Assert.All(TweakCatalog.AccessibilityHotkeyKeys, k => Assert.StartsWith(@"Control Panel\Accessibility\", k.Path));
    }

    // ───────────────────────────── TCP auto-tuning repair ─────────────────────────────

    [Fact]
    public void Tcp_autotuning_command_accepts_only_the_five_netsh_levels()
    {
        Assert.Equal(["disabled", "highlyrestricted", "restricted", "normal", "experimental"], TweakCatalog.TcpAutotuningLevels.ToArray());
        Assert.Equal("interface tcp set global autotuninglevel=normal", TweakCatalog.TcpAutotuningArgs("normal"));
        Assert.Equal("interface tcp set global autotuninglevel=disabled", TweakCatalog.TcpAutotuningArgs("disabled"));
        Assert.Throws<ArgumentException>(() => TweakCatalog.TcpAutotuningArgs("normal & calc"));
        Assert.Throws<ArgumentException>(() => TweakCatalog.TcpAutotuningArgs("Normal"));
    }

    [Fact]
    public void Tcp_autotuning_is_repaired_only_when_one_undo_line_restores_every_template()
    {
        TweakCatalog.TcpAutotuningState State(bool policy, params int[] levels) =>
            new(levels.Select((l, i) => ($"T{i}", l)).ToList(), policy);

        Assert.Equal(TweakCatalog.TcpAutotuningPlan.AlreadyNormal, TweakCatalog.PlanTcpAutotuning(State(false, 3, 3, 3, 3, 3)));
        Assert.Equal(TweakCatalog.TcpAutotuningPlan.Repair, TweakCatalog.PlanTcpAutotuning(State(false, 0, 0, 0, 0, 0)));
        Assert.Equal(TweakCatalog.TcpAutotuningPlan.Repair, TweakCatalog.PlanTcpAutotuning(State(false, 4, 4)));
        Assert.Equal(TweakCatalog.TcpAutotuningPlan.Mixed, TweakCatalog.PlanTcpAutotuning(State(false, 0, 3, 3)));
        Assert.Equal(TweakCatalog.TcpAutotuningPlan.ByPolicy, TweakCatalog.PlanTcpAutotuning(State(true, 0, 0)));
        Assert.Equal(TweakCatalog.TcpAutotuningPlan.ByPolicy, TweakCatalog.PlanTcpAutotuning(State(true, 3, 3)));
    }

    // ───────────────────────────── Windows 11 power mode ─────────────────────────────

    [Fact]
    public void Power_mode_script_sets_only_documented_modes()
    {
        foreach (var mode in new[] { TweakCatalog.PowerModeBalanced, TweakCatalog.PowerModeBestEfficiency, TweakCatalog.PowerModeBestPerformance })
        {
            Assert.True(TweakCatalog.IsDocumentedPowerMode(mode));
            var script = TweakCatalog.PowerModeScript(mode);
            Assert.StartsWith(TweakCatalog.PowerModeTypeDefinition, script);
            Assert.Contains($"$g=[Guid]'{mode:D}';", script);
            Assert.Contains("[Monolith.PowerMode]::SetAc([ref]$g)", script);
            Assert.Contains("if($r -ne 0){throw", script);
        }
        Assert.Equal(Guid.Parse("ded574b5-45a0-4f42-8737-46345c09c238"), TweakCatalog.PowerModeBestPerformance);
        Assert.Equal(Guid.Parse("961cc777-2547-4f9d-8174-7d86181b8a7a"), TweakCatalog.PowerModeBestEfficiency);
        Assert.Equal(Guid.Empty, TweakCatalog.PowerModeBalanced);
        Assert.False(TweakCatalog.IsDocumentedPowerMode(Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => TweakCatalog.PowerModeScript(Guid.NewGuid()));
        Assert.Contains("EntryPoint=\"PowerSetUserConfiguredACPowerMode\"", TweakCatalog.PowerModeTypeDefinition);
    }

    [Fact]
    public async Task Power_mode_interop_type_compiles()
    {
        // Only declares the P/Invoke type; the power mode is not changed.
        var r = await ProcessRunner.RunPowerShellAsync(TweakCatalog.PowerModeTypeDefinition + "Write-Output ('PM-TYPE=' + [bool]('Monolith.PowerMode' -as [type]))",
            new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromSeconds(90) });
        Assert.True(r.Success, r.Combined);
        Assert.Contains("PM-TYPE=True", r.StdOut);
    }

    // ───────────────────────────── DirectX key ownership ─────────────────────────────

    [Fact]
    public void DirectX_script_without_ownership_is_unchanged_for_old_journal_records()
    {
        foreach (var value in new[] { null, "0", "1" })
        {
            var plain = TweakCatalog.DxTokenScript("VRROptimizeEnable", value);
            Assert.DoesNotContain("$mk", plain);
            Assert.DoesNotContain("Remove-Item ", plain);
            Assert.Equal(plain, TweakCatalog.DxTokenScript("VRROptimizeEnable", value, createdParent: true)); // parent needs the key
            Assert.StartsWith("$p='HKCU:\\Software\\Microsoft\\DirectX\\UserGpuPreferences';", plain);
        }
    }

    [Fact]
    public void DirectX_undo_removes_only_an_empty_key_it_created()
    {
        var key = TweakCatalog.DxTokenScript("VRROptimizeEnable", null, createdKey: true);
        Assert.StartsWith("$mk=$true;$mp=$false;$p=", key);
        var empty = key.IndexOf("if($t.Count -eq 0){", StringComparison.Ordinal);
        var other = key.IndexOf("else{", empty, StringComparison.Ordinal);
        var cleanup = key.IndexOf("if($mk){", StringComparison.Ordinal);
        Assert.True(empty > 0 && cleanup > empty && cleanup < other, "cleanup must run only when the last token is gone");
        Assert.Contains("$ki.ValueCount -eq 0 -and $ki.SubKeyCount -eq 0", key); // per-app GPU preferences keep the key

        var parent = TweakCatalog.DxTokenScript("VRROptimizeEnable", "0", createdKey: true, createdParent: true);
        Assert.StartsWith("$mk=$true;$mp=$true;$p=", parent);
        Assert.Contains("$kq.ValueCount -eq 0 -and $kq.SubKeyCount -eq 0", parent);
    }

    [Fact]
    public void DirectX_ownership_follows_the_first_token_that_created_the_key()
    {
        ChangeRecord Dx(string token, bool key, bool parent, string source = "tweak:x") => new()
        {
            Source = source, Kind = ChangeKind.Command, Target = "cmd:dx:" + token, RevertFile = TweakCatalog.PowerShellExe,
            RevertArgs = TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript(token, null, createdKey: key, createdParent: parent)),
        };

        Assert.Equal((true, true), TweakCatalog.DxUndoOwnership(keyExists: false, parentExists: false, []));
        Assert.Equal((true, false), TweakCatalog.DxUndoOwnership(keyExists: false, parentExists: true, []));
        Assert.Equal((false, false), TweakCatalog.DxUndoOwnership(keyExists: true, parentExists: true, []));
        // A second token applied after the first one created the key (and its parent) inherits the cleanup duty.
        Assert.Equal((true, true), TweakCatalog.DxUndoOwnership(true, true, [Dx("SwapEffectUpgradeEnable", key: true, parent: true)]));
        Assert.Equal((true, false), TweakCatalog.DxUndoOwnership(true, true, [Dx("SwapEffectUpgradeEnable", key: true, parent: false)]));
        Assert.Equal((false, false), TweakCatalog.DxUndoOwnership(true, true, [Dx("SwapEffectUpgradeEnable", key: false, parent: false)]));
        // Records of other kinds or targets never count.
        var foreign = new ChangeRecord
        {
            Source = "tweak:y", Kind = ChangeKind.Command, Target = "cmd:spi:visual-effects", RevertFile = TweakCatalog.PowerShellExe,
            RevertArgs = TweakCatalog.PowerShellArgs("$mk=$true;$mp=$true;"),
        };
        Assert.Equal((false, false), TweakCatalog.DxUndoOwnership(true, true, [foreign]));
    }

    [Fact]
    public async Task DirectX_undo_scripts_remove_the_created_key_in_any_revert_order()
    {
        var parentPath = $@"{_testKey}\DirectX";
        var keyPath = $@"{parentPath}\UserGpuPreferences";
        var ps = $@"HKCU:\{keyPath}";

        async Task Run(string script)
        {
            var r = await ProcessRunner.RunAsync(TweakCatalog.PowerShellExe, TweakCatalog.PowerShellArgs(script),
                new ProcessRunOptions { Encoding = OutputEncoding.Utf8, LogCommand = false, Timeout = TimeSpan.FromSeconds(60) });
            Assert.True(r.Success, r.Combined);
        }

        foreach (var aFirst in new[] { true, false })
        {
            Assert.False(RegistryHelper.KeyExists(RegHive.HKCU, parentPath));
            await Run(TweakCatalog.DxTokenScript("A", "1", ps, "Settings"));
            await Run(TweakCatalog.DxTokenScript("B", "1", ps, "Settings"));
            var undoA = TweakCatalog.DxTokenScript("A", null, ps, "Settings", createdKey: true, createdParent: true);
            var undoB = TweakCatalog.DxTokenScript("B", null, ps, "Settings", createdKey: true, createdParent: true);
            await Run(aFirst ? undoA : undoB);
            Assert.True(RegistryHelper.KeyExists(RegHive.HKCU, keyPath)); // the other token is still there
            await Run(aFirst ? undoB : undoA);
            Assert.False(RegistryHelper.KeyExists(RegHive.HKCU, keyPath));
            Assert.False(RegistryHelper.KeyExists(RegHive.HKCU, parentPath));
        }

        // A per-app GPU preference added meanwhile keeps the key.
        await Run(TweakCatalog.DxTokenScript("A", "1", ps, "Settings"));
        RegistryHelper.Write(RegHive.HKCU, keyPath, @"C:\Games\game.exe", RegistryValueKind.String, "GpuPreference=2;");
        await Run(TweakCatalog.DxTokenScript("A", null, ps, "Settings", createdKey: true, createdParent: true));
        Assert.Equal("GpuPreference=2;", RegistryHelper.ReadString(RegHive.HKCU, keyPath, @"C:\Games\game.exe"));
        Assert.Null(RegistryHelper.ReadString(RegHive.HKCU, keyPath, "Settings"));
    }

    private const string DxKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string DxParent = @"Software\Microsoft\DirectX";

    /// <summary>A per-game GPU choice of the Games page, as ChangeSession.SetRegistry journals it.</summary>
    internal static ChangeRecord GpuChoice(string? createdRoot, bool keyExisted, string keyPath = DxKey, RegHive hive = RegHive.HKCU,
        string? sid = null) => new()
    {
        Source = "games", Kind = ChangeKind.Registry, Target = $@"reg:{hive}\{keyPath}\D:\Games\X\x.exe", UserSid = sid,
        Registry = new RegistryValueSnapshot
        {
            Hive = hive, Path = keyPath, Name = @"D:\Games\X\x.exe", KeyExisted = keyExisted, ValueExisted = false, CreatedKeyRoot = createdRoot,
        },
    };

    /// <summary>A DirectX-token record whose undo removes the key it created ($mk) and its parent ($mp).</summary>
    internal static ChangeRecord DxToken(bool key, bool parent, string keyPath = DxKey, string? sid = null) => new()
    {
        Source = "tweak:x", Kind = ChangeKind.Command, Target = "cmd:dx:VRROptimizeEnable", UserSid = sid, RevertFile = TweakCatalog.PowerShellExe,
        RevertArgs = TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript("VRROptimizeEnable", null, @"HKCU:\" + keyPath,
            createdKey: key, createdParent: parent)),
    };

    [Fact]
    public void DirectX_ownership_counts_a_gpu_choice_that_created_the_key()
    {
        // The Games page created UserGpuPreferences (or, inherited from a token, the DirectX parent): the token cleans up too.
        Assert.Equal((true, false), TweakCatalog.DxUndoOwnership(true, true, [GpuChoice(DxKey, keyExisted: false)]));
        Assert.Equal((true, false), TweakCatalog.DxUndoOwnership(true, true, [GpuChoice(null, keyExisted: false)])); // own key only
        Assert.Equal((true, false), TweakCatalog.DxUndoOwnership(true, true, [GpuChoice(DxKey, keyExisted: true)])); // inherited root
        Assert.Equal((true, true), TweakCatalog.DxUndoOwnership(true, true, [GpuChoice(DxParent, keyExisted: true)]));
        // The key was there before MONOLITH, the record is closed, or it is not the current account's HKCU: no claim.
        Assert.Equal((false, false), TweakCatalog.DxUndoOwnership(true, true, [GpuChoice(null, keyExisted: true)]));
        var closed = GpuChoice(DxKey, keyExisted: false);
        closed.Reverted = true;
        Assert.Equal((false, false), TweakCatalog.DxUndoOwnership(true, true, [closed]));
        Assert.Equal((false, false), TweakCatalog.DxUndoOwnership(true, true, [GpuChoice(DxKey, false, hive: RegHive.HKLM)]));
        // A key created deeper than UserGpuPreferences says nothing about it.
        Assert.Equal((false, false), TweakCatalog.DxUndoOwnership(true, true, [GpuChoice(DxKey + @"\Sub", false, keyPath: DxKey + @"\Sub")]));
    }

    [Fact]
    public void A_gpu_choice_inherits_the_key_a_directx_token_created()
    {
        Assert.Equal(DxParent, TweakCatalog.DxCreatedKeyRoot(DxToken(key: true, parent: true)));
        Assert.Equal(DxKey, TweakCatalog.DxCreatedKeyRoot(DxToken(key: true, parent: false)));
        Assert.Null(TweakCatalog.DxCreatedKeyRoot(DxToken(key: false, parent: false)));
        Assert.Null(TweakCatalog.DxCreatedKeyRoot(GpuChoice(DxKey, keyExisted: false)));
        var spi = new ChangeRecord
        {
            Source = "tweak:y", Kind = ChangeKind.Command, Target = "cmd:spi:visual-effects", RevertFile = TweakCatalog.PowerShellExe,
            RevertArgs = TweakCatalog.PowerShellArgs("$mk=$true;$mp=$true;"),
        };
        Assert.Null(TweakCatalog.DxCreatedKeyRoot(spi));

        // A GPU choice carrying either root is still a record the journal replays.
        foreach (var root in new[] { DxKey, DxParent })
            Assert.Null(JournalTrust.Check(GpuChoice(root, keyExisted: true)));
    }

    [Fact]
    public async Task DirectX_tokens_and_gpu_choices_remove_the_key_monolith_created_in_any_revert_order()
    {
        var parentPath = $@"{_testKey}\DirectX";
        var keyPath = $@"{parentPath}\UserGpuPreferences";
        const string exe = @"D:\Games\X\x.exe";

        async Task Run(string script)
        {
            var r = await ProcessRunner.RunAsync(TweakCatalog.PowerShellExe, TweakCatalog.PowerShellArgs(script),
                new ProcessRunOptions { Encoding = OutputEncoding.Utf8, LogCommand = false, Timeout = TimeSpan.FromSeconds(60) });
            Assert.True(r.Success, r.Combined);
        }
        bool Exists(string path) => RegistryHelper.KeyExists(RegHive.HKCU, path);
        string Apply() => TweakCatalog.DxTokenScript("VRROptimizeEnable", "1", @"HKCU:\" + keyPath);

        foreach (var tokenFirst in new[] { true, false })
        foreach (var undoTokenFirst in new[] { true, false })
        {
            Assert.False(Exists(parentPath));
            ChangeRecord token;
            RegistryValueSnapshot gpu;
            if (tokenFirst)
            {
                // Neither key exists: the token creates both, and the GPU choice inherits the token's created root
                // (what ChangeJournal.InheritedKeyRoot hands to ChangeSession.SetRegistry).
                var (k, p) = TweakCatalog.DxUndoOwnership(Exists(keyPath), Exists(parentPath), [], keyPath);
                await Run(Apply());
                token = DxToken(k, p, keyPath);
                var snap = RegistryHelper.Snapshot(RegHive.HKCU, keyPath, exe);
                gpu = snap.WithCreatedKeyRoot(RegistryHelper.ShallowerRoot(snap.CreatedKeyRoot, TweakCatalog.DxCreatedKeyRoot(token, keyPath)));
                RegistryHelper.Write(RegHive.HKCU, keyPath, exe, RegistryValueKind.String, "GpuPreference=2;");
            }
            else
            {
                // DirectX exists (as on every PC that ran a game): the GPU choice creates UserGpuPreferences, the token inherits it.
                RegistryHelper.Write(RegHive.HKCU, parentPath, "Keep", RegistryValueKind.String, "1");
                gpu = RegistryHelper.Snapshot(RegHive.HKCU, keyPath, exe);
                RegistryHelper.Write(RegHive.HKCU, keyPath, exe, RegistryValueKind.String, "GpuPreference=2;");
                var (k, p) = TweakCatalog.DxUndoOwnership(Exists(keyPath), Exists(parentPath), [GpuChoice(gpu.CreatedKeyRoot, gpu.KeyExisted, keyPath)], keyPath);
                Assert.Equal((true, false), (k, p));
                await Run(Apply());
                token = DxToken(k, p, keyPath);
            }

            if (undoTokenFirst)
            {
                await Run(TweakCatalog.DecodePowerShellArgs(token.RevertArgs)!);
                Assert.True(Exists(keyPath)); // the game's value keeps it
                RegistryHelper.Restore(gpu);
            }
            else
            {
                RegistryHelper.Restore(gpu);
                Assert.True(Exists(keyPath)); // the token keeps it
                await Run(TweakCatalog.DecodePowerShellArgs(token.RevertArgs)!);
            }

            Assert.False(Exists(keyPath));
            if (tokenFirst) Assert.False(Exists(parentPath));
            else
            {
                Assert.Equal("1", RegistryHelper.ReadString(RegHive.HKCU, parentPath, "Keep")); // existed before: kept
                Registry.CurrentUser.DeleteSubKeyTree(parentPath, false);
            }
        }
    }

    // ───────────────────────────── Power plans during a game session ─────────────────────────────

    [Fact]
    public void Plan_items_never_count_the_game_session_plan_as_applied()
    {
        var high = PowerSchemes.HighPerformance;
        var balanced = PowerSchemes.Balanced;
        Assert.Equal(TweakState.Applied, TweakCatalog.SchemeState(high, high, sessionPlan: false, _ => true));
        // The session's temporary plan ends with it: the item stays selectable.
        Assert.Equal(TweakState.NotApplied, TweakCatalog.SchemeState(high, high, sessionPlan: true, _ => true));
        Assert.Equal(TweakState.NotApplied, TweakCatalog.SchemeState(high, balanced, sessionPlan: true, _ => true));
        Assert.Equal(TweakState.NotApplicable, TweakCatalog.SchemeState(balanced, high, sessionPlan: false, _ => false));
        Assert.Equal(TweakState.Unknown, TweakCatalog.SchemeState(Guid.Empty, high, sessionPlan: false, _ => true));

        Assert.False(TweakCatalog.SchemeChangeNeeded(high, high, sessionPlan: false));
        Assert.True(TweakCatalog.SchemeChangeNeeded(high, high, sessionPlan: true)); // recorded as the user's own layer
        Assert.True(TweakCatalog.SchemeChangeNeeded(balanced, high, sessionPlan: false));
    }

    private sealed class PlanSystem : IJournalSystem
    {
        public Guid Active { get; set; } = PowerSchemes.Balanced;

        public Task<RevertOutcome> RestoreAsync(ChangeRecord r)
        {
            Active = r.PowerSchemeOriginal ?? PowerSchemes.Balanced;
            return Task.FromResult(RevertOutcome.Done);
        }

        public Guid ActivePowerScheme() => Active;
        public bool? PowerSchemeExists(Guid scheme) => true;
        public void DeletePowerScheme(Guid scheme) { }
    }

    [Fact]
    public async Task A_plan_chosen_during_a_game_session_is_its_own_layer_and_outlasts_the_session()
    {
        var system = new PlanSystem();
        var journal = ChangeJournal.ForTests();
        journal.SystemAccess = system;
        ChangeRecord SetPlan(string source, Guid scheme)
        {
            var (rec, _) = journal.Add(new ChangeRecord
            {
                BatchId = journal.BeginBatch(source).Id, Source = source, Title = source, Kind = ChangeKind.PowerScheme, Target = "power:active",
                OldValue = system.Active.ToString(), NewValue = scheme.ToString(), PowerSchemeOriginal = system.Active,
            });
            system.Active = scheme;
            return rec;
        }

        var session = SetPlan("gameboost", PowerSchemes.HighPerformance);
        // «Высокая производительность» chosen in Optimize while the session's High plan is live: a High → High layer.
        Assert.True(TweakCatalog.SchemeChangeNeeded(system.Active, PowerSchemes.HighPerformance, sessionPlan: true));
        var tweak = SetPlan("tweak:power-plan-performance", PowerSchemes.HighPerformance);
        Assert.NotSame(session, tweak);

        var end = await journal.RevertSourceAsync("gameboost"); // the session ends
        Assert.Equal(1, end.HandedOver);
        Assert.Equal(PowerSchemes.HighPerformance, system.Active); // the user's choice stays
        Assert.Equal(PowerSchemes.Balanced, tweak.PowerSchemeOriginal);

        await journal.RevertSourceAsync("tweak:power-plan-performance");
        Assert.Equal(PowerSchemes.Balanced, system.Active); // and its own undo goes back to the plan from before both
    }

    // ───────────────────────────── DNS registration ─────────────────────────────

    [Fact]
    public void Dns_scripts_keep_the_adapter_dns_registration()
    {
        string[] scripts =
        [
            TweakCatalog.DnsScript("ipv4", DnsGuid, []),
            TweakCatalog.DnsScript("ipv6", DnsGuid, ["2606:4700:4700::1111"]),
            TweakCatalog.DnsScript("ipv4", DnsGuid, ["1.1.1.1", "1.0.0.1"], rollbackTo: []),
            TweakCatalog.DnsScript("ipv6", DnsGuid, [], rollbackTo: ["fd00::1"]),
        ];
        foreach (var s in scripts)
        {
            var capture = s.IndexOf(TweakCatalog.DnsRegistrationCapture, StringComparison.Ordinal);
            var netsh = s.IndexOf("Invoke-Netsh @(", StringComparison.Ordinal);
            var restore = s.IndexOf(TweakCatalog.DnsRegistrationRestore, StringComparison.Ordinal);
            Assert.True(capture > 0 && capture < netsh, "flags must be read before netsh runs");
            Assert.True(restore > netsh, "flags must be put back after netsh");
        }
        Assert.Contains("Get-DnsClient -InterfaceIndex $i", TweakCatalog.DnsRegistrationCapture);
        Assert.Contains("-RegisterThisConnectionsAddress:$re -UseSuffixWhenRegistering:$us", TweakCatalog.DnsRegistrationRestore);

        // The rollback puts the flags back too, after the previous server list.
        var forward = TweakCatalog.DnsScript("ipv4", DnsGuid, ["1.1.1.1"], rollbackTo: ["192.168.1.1"]);
        var catchAt = forward.IndexOf("}catch{$e=$_;", StringComparison.Ordinal);
        Assert.Contains(TweakCatalog.DnsRegistrationRestore, forward[..catchAt]);
        var rollback = forward[catchAt..];
        Assert.True(rollback.IndexOf(TweakCatalog.DnsRegistrationRestore, StringComparison.Ordinal) > rollback.IndexOf("'address=192.168.1.1'", StringComparison.Ordinal));
        Assert.Contains("throw $e", rollback);
    }

    [Fact]
    public void Dns_script_of_older_builds_can_still_be_rebuilt()
    {
        // keepRegistration: false is exactly the text older builds journaled (checked by JournalTrust before an undo).
        var g = DnsGuid;
        foreach (var (servers, rollback) in new (string[], string[]?)[] { ([], null), (["1.1.1.1", "8.8.8.8"], null), (["1.1.1.1"], []) })
        {
            var legacy = TweakCatalog.DnsScript("ipv4", g, servers, rollback, keepRegistration: false);
            Assert.DoesNotContain("DnsClient", legacy);
            var current = TweakCatalog.DnsScript("ipv4", g, servers, rollback);
            var stripped = current.Replace(TweakCatalog.DnsRegistrationCapture, "").Replace(TweakCatalog.DnsRegistrationRestore, "")
                .Replace("try{}catch{};", "");
            Assert.Equal(legacy, stripped);
        }
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private static readonly string[] ForbiddenPathFragments =
    [
        "Windows Defender", "Microsoft Antimalware", "SmartScreen", "WindowsFirewall", "FirewallPolicy", "SharedAccess\\Parameters",
        @"WindowsUpdate\AU", @"Services\WinDefend", @"Services\wuauserv", @"Services\UsoSvc", @"Services\WaaSMedicSvc",
        @"Services\mpssvc", @"Services\SecurityHealthService", "UCPD", "Tamper",
    ];

    private static readonly string[] ForbiddenValueNames =
    [
        "EnableLUA", "ConsentPromptBehaviorAdmin", "ConsentPromptBehaviorUser", "PromptOnSecureDesktop",
        "EnableSmartScreen", "SmartScreenEnabled", "ShellSmartScreenLevel", "EnableWebContentEvaluation",
        "NoAutoUpdate", "AUOptions", "DisableWindowsUpdateAccess", "SetDisableUXWUAccess", "DoNotConnectToWindowsUpdateInternetLocations",
        "DisableAntiSpyware", "DisableAntiVirus", "DisableRealtimeMonitoring", "DisableBehaviorMonitoring", "EnableFirewall",
        "DisableNotifications", "TaskbarDa", "EnableVirtualizationBasedSecurity", "FeatureSettingsOverride", "FeatureSettingsOverrideMask",
        "SystemResponsiveness", "GlobalTimerResolutionRequests", "LargeSystemCache", "DisablePagingExecutive", "ClearPageFileAtShutdown",
        "PlatformAoAcOverride", "AllowGameDVR", "LetAppsRunInBackground",
    ];

    private static bool IsForbidden(RegSet v) =>
        ForbiddenPathFragments.Any(f => v.Path.Contains(f, StringComparison.OrdinalIgnoreCase)) ||
        ForbiddenValueNames.Any(n => string.Equals(v.Name, n, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Registry-backed CustomTweaks are not covered by TweakApplyRevertTests (it only runs RegistryTweak): apply and revert
/// them against a redirected registry (HKCU\Software\MONOLITH-Tests\Redirect-*) and check every value comes back exactly.
/// </summary>
[Collection("Registry")]
public sealed class CustomTweakApplyRevertTests : IDisposable
{
    private readonly string _root = $@"Software\MONOLITH-Tests\Redirect-{Guid.NewGuid():N}";
    private readonly string _journalPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"monolith-customtweak-{Guid.NewGuid():N}.json");

    public CustomTweakApplyRevertTests()
    {
        RegistryHelper.Redirect = (hive, path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");
        ChangeSession.BypassElevationForTests = true;
    }

    public void Dispose()
    {
        RegistryHelper.Redirect = null;
        ChangeSession.BypassElevationForTests = false;
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, false); } catch { }
        foreach (var f in new[] { _journalPath, _journalPath + ".bak", _journalPath + ".tmp" })
            try { System.IO.File.Delete(f); } catch { }
    }

    private const string Sticky = @"Control Panel\Accessibility\StickyKeys";
    private const string Filter = @"Control Panel\Accessibility\Keyboard Response";
    private const string Toggle = @"Control Panel\Accessibility\ToggleKeys";
    private const string FileSystem = @"SYSTEM\CurrentControlSet\Control\FileSystem";

    [Fact]
    public async Task Accessibility_shortcuts_are_cleared_and_restored_byte_for_byte()
    {
        RegistryHelper.Write(RegHive.HKCU, Sticky, "Flags", RegistryValueKind.String, "510");
        RegistryHelper.Write(RegHive.HKCU, Filter, "Flags", RegistryValueKind.String, "126");
        RegistryHelper.Write(RegHive.HKCU, Toggle, "Flags", RegistryValueKind.String, "62");
        var before = Capture();

        var tweak = TweakCatalog.Find("accessibility-hotkeys-off")!;
        Assert.Equal(TweakState.NotApplied, tweak.Detect());
        await Apply(tweak);
        Assert.Equal(TweakState.Applied, tweak.Detect());
        Assert.Equal("506", RegistryHelper.ReadString(RegHive.HKCU, Sticky, "Flags"));
        Assert.Equal("122", RegistryHelper.ReadString(RegHive.HKCU, Filter, "Flags"));
        Assert.Equal("58", RegistryHelper.ReadString(RegHive.HKCU, Toggle, "Flags"));
        Assert.Equal(RegistryValueKind.String, RegistryHelper.Snapshot(RegHive.HKCU, Sticky, "Flags").Kind);

        await RevertAll();
        Assert.Equal(before, Capture());
    }

    [Fact]
    public async Task Accessibility_feature_in_use_and_absent_values_are_handled()
    {
        RegistryHelper.Write(RegHive.HKCU, Sticky, "Flags", RegistryValueKind.String, "511"); // Sticky Keys itself is on
        var before = Capture();                                                               // Filter/Toggle: absent

        var tweak = TweakCatalog.Find("accessibility-hotkeys-off")!;
        await Apply(tweak);
        Assert.Equal("511", RegistryHelper.ReadString(RegHive.HKCU, Sticky, "Flags"));
        Assert.Equal("122", RegistryHelper.ReadString(RegHive.HKCU, Filter, "Flags"));
        Assert.Equal("58", RegistryHelper.ReadString(RegHive.HKCU, Toggle, "Flags"));

        await RevertAll();
        Assert.Equal(before, Capture());
        Assert.False(RegistryHelper.KeyExists(RegHive.HKCU, Toggle)); // the key MONOLITH created is gone again
    }

    [Fact]
    public async Task Trim_is_repaired_only_when_disabled_and_restored_exactly()
    {
        var tweak = TweakCatalog.Find("trim-on")!;
        RegistryHelper.Write(RegHive.HKLM, FileSystem, "DisableDeleteNotification", RegistryValueKind.DWord, 0);
        Assert.Equal(TweakState.Applied, tweak.Detect());
        await Apply(tweak, expectChanges: 0);

        RegistryHelper.Write(RegHive.HKLM, FileSystem, "DisableDeleteNotification", RegistryValueKind.DWord, 1);
        RegistryHelper.Write(RegHive.HKLM, FileSystem, "RefsDisableDeleteNotification", RegistryValueKind.DWord, 1);
        Assert.Equal(TweakState.NotApplied, tweak.Detect());
        await Apply(tweak, expectChanges: 1);
        Assert.Equal(0, RegistryHelper.ReadInt(RegHive.HKLM, FileSystem, "DisableDeleteNotification"));
        Assert.Equal(1, RegistryHelper.ReadInt(RegHive.HKLM, FileSystem, "RefsDisableDeleteNotification")); // ReFS untouched

        await RevertAll();
        Assert.Equal(1, RegistryHelper.ReadInt(RegHive.HKLM, FileSystem, "DisableDeleteNotification"));
    }

    private async Task Apply(Tweak tweak, int? expectChanges = null)
    {
        var journal = new ChangeJournal(_journalPath);
        var session = await ChangeSession.BeginAsync("test " + tweak.Id, createRestorePoint: false, journal: journal);
        tweak.Apply(new TweakContext { Session = session, System = SystemProfile.Current, Tweak = tweak });
        session.Complete();
        Assert.Empty(session.Errors);
        if (expectChanges is { } n) Assert.Equal(n, session.Applied);
    }

    private async Task RevertAll()
    {
        var journal = new ChangeJournal(_journalPath);
        var result = await journal.RevertAllAsync();
        Assert.True(result.Success, string.Join("; ", result.Errors));
    }

    private static string Capture() => string.Join("\n", new[] { Sticky, Filter, Toggle }.Select(p =>
    {
        var s = RegistryHelper.Snapshot(RegHive.HKCU, p, "Flags");
        return $"{p} = {(s.ValueExisted ? $"{s.Kind}:{s.Data}" : "absent")}";
    }));
}

/// <summary>DirectX key ownership is per Windows account: another account's records describe another profile's HKCU.</summary>
[Collection("Registry")] // changes the process-wide test account (UserScope.CurrentSidForTests)
public sealed class DirectXOwnershipAccountTests : IDisposable
{
    private const string Me = "S-1-5-21-1000000001-1000000002-1000000003-1001";
    private const string Other = "S-1-5-21-1000000001-1000000002-1000000003-1002";

    public DirectXOwnershipAccountTests() => UserScope.CurrentSidForTests = () => Me;
    public void Dispose() => UserScope.CurrentSidForTests = null;

    [Fact]
    public void Only_this_accounts_records_claim_the_directx_key()
    {
        // Another account created ITS key: this account's key existed before MONOLITH and is never removed.
        Assert.Equal((false, false), TweakCatalog.DxUndoOwnership(true, true, [TweakCatalogTests.DxToken(true, true, sid: Other)]));
        Assert.Equal((false, false), TweakCatalog.DxUndoOwnership(true, true,
            [TweakCatalogTests.GpuChoice(@"Software\Microsoft\DirectX", keyExisted: false, sid: Other)]));
        // Missing key, existing parent of this account: another account's $mp says nothing about it.
        Assert.Equal((true, false), TweakCatalog.DxUndoOwnership(false, true, [TweakCatalogTests.DxToken(true, true, sid: Other)]));

        Assert.Equal((true, true), TweakCatalog.DxUndoOwnership(true, true, [TweakCatalogTests.DxToken(true, true, sid: Me)]));
        Assert.Equal((true, false), TweakCatalog.DxUndoOwnership(true, true,
            [TweakCatalogTests.GpuChoice(@"Software\Microsoft\DirectX\UserGpuPreferences", keyExisted: false, sid: Me)]));
        // Records of older builds name no account: they were written into this account's HKCU.
        Assert.Equal((true, true), TweakCatalog.DxUndoOwnership(true, true, [TweakCatalogTests.DxToken(true, true)]));
    }
}
