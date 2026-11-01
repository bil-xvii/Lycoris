using System.Diagnostics;
using System.IO;
using Monolith.Core;
using Monolith.Core.Devices;
using static Monolith.Tests.Core.DeviceTestData;

namespace Monolith.Tests.Core;

/// <summary>
/// The display safe-apply protocol as decision logic (a fake writer, fake prompts, a lowered countdown): journal first,
/// guard armed before the change, temporary apply, keep only on an explicit «keep», revert otherwise, startup reconcile
/// and undo. The native side (CCD/GDI, the guard process) is verified by hand on a test machine.
/// </summary>
[Collection("Registry")]
public sealed class DisplaySafeApplyTests : IDisposable
{
    private readonly FakeDisplayWriter _display = new();
    private readonly IDisposable _writers;
    private readonly ChangeJournal _journal = ChangeJournal.ForTests();
    private readonly TimeSpan _window = DisplaySafeApply.ConfirmWindow, _slack = DisplaySafeApply.GuardSlack, _poll = DisplaySafeApply.PollInterval;
    private readonly List<CountdownRequest> _asked = [];

    private static readonly DisplayModeSpec Original = Mode(2560, 1440, 144, 143980, 1000);
    private static readonly DisplayModeSpec Target = Mode(2560, 1440, 240, 239970, 1000);

    public DisplaySafeApplyTests()
    {
        ChangeSession.BypassElevationForTests = true;
        DisplaySafeApply.ConfirmWindow = TimeSpan.FromMilliseconds(300);
        DisplaySafeApply.GuardSlack = TimeSpan.FromMilliseconds(200);
        DisplaySafeApply.PollInterval = TimeSpan.FromMilliseconds(50);
        _writers = DeviceWriters.OverrideForTests(display: _display);
        _display.Current = Original;
        _display.Persisted = Original;
        _display.Hdr = false;
        _display.Offered.AddRange([Original, Target, Mode(1920, 1080, 60)]);
        Answer(true);
        DeviceConfirm.Ask = (_, _, _) => Task.FromResult(true);
    }

    public void Dispose()
    {
        _writers.Dispose();
        DeviceConfirm.Countdown = null;
        DeviceConfirm.Ask = null;
        DisplaySafeApply.ConfirmWindow = _window;
        DisplaySafeApply.GuardSlack = _slack;
        DisplaySafeApply.PollInterval = _poll;
        GameBoostState.Set(false);
        ChangeSession.BypassElevationForTests = false;
    }

    private void Answer(bool keep) => DeviceConfirm.Countdown = (r, _) =>
    {
        _asked.Add(r);
        return Task.FromResult(keep);
    };

    /// <summary>A prompt nobody answers; it completes only when cancelled.</summary>
    private void NeverAnswer() => DeviceConfirm.Countdown = (r, ct) =>
    {
        _asked.Add(r);
        var tcs = new TaskCompletionSource<bool>();
        ct.Register(() => tcs.TrySetResult(false));
        return tcs.Task;
    };

    private async Task<DisplayApplyOutcome> ApplyMode(DisplayModeSpec? target = null)
    {
        var s = await ChangeSession.BeginAsync("test", createRestorePoint: false, journal: _journal);
        return await s.SetDisplayModeAsync(MonitorPath, "X27U W3", target ?? Target, DeviceTargets.Source, "Разрешение и частота");
    }

    private async Task<DisplayApplyOutcome> ApplyHdr(bool enable)
    {
        var s = await ChangeSession.BeginAsync("test", createRestorePoint: false, journal: _journal);
        return await s.SetDisplayHdrAsync(MonitorPath, "X27U W3", enable, DeviceTargets.Source, "HDR");
    }

    // ───────────────────────────── New mode ─────────────────────────────

    [Fact]
    public async Task Keep_persists_resolves_the_guard_and_confirms_the_record()
    {
        var outcome = await ApplyMode();

        Assert.Equal(DisplayApplyResult.Kept, outcome.Result);
        Assert.Equal(["test", "guard", "temp", "persist"], _display.Calls);
        Assert.True(_display.Guards.Single().Resolved);
        Assert.True(_display.Guards.Single().Disposed);
        var r = Assert.Single(_journal.ActiveRecords);
        Assert.False(r.DisplayMode!.PendingConfirm);
        Assert.Equal("2560×1440, 239,97 Гц", r.NewValue);
        Assert.Equal("2560×1440, 143,98 Гц", r.OldValue);
        Assert.Equal(@"\\.\DISPLAY1", _asked.Single().AvoidGdiDevice); // the prompt avoids the monitor being changed
        Assert.Equal(CountdownRequest.Keep, _asked.Single().KeepText);
    }

