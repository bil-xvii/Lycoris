using Monolith.Modules.Devices.Displays;

namespace Monolith.Core.Devices;

/// <summary>Package «displays»: CCD/GDI modes and HDR (with the guard process) and DDC/CI brightness/contrast.</summary>
public static partial class DeviceWriters
{
    static partial void InitDisplays()
    {
        Display = new DisplayWriter();
        Monitor = new DdcMonitorWriter();
    }
}
