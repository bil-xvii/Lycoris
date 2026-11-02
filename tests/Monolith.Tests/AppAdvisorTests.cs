using Monolith.Core;
using Monolith.Modules.Apps;

namespace Monolith.Tests;

/// <summary>
/// Install advice of the "ПРИЛОЖЕНИЯ" page (Modules/Apps/AppAdvisor, docs/research/apps_catalog.md §14): pure rules,
/// nothing is read from the PC. Theories take strings (xUnit on Linux skips theories with app-assembly enum arguments).
/// </summary>
public sealed class AppAdvisorTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);

    private const string Xbox = AppAdvisor.XboxAppId;
    private const string GamingServices = AppAdvisor.GamingServicesId;
    private const string WebView2 = AppAdvisor.WebView2Id;
    private const string Prism = AppAdvisor.PrismLauncherId;
    private const string CurseForge = AppAdvisor.CurseForgeId;
    private const string SevenZip = AppAdvisor.SevenZipId;

    /// <summary>A known, ordinary Windows 11 24H2 PC: AMD graphics, no gaming peripherals, the given programs installed.</summary>
    private static AdviceContext Pc(params string[] installed) => new()
    {
        GpuKnown = true,
        GpuVendors = new HashSet<GpuVendor> { GpuVendor.Amd },
        OsBuild = 26100,
        OsUbr = 6899,
        InstalledKnown = true,
        StoreKnown = true,
        InstalledIds = new HashSet<string>(installed),
        PeripheralsKnown = true,
        Peripherals = new HashSet<PeripheralBrand>(),
        Today = Today,
    };

    private static CatalogEntry E(string id) => AppCatalog.Find(id) ?? throw new InvalidOperationException($"{id} is not in the catalog");

    private static AppAdvice Assess(string id, AdviceContext ctx) => AppAdvisor.Assess(E(id), ctx) ?? throw new InvalidOperationException($"no advice for {id}");

    private static AdviceLevel Level(string name) => Enum.Parse<AdviceLevel>(name);

    /// <summary>Every combination of known / unknown inputs the page can produce, plus edge dates and builds.</summary>
    public static IEnumerable<AdviceContext> ManyContexts()
    {
        var gpus = new (bool Known, GpuVendor[] Vendors)[]
        {
            (false, []), (true, []), (true, [GpuVendor.Nvidia]), (true, [GpuVendor.Amd, GpuVendor.Intel]),
        };
        var builds = new (int Build, int Ubr)[]
        {
            (0, 0), (19041, 0), (19045, 6456), (22000, 3260), (22621, 2428), (22621, 2600), (22621, 2715),
            (22631, 2428), (22631, 2506), (26100, 6899), (26200, 7000),
        };
        // Installed ids (null = detection not run yet) and whether the Store checks have finished.
        var installed = new (string[]? Ids, bool Store)[]
        {
            (null, false), ([], true), ([], false), ([Xbox, Prism], true), ([CurseForge], true),
            ([SevenZip, "Google.Chrome", "VideoLAN.VLC", "AntibodySoftware.WizTree", "RevoUninstaller.RevoUninstaller", "REALiX.HWiNFO",
              "Intel.PresentMon", "VB-Audio.Voicemeeter", "OBSProject.OBSStudio", WebView2], true),
            (AppCatalog.All.SelectMany(e => e.AllIds).ToArray(), true),
        };
        var peripherals = new (bool Known, PeripheralBrand[] Brands)[] { (false, []), (true, []), (true, Enum.GetValues<PeripheralBrand>()) };
        var days = new DateOnly[] { Today, new(2026, 11, 10), new(2029, 1, 1) };

        foreach (var g in gpus)
        foreach (var b in builds)
        foreach (var i in installed)
        foreach (var p in peripherals)
        foreach (var d in days)
            yield return new AdviceContext
            {
                GpuKnown = g.Known, GpuVendors = g.Vendors.ToHashSet(),
                OsBuild = b.Build, OsUbr = b.Ubr,
                InstalledKnown = i.Ids is not null, StoreKnown = i.Store, InstalledIds = new HashSet<string>(i.Ids ?? []),
                PeripheralsKnown = p.Known, Peripherals = p.Brands.ToHashSet(),
                Today = d,
            };
    }

    // ─────────────────────────── levels and texts ───────────────────────────

    [Fact]
    public void Tag_texts_are_exact()
    {
        // The Drivers page may reuse «РЕКОМЕНДУЕТСЯ» / «ПОКА НЕ НУЖНО»: keep these strings.
        Assert.Equal("НУЖНО", AppAdvisor.TagText(AdviceLevel.Required));
        Assert.Equal("РЕКОМЕНДУЕТСЯ", AppAdvisor.TagText(AdviceLevel.Recommended));
        Assert.Equal("ПО ЖЕЛАНИЮ", AppAdvisor.TagText(AdviceLevel.Optional));
        Assert.Equal("ПОКА НЕ НУЖНО", AppAdvisor.TagText(AdviceLevel.NotNow));
        Assert.Equal("НЕ НУЖНО", AppAdvisor.TagText(AdviceLevel.NotNeeded));
        Assert.Equal("НУЖНО", new AppAdvice(AdviceLevel.Required, "x").TagText);
    }

    [Fact]
    public void Every_level_except_optional_explains_itself_for_every_entry()
    {
        var seen = new HashSet<AdviceLevel>();
        foreach (var ctx in ManyContexts())
        foreach (var e in AppCatalog.All)
        {
            var advice = AppAdvisor.Assess(e, ctx);
            if (advice is null) continue;
            seen.Add(advice.Level);
            if (advice.Level == AdviceLevel.Optional) continue;
            Assert.False(string.IsNullOrWhiteSpace(advice.Reason), $"{e.Id}: {advice.Level} without a reason");
            Assert.True(advice.Reason.EndsWith('.') || advice.Reason.EndsWith(')'), $"{e.Id}: «{advice.Reason}» is not a sentence");
            Assert.DoesNotContain('\n', advice.Reason);
            Assert.True(advice.Reason.Length <= 170, $"{e.Id}: reason too long ({advice.Reason.Length})");
        }
        Assert.Equal(Enum.GetValues<AdviceLevel>().Length, seen.Count);
    }

    [Fact]
    public void Risky_paid_and_end_of_life_entries_are_never_pushed()
    {
        var flagged = AppCatalog.All.Where(e => (e.Flags & AppAdvisor.NeverSuggested) != 0).ToList();
        Assert.NotEmpty(flagged);
        var suggestedSomething = false;
        foreach (var ctx in ManyContexts())
        foreach (var e in AppCatalog.All)
        {
            var advice = AppAdvisor.Assess(e, ctx);
            if (advice is not { IsSuggested: true }) continue;
            suggestedSomething = true;
            Assert.True((e.Flags & AppAdvisor.NeverSuggested) == 0, $"{e.Id} ({e.Flags}) got {advice.Level}");
        }
        Assert.True(suggestedSomething);
    }

    [Fact]
    public void Safety_clamp_lowers_any_flagged_suggestion_to_optional()
    {
        // Drives the clamp itself: today's rules happen to suggest no flagged entry, a later catalog edit might.
        var logitech = Pc() with { Peripherals = new HashSet<PeripheralBrand> { PeripheralBrand.Logitech } };
        var suggested = new (string Id, AdviceContext Ctx, AdviceLevel Unflagged)[]
        {
            ("Valve.Steam", Pc(), AdviceLevel.Recommended),
            ("Microsoft.VCRedist.2015+.x64", Pc(), AdviceLevel.Required),
            ("Microsoft.DotNet.DesktopRuntime.10", Pc(), AdviceLevel.Recommended),
            ("Logitech.GHUB", logitech, AdviceLevel.Recommended),
            (GamingServices, Pc(Xbox), AdviceLevel.Required),
        };
        var flags = Enum.GetValues<AppFlags>().Where(f => f != AppFlags.None && (AppAdvisor.NeverSuggested & f) == f).ToList();
        Assert.Equal(7, flags.Count);
        foreach (var (id, ctx, unflagged) in suggested)
        {
            var plain = Assess(id, ctx);
            Assert.Equal(unflagged, plain.Level);
            foreach (var flag in flags)
            {
                var flagged = E(id) with { Flags = E(id).Flags | flag };
                var advice = AppAdvisor.Assess(flagged, ctx)!;
                Assert.Equal(AdviceLevel.Optional, advice.Level);
                Assert.Equal(plain.Reason, advice.Reason);
                Assert.Empty(AppAdvisor.Kit([flagged], ctx));
            }
        }
        // Unflagged, the same runtime is in the kit.
        Assert.Single(AppAdvisor.Kit([E("Microsoft.VCRedist.2015+.x64")], Pc()));
    }

    [Fact]
    public void Windows_components_and_installed_programs_show_no_advice()
    {
        var edge = E("Microsoft.Edge");
        foreach (var ctx in new[] { AdviceContext.Unknown, Pc(), Pc("Microsoft.Edge") })
        {
            Assert.Null(AppAdvisor.Advise(edge, ctx));
            Assert.Null(AppAdvisor.Assess(edge, ctx));
        }

        // Installed: no advice on the card, but the need is still judged (the gamer kit counts it as installed).
        var installed = Pc("Valve.Steam", "Microsoft.VCRedist.2015+.x64", WebView2, "RiotGames.Valorant.NA");
        Assert.Null(AppAdvisor.Advise(E("Valve.Steam"), installed));
        Assert.Null(AppAdvisor.Advise(E("Microsoft.VCRedist.2015+.x64"), installed));
        Assert.Null(AppAdvisor.Advise(E(WebView2), installed));
        Assert.Null(AppAdvisor.Advise(E("RiotGames.Valorant.EU"), installed)); // a regional variant counts
        Assert.Equal(AdviceLevel.Required, Assess("Microsoft.VCRedist.2015+.x64", installed).Level);
        Assert.Equal(AdviceLevel.Recommended, AppAdvisor.Advise(E("Discord.Discord"), installed)!.Level);
    }

    [Fact]
    public void Unknown_facts_never_lead_to_a_confident_claim()
    {
        var ctx = AdviceContext.Unknown;
        Assert.False(ctx.InstalledKnown || ctx.GpuKnown || ctx.PeripheralsKnown);
        foreach (var e in AppCatalog.All)
        {
            var advice = AppAdvisor.Assess(e, ctx);
            // The only "НЕ НУЖНО" that needs no fact about the PC: Process Lasso fights Lycoris's own game mode.
            if (advice is { Level: AdviceLevel.NotNeeded }) Assert.Equal("BitSum.ProcessLasso", e.Id);
        }
        foreach (var id in new[] { "Nvidia.PhysX", "Logitech.GHUB", "Elgato.StreamDeck", "Corsair.iCUE.5", WebView2, GamingServices, SevenZip,
                     "EclipseAdoptium.Temurin.21.JRE", "Microsoft.DotNet.Framework.Runtime" })
            Assert.Equal(AdviceLevel.Optional, Assess(id, ctx).Level);
        // Installed state unknown: no duplicate claims, even when ids are passed along.
        var stale = ctx with { InstalledIds = new HashSet<string> { SevenZip, "Google.Chrome" } };
        Assert.Equal(AdviceLevel.NotNow, Assess("RARLab.WinRAR", stale).Level);
        Assert.Equal(AdviceLevel.Optional, Assess("Mozilla.Firefox.ru", stale).Level);
        Assert.False(stale.IsInstalled(SevenZip));
    }

    // ─────────────────────────── graphics card ───────────────────────────

    [Theory]
    [InlineData("Nvidia.PhysX")]
    [InlineData("9NF8H0H7WMLT")]
    [InlineData("Orbmu2k.nvidiaProfileInspector")]
    [InlineData("TechPowerUp.NVCleanstall")]
    public void Nvidia_packages_follow_the_graphics_card(string id)
    {
        Assert.True(E(id).Has(AppFlags.NvidiaOnly));

        var none = Assess(id, Pc());
        Assert.Equal(AdviceLevel.NotNeeded, none.Level);
        Assert.Equal("Видеокарта NVIDIA не найдена.", none.Reason);

        var nvidia = Assess(id, Pc() with { GpuVendors = new HashSet<GpuVendor> { GpuVendor.Nvidia, GpuVendor.Intel } });
        Assert.Equal(AdviceLevel.NotNow, nvidia.Level);
        Assert.False(string.IsNullOrWhiteSpace(nvidia.Reason));

        var unknown = Assess(id, Pc() with { GpuKnown = false, GpuVendors = new HashSet<GpuVendor>() });
        Assert.Equal(AdviceLevel.Optional, unknown.Level);
        Assert.DoesNotContain("не найдена", unknown.Reason);
    }

    [Fact]
    public void Nvidia_advice_says_why_each_package_is_rarely_needed()
    {
        var nvidia = Pc() with { GpuVendors = new HashSet<GpuVendor> { GpuVendor.Nvidia } };
        Assert.Contains("Драйвер NVIDIA уже содержит PhysX", Assess("Nvidia.PhysX", nvidia).Reason);
        Assert.Contains("вместе с драйвером", Assess("9NF8H0H7WMLT", nvidia).Reason);
        Assert.Contains("для опытных", Assess("Orbmu2k.nvidiaProfileInspector", nvidia).Reason);
        Assert.Contains("для опытных", Assess("TechPowerUp.NVCleanstall", nvidia).Reason);
    }

    // ─────────────────────────── peripherals ───────────────────────────

    [Theory]
    [InlineData("Logitech.GHUB", "Logitech")]
    [InlineData("RazerInc.RazerInstaller.Synapse4", "Razer")]
    [InlineData("Corsair.iCUE.5", "Corsair")]
    [InlineData("SteelSeries.GG", "SteelSeries")]
    [InlineData("Elgato.StreamDeck", "Elgato")]
    public void Peripheral_software_follows_the_connected_devices(string id, string brandName)
    {
        var brand = Enum.Parse<PeripheralBrand>(brandName);
        Assert.Equal(brand, AppAdvisor.PeripheralApps[id]);

        var found = Assess(id, Pc() with { Peripherals = new HashSet<PeripheralBrand> { brand } });
        Assert.Equal(AdviceLevel.Recommended, found.Level);
        Assert.Contains(brandName, found.Reason);

        // Other makers' devices change nothing.
        var other = Enum.GetValues<PeripheralBrand>().Where(b => b != brand).ToHashSet();
        Assert.Equal(Assess(id, Pc()), Assess(id, Pc() with { Peripherals = other }));
        Assert.NotEqual(AdviceLevel.Recommended, Assess(id, Pc()).Level);

        Assert.Equal(AdviceLevel.Optional, Assess(id, Pc() with { PeripheralsKnown = false }).Level);
    }

    [Theory]
    [InlineData("Logitech.GHUB", "NotNeeded", "Logitech")]
    [InlineData("RazerInc.RazerInstaller.Synapse4", "NotNeeded", "Razer")]
    // The scan sees USB/Bluetooth devices only: Sonar works with any headset, iCUE also drives Corsair RGB memory (SMBus),
    // and the Stream Deck app also serves Stream Deck Mobile on a phone.
    [InlineData("SteelSeries.GG", "Optional", "Sonar")]
    [InlineData("Corsair.iCUE.5", "Optional", "память Corsair")]
    [InlineData("Elgato.StreamDeck", "NotNow", "Stream Deck Mobile")]
    public void Peripheral_software_without_the_device_is_not_needed_only_when_it_has_no_other_use(string id, string expected, string reason)
    {
        var none = Assess(id, Pc());
        Assert.Equal(Level(expected), none.Level);
        Assert.Contains(reason, none.Reason);
    }

    // ─────────────────────────── one program per job ───────────────────────────

    [Fact]
    public void Roles_name_catalog_entries_only()
    {
        Assert.Equal(8, AppAdvisor.Roles.Count);
        foreach (var role in AppAdvisor.Roles)
        {
            Assert.True(role.Count >= 2);
            Assert.All(role, id => Assert.NotNull(AppCatalog.Find(id)));
        }
        var all = AppAdvisor.Roles.SelectMany(r => r).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Second_program_for_the_same_job_is_not_needed()
    {
        foreach (var role in AppAdvisor.Roles)
        foreach (var installed in role)
        foreach (var other in role.Where(id => id != installed))
        {
            var advice = Assess(other, Pc(installed));
            Assert.Equal(AdviceLevel.NotNeeded, advice.Level);
            Assert.Equal($"Уже установлен {E(installed).Name} — он делает то же самое.", advice.Reason);
        }
        // Nothing of the role installed: the program's own rule applies.
        Assert.NotEqual(AdviceLevel.NotNeeded, Assess("VideoLAN.VLC", Pc()).Level);
        Assert.NotEqual(AdviceLevel.NotNeeded, Assess("OBSProject.OBSStudio", Pc("Valve.Steam")).Level);
    }

    [Fact]
    public void Second_browser_is_not_now_and_edge_does_not_count()
    {
        var withChrome = Pc("Google.Chrome");
        foreach (var e in AppCatalog.All.Where(e => e.Category == AppCategory.Browsers && !e.SystemComponent && e.Id != "Google.Chrome"))
        {
            var advice = Assess(e.Id, withChrome);
            Assert.Equal(AdviceLevel.NotNow, advice.Level);
            Assert.Contains("Google Chrome", advice.Reason);
        }
        Assert.Null(AppAdvisor.Advise(E("Google.Chrome"), withChrome));

        // Edge is part of Windows: having it is not a reason to skip another browser.
        Assert.Equal(AdviceLevel.Optional, Assess("Mozilla.Firefox.ru", Pc("Microsoft.Edge")).Level);
        Assert.Equal(AdviceLevel.Optional, Assess("Mozilla.Firefox.ru", Pc()).Level);
    }

    // ─────────────────────────── launchers and runtimes ───────────────────────────

    [Fact]
    public void Gaming_services_are_needed_only_with_the_xbox_app()
    {
        Assert.Equal(AdviceLevel.Required, Assess(GamingServices, Pc(Xbox)).Level);
        Assert.Equal(AdviceLevel.NotNow, Assess(GamingServices, Pc()).Level);
        Assert.Equal(AdviceLevel.Optional, Assess(GamingServices, Pc() with { InstalledKnown = false }).Level);
        // Fast detection pass, Store apps not checked yet: the Xbox app may still be there — no "ПОКА НЕ НУЖНО" claim.
        Assert.Equal(AdviceLevel.Optional, Assess(GamingServices, Pc() with { StoreKnown = false }).Level);
        Assert.Equal(AdviceLevel.Required, Assess(GamingServices, Pc(Xbox) with { StoreKnown = false }).Level);
        Assert.DoesNotContain(E(GamingServices), AppAdvisor.Kit(AppCatalog.All, Pc() with { StoreKnown = false }));
        Assert.False(AdviceContext.Unknown.StoreKnown);
    }

    [Fact]
    public void Webview2_is_required_when_missing()
    {
        var missing = Assess(WebView2, Pc());
        Assert.Equal(AdviceLevel.Required, missing.Level);
        Assert.Contains("не найден", missing.Reason);
        // Steam and other CEF-based launchers do not use WebView2: the reason must not claim they do.
        Assert.DoesNotContain("Steam", missing.Reason);
        Assert.Null(AppAdvisor.Advise(E(WebView2), Pc(WebView2)));
        Assert.Equal(AdviceLevel.Optional, Assess(WebView2, Pc() with { InstalledKnown = false }).Level);
    }

    [Theory]
    [InlineData("", "NotNow", "NotNow", "NotNow")]
    [InlineData(Prism, "Recommended", "Optional", "NotNow")]
    [InlineData(CurseForge, "NotNow", "NotNow", "NotNow")]
    [InlineData(Prism + "|" + CurseForge, "Recommended", "Optional", "NotNow")]
    public void Java_is_advised_only_for_prism_launcher(string installed, string java21, string java17, string java8)
    {
        var ctx = Pc(installed.Split('|', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(Level(java21), Assess("EclipseAdoptium.Temurin.21.JRE", ctx).Level);
        Assert.Equal(Level(java17), Assess("EclipseAdoptium.Temurin.17.JRE", ctx).Level);
        Assert.Equal(Level(java8), Assess("EclipseAdoptium.Temurin.8.JRE", ctx).Level);
    }

    [Fact]
    public void Official_minecraft_launcher_needs_no_separate_java()
    {
        var advice = Assess("EclipseAdoptium.Temurin.21.JRE", Pc("Mojang.MinecraftLauncher"));
        Assert.Equal(AdviceLevel.NotNow, advice.Level);
        Assert.Contains("ставит свою Java сам", advice.Reason);
        Assert.Contains("CurseForge", Assess("EclipseAdoptium.Temurin.17.JRE", Pc(CurseForge)).Reason);
    }

    [Theory]
    [InlineData("Microsoft.DotNet.DesktopRuntime.10", "2026-10-27", "Recommended")]
    [InlineData("Microsoft.DotNet.DesktopRuntime.10", "2028-11-13", "Recommended")]
    [InlineData("Microsoft.DotNet.DesktopRuntime.10", "2028-11-14", "NotNow")]
    [InlineData("Microsoft.DotNet.DesktopRuntime.9", "2026-10-27", "Optional")]
    [InlineData("Microsoft.DotNet.DesktopRuntime.9", "2026-11-09", "Optional")]
    [InlineData("Microsoft.DotNet.DesktopRuntime.9", "2026-11-10", "NotNow")]
    [InlineData("Microsoft.DotNet.DesktopRuntime.8", "2026-11-09", "Optional")]
    [InlineData("Microsoft.DotNet.DesktopRuntime.8", "2026-11-10", "NotNow")]
    [InlineData("Microsoft.DotNet.DesktopRuntime.6", "2024-11-11", "Optional")]
    [InlineData("Microsoft.DotNet.DesktopRuntime.6", "2026-10-27", "NotNow")]
    public void Dotnet_runtimes_follow_microsoft_end_of_support(string id, string today, string expected)
    {
        var advice = Assess(id, Pc() with { Today = DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture) });
        Assert.Equal(Level(expected), advice.Level);
        // The reason names the end date (for Optional too: it is the tag's tooltip).
        Assert.Contains(AppAdvisor.FormatDate(AppAdvisor.DotNetEndOfSupport[id]), advice.Reason);
        if (advice.Level == AdviceLevel.NotNow) Assert.Contains("только если программа просит", advice.Reason);
    }

    [Fact]
    public void Dotnet_end_dates_match_microsoft_policy()
    {
        // dotnet.microsoft.com/platform/support/policy/dotnet-core (checked 27.10.2026).
        Assert.Equal(new DateOnly(2028, 11, 14), AppAdvisor.DotNetEndOfSupport["Microsoft.DotNet.DesktopRuntime.10"]);
        Assert.Equal(new DateOnly(2026, 11, 10), AppAdvisor.DotNetEndOfSupport["Microsoft.DotNet.DesktopRuntime.9"]);
        Assert.Equal(new DateOnly(2026, 11, 10), AppAdvisor.DotNetEndOfSupport["Microsoft.DotNet.DesktopRuntime.8"]);
        Assert.Equal(new DateOnly(2024, 11, 12), AppAdvisor.DotNetEndOfSupport["Microsoft.DotNet.DesktopRuntime.6"]);
        Assert.All(AppAdvisor.DotNetEndOfSupport.Keys, id => Assert.NotNull(AppCatalog.Find(id)));
        Assert.Equal("10.11.2026", AppAdvisor.FormatDate(new DateOnly(2026, 11, 10)));
    }

    [Theory]
    [InlineData(19045, "Recommended")]
    [InlineData(19044, "Recommended")]
    [InlineData(22000, "Recommended")]
    [InlineData(19041, "NotNeeded")] // the installer refuses builds before 20H2
    [InlineData(22621, "NotNeeded")] // built in
    [InlineData(0, "Optional")]
    public void Dotnet_framework_481_is_recommended_where_it_is_offered(int build, string expected) =>
        Assert.Equal(Level(expected), Assess("Microsoft.DotNet.Framework.Runtime", Pc() with { OsBuild = build }).Level);

    [Fact]
    public void Game_runtimes_and_main_programs()
    {
        var pc = Pc();
        Assert.Equal(AdviceLevel.Required, Assess("Microsoft.VCRedist.2015+.x64", pc).Level);
        Assert.Equal(AdviceLevel.Required, Assess("Microsoft.VCRedist.2015+.x86", pc).Level);
        foreach (var year in new[] { "2005", "2008", "2010", "2012", "2013" })
        foreach (var arch in new[] { "x86", "x64" })
        {
            var old = Assess($"Microsoft.VCRedist.{year}.{arch}", pc);
            Assert.Equal(AdviceLevel.Recommended, old.Level);
            // Out of Microsoft support (2016–2024) without the Eol flag — nothing replaces them — and the reason says so.
            Assert.Contains("больше не обновляет", old.Reason);
            Assert.False(E($"Microsoft.VCRedist.{year}.{arch}").Has(AppFlags.Eol));
        }
        Assert.Equal(AdviceLevel.Recommended, Assess("Microsoft.DirectX", pc).Level);
        Assert.Equal(AdviceLevel.Recommended, Assess("Valve.Steam", pc).Level);
        Assert.Equal(AdviceLevel.Recommended, Assess("Discord.Discord", pc).Level);
        Assert.Equal(AdviceLevel.NotNeeded, Assess("BitSum.ProcessLasso", pc).Level);
        Assert.Contains("Lycoris", Assess("BitSum.ProcessLasso", pc).Reason);
        Assert.Contains("Автозапуск и службы", Assess("Microsoft.Sysinternals.Autoruns", pc).Reason);
        // Everything else is "ПО ЖЕЛАНИЮ".
        Assert.Equal(AdviceLevel.Optional, Assess("EpicGames.EpicGamesLauncher", pc).Level);
        Assert.Equal(AdviceLevel.Optional, Assess("Telegram.TelegramDesktop", pc).Level);
    }

    [Theory]
    [InlineData("OCBase.OCCT.Personal")]
    [InlineData("Geeks3D.FurMark.2")]
    [InlineData("Resplendence.LatencyMon")]
    [InlineData("Wagnardsoft.DisplayDriverUninstaller")]
    [InlineData("SpecialK.SpecialK")]
    [InlineData("Rufus.Rufus")]
    [InlineData("RevoUninstaller.RevoUninstaller")]
    [InlineData("Klocman.BulkCrapUninstaller")]
    [InlineData("CodecGuide.K-LiteCodecPack.Standard")]
    [InlineData("VB-Audio.Voicemeeter")]
    [InlineData("VB-Audio.Voicemeeter.Banana")]
    [InlineData("LizardByte.Sunshine")]
    [InlineData("AnyDesk.AnyDesk")]
    [InlineData("TeamViewer.TeamViewer")]
    [InlineData("Microsoft.Sysinternals.Autoruns")]
    [InlineData("Microsoft.XNARedist")]
    [InlineData("CreativeTechnology.OpenAL")]
    [InlineData("RARLab.WinRAR")]
    public void Special_purpose_tools_are_not_now(string id)
    {
        var advice = Assess(id, Pc());
        Assert.Equal(AdviceLevel.NotNow, advice.Level);
        Assert.False(string.IsNullOrWhiteSpace(advice.Reason));
    }

    [Fact]
    public void Remote_access_advice_warns_about_scams()
    {
        foreach (var id in new[] { "AnyDesk.AnyDesk", "TeamViewer.TeamViewer" })
        {
            var reason = Assess(id, Pc()).Reason;
            Assert.Contains("мошенники", reason);
            Assert.Contains("доверяете", reason);
        }
    }

    // ─────────────────────────── archives ───────────────────────────

    [Theory]
    [InlineData(19045, 6456, "Recommended")] // Windows 10
    [InlineData(22000, 3260, "Recommended")] // Windows 11 21H2
    [InlineData(22621, 2283, "Recommended")] // 22H2 before KB5030310
    [InlineData(22621, 2428, "Optional")]    // 22H2 with KB5031354: there, but on only with "get the latest updates"
    [InlineData(22621, 2600, "Optional")]    // 22H2 between the preview and the November 2023 update: can't tell
    [InlineData(22621, 2715, "Optional")]    // 22H2 with KB5032190
    [InlineData(22631, 2428, "Optional")]    // 23H2 pre-release build: can't tell
    [InlineData(22631, 2506, "Optional")]    // 23H2 with KB5031455
    [InlineData(22631, 0, "Optional")]       // UBR unknown
    [InlineData(26100, 1, "Optional")]       // 24H2
    [InlineData(26200, 6899, "Optional")]    // 25H2
    [InlineData(0, 0, "Optional")]           // build unknown
    public void Seven_zip_follows_native_rar_and_7z_support(int build, int ubr, string expected) =>
        Assert.Equal(Level(expected), Assess(SevenZip, Pc() with { OsBuild = build, OsUbr = ubr }).Level);

    [Fact]
    public void Native_archive_support_by_build()
    {
        Assert.False(AppAdvisor.HasNativeArchives(19045, 6456));
        Assert.False(AppAdvisor.HasNativeArchives(22000, 3260));
        Assert.False(AppAdvisor.HasNativeArchives(22621, 2360));
        Assert.Null(AppAdvisor.HasNativeArchives(22621, 2361)); // KB5030310: toggle-gated
        Assert.Null(AppAdvisor.HasNativeArchives(22621, 2428)); // KB5031354: toggle-gated
        Assert.Null(AppAdvisor.HasNativeArchives(22621, 2506));
        Assert.Null(AppAdvisor.HasNativeArchives(22621, 2714));
        Assert.True(AppAdvisor.HasNativeArchives(22621, 2715));
        Assert.Null(AppAdvisor.HasNativeArchives(22631, 2428));
        Assert.Null(AppAdvisor.HasNativeArchives(22631, 2505));
        Assert.True(AppAdvisor.HasNativeArchives(22631, 2506));
        Assert.Null(AppAdvisor.HasNativeArchives(22631, 0));
        Assert.Null(AppAdvisor.HasNativeArchives(22623, 3000)); // Insider branch: can't tell
        Assert.True(AppAdvisor.HasNativeArchives(22635, 1));
        Assert.True(AppAdvisor.HasNativeArchives(26100, 0));
        Assert.Null(AppAdvisor.HasNativeArchives(0, 0));

        Assert.Contains("Windows уже открывает RAR и 7z", Assess(SevenZip, Pc()).Reason);
        Assert.Contains("Windows 10", Assess(SevenZip, Pc() with { OsBuild = 19045 }).Reason);
    }

    [Fact]
    public void Winrar_is_not_needed_next_to_another_archiver()
    {
        Assert.Equal(AdviceLevel.NotNow, Assess("RARLab.WinRAR", Pc()).Level);
        Assert.Contains("7-Zip", Assess("RARLab.WinRAR", Pc()).Reason);
        Assert.Equal(AdviceLevel.NotNeeded, Assess("RARLab.WinRAR", Pc(SevenZip)).Level);
        Assert.Equal(AdviceLevel.NotNeeded, Assess(SevenZip, Pc("M2Team.NanaZip")).Level);
    }

    // ─────────────────────────── gamer kit ───────────────────────────

    [Fact]
    public void Kit_is_the_runtimes_this_pc_needs()
    {
        foreach (var ctx in ManyContexts().Where((_, i) => i % 7 == 0))
        {
            var kit = AppAdvisor.Kit(AppCatalog.All, ctx);
            Assert.NotEmpty(kit);
            foreach (var e in kit)
            {
                Assert.Equal(AppCategory.Runtimes, e.Category);
                Assert.True(e.CanInstall);
                Assert.True(AppAdvisor.Assess(e, ctx)!.IsSuggested);
                Assert.Equal(AppFlags.None, e.Flags & AppAdvisor.NeverSuggested);
            }
            // Every suggested runtime is in it.
            Assert.All(AppCatalog.All.Where(e => e.Category == AppCategory.Runtimes && e.CanInstall && AppAdvisor.Assess(e, ctx) is { IsSuggested: true }),
                e => Assert.Contains(e, kit));
        }
    }

    [Fact]
    public void Kit_on_windows_10_with_xbox_and_without_webview2()
    {
        var ctx = Pc(Xbox) with { OsBuild = 19045, OsUbr = 6456 };
        var kit = AppAdvisor.Kit(AppCatalog.ForBuild(ctx.OsBuild), ctx).Select(e => e.Id).ToList();
        Assert.Contains(WebView2, kit);
        Assert.Contains(GamingServices, kit);
        Assert.Contains("Microsoft.DotNet.Framework.Runtime", kit);
        Assert.Contains("Microsoft.DotNet.DesktopRuntime.10", kit);
        Assert.Contains("Microsoft.DirectX", kit);
        Assert.Equal(12, kit.Count(id => id.StartsWith("Microsoft.VCRedist.", StringComparison.Ordinal)));
        Assert.Equal(17, kit.Count);
        foreach (var notNeeded in new[] { "Microsoft.DotNet.DesktopRuntime.9", "Microsoft.DotNet.DesktopRuntime.8", "Microsoft.DotNet.DesktopRuntime.6",
                     "Microsoft.XNARedist", "CreativeTechnology.OpenAL", "Nvidia.PhysX", "EclipseAdoptium.Temurin.21.JRE" })
            Assert.DoesNotContain(notNeeded, kit);
    }

    [Fact]
    public void Kit_changes_with_the_pc()
    {
        // Without the Xbox app: no Gaming Services. After .NET 10's end of support: no .NET at all.
        var later = Pc(WebView2) with { Today = new DateOnly(2028, 11, 14) };
        var kit = AppAdvisor.Kit(AppCatalog.ForBuild(later.OsBuild), later).Select(e => e.Id).ToList();
        Assert.DoesNotContain(GamingServices, kit);
        Assert.DoesNotContain(kit, id => id.StartsWith("Microsoft.DotNet.", StringComparison.Ordinal));
        Assert.Equal(14, kit.Count);

        // Prism Launcher: Java 21 joins (17 and 8 stay out).
        var prism = AppAdvisor.Kit(AppCatalog.All, Pc(Prism)).Select(e => e.Id).ToList();
        Assert.Contains("EclipseAdoptium.Temurin.21.JRE", prism);
        Assert.DoesNotContain("EclipseAdoptium.Temurin.17.JRE", prism);
        Assert.DoesNotContain("EclipseAdoptium.Temurin.8.JRE", prism);

        // Before the first detection: only what every PC needs (no WebView2 / Gaming Services claims).
        var unknown = AppAdvisor.Kit(AppCatalog.All, AdviceContext.Unknown with { Today = Today }).Select(e => e.Id).ToList();
        Assert.DoesNotContain(WebView2, unknown);
        Assert.DoesNotContain(GamingServices, unknown);
        Assert.Equal(14, unknown.Count); // 12 Visual C++, DirectX, .NET 10
    }

    // ─────────────────────────── peripheral scan (pure part) ───────────────────────────

    [Theory]
    [InlineData(@"HID\VID_046D&PID_C539&MI_00\7&2A5B3C1&0&0000", "Mouse", "046D", "Logitech")]
    [InlineData(@"HID\VID_1532&PID_0084&MI_00\7&1F2E3D4C&0&0000", "HIDClass", "1532", "Razer")]
    [InlineData(@"HID\VID_1B1C&PID_1B2D&MI_01\6&ABCDEF&0&0000", "Keyboard", "1B1C", "Corsair")]
    [InlineData(@"usb\vid_1038&pid_12ad&mi_00\6&abc&0&0000", "MEDIA", "1038", "SteelSeries")]      // headset audio
    [InlineData(@"HID\VID_0FD9&PID_0080\7&CL12K1A00001&0&0000", "HIDClass", "0FD9", "Elgato")]     // Stream Deck MK.2
    [InlineData(@"HID\VID_0FD9&PID_006D\7&1&0&0000", "HIDClass", "0FD9", "Elgato")]                // Stream Deck original V2
    [InlineData(@"USB\VID_B58E&PID_9E84&MI_00\6&1&0&0000", "MEDIA", "B58E", "Logitech")]           // Blue Yeti: G HUB
    [InlineData(@"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D_PID&B023\7&1A2B3C4D&0&0", "", "046D", "Logitech")] // no class: not ruled out
    [InlineData(@"HID\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&021038_PID&1830_REV&0001_D1A2B3C4D5E6\8&1&0&0000", "HIDClass", "1038", "SteelSeries")]
    // Not configured by the maker's program: a Corsair flash drive, a composite parent, Elgato capture cards and cameras,
    // a Logitech webcam and its microphone.
    [InlineData(@"USB\VID_1B1C&PID_1A03\0A1B2C3D4E5F", "USB", "1B1C", "")]
    [InlineData(@"USB\VID_1B1C&PID_1A03\0A1B2C3D4E5F", "WPD", "1B1C", "")]
    [InlineData(@"USB\VID_1532&PID_0084\6&1&0&4", "USB", "1532", "")]
    [InlineData(@"USB\VID_0FD9&PID_006A&MI_00\7&1&0&0000", "MEDIA", "0FD9", "")]   // capture card
    [InlineData(@"USB\VID_0FD9&PID_0066&MI_00\7&1&0&0000", "Camera", "0FD9", "")]  // Cam Link
    [InlineData(@"USB\VID_0FD9&PID_0078&MI_00\7&1&0&0000", "Image", "0FD9", "")]   // Facecam
    [InlineData(@"USB\VID_046D&PID_082D&MI_00\7&1&0&0000", "Camera", "046D", "")]  // HD Pro Webcam C920
    [InlineData(@"USB\VID_046D&PID_082D&MI_02\7&1&0&0002", "MEDIA", "046D", "")]   // its microphone
    [InlineData(@"USB\VID_045E&PID_028E\00000001", "XnaComposite", "045E", "")] // Xbox controller: not a brand with software here
    [InlineData(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001009E_PID&4020\7&1&0&0", "MEDIA", "", "")] // Bluetooth SIG company id
    [InlineData(@"PCI\VEN_10DE&DEV_2684&SUBSYS_16F310DE&REV_A1\4&1", "Display", "", "")]
    [InlineData(@"USB\ROOT_HUB30\4&2B1C3D4E&0&0", "USB", "", "")]
    [InlineData("", "", "", "")]
    public void Peripheral_scan_reads_usb_vendor_ids(string deviceId, string pnpClass, string vid, string brand)
    {
        Assert.Equal(vid.Length > 0 ? vid : null, PeripheralScan.ParseVendorId(deviceId));
        Assert.Equal(brand.Length > 0 ? Enum.Parse<PeripheralBrand>(brand) : (PeripheralBrand?)null,
            PeripheralScan.BrandOf(deviceId, pnpClass.Length > 0 ? pnpClass : null));
    }

    [Fact]
    public void Peripheral_vendor_ids_are_the_usb_if_ones()
    {
        Assert.Equal(PeripheralBrand.Logitech, PeripheralScan.VendorIds["046D"]);
        Assert.Equal(PeripheralBrand.Razer, PeripheralScan.VendorIds["1532"]);
        Assert.Equal(PeripheralBrand.Corsair, PeripheralScan.VendorIds["1B1C"]);
        Assert.Equal(PeripheralBrand.SteelSeries, PeripheralScan.VendorIds["1038"]);
        Assert.Equal(PeripheralBrand.Elgato, PeripheralScan.VendorIds["0fd9"]);
        Assert.Equal(PeripheralBrand.Logitech, PeripheralScan.VendorIds["B58E"]); // Blue Microphones (Logitech)
        // usb.ids names 0060/0063/006c/006d "Stream Deck…"; python-elgato-streamdeck lists the newer models.
        Assert.Equal(17, PeripheralScan.StreamDeckPids.Count);
        Assert.All(new[] { "0060", "0063", "006C", "006D", "0080", "0084", "0086", "008F", "0090", "009A" },
            pid => Assert.Contains(pid, PeripheralScan.StreamDeckPids));
        Assert.Equal(Enum.GetValues<PeripheralBrand>().Length, PeripheralScan.VendorIds.Values.Distinct().Count());
        // Every brand has its program in the catalog, and every peripheral program has a brand.
        Assert.All(AppAdvisor.PeripheralApps.Keys, id => Assert.NotNull(AppCatalog.Find(id)));
        Assert.Equal(Enum.GetValues<PeripheralBrand>().Length, AppAdvisor.PeripheralApps.Values.Distinct().Count());
    }

    [Fact]
    public void Peripheral_scan_without_usb_ids_is_unknown()
    {
        Assert.False(PeripheralScan.Classify([]).Known);
        Assert.False(PeripheralScan.Classify([new(null, null), new("", "HIDClass"), new(@"PCI\VEN_8086&DEV_A780", "System"),
            new(@"ACPI\PNP0303\4&1", "Keyboard")]).Known);

        var none = PeripheralScan.Classify([new(@"USB\VID_045E&PID_028E\1", "XnaComposite"), new(@"PCI\VEN_10DE&DEV_2684", "Display")]);
        Assert.True(none.Known);
        Assert.Empty(none.Brands);

        // A composite parent ("USB" class) does not count, its HID child does; a Corsair flash drive never does.
        var some = PeripheralScan.Classify([new(@"USB\VID_046D&PID_C539\1", "USB"), new(@"HID\VID_046D&PID_C539&MI_01\2", "HIDClass"),
            new(@"HID\VID_0FD9&PID_0080\3", "HIDClass"), new(@"USB\VID_1B1C&PID_1A03\4", "USB"), new(@"USB\VID_0FD9&PID_006A&MI_00\5", "MEDIA")]);
        Assert.True(some.Known);
        Assert.True(some.Brands.SetEquals([PeripheralBrand.Logitech, PeripheralBrand.Elgato]));
        Assert.False(PeripheralScanResult.Unknown.Known);

        // Only a maker's non-peripheral hardware: the scan worked, the brand is not there.
        var drive = PeripheralScan.Classify([new(@"USB\VID_1B1C&PID_1A03\4", "USB"), new(@"USB\VID_0FD9&PID_006A&MI_00\5", "MEDIA")]);
        Assert.True(drive.Known);
        Assert.Empty(drive.Brands);
    }

    // ─────────────────────────── card and confirmation ───────────────────────────

    [Fact]
    public void Card_shows_the_advice_first_only_when_known_and_not_installed()
    {
        var physx = new AppItemViewModel(E("Nvidia.PhysX")) { Advice = Assess("Nvidia.PhysX", Pc()) };
        Assert.Null(physx.ShownAdvice);                       // state not known yet
        Assert.Equal("ПРОВЕРКА…", physx.DisplayTags[0].Text);
        Assert.False(physx.IsDimmed);

        physx.IsKnown = true;
        Assert.Equal(AdviceLevel.NotNeeded, physx.ShownAdvice!.Level);
        Assert.Equal("НЕ НУЖНО", physx.DisplayTags[0].Text);
        Assert.Equal(TagKind.Muted, physx.DisplayTags[0].Kind);
        Assert.Equal("Видеокарта NVIDIA не найдена.", physx.DisplayTags[0].ToolTip);
        Assert.True(physx.IsDimmed);
        Assert.True(physx.HasAdviceCaption);
        Assert.False(physx.IsSuggested);
        // One advice tag, no separate "NVIDIA НЕ НАЙДЕНА" badge.
        Assert.Single(physx.DisplayTags, t => t.Text is "НУЖНО" or "РЕКОМЕНДУЕТСЯ" or "ПО ЖЕЛАНИЮ" or "ПОКА НЕ НУЖНО" or "НЕ НУЖНО");
        Assert.DoesNotContain(physx.DisplayTags, t => t.Text.Contains("НЕ НАЙДЕНА", StringComparison.Ordinal));

        physx.InstalledVersion = "9.23";
        Assert.Null(physx.ShownAdvice);
        Assert.StartsWith("УСТАНОВЛЕНО", physx.DisplayTags[0].Text);
        Assert.False(physx.IsDimmed);
        Assert.False(physx.HasAdviceCaption);
    }

    [Fact]
    public void Card_advice_looks()
    {
        var vc = new AppItemViewModel(E("Microsoft.VCRedist.2015+.x64")) { IsKnown = true, Advice = Assess("Microsoft.VCRedist.2015+.x64", Pc()) };
        Assert.Equal(TagKind.Solid, vc.DisplayTags[0].Kind);
        Assert.True(vc.IsSuggested);
        Assert.True(vc.HasAdviceCaption);

        var steam = new AppItemViewModel(E("Valve.Steam")) { IsKnown = true, Advice = Assess("Valve.Steam", Pc()) };
        Assert.Equal("РЕКОМЕНДУЕТСЯ", steam.DisplayTags[0].Text);
        Assert.Equal(TagKind.Normal, steam.DisplayTags[0].Kind);
        Assert.True(steam.IsSuggested);

        // "ПО ЖЕЛАНИЮ": tag with a tooltip, no caption line.
        var epic = new AppItemViewModel(E("EpicGames.EpicGamesLauncher")) { IsKnown = true, Advice = Assess("EpicGames.EpicGamesLauncher", Pc()) };
        Assert.Equal("ПО ЖЕЛАНИЮ", epic.DisplayTags[0].Text);
        Assert.Equal(AppTexts.OptionalToolTip, epic.DisplayTags[0].ToolTip);
        Assert.False(epic.HasAdviceCaption);
        Assert.False(epic.IsSuggested);

        // A suggestion winget cannot install is not counted in «РЕКОМЕНДУЕМ».
        var medal = new AppItemViewModel(E("MedalB.V.Medal")) { IsKnown = true, Advice = new AppAdvice(AdviceLevel.Recommended, "Тест.") };
        Assert.False(medal.IsSuggested);

        var edge = new AppItemViewModel(E("Microsoft.Edge")) { IsKnown = true, Advice = new AppAdvice(AdviceLevel.Recommended, "Тест.") };
        Assert.Null(edge.ShownAdvice);
    }

    [Fact]
    public void Recommended_tab_holds_only_suggested_cards()
    {
        var tab = new CategoryFilterViewModel(null, "РЕКОМЕНДУЕМ", isRecommended: true);
        var all = new CategoryFilterViewModel(null, "ВСЕ");
        var runtimes = new CategoryFilterViewModel(AppCategory.Runtimes, "БИБЛИОТЕКИ ДЛЯ ИГР");
        var vc = new AppItemViewModel(E("Microsoft.VCRedist.2015+.x64")) { IsKnown = true, Advice = Assess("Microsoft.VCRedist.2015+.x64", Pc()) };
        var xna = new AppItemViewModel(E("Microsoft.XNARedist")) { IsKnown = true, Advice = Assess("Microsoft.XNARedist", Pc()) };

        Assert.True(tab.Includes(vc));
        Assert.False(tab.Includes(xna));
        Assert.True(all.Includes(xna));
        Assert.True(runtimes.Includes(xna));
        Assert.NotNull(tab.ToolTip);
        Assert.Null(all.ToolTip);

        vc.InstalledVersion = "14.44";
        Assert.False(tab.Includes(vc));
    }

    [Fact]
    public void Confirmation_lists_discouraged_installs_with_reasons()
    {
        var pc = Pc();
        var plan = new (CatalogEntry, AppAdvice?)[]
        {
            (E("Valve.Steam"), Assess("Valve.Steam", pc)),
            (E("Microsoft.XNARedist"), Assess("Microsoft.XNARedist", pc)),
            (E("Nvidia.PhysX"), Assess("Nvidia.PhysX", pc)),
            (E("EpicGames.EpicGamesLauncher"), Assess("EpicGames.EpicGamesLauncher", pc)),
            (E("Discord.Discord"), null),
        };
        var notes = AppTexts.AdviceNotes(plan);
        Assert.Equal(2, notes.Count);
        Assert.Equal("НЕ НУЖНО — NVIDIA PhysX: Видеокарта NVIDIA не найдена.", notes[0]);
        Assert.StartsWith("ПОКА НЕ НУЖНО — XNA Framework 4.0: ", notes[1]);

        Assert.True(AppTexts.NotedEntries(plan).SetEquals([E("Microsoft.XNARedist"), E("Nvidia.PhysX")]));

        Assert.Equal("", AppTexts.AdviceLead(0));
        Assert.Contains("1 пакет", AppTexts.AdviceLead(1));
        Assert.Contains("его", AppTexts.AdviceLead(1));
        Assert.Contains("2 пакета", AppTexts.AdviceLead(2));
        Assert.Contains("всё равно можно", AppTexts.AdviceLead(5));
    }

    [Fact]
    public void Confirmation_does_not_repeat_the_advice_in_the_warnings()
    {
        var pc = Pc();
        var items = new[] { "RARLab.WinRAR", "Microsoft.Sysinternals.Autoruns", "Microsoft.DotNet.DesktopRuntime.6", "AnyDesk.AnyDesk",
                            "Microsoft.DotNet.DesktopRuntime.8" }
            .Select(id => new AppItemViewModel(E(id)) { IsKnown = true, Advice = Assess(id, pc) })
            .ToList();
        var noted = AppTexts.NotedEntries(items.Select(i => (i.Entry, i.ShownAdvice)));
        Assert.DoesNotContain(E("Microsoft.DotNet.DesktopRuntime.8"), noted); // ПО ЖЕЛАНИЮ before its end date: no note

        var all = AppTexts.Warnings(items);
        var deduped = AppTexts.Warnings(items, noted);
        Assert.Contains(all, w => w.StartsWith("WinRAR: платная", StringComparison.Ordinal));
        Assert.Contains(all, w => w.StartsWith("Autoruns: инструмент", StringComparison.Ordinal));
        Assert.DoesNotContain(deduped, w => w.StartsWith("WinRAR:", StringComparison.Ordinal));
        Assert.DoesNotContain(deduped, w => w.StartsWith("Autoruns: инструмент", StringComparison.Ordinal));
        Assert.DoesNotContain(deduped, w => w.StartsWith(".NET Desktop Runtime 6:", StringComparison.Ordinal));
        // ВНИМАНИЕ lines always stay, and so do facts the reason does not cover and lines of packages without a note.
        Assert.Contains(deduped, w => w.StartsWith("ВНИМАНИЕ — AnyDesk:", StringComparison.Ordinal));
        Assert.Contains(deduped, w => w.StartsWith("Autoruns: портативная", StringComparison.Ordinal));
        Assert.Contains(deduped, w => w.StartsWith(".NET Desktop Runtime 8:", StringComparison.Ordinal));
    }

    [Fact]
    public void Kit_tooltip_names_what_is_in_and_what_is_left_out()
    {
        var pc = Pc(WebView2);
        var kit = AppAdvisor.Kit(AppCatalog.ForBuild(pc.OsBuild), pc);
        var left = AppCatalog.ForBuild(pc.OsBuild).Where(e => e.Category == AppCategory.Runtimes && e.CanInstall && !kit.Contains(e)).ToList();
        var text = AppTexts.KitToolTip(kit, left);
        Assert.Contains(".NET Desktop Runtime 10", text);
        Assert.Contains("Не входят", text);
        Assert.Contains(".NET Desktop Runtime 6", text);
        Assert.Contains("XNA Framework 4.0", text);
        Assert.DoesNotContain("Не входят", AppTexts.KitToolTip(kit, []));
    }
}
