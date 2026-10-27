using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Devices;
using Monolith.Modules.Devices.Audio;
using Monolith.Tests.Core;
using static Monolith.Tests.Devices.AudioAdviceTests;

namespace Monolith.Tests.Devices;

/// <summary>
/// The «Звук» cards built from a hand-made snapshot: monitor outputs as a note, microphone rows (level, mute, the level test),
/// privacy, hardware refs, the ducking tweak row, and the level test → recommendation → apply → re-measure cycle.
/// </summary>
[Collection("Registry")] // DeviceWriters, Elevation and ChangeSession overrides are process-wide
public sealed class AudioProviderTests : IDisposable
{
    private readonly FakeAudioWriter _audio = new();
    private readonly IDisposable _writers;
    private int _captures;

    public AudioProviderTests()
    {
        ChangeSession.BypassElevationForTests = true;
        Elevation.InteractiveUserForTests = () => true;
        _writers = DeviceWriters.OverrideForTests(audio: _audio);
    }

    public void Dispose()
    {
        _writers.Dispose();
        Elevation.InteractiveUserForTests = null;
        ChangeSession.BypassElevationForTests = false;
    }

    private static EndpointInfo RazerMic(float scalar = 0.75f, float db = 5.6f, bool mute = false) =>
        Capture(Mic, "Микрофон (Razer Seiren V3 Mini)", console: true, comms: true) with
        {
            Scalar = scalar, Db = db, DbMin = -16, DbMax = 15, Mute = mute, HardwareSupport = 0x3, Vid = "1532", Pid = "056A",
            ContainerId = "{11111111-2222-3333-4444-555555555555}", InstanceId = @"USB\VID_1532&PID_056A&MI_00\7&2B3C4D5E&0&0000",
            EnhancementsOff = true,
        };

    private static AudioSnapshot OwnerPc(EndpointInfo? mic = null, MicPrivacy privacy = MicPrivacy.Allow, int? ducking = 3) => new(
    [
        Render(Headset, "Наушники (INZONE E9)", 3, console: true, comms: true, instance: @"USB\VID_054C&PID_0F80&MI_00\7&1A2B3C4D&0&0000") with
        {
            Vid = "054C", Pid = "0F80", Scalar = 0.30f, Db = -18, DbMin = -74, DbMax = 0, Mute = false, EnhancementsOff = true,
            SpatialSupported = true, Spatial = SpatialState.Off,
        },
        Render(DisplayOut, "X27U W3 (NVIDIA High Definition Audio)", 9, instance: @"HDAUDIO\FUNC_01&VEN_10DE&DEV_00AB\5&2BB51DC2&0&0001"),
        Render(DisplayOut2, "Odyssey G5 (NVIDIA High Definition Audio)", 9, instance: @"HDAUDIO\FUNC_01&VEN_10DE&DEV_00AB\5&2BB51DC2&0&0001"),
        mic ?? RazerMic(),
    ], ducking, false, privacy);

    private AudioProvider Provider(Func<string, EndpointLevel?>? level = null, float[]? capture = null) => new()
    {
        ReadSnapshot = () => OwnerPc(),
        ReadLevel = level ?? (_ => new EndpointLevel(0.75f, 5.6f, -16, 15, false, 0x3)),
        ReadPrivacy = () => MicPrivacy.Allow,
        Capture = (id, d, p, ct) =>
        {
            _captures++;
            p?.Report(0.05);
            return Task.FromResult(new MicCapture(capture ?? Bursts(-24, 5), 48000, new AudioFormat(48000, 24, 1, false, 24, 3)));
        },
    };

    private static DeviceScanContext Ctx(DeviceResultStore? results = null, bool? interactive = true) =>
        new() { Profile = new SystemProfile(), Results = results ?? new DeviceResultStore(), InteractiveUser = interactive };

    private static float[] Bursts(double dbfs, double seconds)
    {
        var n = (int)(48000 * seconds);
        var s = new float[n];
        var a = Math.Pow(10, dbfs / 20);
        for (var i = 0; i < n; i++) s[i] = (float)((i % 12000 < 7200 ? a : 0.001) * Math.Sin(2 * Math.PI * 300 * i / 48000));
        return s;
    }