    [Fact]
    public async Task Decline_reverts_and_removes_the_record()
    {
        Answer(false);
        var outcome = await ApplyMode();

        Assert.Equal(DisplayApplyResult.Reverted, outcome.Result);
        Assert.Contains("revert", _display.Calls);
        Assert.DoesNotContain("persist", _display.Calls);
        Assert.Equal(Original, _display.Current);
        Assert.True(_display.Guards.Single().Resolved);
        Assert.Empty(_journal.Records);
    }

    [Fact]
    public async Task A_prompt_that_never_answers_reverts_at_the_deadline()
    {
        NeverAnswer();
        var sw = Stopwatch.StartNew();
        var outcome = await ApplyMode();

        Assert.Equal(DisplayApplyResult.Reverted, outcome.Result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.Equal(Original, _display.Current);
        Assert.Empty(_journal.Records);
    }

    [Fact]
    public async Task No_countdown_hook_reverts()
    {
        DeviceConfirm.Countdown = null;
        Assert.Equal(DisplayApplyResult.Reverted, (await ApplyMode()).Result);
        Assert.Equal(Original, _display.Current);
    }

    [Fact]
    public async Task A_mode_not_offered_or_failing_CDS_TEST_changes_nothing()
    {
        Assert.Equal(DisplayApplyResult.Failed, (await ApplyMode(Mode(3840, 2160, 60))).Result);
        _display.FailTest = true;
        Assert.Equal(DisplayApplyResult.Failed, (await ApplyMode()).Result);
        Assert.DoesNotContain("temp", _display.Calls);
        Assert.Empty(_journal.Records);
    }

    [Fact]
    public async Task No_guard_means_no_change()
    {
        _display.GuardStartFails = true;
        var outcome = await ApplyMode();
        Assert.Equal(DisplayApplyResult.Failed, outcome.Result);
        Assert.DoesNotContain("temp", _display.Calls);
        Assert.Empty(_journal.Records);
        Assert.Equal(Original, _display.Current);
    }

    [Fact]
    public async Task Game_session_and_block_reason_block_without_a_record()
    {
        GameBoostState.Set(true);
        Assert.Equal(DisplayApplyResult.Blocked, (await ApplyMode()).Result);
        GameBoostState.Set(false);
        _display.Block = "Идёт удалённый сеанс";
        Assert.Equal(DisplayApplyResult.Blocked, (await ApplyMode()).Result);
        Assert.Empty(_journal.Records);
        Assert.Empty(_display.Calls);
    }

    [Fact]
    public async Task The_same_mode_is_unchanged()
    {
        Assert.Equal(DisplayApplyResult.Unchanged, (await ApplyMode(Mode(2560, 1440, 144))).Result);
        Assert.Empty(_journal.Records);
        Assert.Empty(_display.Calls);
    }

    [Fact]
    public async Task A_monitor_that_disappears_during_the_countdown_is_reverted()
    {
        NeverAnswer();
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            _display.Present = false;
        });
        var outcome = await ApplyMode();
        Assert.Equal(DisplayApplyResult.Reverted, outcome.Result);
        Assert.Contains("revert", _display.Calls);
        Assert.Empty(_journal.Records);
    }

    [Fact]
    public async Task An_unavailable_journal_applies_nothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"monolith-broken-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ damaged");
        await File.WriteAllTextAsync(path + ".bak", "{ damaged");
        try
        {
            var broken = ChangeJournal.ForTests(path);
            Assert.True(broken.IsUnavailable);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DisplaySafeApply.ApplyModeAsync(broken, new ChangeBatch(), MonitorPath, "X27U", Target, DeviceTargets.Source, "x", CancellationToken.None));
            Assert.DoesNotContain("temp", _display.Calls);
            Assert.DoesNotContain("guard", _display.Calls);
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*"))
                try { File.Delete(f); } catch { }
        }
    }

    [Fact]
    public async Task Keep_that_fails_verification_after_persist_saves_the_original_again()
    {
        _display.FirstPersistLandsOn = Mode(1920, 1080, 60); // the driver fell back to another mode
        var outcome = await ApplyMode();

        Assert.Equal(DisplayApplyResult.Reverted, outcome.Result);
        Assert.Equal(Original, _display.Persisted); // not left saved as the target
        Assert.Equal(Original, _display.Current);
        Assert.Empty(_journal.Records);
    }

    [Fact]
    public async Task Keep_that_fails_and_cannot_save_the_original_keeps_the_record_for_undo()
    {
        _display.FirstPersistLandsOn = Mode(1920, 1080, 60);
        _display.PersistFailsFrom = 1;
        var outcome = await ApplyMode();

        Assert.Equal(DisplayApplyResult.Failed, outcome.Result);
        var r = Assert.Single(_journal.ActiveRecords);
        Assert.False(r.DisplayMode!.PendingConfirm);
    }

    // ───────────────────────────── HDR ─────────────────────────────

    [Fact]
    public async Task Hdr_keep_confirms()
    {
        Assert.Equal(DisplayApplyResult.Kept, (await ApplyHdr(true)).Result);
        Assert.True(_display.Hdr);
        var r = Assert.Single(_journal.ActiveRecords);
        Assert.False(r.DisplayHdr!.PendingConfirm);
        Assert.Equal("HDR включён", r.NewValue);
        Assert.Equal("ОСТАВИТЬ HDR ВКЛЮЧЁННЫМ?", _asked.Single().Title);
    }

    [Fact]
    public async Task Hdr_revert_that_fails_keeps_the_record_even_when_the_monitor_is_gone()
    {
        // «gone» is only a read result: a failed write back must not be recorded as reverted.
        _display.HdrSetFailsFrom = 1; // the switch on works, the switch back fails
        NeverAnswer();
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            _display.Present = false;
        });
        var outcome = await ApplyHdr(true);

        Assert.Equal(DisplayApplyResult.Failed, outcome.Result);
        var r = Assert.Single(_journal.ActiveRecords); // kept for undo, not closed as «reverted»
        Assert.False(r.Reverted);
        Assert.Contains("не подтверждено", r.NewValue);
        Assert.False(_display.Guards.Single().Resolved); // the guard keeps trying
    }

    [Fact]
    public async Task Hdr_decline_and_timeout_revert()
    {
        Answer(false);
        Assert.Equal(DisplayApplyResult.Reverted, (await ApplyHdr(true)).Result);
        Assert.False(_display.Hdr);
        NeverAnswer();
        Assert.Equal(DisplayApplyResult.Reverted, (await ApplyHdr(true)).Result);
        Assert.False(_display.Hdr);
        Assert.Empty(_journal.Records);
    }

    // ───────────────────────────── Startup reconcile ─────────────────────────────

    private ChangeRecord Pending(DisplayModeSpec? original = null, DisplayModeSpec? target = null)
    {
        var r = new ChangeRecord
        {
            Source = DeviceTargets.Source, Title = "Режим", Kind = ChangeKind.DisplayMode, Target = DeviceTargets.DisplayMode(MonitorPath),
            DisplayMode = new DisplayModeSnapshot { MonitorPath = MonitorPath, MonitorName = "X27U", Original = original ?? Original, Target = target ?? Target, PendingConfirm = true },
        };
        _journal.Add(r);
        return r;
    }

    [Fact]
    public async Task Reconcile_closes_a_change_that_is_not_in_effect()
    {
        var r = Pending();
        await DisplaySafeApply.ReconcileAsync(_journal);
        Assert.True(r.Reverted);
        Assert.False(r.DisplayMode!.PendingConfirm);
    }

    [Fact]
    public async Task Reconcile_confirms_a_saved_target()
    {
        var r = Pending();
        _display.Current = Target;
        _display.Persisted = Target;
        await DisplaySafeApply.ReconcileAsync(_journal);
        Assert.False(r.Reverted);
        Assert.False(r.DisplayMode!.PendingConfirm);
    }

    [Fact]
    public async Task Reconcile_reverts_a_temporary_never_confirmed_target()
    {
        var r = Pending();
        _display.Current = Target;     // still in the temporary mode
        _display.Persisted = Original; // never saved
        await DisplaySafeApply.ReconcileAsync(_journal);
        Assert.True(r.Reverted);
        Assert.Equal(Original, _display.Current);
    }

    [Fact]
    public async Task Reconcile_leaves_an_unknown_state()
    {
        var r = Pending();
        _display.Current = Mode(1920, 1080, 60);
        _display.Persisted = Mode(1920, 1080, 60);
        await DisplaySafeApply.ReconcileAsync(_journal);
        Assert.False(r.Reverted);
        Assert.True(r.DisplayMode!.PendingConfirm);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reconcile_of_hdr_asks_again(bool keep)
    {
        var r = new ChangeRecord
        {
            Source = DeviceTargets.Source, Title = "HDR", Kind = ChangeKind.DisplayHdr, Target = DeviceTargets.DisplayHdr(MonitorPath),
            DisplayHdr = new DisplayHdrSnapshot { MonitorPath = MonitorPath, MonitorName = "X27U", OriginalEnabled = false, TargetEnabled = true, PendingConfirm = true },
        };
        _journal.Add(r);
        _display.Hdr = true;
        Answer(keep);

        await DisplaySafeApply.ReconcileAsync(_journal);

        Assert.Single(_asked);
        Assert.Equal(!keep, r.Reverted);
        Assert.Equal(keep, _display.Hdr);
    }

    // ───────────────────────────── Undo ─────────────────────────────

    private ChangeRecord Applied()
    {
        var r = Pending();
        _journal.ConfirmPending(r, Target.Describe());
        _display.Current = Target;
        _display.Persisted = Target;
        return r;
    }

    [Fact]
    public async Task Undo_with_the_monitor_missing_fails_and_keeps_the_record()
    {
        var r = Applied();
        _display.Present = false;
        var result = await _journal.RevertAsync([r]);
        Assert.False(result.Success);
        Assert.False(r.Reverted);
        Assert.Contains(result.Errors, e => e.Contains("монитор не подключён"));
    }

    [Fact]
    public async Task Undo_when_the_mode_was_changed_since_asks_first()
    {
        var r = Applied();
        _display.Current = Mode(1920, 1080, 60);
        DeviceConfirm.Ask = (_, _, _) => Task.FromResult(false);
        var result = await _journal.RevertAsync([r]);
        Assert.False(result.Success);
        Assert.False(r.Reverted);
        Assert.Equal(Mode(1920, 1080, 60), _display.Current);
    }

    [Fact]
    public async Task Undo_to_a_mode_no_longer_offered_fails()
    {
        var r = Applied();
        _display.Offered.RemoveAll(m => m.SameMode(Original));
        var result = await _journal.RevertAsync([r]);
        Assert.False(result.Success);
        Assert.False(r.Reverted);
        Assert.Equal(Target, _display.Current);
    }

    [Fact]
    public async Task Undo_kept_reverts_the_record()
    {
        var r = Applied();
        var result = await _journal.RevertAsync([r]);
        Assert.True(result.Success);
        Assert.True(r.Reverted);
        Assert.Equal(Original, _display.Current);
        Assert.Equal(Original, _display.Persisted);
    }

    [Fact]
    public async Task Undo_declined_goes_back_to_the_live_target_and_keeps_the_record()
    {
        var r = Applied();
        Answer(false);
        var result = await _journal.RevertAsync([r]);
        Assert.False(result.Success);
        Assert.False(r.Reverted);
        Assert.Equal(Target, _display.Current); // back to where the undo started, not to the record's original
        Assert.Contains(result.Errors, e => e.Contains("откат отменён"));
    }

    [Fact]
    public async Task Undo_through_the_self_test_lock_is_refused()
    {
        var r = Applied();
        ChangeSession.Locked = true;
        try
        {
            var result = await _journal.RevertAsync([r]);
            Assert.False(result.Success);
        }
        finally
        {
            ChangeSession.Locked = false;
        }
        Assert.False(r.Reverted);
        Assert.Equal(Target, _display.Current);
    }
}
