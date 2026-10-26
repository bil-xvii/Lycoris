using Monolith.Core;

namespace Monolith.Modules.Apps;

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

/// <param name="Tag">Short singular label used as a tag on cards.</param>
public sealed record CategoryInfo(AppCategory Category, string Key, string Title, string Description, string Tag);

/// <summary>One verified winget / Microsoft Store package.</summary>
public sealed record CatalogEntry(
    string Id, string Name, AppCategory Category, PackageSource Source, string Description, string Website,
    InstallScope Scope, AppFlags Flags)
{
    /// <summary>Regional package ids (Riot games). The first one is the default and equals <see cref="Id"/>.</summary>
    public IReadOnlyList<AppVariant> Variants { get; init; } = [];

    /// <summary>
    /// Research: the Roblox manifest is "portable" — winget only drops RobloxPlayerInstaller.exe into WinGet\Packages,
    /// it still has to be run by hand. The page sends the user to the official download page instead.
    /// </summary>
    public bool WebsiteOnly { get; init; }

    /// <summary>Research: Microsoft Edge is a preinstalled system component — never offer install or uninstall.</summary>
    public bool SystemComponent { get; init; }

    public string SourceName => Source == PackageSource.MsStore ? "msstore" : "winget";

    public bool Has(AppFlags flag) => (Flags & flag) == flag;

    /// <summary>
    /// When MONOLITH itself runs elevated, these packages are started de-elevated through the desktop shell:
    /// elevation-prohibited installers, Store apps and user-scope installers (research §13: they would otherwise
    /// land in the elevating account's profile or fail with 0x8A150056).
    /// </summary>
    public bool RunsUnelevated => Has(AppFlags.NoElevation) || Source == PackageSource.MsStore || Scope == InstallScope.User;

    /// <summary>Updates itself (EdgeUpdate, built-in updaters) — "ОБНОВИТЬ ВСЁ" leaves it alone.</summary>
    public bool SelfUpdating => Has(AppFlags.Preinstalled) || Has(AppFlags.ExplicitUpgrade) || SystemComponent || WebsiteOnly;

    /// <summary>Can be offered for "winget upgrade". DirectX (June 2010) is a frozen runtime with nothing to upgrade.</summary>
    public bool CanUpgrade => !SelfUpdating && !Has(AppFlags.ForceExe);

    /// <summary>Can be installed with winget from this page at all.</summary>
    public bool CanInstall => !SystemComponent && !WebsiteOnly;

    /// <summary>Every package id that represents this entry (all regional variants).</summary>
    public IEnumerable<string> AllIds => Variants.Count > 0 ? Variants.Select(v => v.Id) : [Id];

    public WingetPackageRequest CreateRequest(string packageId) =>
        new(packageId, SourceName) { InstallerType = Has(AppFlags.ForceExe) ? "exe" : null };
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
        new(AppCategory.Launchers, "launchers", "ЛАУНЧЕРЫ", "Магазины и клиенты игровых платформ", "ЛАУНЧЕР"),
        new(AppCategory.Runtimes, "runtimes", "БИБЛИОТЕКИ ДЛЯ ИГР", "Среды выполнения, без которых игры не запускаются", "БИБЛИОТЕКА"),
        new(AppCategory.GamerTools, "gamertools", "ИНСТРУМЕНТЫ ГЕЙМЕРА", "Мониторинг, тесты, разгон и диагностика", "ИНСТРУМЕНТ"),
        new(AppCategory.Communication, "communication", "ОБЩЕНИЕ", "Голосовые чаты и мессенджеры", "ОБЩЕНИЕ"),
        new(AppCategory.Browsers, "browsers", "БРАУЗЕРЫ", "Браузеры от разработчиков, без сборок третьих лиц", "БРАУЗЕР"),
        new(AppCategory.Streaming, "streaming", "СТРИМИНГ И ЗАПИСЬ", "Запись, трансляции, звук и ПО для периферии", "СТРИМИНГ"),
        new(AppCategory.Utilities, "utilities", "УТИЛИТЫ", "Архиваторы, плееры, поиск и полезные мелочи", "УТИЛИТА"),
    ];

    public static CategoryInfo Info(AppCategory category) => Categories.First(c => c.Category == category);

    /// <summary>
    /// "НАБОР ГЕЙМЕРА": every Visual C++ redistributable (x86 + x64), the DirectX End-User Runtime (June 2010)
    /// and the .NET Desktop Runtimes from the catalog.
    /// </summary>
    public static IReadOnlyList<string> GamerKitIds { get; } =
    [
        "Microsoft.VCRedist.2005.x86", "Microsoft.VCRedist.2005.x64",
        "Microsoft.VCRedist.2008.x86", "Microsoft.VCRedist.2008.x64",
        "Microsoft.VCRedist.2010.x86", "Microsoft.VCRedist.2010.x64",
        "Microsoft.VCRedist.2012.x86", "Microsoft.VCRedist.2012.x64",
        "Microsoft.VCRedist.2013.x86", "Microsoft.VCRedist.2013.x64",
        "Microsoft.VCRedist.2015+.x86", "Microsoft.VCRedist.2015+.x64",
        "Microsoft.DirectX",
        "Microsoft.DotNet.DesktopRuntime.10", "Microsoft.DotNet.DesktopRuntime.9",
        "Microsoft.DotNet.DesktopRuntime.8", "Microsoft.DotNet.DesktopRuntime.6",
    ];

    /// <summary>App Installer (winget itself) is never upgraded by MONOLITH: updating it replaces winget while it runs.</summary>
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
        W("Valve.Steam", "Steam", L, "Крупнейший магазин и лаунчер игр для ПК", "https://store.steampowered.com/about/", Machine),
        W("EpicGames.EpicGamesLauncher", "Epic Games Launcher", L, "Магазин Epic Games с еженедельными бесплатными играми", "https://epicgames.com/download", Machine),
        W("GOG.Galaxy", "GOG GALAXY", L, "Лаунчер GOG: игры без DRM и объединённая библиотека", "https://www.gog.com/galaxy", Machine),
        W("ElectronicArts.EADesktop", "EA app", L, "Официальный лаунчер игр Electronic Arts", "https://www.ea.com/ea-app", Machine),
        W("Ubisoft.Connect", "Ubisoft Connect", L, "Лаунчер и магазин игр Ubisoft", "https://ubisoftconnect.com/", Machine),
        W("Blizzard.BattleNet", "Battle.net", L, "Лаунчер игр Blizzard и Activision", "https://download.battle.net/en-us/desktop", Machine),
        W("RockstarGames.Launcher", "Rockstar Games Launcher", L, "Лаунчер игр Rockstar (GTA, RDR)", "https://socialclub.rockstargames.com/rockstar-games-launcher", Any),
        W("Amazon.Games", "Amazon Games", L, "Лаунчер Amazon и Prime Gaming", "https://gaming.amazon.com/home", User),
        W("Wargaming.GameCenter", "Wargaming.net Game Center", L, "Лаунчер игр Wargaming", "https://wargaming.net/wgc", Any),
        W("RiotGames.LeagueOfLegends.EUW", "League of Legends", L, "League of Legends вместе с Riot Client, выбор региона", "https://www.leagueoflegends.com/", User,
            AppFlags.Region | AppFlags.Anticheat | AppFlags.KernelDriver | AppFlags.Reboot) with
        {
            Variants = Regions("RiotGames.LeagueOfLegends", "EUW", "EUNE", "RU", "NA", "BR", "JP", "KR", "LA1", "LA2", "OC1", "TR", "TW2", "VN2", "SG2", "PBE"),
        },
        W("RiotGames.Valorant.EU", "VALORANT", L, "Тактический шутер Riot вместе с Riot Client и Vanguard", "https://playvalorant.com/", User,
            AppFlags.Region | AppFlags.Anticheat | AppFlags.KernelDriver | AppFlags.Reboot) with
        {
            Variants = Regions("RiotGames.Valorant", "EU", "AP", "BR", "KR", "LATAM", "NA"),
        },
        Store("9MV0B5HZVK9Z", "Xbox", L, "Приложение Xbox и PC Game Pass", "https://apps.microsoft.com/detail/9MV0B5HZVK9Z", User),
        W("Mojang.MinecraftLauncher", "Minecraft Launcher", L, "Официальный лаунчер Minecraft от Mojang", "https://www.minecraft.net/download", Machine),
        W("PrismLauncher.PrismLauncher", "Prism Launcher", L, "Открытый лаунчер Minecraft с модами и несколькими сборками", "https://prismlauncher.org/", User),
        W("Overwolf.CurseForge", "CurseForge", L, "Менеджер модов и сборок для игр", "https://curseforge.overwolf.com/", Both),
        W("ppy.osu", "osu!(lazer)", L, "Ритм-игра osu!", "https://osu.ppy.sh/", User),
        W("Roblox.Roblox", "Roblox Player", L, "Клиент Roblox (установщик запускается отдельно)", "https://www.roblox.com/download", Both, AppFlags.Portable) with { WebsiteOnly = true },
        W("Playnite.Playnite", "Playnite", L, "Единая библиотека всех лаунчеров", "https://playnite.link/", User),
        W("HeroicGamesLauncher.HeroicGamesLauncher", "Heroic", L, "Открытый клиент для Epic, GOG и Amazon", "https://heroicgameslauncher.com/", Both),
        W("ItchIo.Itch", "itch", L, "Клиент инди-магазина itch.io", "https://itch.io/app", User),
        W("Nvidia.GeForceNow", "GeForce NOW", L, "Облачный гейминг NVIDIA", "https://www.nvidia.com/en-us/geforce-now", User),

        // ───────────── Communication ─────────────
        W("Discord.Discord", "Discord", C, "Голосовой и текстовый чат для геймеров", "https://discord.com/download", User, AppFlags.NoElevation | AppFlags.ExplicitUpgrade),
        W("Telegram.TelegramDesktop", "Telegram Desktop", C, "Мессенджер Telegram для ПК", "https://desktop.telegram.org/", User, AppFlags.NoElevation),
        W("TeamSpeakSystems.TeamSpeakClient", "TeamSpeak 3", C, "Голосовая связь с низкой задержкой", "https://www.teamspeak.com/", Both),
        W("Mumble.Mumble.Client", "Mumble", C, "Открытый голосовой чат с низкой задержкой", "https://www.mumble.info/", Machine),
        W("Zoom.Zoom", "Zoom Workplace", C, "Видеозвонки и конференции", "https://zoom.us/", Machine),
        Store("9NKSQGP7F2NH", "WhatsApp", C, "Мессенджер WhatsApp для Windows", "https://apps.microsoft.com/detail/9NKSQGP7F2NH", User),
        W("OpenWhisperSystems.Signal", "Signal", C, "Защищённый мессенджер", "https://www.signal.org/", User, AppFlags.NoElevation),
        W("Rakuten.Viber", "Viber", C, "Мессенджер и звонки Viber", "https://www.viber.com/", Machine),

        // ───────────── Browsers ─────────────
        W("Google.Chrome", "Google Chrome", B, "Браузер Google", "https://www.google.com/chrome/", Machine),
        W("Mozilla.Firefox.ru", "Mozilla Firefox (ru)", B, "Браузер Firefox на русском языке", "https://www.mozilla.org/ru/firefox/", Machine),
        W("Brave.Brave", "Brave", B, "Браузер со встроенной блокировкой рекламы", "https://brave.com/download", Both),
        W("Opera.OperaGX", "Opera GX", B, "Игровой браузер с лимитами CPU и RAM", "https://www.opera.com/gx", Both),
        W("Opera.Opera", "Opera", B, "Браузер Opera", "https://www.opera.com/browsers/opera", Both),
        W("Microsoft.Edge", "Microsoft Edge", B, "Системный браузер Windows (уже установлен)", "https://www.microsoft.com/edge", Machine, AppFlags.Preinstalled) with { SystemComponent = true },
        W("Vivaldi.Vivaldi", "Vivaldi", B, "Гибко настраиваемый браузер", "https://vivaldi.com/desktop/", Both),
        W("Yandex.Browser", "Яндекс Браузер", B, "Браузер Яндекса", "https://browser.yandex.com/", User, AppFlags.NoElevation),
        W("LibreWolf.LibreWolf", "LibreWolf", B, "Приватная сборка Firefox", "https://librewolf.net/", Machine),
        W("Zen-Team.Zen-Browser", "Zen Browser", B, "Минималистичный браузер на базе Firefox", "https://zen-browser.app/", Machine),

        // ───────────── Runtimes ─────────────
        W("Microsoft.VCRedist.2005.x86", "Visual C++ 2005 (x86)", R, "Библиотеки VC++ 2005 для старых игр", "https://www.microsoft.com/en-US/download/details.aspx?id=26347", Machine),
        W("Microsoft.VCRedist.2005.x64", "Visual C++ 2005 (x64)", R, "Библиотеки VC++ 2005 x64", "https://www.microsoft.com/en-US/download/details.aspx?id=26347", Machine),
        W("Microsoft.VCRedist.2008.x86", "Visual C++ 2008 (x86)", R, "Библиотеки VC++ 2008", "https://www.microsoft.com/en-US/download/details.aspx?id=26368", Machine),
        W("Microsoft.VCRedist.2008.x64", "Visual C++ 2008 (x64)", R, "Библиотеки VC++ 2008 x64", "https://www.microsoft.com/en-US/download/details.aspx?id=26368", Machine),
        W("Microsoft.VCRedist.2010.x86", "Visual C++ 2010 (x86)", R, "Библиотеки VC++ 2010", "https://www.microsoft.com/download/details.aspx?id=26999", Machine),
        W("Microsoft.VCRedist.2010.x64", "Visual C++ 2010 (x64)", R, "Библиотеки VC++ 2010 x64", "https://www.microsoft.com/download/details.aspx?id=26999", Machine),
        W("Microsoft.VCRedist.2012.x86", "Visual C++ 2012 (x86)", R, "Библиотеки VC++ 2012", "https://www.microsoft.com/en-us/download/details.aspx?id=30679", Machine),
        W("Microsoft.VCRedist.2012.x64", "Visual C++ 2012 (x64)", R, "Библиотеки VC++ 2012 x64", "https://www.microsoft.com/en-us/download/details.aspx?id=30679", Machine),
        W("Microsoft.VCRedist.2013.x86", "Visual C++ 2013 (x86)", R, "Библиотеки VC++ 2013", "https://support.microsoft.com/help/4032938", Machine),
        W("Microsoft.VCRedist.2013.x64", "Visual C++ 2013 (x64)", R, "Библиотеки VC++ 2013 x64", "https://support.microsoft.com/help/4032938", Machine),
        W("Microsoft.VCRedist.2015+.x86", "Visual C++ 2015-2022 (x86)", R, "Актуальные библиотеки VC++ v14 x86", "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist", Machine),
        W("Microsoft.VCRedist.2015+.x64", "Visual C++ 2015-2022 (x64)", R, "Актуальные библиотеки VC++ v14 x64", "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist", Machine),
        W("Microsoft.DirectX", "DirectX End-User Runtime", R, "D3DX9/10/11, XAudio 2.7, XInput 1.3 для старых игр", "https://www.microsoft.com/download/details.aspx?id=35", Any, AppFlags.ForceExe),
        W("Microsoft.DotNet.DesktopRuntime.10", ".NET Desktop Runtime 10", R, "Среда .NET 10 для настольных приложений (LTS)", "https://dotnet.microsoft.com", Machine),
        W("Microsoft.DotNet.DesktopRuntime.9", ".NET Desktop Runtime 9", R, "Среда .NET 9 (поддержка до 10.11.2026)", "https://dotnet.microsoft.com", Machine, AppFlags.Eol),
        W("Microsoft.DotNet.DesktopRuntime.8", ".NET Desktop Runtime 8", R, "Среда .NET 8 (поддержка до 10.11.2026)", "https://dotnet.microsoft.com", Machine, AppFlags.Eol),
        W("Microsoft.DotNet.DesktopRuntime.6", ".NET Desktop Runtime 6", R, "Среда .NET 6 для старых программ (без поддержки)", "https://dotnet.microsoft.com/download/dotnet/6.0", Machine, AppFlags.Eol),
        W("Microsoft.DotNet.Framework.Runtime", ".NET Framework 4.8.1", R, ".NET Framework 4.8.1 (встроен в Windows 11)", "https://dotnet.microsoft.com/download/dotnet-framework/net481", Any, AppFlags.Win10Only),
        W("Microsoft.XNARedist", "XNA Framework 4.0", R, "Среда XNA для инди-игр (Terraria и др.)", "https://www.microsoft.com/download/details.aspx?id=20914", Any),
        W("CreativeTechnology.OpenAL", "OpenAL", R, "Звуковая библиотека OpenAL для старых игр", "https://openal.org/", Any),
        W("EclipseAdoptium.Temurin.21.JRE", "Java 21 (Temurin JRE)", R, "Java 21 для Minecraft 1.20.5+ и модов", "https://adoptium.net/", Machine),
        W("EclipseAdoptium.Temurin.17.JRE", "Java 17 (Temurin JRE)", R, "Java 17 для Minecraft 1.18–1.20.4", "https://adoptium.net/", Machine),
        W("EclipseAdoptium.Temurin.8.JRE", "Java 8 (Temurin JRE)", R, "Java 8 для старых модпаков", "https://adoptium.net/", Machine),
        Store("9MWPM2CQNLHN", "Gaming Services", R, "Игровые службы для Xbox и Game Pass", "https://apps.microsoft.com/detail/9MWPM2CQNLHN", User),
        W("Nvidia.PhysX", "NVIDIA PhysX", R, "Физический движок PhysX для старых игр", "https://www.nvidia.com/en-us/drivers/physx/physx-9-26-0703-driver/", Machine, AppFlags.NvidiaOnly),
        W("Microsoft.EdgeWebView2Runtime", "WebView2 Runtime", R, "Веб-движок для лаунчеров и приложений", "https://developer.microsoft.com/microsoft-edge/webview2/", Both, AppFlags.Preinstalled),

        // ───────────── Gamer tools ─────────────
        W("Guru3D.Afterburner", "MSI Afterburner", G, "Разгон GPU, кривые вентиляторов, мониторинг", "https://www.msi.com/Landing/afterburner", Machine, AppFlags.Expert),
        W("Guru3D.RTSS", "RivaTuner Statistics Server", G, "Оверлей FPS и ограничитель кадров", "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/", Machine, AppFlags.Anticheat),
        W("REALiX.HWiNFO", "HWiNFO", G, "Подробная информация о железе и датчиках", "https://www.hwinfo.com/", Machine),
        W("CPUID.CPU-Z", "CPU-Z", G, "Информация о процессоре, памяти и плате", "https://www.cpuid.com/softwares/cpu-z.html", Machine),
        W("CPUID.HWMonitor", "HWMonitor", G, "Температуры, напряжения и частоты", "https://www.cpuid.com/softwares/hwmonitor.html", Machine),
        W("TechPowerUp.GPU-Z", "GPU-Z", G, "Информация о видеокарте", "https://www.techpowerup.com/gpuz/", Machine),
        W("CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", G, "Здоровье и SMART дисков", "https://crystalmark.info/en/software/crystaldiskinfo/", Machine),
        W("CrystalDewWorld.CrystalDiskMark", "CrystalDiskMark", G, "Тест скорости дисков", "https://crystalmark.info/en/software/crystaldiskmark/", Machine),
        W("OCBase.OCCT.Personal", "OCCT", G, "Стресс-тест стабильности CPU, GPU и RAM", "https://www.ocbase.com/", Both, AppFlags.Portable | AppFlags.Expert),
        W("Resplendence.LatencyMon", "LatencyMon", G, "Анализ задержек DPC/ISR (заикания звука и кадров)", "https://www.resplendence.com/latencymon", Machine),
        W("Intel.PresentMon", "Intel PresentMon", G, "Замер FPS, frametime и задержек", "https://game.intel.com/intel-presentmon/", Machine),
        W("CXWorld.CapFrameX", "CapFrameX", G, "Запись и анализ frametime", "https://www.capframex.com/", Both, AppFlags.Portable),
        W("Rem0o.FanControl", "FanControl", G, "Гибкое управление вентиляторами", "https://getfancontrol.com/", Both, AppFlags.Portable | AppFlags.KernelDriver | AppFlags.Anticheat),
        W("LibreHardwareMonitor.LibreHardwareMonitor", "LibreHardwareMonitor", G, "Открытый мониторинг датчиков", "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor", Both, AppFlags.Portable | AppFlags.KernelDriver),
        W("Orbmu2k.nvidiaProfileInspector", "NVIDIA Profile Inspector", G, "Расширенные профили драйвера NVIDIA", "https://github.com/Orbmu2k/nvidiaProfileInspector", Both, AppFlags.Portable | AppFlags.Expert | AppFlags.NvidiaOnly),
        W("Wagnardsoft.DisplayDriverUninstaller", "Display Driver Uninstaller", G, "Полное удаление видеодрайверов (лучше в безопасном режиме)", "https://www.wagnardsoft.com/display-driver-uninstaller-ddu", Machine, AppFlags.Expert),
        W("TechPowerUp.NVCleanstall", "NVCleanstall", G, "Установка драйвера NVIDIA без лишних компонентов", "https://www.techpowerup.com/download/techpowerup-nvcleanstall/", Machine, AppFlags.Expert | AppFlags.NvidiaOnly),
        W("SpecialK.SpecialK", "Special K", G, "Моддинг-фреймворк: HDR, лимитер кадров (внедряется в игры)", "https://special-k.info/", Both, AppFlags.Expert | AppFlags.Anticheat),
        W("Geeks3D.FurMark.2", "FurMark 2", G, "Стресс-тест видеокарты", "https://geeks3d.com/furmark/", Both, AppFlags.Expert),
        W("BitSum.ProcessLasso", "Process Lasso", G, "Автоматическое управление приоритетами процессов", "https://bitsum.com/", Machine, AppFlags.Expert),
        W("Rufus.Rufus", "Rufus", G, "Создание загрузочных USB-флешек", "https://rufus.ie/", Both, AppFlags.Portable),
        Store("9NF8H0H7WMLT", "NVIDIA Control Panel", G, "Панель управления NVIDIA (если её нет после установки драйвера)", "https://apps.microsoft.com/detail/9NF8H0H7WMLT", User, AppFlags.NvidiaOnly),
        W("Microsoft.Sysinternals.ProcessExplorer", "Process Explorer", G, "Расширенный диспетчер процессов от Microsoft", "https://learn.microsoft.com/sysinternals/downloads/process-explorer", Both, AppFlags.Portable),
        W("Microsoft.Sysinternals.Autoruns", "Autoruns", G, "Полный контроль автозагрузки", "https://learn.microsoft.com/sysinternals/downloads/autoruns", Both, AppFlags.Portable | AppFlags.Expert),

        // ───────────── Streaming, recording, peripherals ─────────────
        W("OBSProject.OBSStudio", "OBS Studio", S, "Запись и стриминг экрана", "https://obsproject.com/", Machine),
        W("Streamlabs.Streamlabs", "Streamlabs Desktop", S, "Стриминг с готовыми оверлеями и алертами", "https://streamlabs.com/", Machine),
        W("MedalB.V.Medal", "Medal", S, "Запись игровых клипов", "https://medal.tv/", User),
        W("VB-Audio.Voicemeeter", "Voicemeeter", S, "Виртуальный аудиомикшер", "https://vb-audio.com/Voicemeeter/", Machine, AppFlags.KernelDriver | AppFlags.Reboot),
        W("VB-Audio.Voicemeeter.Banana", "Voicemeeter Banana", S, "Расширенный виртуальный аудиомикшер", "https://vb-audio.com/Voicemeeter/banana.htm", Machine, AppFlags.KernelDriver | AppFlags.Reboot),
        W("SteelSeries.GG", "SteelSeries GG", S, "ПО для периферии SteelSeries и Sonar", "https://steelseries.com/gg", Machine),
        W("Logitech.GHUB", "Logitech G HUB", S, "ПО для игровой периферии Logitech", "https://www.logitechg.com/innovation/g-hub.html", Machine),
        W("RazerInc.RazerInstaller.Synapse4", "Razer Synapse 4", S, "ПО для периферии Razer", "https://www.razer.com/synapse-4", Any),
        W("Corsair.iCUE.5", "Corsair iCUE 5", S, "ПО для периферии и подсветки Corsair", "https://www.corsair.com/icue", Machine),
        W("Elgato.StreamDeck", "Elgato Stream Deck", S, "ПО для пультов Stream Deck", "https://www.elgato.com/s/downloads", Machine),
        W("LizardByte.Sunshine", "Sunshine", S, "Сервер стриминга игр с ПК (для Moonlight)", "https://github.com/LizardByte/Sunshine", Machine, AppFlags.Expert),
        W("MoonlightGameStreamingProject.Moonlight", "Moonlight", S, "Клиент стриминга игр", "https://moonlight-stream.org/", User),

        // ───────────── Utilities ─────────────
        W("7zip.7zip", "7-Zip", U, "Бесплатный архиватор", "https://7-zip.org/", Machine),
        W("RARLab.WinRAR", "WinRAR", U, "Архиватор RAR и ZIP", "https://www.win-rar.com/", Machine, AppFlags.Trialware),
        W("M2Team.NanaZip", "NanaZip", U, "Современный форк 7-Zip для Windows 11", "https://github.com/M2Team/NanaZip", Both),
        W("Microsoft.PowerToys", "PowerToys", U, "Набор утилит Microsoft для продвинутых пользователей", "https://github.com/microsoft/PowerToys", Both),
        W("voidtools.Everything", "Everything", U, "Мгновенный поиск файлов", "https://www.voidtools.com/", Machine),
        W("ShareX.ShareX", "ShareX", U, "Скриншоты и запись экрана", "https://getsharex.com/", Both),
        W("Notepad++.Notepad++", "Notepad++", U, "Текстовый редактор", "https://notepad-plus-plus.org/", Machine),
        W("VideoLAN.VLC", "VLC", U, "Универсальный медиаплеер", "https://www.videolan.org/vlc/", Machine),
        W("Daum.PotPlayer", "PotPlayer", U, "Мощный видеоплеер", "https://potplayer.tv/", Machine),
        W("AIMP.AIMP", "AIMP", U, "Аудиоплеер", "https://www.aimp.ru", Machine),
        W("CodecGuide.K-LiteCodecPack.Standard", "K-Lite Codec Pack Standard", U, "Набор кодеков и MPC-HC", "https://codecguide.com/", Machine),
        W("qBittorrent.qBittorrent", "qBittorrent", U, "Открытый торрент-клиент без рекламы", "https://www.qbittorrent.org/", Machine),
        W("Spotify.Spotify", "Spotify", U, "Музыкальный стриминг", "https://www.spotify.com/download/windows/", User, AppFlags.NoElevation),
        W("Yandex.Music", "Яндекс Музыка", U, "Музыкальный сервис Яндекса", "https://music.yandex.com/", User, AppFlags.NoElevation),
        W("RevoUninstaller.RevoUninstaller", "Revo Uninstaller", U, "Удаление программ вместе с остатками", "https://www.revouninstaller.com/", Machine, AppFlags.Expert),
        W("Klocman.BulkCrapUninstaller", "Bulk Crap Uninstaller", U, "Массовое удаление программ", "https://www.bcuninstaller.com/", Machine, AppFlags.Expert),
        W("AntibodySoftware.WizTree", "WizTree", U, "Быстрый анализ занятого места на диске", "https://diskanalyzer.com/", Machine),
        W("JAMSoftware.TreeSize.Free", "TreeSize Free", U, "Анализ размеров папок", "https://www.jam-software.com/treesize_free", Both),
        W("Rainmeter.Rainmeter", "Rainmeter", U, "Виджеты рабочего стола", "https://www.rainmeter.net/", Machine),
        W("AnyDesk.AnyDesk", "AnyDesk", U, "Удалённый доступ (давайте его только тем, кому доверяете)", "https://anydesk.com/", Machine, AppFlags.RemoteAccess),
        W("TeamViewer.TeamViewer", "TeamViewer", U, "Удалённый доступ (давайте его только тем, кому доверяете)", "https://www.teamviewer.com/", Machine, AppFlags.RemoteAccess),
    ];

    /// <summary>Entries offered on this machine: Windows-10-only runtimes are hidden where Windows already ships them (Win11 22H2+).</summary>
    public static IEnumerable<CatalogEntry> ForBuild(int osBuild) =>
        All.Where(e => !e.Has(AppFlags.Win10Only) || osBuild < 22621);

    public static CatalogEntry? Find(string id) =>
        All.FirstOrDefault(e => e.AllIds.Contains(id, StringComparer.OrdinalIgnoreCase));
}
