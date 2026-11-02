using Lycoris.Core;
using Lycoris.Core.Devices;

namespace Lycoris.Modules.Devices.Audio;

/// <summary>
/// Endpoint volume of capture endpoints through IAudioEndpointVolume (documented). Only ChangeSession.SetAudioLevel and the
/// journal's undo call the Set* methods (DeviceWriters wraps them with the self-test SafetyLock). Every Set passes Lycoris's
/// fixed event context. Render endpoints are refused here too: Lycoris never raises an output volume. Any HRESULT failure
/// returns false; blocking, MTA only (callers on an STA thread are moved to the pool).
/// </summary>
internal sealed class AudioWriter : IAudioWriter
{
    /// <summary>
    /// False only when Windows knows no endpoint with this id (E_NOTFOUND: removed, or the driver was reinstalled and the id
    /// changed). Any other failure throws, so undo keeps its record instead of closing it; an inactive (unplugged, disabled)
    /// endpoint is present here and fails <see cref="Read"/> instead.
    /// </summary>
    public bool IsPresent(string endpointId)
    {
        if (!DeviceTargets.IsEndpointId(endpointId)) return false;
        return CoreAudio.Mta(() =>
        {
            IMMDeviceEnumerator? en = null;
            IMMDevice? dev = null;
            try
            {
                en = CoreAudio.CreateEnumerator();
                var hr = en.GetDevice(endpointId, out dev);
                if (hr == CoreAudio.E_NOTFOUND) return false;
                if (hr < 0 || dev is null) throw new InvalidOperationException(Loc.Tf("звуковое устройство не прочитано (0x{0}) — повторите позже", hr.ToString("X8")));
                return true;
            }
            finally
            {
                CoreAudio.Release(dev);
                CoreAudio.Release(en);
            }
        });
    }

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
            var ctx = CoreAudio.LycorisEventContext;
            return v.SetMasterVolumeLevelScalar(level, ref ctx) >= 0;
        }, false);
    }

    public bool SetDb(string endpointId, float db)
    {
        if (!DeviceTargets.IsCaptureEndpointId(endpointId) || !float.IsFinite(db)) return false;
        return WithVolume(endpointId, v =>
        {
            if (v.GetVolumeRange(out var min, out var max, out _) < 0 || !float.IsFinite(min) || !float.IsFinite(max) || max < min) return false;
            var ctx = CoreAudio.LycorisEventContext;
            return v.SetMasterVolumeLevel(Math.Clamp(db, min, max), ref ctx) >= 0;
        }, false);
    }

    public bool SetMute(string endpointId, bool mute)
    {
        if (!DeviceTargets.IsCaptureEndpointId(endpointId)) return false;
        return WithVolume(endpointId, v =>
        {
            var ctx = CoreAudio.LycorisEventContext;
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
            Log.Warn("Звук: {0}", ex.Message);
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
