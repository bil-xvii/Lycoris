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
    public static async Task<ChangeSession> BeginAsync(string title, bool createRestorePoint = true,
        IProgress<string>? progress = null, ChangeJournal? journal = null)
    {
        Elevation.Demand(title);
        journal ??= ChangeJournal.Instance;
        var batch = journal.BeginBatch(title);
        var session = new ChangeSession(journal, batch);
        Log.Info($"Начало изменений: {title}");

        if (createRestorePoint && AppSettings.Current.CreateRestorePoints)
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
            (rec, isNew) = _journal.Add(new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.Registry, Target = target,
                OldValue = snapshot.Describe(), NewValue = newText, Registry = snapshot,
            });

            if (value is null) RegistryHelper.DeleteValue(hive, path, name);
            else RegistryHelper.Write(hive, path, name, kind, value);

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

            (rec, isNew) = _journal.Add(new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.Service, Target = $"svc:{service}",
                OldValue = snapshot.Describe(), NewValue = ServiceControl.DescribeStart(mode, delayed), Service = snapshot,
            });
            ServiceControl.SetStartMode(service, mode, delayed);
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
            (rec, isNew) = _journal.Add(new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.PowerScheme, Target = "power:active",
                OldValue = PowerSchemes.GetName(original), NewValue = PowerSchemes.GetName(scheme),
                PowerSchemeOriginal = original, PowerSchemeCreated = createdScheme,
            });
            PowerSchemes.SetActive(scheme);
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
            (rec, isNew) = _journal.Add(new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.ScheduledTask, Target = $"task:{taskPath}",
                OldValue = info.Enabled ? "Включена" : "Отключена", NewValue = enabled ? "Включена" : "Отключена",
                TaskPath = taskPath, TaskWasEnabled = info.Enabled,
            });
            ScheduledTasks.SetEnabled(taskPath, enabled);
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
    /// </summary>
    public async Task<bool> RunCommandAsync(string target, string file, string args, string revertFile, string revertArgs,
        string oldValue, string newValue, string source, string title, OutputEncoding encoding = OutputEncoding.Oem)
    {
        ChangeRecord? rec = null;
        bool isNew = false;
        try
        {
            (rec, isNew) = _journal.Add(new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.Command, Target = $"cmd:{target}",
                OldValue = oldValue, NewValue = newValue, RevertFile = revertFile, RevertArgs = revertArgs,
            });
            var r = await ProcessRunner.RunAsync(file, args, new ProcessRunOptions { Encoding = encoding, Timeout = TimeSpan.FromMinutes(2) }).ConfigureAwait(false);
            if (!r.Success) throw new InvalidOperationException($"код выхода {r.ExitCode}: {r.Combined.Trim()}");
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
