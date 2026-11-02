using Lycoris.Core.Devices;
using Lycoris.Modules.Devices.Audio;

namespace Lycoris.Tests.Devices;

/// <summary>Pure parsing of Core Audio blobs, instance paths, endpoint ids and the registry readings (no devices touched).</summary>
public sealed class AudioParsingTests
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private static byte[] WaveFormatEx(ushort tag, ushort channels, uint rate, ushort bits, ushort cbSize = 0)
    {
        var block = (ushort)(channels * bits / 8);
        var b = new List<byte>();
        b.AddRange(BitConverter.GetBytes(tag));
        b.AddRange(BitConverter.GetBytes(channels));
        b.AddRange(BitConverter.GetBytes(rate));
        b.AddRange(BitConverter.GetBytes(rate * block));
        b.AddRange(BitConverter.GetBytes(block));
        b.AddRange(BitConverter.GetBytes(bits));
        b.AddRange(BitConverter.GetBytes(cbSize));
        return [.. b];
    }

    private static byte[] Extensible(ushort channels, uint rate, ushort container, ushort valid, Guid subtype)
    {
        var b = new List<byte>(WaveFormatEx(0xFFFE, channels, rate, container, 22));
        b.AddRange(BitConverter.GetBytes(valid));
        b.AddRange(BitConverter.GetBytes(channels == 1 ? 0x4u : 0x3u));
        b.AddRange(subtype.ToByteArray());
        return [.. b];
    }

    [Fact]
    public void Waveformatex_48000_16_stereo()
    {
        var f = AudioParsing.ParseWaveFormat(WaveFormatEx(1, 2, 48000, 16));
        Assert.Equal(new AudioFormat(48000, 16, 2, false, 16, 4), f);
        Assert.Equal("48 кГц · 16 бит · стерео", AudioParsing.FormatText(f!));
        Assert.Equal("48 кГц · 16 бит", AudioParsing.RateBitsText(f!));
    }

    [Fact]
    public void Waveformatextensible_48000_24_mono_in_a_32_bit_container_reads_as_24_bits()
    {
        var f = AudioParsing.ParseWaveFormat(Extensible(1, 48000, 32, 24, SubtypePcm));
        Assert.NotNull(f);
        Assert.Equal((48000, 24, 1, false, 32), (f!.SampleRate, f.Bits, f.Channels, f.IsFloat, f.ContainerBits));
        Assert.Equal("48 кГц · 24 бит · моно", AudioParsing.FormatText(f));
    }

    [Fact]
    public void Float_mix_formats_are_recognised_in_both_forms()
    {
        Assert.True(AudioParsing.ParseWaveFormat(Extensible(2, 48000, 32, 32, SubtypeFloat))!.IsFloat);
        Assert.True(AudioParsing.ParseWaveFormat(WaveFormatEx(3, 2, 44100, 32))!.IsFloat);
        Assert.Equal("44,1 кГц", AudioParsing.RateText(44100));
    }

    [Fact]
    public void Malformed_or_unknown_formats_are_null()
    {
        Assert.Null(AudioParsing.ParseWaveFormat([]));
        Assert.Null(AudioParsing.ParseWaveFormat(new byte[10]));
        Assert.Null(AudioParsing.ParseWaveFormat(WaveFormatEx(0x55, 2, 48000, 16)));          // MP3
        Assert.Null(AudioParsing.ParseWaveFormat(WaveFormatEx(1, 0, 48000, 16)));             // no channels
        Assert.Null(AudioParsing.ParseWaveFormat(WaveFormatEx(0xFFFE, 2, 48000, 16, 22)));    // extensible, cut short
        Assert.Null(AudioParsing.ParseWaveFormat(Extensible(2, 48000, 16, 16, Guid.NewGuid()))); // unknown subtype
    }

    [Theory]
    [InlineData(@"{1}.USB\VID_054C&PID_0F80&MI_00\7&1A2B3C4D&0&0000", "054C", "0F80")]
    [InlineData(@"{1}.USB\VID_1532&PID_056A&MI_00\7&2B3C4D5E&0&0000", "1532", "056A")]
    [InlineData(@"USB\vid_046d&pid_0a87&mi_00\6&abc", "046D", "0A87")]
    public void Instance_path_gives_usb_vid_and_pid(string path, string vid, string pid) =>
        Assert.Equal<(string, string)?>((vid, pid), AudioParsing.ParseUsbIds(path));

    [Theory]
    [InlineData(@"{1}.HDAUDIO\FUNC_01&VEN_10DE&DEV_00AB&SUBSYS_10DE0000&REV_1001\5&2BB51DC2&0&0001")]
    [InlineData(@"{1}.BTHHFENUM\BthHFPAudio\8&1234")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_usb_paths_have_no_vid_pid(string? path) => Assert.Null(AudioParsing.ParseUsbIds(path));

    [Fact]
    public void Instance_id_drops_the_property_store_prefix()
    {
        Assert.Equal(@"USB\VID_054C&PID_0F80&MI_00\7&X", AudioParsing.InstanceIdFromPath(@"{1}.USB\VID_054C&PID_0F80&MI_00\7&X"));
        Assert.Equal(@"USB\VID_054C", AudioParsing.InstanceIdFromPath(@"USB\VID_054C"));
        Assert.Null(AudioParsing.InstanceIdFromPath(" "));
    }

    [Fact]
    public void Endpoint_id_gives_its_registry_key_and_flow()
    {
        const string capture = "{0.0.1.00000000}.{8baa6d9f-01c8-4bfe-b9e5-758c42eb3681}";
        const string render = "{0.0.0.00000000}.{13115c35-2308-4ab1-8fa0-2a1a69dff0a8}";
        Assert.Equal("{8baa6d9f-01c8-4bfe-b9e5-758c42eb3681}", AudioParsing.EndpointRegistryKey(capture));
        Assert.Equal("{13115c35-2308-4ab1-8fa0-2a1a69dff0a8}", AudioParsing.EndpointRegistryKey(render));
        Assert.Null(AudioParsing.EndpointRegistryKey(@"{0.0.1.00000000}.{8baa6d9f}\..\..\Run"));
        Assert.Null(AudioParsing.EndpointRegistryKey(null));
        Assert.Equal(AudioFlow.Capture, AudioParsing.FlowOf(capture));
        Assert.Equal(AudioFlow.Render, AudioParsing.FlowOf(render));
        Assert.Equal(@"\\?\SWD#MMDEVAPI#" + render + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}", AudioParsing.RenderInterfaceId(render));
    }

    [Fact]
    public void Device_targets_accept_render_and_capture_ids_but_levels_only_for_capture()
    {
        const string capture = "{0.0.1.00000000}.{8baa6d9f-01c8-4bfe-b9e5-758c42eb3681}";
        const string render = "{0.0.0.00000000}.{13115c35-2308-4ab1-8fa0-2a1a69dff0a8}";
        Assert.True(DeviceTargets.IsEndpointId(capture));
        Assert.True(DeviceTargets.IsEndpointId(render));
        Assert.True(DeviceTargets.IsCaptureEndpointId(capture));
        Assert.False(DeviceTargets.IsCaptureEndpointId(render));
        Assert.False(DeviceTargets.IsEndpointId("{0.0.2.00000000}.{8baa6d9f-01c8-4bfe-b9e5-758c42eb3681}"));
    }

    [Theory]
    [InlineData(true, 1, true)]       // Audio enhancements: Off
    [InlineData(true, 0, false)]      // On (Device Default Effects)
    [InlineData(true, null, false)]   // value absent → On
    [InlineData(false, null, null)]   // no effects store: unknown
    public void Fx_properties_value_means_enhancements_off(bool store, int? value, bool? off) =>
        Assert.Equal(off, AudioParsing.EnhancementsOff(store, value));

    [Fact]
    public void Fx_properties_of_another_type_are_unknown() => Assert.Null(AudioParsing.EnhancementsOff(true, new byte[] { 1, 0, 0, 0 }));

    [Theory]
    [InlineData("Allow", "Allow", "Allow", null, MicPrivacy.Allow)]
    [InlineData(null, null, null, null, MicPrivacy.Allow)]          // nothing set: Windows allows
    [InlineData("Deny", "Allow", "Allow", null, MicPrivacy.Deny)]   // the user's switch
    [InlineData("Allow", "Deny", "Allow", null, MicPrivacy.Deny)]   // desktop apps
    [InlineData("Allow", "Allow", "Deny", null, MicPrivacy.Deny)]   // the whole machine
    [InlineData("Deny", "Deny", "Deny", 1, MicPrivacy.Allow)]       // the policy wins: force allow
    [InlineData("Allow", "Allow", "Allow", 2, MicPrivacy.Deny)]     // the policy wins: force deny
    [InlineData("Allow", "Allow", "Allow", 0, MicPrivacy.Allow)]    // user in control
    public void Privacy_any_deny_denies_and_the_policy_wins(string? user, string? nonPackaged, string? machine, int? policy, MicPrivacy expected) =>
        Assert.Equal(expected, AudioParsing.CombinePrivacy(user, nonPackaged, machine, policy));

    [Fact]
    public void Spatial_default_format_off_sonic_or_other()
    {
        const string sonic = "{b1f1e7a6-a1b7-4a65-9c4b-91b1c1e1a1e1}";
        Assert.Equal(SpatialState.Off, AudioParsing.Spatial("{00000000-0000-0000-0000-000000000000}", sonic));
        Assert.Equal(SpatialState.Off, AudioParsing.Spatial("", sonic));
        Assert.Equal(SpatialState.WindowsSonic, AudioParsing.Spatial(sonic.ToUpperInvariant(), sonic));
        Assert.Equal(SpatialState.Other, AudioParsing.Spatial("{11111111-2222-3333-4444-555555555555}", sonic));
    }

    [Fact]
    public void Texts_use_russian_units_and_the_typographic_minus()
    {
        Assert.Equal("+5,6 дБ", AudioParsing.DbText(5.6));
        Assert.Equal("\u221216 дБ", AudioParsing.DbText(-16));
        Assert.Equal("0 дБ", AudioParsing.DbText(-0.01));
        Assert.Equal("\u221216…+15 дБ", AudioParsing.RangeText(-16, 15));
        Assert.Equal("7.1", AudioParsing.ChannelsText(8));
        Assert.Equal("X27U W3", AudioParsing.DisplayName("X27U W3 (NVIDIA High Definition Audio)"));
        Assert.Equal("Odyssey G5", AudioParsing.DisplayName("Odyssey G5"));
    }

    [Fact]
    public void Connection_never_shows_the_instance_tail()
    {
        static EndpointInfo E(string? instance, int ff = 3) => new()
        {
            Id = "{0.0.0.00000000}.{13115c35-2308-4ab1-8fa0-2a1a69dff0a8}", Flow = AudioFlow.Render, Name = "x", FormFactor = ff, InstanceId = instance,
        };
        Assert.Equal("USB", AudioParsing.ConnectionText(E(@"USB\VID_054C&PID_0F80&MI_00\7&SERIAL")));
        Assert.Equal("HDMI/DisplayPort", AudioParsing.ConnectionText(E(@"HDAUDIO\FUNC_01&VEN_10DE", 9)));
        Assert.Equal("встроенный звук", AudioParsing.ConnectionText(E(@"HDAUDIO\FUNC_01&VEN_10EC", 1)));
        Assert.Equal("Bluetooth", AudioParsing.ConnectionText(E(@"BTHHFENUM\BthHFPAudio\8&1")));
        Assert.Null(AudioParsing.ConnectionText(E(null)));
        Assert.True(E(@"BTHHFENUM\BthHFPAudio\8&1").IsBluetoothHandsFree);
    }
}
