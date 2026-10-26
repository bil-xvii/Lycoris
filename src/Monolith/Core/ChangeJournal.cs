using System.Globalization;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Monolith.Modules.Services;
using Monolith.Tweaks;

namespace Monolith.Core;

public enum ChangeKind { Registry, Service, PowerScheme, ScheduledTask, Command }

/// <summary>One reversible change MONOLITH made to the system, with everything needed to undo it.</summary>
public sealed class ChangeRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid BatchId { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Who made the change: "tweak:game-mode", "services", "startup", "gameboost"...</summary>
    public string Source { get; init; } = "";
    /// <summary>Human readable, Russian: "Игровой режим Windows".</summary>
    public string Title { get; init; } = "";
    public ChangeKind Kind { get; init; }
    /// <summary>Unique key of the changed thing: "reg:HKCU\...\Name", "svc:DiagTrack", "power:active", "task:\Microsoft\...".</summary>
    public string Target { get; init; } = "";
    public string OldValue { get; init; } = "";
    public string NewValue { get; set; } = "";

    public RegistryValueSnapshot? Registry { get; init; }
    public ServiceStartSnapshot? Service { get; init; }
    public Guid? PowerSchemeOriginal { get; init; }
    /// <summary>A scheme MONOLITH created (Ultimate Performance copy) and may delete on revert.</summary>
    public Guid? PowerSchemeCreated { get; set; }
    /// <summary>
    /// Other sources that changed the same target after this record was created. The record still holds the
    /// true original value; reverting from any of these sources restores it.
    /// </summary>
    public List<string> AlsoSources { get; set; } = [];

    public bool TouchedBy(string source) =>
        Source == source || AlsoSources.Contains(source);
    public bool? TaskWasEnabled { get; init; }
    public string? TaskPath { get; init; }
    public string? RevertFile { get; init; }
    public string? RevertArgs { get; init; }

    public bool Reverted { get; set; }
    public DateTime? RevertedUtc { get; set; }
    public string? RevertError { get; set; }

    [JsonIgnore] public DateTime Timestamp => TimestampUtc.ToLocalTime();
    [JsonIgnore] public bool IsActive => !Reverted;
}

public sealed class ChangeBatch
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string Title { get; init; } = "";
    public string? RestorePoint { get; set; }
    [JsonIgnore] public DateTime Timestamp => TimestampUtc.ToLocalTime();
}

public sealed record RevertResult(int Reverted, int Failed, IReadOnlyList<string> Errors)
{
    public bool Success => Failed == 0;
}

