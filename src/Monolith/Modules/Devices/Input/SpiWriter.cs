using System.Runtime.InteropServices;
using Monolith.Core.Devices;

namespace Monolith.Modules.Devices.Input;

/// <summary>
/// SystemParametersInfo for the <see cref="SpiCatalog"/> entries (research devices_mouse-keyboard.md §3–§4). Set uses
/// SPIF_UPDATEINIFILE | SPIF_SENDCHANGE: live session + the account's HKCU value, broadcast WM_SETTINGCHANGE — which can
/// block on a hung window, so it is never called on the UI thread (ChangeSession callers are on the thread pool).
/// Only ChangeSession / the journal undo call <see cref="Set"/>; everything else here only reads.
/// </summary>
internal sealed class SpiWriter : ISpiWriter
{
    private const uint SPIF_UPDATEINIFILE = 0x1;
    private const uint SPIF_SENDCHANGE = 0x2;
    private const uint WinIni = SPIF_UPDATEINIFILE | SPIF_SENDCHANGE;

    private const uint SPI_GETMOUSE = 0x0003;
    private const uint SPI_GETFILTERKEYS = 0x0032;
    private const uint SPI_GETSTICKYKEYS = 0x003A;

    private const int SM_CXSCREEN = 0;
    private const int SM_SWAPBUTTON = 23;
    private const int SM_MOUSEHORIZONTALWHEELPRESENT = 91;

    private const int SKF_STICKYKEYSON = 0x1;
    private const int FKF_FILTERKEYSON = 0x1;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SpiGetInt(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SpiGetInts(uint uiAction, uint uiParam, [In, Out] int[] pvParam, uint fWinIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SpiSetValue(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetSystemMetrics(int nIndex);

    // ───────────────────────────── ISpiWriter ─────────────────────────────

    public int? Get(SpiEntry e)
    {
        try
        {
            switch (e.GetShape)
            {
                case SpiGet.PvInt:
                {
                    var v = 0;
                    // WHEEL_PAGESCROLL (UINT_MAX) reads as -1 — exactly what the catalog stores for «экран целиком».
                    return SpiGetInt(e.GetAction, 0, ref v, 0) ? v : null;
                }
                case SpiGet.PvBool:
                {
                    var v = 0;
                    return SpiGetInt(e.GetAction, 0, ref v, 0) ? (v != 0 ? 1 : 0) : null;
                }
                case SpiGet.DoubleClickTime:
                    return checked((int)GetDoubleClickTime());
                case SpiGet.SwapMetric:
                    return GetSystemMetrics(SM_SWAPBUTTON) != 0 ? 1 : 0;
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            return null;
        }
    }

    public bool Set(SpiEntry e, int value)
    {
        try
        {
            return e.SetShape switch
            {
                SpiSet.UiParam => SpiSetValue(e.SetAction, value == SpiCatalog.PageScroll ? uint.MaxValue : checked((uint)value), IntPtr.Zero, WinIni),
                SpiSet.PvParam => SpiSetValue(e.SetAction, 0, (IntPtr)value, WinIni),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            return false;
        }
    }

    // ───────────────────────────── Read-only helpers ─────────────────────────────

    /// <summary>«Повышенная точность указателя» live: SPI_GETMOUSE {threshold1, threshold2, acceleration}; acceleration ≠ 0 = on.</summary>
    public static bool? ReadMouseAcceleration()
    {
        try
        {
            var mouse = new int[3];
            return SpiGetInts(SPI_GETMOUSE, 0, mouse, 0) ? mouse[2] != 0 : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>STICKYKEYS (cbSize 8): SKF_STICKYKEYSON.</summary>
    public static bool ReadStickyKeysOn()
    {
        var s = ReadStruct(SPI_GETSTICKYKEYS, 2);
        return s is not null && (s[1] & SKF_STICKYKEYSON) != 0;
    }

    /// <summary>FILTERKEYS (cbSize 24): FKF_FILTERKEYSON.</summary>
    public static bool ReadFilterKeysOn()
    {
        var f = ReadStruct(SPI_GETFILTERKEYS, 6);
        return f is not null && (f[1] & FKF_FILTERKEYSON) != 0;
    }

    /// <summary>FILTERKEYS.iDelayMSec: how long a key is held before it repeats while «Фильтрация ввода» is on.</summary>
    public static int FilterKeysDelayMs() => ReadStruct(SPI_GETFILTERKEYS, 6) is { } f ? Math.Max(0, f[3]) : 0;

    /// <summary>FILTERKEYS.iWaitMSec: how long a key must be held before it counts (SlowKeys).</summary>
    public static int FilterKeysWaitMs() => ReadStruct(SPI_GETFILTERKEYS, 6) is { } f ? Math.Max(0, f[2]) : 0;

    /// <summary>A mouse with a tilt wheel is connected (SM_MOUSEHORIZONTALWHEELPRESENT).</summary>
    public static bool HasHorizontalWheel()
    {
        try { return GetSystemMetrics(SM_MOUSEHORIZONTALWHEELPRESENT) != 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>Width of the primary screen in physical pixels (Lycoris is DPI aware); 0 when unknown.</summary>
    public static int ScreenWidth()
    {
        try { return Math.Max(0, GetSystemMetrics(SM_CXSCREEN)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return 0; }
    }

    /// <summary>An accessibility struct as ints: [0] = cbSize, [1] = dwFlags, then the struct's own fields. Null on failure.</summary>
    private static int[]? ReadStruct(uint action, int ints)
    {
        try
        {
            var s = new int[ints];
            s[0] = ints * sizeof(int);
            return SpiGetInts(action, (uint)(ints * sizeof(int)), s, 0) ? s : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