    private static DeviceSetting Row(DeviceCard card, string id) => card.Settings.Single(s => s.Id == id);

    private static async Task RunTool(DeviceCard card, DeviceTool tool, DeviceResultStore results) =>
        await tool.Run(new DeviceToolRun(tool, card, results, CancellationToken.None), CancellationToken.None);

    // ───────────────────────────── Mapping ─────────────────────────────

    [Fact]
    public void Unused_monitor_outputs_are_one_note_not_cards()
    {
        var cards = Provider().Build(OwnerPc(), Ctx());

        Assert.Equal(["audio:" + Headset, "audio:" + Mic], cards.Select(c => c.Key));
        var output = cards[0];
        Assert.Equal(DeviceKind.AudioOutput, output.Kind);
        Assert.Equal("по умолчанию · связь", output.Subtitle);
        var note = Assert.Single(output.Notes);
        Assert.Contains("X27U W3, Odyssey G5", note.Text);
        Assert.Equal("ms-settings:sound", note.Link!.Target);
        Assert.Equal(["USB", "стерео", "громкость 30 %"], output.Facts);
        Assert.Equal(cards[1].Order, cards.Max(c => c.Order));
        Assert.True(output.Order < cards[1].Order);
    }

    [Fact]
    public void A_monitor_output_in_use_is_a_card_with_the_finding_on_the_headphones()
    {
        var snap = new AudioSnapshot(
        [
            Render(Headset, "Наушники (INZONE E9)", 3, comms: true),
            Render(DisplayOut, "X27U W3 (NVIDIA High Definition Audio)", 9, console: true),
        ], null, false, MicPrivacy.Allow);

        var cards = Provider().Build(snap, Ctx());

        Assert.Equal(["audio:" + DisplayOut, "audio:" + Headset], cards.Select(c => c.Key));
        var finding = Row(cards[1], "audio-default").Recommended!;
        Assert.Equal(RecommendPolicy.Advice, finding.Policy);
        Assert.Equal(DeviceLinkKind.SoundPanel, finding.Link!.Kind);
        Assert.Null(Row(cards[0], "audio-default").Recommended);
        var plan = DevicePlan.Build(cards, true);
        Assert.Contains(plan, p => p.Kind == PlanItemKind.Advice && p.Card == cards[1]);
    }

    [Fact]
    public void Outputs_are_read_only_info_rows_and_the_main_one_has_the_ducking_tweak()
    {
        var output = Provider().Build(OwnerPc(ducking: null), Ctx())[0];

        Assert.All(output.Settings, s => Assert.True(s.IsReadOnly, s.Id));
        Assert.Equal(["audio-default", "audio-format", "audio-enhancements", "audio-spatial", "tweak:audio-ducking"], output.Settings.Select(s => s.Id));
        Assert.Equal("48 кГц · 16 бит", Row(output, "audio-format").CurrentText);
        Assert.StartsWith("ms-settings:sound-properties?endpointId=%7B0.0.0.00000000%7D", Row(output, "audio-format").Link!.Target);
        Assert.Null(DeviceApply.ValidateLink(Row(output, "audio-format").Link!));

        var ducking = Row(output, "tweak:audio-ducking");
        Assert.Equal(AudioProvider.DuckingTweakId, ducking.TweakId);
        Assert.Null(ducking.Apply); // never a second toggle: the plan applies the tweak through TweakEngine
        Assert.Equal(RecommendPolicy.Suggest, ducking.TweakPolicy);
        Assert.Equal(new DeviceLink(DeviceLinkKind.SoundPanel, "ВКЛАДКА «СВЯЗЬ»", "3"), ducking.Link);
        Assert.Equal("тише на 80 %", ducking.FormatValue(DeviceValue.Of(false)));
        Assert.Equal("ничего не делать", ducking.FormatValue(DeviceValue.Of(true)));
        Assert.NotNull(Monolith.Tweaks.TweakCatalog.Find(ducking.TweakId!));
    }

