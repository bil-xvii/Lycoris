using System.Diagnostics;

namespace Lycoris.Core.Devices;

/// <summary>
/// The safe way to change a display mode or HDR (design devices_design.md §5; Windows gives API callers no safety net):
/// journal first (PendingConfirm) → an out-of-process guard is armed → the change is made temporarily → a 15-second
/// countdown on another monitor, default «go back» → keep = persist + confirm, anything else = revert + drop the
/// record. The guard reverts when Lycoris dies or the deadline passes; startup reconciles leftovers. The same
/// skeleton undoes a change (<see cref="RestoreModeAsync"/>, <see cref="RestoreHdrAsync"/>).
/// </summary>
internal static class DisplaySafeApply
{
    /// <summary>How long the user has to confirm (tests lower it).</summary>
    internal static TimeSpan ConfirmWindow = TimeSpan.FromSeconds(15);
    /// <summary>How much longer the guard waits than the countdown before it reverts on its own.</summary>
    internal static TimeSpan GuardSlack = TimeSpan.FromSeconds(20);
    /// <summary>How often the countdown checks that the monitor is still there.</summary>
    internal static TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private static string NoWriter => Loc.T("эта версия Lycoris не умеет менять параметры экрана");
    private static string GameBlocked => Loc.T("Во время игрового сеанса Lycoris не меняет параметры экрана");
    private static string GameBlockedUndo => Loc.T("во время игрового сеанса Lycoris не меняет параметры экрана — повторите откат после игры");
    private static string KeepModeTitle => Loc.T("ОСТАВИТЬ ЭТИ ПАРАМЕТРЫ ЭКРАНА?");

    private static DisplayApplyOutcome Failed(string message) => new(DisplayApplyResult.Failed, message);
    private static DisplayApplyOutcome Blocked(string message) => new(DisplayApplyResult.Blocked, message);

    private static CountdownRequest Request(string title, string message, string? avoidGdi) =>
        new(title, message, (int)Math.Ceiling(ConfirmWindow.TotalSeconds), CountdownRequest.Keep, CountdownRequest.Revert, avoidGdi)
        {
            Window = ConfirmWindow,
        };

    private static DateTime GuardDeadline() => DateTime.UtcNow + ConfirmWindow + GuardSlack;

    // ───────────────────────────── New change: mode ─────────────────────────────

