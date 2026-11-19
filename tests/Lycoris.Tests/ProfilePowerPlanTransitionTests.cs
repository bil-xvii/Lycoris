using System.Globalization;
using Lycoris.Core;
using Lycoris.Modules.Optimize;
using Lycoris.Tweaks;

namespace Lycoris.Tests;

// The existing nonparallel collection also guards ChangeSession.BypassElevationForTests.
[Collection("Registry")]
public sealed class ProfilePowerPlanTransitionTests
{
    private const uint Goal = 180;
    private const string PlanId = "balanced";
    private const string TimeoutId = "shorten-timeout";
    private static readonly Guid Old = PowerSchemes.HighPerformance;
    private static readonly Guid Target = PowerSchemes.Balanced;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Satisfied_on_old_plan_still_applies_to_the_selected_ac_or_dc_side_and_undo_restores_it(bool dc)
    {
        var power = new FakePowerSettings(targetSeconds: 300, dc);
        var session = await BeginAsync(power);
        var batch = PendingPowerBatch(power, dc);
        Assert.Equal([TimeoutId, PlanId], batch.Select(t => t.Id)); // Old plan's 120 seconds already satisfies the goal.

        var result = TweakEngine.ApplyInSession(session, batch, new SystemProfile(), power.ActivePowerScheme);
        session.Complete();

        Assert.Empty(result.SkippedIds);
        Assert.Empty(session.Errors);
        Assert.Equal(Target, power.Active);
        Assert.Equal(Goal, power.Read(Target, dc));
        Assert.Equal(120u, power.Read(Old, dc));
        Assert.Equal(900u, power.Read(Target, !dc));
        Assert.Equal(900u, power.Read(Old, !dc));
        var record = Assert.Single(session.Journal.Records, r => r.Kind == ChangeKind.Command);
        Assert.Equal($"cmd:powercfg:{Target:D}:{TweakCatalog.VideoIdle:D}:{(dc ? "dc" : "ac")}", record.Target);
        Assert.Equal((Target, TweakCatalog.SubVideo, TweakCatalog.VideoIdle, 300u), JournalTrust.PowercfgUndo(record)!.Value);

        var undo = await session.Journal.RevertAsync(session.Journal.Records);
        Assert.True(undo.Success, string.Join(Environment.NewLine, undo.Errors));
        Assert.Equal(Old, power.Active);
        Assert.Equal(300u, power.Read(Target, dc));
        Assert.Equal(120u, power.Read(Old, dc));
    }

    [Theory]
    [InlineData(90u)]
    [InlineData(180u)]
    public async Task A_target_that_already_satisfies_the_goal_gets_no_setting_write(uint current)
    {
        var power = new FakePowerSettings(current, dc: true);
        var session = await BeginAsync(power);

        var result = TweakEngine.ApplyInSession(session, PendingPowerBatch(power, dc: true), new SystemProfile(), power.ActivePowerScheme);
        session.Complete();

        Assert.Empty(result.SkippedIds);
        Assert.Empty(session.Errors);
        Assert.Equal(Target, power.Active);
        Assert.Equal(current, power.Read(Target, dc: true));
        Assert.Equal(ChangeKind.PowerScheme, Assert.Single(session.Journal.Records).Kind);
    }

