using System.Globalization;
using System.Text.RegularExpressions;

namespace Lycoris.Modules.Drivers.Hardware;

/// <summary>Pure helpers of the hardware analysis: id parsing, vendor mapping, version and date verdicts.</summary>
internal static partial class HardwareRules
{

    /// <summary>A BIOS older than this is worth checking on the board maker's site (information only).</summary>
    public const int BiosOldMonths = 24;

    /// <summary>
    /// Programs built with Visual Studio 2022 17.10+ need the v14 runtime 14.40 or newer
    /// (the redistributable must not be older than the build tools), otherwise they may crash at start.
    /// </summary>
    public static readonly Version VcMinimum = new(14, 40);

    // ───────────────────────────── Status texts ─────────────────────────────

    public static bool NeedsAttention(HwStatus s) => s is HwStatus.UpdateAvailable or HwStatus.NotInstalled or HwStatus.ManualOnly;

    public static string StatusText(HwStatus s) => s switch
    {
        HwStatus.Current => Loc.T("АКТУАЛЬНО"),
        HwStatus.Installed => Loc.T("УСТАНОВЛЕНО"),
        HwStatus.UpdateAvailable => Loc.T("ЕСТЬ ОБНОВЛЕНИЕ"),
        HwStatus.NotInstalled => Loc.T("НЕ УСТАНОВЛЕНО"),
        HwStatus.ManualOnly => Loc.T("ТОЛЬКО ВРУЧНУЮ"),
        HwStatus.Unchecked => Loc.T("НЕ ПРОВЕРЕНО"),
        _ => Loc.T("СВЕДЕНИЯ"),
    };

    /// <summary>Style key for the status tag.</summary>
    public static string StatusKey(HwStatus s) => s switch
    {
        HwStatus.UpdateAvailable => "update",
        HwStatus.NotInstalled => "missing",
        HwStatus.ManualOnly => "manual",
        HwStatus.Current or HwStatus.Installed => "ok",
        _ => "muted",
    };

    public static string SourceText(HwSource s) => s switch
    {
        HwSource.Winget => "WINGET",
        HwSource.VendorServer => Loc.T("ОФИЦ. СЕРВЕР"),
        HwSource.WindowsUpdate => "WINDOWS UPDATE",
        _ => Loc.T("ВРУЧНУЮ"),
    };

    // ───────────────────────────── Device ids ─────────────────────────────