    public static async Task<DisplayApplyOutcome> ApplyModeAsync(ChangeJournal journal, ChangeBatch batch, string monitorPath, string monitorName,
        DisplayModeSpec target, string source, string title, CancellationToken ct)
    {
        if (DeviceWriters.Display is not { } w) return Failed(NoWriter);
        if (GameBoostState.IsActive) return Blocked(GameBlocked);
        if (w.BlockReason() is { } why) return Blocked(why);
        if (!DeviceTargets.IsMonitorPath(monitorPath)) return Failed(Loc.T("некорректный путь монитора"));
        if (!target.IsSaneTarget) return Failed(Loc.T("недопустимый режим экрана"));

        var original = w.ReadCurrentMode(monitorPath);
        if (original is null) return Failed(Loc.T("монитор не найден"));
        if (original.SameMode(target)) return DisplayApplyOutcome.Unchanged;
        if (!w.IsModeOffered(monitorPath, target)) return Failed(Loc.T("режим не поддерживается монитором"));
        if (w.TestMode(monitorPath, target) is { Ok: false } test) return Failed(test.Message ?? Loc.T("драйвер видеокарты отклонил этот режим"));

        var record = new ChangeRecord
        {
            BatchId = batch.Id, Source = source, Title = title, Kind = ChangeKind.DisplayMode, Target = DeviceTargets.DisplayMode(monitorPath),
            OldValue = original.Describe(), NewValue = target.Describe() + Loc.T(" (не подтверждено)"),
            DisplayMode = new DisplayModeSnapshot
            {
                MonitorPath = monitorPath, MonitorName = monitorName, Original = original, Target = target, PendingConfirm = true,
            },
        };
        if (JournalTrust.Check(record) is { } refusal) return Failed(Loc.Tf("Lycoris не смог бы откатить это изменение ({0})", refusal));
        var (added, isNew) = journal.Add(record); // throws when the journal cannot be written: nothing changed
        if (!isNew || !ReferenceEquals(added, record)) return Failed(Loc.T("этот монитор уже меняется в этом пакете изменений"));

        using var guard = w.StartGuard(record.Id, GuardDeadline(), new DisplayGuardAction(DisplayGuardKind.Mode, monitorPath, false));
        if (guard is null)
        {
            journal.Remove(record);
            return Failed(Loc.T("не удалось запустить защитный процесс — параметры не изменены"));
        }

        var persistAttempted = false;
        try
        {
            var temp = w.ApplyTemporary(monitorPath, target);
            if (!temp.Ok)
            {
                w.RevertTemporary(monitorPath, original);
                guard.Resolve();
                journal.Remove(record);
                return Failed(temp.Message ?? Loc.T("режим не применился"));
            }

            var (keep, gone) = await CountdownWhilePresentAsync(Request(KeepModeTitle, $"{monitorName}: {target.Describe()}", w.GdiName(monitorPath)),
                () => w.ReadCurrentMode(monitorPath) is not null, ct).ConfigureAwait(false);

            if (keep && !gone)
            {
                persistAttempted = true;
                var persisted = w.Persist(monitorPath, target);
                var live = w.ReadCurrentMode(monitorPath);
                if (persisted.Ok && live is not null && live.SameMode(target))
                {
                    guard.Resolve();
                    journal.ConfirmPending(record, live.Describe());
                    Log.Success($"{title}: {monitorName} — {live.Describe()}");
                    return new DisplayApplyOutcome(DisplayApplyResult.Kept);
                }
                Log.Warn("{0}: режим не сохранился или не прошёл проверку ({1}) — возврат.", title, persisted.Message ?? live?.Describe() ?? Loc.T("монитор не отвечает"));
            }

            return RevertNewMode(journal, w, guard, record, monitorPath, original, persistAttempted, gone);
        }
        catch (Exception ex)
        {
            // Unknown state: try to go back; the guard is still armed and reverts at its deadline otherwise.
            Log.Warn("{0}: ошибка во время смены режима — возврат ({1}).", title, ex.Message);
            try { return RevertNewMode(journal, w, guard, record, monitorPath, original, persistAttempted, gone: false); }
            catch (Exception inner)
            {
                Log.Warn("{0}: возврат не удался ({1}); защитный процесс вернёт прежний режим.", title, inner.Message);
                if (!persistAttempted) journal.Remove(record);
                return Failed(ex.Message);
            }
        }
    }

