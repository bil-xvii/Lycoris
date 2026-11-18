using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Core.Assistant;
using Lycoris.Modules.Assistant;
using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Shell;
using Lycoris.Tweaks;

namespace Lycoris.Tests;

public class AssistantDiagnosticsTests
{
    private static readonly DateTime Observed = new(2026, 11, 18, 17, 12, 0);

    private static HealthCheckResult GameMode(bool enabled = false) =>
        HealthRules.CheckGameMode(new SystemSnapshot { GameModeOn = enabled })!;

    private static AssistantDiagnosticSnapshot Snapshot(HealthCheckResult check, bool verified = true) =>
        new(Observed, [check], verified ? new HashSet<string> { check.Id } : new HashSet<string>());

    private static Func<Task<AssistantDiagnosticSnapshot?>> Read(AssistantDiagnosticSnapshot? snapshot) =>
        () => Task.FromResult(snapshot);

    private static AssistantViewModel Offline(Func<Task<AssistantDiagnosticSnapshot?>> refresh,
        Action<string, string?>? open = null, Func<string, Func<string, bool>, Task<AssistantFixResult>>? fix = null) =>
        new(refresh, fix ?? ((_, _) => throw new InvalidOperationException("Unexpected fix")),
            open ?? ((_, _) => throw new InvalidOperationException("Unexpected navigation")),
            () => throw new InvalidOperationException("Unexpected chat context"))
        {
            State = AssistantState.NoOllama,
        };

    [Theory]
    [InlineData("ru", "Объясни найденные проблемы", "Предупреждения по результатам проверок", "Рекомендации по результатам проверок", "Источник — проверка Lycoris от")]
    [InlineData("en", "Explain the problems found", "Warnings from the checks", "Recommendations from the checks", "Source — Lycoris check on")]
    public async Task The_explanation_preset_works_offline_and_only_opens_places_from_actual_findings(
        string language, string preset, string warnings, string recommendations, string source)
    {
        using var loc = Loc.UseLanguage(language);
        var facts = new SystemSnapshot { StartupCount = 17, ProblemDevices = [], WindowedGamesOptimization = false };
        var startup = HealthRules.CheckStartup(facts)!;
        var windowedGames = HealthRules.CheckWindowedGames(facts)!;
        var healthyDevices = HealthRules.CheckDevices(facts)!;
        var fresh = new AssistantDiagnosticSnapshot(Observed, [startup, windowedGames, healthyDevices], new HashSet<string>());
        var opened = new List<(string Page, string? Focus)>();
        var vm = Offline(Read(fresh), (page, focus) => opened.Add((page, focus)));
        var older = GameMode();
        vm.ShowDiagnostics(Snapshot(older));

        Assert.Contains(preset, vm.QuickQuestions);
        Assert.True(vm.AskCommand.CanExecute(preset));
        Assert.False(vm.AskCommand.CanExecute(vm.QuickQuestions[0]));
        vm.Question = preset;
        Assert.False(vm.AskCommand.CanExecute(null)); // Typed questions still require the local model.
        await vm.AskCommand.ExecuteAsync(preset);

        var reply = vm.Messages[1];
        Assert.Contains(warnings + ":\n• " + startup.Title, reply.Text);
        Assert.Contains(startup.Detail, reply.Text);
        Assert.Contains(recommendations + ":\n• " + windowedGames.Title, reply.Text);
        Assert.DoesNotContain(healthyDevices.Title, reply.Text);
        Assert.DoesNotContain(older.Title, reply.Text);
        Assert.Equal($"{source} {Loc.DateTimeText(Observed)}", reply.Note);
        foreach (var action in reply.Actions) vm.OpenPlaceCommand.Execute(action);
        Assert.Equal([("services", "tab:startup"), ("optimize", HealthRules.TweakWindowedGames)], opened);
    }

    [Fact]
    public async Task The_explanation_preset_refreshes_every_time_and_reports_unavailable_facts()
    {
        using var loc = Loc.UseLanguage("en");
        var observations = new Queue<AssistantDiagnosticSnapshot?>();
        observations.Enqueue(Snapshot(GameMode()));
        observations.Enqueue(Snapshot(GameMode(enabled: true)) with { ObservedAt = Observed.AddMinutes(5) });
        observations.Enqueue(null);
        var vm = Offline(() => Task.FromResult(observations.Dequeue()));
        const string preset = "Explain the problems found";

        await vm.AskCommand.ExecuteAsync(preset);
        Assert.Single(vm.Messages[^1].Actions);
        await vm.AskCommand.ExecuteAsync(preset);
        Assert.Equal("The available checks did not find any warnings.", vm.Messages[^1].Text);
        Assert.EndsWith(Loc.DateTimeText(Observed.AddMinutes(5)), vm.Messages[^1].Note);
        Assert.Empty(vm.Messages[^1].Actions);
        await vm.AskCommand.ExecuteAsync(preset);
        Assert.StartsWith("Fresh results are unavailable.", vm.Messages[^1].Text);
        Assert.Null(vm.Messages[^1].Note);
        Assert.Empty(vm.Messages[^1].Actions);
        Assert.Empty(observations);
    }

