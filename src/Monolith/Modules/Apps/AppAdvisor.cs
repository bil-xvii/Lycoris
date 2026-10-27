using System.Globalization;
using Monolith.Core;

namespace Monolith.Modules.Apps;

/// <summary>How strongly MONOLITH suggests a catalog entry for this PC (docs/research/apps_catalog.md §14).</summary>
public enum AdviceLevel { Required, Recommended, Optional, NotNow, NotNeeded }

/// <summary>One piece of install advice: the level and one plain sentence why (may be empty for <see cref="AdviceLevel.Optional"/>).</summary>
public sealed record AppAdvice(AdviceLevel Level, string Reason)
{
    public string TagText => AppAdvisor.TagText(Level);

    /// <summary>"НУЖНО" or "РЕКОМЕНДУЕТСЯ": counted in the «РЕКОМЕНДУЕМ» tab and the gamer kit.</summary>
    public bool IsSuggested => Level is AdviceLevel.Required or AdviceLevel.Recommended;

    /// <summary>"ПОКА НЕ НУЖНО" or "НЕ НУЖНО": listed with the reason in the install confirmation.</summary>
    public bool IsDiscouraged => Level is AdviceLevel.NotNow or AdviceLevel.NotNeeded;
}

/// <summary>Peripheral makers whose configuration software is in the catalog.</summary>
public enum PeripheralBrand { Logitech, Razer, Corsair, SteelSeries, Elgato }

/// <summary>
/// Everything the advisor knows about this PC. Every fact has a "known" flag: an unknown fact never leads to a
/// confident claim (the advice falls back to <see cref="AdviceLevel.Optional"/> with a neutral reason).
/// </summary>
public sealed record AdviceContext
{
    private readonly HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nothing is known yet (no system profile, no detection, no device scan).</summary>
    public static AdviceContext Unknown { get; } = new();

    /// <summary>The graphics cards were read (WMI section without errors, at least one adapter).</summary>
    public bool GpuKnown { get; init; }

    /// <summary>Makers of the graphics cards found (NVIDIA / AMD / Intel; others are ignored).</summary>
    public IReadOnlySet<GpuVendor> GpuVendors { get; init; } = new HashSet<GpuVendor>();

    /// <summary>Windows build (0 = unknown) and update build revision (0 = unknown).</summary>
    public int OsBuild { get; init; }
    public int OsUbr { get; init; }

    /// <summary>At least one detection pass (or the cache) has been applied, so <see cref="InstalledIds"/> is meaningful.</summary>
    public bool InstalledKnown { get; init; }

    /// <summary>
    /// Installed package ids, case-insensitive: every id found (regional variants included), also components installed
    /// outside winget (found by the read-only cross-checks).
    /// </summary>
    public IReadOnlySet<string> InstalledIds
    {
        get => _installed;
        init => _installed = new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The USB/Bluetooth device scan finished (see <see cref="PeripheralScan"/>).</summary>
    public bool PeripheralsKnown { get; init; }
    public IReadOnlySet<PeripheralBrand> Peripherals { get; init; } = new HashSet<PeripheralBrand>();

    /// <summary>Today's date (injected so the end-of-support rules can be tested).</summary>
    public DateOnly Today { get; init; } = DateOnly.FromDateTime(DateTime.Today);

    public bool HasGpu(GpuVendor vendor) => GpuVendors.Contains(vendor);

    /// <summary>The id is installed — false while detection has not run yet.</summary>
    public bool IsInstalled(string id) => InstalledKnown && _installed.Contains(id);

    /// <summary>Any package id of the entry (all regional variants) is installed.</summary>
    public bool IsInstalled(CatalogEntry e) => InstalledKnown && e.AllIds.Any(_installed.Contains);
}

/// <summary>
/// Personalised, honest install advice for every catalog entry: "НУЖНО", "РЕКОМЕНДУЕТСЯ", "ПО ЖЕЛАНИЮ",
/// "ПОКА НЕ НУЖНО", "НЕ НУЖНО". Pure and cheap: the same entry and context always give the same advice.
/// Rules, verified facts and sources: docs/research/apps_catalog.md §14.
/// </summary>
public static class AppAdvisor
{
    public const string XboxAppId = "9MV0B5HZVK9Z";
    public const string GamingServicesId = "9MWPM2CQNLHN";
    public const string WebView2Id = "Microsoft.EdgeWebView2Runtime";
    public const string PrismLauncherId = "PrismLauncher.PrismLauncher";
    public const string CurseForgeId = "Overwolf.CurseForge";
    public const string SevenZipId = "7zip.7zip";