    /// <summary>
    /// The revert branch of a new mode change. Before a persist attempt the change is only temporary: a temporary revert
    /// suffices. After one, the saved mode may already be the target, so the original is saved again and checked — and
    /// when that cannot be verified the record stays (no longer pending) so the change can still be undone.
    /// </summary>
    private static DisplayApplyOutcome RevertNewMode(ChangeJournal journal, IDisplayWriter w, IDisplayGuard guard, ChangeRecord record,
        string monitorPath, DisplayModeSpec original, bool persistAttempted, bool gone)
    {
        if (!persistAttempted)
        {
            var back = w.RevertTemporary(monitorPath, original);
            if (!back.Ok && !gone)
            {
                // The guard stays armed and reverts at its deadline; the temporary mode never survives a reboot either.
                journal.Remove(record);
                Log.Warn("Не удалось сразу вернуть режим экрана ({0}) — его вернёт защитный процесс.", back.Message);
                return Failed(Loc.T("не удалось сразу вернуть прежний режим — защитный процесс вернёт его в течение минуты"));
            }
            guard.Resolve();
            journal.Remove(record);
            return new DisplayApplyOutcome(DisplayApplyResult.Reverted, gone ? Loc.T("монитор пропал — возвращены прежние параметры") : Loc.T("возвращены прежние параметры"));
        }

        var saved = w.Persist(monitorPath, original);
        var live = w.ReadCurrentMode(monitorPath);
        var stored = w.ReadPersistedMode(monitorPath);
        guard.Resolve(); // its revert would return to the saved mode — that is the target now, so it cannot help
        if (saved.Ok && live is not null && live.SameMode(original) && (stored is null || stored.SameMode(original)))
        {
            journal.Remove(record);
            return new DisplayApplyOutcome(DisplayApplyResult.Reverted, Loc.T("возвращены прежние параметры"));
        }
        journal.ConfirmPending(record, (live?.Describe() ?? Loc.T("неизвестно")) + Loc.T(" (не прошло проверку)"));
        Log.Warn("«{0}»: прежний режим вернуть не удалось — запись оставлена в журнале для отката.", record.Title);
        return Failed(Loc.T("режим сохранился, но не прошёл проверку, а прежний вернуть не удалось — откатите запись в «Восстановлении»"));
    }

    // ───────────────────────────── New change: HDR ─────────────────────────────

    public static async Task<DisplayApplyOutcome> ApplyHdrAsync(ChangeJournal journal, ChangeBatch batch, string monitorPath, string monitorName,
        bool enable, string source, string title, CancellationToken ct)
    {
        if (DeviceWriters.Display is not { } w) return Failed(NoWriter);
        if (GameBoostState.IsActive) return Blocked(GameBlocked);
        if (w.BlockReason() is { } why) return Blocked(why);
        if (!DeviceTargets.IsMonitorPath(monitorPath)) return Failed(Loc.T("некорректный путь монитора"));

        if (w.ReadHdrEnabled(monitorPath) is not { } original) return Failed(Loc.T("HDR этого монитора недоступен"));
        if (original == enable) return DisplayApplyOutcome.Unchanged;
        var modeBefore = w.ReadCurrentMode(monitorPath);
        var record = new ChangeRecord
        {
            BatchId = batch.Id, Source = source, Title = title, Kind = ChangeKind.DisplayHdr, Target = DeviceTargets.DisplayHdr(monitorPath),
            OldValue = DeviceTargets.DescribeHdr(original), NewValue = DeviceTargets.DescribeHdr(enable) + Loc.T(" (не подтверждено)"),
            DisplayHdr = new DisplayHdrSnapshot
            {
                MonitorPath = monitorPath, MonitorName = monitorName, OriginalEnabled = original, TargetEnabled = enable, PendingConfirm = true,
                ModeBefore = modeBefore is { IsSane: true } ? modeBefore : null, AcmBefore = w.ReadAcmEnabled(monitorPath),
            },
        };
        if (JournalTrust.Check(record) is { } refusal) return Failed(Loc.Tf("Lycoris не смог бы откатить это изменение ({0})", refusal));
        var (added, isNew) = journal.Add(record);
        if (!isNew || !ReferenceEquals(added, record)) return Failed(Loc.T("HDR этого монитора уже меняется в этом пакете изменений"));

        using var guard = w.StartGuard(record.Id, GuardDeadline(), new DisplayGuardAction(DisplayGuardKind.Hdr, monitorPath, original));
        if (guard is null)
        {
            journal.Remove(record);
            return Failed(Loc.T("не удалось запустить защитный процесс — HDR не изменён"));
        }

        try
        {
            var set = w.SetHdr(monitorPath, enable);
            if (!set.Ok || w.ReadHdrEnabled(monitorPath) != enable)
            {
                if (w.ReadHdrEnabled(monitorPath) != original) w.SetHdr(monitorPath, original);
                guard.Resolve();
                journal.Remove(record);
                return Failed(set.Ok ? Loc.T("драйвер видеокарты вернул прежнее состояние HDR") : set.Message ?? Loc.T("HDR не переключился"));
            }

            var message = $"{monitorName}: {DeviceTargets.DescribeHdr(enable)}";
            if (modeBefore is not null && w.ReadCurrentMode(monitorPath) is { } after && !after.SameMode(modeBefore))
                message += Loc.Tf(". Частота изменилась: {0} → {1}", modeBefore.HzText(), after.HzText());
            var (keep, gone) = await CountdownWhilePresentAsync(
                Request(enable ? Loc.T("ОСТАВИТЬ HDR ВКЛЮЧЁННЫМ?") : Loc.T("ОСТАВИТЬ HDR ВЫКЛЮЧЕННЫМ?"), message, w.GdiName(monitorPath)),
                () => w.ReadHdrEnabled(monitorPath) is not null, ct).ConfigureAwait(false);

            if (keep && !gone)
            {
                guard.Resolve(); // Windows saves the HDR state at once: nothing to persist
                journal.ConfirmPending(record, DeviceTargets.DescribeHdr(enable));
                return new DisplayApplyOutcome(DisplayApplyResult.Kept);
            }
            return RevertNewHdr(journal, w, guard, record, monitorPath, original, gone);
        }
        catch (Exception ex)
        {
            Log.Warn("{0}: ошибка во время переключения HDR — возврат ({1}).", title, ex.Message);
            try { return RevertNewHdr(journal, w, guard, record, monitorPath, original, gone: false); }
            catch { return Failed(ex.Message); }
        }
    }

