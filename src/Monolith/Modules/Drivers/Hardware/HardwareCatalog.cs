using Monolith.Modules.Apps;

namespace Monolith.Modules.Drivers.Hardware;

/// <summary>
/// Official vendor software for a device. <see cref="WingetId"/> is set only for packages verified read-only with
/// "winget show --id &lt;ID&gt; -e" (publisher = the vendor, installer on the vendor's own servers, 2026-10-27);
/// everything else is offered as the official page / Microsoft Store page only.
/// </summary>
internal sealed record VendorSoftware(
    string Name,
    string Publisher,
    string? WingetId,
    string? StoreId,
    string? Url,
    string Description,
    IReadOnlyList<string> InstalledNames)
{
    /// <summary>
    /// Text the "Apps &amp; features" Publisher must contain (verified on a real install, e.g. "Logitech", "Sony"); null when not
    /// verified — then the display name alone decides. Stops look-alikes ("OpenInzone Hub" from another publisher).
    /// </summary>
    public string? InstalledPublisher { get; init; }

    public bool IsInstalled(IEnumerable<UninstallEntry> apps) => apps.Any(a => Matches(a, InstalledNames, InstalledPublisher));

    /// <summary>
    /// The one match rule of Drivers and «Устройства и сеть»: the display name contains one of <paramref name="names"/> and,
    /// when <paramref name="publisher"/> is given, the entry's Publisher contains it (ordinal, ignoring case).
    /// </summary>
    public static bool Matches(UninstallEntry a, IEnumerable<string> names, string? publisher) =>
        names.Any(n => n.Length > 0 && a.DisplayName.Contains(n, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrEmpty(publisher) || (a.Publisher?.Contains(publisher, StringComparison.OrdinalIgnoreCase) ?? false));
}

/// <summary>A USB peripheral maker identified by its USB vendor id.</summary>
internal sealed record PeripheralVendor(string Vid, string Name, IReadOnlyList<VendorSoftware> Software, string? NoSoftwareNote = null)
{
    /// <summary>Software applies only when a device name contains this text (Sony: INZONE devices only).</summary>
    public string? SoftwareNameFilter { get; init; }
}

/// <summary>A motherboard vendor: short name and official support page (BIOS, chipset and audio drivers).</summary>
internal sealed record BoardVendor(string ShortName, string SupportUrl);

/// <summary>Verified vendor ids, packages and official pages used by the hardware analysis.</summary>
internal static class HardwareCatalog
{
    // ── Verified winget ids (winget show --id … -e, 2026-10-27; peripherals also in docs/research/apps_catalog.md §6) ──
    public const string LogitechGHub = "Logitech.GHUB";
    public const string LogiOptionsPlus = "Logitech.OptionsPlus";
    public const string RazerSynapse4 = "RazerInc.RazerInstaller.Synapse4";
    public const string SteelSeriesGg = "SteelSeries.GG";
    public const string CorsairIcue5 = "Corsair.iCUE.5";
    public const string KingstonSsdManager = "Kingston.SSDManager";
    public const string CrucialStorageExecutive = "Crucial.StorageExecutive";
    public const string SanDiskDashboard = "SanDisk.Dashboard";
    public const string SeagateSeaTools = "Seagate.SeaTools";

    // ── Microsoft Store products (vendor-published listings) ──
    public const string SamsungMagicianStoreId = "XPDDT99J9GKB5C";

    // ── Game runtimes: the Apps catalog ids (docs/research/apps_catalog.md) ──
    public const string VcX64 = "Microsoft.VCRedist.2015+.x64";
    public const string VcX86 = "Microsoft.VCRedist.2015+.x86";
    public const string DirectX = "Microsoft.DirectX";
    public const string DesktopRuntimePrefix = "Microsoft.DotNet.DesktopRuntime.";
    /// <summary>Current LTS .NET Desktop Runtime offered when a PC has none.</summary>
    public const int DesktopRuntimeLts = 10;

    public static CatalogEntry? Runtime(string id) => AppCatalog.All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public static readonly VendorSoftware GHub = new("Logitech G HUB", "Logitech, Inc.", LogitechGHub, null,
        "https://www.logitechg.com/innovation/g-hub.html",
        "Настройка игровых мышей, клавиатур и гарнитур Logitech G: кнопки, DPI, подсветка, обновление прошивки.",
        ["Logitech G HUB"])
    {
        InstalledPublisher = "Logitech", // verified: Publisher "Logitech" (2026-10-27)
    };

    public static readonly VendorSoftware OptionsPlus = new("Logi Options+", "Logitech", LogiOptionsPlus, null, null,
        "Для офисных мышей и клавиатур Logitech (серии MX, ERGO) — G HUB их не настраивает.",
        ["Logi Options+", "Logitech Options+"]);

    public static readonly VendorSoftware Synapse = new("Razer Synapse 4", "Razer Inc.", RazerSynapse4, null,
        "https://www.razer.com/synapse-4",
        "Настройка устройств Razer: кнопки, подсветка, звук микрофона, прошивки. Часть старых устройств работает только с Synapse 3.",
        ["Razer Synapse"])
    {
        InstalledPublisher = "Razer", // verified: Publisher "Razer Inc." (WOW6432Node)
    };

    public static readonly VendorSoftware SteelSeriesGgApp = new("SteelSeries GG", "SteelSeries ApS", SteelSeriesGg, null,
        "https://steelseries.com/gg",
        "Настройка устройств SteelSeries (бывший SteelSeries Engine) и звук Sonar.",
        ["SteelSeries GG", "SteelSeries Engine"]);

    public static readonly VendorSoftware Icue = new("Corsair iCUE 5", "Corsair", CorsairIcue5, null,
        "https://www.corsair.com/icue",
        "Настройка периферии, подсветки и охлаждения Corsair. Ставит свои службы — нужен, только если вы что-то настраиваете.",
        ["iCUE"]);

    public static readonly VendorSoftware Ngenuity = new("HyperX NGENUITY", "HP Inc.", null, null,
        "https://hyperx.com/pages/ngenuity",
        "ПО для устройств HyperX. В winget его нет — только с официального сайта HyperX или из Microsoft Store.",
        ["NGENUITY"]);

    public static readonly VendorSoftware ArmouryCrate = new("ASUS Armoury Crate", "ASUS", null, null,
        "https://www.asus.com/supportonly/armoury%20crate/helpdesk_download/",
        "Ставится собственным загрузчиком ASUS и добавляет несколько служб — MONOLITH не устанавливает его сам.",
        ["Armoury Crate", "ArmouryCrate"]);

    public static readonly VendorSoftware Wootility = new("Wootility", "Wooting", null, null,
        "https://wooting.io/wootility",
        "Настройка аналоговых клавиатур Wooting. В winget пакета нет — только с сайта wooting.io.",
        ["Wootility"]);

    public static readonly VendorSoftware InzoneHub = new("INZONE Hub", "Sony", null, null,
        "https://www.sony.ca/en/electronics/support/others-software/inzone-hub/software/00279252",
        "Настройка звука и профилей игровых устройств Sony INZONE. В winget пакета нет — только с сайта Sony.",
        ["INZONE Hub"])
    {
        InstalledPublisher = "Sony", // verified: Publisher "Sony Corporation"; third-party look-alikes are not INZONE Hub
    };

    /// <summary>
    /// MSI Center (Mystic Light module) — link-first: the winget package is an unversioned zip (likely hash mismatches) and
    /// the product page could not be verified, so the button opens MSI's support site.
    /// </summary>
    public static readonly VendorSoftware MsiCenter = new("MSI Center", "Micro-Star International", null, null,
        "https://www.msi.com/support",
        "Подсветка Mystic Light и настройки платы MSI. Скачивается на сайте MSI (поддержка вашей платы). Ставит свои службы и SDK — нужен, только если вы что-то настраиваете.",
        ["MSI Center"]);

    public static readonly VendorSoftware SamsungMagician = new("Samsung Magician", "Samsung Electronics Co. Ltd.", null, SamsungMagicianStoreId,
        "https://semiconductor.samsung.com/consumer-storage/magician/",
        "Состояние SSD Samsung и обновление его прошивки. В winget есть только версия из Microsoft Store.",
        ["Samsung Magician"]);

    public static readonly VendorSoftware KingstonManager = new("Kingston SSD Manager", "Kingston Digital, Inc.", KingstonSsdManager, null, null,
        "Состояние SSD Kingston, обновление прошивки и безопасное стирание.",
        ["Kingston SSD Manager"]);

    public static readonly VendorSoftware CrucialExecutive = new("Crucial Storage Executive", "Crucial", CrucialStorageExecutive, null,
        "https://www.crucial.com/support/storage-executive",
        "Состояние SSD Crucial и обновление его прошивки.",
        ["Storage Executive"]);

    public static readonly VendorSoftware SanDiskDashboardApp = new("SanDisk Dashboard", "SanDisk Corporation", SanDiskDashboard, null,
        "https://support-en.sandisk.com/app/answers/detailweb/a_id/31759",
        "Бывший WD Dashboard: состояние SSD WD и SanDisk, обновление прошивки.",
        ["SanDisk Dashboard", "Western Digital Dashboard", "WD Dashboard"]);

    public static readonly VendorSoftware SeaTools = new("Seagate SeaTools", "Seagate", SeagateSeaTools, null,
        "https://www.seagate.com/support/downloads/seatools/",
        "Диагностика накопителей Seagate (проверка поверхности и SMART).",
        ["SeaTools"]);

    public static readonly VendorSoftware IntelDsa = new(DriverSources.IntelDsa.Name, DriverSources.IntelDsa.Publisher,
        DriverSources.IntelDsaWingetId, null, DriverSources.IntelDetectPage,
        "Официальный помощник Intel: находит драйверы видеоядра, Wi-Fi, Bluetooth, сетевых карт и чипсета Intel.",
        ["Driver & Support Assistant"]);

    /// <summary>USB vendor ids of gaming peripheral makers. Generic chip vendors (Sinowealth, Pixart…) are not identifiable.</summary>
    public static readonly IReadOnlyList<PeripheralVendor> Peripherals =
    [
        new("046D", "Logitech", [GHub, OptionsPlus]),
        new("1532", "Razer", [Synapse]),
        new("1038", "SteelSeries", [SteelSeriesGgApp]),
        new("1B1C", "Corsair", [Icue]),
        new("03F0", "HyperX (HP)", [Ngenuity]),
        new("0951", "HyperX (Kingston)", [Ngenuity]),
        new("0B05", "ASUS", [ArmouryCrate]),
        new("31E3", "Wooting", [Wootility]),
        new("054C", "Sony", [InzoneHub], "Геймпады и аксессуары Sony работают на драйверах Windows — отдельное ПО не нужно.")
        {
            SoftwareNameFilter = "INZONE",
        },
        new("045E", "Microsoft", [], "Устройства Microsoft и геймпады Xbox работают на драйверах Windows — отдельное ПО не нужно."),
        // 0DB0:0076 «MYSTIC LIGHT» = the board's RGB controller (internal USB header); MSI Wi-Fi / BT modules are not peripherals.
        new("0DB0", "MSI", [MsiCenter]),
    ];

    public static PeripheralVendor? PeripheralByVid(string? vid) =>
        vid is null ? null : Peripherals.FirstOrDefault(p => string.Equals(p.Vid, vid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Devices Windows names only by class («USB-устройство ввода»), by VID:PID (verified on the dev PC).</summary>
    public static string? KnownDeviceName(string vid, string pid) => $"{vid}:{pid}".ToUpperInvariant() switch
    {
        "0DB0:0076" => "Mystic Light (подсветка платы)",
        _ => null,
    };

    /// <summary>PCI / USB vendor ids of network and Bluetooth chips.</summary>
    private static readonly Dictionary<string, string> NetVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["10EC"] = "Realtek", ["0BDA"] = "Realtek",
        ["8086"] = "Intel", ["8087"] = "Intel",
        ["1969"] = "Killer (Qualcomm Atheros)",
        ["14C3"] = "MediaTek", ["0E8D"] = "MediaTek",
        ["17CB"] = "Qualcomm", ["168C"] = "Qualcomm Atheros",
        ["14E4"] = "Broadcom", ["0A5C"] = "Broadcom",
        ["1D6A"] = "Marvell (Aquantia)",
        ["10DE"] = "NVIDIA", ["1022"] = "AMD",
    };

    public static string? NetVendorName(string? vid) => vid is not null && NetVendors.TryGetValue(vid, out var n) ? n : null;

    /// <summary>Motherboard maker (Win32_BaseBoard.Manufacturer) → short name and support page.</summary>
    public static BoardVendor? Board(string? manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return null;
        var m = manufacturer.Trim();
        if (m.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || m.StartsWith("MSI", StringComparison.OrdinalIgnoreCase))
            return new("MSI", "https://www.msi.com/support");
        if (m.Contains("ASUSTeK", StringComparison.OrdinalIgnoreCase) || m.StartsWith("ASUS", StringComparison.OrdinalIgnoreCase))
            return new("ASUS", "https://www.asus.com/support/download-center/");
        if (m.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase))
            return new("GIGABYTE", "https://www.gigabyte.com/Support");
        if (m.Contains("ASRock", StringComparison.OrdinalIgnoreCase))
            return new("ASRock", "https://www.asrock.com/support/index.asp");
        return null;
    }
}
