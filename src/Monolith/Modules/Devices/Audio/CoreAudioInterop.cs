using System.Runtime.InteropServices;

namespace Monolith.Modules.Devices.Audio;

// Core Audio (MMDevice API, endpoint volume, shared-mode capture) — documented COM interfaces only (research
// devices_audio.md §2). Every method is [PreserveSig] with an int HRESULT: without it the CLR treats the return value as an
// extra [out, retval] parameter, and on x64 the calls "work" while every HRESULT check reads garbage (seen in the research).
// Vtable order below is exactly the SDK headers' order (mmdeviceapi.h, endpointvolume.h, audioclient.h, propsys.h).
// IPolicyConfig, FxProperties writes, MMCSS and every other DO-NOT-INCLUDE mechanism are deliberately absent.

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid fmtid, uint pid)
{
    public Guid FormatId = fmtid;
    public uint PropertyId = pid;
}

/// <summary>PROPVARIANT (24 bytes on x64): vt, three reserved words, then the 16-byte value union.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Vt;
    /// <summary>VT_LPWSTR (pwszVal), VT_CLSID (puuid).</summary>
    [FieldOffset(8)] public IntPtr Pointer;
    /// <summary>VT_UI4 (ulVal).</summary>
    [FieldOffset(8)] public uint UInt;
    /// <summary>VT_BLOB: cbSize.</summary>
    [FieldOffset(8)] public uint BlobSize;
    /// <summary>VT_BLOB: pBlobData (aligned to 8 after cbSize).</summary>
    [FieldOffset(16)] public IntPtr BlobData;
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorCoClass;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    /// <summary>Declared for the vtable; MONOLITH registers no notification client.</summary>
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    /// <summary>Declared for the vtable only: MONOLITH opens the store with STGM_READ and never writes it.</summary>
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint channels);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
    [PreserveSig] int VolumeStepUp(ref Guid eventContext);
    [PreserveSig] int VolumeStepDown(ref Guid eventContext);
    [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
    [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint framesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint framesRead);
    [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
}

/// <summary>Constants, property keys and small helpers around the interfaces above.</summary>
internal static class CoreAudio
{
    public const uint DeviceStateActive = 0x1;
    public const uint ClsCtxAll = 0x17;
    public const uint StgmRead = 0x0;
    public const int AudclntSharemodeShared = 0;
    public const uint AudclntBufferflagsSilent = 0x2;
    public const int E_NOTFOUND = unchecked((int)0x80070490);

    public const ushort VtEmpty = 0;
    public const ushort VtUi4 = 19;
    public const ushort VtLpwstr = 31;
    public const ushort VtBlob = 65;
    public const ushort VtClsid = 72;

    /// <summary>ENDPOINT_HARDWARE_SUPPORT_VOLUME / _MUTE / _METER.</summary>
    public const uint HardwareVolume = 0x1;
    public const uint HardwareMute = 0x2;

    /// <summary>
    /// Passed to every Set* call, so a volume-change callback (or a later drift check) can tell MONOLITH's own changes
    /// from the user's.
    /// </summary>
    public static readonly Guid MonolithEventContext = new("4d4f4e4f-4c49-5448-8a75-64696f000001");

    public static readonly Guid IidAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    public static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    // PROPERTYKEYs (functiondiscoverykeys_devpkey.h, mmdeviceapi.h).
    public static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    public static readonly PropertyKey FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    public static readonly PropertyKey DeviceFormat = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);
    public static readonly PropertyKey ContainerId = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    /// <summary>"{1}.USB\VID_054C&amp;PID_0F80&amp;MI_00\…" — the endpoint's PnP device.</summary>
    public static readonly PropertyKey InstancePath = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);
    /// <summary>UNOFFICIAL (not in the SDK headers): «Allow applications to take exclusive control». VT_EMPTY = Windows default (on).</summary>
    public static readonly PropertyKey ExclusiveAllow = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 3);
    /// <summary>UNOFFICIAL (not in the SDK headers): «Give exclusive mode applications priority». VT_EMPTY = Windows default (on).</summary>
    public static readonly PropertyKey ExclusivePriority = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 4);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant value);

    public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();

    /// <summary>Releases an RCW now (never waits for the finalizer); ignores anything that is not a COM object.</summary>
    public static void Release(object? o)
    {
        if (o is null || !Marshal.IsComObject(o)) return;
        try { Marshal.ReleaseComObject(o); }
        catch { /* already released */ }
    }

    /// <summary>
    /// Core Audio is used from MTA threads only (thread pool): a caller on an STA thread (the UI) is moved to the pool and
    /// waits, which cannot deadlock because nothing here needs the caller's apartment.
    /// </summary>
    public static T Mta<T>(Func<T> body) =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA ? body() : Task.Run(body).GetAwaiter().GetResult();

    /// <summary>The device with this endpoint id when it exists and is active; null otherwise (the caller releases it).</summary>
    public static IMMDevice? ActiveDevice(IMMDeviceEnumerator enumerator, string id)
    {
        if (enumerator.GetDevice(id, out var device) < 0 || device is null) return null;
        if (device.GetState(out var state) < 0 || state != DeviceStateActive)
        {
            Release(device);
            return null;
        }
        return device;
    }

    public static T? Activate<T>(IMMDevice device, Guid iid) where T : class
    {
        var id = iid;
        if (device.Activate(ref id, ClsCtxAll, IntPtr.Zero, out var instance) < 0) return null;
        if (instance is T typed) return typed;
        Release(instance);
        return null;
    }

    public static string? GetId(IMMDevice device) => device.GetId(out var id) >= 0 ? id : null;

    // ───────────────────────────── Property store reads ─────────────────────────────

    private static bool TryGet(IPropertyStore store, PropertyKey key, out PropVariant value)
    {
        var k = key;
        if (store.GetValue(ref k, out value) >= 0) return true;
        value = default;
        return false;
    }

    public static string? GetString(IPropertyStore store, PropertyKey key)
    {
        if (!TryGet(store, key, out var v)) return null;
        try
        {
            return v.Vt == VtLpwstr && v.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(v.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref v);
        }
    }

    /// <summary>The value; null when absent (VT_EMPTY) or of another type. <paramref name="present"/> tells the two apart.</summary>
    public static uint? GetUInt(IPropertyStore store, PropertyKey key, out bool present)
    {
        present = false;
        if (!TryGet(store, key, out var v)) return null;
        try
        {
            present = v.Vt != VtEmpty;
            return v.Vt == VtUi4 ? v.UInt : null;
        }
        finally
        {
            PropVariantClear(ref v);
        }
    }

    public static byte[]? GetBlob(IPropertyStore store, PropertyKey key)
    {
        if (!TryGet(store, key, out var v)) return null;
        try
        {
            if (v.Vt != VtBlob || v.BlobData == IntPtr.Zero || v.BlobSize is 0 or > 4096) return null;
            var bytes = new byte[v.BlobSize];
            Marshal.Copy(v.BlobData, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            PropVariantClear(ref v);
        }
    }

    public static Guid? GetGuid(IPropertyStore store, PropertyKey key)
    {
        if (!TryGet(store, key, out var v)) return null;
        try
        {
            return v.Vt == VtClsid && v.Pointer != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(v.Pointer) : null;
        }
        finally
        {
            PropVariantClear(ref v);
        }
    }

    /// <summary>A WAVEFORMATEX(TENSIBLE) in native memory (GetMixFormat), copied out for the pure parser.</summary>
    public static byte[] CopyWaveFormat(IntPtr format)
    {
        var head = new byte[18];
        Marshal.Copy(format, head, 0, head.Length);
        var extra = BitConverter.ToUInt16(head, 16);
        var total = 18 + Math.Min((int)extra, 22);
        var all = new byte[total];
        Marshal.Copy(format, all, 0, total);
        return all;
    }
}