    private static DisplayApplyOutcome RevertNewHdr(ChangeJournal journal, IDisplayWriter w, IDisplayGuard guard, ChangeRecord record,
        string monitorPath, bool original, bool gone)
    {
        // «gone» is only a read result: the record is closed only when the write back succeeded (read back when it can be).
        var back = w.SetHdr(monitorPath, original);
        var after = back.Ok ? w.ReadHdrEnabled(monitorPath) : null;
        // A monitor that is gone cannot be read back: a successful write counts, a failed one never does.
        var reverted = back.Ok && (after == original || (gone && after is null));
        if (reverted)
        {
            guard.Resolve();
            journal.Remove(record);
            return new DisplayApplyOutcome(DisplayApplyResult.Reverted, gone ? Loc.T("монитор пропал — HDR возвращён") : Loc.T("возвращено прежнее состояние HDR"));
        }
        // The guard stays armed and tries again at its deadline; the record stays so the change can be undone.
        journal.ConfirmPending(record, DeviceTargets.DescribeHdr(!original) + Loc.T(" (не подтверждено)"));
        return Failed(gone
            ? Loc.T("монитор пропал, а HDR вернуть не удалось — защитный процесс попробует ещё раз; если не выйдет, откатите запись в «Восстановлении»")
            : Loc.T("не удалось вернуть HDR — защитный процесс попробует ещё раз; если не выйдет, откатите запись в «Восстановлении»"));
    }

    // ───────────────────────────── Countdown ─────────────────────────────

    /// <summary>The countdown, cancelled (= go back) as soon as <paramref name="present"/> reports the monitor gone.</summary>
    private static async Task<(bool Keep, bool Gone)> CountdownWhilePresentAsync(CountdownRequest request, Func<bool> present, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gone = false;
        var countdown = DeviceConfirm.CountdownAsync(request, cts.Token);
        var poll = Task.Run(async () =>
        {
            while (!countdown.IsCompleted)
            {
                try { await Task.WhenAny(countdown, Task.Delay(PollInterval, CancellationToken.None)).ConfigureAwait(false); }
                catch { }
                if (countdown.IsCompleted) break;
                bool stillThere;
                try { stillThere = present(); }
                catch { stillThere = false; }
                if (!stillThere)
                {
                    gone = true;
                    try { cts.Cancel(); } catch { }
                    break;
                }
            }
        }, CancellationToken.None);
        var keep = await countdown.ConfigureAwait(false);
        await poll.ConfigureAwait(false);
        return (keep && !gone, gone);
    }

