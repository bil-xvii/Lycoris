using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.Modules.Removal;

namespace Lycoris.Modules.Startup;

/// <summary>Result of a batch of autostart changes.</summary>
public sealed record StartupChangeOutcome(int Applied, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Every autostart change goes through <see cref="ChangeSession"/> (journal + .reg backup + optional restore point).
/// Disabling/enabling only writes the mark Task Manager uses — the StartupApproved REG_BINARY for Run / Startup-folder
/// entries, the DWORD <c>State</c> (2 on, 1 off) for packaged apps' startup tasks. Run values, shortcuts and packages
/// are never deleted, packaged task keys are never created, policy-managed tasks are never written.
/// When an entry is switched back to the state it had before Lycoris touched it, the journal record is reverted
/// instead (the exact original value comes back and the journal stays clean).
/// </summary>
public static class StartupChanges
{
    public const string EntrySource = "startup";
    public const string TaskSource = "startup-tasks";

    public static string EntryRecordTitle(StartupEntry e) => string.Format(Loc.Keep("Автозапуск: {0}"), e.DisplayName); // journal data: always Russian

    public static string TaskRecordTitle(LogonTask t) => string.Format(Loc.Keep("Автозапуск: задача «{0}»"), t.Name);

    public static string BatchTitle(IReadOnlyList<string> names, bool enable)
    {
        var verb = enable ? Loc.Keep("включение") : Loc.Keep("отключение");
        return names.Count == 1 ? string.Format(Loc.Keep("Автозапуск: {0} «{1}»"), verb, names[0]) : string.Format(Loc.Keep("Автозапуск: {0} ({1} шт.)"), verb, names.Count);
    }

    public static async Task<StartupChangeOutcome> SetEntriesAsync(IReadOnlyList<StartupEntry> entries, bool enable,
        bool createRestorePoint, IProgress<string>? progress = null)
    {
        var journal = ChangeJournal.Instance;
        var errors = new List<string>();
        var applied = 0;

        var revert = new List<(IReadOnlyList<ChangeRecord> Layers, Func<string?>? Refusal)>();
        var write = new List<StartupEntry>();
        foreach (var e in entries)
        {
            var packaged = StartupLocations.IsPackaged(e.Source);
            if (!enable && EntryDisableRefusal(e) is { } refused)
            {
                errors.Add($"{e.DisplayName}: {refused}");
                continue;
            }
            // Packaged tasks are checked against the live value right before anything is written (undo included): the
            // package may have been removed or put under policy since the scan. Nothing is written then — no key is created.
            if (packaged && PackagedWriteRefusal(e) is { } refusal)
            {
                errors.Add($"{e.DisplayName}: {refusal}.");
                continue;
            }
            // Back to how it was before Lycoris: undo every layer of this entry instead of stacking a new one.
            var layers = journal.ActiveLayers(StartupLocations.JournalTarget(e));
            if (UndoesLayers(e, enable, layers))
            {
                revert.Add((layers, enable ? null : () => EntryDisableRefusal(e)));
                continue;
            }
            if (packaged && PackagedStartup.IsEnabled(PackagedStartup.ReadState(e.PackageFamilyName!, e.Name)) == enable) continue;
            write.Add(e);
        }

        if (write.Count > 0)
        {
            var session = await ChangeSession.BeginAsync(BatchTitle(write.Select(w => w.DisplayName).ToList(), enable),
                createRestorePoint, progress).ConfigureAwait(false);
            foreach (var e in write)
            {
                progress?.Report(enable ? Loc.Tf("Включение: {0}", e.DisplayName) : Loc.Tf("Отключение: {0}", e.DisplayName));
                if (!enable)
                    DisableEntry(session, e, errors);
                else if (StartupLocations.IsPackaged(e.Source))
                    // Re-check the live state inside the journal gate: the app may have been removed or put under policy.
                    session.Exclusive(() =>
                    {
                        if (PackagedWriteRefusal(e) is { } late) { errors.Add($"{e.DisplayName}: {late}."); return; }
                        session.SetRegistry(RegHive.HKCU, PackagedStartup.KeyPath(e.PackageFamilyName!, e.Name), PackagedStartup.StateValue,
                            RegistryValueKind.DWord, PackagedStartup.TargetState(enable), EntrySource, EntryRecordTitle(e));
                    });
                else
                    session.SetRegistry(StartupLocations.ApprovedHive(e.Source), StartupLocations.ApprovedKey(e.Source), e.Name,
                        RegistryValueKind.Binary, StartupApproved.Build(enable), EntrySource, EntryRecordTitle(e));
            }
            session.Complete();
            applied += session.Applied;
            errors.AddRange(session.Errors);
        }

        if (revert.Count > 0)
        {
            progress?.Report(Loc.T("Возврат исходного состояния…"));
            applied += await ApplyRevertsAsync(journal, revert, errors).ConfigureAwait(false);
        }

        return new StartupChangeOutcome(applied, errors);
    }

    public static async Task<StartupChangeOutcome> SetTasksAsync(IReadOnlyList<LogonTask> tasks, bool enable,
        bool createRestorePoint, IProgress<string>? progress = null)
    {
        var journal = ChangeJournal.Instance;
        var errors = new List<string>();
        var applied = 0;

        var revert = new List<(IReadOnlyList<ChangeRecord> Layers, Func<string?>? Refusal)>();
        var write = new List<LogonTask>();
        foreach (var t in tasks)
        {
            if (!enable && TaskDisableRefusal(t) is { } refused)
            {
                errors.Add($"{t.Name}: {refused}");
                continue;
            }
            var layers = journal.ActiveLayers($"task:{t.Path}");
            if (layers.Count > 0 && layers[0].TaskWasEnabled is { } wasEnabled && wasEnabled == enable)
                revert.Add((layers, enable ? null : () => TaskDisableRefusal(t)));
            else write.Add(t);
        }

        if (write.Count > 0)
        {
            var session = await ChangeSession.BeginAsync(BatchTitle(write.Select(w => w.Name).ToList(), enable),
                createRestorePoint, progress).ConfigureAwait(false);
            foreach (var t in write)
            {
                progress?.Report(enable ? Loc.Tf("Включение задачи: {0}", t.Name) : Loc.Tf("Отключение задачи: {0}", t.Name));
                session.Exclusive(() =>
                {
                    if (!enable && TaskDisableRefusal(t) is { } late) { errors.Add($"{t.Name}: {late}"); return; }
                    session.SetTaskEnabled(t.Path, enable, TaskSource, TaskRecordTitle(t));
                });
            }
            session.Complete();
            applied += session.Applied;
            errors.AddRange(session.Errors);
        }

        if (revert.Count > 0)
        {
            progress?.Report(Loc.T("Возврат исходного состояния…"));
            applied += await ApplyRevertsAsync(journal, revert, errors).ConfigureAwait(false);
        }

        return new StartupChangeOutcome(applied, errors);
    }

    private static void DisableEntry(ChangeSession session, StartupEntry entry, List<string> errors)
    {
        bool Check(RegistryValueSnapshot snapshot)
        {
            var refusal = EntryDisableRefusal(entry);
            if (refusal is null)
            {
                var markMatches = StartupLocations.IsPackaged(entry.Source)
                    ? PackagedStartup.OriginalState(snapshot) == entry.PackageState
                    : StartupDisableObservation.ReadApproval(entry) is { IsEnabled: true } mark && mark.Matches(snapshot);
                if (markMatches) return true;
            }
            errors.Add($"{entry.DisplayName}: {refusal ?? StartupDisableGuard.Changed}");
            return false;
        }

        if (StartupLocations.IsPackaged(entry.Source))
            session.SetRegistry(RegHive.HKCU, PackagedStartup.KeyPath(entry.PackageFamilyName!, entry.Name), PackagedStartup.StateValue,
                RegistryValueKind.DWord, PackagedStartup.TargetState(false), EntrySource, EntryRecordTitle(entry), Check);
        else
            session.SetRegistry(StartupLocations.ApprovedHive(entry.Source), StartupLocations.ApprovedKey(entry.Source), entry.Name,
                RegistryValueKind.Binary, StartupApproved.Build(false), EntrySource, EntryRecordTitle(entry), Check);
    }

    private static string? EntryDisableRefusal(StartupEntry entry)
    {
        if (StartupDisableGuard.IsProtected(entry)) return StartupDisableGuard.Protected;
        try
        {
            var current = StartupLocations.IsFolder(entry.Source) || StartupLocations.IsPackaged(entry.Source)
                ? StaWorker.Run(() => StartupScanner.ReadEntry(entry), "Lycoris startup entry check").GetAwaiter().GetResult()
                : StartupScanner.ReadEntry(entry);
            if (StartupDisableGuard.EntryRefusal(entry, current, StartupSignatures.ReadUncached) is { } refusal) return refusal;
            if (StartupLocations.IsPackaged(entry.Source)) return PackagedWriteRefusal(entry);

            return StartupDisableObservation.ReadApproval(entry).IsEnabled
                && RegistryHelper.ReadDwordOrMissing(RegHive.HKCU, StartupDisableObservation.ExplorerPolicy, StartupDisableObservation.UserRunPolicy) is not > 0
                && RegistryHelper.ReadDwordOrMissing(RegHive.HKLM, StartupDisableObservation.ExplorerPolicy, StartupDisableObservation.MachineRunPolicy) is not > 0
                ? null : StartupDisableGuard.Changed;
        }
        catch (Exception ex) when (RemovalSoftwareEvidence.ReadFailed(ex))
        {
            return StartupDisableGuard.Changed;
        }
    }

    private static string? TaskDisableRefusal(LogonTask task)
    {
        if (StartupDisableGuard.IsProtected(task)) return StartupDisableGuard.Protected;
        try
        {
            return StartupDisableGuard.TaskRefusal(task, LogonTaskScanner.ReadCurrent(task.Path), StartupSignatures.ReadUncached);
        }
        catch (Exception ex) when (RemovalSoftwareEvidence.ReadFailed(ex))
        {
            return StartupDisableGuard.Changed;
        }
    }

    private static async Task<int> ApplyRevertsAsync(ChangeJournal journal,
        IReadOnlyList<(IReadOnlyList<ChangeRecord> Layers, Func<string?>? Refusal)> revert, List<string> errors)
    {
        if (revert[0].Refusal is null)
        {
            var result = await journal.RevertAsync(revert.SelectMany(target => target.Layers)).ConfigureAwait(false);
            errors.AddRange(result.Errors);
            return result.Reverted;
        }

        var applied = 0;
        foreach (var (layers, refusal) in revert)
        {
            applied += await journal.ExclusiveAsync(async () =>
            {
                var reason = refusal!();
                if (reason is null && !journal.ActiveLayers(layers[0].Target).SequenceEqual(layers))
                    reason = StartupDisableGuard.Changed;
                if (reason is not null)
                {
                    errors.Add($"{Loc.Sink(layers[0].Title)}: {reason}");
                    return 0;
                }
                var result = await journal.RevertAsync(layers).ConfigureAwait(false);
                errors.AddRange(result.Errors);
                return result.Reverted;
            }).ConfigureAwait(false);
        }
        return applied;
    }

    /// <summary>Why a packaged task must not be written now (live State re-read), or null.</summary>
    internal static string? PackagedWriteRefusal(StartupEntry e)
    {
        if (!PackagedStartup.IsJournalSafe(e.PackageFamilyName, e.Name))
            return Loc.T("нестандартное имя пакета или задачи — такую запись Lycoris не смог бы откатить, поэтому не меняет");
        return PackagedStartup.WriteRefusal(PackagedStartup.ReadState(e.PackageFamilyName!, e.Name));
    }

    /// <summary>
    /// True when switching to <paramref name="enable"/> goes back to the state the entry had before Lycoris: then every
    /// layer (<paramref name="layers"/>, this account's, oldest first) is undone and the exact original value comes back.
    /// </summary>
    public static bool UndoesLayers(StartupEntry e, bool enable, IReadOnlyList<ChangeRecord> layers) =>
        layers.Count > 0 && layers[0].Registry is { } snapshot && OriginalEnabled(e, snapshot) == enable;

    /// <summary>A packaged task's State from before Lycoris (the oldest layer's original), or null when unknown.</summary>
    public static int? PackagedStateBefore(IReadOnlyList<ChangeRecord> layers) =>
        layers.Count > 0 && layers[0].Registry is { } snapshot ? PackagedStartup.OriginalState(snapshot) : null;

    /// <summary>Same, from the current account's journal layers of the entry.</summary>
    public static int? PackagedStateBefore(StartupEntry e) =>
        StartupLocations.IsPackaged(e.Source) ? PackagedStateBefore(ChangeJournal.Instance.ActiveLayers(StartupLocations.JournalTarget(e))) : null;

    /// <summary>
    /// Was the entry enabled before Lycoris changed it? Packaged tasks: from the DWORD State (null when unknown, so
    /// the page writes instead of reverting); everything else: the StartupApproved rule below.
    /// </summary>
    public static bool? OriginalEnabled(StartupEntry e, RegistryValueSnapshot snapshot) =>
        StartupLocations.IsPackaged(e.Source) ? PackagedStartup.OriginalEnabled(snapshot) : OriginalEnabled(snapshot);

    /// <summary>Was the entry enabled before Lycoris changed it? (absent value / non-binary = enabled).</summary>
    public static bool OriginalEnabled(RegistryValueSnapshot snapshot)
    {
        if (!snapshot.ValueExisted || snapshot.Kind != RegistryValueKind.Binary) return true;
        try
        {
            return StartupApproved.IsEnabled(Convert.FromBase64String(snapshot.Data ?? ""));
        }
        catch (FormatException)
        {
            return true;
        }
    }

    /// <summary>
    /// Active journal records made by this page for the current account (the «Lycoris» tag). HKCU targets read the
    /// same for every account, so another account's records are left out: there is nothing of theirs to undo here.
    /// </summary>
    public static IReadOnlyList<ChangeRecord> ActiveRecords() => PageRecords(ChangeJournal.Instance.ActiveRecords, UserScope.CurrentSid);

    internal static IReadOnlyList<ChangeRecord> PageRecords(IEnumerable<ChangeRecord> records, string? currentSid) =>
        records.Where(r => !r.Reverted && r.Source is (EntrySource or TaskSource) && !ChangeJournal.IsOtherUser(r, currentSid)).ToList();
}