    /// <summary>
    /// End of Microsoft support per .NET Desktop Runtime (dotnet.microsoft.com/platform/support/policy/dotnet-core,
    /// checked 27.10.2026): .NET 10 LTS → 14.11.2028, .NET 9 STS and .NET 8 LTS → 10.11.2026, .NET 6 → 12.11.2024.
    /// From this date on the runtime gets no security fixes.
    /// </summary>
    public static IReadOnlyDictionary<string, DateOnly> DotNetEndOfSupport { get; } = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.DotNet.DesktopRuntime.10"] = new(2028, 11, 14),
        ["Microsoft.DotNet.DesktopRuntime.9"] = new(2026, 11, 10),
        ["Microsoft.DotNet.DesktopRuntime.8"] = new(2026, 11, 10),
        ["Microsoft.DotNet.DesktopRuntime.6"] = new(2024, 11, 12),
    };

    /// <summary>The current .NET LTS: recommended until its end of support.</summary>
    public const string CurrentDotNetId = "Microsoft.DotNet.DesktopRuntime.10";

    /// <summary>
    /// Windows 11 23H2 (22631) reads .rar and .7z in File Explorer from its first release build 22631.2506 (KB5031455,
    /// libarchive). On 22H2 (22621) KB5031455 shipped it as an optional preview whose new features were switched on for
    /// everyone only by the November 2023 security update KB5032190 (22621.2715). Every later Windows 11 has it.
    /// </summary>
    public const int NativeArchivesUbr23H2 = 2506;
    public const int NativeArchivesPreviewUbr22H2 = 2506;
    public const int NativeArchivesUbr22H2 = 2715;

    /// <summary>Peripheral software → the maker whose devices it configures.</summary>
    public static IReadOnlyDictionary<string, PeripheralBrand> PeripheralApps { get; } = new Dictionary<string, PeripheralBrand>(StringComparer.OrdinalIgnoreCase)
    {
        ["Logitech.GHUB"] = PeripheralBrand.Logitech,
        ["RazerInc.RazerInstaller.Synapse4"] = PeripheralBrand.Razer,
        ["Corsair.iCUE.5"] = PeripheralBrand.Corsair,
        ["SteelSeries.GG"] = PeripheralBrand.SteelSeries,
        ["Elgato.StreamDeck"] = PeripheralBrand.Elgato,
    };

    /// <summary>"One is enough": programs that do the same job. Browsers are a separate, softer role (<see cref="Duplicate"/>).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Roles { get; } =
    [
        [SevenZipId, "RARLab.WinRAR", "M2Team.NanaZip"],
        ["VideoLAN.VLC", "Daum.PotPlayer", "CodecGuide.K-LiteCodecPack.Standard"],
        ["AntibodySoftware.WizTree", "JAMSoftware.TreeSize.Free"],
        ["RevoUninstaller.RevoUninstaller", "Klocman.BulkCrapUninstaller"],
        ["REALiX.HWiNFO", "CPUID.HWMonitor", "LibreHardwareMonitor.LibreHardwareMonitor"],
        ["Intel.PresentMon", "CXWorld.CapFrameX"],
        ["VB-Audio.Voicemeeter", "VB-Audio.Voicemeeter.Banana"],
        ["OBSProject.OBSStudio", "Streamlabs.Streamlabs"],
    ];

