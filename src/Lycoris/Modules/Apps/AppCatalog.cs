using Lycoris.Core;

namespace Lycoris.Modules.Apps;

/// <summary>Catalog sections, in the order the page shows them.</summary>
public enum AppCategory { Launchers, Runtimes, GamerTools, Communication, Browsers, Streaming, Utilities }

public enum PackageSource { Winget, MsStore }

/// <summary>Install scopes declared by the manifest. <see cref="Any"/> = none declared (never pass --scope).</summary>
public enum InstallScope { Machine, User, Both, Any }

/// <summary>Flags from docs/research/apps_catalog.md §8.</summary>
[Flags]
public enum AppFlags
{
    None = 0,
    /// <summary>Run winget unelevated (elevationProhibited manifests, e.g. Spotify → 0x8A150056 when elevated).</summary>
    NoElevation = 1 << 0,
    /// <summary>Skipped by "upgrade --all"; the app updates itself.</summary>
    ExplicitUpgrade = 1 << 1,
    KernelDriver = 1 << 2,
    /// <summary>Installs anti-cheat, or may trigger it.</summary>
    Anticheat = 1 << 3,
    Reboot = 1 << 4,
    Expert = 1 << 5,
    Paid = 1 << 6,
    Trialware = 1 << 7,
    /// <summary>Runtime past (or about to reach) end of support.</summary>
    Eol = 1 << 8,
    Preinstalled = 1 << 9,
    Win10Only = 1 << 10,
    Portable = 1 << 11,
    RemoteAccess = 1 << 12,
    /// <summary>Install with --installer-type exe (DirectX: the default pick is an MSIX framework without D3DX9/XAudio 2.7).</summary>
    ForceExe = 1 << 13,
    /// <summary>One package id per game region.</summary>
    Region = 1 << 14,
    NvidiaOnly = 1 << 15,
}

public sealed record AppVariant(string Label, string Id)
{
    public override string ToString() => Label;
}

/// <summary>
/// The vendor's own installer link, for the direct download through <see cref="VendorDownloads"/> when winget cannot
/// install the package (hash mismatch after the vendor replaced the file behind an unversioned link, or a manifest
/// winget-pkgs has not caught up with yet). Never a bypass of a hash: the file must carry a valid signature of one
/// of <paramref name="Signers"/>.
/// </summary>
/// <param name="Url">Official HTTPS link to the .exe on the vendor's download server.</param>
/// <param name="Signers">Organizations whose valid Authenticode signature the file must carry.</param>
public sealed record VendorInstaller(string Url, IReadOnlyList<string> Signers)
{
    /// <summary>Hosts the file may come from (the link's host plus documented redirect targets). Empty = the link's host.</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    public IReadOnlyList<string> AllowedHosts =>
        Hosts.Count > 0 ? Hosts : Uri.TryCreate(Url, UriKind.Absolute, out var u) ? [u.Host] : [];

    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : "";

    /// <summary>The download request, or null when the link, file name or signer list is not acceptable.</summary>
    public VendorDownloadRequest? ToRequest(string label)
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var url) || VendorDownloads.SafeFileName(url) is not { } file) return null;
        // App links are unversioned (the vendor swaps the file behind them): always download the current file.
        var request = new VendorDownloadRequest(url, file, AllowedHosts, Signers, VendorDownloads.AppsDir) { Label = label, ReuseVerifiedCopy = false };
        return VendorDownloads.Validate(request) is null ? request : null;
    }
}

/// <param name="Tag">Short singular label used as a tag on cards.</param>
public sealed record CategoryInfo(AppCategory Category, string Key, string Title, string Description, string Tag)
{
    /// <summary>Shorter title for the category tab, so all tabs fit one row on a 1366–1440 px window.</summary>
    public string? ShortTitle { get; init; }

    public string TabTitle => ShortTitle ?? Title;
}

