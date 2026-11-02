namespace Lycoris.Core.Devices;

// Package «input»: the SystemParametersInfo writer of the mouse and keyboard parameters (SpiCatalog).
public static partial class DeviceWriters
{
    static partial void InitInput() => Spi = new Lycoris.Modules.Devices.Input.SpiWriter();
}
