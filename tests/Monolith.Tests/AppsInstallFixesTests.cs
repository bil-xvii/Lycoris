using System.IO;
using System.Text.RegularExpressions;
using Monolith.Core;
using Monolith.Modules.Apps;

namespace Monolith.Tests;

/// <summary>
/// App installs that failed on the owner's PC and in the manifest audit: install location (0x8A15005F), installer hash
/// mismatch (0x8A150011, never bypassed — signed vendor installer instead), outdated catalog data, the SDK-installed
/// .NET runtime that winget cannot upgrade, and honest queue texts. Nothing is installed or downloaded.
/// </summary>
public sealed class AppsInstallFixesTests
{
    private static readonly Regex Cyrillic = new("[А-Яа-яЁё]");

    private static CatalogEntry Entry(string id) => AppCatalog.Find(id) ?? throw new InvalidOperationException(id);

    // ─────────────────────────── install location (0x8A15005F) ───────────────────────────

    [Fact]
    public void Battle_net_is_installed_into_its_standard_folder()
    {
        var bnet = Entry("Blizzard.BattleNet");
        Assert.Equal(@"%ProgramFiles(x86)%\Battle.net", bnet.InstallLocation);

        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net");
        foreach (var args in new[] { WingetClient.BuildInstallArgs(bnet.CreateRequest(bnet.Id)), WingetClient.BuildUpgradeArgs(bnet.CreateRequest(bnet.Id)) })
        {
            var i = args.ToList().IndexOf("--location");
            Assert.True(i > 0);
            Assert.Equal(expected, args[i + 1]);
            Assert.Contains("--disable-interactivity", args);
            Assert.DoesNotContain("--ignore-security-hash", args);
            Assert.DoesNotContain("--force", args);
        }
    }

    [Fact]
    public void Packages_without_a_catalog_location_get_no_location_switch()
    {
        Assert.DoesNotContain("--location", WingetClient.BuildInstallArgs(Entry("Valve.Steam").CreateRequest("Valve.Steam")));
        Assert.All(AppCatalog.All.Where(e => e.InstallLocation is not null), e => Assert.True(WingetClient.IsValidLocationTemplate(e.InstallLocation), e.Id));
    }

    [Fact]
    public void Install_location_required_has_its_own_code_and_text()
    {
        Assert.Equal(unchecked((int)0x8A15005F), WingetClient.InstallLocationRequired);
        Assert.Equal("нужно указать папку установки", WingetClient.Describe(WingetClient.InstallLocationRequired));
    }