/// <summary>One verified winget / Microsoft Store package.</summary>
public sealed record CatalogEntry(
    string Id, string Name, AppCategory Category, PackageSource Source, string Description, string Website,
    InstallScope Scope, AppFlags Flags)
{
    /// <summary>Regional package ids (Riot games). The first one is the default and equals <see cref="Id"/>.</summary>
    public IReadOnlyList<AppVariant> Variants { get; init; } = [];

    /// <summary>
    /// winget cannot install this package right now (see <see cref="WebsiteReason"/>): the page sends the user to the
    /// official download page — or, with <see cref="VendorInstaller"/>, downloads the signed vendor installer directly.
    /// </summary>
    public bool WebsiteOnly { get; init; }

    /// <summary>Plain-language reason for <see cref="WebsiteOnly"/>, shown on the card.</summary>
    public string? WebsiteReason { get; init; }

    /// <summary>Research: Microsoft Edge is a preinstalled system component — never offer install or uninstall.</summary>
    public bool SystemComponent { get; init; }

    /// <summary>
    /// winget --location template for manifests with InstallLocationRequired (Battle.net): winget cannot ask for the
    /// folder with --disable-interactivity and stops with 0x8A15005F. A known root variable + folder names,
    /// expanded at run time (<see cref="WingetClient.IsValidLocationTemplate"/>).
    /// </summary>
    public string? InstallLocation { get; init; }

    /// <summary>Official installer for the direct, signature-checked download when winget's manifest is outdated.</summary>
    public VendorInstaller? VendorInstaller { get; init; }

    /// <summary>Oldest Windows build the package installs on (0 = no limit), e.g. .NET Framework 4.8.1 needs 20H2 (19042).</summary>
    public int MinOsBuild { get; init; }

    public string SourceName => Source == PackageSource.MsStore ? "msstore" : "winget";

    public bool Has(AppFlags flag) => (Flags & flag) == flag;

    /// <summary>
    /// When Lycoris itself runs elevated, these packages are started de-elevated through the desktop shell:
    /// elevation-prohibited installers, Store apps, user-scope installers and winget portables (research §13: they would
    /// otherwise land in the elevating account's profile or fail with 0x8A150056; winget installs portables per user).
    /// </summary>
    public bool RunsUnelevated => Has(AppFlags.NoElevation) || Source == PackageSource.MsStore || Scope == InstallScope.User
                                  || (Has(AppFlags.Portable) && !WebsiteOnly);

    /// <summary>Updates itself (EdgeUpdate, built-in updaters) — "ОБНОВИТЬ ВСЁ" leaves it alone.</summary>
    public bool SelfUpdating => Has(AppFlags.Preinstalled) || Has(AppFlags.ExplicitUpgrade) || SystemComponent || WebsiteOnly;

    /// <summary>Can be offered for "winget upgrade". DirectX (June 2010) is a frozen runtime with nothing to upgrade.</summary>
    public bool CanUpgrade => !SelfUpdating && !Has(AppFlags.ForceExe);

    /// <summary>Can be installed with winget from this page at all.</summary>
    public bool CanInstall => !SystemComponent && !WebsiteOnly;

    /// <summary>Every package id that represents this entry (all regional variants).</summary>
    public IEnumerable<string> AllIds => Variants.Count > 0 ? Variants.Select(v => v.Id) : [Id];

    public WingetPackageRequest CreateRequest(string packageId) =>
        new(packageId, SourceName) { InstallerType = Has(AppFlags.ForceExe) ? "exe" : null, Location = InstallLocation };

    /// <summary>
    /// Folder for the one retry after winget answered 0x8A15005F for a package without a catalog location:
    /// "%LOCALAPPDATA%\Programs\&lt;Name&gt;" for packages installed per user, "%ProgramFiles%\&lt;Name&gt;" otherwise.
    /// </summary>
    public string DefaultLocationTemplate =>
        (RunsUnelevated ? @"%LOCALAPPDATA%\Programs\" : @"%ProgramFiles%\") + FolderName(Name, Id);

    /// <summary>Plain folder name from the program name ("osu!(lazer)" → "osu(lazer)"); falls back to the package id.</summary>
    internal static string FolderName(string name, string id)
    {
        static string Clean(string s)
        {
            var kept = new string(s.Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '+' or '(' or ')' or '-').ToArray());
            kept = string.Join(' ', kept.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var start = 0;
            while (start < kept.Length && !char.IsAsciiLetterOrDigit(kept[start])) start++;
            kept = kept[start..];
            if (kept.Length > 64) kept = kept[..64];
            return kept.TrimEnd('.', ' ');
        }
        var n = Clean(name);
        return n.Length >= 2 && char.IsAsciiLetterOrDigit(n[0]) ? n : Clean(id);
    }
}

/// <summary>
/// The verified package catalog from docs/research/apps_catalog.md (JSON block, §8). Only entries the research
/// verified with "winget show" are listed; excluded items (Vesktop, Chromium, AMD "Cloud Edition", Oracle Java,
/// BleachBit, PC Manager, Skype, Vulkan RT…) are deliberately absent.
/// Vendor driver tools (NVIDIA App, Intel DSA) are offered only on the Drivers page (DriverSources), not here.
/// </summary>
public static class AppCatalog
{
    public static IReadOnlyList<CategoryInfo> Categories { get; } =
    [
        new(AppCategory.Launchers, "launchers", Loc.T("ЛАУНЧЕРЫ"), Loc.T("Магазины и клиенты игровых платформ"), Loc.T("ЛАУНЧЕР")),
        new(AppCategory.Runtimes, "runtimes", Loc.T("БИБЛИОТЕКИ ДЛЯ ИГР"), Loc.T("Среды выполнения, без которых игры не запускаются"), Loc.T("БИБЛИОТЕКА")) { ShortTitle = Loc.T("БИБЛИОТЕКИ") },
        new(AppCategory.GamerTools, "gamertools", Loc.T("ИНСТРУМЕНТЫ ГЕЙМЕРА"), Loc.T("Мониторинг, тесты, разгон и диагностика"), Loc.T("ИНСТРУМЕНТ")) { ShortTitle = Loc.T("ИНСТРУМЕНТЫ") },
        new(AppCategory.Communication, "communication", Loc.T("ОБЩЕНИЕ"), Loc.T("Голосовые чаты и мессенджеры"), Loc.T("ОБЩЕНИЕ")),
        new(AppCategory.Browsers, "browsers", Loc.T("БРАУЗЕРЫ"), Loc.T("Браузеры от разработчиков, без сборок третьих лиц"), Loc.T("БРАУЗЕР")),
        new(AppCategory.Streaming, "streaming", Loc.T("СТРИМИНГ И ЗАПИСЬ"), Loc.T("Запись, трансляции, звук и ПО для периферии"), Loc.T("СТРИМИНГ")) { ShortTitle = Loc.T("СТРИМИНГ") },
        new(AppCategory.Utilities, "utilities", Loc.T("УТИЛИТЫ"), Loc.T("Архиваторы, плееры, поиск и полезные мелочи"), Loc.T("УТИЛИТА")),
    ];

    public static CategoryInfo Info(AppCategory category) => Categories.First(c => c.Category == category);

    // "НАБОР ГЕЙМЕРА" is computed from the install advice for this PC: AppAdvisor.Kit.

    /// <summary>App Installer (winget itself) is never upgraded by Lycoris: updating it replaces winget while it runs.</summary>
    public const string AppInstallerId = "Microsoft.AppInstaller";

    /// <summary>Microsoft Store page of App Installer (winget).</summary>
    public const string AppInstallerStoreUri = "ms-windows-store://pdp/?productid=9NBLGGH4NNS1";

    private const AppCategory L = AppCategory.Launchers;
    private const AppCategory R = AppCategory.Runtimes;
    private const AppCategory G = AppCategory.GamerTools;
    private const AppCategory C = AppCategory.Communication;
    private const AppCategory B = AppCategory.Browsers;
    private const AppCategory S = AppCategory.Streaming;
    private const AppCategory U = AppCategory.Utilities;

    private const InstallScope Machine = InstallScope.Machine;
    private const InstallScope User = InstallScope.User;
    private const InstallScope Both = InstallScope.Both;
    private const InstallScope Any = InstallScope.Any;

    private static CatalogEntry W(string id, string name, AppCategory cat, string desc, string site, InstallScope scope, AppFlags flags = AppFlags.None) =>
        new(id, name, cat, PackageSource.Winget, desc, site, scope, flags);

    private static CatalogEntry Store(string id, string name, AppCategory cat, string desc, string site, InstallScope scope, AppFlags flags = AppFlags.None) =>
        new(id, name, cat, PackageSource.MsStore, desc, site, scope, flags);

    private static IReadOnlyList<AppVariant> Regions(string prefix, params string[] regions) =>
        regions.Select(r => new AppVariant(r, $"{prefix}.{r}")).ToList();

    public static IReadOnlyList<CatalogEntry> All { get; } =
    [
        // ───────────── Launchers ─────────────
        W("Valve.Steam", "Steam", L, Loc.T("Крупнейший магазин и лаунчер игр для ПК"), "https://store.steampowered.com/about/", Machine),
        W("EpicGames.EpicGamesLauncher", "Epic Games Launcher", L, Loc.T("Магазин Epic Games с еженедельными бесплатными играми"), "https://epicgames.com/download", Machine),
        W("GOG.Galaxy", "GOG GALAXY", L, Loc.T("Лаунчер GOG: игры без DRM и объединённая библиотека"), "https://www.gog.com/galaxy", Machine),
        W("ElectronicArts.EADesktop", "EA app", L, Loc.T("Официальный лаунчер игр Electronic Arts"), "https://www.ea.com/ea-app", Machine),
        // Unversioned "latest" link: every Ubisoft release breaks the winget hash until the bot PR merges.
        W("Ubisoft.Connect", "Ubisoft Connect", L, Loc.T("Лаунчер и магазин игр Ubisoft"), "https://ubisoftconnect.com/", Machine) with
        {
            VendorInstaller = new("https://static3.cdn.ubi.com/orbit/launcher_installer/UbisoftConnectInstaller.exe",
                ["Ubisoft Entertainment", "Ubisoft Entertainment Sweden AB", "Ubisoft Entertainment SA"]),
        },
        // Manifest: InstallLocationRequired (--installpath), default %PROGRAMFILES(X86)%\Battle.net (x86 installer).
        W("Blizzard.BattleNet", "Battle.net", L, Loc.T("Лаунчер игр Blizzard и Activision"), "https://download.battle.net/en-us/desktop", Machine) with
        {
            InstallLocation = @"%ProgramFiles(x86)%\Battle.net",
        },
        W("RockstarGames.Launcher", "Rockstar Games Launcher", L, Loc.T("Лаунчер игр Rockstar (GTA, RDR)"), "https://socialclub.rockstargames.com/rockstar-games-launcher", Any),
        W("Amazon.Games", "Amazon Games", L, Loc.T("Лаунчер Amazon и Prime Gaming"), "https://gaming.amazon.com/home", User),
        W("Wargaming.GameCenter", "Wargaming.net Game Center", L, Loc.T("Лаунчер игр Wargaming"), "https://wargaming.net/wgc", Any),
        // All regional manifests: RequireExplicitUpgrade — Riot Client updates the game itself.
        W("RiotGames.LeagueOfLegends.EUW", "League of Legends", L, Loc.T("League of Legends вместе с Riot Client, выбор региона"), "https://www.leagueoflegends.com/", User,
            AppFlags.Region | AppFlags.Anticheat | AppFlags.KernelDriver | AppFlags.Reboot | AppFlags.ExplicitUpgrade) with
        {
            Variants = Regions("RiotGames.LeagueOfLegends", "EUW", "EUNE", "RU", "NA", "BR", "JP", "KR", "LA1", "LA2", "OC1", "TR", "TW2", "VN2", "SG2", "PBE"),
        },
        W("RiotGames.Valorant.EU", "VALORANT", L, Loc.T("Тактический шутер Riot вместе с Riot Client и Vanguard"), "https://playvalorant.com/", User,
            AppFlags.Region | AppFlags.Anticheat | AppFlags.KernelDriver | AppFlags.Reboot | AppFlags.ExplicitUpgrade) with
        {
            Variants = Regions("RiotGames.Valorant", "EU", "AP", "BR", "KR", "LATAM", "NA"),
        },
        Store("9MV0B5HZVK9Z", "Xbox", L, Loc.T("Приложение Xbox и PC Game Pass"), "https://apps.microsoft.com/detail/9MV0B5HZVK9Z", User),
        W("Mojang.MinecraftLauncher", "Minecraft Launcher", L, Loc.T("Официальный лаунчер Minecraft от Mojang"), "https://www.minecraft.net/download", Machine),
        W("PrismLauncher.PrismLauncher", "Prism Launcher", L, Loc.T("Открытый лаунчер Minecraft с модами и несколькими сборками"), "https://prismlauncher.org/", User),
        W("Overwolf.CurseForge", "CurseForge", L, Loc.T("Менеджер модов и сборок для игр"), "https://curseforge.overwolf.com/", Both),
        W("ppy.osu", "osu!(lazer)", L, Loc.T("Ритм-игра osu!"), "https://osu.ppy.sh/", User),
        W("Roblox.Roblox", "Roblox Player", L, Loc.T("Клиент Roblox (установщик запускается отдельно)"), "https://www.roblox.com/download", Any, AppFlags.Portable) with
        {
            WebsiteOnly = true,
            WebsiteReason = Loc.T("winget только скачивает установщик Roblox — запускать его всё равно придётся вручную. Надёжнее скачать с официального сайта."),
        },
        W("Playnite.Playnite", "Playnite", L, Loc.T("Единая библиотека всех лаунчеров"), "https://playnite.link/", User),
        W("HeroicGamesLauncher.HeroicGamesLauncher", "Heroic", L, Loc.T("Открытый клиент для Epic, GOG и Amazon"), "https://heroicgameslauncher.com/", Both),
        W("ItchIo.Itch", "itch", L, Loc.T("Клиент инди-магазина itch.io"), "https://itch.io/app", User),
        W("Nvidia.GeForceNow", "GeForce NOW", L, Loc.T("Облачный гейминг NVIDIA"), "https://www.nvidia.com/en-us/geforce-now", User),

        // ───────────── Communication ─────────────
        W("Discord.Discord", "Discord", C, Loc.T("Голосовой и текстовый чат для геймеров"), "https://discord.com/download", User, AppFlags.NoElevation | AppFlags.ExplicitUpgrade),
        W("Telegram.TelegramDesktop", "Telegram Desktop", C, Loc.T("Мессенджер Telegram для ПК"), "https://desktop.telegram.org/", User, AppFlags.NoElevation),
        W("TeamSpeakSystems.TeamSpeakClient", "TeamSpeak 3", C, Loc.T("Голосовая связь с низкой задержкой"), "https://www.teamspeak.com/", Both),
        W("Mumble.Mumble.Client", "Mumble", C, Loc.T("Открытый голосовой чат с низкой задержкой"), "https://www.mumble.info/", Machine),
        W("Zoom.Zoom", "Zoom Workplace", C, Loc.T("Видеозвонки и конференции"), "https://zoom.us/", Machine),
        Store("9NKSQGP7F2NH", "WhatsApp", C, Loc.T("Мессенджер WhatsApp для Windows"), "https://apps.microsoft.com/detail/9NKSQGP7F2NH", User),
        W("OpenWhisperSystems.Signal", "Signal", C, Loc.T("Защищённый мессенджер"), "https://www.signal.org/", User, AppFlags.NoElevation),
        W("Rakuten.Viber", "Viber", C, Loc.T("Мессенджер и звонки Viber"), "https://www.viber.com/", Machine),

        // ───────────── Browsers ─────────────
        W("Google.Chrome", "Google Chrome", B, Loc.T("Браузер Google"), "https://www.google.com/chrome/", Machine),
        W("Mozilla.Firefox.ru", "Mozilla Firefox (ru)", B, Loc.T("Браузер Firefox на русском языке"), "https://www.mozilla.org/ru/firefox/", Machine),
        W("Brave.Brave", "Brave", B, Loc.T("Браузер со встроенной блокировкой рекламы"), "https://brave.com/download", Both),
        W("Opera.OperaGX", "Opera GX", B, Loc.T("Игровой браузер с лимитами CPU и RAM"), "https://www.opera.com/gx", Both),
        W("Opera.Opera", "Opera", B, Loc.T("Браузер Opera"), "https://www.opera.com/browsers/opera", Both),
        W("Microsoft.Edge", "Microsoft Edge", B, Loc.T("Системный браузер Windows (уже установлен)"), "https://www.microsoft.com/edge", Machine, AppFlags.Preinstalled) with { SystemComponent = true },
        W("Vivaldi.Vivaldi", "Vivaldi", B, Loc.T("Гибко настраиваемый браузер"), "https://vivaldi.com/desktop/", Both),
        W("Yandex.Browser", Loc.T("Яндекс Браузер"), B, Loc.T("Браузер Яндекса"), "https://browser.yandex.com/", User, AppFlags.NoElevation),
        W("LibreWolf.LibreWolf", "LibreWolf", B, Loc.T("Приватная сборка Firefox"), "https://librewolf.net/", Machine),
        W("Zen-Team.Zen-Browser", "Zen Browser", B, Loc.T("Минималистичный браузер на базе Firefox"), "https://zen-browser.app/", Machine),

        // ───────────── Runtimes ─────────────
        W("Microsoft.VCRedist.2005.x86", "Visual C++ 2005 (x86)", R, Loc.T("Библиотеки VC++ 2005 для старых игр"), "https://www.microsoft.com/en-US/download/details.aspx?id=26347", Machine),
        W("Microsoft.VCRedist.2005.x64", "Visual C++ 2005 (x64)", R, Loc.T("Библиотеки VC++ 2005 x64"), "https://www.microsoft.com/en-US/download/details.aspx?id=26347", Machine),
        W("Microsoft.VCRedist.2008.x86", "Visual C++ 2008 (x86)", R, Loc.T("Библиотеки VC++ 2008"), "https://www.microsoft.com/en-US/download/details.aspx?id=26368", Machine),
        W("Microsoft.VCRedist.2008.x64", "Visual C++ 2008 (x64)", R, Loc.T("Библиотеки VC++ 2008 x64"), "https://www.microsoft.com/en-US/download/details.aspx?id=26368", Machine),
        W("Microsoft.VCRedist.2010.x86", "Visual C++ 2010 (x86)", R, Loc.T("Библиотеки VC++ 2010"), "https://www.microsoft.com/download/details.aspx?id=26999", Machine),
        W("Microsoft.VCRedist.2010.x64", "Visual C++ 2010 (x64)", R, Loc.T("Библиотеки VC++ 2010 x64"), "https://www.microsoft.com/download/details.aspx?id=26999", Machine),
        W("Microsoft.VCRedist.2012.x86", "Visual C++ 2012 (x86)", R, Loc.T("Библиотеки VC++ 2012"), "https://www.microsoft.com/en-us/download/details.aspx?id=30679", Machine),
        W("Microsoft.VCRedist.2012.x64", "Visual C++ 2012 (x64)", R, Loc.T("Библиотеки VC++ 2012 x64"), "https://www.microsoft.com/en-us/download/details.aspx?id=30679", Machine),
        W("Microsoft.VCRedist.2013.x86", "Visual C++ 2013 (x86)", R, Loc.T("Библиотеки VC++ 2013"), "https://support.microsoft.com/help/4032938", Machine),
        W("Microsoft.VCRedist.2013.x64", "Visual C++ 2013 (x64)", R, Loc.T("Библиотеки VC++ 2013 x64"), "https://support.microsoft.com/help/4032938", Machine),
        W("Microsoft.VCRedist.2015+.x86", "Visual C++ 2015-2022 (x86)", R, Loc.T("Актуальные библиотеки VC++ v14 x86"), "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist", Machine),
        W("Microsoft.VCRedist.2015+.x64", "Visual C++ 2015-2022 (x64)", R, Loc.T("Актуальные библиотеки VC++ v14 x64"), "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist", Machine),
        W("Microsoft.DirectX", "DirectX End-User Runtime", R, Loc.T("D3DX9/10/11, XAudio 2.7, XInput 1.3 для старых игр"), "https://www.microsoft.com/download/details.aspx?id=35", Any, AppFlags.ForceExe),
        W("Microsoft.DotNet.DesktopRuntime.10", ".NET Desktop Runtime 10", R, Loc.T("Среда .NET 10 для настольных приложений (LTS)"), "https://dotnet.microsoft.com", Machine),
        W("Microsoft.DotNet.DesktopRuntime.9", ".NET Desktop Runtime 9", R, Loc.T("Среда .NET 9 (поддержка до 10.11.2026)"), "https://dotnet.microsoft.com", Machine, AppFlags.Eol),
        W("Microsoft.DotNet.DesktopRuntime.8", ".NET Desktop Runtime 8", R, Loc.T("Среда .NET 8 (поддержка до 10.11.2026)"), "https://dotnet.microsoft.com", Machine, AppFlags.Eol),
        W("Microsoft.DotNet.DesktopRuntime.6", ".NET Desktop Runtime 6", R, Loc.T("Среда .NET 6 для старых программ (без поддержки)"), "https://dotnet.microsoft.com/download/dotnet/6.0", Machine, AppFlags.Eol),
        // NDP481 installs only on Windows 10 20H2 (19042) and later; Windows 11 22H2+ ships it.
        W("Microsoft.DotNet.Framework.Runtime", ".NET Framework 4.8.1", R, Loc.T(".NET Framework 4.8.1 (встроен в Windows 11)"), "https://dotnet.microsoft.com/download/dotnet-framework/net481", Any, AppFlags.Win10Only) with
        {
            MinOsBuild = 19042,
        },
        W("Microsoft.XNARedist", "XNA Framework 4.0", R, Loc.T("Среда XNA для инди-игр (Terraria и др.)"), "https://www.microsoft.com/download/details.aspx?id=20914", Any),
        W("CreativeTechnology.OpenAL", "OpenAL", R, Loc.T("Звуковая библиотека OpenAL для старых игр"), "https://openal.org/", Any),
        W("EclipseAdoptium.Temurin.21.JRE", "Java 21 (Temurin JRE)", R, Loc.T("Java 21 для Minecraft 1.20.5–1.21 и модов"), "https://adoptium.net/", Machine),
        W("EclipseAdoptium.Temurin.17.JRE", "Java 17 (Temurin JRE)", R, Loc.T("Java 17 для Minecraft 1.18–1.20.4"), "https://adoptium.net/", Machine),
        W("EclipseAdoptium.Temurin.8.JRE", "Java 8 (Temurin JRE)", R, Loc.T("Java 8 для старых модпаков"), "https://adoptium.net/", Machine),
        Store("9MWPM2CQNLHN", "Gaming Services", R, Loc.T("Игровые службы для Xbox и Game Pass"), "https://apps.microsoft.com/detail/9MWPM2CQNLHN", User),
        W("Nvidia.PhysX", "NVIDIA PhysX", R, Loc.T("Физический движок PhysX для старых игр"), "https://www.nvidia.com/en-us/drivers/physx/physx-9-26-0703-driver/", Machine, AppFlags.NvidiaOnly),
        W("Microsoft.EdgeWebView2Runtime", "WebView2 Runtime", R, Loc.T("Веб-движок для лаунчеров и приложений"), "https://developer.microsoft.com/microsoft-edge/webview2/", Both, AppFlags.Preinstalled),

        // ───────────── Gamer tools ─────────────
        W("Guru3D.Afterburner", "MSI Afterburner", G, Loc.T("Разгон GPU, кривые вентиляторов, мониторинг"), "https://www.msi.com/Landing/afterburner", Machine, AppFlags.Expert),
        W("Guru3D.RTSS", "RivaTuner Statistics Server", G, Loc.T("Оверлей FPS и ограничитель кадров"), "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/", Machine, AppFlags.Anticheat),
        W("REALiX.HWiNFO", "HWiNFO", G, Loc.T("Подробная информация о железе и датчиках"), "https://www.hwinfo.com/", Machine),
        W("CPUID.CPU-Z", "CPU-Z", G, Loc.T("Информация о процессоре, памяти и плате"), "https://www.cpuid.com/softwares/cpu-z.html", Machine),
        W("CPUID.HWMonitor", "HWMonitor", G, Loc.T("Температуры, напряжения и частоты"), "https://www.cpuid.com/softwares/hwmonitor.html", Machine),
        W("TechPowerUp.GPU-Z", "GPU-Z", G, Loc.T("Информация о видеокарте"), "https://www.techpowerup.com/gpuz/", Machine),
        W("CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", G, Loc.T("Здоровье и SMART дисков"), "https://crystalmark.info/en/software/crystaldiskinfo/", Machine),
        W("CrystalDewWorld.CrystalDiskMark", "CrystalDiskMark", G, Loc.T("Тест скорости дисков"), "https://crystalmark.info/en/software/crystaldiskmark/", Machine),
        // Manifests declare no scope (portable / zip-portable): winget installs them per user.
        W("OCBase.OCCT.Personal", "OCCT", G, Loc.T("Стресс-тест стабильности CPU, GPU и RAM"), "https://www.ocbase.com/", Any, AppFlags.Portable | AppFlags.Expert),
        // winget-pkgs is stuck at 7.31 while the unversioned link serves 7.40 (hash mismatch, no open PR): the signed
        // vendor installer is downloaded directly instead.
        W("Resplendence.LatencyMon", "LatencyMon", G, Loc.T("Анализ задержек DPC/ISR (заикания звука и кадров)"), "https://www.resplendence.com/latencymon", Machine) with
        {
            WebsiteOnly = true,
            WebsiteReason = Loc.T("Производитель выложил LatencyMon 7.40 по той же ссылке, а каталог winget всё ещё описывает 7.31 со старой контрольной суммой — winget откажется ставить."),
            VendorInstaller = new("https://www.resplendence.com/download/LatencyMon.exe", ["SOFTREV LABS S.R.L.S."]),
        },
        W("Intel.PresentMon", "Intel PresentMon", G, Loc.T("Замер FPS, frametime и задержек"), "https://game.intel.com/intel-presentmon/", Machine),
        // winget picks the Burn bootstrapper (a real installer), not the portable zip; no scope declared.
        W("CXWorld.CapFrameX", "CapFrameX", G, Loc.T("Запись и анализ frametime"), "https://www.capframex.com/", Any),
        // winget picks the Inno installer (elevationRequired), not the portable zip; no scope declared.
        W("Rem0o.FanControl", "FanControl", G, Loc.T("Гибкое управление вентиляторами"), "https://getfancontrol.com/", Any, AppFlags.KernelDriver | AppFlags.Anticheat),
        W("LibreHardwareMonitor.LibreHardwareMonitor", "LibreHardwareMonitor", G, Loc.T("Открытый мониторинг датчиков"), "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor", Any, AppFlags.Portable | AppFlags.KernelDriver),
        W("Orbmu2k.nvidiaProfileInspector", "NVIDIA Profile Inspector", G, Loc.T("Расширенные профили драйвера NVIDIA"), "https://github.com/Orbmu2k/nvidiaProfileInspector", Any, AppFlags.Portable | AppFlags.Expert | AppFlags.NvidiaOnly),
        W("Wagnardsoft.DisplayDriverUninstaller", "Display Driver Uninstaller", G, Loc.T("Полное удаление видеодрайверов (лучше в безопасном режиме)"), "https://www.wagnardsoft.com/display-driver-uninstaller-ddu", Machine, AppFlags.Expert),
        W("TechPowerUp.NVCleanstall", "NVCleanstall", G, Loc.T("Установка драйвера NVIDIA без лишних компонентов"), "https://www.techpowerup.com/download/techpowerup-nvcleanstall/", Machine, AppFlags.Expert | AppFlags.NvidiaOnly),
        W("SpecialK.SpecialK", "Special K", G, Loc.T("Моддинг-фреймворк: HDR, лимитер кадров (внедряется в игры)"), "https://special-k.info/", Both, AppFlags.Expert | AppFlags.Anticheat),
        // winget picks the Inno installer (Scope: machine, elevatesSelf) on x64.
        W("Geeks3D.FurMark.2", "FurMark 2", G, Loc.T("Стресс-тест видеокарты"), "https://geeks3d.com/furmark/", Machine, AppFlags.Expert),
        W("BitSum.ProcessLasso", "Process Lasso", G, Loc.T("Автоматическое управление приоритетами процессов"), "https://bitsum.com/", Machine, AppFlags.Expert),
        W("Rufus.Rufus", "Rufus", G, Loc.T("Создание загрузочных USB-флешек"), "https://rufus.ie/", Both, AppFlags.Portable),
        Store("9NF8H0H7WMLT", "NVIDIA Control Panel", G, Loc.T("Панель управления NVIDIA (если её нет после установки драйвера)"), "https://apps.microsoft.com/detail/9NF8H0H7WMLT", User, AppFlags.NvidiaOnly),
        W("Microsoft.Sysinternals.ProcessExplorer", "Process Explorer", G, Loc.T("Расширенный диспетчер процессов от Microsoft"), "https://learn.microsoft.com/sysinternals/downloads/process-explorer", Both, AppFlags.Portable),
        W("Microsoft.Sysinternals.Autoruns", "Autoruns", G, Loc.T("Полный контроль автозагрузки"), "https://learn.microsoft.com/sysinternals/downloads/autoruns", Both, AppFlags.Portable | AppFlags.Expert),

        // ───────────── Streaming, recording, peripherals ─────────────
        W("OBSProject.OBSStudio", "OBS Studio", S, Loc.T("Запись и стриминг экрана"), "https://obsproject.com/", Machine),
        W("Streamlabs.Streamlabs", "Streamlabs Desktop", S, Loc.T("Стриминг с готовыми оверлеями и алертами"), "https://streamlabs.com/", Machine),
        // The only manifest (4.2309.0, January 2024) links to an installer that now answers 404.
        W("MedalB.V.Medal", "Medal", S, Loc.T("Запись игровых клипов"), "https://medal.tv/download", User) with
        {
            WebsiteOnly = true,
            WebsiteReason = Loc.T("Каталог winget знает только версию 2024 года, а её установщик удалён с сервера Medal (ошибка 404) — winget не сможет его скачать."),
        },
        W("VB-Audio.Voicemeeter", "Voicemeeter", S, Loc.T("Виртуальный аудиомикшер"), "https://vb-audio.com/Voicemeeter/", Machine, AppFlags.KernelDriver | AppFlags.Reboot),
        W("VB-Audio.Voicemeeter.Banana", "Voicemeeter Banana", S, Loc.T("Расширенный виртуальный аудиомикшер"), "https://vb-audio.com/Voicemeeter/banana.htm", Machine, AppFlags.KernelDriver | AppFlags.Reboot),
        W("SteelSeries.GG", "SteelSeries GG", S, Loc.T("ПО для периферии SteelSeries и Sonar"), "https://steelseries.com/gg", Machine),
        // Unversioned "latest" link: Logitech replaces the file before winget-pkgs updates the hash (0x8A150011).
        W("Logitech.GHUB", "Logitech G HUB", S, Loc.T("ПО для игровой периферии Logitech"), "https://www.logitechg.com/innovation/g-hub.html", Machine) with
        {
            VendorInstaller = new("https://download01.logi.com/web/ftp/pub/techsupport/gaming/lghub_installer.exe", ["Logitech", "Logitech Europe S.A."]),
        },
        W("RazerInc.RazerInstaller.Synapse4", "Razer Synapse 4", S, Loc.T("ПО для периферии Razer"), "https://www.razer.com/synapse-4", Any),
        W("Corsair.iCUE.5", "Corsair iCUE 5", S, Loc.T("ПО для периферии и подсветки Corsair"), "https://www.corsair.com/icue", Machine),
        W("Elgato.StreamDeck", "Elgato Stream Deck", S, Loc.T("ПО для пультов Stream Deck"), "https://www.elgato.com/s/downloads", Machine),
        W("LizardByte.Sunshine", "Sunshine", S, Loc.T("Сервер стриминга игр с ПК (для Moonlight)"), "https://github.com/LizardByte/Sunshine", Machine, AppFlags.Expert),
        // The manifest says Scope: user, but the Burn bundle installs per machine into %ProgramFiles% (and would ask for
        // UAC itself when de-elevated): run it like a machine installer. Lycoris never passes --scope, so this does not
        // change which installer winget picks.
        W("MoonlightGameStreamingProject.Moonlight", "Moonlight", S, Loc.T("Клиент стриминга игр"), "https://moonlight-stream.org/", Machine),

        // ───────────── Utilities ─────────────
        W("7zip.7zip", "7-Zip", U, Loc.T("Бесплатный архиватор"), "https://7-zip.org/", Machine),
        W("RARLab.WinRAR", "WinRAR", U, Loc.T("Архиватор RAR и ZIP"), "https://www.win-rar.com/", Machine, AppFlags.Trialware),
        // MSIX bundle: registered per user, like a Store app — install it as the signed-in user.
        W("M2Team.NanaZip", "NanaZip", U, Loc.T("Современный форк 7-Zip для Windows 11"), "https://github.com/M2Team/NanaZip", User),
        W("Microsoft.PowerToys", "PowerToys", U, Loc.T("Набор утилит Microsoft для продвинутых пользователей"), "https://github.com/microsoft/PowerToys", Both),
        W("voidtools.Everything", "Everything", U, Loc.T("Мгновенный поиск файлов"), "https://www.voidtools.com/", Machine),
        W("ShareX.ShareX", "ShareX", U, Loc.T("Скриншоты и запись экрана"), "https://getsharex.com/", Both),
        W("Notepad++.Notepad++", "Notepad++", U, Loc.T("Текстовый редактор"), "https://notepad-plus-plus.org/", Machine),
        W("VideoLAN.VLC", "VLC", U, Loc.T("Универсальный медиаплеер"), "https://www.videolan.org/vlc/", Machine),
        W("Daum.PotPlayer", "PotPlayer", U, Loc.T("Мощный видеоплеер"), "https://potplayer.tv/", Machine),
        // AIMP moved its builds from /files/windows/ to /files/desktop/: the manifest's links (5.10.2418) answer 404.
        W("AIMP.AIMP", "AIMP", U, Loc.T("Аудиоплеер"), "https://www.aimp.ru", Machine) with
        {
            WebsiteOnly = true,
            WebsiteReason = Loc.T("Ссылки в каталоге winget ведут на старую версию 5.10, которой больше нет на сервере AIMP (ошибка 404), — winget не сможет скачать установщик."),
        },
        W("CodecGuide.K-LiteCodecPack.Standard", "K-Lite Codec Pack Standard", U, Loc.T("Набор кодеков и MPC-HC"), "https://codecguide.com/", Machine),
        W("qBittorrent.qBittorrent", "qBittorrent", U, Loc.T("Открытый торрент-клиент без рекламы"), "https://www.qbittorrent.org/", Machine),
        W("Spotify.Spotify", "Spotify", U, Loc.T("Музыкальный стриминг"), "https://www.spotify.com/download/windows/", User, AppFlags.NoElevation),
        W("Yandex.Music", Loc.T("Яндекс Музыка"), U, Loc.T("Музыкальный сервис Яндекса"), "https://music.yandex.com/", User, AppFlags.NoElevation),
        W("RevoUninstaller.RevoUninstaller", "Revo Uninstaller", U, Loc.T("Удаление программ вместе с остатками"), "https://www.revouninstaller.com/", Machine, AppFlags.Expert),
        W("Klocman.BulkCrapUninstaller", "Bulk Crap Uninstaller", U, Loc.T("Массовое удаление программ"), "https://www.bcuninstaller.com/", Machine, AppFlags.Expert),
        W("AntibodySoftware.WizTree", "WizTree", U, Loc.T("Быстрый анализ занятого места на диске"), "https://diskanalyzer.com/", Machine),
        W("JAMSoftware.TreeSize.Free", "TreeSize Free", U, Loc.T("Анализ размеров папок"), "https://www.jam-software.com/treesize_free", Both),
        W("Rainmeter.Rainmeter", "Rainmeter", U, Loc.T("Виджеты рабочего стола"), "https://www.rainmeter.net/", Machine),
        W("AnyDesk.AnyDesk", "AnyDesk", U, Loc.T("Удалённый доступ (давайте его только тем, кому доверяете)"), "https://anydesk.com/", Machine, AppFlags.RemoteAccess),
        W("TeamViewer.TeamViewer", "TeamViewer", U, Loc.T("Удалённый доступ (давайте его только тем, кому доверяете)"), "https://www.teamviewer.com/", Machine, AppFlags.RemoteAccess),
    ];

    /// <summary>
    /// Entries offered on this machine: Windows-10-only runtimes are hidden where Windows already ships them (Win11 22H2+),
    /// packages whose installer refuses older builds are hidden there (<see cref="CatalogEntry.MinOsBuild"/>).
    /// </summary>
    public static IEnumerable<CatalogEntry> ForBuild(int osBuild) =>
        All.Where(e => (!e.Has(AppFlags.Win10Only) || osBuild < 22621) && osBuild >= e.MinOsBuild);

    public static CatalogEntry? Find(string id) =>
        All.FirstOrDefault(e => e.AllIds.Contains(id, StringComparer.OrdinalIgnoreCase));
}
