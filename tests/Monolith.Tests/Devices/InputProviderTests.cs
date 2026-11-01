using Microsoft.Win32;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Devices;
using Monolith.Modules.Devices.Input;
using Monolith.Tests.Core;
using static Monolith.Tests.Core.DeviceTestData;

namespace Monolith.Tests.Devices;

/// <summary>
/// The mouse and keyboard cards against a fake SystemParametersInfo, fake devices and a redirected registry
/// (HKCU\Software\MONOLITH-Tests\…). Nothing real is read or changed: no SPI setter, no raw input.
/// </summary>
[Collection("Registry")]
public sealed class InputProviderTests : IDisposable
{
    private readonly string _root = $@"Software\MONOLITH-Tests\Redirect-{Guid.NewGuid():N}";
    private readonly FakeSpiWriter _spi = new();
    private readonly IDisposable _writers;
    private readonly DeviceResultStore _results = new();
    private bool? _interactive = true;
    private InputSystemState _system = new(false, false, false, 0, 0, false, 2560);
    private InputInventory _inventory = InputInventory.Empty;

    private static readonly string[] SpiIds =
        ["mouse-speed", "wheel-lines", "wheel-chars", "wheel-routing", "double-click", "swap-buttons", "mouse-trails", "mouse-sonar", "mouse-vanish",
         "keyboard-delay", "keyboard-speed"];

