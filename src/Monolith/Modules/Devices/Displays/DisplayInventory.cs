using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Dashboard.Monitoring;
using static Monolith.Modules.Devices.Displays.DisplayNative;

namespace Monolith.Modules.Devices.Displays;

/// <summary>A resolution as Windows uses it (already in the current orientation: a portrait monitor is 1440×2560).</summary>
public readonly record struct DisplaySize(int Width, int Height)
{
    public long Pixels => (long)Width * Height;
    /// <summary>"2560x1440": the option key of the resolution list.</summary>
    public string Key => $"{Width.ToString(CultureInfo.InvariantCulture)}x{Height.ToString(CultureInfo.InvariantCulture)}";
    public override string ToString() => $"{Width.ToString(CultureInfo.InvariantCulture)}×{Height.ToString(CultureInfo.InvariantCulture)}";

    public static DisplaySize? FromKey(string? key)
    {
        if (key is null) return null;
        var x = key.IndexOf('x');
        return x > 0 && int.TryParse(key[..x], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                     && int.TryParse(key[(x + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                     && w is >= 320 and <= 16384 && h is >= 320 and <= 16384
            ? new DisplaySize(w, h) : null;
    }
}

/// <summary>A mode the driver offers (EnumDisplaySettingsEx: progressive, 32 bpp, current orientation), GDI integer rate.</summary>
public sealed record OfferedMode(int Width, int Height, int Hz)
{
    public DisplaySize Size => new(Width, Height);
}

/// <summary>Advanced colour of a monitor (type 15 on 24H2+, else type 9).</summary>
public sealed record AdvancedColorInfo(bool HdrSupported, bool HdrEnabled, bool WcgSupported, bool WcgEnabled, int Bpc, bool LimitedByPolicy);

/// <summary>Everything the monitor card needs, read once per scan (read-only). A failed probe leaves its field null.</summary>
public sealed record MonitorInfo
{
    /// <summary>The CCD monitor device path — the key (stable across reboots and ports).</summary>
    public required string MonitorPath { get; init; }
    public required string Name { get; init; }
    /// <summary>\\.\DISPLAYn — a hint only: the numbers are reassigned.</summary>
    public string? GdiName { get; init; }
    /// <summary>LUID of the adapter that drives the monitor, in MonitorMath.LuidKey form.</summary>
    public string AdapterKey { get; init; } = "";
    public uint TargetId { get; init; }
    public int OutputTechnology { get; init; }
    public bool IsPrimary { get; init; }
    public DisplayModeSpec? Current { get; init; }
    public IReadOnlyList<OfferedMode> Modes { get; init; } = [];
    /// <summary>CCD preferred mode size, in the current orientation.</summary>
    public DisplaySize? PreferredSize { get; init; }
    /// <summary>The pixel rate of the active signal (Hz), for the bandwidth estimate.</summary>
    public long? PixelRate { get; init; }
    public EdidInfo? Edid { get; init; }
    public AdvancedColorInfo? Color { get; init; }
    public double? SdrWhiteNits { get; init; }
    public int? ScalePercent { get; init; }
    public VcpReading? Brightness { get; init; }
    public VcpReading? Contrast { get; init; }

    public bool IsInternal => DisplayNative.IsInternal(OutputTechnology);
    public bool IsPortrait => Current is { } c && c.Width < c.Height;

    /// <summary>The EDID's first timing in the current orientation.</summary>
    public DisplaySize? EdidFirstSize => Edid?.FirstDtd is { } t
        ? IsPortrait ? new DisplaySize(Math.Min(t.Width, t.Height), Math.Max(t.Width, t.Height)) : new DisplaySize(t.Width, t.Height)
        : null;

    public string OutputName => OutputTechnology switch
    {
        OUTPUT_HDMI => "HDMI",
        OUTPUT_DISPLAYPORT_EXTERNAL => "DisplayPort",
        OUTPUT_DISPLAYPORT_USB_TUNNEL => Loc.T("DisplayPort через USB-C"),
        OUTPUT_DVI => "DVI",
        OUTPUT_HD15 => "VGA",
        OUTPUT_MIRACAST => "Miracast",
        OUTPUT_INDIRECT_WIRED or OUTPUT_INDIRECT_VIRTUAL => Loc.T("виртуальный"),
        _ when IsInternal => Loc.T("встроенный"),
        _ => "",
    };

    /// <summary>The highest offered rate at a size (0 when the size is not offered).</summary>
    public int MaxHzAt(DisplaySize size) => Modes.Where(m => m.Size == size).Select(m => m.Hz).DefaultIfEmpty(0).Max();
}

/// <summary>One active CCD path, resolved by its monitor device path.</summary>
internal sealed record ActivePath(PATH_INFO Path, string MonitorPath, string Name, string? GdiName, int OutputTechnology)
{
    public LUID TargetAdapter => Path.targetInfo.adapterId;
    public uint TargetId => Path.targetInfo.id;
}

/// <summary>CCD helpers shared by the inventory and the writers (read-only).</summary>
internal static class Ccd
{
    /// <summary>The active paths and modes (QDC_ONLY_ACTIVE_PATHS), retried when the topology changes in between.</summary>
    public static (PATH_INFO[] Paths, MODE_INFO[] Modes)? Query()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != ERROR_SUCCESS) return null;
            var paths = new PATH_INFO[Math.Max(1u, pathCount)];
            var modes = new MODE_INFO[Math.Max(1u, modeCount)];
            var rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (rc == ERROR_INSUFFICIENT_BUFFER) continue;
            if (rc != ERROR_SUCCESS) return null;
            Array.Resize(ref paths, (int)pathCount);
            Array.Resize(ref modes, (int)modeCount);
            return (paths, modes);
        }
        return null;
    }

    public static List<ActivePath> ActivePaths()
    {
        var result = new List<ActivePath>();
        if (Query() is not { } q) return result;
        foreach (var path in q.Paths)
        {
            var target = new TARGET_DEVICE_NAME();
            target.header.type = DEVICE_INFO_GET_TARGET_NAME;
            target.header.size = (uint)Marshal.SizeOf<TARGET_DEVICE_NAME>();
            target.header.adapterId = path.targetInfo.adapterId;
            target.header.id = path.targetInfo.id;
            if (DisplayConfigGetDeviceInfo(ref target) != ERROR_SUCCESS || string.IsNullOrWhiteSpace(target.monitorDevicePath)) continue;

            var source = new SOURCE_DEVICE_NAME();
            source.header.type = DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>();
            source.header.adapterId = path.sourceInfo.adapterId;
            source.header.id = path.sourceInfo.id;
            var gdi = DisplayConfigGetDeviceInfo(ref source) == ERROR_SUCCESS && !string.IsNullOrWhiteSpace(source.viewGdiDeviceName)
                ? source.viewGdiDeviceName : null;

            var tech = path.targetInfo.outputTechnology;
            var name = target.monitorFriendlyDeviceName?.Trim();
            if (string.IsNullOrEmpty(name)) name = DisplayNative.IsInternal(tech) ? Loc.T("Встроенный дисплей") : Loc.T("Монитор");
            result.Add(new ActivePath(path, target.monitorDevicePath, name, gdi, tech));
        }
        return result;
    }

    public static ActivePath? Find(string monitorPath) =>
        ActivePaths().FirstOrDefault(p => string.Equals(p.MonitorPath, monitorPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>A GDI DEVMODE (ENUM_CURRENT_SETTINGS / ENUM_REGISTRY_SETTINGS / index); null when the call fails.</summary>
    public static DEVMODE? Settings(string gdiName, int mode)
    {
        var dm = NewDevMode();
        return EnumDisplaySettingsExW(gdiName, mode, ref dm, 0) ? dm : null;
    }

    /// <summary>The mode of a GDI DEVMODE plus, when given, the exact rational refresh of the CCD path.</summary>
    public static DisplayModeSpec ToSpec(DEVMODE dm, RATIONAL? exact)
    {
        uint num = 0, den = 0;
        if (exact is { Numerator: > 0, Denominator: > 0 } r)
        {
            num = r.Numerator;
            den = r.Denominator;
        }
        return new DisplayModeSpec((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency, num, den,
            (int)dm.dmBitsPerPel, (int)dm.dmDisplayOrientation, dm.dmPositionX, dm.dmPositionY);
    }

    /// <summary>The live mode of a path: GDI size/bpp/orientation/position with the exact CCD refresh.</summary>
    public static DisplayModeSpec? CurrentMode(ActivePath p) =>
        p.GdiName is { } gdi && Settings(gdi, ENUM_CURRENT_SETTINGS) is { } dm ? ToSpec(dm, p.Path.targetInfo.refreshRate) : null;

    /// <summary>Offered modes: progressive, 32 bpp, same orientation as the current mode; distinct (w, h, Hz).</summary>
    public static List<OfferedMode> OfferedModes(string gdiName, DisplayModeSpec? current)
    {
        var portrait = current is { } c && c.Width < c.Height;
        var set = new HashSet<OfferedMode>();
        for (var i = 0; i < 8192; i++)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettingsExW(gdiName, i, ref dm, 0)) break;
            if ((dm.dmDisplayFlags & DM_INTERLACED) != 0 || dm.dmBitsPerPel != 32 || dm.dmDisplayFrequency <= 1) continue;
            var w = (int)dm.dmPelsWidth;
            var h = (int)dm.dmPelsHeight;
            if (current is not null && (w < h) != portrait && w != h) continue;
            set.Add(new OfferedMode(w, h, (int)dm.dmDisplayFrequency));
        }
        return set.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Hz).ToList();
    }

    /// <summary>Advanced colour: type 15 on build ≥ 26100 (exact HDR bits), else type 9.</summary>
    public static AdvancedColorInfo? Color(LUID adapter, uint targetId, int osBuild)
    {
        if (osBuild >= 26100)
        {
            var info2 = new ADVANCED_COLOR_INFO_2();
            info2.header.type = DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2;
            info2.header.size = (uint)Marshal.SizeOf<ADVANCED_COLOR_INFO_2>();
            info2.header.adapterId = adapter;
            info2.header.id = targetId;
            if (DisplayConfigGetDeviceInfo(ref info2) == ERROR_SUCCESS)
            {
                var v = info2.value;
                return new AdvancedColorInfo(HdrSupported: (v & 0x10) != 0, HdrEnabled: (v & 0x20) != 0, WcgSupported: (v & 0x40) != 0,
                    WcgEnabled: (v & 0x80) != 0, Bpc: (int)info2.bitsPerColorChannel, LimitedByPolicy: (v & 0x08) != 0);
            }
        }
        var info = new ADVANCED_COLOR_INFO();
        info.header.type = DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
        info.header.size = (uint)Marshal.SizeOf<ADVANCED_COLOR_INFO>();
        info.header.adapterId = adapter;
        info.header.id = targetId;
        if (DisplayConfigGetDeviceInfo(ref info) != ERROR_SUCCESS) return null;
        var value = info.value;
        var wcgEnforced = (value & 0x4) != 0;
        return new AdvancedColorInfo(HdrSupported: (value & 0x1) != 0 && !wcgEnforced, HdrEnabled: (value & 0x2) != 0 && !wcgEnforced,
            WcgSupported: wcgEnforced, WcgEnabled: wcgEnforced, Bpc: (int)info.bitsPerColorChannel, LimitedByPolicy: (value & 0x8) != 0);
    }

    /// <summary>HMONITOR of a GDI device (\\.\DISPLAYn), or zero.</summary>
    public static IntPtr MonitorHandle(string gdiName)
    {
        var found = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (GetMonitorInfoW(h, ref mi) && string.Equals(mi.szDevice, gdiName, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool IsWindows26100OrLater => Environment.OSVersion.Version.Build >= 26100;
}

/// <summary>
/// Reads every active monitor (docs/research/devices_displays.md §5). Read-only, off the UI thread: each probe is wrapped
/// and a failure leaves its field null (Log.Warn only). Never reads the SYSTEM-only GraphicsDrivers keys.
/// </summary>
public static class DisplayInventory
{
    public static IReadOnlyList<MonitorInfo> Read(int osBuild)
    {
        var list = new List<MonitorInfo>();
        List<ActivePath> paths;
        try { paths = Ccd.ActivePaths(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("Мониторы: CCD недоступен ({0}).", ex.Message);
            return list;
        }

        var q = Val(Ccd.Query, Loc.T("режимы"));
        var primaries = Ref(PrimaryGdiNames, Loc.T("основной монитор")) ?? [];
        foreach (var p in paths)
        {
            if (!DeviceTargets.IsMonitorPath(p.MonitorPath)) continue;
            var current = Ref(() => Ccd.CurrentMode(p), Loc.T("текущий режим"));
            var modes = p.GdiName is { } gdi ? Ref(() => Ccd.OfferedModes(gdi, current), Loc.T("список режимов")) ?? [] : [];
            var edid = Ref(() => EdidParser.Parse(ReadEdid(p.MonitorPath)), "EDID");
            var portrait = current is { } c && c.Width < c.Height;
            var internalPanel = DisplayNative.IsInternal(p.OutputTechnology);

            list.Add(new MonitorInfo
            {
                MonitorPath = p.MonitorPath,
                Name = Ref(() => PreferName(p.Name, edid), Loc.T("имя")) ?? p.Name,
                GdiName = p.GdiName,
                AdapterKey = MonitorMath.LuidKey(p.TargetAdapter.HighPart, p.TargetAdapter.LowPart),
                TargetId = p.TargetId,
                OutputTechnology = p.OutputTechnology,
                IsPrimary = p.GdiName is { } g && primaries.Contains(g),
                Current = current,
                Modes = modes,
                PreferredSize = Val(() => PreferredSize(p, portrait), Loc.T("родное разрешение")),
                PixelRate = q is { } qq ? Val(() => PixelRate(p, qq.Modes), Loc.T("частота пикселей")) : null,
                Edid = edid,
                Color = Ref(() => Ccd.Color(p.TargetAdapter, p.TargetId, osBuild), "HDR"),
                SdrWhiteNits = Val(() => SdrWhite(p), Loc.T("яркость SDR")),
                ScalePercent = p.GdiName is { } sg ? Val(() => Scale(sg), Loc.T("масштаб")) : null,
                Brightness = internalPanel ? null : Val(() => DdcMonitorWriter.Read(p.MonitorPath, MonitorVcp.Brightness), "DDC/CI"),
                Contrast = internalPanel ? null : Val(() => DdcMonitorWriter.Read(p.MonitorPath, MonitorVcp.Contrast), "DDC/CI"),
            });
        }
        // The primary first, then in Windows' order.
        return list.OrderByDescending(m => m.IsPrimary).ToList();
    }

    /// <summary>Is HDR on for at least one active monitor? Null when it cannot be read (cheap: CCD only).</summary>
    public static bool? AnyHdrEnabled()
    {
        try
        {
            if (Ccd.Query() is not { } q) return null;
            var build = Environment.OSVersion.Version.Build;
            var known = false;
            foreach (var path in q.Paths)
            {
                if (Ccd.Color(path.targetInfo.adapterId, path.targetInfo.id, build) is not { } color) continue;
                known = true;
                if (color.HdrEnabled) return true;
            }
            return known ? false : null;
        }
        catch
        {
            return null; // cannot tell: never a reason to fail the caller
        }
    }

    private static T? Ref<T>(Func<T?> read, string what) where T : class
    {
        try { return read(); }
        catch (Exception ex)
        {
            Log.Warn("Мониторы: не удалось прочитать {0} — {1}", what, ex.Message);
            return null;
        }
    }

    private static T? Val<T>(Func<T?> read, string what) where T : struct
    {
        try { return read(); }
        catch (Exception ex)
        {
            Log.Warn("Мониторы: не удалось прочитать {0} — {1}", what, ex.Message);
            return null;
        }
    }

    /// <summary>The friendly name, or the EDID name when Windows only says «Монитор».</summary>
    private static string PreferName(string ccdName, EdidInfo? edid) =>
        (ccdName == Loc.T("Монитор") || ccdName == Loc.Keep("Монитор") || ccdName == "Generic PnP Monitor") && edid?.Name is { Length: > 0 } n ? n : ccdName;

    private static HashSet<string> PrimaryGdiNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < 64; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0) set.Add(dd.DeviceName);
        }
        return set;
    }

    private static DisplaySize? PreferredSize(ActivePath p, bool portrait)
    {
        var pref = new TARGET_PREFERRED_MODE();
        pref.header.type = DEVICE_INFO_GET_TARGET_PREFERRED_MODE;
        pref.header.size = (uint)Marshal.SizeOf<TARGET_PREFERRED_MODE>();
        pref.header.adapterId = p.TargetAdapter;
        pref.header.id = p.TargetId;
        if (DisplayConfigGetDeviceInfo(ref pref) != ERROR_SUCCESS || pref.width < 320 || pref.height < 200) return null;
        int w = (int)pref.width, h = (int)pref.height;
        return portrait ? new DisplaySize(Math.Min(w, h), Math.Max(w, h)) : new DisplaySize(Math.Max(w, h), Math.Min(w, h));
    }

    private static long? PixelRate(ActivePath p, MODE_INFO[] modes)
    {
        var idx = p.Path.targetInfo.modeInfoIdx;
        if (idx >= modes.Length || modes[idx].infoType != MODE_INFO.TypeTarget) return null;
        var rate = modes[idx].targetVideoSignal.pixelRate;
        return rate is > 0 and < 100_000_000_000UL ? (long)rate : null;
    }

    private static double? SdrWhite(ActivePath p)
    {
        var level = new SDR_WHITE_LEVEL();
        level.header.type = DEVICE_INFO_GET_SDR_WHITE_LEVEL;
        level.header.size = (uint)Marshal.SizeOf<SDR_WHITE_LEVEL>();
        level.header.adapterId = p.TargetAdapter;
        level.header.id = p.TargetId;
        return DisplayConfigGetDeviceInfo(ref level) == ERROR_SUCCESS && level.SDRWhiteLevel > 0 ? level.SDRWhiteLevel / 1000.0 * 80 : null;
    }

    /// <summary>Effective scale in percent, read from a per-monitor-v2-aware thread (DPI × 100 / 96).</summary>
    private static int? Scale(string gdiName)
    {
        var previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            var h = Ccd.MonitorHandle(gdiName);
            if (h == IntPtr.Zero || GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out var dpi, out _) != 0 || dpi == 0) return null;
            return (int)Math.Round(dpi * 100.0 / 96);
        }
        finally
        {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    // ───────────────────────────── EDID ─────────────────────────────

    private static readonly Regex DevicePath = new(
        @"^\\\\\?\\DISPLAY#(?<model>[A-Za-z0-9]{3,8})#(?<instance>[A-Za-z0-9&_.\-]{1,120})#\{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7\}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// \\?\DISPLAY#ACR1132#5&amp;30ca09c6&amp;0&amp;UID4355#{e6f07b5f-…} → SYSTEM\CurrentControlSet\Enum\DISPLAY\ACR1132\5&amp;30ca09c6&amp;0&amp;UID4355
    /// (only the instance of the active path, never a stale one). Null for anything else.
    /// </summary>
    public static string? EdidKeyPath(string monitorPath)
    {
        var m = DevicePath.Match(monitorPath);
        return m.Success ? $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{m.Groups["model"].Value}\{m.Groups["instance"].Value}\Device Parameters" : null;
    }

    private static byte[]? ReadEdid(string monitorPath) =>
        EdidKeyPath(monitorPath) is { } key ? RegistryHelper.Read(RegHive.HKLM, key, "EDID") as byte[] : null;
}
