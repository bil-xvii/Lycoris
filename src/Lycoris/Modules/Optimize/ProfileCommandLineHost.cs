using Lycoris.Core;
using Lycoris.Tweaks;

namespace Lycoris.Modules.Optimize;

/// <summary>
/// The real system behind «--apply-profile»: detection and selection are the Optimize page's own (<see cref="TweakEngine.DetectAllAsync"/>
/// + <see cref="OptimizeViewModel.PendingForProfile"/>), the apply is <see cref="TweakEngine.ApplyAsync"/> (ChangeSession: journal,
/// .reg backups, restore point) under the <see cref="OperationClass.JournalChange"/> lease — no second write path.
/// Create and call on the dispatcher thread (the page view-model owns DispatcherTimers).
/// </summary>
internal sealed class ProfileCommandLineHost(Func<bool> tryLockInstance) : IProfileCliHost
{
    public bool IsElevated => Elevation.IsElevated;

    public async Task<ProfileCliSelection> SelectAsync(OptimizeProfile profile)
    {
        var sp = await SystemProfile.LoadAsync();
        var tweaks = TweakCatalog.All;
        var excluded = await Task.Run(() => tweaks.Where(t => TweakEngine.Applicability(t, sp) == false).Select(t => t.Id).ToList());
        var states = await TweakEngine.DetectAllAsync(tweaks);

        var page = new OptimizeViewModel();
        page.BuildCatalog(tweaks);
        var pending = page.PendingForProfile(profile, states, excluded);
        var defaults = TweakCatalog.DefaultsFor(OptimizeLogic.ToKind(profile)!.Value).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var pendingIds = pending.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        int applied = 0, notApplicable = 0;
        foreach (var id in defaults)
        {
            if (pendingIds.Contains(id) || !states.TryGetValue(id, out var s)) continue;
            if (s == TweakState.Applied) applied++;
            else if (s == TweakState.NotApplicable) notApplicable++;
        }
        return new ProfileCliSelection(pending.Select(p => new ProfileCliTarget(p.Tweak, p.State ?? TweakState.Unknown)).ToList(),
            applied, notApplicable, sp.HardwareFactsKnown && sp.IsLaptop);
    }

    public IReadOnlyList<ProfileCliLine> ReadChanges(Tweak tweak) =>
        OptimizeLogic.PlannedChanges(tweak).Select(p => new ProfileCliLine(p.Target, SafeRead(p.ReadCurrent), p.NewValue)).ToList();

    private static string SafeRead(Func<string>? read)
    {
        try { return read?.Invoke() ?? "—"; }
        catch { return Loc.T("не удалось прочитать"); }
    }

    public Task<string> RestorePointNoteAsync() => OptimizeViewModel.RestorePointFootnoteAsync();

    public bool TryLockInstance() => tryLockInstance();

    public async Task<TweakRunResult> ApplyAsync(IReadOnlyList<Tweak> tweaks, string batchTitle, IProgress<string> progress)
    {
        // As at an elevated start (App.OnStartup): lock %ProgramData%\Lycoris down before the journal is touched, and repair
        // the restore-point frequency an interrupted run may have left lifted.
        AppPaths.SecureMachineDir();
        RestorePointService.RecoverPendingFrequency();
        return await OperationCoordinator.Instance.RunAsync(
            new OperationRequest(OperationClass.JournalChange, Loc.T("Применение изменений…")) { Owner = "optimize" },
            _ => TweakEngine.ApplyAsync(tweaks, batchTitle, progress));
    }

    public async Task<IReadOnlyDictionary<string, TweakState>> DetectAsync(IReadOnlyList<Tweak> tweaks) =>
        await TweakEngine.DetectAllAsync(tweaks);
}
