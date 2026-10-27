using System.IO;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Core.Devices;
using static Monolith.Tests.Core.DeviceTestData;

namespace Monolith.Tests.Core;

/// <summary>
/// The device primitives of ChangeSession against fakes and a redirected registry (HKCU\Software\MONOLITH-Tests\…):
/// record before the change, read-back, rollback on failure, exact undo through the real WindowsJournalSystem.
/// </summary>
[Collection("Registry")]
public sealed class DeviceJournalTests : IDisposable
{
    private readonly string _root = $@"Software\MONOLITH-Tests\Redirect-{Guid.NewGuid():N}";
    private readonly FakeSpiWriter _spi = new();
    private readonly FakeMonitorWriter _monitor = new();
    private readonly FakeAudioWriter _audio = new();
    private readonly FakeNicWriter _nic = new();
    private readonly IDisposable _writers;
    private readonly ChangeJournal _journal = ChangeJournal.ForTests();
    private bool? _interactive = true;

    private static readonly SpiEntry Speed = SpiCatalog.Find("mouse-speed")!;
    private static readonly SpiPersisted SpeedReg = Speed.Persisted!;

    public DeviceJournalTests()
    {
        RegistryHelper.Redirect = (hive, path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");
        ChangeSession.BypassElevationForTests = true;
        UserScope.CurrentSidForTests = () => Sid;
        Elevation.InteractiveUserForTests = () => _interactive;
        _writers = DeviceWriters.OverrideForTests(_spi, monitor: _monitor, audio: _audio, nic: _nic);
        DeviceConfirm.Countdown = null;
    }

    public void Dispose()
    {
        _writers.Dispose();
        DeviceConfirm.Countdown = null;
        Elevation.InteractiveUserForTests = null;
        UserScope.CurrentSidForTests = null;
        RegistryHelper.Redirect = null;
        ChangeSession.BypassElevationForTests = false;
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, false); } catch { }
    }

    private Task<ChangeSession> Begin(string title = "test") => ChangeSession.BeginAsync(title, createRestorePoint: false, journal: _journal);

    private static RegistryValueSnapshot Persisted() => RegistryHelper.Snapshot(RegHive.HKCU, SpeedReg.Path, SpeedReg.Name);

    private ChangeRecord Single(string target) => Assert.Single(_journal.ActiveRecords, r => r.Target == target);

    // ───────────────────────────── SPI ─────────────────────────────

    [Fact]
    public async Task SetUserParameter_records_original_and_absent_persisted_value()
    {
        _spi.Live["mouse-speed"] = 14;
        var s = await Begin();

        Assert.True(s.SetUserParameter("mouse-speed", 10, DeviceTargets.Source, "Скорость указателя"));

        var r = Single(DeviceTargets.Spi("mouse-speed"));
        Assert.Equal(ChangeKind.UserParameter, r.Kind);
        Assert.Equal(14, r.Spi!.Value);
        Assert.False(r.Spi.Persisted!.ValueExisted);
        Assert.Equal(Sid, r.UserSid);
        Assert.Equal("14", r.OldValue);
        Assert.Equal("10", r.NewValue);
        Assert.Equal(10, _spi.Live["mouse-speed"]);
        Assert.Equal("10", Persisted().Data);
        Assert.Equal(1, s.Applied);
    }

    [Fact]
    public async Task SetUserParameter_records_existing_persisted_value()
    {
        _spi.Live["mouse-speed"] = 14;
        RegistryHelper.Write(RegHive.HKCU, SpeedReg.Path, SpeedReg.Name, RegistryValueKind.String, "14");
        var s = await Begin();

        Assert.True(s.SetUserParameter("mouse-speed", 10, DeviceTargets.Source, "Скорость указателя"));

        var p = Single(DeviceTargets.Spi("mouse-speed")).Spi!.Persisted!;
        Assert.True(p.ValueExisted);
        Assert.Equal(RegistryValueKind.String, p.Kind);
        Assert.Equal("14", p.Data);
    }

    [Fact]
    public async Task Equal_value_creates_no_record()
    {
        _spi.Live["mouse-speed"] = 10;
        var s = await Begin();
        Assert.True(s.SetUserParameter("mouse-speed", 10, DeviceTargets.Source, "Скорость указателя"));
        Assert.Empty(_journal.ActiveRecords);
        Assert.Equal(0, _spi.SetCalls);
    }

    [Theory]
    [InlineData("mouse-speed", 21)]
    [InlineData("mouse-speed", 0)]
    [InlineData("wheel-routing", 1)] // the undocumented hybrid mode: never set
    [InlineData("mouse-trails", 50)]
    [InlineData("no-such-parameter", 1)]
    public async Task Value_outside_the_allowed_targets_is_refused(string id, int value)
    {
        _spi.Live[id] = 0;
        var s = await Begin();
        Assert.False(s.SetUserParameter(id, value, DeviceTargets.Source, "x"));
        Assert.Empty(_journal.ActiveRecords);
        Assert.Single(s.Errors);
        Assert.Equal(0, _spi.SetCalls);
    }

    [Fact]
    public async Task Read_back_mismatch_restores_original_deletes_created_value_and_removes_record()
    {
        _spi.Live["mouse-speed"] = 14;
        _spi.IgnoreSets = true; // the registry gets the new value, the live value does not change
        var s = await Begin();

        Assert.False(s.SetUserParameter("mouse-speed", 10, DeviceTargets.Source, "Скорость указателя"));

        Assert.Empty(_journal.Records);
        Assert.False(Persisted().ValueExisted);
        Assert.Equal(14, _spi.Live["mouse-speed"]);
        Assert.Single(s.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Not_the_desktop_user_is_refused_and_so_is_the_revert(bool? state)
    {
        _spi.Live["mouse-speed"] = 14;
        var s = await Begin();
        Assert.True(s.SetUserParameter("mouse-speed", 10, DeviceTargets.Source, "Скорость указателя"));

        _interactive = state;
        var s2 = await Begin();
        Assert.False(s2.SetUserParameter("mouse-speed", 6, DeviceTargets.Source, "Скорость указателя"));
        Assert.Contains(s2.Errors, e => e.Contains(state == false ? "другой учётной записи" : "Не удалось определить"));

        var result = await _journal.RevertAsync(_journal.ActiveRecords);
        Assert.False(result.Success);
        Assert.Equal(10, _spi.Live["mouse-speed"]);
        Assert.Single(_journal.ActiveRecords);
    }

    [Fact]
    public async Task Revert_puts_live_value_back_and_deletes_the_persisted_value_it_created()
    {
        _spi.Live["mouse-speed"] = 14;
        var s = await Begin();
        s.SetUserParameter("mouse-speed", 10, DeviceTargets.Source, "Скорость указателя");

        var result = await _journal.RevertAsync(_journal.ActiveRecords);

        Assert.True(result.Success);
        Assert.Equal(14, _spi.Live["mouse-speed"]);
        Assert.False(Persisted().ValueExisted);
    }

    [Theory]
    [InlineData(RegistryValueKind.String, "14")]
    [InlineData(RegistryValueKind.DWord, "14")] // left by a third-party tool: comes back as a DWORD
    public async Task Revert_restores_the_persisted_value_byte_exact(RegistryValueKind kind, string data)
    {
        _spi.Live["mouse-speed"] = 14;
        RegistryHelper.Write(RegHive.HKCU, SpeedReg.Path, SpeedReg.Name, kind, kind == RegistryValueKind.DWord ? 14 : data);
        var before = Persisted();
        var s = await Begin();
        Assert.True(s.SetUserParameter("mouse-speed", 10, DeviceTargets.Source, "Скорость указателя"));

        Assert.True((await _journal.RevertAsync(_journal.ActiveRecords)).Success);

        var after = Persisted();
        Assert.Equal(before.Kind, after.Kind);
        Assert.Equal(before.Data, after.Data);
    }

    [Fact]
    public async Task Two_layers_hand_over_and_end_at_the_original()
    {
        _spi.Live["mouse-speed"] = 14;
        var s1 = await Begin("one");
        s1.SetUserParameter("mouse-speed", 10, DeviceTargets.Source, "Скорость указателя");
        var s2 = await Begin("two");
        s2.SetUserParameter("mouse-speed", 6, DeviceTargets.Source, "Скорость указателя");
        var layers = _journal.ActiveLayers(DeviceTargets.Spi("mouse-speed"));
        Assert.Equal(2, layers.Count);

        Assert.True((await _journal.RevertAsync([layers[0]])).Success);
        Assert.Equal(6, _spi.Live["mouse-speed"]); // the newer change keeps working
        Assert.Equal(14, layers[1].Spi!.Value);    // and took over the original

        Assert.True((await _journal.RevertAsync([layers[1]])).Success);
        Assert.Equal(14, _spi.Live["mouse-speed"]);
        Assert.False(Persisted().ValueExisted);
    }

    [Fact]
    public async Task Sonar_never_touches_UserPreferencesMask()
    {
        _spi.Live["mouse-sonar"] = 0;
        var s = await Begin();
        Assert.True(s.SetUserParameter("mouse-sonar", 1, DeviceTargets.Source, "Показывать указатель по Ctrl"));
        Assert.Null(Single(DeviceTargets.Spi("mouse-sonar")).Spi!.Persisted);

        Assert.True((await _journal.RevertAsync(_journal.ActiveRecords)).Success);
        Assert.Equal(0, _spi.Live["mouse-sonar"]);
        Assert.False(RegistryHelper.Snapshot(RegHive.HKCU, @"Control Panel\Desktop", "UserPreferencesMask").ValueExisted);
    }

    [Fact]
    public async Task Confirmed_parameter_declined_reverts_its_own_record_in_the_session_journal()
    {
        _spi.Live["swap-buttons"] = 0;
        DeviceConfirm.Countdown = (_, _) => Task.FromResult(false);
        var s = await Begin();
        var request = new CountdownRequest("ОСТАВИТЬ КНОПКИ?", "", 15, CountdownRequest.Keep, CountdownRequest.Revert, null) { Window = TimeSpan.FromSeconds(1) };

        var outcome = await s.SetUserParameterConfirmedAsync("swap-buttons", 1, request, DeviceTargets.Source, "Кнопки мыши");

        Assert.Equal(DisplayApplyResult.Reverted, outcome.Result);
        Assert.Equal(0, _spi.Live["swap-buttons"]);
        Assert.Empty(_journal.ActiveRecords);
        Assert.Single(_journal.Records, r => r.Reverted);
        Assert.Equal(0, s.Applied);
    }

    [Fact]
    public async Task Confirmed_parameter_kept()
    {
        _spi.Live["swap-buttons"] = 0;
        DeviceConfirm.Countdown = (_, _) => Task.FromResult(true);
        var s = await Begin();
        var request = new CountdownRequest("ОСТАВИТЬ КНОПКИ?", "", 15, CountdownRequest.Keep, CountdownRequest.Revert, null);

        var outcome = await s.SetUserParameterConfirmedAsync("swap-buttons", 1, request, DeviceTargets.Source, "Кнопки мыши");

        Assert.Equal(DisplayApplyResult.Kept, outcome.Result);
        Assert.Equal(1, _spi.Live["swap-buttons"]);
        Assert.Single(_journal.ActiveRecords);
    }

    // ───────────────────────────── DDC/CI ─────────────────────────────

    [Fact]
    public async Task Monitor_value_above_max_is_refused()
    {
        _monitor.Values[(MonitorPath, MonitorVcp.Brightness)] = new(0, 50, 100);
        var s = await Begin();
        var outcome = await s.SetMonitorSettingAsync(MonitorPath, "X27U", MonitorVcp.Brightness, 101, DeviceTargets.Source, "Яркость");
        Assert.Equal(DisplayApplyResult.Failed, outcome.Result);
        Assert.Empty(_journal.Records);
        Assert.Equal(0, _monitor.SetCalls);
    }

    [Fact]
    public async Task Monitor_read_back_mismatch_rolls_back()
    {
        _monitor.Values[(MonitorPath, MonitorVcp.Brightness)] = new(0, 50, 100);
        _monitor.IgnoreSets = true;
        var s = await Begin();
        var outcome = await s.SetMonitorSettingAsync(MonitorPath, "X27U", MonitorVcp.Brightness, 70, DeviceTargets.Source, "Яркость");
        Assert.Equal(DisplayApplyResult.Failed, outcome.Result);
        Assert.Empty(_journal.Records);
        Assert.Equal(50u, _monitor.Values[(MonitorPath, MonitorVcp.Brightness)].Value);
    }

    [Fact]
    public async Task Monitor_apply_and_revert()
    {
        _monitor.Values[(MonitorPath, MonitorVcp.Contrast)] = new(0, 50, 100);
        var s = await Begin();
        var outcome = await s.SetMonitorSettingAsync(MonitorPath, "X27U", MonitorVcp.Contrast, 70, DeviceTargets.Source, "Контраст");
        Assert.Equal(DisplayApplyResult.Kept, outcome.Result);
        Assert.Equal(70u, _monitor.Values[(MonitorPath, MonitorVcp.Contrast)].Value);

        Assert.True((await _journal.RevertAsync(_journal.ActiveRecords)).Success);
        Assert.Equal(50u, _monitor.Values[(MonitorPath, MonitorVcp.Contrast)].Value);
    }

    [Theory]
    [InlineData(false, DisplayApplyResult.Reverted, 50u)]
    [InlineData(true, DisplayApplyResult.Kept, 5u)]
    public async Task Very_low_brightness_needs_the_countdown(bool keep, DisplayApplyResult expected, uint live)
    {
        _monitor.Values[(MonitorPath, MonitorVcp.Brightness)] = new(0, 50, 100);
        CountdownRequest? asked = null;
        DeviceConfirm.Countdown = (r, _) =>
        {
            asked = r;
            return Task.FromResult(keep);
        };
        var s = await Begin();
        var outcome = await s.SetMonitorSettingAsync(MonitorPath, "X27U", MonitorVcp.Brightness, 5, DeviceTargets.Source, "Яркость");

        Assert.Equal(expected, outcome.Result);
        Assert.NotNull(asked);
        Assert.Equal(live, _monitor.Values[(MonitorPath, MonitorVcp.Brightness)].Value);
        Assert.Equal(keep ? 1 : 0, _journal.ActiveRecords.Count);
    }

    // ───────────────────────────── Endpoint volume ─────────────────────────────

    [Fact]
    public async Task Audio_scalar_bits_round_trip_exactly()
    {
        const float original = 0.8234567f;
        _audio.Endpoints[MicId] = (original, false);
        var s = await Begin();
        Assert.True(s.SetAudioLevel(MicId, "Razer", 0.5f, null, null, DeviceTargets.Source, "Уровень микрофона"));
        var r = Single(DeviceTargets.AudioLevel(MicId));
        Assert.Equal(BitConverter.SingleToUInt32Bits(original), r.Audio!.ScalarBits);
        Assert.Equal("уровень 50 %", r.NewValue);

        Assert.True((await _journal.RevertAsync(_journal.ActiveRecords)).Success);
        Assert.Equal(BitConverter.SingleToUInt32Bits(original), BitConverter.SingleToUInt32Bits(_audio.Endpoints[MicId].Scalar));
    }

    [Fact]
    public async Task Audio_read_back_mismatch_rolls_back()
    {
        _audio.Endpoints[MicId] = (0.8f, false);
        _audio.IgnoreSets = true;
        var s = await Begin();
        Assert.False(s.SetAudioLevel(MicId, "Razer", 0.5f, null, null, DeviceTargets.Source, "Уровень микрофона"));
        Assert.Empty(_journal.Records);
    }

    [Fact]
    public async Task Level_revert_keeps_a_mute_set_later()
    {
        _audio.Endpoints[MicId] = (0.8f, false);
        var s = await Begin();
        s.SetAudioLevel(MicId, "Razer", 0.5f, null, null, DeviceTargets.Source, "Уровень микрофона");
        _audio.Endpoints[MicId] = (0.5f, true); // muted with the hardware key afterwards

        Assert.True((await _journal.RevertAsync(_journal.ActiveRecords)).Success);

        Assert.Equal(0.8f, _audio.Endpoints[MicId].Scalar);
        Assert.True(_audio.Endpoints[MicId].Mute);
    }

    [Fact]
    public async Task Level_and_mute_are_separate_records()
    {
        _audio.Endpoints[MicId] = (0.8f, false);
        var s = await Begin();
        Assert.True(s.SetAudioLevel(MicId, "Razer", 0.5f, null, true, DeviceTargets.Source, "Микрофон"));
        Assert.Single(_journal.ActiveRecords, r => r.Target == DeviceTargets.AudioLevel(MicId) && r.Audio!.Component == AudioComponent.Level);
        Assert.Single(_journal.ActiveRecords, r => r.Target == DeviceTargets.AudioMute(MicId) && r.Audio!.Component == AudioComponent.Mute);
        _audio.Endpoints[MicId] = (0.3f, true); // level adjusted since

        Assert.True((await _journal.RevertAsync([Single(DeviceTargets.AudioMute(MicId))])).Success);
        Assert.Equal((0.3f, false), _audio.Endpoints[MicId]);
    }

    [Fact]
    public async Task Render_endpoints_and_other_accounts_are_refused()
    {
        _audio.Endpoints[SpeakerId] = (0.5f, false);
        _audio.Endpoints[MicId] = (0.5f, false);
        var s = await Begin();
        Assert.False(s.SetAudioLevel(SpeakerId, "Наушники", 1f, null, null, DeviceTargets.Source, "Громкость"));
        _interactive = false;
        Assert.False(s.SetAudioLevel(MicId, "Razer", 0.9f, null, null, DeviceTargets.Source, "Уровень микрофона"));
        Assert.Empty(_journal.Records);
        Assert.Equal(0, _audio.SetCalls);
    }

    [Fact]
    public async Task Audio_undo_of_a_vanished_endpoint_is_skipped_not_failed()
    {
        _audio.Endpoints[MicId] = (0.8f, false);
        var s = await Begin();
        s.SetAudioLevel(MicId, "Razer", 0.5f, null, null, DeviceTargets.Source, "Уровень микрофона");
        _audio.Endpoints.Remove(MicId);

        var result = await _journal.RevertAsync(_journal.ActiveRecords);
        Assert.True(result.Success);
        Assert.Empty(_journal.ActiveRecords);
    }

    // ───────────────────────────── Network adapter ─────────────────────────────

    private void Nic(string keyword, string? value, string? def, params string[] valid) =>
        _nic.Props[keyword] = new NicPropertyInfo(keyword, value, valid, def);

    [Fact]
    public async Task Nic_property_apply_and_revert()
    {
        Nic("*SpeedDuplex", "2", "0", "0", "1", "2", "3", "4", "6");
        var s = await Begin();
        Assert.True(s.SetNicAdvancedProperty(_nic.Adapter, "*SpeedDuplex", "0", DeviceTargets.Source, "Скорость и дуплекс"));
        var r = Single(DeviceTargets.NicProperty(_nic.Adapter, "*SpeedDuplex"));
        Assert.Contains("1–6 с", r.Title);
        Assert.Equal("2", r.Nic!.Value);

        Assert.True((await _journal.RevertAsync(_journal.ActiveRecords)).Success);
        Assert.Equal("2", _nic.Props["*SpeedDuplex"].Value);
    }

    [Fact]
    public async Task Nic_absent_value_is_restored_as_the_driver_default()
    {
        Nic("*EEE", null, "1", "0", "1");
        var s = await Begin();
        Assert.True(s.SetNicAdvancedProperty(_nic.Adapter, "*EEE", "0", DeviceTargets.Source, "EEE"));
        Assert.True((await _journal.RevertAsync(_journal.ActiveRecords)).Success);
        Assert.Equal("1", _nic.Props["*EEE"].Value);
    }

    [Theory]
    [InlineData("*SpeedDuplex", "5")]          // not in ValidRegistryValues
    [InlineData("*InterruptModeration", "0")]  // not whitelisted (DO-NOT-INCLUDE)
    [InlineData("*JumboPacket", "9014")]
    [InlineData("*SpeedDuplex", "auto")]       // not numeric
    public async Task Nic_value_or_keyword_outside_the_rules_is_refused(string keyword, string value)
    {
        Nic(keyword, "0", "0", "0", "1", "2", "3", "4", "6", "9014");
        var s = await Begin();
        Assert.False(s.SetNicAdvancedProperty(_nic.Adapter, keyword, value, DeviceTargets.Source, "x"));
        Assert.Empty(_journal.Records);
        Assert.Equal(0, _nic.SetCalls);
    }

    [Fact]
    public async Task Nic_change_and_undo_wait_while_a_game_runs()
    {
        Nic("*SpeedDuplex", "2", "0", "0", "1", "2", "3", "4", "6");
        var s = await Begin();
        _nic.Game = "Counter-Strike 2";
        Assert.False(s.SetNicAdvancedProperty(_nic.Adapter, "*SpeedDuplex", "0", DeviceTargets.Source, "Скорость и дуплекс"));
        Assert.Empty(_journal.Records);
        Assert.Equal(0, _nic.SetCalls);

        _nic.Game = null;
        Assert.True(s.SetNicAdvancedProperty(_nic.Adapter, "*SpeedDuplex", "0", DeviceTargets.Source, "Скорость и дуплекс"));
        _nic.Game = "Counter-Strike 2"; // per-row ОТКАТИТЬ during a library game (no Game Boost session)
        var refused = await _journal.RevertAsync(_journal.ActiveRecords);
        Assert.False(refused.Success);
        Assert.Contains(refused.Errors, e => e.Contains("Counter-Strike 2"));
        Assert.Equal("0", _nic.Props["*SpeedDuplex"].Value);
        Assert.Single(_journal.ActiveRecords);

        _nic.Game = null;
        Assert.True((await _journal.RevertAsync(_journal.ActiveRecords)).Success);
        Assert.Equal("2", _nic.Props["*SpeedDuplex"].Value);
    }

    [Fact]
    public async Task Nic_undo_after_the_keyword_vanished_counts_as_restored()
    {
        Nic("EEE", "1", "1", "0", "1");
        var s = await Begin();
        s.SetNicAdvancedProperty(_nic.Adapter, "EEE", "0", DeviceTargets.Source, "EEE");
        _nic.Props.Remove("EEE"); // driver changed

        var result = await _journal.RevertAsync(_journal.ActiveRecords);
        Assert.True(result.Success);
        Assert.Contains(result.Notes, n => n.Contains("откатывать нечего"));
    }

    // ───────────────────────────── Safety lock, trust pre-check ─────────────────────────────

    [Fact]
    public async Task Self_test_lock_refuses_every_device_write()
    {
        _spi.Live["mouse-speed"] = 14;
        var s = await Begin();
        ChangeSession.Locked = true;
        try
        {
            Assert.False(s.SetUserParameter("mouse-speed", 10, DeviceTargets.Source, "x"));
            Assert.Throws<InvalidOperationException>(() => DeviceWriters.Spi!.Set(Speed, 10));
        }
        finally
        {
            ChangeSession.Locked = false;
        }
        Assert.Equal(0, _spi.SetCalls);
        Assert.Empty(_journal.Records);
    }

    [Fact]
    public async Task A_record_the_journal_check_would_refuse_is_never_made()
    {
        // An original outside the catalog's valid range could never be put back: refuse before anything changes.
        _spi.Live["mouse-speed"] = 99;
        var s = await Begin();
        Assert.False(s.SetUserParameter("mouse-speed", 10, DeviceTargets.Source, "x"));
        Assert.Equal(0, _spi.SetCalls);
        // A source other than the page: the journal check refuses device kinds from it.
        _spi.Live["mouse-speed"] = 14;
        Assert.False(s.SetUserParameter("mouse-speed", 10, "tweak:mouse-acceleration-off", "x"));
        Assert.Equal(0, _spi.SetCalls);
        Assert.Empty(_journal.Records);
    }

    // ───────────────────────────── Storage ─────────────────────────────

    [Fact]
    public async Task Every_snapshot_type_round_trips_through_the_journal_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"monolith-devices-{Guid.NewGuid():N}.json");
        try
        {
            var journal = ChangeJournal.ForTests(path);
            var batch = journal.BeginBatch("b");
            foreach (var r in DeviceTrustTests.GenuineRecords(batch.Id)) journal.Add(r);

            var loaded = new ChangeJournal(path).Records;
            Assert.Equal(6, loaded.Count);
            foreach (var r in loaded) Assert.Null(JournalTrust.Check(r));
            var mode = loaded.Single(r => r.Kind == ChangeKind.DisplayMode).DisplayMode!;
            Assert.Equal(239.97, mode.Original.ExactHz, 2);
            Assert.True(mode.PendingConfirm);
            Assert.Equal(AudioComponent.Mute, loaded.Single(r => r.Kind == ChangeKind.AudioLevel).Audio!.Component);
            Assert.Equal(MonitorVcp.Contrast, loaded.Single(r => r.Kind == ChangeKind.MonitorSetting).Monitor!.Code);
            Assert.Equal("2", loaded.Single(r => r.Kind == ChangeKind.NicProperty).Nic!.Value);
            Assert.False(loaded.Single(r => r.Kind == ChangeKind.UserParameter).Spi!.Persisted!.ValueExisted);
            Assert.NotNull(loaded.Single(r => r.Kind == ChangeKind.DisplayHdr).DisplayHdr!.ModeBefore);
        }
        finally
        {
            foreach (var f in new[] { path, path + ".bak", path + ".tmp" })
                try { File.Delete(f); } catch { }
        }
    }
}