/// <summary>
/// The undo journal: every system change is written here BEFORE it is made, with the original value.
/// Stored as JSON in %ProgramData%\MONOLITH\journal.json, saved atomically.
/// </summary>
public sealed class ChangeJournal
{
    private sealed class JournalFile
    {
        public int Version { get; set; } = 1;
        public List<ChangeBatch> Batches { get; set; } = [];
        public List<ChangeRecord> Records { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lazy<ChangeJournal> Default = new(() => new ChangeJournal(AppPaths.JournalFile));
    public static ChangeJournal Instance => Default.Value;

    private readonly object _lock = new();
    private readonly string _path;
    /// <summary>The real journal in %ProgramData%: only an elevated process may write it.</summary>
    private readonly bool _protected;
    private JournalFile _data = new();

    public event Action? Changed;

    /// <summary>Set when the journal had to be quarantined because its folder was writable by non-admins.</summary>
    public string? QuarantinedFile { get; private set; }

    /// <summary>
    /// Set when the journal cannot be trusted or read (journal.json and its .bak are both damaged, or the data
    /// folder could not be locked down). No new changes are allowed until the user starts a new journal
    /// (<see cref="StartNewJournal"/>); the damaged files are never overwritten.
    /// </summary>
    public string? UnavailableReason { get; private set; }
    public bool IsUnavailable => UnavailableReason is not null;

    private const string UnavailableMessage =
        "Журнал изменений недоступен, поэтому MONOLITH не вносит новые изменения: без журнала их нельзя было бы отменить.";

    public ChangeJournal(string path)
    {
        _path = path;
        _protected = AppPaths.IsUnderMachineDir(path);
        Load();
    }

    private bool CanWrite => !IsUnavailable && (!_protected || Elevation.IsElevated || ChangeSession.BypassElevationForTests);

    public IReadOnlyList<ChangeRecord> Records { get { lock (_lock) return _data.Records.ToList(); } }
    public IReadOnlyList<ChangeBatch> Batches { get { lock (_lock) return _data.Batches.ToList(); } }
    public IReadOnlyList<ChangeRecord> ActiveRecords { get { lock (_lock) return _data.Records.Where(r => !r.Reverted).ToList(); } }

    /// <summary>Starts a batch. Throws when the journal cannot be written: no change may be made without it.</summary>
    public ChangeBatch BeginBatch(string title)
    {
        var batch = new ChangeBatch { Title = title };
        lock (_lock)
        {
            if (UnavailableReason is { } reason) throw new InvalidOperationException($"{UnavailableMessage} {reason}");
            _data.Batches.Add(batch);
            if (!SaveLocked())
            {
                _data.Batches.Remove(batch);
                throw new InvalidOperationException("Не удалось записать журнал изменений на диск — изменение не выполнено.");
            }
        }
        return batch;
    }

    public void UpdateBatch(ChangeBatch batch)
    {
        Save();
    }

    /// <summary>
    /// After the journal was found damaged or untrusted: sets the old files aside (never deletes them) and
    /// starts an empty journal so changes can be made again. Elevated only. Returns false when that failed.
    /// </summary>
    public bool StartNewJournal()
    {
        lock (_lock)
        {
            if (!IsUnavailable) return true;
            if (_protected && !Elevation.IsElevated && !ChangeSession.BypassElevationForTests) return false;
            if (_protected && Elevation.IsElevated)
            {
                AppPaths.SecureMachineDir();
                if (!AppPaths.MachineDirSecured) return false;
            }
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            try
            {
                if (File.Exists(_path)) File.Move(_path, _path + $".unavailable-{stamp}");
                if (File.Exists(_path + ".bak")) File.Move(_path + ".bak", _path + $".bak.unavailable-{stamp}");
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось отложить старый журнал изменений", ex);
                return false;
            }
            _data = new JournalFile();
            UnavailableReason = null;
            Log.Warn($"Начат новый журнал изменений; старые файлы сохранены с отметкой {stamp}.");
        }
        Changed?.Invoke();
        return true;
    }

    public ChangeRecord? FindActive(string target)
    {
        lock (_lock)
            return _data.Records.LastOrDefault(r => !r.Reverted && string.Equals(r.Target, target, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasActiveSource(string source)
    {
        lock (_lock) return _data.Records.Any(r => !r.Reverted && r.TouchedBy(source));
    }

    /// <summary>
    /// Adds a record BEFORE the change is made. If the same target was already changed (and not reverted),
    /// the FIRST record is kept untouched, because it holds the true original value — call
    /// <see cref="MergeInto"/> after the new change succeeded to note the new value and source.
    /// Returns the record that represents the target, and whether it is new.
    /// Throws (and keeps nothing) when a new record could not be written to disk: the caller must then not
    /// make the change — no change without an on-disk undo record.
    /// </summary>
    public (ChangeRecord Record, bool IsNew) Add(ChangeRecord record)
    {
        ChangeRecord result;
        bool isNew;
        lock (_lock)
        {
            var existing = _data.Records.LastOrDefault(r => !r.Reverted && string.Equals(r.Target, record.Target, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                result = existing;
                isNew = false;
            }
            else
            {
                if (UnavailableReason is { } reason) throw new InvalidOperationException($"{UnavailableMessage} {reason}");
                _data.Records.Add(record);
                if (!SaveLocked())
                {
                    _data.Records.Remove(record);
                    throw new InvalidOperationException("Не удалось записать журнал изменений на диск — изменение не выполнено.");
                }
                result = record;
                isNew = true;
            }
        }
        if (isNew) Changed?.Invoke();
        return (result, isNew);
    }

    /// <summary>After a successful change of an already-journaled target: remember its new value and source.</summary>
    public void MergeInto(ChangeRecord existing, ChangeRecord incoming)
    {
        lock (_lock)
        {
            existing.NewValue = incoming.NewValue;
            if (incoming.Source != existing.Source && !existing.AlsoSources.Contains(incoming.Source))
                existing.AlsoSources.Add(incoming.Source);
            existing.PowerSchemeCreated ??= incoming.PowerSchemeCreated;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Removes a record that was added but whose change then failed.</summary>
    public void Remove(ChangeRecord record)
    {
        lock (_lock)
        {
            _data.Records.Remove(record);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public async Task<RevertResult> RevertAsync(IEnumerable<ChangeRecord> records)
    {
        // Newest first, so chained changes unwind in the right order.
        var list = records.Where(r => !r.Reverted).OrderByDescending(r => r.TimestampUtc).ToList();
        int ok = 0, failed = 0;
        var errors = new List<string>();
        foreach (var r in list)
        {
            try
            {
                await RevertOneAsync(r).ConfigureAwait(false);
                lock (_lock)
                {
                    r.Reverted = true;
                    r.RevertedUtc = DateTime.UtcNow;
                    r.RevertError = null;
                }
                ok++;
                Log.Success($"Откат: {r.Title} → {r.OldValue}");
            }
            catch (Exception ex)
            {
                lock (_lock) r.RevertError = ex.Message;
                failed++;
                errors.Add($"{r.Title}: {ex.Message}");
                Log.Error($"Не удалось откатить «{r.Title}»", ex);
            }
        }
        Save();
        Changed?.Invoke();
        return new RevertResult(ok, failed, errors);
    }

    public Task<RevertResult> RevertSourceAsync(string source)
    {
        List<ChangeRecord> items;
        lock (_lock) items = _data.Records.Where(r => !r.Reverted && r.TouchedBy(source)).ToList();
        return RevertAsync(items);
    }

    public Task<RevertResult> RevertBatchAsync(Guid batchId)
    {
        List<ChangeRecord> items;
        lock (_lock) items = _data.Records.Where(r => !r.Reverted && r.BatchId == batchId).ToList();
        return RevertAsync(items);
    }

    public Task<RevertResult> RevertAllAsync() => RevertAsync(ActiveRecords);

    /// <summary>The only programs a journaled undo command may run (full System32 paths).</summary>
    private static readonly string[] AllowedRevertPrograms =
    [
        ProcessRunner.Sys("cmd.exe"),
        Path.Combine(ProcessRunner.System32, "WindowsPowerShell", "v1.0", "powershell.exe"),
        ProcessRunner.Sys("powercfg.exe"),
        ProcessRunner.Sys("netsh.exe"),
        ProcessRunner.Sys("fsutil.exe"),
        ProcessRunner.Sys("pnputil.exe"),
    ];

    /// <summary>
    /// Registry locations MONOLITH never writes during an undo: autostart and process-hijack points.
    /// No legitimate MONOLITH record targets them; refusing them makes a forged journal useless.
    /// </summary>
    internal static bool IsForbiddenRestoreTarget(RegistryValueSnapshot s)
    {
        var p = s.Path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var n = s.Name.ToLowerInvariant();
        if (p.Contains("image file execution options") || p.Contains("silentprocessexit")) return true;
        if (p.Contains(@"\winlogon") || p.Contains(@"\appinit_dlls") || n == "appinit_dlls") return true;
        if (p.EndsWith(@"currentversion\run") || p.EndsWith(@"currentversion\runonce") || p.EndsWith(@"currentversion\runonceex")
            || p.EndsWith(@"currentversion\runservices") || p.EndsWith(@"currentversion\runservicesonce")
            || p.EndsWith(@"policies\explorer\run")) return true;
        if (p.Contains(@"\services\") && n is "imagepath" or "servicedll" or "failurecommand") return true;
        if (p.Contains(@"\shell\open\command") || p.Contains(@"\shellex\")) return true;
        if (p.Contains(@"windows defender") || p.Contains(@"\lsa")) return true;
        return false;
    }

    private static async Task RevertOneAsync(ChangeRecord r)
    {
        // The journal file is input, not authority: refuse anything MONOLITH's own code could not have recorded.
        if (JournalTrust.Check(r) is { } refusal)
            throw new InvalidOperationException($"Отказано: {refusal}. Такую запись MONOLITH создать не мог.");

        switch (r.Kind)
        {
            case ChangeKind.Registry when r.Registry is not null:
                if (IsForbiddenRestoreTarget(r.Registry))
                    throw new InvalidOperationException($"Отказано: {r.Registry.FullName} — защищённое место реестра. Такую запись MONOLITH создать не мог.");
                RegistryHelper.Restore(r.Registry);
                break;

            case ChangeKind.Service when r.Service is not null:
                if (ServiceControl.Query(r.Service.Name) is null)
                {
                    Log.Info($"Откат пропущен: службы {r.Service.Name} больше нет в системе.");
                    break;
                }
                // Never allowProtected: no legitimate record can target a protected service (JournalTrust refuses it too).
                ServiceControl.SetStartMode(r.Service.Name, r.Service.StartMode, r.Service.DelayedAutoStart);
                break;

            case ChangeKind.PowerScheme:
                if (r.PowerSchemeOriginal is { } original && original != Guid.Empty)
                {
                    if (PowerSchemes.Exists(original)) PowerSchemes.SetActive(original);
                    else PowerSchemes.SetActive(PowerSchemes.Balanced);
                }
                if (r.PowerSchemeCreated is { } created) PowerSchemes.Delete(created);
                break;

            case ChangeKind.ScheduledTask when r.TaskPath is not null && r.TaskWasEnabled is { } wasEnabled:
                if (!ScheduledTasks.Query(r.TaskPath).Exists)
                {
                    Log.Info($"Откат пропущен: задачи {r.TaskPath} больше нет в планировщике.");
                    break;
                }
                ScheduledTasks.SetEnabled(r.TaskPath, wasEnabled);
                break;

            case ChangeKind.Command when !string.IsNullOrWhiteSpace(r.RevertFile):
                SafetyLock.ThrowIfLocked("откат команды");
                // DNS records from older builds name the adapter by its (non-persistent) index: run the current
                // GUID-based script with the same servers instead.
                var (revertFile, revertArgs) = JournalTrust.UpgradeLegacyDns(r) ?? (r.RevertFile!, r.RevertArgs ?? "");
                if (!AllowedRevertPrograms.Contains(revertFile, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Отказано: команда отката «{revertFile}» не из списка системных утилит.");
                var encoded = revertArgs.Contains("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
                if (encoded) Log.Command($"powershell: откат «{r.Title}»");
                var result = await ProcessRunner.RunAsync(revertFile, revertArgs,
                    new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(2), LogCommand = !encoded }).ConfigureAwait(false);
                var rebootPending = result is { ExitCode: ProcessExit.RebootRequired, TimedOut: false, Cancelled: false }
                                    && string.Equals(r.RevertFile, ProcessRunner.Sys("pnputil.exe"), StringComparison.OrdinalIgnoreCase);
                if (rebootPending) Log.Warn($"Откат «{r.Title}» вступит в силу после перезагрузки.");
                else if (!result.Success) throw new InvalidOperationException($"Команда отката завершилась с кодом {result.ExitCode}");
                break;

            default:
                throw new InvalidOperationException("Запись журнала не содержит данных для отката.");
        }
    }

    private void Load()
    {
        if (_protected && Elevation.IsElevated)
        {
            AppPaths.SecureMachineDir();
            if (!AppPaths.MachineDirSecured)
            {
                // Could not lock the folder down: anyone might still be able to edit the journal. Never replay it.
                UnavailableReason = "Не удалось защитить папку данных MONOLITH от записи обычными пользователями.";
                Log.Error("Журнал изменений не загружен: папка данных не защищена.");
                return;
            }
            if (AppPaths.MachineDirWasInsecure && (File.Exists(_path) || File.Exists(_path + ".bak")))
            {
                // Someone without admin rights could have edited it: never replay it. Keep it for inspection.
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var quarantined = _path + $".untrusted-{stamp}";
                try
                {
                    if (File.Exists(_path)) File.Move(_path, quarantined);
                    if (File.Exists(_path + ".bak")) File.Move(_path + ".bak", quarantined + ".bak");
                }
                catch (Exception ex)
                {
                    UnavailableReason = "Журнал хранился в незащищённой папке, и его не удалось отложить.";
                    Log.Error("Не удалось отложить недоверенный журнал изменений", ex);
                    return;
                }
                QuarantinedFile = quarantined;
                Log.Warn($"Журнал изменений хранился в незащищённой папке и отложен как недоверенный: {QuarantinedFile}");
                return;
            }
        }

        if (TryRead(_path, out var data, out var error))
        {
            _data = data;
            return;
        }
        var backupPath = _path + ".bak";
        if (error is null)
        {
            // No journal.json. A surviving .bak still holds undo records (e.g. a crash during a save).
            if (TryRead(backupPath, out var orphan, out _))
            {
                _data = orphan;
                Log.Warn("Журнал изменений не найден — загружена резервная копия.");
            }
            return;
        }

        // Damaged: keep a copy, then fall back to the last good version.
        try { File.Copy(_path, _path + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
        if (TryRead(backupPath, out var backup, out _))
        {
            _data = backup;
            Log.Warn("Журнал изменений был повреждён — восстановлен из резервной копии.");
            // Move the damaged file away so the next save does not rotate it into .bak over the good copy.
            if (CanWrite)
            {
                try { File.Delete(_path); } catch { }
            }
            return;
        }

        // Both copies are unreadable: keep every file untouched and refuse new changes (their undo would be lost).
        _data = new JournalFile();
        UnavailableReason = "Файл журнала и его резервная копия повреждены.";
        Log.Error("Журнал изменений и его резервная копия повреждены — новые изменения заблокированы", error);
    }

    private static bool TryRead(string path, out JournalFile data, out Exception? error)
    {
        data = new JournalFile();
        error = null;
        try
        {
            if (!File.Exists(path)) return false;
            data = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(path), JsonOptions) ?? new JournalFile();
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>Writes the journal to disk. Returns false when nothing was written (the error is logged).</summary>
    public bool Save()
    {
        lock (_lock) return SaveLocked();
    }

    private bool SaveLocked()
    {
        // A non-elevated MONOLITH never creates or touches the machine journal; an unavailable one is never overwritten.
        if (!CanWrite) return false;
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            if (!AppPaths.TryEnsureDir(dir))
            {
                Log.Error($"Не удалось сохранить журнал изменений: папка {dir} недоступна.");
                return false;
            }
            var tmp = _path + ".tmp";
            try { File.Delete(tmp); } catch { }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_data, JsonOptions);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(_path))
                File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmp, _path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить журнал изменений", ex);
            return false;
        }
    }
}

/// <summary>Well-known exit codes of Windows tools.</summary>
public static class ProcessExit
{
    /// <summary>ERROR_SUCCESS_REBOOT_REQUIRED: done, takes effect after a restart (pnputil, msiexec, DISM).</summary>
    public const int RebootRequired = 3010;
}

/// <summary>
/// Validates journal records before they are replayed with admin rights. The journal is untrusted input
/// (CLAUDE.md §7): a record is accepted only when it has exactly the shape MONOLITH's own code writes —
/// a known source, the kind and target that source writes, no protected or hard-blocked service, and for
/// commands an undo command line that is rebuilt from the same code and compared character for character.
/// </summary>
internal static class JournalTrust
{
    private static readonly HashSet<string> ModuleSources = new(StringComparer.Ordinal)
    {
        "services", "services-tasks", "startup", "startup-tasks", "gameboost", "drivers",
    };

    private static readonly Regex TweakSource = new(@"^tweak:[a-z0-9][a-z0-9-]{0,80}$", RegexOptions.CultureInvariant);

    internal static bool IsKnownSource(string source) => ModuleSources.Contains(source) || TweakSource.IsMatch(source);

    /// <summary>Null when the record may be reverted; otherwise a short Russian reason.</summary>
    internal static string? Check(ChangeRecord r)
    {
        if (!IsKnownSource(r.Source)) return $"неизвестный источник записи «{r.Source}»";
        foreach (var s in r.AlsoSources)
            if (!IsKnownSource(s)) return $"неизвестный источник записи «{s}»";
        var isTweak = r.Source.StartsWith("tweak:", StringComparison.Ordinal);

        // Each module writes exactly one kind of record; tweaks may write any kind.
        ChangeKind? expected = r.Source switch
        {
            "services" => ChangeKind.Service,
            "services-tasks" or "startup-tasks" => ChangeKind.ScheduledTask,
            "startup" => ChangeKind.Registry,
            "gameboost" => ChangeKind.PowerScheme,
            "drivers" => ChangeKind.Command,
            _ => null,
        };
        if (expected is { } kind && r.Kind != kind) return $"источник «{r.Source}» не создаёт записи типа {r.Kind}";

        switch (r.Kind)
        {
            case ChangeKind.Registry when r.Registry is { } reg:
                if (!string.Equals(r.Target, $@"reg:{reg.Hive}\{reg.Path}\{reg.Name}", StringComparison.OrdinalIgnoreCase))
                    return "цель записи не совпадает с параметром реестра";
                if (reg.Hive == RegHive.HKCR) return $"раздел {reg.Hive} MONOLITH не меняет";
                if (ChangeJournal.IsForbiddenRestoreTarget(reg)) return $"{reg.FullName} — защищённое место реестра";
                if (r.Source == "startup" && !IsStartupApproved(reg)) return $"{reg.FullName} — не отметка автозагрузки StartupApproved";
                return null;

            case ChangeKind.Service when r.Service is { } svc:
                if (!string.Equals(r.Target, "svc:" + svc.Name, StringComparison.OrdinalIgnoreCase)) return "цель записи не совпадает со службой";
                if (ServiceControl.IsProtected(svc.Name)) return $"служба {svc.Name} защищена от изменений";
                // Older builds could change some services that are hard-blocked now; putting back a start type that
                // lets the service run is always the safe direction, so only a revert TO Disabled is refused.
                if (ServiceGuard.IsHardBlocked(svc.Name) && svc.StartMode == System.ServiceProcess.ServiceStartMode.Disabled)
                    return $"службу {svc.Name} MONOLITH никогда не отключает";
                return null;

            case ChangeKind.ScheduledTask when r.TaskPath is { } task:
                if (!string.Equals(r.Target, "task:" + task, StringComparison.OrdinalIgnoreCase)) return "цель записи не совпадает с задачей";
                if (!task.StartsWith('\\') || task.Any(char.IsControl)) return "некорректный путь задачи";
                return null;

            case ChangeKind.PowerScheme:
                if (r.Target != "power:active") return "некорректная цель записи плана питания";
                if (r.PowerSchemeCreated is not null && !isTweak && !r.AlsoSources.Any(s => s.StartsWith("tweak:", StringComparison.Ordinal)))
                    return "план питания на удаление указан не твиком";
                return null;

            case ChangeKind.Command:
                return CheckCommand(r, isTweak);

            default:
                return null; // no data to revert: RevertOneAsync reports that itself
        }
    }

    private static bool IsStartupApproved(RegistryValueSnapshot reg)
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        var p = reg.Path.TrimEnd('\\');
        return new[] { "Run", "Run32", "StartupFolder" }.Any(sub => string.Equals(p, root + sub, StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────────── Undo commands ─────────────────────────────

    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
    private const string PsPrefix = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";
    private const string CmdHead = "/d /s /c \"";
    private const string Unknown = "команда отката не совпадает ни с одной командой MONOLITH";

    private static string Quoted(string exe) => "\"" + ProcessRunner.Sys(exe) + "\"";
    private static bool SameFile(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? CheckCommand(ChangeRecord r, bool isTweak)
    {
        var file = r.RevertFile ?? "";
        var args = r.RevertArgs ?? "";
        if (!r.Target.StartsWith("cmd:", StringComparison.Ordinal)) return "некорректная цель команды";
        var target = r.Target[4..];

        // Drivers → enable a device; its undo is pnputil /disable-device "<the same instance ID>".
        if (target.StartsWith("device-enable:", StringComparison.Ordinal))
        {
            var id = target["device-enable:".Length..];
            if (r.Source != "drivers" || id.Length == 0 || id.Contains('"') || id.Any(char.IsControl)) return Unknown;
            return SameFile(file, ProcessRunner.Sys("pnputil.exe")) && args == $"/disable-device \"{id}\"" ? null : Unknown;
        }
        if (!isTweak) return Unknown;

        // PowerShell undo scripts: decode, read the original value back out, rebuild with the catalog code, compare.
        if (target.StartsWith("dx:", StringComparison.Ordinal) || target == "spi:visual-effects")
        {
            if (!SameFile(file, TweakCatalog.PowerShellExe) || PsScript(args) is not { } script) return Unknown;
            var rebuilt = target == "spi:visual-effects" ? RebuildSpi(script) : RebuildDx(target[3..], script);
            return rebuilt == args ? null : Unknown;
        }

        var dns = Regex.Match(target, $@"^dns([46]):(\{{?{GuidPattern}\}}?)$");
        if (dns.Success)
        {
            if (UpgradeLegacyDns(r) is not null) return null;
            if (!SameFile(file, TweakCatalog.PowerShellExe) || PsScript(args) is not { } dnsScript) return Unknown;
            var rebuilt = RebuildDns(dns.Groups[1].Value == "6" ? "ipv6" : "ipv4", dns.Groups[2].Value, dnsScript);
            return rebuilt == args ? null : Unknown;
        }

        // cmd.exe undo lines: only the exact powercfg / hibernate lines the catalog writes.
        if (!SameFile(file, ProcessRunner.Sys("cmd.exe"))) return Unknown;
        if (!args.StartsWith(CmdHead, StringComparison.Ordinal) || !args.EndsWith('"') || args.Length <= CmdHead.Length) return Unknown;
        var inner = args[CmdHead.Length..^1];
        var powercfg = Quoted("powercfg.exe");

        if (target == "hibernate")
            return inner == $"{powercfg} /hibernate on" || inner == $"{powercfg} /hibernate on && {powercfg} /hibernate /type reduced"
                ? null : Unknown;

        var pc = Regex.Match(target, $"^powercfg:({GuidPattern}):({GuidPattern}):ac$");
        if (pc.Success)
        {
            var exe = Regex.Escape(powercfg);
            var m = Regex.Match(inner,
                $"^{exe} /setacvalueindex ({GuidPattern}) ({GuidPattern}) ({GuidPattern}) ([0-9]{{1,10}}) && {exe} /setactive SCHEME_CURRENT$");
            return m.Success
                   && string.Equals(m.Groups[1].Value, pc.Groups[1].Value, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(m.Groups[3].Value, pc.Groups[2].Value, StringComparison.OrdinalIgnoreCase)
                ? null : Unknown;
        }

        return Unknown;
    }

    private static string? PsScript(string args)
    {
        if (!args.StartsWith(PsPrefix, StringComparison.Ordinal)) return null;
        try { return Encoding.Unicode.GetString(Convert.FromBase64String(args[PsPrefix.Length..])); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// A DNS undo record written by an older build (cmd.exe + netsh by interface index), rebuilt character for
    /// character with that build's code to prove it is genuine. Returns the equivalent current command (PowerShell,
    /// adapter found by GUID at run time), or null when the record is not such a legacy record.
    /// </summary>
    internal static (string File, string Args)? UpgradeLegacyDns(ChangeRecord r)
    {
        if (r.Kind != ChangeKind.Command || !r.Target.StartsWith("cmd:", StringComparison.Ordinal)) return null;
        var dns = Regex.Match(r.Target[4..], $@"^dns([46]):(\{{?{GuidPattern}\}}?)$");
        if (!dns.Success || !SameFile(r.RevertFile, ProcessRunner.Sys("cmd.exe"))) return null;
        var args = r.RevertArgs ?? "";
        if (!args.StartsWith(CmdHead, StringComparison.Ordinal) || !args.EndsWith('"') || args.Length <= CmdHead.Length) return null;
        var inner = args[CmdHead.Length..^1];
        var family = dns.Groups[1].Value == "6" ? "ipv6" : "ipv4";
        var flush = " && " + Quoted("ipconfig.exe") + " /flushdns";
        if (!inner.EndsWith(flush, StringComparison.Ordinal)) return null;
        var body = inner[..^flush.Length];
        var first = Regex.Match(body, $@"interface {family} set dnsservers name=([0-9]{{1,9}}) source=(?:dhcp|static address=([0-9A-Fa-f:.%]+) register=primary)");
        if (!first.Success) return null;
        var index = int.Parse(first.Groups[1].Value, CultureInfo.InvariantCulture);
        var servers = new List<string>();
        if (first.Groups[2].Success) servers.Add(first.Groups[2].Value);
        foreach (Match m in Regex.Matches(body, "add dnsservers name=[0-9]{1,9} address=([0-9A-Fa-f:.%]+) index="))
            servers.Add(m.Groups[1].Value);
        try
        {
            if (LegacyDnsCommand(family, index, servers) != body) return null;
            return (TweakCatalog.PowerShellExe, TweakCatalog.PowerShellArgs(TweakCatalog.DnsScript(family, dns.Groups[2].Value, servers)));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return null; }
    }

    /// <summary>The netsh line older builds wrote (TweakCatalog.DnsCommand before the GUID-based script).</summary>
    private static string LegacyDnsCommand(string family, int index, IReadOnlyList<string> servers)
    {
        var netsh = Quoted("netsh.exe");
        if (servers.Count == 0) return $"{netsh} interface {family} set dnsservers name={index} source=dhcp";
        var addresses = servers.Select(s => System.Net.IPAddress.TryParse(s, out var ip)
            ? ip.ToString()
            : throw new InvalidOperationException($"некорректный адрес DNS-сервера: {s}")).ToList();
        return string.Join(" && ", addresses.Select((s, i) => i == 0
            ? $"{netsh} interface {family} set dnsservers name={index} source=static address={s} register=primary validate=no"
            : $"{netsh} interface {family} add dnsservers name={index} address={s} index={i + 1} validate=no"));
    }

    /// <summary>Reads the server list back out of a DNS undo script and rebuilds it with the catalog code.</summary>
    private static string? RebuildDns(string family, string guid, string script)
    {
        var servers = new List<string>();
        foreach (Match m in Regex.Matches(script, "'address=([0-9A-Fa-f:.%]+)'"))
            servers.Add(m.Groups[1].Value);
        try { return TweakCatalog.PowerShellArgs(TweakCatalog.DnsScript(family, guid, servers)); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return null; }
    }

    private static string? RebuildDx(string token, string script)
    {
        if (!Regex.IsMatch(token, "^[A-Za-z0-9_]{1,64}$")) return null;
        var m = Regex.Match(script, @"\$v=(\$null|'(?:[^']|'')*');");
        if (!m.Success) return null;
        var raw = m.Groups[1].Value;
        var value = raw == "$null" ? null : raw[1..^1].Replace("''", "'");
        return TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript(token, value));
    }

    private static string? RebuildSpi(string script)
    {
        var states = new List<(uint SetAction, bool On)>();
        foreach (var f in TweakCatalog.VisualEffectFlags)
        {
            var m = Regex.Match(script, $@"\[Monolith\.Spi\]::Set\(0x{f.Set:X},0,\[IntPtr\]([01]),3\)");
            if (!m.Success) return null;
            states.Add((f.Set, m.Groups[1].Value == "1"));
        }
        var anim = Regex.Match(script, @"SetAnimation\(0x49,8,\[int\[\]\]@\(8,([0-9]{1,6})\),3\)");
        if (!anim.Success) return null;
        var minAnimate = int.Parse(anim.Groups[1].Value, CultureInfo.InvariantCulture);
        return TweakCatalog.PowerShellArgs(TweakCatalog.SpiScript(states, minAnimate));
    }
}
