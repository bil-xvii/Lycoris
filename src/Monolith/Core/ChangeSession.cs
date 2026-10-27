using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;
using Monolith.Core.Devices;

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
    /// <summary>The journal this session writes to (a page that reverts its own just-made change uses this one, never Instance).</summary>
    public ChangeJournal Journal => _journal;
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

    // ───────────────────────────── Devices ─────────────────────────────
    // Same pattern as SetRegistry: journal gate, the record is checked against JournalTrust (so MONOLITH never makes a
    // change it could not undo) and written BEFORE the change, read-back after it, the original put back and the record
    // removed on failure. Every primitive refuses in self-test mode and without a writer for the device.

    /// <summary>
    /// Sets a per-user Windows input parameter (<see cref="SpiCatalog"/>): live session and the account's HKCU value.
    /// Only when MONOLITH runs as the desktop user (otherwise the persisted value would land in another account).
    /// An equal value creates no record.
    /// </summary>
    public bool SetUserParameter(string spiId, int value, string source, string title) =>
        _journal.Exclusive(() => SetUserParameterCore(spiId, value, source, title, out _));

    private bool SetUserParameterCore(string spiId, int value, string source, string title, out ChangeRecord? created)
    {
        created = null;
        ChangeRecord? rec = null;
        var isNew = false;
        SpiEntry? entry = null;
        ISpiWriter? writer = null;
        int? original = null;
        RegistryValueSnapshot? persisted = null;
        try
        {
            SafetyLock.ThrowIfLocked(title);
            entry = SpiCatalog.Find(spiId) ?? throw new InvalidOperationException($"неизвестный параметр «{spiId}»");
            if (!entry.IsAllowedTarget(value)) throw new InvalidOperationException($"значение {value} вне допустимого диапазона");
            if (Elevation.InteractiveUserProblem is { } account) throw new InvalidOperationException(account);
            writer = DeviceWriters.Spi ?? throw new InvalidOperationException(DeviceWriters.NoWriterApply);
            original = writer.Get(entry) ?? throw new InvalidOperationException("не удалось прочитать текущее значение");
            if (original == value)
            {
                Log.Info($"{title}: уже {entry.Describe(value)} — менять нечего.");
                return true;
            }
            if (!entry.IsValidOriginal(original.Value))
                throw new InvalidOperationException($"текущее значение {original} вне известного диапазона — MONOLITH не смог бы его вернуть");

            if (entry.Persisted is { } p)
            {
                ExportOnce(RegHive.HKCU, p.Path);
                // Windows creates the value (and, on a fresh profile, its Control Panel key) itself; an undo only deletes the
                // value it created and never removes a Control Panel key, so no created-key root is recorded.
                persisted = RegistryHelper.Snapshot(RegHive.HKCU, p.Path, p.Name).WithCreatedKeyRoot(null);
            }
            var incoming = new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.UserParameter, Target = DeviceTargets.Spi(entry.Id),
                OldValue = entry.Describe(original.Value), NewValue = entry.Describe(value),
                Spi = new SpiSnapshot { Id = entry.Id, Value = original.Value, Persisted = persisted },
                UserSid = UserScope.CurrentSid,
            };
            RefuseUntrusted(incoming);
            (rec, isNew) = _journal.Add(incoming);

            if (!writer.Set(entry, value)) throw new InvalidOperationException("Windows не приняла новое значение");
            var back = writer.Get(entry);
            if (back != value) throw new InvalidOperationException($"после записи прочитано {back?.ToString() ?? "—"}, ожидалось {value}");

            if (!isNew) _journal.MergeInto(rec, incoming);
            Applied++;
            created = rec;
            Log.Info($"{title}: {entry.Describe(original.Value)} → {entry.Describe(value)}");
            return true;
        }
        catch (Exception ex)
        {
            if (rec is not null && isNew && writer is not null && entry is not null && original is { } o)
            {
                // Put the live and the persisted value back exactly; keep the record when that fails (it is the undo).
                var restored = false;
                try { restored = DeviceRestore.RestoreSpi(writer, entry, o, persisted, out _); }
                catch (Exception restoreEx) { Log.Warn($"{title}: возврат не удался — {restoreEx.Message}"); }
                if (restored) _journal.Remove(rec);
                else Log.Warn($"{title}: прежнее значение вернуть не удалось — запись сохранена для отката.");
            }
            Fail(title, ex);
            return false;
        }
    }

    /// <summary>
    /// A parameter that can leave the user unable to click (button swap, a very low pointer speed): applied, then kept
    /// only when the user confirms within the countdown — with the new button. A decline or timeout reverts this
    /// session's own record; when that fails the original is written back as a new journaled layer; when that fails too,
    /// the outcome says how to put it back by hand (<paramref name="manualHint"/>).
    /// </summary>
    public Task<DisplayApplyOutcome> SetUserParameterConfirmedAsync(string spiId, int value, CountdownRequest confirm, string source, string title,
        CancellationToken ct = default, string? manualHint = null) =>
        _journal.ExclusiveAsync(async () =>
        {
            var (applied, ok) = await Task.Run(() =>
            {
                var done = SetUserParameterCore(spiId, value, source, title, out var r);
                return (done, r);
            }, CancellationToken.None).ConfigureAwait(false);
            if (!applied) return new DisplayApplyOutcome(DisplayApplyResult.Failed, Errors.Count > 0 ? Errors[^1] : "не применено");
            if (ok is null) return DisplayApplyOutcome.Unchanged;

            if (await DeviceConfirm.CountdownAsync(confirm, ct).ConfigureAwait(false))
                return new DisplayApplyOutcome(DisplayApplyResult.Kept);

            var revert = await _journal.RevertAsync([ok]).ConfigureAwait(false);
            if (revert.Success && ok.Reverted)
            {
                Applied--;
                Log.Info($"{title}: не подтверждено — возвращено прежнее значение.");
                return new DisplayApplyOutcome(DisplayApplyResult.Reverted, "не подтверждено — возвращено прежнее значение");
            }
            if (ok.Spi is { } snap && await Task.Run(() => SetUserParameterCore(spiId, snap.Value, source, title, out _)).ConfigureAwait(false))
                return new DisplayApplyOutcome(DisplayApplyResult.Reverted, "не подтверждено — прежнее значение записано заново");
            var text = "Не удалось вернуть прежнее значение автоматически." + (manualHint is null ? "" : " " + manualHint);
            Log.Warn($"{title}: {text}");
            return new DisplayApplyOutcome(DisplayApplyResult.Failed, text);
        });

    /// <summary>Changes the resolution and refresh rate of one monitor through the on-screen confirmation (see <see cref="DisplaySafeApply"/>).</summary>
    public Task<DisplayApplyOutcome> SetDisplayModeAsync(string monitorPath, string monitorName, DisplayModeSpec target, string source, string title,
        CancellationToken ct = default) =>
        _journal.ExclusiveAsync(() => RunDisplayAsync(title,
            () => DisplaySafeApply.ApplyModeAsync(_journal, Batch, monitorPath, monitorName, target, source, title, ct)));

    /// <summary>Turns HDR of one monitor on or off through the on-screen confirmation.</summary>
    public Task<DisplayApplyOutcome> SetDisplayHdrAsync(string monitorPath, string monitorName, bool enable, string source, string title,
        CancellationToken ct = default) =>
        _journal.ExclusiveAsync(() => RunDisplayAsync(title,
            () => DisplaySafeApply.ApplyHdrAsync(_journal, Batch, monitorPath, monitorName, enable, source, title, ct)));

    private async Task<DisplayApplyOutcome> RunDisplayAsync(string title, Func<Task<DisplayApplyOutcome>> body)
    {
        DisplayApplyOutcome outcome;
        try
        {
            SafetyLock.ThrowIfLocked(title);
            outcome = await body().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(title, ex);
            return new DisplayApplyOutcome(DisplayApplyResult.Failed, ex.Message);
        }
        Account(title, outcome);
        return outcome;
    }

    private void Account(string title, DisplayApplyOutcome outcome)
    {
        switch (outcome.Result)
        {
            case DisplayApplyResult.Kept:
                Applied++;
                break;
            case DisplayApplyResult.Failed:
                Errors.Add($"{title}: {outcome.Message}");
                Log.Warn($"{title} — не применено: {outcome.Message}");
                break;
            case DisplayApplyResult.Blocked:
                Log.Info($"{title}: {outcome.Message}");
                break;
            case DisplayApplyResult.Reverted:
                Log.Info($"{title}: {outcome.Message ?? "не подтверждено — возвращено прежнее"}");
                break;
        }
    }

    /// <summary>
    /// Monitor brightness or contrast over DDC/CI (within the monitor's own range). A value so low that the picture could
    /// go dark is kept only after the countdown, shown on another monitor when there is one.
    /// </summary>
    public Task<DisplayApplyOutcome> SetMonitorSettingAsync(string monitorPath, string monitorName, MonitorVcp code, uint value, string source,
        string title, CancellationToken ct = default) =>
        _journal.ExclusiveAsync(async () =>
        {
            ChangeRecord? rec = null;
            IMonitorWriter? writer = null;
            VcpReading now = default;
            try
            {
                SafetyLock.ThrowIfLocked(title);
                writer = DeviceWriters.Monitor ?? throw new InvalidOperationException(DeviceWriters.NoWriterApply);
                if (!DeviceTargets.IsMonitorPath(monitorPath)) throw new InvalidOperationException("некорректный путь монитора");
                now = await Task.Run(() => writer.Get(monitorPath, code)).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("монитор не отвечает по DDC/CI");
                if (value < now.Min || value > now.Max) throw new InvalidOperationException($"значение {value} вне диапазона монитора {now.Min}–{now.Max}");
                if (now.Value == value) return DisplayApplyOutcome.Unchanged;

                var incoming = new ChangeRecord
                {
                    BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.MonitorSetting, Target = DeviceTargets.Monitor(monitorPath, code),
                    OldValue = DeviceTargets.DescribeVcp(code, now.Value, now.Max), NewValue = DeviceTargets.DescribeVcp(code, value, now.Max),
                    Monitor = new MonitorVcpSnapshot { MonitorPath = monitorPath, MonitorName = monitorName, Code = code, Value = now.Value, Max = now.Max, Min = now.Min },
                };
                RefuseUntrusted(incoming);
                var (existing, isNew) = _journal.Add(incoming);
                rec = isNew ? existing : null; // merged into this batch's earlier record: that one keeps the first original
                var w = writer;
                var written = await Task.Run(() => w.Set(monitorPath, code, value) && w.Get(monitorPath, code)?.Value == value).ConfigureAwait(false);
                if (!written) throw new InvalidOperationException("монитор не принял новое значение");

                if (DeviceTargets.NeedsMonitorConfirm(code, value, now.Min, now.Max))
                {
                    var request = new CountdownRequest(code == MonitorVcp.Contrast ? "ОСТАВИТЬ ЭТОТ КОНТРАСТ?" : "ОСТАВИТЬ ЭТУ ЯРКОСТЬ?",
                        $"{monitorName}: {DeviceTargets.DescribeVcp(code, value, now.Max)}", (int)Math.Ceiling(DisplaySafeApply.ConfirmWindow.TotalSeconds),
                        CountdownRequest.Keep, CountdownRequest.Revert, DeviceWriters.Display?.GdiName(monitorPath))
                    { Window = DisplaySafeApply.ConfirmWindow };
                    if (!await DeviceConfirm.CountdownAsync(request, ct).ConfigureAwait(false))
                    {
                        var back = await Task.Run(() => w.Set(monitorPath, code, now.Value) && w.Get(monitorPath, code)?.Value == now.Value).ConfigureAwait(false);
                        if (back && rec is not null) _journal.Remove(rec);
                        return back
                            ? new DisplayApplyOutcome(DisplayApplyResult.Reverted, "не подтверждено — возвращено прежнее значение")
                            : new DisplayApplyOutcome(DisplayApplyResult.Failed, "не удалось вернуть прежнее значение — откатите запись в «Восстановлении»");
                    }
                }
                if (!isNew) _journal.MergeInto(existing, incoming);
                Applied++;
                Log.Info($"{title}: {incoming.OldValue} → {incoming.NewValue}");
                return new DisplayApplyOutcome(DisplayApplyResult.Kept);
            }
            catch (Exception ex)
            {
                if (rec is not null && writer is not null)
                {
                    var original = now.Value;
                    var w = writer;
                    var back = false;
                    try { back = await Task.Run(() => w.Set(monitorPath, code, original) && w.Get(monitorPath, code)?.Value == original).ConfigureAwait(false); }
                    catch { }
                    if (back) _journal.Remove(rec);
                    else Log.Warn($"{title}: прежнее значение вернуть не удалось — запись сохранена для отката.");
                }
                Fail(title, ex);
                return new DisplayApplyOutcome(DisplayApplyResult.Failed, ex.Message);
            }
        });

    /// <summary>
    /// Level (exactly one of <paramref name="scalar"/> 0..1 or <paramref name="db"/>) and/or mute of a capture endpoint.
    /// Level and mute are separate records (separate targets), so each undo puts back only its own part. Only for the
    /// desktop user (Core Audio from another account sees that account's view). Blocking: call off the UI thread.
    /// </summary>
    public bool SetAudioLevel(string endpointId, string endpointName, float? scalar, float? db, bool? mute, string source, string title) =>
        _journal.Exclusive(() =>
        {
            try
            {
                SafetyLock.ThrowIfLocked(title);
                if (scalar is not null && db is not null) throw new InvalidOperationException("укажите уровень либо в процентах, либо в децибелах");
                if (scalar is { } s && (!float.IsFinite(s) || s < 0f || s > 1f)) throw new InvalidOperationException("уровень вне диапазона 0–100 %");
                if (db is { } d && !float.IsFinite(d)) throw new InvalidOperationException("недопустимый уровень в децибелах");
                if (!DeviceTargets.IsCaptureEndpointId(endpointId)) throw new InvalidOperationException("MONOLITH меняет уровень только у устройств записи");
                if (Elevation.InteractiveUserProblem is { } account) throw new InvalidOperationException(account);
                var writer = DeviceWriters.Audio ?? throw new InvalidOperationException(DeviceWriters.NoWriterApply);
                if (!writer.IsPresent(endpointId)) throw new InvalidOperationException("устройство не найдено");
            }
            catch (Exception ex)
            {
                Fail(title, ex);
                return false;
            }

            var ok = true;
            if (scalar is not null || db is not null) ok &= SetAudioPart(endpointId, endpointName, AudioComponent.Level, scalar, db, null, source, title);
            if (mute is { } m) ok &= SetAudioPart(endpointId, endpointName, AudioComponent.Mute, null, null, m, source, title);
            return ok;
        });

    private bool SetAudioPart(string endpointId, string endpointName, AudioComponent part, float? scalar, float? db, bool? mute, string source, string title)
    {
        var writer = DeviceWriters.Audio!;
        ChangeRecord? rec = null;
        var isNew = false;
        (float Scalar, bool Mute) now = default;
        try
        {
            now = writer.Read(endpointId) ?? throw new InvalidOperationException("не удалось прочитать уровень устройства");
            if (part == AudioComponent.Level && scalar is { } target && Math.Abs(target - now.Scalar) < 0.0005f) return true;
            if (part == AudioComponent.Mute && mute == now.Mute) return true;

            var incoming = new ChangeRecord
            {
                BatchId = Batch.Id, Source = source, Title = title, Kind = ChangeKind.AudioLevel, Target = DeviceTargets.Audio(endpointId, part),
                OldValue = part == AudioComponent.Mute ? DeviceTargets.DescribeMute(now.Mute) : DeviceTargets.DescribeLevel(now.Scalar),
                NewValue = part == AudioComponent.Mute ? DeviceTargets.DescribeMute(mute == true)
                    : scalar is { } sc ? DeviceTargets.DescribeLevel(sc) : $"{db:0.#} дБ",
                Audio = new AudioLevelSnapshot
                {
                    EndpointId = endpointId, EndpointName = endpointName, Component = part,
                    ScalarBits = BitConverter.SingleToUInt32Bits(now.Scalar), Mute = now.Mute,
                },
            };
            RefuseUntrusted(incoming);
            (rec, isNew) = _journal.Add(incoming);

            var written = part switch
            {
                AudioComponent.Mute => writer.SetMute(endpointId, mute == true),
                _ when scalar is { } sv => writer.SetScalar(endpointId, sv),
                _ => writer.SetDb(endpointId, db!.Value),
            };
            var back = writer.Read(endpointId);
            if (!written || back is null) throw new InvalidOperationException("устройство не приняло новое значение");
            if (part == AudioComponent.Mute && back.Value.Mute != mute) throw new InvalidOperationException("состояние звука не изменилось");
            if (part == AudioComponent.Level && scalar is { } want && Math.Abs(back.Value.Scalar - want) > 0.001f)
                throw new InvalidOperationException("уровень не совпал после записи");
            if (part == AudioComponent.Level && Math.Abs(back.Value.Scalar - now.Scalar) <= 0.0005f)
            {
                if (isNew) _journal.Remove(rec); // the endpoint's range clamped it: nothing changed
                return true;
            }

            // The value read back is what the record says it set (a dB request lands on the endpoint's own steps).
            incoming.NewValue = part == AudioComponent.Mute ? DeviceTargets.DescribeMute(back.Value.Mute) : DeviceTargets.DescribeLevel(back.Value.Scalar);
            _journal.MergeInto(rec, incoming);
            Applied++;
            Log.Info($"{title}: {incoming.OldValue} → {incoming.NewValue}");
            return true;
        }
        catch (Exception ex)
        {
            if (rec is not null && isNew)
            {
                var restored = false;
                try
                {
                    restored = part == AudioComponent.Mute
                        ? writer.SetMute(endpointId, now.Mute) && writer.Read(endpointId)?.Mute == now.Mute
                        : writer.SetScalar(endpointId, now.Scalar) && writer.Read(endpointId) is { } b && Math.Abs(b.Scalar - now.Scalar) <= 0.001f;
                }
                catch { }
                if (restored) _journal.Remove(rec);
                else Log.Warn($"{title}: прежнее значение вернуть не удалось — запись сохранена для отката.");
            }
            Fail(title, ex);
            return false;
        }
    }

    /// <summary>
    /// Sets one whitelisted NDIS advanced property (<see cref="DeviceTargets.NicKeywords"/>) of the adapter with this
    /// InterfaceGuid, to a value the adapter itself lists as valid. The driver restarts the adapter: the link drops for
    /// about 1–6 s, so it is refused during a game session. Blocking: call off the UI thread.
    /// </summary>
    public bool SetNicAdvancedProperty(Guid interfaceGuid, string keyword, string value, string source, string title) =>
        _journal.Exclusive(() =>
        {
            ChangeRecord? rec = null;
            var isNew = false;
            INicWriter? writer = null;
            NicPropertySnapshot? snap = null;
            try
            {
                SafetyLock.ThrowIfLocked(title);
                if (GameBoostState.IsActive) throw new InvalidOperationException("во время игрового сеанса MONOLITH не перезапускает сетевой адаптер");
                if (!DeviceTargets.IsNicKeyword(keyword)) throw new InvalidOperationException($"свойство «{keyword}» MONOLITH не меняет");
                if (!DeviceTargets.IsNicValue(value)) throw new InvalidOperationException("значение свойства должно быть числом");
                writer = DeviceWriters.Nic ?? throw new InvalidOperationException(DeviceWriters.NoWriterApply);
                if (writer.RunningGame() is { } game) throw new InvalidOperationException($"идёт игра ({game}) — MONOLITH не перезапускает сетевой адаптер");
                var name = writer.AdapterName(interfaceGuid) ?? throw new InvalidOperationException("сетевой адаптер не найден");
                var info = writer.Read(interfaceGuid, keyword) ?? throw new InvalidOperationException($"у адаптера нет свойства {keyword}");
                if (!info.ValidValues.Contains(value, StringComparer.Ordinal))
                    throw new InvalidOperationException($"значение {value} не входит в допустимые для этого адаптера");
                if (string.Equals(info.Value ?? info.DefaultValue, value, StringComparison.Ordinal)) return true;

                snap = new NicPropertySnapshot { InterfaceGuid = interfaceGuid, AdapterName = name, Keyword = info.Keyword, Value = info.Value, DefaultValue = info.DefaultValue };
                if (snap.RestoreValue is null) throw new InvalidOperationException("у свойства нет ни значения, ни значения по умолчанию — вернуть его было бы нельзя");
                var incoming = new ChangeRecord
                {
                    BatchId = Batch.Id, Source = source, Title = $"{title} ({DeviceTargets.NicRestartNote})", Kind = ChangeKind.NicProperty,
                    Target = DeviceTargets.NicProperty(interfaceGuid, info.Keyword),
                    OldValue = DeviceTargets.DescribeNic(info.Value), NewValue = value, Nic = snap,
                };
                RefuseUntrusted(incoming);
                (rec, isNew) = _journal.Add(incoming);
                Log.Info($"{title}: {name} — {DeviceTargets.NicRestartNote}.");
                if (!writer.Set(interfaceGuid, info.Keyword, value) || writer.Read(interfaceGuid, info.Keyword)?.Value != value)
                    throw new InvalidOperationException("адаптер не принял новое значение");
                if (!isNew) _journal.MergeInto(rec, incoming);
                Applied++;
                Log.Info($"{title}: {name} {info.Keyword} {DeviceTargets.DescribeNic(info.Value)} → {value}");
                return true;
            }
            catch (Exception ex)
            {
                if (rec is not null && isNew && writer is not null && snap?.RestoreValue is { } original)
                {
                    var restored = false;
                    try
                    {
                        var live = writer.Read(interfaceGuid, snap.Keyword);
                        restored = live is not null && (string.Equals(live.Value ?? live.DefaultValue, original, StringComparison.Ordinal)
                                                        || (writer.Set(interfaceGuid, snap.Keyword, original) && writer.Read(interfaceGuid, snap.Keyword)?.Value == original));
                    }
                    catch { }
                    if (restored) _journal.Remove(rec);
                    else Log.Warn($"{title}: прежнее значение вернуть не удалось — запись сохранена для отката.");
                }
                Fail(title, ex);
                return false;
            }
        });

    /// <summary>The journal check runs on every new device record BEFORE anything changes: never make a change MONOLITH could not undo.</summary>
    private static void RefuseUntrusted(ChangeRecord incoming)
    {
        if (JournalTrust.Check(incoming) is { } refusal)
            throw new InvalidOperationException($"MONOLITH не смог бы откатить это изменение ({refusal}) — ничего не изменено");
    }

    private void ExportOnce(RegHive hive, string path)
    {
        if (!_exportedKeys.Add($"{hive}\\{path}")) return;
        var file = Path.Combine(BackupDir, SafeFileName($"{hive}_{path}") + ".reg");
        _ = RegistryHelper.ExportKeyAsync(hive, path, file).GetAwaiter().GetResult();
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
