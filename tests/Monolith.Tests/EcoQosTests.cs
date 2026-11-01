using Monolith.Core;
using Monolith.Modules.Games;

namespace Monolith.Tests;

/// <summary>
/// Efficiency mode (EcoQoS) for the Game Boost close list — docs/research/gaming_tweaks.md §2 H `boost-ecoqos-close-list`.
/// Pure rules only: exclusions, the crash-marker JSON and its pid + start-time match, the exact restore plan, the texts.
/// Nothing here opens a process or touches Windows.
/// </summary>
public sealed class EcoQosTests
{
    private static readonly IReadOnlySet<int> NoAudio = new HashSet<int>();

    // ───────────────────────────── Exclusions ─────────────────────────────

    [Theory]
    [InlineData("Discord")]
    [InlineData("discord.exe")]
    [InlineData(@"C:\Users\x\AppData\Local\Discord\app-1.0\Discord.exe")]
    [InlineData("DiscordCanary")]
    [InlineData("TeamSpeak3")]
    [InlineData("ts3client_win64")]
    [InlineData("obs64")]
    [InlineData("obs-browser-page")]
    [InlineData("Streamlabs OBS")]
    [InlineData("NVIDIA Share")]
    [InlineData("NVIDIA Broadcast")]
    [InlineData("nvcontainer")]
    [InlineData("voicemeeter8x64")]
    [InlineData("VoiceMeeterPro")]
    [InlineData("WaveLink")]
    [InlineData("Sonar")]
    [InlineData("NahimicService")]
    [InlineData("RTSS")]
    [InlineData("MSIAfterburner")]
    [InlineData("lghub_agent")]
    [InlineData("RazerCentralService")]
    [InlineData("iCUE")]
    [InlineData("SteelSeriesGG")]
    [InlineData("Zoom")]
    [InlineData("ms-teams")]
    [InlineData("Slack")]
    [InlineData("audiodg")]
    public void Voice_stream_audio_overlay_and_peripheral_software_is_never_throttled(string name) =>
        Assert.True(UserProcesses.IsNeverThrottled(name));

    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("Spotify")]
    [InlineData("Telegram")]
    [InlineData("OneDrive")]
    [InlineData("Obsidian")]
    [InlineData("Code")]
    public void Ordinary_close_list_apps_may_be_throttled(string name)
    {
        Assert.False(UserProcesses.IsNeverThrottled(name));
        Assert.Null(EcoQos.ExclusionReason(name, 4242, selfPid: 1000, NoAudio));
    }

    [Fact]
    public void Exclusion_reason_names_the_cause()
    {
        Assert.Contains("звук", EcoQos.ExclusionReason("Discord", 10, 1, NoAudio)!);
        Assert.Contains("MONOLITH", EcoQos.ExclusionReason("chrome", 1, selfPid: 1, NoAudio)!);
        Assert.Contains("MONOLITH", EcoQos.ExclusionReason("MONOLITH", 55, selfPid: 1, NoAudio)!);
        Assert.Contains("античит", EcoQos.ExclusionReason("EasyAntiCheat_EOS", 77, 1, NoAudio)!);
        Assert.Contains("игры", EcoQos.ExclusionReason("UnityCrashHandler64", 78, 1, NoAudio)!);
        Assert.Contains("системный", EcoQos.ExclusionReason("explorer", 79, 1, NoAudio)!);
        Assert.NotNull(EcoQos.ExclusionReason("", 80, 1, NoAudio));
    }

    [Fact]
    public void A_process_with_an_active_audio_session_is_skipped()
    {
        var audible = new HashSet<int> { 4242 };
        Assert.Contains("звук", EcoQos.ExclusionReason("Spotify", 4242, 1, audible)!);
        Assert.Null(EcoQos.ExclusionReason("Spotify", 4243, 1, audible));
    }

    [Fact]
    public void Protected_names_stay_protected_and_the_never_throttle_set_is_normalised() =>
        Assert.All(UserProcesses.NeverThrottle, n => Assert.Equal(n, UserProcesses.NormalizeName(n)));

    // ───────────────────────────── Throttling state ─────────────────────────────

    [Fact]
    public void Eco_state_is_the_documented_sample_and_system_managed_is_zero_masks()
    {
        Assert.Equal(new PowerThrottlingState(1, 1, 1), EcoQos.Eco);
        Assert.Equal(new PowerThrottlingState(1, 0, 0), EcoQos.SystemManaged);
        Assert.True(EcoQos.IsEco(EcoQos.Eco));
        Assert.False(EcoQos.IsEco(EcoQos.SystemManaged));
        Assert.False(EcoQos.IsEco(new PowerThrottlingState(1, 1, 0))); // explicit HighQoS
    }

    [Fact]
    public void A_process_that_controls_execution_speed_itself_is_self_managed()
    {
        Assert.True(EcoQos.IsSelfManaged(new PowerThrottlingState(1, 1, 1)));  // Edge/Chrome: EcoQoS on
        Assert.True(EcoQos.IsSelfManaged(new PowerThrottlingState(1, 1, 0)));  // explicit HighQoS
        Assert.False(EcoQos.IsSelfManaged(new PowerThrottlingState(1, 0, 0)));
        Assert.False(EcoQos.IsSelfManaged(new PowerThrottlingState(1, 4, 4))); // only the timer-resolution bit: fine to tag
    }

    [Fact]
    public void Restore_plan_is_the_original_state_not_zeros()
    {
        var high = new PowerThrottlingState(1, 1, 0);
        Assert.Equal(high, EcoQos.PlanRestore(high));
        var timer = new PowerThrottlingState(1, 4, 4);
        Assert.Equal(timer, EcoQos.PlanRestore(timer));
        Assert.Equal(EcoQos.SystemManaged, EcoQos.PlanRestore(new PowerThrottlingState(1, 0, 0)));
        // An odd version from the read is normalised; the masks are kept exactly.
        Assert.Equal(new PowerThrottlingState(1, 4, 0), EcoQos.PlanRestore(new PowerThrottlingState(0, 4, 0)));
    }

    // ───────────────────────────── Crash marker ─────────────────────────────

    [Fact]
    public void Marker_round_trips_pid_and_start_time_exactly()
    {
        var t1 = new DateTime(2026, 11, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567);
        var t2 = new DateTime(2026, 11, 1, 12, 35, 0, DateTimeKind.Utc);
        var json = EcoQos.SerializeMarker([new EcoQosMarkerEntry(4242, t1), new EcoQosMarkerEntry(777, t2)]);
        Assert.Contains("\"pid\": 4242", json);
        Assert.Contains("\"startTimeUtc\"", json);
        var back = EcoQos.ParseMarker(json);
        Assert.Equal(2, back.Count);
        Assert.Equal(4242, back[0].Pid);
        Assert.Equal(t1, back[0].StartTimeUtc);
        Assert.Equal(DateTimeKind.Utc, back[0].StartTimeUtc.Kind);
        Assert.Equal(777, back[1].Pid);
        Assert.Equal(t2, back[1].StartTimeUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"pid\": 1}")]
    [InlineData("[{\"pid\": \"x\"}]")]
    [InlineData("[{\"pid\": 4, \"startTimeUtc\": \"2026-11-01T12:00:00Z\"}]")]
    [InlineData("[{\"pid\": 4242}]")]
    public void Marker_is_untrusted_input_garbage_and_system_pids_are_dropped(string json) =>
        Assert.Empty(EcoQos.ParseMarker(json));

    [Fact]
    public void Marker_matches_a_process_only_by_pid_and_start_time_together()
    {
        var start = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var e = new EcoQosMarkerEntry(4242, start);
        Assert.True(EcoQos.Matches(e, 4242, start));
        Assert.False(EcoQos.Matches(e, 4243, start));                       // another process
        Assert.False(EcoQos.Matches(e, 4242, start.AddMilliseconds(1)));   // the PID was reused after a restart
        Assert.True(EcoQos.Matches(e, 4242, start.ToLocalTime()));          // kind-insensitive compare
    }

    // ───────────────────────────── Texts ─────────────────────────────

    [Fact]
    public void Describe_names_efficiency_mode_and_the_exclusions_without_figures()
    {
        var plan = new BoostPlan(PowerPlanAction.SkipDisabled, "План питания не меняется (выключено).",
            [new RunningApp("chrome", "", 12, 0, true), new RunningApp("Telegram", "", 1, 0, true)], [], false, false, BoostCloseMode.EcoQos);
        var line = plan.DescribeCloseList();
        Assert.StartsWith("Режим эффективности для: chrome (12), Telegram.", line);
        Assert.Contains("не закрываются", line);
        Assert.Contains("звук и голосовые программы исключены", line);
        Assert.Contains("После игры режим возвращается", line);
        Assert.DoesNotContain("%", line);
        Assert.DoesNotContain("76", line);
        Assert.Contains(plan.Describe(null), l => l == line);
    }

    [Fact]
    public void Describe_with_an_empty_list_says_nobody_gets_the_mode()
    {
        var empty = new BoostPlan(PowerPlanAction.SkipDisabled, "x", [], [], false, false, BoostCloseMode.EcoQos);
        Assert.Contains("список приложений пуст", empty.DescribeCloseList());
        Assert.DoesNotContain("закрыва", empty.DescribeCloseList());

        var notRunning = new BoostPlan(PowerPlanAction.SkipDisabled, "x", [], ["Spotify"], false, false, BoostCloseMode.EcoQos);
        Assert.Contains("не запущены", notRunning.DescribeCloseList());
        Assert.Contains("режим эффективности", notRunning.DescribeCloseList());

        // The default mode keeps the old texts exactly.
        var close = new BoostPlan(PowerPlanAction.SkipDisabled, "x", [], [], false, false);
        Assert.Equal(BoostCloseMode.Close, close.CloseMode);
        Assert.Equal("Приложения не закрываются (список закрытия пуст).", close.DescribeCloseList());
    }

    [Fact]
    public void Close_mode_setting_defaults_to_closing_and_tolerates_unknown_values()
    {
        var s = new AppSettings();
        Assert.Equal(AppSettings.CloseModeClose, s.GameBoostCloseMode);
        Assert.False(s.GameBoostUsesEcoQos);
        s.GameBoostCloseMode = "ecoqos";
        Assert.True(s.GameBoostUsesEcoQos);
        s.GameBoostCloseMode = "SomethingNewer";
        Assert.False(s.GameBoostUsesEcoQos);
    }

    [Fact]
    public void Efficiency_caption_promises_no_numbers()
    {
        Assert.DoesNotContain("%", GamesViewModel.CloseModeEcoQosCaption);
        Assert.Contains("Диспетчере задач", GamesViewModel.CloseModeEcoQosCaption);
        Assert.Contains("без снижения приоритета", GamesViewModel.CloseModeEcoQosCaption);
    }
}
