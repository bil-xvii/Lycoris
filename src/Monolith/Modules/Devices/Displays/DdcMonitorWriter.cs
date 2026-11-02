using Monolith.Core;
using Monolith.Core.Devices;
using static Monolith.Modules.Devices.Displays.DisplayNative;

namespace Monolith.Modules.Devices.Displays;

/// <summary>
/// Monitor brightness and contrast over DDC/CI through the documented high-level monitor configuration API (dxva2):
/// only VCP 0x10 and 0x12, never vendor codes, factory resets or SaveCurrentMonitorSettings (DO-NOT-INCLUDE). The
/// monitor path → CCD source → HMONITOR → exactly one physical monitor; the handles are always destroyed. Each call
/// takes about 50 ms: callers are off the UI thread. A monitor counts as supported when the getter works (the
/// capabilities string fails on some monitors that do support it).
/// </summary>
internal sealed class DdcMonitorWriter : IMonitorWriter
{
    public VcpReading? Get(string monitorPath, MonitorVcp code) => Read(monitorPath, code);

    public bool Set(string monitorPath, MonitorVcp code, uint value)
    {
        SafetyLock.ThrowIfLocked(Loc.T("изменение яркости или контраста монитора"));
        if (!DeviceTargets.IsMonitorPath(monitorPath) || code is not (MonitorVcp.Brightness or MonitorVcp.Contrast)) return false;
        var ok = WithPhysicalMonitor(monitorPath, h => code == MonitorVcp.Contrast ? SetMonitorContrast(h, value) : SetMonitorBrightness(h, value));
        if (ok != true)
        {
            Log.Warn("DDC/CI: монитор не принял {0} {1}.", code == MonitorVcp.Contrast ? Loc.T("контраст") : Loc.T("яркость"), value);
            return false;
        }
        return Read(monitorPath, code)?.Value == value;
    }

    /// <summary>The reading, or null when the monitor does not answer over DDC/CI (or answers nonsense).</summary>
    public static VcpReading? Read(string monitorPath, MonitorVcp code)
    {
        if (!DeviceTargets.IsMonitorPath(monitorPath) || code is not (MonitorVcp.Brightness or MonitorVcp.Contrast)) return null;
        VcpReading? reading = null;
        WithPhysicalMonitor(monitorPath, h =>
        {
            uint min, cur, max;
            var ok = code == MonitorVcp.Contrast ? GetMonitorContrast(h, out min, out cur, out max) : GetMonitorBrightness(h, out min, out cur, out max);
            if (ok && max > min && max <= 10_000 && cur <= max) reading = new VcpReading(min, cur, max);
            return ok;
        });
        return reading;
    }

    /// <summary>Runs <paramref name="action"/> on the one physical monitor behind the path; null when there is not exactly one.</summary>
    private static bool? WithPhysicalMonitor(string monitorPath, Func<IntPtr, bool> action)
    {
        try
        {
            if (Ccd.Find(monitorPath) is not { GdiName: { } gdi }) return null;
            var hMonitor = Ccd.MonitorHandle(gdi);
            if (hMonitor == IntPtr.Zero) return null;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count != 1) return null;
            var monitors = new PHYSICAL_MONITOR[1];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, 1, monitors)) return null;
            try
            {
                return action(monitors[0].hPhysicalMonitor);
            }
            finally
            {
                DestroyPhysicalMonitors(1, monitors);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