    [Fact]
    public void The_microphone_card_has_level_mute_and_the_inline_level_test()
    {
        var mic = Provider().Build(OwnerPc(), Ctx())[1];

        Assert.Equal(DeviceKind.AudioInput, mic.Kind);
        Assert.Equal(["mic-level", "mic-mute", "audio-default", "audio-format", "audio-enhancements"], mic.Settings.Select(s => s.Id));
        var level = Row(mic, "mic-level");
        Assert.Equal(SettingControl.Slider, level.Control);
        Assert.Equal(DeviceTargets.AudioLevel(Mic), level.JournalTarget);
        Assert.True(level.PerUser);
        Assert.Equal("75 %", level.CurrentText);
        Assert.Contains("−16…+15 дБ", level.Hint); // the endpoint's own range, not a hard-coded one
        Assert.Equal(AudioProvider.ToolId(Mic), level.Tool!.Id);
        Assert.Equal("ПРОВЕРИТЬ", level.Tool.ButtonText);

        var mute = Row(mic, "mic-mute");
        Assert.Equal(SettingControl.Toggle, mute.Control);
        Assert.Equal(DeviceTargets.AudioMute(Mic), mute.JournalTarget); // a separate target: undo restores only its own part
        Assert.True(mute.PerUser);
        Assert.Equal("выкл", mute.CurrentText);

        Assert.All(mic.Settings.Concat(Provider().Build(OwnerPc(), Ctx())[0].Settings), s => Assert.Null(DeviceScan.HintProblem(s)));
        Assert.Equal(["USB", "моно", "уровень меняется в самом микрофоне"], mic.Facts);
        Assert.Empty(mic.Tools); // the measurement lives in its setting's row
        Assert.Equal(0, _captures); // building cards never opens a capture stream
    }

    [Fact]
    public void Hardware_refs_carry_vid_pid_container_and_instance()
    {
        var cards = Provider().Build(OwnerPc(), Ctx());
        var headset = Assert.Single(cards[0].Hardware);
        Assert.Equal(("054C", "0F80"), (headset.Vid, headset.Pid));
        var mic = Assert.Single(cards[1].Hardware);
        Assert.Equal(("1532", "056A", "{11111111-2222-3333-4444-555555555555}"), (mic.Vid, mic.Pid, mic.ContainerId));
        Assert.Equal([@"USB\VID_1532&PID_056A&MI_00\7&2B3C4D5E&0&0000"], mic.InstanceIds);
    }

    [Fact]
    public async Task Privacy_deny_adds_a_note_and_disables_the_level_test()
    {
        var provider = new AudioProvider
        {
            ReadSnapshot = () => OwnerPc(privacy: MicPrivacy.Deny),
            ReadLevel = _ => new EndpointLevel(0.75f, 5.6f, -16, 15, false),
            ReadPrivacy = () => MicPrivacy.Deny,
            Capture = (_, _, _, _) =>
            {
                _captures++;
                throw new InvalidOperationException("must not capture");
            },
        };
        var results = new DeviceResultStore();
        var mic = provider.Build(OwnerPc(privacy: MicPrivacy.Deny), Ctx(results))[1];

        var note = Assert.Single(mic.Notes);
        Assert.Equal("ms-settings:privacy-microphone", note.Link!.Target);
        var tool = Row(mic, "mic-level").Tool!;
        Assert.StartsWith("Недоступно", tool.Description);

        await RunTool(mic, tool, results);

        Assert.Equal(0, _captures);
        Assert.Contains("запрещён", tool.ResultText);
    }

    [Fact]
    public void Another_account_disables_the_per_user_rows()
    {
        var mic = Provider().Build(OwnerPc(), Ctx(interactive: null))[1];
        Assert.NotNull(Row(mic, "mic-level").DisabledReason);
        Assert.NotNull(Row(mic, "mic-mute").DisabledReason);
        Assert.DoesNotContain(DevicePlan.Build([mic], interactiveUser: false), p => p.Selectable);
    }

