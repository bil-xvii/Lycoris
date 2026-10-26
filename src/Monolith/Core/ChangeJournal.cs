using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public ChangeJournal(string path)
    {
        _path = path;
        _protected = AppPaths.IsUnderMachineDir(path);
        Load();
    }

    private bool CanWrite => !_protected || Elevation.IsElevated || ChangeSession.BypassElevationForTests;

    public IReadOnlyList<ChangeRecord> Records { get { lock (_lock) return _data.Records.ToList(); } }
    public IReadOnlyList<ChangeBatch> Batches { get { lock (_lock) return _data.Batches.ToList(); } }
    public IReadOnlyList<ChangeRecord> ActiveRecords { get { lock (_lock) return _data.Records.Where(r => !r.Reverted).ToList(); } }

    public ChangeBatch BeginBatch(string title)
    {
        var batch = new ChangeBatch { Title = title };
        lock (_lock) _data.Batches.Add(batch);
        Save();
        return batch;
    }

    public void UpdateBatch(ChangeBatch batch)
    {
        Save();
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
                _data.Records.Add(record);
                result = record;
                isNew = true;
                SaveLocked();
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
                // Never allowProtected: no legitimate record can target a protected service.
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
                if (!AllowedRevertPrograms.Contains(r.RevertFile, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Отказано: команда отката «{r.RevertFile}» не из списка системных утилит.");
                var encoded = (r.RevertArgs ?? "").Contains("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
                if (encoded) Log.Command($"powershell: откат «{r.Title}»");
                var result = await ProcessRunner.RunAsync(r.RevertFile!, r.RevertArgs ?? "",
                    new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(2), LogCommand = !encoded }).ConfigureAwait(false);
                if (!result.Success) throw new InvalidOperationException($"Команда отката завершилась с кодом {result.ExitCode}");
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
            if (AppPaths.MachineDirWasInsecure && (File.Exists(_path) || File.Exists(_path + ".bak")))
            {
                // Someone without admin rights could have edited it: never replay it. Keep it for inspection.
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                QuarantinedFile = _path + $".untrusted-{stamp}";
                try { if (File.Exists(_path)) File.Move(_path, QuarantinedFile); } catch { }
                try { if (File.Exists(_path + ".bak")) File.Move(_path + ".bak", QuarantinedFile + ".bak"); } catch { }
                Log.Warn($"Журнал изменений хранился в незащищённой папке и отложен как недоверенный: {QuarantinedFile}");
                return;
            }
        }

        if (TryRead(_path, out var data, out var error))
        {
            _data = data;
            return;
        }
        if (error is null) return; // no journal yet

        // Damaged: keep a copy, then fall back to the last good version.
        try { File.Copy(_path, _path + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
        if (TryRead(_path + ".bak", out var backup, out _))
        {
            _data = backup;
            Log.Warn("Журнал изменений был повреждён — восстановлен из резервной копии.");
        }
        else
        {
            _data = new JournalFile();
            Log.Error("Журнал изменений повреждён — создана копия, начат новый журнал", error);
        }
        // Move the damaged file away so the next save does not rotate it into .bak over the good copy.
        if (CanWrite)
        {
            try { File.Delete(_path); } catch { }
        }
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

    public void Save()
    {
        lock (_lock) SaveLocked();
    }

    private void SaveLocked()
    {
        // A non-elevated MONOLITH never creates or touches the machine journal.
        if (!CanWrite) return;
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            if (!AppPaths.TryEnsureDir(dir)) return;
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
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить журнал изменений", ex);
        }
    }
}
