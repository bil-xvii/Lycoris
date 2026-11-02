using Lycoris.Modules.Devices.Network;

namespace Lycoris.Core.Devices;

// The network package's hook into core's writer registry (core: DeviceWriters.cs, partial InitNetwork).
public static partial class DeviceWriters
{
    static partial void InitNetwork() => Nic = new NicWriter();
}
