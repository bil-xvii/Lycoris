namespace Monolith.Modules.Devices;

// Package «audio»: headphones, speakers and microphones («Звук»).
public static partial class DeviceProviders
{
    static partial void AddAudio(List<IDeviceProvider> p) => p.Add(new Audio.AudioProvider());
}
