using System.Runtime.InteropServices;
using Monolith.Modules.Dashboard.Monitoring;

namespace Monolith.Modules.Dashboard.Checks;

/// <summary>
/// Read-only display facts: every monitor attached to the desktop with its current mode and the best refresh rate
/// it offers at that resolution (EnumDisplaySettings, per tech.md §7), plus the monitor's friendly name
/// (QueryDisplayConfig), the adapter that drives it (target LUID) and the number of outputs per adapter.
/// Never changes display settings.
/// </summary>
internal static class DisplayProbe
{
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DM_INTERLACED = 0x2;
    private const uint QDC_ALL_PATHS = 0x1;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    public static List<DisplayInfo> Read()
    {
        var names = ReadFriendlyNames();
        var list = new List<DisplayInfo>();
        for (uint i = 0; i < 32; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

            var current = NewDevMode();
            if (!EnumDisplaySettingsW(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref current)) continue;
            int width = (int)current.dmPelsWidth, height = (int)current.dmPelsHeight, hz = (int)current.dmDisplayFrequency;

            var modes = new List<DisplayMode>();
            for (var k = 0; k < 4096; k++)
            {
                var m = NewDevMode();
                if (!EnumDisplaySettingsW(dd.DeviceName, k, ref m)) break;
                modes.Add(new DisplayMode((int)m.dmPelsWidth, (int)m.dmPelsHeight, (int)m.dmDisplayFrequency, (m.dmDisplayFlags & DM_INTERLACED) != 0));
            }
            var max = Math.Max(hz, HealthRules.MaxRefreshAt(modes, width, height));

            string name;
            double? exact = null;
            string adapterKey = "";
            var isInternal = false;
            if (names.TryGetValue(dd.DeviceName, out var info))
            {
                name = info.Name;
                exact = info.Hz;
                adapterKey = info.AdapterKey;
                isInternal = info.IsInternal;
            }
            else
            {
                name = MonitorString(dd.DeviceName) ?? dd.DeviceString;
            }
            var primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
            list.Add(new DisplayInfo(string.IsNullOrWhiteSpace(name) ? "Монитор" : name.Trim(), width, height, hz, max, exact,
                adapterKey, isInternal, primary));
        }
        return list;
    }

    private static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    private static string? MonitorString(string adapterDevice)
    {
        var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        return EnumDisplayDevicesW(adapterDevice, 0, ref mon, 0) && !string.IsNullOrWhiteSpace(mon.DeviceString) ? mon.DeviceString : null;
    }

    /// <summary>
    /// Number of display outputs (targets) per adapter LUID key, from every possible path (QDC_ALL_PATHS) — connected or not.
    /// A discrete GPU with no outputs (compute card, hybrid mobile chip in a mini-PC) has none. Empty on failure.
    /// </summary>
    public static Dictionary<string, int> TargetsPerAdapter()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out var pathCount, out var modeCount) != 0 || pathCount == 0) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[Math.Max(1u, modeCount)];
            if (QueryDisplayConfig(QDC_ALL_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;
            var seen = new HashSet<(string, uint)>();
            for (var i = 0; i < pathCount && i < paths.Length; i++)
            {
                var t = paths[i].targetInfo;
                var key = MonitorMath.LuidKey(t.adapterId.HighPart, t.adapterId.LowPart);
                if (seen.Add((key, t.id))) result[key] = result.GetValueOrDefault(key) + 1;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return result;
    }

    /// <summary>GDI source name ("\\.\DISPLAY1") → monitor friendly name, exact refresh rate, target adapter and panel type.</summary>
    private static Dictionary<string, (string Name, double? Hz, string AdapterKey, bool IsInternal)> ReadFriendlyNames()
    {
        var map = new Dictionary<string, (string, double?, string, bool)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return map;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return map;

            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref source) != 0 || string.IsNullOrEmpty(source.viewGdiDeviceName)) continue;

                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                target.header.adapterId = path.targetInfo.adapterId;
                target.header.id = path.targetInfo.id;
                var name = DisplayConfigGetDeviceInfo(ref target) == 0 ? target.monitorFriendlyDeviceName?.Trim() : null;
                var isInternal = IsInternal(path.targetInfo.outputTechnology);
                if (string.IsNullOrEmpty(name))
                    name = isInternal ? "Встроенный дисплей" : "Монитор";

                var rr = path.targetInfo.refreshRate;
                double? hz = rr.Denominator > 0 && rr.Numerator > 0 ? (double)rr.Numerator / rr.Denominator : null;
                var adapter = MonitorMath.LuidKey(path.targetInfo.adapterId.HighPart, path.targetInfo.adapterId.LowPart);
                map.TryAdd(source.viewGdiDeviceName, (name, hz, adapter, isInternal));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return map;
    }

    /// <summary>Internal panels: DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL, DISPLAYPORT_EMBEDDED, UDI_EMBEDDED, LVDS.</summary>
    private static bool IsInternal(int tech) => tech is unchecked((int)0x80000000) or 11 or 13 or 6;

    #region P/Invoke
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    /// <summary>DEVMODEW, display variant (220 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public int outputTechnology;
        public int rotation;
        public int scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public int scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>Only the header is read; the 48-byte union is kept as padding (total 64 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public int infoType;
        public uint id;
        public LUID adapterId;
        public ulong union0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public int outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
    #endregion
}
