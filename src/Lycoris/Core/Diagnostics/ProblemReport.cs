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

    private static readonly TimeSpan RxTimeout = TimeSpan.FromMilliseconds(200);
    private const RegexOptions Rx = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private const string ExcludedLine = "<line removed>";
    private const string SecretWords = @"(?<![A-Za-z])(?:key|token|secret|passw(?:or)?d|pwd|bearer|парол\w*|ключ\w*|токен\w*)";

    /// <summary>
    /// Removes identity data line by line. Text is first normalized (Unicode FormKC, no invisible format characters, '/' as '\'
    /// inside paths). Anything that cannot be cleaned reliably (regex timeout, a leftover that still looks personal, a very long line)
    /// removes the WHOLE line.
    /// </summary>
    public static string Anonymize(string? value, string? userName = null, string? computerName = null)
    {
        var text = value ?? string.Empty;
        if (text.Length > MaxTextChars) text = text[..MaxTextChars];
        text = Normalize(text);
        var names = new[] { (Needle: Normalize(userName ?? Environment.UserName), Replacement: "<user>"), (Needle: Normalize(computerName ?? Environment.MachineName), Replacement: "<pc>") }
            .Where(x => x.Needle.Length > 0).DistinctBy(x => x.Needle, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Needle.Length).ToArray();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            string line;
            try { line = CleanLine(lines[i], names); }
            catch (RegexMatchTimeoutException) { line = ExcludedLine; }
            lines[i] = line;
        }
        return string.Join("\n", lines);
    }

    private static string Normalize(string text)
    {
        string n;
        try { n = text.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { n = new string(text.Where(c => !char.IsSurrogate(c)).ToArray()).Normalize(NormalizationForm.FormKC); }
        var sb = new StringBuilder(n.Length);
        foreach (var c in n)
        {
            var cat = char.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.Format) continue; // zero-width and bidi characters split names invisibly
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string CleanLine(string line, (string Needle, string Replacement)[] names)
    {
        if (line.Length > 8192) return ExcludedLine;
        foreach (var item in names)
            line = Regex.Replace(line, Regex.Escape(item.Needle), item.Replacement, Rx, RxTimeout);
        // Profile folders of ANY account, either separator, doubled (JSON-escaped) or not, with or without a drive.
        line = Regex.Replace(line, @"(?:[A-Z]:)?[\\/]+(?:Users|home)[\\/]+[^\\/\s""'<>|:*?]+", "%USERPROFILE%", Rx, RxTimeout);
        line = Regex.Replace(line, @"(?<![0-9a-f])(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}(?![0-9a-f])|(?<![0-9a-f])(?:[0-9a-f]{4}\.){2}[0-9a-f]{4}(?![0-9a-f])", "<mac>", Rx, RxTimeout);
        line = Regex.Replace(line, @"(?<![0-9a-f:.])(?:[0-9a-f]{0,4}:){2,}[0-9a-f.:]*(?:%\w+)?", m => ReplaceIpv6(m.Value), Rx, RxTimeout);
        line = Regex.Replace(line, @"(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?![\d])", m => System.Net.IPAddress.TryParse(m.Value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "<ip>" : m.Value, Rx, RxTimeout);
        line = Regex.Replace(line, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "<email>", Rx, RxTimeout);
        if (Regex.IsMatch(line, SecretWords, Rx, RxTimeout))
        {
            line = Regex.Replace(line, @"(?<![A-Za-z0-9+/_=-])[A-Za-z0-9+/_-]{24,}={0,2}(?![A-Za-z0-9+/_=-])", "<secret>", RegexOptions.CultureInvariant, RxTimeout);
            // «key=value», «password: value», «Bearer value»: the value after the word is dropped whatever its shape.
            line = Regex.Replace(line, @"(" + SecretWords + @"\w*(?:\s*[:=]\s*|\s+(?=bearer\s|\S{24,})))(?:""[^""]*""|'[^']*'|\S+)", "$1<redacted>", Rx, RxTimeout);
        }
        line = Regex.Replace(line, @"\b(?:serial(?:\s*(?:number|no\.?))?|s/n|серийный\s+номер)\s*[:=#]?\s*[\w-]+", "serial: <redacted>", Rx, RxTimeout);
        return StillLooksPersonal(line) ? ExcludedLine : line;
    }

    private static string ReplaceIpv6(string candidate)
    {
        var core = candidate.TrimEnd(':', '.');
        var tail = candidate[core.Length..];
        if (core.Length > 0 && System.Net.IPAddress.TryParse(core, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return "<ip>" + tail;
        // A trailing «:»/«.» can also belong to the address («fe80::1:»), try the whole text too.
        return System.Net.IPAddress.TryParse(candidate, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "<ip>" : candidate;
    }

    /// <summary>Second pass over a finished line: anything that would still identify the user means the line is dropped.</summary>
    private static bool StillLooksPersonal(string line)
    {
        if (line.Contains('@') && Regex.IsMatch(line, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", Rx, RxTimeout)) return true;
        if (Regex.IsMatch(line, @"[\\/]+(?:Users|home)[\\/]+", Rx, RxTimeout)) return true;
        if (Regex.IsMatch(line, SecretWords, Rx, RxTimeout) && Regex.IsMatch(line, @"[A-Za-z0-9+/_-]{24,}", RegexOptions.CultureInvariant, RxTimeout)) return true;
        return false;
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

    private static string ReadJournal() => SummarizeJournal(ChangeJournal.Instance.Records);

    /// <summary>Only the change kind and the number of changes of the last 20 records: no titles, targets, values or times.</summary>
    internal static string SummarizeJournal(IEnumerable<ChangeRecord> records)
    {
        var summary = records.TakeLast(20).GroupBy(r => r.Kind).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => new { Kind = g.Key.ToString(), Changes = g.Count() });
        return JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
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
