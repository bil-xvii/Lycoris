using System.Globalization;
using System.Text.RegularExpressions;
using Monolith.Core;
using Monolith.Core.Devices;

namespace Monolith.Modules.Devices.Audio;

public enum AudioFlow { Render, Capture }

/// <summary>Microphone access for desktop apps (privacy settings + policy).</summary>
public enum MicPrivacy { Allow, Deny }

/// <summary>Windows spatial sound of a render endpoint (user's default format).</summary>
public enum SpatialState { Off, WindowsSonic, Other }

/// <summary>The shared-mode format: rate, valid bits, channels; container bits and block align for sample conversion.</summary>
public sealed record AudioFormat(int SampleRate, int Bits, int Channels, bool IsFloat, int ContainerBits = 0, int BlockAlign = 0);

/// <summary>Live level of one endpoint (endpoint volume).</summary>
public sealed record EndpointLevel(float Scalar, float? Db, float? DbMin, float? DbMax, bool Mute, uint? HardwareSupport = null);

/// <summary>One active audio endpoint, read-only facts (never matched by localized names for logic).</summary>
public sealed record EndpointInfo
{
    public required string Id { get; init; }
    public required AudioFlow Flow { get; init; }
    public required string Name { get; init; }
    /// <summary>PKEY_AudioEndpoint_FormFactor: 1 speakers, 3 headphones, 4 microphone, 5 headset, 9 monitor (HDMI/DP)…</summary>
    public int? FormFactor { get; init; }
    public bool IsDefaultConsole { get; init; }
    public bool IsDefaultMultimedia { get; init; }
    public bool IsDefaultCommunications { get; init; }
    public AudioFormat? Format { get; init; }
    public float? Scalar { get; init; }
    public float? Db { get; init; }
    public float? DbMin { get; init; }
    public float? DbMax { get; init; }
    public bool? Mute { get; init; }
    /// <summary>QueryHardwareSupport mask (1 volume, 2 mute, 4 meter in the device itself).</summary>
    public uint? HardwareSupport { get; init; }
    public string? ContainerId { get; init; }
    public string? Vid { get; init; }
    public string? Pid { get; init; }
    /// <summary>PnP instance id of the endpoint's device ("USB\VID_…"), never shown.</summary>
    public string? InstanceId { get; init; }
    /// <summary>«Улучшения звука» off (true) / on (false); null when the endpoint has no effects store or it is unreadable.</summary>
    public bool? EnhancementsOff { get; init; }
    public bool? SpatialSupported { get; init; }
    public SpatialState? Spatial { get; init; }
    /// <summary>UNOFFICIAL readings (undocumented PKEYs, shown as such): exclusive mode allowed / has priority. Empty = on.</summary>
    public bool? ExclusiveAllowed { get; init; }
    public bool? ExclusivePriority { get; init; }

    public bool IsDefault => IsDefaultConsole || IsDefaultMultimedia;
    public bool IsDefaultAny => IsDefaultConsole || IsDefaultMultimedia || IsDefaultCommunications;
    public bool IsDisplayAudio => FormFactor == AudioParsing.FormFactorDisplay;
    public bool IsHeadphones => FormFactor is AudioParsing.FormFactorHeadphones or AudioParsing.FormFactorHeadset;
    /// <summary>A Bluetooth headset in its hands-free (call) profile: narrow-band audio while its mic is open.</summary>
    public bool IsBluetoothHandsFree => InstanceId?.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record AudioSnapshot(IReadOnlyList<EndpointInfo> Endpoints, int? DuckingPreference, bool MonoOn, MicPrivacy Privacy)
{
    public static readonly AudioSnapshot Empty = new([], null, false, MicPrivacy.Allow);
}

/// <summary>Pure parsing of what Core Audio and the registry return (unit-tested without devices).</summary>
public static class AudioParsing
{
    public const int FormFactorSpeakers = 1;
    public const int FormFactorHeadphones = 3;
    public const int FormFactorMicrophone = 4;
    public const int FormFactorHeadset = 5;
    public const int FormFactorDisplay = 9;

    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatExtensible = 0xFFFE;
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// WAVEFORMATEX (18 bytes + cbSize) or WAVEFORMATEXTENSIBLE (40 bytes): rate, valid bits (24 in a 32-bit container
    /// reads as 24), channels, float or PCM. Null for anything malformed or not PCM/float.
    /// </summary>
    public static AudioFormat? ParseWaveFormat(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 16) return null;
        var tag = BitConverter.ToUInt16(blob[..2]);
        var channels = BitConverter.ToUInt16(blob[2..4]);
        var rate = BitConverter.ToUInt32(blob[4..8]);
        var blockAlign = BitConverter.ToUInt16(blob[12..14]);
        var bits = BitConverter.ToUInt16(blob[14..16]);
        if (channels is 0 or > 32 || rate is < 1000 or > 1_000_000 || bits is 0 or > 64) return null;

