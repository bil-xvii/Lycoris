using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lycoris.Core.Security;

namespace Lycoris.Core.Diagnostics;

/// <summary>Builds a bounded, local-only diagnostic archive. This class deliberately has no network APIs.</summary>
public static partial class ProblemReport
{
    public const int MaxArchiveBytes = 5 * 1024 * 1024;
    private const int MaxLogs = 3;
    private const int MaxLogBytes = 512 * 1024;
    private static readonly HashSet<string> SafeSettings = new(StringComparer.Ordinal)
        { "Language", "ExpertMode", "MonitorIntervalMs", "ConfirmBeforeApply", "StartElevated", "Theme" };

    public sealed record Item(string Name, long Bytes);
    public sealed record Result(byte[] Archive, IReadOnlyList<Item> Items);

    public static string Anonymize(string? value, string? userName = null, string? computerName = null)
    {
        var text = value ?? "";
        if (text.Length > 2_000_000) text = text[..2_000_000];
        var replacements = new[] { (Needle: userName ?? Environment.UserName, Replacement: "<user>"), (Needle: computerName ?? Environment.MachineName, Replacement: "<pc>") }
            .Where(x => !string.IsNullOrWhiteSpace(x.Needle)).OrderByDescending(x => x.Needle.Length);
        foreach (var item in replacements)
            text = Regex.Replace(text, Regex.Escape(item.Needle), item.Replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"(?i)\b[A-Z]:\\Users\\[^\\\s]+", "%USERPROFILE%", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"(?i)\b(?:[A-F0-9]{1,4}:){2,}[A-F0-9:]{0,}", "<ip>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "<ip>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", "<mac>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "<email>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"(?im)^.*(?:token|key|secret|password|парол|ключ|токен).*$(?:\r?\n)?", "<redacted>\n", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        text = Regex.Replace(text, @"(?i)\b(?:serial(?:number)?|серийный\s+номер)\s*[:=#]?\s*[\w-]+", "serial: <redacted>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return text;
    }

    public static Result Create()
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Add(string name, Func<byte[]> section) { try { entries[name] = section(); } catch { entries[name] = Encoding.UTF8.GetBytes("нет данных"); } }
        Add("report.txt", () => Encoding.UTF8.GetBytes(Anonymize(BuildReport())));
        Add("settings.json", ReadSettings);
        Add("journal.txt", ReadJournal);
        Add("README.txt", () => Encoding.UTF8.GetBytes("Локальный диагностический отчёт. Внутри: report.txt, последние доступные логи, безопасные параметры, сводка журнала. Не включены значения реестра, пользовательские пути и секреты. Файл не отправляется автоматически.\n"));
        try
        {
            if (Directory.Exists(AppPaths.LogsDir))
                foreach (var path in Directory.EnumerateFiles(AppPaths.LogsDir).Where(p => !IsLink(p)).OrderByDescending(File.GetLastWriteTimeUtc).Take(MaxLogs))
                    try { entries["logs/" + Path.GetFileName(path)] = Encoding.UTF8.GetBytes(Anonymize(SafeIo.ReadText(path, MaxLogBytes))); } catch { }
        }
        catch { }
        var sizes = entries.Select(e => new Item(e.Key, e.Value.LongLength)).ToArray();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var entry in entries)
            {
                var target = zip.CreateEntry(entry.Key, CompressionLevel.Optimal);
                using var stream = target.Open(); stream.Write(entry.Value);
            }
        var archive = ms.ToArray();
        if (archive.Length > MaxArchiveBytes) throw new InvalidDataException("Report exceeds the archive size limit.");
        return new(archive, sizes);
    }

    private static bool IsLink(string path) { try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); } catch { return true; } }
    private static byte[] ReadSettings()
    {
        using var doc = JsonDocument.Parse(SafeIo.ReadBytes(AppPaths.SettingsReadFile));
        var safe = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject()) if (SafeSettings.Contains(p.Name)) safe[p.Name] = p.Value.Clone();
        return Encoding.UTF8.GetBytes(Anonymize(JsonSerializer.Serialize(safe, new JsonSerializerOptions { WriteIndented = true })));
    }
    private static byte[] ReadJournal()
    {
        var records = ChangeJournal.Instance.Records.TakeLast(20).Select(r => new { r.Timestamp, r.Title, Count = 1 });
        return Encoding.UTF8.GetBytes(Anonymize(JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true })));
    }
    private static string BuildReport()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var lines = new List<string> { $"Lycoris: {version}", $"Language: {Loc.Language}", $"Administrator: {Elevation.IsElevated}", $"Windows: {Environment.OSVersion.Version} ({Environment.OSVersion.VersionString})", $"Mode: {(AppSettings.Current.ExpertMode ? "Expert" : "Simple")}" };
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, OSArchitecture FROM Win32_OperatingSystem");
            foreach (ManagementObject os in searcher.Get()) lines.Add($"Windows edition: {os["Caption"]}; build {os["Version"]}; {os["OSArchitecture"]}");
        }
        catch { lines.Add("Hardware: нет данных"); }
        return string.Join("\n", lines) + "\n";
    }
}
