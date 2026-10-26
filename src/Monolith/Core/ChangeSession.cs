using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Monolith.Core;

/// <summary>
/// The single gateway for every reversible system change.
/// Each Set* method: captures the original state → writes it to the journal → makes the change.
/// If the change fails, the journal entry is removed again. Everything done through a session
/// shows up in the Restore center and can be undone one by one or all at once.
/// </summary>
public sealed class ChangeSession
{
    private readonly ChangeJournal _journal;
    private readonly HashSet<string> _exportedKeys = new(StringComparer.OrdinalIgnoreCase);

    public ChangeBatch Batch { get; }
    public string BackupDir { get; }
    public int Applied { get; private set; }
    /// <summary>Set when a command reported "done, restart required" (e.g. pnputil exit code 3010).</summary>
    public bool RebootRequired { get; private set; }
    public List<string> Errors { get; } = [];

    private ChangeSession(ChangeJournal journal, ChangeBatch batch)
    {
        _journal = journal;
        Batch = batch;
        BackupDir = Path.Combine(AppPaths.BackupsDir, $"{batch.Timestamp:yyyyMMdd-HHmmss}-{batch.Id.ToString()[..8]}");
    }

    /// <summary>
    /// Starts a batch of changes. Creates a System Restore point first when <paramref name="createRestorePoint"/>
    /// is true and the user hasn't turned that off in settings.
    /// </summary>
    /// <summary>TESTS ONLY: lets unit tests run sessions without admin rights (with a redirected registry and a temp journal).</summary>
    internal static bool BypassElevationForTests { get; set; }

    /// <summary>Set by the self-test mode: any attempt to change the system fails loudly.</summary>
    public static bool Locked { get; set; }

    public static async Task<ChangeSession> BeginAsync(string title, bool createRestorePoint = true,
        IProgress<string>? progress = null, ChangeJournal? journal = null)
    {
        if (Locked) throw new InvalidOperationException("Изменения системы заблокированы (режим самопроверки).");
        if (!BypassElevationForTests) Elevation.Demand(title);
        journal ??= ChangeJournal.Instance;
        var batch = journal.BeginBatch(title);
        var session = new ChangeSession(journal, batch);
        Log.Info($"Начало изменений: {title}");

        if (createRestorePoint && AppSettings.Current.CreateRestorePoints && !BypassElevationForTests)
        {
            progress?.Report("Создание точки восстановления…");
            var rp = await RestorePointService.CreateAsync($"MONOLITH: {title}").ConfigureAwait(false);
            batch.RestorePoint = rp.Message;
            journal.UpdateBatch(batch);
        }
        return session;
    }

    // ───────────────────────────── Registry ─────────────────────────────