        var valid = (int)bits;
        bool isFloat;
        switch (tag)
        {
            case WaveFormatPcm:
                isFloat = false;
                break;
            case WaveFormatIeeeFloat:
                isFloat = true;
                break;
            case WaveFormatExtensible:
                if (blob.Length < 40) return null;
                var validBits = BitConverter.ToUInt16(blob[18..20]);
                var sub = new Guid(blob[24..40]);
                if (sub == SubtypeFloat) isFloat = true;
                else if (sub == SubtypePcm) isFloat = false;
                else return null;
                if (validBits is > 0 and <= 64 && validBits <= bits) valid = validBits;
                break;
            default:
                return null;
        }
        return new AudioFormat((int)rate, valid, channels, isFloat, bits, blockAlign);
    }

    private static readonly Regex UsbIdPattern = new(@"(?:^|[\\.])USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>"{1}.USB\VID_054C&amp;PID_0F80&amp;MI_00\7&amp;…" → ("054C", "0F80"); HDAUDIO, Bluetooth and anything else → null.</summary>
    public static (string Vid, string Pid)? ParseUsbIds(string? instancePath) =>
        instancePath is not null && UsbIdPattern.Match(instancePath) is { Success: true } m
            ? (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant())
            : null;

    /// <summary>The PnP instance id from the endpoint's instance path: "{1}.USB\VID_…" → "USB\VID_…".</summary>
    public static string? InstanceIdFromPath(string? instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath)) return null;
        var m = Regex.Match(instancePath, @"^\{\d+\}\.(.+)$", RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : instancePath;
    }

    private static readonly Regex EndpointGuid = new(@"^\{0\.0\.[01]\.00000000\}\.(\{[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\})$",
        RegexOptions.CultureInvariant);

    /// <summary>"{0.0.1.00000000}.{8baa…}" → "{8baa…}" (the MMDevices registry key name); null for anything else.</summary>
    public static string? EndpointRegistryKey(string? endpointId) =>
        endpointId is not null && EndpointGuid.Match(endpointId) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Capture ids are {0.0.1…}, render ids {0.0.0…}.</summary>
    public static AudioFlow? FlowOf(string? endpointId) =>
        endpointId is null ? null
        : endpointId.StartsWith("{0.0.1.", StringComparison.Ordinal) ? AudioFlow.Capture
        : endpointId.StartsWith("{0.0.0.", StringComparison.Ordinal) ? AudioFlow.Render
        : null;

    /// <summary>
    /// «Улучшения звука» from the FxProperties value {1da5d803-…},5 (PKEY_AudioEndpoint_Disable_SysFx): 1 → off (true);
    /// 0 or absent → on (false). No effects store at all, or a value of another type → null (unknown).
    /// </summary>
    public static bool? EnhancementsOff(bool storeExists, object? value) => !storeExists
        ? null
        : value switch
        {
            null => false,
            int i => i == 1,
            long l => l == 1,
            _ => null,
        };

    /// <summary>
    /// Mic access for desktop apps: the policy (LetAppsAccessMicrophone 1 = force allow, 2 = force deny) wins; otherwise any
    /// «Deny» (user switch, desktop-apps switch, machine switch) means Deny.
    /// </summary>
    public static MicPrivacy CombinePrivacy(string? user, string? userNonPackaged, string? machine, int? policy)
    {
        if (policy == 2) return MicPrivacy.Deny;
        if (policy == 1) return MicPrivacy.Allow;
        return IsDeny(user) || IsDeny(userNonPackaged) || IsDeny(machine) ? MicPrivacy.Deny : MicPrivacy.Allow;
    }

    private static bool IsDeny(string? v) => string.Equals(v?.Trim(), "Deny", StringComparison.OrdinalIgnoreCase);

