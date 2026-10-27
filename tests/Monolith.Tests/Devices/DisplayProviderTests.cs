using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Devices;
using Monolith.Modules.Devices.Displays;
using Monolith.Tests.Core;

namespace Monolith.Tests.Devices;

/// <summary>
/// The monitor cards built from hand-made MonitorInfo (no native calls; fake writers through DeviceWriters.OverrideForTests),
/// plus one apply through a test journal with a fake display and a fake countdown answer.
/// </summary>
[Collection("Registry")] // DeviceWriters.OverrideForTests, DeviceConfirm and ChangeSession test switches are process-wide
public sealed class DisplayProviderTests : IDisposable
{
    private const string Path = DeviceTestData.MonitorPath;
    private readonly FakeDisplayWriter _display = new();
    private readonly FakeMonitorWriter _ddc = new();
    private IDisposable _writers;

    public DisplayProviderTests() => _writers = DeviceWriters.OverrideForTests(display: _display, monitor: _ddc);

    public void Dispose()
    {
        _writers.Dispose();
        DeviceConfirm.Countdown = null;
        ChangeSession.BypassElevationForTests = false;
    }

    private static readonly OfferedMode[] Offered =
    [
        new(2560, 1440, 240), new(2560, 1440, 144), new(2560, 1440, 60), new(1920, 1080, 240), new(1920, 1080, 60),
    ];

    private static MonitorInfo Monitor(int hz = 240, bool hdrSupported = true, bool hdrOn = false, bool ddc = true, bool primary = true,
        string path = Path, string name = "X27U W3") => new()
    {
        MonitorPath = path,
        Name = name,
        GdiName = @"\\.\DISPLAY1",
        OutputTechnology = 10,
        IsPrimary = primary,
        Current = new DisplayModeSpec(2560, 1440, hz, hz == 240 ? 239970u : 0, hz == 240 ? 1000u : 0, 32, 0, 0, 0),
        Modes = Offered,
        PreferredSize = new DisplaySize(2560, 1440),
        Edid = EdidParser.Parse(EdidParserTests.X27u()),
        Color = new AdvancedColorInfo(hdrSupported, hdrOn, true, false, 8, false),
        SdrWhiteNits = hdrOn ? 200 : 80,
        ScalePercent = 100,
        Brightness = ddc ? new VcpReading(0, 100, 100) : null,
        Contrast = ddc ? new VcpReading(0, 50, 100) : null,
    };

    private static DeviceCard Card(MonitorInfo m, DisplayEnvironment? env = null) =>
        DisplayProvider.BuildCards([m], env ?? new DisplayEnvironment()).Single();

    private static DeviceSetting? Row(DeviceCard c, string id) => c.Settings.FirstOrDefault(s => s.Id == id);

    [Fact]
    public void Card_key_title_and_facts()
    {
        var c = Card(Monitor());

        Assert.Equal("display:" + Path, c.Key);
        Assert.Equal(DeviceKind.Monitor, c.Kind);
        Assert.Equal("X27U W3", c.Title);
        Assert.Equal("основной · DisplayPort", c.Subtitle);
        Assert.Equal(30, c.Order);
        Assert.Equal(["2560×1440", "239,97 Гц", "8 бит", "HDR: поддерживается, выкл", "масштаб 100 %"], c.Facts);
        Assert.Contains("SDR-контент ≈ 200 нит", Card(Monitor(hdrOn: true)).Facts);
    }

    [Fact]
    public void Mode_row_uses_the_integer_rate_key_and_shows_the_exact_rate()
    {
        var mode = Row(Card(Monitor()), DisplayProvider.SettingMode)!;

        Assert.Equal(SettingControl.DualChoice, mode.Control);
        Assert.Equal("Разрешение и частота", mode.Title);
        Assert.Equal("2560x1440@240", mode.Current!.Value.Text);
        Assert.Equal("2560×1440, 239,97 Гц", mode.CurrentText);
        Assert.Equal("2560×1440 · родное", mode.Options[0].Label);
        Assert.Equal(["240 Гц", "144 Гц", "60 Гц"], mode.SecondaryOptions!(mode.Options[0].Value).Select(o => o.Label));
        Assert.True(mode.NeedsCountdown);
        Assert.Equal(DeviceTargets.DisplayMode(Path), mode.JournalTarget);
        Assert.Null(mode.Recommended); // 239,97 Гц is the 240 Гц mode: nothing to recommend
    }

    [Fact]
    public void Mode_recommendation_only_when_below_max()
    {
        var mode = Row(Card(Monitor(hz: 144)), DisplayProvider.SettingMode)!;

        Assert.NotNull(mode.Recommended);
        Assert.Equal("2560x1440@240", mode.Recommended!.Value.Text);
        Assert.Equal(RecommendPolicy.Include, mode.Recommended.Policy);
        Assert.Equal("рек. 2560×1440, 240 Гц", mode.RecommendedText);

        var plan = DevicePlan.Build([Card(Monitor(hz: 144))], interactiveUser: true);
        var row = Assert.Single(plan);
        Assert.Equal("2560×1440, 144 Гц", row.From);
        Assert.Equal("2560×1440, 240 Гц", row.To);
        Assert.Equal("С ПОДТВЕРЖДЕНИЕМ", row.TagText);

        // On battery: no refresh recommendation.
        Assert.Null(Row(Card(Monitor(hz: 144), new DisplayEnvironment { OnBattery = true }), DisplayProvider.SettingMode)!.Recommended);
    }

