using System.Text.RegularExpressions;
using Monolith.Core;
using Monolith.Modules.Apps;

namespace Monolith.Tests;

/// <summary>Pure logic of the "ПРИЛОЖЕНИЯ" page: catalog integrity, winget arguments, exit codes, parsing. Nothing is installed.</summary>
public sealed class AppsTests
{
    // ─────────────────────────── catalog integrity ───────────────────────────

    [Fact]
    public void Catalog_contains_every_verified_research_entry()
    {
        // docs/research/apps_catalog.md §8 lists 124 verified packages; the two vendor driver tools
        // (NVIDIA App, Intel DSA) are offered only on the Drivers page.
        Assert.Equal(122, AppCatalog.All.Count);
    }

    [Theory]
    [InlineData("XP8CLZL93F5Z4P")]
    [InlineData("Intel.IntelDriverAndSupportAssistant")]
    public void Vendor_driver_tools_live_only_on_the_drivers_page(string id)
    {
        Assert.Null(AppCatalog.Find(id));
        Assert.Contains(new[] { GpuVendor.Nvidia, GpuVendor.Intel },
            v => Monolith.Modules.Drivers.DriverSources.ToolFor(v)?.WingetId == id);
    }

    [Fact]
    public void Package_ids_are_unique_including_regional_variants()
    {
        var ids = AppCatalog.All.SelectMany(e => e.AllIds).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(AppCatalog.All.Count, AppCatalog.All.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Package_ids_are_plain_tokens()
    {
        foreach (var id in AppCatalog.All.SelectMany(e => e.AllIds))
        {
            Assert.True(WingetClient.IsValidPackageId(id), id);
            Assert.True(UnelevatedWinget.IsSafeToken(id), id);
        }
    }

    [Fact]
    public void Store_ids_are_store_product_ids()
    {
        // Store product ids: "9…" (12 chars, MSIX/UWP) or "XP…" (14 chars, Win32 apps served by the Store).
        var storeId = new Regex("^(9[0-9A-Z]{11}|XP[0-9A-Z]{12})$");
        foreach (var e in AppCatalog.All.Where(e => e.Source == PackageSource.MsStore))
            Assert.Matches(storeId, e.Id);
        foreach (var e in AppCatalog.All.Where(e => e.Source == PackageSource.Winget))
            Assert.Contains('.', e.Id);
    }

    [Fact]
    public void Categories_are_valid_complete_and_in_page_order()
    {
        Assert.Equal(
            ["ЛАУНЧЕРЫ", "БИБЛИОТЕКИ ДЛЯ ИГР", "ИНСТРУМЕНТЫ ГЕЙМЕРА", "ОБЩЕНИЕ", "БРАУЗЕРЫ", "СТРИМИНГ И ЗАПИСЬ", "УТИЛИТЫ"],
            AppCatalog.Categories.Select(c => c.Title));
        Assert.Equal(Enum.GetValues<AppCategory>().Length, AppCatalog.Categories.Select(c => c.Category).Distinct().Count());
        foreach (var e in AppCatalog.All)
            Assert.Contains(AppCatalog.Categories, c => c.Category == e.Category);
        foreach (var c in AppCatalog.Categories)
            Assert.Contains(AppCatalog.All, e => e.Category == c.Category);
    }

    [Fact]
    public void Every_entry_has_a_name_and_a_russian_description()
    {
        var cyrillic = new Regex("[А-Яа-яЁё]");
        foreach (var e in AppCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.Description), e.Id);
            Assert.Matches(cyrillic, e.Description);
        }
        foreach (var c in AppCatalog.Categories)
        {
            Assert.Matches(cyrillic, c.Description);
            Assert.Equal(c.Title, c.Title.ToUpperInvariant());
            Assert.Equal(c.Tag, c.Tag.ToUpperInvariant());
        }
    }

    [Fact]
    public void Websites_are_absolute_https_links()
    {
        foreach (var e in AppCatalog.All)
        {
            Assert.True(Uri.TryCreate(e.Website, UriKind.Absolute, out var uri), e.Id);
            Assert.Equal("https", uri!.Scheme);
            Assert.False(string.IsNullOrEmpty(uri.Host), e.Id);
        }
    }

