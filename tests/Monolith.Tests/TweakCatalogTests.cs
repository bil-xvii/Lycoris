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
            TweakCatalog.SpiApplyScript(flagsOff, 0, flagsOn, 1),
            TweakCatalog.DxTokenScript("SwapEffectUpgradeEnable", "1"),
            TweakCatalog.DxTokenScript("VRROptimizeEnable", null),
            TweakCatalog.DnsScript(AdapterGuid, false, ["1.1.1.1", "1.0.0.1"], rollback: []),
            TweakCatalog.DnsScript(AdapterGuid, true, ["2606:4700:4700::1111", "2606:4700:4700::1001"], rollback: ["fe80::1"]),
            TweakCatalog.DnsScript(AdapterGuid, false, []),
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
        Assert.True(TweakCatalog.PowerShellArgs(TweakCatalog.SpiApplyScript(flags, 1, flags, 1)).Length < 8000);
        Assert.True(TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript("SwapEffectUpgradeEnable", "1")).Length < 8000);
        Assert.True(TweakCatalog.PowerShellArgs(TweakCatalog.DnsScript(AdapterGuid, true,
            ["2606:4700:4700::1111", "2606:4700:4700::1001"], rollback: ["2001:db8::1", "2001:db8::2", "2001:db8::3"])).Length < 8000);
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
        var actions = string.Join(",", TweakCatalog.VisualEffectFlags.Select(f => $"0x{f.Set:X}"));
        var off = string.Join(",", TweakCatalog.VisualEffectFlags.Select(_ => "0"));
        Assert.Contains($"$fl=@({actions});$fv=@({off});", script);
        Assert.Contains("[Monolith.Spi]::Set($fl[$k],0,[IntPtr]($fv[$k]),3)", script);
        Assert.Contains("SetAnimation(0x49,8,[int[]]@(8,0),3)", script);
        Assert.DoesNotContain("$fv=@(1", script);
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

    private const string AdapterGuid = "{4D36E972-E325-11CE-BFC1-08002BE10318}";

    [Fact]
    public void Dns_scripts_set_static_lists_or_return_to_dhcp_on_the_adapter_found_by_guid()
    {
        var dhcp = TweakCatalog.DnsScript(AdapterGuid, false, []);
        Assert.Contains($"$g='{AdapterGuid}'", dhcp);
        Assert.Contains("GetIPv4Properties()", dhcp);
        Assert.Contains("'interface' 'ipv4' 'set' 'dnsservers' \"name=$i\" 'source=dhcp'", dhcp);
        Assert.Contains("netsh.exe'", dhcp);
        Assert.Contains("'/flushdns'", dhcp);

        var stat = TweakCatalog.DnsScript(AdapterGuid.ToLowerInvariant(), true, ["2606:4700:4700::1111", "2606:4700:4700::1001"]);
        Assert.Contains($"$g='{AdapterGuid}'", stat); // normalized spelling
        Assert.Contains("GetIPv6Properties()", stat);
        Assert.Contains("'interface' 'ipv6' 'set' 'dnsservers' \"name=$i\" 'source=static' 'address=2606:4700:4700::1111' 'register=primary' 'validate=no'", stat);
        Assert.Contains("'interface' 'ipv6' 'add' 'dnsservers' \"name=$i\" 'address=2606:4700:4700::1001' 'index=2' 'validate=no'", stat);
        // The interface index is never frozen into the script: it is looked up from the GUID when the script runs.
        Assert.DoesNotMatch(@"name=\d", stat);
    }

    [Fact]
    public void Dns_apply_script_rolls_back_to_the_previous_servers_on_failure()
    {
        var script = TweakCatalog.DnsScript(AdapterGuid, false, ["1.1.1.1", "1.0.0.1"], rollback: []);
        Assert.Contains("$mChanged=0;try{", script);
        Assert.Contains("'source=dhcp'", script); // the rollback branch puts DHCP back
        Assert.True(script.IndexOf("'address=1.1.1.1'", StringComparison.Ordinal) < script.IndexOf("'source=dhcp'", StringComparison.Ordinal));
        Assert.DoesNotContain("$mChanged=0", TweakCatalog.DnsScript(AdapterGuid, false, [])); // undo scripts need no rollback
    }

    [Fact]
    public void Dns_scripts_reject_anything_that_is_not_an_ip_address_or_a_guid()
    {
        Assert.Throws<InvalidOperationException>(() => TweakCatalog.DnsScript(AdapterGuid, false, ["1.1.1.1 & calc"]));
        Assert.Throws<InvalidOperationException>(() => TweakCatalog.DnsScript(AdapterGuid, false, ["1.1.1.1"], rollback: ["8.8.8.8';calc;'"]));
        Assert.Throws<InvalidOperationException>(() => TweakCatalog.DnsScript("{x}'; calc; '", false, ["1.1.1.1"]));
    }

    [Fact]
    public async Task Dns_script_fails_without_touching_anything_when_the_adapter_is_gone()
    {
        // A GUID that belongs to no adapter: the lookup throws before any netsh call.
        var script = TweakCatalog.DnsScript(Guid.NewGuid().ToString("B"), false, ["1.1.1.1"], rollback: []);
        var r = await ProcessRunner.RunAsync(TweakCatalog.PowerShellExe, TweakCatalog.PowerShellArgs(script),
            new ProcessRunOptions { Encoding = OutputEncoding.Utf8, LogCommand = false, Timeout = TimeSpan.FromSeconds(60) });
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("не найден", r.Combined);
    }

    [Fact]
    public async Task Dns_adapter_lookup_finds_the_current_interface_index()
    {
        // Read-only: resolves the index of a real interface from its GUID exactly like the undo script does.
        var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
        {
            try { return Guid.TryParse(n.Id, out _) && n.Supports(System.Net.NetworkInformation.NetworkInterfaceComponent.IPv4) && n.GetIPProperties().GetIPv4Properties() is not null; }
            catch { return false; }
        });
        if (nic is null) return; // no IPv4 interface on this machine
        var expected = nic.GetIPProperties().GetIPv4Properties()!.Index;

        var script = TweakCatalog.DnsResolveIndexScript(nic.Id, false) + "Write-Output ('IDX='+$i)";
        var r = await ProcessRunner.RunAsync(TweakCatalog.PowerShellExe, TweakCatalog.PowerShellArgs(script),
            new ProcessRunOptions { Encoding = OutputEncoding.Utf8, LogCommand = false, Timeout = TimeSpan.FromSeconds(60) });
        Assert.True(r.Success, r.Combined);
        Assert.Contains($"IDX={expected}", r.StdOut);
    }

    // ───────────────────────────── Self-rollback of multi-step commands ─────────────────────────────

    private static async Task<ProcessResult> RunPs(string script) =>
        await ProcessRunner.RunAsync(TweakCatalog.PowerShellExe, TweakCatalog.PowerShellArgs(script),
            new ProcessRunOptions { Encoding = OutputEncoding.Utf8, LogCommand = false, Timeout = TimeSpan.FromSeconds(60) });

    [Fact]
    public async Task Self_rollback_script_restores_and_fails_after_a_partial_change()
    {
        var r = await RunPs(TweakCatalog.SelfRollbackScript("Write-Output STEP1;$mChanged++;throw 'шаг 2'", "Write-Output ROLLBACK", "оставлено"));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("STEP1", r.StdOut);
        Assert.Contains("ROLLBACK", r.StdOut);
        Assert.Contains("шаг 2", r.Combined);
    }

    [Fact]
    public async Task Self_rollback_script_does_nothing_extra_when_the_first_step_fails()
    {
        var r = await RunPs(TweakCatalog.SelfRollbackScript("throw 'шаг 1'", "Write-Output ROLLBACK", "оставлено"));
        Assert.Equal(1, r.ExitCode);
        Assert.DoesNotContain("ROLLBACK", r.StdOut);
    }

    [Fact]
    public async Task Self_rollback_script_keeps_the_journal_record_when_the_rollback_fails_too()
    {
        // Exit code 0 = ChangeSession keeps the record, so the half-applied change can still be undone.
        var r = await RunPs(TweakCatalog.SelfRollbackScript("$mChanged++;throw 'шаг 2'", "throw 'откат'", "ЗАПИСЬ ОСТАВЛЕНА"));
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("ЗАПИСЬ ОСТАВЛЕНА", r.Combined);
    }

    [Fact]
    public async Task Self_rollback_script_succeeds_normally()
    {
        var r = await RunPs(TweakCatalog.SelfRollbackScript("Write-Output DONE;$mChanged++", "Write-Output ROLLBACK", "оставлено"));
        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("ROLLBACK", r.StdOut);
    }

    private static async Task<ProcessResult> RunCmd(string line) =>
        await ProcessRunner.RunAsync(ProcessRunner.Sys("cmd.exe"), TweakCatalog.CmdArgs(line),
            new ProcessRunOptions { Encoding = OutputEncoding.Oem, LogCommand = false, Timeout = TimeSpan.FromSeconds(30) });

    private static readonly string Ok = $"\"{ProcessRunner.Sys("cmd.exe")}\" /d /c exit 0";
    private static readonly string Fail = $"\"{ProcessRunner.Sys("cmd.exe")}\" /d /c exit 1";

    [Fact]
    public async Task Write_activate_command_succeeds_when_both_steps_succeed()
    {
        var r = await RunCmd(TweakCatalog.WriteActivateOrRollbackCmd($"(echo WRITE&{Ok})", $"(echo ACT&{Ok})", "echo BACK"));
        Assert.Equal(0, r.ExitCode);
        Assert.DoesNotContain("BACK", r.StdOut);
    }

    [Fact]
    public async Task Write_activate_command_fails_without_activation_when_the_write_fails()
    {
        var r = await RunCmd(TweakCatalog.WriteActivateOrRollbackCmd($"(echo WRITE&{Fail})", "echo ACT", "echo BACK"));
        Assert.NotEqual(0, r.ExitCode);
        Assert.DoesNotContain("ACT", r.StdOut);
        Assert.DoesNotContain("BACK", r.StdOut);
    }

    [Fact]
    public async Task Write_activate_command_writes_back_and_fails_when_activation_fails()
    {
        var r = await RunCmd(TweakCatalog.WriteActivateOrRollbackCmd("echo WRITE", $"(echo ACT&{Fail})", "echo BACK"));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("BACK", r.StdOut);
    }

    [Fact]
    public async Task Write_activate_command_keeps_the_record_when_the_write_back_fails_too()
    {
        // Value changed, activation and write-back failed: success (exit 0) keeps the journal record and its undo.
        var r = await RunCmd(TweakCatalog.WriteActivateOrRollbackCmd("echo WRITE", $"(echo ACT&{Fail})", $"(echo BACK&{Fail})"));
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("BACK", r.StdOut);
    }

    [Fact]
    public void Power_setting_command_writes_back_the_original_value_when_activation_fails()
    {
        var scheme = Guid.NewGuid();
        var sub = Guid.NewGuid();
        var setting = Guid.NewGuid();
        var apply = TweakCatalog.PowerSettingApplyCmd(scheme, sub, setting, 0, 3);
        Assert.Contains($"/setacvalueindex {scheme:D} {sub:D} {setting:D} 0 && (", apply);
        Assert.Contains($"/setacvalueindex {scheme:D} {sub:D} {setting:D} 3 && (", apply);
        Assert.Contains("/setactive SCHEME_CURRENT", apply);
        Assert.Contains("exit /b 1", apply);
        Assert.Contains("exit /b 0", apply);

        var revert = TweakCatalog.PowerSettingRevertCmd(scheme, sub, setting, 3);
        Assert.Contains($"/setacvalueindex {scheme:D} {sub:D} {setting:D} 3 && ", revert);
        Assert.DoesNotContain("exit /b", revert);
    }

    [Fact]
    public void Visual_effects_apply_script_rolls_back_and_defines_the_type_first()
    {
        var off = TweakCatalog.VisualEffectFlags.Select(f => (f.Set, false)).ToList();
        var on = TweakCatalog.VisualEffectFlags.Select(f => (f.Set, true)).ToList();
        var script = TweakCatalog.SpiApplyScript(off, 0, on, 1);
        Assert.StartsWith(TweakCatalog.SpiTypeDefinition, script);
        var rollback = script.IndexOf("$mBack=$true", StringComparison.Ordinal);
        Assert.True(rollback > 0);
        var allOn = string.Join(",", TweakCatalog.VisualEffectFlags.Select(_ => "1"));
        Assert.Contains($"$fv=@({allOn})", script[rollback..]); // original flags are put back in the rollback branch
        Assert.Contains("SetAnimation(0x49,8,[int[]]@(8,1),3)", script[rollback..]);
        Assert.DoesNotContain($"$fv=@({allOn})", script[..rollback]);
    }

    /// <summary>
    /// A stand-in for the SystemParametersInfo wrapper: same type name and signatures, but it only prints each call
    /// (and fails the call number <c>FailAt</c>). Defined first, the real P/Invoke type is then skipped by the script.
    /// </summary>
    private const string FakeSpiType =
        "Add-Type -TypeDefinition 'namespace Monolith { public static class Spi { public static int N = 0; public static int FailAt = -1; " +
        "public static bool Set(uint a, uint u, System.IntPtr p, uint f) { N++; System.Console.WriteLine(\"CALL S\" + a.ToString(\"X\") + \"=\" + p.ToInt64()); return N != FailAt; } " +
        "public static bool SetAnimation(uint a, uint u, int[] p, uint f) { N++; System.Console.WriteLine(\"CALL A=\" + p[1]); return N != FailAt; } } }';";

    [Theory]
    [InlineData(-1, 0, 9)]  // all calls succeed: 8 flags + animation
    [InlineData(1, 1, 1)]   // the first call fails: nothing changed, no rollback
    [InlineData(3, 1, 12)]  // the third call fails: 3 calls, then 8 flags + animation put back
    public async Task Visual_effects_apply_script_restores_the_original_flags_after_a_partial_failure(int failAt, int exitCode, int calls)
    {
        var off = TweakCatalog.VisualEffectFlags.Select(f => (f.Set, false)).ToList();
        var on = TweakCatalog.VisualEffectFlags.Select(f => (f.Set, true)).ToList();
        var r = await RunPs(FakeSpiType + $"[Monolith.Spi]::FailAt={failAt};" + TweakCatalog.SpiApplyScript(off, 0, on, 2));

        Assert.Equal(exitCode, r.ExitCode);
        var lines = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(l => l.StartsWith("CALL ", StringComparison.Ordinal)).ToList();
        Assert.Equal(calls, lines.Count);
        Assert.Equal($"CALL S{TweakCatalog.VisualEffectFlags[0].Set:X}=0", lines[0]);
        if (failAt == 3)
        {
            Assert.All(lines.Skip(3).Take(8), l => Assert.EndsWith("=1", l)); // original "on" values written back
            Assert.Equal("CALL A=2", lines[^1]);                              // and the original window animation
        }
        if (failAt == -1) Assert.Equal("CALL A=0", lines[^1]);
    }

    // ───────────────────────────── Honest classification ─────────────────────────────

    [Fact]
    public void Data_deleting_tweaks_are_marked_and_never_preselected()
    {
        Assert.NotEmpty(TweakCatalog.DataLossIds);
        foreach (var id in TweakCatalog.DataLossIds)
        {
            var t = TweakCatalog.Find(id);
            Assert.NotNull(t);
            Assert.NotEqual(TweakRisk.Safe, t!.Risk);
            Assert.Equal(ProfileSet.None, t.DefaultIn);
            var note = TweakCatalog.DataLossNote(t);
            Assert.False(string.IsNullOrWhiteSpace(note));
            Assert.Matches("[А-Яа-я]", note!);
            Assert.Contains("откат", note, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("recall-snapshots-off", TweakCatalog.DataLossIds);
        Assert.Null(TweakCatalog.DataLossNote(TweakCatalog.Find("game-mode")!));
    }

    [Fact]
    public void Recall_item_says_that_deleted_snapshots_do_not_come_back()
    {
        var recall = TweakCatalog.Find("recall-snapshots-off")!;
        Assert.Contains("удалены", recall.Description);
        Assert.Contains("не вернутся", recall.Details);
    }

    [Fact]
    public void Diagtrack_manual_matches_the_services_page_classification()
    {
        var t = TweakCatalog.Find("diagtrack-manual")!;
        Assert.Equal(TweakRisk.Optional, t.Risk);
        Assert.Equal(ProfileSet.None, t.DefaultIn);
        foreach (var kind in Enum.GetValues<ProfileKind>())
            Assert.DoesNotContain(TweakCatalog.DefaultsFor(kind), x => x.Id == "diagtrack-manual");
        Assert.Contains("Центра отзывов", t.Details);
        Assert.Contains("Только обязательная диагностика", t.Details);
    }

    [Fact]
    public void Delivery_optimization_is_recommended_but_not_sold_as_a_gaming_boost()
    {
        var t = TweakCatalog.Find("delivery-optimization-local")!;
        Assert.True(t.DefaultIn.HasFlag(ProfileSet.Recommended));
        Assert.False(t.DefaultIn.HasFlag(ProfileSet.Gaming));
        Assert.DoesNotContain("мешать онлайн-играм", t.Details);
        Assert.Contains("локальной сети", t.Details);
    }

    [Fact]
    public void Windows_game_mode_is_never_called_just_igrovoy_rezhim_in_running_text()
    {
        // «Игровой режим» alone is the name of MONOLITH's game boost; the Windows feature is always «игровой режим Windows».
        var bare = new Regex(@"игров\w* (?>режим\w*)(?! Windows)", RegexOptions.IgnoreCase);
        foreach (var t in TweakCatalog.All)
        {
            foreach (var text in new[] { t.Description, t.Details, t.Warning ?? "" })
            {
                var cleaned = text.Replace("Параметры → Игры → Игровой режим", "", StringComparison.Ordinal);
                Assert.False(bare.IsMatch(cleaned), $"{t.Id}: «{cleaned}»");
            }
        }
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
