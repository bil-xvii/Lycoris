using Lycoris.Modules.Devices.Displays;

namespace Lycoris.Modules.Devices;

/// <summary>Package «displays»: the monitor cards.</summary>
public static partial class DeviceProviders
{
    static partial void AddDisplays(List<IDeviceProvider> p) => p.Add(new DisplayProvider());
}