    /// <summary>The user's default spatial format: empty / Guid.Empty = off; the Windows Sonic subtype; anything else.</summary>
    public static SpatialState Spatial(string? defaultFormat, string? windowsSonicSubtype)
    {
        var format = NormalizeGuid(defaultFormat);
        if (format is null || format == Guid.Empty) return SpatialState.Off;
        return NormalizeGuid(windowsSonicSubtype) is { } sonic && sonic == format ? SpatialState.WindowsSonic : SpatialState.Other;
    }

    private static Guid? NormalizeGuid(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : Guid.TryParse(s.Trim(), out var g) ? g : null;

    /// <summary>"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{e6327cad-…}" — the WinRT device interface id of a render endpoint.</summary>
    public static string RenderInterfaceId(string endpointId) => $@"\\?\SWD#MMDEVAPI#{endpointId}#{{e6327cad-dcec-4949-ae8a-991e976a79d2}}";

    // ───────────────────────────── Text ─────────────────────────────

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>48000 → «48 кГц», 44100 → «44,1 кГц».</summary>
    public static string RateText(int rate) => $"{(rate / 1000.0).ToString("0.#", Ru)} кГц";

    public static string ChannelsText(int channels) => channels switch
    {
        1 => "моно",
        2 => "стерео",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels} кан.",
    };

    /// <summary>«48 кГц · 16 бит · стерео».</summary>
    public static string FormatText(AudioFormat f) => $"{RateText(f.SampleRate)} · {f.Bits} бит · {ChannelsText(f.Channels)}";

    /// <summary>«+5,6 дБ» / «−16 дБ» (typographic minus).</summary>
    public static string DbText(double db) => SignedNumber(db, "0.#") + " дБ";

    /// <summary>A signed number with the typographic minus and a plus for positive values: «+15», «−9», «0».</summary>
    public static string SignedNumber(double value, string format)
    {
        var rounded = Math.Round(value, format.Contains('.') ? 1 : 0, MidpointRounding.AwayFromZero);
        var text = Math.Abs(rounded).ToString(format, Ru);
        return rounded > 0 ? "+" + text : rounded < 0 ? "\u2212" + text : text;
    }

    /// <summary>«−16…+15 дБ».</summary>
    public static string RangeText(double min, double max) => $"{SignedNumber(min, "0.#")}…{SignedNumber(max, "0.#")} дБ";

    /// <summary>A monitor endpoint «X27U W3 (NVIDIA High Definition Audio)» → «X27U W3» (the adapter is not the device).</summary>
    public static string DisplayName(string name)
    {
        var open = name.LastIndexOf(" (", StringComparison.Ordinal);
        return open > 0 && name.EndsWith(')') ? name[..open] : name;
    }