    [Fact]
    public async Task A_missing_target_setting_is_reported_and_neither_plan_gets_a_setting_write()
    {
        var power = new FakePowerSettings(targetSeconds: null, dc: true);
        var session = await BeginAsync(power);

        var result = TweakEngine.ApplyInSession(session, PendingPowerBatch(power, dc: true), new SystemProfile(), power.ActivePowerScheme);
        session.Complete();

        Assert.Empty(result.SkippedIds);
        Assert.Contains("параметр отсутствует в текущей схеме питания", Assert.Single(session.Errors));
        Assert.Equal(Target, power.Active);
        Assert.Null(power.Read(Target, dc: true));
        Assert.Equal(120u, power.Read(Old, dc: true));
        Assert.Equal(ChangeKind.PowerScheme, Assert.Single(session.Journal.Records).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_or_unconfirmed_switch_skips_the_setting_and_never_writes_the_old_plan(bool reportsError)
    {
        var power = new FakePowerSettings(targetSeconds: 300, dc: true, oldSeconds: 600);
        var session = await BeginAsync(power);
        var batch = PendingPowerBatch(power, dc: true, applyPlan: ctx =>
        {
            if (reportsError) ctx.Session.Errors.Add("Plan switch refused");
            // No switch happened, even if it returned without a reported error.
        });

        var result = TweakEngine.ApplyInSession(session, batch, new SystemProfile(), power.ActivePowerScheme);
        session.Complete();

        Assert.Equal(TimeoutId, Assert.Single(result.SkippedIds));
        Assert.Contains("не подтверждено включение выбранной схемы", Assert.Single(result.SkippedReasons));
        Assert.Equal(Old, power.Active);
        Assert.Equal(600u, power.Read(Old, dc: true));
        Assert.Equal(300u, power.Read(Target, dc: true));
        Assert.Empty(session.Journal.Records);
        Assert.Equal(reportsError ? 1 : 0, session.Errors.Count);
    }

    [Fact]
    public async Task A_change_of_active_plan_between_actions_cannot_redirect_the_setting()
    {
        var power = new FakePowerSettings(targetSeconds: 300, dc: true, oldSeconds: 600);
        var session = await BeginAsync(power);
        var batch = PendingPowerBatch(power, dc: true);
        var progress = new CallbackProgress(title =>
        {
            if (title == TimeoutId) power.Active = Old;
        });

        var result = TweakEngine.ApplyInSession(session, batch, new SystemProfile(), power.ActivePowerScheme, progress);
        session.Complete();

        Assert.Equal(TimeoutId, Assert.Single(result.SkippedIds));
        Assert.Equal(Old, power.Active);
        Assert.Equal(600u, power.Read(Old, dc: true));
        Assert.Equal(300u, power.Read(Target, dc: true));
        Assert.Equal(ChangeKind.PowerScheme, Assert.Single(session.Journal.Records).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Drift_after_planning_an_already_applied_plan_skips_settings_before_any_journal_batch(bool planStillChecked)
    {
        var power = new FakePowerSettings(300, dc: true, oldSeconds: 600) { Active = Target };
        var rows = PowerRows(power, dc: true);
        var (batch, intent) = CaptureBatch(power, planStillChecked ? rows : [rows[0]]);
        Assert.Equal(TimeoutId, Assert.Single(batch).Id);
        Assert.Equal(Target, intent!.OriginalScheme);
        Assert.False(intent.SwitchPending);
        Assert.Equal(planStillChecked ? PlanId : null, intent.SelectedPlan?.Id);
        power.Active = Old;
        var openedBatches = 0;

        var result = await TweakEngine.ApplyCapturedAsync(batch, new SystemProfile(), intent, power.ActivePowerScheme, () =>
        {
            openedBatches++;
            return BeginAsync(power);
        });

        Assert.Equal(0, openedBatches);
        Assert.Equal(TimeoutId, Assert.Single(result.SkippedIds));
        Assert.Contains("активная схема питания изменилась", Assert.Single(result.SkippedReasons));
        Assert.Equal(Old, power.Active);
        Assert.Equal(600u, power.Read(Old, dc: true));
        Assert.Equal(300u, power.Read(Target, dc: true));
    }

    [Fact]
    public async Task Live_detection_must_support_the_cached_applied_plan_before_capturing_it()
    {
        var power = new FakePowerSettings(300, dc: true, oldSeconds: 600);
        var (batch, intent) = CaptureBatch(power, PowerRows(power, dc: true), cachedPlan: TweakState.Applied);

        Assert.Equal(TimeoutId, Assert.Single(batch).Id);
        Assert.Equal(Guid.Empty, intent!.OriginalScheme);
        Assert.Equal(PlanId, intent.SelectedPlan?.Id);
        var result = await TweakEngine.ApplyCapturedAsync(batch, new SystemProfile(), intent, power.ActivePowerScheme,
            () => throw new InvalidOperationException("A stale plan must not open a session."));

        Assert.Equal(TimeoutId, Assert.Single(result.SkippedIds));
        Assert.Equal(600u, power.Read(Old, dc: true));
        Assert.Equal(300u, power.Read(Target, dc: true));
    }

    [Fact]
    public void A_changed_guid_during_the_live_applied_probe_invalidates_the_capture()
    {
        var power = new FakePowerSettings(300, dc: true) { Active = Target };
        var rows = PowerRows(power, dc: true);
        var reads = new Queue<Guid>([Target, Old]);

        var intent = TweakEngine.CapturePowerPlanIntent([rows[0]], rows[1], reads.Dequeue);

        Assert.Equal(Guid.Empty, intent!.OriginalScheme);
        Assert.Empty(reads);
    }

    [Fact]
    public async Task An_already_applied_oem_plan_is_the_exact_target_of_the_setting_write()
    {
        var oem = Guid.NewGuid();
        var power = new FakePowerSettings(300, dc: true, targetScheme: oem) { Active = oem };
        var (batch, intent) = CaptureBatch(power, PowerRows(power, dc: true));

        Assert.Equal(TimeoutId, Assert.Single(batch).Id);
        Assert.Equal(oem, intent!.OriginalScheme);
        Assert.False(intent.SwitchPending);
        var session = await BeginAsync(power);
        var result = TweakEngine.ApplyInSession(session, batch, new SystemProfile(), intent, power.ActivePowerScheme);
        session.Complete();

        Assert.Empty(result.SkippedIds);
        Assert.Empty(session.Errors);
        Assert.Equal(oem, power.Active);
        Assert.Equal(Goal, power.Read(oem, dc: true));
        Assert.Equal(120u, power.Read(Old, dc: true));
        var record = Assert.Single(session.Journal.Records);
        Assert.Equal((oem, TweakCatalog.SubVideo, TweakCatalog.VideoIdle, 300u), JournalTrust.PowercfgUndo(record)!.Value);
    }

    [Fact]
    public async Task Drift_during_session_setup_skips_both_the_pending_plan_and_its_setting()
    {
        var power = new FakePowerSettings(300, dc: true, oldSeconds: 600);
        var (batch, intent) = CaptureBatch(power, PowerRows(power, dc: true));
        Assert.True(intent!.SwitchPending);
        var session = await BeginAsync(power);

        var result = await TweakEngine.ApplyCapturedAsync(batch, new SystemProfile(), intent, power.ActivePowerScheme, () =>
        {
            power.Active = PowerSchemes.PowerSaver;
            return Task.FromResult(session);
        });

        Assert.Equal(new[] { PlanId, TimeoutId }, result.SkippedIds.Order());
        Assert.Equal(PowerSchemes.PowerSaver, power.Active);
        Assert.Equal(600u, power.Read(Old, dc: true));
        Assert.Equal(300u, power.Read(Target, dc: true));
        Assert.Empty(session.Journal.Records);
    }

    [Fact]
    public async Task A_plan_action_without_an_activation_request_cannot_adopt_an_external_switch()
    {
        var power = new FakePowerSettings(300, dc: true, oldSeconds: 600);
        var (batch, intent) = CaptureBatch(power, PowerRows(power, dc: true, applyPlan: _ => power.Active = Target));
        var session = await BeginAsync(power);

        var result = TweakEngine.ApplyInSession(session, batch, new SystemProfile(), intent, power.ActivePowerScheme);
        session.Complete();

        Assert.Equal(TimeoutId, Assert.Single(result.SkippedIds));
        Assert.Equal(Target, power.Active);
        Assert.Equal(300u, power.Read(Target, dc: true));
        Assert.Equal(600u, power.Read(Old, dc: true));
        Assert.Empty(session.Journal.Records);
    }

    [Fact]
    public async Task Drift_between_two_settings_does_not_redirect_the_second_setting()
    {
        var power = new FakePowerSettings(300, dc: false, oldSeconds: 600) { Active = Target };
        var (batch, intent) = CaptureBatch(power,
            [.. PowerRows(power, dc: false), PowerRows(power, dc: true, settingId: "second-setting")[0]]);
        var session = await BeginAsync(power);
        var progress = new CallbackProgress(title =>
        {
            if (title == "second-setting") power.Active = Old;
        });

        var result = TweakEngine.ApplyInSession(session, batch, new SystemProfile(), intent, power.ActivePowerScheme, progress);
        session.Complete();

        Assert.Equal("second-setting", Assert.Single(result.SkippedIds));
        Assert.Equal(Goal, power.Read(Target, dc: false));
        Assert.Equal(900u, power.Read(Target, dc: true));
        Assert.Equal(900u, power.Read(Old, dc: true));
        Assert.Equal(ChangeKind.Command, Assert.Single(session.Journal.Records).Kind);
    }

    [Fact]
    public async Task Drift_after_reading_the_setting_is_caught_at_the_write_guard()
    {
        var power = new FakePowerSettings(300, dc: true, oldSeconds: 600) { Active = Target };
        var (batch, intent) = CaptureBatch(power, PowerRows(power, dc: true, beforeWrite: () => power.Active = Old));
        var session = await BeginAsync(power);

        var result = TweakEngine.ApplyInSession(session, batch, new SystemProfile(), intent, power.ActivePowerScheme);
        session.Complete();

        Assert.Equal(TimeoutId, Assert.Single(result.SkippedIds));
        Assert.Empty(session.Journal.Records);
        Assert.Empty(session.Errors);
        Assert.Equal(300u, power.Read(Target, dc: true));
        Assert.Equal(600u, power.Read(Old, dc: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_active_guid_or_missing_intent_cannot_authorize_a_power_setting(bool missingIntent)
    {
        var power = new FakePowerSettings(300, dc: true, oldSeconds: 600);
        if (!missingIntent) power.Active = Guid.Empty;
        var timeout = PowerRows(power, dc: true)[0];
        Tweak[] batch = [timeout];
        var intent = missingIntent ? null : TweakEngine.CapturePowerPlanIntent(batch, null, power.ActivePowerScheme);
        if (!missingIntent) Assert.Equal(Guid.Empty, intent!.OriginalScheme);

        var result = await TweakEngine.ApplyCapturedAsync(batch, new SystemProfile(), intent, power.ActivePowerScheme,
            () => throw new InvalidOperationException("An unconfirmed plan must not open a session."));

        Assert.Equal(TimeoutId, Assert.Single(result.SkippedIds));
        Assert.Empty(result.Errors);
        Assert.Equal(600u, power.Read(Old, dc: true));
        Assert.Equal(300u, power.Read(Target, dc: true));
    }

    [Fact]
    public async Task A_changed_plan_does_not_block_unrelated_changes_in_the_batch()
    {
        var power = new FakePowerSettings(300, dc: true, oldSeconds: 600) { Active = Target };
        var unrelatedCalls = 0;
        var unrelated = new CustomTweak
        {
            Id = "unrelated", Title = "", Description = "", DetectFunc = () => TweakState.NotApplied,
            ApplyAction = _ => unrelatedCalls++,
        };
        var (batch, intent) = CaptureBatch(power, [.. PowerRows(power, dc: true), unrelated]);
        power.Active = Old;

        var result = await TweakEngine.ApplyCapturedAsync(batch, new SystemProfile(), intent, power.ActivePowerScheme,
            () => BeginAsync(power));

        Assert.Equal(1, unrelatedCalls);
        Assert.Equal(TimeoutId, Assert.Single(result.SkippedIds));
        Assert.Equal(600u, power.Read(Old, dc: true));
        Assert.Equal(300u, power.Read(Target, dc: true));
    }

    private static Tweak[] PendingPowerBatch(FakePowerSettings power, bool dc, Action<TweakContext>? applyPlan = null) =>
        CaptureBatch(power, PowerRows(power, dc, applyPlan)).Batch;

    private static (Tweak[] Batch, PowerPlanIntent? Intent) CaptureBatch(
        FakePowerSettings power, Tweak[] tweaks, TweakState? cachedPlan = null)
    {
        var page = new OptimizeViewModel();
        page.BuildCatalog(tweaks);
        page.SetProfileDefaults(OptimizeProfile.Gaming, tweaks.Select(t => t.Id));
        var states = tweaks.ToDictionary(t => t.Id, t => t.Id == PlanId && cachedPlan.HasValue ? cachedPlan.Value : t.Detect());
        var batch = page.PendingForProfile(OptimizeProfile.Gaming, states, []).Select(i => i.Tweak).ToArray();
        return (batch, page.CapturePowerPlanIntent(batch, power.ActivePowerScheme));
    }

    private static Tweak[] PowerRows(FakePowerSettings power, bool dc, Action<TweakContext>? applyPlan = null,
        string settingId = TimeoutId, Action? beforeWrite = null)
    {
        var plan = new CustomTweak
        {
            Id = PlanId, Title = PlanId, Description = "", ExclusiveGroup = TweakGroups.PowerPlan,
            DetectFunc = () => power.Active == power.TargetScheme ? TweakState.Applied : TweakState.NotApplied,
            ApplyAction = applyPlan ?? power.Switch,
        };
        var timeout = new CustomTweak
        {
            Id = settingId, Title = settingId, Description = "", UsesActivePowerPlan = true,
            DetectFunc = () => power.Read(power.Active, dc) is { } value
                ? TweakCatalog.PowerSettingState(value, Goal, TweakCatalog.Satisfied(null, onlyShorten: true, Goal))
                : TweakState.NotApplicable,
            ApplyAction = ctx =>
            {
                var scheme = ctx.ConfirmedPowerScheme ?? power.Active;
                TweakCatalog.ApplyActivePowerSetting(scheme, Goal, TweakCatalog.Satisfied(null, onlyShorten: true, Goal),
                    target => power.Read(target, dc), original =>
                    {
                        beforeWrite?.Invoke();
                        ctx.RequireCurrentPowerScheme();
                        power.Write(ctx, scheme, dc, original);
                    });
            }
        };
        return [timeout, plan];
    }

    private static async Task<ChangeSession> BeginAsync(FakePowerSettings power)
    {
        var previous = ChangeSession.BypassElevationForTests;
        ChangeSession.BypassElevationForTests = true;
        try
        {
            var journal = ChangeJournal.ForTests();
            journal.SystemAccess = power;
            return await ChangeSession.BeginAsync("Fake plan transition", createRestorePoint: false, journal: journal);
        }
        finally
        {
            ChangeSession.BypassElevationForTests = previous;
        }
    }

    private sealed class CallbackProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class FakePowerSettings(uint? targetSeconds, bool dc, uint oldSeconds = 120, Guid? targetScheme = null) : IJournalSystem
    {
        private readonly Dictionary<(Guid, bool), uint?> _values = new()
        {
            [(Old, dc)] = oldSeconds, [(Old, !dc)] = 900,
            [(targetScheme ?? Target, dc)] = targetSeconds, [(targetScheme ?? Target, !dc)] = 900,
        };

        public Guid Active { get; set; } = Old;
        public Guid TargetScheme { get; } = targetScheme ?? Target;

        public Guid ActivePowerScheme() => Active;
        public bool? PowerSchemeExists(Guid scheme) => scheme == Old || scheme == TargetScheme;
        public void DeletePowerScheme(Guid scheme) => throw new InvalidOperationException("The fake uses two built-in plans.");

        public uint? Read(Guid scheme, bool dc) => _values.GetValueOrDefault((scheme, dc));

        public void Switch(TweakContext ctx)
        {
            ctx.SetPowerScheme(TargetScheme, null, (scheme, _) =>
            {
                ctx.Session.Journal.Add(new ChangeRecord
                {
                    BatchId = ctx.Session.Batch.Id, Source = ctx.Source, Title = ctx.Tweak.TitleRu,
                    Kind = ChangeKind.PowerScheme, Target = "power:active", PowerSchemeOriginal = Active,
                });
                Active = scheme;
                return true;
            });
        }

        public void Write(TweakContext ctx, Guid scheme, bool dc, uint original)
        {
            var undo = dc ? TweakCatalog.PowerSettingCommandDc(scheme, TweakCatalog.SubVideo, TweakCatalog.VideoIdle, original)
                : TweakCatalog.PowerSettingCommand(scheme, TweakCatalog.SubVideo, TweakCatalog.VideoIdle, original);
            ctx.Session.Journal.Add(new ChangeRecord
            {
                BatchId = ctx.Session.Batch.Id, Source = ctx.Source, Title = ctx.Tweak.TitleRu, Kind = ChangeKind.Command,
                Target = "cmd:" + TweakCatalog.PowercfgTarget(dc, scheme, TweakCatalog.VideoIdle),
                OldValue = original.ToString(CultureInfo.InvariantCulture), NewValue = Goal.ToString(CultureInfo.InvariantCulture),
                RevertFile = ProcessRunner.Sys("cmd.exe"), RevertArgs = TweakCatalog.CmdArgs(undo),
            });
            _values[(scheme, dc)] = Goal;
        }

        public Task<RevertOutcome> RestoreAsync(ChangeRecord record)
        {
            if (record.Kind == ChangeKind.PowerScheme) Active = record.PowerSchemeOriginal!.Value;
            else if (JournalTrust.PowercfgUndo(record) is { } undo && JournalTrust.PowercfgIsDc(record.Target) is { } dc)
                _values[(undo.Scheme, dc)] = undo.Value;
            else throw new InvalidOperationException("Unexpected fake undo target.");
            return Task.FromResult(RevertOutcome.Done);
        }
    }
}