    /// <summary>Flags that never allow "НУЖНО" or "РЕКОМЕНДУЕТСЯ" (hard safety rule).</summary>
    public const AppFlags NeverSuggested = AppFlags.KernelDriver | AppFlags.Anticheat | AppFlags.RemoteAccess
                                         | AppFlags.Expert | AppFlags.Trialware | AppFlags.Paid | AppFlags.Eol;

    public static string TagText(AdviceLevel level) => level switch
    {
        AdviceLevel.Required => "НУЖНО",
        AdviceLevel.Recommended => "РЕКОМЕНДУЕТСЯ",
        AdviceLevel.NotNow => "ПОКА НЕ НУЖНО",
        AdviceLevel.NotNeeded => "НЕ НУЖНО",
        _ => "ПО ЖЕЛАНИЮ",
    };

    /// <summary>The advice a card shows: none for Windows components (Edge) and for entries that are already installed.</summary>
    public static AppAdvice? Advise(CatalogEntry e, AdviceContext ctx) =>
        e.SystemComponent || ctx.IsInstalled(e) ? null : Assess(e, ctx);

    /// <summary>
    /// What this PC needs, judged as if the entry were not installed (the card hides it once installed; the gamer kit
    /// counts "installed / needed" with it). Null only for Windows components.
    /// </summary>
    public static AppAdvice? Assess(CatalogEntry e, AdviceContext ctx)
    {
        if (e.SystemComponent) return null;
        var advice = Duplicate(e, ctx) ?? Rule(e, ctx) ?? Optional();
        // Hard safety rule: drivers, anti-cheat, remote access, expert tools, paid/trial and end-of-life packages
        // are never pushed, whatever a rule above says.
        if (advice.IsSuggested && (e.Flags & NeverSuggested) != 0) advice = advice with { Level = AdviceLevel.Optional };
        return advice;
    }

    /// <summary>
    /// "НАБОР ГЕЙМЕРА": runtime libraries this PC needs ("НУЖНО" / "РЕКОМЕНДУЕТСЯ", judged as if not installed),
    /// installable with winget, in catalog order.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Kit(IEnumerable<CatalogEntry> entries, AdviceContext ctx) =>
        entries.Where(e => e.Category == AppCategory.Runtimes && e.CanInstall && Assess(e, ctx) is { IsSuggested: true }).ToList();

    // ─────────────────────────── rules ───────────────────────────

    private static AppAdvice Required(string reason) => new(AdviceLevel.Required, reason);
    private static AppAdvice Recommended(string reason) => new(AdviceLevel.Recommended, reason);
    private static AppAdvice Optional(string reason = "") => new(AdviceLevel.Optional, reason);
    private static AppAdvice NotNow(string reason) => new(AdviceLevel.NotNow, reason);
    private static AppAdvice NotNeeded(string reason) => new(AdviceLevel.NotNeeded, reason);

