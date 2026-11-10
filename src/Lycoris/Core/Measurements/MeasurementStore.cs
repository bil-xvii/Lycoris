using System.IO;
using System.Text.Json;
using Lycoris.Core.Security;

namespace Lycoris.Core.Measurements;

/// <summary>Local measurement history. Writes occur only when the caller explicitly calls Save.</summary>
public sealed class MeasurementStore
{
    public const int MaxRecords = 50;
    public static string DefaultPath => Path.Combine(AppPaths.UserDir, "measurements.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    // One writer at a time in this process: two quick clicks must not read the same old file and drop a record.
    private static readonly object Gate = new();
    private readonly string _path;

    public MeasurementStore() : this(DefaultPath) { }
    public MeasurementStore(string path) => _path = Path.GetFullPath(path);

    /// <summary>Where an unreadable history was moved aside by the last Save (null when nothing was moved).</summary>
    public string? PreservedCorruptFile { get; private set; }

    public IReadOnlyList<Measurement> Read() => ReadCore(out _);

    private IReadOnlyList<Measurement> ReadCore(out bool corrupt)
    {
        corrupt = false;
        try
        {
            if (!File.Exists(_path)) return [];
            var rows = JsonSerializer.Deserialize<List<Measurement>>(SafeIo.ReadText(_path), JsonOptions);
            return rows?.Where(IsValid).OrderBy(x => x.RecordedAt).TakeLast(MaxRecords).ToArray() ?? [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            corrupt = true;
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public bool Save(Measurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        if (!IsValid(measurement)) throw new ArgumentException("Measurement contains invalid values.", nameof(measurement));
        lock (Gate)
        {
            try
            {
                if (!AppPaths.TryEnsureDir(Path.GetDirectoryName(_path)!)) return false;
                var existing = ReadCore(out var corrupt);
                if (corrupt)
                {
                    // Never overwrite an unreadable history silently: keep it next to the new file for the owner.
                    var aside = Path.Combine(Path.GetDirectoryName(_path)!,
                        Path.GetFileNameWithoutExtension(_path) + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    File.Move(_path, aside, overwrite: false);
                    PreservedCorruptFile = aside;
                }
                var rows = existing.Where(x => x.Id != measurement.Id).Append(measurement)
                    .OrderBy(x => x.RecordedAt).TakeLast(MaxRecords).ToArray();
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(rows, JsonOptions));
                File.Move(tmp, _path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                return false;
            }
        }
    }

    private static bool IsValid(Measurement row) => row.Id != Guid.Empty && row.Conditions is { } c
        && !string.IsNullOrWhiteSpace(c.WindowsVersion) && !string.IsNullOrWhiteSpace(c.WindowsBuild)
        && !string.IsNullOrWhiteSpace(c.LycorisVersion) && !string.IsNullOrWhiteSpace(c.BootTimeSource)
        && c.EnabledStartupItems >= 0 && (c.FreeMemoryBytes is null or >= 0)
        && (c.BootTimeSeconds is null or >= 0)
        && (row.ChangesApplied is null or >= 0) && (row.ChangesReverted is null or >= 0);
}
