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
        "delivery-optimization-local",
    };

    // ───────────────────────────── Catalog integrity ─────────────────────────────

    [Fact]
    public void Catalog_has_a_full_set_of_tweaks()
    {
        Assert.InRange(TweakCatalog.All.Count, 35, 90);
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
