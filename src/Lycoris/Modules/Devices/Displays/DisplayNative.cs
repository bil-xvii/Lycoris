using System.Runtime.InteropServices;

namespace Lycoris.Modules.Devices.Displays;

/// <summary>
/// P/Invoke of the display APIs (user32 CCD/GDI, shell32, shcore, dxva2). Documented calls only
/// (docs/research/devices_displays.md). Low-level DDC/CI (SetVCPFeature), SaveCurrentMonitorSettings and
/// RestoreMonitorFactory* are DO-NOT-INCLUDE and deliberately not declared here.
/// </summary>
internal static class DisplayNative
{
    // ───────────────────────────── Constants ─────────────────────────────

    public const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    public const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int ENUM_REGISTRY_SETTINGS = -2;

    public const uint DM_INTERLACED = 0x2;
    public const uint DM_BITSPERPEL = 0x40000;
    public const uint DM_PELSWIDTH = 0x80000;
    public const uint DM_PELSHEIGHT = 0x100000;
    public const uint DM_DISPLAYFREQUENCY = 0x400000;

    public const uint CDS_UPDATEREGISTRY = 0x1;
    public const uint CDS_TEST = 0x2;
    public const int DISP_CHANGE_SUCCESSFUL = 0;
    public const int DISP_CHANGE_RESTART = 1;

    public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    public const uint SDC_USE_DATABASE_CURRENT = 0xF;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    public const uint SDC_APPLY = 0x80;
    public const uint SDC_ALLOW_CHANGES = 0x400;

    public const int DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const int DEVICE_INFO_GET_TARGET_NAME = 2;
    public const int DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;
    public const int DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    public const int DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;
    public const int DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;
    public const int DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 15;
    public const int DEVICE_INFO_SET_HDR_STATE = 16;

    public const int SM_REMOTESESSION = 0x1000;

    /// <summary>SHQueryUserNotificationState: busy (full-screen app), D3D full screen, presentation mode.</summary>
    public const int QUNS_BUSY = 2;
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    public const int QUNS_PRESENTATION_MODE = 4;

    public const int MDT_EFFECTIVE_DPI = 0;
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    // DISPLAYCONFIG_OUTPUT_TECHNOLOGY
    public const int OUTPUT_HD15 = 0;
    public const int OUTPUT_DVI = 4;
    public const int OUTPUT_HDMI = 5;
    public const int OUTPUT_LVDS = 6;
    public const int OUTPUT_DISPLAYPORT_EXTERNAL = 10;
    public const int OUTPUT_DISPLAYPORT_EMBEDDED = 11;
    public const int OUTPUT_UDI_EMBEDDED = 13;
    public const int OUTPUT_MIRACAST = 15;
    public const int OUTPUT_INDIRECT_WIRED = 16;
    public const int OUTPUT_INDIRECT_VIRTUAL = 17;
    public const int OUTPUT_DISPLAYPORT_USB_TUNNEL = 18;
    public const int OUTPUT_INTERNAL = unchecked((int)0x80000000);

    /// <summary>Built-in panels: INTERNAL, DISPLAYPORT_EMBEDDED, UDI_EMBEDDED, LVDS (same set as Dashboard DisplayProbe).</summary>
    public static bool IsInternal(int tech) => tech is OUTPUT_INTERNAL or OUTPUT_DISPLAYPORT_EMBEDDED or OUTPUT_UDI_EMBEDDED or OUTPUT_LVDS;

    // ───────────────────────────── Structs ─────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
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
    public struct DEVMODE
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

