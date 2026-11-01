using Monolith.Modules.Devices.Network;

namespace Monolith.Modules.Devices;

// The network package's hook into core's provider list (core: DeviceProviders.cs, partial AddNetwork).
public static partial class DeviceProviders
{
    static partial void AddNetwork(List<IDeviceProvider> p) => p.Add(new NetworkProvider());
}