    // ───────────────────────────── Undo ─────────────────────────────

    /// <summary>
    /// Undo of a mode change: the original must still be offered and pass CDS_TEST; it is applied temporarily and
    /// confirmed like a new change. A decline returns to the mode that was live when the undo started (normally the
    /// record's target) and fails the undo, so the record stays active.
    /// </summary>
    public static async Task<RevertOutcome> RestoreModeAsync(ChangeRecord r)
    {
        var snap = r.DisplayMode ?? throw new InvalidOperationException(Loc.T("Запись журнала не содержит данных для отката."));
        if (DeviceWriters.Display is not { } w) throw new InvalidOperationException(DeviceWriters.NoWriterRevert);
        if (GameBoostState.IsActive) throw new InvalidOperationException(GameBlockedUndo);
        if (w.BlockReason() is { } why) throw new InvalidOperationException(why);
        var path = snap.MonitorPath;

        var live = w.ReadCurrentMode(path) ?? throw new InvalidOperationException(Loc.T("монитор не подключён — подключите его и повторите откат"));
        if (live.SameMode(snap.Original))
        {
            // Already back (e.g. never confirmed): make sure the saved mode is the original too.
            if (w.ReadPersistedMode(path) is { } stored && !stored.SameMode(snap.Original) && w.IsModeOffered(path, snap.Original))
                w.Persist(path, snap.Original);
            return RevertOutcome.Done;
        }
        if (!live.SameMode(snap.Target)
            && !await DeviceConfirm.AskAsync(Loc.T("Вернуть прежние параметры экрана?"),
                Loc.Tf("{0}: сейчас {1}, а Lycoris устанавливал {2}. Вернуть {3}?", snap.MonitorName, live.Describe(), snap.Target.Describe(), snap.Original.Describe()),
                []).ConfigureAwait(false))
            throw new InvalidOperationException(Loc.T("откат отменён — оставлены текущие параметры"));
        if (!w.IsModeOffered(path, snap.Original) || !w.TestMode(path, snap.Original).Ok)
            throw new InvalidOperationException(Loc.Tf("прежний режим {0} монитор больше не поддерживает", snap.Original.Describe()));

        var revertTo = live; // where a decline, a timeout or the guard goes back to
        using var guard = w.StartGuard(r.Id, GuardDeadline(), new DisplayGuardAction(DisplayGuardKind.Mode, path, false))
                          ?? throw new InvalidOperationException(Loc.T("не удалось запустить защитный процесс — параметры не изменены"));
        var temp = w.ApplyTemporary(path, snap.Original);
        if (!temp.Ok)
        {
            w.RevertTemporary(path, revertTo);
            guard.Resolve();
            throw new InvalidOperationException(temp.Message ?? Loc.T("прежний режим не применился"));
        }

        var (keep, gone) = await CountdownWhilePresentAsync(Request(KeepModeTitle, $"{snap.MonitorName}: {snap.Original.Describe()}", w.GdiName(path)),
            () => w.ReadCurrentMode(path) is not null, CancellationToken.None).ConfigureAwait(false);
        if (keep && !gone)
        {
            var saved = w.Persist(path, snap.Original);
            if (saved.Ok && w.ReadCurrentMode(path) is { } now && now.SameMode(snap.Original))
            {
                guard.Resolve();
                return RevertOutcome.Done;
            }
            // Could not save the original: go back to where the undo started (saved again) and keep the record.
            w.Persist(path, revertTo);
            guard.Resolve();
            throw new InvalidOperationException(Loc.T("прежний режим не сохранился — оставлены текущие параметры"));
        }

        w.RevertTemporary(path, revertTo);
        guard.Resolve();
        throw new InvalidOperationException(gone ? Loc.T("монитор пропал во время отката — оставлены текущие параметры") : Loc.T("откат отменён — оставлены текущие параметры"));
    }