    [Theory]
    [InlineData("Vencord.Vesktop")]
    [InlineData("Hibbiki.Chromium")]
    [InlineData("AMD.AMDSoftwareCloudEdition")]
    [InlineData("Oracle.JavaRuntimeEnvironment")]
    [InlineData("KhronosGroup.VulkanRT")]
    [InlineData("Microsoft.OpenCLGLVulkanCompatibilityPack")]
    [InlineData("Microsoft.VCLibs.Desktop.14")]
    [InlineData("BleachBit.BleachBit")]
    [InlineData("9PM860492SZD")]
    [InlineData("Nvidia.NVIDIAApp")]
    [InlineData("Nvidia.GeForceExperience")]
    [InlineData("Microsoft.AppInstaller")]
    public void Excluded_packages_are_not_in_the_catalog(string id) => Assert.Null(AppCatalog.Find(id));

    [Fact]
    public void Gamer_kit_is_every_vc_redist_directx_and_the_current_desktop_runtime()
    {
        // The kit is computed from the install advice (AppAdvisor.Kit); a typical Windows 11 24H2 PC without the Xbox app.
        var ctx = new AdviceContext
        {
            OsBuild = 26100, OsUbr = 6899, InstalledKnown = true, StoreKnown = true,
            InstalledIds = new HashSet<string> { "Microsoft.EdgeWebView2Runtime" },
            Today = new DateOnly(2026, 10, 27),
        };
        var kit = AppAdvisor.Kit(AppCatalog.ForBuild(ctx.OsBuild), ctx).Select(e => e.Id).ToList();
        Assert.Equal(kit.Count, kit.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var vc = AppCatalog.All.Where(e => e.Id.StartsWith("Microsoft.VCRedist.", StringComparison.Ordinal)).Select(e => e.Id).ToList();
        Assert.Equal(12, vc.Count);
        Assert.All(vc, id => Assert.Contains(id, kit));
        Assert.Equal(6, vc.Count(id => id.EndsWith(".x86", StringComparison.Ordinal)));

        Assert.Contains("Microsoft.DirectX", kit);
        Assert.True(AppCatalog.Find("Microsoft.DirectX")!.Has(AppFlags.ForceExe));
        Assert.Contains("Microsoft.DotNet.DesktopRuntime.10", kit);
        // End-of-life (or ending) .NET runtimes are installed one by one, only when a program asks.
        foreach (var old in new[] { "Microsoft.DotNet.DesktopRuntime.9", "Microsoft.DotNet.DesktopRuntime.8", "Microsoft.DotNet.DesktopRuntime.6" })
            Assert.DoesNotContain(old, kit);
        // Needed by this PC and already there: counted as installed, never re-installed.
        Assert.Contains("Microsoft.EdgeWebView2Runtime", kit);
        Assert.DoesNotContain(AppAdvisor.GamingServicesId, kit);
        Assert.Equal(15, kit.Count);
        Assert.All(kit, id => Assert.Equal(AppCategory.Runtimes, AppCatalog.Find(id)!.Category));
        Assert.All(kit, id => Assert.Equal(AppFlags.None, AppCatalog.Find(id)!.Flags & AppAdvisor.NeverSuggested));
    }

    [Fact]
    public void Regional_variants_start_with_the_default_id()
    {
        var regional = AppCatalog.All.Where(e => e.Has(AppFlags.Region)).ToList();
        Assert.Equal(2, regional.Count);
        foreach (var e in regional)
        {
            Assert.NotEmpty(e.Variants);
            Assert.Equal(e.Id, e.Variants[0].Id);
            Assert.All(e.Variants, v => Assert.EndsWith("." + v.Label, v.Id));
        }
        Assert.Equal("RiotGames.LeagueOfLegends.EUW", AppCatalog.Find("RiotGames.LeagueOfLegends.RU")!.Id);
    }

    [Fact]
    public void Elevation_rules_follow_the_research()
    {
        var spotify = AppCatalog.Find("Spotify.Spotify")!;
        Assert.True(spotify.Has(AppFlags.NoElevation));
        Assert.True(spotify.RunsUnelevated);

        Assert.All(AppCatalog.All.Where(e => e.Source == PackageSource.MsStore), e => Assert.True(e.RunsUnelevated, e.Id));
        Assert.All(AppCatalog.All.Where(e => e.Has(AppFlags.NoElevation)), e => Assert.True(e.RunsUnelevated, e.Id));
        Assert.False(AppCatalog.Find("Valve.Steam")!.RunsUnelevated);
        Assert.False(AppCatalog.Find("Microsoft.VCRedist.2015+.x64")!.RunsUnelevated);
    }

    [Fact]
    public void Special_entries_are_never_installed_or_upgraded_blindly()
    {
        var edge = AppCatalog.Find("Microsoft.Edge")!;
        Assert.True(edge.SystemComponent);
        Assert.False(edge.CanInstall);
        Assert.False(edge.CanUpgrade);

        var roblox = AppCatalog.Find("Roblox.Roblox")!;
        Assert.True(roblox.WebsiteOnly);
        Assert.False(roblox.CanInstall);

        Assert.False(AppCatalog.Find("Discord.Discord")!.CanUpgrade);
        Assert.False(AppCatalog.Find("Microsoft.EdgeWebView2Runtime")!.CanUpgrade);
        Assert.False(AppCatalog.Find("Microsoft.DirectX")!.CanUpgrade);
        Assert.True(AppCatalog.Find("Valve.Steam")!.CanUpgrade);
    }

    [Fact]
    public void Remote_access_tools_are_flagged_and_warned_about()
    {
        foreach (var id in new[] { "AnyDesk.AnyDesk", "TeamViewer.TeamViewer" })
        {
            var e = AppCatalog.Find(id)!;
            Assert.True(e.Has(AppFlags.RemoteAccess));
            var tag = Assert.Single(AppTexts.BuildTags(e), t => t.Text == "УДАЛЁННЫЙ ДОСТУП");
            Assert.Equal(TagKind.Hatch, tag.Kind);
            Assert.False(string.IsNullOrWhiteSpace(tag.ToolTip));
            Assert.True(AppTexts.IsRisky(e));
            Assert.Contains(AppTexts.Warnings([new AppItemViewModel(e)]), w => w.Contains("удалённого доступа", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Store_entries_get_a_store_tag_and_category_tag_comes_first()
    {
        foreach (var e in AppCatalog.All)
        {
            var tags = AppTexts.BuildTags(e);
            Assert.Equal(AppCatalog.Info(e.Category).Tag, tags[0].Text);
            Assert.Equal(e.Source == PackageSource.MsStore, tags.Any(t => t.Text == "MS STORE"));
            Assert.All(tags, t => Assert.Equal(t.Text, t.Text.ToUpperInvariant()));
        }
    }

    [Fact]
    public void Windows10_only_runtime_is_hidden_on_windows11_22h2()
    {
        Assert.Contains(AppCatalog.ForBuild(19045), e => e.Id == "Microsoft.DotNet.Framework.Runtime");
        Assert.DoesNotContain(AppCatalog.ForBuild(22621), e => e.Id == "Microsoft.DotNet.Framework.Runtime");
        Assert.Equal(AppCatalog.All.Count - 1, AppCatalog.ForBuild(26100).Count());
    }

    [Theory]
    [InlineData(14393, false)] // LTSB 2016: NDP481 refuses to install
    [InlineData(17763, false)] // LTSC 2019
    [InlineData(19041, false)] // 2004
    [InlineData(19042, true)]  // 20H2 — the oldest build 4.8.1 supports
    [InlineData(19045, true)]
    [InlineData(22000, true)]  // Windows 11 21H2
    [InlineData(22621, false)] // 22H2+ ships it
    public void Net_framework_481_is_offered_only_where_it_installs(int build, bool offered) =>
        Assert.Equal(offered, AppCatalog.ForBuild(build).Any(e => e.Id == "Microsoft.DotNet.Framework.Runtime"));

    // ─────────────────────────── winget arguments ───────────────────────────

    [Fact]
    public void Install_args_are_exact_silent_and_never_dangerous()
    {
        var args = WingetClient.BuildInstallArgs(new WingetPackageRequest("Valve.Steam"));
        Assert.Equal(
            ["install", "--id", "Valve.Steam", "--exact", "--source", "winget", "--accept-package-agreements", "--silent",
             "--accept-source-agreements", "--disable-interactivity"],
            args);

        foreach (var e in AppCatalog.All)
        {
            var all = WingetClient.BuildInstallArgs(e.CreateRequest(e.Id)).Concat(WingetClient.BuildUpgradeArgs(e.CreateRequest(e.Id))).ToList();
            Assert.DoesNotContain("--force", all);
            Assert.DoesNotContain("--ignore-security-hash", all);
            Assert.DoesNotContain("--allow-reboot", all);
            Assert.DoesNotContain("--scope", all);
            Assert.DoesNotContain("--include-unknown", all);
        }
    }

    [Fact]
    public void Store_installs_have_no_silent_switch()
    {
        var args = WingetClient.BuildInstallArgs(AppCatalog.Find("9MV0B5HZVK9Z")!.CreateRequest("9MV0B5HZVK9Z"));
        Assert.Contains("msstore", args);
        Assert.DoesNotContain("--silent", args);
    }

    [Fact]
    public void DirectX_is_installed_with_the_exe_installer()
    {
        var dx = AppCatalog.Find("Microsoft.DirectX")!;
        var args = WingetClient.BuildInstallArgs(dx.CreateRequest(dx.Id)).ToList();
        var i = args.IndexOf("--installer-type");
        Assert.True(i > 0);
        Assert.Equal("exe", args[i + 1]);
        Assert.DoesNotContain("--installer-type", WingetClient.BuildInstallArgs(new WingetPackageRequest("Valve.Steam")));
    }

    [Fact]
    public void Upgrade_args_target_one_exact_package()
    {
        var args = WingetClient.BuildUpgradeArgs(new WingetPackageRequest("7zip.7zip"));
        Assert.Equal("upgrade", args[0]);
        Assert.Contains("--exact", args);
        Assert.DoesNotContain("--all", args);
    }

    [Theory]
    [InlineData("Valve.Steam", true)]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("Notepad++.Notepad++", true)]
    [InlineData("9MV0B5HZVK9Z", true)]
    [InlineData("Zen-Team.Zen-Browser", true)]
    [InlineData("", false)]
    [InlineData(" Valve.Steam", false)]
    [InlineData("Valve.Steam --force", false)]
    [InlineData("a&calc", false)]
    [InlineData("x\"y", false)]
    [InlineData("-id", false)]
    [InlineData(null, false)]
    public void Package_id_validation(string? id, bool valid) => Assert.Equal(valid, WingetClient.IsValidPackageId(id));

    [Fact]
    public async Task Invalid_requests_throw_on_build_and_fail_softly_on_run()
    {
        Assert.Throws<ArgumentException>(() => WingetClient.BuildInstallArgs(new WingetPackageRequest("bad id")));
        Assert.Throws<ArgumentException>(() => WingetClient.BuildInstallArgs(new WingetPackageRequest("Valve.Steam", "evil")));
        Assert.Throws<ArgumentException>(() => WingetClient.BuildInstallArgs(new WingetPackageRequest("Valve.Steam") { Architecture = "x99" }));
        Assert.Throws<ArgumentException>(() => WingetClient.BuildInstallArgs(new WingetPackageRequest("Valve.Steam") { InstallerType = "bat" }));

        // Rejected before any process is started.
        var r = await WingetClient.InstallPackageAsync(new WingetPackageRequest("bad id & calc"));
        Assert.False(r.Success);
    }

    [Theory]
    [InlineData("1.11.510", false)]
    [InlineData("v1.29.380", false)]
    [InlineData("1.6.2771", false)]
    [InlineData("1.5.1881", true)]
    [InlineData("v1.2.10271", true)]
    [InlineData("1.12.0-preview", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("нет", false)]
    public void Old_winget_clients_are_detected(string? version, bool outdated) => Assert.Equal(outdated, WingetClient.IsClientOutdated(version));

    // ─────────────────────────── export parsing ───────────────────────────

    [Fact]
    public void Export_json_is_parsed_into_id_version_map()
    {
        const string json = """
        {
          "$schema": "https://aka.ms/winget-packages.schema.2.0.json",
          "CreationDate": "2026-10-26T20:00:00.000-00:00",
          "Sources": [
            { "SourceDetails": { "Name": "winget", "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe", "Argument": "https://cdn.winget.microsoft.com/cache", "Type": "Microsoft.PreIndexed.Package" },
              "Packages": [
                { "PackageIdentifier": "Valve.Steam", "Version": "2.10.91.91" },
                { "PackageIdentifier": "Microsoft.WindowsAppRuntime.1.8", "Version": "> 1.8.10" },
                { "PackageIdentifier": "Microsoft.VCRedist.2015+.x64" }
              ] },
            { "SourceDetails": { "Name": "msstore" }, "Packages": [ { "PackageIdentifier": "9MV0B5HZVK9Z", "Version": "Unknown" } ] }
          ],
          "WinGetVersion": "1.29.380"
        }
        """;
        var map = WingetClient.ParseExport(json);
        Assert.Equal(4, map.Count);
        Assert.Equal("2.10.91.91", map["valve.steam"]);
        Assert.Equal("", map["Microsoft.VCRedist.2015+.x64"]);
        Assert.Equal("1.8.10", InstalledDetector.CleanVersion(map["Microsoft.WindowsAppRuntime.1.8"]));
        Assert.Equal("", InstalledDetector.CleanVersion(map["9MV0B5HZVK9Z"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"Sources\": 5}")]
    [InlineData("{\"Sources\": [ { \"Packages\": [ 1, \"x\", { \"Version\": \"1\" } ] } ]}")]
    public void Malformed_export_gives_an_empty_map(string json) => Assert.Empty(WingetClient.ParseExport(json));

    [Theory]
    [InlineData("v14.36.32532.00", "14.36.32532.00")]
    [InlineData("> 1.8.10", "1.8.10")]
    [InlineData("< 2.0", "2.0")]
    [InlineData("Unknown", "")]
    [InlineData(null, "")]
    [InlineData("  10.0.14 ", "10.0.14")]
    public void Versions_are_cleaned_for_display(string? raw, string expected) => Assert.Equal(expected, InstalledDetector.CleanVersion(raw));

    // ─────────────────────────── exit codes ───────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(unchecked((int)0x8A15002B))]
    [InlineData(unchecked((int)0x8A150061))]
    [InlineData(unchecked((int)0x8A15010D))]
    [InlineData(unchecked((int)0x8A15010E))]
    [InlineData(unchecked((int)0x8A150014))]
    [InlineData(unchecked((int)0x8A150016))]
    [InlineData(unchecked((int)0x8A150010))]
    [InlineData(unchecked((int)0x8A150056))]
    [InlineData(unchecked((int)0x8A150019))]
    [InlineData(unchecked((int)0x8A150006))]
    [InlineData(unchecked((int)0x8A150008))]
    [InlineData(unchecked((int)0x8A150107))]
    [InlineData(unchecked((int)0x8A150011))]
    [InlineData(unchecked((int)0x8A150041))]
    [InlineData(unchecked((int)0x8A150046))]
    [InlineData(unchecked((int)0x8A150045))]
    [InlineData(unchecked((int)0x8A150007))]
    [InlineData(unchecked((int)0x8A15004F))]
    [InlineData(unchecked((int)0x8A150050))]
    [InlineData(unchecked((int)0x8A150068))]
    [InlineData(unchecked((int)0x8A150101))]
    [InlineData(unchecked((int)0x8A150103))]
    [InlineData(unchecked((int)0x8A150111))]
    [InlineData(unchecked((int)0x8A150102))]
    [InlineData(unchecked((int)0x8A150109))]
    [InlineData(unchecked((int)0x8A15010A))]
    [InlineData(unchecked((int)0x8A15010B))]
    [InlineData(unchecked((int)0x8A15010C))]
    [InlineData(unchecked((int)0x8A15010F))]
    [InlineData(unchecked((int)0x8A15003A))]
    [InlineData(unchecked((int)0x8A15001B))]
    [InlineData(unchecked((int)0x8A15001C))]
    [InlineData(unchecked((int)0x8A150083))]
    [InlineData(unchecked((int)0x8A150105))]
    [InlineData(unchecked((int)0x8A150113))]
    [InlineData(unchecked((int)0x8A150001))]
    [InlineData(unchecked((int)0x8A15005F))]
    [InlineData(unchecked((int)0x80190194))]
    [InlineData(unchecked((int)0x80190193))]
    [InlineData(unchecked((int)0x8A150049))]
    [InlineData(unchecked((int)0x8A150052))]
    [InlineData(unchecked((int)0x8A15005E))]
    [InlineData(unchecked((int)0x8A150060))]
    [InlineData(unchecked((int)0x8A150104))]
    [InlineData(unchecked((int)0x8A150106))]
    [InlineData(unchecked((int)0x8A150108))]
    [InlineData(unchecked((int)0x8A150110))]
    [InlineData(unchecked((int)0x8A150112))]
    [InlineData(unchecked((int)0x8A150114))]
    [InlineData(unchecked((int)0x8A150115))]
    [InlineData(unchecked((int)0x8A15004B))]
    [InlineData(unchecked((int)0x8A150002))]
    [InlineData(unchecked((int)0x8A150005))]
    [InlineData(-1)]
    public void Every_researched_exit_code_has_a_russian_description(int code)
    {
        var text = WingetClient.Describe(code);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("0x", text);
        Assert.Matches("[а-яё]", text);
    }

    [Fact]
    public void Unknown_exit_code_shows_the_hresult()
    {
        Assert.Equal("код -1978335000 (0x8A1500E8)", WingetClient.Describe(unchecked((int)0x8A1500E8)));
    }

    [Fact]
    public void Exit_code_classes()
    {
        Assert.True(WingetClient.IsSkipped(WingetClient.PackageIsPinned));
        Assert.True(WingetClient.IsSkipped(WingetClient.UpgradeVersionUnknown));
        Assert.False(WingetClient.IsSkipped(0));
        Assert.True(WingetClient.IsRebootRequired(WingetClient.RebootRequiredToFinish));
        Assert.True(WingetClient.IsRebootRequired(WingetClient.RebootInitiated));
        Assert.False(WingetClient.IsRebootRequired(WingetClient.UpdateNotApplicable));
    }

    [Theory]
    [InlineData(0, QueueOperation.Install, QueueState.Done, false)]
    [InlineData(unchecked((int)0x8A15002B), QueueOperation.Upgrade, QueueState.Done, false)]
    [InlineData(unchecked((int)0x8A150061), QueueOperation.Install, QueueState.Done, false)]
    [InlineData(unchecked((int)0x8A150109), QueueOperation.Install, QueueState.Done, true)]
    [InlineData(unchecked((int)0x8A15010A), QueueOperation.Install, QueueState.Failed, true)]
    [InlineData(unchecked((int)0x8A150068), QueueOperation.Upgrade, QueueState.Skipped, false)]
    [InlineData(unchecked((int)0x8A15010E), QueueOperation.Upgrade, QueueState.Skipped, false)]
    [InlineData(unchecked((int)0x8A150011), QueueOperation.Install, QueueState.Failed, false)]
    [InlineData(unchecked((int)0x8A150056), QueueOperation.Install, QueueState.Failed, false)]
    [InlineData(unchecked((int)0x8A15005F), QueueOperation.Install, QueueState.Failed, false)]
    [InlineData(unchecked((int)0x8A150014), QueueOperation.Install, QueueState.Failed, false)]
    [InlineData(unchecked((int)0x8A150014), QueueOperation.Upgrade, QueueState.Skipped, false)] // installed some other way (SDK runtime)
    public void Queue_results_are_classified(int code, QueueOperation op, QueueState state, bool reboot)
    {
        var (s, text, r) = AppTexts.DescribeResult(code, op);
        Assert.Equal(state, s);
        Assert.Equal(reboot, r);
        Assert.False(string.IsNullOrWhiteSpace(text));
        if (state == QueueState.Failed) Assert.StartsWith("ОШИБКА: ", text);
    }

    // ─────────────────────────── de-elevated runner ───────────────────────────

    [Fact]
    public void Unelevated_command_line_redirects_output_and_exit_code()
    {
        var line = UnelevatedWinget.BuildCommandLine(@"C:\Users\Test User\AppData\Local\Microsoft\WindowsApps\winget.exe",
            ["install", "--id", "Spotify.Spotify", "--exact"], "MONOLITH-winget-ab12.log", "MONOLITH-winget-ab12.exit");
        Assert.Equal(
            "/d /v:on /s /c \"\"C:\\Users\\Test User\\AppData\\Local\\Microsoft\\WindowsApps\\winget.exe\" install --id Spotify.Spotify --exact " +
            ">\"%TEMP%\\MONOLITH-winget-ab12.log\" 2>&1 & (echo !ERRORLEVEL!)>\"%TEMP%\\MONOLITH-winget-ab12.exit\"\"",
            line);
    }

    [Theory]
    [InlineData("Spotify.Spotify & calc")]
    [InlineData("a|b")]
    [InlineData("x>y")]
    [InlineData("%PATH%")]
    [InlineData("!x!")]
    [InlineData("a\"b")]
    [InlineData("a b")]
    [InlineData("")]
    public void Unelevated_command_line_rejects_shell_metacharacters(string arg)
    {
        Assert.Throws<ArgumentException>(() => UnelevatedWinget.BuildCommandLine("winget.exe", ["install", arg], "a.log", "a.exit"));
    }

    [Theory]
    [InlineData("C:\\a\"b\\winget.exe")]
    [InlineData("C:\\%TEMP%\\winget.exe")]
    [InlineData("C:\\a!b\\winget.exe")]
    [InlineData("")]
    public void Unelevated_command_line_rejects_unsafe_winget_paths(string exe)
    {
        Assert.Throws<ArgumentException>(() => UnelevatedWinget.BuildCommandLine(exe, ["--version"], "a.log", "a.exit"));
    }

    [Fact]
    public void All_catalog_arguments_can_go_through_the_unelevated_runner()
    {
        foreach (var e in AppCatalog.All.Where(e => e.RunsUnelevated))
            foreach (var id in e.AllIds)
            {
                var request = e.CreateRequest(id);
                foreach (var args in new[]
                {
                    WingetClient.BuildInstallArgs(request, keepLocationTemplate: true),
                    WingetClient.BuildUpgradeArgs(request, keepLocationTemplate: true),
                    // the one retry after 0x8A15005F
                    WingetClient.BuildInstallArgs(request with { Location = e.DefaultLocationTemplate }, keepLocationTemplate: true),
                })
                {
                    var line = UnelevatedWinget.BuildCommandLine("winget.exe", args, "a.log", "a.exit");
                    Assert.Contains(id, line);
                }
            }
    }

    [Theory]
    [InlineData("0\r\n", 0)]
    [InlineData("-1978335189 \r\n", -1978335189)]
    [InlineData("  -1978334967", -1978334967)]
    public void Exit_code_file_is_parsed(string text, int expected)
    {
        Assert.True(UnelevatedWinget.TryParseExitCode(text, out var code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ECHO is on.")]
    [InlineData("!ERRORLEVEL!")]
    public void Garbage_exit_code_file_is_rejected(string text) => Assert.False(UnelevatedWinget.TryParseExitCode(text, out _));

    [Theory]
    [InlineData("  ██████▒▒▒▒  1 MB / 3 MB\r  ██████████  3 MB / 3 MB\r", "  ██████████  3 MB / 3 MB")]
    [InlineData("\u001b[2KНайдено Steam [Valve.Steam]\r", "Найдено Steam [Valve.Steam]")]
    [InlineData("-\b\\\b|\bУспешно установлено", "-\\|Успешно установлено")]
    [InlineData("a\0b", "ab")]
    public void Redirected_output_lines_are_cleaned(string raw, string expected) => Assert.Equal(expected, UnelevatedWinget.CleanLine(raw));

    // ─────────────────────────── search, layout, wording ───────────────────────────

    [Theory]
    [InlineData("Valve.Steam", "steam", true)]
    [InlineData("Valve.Steam", "VALVE", true)]
    [InlineData("Yandex.Browser", "яндекс", true)]
    [InlineData("AnyDesk.AnyDesk", "удаленный", true)]
    [InlineData("RiotGames.LeagueOfLegends.EUW", "leagueoflegends.ru", true)]
    [InlineData("Microsoft.VCRedist.2015+.x64", "библиотека", true)]
    [InlineData("Valve.Steam", "discord", false)]
    [InlineData("Valve.Steam", "   ", true)]
    public void Search_matches_name_id_description_and_category(string id, string query, bool expected) =>
        Assert.Equal(expected, AppTexts.Matches(AppCatalog.Find(id)!, query));

    [Theory]
    [InlineData(1096, 3)]
    [InlineData(700, 2)]
    [InlineData(330, 1)]
    [InlineData(100, 1)]
    [InlineData(3000, 4)]
    [InlineData(double.PositiveInfinity, 1)]
    public void Card_grid_columns(double width, int expected) => Assert.Equal(expected, CardGrid.ColumnsFor(width, 318, 12, 4));

    [Theory]
    [InlineData(1, "1 программа")]
    [InlineData(3, "3 программы")]
    [InlineData(11, "11 программ")]
    [InlineData(21, "21 программа")]
    [InlineData(123, "123 программы")]
    [InlineData(125, "125 программ")]
    public void Program_count_wording(int n, string expected) => Assert.Equal(expected, AppTexts.Programs(n));

    [Theory]
    [InlineData(1, "1 пакет")]
    [InlineData(2, "2 пакета")]
    [InlineData(15, "15 пакетов")]
    [InlineData(22, "22 пакета")]
    public void Package_count_wording(int n, string expected) => Assert.Equal(expected, AppTexts.Packages(n));

    [Fact]
    public void Queue_line_names_package_id_and_source()
    {
        var steam = AppCatalog.Find("Valve.Steam")!;
        Assert.Equal("Steam — Valve.Steam · winget · установка", AppTexts.QueueLine(steam, steam.Id, QueueOperation.Install));
        Assert.Equal("Steam — Valve.Steam · winget · обновление (сейчас 2.10)", AppTexts.QueueLine(steam, steam.Id, QueueOperation.Upgrade, "2.10"));
        var xbox = AppCatalog.Find("9MV0B5HZVK9Z")!;
        Assert.Contains("· msstore ·", AppTexts.QueueLine(xbox, xbox.Id, QueueOperation.Install));
    }

    [Fact]
    public void Card_action_follows_install_state()
    {
        var steam = new AppItemViewModel(AppCatalog.Find("Valve.Steam")!) { IsKnown = true, WingetReady = true };
        Assert.Equal(AppAction.Install, steam.Action);
        Assert.True(steam.CanRunAction);
        Assert.True(steam.CanSelect);
        steam.InstalledVersion = "2.10";
        Assert.Equal(AppAction.Upgrade, steam.Action);
        Assert.Equal("УСТАНОВЛЕНО 2.10", steam.InstalledText);
        steam.QueueBusy = true;
        Assert.False(steam.CanRunAction);

        var discord = new AppItemViewModel(AppCatalog.Find("Discord.Discord")!) { IsKnown = true, WingetReady = true, InstalledVersion = "" };
        Assert.Equal(AppAction.Installed, discord.Action);
        Assert.False(discord.CanRunAction);
        Assert.Equal("УСТАНОВЛЕНО", discord.InstalledText);

        var edge = new AppItemViewModel(AppCatalog.Find("Microsoft.Edge")!) { IsKnown = true, WingetReady = true };
        Assert.Equal(AppAction.SystemComponent, edge.Action);
        Assert.False(edge.CanSelect);

        var roblox = new AppItemViewModel(AppCatalog.Find("Roblox.Roblox")!) { IsKnown = true, WingetReady = false };
        Assert.Equal(AppAction.Website, roblox.Action);
        Assert.True(roblox.CanRunAction);
        Assert.False(roblox.CanSelect);
        Assert.False(roblox.ShowSiteButton);

        var unknown = new AppItemViewModel(AppCatalog.Find("Valve.Steam")!) { WingetReady = true };
        Assert.False(unknown.CanRunAction);
        Assert.Equal("ПРОВЕРКА…", unknown.DisplayTags[0].Text);
    }

    [Fact]
    public void Selecting_a_region_changes_the_package_id()
    {
        var lol = new AppItemViewModel(AppCatalog.Find("RiotGames.LeagueOfLegends.EUW")!);
        Assert.Equal("RiotGames.LeagueOfLegends.EUW", lol.PackageId);
        lol.SelectedVariant = lol.Variants.First(v => v.Label == "NA");
        Assert.Equal("RiotGames.LeagueOfLegends.NA", lol.PackageId);
        Assert.Equal("NA", lol.SelectedVariant!.ToString());
    }

    // ─────────────────────────── confirmation texts ───────────────────────────

    [Fact]
    public void Sha256_claim_is_made_only_for_winget_entries()
    {
        var winget = AppCatalog.All.First(e => e.Source == PackageSource.Winget);
        var store = AppCatalog.All.First(e => e.Source == PackageSource.MsStore);

        Assert.Equal(AppTexts.WingetVerification, AppTexts.VerificationText([winget]));
        Assert.Equal(AppTexts.StoreVerification, AppTexts.VerificationText([store]));
        var mixed = AppTexts.VerificationText([winget, store]);
        Assert.Contains("SHA256", mixed);
        Assert.Contains("Microsoft Store", mixed);
        Assert.DoesNotContain("SHA256", AppTexts.VerificationText([store]));
    }

    [Fact]
    public void License_text_mentions_store_terms_only_for_store_packages()
    {
        var winget = AppCatalog.All.First(e => e.Source == PackageSource.Winget);
        var store = AppCatalog.All.First(e => e.Source == PackageSource.MsStore);

        Assert.Contains("лицензии издателей", AppTexts.LicenseText([winget], "Установить"));
        Assert.DoesNotContain("Microsoft Store", AppTexts.LicenseText([winget], "Установить"));
        Assert.Contains("условия Microsoft Store", AppTexts.LicenseText([winget, store], "Обновить"));
        Assert.Contains("«Обновить»", AppTexts.LicenseText([store], "Обновить"));
    }
}