    /// <summary>Sets a registry value. <paramref name="value"/> = null deletes the value.</summary>
    public bool SetRegistry(RegHive hive, string path, string name, RegistryValueKind kind, object? value,
        string source, string title)
    {
        var target = $"reg:{hive}\\{path}\\{name}";
        ChangeRecord? rec = null;
        bool isNew = false;
        try
        {
            if (!_exportedKeys.Contains($"{hive}\\{path}"))
            {
                _exportedKeys.Add($"{hive}\\{path}");
                var file = Path.Combine(BackupDir, SafeFileName($"{hive}_{path}") + ".reg");
                _ = RegistryHelper.ExportKeyAsync(hive, path, file).GetAwaiter().GetResult();
            }

            var snapshot = RegistryHelper.Snapshot(hive, path, name);
            var newText = value is null ? "удалено" : $"{kind}: {FormatValue(kind, value)}";
            var incoming = new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.Registry, Target = target,
                OldValue = snapshot.Describe(), NewValue = newText, Registry = snapshot,
            };
            (rec, isNew) = _journal.Add(incoming);

            if (value is null) RegistryHelper.DeleteValue(hive, path, name);
            else RegistryHelper.Write(hive, path, name, kind, value);

            if (!isNew) _journal.MergeInto(rec, incoming);
            Applied++;
            Log.Info($"{title}: {snapshot.FullName} = {newText}");
            return true;
        }
        catch (Exception ex)
        {
            if (rec is not null && isNew) _journal.Remove(rec);
            Fail(title, ex);
            return false;
        }
    }

    // ───────────────────────────── Services ─────────────────────────────

    public bool SetServiceStart(string service, ServiceStartMode mode, string source, string title, bool delayed = false)
    {
        ChangeRecord? rec = null;
        bool isNew = false;
        try
        {
            var snapshot = ServiceControl.Query(service);
            if (snapshot is null)
            {
                Log.Info($"{title}: служба {service} не найдена — пропущено.");
                return false;
            }
            if (ServiceControl.IsProtected(service))
                throw new InvalidOperationException($"служба {service} защищена от изменений");
            // The journal refuses to replay such records, so they are never created either.
            if (Monolith.Modules.Services.ServiceGuard.IsHardBlocked(service))
                throw new InvalidOperationException($"служба {service} в списке служб, которые MONOLITH не меняет");

            var incoming = new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.Service, Target = $"svc:{service}",
                OldValue = snapshot.Describe(), NewValue = ServiceControl.DescribeStart(mode, delayed), Service = snapshot,
            };
            (rec, isNew) = _journal.Add(incoming);
            ServiceControl.SetStartMode(service, mode, delayed);
            if (!isNew) _journal.MergeInto(rec, incoming);
            Applied++;
            Log.Info($"{title}: {service} → {ServiceControl.DescribeStart(mode, delayed)}");
            return true;
        }
        catch (Exception ex)
        {
            if (rec is not null && isNew) _journal.Remove(rec);
            Fail(title, ex);
            return false;
        }
    }

    // ───────────────────────────── Power plans ─────────────────────────────

    /// <summary>Activates a power plan. <paramref name="createdScheme"/> marks a plan MONOLITH created, so revert can delete it.</summary>
    public bool SetPowerScheme(Guid scheme, string source, string title, Guid? createdScheme = null)
    {
        ChangeRecord? rec = null;
        bool isNew = false;
        try
        {
            var original = PowerSchemes.GetActive();
            var incoming = new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.PowerScheme, Target = "power:active",
                OldValue = PowerSchemes.GetName(original), NewValue = PowerSchemes.GetName(scheme),
                PowerSchemeOriginal = original, PowerSchemeCreated = createdScheme,
            };
            (rec, isNew) = _journal.Add(incoming);
            PowerSchemes.SetActive(scheme);
            if (!isNew) _journal.MergeInto(rec, incoming);
            Applied++;
            Log.Info($"{title}: план питания → {PowerSchemes.GetName(scheme)}");
            return true;
        }
        catch (Exception ex)
        {
            if (rec is not null && isNew) _journal.Remove(rec);
            Fail(title, ex);
            return false;
        }
    }

    // ───────────────────────────── Scheduled tasks ─────────────────────────────

    public bool SetTaskEnabled(string taskPath, bool enabled, string source, string title)
    {
        ChangeRecord? rec = null;
        bool isNew = false;
        try
        {
            var info = ScheduledTasks.Query(taskPath);
            if (!info.Exists)
            {
                Log.Info($"{title}: задача {taskPath} не найдена — пропущено.");
                return false;
            }
            var incoming = new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.ScheduledTask, Target = $"task:{taskPath}",
                OldValue = info.Enabled ? "Включена" : "Отключена", NewValue = enabled ? "Включена" : "Отключена",
                TaskPath = taskPath, TaskWasEnabled = info.Enabled,
            };
            (rec, isNew) = _journal.Add(incoming);
            ScheduledTasks.SetEnabled(taskPath, enabled);
            if (!isNew) _journal.MergeInto(rec, incoming);
            Applied++;
            Log.Info($"{title}: {taskPath} → {(enabled ? "включена" : "отключена")}");
            return true;
        }
        catch (Exception ex)
        {
            if (rec is not null && isNew) _journal.Remove(rec);
            Fail(title, ex);
            return false;
        }
    }

    // ───────────────────────────── Commands ─────────────────────────────

    /// <summary>
    /// Runs a command whose effect is undone by another command (e.g. fsutil / bcdedit style settings).
    /// <paramref name="target"/> identifies the setting so repeated applies keep the first original.
    /// <paramref name="successCodes"/> lists the exit codes that mean "applied" (default: only 0). An applied
    /// non-zero code keeps the journal record; 3010 also sets <see cref="RebootRequired"/>.
    /// </summary>
    public async Task<bool> RunCommandAsync(string target, string file, string args, string revertFile, string revertArgs,
        string oldValue, string newValue, string source, string title, OutputEncoding encoding = OutputEncoding.Oem,
        IReadOnlySet<int>? successCodes = null)
    {
        ChangeRecord? rec = null;
        bool isNew = false;
        try
        {
            var incoming = new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.Command, Target = $"cmd:{target}",
                OldValue = oldValue, NewValue = newValue, RevertFile = revertFile, RevertArgs = revertArgs,
            };
            (rec, isNew) = _journal.Add(incoming);
            // Encoded PowerShell scripts are unreadable base64 — log a readable line instead.
            var encoded = args.Contains("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
            if (encoded) Log.Command($"powershell: {title} ({newValue})");
            var r = await ProcessRunner.RunAsync(file, args, new ProcessRunOptions { Encoding = encoding, Timeout = TimeSpan.FromMinutes(2), LogCommand = !encoded }).ConfigureAwait(false);
            var applied = r.Success || (!r.TimedOut && !r.Cancelled && successCodes?.Contains(r.ExitCode) == true);
            if (!applied) throw new InvalidOperationException($"код выхода {r.ExitCode}: {r.Combined.Trim()}");
            if (r.ExitCode == ProcessExit.RebootRequired)
            {
                RebootRequired = true;
                Log.Warn($"{title}: изменение вступит в силу после перезагрузки.");
            }
            if (!isNew) _journal.MergeInto(rec, incoming);
            Applied++;
            return true;
        }
        catch (Exception ex)
        {
            if (rec is not null && isNew) _journal.Remove(rec);
            Fail(title, ex);
            return false;
        }
    }

    public void Complete()
    {
        _journal.Save();
        if (Errors.Count == 0) Log.Success($"Готово: {Batch.Title} — изменений: {Applied}");
        else Log.Warn($"{Batch.Title}: применено {Applied}, ошибок {Errors.Count}");
    }

    private void Fail(string title, Exception ex)
    {
        Errors.Add($"{title}: {ex.Message}");
        Log.Error($"{title} — не применено", ex);
    }

    private static string FormatValue(RegistryValueKind kind, object value) => kind switch
    {
        RegistryValueKind.Binary when value is byte[] b => BitConverter.ToString(b).Replace("-", " "),
        RegistryValueKind.MultiString when value is string[] a => string.Join("; ", a),
        _ => value.ToString() ?? "",
    };

    private static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 120 ? s[..120] : s;
    }
}
