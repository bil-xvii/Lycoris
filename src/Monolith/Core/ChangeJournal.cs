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
    private JournalFile _data = new();

    public event Action? Changed;

    public ChangeJournal(string path)
    {
        _path = path;
        Load();
    }

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
    /// Adds a record. If the same target was already changed (and not reverted), the FIRST record is kept,
    /// because it holds the true original value; only its NewValue is refreshed.
    /// Returns the record that now represents the target, and whether it is new.
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
                existing.NewValue = record.NewValue;
                if (record.Source != existing.Source && !existing.AlsoSources.Contains(record.Source))
                    existing.AlsoSources.Add(record.Source);
                existing.PowerSchemeCreated ??= record.PowerSchemeCreated;
                result = existing;
                isNew = false;
            }
            else
            {
                _data.Records.Add(record);
                result = record;
                isNew = true;
            }
            SaveLocked();
        }
        Changed?.Invoke();
        return (result, isNew);
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

    private static async Task RevertOneAsync(ChangeRecord r)
    {
        switch (r.Kind)
        {
            case ChangeKind.Registry when r.Registry is not null:
                RegistryHelper.Restore(r.Registry);
                break;

            case ChangeKind.Service when r.Service is not null:
                ServiceControl.Restore(r.Service);
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
                ScheduledTasks.SetEnabled(r.TaskPath, wasEnabled);
                break;

            case ChangeKind.Command when !string.IsNullOrWhiteSpace(r.RevertFile):
                SafetyLock.ThrowIfLocked("откат команды");
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
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            _data = JsonSerializer.Deserialize<JournalFile>(json, JsonOptions) ?? new JournalFile();
        }
        catch (Exception ex)
        {
            // Never lose a damaged journal: keep a copy for manual recovery.
            try { File.Copy(_path, _path + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
            Log.Error("Журнал изменений повреждён — создана копия, начат новый журнал", ex);
            _data = new JournalFile();
        }
    }

    public void Save()
    {
        lock (_lock) SaveLocked();
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            if (!AppPaths.TryEnsureDir(dir)) return;
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, JsonOptions));
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
