using Lycoris.Core;
using Lycoris.Tweaks;

namespace Lycoris.Modules.Optimize;

/// <summary>
/// The real system behind «--apply-profile»: detection and selection are the Optimize page's own (<see cref="TweakEngine.DetectAllAsync"/>
/// + <see cref="OptimizeViewModel.PendingForProfile"/>), the apply is <see cref="TweakEngine.ApplyAsync"/> (ChangeSession: journal,
/// .reg backups, restore point) under the <see cref="OperationClass.JournalChange"/> lease — no second write path.
/// Create and call on the dispatcher thread (the page view-model owns DispatcherTimers).
/// </summary>
internal sealed class ProfileCommandLineHost(Func<bool> tryLockInstance, Func<string?> tryLockJournal) : IProfileCliHost
{
    private PowerPlanIntent? _powerPlan;

    public bool IsElevated => Elevation.IsElevated;

    public async Task<ProfileCliSelection> SelectAsync(OptimizeProfile profile)
    {
        var sp = await SystemProfile.LoadAsync();
        var tweaks = TweakCatalog.All;
        var excluded = await Task.Run(() => tweaks.Where(t => TweakEngine.Applicability(t, sp) is not true).Select(t => t.Id).ToList());
        var states = await TweakEngine.DetectAllAsync(tweaks, sp);

        var page = new OptimizeViewModel();
        page.BuildCatalog(tweaks);
        var hardwareFactsKnown = sp.HardwareFactsKnown && sp.Identity.IsKnown;
        page.MarkDetected(hardwareFactsKnown, hardwareFactsKnown && sp.IsLaptop);
        var pending = page.PendingForProfile(profile, states, excluded);
        _powerPlan = page.CapturePowerPlanIntent(pending.Select(p => p.Tweak).ToList());
        var defaults = TweakCatalog.DefaultsFor(OptimizeLogic.ToKind(profile)!.Value).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var pendingIds = pending.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var applied = page.Items.Count(i => i.IsChecked && i.IsApplied && !i.IsPending);
        int notApplicable = 0;
        foreach (var id in defaults)
        {
            if (pendingIds.Contains(id) || !states.TryGetValue(id, out var s)) continue;
            if (s == TweakState.NotApplicable) notApplicable++;
        }
        return new ProfileCliSelection(pending.Select(p => new ProfileCliTarget(p.Tweak, p.State ?? TweakState.Unknown)).ToList(),
            applied, notApplicable, hardwareFactsKnown && sp.IsLaptop);
    }

    public IReadOnlyList<ProfileCliLine> ReadChanges(Tweak tweak) =>
        _powerPlan?.SwitchPending == true && tweak.UsesActivePowerPlan
            ? [new(Loc.T("состояние"), Loc.T("проверится после переключения схемы"), OptimizeLogic.StateText(TweakState.Applied))]
            : OptimizeLogic.PlannedChanges(tweak).Select(p => new ProfileCliLine(p.Target, SafeRead(p.ReadCurrent), p.NewValue)).ToList();

    private static string SafeRead(Func<string>? read)
    {
        try { return read?.Invoke() ?? "—"; }
        catch { return Loc.T("не удалось прочитать"); }
    }

    public Task<string> RestorePointNoteAsync() => OptimizeViewModel.RestorePointFootnoteAsync();

    public bool TryLockInstance() => tryLockInstance();

    public string? TryLockJournal() => tryLockJournal();

    public async Task<TweakRunResult> ApplyAsync(IReadOnlyList<Tweak> tweaks, string batchTitle, IProgress<string> progress)
    {
        var powerPlan = _powerPlan;
        return await OperationCoordinator.Instance.RunAsync(
            new OperationRequest(OperationClass.JournalChange, Loc.T("Применение изменений…")) { Owner = "optimize" },
            _ => TweakEngine.ApplyWithPowerPlanAsync(tweaks, batchTitle, powerPlan, progress, PrepareJournal));
    }

    private static void PrepareJournal()
    {
        // The machine writer lease is held. Recover after the captured plan has been validated.
        RestorePointService.RecoverPendingFrequency();
    }

    public async Task<IReadOnlyDictionary<string, TweakState>> DetectAsync(IReadOnlyList<Tweak> tweaks) =>
        await TweakEngine.DetectAllAsync(tweaks);
}
