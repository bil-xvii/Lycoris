using Monolith.Modules.Devices.Displays;

namespace Monolith.Modules.Devices;

/// <summary>Package «displays»: the monitor cards.</summary>
public static partial class DeviceProviders
{
    static partial void AddDisplays(List<IDeviceProvider> p) => p.Add(new DisplayProvider());
}