    public static string FormatDate(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    /// <summary>Another program with the same job is already installed.</summary>
    private static AppAdvice? Duplicate(CatalogEntry e, AdviceContext ctx)
    {
        if (!ctx.InstalledKnown) return null;
        if (e.Category == AppCategory.Browsers)
        {
            var browser = AppCatalog.All.FirstOrDefault(o => o.Category == AppCategory.Browsers && !o.SystemComponent
                                                             && !ReferenceEquals(o, e) && ctx.IsInstalled(o));
            return browser is null ? null : NotNow($"Уже установлен {browser.Name} — второй браузер ставьте, только если он вам нужен.");
        }
        var role = Roles.FirstOrDefault(r => r.Contains(e.Id, StringComparer.OrdinalIgnoreCase));
        if (role is null) return null;
        var other = role.Where(id => !string.Equals(id, e.Id, StringComparison.OrdinalIgnoreCase) && ctx.IsInstalled(id))
            .Select(AppCatalog.Find).FirstOrDefault(o => o is not null);
        return other is null ? null : NotNeeded($"Уже установлен {other.Name} — он делает то же самое.");
    }

    private static AppAdvice? Rule(CatalogEntry e, AdviceContext ctx)
    {
        if (e.Has(AppFlags.NvidiaOnly)) return Nvidia(e, ctx);
        if (PeripheralApps.TryGetValue(e.Id, out var brand)) return Peripheral(brand, ctx);
        if (DotNetEndOfSupport.TryGetValue(e.Id, out var end)) return DotNet(e.Id, end, ctx);
        if (e.Id.StartsWith("EclipseAdoptium.Temurin.", StringComparison.OrdinalIgnoreCase)) return Java(e.Id, ctx);
        if (e.Id.StartsWith("Microsoft.VCRedist.", StringComparison.OrdinalIgnoreCase)) return VcRedist(e.Id);

        switch (e.Id)
        {
            case "Microsoft.DirectX":
                return Recommended("Старым играм нужны D3DX9, XAudio 2.7 и XInput 1.3 — в Windows их нет, без них такие игры не запускаются.");
            case WebView2Id:
                return ctx.InstalledKnown
                    ? Required("Веб-движок Windows: без него не работают вход в Xbox (приложение Xbox, Minecraft Launcher) и окна многих программ — здесь он не найден.")
                    : Optional("Веб-движок Windows для окон программ и входа в Xbox; обычно уже установлен.");
            case GamingServicesId:
                if (!ctx.InstalledKnown) return Optional("Нужны для игр из приложения Xbox и Game Pass.");
                return ctx.IsInstalled(XboxAppId)
                    ? Required("Установлено приложение Xbox: без игровых служб игры Game Pass не устанавливаются и не запускаются.")
                    : NotNow("Нужны только для игр из приложения Xbox и Game Pass — приложение Xbox само предложит их поставить.");
            case "Microsoft.DotNet.Framework.Runtime":
                if (ctx.OsBuild <= 0) return Optional("Последняя версия .NET Framework для старых программ и лаунчеров.");
                if (ctx.OsBuild >= 22621) return NotNeeded("Уже встроен в эту версию Windows.");
                if (ctx.OsBuild < e.MinOsBuild) return NotNeeded("Не устанавливается на эту версию Windows: нужна Windows 10 20H2 или новее.");
                return Recommended("Последняя версия .NET Framework для старых программ и лаунчеров; в эту Windows встроена более ранняя.");
            case "Microsoft.XNARedist":
                return NotNow("Нужна лишь некоторым старым инди-играм; Steam обычно ставит её сам вместе с игрой.");
            case "CreativeTechnology.OpenAL":
                return NotNow("Нужна лишь некоторым старым играм; Steam обычно ставит её сам вместе с игрой.");

            case "Valve.Steam":
                return Recommended("Крупнейший магазин игр для ПК — нужен большинству игроков.");
            case "Discord.Discord":
                return Recommended("Главный голосовой и текстовый чат игроков.");

            case "BitSum.ProcessLasso":
                return NotNeeded("Спорит с игровым режимом MONOLITH: обе программы меняют приоритеты процессов.");
            case "Microsoft.Sysinternals.Autoruns":
                return NotNow("Инструмент для опытных; основное есть на странице «Автозапуск и службы».");
            case "OCBase.OCCT.Personal":
            case "Geeks3D.FurMark.2":
                return NotNow("Только чтобы проверить стабильность после разгона или при подозрении на неисправность: сильная нагрузка и нагрев.");
            case "Resplendence.LatencyMon":
                return NotNow("Нужен, только если заикается звук или картинка, — чтобы найти виноватый драйвер.");
            case "Wagnardsoft.DisplayDriverUninstaller":
                return NotNow("Только для полной переустановки видеодрайвера, когда с ним проблемы.");
            case "SpecialK.SpecialK":
                return NotNow("Внедряется в игры — онлайн-игры с античитом могут счесть это нарушением.");
            case "Rufus.Rufus":
                return NotNow("Нужен, только чтобы сделать загрузочную флешку.");
            case "RevoUninstaller.RevoUninstaller":
            case "Klocman.BulkCrapUninstaller":
                return NotNow("Windows сама удаляет программы; это — для тех, что не удаляются обычным способом.");
            case "CodecGuide.K-LiteCodecPack.Standard":
                return NotNow("Меняет программы по умолчанию для видео, а VLC и так открывает почти всё без кодеков.");
            case "VB-Audio.Voicemeeter":
            case "VB-Audio.Voicemeeter.Banana":
                return NotNow("Ставит виртуальный звуковой драйвер и требует перезагрузки — нужен только для сведения звука, например на стримах.");
            case "LizardByte.Sunshine":
                return NotNow("Ставит службу и открывает сетевые порты — нужен, только чтобы стримить игры с этого ПК на другие устройства.");
            case "AnyDesk.AnyDesk":
            case "TeamViewer.TeamViewer":
                return NotNow("Ставьте, только когда вам помогает тот, кому вы доверяете: мошенники часто просят установить такие программы.");

            case SevenZipId:
                return SevenZip(ctx);
            case "RARLab.WinRAR":
                return NotNow("Платный после пробного периода, а бесплатный 7-Zip открывает те же архивы.");
        }
        return null;
    }

    private static AppAdvice VcRedist(string id)
    {
        if (id.StartsWith("Microsoft.VCRedist.2015+", StringComparison.OrdinalIgnoreCase))
            return id.EndsWith(".x86", StringComparison.OrdinalIgnoreCase)
                ? Required("Её требуют 32-битные игры и программы — их много даже на 64-битной Windows.")
                : Required("Без неё не запускается большинство современных игр и программ (ошибка «MSVCP140.dll не найден»).");
        return Recommended("Старые игры ищут именно эту версию Visual C++ — в Windows её нет, а сама игра ставит её не всегда.");
    }

    private static AppAdvice Nvidia(CatalogEntry e, AdviceContext ctx)
    {
        if (!ctx.GpuKnown) return Optional("Видеокарту определить не удалось. Пакет нужен только с видеокартой NVIDIA.");
        if (!ctx.HasGpu(GpuVendor.Nvidia)) return NotNeeded("Видеокарта NVIDIA не найдена.");
        return e.Id switch
        {
            "Nvidia.PhysX" => NotNow("Драйвер NVIDIA уже содержит PhysX — отдельный пакет нужен, только если игра на него жалуется."),
            "9NF8H0H7WMLT" => NotNow("Обычно ставится вместе с драйвером NVIDIA — нужна, только если панели нет."),
            "Orbmu2k.nvidiaProfileInspector" => NotNow("Инструмент для опытных: меняет скрытые настройки драйвера NVIDIA."),
            "TechPowerUp.NVCleanstall" => NotNow("Инструмент для опытных: собирает урезанный установщик драйвера NVIDIA."),
            _ => Optional("Для видеокарт NVIDIA."),
        };
    }

    public static string BrandName(PeripheralBrand b) => b switch
    {
        PeripheralBrand.Logitech => "Logitech",
        PeripheralBrand.Razer => "Razer",
        PeripheralBrand.Corsair => "Corsair",
        PeripheralBrand.SteelSeries => "SteelSeries",
        _ => "Elgato",
    };

    private static AppAdvice Peripheral(PeripheralBrand b, AdviceContext ctx)
    {
        var name = BrandName(b);
        if (!ctx.PeripheralsKnown) return Optional($"Нужна только владельцам устройств {name}.");
        if (!ctx.Peripherals.Contains(b)) return NotNeeded($"Устройства {name} сейчас не подключены — программа нужна только для них.");
        return Recommended(b switch
        {
            PeripheralBrand.Logitech => "Найдено устройство Logitech: G HUB настраивает игровые мыши, клавиатуры и гарнитуры Logitech G (офисным моделям и веб-камерам не нужен).",
            PeripheralBrand.Razer => "Найдено устройство Razer: Synapse настраивает его кнопки, подсветку и чувствительность.",
            PeripheralBrand.Corsair => "Найдено устройство Corsair: iCUE настраивает его кнопки, подсветку и вентиляторы.",
            PeripheralBrand.SteelSeries => "Найдено устройство SteelSeries: GG настраивает его и добавляет звуковой микшер Sonar.",
            _ => "Найдено устройство Elgato: программа нужна, если это пульт Stream Deck (картам захвата, камерам и микрофонам не нужна).",
        });
    }

    private static AppAdvice DotNet(string id, DateOnly end, AdviceContext ctx)
    {
        if (ctx.Today >= end)
            return NotNow($"Без обновлений безопасности с {FormatDate(end)} — ставьте, только если программа просит именно эту версию.");
        return string.Equals(id, CurrentDotNetId, StringComparison.OrdinalIgnoreCase)
            ? Recommended($"Актуальная версия .NET (поддержка Microsoft до {FormatDate(end)}): на ней работают новые программы и утилиты.")
            : Optional($"Поддержка Microsoft заканчивается {FormatDate(end)} — ставьте, если программа просит именно эту версию.");
    }

    /// <summary>
    /// The official Minecraft Launcher brings its own Java; CurseForge downloads the right Java for Minecraft itself;
    /// Prism Launcher 9.0+ can download it too (Settings → Java), so a separate JRE is a fallback for Prism only.
    /// </summary>
    private static AppAdvice Java(string id, AdviceContext ctx)
    {
        if (!ctx.InstalledKnown) return Optional("Нужна сторонним лаунчерам Minecraft; официальный лаунчер ставит свою Java сам.");
        if (ctx.IsInstalled(PrismLauncherId))
        {
            if (id.Contains(".21.", StringComparison.Ordinal))
                return Optional("Prism Launcher обычно сам скачивает нужную Java (Настройки → Java); Java 21 — для Minecraft 1.20.5–1.21, если он её попросит.");
            if (id.Contains(".17.", StringComparison.Ordinal))
                return Optional("Для Minecraft 1.18–1.20.4 в Prism Launcher, если он не скачал Java сам.");
            return NotNow("Только для старых сборок Minecraft (до 1.17), если Prism Launcher не скачал Java сам.");
        }
        if (ctx.IsInstalled(CurseForgeId)) return NotNow("CurseForge сам скачивает нужную Java для Minecraft.");
        return NotNow("Официальный лаунчер Minecraft ставит свою Java сам — отдельная нужна только сторонним лаунчерам.");
    }

    /// <summary>True when this Windows build opens .rar and .7z itself; null when it cannot be told (build/UBR unknown or staged).</summary>
    public static bool? HasNativeArchives(int build, int ubr)
    {
        if (build <= 0) return null;
        if (build < 22621) return false;              // Windows 10 and Windows 11 21H2 (22000)
        if (build > 22631) return true;               // 23H2 Insider (22635), 24H2 (26100), 25H2 (26200) and later
        if (build is not (22621 or 22631) || ubr <= 0) return null;
        if (build == 22631) return ubr >= NativeArchivesUbr23H2;
        if (ubr >= NativeArchivesUbr22H2) return true;
        return ubr >= NativeArchivesPreviewUbr22H2 ? null : false; // optional preview: on only with "get the latest updates"
    }

    private static AppAdvice SevenZip(AdviceContext ctx) => HasNativeArchives(ctx.OsBuild, ctx.OsUbr) switch
    {
        true => Optional("Windows уже открывает RAR и 7z (кроме архивов с паролем)."),
        false => Recommended(ctx.OsBuild < 22000
            ? "Бесплатный архиватор: Windows 10 сама не открывает RAR и 7z."
            : "Бесплатный архиватор: эта сборка Windows ещё не открывает RAR и 7z сама."),
        _ => Optional("Бесплатный архиватор для RAR, 7z и архивов с паролем."),
    };
}