    [Fact]
    public void Hdr_row_only_when_supported()
    {
        var hdr = Row(Card(Monitor()), DisplayProvider.SettingHdr)!;
        Assert.Equal(SettingControl.Toggle, hdr.Control);
        Assert.Equal(DeviceValue.Of(false), hdr.Current);
        Assert.Null(hdr.Recommended); // taste and panel class: never an automatic value
        Assert.Contains("456", hdr.Hint);
        Assert.True(hdr.NeedsCountdown);
        Assert.Null(Row(Card(Monitor(hdrSupported: false)), DisplayProvider.SettingHdr));

        var limited = Monitor() with { Color = new AdvancedColorInfo(true, false, true, false, 8, LimitedByPolicy: true) };
        var blocked = Row(Card(limited), DisplayProvider.SettingHdr)!;
        Assert.True(blocked.IsReadOnly);
        Assert.NotNull(blocked.DisabledReason);
    }

    [Fact]
    public void Ddc_rows_only_when_the_monitor_answers_and_the_bottom_is_cut_off()
    {
        var c = Card(Monitor());
        var brightness = Row(c, DisplayProvider.SettingBrightness)!;
        var contrast = Row(c, DisplayProvider.SettingContrast)!;

        Assert.Equal(SettingControl.Slider, brightness.Control);
        Assert.Equal(5, brightness.Min);   // 5 % of 0..100
        Assert.Equal(100, brightness.Max);
        Assert.Equal(10, contrast.Min);    // 10 % of 0..100
        Assert.Equal("50 из 100", contrast.CurrentText);
        Assert.Null(brightness.Recommended);
        Assert.Equal(DeviceTargets.Monitor(Path, MonitorVcp.Brightness), brightness.JournalTarget);
        Assert.Equal(3u, DisplayProvider.DdcFloor(MonitorVcp.Brightness, 0, 50)); // the G5: 0..50

        var none = Card(Monitor(ddc: false));
        Assert.Null(Row(none, DisplayProvider.SettingBrightness));
        Assert.Null(Row(none, DisplayProvider.SettingContrast));
    }

    [Fact]
    public void Auto_hdr_row_only_when_hdr_is_on_and_vrr_once_never_planned()
    {
        Assert.DoesNotContain(Card(Monitor()).Settings, s => s.TweakId == DisplayProvider.AutoHdrTweak);
        var on = Card(Monitor(hdrOn: true));
        Assert.Contains(on.Settings, s => s.TweakId == DisplayProvider.AutoHdrTweak);

        // Two VRR monitors: one VRR tweak row (on the primary), one range row each.
        var cards = DisplayProvider.BuildCards(
            [Monitor(primary: false, path: Path.Replace("UID4352", "UID9"), name: "G5"), Monitor()], new DisplayEnvironment());
        var vrrRows = cards.SelectMany(c => c.Settings).Where(s => s.TweakId == DisplayProvider.VrrTweak).ToList();
        var vrr = Assert.Single(vrrRows);
        Assert.Contains(cards.Single(c => c.Title == "X27U W3").Settings, s => ReferenceEquals(s, vrr));
        Assert.Equal(RecommendPolicy.None, vrr.TweakPolicy);
        Assert.Equal(2, cards.SelectMany(c => c.Settings).Count(s => s.Id == DisplayProvider.SettingVrr));
        Assert.Equal("48–240 Гц", Row(cards[1], DisplayProvider.SettingVrr)!.CurrentText);
        Assert.Contains("237", Row(cards[1], DisplayProvider.SettingVrr)!.Hint);
    }

    [Fact]
    public void Without_a_writer_or_with_a_bad_path_the_rows_are_read_only()
    {
        _writers.Dispose();
        _writers = DeviceWriters.OverrideForTests();
        var c = Card(Monitor());
        Assert.All(c.Settings.Where(s => s.TweakId is null), s => Assert.Null(s.Apply));
        Assert.Equal(SettingControl.Info, Row(c, DisplayProvider.SettingMode)!.Control);
        Assert.NotNull(Row(c, DisplayProvider.SettingMode)!.DisabledReason);

        _writers.Dispose();
        _writers = DeviceWriters.OverrideForTests(display: _display, monitor: _ddc);
        var bad = Card(Monitor(path: @"\\.\DISPLAY1"));
        Assert.All(bad.Settings.Where(s => s.TweakId is null), s => Assert.Null(s.Apply));
    }

    [Fact]
    public void Every_row_that_changes_something_explains_itself_briefly()
    {
        foreach (var c in DisplayProvider.BuildCards([Monitor(hz: 144, hdrOn: true)], new DisplayEnvironment()))
        foreach (var s in c.Settings)
            Assert.Null(DeviceScan.HintProblem(s));
    }

