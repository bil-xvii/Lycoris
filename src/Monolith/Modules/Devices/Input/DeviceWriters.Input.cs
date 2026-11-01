namespace Monolith.Core.Devices;

// Package «input»: the SystemParametersInfo writer of the mouse and keyboard parameters (SpiCatalog).
public static partial class DeviceWriters
{
    static partial void InitInput() => Spi = new Monolith.Modules.Devices.Input.SpiWriter();
}