    // ───────────────────────────── Measure → recommend → apply → re-measure ─────────────────────────────

    [Fact]
    public void Without_a_measurement_the_main_microphone_asks_for_one()
    {
        var cards = Provider().Build(OwnerPc(), Ctx());
        var rec = Row(cards[1], "mic-level").Recommended!;
        Assert.True(rec.NeedsMeasurement);
        Assert.Equal(new DeviceLink(DeviceLinkKind.Tool, "К ПРОВЕРКЕ", "audio:" + Mic, AudioProvider.ToolId(Mic)), rec.Link);
        var item = Assert.Single(DevicePlan.Build(cards, true));
        Assert.Equal(PlanItemKind.Measurement, item.Kind);
        Assert.Equal("рек.: сначала измерьте", Row(cards[1], "mic-level").RecommendedText);
    }

    [Fact]
    public async Task A_quiet_test_recommends_the_absolute_level_and_stages_it()
    {
        var results = new DeviceResultStore();
        var provider = Provider();
        var mic = provider.Build(OwnerPc(), Ctx(results))[1];
        var level = Row(mic, "mic-level");

        await RunTool(mic, level.Tool!, results);

        Assert.Equal(1, _captures);
        Assert.Equal("Тихо: пики −24 дБFS", level.Tool!.ResultText);
        var measurement = results.Get<MicMeasurement>(AudioProvider.ResultKey(Mic));
        Assert.NotNull(measurement);
        Assert.Equal(15, measurement!.TargetDb);
        Assert.Equal(5.6, measurement.MeasuredAtDb!.Value, 3);

        var rec = level.Recommended!;
        Assert.False(rec.NeedsMeasurement);
        Assert.Equal(RecommendPolicy.Suggest, rec.Policy);
        Assert.True(MicLevelValue.TryDb(rec.Value, out var db) && db == 15);
        Assert.Equal(rec.Value, level.Staged); // ПРИМЕНИТЬ is ready with the recommended level
        Assert.True(level.IsDirty);
        var item = Assert.Single(DevicePlan.Build([mic], true));
        Assert.Equal(PlanItemKind.Setting, item.Kind);
        Assert.Equal(("75 %", "≈100 %"), (item.From, item.To));

        // The same level after a quiet re-scan: the same recommendation, not a repeated delta.
        var again = provider.Build(OwnerPc(), Ctx(results))[1];
        Assert.Equal(rec, Row(again, "mic-level").Recommended);
        Assert.Equal(level.Tool.ResultText, Row(again, "mic-level").Tool!.ResultText);

        // Once the live level is the target (within 0,5 dB), the row leaves the plan.
        level.Current = MicLevelValue.Of(0.99f, 14.8f);
        Assert.Empty(DevicePlan.Build([mic], true));
    }

    [Fact]
    public async Task A_level_changed_since_the_test_drops_the_stale_suggestion()
    {
        var results = new DeviceResultStore();
        var mic = Provider().Build(OwnerPc(), Ctx(results))[1];
        var tool = Row(mic, "mic-level").Tool!;
        await RunTool(mic, tool, results);

        // The user moved the level in Windows: +10 dB.
        var moved = Provider().Build(OwnerPc(mic: RazerMic(scalar: 0.95f, db: 12f)), Ctx(results))[1];

        Assert.Null(results.Get<MicMeasurement>(AudioProvider.ResultKey(Mic)));
        var rec = Row(moved, "mic-level").Recommended!;
        Assert.True(rec.NeedsMeasurement);
        Assert.Contains("изменён", rec.Reason);
        Assert.Contains("изменён", Row(moved, "mic-level").Tool!.ResultText);
        Assert.Contains("изменён", tool.ResultText); // the old (live) tool line says so too
    }

