using Lycoris.Modules.Devices.Displays;

namespace Lycoris.Core.Devices;

/// <summary>Package «displays»: CCD/GDI modes and HDR (with the guard process) and DDC/CI brightness/contrast.</summary>
public static partial class DeviceWriters
{
    static partial void InitDisplays()
    {
        Display = new DisplayWriter();
        Monitor = new DdcMonitorWriter();
    }
}
