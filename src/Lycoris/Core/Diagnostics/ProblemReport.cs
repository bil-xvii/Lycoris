using System.IO;
using System.IO.Compression;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lycoris.Core.Security;

namespace Lycoris.Core.Diagnostics;

/// <summary>Creates a bounded local archive only. No networking APIs are used here.</summary>
public static partial class ProblemReport
{
    public const int MaxArchiveBytes = 5 * 1024 * 1024;
    private const int MaxLogBytes = 512 * 1024;
    private const int MaxLogFilesScanned = 50;
    private const long MaxLogReadBytes = 5L * 1024 * 1024;
    private const int MaxTextChars = 2_000_000;
    private static readonly HashSet<string> SafeSettings = new(StringComparer.Ordinal)
        { "Language", "ExpertMode", "MonitorIntervalMs", "ConfirmBeforeApply", "StartElevated", "Theme" };

    public sealed record Item(string Name, long Bytes);
    public sealed record Result(byte[] Archive, IReadOnlyList<Item> Items);

    public static string Anonymize(string? value, string? userName = null, string? computerName = null)
    {
        var text = value ?? string.Empty;
        if (text.Length > MaxTextChars) text = text[..MaxTextChars];
        var names = new[] { (Needle: userName ?? Environment.UserName, Replacement: "<user>"), (Needle: computerName ?? Environment.MachineName, Replacement: "<pc>") }
            .Where(x => x.Needle.Length > 0).DistinctBy(x => x.Needle, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Needle.Length);
        foreach (var item in names)
            text = Regex.Replace(text, Regex.Escape(item.Needle), item.Replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        text = Regex.Replace(text, @"(?i)\b[A-Z]:\\Users\\[^\\\s\""'<>|]+", "%USERPROFILE%", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        text = Regex.Replace(text, @"(?i)(?<![0-9a-f:])(?:[0-9a-f]{1,4}:){2,}[0-9a-f:]{0,}(?![0-9a-f:])", "<ip>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        text = Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "<ip>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        text = Regex.Replace(text, @"\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", "<mac>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        text = Regex.Replace(text, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "<email>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        text = Regex.Replace(text, @"(?im)^.*(?:token|key|secret|password|парол|ключ|токен).*$", "<redacted>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        text = Regex.Replace(text, @"(?i)\b(?:serial(?:number)?|серийный\s+номер)\s*[:=#]?\s*[\w-]+", "serial: <redacted>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        return text;
    }

    public static Result Create()
    {
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        Add("report.txt", BuildReport);
        Add("settings.json", ReadSettings);
        Add("journal.txt", ReadJournal);
        Add("README.txt", () => "Локальный диагностический архив. Содержит report.txt, до трёх последних журналов работы, безопасные настройки и краткую сводку журнала изменений. Значения реестра, пользовательские пути и секреты не включены. Архив не отправляется автоматически." );
        foreach (var (name, data) in CollectLogs(AppPaths.LogsDir)) entries[name] = data;

        var items = entries.Select(e => new Item(e.Key, e.Value.LongLength)).ToArray();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var entry in entries)
            {
                var target = zip.CreateEntry(entry.Key, CompressionLevel.Optimal);
                using var stream = target.Open(); stream.Write(entry.Value);
            }
        }
        var archive = ms.ToArray();
        if (archive.Length > MaxArchiveBytes) throw new InvalidDataException("Problem report exceeds the archive size limit.");
        return new(archive, items);

        void Add(string name, Func<string> section)
        {
            try { entries[name] = Utf8(Anonymize(section())); }
            catch { entries[name] = Utf8("нет данных"); }
        }
    }

    /// <summary>Newest logs of a folder as anonymized bytes under FIXED names (logs/log-N.txt): the source file name is never used.</summary>
    internal static IReadOnlyList<(string Name, byte[] Data)> CollectLogs(string dir, int maxFiles = MaxLogFilesScanned, long maxReadBytes = MaxLogReadBytes)
    {
        var result = new List<(string, byte[])>();
        try
        {
            if (!Directory.Exists(dir)) return result;
            // At most maxFiles entries are looked at BEFORE sorting (a folder with millions of files must not stall the report).
            var candidates = Directory.EnumerateFiles(dir).Take(maxFiles).Where(p => !IsLink(p)).OrderByDescending(SafeLastWrite).ToList();
            long read = 0;
            foreach (var path in candidates)
            {
                if (result.Count >= 3) break;
                long charge;
                try { charge = Math.Min(new FileInfo(path).Length, MaxLogBytes); } catch { continue; }
                if (read + charge > maxReadBytes) break;
                read += charge;
                try { result.Add(($"logs/log-{result.Count + 1}.txt", Utf8(Anonymize(SafeIo.ReadText(path, MaxLogBytes))))); } catch { }
            }
        }
        catch { }
        return result;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
    private static bool IsLink(string path) { try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); } catch { return true; } }
    private static DateTime SafeLastWrite(string path) { try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; } }

    private static string ReadSettings()
    {
        using var doc = JsonDocument.Parse(SafeIo.ReadBytes(AppPaths.SettingsReadFile));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Settings root is not an object.");
        var safe = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject()) if (SafeSettings.Contains(p.Name)) safe[p.Name] = p.Value.Clone();
        return JsonSerializer.Serialize(safe, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ReadJournal()
    {
        var records = ChangeJournal.Instance.Records.TakeLast(20).Select(r => new { Time = r.TimestampUtc, r.Title, Changes = 1 });
        return JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildReport()
    {
        var lines = new List<string>
        {
            $"Lycoris: {Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown"}",
            $"Language: {Loc.Language}", $"Administrator: {Elevation.IsElevated}",
            $"Windows: {Environment.OSVersion.Version} ({Environment.OSVersion.VersionString})",
            $"Mode: {(AppSettings.Current.ExpertMode ? "Expert" : "Simple")}",
        };
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem");
            foreach (ManagementObject os in searcher.Get()) lines.Add($"Windows edition: {os["Caption"]}; version {os["Version"]}; build {os["BuildNumber"]}; {os["OSArchitecture"]}");
        }
        catch { lines.Add("Windows edition: нет данных"); }
        AddWmi(lines, "Processor", "SELECT Name FROM Win32_Processor", "Name");
        AddWmi(lines, "Graphics", "SELECT Name FROM Win32_VideoController", "Name");
        AddWmi(lines, "Memory (bytes)", "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem", "TotalPhysicalMemory");
        AddWmi(lines, "Disks", "SELECT Model, Size FROM Win32_DiskDrive", "Model", "Size");
        return string.Join("\n", lines) + "\n";
    }

    private static void AddWmi(List<string> lines, string title, string query, params string[] properties)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            var rows = searcher.Get().Cast<ManagementObject>().Select(row => string.Join("; ", properties.Select(p => $"{p}: {row[p] ?? "?"}"))).ToArray();
            lines.Add(title + ": " + (rows.Length == 0 ? "нет данных" : string.Join(" | ", rows)));
        }
        catch { lines.Add(title + ": нет данных"); }
    }
}
