using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Core.Assistant;
using Lycoris.Modules.Cleaner;
using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Modules.Dashboard.Doctor;
using Lycoris.Modules.Home;
using Lycoris.Modules.Maintenance;
using Lycoris.Modules.Optimize;
using Lycoris.Modules.Tools;
using Lycoris.Tweaks;

namespace Lycoris.Tests;

public sealed class DashboardAdviceActionTests
{
    private const ulong Gb = 1UL << 30;

    private static CheckItemViewModel Row(HealthCheckResult check, Func<CheckAction, string?>? label = null) =>
        new(check, _ => throw new InvalidOperationException("Unexpected primary action"), label);

    [Fact]
    public async Task Opening_a_preview_with_a_known_cleanup_size_only_navigates()
    {
        var check = HealthRules.CheckDiskSpace(new SystemSnapshot
        {
            SystemDriveFree = Gb, SystemDriveTotal = 256 * Gb, HiberfilBytes = 13 * Gb,
            Identity = ComputerIdentity.FromFacts("Vendor", "Desktop", 1, [3]),
        })!;
        var row = Row(check, action => action.Kind == CheckActionKind.Clean ? "ОЧИСТИТЬ 3 ГБ" : null);
        Assert.True(row.ShowPrimaryAction);
        Assert.Equal("ОЧИСТИТЬ 3 ГБ", row.ActionText);
        Assert.Equal("Открыть очистку", row.OpenText);
        Assert.Contains("проверьте отмеченные пункты", row.NextStep);
        Assert.Contains("гибернац", row.SecondaryPathText.ToLowerInvariant());
        Assert.Contains("Оптимизация", row.SecondaryActionTooltip);

        var opened = new List<(string Page, string? Focus)>();
        Task Dispatch(CheckAction? action) => DashboardViewModel.RunReadOnlyActionAsync(action,
            (page, focus) => opened.Add((page, focus)), (_, _, _) => throw new InvalidOperationException("Unexpected shell"),
            () => throw new InvalidOperationException("Unexpected measurement"));
        await Dispatch(row.Guidance!.OpenAction);
        await Dispatch(check.Action); // A write kind cannot be handled by the Open dispatcher.
        Assert.Equal(("cleaner", (string?)null), Assert.Single(opened));
    }