    public InputProviderTests()
    {
        RegistryHelper.Redirect = (hive, path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");
        ChangeSession.BypassElevationForTests = true;
        UserScope.CurrentSidForTests = () => Sid;
        Elevation.InteractiveUserForTests = () => _interactive;
        _writers = DeviceWriters.OverrideForTests(_spi);
        DeviceConfirm.Countdown = null;
        // Windows defaults (research §7).
        foreach (var (id, v) in new[]
                 {
                     ("mouse-speed", 10), ("wheel-lines", 3), ("wheel-chars", 3), ("wheel-routing", 2), ("double-click", 500), ("swap-buttons", 0),
                     ("mouse-trails", 0), ("mouse-sonar", 0), ("mouse-vanish", 1), ("keyboard-delay", 1), ("keyboard-speed", 31),
                 })
            _spi.Live[id] = v;
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

    private DeviceScanContext Ctx() => new() { Profile = new SystemProfile(), Results = _results, InteractiveUser = _interactive };

    private (DeviceCard Mouse, DeviceCard Keyboard) Build()
    {
        var cards = new InputProvider(() => _inventory, () => _system).Build(Ctx());
        return (cards.Single(c => c.Key == "mouse"), cards.Single(c => c.Key == "keyboard"));
    }

    private static DeviceSetting Row(DeviceCard card, string id) => card.Settings.Single(s => s.Id == id);

    // ───────────────────────────── Cards ─────────────────────────────

    [Fact]
    public void Cards_have_the_expected_rows()
    {
        var (mouse, keyboard) = Build();

        Assert.Equal(DeviceKind.Mouse, mouse.Kind);
        Assert.Equal("Мышь", mouse.Title);
        Assert.Equal(10, mouse.Order);
        Assert.Equal(["mouse-speed", "tweak:mouse-acceleration-off", "mouse-dpi", "mouse-polling", "wheel-lines", "wheel-routing", "double-click",
            "swap-buttons", "mouse-trails", "mouse-sonar", "mouse-vanish"], mouse.Settings.Select(s => s.Id));
        Assert.Equal(DeviceKind.Keyboard, keyboard.Kind);
        Assert.Equal("Клавиатура", keyboard.Title);
        Assert.Equal(20, keyboard.Order);
        Assert.Equal(["keyboard-delay", "keyboard-speed", "tweak:accessibility-hotkeys-off"], keyboard.Settings.Select(s => s.Id));

        // One row per measurement: the tool lives in the row, the card has no separate tool rows.
        Assert.Empty(mouse.Tools);
        Assert.Equal(InputProvider.DpiTool, Row(mouse, "mouse-dpi").Tool!.Id);
        Assert.True(Row(mouse, "mouse-dpi").Tool!.NeedsDragPad);
        Assert.Equal(InputProvider.PollingTool, Row(mouse, "mouse-polling").Tool!.Id);
        // No «Windows: …» facts line that would repeat the rows.
        Assert.Empty(mouse.Facts);
    }

    [Fact]
    public void Horizontal_wheel_row_only_with_a_tilt_wheel()
    {
        _system = _system with { HorizontalWheel = true };
        Assert.Contains(Build().Mouse.Settings, s => s.Id == "wheel-chars");
    }

    [Fact]
    public void Every_spi_row_is_per_user_with_its_catalog_target()
    {
        _system = _system with { HorizontalWheel = true };
        var (mouse, keyboard) = Build();
        var spiRows = mouse.Settings.Concat(keyboard.Settings).Where(s => s.JournalTarget is not null).ToList();

        Assert.Equal(SpiIds.Order(), spiRows.Select(s => s.Id).Order());
        foreach (var s in spiRows)
        {
            Assert.NotNull(SpiCatalog.Find(s.Id));
            Assert.Equal(DeviceTargets.Spi(s.Id), s.JournalTarget);
            Assert.True(s.PerUser);
            Assert.NotNull(s.Apply);
            Assert.NotNull(s.ReadCurrent);
            Assert.Null(s.DisabledReason);
            Assert.Equal(_spi.Live[s.Id], s.Current!.Value.Number);
        }
        // Tweak rows are read-only here (applied from the plan through TweakEngine).
        Assert.True(Row(mouse, "tweak:mouse-acceleration-off").IsReadOnly);
        Assert.True(Row(keyboard, "tweak:accessibility-hotkeys-off").IsReadOnly);
    }

    [Fact]
    public void Every_row_that_changes_something_explains_itself_in_one_short_line()
    {
        _system = _system with { HorizontalWheel = true };
        var (mouse, keyboard) = Build();
        foreach (var s in mouse.Settings.Concat(keyboard.Settings))
        {
            Assert.Null(DeviceScan.HintProblem(s));
            Assert.False(string.IsNullOrWhiteSpace(s.Hint), s.Id);
            Assert.True(s.Hint.Length <= 120, $"{s.Id}: {s.Hint.Length}");
        }
    }

    [Fact]
    public void Windows_defaults_need_no_change()
    {
        var (mouse, keyboard) = Build();
        Assert.All(mouse.Settings.Concat(keyboard.Settings).Where(s => s.TweakId is null), s => Assert.Null(s.Recommended));
        Assert.Empty(keyboard.Notes);
    }

    // ───────────────────────────── Recommendations ─────────────────────────────

    [Fact]
    public void Slow_key_repeat_suggests_31()
    {
        _spi.Live["keyboard-speed"] = 20;
        var r = Row(Build().Keyboard, "keyboard-speed").Recommended!;
        Assert.Equal(31, r.Value.Number);
        Assert.Equal(RecommendPolicy.Suggest, r.Policy);
    }

    [Fact]
    public void Trails_suggest_off_and_trails_1_counts_as_off()
    {
        _spi.Live["mouse-trails"] = 5;
        var on = Row(Build().Mouse, "mouse-trails");
        Assert.Equal(0, on.Recommended!.Value.Number);
        Assert.Equal(RecommendPolicy.Suggest, on.Recommended.Policy);

        _spi.Live["mouse-trails"] = 1;
        var one = Row(Build().Mouse, "mouse-trails");
        Assert.Null(one.Recommended);
        Assert.Equal("выкл", one.SelectedOption!.Label); // the ComboBox is not blank
        Assert.Equal("выкл", one.CurrentText);
        one.SelectedOption = one.Options[0];
        Assert.False(one.IsDirty); // 1 → 0 is no change
    }

    [Fact]
    public void Sonar_on_suggests_off()
    {
        _spi.Live["mouse-sonar"] = 1;
        var r = Row(Build().Mouse, "mouse-sonar").Recommended!;
        Assert.Equal(0, r.Value.Number);
        Assert.Contains("Ctrl", r.Reason);
    }

    [Fact]
    public void Wheel_routing_off_suggests_on_and_the_toggle_stages_2()
    {
        _spi.Live["wheel-routing"] = 0;
        var row = Row(Build().Mouse, "wheel-routing");
        Assert.Equal(2, row.Recommended!.Value.Number);
        Assert.False(row.ToggleValue);
        row.ToggleValue = true;
        Assert.Equal(2, row.Staged!.Value.Number); // never the undocumented hybrid 1
        Assert.True(row.CanApply);
    }

    [Fact]
    public void Pointer_speed_without_dpi_waits_for_the_measurement()
    {
        _spi.Live["mouse-speed"] = 14;
        var (mouse, _) = Build();
        var r = Row(mouse, "mouse-speed").Recommended!;
        Assert.True(r.NeedsMeasurement);
        Assert.Equal(DeviceLinkKind.Tool, r.Link!.Kind);
        Assert.Equal(InputProvider.DpiTool, r.Link.FocusId);

        var plan = DevicePlan.Build([mouse], interactiveUser: true);
        var item = Assert.Single(plan, i => i.Setting.Id == "mouse-speed");
        Assert.Equal(PlanItemKind.Measurement, item.Kind);
        Assert.False(item.Selectable);
    }

    [Fact]
    public void Pointer_speed_with_measured_dpi_suggests_10_and_the_compensating_dpi()
    {
        _spi.Live["mouse-speed"] = 14;
        _results.Set(InputProvider.DpiKey, new DpiMeasurement(1600, 1, 10, [1600, 1600, 1600]));
        var (mouse, _) = Build();

        var r = Row(mouse, "mouse-speed").Recommended!;
        Assert.Equal(10, r.Value.Number);
        Assert.Equal(RecommendPolicy.Suggest, r.Policy);
        Assert.False(r.NeedsMeasurement);
        Assert.Contains("3200", r.Reason);
        // The DPI row shows the measurement but does not add a contradicting «outside 800–1600» advice meanwhile.
        Assert.Equal(1600, Row(mouse, "mouse-dpi").Current!.Value.Number);
        Assert.Null(Row(mouse, "mouse-dpi").Recommended);

        var item = Assert.Single(DevicePlan.Build([mouse], interactiveUser: true), i => i.Setting.Id == "mouse-speed");
        Assert.Equal(PlanItemKind.Setting, item.Kind);
        Assert.Equal("14 (×2) → 10 (×1)", $"{item.From} → {item.To}");
    }

    [Fact]
    public void Dpi_outside_the_range_is_a_finding_at_speed_10()
    {
        _results.Set(InputProvider.DpiKey, new DpiMeasurement(3200, 1, 10, [3200, 3200, 3200]));
        var dpi = Row(Build().Mouse, "mouse-dpi");
        Assert.Equal(RecommendPolicy.Advice, dpi.Recommended!.Policy);
        Assert.Equal(DeviceLinkKind.VendorApp, dpi.Recommended.Link!.Kind);
        Assert.Equal("≈ 3200 DPI", dpi.CurrentText);

        _results.Set(InputProvider.DpiKey, new DpiMeasurement(1200, 1, 10, [1200, 1200, 1200]));
        Assert.Null(Row(Build().Mouse, "mouse-dpi").Recommended);
    }

    [Fact]
    public void Polling_rate_findings()
    {
        _results.Set(InputProvider.PollingKey, new PollingMeasurement(500, 500, 0.95, null));
        var low = Row(Build().Mouse, "mouse-polling");
        Assert.Equal("≈ 500 Гц", low.CurrentText);
        Assert.Equal(RecommendPolicy.Advice, low.Recommended!.Policy);

        _results.Set(InputProvider.PollingKey, new PollingMeasurement(1000, 1000, 0.95, null));
        Assert.Null(Row(Build().Mouse, "mouse-polling").Recommended);
        Assert.Null(new InputProvider(() => _inventory, () => _system).Build(new DeviceScanContext
        {
            Profile = new SystemProfile(), Results = new DeviceResultStore(), InteractiveUser = true,
        }).Single(c => c.Key == "mouse").Settings.Single(s => s.Id == "mouse-polling").Current);
    }

    [Fact]
    public void Re_reading_the_pointer_speed_refreshes_its_suggestion()
    {
        _spi.Live["mouse-speed"] = 14;
        _results.Set(InputProvider.DpiKey, new DpiMeasurement(1600, 1, 10, [1600, 1600, 1600]));
        var (mouse, _) = Build();
        var speed = Row(mouse, "mouse-speed");
        Assert.Contains("3200", speed.Recommended!.Reason);

        speed.Current = DeviceValue.Of(12); // the page re-read the value after a single ПРИМЕНИТЬ
        Assert.Contains("2400", speed.Recommended!.Reason);
        speed.Current = DeviceValue.Of(10);
        Assert.Null(speed.Recommended);
        Assert.Null(Row(mouse, "mouse-dpi").Recommended); // 1600 at speed 10 is fine
    }

    [Fact]
    public void Swap_buttons_is_never_recommended_and_needs_the_countdown()
    {
        _spi.Live["swap-buttons"] = 1;
        var row = Row(Build().Mouse, "swap-buttons");
        Assert.Null(row.Recommended);
        Assert.True(row.NeedsCountdown);
    }

    // ───────────────────────────── Account, writer, state ─────────────────────────────

    [Theory]
    [InlineData(false, "другой учётной записи")]
    [InlineData(null, "не удалось определить")]
    public void Another_or_unknown_account_disables_every_per_user_row_without_card_notes(bool? interactive, string reason)
    {
        _interactive = interactive;
        _spi.Live["keyboard-speed"] = 20;
        var (mouse, keyboard) = Build();
        foreach (var s in mouse.Settings.Concat(keyboard.Settings).Where(s => s.PerUser))
        {
            Assert.Contains(reason, s.DisabledReason);
            Assert.False(s.ControlsEnabled);
        }
        Assert.Empty(mouse.Notes); // the page shows the account text once, not on every card
        Assert.Empty(keyboard.Notes);
        Assert.DoesNotContain(DevicePlan.Build([mouse, keyboard], interactiveUser: false), i => i.Selectable);
    }

    [Fact]
    public void Without_a_writer_only_info_rows()
    {
        using var none = DeviceWriters.OverrideForTests();
        _spi.Live["keyboard-speed"] = 20;
        var (mouse, keyboard) = Build();
        foreach (var s in mouse.Settings.Concat(keyboard.Settings))
        {
            Assert.Equal(SettingControl.Info, s.Control);
            Assert.True(s.IsReadOnly);
            if (s.TweakId is null) Assert.True(s.Recommended is null or { Policy: RecommendPolicy.Advice });
        }
        Assert.DoesNotContain(DevicePlan.Build([mouse, keyboard], interactiveUser: true), i => i.Selectable);
    }

    [Fact]
    public void Saved_value_different_from_the_live_one_waits_for_sign_out()
    {
        var p = SpiCatalog.Find("mouse-speed")!.Persisted!;
        RegistryHelper.Write(RegHive.HKCU, p.Path, p.Name, RegistryValueKind.String, "14");
        var (mouse, _) = Build();
        Assert.Equal(InputProvider.PendingSignOut, Row(mouse, "mouse-speed").StateTag);

        RegistryHelper.Write(RegHive.HKCU, p.Path, p.Name, RegistryValueKind.String, "10");
        Assert.Null(Row(Build().Mouse, "mouse-speed").StateTag);
    }

    [Fact]
    public void Acceleration_tweak_applied_but_session_still_accelerates_waits_for_sign_out()
    {
        foreach (var name in new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" })
            RegistryHelper.Write(RegHive.HKCU, @"Control Panel\Mouse", name, RegistryValueKind.String, "0");
        _system = _system with { AccelerationOn = true };
        Assert.Equal(InputProvider.PendingSignOut, Row(Build().Mouse, "tweak:mouse-acceleration-off").StateTag);

        _system = _system with { AccelerationOn = false };
        Assert.Null(Row(Build().Mouse, "tweak:mouse-acceleration-off").StateTag);
    }

    [Fact]
    public void Filter_keys_take_over_the_repeat_settings_and_are_noted()
    {
        _spi.Live["keyboard-speed"] = 20;
        _system = _system with { FilterKeysOn = true, FilterDelayMs = 1000, FilterWaitMs = 500, StickyKeysOn = true };
        var keyboard = Build().Keyboard;

        Assert.Contains("Фильтрация ввода", Row(keyboard, "keyboard-delay").DisabledReason);
        Assert.Contains("1000 мс", Row(keyboard, "keyboard-delay").DisabledReason);
        Assert.Contains("Фильтрация ввода", Row(keyboard, "keyboard-speed").DisabledReason);
        Assert.Null(Row(keyboard, "keyboard-speed").Recommended);
        Assert.Equal(2, keyboard.Notes.Count);
        Assert.All(keyboard.Notes, n => Assert.Equal("ms-settings:easeofaccess-keyboard", n.Link!.Target));
        Assert.Contains(keyboard.Notes, n => n.Text.Contains("500 мс"));
        Assert.All(keyboard.Notes, n => Assert.Null(DeviceApply.ValidateLink(n.Link!)));
    }

    [Fact]
    public async Task Optimize_tweak_rows_enter_the_plan_as_tweaks_and_leave_it_once_applied()
    {
        var provider = new InputProvider(() => _inventory, () => _system);
        using (DeviceProviders.OverrideForTests([provider]))
        {
            var scan = await DeviceScan.RunAsync(Ctx());
            var mouse = scan.Cards.Single(c => c.Key == "mouse");
            var epp = Row(mouse, "tweak:mouse-acceleration-off");
            Assert.Equal("вкл", epp.CurrentText); // EPP on in the (empty, redirected) profile = tweak not applied
            var item = Assert.Single(DevicePlan.Build(scan.Cards, interactiveUser: true), i => i.Setting.Id == "tweak:mouse-acceleration-off");
            Assert.Equal(PlanItemKind.OptimizeTweak, item.Kind);
            Assert.Equal("mouse-acceleration-off", item.TweakId);
            Assert.Equal("вкл → выкл", $"{item.From} → {item.To}");
        }

        foreach (var name in new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" })
            RegistryHelper.Write(RegHive.HKCU, @"Control Panel\Mouse", name, RegistryValueKind.String, "0");
        using (DeviceProviders.OverrideForTests([provider]))
        {
            var scan = await DeviceScan.RunAsync(Ctx());
            Assert.DoesNotContain(DevicePlan.Build(scan.Cards, interactiveUser: true), i => i.Setting.Id == "tweak:mouse-acceleration-off");
            Assert.Equal("выкл", Row(scan.Cards.Single(c => c.Key == "mouse"), "tweak:mouse-acceleration-off").CurrentText);
        }
    }

    // ───────────────────────────── Apply ─────────────────────────────

    private readonly ChangeJournal _journal = ChangeJournal.ForTests();

    private Task<ChangeSession> Begin() => ChangeSession.BeginAsync("test", createRestorePoint: false, journal: _journal);

    [Fact]
    public async Task Plain_apply_records_and_reports_no_change_when_equal()
    {
        var row = Row(Build().Mouse, "wheel-lines");
        var s = await Begin();
        Assert.Equal(ApplyOutcome.Applied, await row.Apply!(s, DeviceValue.Of(5), CancellationToken.None));
        Assert.Equal(5, _spi.Live["wheel-lines"]);
        Assert.Single(_journal.ActiveRecords, r => r.Target == DeviceTargets.Spi("wheel-lines") && r.Source == DeviceTargets.Source);
        Assert.Equal(ApplyOutcome.NoChange, await row.Apply!(s, DeviceValue.Of(5), CancellationToken.None));
        Assert.Equal(5, row.ReadCurrent!()!.Value.Number);
    }

    [Fact]
    public async Task Swap_not_confirmed_is_reverted_in_the_sessions_own_journal()
    {
        DeviceConfirm.Countdown = (_, _) => Task.FromResult(false);
        var row = Row(Build().Mouse, "swap-buttons");
        var s = await Begin();

        var outcome = await row.Apply!(s, DeviceValue.Of(1), CancellationToken.None);

        Assert.Equal(ApplyOutcome.Declined, outcome);
        Assert.Equal(0, _spi.Live["swap-buttons"]);
        Assert.Empty(_journal.ActiveRecords);
        Assert.Single(_journal.Records, r => r.Target == DeviceTargets.Spi("swap-buttons") && r.Reverted);
    }

    [Fact]
    public async Task Swap_confirmed_is_kept()
    {
        CountdownRequest? asked = null;
        DeviceConfirm.Countdown = (r, _) =>
        {
            asked = r;
            return Task.FromResult(true);
        };
        var row = Row(Build().Mouse, "swap-buttons");
        var s = await Begin();

        Assert.Equal(ApplyOutcome.Applied, await row.Apply!(s, DeviceValue.Of(1), CancellationToken.None));
        Assert.Equal(1, _spi.Live["swap-buttons"]);
        Assert.Single(_journal.ActiveRecords);
        Assert.Equal(CountdownRequest.Keep, asked!.KeepText);
        Assert.Equal(15, asked.Seconds);
    }

    [Fact]
    public async Task Very_low_pointer_speed_needs_the_countdown_and_normal_speeds_do_not()
    {
        var asked = 0;
        DeviceConfirm.Countdown = (_, _) =>
        {
            asked++;
            return Task.FromResult(false);
        };
        var row = Row(Build().Mouse, "mouse-speed");
        var s = await Begin();

        Assert.Equal(ApplyOutcome.Declined, await row.Apply!(s, DeviceValue.Of(2), CancellationToken.None));
        Assert.Equal(10, _spi.Live["mouse-speed"]);
        Assert.Equal(1, asked);

        Assert.Equal(ApplyOutcome.Applied, await row.Apply!(s, DeviceValue.Of(8), CancellationToken.None));
        Assert.Equal(8, _spi.Live["mouse-speed"]);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task Apply_for_another_account_is_refused_without_a_change()
    {
        var row = Row(Build().Mouse, "wheel-lines");
        _interactive = false;
        var s = await Begin();
        Assert.Equal(ApplyOutcome.Failed, await row.Apply!(s, DeviceValue.Of(5), CancellationToken.None));
        Assert.Equal(3, _spi.Live["wheel-lines"]);
        Assert.Equal(0, _spi.SetCalls);
        Assert.Empty(_journal.Records);
    }

    // ───────────────────────────── Devices ─────────────────────────────

    [Theory]
    [InlineData(@"\\?\HID#VID_046D&PID_C54D&MI_00#7&abc&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}", @"HID\VID_046D&PID_C54D&MI_00\7&abc&0&0000")]
    [InlineData(@"\\?\HID#VID_046D&PID_C54D&MI_01&Col01#8&2a&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}", @"HID\VID_046D&PID_C54D&MI_01&Col01\8&2a&0&0000")]
    [InlineData(@"\\?\USB#VID_046D&PID_C54D#375C32603335#{a5dcbf10-6530-11d2-901f-00c04fb951ed}", @"USB\VID_046D&PID_C54D\375C32603335")]
    [InlineData(@"\\?\ACPI#PNP0303#4&1d401fb5&0#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}\KBD", @"ACPI\PNP0303\4&1d401fb5&0")]
    public void Interface_path_to_instance_id(string path, string expected) =>
        Assert.Equal(expected, InputDevices.InterfacePathToInstanceId(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"HID#VID_046D&PID_C54D#x#{378de44c-56ef-11d1-bc8c-00a0c91405dd}")] // no \\?\ prefix
    [InlineData(@"\\?\HID")]
    [InlineData(@"\\?\HID#VID_046D&PID_C54D#7&abc")] // no interface class
    [InlineData(@"\\?\HID#{378de44c-56ef-11d1-bc8c-00a0c91405dd}")] // nothing before the class
    [InlineData(@"\\?\HID##x#{378de44c-56ef-11d1-bc8c-00a0c91405dd}")]
    [InlineData(@"\\?\HID#VID_046D&PID_C54D#x#{not-a-guid}")]
    public void Malformed_interface_paths_give_null(string? path) =>
        Assert.Null(InputDevices.InterfacePathToInstanceId(path));

    [Fact]
    public void Vid_pid_and_interface_order()
    {
        Assert.Equal(("046D", "C54D"), InputDevices.ParseVidPid(@"USB\VID_046d&PID_c54d\1"));
        Assert.Equal(("046D", "B023"), InputDevices.ParseVidPid(@"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002046d_PID&b023\9&1"));
        Assert.Equal(("046D", "B023"), InputDevices.ParseVidPid(@"BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02046d_PID&b023_REV&0011_d4c3\8&1"));
        Assert.Null(InputDevices.ParseVidPid(@"ACPI\PNP0303\4&1"));
        Assert.Equal((1, 1), InputDevices.InterfaceOrder(@"HID\VID_046D&PID_C54D&MI_01&Col01\8&2a"));
        Assert.Equal((0, 0), InputDevices.InterfaceOrder(@"HID\VID_046D&PID_C339\1"));
        Assert.True(InputDevices.IsUsbDeviceId(@"USB\VID_046D&PID_C54D\375C32603335"));
        Assert.False(InputDevices.IsUsbDeviceId(@"USB\VID_046D&PID_C54D&MI_00\7&1"));
    }

    private const string ReceiverUsb = @"USB\VID_046D&PID_C54D\375C32603335";
    private const string KeyboardUsb = @"USB\VID_046D&PID_C339\1234567890";

    /// <summary>The owner's PC: LIGHTSPEED receiver (mouse MI_00 + keyboard TLC MI_01 Col01), PRO keyboard, G HUB virtual devices.</summary>
    private static (List<RawCollection> Raw, Func<string, CollectionNode?> Resolve) OwnerDevices()
    {
        var raw = new List<RawCollection>
        {
            new(1, RawKind.Mouse, @"HID\VID_046D&PID_C54D&MI_00\7&1&0&0000"),
            new(2, RawKind.Keyboard, @"HID\VID_046D&PID_C54D&MI_01&Col01\7&2&0&0000"),
            new(3, RawKind.Keyboard, @"HID\VID_046D&PID_C339&MI_00\7&3&0&0000"),
            new(4, RawKind.Mouse, @"HID\VID_046D&PID_C231\1&4"),
            new(5, RawKind.Keyboard, @"HID\VID_046D&PID_C232\1&5"),
            new(6, RawKind.Hid, @"HID\VID_046D&PID_C54D&MI_02\7&6&0&0000"),
        };
        CollectionNode? Resolve(string id) => id switch
        {
            _ when id.Contains("PID_C54D") => new CollectionNode(false, ReceiverUsb, "USB", "046D", "C54D", "{aaaaaaaa-0000-0000-0000-000000000001}", false, "USB Receiver"),
            _ when id.Contains("PID_C339") => new CollectionNode(false, KeyboardUsb, "USB", "046D", "C339", "{bbbbbbbb-0000-0000-0000-000000000002}", false, "PRO Gaming Keyboard"),
            _ when id.Contains("PID_C23") => new CollectionNode(true, null, null, "046D", id.Contains("C231") ? "C231" : "C232", null, false, null),
            _ => null,
        };
        return (raw, Resolve);
    }

    [Fact]
    public void Receiver_keyboard_collection_does_not_make_a_second_keyboard()
    {
        var (raw, resolve) = OwnerDevices();
        var inventory = InputDevices.Classify(InputDevices.Group(raw, resolve));

        var mouse = Assert.Single(inventory.Mice);
        Assert.Equal("C54D", mouse.Pid);
        Assert.Equal([(IntPtr)1], mouse.MouseHandles);
        Assert.Equal(DeviceKind.Mouse, mouse.PrimaryKind);
        var keyboard = Assert.Single(inventory.Keyboards);
        Assert.Equal("C339", keyboard.Pid);
        Assert.Equal("PRO Gaming Keyboard", keyboard.Name);
        Assert.Equal([(IntPtr)1], inventory.MouseHandles); // the G HUB virtual mouse is never measured
        Assert.Contains(ReceiverUsb, mouse.InstanceIds);

        _inventory = inventory;
        var (mouseCard, keyboardCard) = Build();
        Assert.Equal("USB Receiver · USB 046D:C54D", mouseCard.Subtitle);
        Assert.Equal("C54D", Assert.Single(mouseCard.Hardware).Pid);
        Assert.Equal("PRO Gaming Keyboard", keyboardCard.Title);
        Assert.Equal("USB 046D:C339", keyboardCard.Subtitle);
        Assert.Equal("C339", Assert.Single(keyboardCard.Hardware).Pid);
    }

    [Fact]
    public void A_receiver_alone_still_gives_a_keyboard()
    {
        var (raw, resolve) = OwnerDevices();
        var inventory = InputDevices.Classify(InputDevices.Group(raw.Where(r => !r.InstanceId.Contains("C339")), resolve));
        Assert.Equal("C54D", Assert.Single(inventory.Keyboards).Pid);
        Assert.Equal("C54D", Assert.Single(inventory.Mice).Pid);
    }

    [Fact]
    public void Built_in_devices_in_the_local_machine_container_stay_apart()
    {
        var raw = new List<RawCollection>
        {
            new(1, RawKind.Mouse, @"HID\VEN_ELAN&DEV_0001&Col01\5&1"),
            new(2, RawKind.Keyboard, @"ACPI\PNP0303\4&1"),
        };
        var local = new Guid("00000000-0000-0000-ffff-ffffffffffff").ToString("B");
        var devices = InputDevices.Group(raw, id => new CollectionNode(false, null, null, null, null, local, true, null));
        Assert.Equal(2, devices.Count);
        var inventory = InputDevices.Classify(devices);
        Assert.Single(inventory.Mice);
        Assert.Single(inventory.Keyboards);
        Assert.Null(inventory.Mice[0].ContainerId);
    }
}
