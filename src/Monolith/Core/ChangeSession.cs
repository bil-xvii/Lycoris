using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Monolith.Core;

/// <summary>
/// The single gateway for every reversible system change.
/// Each Set* method: captures the original state → writes it to the journal → makes the change.
/// If the change fails, the journal entry is removed again (unless the outcome is unknown). Everything done through
/// a session shows up in the Restore center and can be undone one by one or all at once. A change of a target that
/// already has an active record becomes a new layer holding the state right before it (see <see cref="ChangeJournal.Add"/>).
/// Changes in one account's profile (HKCU, per-user commands) record that account's SID.
/// Every Set* call holds the journal gate (<see cref="ChangeJournal.Exclusive{T}"/>) from reading the original through
/// the journal record and the write, so no revert runs in between; <see cref="Exclusive"/> widens that to a whole
/// tweak (which reads the live state itself before calling the session).
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

    /// <summary>
    /// Runs <paramref name="body"/> (reads of the live state and Set* calls) under the journal gate: no revert and no
    /// other journaled change runs meanwhile. Blocking: call off the UI thread. Never revert from inside it.
    /// </summary>
    public void Exclusive(Action body) => _journal.Exclusive(body);

    // ───────────────────────────── Registry ─────────────────────────────

    /// <summary>Sets a registry value. <paramref name="value"/> = null deletes the value.</summary>
    public bool SetRegistry(RegHive hive, string path, string name, RegistryValueKind kind, object? value,
        string source, string title) =>
        _journal.Exclusive(() => SetRegistryCore(hive, path, name, kind, value, source, title));

    private bool SetRegistryCore(RegHive hive, string path, string name, RegistryValueKind kind, object? value,
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

            // HKCU is the profile of the account MONOLITH runs as; the journal is shared by every account.
            var userSid = hive == RegHive.HKCU ? UserScope.CurrentSid : null;
            var snapshot = RegistryHelper.Snapshot(hive, path, name);
            // Writing into a key MONOLITH created earlier (still claimed by an active record): carry its created root,
            // so whichever of these records is reverted last removes the created keys.
            if (!snapshot.ValueExisted && _journal.InheritedKeyRoot(hive, path, userSid) is { } inherited)
                snapshot = snapshot.WithCreatedKeyRoot(RegistryHelper.ShallowerRoot(snapshot.CreatedKeyRoot, inherited));
            var newText = value is null ? "удалено" : $"{kind}: {FormatValue(kind, value)}";
            var incoming = new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.Registry, Target = target,
                OldValue = snapshot.Describe(), NewValue = newText, Registry = snapshot, UserSid = userSid,
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

    public bool SetServiceStart(string service, ServiceStartMode mode, string source, string title, bool delayed = false) =>
        _journal.Exclusive(() => SetServiceStartCore(service, mode, source, title, delayed));

    private bool SetServiceStartCore(string service, ServiceStartMode mode, string source, string title, bool delayed)
    {
        ChangeRecord? rec = null;
        bool isNew = false;
        ServiceStartSnapshot? snapshot = null;
        try
        {
            snapshot = ServiceControl.Query(service);
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
            // Drop the new record only when the service is back at (or never left) its original start type.
            if (rec is not null && isNew)
            {
                if (snapshot is null || ServiceUnchanged(service, snapshot)) _journal.Remove(rec);
                else Log.Warn($"{title}: служба {service} изменена лишь частично — запись сохранена для отката.");
            }
            Fail(title, ex);
            return false;
        }
    }

    private static bool ServiceUnchanged(string service, ServiceStartSnapshot original)
    {
        try
        {
            return ServiceControl.Query(service) is not { } now
                   || (now.StartMode == original.StartMode && now.DelayedAutoStart == original.DelayedAutoStart
                       && (original.RawDelayed is null || now.RawDelayed == original.RawDelayed));
        }
        catch
        {
            return false;
        }
    }

    // ───────────────────────────── Power plans ─────────────────────────────

    /// <summary>Activates a power plan. <paramref name="createdScheme"/> marks a plan MONOLITH created, so revert can delete it.</summary>
    public bool SetPowerScheme(Guid scheme, string source, string title, Guid? createdScheme = null) =>
        _journal.Exclusive(() => SetPowerSchemeCore(scheme, source, title, createdScheme));

    private bool SetPowerSchemeCore(Guid scheme, string source, string title, Guid? createdScheme)
    {
        ChangeRecord? rec = null;
        bool isNew = false;
        try
        {
            var original = PowerSchemes.GetActive();
            // Without the current plan there is nothing to go back to: change nothing.
            if (original == Guid.Empty)
                throw new InvalidOperationException("не удалось определить текущую схему питания — схема не изменена");
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

    public bool SetTaskEnabled(string taskPath, bool enabled, string source, string title) =>
        _journal.Exclusive(() => SetTaskEnabledCore(taskPath, enabled, source, title));

    private bool SetTaskEnabledCore(string taskPath, bool enabled, string source, string title)
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
    /// <paramref name="target"/> identifies the setting (a later change of it becomes a new layer).
    /// <paramref name="successCodes"/> lists the exit codes that mean "applied" (default: only 0). An applied
    /// non-zero code keeps the journal record; 3010 also sets <see cref="RebootRequired"/>. A command that timed out
    /// or was interrupted keeps its record too: it may have changed something, and the undo command writes the
    /// original state back either way. Per-user targets (DirectX tokens, visual effects) record the account.
    /// </summary>
    public Task<bool> RunCommandAsync(string target, string file, string args, string revertFile, string revertArgs,
        string oldValue, string newValue, string source, string title, OutputEncoding encoding = OutputEncoding.Oem,
        IReadOnlySet<int>? successCodes = null) =>
        _journal.ExclusiveAsync(() => RunCommandCoreAsync(target, file, args, revertFile, revertArgs, oldValue, newValue, source,
            title, encoding, successCodes));

    private async Task<bool> RunCommandCoreAsync(string target, string file, string args, string revertFile, string revertArgs,
        string oldValue, string newValue, string source, string title, OutputEncoding encoding, IReadOnlySet<int>? successCodes)
    {
        ChangeRecord? rec = null;
        bool isNew = false;
        ProcessResult? r = null;
        try
        {
            var incoming = new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.Command, Target = $"cmd:{target}",
                OldValue = oldValue, NewValue = newValue, RevertFile = revertFile, RevertArgs = revertArgs,
                UserSid = UserScope.IsPerUserCommand(target) ? UserScope.CurrentSid : null,
            };
            (rec, isNew) = _journal.Add(incoming);
            // Encoded PowerShell scripts are unreadable base64 — log a readable line instead.
            var encoded = args.Contains("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
            if (encoded) Log.Command($"powershell: {title} ({newValue})");
            r = await ProcessRunner.RunAsync(file, args, new ProcessRunOptions { Encoding = encoding, Timeout = TimeSpan.FromMinutes(2), LogCommand = !encoded }).ConfigureAwait(false);
            if (OutcomeUnknown(r))
                throw new InvalidOperationException("результат неизвестен (команда прервана по тайм-ауту) — запись сохранена для отката");
            var applied = r.Success || successCodes?.Contains(r.ExitCode) == true;
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
            // Remove the record only when the command ran to its end and failed (its own rollback has run) or never
            // started; a killed or still-running command may have changed the system.
            if (rec is not null && isNew && !OutcomeUnknown(r)) _journal.Remove(rec);
            Fail(title, ex);
            return false;
        }
    }

    /// <summary>The command was killed on timeout, cancelled or is still running: what it changed is not known.</summary>
    internal static bool OutcomeUnknown(ProcessResult? r) => r is { TimedOut: true } or { Cancelled: true } or { StillRunning: true };

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