    /// <summary>How the endpoint's device is connected (never the instance-id tail).</summary>
    public static string? ConnectionText(EndpointInfo e)
    {
        var id = e.InstanceId;
        if (id is null) return null;
        if (id.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return "USB";
        if (id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return "Bluetooth";
        if (id.StartsWith("HDAUDIO", StringComparison.OrdinalIgnoreCase)) return e.IsDisplayAudio ? "HDMI/DisplayPort" : "встроенный звук";
        return null;
    }
}

/// <summary>Reads every active endpoint and the per-user audio state. Read-only: nothing here writes anywhere.</summary>
public static class AudioInventory
{
    private const string MmDevicesRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    private const string FxDisableName = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    private const string AudioUserKey = @"Software\Microsoft\Multimedia\Audio";
    private const string MicConsent = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string MicPolicy = @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy";

    /// <summary>Blocking (COM + registry); off the UI thread.</summary>
    public static AudioSnapshot Read()
    {
        var endpoints = CoreAudio.Mta(ReadEndpoints);
        return new AudioSnapshot(endpoints, ReadDuckingPreference(), ReadMonoOn(), ReadPrivacy());
    }

    /// <summary>HKCU UserDuckingPreference (DWORD 0–3); null when absent (Windows default: 1, «на 80 %»).</summary>
    public static int? ReadDuckingPreference() =>
        RegistryHelper.Read(RegHive.HKCU, DeviceTargets.DuckingPath, DeviceTargets.DuckingName) is int i ? i : null;

    public static bool ReadMonoOn() => RegistryHelper.Read(RegHive.HKCU, AudioUserKey, "AccessibilityMonoMixState") is int i && i == 1;

    public static MicPrivacy ReadPrivacy() => AudioParsing.CombinePrivacy(
        RegistryHelper.ReadString(RegHive.HKCU, MicConsent, "Value"),
        RegistryHelper.ReadString(RegHive.HKCU, MicConsent + @"\NonPackaged", "Value"),
        RegistryHelper.ReadString(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", "Value"),
        RegistryHelper.Read(RegHive.HKLM, MicPolicy, "LetAppsAccessMicrophone") is int p ? p : null);

    /// <summary>The live level of one active endpoint (for the re-read after ПРИМЕНИТЬ and the level test); null when gone.</summary>
    public static EndpointLevel? ReadLevel(string endpointId)
    {
        if (!DeviceTargets.IsEndpointId(endpointId)) return null;
        return CoreAudio.Mta(() =>
        {
            IMMDeviceEnumerator? en = null;
            IMMDevice? dev = null;
            try
            {
                en = CoreAudio.CreateEnumerator();
                dev = CoreAudio.ActiveDevice(en, endpointId);
                return dev is null ? null : ReadVolume(dev);
            }
            catch (Exception ex)
            {
                Log.Warn($"Звук: не удалось прочитать уровень — {ex.Message}");
                return null;
            }
            finally
            {
                CoreAudio.Release(dev);
                CoreAudio.Release(en);
            }
        });
    }

    private static IReadOnlyList<EndpointInfo> ReadEndpoints()
    {
        var list = new List<EndpointInfo>();
        IMMDeviceEnumerator? en = null;
        try
        {
            en = CoreAudio.CreateEnumerator();
            foreach (var flow in new[] { AudioFlow.Render, AudioFlow.Capture })
            {
                var data = flow == AudioFlow.Render ? EDataFlow.Render : EDataFlow.Capture;
                var console = DefaultId(en, data, ERole.Console);
                var multimedia = DefaultId(en, data, ERole.Multimedia);
                var comms = DefaultId(en, data, ERole.Communications);
                if (en.EnumAudioEndpoints(data, CoreAudio.DeviceStateActive, out var collection) < 0 || collection is null) continue;
                try
                {
                    if (collection.GetCount(out var count) < 0) continue;
                    for (uint i = 0; i < Math.Min(count, 64u); i++)
                    {
                        if (collection.Item(i, out var dev) < 0 || dev is null) continue;
                        try
                        {
                            if (ReadEndpoint(dev, flow, console, multimedia, comms) is { } info) list.Add(info);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Звук: устройство пропущено — {ex.Message}");
                        }
                        finally
                        {
                            CoreAudio.Release(dev);
                        }
                    }
                }
                finally
                {
                    CoreAudio.Release(collection);
                }
            }
        }
        finally
        {
            CoreAudio.Release(en);
        }
        return list;
    }

    private static string? DefaultId(IMMDeviceEnumerator en, EDataFlow flow, ERole role)
    {
        if (en.GetDefaultAudioEndpoint(flow, role, out var dev) < 0 || dev is null) return null; // E_NOTFOUND: no device
        try { return CoreAudio.GetId(dev); }
        finally { CoreAudio.Release(dev); }
    }

    private static EndpointInfo? ReadEndpoint(IMMDevice dev, AudioFlow flow, string? console, string? multimedia, string? comms)
    {
        var id = CoreAudio.GetId(dev);
        if (id is null || !DeviceTargets.IsEndpointId(id)) return null;
        if (dev.GetState(out var state) < 0 || state != CoreAudio.DeviceStateActive) return null;

        string? name = null, instancePath = null;
        int? formFactor = null;
        AudioFormat? format = null;
        Guid? container = null;
        bool? exclusiveAllowed = null, exclusivePriority = null;
        if (dev.OpenPropertyStore(CoreAudio.StgmRead, out var store) >= 0 && store is not null)
        {
            try
            {
                name = CoreAudio.GetString(store, CoreAudio.FriendlyName);
                formFactor = CoreAudio.GetUInt(store, CoreAudio.FormFactor, out _) is { } ff ? (int)ff : null;
                format = CoreAudio.GetBlob(store, CoreAudio.DeviceFormat) is { } blob ? AudioParsing.ParseWaveFormat(blob) : null;
                container = CoreAudio.GetGuid(store, CoreAudio.ContainerId);
                instancePath = CoreAudio.GetString(store, CoreAudio.InstancePath);
                exclusiveAllowed = CoreAudio.GetUInt(store, CoreAudio.ExclusiveAllow, out var hasAllow) is { } a ? a != 0 : hasAllow ? null : true;
                exclusivePriority = CoreAudio.GetUInt(store, CoreAudio.ExclusivePriority, out var hasPrio) is { } p ? p != 0 : hasPrio ? null : true;
            }
            finally
            {
                CoreAudio.Release(store);
            }
        }

        var level = ReadVolume(dev);
        var ids = AudioParsing.ParseUsbIds(instancePath);
        var (spatialSupported, spatial) = flow == AudioFlow.Render ? ReadSpatial(id) : (null, null);
        return new EndpointInfo
        {
            Id = id,
            Flow = flow,
            Name = string.IsNullOrWhiteSpace(name) ? (flow == AudioFlow.Render ? "Устройство вывода" : "Микрофон") : name.Trim(),
            FormFactor = formFactor,
            IsDefaultConsole = string.Equals(id, console, StringComparison.OrdinalIgnoreCase),
            IsDefaultMultimedia = string.Equals(id, multimedia, StringComparison.OrdinalIgnoreCase),
            IsDefaultCommunications = string.Equals(id, comms, StringComparison.OrdinalIgnoreCase),
            Format = format,
            Scalar = level?.Scalar,
            Db = level?.Db,
            DbMin = level?.DbMin,
            DbMax = level?.DbMax,
            Mute = level?.Mute,
            HardwareSupport = level?.HardwareSupport,
            ContainerId = container?.ToString("B"),
            Vid = ids?.Vid,
            Pid = ids?.Pid,
            InstanceId = AudioParsing.InstanceIdFromPath(instancePath),
            EnhancementsOff = ReadEnhancementsOff(id, flow),
            SpatialSupported = spatialSupported,
            Spatial = spatial,
            ExclusiveAllowed = exclusiveAllowed,
            ExclusivePriority = exclusivePriority,
        };
    }

    private static EndpointLevel? ReadVolume(IMMDevice dev)
    {
        var volume = CoreAudio.Activate<IAudioEndpointVolume>(dev, CoreAudio.IidAudioEndpointVolume);
        if (volume is null) return null;
        try
        {
            if (volume.GetMasterVolumeLevelScalar(out var scalar) < 0 || !float.IsFinite(scalar)) return null;
            if (volume.GetMute(out var mute) < 0) return null;
            float? db = volume.GetMasterVolumeLevel(out var d) >= 0 && float.IsFinite(d) ? d : null;
            float? min = null, max = null;
            if (volume.GetVolumeRange(out var lo, out var hi, out _) >= 0 && float.IsFinite(lo) && float.IsFinite(hi) && hi > lo)
            {
                min = lo;
                max = hi;
            }
            uint? hardware = volume.QueryHardwareSupport(out var mask) >= 0 ? mask : null;
            return new EndpointLevel(Math.Clamp(scalar, 0f, 1f), db, min, max, mute, hardware);
        }
        finally
        {
            CoreAudio.Release(volume);
        }
    }

    /// <summary>The effects store in the registry (READ ONLY — the documented property store never shows this value).</summary>
    private static bool? ReadEnhancementsOff(string id, AudioFlow flow)
    {
        if (AudioParsing.EndpointRegistryKey(id) is not { } key) return null;
        var path = $@"{MmDevicesRoot}\{(flow == AudioFlow.Render ? "Render" : "Capture")}\{key}\FxProperties";
        return AudioParsing.EnhancementsOff(RegistryHelper.KeyExists(RegHive.HKLM, path), RegistryHelper.Read(RegHive.HKLM, path, FxDisableName));
    }

    /// <summary>Windows spatial sound (WinRT, read only). Any failure → unknown.</summary>
    private static (bool? Supported, SpatialState? State) ReadSpatial(string id)
    {
        try
        {
            var config = global::Windows.Media.Audio.SpatialAudioDeviceConfiguration.GetForDeviceId(AudioParsing.RenderInterfaceId(id));
            if (config is null) return (null, null);
            if (!config.IsSpatialAudioSupported) return (false, null);
            return (true, AudioParsing.Spatial(config.DefaultSpatialAudioFormat, global::Windows.Media.Audio.SpatialAudioFormatSubtype.WindowsSonic));
        }
        catch
        {
            return (null, null);
        }
    }
}