    /// <summary>Undo of an HDR switch, with the same confirmation; a decline returns to the state from the start of the undo.</summary>
    public static async Task<RevertOutcome> RestoreHdrAsync(ChangeRecord r)
    {
        var snap = r.DisplayHdr ?? throw new InvalidOperationException(Loc.T("Запись журнала не содержит данных для отката."));
        if (DeviceWriters.Display is not { } w) throw new InvalidOperationException(DeviceWriters.NoWriterRevert);
        if (GameBoostState.IsActive) throw new InvalidOperationException(GameBlockedUndo);
        if (w.BlockReason() is { } why) throw new InvalidOperationException(why);
        var path = snap.MonitorPath;

        var live = w.ReadHdrEnabled(path) ?? throw new InvalidOperationException(Loc.T("монитор не подключён — подключите его и повторите откат"));
        if (live == snap.OriginalEnabled) return SideEffectNote(w, snap);

        using var guard = w.StartGuard(r.Id, GuardDeadline(), new DisplayGuardAction(DisplayGuardKind.Hdr, path, live))
                          ?? throw new InvalidOperationException(Loc.T("не удалось запустить защитный процесс — HDR не изменён"));
        var set = w.SetHdr(path, snap.OriginalEnabled);
        if (!set.Ok || w.ReadHdrEnabled(path) != snap.OriginalEnabled)
        {
            w.SetHdr(path, live);
            guard.Resolve();
            throw new InvalidOperationException(set.Message ?? Loc.T("HDR не переключился"));
        }

        var (keep, gone) = await CountdownWhilePresentAsync(
            Request(snap.OriginalEnabled ? Loc.T("ОСТАВИТЬ HDR ВКЛЮЧЁННЫМ?") : Loc.T("ОСТАВИТЬ HDR ВЫКЛЮЧЕННЫМ?"),
                $"{snap.MonitorName}: {DeviceTargets.DescribeHdr(snap.OriginalEnabled)}", w.GdiName(path)),
            () => w.ReadHdrEnabled(path) is not null, CancellationToken.None).ConfigureAwait(false);
        if (keep && !gone)
        {
            guard.Resolve();
            return SideEffectNote(w, snap);
        }
        w.SetHdr(path, live);
        guard.Resolve();
        throw new InvalidOperationException(Loc.T("откат отменён — оставлено текущее состояние HDR"));
    }

    /// <summary>After an HDR undo: did the mode or colour management come back too? A note says what to check (no extra write).</summary>
    private static RevertOutcome SideEffectNote(IDisplayWriter w, DisplayHdrSnapshot snap)
    {
        var notes = new List<string>();
        if (snap.ModeBefore is { } before && w.ReadCurrentMode(snap.MonitorPath) is { } now && !now.SameMode(before))
            notes.Add(Loc.Tf("Режим экрана не вернулся к {0} (сейчас {1}) — выберите его в «Устройствах и сети».", before.Describe(), now.Describe()));
        if (snap.AcmBefore is { } acm && w.ReadAcmEnabled(snap.MonitorPath) is { } acmNow && acmNow != acm)
            notes.Add(Loc.T("Автоматическое управление цветом изменилось вместе с HDR — проверьте его в Параметрах → Дисплей."));
        return notes.Count == 0 ? RevertOutcome.Done : new RevertOutcome(string.Join(" ", notes));
    }

    // ───────────────────────────── Startup reconcile ─────────────────────────────

