namespace Monolith.Modules.Devices;

// Package «input»: the mouse and keyboard cards.
public static partial class DeviceProviders
{
    static partial void AddInput(List<IDeviceProvider> p) => p.Add(new Input.InputProvider());
}
