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
/// Disabling/enabling only writes the StartupApproved mark Task Manager uses — Run values and shortcuts are never deleted.
/// When an entry is switched back to the state it had before MONOLITH touched it, the journal record is reverted
/// instead (exact original bytes come back and the journal stays clean).
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
            var record = journal.FindActive(StartupLocations.JournalTarget(e.Source, e.Name));
            if (record?.Registry is { } snapshot && OriginalEnabled(snapshot) == enable) revert.Add(record);
            else write.Add(e);
        }

        if (write.Count > 0)
        {
            var session = await ChangeSession.BeginAsync(BatchTitle(write.Select(w => w.DisplayName).ToList(), enable),
                createRestorePoint, progress).ConfigureAwait(false);
            foreach (var e in write)
            {
                progress?.Report($"{(enable ? "Включение" : "Отключение")}: {e.DisplayName}");
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
            var record = journal.FindActive($"task:{t.Path}");
            if (record?.TaskWasEnabled is { } wasEnabled && wasEnabled == enable) revert.Add(record);
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

    /// <summary>Active journal records made by this page.</summary>
    public static IReadOnlyList<ChangeRecord> ActiveRecords() =>
        ChangeJournal.Instance.ActiveRecords.Where(r => r.Source is EntrySource or TaskSource).ToList();
}