    [Fact]
    public async Task Assistant_awaits_the_dashboard_checks_already_in_progress()
    {
        var collect = new TaskCompletionSource<SystemSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var dashboard = new DashboardViewModel { Coordinator = new(), InspectAsync = () => { calls++; return collect.Task; } };
        var dashboardRefresh = dashboard.RefreshChecksCommand.ExecuteAsync(null);
        var assistantRefresh = dashboard.RefreshForAssistantAsync();

        Assert.Same(assistantRefresh, dashboard.RefreshForAssistantAsync());
        Assert.False(assistantRefresh.IsCompleted);
        Assert.Equal(1, calls);

        collect.SetResult(new SystemSnapshot { Now = Observed, GameModeOn = false });
        var fresh = await assistantRefresh;
        await dashboardRefresh;
        Assert.NotNull(fresh);
        Assert.Equal(Observed, fresh.ObservedAt);
        Assert.Contains(fresh.Checks, c => c.Id == "game-mode" && c.Severity == CheckSeverity.Warning);
        Assert.Contains("game-mode", fresh.VerifiedFixIds);
    }

    [Fact]
    public async Task A_refused_or_failed_refresh_does_not_reuse_the_last_dashboard_observation()
    {
        var calls = 0;
        var dashboard = new DashboardViewModel
        {
            Coordinator = new(),
            InspectAsync = () => ++calls == 1
                ? Task.FromResult(new SystemSnapshot { Now = Observed, GameModeOn = false })
                : Task.FromException<SystemSnapshot>(new InvalidOperationException("Probe failed")),
        };
        Assert.NotNull(await dashboard.RefreshForAssistantAsync());
        Assert.True(dashboard.HasResults);

        dashboard.IsBusy = true;
        Assert.Null(await dashboard.RefreshAssistantContextAsync());
        Assert.Equal(1, calls);

        dashboard.IsBusy = false;
        Assert.Null(await dashboard.RefreshAssistantContextAsync());
        Assert.Equal(2, calls);
        Assert.True(dashboard.HasResults);
    }

