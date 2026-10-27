using Monolith.Core;
using Monolith.Core.Devices;

namespace Monolith.Modules.Devices.Audio;

/// <summary>
/// Endpoint volume of capture endpoints through IAudioEndpointVolume (documented). Only ChangeSession.SetAudioLevel and the
/// journal's undo call the Set* methods (DeviceWriters wraps them with the self-test SafetyLock). Every Set passes MONOLITH's
/// fixed event context. Render endpoints are refused here too: MONOLITH never raises an output volume. Any HRESULT failure
/// returns false; blocking, MTA only (callers on an STA thread are moved to the pool).
/// </summary>
internal sealed class AudioWriter : IAudioWriter
{
    public bool IsPresent(string endpointId) =>
        DeviceTargets.IsEndpointId(endpointId) && WithDevice(endpointId, _ => true, false);

    public (float Scalar, bool Mute)? Read(string endpointId) =>
        DeviceTargets.IsEndpointId(endpointId)
            ? WithVolume<(float, bool)?>(endpointId, v =>
                v.GetMasterVolumeLevelScalar(out var scalar) >= 0 && float.IsFinite(scalar) && v.GetMute(out var mute) >= 0
                    ? (Math.Clamp(scalar, 0f, 1f), mute)
                    : null, null)
            : null;

    public bool SetScalar(string endpointId, float scalar)
    {
        if (!DeviceTargets.IsCaptureEndpointId(endpointId) || !float.IsFinite(scalar)) return false;
        var level = Math.Clamp(scalar, 0f, 1f);
        return WithVolume(endpointId, v =>
        {
            var ctx = CoreAudio.MonolithEventContext;
            return v.SetMasterVolumeLevelScalar(level, ref ctx) >= 0;
        }, false);
    }

    public bool SetDb(string endpointId, float db)
    {
        if (!DeviceTargets.IsCaptureEndpointId(endpointId) || !float.IsFinite(db)) return false;
        return WithVolume(endpointId, v =>
        {
            if (v.GetVolumeRange(out var min, out var max, out _) < 0 || !float.IsFinite(min) || !float.IsFinite(max) || max < min) return false;
            var ctx = CoreAudio.MonolithEventContext;
            return v.SetMasterVolumeLevel(Math.Clamp(db, min, max), ref ctx) >= 0;
        }, false);
    }

    public bool SetMute(string endpointId, bool mute)
    {
        if (!DeviceTargets.IsCaptureEndpointId(endpointId)) return false;
        return WithVolume(endpointId, v =>
        {
            var ctx = CoreAudio.MonolithEventContext;
            return v.SetMute(mute, ref ctx) >= 0;
        }, false);
    }

    private static T WithDevice<T>(string endpointId, Func<IMMDevice, T> body, T fallback) => CoreAudio.Mta(() =>
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        try
        {
            en = CoreAudio.CreateEnumerator();
            dev = CoreAudio.ActiveDevice(en, endpointId);
            return dev is null ? fallback : body(dev);
        }
        catch (Exception ex)
        {
            Log.Warn($"Звук: {ex.Message}");
            return fallback;
        }
        finally
        {
            CoreAudio.Release(dev);
            CoreAudio.Release(en);
        }
    });

    private static T WithVolume<T>(string endpointId, Func<IAudioEndpointVolume, T> body, T fallback) =>
        WithDevice(endpointId, dev =>
        {
            var volume = CoreAudio.Activate<IAudioEndpointVolume>(dev, CoreAudio.IidAudioEndpointVolume);
            if (volume is null) return fallback;
            try { return body(volume); }
            finally { CoreAudio.Release(volume); }
        }, fallback);
}