    [Theory]
    [InlineData(@"%ProgramFiles%\Steam")]
    [InlineData(@"%ProgramFiles(x86)%\Battle.net")]
    [InlineData(@"%programfiles(X86)%\Battle.net")]
    [InlineData(@"%LOCALAPPDATA%\Programs\osu(lazer)")]
    [InlineData(@"%LOCALAPPDATA%\Programs\Notepad++")]
    [InlineData(@"%ProgramFiles%\Vendor\Product Name\Sub")]
    public void Valid_location_templates(string template)
    {
        Assert.True(WingetClient.IsValidLocationTemplate(template));
        var path = WingetClient.ExpandLocation(template);
        Assert.NotNull(path);
        Assert.True(Path.IsPathFullyQualified(path!));
        Assert.DoesNotContain("%", path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Program Files\Steam")]            // absolute paths are never passed — only known roots
    [InlineData(@"\\server\share\Steam")]
    [InlineData(@"%TEMP%\Steam")]
    [InlineData(@"%APPDATA%\Steam")]
    [InlineData(@"%ProgramFiles%")]
    [InlineData(@"%ProgramFiles%\")]
    [InlineData(@"%ProgramFiles%\..\Windows")]
    [InlineData(@"%ProgramFiles%\a\..\..\Windows")]
    [InlineData(@"%ProgramFiles%\Steam\")]
    [InlineData(@"%ProgramFiles%\Steam.")]
    [InlineData(@"%ProgramFiles%\Steam ")]
    [InlineData(@"%ProgramFiles%\a""b")]
    [InlineData(@"%ProgramFiles%\a&calc")]
    [InlineData(@"%ProgramFiles%\a|b")]
    [InlineData(@"%ProgramFiles%\a%PATH%")]
    [InlineData(@"%ProgramFiles%\a!x!")]
    [InlineData(@"%ProgramFiles%\a^b")]
    [InlineData(@"%ProgramFiles%\a/b")]
    [InlineData(@"%ProgramFiles%\Яндекс")]
    [InlineData(@"%ProgramFiles%\a\b\c\d\e")]          // at most four folder levels
    public void Unsafe_location_templates_are_refused(string? template)
    {
        Assert.False(WingetClient.IsValidLocationTemplate(template));
        Assert.Null(WingetClient.ExpandLocation(template));
    }

    [Fact]
    public async Task Invalid_location_throws_on_build_and_fails_softly_on_run()
    {
        var bad = new WingetPackageRequest("Valve.Steam") { Location = @"C:\Windows\System32" };
        Assert.Throws<ArgumentException>(() => WingetClient.BuildInstallArgs(bad));
        Assert.Throws<ArgumentException>(() => WingetClient.BuildUpgradeArgs(bad, keepLocationTemplate: true));
        var r = await WingetClient.InstallPackageAsync(bad);
        Assert.False(r.Success);
    }

    [Fact]
    public void Unelevated_runner_quotes_the_location_template_and_lets_the_users_cmd_expand_it()
    {
        var args = WingetClient.BuildInstallArgs(new WingetPackageRequest("Spotify.Spotify") { Location = @"%LOCALAPPDATA%\Programs\Spotify" },
            keepLocationTemplate: true);
        Assert.Equal(@"%LOCALAPPDATA%\Programs\Spotify", args[args.ToList().IndexOf("--location") + 1]);
        var line = UnelevatedWinget.BuildCommandLine("winget.exe", args, "a.log", "a.exit");
        Assert.Contains(@"--location ""%LOCALAPPDATA%\Programs\Spotify"" ", line);
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\Spotify")]
    [InlineData(@"%PATH%")]
    [InlineData(@"%LOCALAPPDATA%\x"" & calc & """)]
    [InlineData(@"%LOCALAPPDATA%\!x!")]
    public void Unelevated_runner_refuses_anything_else_after_location(string value) =>
        Assert.Throws<ArgumentException>(() => UnelevatedWinget.BuildCommandLine("winget.exe", ["install", "--location", value], "a.log", "a.exit"));

    [Fact]
    public void Retry_folder_follows_the_install_scope()
    {
        Assert.Equal(@"%ProgramFiles%\Steam", Entry("Valve.Steam").DefaultLocationTemplate);
        Assert.Equal(@"%LOCALAPPDATA%\Programs\Spotify", Entry("Spotify.Spotify").DefaultLocationTemplate);
        Assert.Equal(@"%LOCALAPPDATA%\Programs\osu(lazer)", Entry("ppy.osu").DefaultLocationTemplate);
        Assert.Equal(@"%LOCALAPPDATA%\Programs\Yandex.Browser", Entry("Yandex.Browser").DefaultLocationTemplate); // Cyrillic name → id
        foreach (var e in AppCatalog.All)
        {
            Assert.True(WingetClient.IsValidLocationTemplate(e.DefaultLocationTemplate), $"{e.Id}: {e.DefaultLocationTemplate}");
            Assert.NotNull(WingetClient.ExpandLocation(e.DefaultLocationTemplate));
        }
    }

    // ─────────────────────────── hash mismatch (0x8A150011): never bypassed ───────────────────────────

    [Fact]
    public void Hash_mismatch_is_explained_honestly_and_never_bypassed()
    {
        var ghub = Entry("Logitech.GHUB");
        var (state, text, _) = AppTexts.DescribeResult(WingetClient.InstallerHashMismatch, QueueOperation.Install);
        Assert.Equal(QueueState.Failed, state);
        Assert.Equal("ОШИБКА: производитель обновил установщик, а каталог winget ещё нет — установка заблокирована ради безопасности", text);

        var detail = AppTexts.ResultDetail(WingetClient.InstallerHashMismatch, QueueOperation.Install, ghub);
        Assert.Contains("download01.logi.com", detail);
        Assert.Contains("СКАЧАТЬ У ПРОИЗВОДИТЕЛЯ", detail);
        Assert.Contains("подменён", detail); // honest: a mismatch can also mean a tampered file
        Assert.Equal(QueueFallback.VendorDownload, AppTexts.FallbackFor(WingetClient.InstallerHashMismatch, QueueOperation.Install, ghub));

        // Without vendor data: the official site.
        var steam = Entry("Valve.Steam");
        Assert.Equal(QueueFallback.Website, AppTexts.FallbackFor(WingetClient.InstallerHashMismatch, QueueOperation.Upgrade, steam));
        Assert.Contains("ОТКРЫТЬ САЙТ", AppTexts.ResultDetail(WingetClient.InstallerHashMismatch, QueueOperation.Upgrade, steam));

        foreach (var e in AppCatalog.All)
            foreach (var id in e.AllIds)
            {
                var all = WingetClient.BuildInstallArgs(e.CreateRequest(id)).Concat(WingetClient.BuildUpgradeArgs(e.CreateRequest(id)));
                Assert.DoesNotContain("--ignore-security-hash", all);
            }
    }

    [Theory]
    [InlineData("Logitech.GHUB", "download01.logi.com", "lghub_installer.exe")]
    [InlineData("Ubisoft.Connect", "static3.cdn.ubi.com", "UbisoftConnectInstaller.exe")]
    [InlineData("Resplendence.LatencyMon", "www.resplendence.com", "LatencyMon.exe")]
    public void Unversioned_link_packages_have_a_verified_vendor_download(string id, string host, string file)
    {
        var e = Entry(id);
        Assert.NotNull(e.VendorInstaller);
        var r = e.VendorInstaller!.ToRequest(e.Name);
        Assert.NotNull(r);
        Assert.Equal("https", r!.Url.Scheme);
        Assert.Equal(host, r.Url.Host);
        Assert.Equal(file, r.FileName);
        Assert.Equal(VendorDownloads.AppsDir, r.Folder);
        Assert.NotEmpty(r.Signers);
        Assert.Null(VendorDownloads.Validate(r));
        Assert.Contains(host, AppTexts.VendorDownloadToolTip(e));
    }

    [Fact]
    public void Every_catalog_vendor_installer_is_acceptable_to_the_shared_mechanism()
    {
        foreach (var e in AppCatalog.All.Where(e => e.VendorInstaller is not null))
        {
            var r = e.VendorInstaller!.ToRequest(e.Name);
            Assert.NotNull(r);
            Assert.All(r!.Signers, s => Assert.False(string.IsNullOrWhiteSpace(s)));
            var plan = string.Join("\n", AppTexts.VendorPlan(r, restorePoint: true));
            Assert.Contains(r.Url.Host, plan);
            Assert.Contains(r.FileName, plan);
            Assert.Contains("цифровая подпись", plan);
            Assert.Contains("от вашего имени", plan);
            Assert.Contains("точка восстановления", plan);
            Assert.DoesNotContain("будет создана", string.Join("\n", AppTexts.VendorPlan(r, restorePoint: false)));
        }
    }

    [Theory]
    [InlineData("http://download01.logi.com/web/ftp/pub/techsupport/gaming/lghub_installer.exe")]  // no TLS
    [InlineData("https://download01.logi.com.evil.example/lghub_installer.exe")]
    [InlineData("https://download01.logi.com:8443/lghub_installer.exe")]
    [InlineData("https://download01.logi.com/lghub_installer.zip")]
    [InlineData("https://user:pw@download01.logi.com/lghub_installer.exe")]
    public void Vendor_installer_links_outside_the_rules_are_refused(string url)
    {
        var v = new VendorInstaller(url, ["Logitech"]) { Hosts = ["download01.logi.com"] };
        Assert.Null(v.ToRequest("Logitech G HUB"));
    }

    [Fact]
    public void Download_requests_must_target_the_admin_only_folder_and_name_a_signer()
    {
        var url = new Uri("https://download01.logi.com/web/ftp/pub/techsupport/gaming/lghub_installer.exe");
        var ok = new VendorDownloadRequest(url, "lghub_installer.exe", ["download01.logi.com"], ["Logitech"], VendorDownloads.AppsDir);
        Assert.Null(VendorDownloads.Validate(ok));
        Assert.Null(VendorDownloads.Validate(ok with { Folder = VendorDownloads.RootDir }));

        Assert.NotNull(VendorDownloads.Validate(ok with { Folder = Path.GetTempPath() }));
        Assert.NotNull(VendorDownloads.Validate(ok with { Folder = Path.Combine(VendorDownloads.AppsDir, "deeper") }));
        Assert.NotNull(VendorDownloads.Validate(ok with { Folder = Path.Combine(VendorDownloads.AppsDir, "..", "..", "..") }));
        Assert.NotNull(VendorDownloads.Validate(ok with { Signers = [] }));
        Assert.NotNull(VendorDownloads.Validate(ok with { Signers = [" "] }));
        Assert.NotNull(VendorDownloads.Validate(ok with { FileName = @"..\..\evil.exe" }));
        Assert.NotNull(VendorDownloads.Validate(ok with { AllowedHosts = ["example.com"] }));
    }

    [Fact]
    public async Task Vendor_download_refuses_a_bad_request_before_touching_the_network()
    {
        var evil = new VendorDownloadRequest(new Uri("https://evil.example/lghub_installer.exe"), "lghub_installer.exe",
            ["download01.logi.com"], ["Logitech"], VendorDownloads.AppsDir);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => VendorDownloads.DownloadAsync(evil, null, CancellationToken.None));
        Assert.Contains("официальный сервер", ex.Message);
    }

    [Fact]
    public void Installers_are_started_only_from_directly_inside_the_folder()
    {
        var dir = VendorDownloads.AppsDir;
        Assert.True(VendorDownloads.IsDirectlyInFolder(Path.Combine(dir, "lghub_installer.exe"), dir));
        Assert.False(VendorDownloads.IsDirectlyInFolder(Path.Combine(dir, "sub", "lghub_installer.exe"), dir));
        Assert.False(VendorDownloads.IsDirectlyInFolder(Path.Combine(dir, "..", "lghub_installer.exe"), dir));
        Assert.False(VendorDownloads.IsDirectlyInFolder(@"C:\Windows\notepad.exe", dir));
        Assert.False(VendorDownloads.IsDirectlyInFolder(null, dir));
    }

    private static string SigJson(string status, string org) =>
        System.Text.Json.JsonSerializer.Serialize(new { Status = status, Subject = $"CN={org}, O={org}", Names = $"C=US\r\nO={org}\r\nCN={org}" });

    [Theory]
    [InlineData("Logitech Inc", "Logitech.GHUB", true)]
    [InlineData("Logitech, Inc.", "Logitech.GHUB", true)]
    [InlineData("Logitech Europe S.A.", "Logitech.GHUB", true)]
    [InlineData("Logitech Fans Club", "Logitech.GHUB", false)]
    [InlineData("Not Logitech Inc", "Logitech.GHUB", false)]
    [InlineData("UBISOFT ENTERTAINMENT INC.", "Ubisoft.Connect", true)]
    [InlineData("Ubisoft Entertainment Sweden AB", "Ubisoft.Connect", true)]
    [InlineData("Ubisoft Entertainment SA", "Ubisoft.Connect", true)]
    [InlineData("Ubisoft Entertainment Evil AB", "Ubisoft.Connect", false)]
    [InlineData("SOFTREV LABS S.R.L.S.", "Resplendence.LatencyMon", true)]
    [InlineData("Resplendence Fake Ltd", "Resplendence.LatencyMon", false)]
    public void Vendor_signers_match_exactly(string org, string id, bool ok)
    {
        var signers = Entry(id).VendorInstaller!.Signers;
        Assert.Equal(ok, VendorDownloads.EvaluateSignature(SigJson("Valid", org), signers).Ok);
        Assert.False(VendorDownloads.EvaluateSignature(SigJson("HashMismatch", org), signers).Ok);
        Assert.False(VendorDownloads.EvaluateSignature(SigJson("NotSigned", org), signers).Ok);
    }

    [Fact]
    public void Signature_mismatch_names_every_accepted_signer()
    {
        var (ok, text) = VendorDownloads.EvaluateSignature(SigJson("Valid", "Evil Ltd"), ["Ubisoft Entertainment", "Ubisoft Entertainment Sweden AB"]);
        Assert.False(ok);
        Assert.Contains("«Ubisoft Entertainment» или «Ubisoft Entertainment Sweden AB»", text);
    }

    [Fact]
    public void Driver_downloads_still_use_their_own_hosts_and_the_downloads_root()
    {
        Assert.Equal(VendorDownloads.RootDir, Monolith.Modules.Drivers.DriverDownloads.DownloadsDir);
        Assert.False(Monolith.Modules.Drivers.DriverDownloads.IsAllowedDownload(new Uri("https://download01.logi.com/web/lghub_installer.exe")));
        Assert.False(VendorDownloads.IsAllowedUrl(new Uri("https://us.download.nvidia.com/Windows/621.01/x-dch.exe"),
            Entry("Logitech.GHUB").VendorInstaller!.AllowedHosts));
    }

    // ─────────────────────────── queue rows: what happened and what to do ───────────────────────────

    public static TheoryData<int> AllKnownCodes() => new()
    {
        0x0, unchecked((int)0x8A15002B), unchecked((int)0x8A150061), unchecked((int)0x8A15010D), unchecked((int)0x8A15010E),
        unchecked((int)0x8A150014), unchecked((int)0x8A150016), unchecked((int)0x8A150010), unchecked((int)0x8A150056),
        unchecked((int)0x8A150019), unchecked((int)0x8A150006), unchecked((int)0x8A150008), unchecked((int)0x8A150107),
        unchecked((int)0x8A150011), unchecked((int)0x8A150041), unchecked((int)0x8A150046), unchecked((int)0x8A150045),
        unchecked((int)0x8A150007), unchecked((int)0x8A15004F), unchecked((int)0x8A150050), unchecked((int)0x8A150068),
        unchecked((int)0x8A150101), unchecked((int)0x8A150103), unchecked((int)0x8A150111), unchecked((int)0x8A150102),
        unchecked((int)0x8A150109), unchecked((int)0x8A15010A), unchecked((int)0x8A15010B), unchecked((int)0x8A15010C),
        unchecked((int)0x8A15010F), unchecked((int)0x8A15003A), unchecked((int)0x8A15001B), unchecked((int)0x8A15001C),
        unchecked((int)0x8A150083), unchecked((int)0x8A150105), unchecked((int)0x8A150113), unchecked((int)0x8A150001),
        unchecked((int)0x8A15005F), unchecked((int)0x80190194), unchecked((int)0x80190193), unchecked((int)0x8A150049),
        unchecked((int)0x8A150104), unchecked((int)0x8A150110), unchecked((int)0x8A150114), unchecked((int)0x8A150115),
        unchecked((int)0x8A15005E), unchecked((int)0x8A150005), -1, unchecked((int)0x8A1500E8), 12345,
    };

    [Theory]
    [MemberData(nameof(AllKnownCodes))]
    public void Every_unfinished_queue_row_says_what_happened_and_what_to_do(int code)
    {
        foreach (var op in new[] { QueueOperation.Install, QueueOperation.Upgrade })
            foreach (var e in new[] { Entry("Valve.Steam"), Entry("Logitech.GHUB"), Entry("9MV0B5HZVK9Z") })
            {
                var (state, text, _) = AppTexts.DescribeResult(code, op);
                Assert.False(string.IsNullOrWhiteSpace(text));
                if (state == QueueState.Done) continue;
                var detail = AppTexts.ResultDetail(code, op, e);
                Assert.Matches(Cyrillic, detail);
                Assert.True(detail.Length > 30, detail);
                Assert.EndsWith(".", detail.TrimEnd(')'));
                Assert.True(Enum.IsDefined(AppTexts.FallbackFor(code, op, e)));
            }
    }

    [Theory]
    [InlineData(unchecked((int)0x8A15005F), QueueFallback.Website)]
    [InlineData(unchecked((int)0x80190194), QueueFallback.Website)]
    [InlineData(unchecked((int)0x8A150008), QueueFallback.Website)]
    [InlineData(unchecked((int)0x8A150010), QueueFallback.Website)]
    [InlineData(unchecked((int)0x8A150101), QueueFallback.None)]
    [InlineData(unchecked((int)0x8A15010A), QueueFallback.None)]
    public void Install_failures_offer_the_official_site_where_it_helps(int code, QueueFallback expected) =>
        Assert.Equal(expected, AppTexts.FallbackFor(code, QueueOperation.Install, Entry("Valve.Steam")));

    [Fact]
    public void Dead_installer_link_is_explained()
    {
        Assert.Contains("404", WingetClient.Describe(WingetClient.HttpNotFound));
        Assert.True(WingetClient.IsDownloadProblem(WingetClient.HttpNotFound));
        Assert.True(WingetClient.IsHttpStatus(unchecked((int)0x801901F4))); // 500
        Assert.False(WingetClient.IsHttpStatus(unchecked((int)0x8A150008)));
        Assert.Contains("устарела", AppTexts.ResultDetail(WingetClient.HttpNotFound, QueueOperation.Install, Entry("Valve.Steam")));
    }

    [Fact]
    public void Queue_row_offers_its_fallback_only_when_finished()
    {
        var app = new AppItemViewModel(Entry("Logitech.GHUB"));
        var job = new QueueItemViewModel(app, QueueOperation.Install) { Fallback = QueueFallback.VendorDownload, Detail = "x" };
        Assert.False(job.HasFallback);
        Assert.False(job.ShowDetail);
        job.State = QueueState.Failed;
        Assert.True(job.HasFallback);
        Assert.True(job.ShowDetail);
        Assert.Equal("СКАЧАТЬ У ПРОИЗВОДИТЕЛЯ", job.FallbackText);
        Assert.Contains("download01.logi.com", job.FallbackToolTip);

        job.IsDownloading = true;
        job.State = QueueState.Running;
        Assert.True(job.HasFallback);
        Assert.Equal("ОТМЕНИТЬ ЗАГРУЗКУ", job.FallbackText);

        var steam = new QueueItemViewModel(new AppItemViewModel(Entry("Valve.Steam")), QueueOperation.Install) { Fallback = QueueFallback.Website, State = QueueState.Failed };
        Assert.Equal("ОТКРЫТЬ САЙТ", steam.FallbackText);
        Assert.Contains("store.steampowered.com", steam.FallbackToolTip);
    }

    // ─────────────────────────── .NET Desktop Runtime 10 "пакет не найден" ───────────────────────────

    [Fact]
    public void Runtime_found_only_by_the_cross_checks_is_not_offered_for_winget_upgrade()
    {
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.DotNet.DesktopRuntime.10"] = "10.0.14", // shared-framework folder, brought by the .NET SDK
            ["Valve.Steam"] = "2.10",                          // winget export
        };
        var fromWinget = new HashSet<string>(["valve.steam"], StringComparer.OrdinalIgnoreCase);
        var unmanaged = InstalledDetector.UnmanagedIds(installed, fromWinget);
        Assert.Equal(["Microsoft.DotNet.DesktopRuntime.10"], unmanaged);

        var runtime = new AppItemViewModel(Entry("Microsoft.DotNet.DesktopRuntime.10")) { IsKnown = true, WingetReady = true, InstalledVersion = "10.0.14" };
        Assert.Equal(AppAction.Upgrade, runtime.Action);
        runtime.IsUnmanaged = true;
        Assert.Equal(AppAction.Installed, runtime.Action);
        Assert.False(runtime.CanUpgrade);
        Assert.False(runtime.CanSelect);
        Assert.Contains("не через winget", runtime.ActionToolTip);
        Assert.Contains("не через winget", runtime.DisplayTags[0].ToolTip);

        // And if it still happens: a skip with an explanation, not an error.
        var (state, text, _) = AppTexts.DescribeResult(WingetClient.NoApplicationsFound, QueueOperation.Upgrade);
        Assert.Equal(QueueState.Skipped, state);
        Assert.Contains("не через winget", text);
        Assert.Equal(QueueFallback.None, AppTexts.FallbackFor(WingetClient.NoApplicationsFound, QueueOperation.Upgrade, Entry("Microsoft.DotNet.DesktopRuntime.10")));
    }

    // ─────────────────────────── catalog audit ───────────────────────────

    [Theory]
    [InlineData("RiotGames.LeagueOfLegends.EUW")]
    [InlineData("RiotGames.Valorant.EU")]
    public void Riot_games_update_themselves(string id)
    {
        var e = Entry(id);
        Assert.True(e.Has(AppFlags.ExplicitUpgrade));
        Assert.False(e.CanUpgrade);
        Assert.True(e.CanInstall);
    }

    [Theory]
    [InlineData("Resplendence.LatencyMon", true)]
    [InlineData("AIMP.AIMP", false)]
    [InlineData("MedalB.V.Medal", false)]
    [InlineData("Roblox.Roblox", false)]
    public void Packages_winget_cannot_install_now_go_to_the_vendor(string id, bool direct)
    {
        var e = Entry(id);
        Assert.True(e.WebsiteOnly);
        Assert.False(e.CanInstall);
        Assert.Matches(Cyrillic, e.WebsiteReason ?? "");
        Assert.Equal(direct, e.VendorInstaller is not null);

        var card = new AppItemViewModel(e) { IsKnown = true, WingetReady = true };
        Assert.Equal(direct ? AppAction.VendorDownload : AppAction.Website, card.Action);
        Assert.True(card.CanRunAction);
        Assert.False(card.CanSelect);
        Assert.Equal(direct, card.ShowSiteButton);
        Assert.Contains(e.WebsiteReason!, card.ActionToolTip);
        Assert.Contains(AppTexts.BuildTags(e), t => t.Text == "НЕ ЧЕРЕЗ WINGET" && t.ToolTip == e.WebsiteReason);
        card.QueueBusy = true;
        Assert.Equal(!direct, card.CanRunAction);
    }

    [Theory]
    [InlineData("Roblox.Roblox", InstallScope.Any)]
    [InlineData("Rem0o.FanControl", InstallScope.Any)]
    [InlineData("CXWorld.CapFrameX", InstallScope.Any)]
    [InlineData("OCBase.OCCT.Personal", InstallScope.Any)]
    [InlineData("LibreHardwareMonitor.LibreHardwareMonitor", InstallScope.Any)]
    [InlineData("Orbmu2k.nvidiaProfileInspector", InstallScope.Any)]
    [InlineData("M2Team.NanaZip", InstallScope.User)]
    [InlineData("Geeks3D.FurMark.2", InstallScope.Machine)]
    [InlineData("MoonlightGameStreamingProject.Moonlight", InstallScope.Machine)]
    public void Scopes_follow_the_installer_winget_actually_picks(string id, InstallScope scope) =>
        Assert.Equal(scope, Entry(id).Scope);

    [Fact]
    public void Real_installers_are_not_tagged_portable_and_portables_install_for_the_signed_in_user()
    {
        Assert.False(Entry("Rem0o.FanControl").Has(AppFlags.Portable));
        Assert.True(Entry("Rem0o.FanControl").Has(AppFlags.KernelDriver));
        Assert.False(Entry("Rem0o.FanControl").RunsUnelevated); // the Inno installer needs elevation
        Assert.False(Entry("CXWorld.CapFrameX").Has(AppFlags.Portable));
        Assert.DoesNotContain(AppTexts.BuildTags(Entry("Rem0o.FanControl")), t => t.Text == "ПОРТАТИВНАЯ");

        foreach (var e in AppCatalog.All.Where(e => e.Has(AppFlags.Portable) && !e.WebsiteOnly))
            Assert.True(e.RunsUnelevated, e.Id);
        Assert.True(Entry("M2Team.NanaZip").RunsUnelevated);       // MSIX: registered per user
        Assert.False(Entry("MoonlightGameStreamingProject.Moonlight").RunsUnelevated); // per-machine Burn bundle
        Assert.DoesNotContain(AppTexts.BuildTags(Entry("OCBase.OCCT.Personal")), t => t.ToolTip?.Contains("Roblox") == true);
    }

    [Fact]
    public void Install_hint_names_the_install_folder_and_the_vendor_fallback()
    {
        var bnet = new AppItemViewModel(Entry("Blizzard.BattleNet")) { IsKnown = true, WingetReady = true };
        Assert.Contains("Battle.net", bnet.ActionToolTip);
        Assert.Contains("Папка установки", bnet.ActionToolTip);
        var ghub = new AppItemViewModel(Entry("Logitech.GHUB")) { IsKnown = true, WingetReady = true };
        Assert.Contains("напрямую у производителя", ghub.ActionToolTip);
    }
}
