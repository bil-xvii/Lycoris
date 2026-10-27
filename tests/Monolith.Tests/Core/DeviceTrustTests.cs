using Microsoft.Win32;
using Monolith.Core;
using Monolith.Core.Devices;
using static Monolith.Tests.Core.DeviceTestData;

namespace Monolith.Tests.Core;

/// <summary>
/// The journal is untrusted input: device records are accepted only with exactly the shape the page's own code writes
/// (source, kind, target, validated payload), and the device kinds only from the page.
/// </summary>
public sealed class DeviceTrustTests
{
    private static readonly Guid Adapter = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    public static ChangeRecord Spi(int value = 14, RegistryValueSnapshot? persisted = null, string id = "mouse-speed", string? sid = Sid) => new()
    {
        Source = DeviceTargets.Source, Title = "Скорость указателя", Kind = ChangeKind.UserParameter, Target = DeviceTargets.Spi(id), UserSid = sid,
        Spi = new SpiSnapshot
        {
            Id = id, Value = value,
            Persisted = persisted ?? new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = @"Control Panel\Mouse", Name = "MouseSensitivity", KeyExisted = true },
        },
    };

    public static ChangeRecord Mode(string path = MonitorPath, DisplayModeSpec? original = null) => new()
    {
        Source = DeviceTargets.Source, Title = "Режим", Kind = ChangeKind.DisplayMode, Target = DeviceTargets.DisplayMode(path),
        DisplayMode = new DisplayModeSnapshot
        {
            MonitorPath = path, MonitorName = "X27U W3", Original = original ?? DeviceTestData.Mode(2560, 1440, 240, 239970, 1000),
            Target = DeviceTestData.Mode(2560, 1440, 144), PendingConfirm = true,
        },
    };

    public static ChangeRecord Hdr() => new()
    {
        Source = DeviceTargets.Source, Title = "HDR", Kind = ChangeKind.DisplayHdr, Target = DeviceTargets.DisplayHdr(MonitorPath),
        DisplayHdr = new DisplayHdrSnapshot
        {
            MonitorPath = MonitorPath, MonitorName = "X27U W3", OriginalEnabled = false, TargetEnabled = true,
            ModeBefore = DeviceTestData.Mode(2560, 1440, 240), AcmBefore = false,
        },
    };

    public static ChangeRecord Ddc(MonitorVcp code = MonitorVcp.Contrast, uint value = 50, uint max = 100) => new()
    {
        Source = DeviceTargets.Source, Title = "Контраст", Kind = ChangeKind.MonitorSetting, Target = DeviceTargets.Monitor(MonitorPath, code),
        Monitor = new MonitorVcpSnapshot { MonitorPath = MonitorPath, MonitorName = "X27U W3", Code = code, Value = value, Max = max },
    };

    public static ChangeRecord Audio(string id = MicId, float scalar = 0.8f, AudioComponent part = AudioComponent.Mute) => new()
    {
        Source = DeviceTargets.Source, Title = "Микрофон", Kind = ChangeKind.AudioLevel, Target = DeviceTargets.Audio(id, part),
        Audio = new AudioLevelSnapshot { EndpointId = id, EndpointName = "Razer", Component = part, ScalarBits = BitConverter.SingleToUInt32Bits(scalar) },
    };

    public static ChangeRecord Nic(string keyword = "*SpeedDuplex", string? value = "2", string? def = "0") => new()
    {
        Source = DeviceTargets.Source, Title = "Скорость и дуплекс", Kind = ChangeKind.NicProperty, Target = DeviceTargets.NicProperty(Adapter, keyword),
        Nic = new NicPropertySnapshot { InterfaceGuid = Adapter, AdapterName = "Ethernet", Keyword = keyword, Value = value, DefaultValue = def },
    };

    public static IReadOnlyList<ChangeRecord> GenuineRecords(Guid batch) =>
        new[] { Spi(), Mode(), Hdr(), Ddc(), Audio(), Nic() }.Select(r => With(r, batch: batch)).ToList();

    /// <summary>A copy with another source / target / account (the record's other fields stay).</summary>
    private static ChangeRecord With(ChangeRecord r, string? source = null, string? target = null, string? sid = "keep", Guid? batch = null, ChangeKind? kind = null) => new()
    {
        BatchId = batch ?? r.BatchId, Source = source ?? r.Source, Title = r.Title, Kind = kind ?? r.Kind, Target = target ?? r.Target,
        UserSid = sid == "keep" ? r.UserSid : sid, OldValue = r.OldValue, NewValue = r.NewValue,
        Spi = r.Spi, DisplayMode = r.DisplayMode, DisplayHdr = r.DisplayHdr, Monitor = r.Monitor, Audio = r.Audio, Nic = r.Nic,
    };

    [Fact]
    public void Genuine_records_of_every_kind_are_accepted()
    {
        foreach (var r in GenuineRecords(Guid.NewGuid())) Assert.Null(JournalTrust.Check(r));
        Assert.Null(JournalTrust.Check(Audio(part: AudioComponent.Level)));
        Assert.Null(JournalTrust.Check(Spi(-1, id: "wheel-lines",
            persisted: new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = @"Control Panel\Desktop", Name = "WheelScrollLines", KeyExisted = true, ValueExisted = true, Kind = RegistryValueKind.String, Data = "-1" })));
        Assert.Null(JournalTrust.Check(new ChangeRecord
        {
            Source = DeviceTargets.Source, Kind = ChangeKind.UserParameter, Target = DeviceTargets.Spi("mouse-sonar"), UserSid = Sid,
            Spi = new SpiSnapshot { Id = "mouse-sonar", Value = 0 },
        }));
        // GDI «hardware default» (0 Hz) is a valid original.
        Assert.Null(JournalTrust.Check(Mode(original: DeviceTestData.Mode(1920, 1080, 0))));
    }

    [Fact]
    public void Spi_records_outside_the_catalog_are_refused()
    {
        Assert.NotNull(JournalTrust.Check(Spi(id: "no-such-parameter")));
        Assert.NotNull(JournalTrust.Check(Spi(value: 21)));
        Assert.NotNull(JournalTrust.Check(Spi(sid: null)));
        Assert.NotNull(JournalTrust.Check(Spi(persisted: new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = @"Control Panel\Desktop", Name = "MouseSensitivity" })));
        Assert.NotNull(JournalTrust.Check(Spi(persisted: new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = @"Control Panel\Mouse", Name = "Other" })));
        Assert.NotNull(JournalTrust.Check(Spi(persisted: new RegistryValueSnapshot { Hive = RegHive.HKLM, Path = @"Control Panel\Mouse", Name = "MouseSensitivity" })));
        Assert.NotNull(JournalTrust.Check(Spi(persisted: new RegistryValueSnapshot
        {
            Hive = RegHive.HKCU, Path = @"Control Panel\Mouse", Name = "MouseSensitivity", ValueExisted = true, Kind = RegistryValueKind.Binary, Data = "00",
        })));
        Assert.NotNull(JournalTrust.Check(Spi(persisted: new RegistryValueSnapshot
        {
            Hive = RegHive.HKCU, Path = @"Control Panel\Mouse", Name = "MouseSensitivity", CreatedKeyRoot = @"Control Panel\Mouse",
        })));
        // Sonar has no persisted value: one in the record would restore the whole UserPreferencesMask.
        var sonar = Spi(1, id: "mouse-sonar");
        Assert.NotNull(JournalTrust.Check(sonar));
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY1")]
    [InlineData("\\\\?\\DISPLAY#AOC2701#5&2a\"1#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")]
    [InlineData(@"\\?\DISPLAY#AOC2701#5&2a1b3c4d&0&UID4352#{00000000-ee97-4a90-b076-33f57bf4eaa7}")]
    public void Display_records_with_a_bad_monitor_path_are_refused(string path) => Assert.NotNull(JournalTrust.Check(Mode(path)));

    [Fact]
    public void Display_records_must_be_sane_and_machine_wide()
    {
        Assert.NotNull(JournalTrust.Check(With(Mode(), sid: Sid)));
        Assert.NotNull(JournalTrust.Check(Mode(original: DeviceTestData.Mode(100, 1440, 240))));
        Assert.NotNull(JournalTrust.Check(Mode(original: DeviceTestData.Mode(2560, 1440, 5000))));
        Assert.NotNull(JournalTrust.Check(Mode(original: new DisplayModeSpec(2560, 1440, 240, 239970, 0, 32, 0, 0, 0))));
        Assert.NotNull(JournalTrust.Check(Mode(original: new DisplayModeSpec(2560, 1440, 240, 0, 0, 12, 0, 0, 0))));
        Assert.NotNull(JournalTrust.Check(Mode(original: new DisplayModeSpec(2560, 1440, 240, 0, 0, 32, 4, 0, 0))));
        Assert.NotNull(JournalTrust.Check(Mode(original: new DisplayModeSpec(2560, 1440, 240, 0, 0, 32, 0, 70000, 0))));
    }

    [Fact]
    public void Ddc_records_only_brightness_and_contrast_within_range()
    {
        Assert.NotNull(JournalTrust.Check(Ddc((MonitorVcp)0x60)));
        Assert.NotNull(JournalTrust.Check(Ddc((MonitorVcp)0xD6)));
        Assert.NotNull(JournalTrust.Check(Ddc(value: 101, max: 100)));
        Assert.NotNull(JournalTrust.Check(Ddc(max: 0, value: 0)));
    }

    [Fact]
    public void Audio_records_only_capture_endpoints_with_a_valid_level()
    {
        Assert.NotNull(JournalTrust.Check(Audio(scalar: float.NaN)));
        Assert.NotNull(JournalTrust.Check(Audio(scalar: 1.5f)));
        Assert.NotNull(JournalTrust.Check(Audio(id: "{0.0.1.00000000}.{not-a-guid}")));
        Assert.NotNull(JournalTrust.Check(Audio(id: SpeakerId))); // never «restore» a headset to full volume
    }

    [Fact]
    public void Nic_records_only_whitelisted_numeric_keywords()
    {
        Assert.Null(JournalTrust.Check(Nic("EEE", "1", null)));
        Assert.Null(JournalTrust.Check(Nic("AdvancedEEE", null, "1")));
        Assert.NotNull(JournalTrust.Check(Nic("*InterruptModeration", "1")));
        Assert.NotNull(JournalTrust.Check(Nic("*SpeedDuplex", "auto")));
        Assert.NotNull(JournalTrust.Check(Nic("*SpeedDuplex", null, null)));
        Assert.NotNull(JournalTrust.Check(With(Nic(), sid: Sid)));
    }

    [Fact]
    public void Device_kinds_come_only_from_the_page_and_the_page_writes_only_device_kinds()
    {
        Assert.NotNull(JournalTrust.Check(With(Spi(), source: "tweak:mouse-acceleration-off")));
        Assert.NotNull(JournalTrust.Check(With(Mode(), source: "services")));
        foreach (var kind in new[] { ChangeKind.Service, ChangeKind.Command, ChangeKind.PowerScheme, ChangeKind.Registry, ChangeKind.ScheduledTask })
            Assert.NotNull(JournalTrust.Check(new ChangeRecord { Source = DeviceTargets.Source, Kind = kind, Target = "x" }));
        // The ducking preference is the Optimize tweak «audio-ducking», not a page write.
        Assert.NotNull(JournalTrust.Check(new ChangeRecord
        {
            Source = DeviceTargets.Source, Kind = ChangeKind.Registry, Target = $@"reg:HKCU\{DeviceTargets.DuckingPath}\{DeviceTargets.DuckingName}", UserSid = Sid,
            Registry = new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = DeviceTargets.DuckingPath, Name = DeviceTargets.DuckingName },
        }));
    }

    [Fact]
    public void A_target_that_does_not_match_the_payload_is_refused_for_every_kind()
    {
        foreach (var r in GenuineRecords(Guid.Empty))
            Assert.NotNull(JournalTrust.Check(With(r, target: r.Target + "x")));
        Assert.NotNull(JournalTrust.Check(With(Audio(part: AudioComponent.Mute), target: DeviceTargets.AudioLevel(MicId))));
    }
}