    [GeneratedRegex(@"^(?:USB|HID)\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbIdRegex();

    /// <summary>Bluetooth HID ids carry the USB vendor id too: "…_VID&amp;0002046D_PID&amp;B023…" (classic), "…_VID&amp;02046d_PID&amp;b023…" (LE).</summary>
    [GeneratedRegex(@"_VID&(?:0002|02)([0-9A-F]{4})_PID&([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex BluetoothIdRegex();

    /// <summary>
    /// "USB\VID_046D&amp;PID_C339&amp;MI_01\…" → ("046D", "C339"); Bluetooth HID ids are understood as well.
    /// Null for ids without a USB vendor id.
    /// </summary>
    public static (string Vid, string Pid)? UsbId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        var id = deviceId.Trim();
        var m = UsbIdRegex().Match(id);
        if (!m.Success && (id.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"HID\{", StringComparison.OrdinalIgnoreCase)))
            m = BluetoothIdRegex().Match(id);
        return m.Success ? (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant()) : null;
    }

    /// <summary>Vendor id from PCI / USB / HD Audio ids ("PCI\VEN_10EC&amp;…" → "10EC").</summary>
    public static string? VendorId(string? deviceId) => DeviceNaming.VendorId(deviceId);

    private static readonly string[] GenericNameMarkers =
    [
        "HID", Loc.Keep("Составное"), "Composite", Loc.Keep("устройство ввода"), "Input Device", Loc.Keep("концентратор"), "Hub",
        Loc.Keep("Запоминающее"), "Mass Storage", Loc.Keep("Аудио устройства USB"), "USB Audio Device", Loc.Keep("Универсальн"), "Generic",
        Loc.Keep("Устройство USB"), "USB Device", Loc.Keep("Клавиатура HID"), "HID Keyboard", Loc.Keep("Поставщик"), "vendor-defined",
        Loc.Keep("системный контроллер"), "System Controller", "Consumer Control", Loc.Keep("устройство управления"),
    ];

    /// <summary>
    /// Names Windows gives to devices by class ("Составное USB устройство", "HID-совместимая мышь"…) — they say nothing
    /// about the product. Product strings reported by the device itself ("PRO", "Razer Seiren V3 Mini") are kept.
    /// </summary>
    public static bool IsGenericDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return GenericNameMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A software device, not hardware: no physical bus in its parent chain (read by the scanner), G HUB's virtual HID driver,
    /// or its LGHUBDevice\ enumerator (the virtual mouse / keyboard 046D:C231 / C232 that G HUB adds).
    /// </summary>
    public static bool IsVirtualDevice(PnpDevice d) =>
        d.Virtual
        || string.Equals(d.Service, "logi_joy_vir_hid", StringComparison.OrdinalIgnoreCase)
        || d.DeviceId.StartsWith(@"LGHUBDEVICE\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Realtek HD Audio codec: "HDAUDIO\FUNC_01&amp;VEN_10EC&amp;DEV_0897&amp;…" → "Realtek ALC897".</summary>
    public static string? RealtekCodecName(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !deviceId.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Regex.Match(deviceId, @"VEN_10EC&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        if (!m.Success) return null;
        var dev = m.Groups[1].Value.TrimStart('0').ToUpperInvariant();
        return dev.Length is >= 3 and <= 4 ? $"Realtek ALC{dev}" : null;
    }

    public static bool IsWireless(string? name) =>
        !string.IsNullOrEmpty(name) &&
        (name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || name.Contains("WiFi", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) || name.Contains("WLAN", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("802.11", StringComparison.Ordinal) || name.Contains(Loc.Keep("Беспровод"), StringComparison.OrdinalIgnoreCase));

    /// <summary>Intel network chips (incl. Intel-made Killer Wi-Fi) are serviced by Intel Driver &amp; Support Assistant.</summary>
    public static bool IsIntelNetwork(string? deviceId) => VendorId(deviceId) is "8086" or "8087";

    // ───────────────────────────── Windows Update matching ─────────────────────────────

    /// <summary>
    /// A WU driver applies to the device when its hardware id equals one of the device's ids or prefixes its instance path
    /// ("PCI\VEN_10EC&amp;DEV_8126" ↔ "PCI\VEN_10EC&amp;DEV_8126&amp;SUBSYS_…\…").
    /// </summary>
    public static bool WuMatches(string? deviceInstanceId, IEnumerable<string>? hardwareIds, string? wuHardwareId)
    {
        if (string.IsNullOrWhiteSpace(wuHardwareId)) return false;
        var id = wuHardwareId.Trim();
        if (hardwareIds is not null && hardwareIds.Any(h => string.Equals(h?.Trim(), id, StringComparison.OrdinalIgnoreCase))) return true;
        if (string.IsNullOrWhiteSpace(deviceInstanceId)) return false;
        var inst = deviceInstanceId.Trim();
        return inst.StartsWith(id, StringComparison.OrdinalIgnoreCase) &&
               (inst.Length == id.Length || inst[id.Length] is '\\' or '&');
    }

    /// <summary>Device setup class of a WU driver → the hardware group it belongs to (null = not shown in a group).</summary>
    public static HwGroupKind? WuGroup(string? driverClass) => driverClass?.Trim().ToUpperInvariant() switch
    {
        "DISPLAY" => HwGroupKind.Gpu,
        "NET" or "BLUETOOTH" => HwGroupKind.Network,
        "MEDIA" or "AUDIOENDPOINT" or "AUDIOPROCESSINGOBJECT" => HwGroupKind.Audio,
        "MONITOR" => HwGroupKind.Monitors,
        "SYSTEM" or "PROCESSOR" or "SECURITYDEVICES" => HwGroupKind.Platform,
        "HIDCLASS" or "MOUSE" or "KEYBOARD" or "USBDEVICE" or "CAMERA" or "IMAGE" => HwGroupKind.Peripherals,
        "SCSIADAPTER" or "HDC" or "DISKDRIVE" => HwGroupKind.Storage,
        _ => null,
    };

    // ───────────────────────────── Versions and dates ─────────────────────────────

    /// <summary>Lenient version parse: "14.44.35211.0", "v14.40", "26.9.1" → Version; null when there is no number.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"\d+(?:\.\d+){1,3}", RegexOptions.None, TimeSpan.FromMilliseconds(250));
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    /// <summary>Compares dotted versions ("26.9.1" vs "24.10.24"); null when either is not a version.</summary>
    public static int? CompareVersions(string? a, string? b)
    {
        var x = ParseVersion(a);
        var y = ParseVersion(b);
        if (x is null || y is null) return null;
        return Normalize(x).CompareTo(Normalize(y));

        static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    public static string? MaxVersion(string? a, string? b)
    {
        if (ParseVersion(a) is null) return ParseVersion(b) is null ? (a ?? b) : b;
        if (ParseVersion(b) is null) return a;
        return CompareVersions(a, b) >= 0 ? a : b;
    }

    [GeneratedRegex(@"^Microsoft Visual C\+\+ (?:v14|2015|2017|2019|2022)(?:-20\d\d)?\b(?!.*Debug).*?\b(x64|x86)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VcEntryRegex();

    /// <summary>
    /// Newest Visual C++ 2015+ (v14) runtime of the given architecture listed in "Apps &amp; features":
    /// "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "Microsoft Visual C++ v14 Redistributable (x86) - 14.50…",
    /// "Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.44.35211". Returns null when none is installed.
    /// </summary>
    public static string? ParseVcRedist(IEnumerable<UninstallEntry> apps, string arch)
    {
        string? best = null;
        foreach (var a in apps)
        {
            var m = VcEntryRegex().Match(a.DisplayName);
            if (!m.Success || !string.Equals(m.Groups[1].Value, arch, StringComparison.OrdinalIgnoreCase)) continue;
            var version = ParseVersion(a.Version)?.ToString() ?? ParseVersion(a.DisplayName[m.Length..])?.ToString();
            if (version is null || ParseVersion(version)!.Major != 14) continue;
            best = MaxVersion(best, version);
        }
        return best;
    }

    /// <summary>True when an installed v14 runtime is older than <see cref="VcMinimum"/>.</summary>
    public static bool VcIsOutdated(string? version) => ParseVersion(version) is { } v && v < VcMinimum;

    /// <summary>Whole months since the BIOS release date; null when unknown.</summary>
    public static int? BiosAgeMonths(DateTime? biosDate, DateTime now) => DriverAge.Months(biosDate, now);

    public static bool BiosIsOld(DateTime? biosDate, DateTime now) => BiosAgeMonths(biosDate, now) is >= BiosOldMonths;

    // ───────────────────────────── Storage software ─────────────────────────────

    [GeneratedRegex(@"^CT\d{3,4}[A-Z]", RegexOptions.IgnoreCase)]
    private static partial Regex CrucialModelRegex();

    [GeneratedRegex(@"^ST\d{3,5}[A-Z]{2}", RegexOptions.IgnoreCase)]
    private static partial Regex SeagateModelRegex();

    [GeneratedRegex(@"^(?:WDC\b|WDS\d|WD_BLACK|WD (?:Blue|Green|Red|Black)|WD\d)", RegexOptions.IgnoreCase)]
    private static partial Regex WdModelRegex();

    /// <summary>Official tool of the drive maker (internal drives only; external USB drives need nothing).</summary>
    public static VendorSoftware? StorageSoftware(DiskDetail disk)
    {
        if (disk.IsUsb) return null;
        var m = disk.Model.Trim();
        if (m.StartsWith("Samsung", StringComparison.OrdinalIgnoreCase) && disk.IsSsd) return HardwareCatalog.SamsungMagician;
        if (m.Contains("Kingston", StringComparison.OrdinalIgnoreCase) && disk.IsSsd) return HardwareCatalog.KingstonManager;
        if ((m.Contains("Crucial", StringComparison.OrdinalIgnoreCase) || CrucialModelRegex().IsMatch(m)) && disk.IsSsd) return HardwareCatalog.CrucialExecutive;
        if ((WdModelRegex().IsMatch(m) || m.StartsWith("SanDisk", StringComparison.OrdinalIgnoreCase)) && disk.IsSsd) return HardwareCatalog.SanDiskDashboardApp;
        if (m.Contains("Seagate", StringComparison.OrdinalIgnoreCase) || SeagateModelRegex().IsMatch(m)) return HardwareCatalog.SeaTools;
        return null;
    }

    // ───────────────────────────── Formatting ─────────────────────────────


    public static string Gb(ulong bytes) => bytes == 0 ? "—" : SizeText(bytes);

    /// <summary>Decimal sizes the way drive makers print them: 1 ТБ, 2 ТБ, 512 ГБ.</summary>
    public static string DiskSize(ulong bytes)
    {
        if (bytes == 0) return "—";
        var gb = bytes / 1e9;
        return gb >= 1000 ? Loc.Tf("{0} ТБ", (gb / 1000).ToString(gb >= 10000 ? "0" : "0.#", Loc.Culture)) : Loc.Tf("{0} ГБ", gb.ToString("0", Loc.Culture));
    }

    private static string SizeText(ulong bytes) => Loc.Tf("{0} ГБ", (bytes / 1073741824d).ToString("0.#", Loc.Culture));

    public static string Date(DateTime? d) => DriverFormat.Date(d);

    public static string Ghz(int mhz) => mhz <= 0 ? "" : Loc.Tf("{0} ГГц", (mhz / 1000d).ToString("0.0#", Loc.Culture));
}