    [Fact]
    public async Task Applying_the_recommendation_writes_decibels_through_the_journal_and_asks_to_measure_again()
    {
        _audio.Endpoints[Mic] = (0.75f, false);
        var results = new DeviceResultStore();
        var mic = Provider().Build(OwnerPc(), Ctx(results))[1];
        var level = Row(mic, "mic-level");
        await RunTool(mic, level.Tool!, results);

        var journal = ChangeJournal.ForTests();
        var session = await ChangeSession.BeginAsync("test", createRestorePoint: false, journal: journal);
        var outcome = await level.Apply!(session, level.Recommended!.Value, CancellationToken.None);
        session.Complete();

        Assert.Equal(ApplyOutcome.Applied, outcome);
        Assert.Equal(0.875f, _audio.Endpoints[Mic].Scalar); // FakeAudioWriter.SetDb: (15 + 20) / 40
        var record = Assert.Single(journal.ActiveRecords);
        Assert.Equal(DeviceTargets.AudioLevel(Mic), record.Target);
        Assert.Equal(DeviceTargets.Source, record.Source);
        Assert.Equal(AudioComponent.Level, record.Audio!.Component);
        Assert.Null(JournalTrust.Check(record));
        Assert.Null(results.Get<MicMeasurement>(AudioProvider.ResultKey(Mic))); // no stale repeat
        Assert.Equal("Уровень изменён — проверьте ещё раз.", level.Tool!.ResultText);
        Assert.True(level.Recommended!.NeedsMeasurement);
    }

    [Fact]
    public async Task A_slider_value_is_written_as_a_scalar_and_mute_is_its_own_record()
    {
        _audio.Endpoints[Mic] = (0.75f, false);
        var mic = Provider().Build(OwnerPc(), Ctx())[1];
        var journal = ChangeJournal.ForTests();
        var session = await ChangeSession.BeginAsync("test", createRestorePoint: false, journal: journal);

        Assert.Equal(ApplyOutcome.Applied, await Row(mic, "mic-level").Apply!(session, DeviceValue.Of(40), CancellationToken.None));
        Assert.Equal(ApplyOutcome.NoChange, await Row(mic, "mic-level").Apply!(session, DeviceValue.Of(40), CancellationToken.None));
        Assert.Equal(ApplyOutcome.Applied, await Row(mic, "mic-mute").Apply!(session, DeviceValue.Of(true), CancellationToken.None));
        session.Complete();

        Assert.Equal((0.40f, true), _audio.Endpoints[Mic]);
        Assert.Equal(
            [DeviceTargets.AudioLevel(Mic), DeviceTargets.AudioMute(Mic)],
            journal.ActiveRecords.Select(r => r.Target).OrderBy(t => t, StringComparer.Ordinal));
        Assert.All(journal.ActiveRecords, r => Assert.Null(JournalTrust.Check(r)));
    }

    [Fact]
    public async Task A_test_in_range_recommends_keeping_the_level()
    {
        var results = new DeviceResultStore();
        var provider = Provider(capture: Bursts(-9, 5));
        var mic = provider.Build(OwnerPc(), Ctx(results))[1];
        var level = Row(mic, "mic-level");

        await RunTool(mic, level.Tool!, results);

        Assert.StartsWith("В норме", level.Tool!.ResultText);
        Assert.Null(level.Staged);
        Assert.True(level.MatchesRecommendation);
        Assert.Empty(DevicePlan.Build([mic], true));
    }

    [Fact]
    public async Task A_muted_microphone_is_not_measured()
    {
        var results = new DeviceResultStore();
        var provider = Provider(level: _ => new EndpointLevel(0.75f, 5.6f, -16, 15, true));
        var mic = provider.Build(OwnerPc(), Ctx(results))[1];
        var tool = Row(mic, "mic-level").Tool!;

        await RunTool(mic, tool, results);

        Assert.Equal(0, _captures);
        Assert.Contains("Без звука", tool.ResultText);
    }

    [Fact]
    public void Role_tags()
    {
        Assert.Equal("по умолчанию · связь", AudioProvider.RoleTags(RazerMic()));
        Assert.Equal("связь", AudioProvider.RoleTags(Capture(Mic, "x", comms: true)));
        Assert.Null(AudioProvider.RoleTags(Capture(Mic, "x")));
    }
}