    /// <summary>
    /// Records left pending by a crash (Lycoris or the PC died during a countdown). A pending change was never
    /// confirmed, so it is reverted — except when the saved mode is already the target (the keep was saved, only the
    /// journal write was lost). HDR has no temporary state: the user is asked again (default: go back). Runs elevated,
    /// off the UI thread, after any running guard process has finished.
    /// </summary>
    public static async Task ReconcileAsync(ChangeJournal journal)
    {
        var all = journal.PendingDisplayRecords();
        // Input parameters applied with a countdown (button swap, a very low pointer speed) are persisted at once: an
        // unconfirmed one is simply reverted through the journal.
        foreach (var r in all.Where(r => r.Spi is { PendingConfirm: true }))
        {
            try { await ReconcileSpiAsync(journal, r).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("«{0}»: незавершённое изменение параметра не проверено — {1}", r.Title, ex.Message); }
        }
        var pending = all.Where(r => r.DisplayMode is not null || r.DisplayHdr is not null).ToList();
        if (pending.Count == 0) return;
        await DisplayGuardProcess.WaitForGuardsAsync(ConfirmWindow + GuardSlack + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (DeviceWriters.Display is not { } w)
        {
            Log.Warn("Незавершённые изменения экрана: {0} — эта версия Lycoris не умеет их проверить.", pending.Count);
            return;
        }
        foreach (var r in pending)
        {
            try
            {
                if (r.DisplayMode is { } mode) ReconcileMode(journal, w, r, mode);
                else if (r.DisplayHdr is { } hdr) await ReconcileHdrAsync(journal, w, r, hdr).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("«{0}»: незавершённое изменение экрана не проверено — {1}", r.Title, ex.Message);
            }
        }
    }

    private static string NotConfirmed => Loc.T("Изменение не было подтверждено — прежние параметры на месте.");

    /// <summary>An input parameter left pending by a crash: reverted (the journal's own undo, which re-validates the record and the account).</summary>
    internal static async Task ReconcileSpiAsync(ChangeJournal journal, ChangeRecord r)
    {
        if (JournalTrust.Check(r) is { } refusal)
        {
            Log.Warn("«{0}»: незавершённое изменение параметра пропущено — {1}.", r.Title, refusal);
            return;
        }
        var result = await journal.RevertAsync([r]).ConfigureAwait(false);
        if (result.Success && r.Reverted)
            Log.Info("«{0}»: изменение не было подтверждено (Lycoris закрылся во время обратного отсчёта) — возвращено прежнее значение.", r.Title);
        else
            Log.Warn("«{0}»: неподтверждённое изменение вернуть не удалось ({1}) — откатите запись в «Восстановлении».", r.Title, string.Join("; ", result.Errors));
    }

    internal static void ReconcileMode(ChangeJournal journal, IDisplayWriter w, ChangeRecord r, DisplayModeSnapshot snap)
    {
        if (JournalTrust.Check(r) is { } refusal)
        {
            Log.Warn("«{0}»: незавершённое изменение экрана пропущено — {1}.", r.Title, refusal);
            return;
        }
        var live = w.ReadCurrentMode(snap.MonitorPath);
        var stored = w.ReadPersistedMode(snap.MonitorPath);
        if (live is null)
        {
            Log.Warn("«{0}»: монитор не подключён — незавершённое изменение экрана проверим при следующем запуске.", r.Title);
            return;
        }
        if (live.SameMode(snap.Original) && (stored is null || stored.SameMode(snap.Original)))
        {
            journal.ClosePending(r, NotConfirmed);
            return;
        }
        if (live.SameMode(snap.Target) && stored is not null && stored.SameMode(snap.Target))
        {
            journal.ConfirmPending(r, live.Describe()); // the keep was saved; only the journal write was lost
            return;
        }
        if (live.SameMode(snap.Target) && stored is not null && stored.SameMode(snap.Original))
        {
            // Still in the temporary, never confirmed mode: go back to the saved original.
            if (w.RevertTemporary(snap.MonitorPath, snap.Original).Ok && w.ReadCurrentMode(snap.MonitorPath) is { } back && back.SameMode(snap.Original))
            {
                journal.ClosePending(r, Loc.T("Изменение не было подтверждено — возвращены прежние параметры."));
                return;
            }
        }
        Log.Warn("«{0}»: состояние экрана после сбоя не совпадает ни с прежним, ни с новым — запись оставлена, откатите её в «Восстановлении».", r.Title);
    }

    private static async Task ReconcileHdrAsync(ChangeJournal journal, IDisplayWriter w, ChangeRecord r, DisplayHdrSnapshot snap)
    {
        if (JournalTrust.Check(r) is { } refusal)
        {
            Log.Warn("«{0}»: незавершённое изменение HDR пропущено — {1}.", r.Title, refusal);
            return;
        }
        var live = w.ReadHdrEnabled(snap.MonitorPath);
        if (live is null)
        {
            Log.Warn("«{0}»: монитор не подключён — незавершённое изменение HDR проверим при следующем запуске.", r.Title);
            return;
        }
        if (live == snap.OriginalEnabled)
        {
            journal.ClosePending(r, NotConfirmed);
            return;
        }
        // HDR is saved at once, so «on» does not mean «confirmed»: ask again, default back.
        var keep = await DeviceConfirm.CountdownAsync(
            Request(snap.TargetEnabled ? Loc.T("ОСТАВИТЬ HDR ВКЛЮЧЁННЫМ?") : Loc.T("ОСТАВИТЬ HDR ВЫКЛЮЧЕННЫМ?"),
                Loc.Tf("{0}: изменение HDR не было подтверждено перед сбоем. Оставить?", snap.MonitorName), w.GdiName(snap.MonitorPath)),
            CancellationToken.None).ConfigureAwait(false);
        if (keep)
        {
            journal.ConfirmPending(r, DeviceTargets.DescribeHdr(snap.TargetEnabled));
            return;
        }
        if (w.SetHdr(snap.MonitorPath, snap.OriginalEnabled).Ok && w.ReadHdrEnabled(snap.MonitorPath) == snap.OriginalEnabled)
            journal.ClosePending(r, Loc.T("Изменение HDR не было подтверждено — возвращено прежнее состояние."));
        else
            Log.Warn("«{0}»: не удалось вернуть HDR — запись оставлена, откатите её в «Восстановлении».", r.Title);
    }
}

/// <summary>
/// The «--display-guard» process marks itself with a named event, so a new Lycoris never treats it as a hung copy
/// (and never offers to end it) and waits for it before reconciling pending display changes.
/// </summary>
public static class DisplayGuardProcess
{
    public const string Switch = "--display-guard";

    public static string MarkerName(int pid) => $@"Local\Lycoris-DisplayGuard-{pid}";

    /// <summary>The guard's first action: creates its marker (kept for the process lifetime).</summary>
    public static IDisposable? MarkCurrentProcess()
    {
        try { return new EventWaitHandle(false, EventResetMode.ManualReset, MarkerName(Environment.ProcessId)); }
        catch { return null; }
    }

    /// <summary>True when the process with this id is a display guard (an elevated guard's marker may be unreadable: still a guard).</summary>
    public static bool IsGuard(int pid)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(MarkerName(pid), out var handle)) return false;
            handle.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Running guard processes (other Lycoris processes carrying the marker).</summary>
    public static IReadOnlyList<int> RunningGuards()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var result = new List<int>();
            foreach (var p in Process.GetProcessesByName(self.ProcessName))
            {
                using (p)
                {
                    if (p.Id != self.Id && IsGuard(p.Id)) result.Add(p.Id);
                }
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Waits (at most <paramref name="max"/>) until no guard runs: each ends by itself by its deadline.</summary>
    public static async Task WaitForGuardsAsync(TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (RunningGuards().Count > 0 && DateTime.UtcNow < until)
            await Task.Delay(500).ConfigureAwait(false);
    }
}
