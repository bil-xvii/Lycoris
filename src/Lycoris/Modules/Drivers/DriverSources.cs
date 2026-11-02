using Lycoris.Core;

namespace Lycoris.Modules.Drivers;

/// <summary>An official vendor utility that keeps drivers up to date (docs/research/drivers.md §3).</summary>
internal sealed record VendorTool(
    string Name,
    string Publisher,
    string? WingetId,
    string WingetSource,
    string SiteUrl,
    string Description);

/// <summary>
/// Official driver sources only — vendor sites, vendor tools and Windows Update.
/// Every id here is verified in docs/research/drivers.md; third-party driver databases are never offered.
/// </summary>
internal static class DriverSources
{
    // ── Official pages ──
    public const string NvidiaDriversPage = "https://www.nvidia.com/en-us/drivers/";
    public const string AmdDriversPage = "https://www.amd.com/en/support/download/drivers.html";
    public const string IntelDetectPage = "https://www.intel.com/content/www/us/en/support/detect.html";
    public const string DduPage = "https://www.wagnardsoft.com/";

    // ── Windows settings pages ──
    public const string OptionalUpdatesUri = "ms-settings:windowsupdate-optionalupdates";
    public const string WindowsUpdateUri = "ms-settings:windowsupdate";

    // ── Verified package ids (research §3 "Verified IDs") ──
    public const string NvidiaAppStoreId = "XP8CLZL93F5Z4P";
    public const string IntelDsaWingetId = "Intel.IntelDriverAndSupportAssistant";

    /// <summary>Packages that exist in winget but must never be offered (driver packs, Azure-only builds).</summary>
    public static readonly IReadOnlyList<string> BlockedWingetIds =
    [
        "GlennDelahoy.SnappyDriverInstallerOrigin",
        "samlab-ws.SnappyDriverInstaller",
        "AMD.AMDSoftwareCloudEdition",
    ];

    public static readonly VendorTool NvidiaApp = new(
        "NVIDIA App", "NVIDIA Corp.", NvidiaAppStoreId, "msstore", NvidiaDriversPage,
        Loc.T("Официальное приложение NVIDIA: скачивает и ставит драйверы Game Ready / Studio, показывает их описание."));

    public static readonly VendorTool IntelDsa = new(
        "Intel Driver & Support Assistant", "Intel", IntelDsaWingetId, "winget", IntelDetectPage,
        Loc.T("Официальный помощник Intel: находит драйверы для видеоядра, Wi-Fi, Bluetooth и чипсета Intel."));

    /// <summary>AMD has no winget package for Adrenalin: the official auto-detect installer lives on amd.com.</summary>
    public static readonly VendorTool AmdInstaller = new(
        "AMD Software: Adrenalin Edition", "Advanced Micro Devices", null, "", AmdDriversPage,
        Loc.T("У AMD нет пакета в winget. Официальный установщик с автоопределением видеокарты — на сайте amd.com."));

    public static VendorTool? ToolFor(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => NvidiaApp,
        GpuVendor.Amd => AmdInstaller,
        GpuVendor.Intel => IntelDsa,
        _ => null,
    };

    public static string? SiteFor(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => NvidiaDriversPage,
        GpuVendor.Amd => AmdDriversPage,
        GpuVendor.Intel => IntelDetectPage,
        _ => null,
    };

    private static readonly string[] AllowedHosts = ["nvidia.com", "amd.com", "intel.com", "wagnardsoft.com"];

    /// <summary>
    /// Only https links to the vendors' own domains may be opened (protects against a changed NVIDIA API answer
    /// pointing somewhere else). ms-settings: links are allowed for the Windows Update pages.
    /// </summary>
    public static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url is OptionalUpdatesUri or WindowsUpdateUri) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        return AllowedHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
    }

    public static bool IsBlocked(string wingetId) =>
        BlockedWingetIds.Any(b => string.Equals(b, wingetId, StringComparison.OrdinalIgnoreCase));
}
