namespace Lycoris.Core.Devices;

// Package «audio»: the capture-endpoint level writer (IAudioEndpointVolume).
public static partial class DeviceWriters
{
    static partial void InitAudio() => Audio = new Lycoris.Modules.Devices.Audio.AudioWriter();
}
