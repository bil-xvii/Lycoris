using Monolith.Modules.Devices.Network;

namespace Monolith.Core.Devices;

// The network package's hook into core's writer registry (core: DeviceWriters.cs, partial InitNetwork).
public static partial class DeviceWriters
{
    static partial void InitNetwork() => Nic = new NicWriter();
}