    [Fact]
    public void Notes_only_when_actionable()
    {
        Assert.Empty(Card(Monitor()).Notes);

        // HDR on and the calibration app missing: one note with the HDR settings link.
        var hdr = Card(Monitor(hdrOn: true), new DisplayEnvironment { HdrCalibrationInstalled = false });
        var note = Assert.Single(hdr.Notes);
        Assert.Equal("ms-settings:display-hdr", note.Link!.Target);
        Assert.Null(DeviceApply.ValidateLink(note.Link));
        Assert.Empty(Card(Monitor(hdrOn: true), new DisplayEnvironment { HdrCalibrationInstalled = true }).Notes);

        // The link is the bottleneck: Windows offers at most 144 Гц, the EDID lists 239,97.
        var slow = Monitor() with { Modes = [new(2560, 1440, 144), new(2560, 1440, 60)], Current = new DisplayModeSpec(2560, 1440, 144, 0, 0, 32, 0, 0, 0) };
        var cable = Assert.Single(Card(slow).Notes);
        Assert.Contains("240 Гц", cable.Text);
        Assert.Null(cable.Link); // not on the iGPU: no motherboard advice
        var onIgpu = Card(slow with { AdapterKey = "igpu" }, new DisplayEnvironment { IntegratedAdapterKeys = new HashSet<string> { "igpu" } });
        Assert.Equal("dashboard", Assert.Single(onIgpu.Notes).Link!.Target);

        // A slower external monitor is primary: the fastest one says so.
        var cards = DisplayProvider.BuildCards(
            [Monitor(primary: true, path: Path.Replace("UID4352", "UID9"), name: "Slow") with { Modes = [new(2560, 1440, 60)], Current = new DisplayModeSpec(2560, 1440, 60, 0, 0, 32, 0, 0, 0), Edid = null },
             Monitor(primary: false)], new DisplayEnvironment());
        Assert.Contains(cards.Single(c => c.Title == "X27U W3").Notes, n => n.Link?.Target == "ms-settings:display");
        Assert.Empty(cards.Single(c => c.Title == "Slow").Notes);
    }

    [Fact]
    public async Task Apply_parses_the_value_and_goes_through_the_safe_apply_protocol()
    {
        ChangeSession.BypassElevationForTests = true;
        DeviceConfirm.Countdown = (_, _) => Task.FromResult(true);
        var original = new DisplayModeSpec(2560, 1440, 144, 143980, 1000, 32, 0, 0, 0);
        _display.Current = original;
        _display.Persisted = original;
        _display.Offered.AddRange([original, DeviceTestData.Mode(2560, 1440, 240, 239970, 1000)]);
        var journal = ChangeJournal.ForTests();
        var mode = Row(Card(Monitor(hz: 144)), DisplayProvider.SettingMode)!;

        var s = await ChangeSession.BeginAsync("test", createRestorePoint: false, journal: journal);
        var outcome = await mode.Apply!(s, mode.Recommended!.Value, CancellationToken.None);

        Assert.Equal(ApplyOutcome.Applied, outcome);
        var r = Assert.Single(journal.ActiveRecords);
        Assert.Equal(DeviceTargets.DisplayMode(Path), r.Target);
        Assert.Equal(DeviceTargets.Source, r.Source);
        Assert.Equal(240, r.DisplayMode!.Target.Frequency);
        Assert.Equal(original, r.DisplayMode.Original);
        Assert.Equal("2560x1440@240", mode.ReadCurrent!()!.Value.Text);

        // A value that is not in the monitor's own list never reaches the writer.
        var calls = _display.Calls.Count;
        Assert.Equal(ApplyOutcome.Failed, await mode.Apply!(s, DeviceValue.Of("2560x1440@500"), CancellationToken.None));
        Assert.Equal(calls, _display.Calls.Count);
    }

    [Fact]
    public void Display_outcomes_map_to_row_outcomes()
    {
        ChangeSession.BypassElevationForTests = true;
        var session = ChangeSession.BeginAsync("map", createRestorePoint: false, journal: ChangeJournal.ForTests()).GetAwaiter().GetResult();
        Assert.Equal(ApplyOutcome.Applied, DisplayProvider.Map(session, "t", new DisplayApplyOutcome(DisplayApplyResult.Kept)));
        Assert.Equal(ApplyOutcome.NoChange, DisplayProvider.Map(session, "t", DisplayApplyOutcome.Unchanged));
        Assert.Equal(ApplyOutcome.Declined, DisplayProvider.Map(session, "t", new DisplayApplyOutcome(DisplayApplyResult.Reverted)));
        Assert.Equal(ApplyOutcome.Failed, DisplayProvider.Map(session, "t", new DisplayApplyOutcome(DisplayApplyResult.Failed, "x")));
        Assert.Equal(ApplyOutcome.Blocked, DisplayProvider.Map(session, "t", new DisplayApplyOutcome(DisplayApplyResult.Blocked, "удалённый сеанс")));
        Assert.Contains(session.Errors, e => e.Contains("удалённый сеанс")); // the reason reaches the toast
    }
}
