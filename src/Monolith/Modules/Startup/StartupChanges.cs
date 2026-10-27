using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Modules.Startup;

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
/// When an entry is switched back to the state it had before MONOLITH touched it, the journal record is reverted
/// instead (the exact original value comes back and the journal stays clean).
/// </summary>
public static class StartupChanges
{
    public const string EntrySource = "startup";
    public const string TaskSource = "startup-tasks";

    public static string EntryRecordTitle(StartupEntry e) => $"Автозапуск: {e.DisplayName}";

    public static string TaskRecordTitle(LogonTask t) => $"Автозапуск: задача «{t.Name}»";

    public static string BatchTitle(IReadOnlyList<string> names, bool enable)
    {
        var verb = enable ? "включение" : "отключение";
        return names.Count == 1 ? $"Автозапуск: {verb} «{names[0]}»" : $"Автозапуск: {verb} ({names.Count} шт.)";
    }

    public static async Task<StartupChangeOutcome> SetEntriesAsync(IReadOnlyList<StartupEntry> entries, bool enable,
        bool createRestorePoint, IProgress<string>? progress = null)
    {
        var journal = ChangeJournal.Instance;
        var errors = new List<string>();
        var applied = 0;

        var revert = new List<ChangeRecord>();
        var write = new List<StartupEntry>();
        foreach (var e in entries)
        {
            var packaged = StartupLocations.IsPackaged(e.Source);
            // Packaged tasks are checked against the live value right before anything is written (undo included): the
            // package may have been removed or put under policy since the scan. Nothing is written then — no key is created.
            if (packaged && PackagedWriteRefusal(e) is { } refusal)
            {
                errors.Add($"{e.DisplayName}: {refusal}.");
                continue;
            }
            // Back to how it was before MONOLITH: undo every layer of this entry instead of stacking a new one.
            var layers = journal.ActiveLayers(StartupLocations.JournalTarget(e));
            if (UndoesLayers(e, enable, layers))
            {
                revert.AddRange(layers);
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
                progress?.Report($"{(enable ? "Включение" : "Отключение")}: {e.DisplayName}");
                if (StartupLocations.IsPackaged(e.Source))
                    session.SetRegistry(RegHive.HKCU, PackagedStartup.KeyPath(e.PackageFamilyName!, e.Name), PackagedStartup.StateValue,
                        RegistryValueKind.DWord, PackagedStartup.TargetState(enable), EntrySource, EntryRecordTitle(e));
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
            progress?.Report("Возврат исходного состояния…");
            var r = await journal.RevertAsync(revert).ConfigureAwait(false);
            applied += r.Reverted;
            errors.AddRange(r.Errors);
        }

        return new StartupChangeOutcome(applied, errors);
    }

    public static async Task<StartupChangeOutcome> SetTasksAsync(IReadOnlyList<LogonTask> tasks, bool enable,
        bool createRestorePoint, IProgress<string>? progress = null)
    {
        var journal = ChangeJournal.Instance;
        var errors = new List<string>();
        var applied = 0;

        var revert = new List<ChangeRecord>();
        var write = new List<LogonTask>();
        foreach (var t in tasks)
        {
            var layers = journal.ActiveLayers($"task:{t.Path}");
            if (layers.Count > 0 && layers[0].TaskWasEnabled is { } wasEnabled && wasEnabled == enable) revert.AddRange(layers);
            else write.Add(t);
        }

        if (write.Count > 0)
        {
            var session = await ChangeSession.BeginAsync(BatchTitle(write.Select(w => w.Name).ToList(), enable),
                createRestorePoint, progress).ConfigureAwait(false);
            foreach (var t in write)
            {
                progress?.Report($"{(enable ? "Включение" : "Отключение")} задачи: {t.Name}");
                session.SetTaskEnabled(t.Path, enable, TaskSource, TaskRecordTitle(t));
            }
            session.Complete();
            applied += session.Applied;
            errors.AddRange(session.Errors);
        }

        if (revert.Count > 0)
        {
            progress?.Report("Возврат исходного состояния…");
            var r = await journal.RevertAsync(revert).ConfigureAwait(false);
            applied += r.Reverted;
            errors.AddRange(r.Errors);
        }

        return new StartupChangeOutcome(applied, errors);
    }

    /// <summary>Why a packaged task must not be written now (live State re-read), or null.</summary>
    internal static string? PackagedWriteRefusal(StartupEntry e)
    {
        if (!PackagedStartup.IsJournalSafe(e.PackageFamilyName, e.Name))
            return "нестандартное имя пакета или задачи — такую запись MONOLITH не смог бы откатить, поэтому не меняет";
        return PackagedStartup.WriteRefusal(PackagedStartup.ReadState(e.PackageFamilyName!, e.Name));
    }

    /// <summary>
    /// True when switching to <paramref name="enable"/> goes back to the state the entry had before MONOLITH: then every
    /// layer (<paramref name="layers"/>, this account's, oldest first) is undone and the exact original value comes back.
    /// </summary>
    public static bool UndoesLayers(StartupEntry e, bool enable, IReadOnlyList<ChangeRecord> layers) =>
        layers.Count > 0 && layers[0].Registry is { } snapshot && OriginalEnabled(e, snapshot) == enable;

    /// <summary>A packaged task's State from before MONOLITH (the oldest layer's original), or null when unknown.</summary>
    public static int? PackagedStateBefore(IReadOnlyList<ChangeRecord> layers) =>
        layers.Count > 0 && layers[0].Registry is { } snapshot ? PackagedStartup.OriginalState(snapshot) : null;

    /// <summary>Same, from the current account's journal layers of the entry.</summary>
    public static int? PackagedStateBefore(StartupEntry e) =>
        StartupLocations.IsPackaged(e.Source) ? PackagedStateBefore(ChangeJournal.Instance.ActiveLayers(StartupLocations.JournalTarget(e))) : null;

    /// <summary>
    /// Was the entry enabled before MONOLITH changed it? Packaged tasks: from the DWORD State (null when unknown, so
    /// the page writes instead of reverting); everything else: the StartupApproved rule below.
    /// </summary>
    public static bool? OriginalEnabled(StartupEntry e, RegistryValueSnapshot snapshot) =>
        StartupLocations.IsPackaged(e.Source) ? PackagedStartup.OriginalEnabled(snapshot) : OriginalEnabled(snapshot);

    /// <summary>Was the entry enabled before MONOLITH changed it? (absent value / non-binary = enabled).</summary>
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
    /// Active journal records made by this page for the current account (the «MONOLITH» tag). HKCU targets read the
    /// same for every account, so another account's records are left out: there is nothing of theirs to undo here.
    /// </summary>
    public static IReadOnlyList<ChangeRecord> ActiveRecords() => PageRecords(ChangeJournal.Instance.ActiveRecords, UserScope.CurrentSid);

    internal static IReadOnlyList<ChangeRecord> PageRecords(IEnumerable<ChangeRecord> records, string? currentSid) =>
        records.Where(r => !r.Reverted && r.Source is (EntrySource or TaskSource) && !ChangeJournal.IsOtherUser(r, currentSid)).ToList();
}