    [Fact]
    public void Panel_and_Assistant_share_exact_internal_destinations()
    {
        var facts = new SystemSnapshot
        {
            StartupCount = 40, RestoreEnabled = false, DriftCount = 1,
            PrimaryGpu = new("NVIDIA RTX 5070", GpuVendor.Nvidia, "32.0.15.8180", DateTime.Now.AddYears(-1), 0, "PCI\\VEN_10DE"),
        };
        var expected = new (HealthCheckResult Check, string Page, string Focus)[]
        {
            (HealthRules.CheckStartup(facts)!, "services", "tab:startup"),
            (HealthRules.CheckRestore(facts)!, "restore", "section:restore-points"),
            (HealthRules.CheckDrift(facts)!, "restore", "section:drift"),
            (HealthRules.CheckGpuDriver(facts)!, "drivers", "gpu:PCI\\VEN_10DE"),
            (HealthRules.CheckGpuDriver(facts with { PrimaryGpu = facts.PrimaryGpu! with { PnpDeviceId = "" } })!, "drivers", "tab:hardware"),
            (HealthRules.CheckGameMode(new SystemSnapshot { GameModeOn = false })!, "optimize", HealthRules.TweakGameMode),
        };
        foreach (var (check, page, focus) in expected)
        {
            var row = Row(check);
            var card = Assert.Single(AssistantDiagnostics.Cards(new(DateTime.Now, [check], new HashSet<string>())));
            Assert.Equal(page, row.Guidance!.PageKey);
            Assert.Equal(focus, row.Guidance.FocusId);
            Assert.Equal(page, card.PageKey);
            Assert.Equal(focus, card.FocusId);
            Assert.Equal(row.PathText, card.PathText);
            Assert.Equal(row.NextStep, card.NextStep);
            Assert.Contains("→", row.PathText);
            Assert.NotEmpty(row.NextStep);
        }
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Doctor_findings_have_visible_paths_steps_and_read_only_destinations(string language)
    {
        using var _ = Loc.UseLanguage(language);
        const long processMemoryBytes = (long)Gb;
        var sample = new DoctorSample(TimeSpan.FromSeconds(10), 90, 90, 95, 95,
            [new("Render", 35, processMemoryBytes)], [new("Editor", 4, processMemoryBytes)]);
        var snapshot = new SystemSnapshot
        {
            SystemDriveFree = Gb, SystemDriveTotal = 256 * Gb, StartupCount = 20,
            PendingReboot = ["Windows Update"], EnergySaverOn = true, OnAcPower = true,
        };
        var rows = PcDoctor.Diagnose(sample, snapshot).Select(check => Row(check)).ToDictionary(row => row.Result.Id);
        Assert.Equal(8, rows.Count);
        Assert.All(rows.Values, row =>
        {
            Assert.True(row.HasOpenAction);
            Assert.Contains("→", row.PathText);
            Assert.NotEmpty(row.NextStep);
            if (language == "en") Assert.DoesNotMatch("[А-Яа-яЁё]", row.PathText + row.NextStep + row.OpenText);
        });
        Assert.Equal(CheckActionKind.OpenTaskManager, rows["doctor-ram"].Guidance!.OpenAction!.Kind);
        Assert.Equal("tab:startup", rows["doctor-startup"].Guidance!.FocusId);
        Assert.Equal(CheckActionKind.OpenUri, rows["doctor-restart"].Guidance!.OpenAction!.Kind);
        Assert.Equal("cleaner", rows["doctor-space"].Guidance!.PageKey);

        var interrupted = Row(Assert.Single(PcDoctor.Diagnose(sample with { Samples = 0 }, snapshot)));
        Assert.Equal(CheckActionKind.DiagnoseAgain, interrupted.Guidance!.OpenAction!.Kind);
        Assert.False(interrupted.ShowPrimaryAction);
    }

    [Fact]
    public async Task Windows_tools_and_URIs_are_dispatched_from_fixed_destinations()
    {
        var launches = new List<(string Target, string? Args)>();
        Task Dispatch(CheckAction action) => DashboardViewModel.RunReadOnlyActionAsync(action,
            (page, focus) => throw new InvalidOperationException($"Unexpected navigation to {page} ({focus})"),
            (target, args, _) => { launches.Add((target, args)); return Task.CompletedTask; },
            () => throw new InvalidOperationException("Unexpected measurement"));
        await Dispatch(new(CheckActionKind.OpenReliabilityMonitor, "", "fake.exe", "/report"));
        await Dispatch(new(CheckActionKind.OpenTaskManager, "", "fake.exe"));
        await Dispatch(new(CheckActionKind.OpenSystemInformation, "", "fake.exe"));
        await Dispatch(new(CheckActionKind.OpenUri, "", HealthRules.WindowsSecurityUri));
        await Dispatch(new(CheckActionKind.OpenUri, "", "file:///fake.exe"));
        await Dispatch(new(CheckActionKind.MemoryDiagnostic, ""));
        Assert.Equal(new (string, string?)[]
        {
            (ProcessRunner.Sys("perfmon.exe"), "/rel"),
            (ProcessRunner.Sys("Taskmgr.exe"), null),
            (ProcessRunner.Sys("msinfo32.exe"), null),
            ("windowsdefender://coreisolation/", null),
        }, launches);
    }

    [Fact]
    public async Task Memory_advice_uses_the_hosted_Tools_command_and_action()
    {
        var tools = new ToolsViewModel();
        var maintenance = new MaintenanceViewModel(new CleanerViewModel(), tools);
        var expected = Assert.Single(tools.Sections.SelectMany(s => s.Cards).SelectMany(c => c.Actions),
            action => action.Op == ToolOp.MemoryDiagnostic);
        ToolCardAction? ran = null;
        var ready = new AsyncRelayCommand<ToolCardAction?>(action =>
        {
            ran = action;
            return Task.CompletedTask;
        });
        Assert.True(await DashboardViewModel.RunMemoryDiagnosticAdviceAsync(maintenance, owner =>
        {
            Assert.Same(tools, owner);
            return ready;
        }));
        Assert.Same(expected, ran);
        Assert.True(ran!.RequiresAdmin); // owned command handles the admin replay and confirmation

        ran = null;
        var busy = new AsyncRelayCommand<ToolCardAction?>(_ => throw new InvalidOperationException("Command is busy"), _ => false);
        Assert.False(await DashboardViewModel.RunMemoryDiagnosticAdviceAsync(maintenance, _ => busy));
        Assert.Null(ran);
    }

    [Fact]
    public async Task Cancelling_cannot_queue_refresh_or_apply_the_fix()
    {
        await DashboardViewModel.RunConfirmedFixAsync(
            () => Task.FromResult(false),
            _ => throw new InvalidOperationException("Cancelled fix reached the queue"),
            () => throw new InvalidOperationException("Cancelled fix refreshed"),
            () => throw new InvalidOperationException("Cancelled fix applied"));
    }

    [Theory]
    [InlineData(HagsState.Off, true, 1)]
    [InlineData(HagsState.Off, false, 0)]
    [InlineData(HagsState.On, true, 0)]
    public async Task The_queue_rechecks_the_exact_confirmed_safe_fix_before_writing(HagsState currentState, bool sameGpu, int expectedWrites)
    {
        var changes = new List<string>();
        var before = Hags(HagsState.Off, true);
        var tweak = TweakCatalog.Find(HealthRules.TweakHags)!;
        var approved = new DashboardViewModel.FixSelection(before.Id, before.Action!.Target!, tweak);
        var observations = new List<HealthCheckResult> { before };
        var confirmationSeen = false;
        await DashboardViewModel.RunConfirmedFixAsync(
            () => { confirmationSeen = true; return Task.FromResult(true); },
            async work =>
            {
                Assert.True(confirmationSeen);
                observations[0] = Hags(currentState, sameGpu);
                await work();
            },
            () => Task.FromResult(DashboardViewModel.FixesStillMatch([approved], observations, safeOnly: true, _ => true)),
            () => { changes.Add(tweak.Id); return Task.CompletedTask; });
        Assert.Equal(expectedWrites, changes.Count);
        if (expectedWrites == 1) Assert.Equal("hags", Assert.Single(changes));
    }

    [Fact]
    public void Optional_GPU_advice_stays_available_for_one_exact_confirmed_fix()
    {
        var safe = TweakCatalog.Find(HealthRules.TweakHags)!;
        var optional = TweakCatalog.Find(HealthRules.TweakHagsOther)!;
        var onlyOptionalApplies = (Tweak tweak) => (bool?)(tweak.Id == optional.Id);
        Assert.Equal(TweakRisk.Safe, safe.Risk);
        Assert.Equal(TweakRisk.Optional, optional.Risk);
        Assert.Null(DashboardViewModel.ResolveFix(safe, safeOnly: true, onlyOptionalApplies).Tweak);
        Assert.Null(DashboardViewModel.ResolveFix(optional, safeOnly: true, onlyOptionalApplies).Tweak);
        Assert.Equal(optional.Id, DashboardViewModel.ResolveFix(optional, safeOnly: false, onlyOptionalApplies).Tweak!.Id);
        var check = Hags(HagsState.Off, false);
        var row = Row(check);
        Assert.True(row.ShowPrimaryAction);
        Assert.Equal(optional.Id, row.Guidance!.FocusId);
        Assert.False(DashboardViewModel.IsSafeFix(row));
        var picked = new DashboardViewModel.FixSelection(check.Id, optional.Id, optional);
        Assert.True(DashboardViewModel.FixesStillMatch([picked], [check], safeOnly: false, onlyOptionalApplies));
        Assert.False(DashboardViewModel.FixesStillMatch([picked], [check], safeOnly: true, onlyOptionalApplies));
    }

    [Fact]
    public void Admin_replay_keeps_its_catalog_target_and_Home_can_still_use_the_rule_id()
    {
        var safe = Row(Hags(HagsState.Off, true));
        var optional = Row(Hags(HagsState.Off, false));
        Assert.Same(safe, DashboardViewModel.FindCheckForReplay([safe], "hags|hags"));
        Assert.Null(DashboardViewModel.FindCheckForReplay([optional], "hags|hags"));
        Assert.Null(DashboardViewModel.FindCheckForReplay([Row(Hags(HagsState.On, true))], "hags|hags"));
        Assert.Same(optional, DashboardViewModel.FindCheckForReplay([optional], "hags"));
    }

    [Fact]
    public async Task Home_opens_cleanup_and_carries_the_exact_catalog_item_for_explicit_fixes()
    {
        var disk = HealthRules.CheckDiskSpace(new SystemSnapshot { SystemDriveFree = Gb, SystemDriveTotal = 256 * Gb })!;
        var issues = HomeViewModel.BuildDoctorIssues([Row(disk, _ => "ОЧИСТИТЬ 3 ГБ"), Row(Hags(HagsState.Off, false))]);
        var cleanup = issues.Single(i => i.RuleId == "disk-space");
        var hags = issues.Single(i => i.RuleId == "hags");
        Assert.Equal("Открыть очистку", cleanup.Action);
        Assert.Equal("ПО ЖЕЛАНИЮ", hags.Risk);
        var replay = new List<string>();
        var home = new HomeViewModel(new DashboardViewModel(), parameter =>
        {
            replay.Add(parameter);
            return Task.CompletedTask;
        });
        await home.RunDoctorActionCommand.ExecuteAsync(cleanup);
        await home.RunDoctorActionCommand.ExecuteAsync(hags);
        Assert.Equal(["disk-space", "hags|hags-other-gpu"], replay);
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 0)]
    public async Task Restart_checks_blockers_again_after_the_confirmation_dialog(
        bool gameStartsInDialog, bool userConfirms, int expectedSchedules)
    {
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeGame = false;
        var messages = new List<string>();
        var scheduled = 0;
        var run = DashboardViewModel.ConfirmAndScheduleRestartAsync(
            () => SessionActions.RebootBlockerText([], activeGame), () => confirmation.Task,
            () => { scheduled++; return Task.FromResult(new SessionActionResult(true, "Scheduled")); }, messages.Add);
        Assert.False(run.IsCompleted);
        activeGame = gameStartsInDialog;
        confirmation.SetResult(userConfirms);
        var outcome = await run;
        Assert.Equal(expectedSchedules, scheduled);
        if (gameStartsInDialog)
            Assert.Contains("игровой сеанс", Assert.Single(messages));
        else
            Assert.Empty(messages);
        if (expectedSchedules == 1) Assert.True(outcome?.Success);
        else Assert.Null(outcome);
    }

    private static HealthCheckResult Hags(HagsState state, bool rtx) =>
        HealthRules.CheckHags(new SystemSnapshot
        {
            Hags = state,
            PrimaryGpu = new(rtx ? "GeForce RTX 5070" : "GeForce RTX 3070", GpuVendor.Nvidia, "", null, 0, "PCI"),
        })!;
}