    [Fact]
    public async Task The_fix_recheck_runs_inside_the_journal_lease_while_the_dashboard_is_busy()
    {
        var coordinator = new OperationCoordinator(lowPriorityCap: 1);
        var dashboard = new DashboardViewModel
        {
            Coordinator = coordinator,
            IsBusy = true,
            InspectAsync = () => Task.FromResult(new SystemSnapshot { Now = Observed, GameModeOn = false }),
        };
        var refresh = typeof(DashboardViewModel).GetMethod("RefreshAfterBoundaryAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(refresh);

        // An independent caller already holds the only ordinary measurement slot.
        using (await coordinator.AcquireAsync(new OperationRequest(OperationClass.Measure, "Independent measurement")))
        {
            var fresh = await coordinator.RunAsync(new OperationRequest(OperationClass.JournalChange, "Test journal lease"), async lease =>
            {
                Assert.True(lease.IsActive);
                var check = Assert.IsAssignableFrom<Task<AssistantDiagnosticSnapshot?>>(refresh.Invoke(dashboard, null));
                return await check.WaitAsync(TimeSpan.FromSeconds(5));
            });

            Assert.NotNull(fresh);
            Assert.Equal(Observed, fresh.ObservedAt);
            Assert.Contains("game-mode", fresh.VerifiedFixIds);
            Assert.Equal(OperationClass.Measure, Assert.Single(coordinator.Snapshot()).Class);
        }

        Assert.False(dashboard.IsChecking);
        Assert.Empty(coordinator.Snapshot());
    }

    [Theory]
    [InlineData("ru", "Внимание", "Совет", "Проверка от", "Открыть очистку")]
    [InlineData("en", "Attention", "Recommendation", "Checked on", "Open cleanup")]
    public async Task Offline_guidance_shows_observed_time_and_opens_cleanup_without_executing_it(
        string language, string warning, string advice, string timestampPrefix, string openCleanup)
    {
        using var loc = Loc.UseLanguage(language);
        var disk = new HealthCheckResult("disk-space", "Space", "11 GB of 300 GB", CheckSeverity.Warning, 12,
            new(CheckActionKind.Navigate, "Clean", "cleaner"));
        var optional = HealthRules.CheckWindowedGames(new SystemSnapshot { WindowedGamesOptimization = false })!;
        var snapshot = new AssistantDiagnosticSnapshot(Observed, [optional, disk], new HashSet<string>());
        (string, string?) opened = default;
        var vm = Offline(Read(snapshot), (page, focus) => opened = (page, focus));

        Assert.True(vm.RefreshDiagnosticsCommand.CanExecute(null));
        await vm.RefreshDiagnosticsCommand.ExecuteAsync(null);

        Assert.Equal(AssistantState.NoOllama, vm.State);
        Assert.True(vm.HasDiagnostics);
        Assert.Equal($"{timestampPrefix} {Loc.DateTimeText(Observed)}", vm.DiagnosticsTimestamp);
        Assert.Collection(vm.DiagnosticCards,
            card =>
            {
                Assert.Equal(warning, card.SeverityLabel);
                Assert.Equal(disk.Detail, card.Detail);
                Assert.True(card.CanOpen);
                Assert.False(card.CanFix);
                Assert.Equal(openCleanup, card.OpenLabel);
                vm.OpenDiagnosticCommand.Execute(card);
                Assert.Equal(("cleaner", (string?)null), opened);
            },
            card =>
            {
                Assert.Equal(advice, card.SeverityLabel);
                Assert.True(card.CanOpen);
                Assert.False(card.CanFix);
            });
    }

    [Fact]
    public async Task A_failed_offline_refresh_clears_cards_and_the_timestamp()
    {
        using var loc = Loc.UseLanguage("en");
        AssistantDiagnosticSnapshot? next = Snapshot(GameMode());
        var vm = Offline(() => Task.FromResult<AssistantDiagnosticSnapshot?>(next));
        await vm.RefreshDiagnosticsCommand.ExecuteAsync(null);
        Assert.Equal("Turn on Game Mode", Assert.Single(vm.DiagnosticCards).FixLabel);

        next = null;
        await vm.RefreshDiagnosticsCommand.ExecuteAsync(null);

        Assert.False(vm.HasDiagnostics);
        Assert.Empty(vm.DiagnosticCards);
        Assert.Empty(vm.DiagnosticsTimestamp);
        Assert.Contains("Fresh results are unavailable", vm.DiagnosticStatus);
    }

    [Theory]
    [InlineData("unknown", CheckSeverity.Warning, HealthRules.TweakGameMode, true)]
    [InlineData("game-mode", CheckSeverity.Ok, HealthRules.TweakGameMode, true)]
    [InlineData("game-mode", CheckSeverity.Info, HealthRules.TweakGameMode, true)]
    [InlineData("game-mode", CheckSeverity.Warning, HealthRules.TweakGameMode, false)]
    [InlineData("game-mode", CheckSeverity.Warning, HealthRules.TweakHibernationOff, true)]
    [InlineData("hags", CheckSeverity.Warning, HealthRules.TweakHagsOther, true)]
    public async Task A_missing_ok_unverified_or_mismatched_check_cannot_reach_apply(
        string checkId, CheckSeverity severity, string target, bool verified)
    {
        var check = GameMode() with { Id = checkId, Severity = severity, Action = new(CheckActionKind.ApplyTweak, "Apply", target) };
        var id = checkId == "hags" ? "hags" : "game-mode";
        var result = await AssistantDiagnostics.FixAsync(id, Read(Snapshot(check, verified)), Read(null),
            (_, _) => throw new InvalidOperationException("Unexpected fix"));

        Assert.Equal(AssistantFixState.NoLongerEligible, result.State);
    }

    [Fact]
    public async Task An_unavailable_precheck_never_uses_a_previously_visible_fix()
    {
        var result = await AssistantDiagnostics.FixAsync("game-mode", Read(null), Read(null),
            (_, _) => throw new InvalidOperationException("Unexpected fix"));
        Assert.Equal(AssistantFixState.RefreshUnavailable, result.State);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Hags_fix_requires_a_fresh_positive_driver_capability_even_if_the_catalog_allows_it(
        bool? latestCapability, bool expected)
    {
        var facts = new SystemSnapshot
        {
            Hags = HagsState.Off,
            PrimaryGpu = new("NVIDIA GeForce RTX 5070 Ti", GpuVendor.Nvidia, "", null, 16UL << 30, ""),
            PrimaryGpuSupportsHags = latestCapability,
        };
        var check = HealthRules.CheckHags(facts)!;
        var eligible = DashboardViewModel.IsAssistantFixApplicable(check, facts, _ => true);

        Assert.Equal(CheckSeverity.Warning, check.Severity);
        Assert.Equal(expected, eligible);
        var card = Assert.Single(AssistantDiagnostics.Cards(Snapshot(check, verified: eligible)));
        Assert.True(card.CanOpen);
        Assert.Equal(expected, card.CanFix);
    }

    [Fact]
    public void A_configured_trim_policy_blocks_the_fix_even_if_the_catalog_allows_it()
    {
        var facts = new SystemSnapshot
        {
            TrimDisabled = true,
            SystemDisk = new SystemDisk("Test SSD", "SSD", "NVMe", 30, 100),
            TrimPolicyAbsent = false,
        };

        Assert.False(DashboardViewModel.IsAssistantFixApplicable(HealthRules.CheckTrim(facts)!, facts, _ => true));
    }

    [Fact]
    public async Task The_executor_rechecks_the_same_warning_before_writing_after_confirmation()
    {
        var result = await AssistantDiagnostics.FixAsync("game-mode", Read(Snapshot(GameMode())),
            Read(Snapshot(GameMode(enabled: true))), async (tweak, checkCurrent) =>
            {
                Assert.Equal(HealthRules.TweakGameMode, tweak.Id);
                Assert.False(await checkCurrent());
                return null;
            });

        Assert.Equal(AssistantFixState.NoLongerEligible, result.State);
        Assert.Equal(CheckSeverity.Ok, Assert.Single(result.Snapshot!.Checks).Severity);
    }

    [Theory]
    [InlineData(1, RestartRequirement.None, false, true, nameof(AssistantFixState.Resolved))]
    [InlineData(1, RestartRequirement.None, false, false, nameof(AssistantFixState.Unverified))]
    [InlineData(1, RestartRequirement.Reboot, false, true, nameof(AssistantFixState.PendingRestart))]
    [InlineData(1, RestartRequirement.None, true, true, nameof(AssistantFixState.Failed))]
    [InlineData(0, RestartRequirement.None, false, true, nameof(AssistantFixState.NotApplied))]
    public async Task A_fix_is_verified_only_when_a_write_succeeded_and_the_same_check_passed(
        int applied, RestartRequirement restart, bool errors, bool hasPostcheck, string expected)
    {
        var warning = Snapshot(GameMode());
        var reads = 0;
        var result = await AssistantDiagnostics.FixAsync("game-mode", Read(warning),
            () => Task.FromResult(++reads == 1 ? warning : hasPostcheck ? Snapshot(GameMode(enabled: true)) : null),
            async (_, checkCurrent) =>
            {
                Assert.True(await checkCurrent());
                return new TweakRunResult(applied, 0, errors ? ["failed"] : [], null, restart);
            });

        Assert.Equal(expected, result.State.ToString());
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Cancellation_cannot_report_a_verified_fix()
    {
        var result = await AssistantDiagnostics.FixAsync("game-mode", Read(Snapshot(GameMode())),
            () => throw new InvalidOperationException("Unexpected recheck"), (_, _) => Task.FromResult<TweakRunResult?>(null));
        Assert.Equal(AssistantFixState.NotApplied, result.State);
    }

    [Fact]
    public async Task Assistant_admin_replay_accepts_only_a_known_check_id()
    {
        var calls = new List<string>();
        var vm = Offline(Read(null), fix: (id, _) =>
        {
            calls.Add(id);
            return Task.FromResult(new AssistantFixResult(AssistantFixState.NoLongerEligible, Snapshot(GameMode(enabled: true))));
        });
        var bad = new AdminIntent("assistant", "RunCheck", "cleaner");
        var known = new AdminIntent("assistant", "RunCheck", "game-mode");

        var rejected = Assert.IsAssignableFrom<IAsyncRelayCommand>(bad.FindCommand(vm));
        Assert.False(rejected.CanExecute(bad.Parameter));
        await rejected.ExecuteAsync(bad.Parameter);
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(known.FindCommand(vm)).ExecuteAsync(known.Parameter);

        Assert.Equal(["game-mode"], calls);
        Assert.Empty(vm.DiagnosticCards);
        Assert.DoesNotContain("подтверждено повторной проверкой", vm.DiagnosticStatus);
    }
}