    public static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct REGION_2D
    {
        public uint cx;
        public uint cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public int outputTechnology;
        public int rotation;
        public int scaling;
        public RATIONAL refreshRate;
        public int scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PATH_INFO
    {
        public PATH_SOURCE_INFO sourceInfo;
        public PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>DISPLAYCONFIG_VIDEO_SIGNAL_INFO (48 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public RATIONAL hSyncFreq;
        public RATIONAL vSyncFreq;
        public REGION_2D activeSize;
        public REGION_2D totalSize;
        public uint videoStandard;
        public int scanLineOrdering;
    }

    /// <summary>DISPLAYCONFIG_SOURCE_MODE (20 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SOURCE_MODE
    {
        public uint width;
        public uint height;
        public int pixelFormat;
        public int positionX;
        public int positionY;
    }

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO: the full 64-byte struct with its union (target mode / source mode / desktop image), so that
    /// a snapshot taken by QueryDisplayConfig can be handed back unchanged to SetDisplayConfig (last-resort revert).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct MODE_INFO
    {
        public const int TypeSource = 1;
        public const int TypeTarget = 2;

        [FieldOffset(0)] public int infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public VIDEO_SIGNAL_INFO targetVideoSignal;
        [FieldOffset(16)] public SOURCE_MODE sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags;
        public int outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    /// <summary>DISPLAYCONFIG_TARGET_PREFERRED_MODE: width/height in the target's own (unrotated) orientation.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TARGET_PREFERRED_MODE
    {
        public DEVICE_INFO_HEADER header;
        public uint width;
        public uint height;
        public VIDEO_SIGNAL_INFO targetMode;
    }

    /// <summary>
    /// DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO (type 9): bits advancedColorSupported 0, advancedColorEnabled 1,
    /// wideColorEnforced 2, advancedColorForceDisabled 3.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ADVANCED_COLOR_INFO
    {
        public DEVICE_INFO_HEADER header;
        public uint value;
        public int colorEncoding;
        public uint bitsPerColorChannel;
    }

    /// <summary>
    /// DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 (type 15, SDK 26100 wingdi.h): bits advancedColorSupported 0,
    /// advancedColorActive 1, advancedColorLimitedByPolicy 3, highDynamicRangeSupported 4, highDynamicRangeUserEnabled 5,
    /// wideColorSupported 6, wideColorUserEnabled 7.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ADVANCED_COLOR_INFO_2
    {
        public DEVICE_INFO_HEADER header;
        public uint value;
        public int colorEncoding;
        public uint bitsPerColorChannel;
        public int activeColorMode;
    }

    /// <summary>DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE (type 10, bit 0 enableAdvancedColor) / DISPLAYCONFIG_SET_HDR_STATE (type 16, bit 0 enableHdr).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SET_STATE
    {
        public DEVICE_INFO_HEADER header;
        public uint value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SDR_WHITE_LEVEL
    {
        public DEVICE_INFO_HEADER header;
        public uint SDRWhiteLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    // ───────────────────────────── user32 ─────────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevicesW(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettingsExW(string deviceName, int modeNum, ref DEVMODE devMode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ChangeDisplaySettingsExW(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    /// <summary>The NULL overload: ChangeDisplaySettingsEx(NULL, NULL, NULL, 0, NULL) returns to the saved (registry) mode.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int ChangeDisplaySettingsExW(string? deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int SetDisplayConfig(uint numPathArrayElements, [In] PATH_INFO[]? pathArray, uint numModeInfoArrayElements,
        [In] MODE_INFO[]? modeInfoArray, uint flags);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int DisplayConfigGetDeviceInfo(ref TARGET_PREFERRED_MODE requestPacket);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int DisplayConfigGetDeviceInfo(ref ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int DisplayConfigGetDeviceInfo(ref ADVANCED_COLOR_INFO_2 requestPacket);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int DisplayConfigGetDeviceInfo(ref SDR_WHITE_LEVEL requestPacket);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int DisplayConfigSetDeviceInfo(ref SET_STATE setPacket);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    // ───────────────────────────── shell32 / shcore ─────────────────────────────

    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int SHQueryUserNotificationState(out int state);

    [DllImport("shcore.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ───────────────────────────── dxva2 (high-level monitor configuration) ─────────────────────────────

    [DllImport("dxva2.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorBrightness(IntPtr hPhysicalMonitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMonitorBrightness(IntPtr hPhysicalMonitor, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorContrast(IntPtr hPhysicalMonitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMonitorContrast(IntPtr hPhysicalMonitor, uint value);
}
