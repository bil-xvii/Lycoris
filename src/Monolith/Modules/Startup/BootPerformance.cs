using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Monolith.Core;

namespace Monolith.Modules.Startup;

/// <summary>One measured boot (Diagnostics-Performance event 100). Times in milliseconds.</summary>
/// <param name="MainPathMs">From the Windows logo until the sign-in screen (or the desktop with automatic sign-in).</param>
/// <param name="PostBootMs">From there until the system is about 80 % idle — when startup apps load.</param>
public sealed record BootSample(DateTime StartUtc, int BootMs, int MainPathMs, int PostBootMs, bool IsDegradation, bool AfterInstall);

/// <summary>
/// A component Windows reported as slower than its own usual time during a boot (events 101–110).
/// <see cref="DegradationMs"/> is the extra time over that usual time, not the component's whole cost.
/// </summary>
public sealed record BootDelay(int EventId, DateTime BootStartUtc, string Name, string? FriendlyName, string? Path, int TotalMs, int DegradationMs)
{
    public bool IsApplication => EventId == BootPerformance.ApplicationEvent;
}

/// <summary>The last boots (newest first) and the slowdowns Windows recorded for them.</summary>
public sealed record BootReport(IReadOnlyList<BootSample> Boots, IReadOnlyList<BootDelay> Delays)
{
    public BootSample? Last => Boots.Count > 0 ? Boots[0] : null;
}

/// <summary>An autostart row matched to the application slowdowns: the newest one and in how many boots it was reported.</summary>
public sealed record BootDelayMatch(BootDelay Latest, int Boots);

/// <summary>A parsed event: id, creation time and its EventData values by name.</summary>
public sealed record PerfEvent(int Id, DateTime? CreatedUtc, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Boot-time measurement from Windows' own counters: the Microsoft-Windows-Diagnostics-Performance/Operational log
/// (event 100 = boot duration, 101 app / 102 driver / 103 service / 109 device / 104–108, 110 system slowdowns).
/// Read-only, nothing is stored. The log needs administrator rights; without them <see cref="Read"/> returns null and
/// the page shows nothing. Windows does not measure every power-on (fast startup, resume from hibernation).
/// </summary>
public static class BootPerformance
{
    public const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";
    public const int BootEvent = 100;
    public const int ApplicationEvent = 101;
    public const int MaxBoots = 10;
    private const int MaxEvents = 800;

    /// <summary>
    /// File names that start other programs (or are too generic): a row is matched to them only by the full path,
    /// never by the name alone (Discord's and Teams' "Update.exe" are different programs).
    /// </summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "update.exe", "updater.exe", "launcher.exe", "setup.exe", "helper.exe", "rundll32.exe", "cmd.exe", "wscript.exe",
        "cscript.exe", "powershell.exe", "pwsh.exe", "mshta.exe", "regsvr32.exe", "conhost.exe", "explorer.exe", "svchost.exe",
        "dllhost.exe", "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "node.exe", "electron.exe", "app.exe",
    };

    // ───────────────────────────── Live log (read-only) ─────────────────────────────

    /// <summary>Reads the last <see cref="MaxBoots"/> boots and their slowdowns; null without access or without the log.</summary>
    public static BootReport? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName, $"*[System[(EventID>={BootEvent} and EventID<=110)]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            var events = new List<PerfEvent>();
            var boots = 0;
            for (var i = 0; i < MaxEvents; i++)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;
                if (ParseEventXml(record.ToXml()) is not { } e) continue;
                events.Add(e);
                // Newest first: the slowdowns of a boot are logged after its event 100, so they are already read here.
                if (e.Id == BootEvent && ++boots >= MaxBoots) break;
            }
            return Build(events);
        }
        catch (UnauthorizedAccessException)
        {
            return null; // the log is for administrators: read-only mode shows nothing
        }
        catch (EventLogNotFoundException)
        {
            return null;
        }
        catch (EventLogException ex)
        {
            Log.Warn($"Автозапуск: журнал времени загрузки недоступен: {ex.Message}");
            return null;
        }
    }

    // ───────────────────────────── Parsing and matching (pure) ─────────────────────────────

    /// <summary>Event id, TimeCreated and EventData of one event as rendered by <c>EventRecord.ToXml()</c>. DTDs are refused.</summary>
    public static PerfEvent? ParseEventXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        XDocument doc;
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
        if (doc.Root is not { } root) return null;

        var system = Child(root, "System");
        if (!int.TryParse(Child(system, "EventID")?.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
        var created = ParseUtc((string?)Child(system, "TimeCreated")?.Attribute("SystemTime"));

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Child(root, "EventData")?.Elements().Where(e => e.Name.LocalName == "Data") ?? [])
            if ((string?)d.Attribute("Name") is { Length: > 0 } name) data[name] = d.Value.Trim();
        return new PerfEvent(id, created, data);

        static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
    }

    /// <summary>
    /// Boots newest first (at most <paramref name="maxBoots"/>) and the slowdowns that belong to them: a slowdown's
    /// StartTime is the start of its boot, so older ones (and those of boots that are not shown) are dropped.
    /// </summary>
    public static BootReport Build(IEnumerable<PerfEvent> events, int maxBoots = MaxBoots)
    {
        var boots = new List<BootSample>();
        var delays = new List<BootDelay>();
        foreach (var e in events)
        {
            if (e.Id == BootEvent && ToBoot(e) is { } boot) boots.Add(boot);
            else if (e.Id is > BootEvent and <= 110 && ToDelay(e) is { } delay) delays.Add(delay);
        }

        boots = boots.GroupBy(b => b.StartUtc).Select(g => g.First())
            .OrderByDescending(b => b.StartUtc).Take(Math.Max(1, maxBoots)).ToList();
        DateTime? from = boots.Count > 0
            ? boots[^1].StartUtc.AddSeconds(-1)
            : delays.Select(d => d.BootStartUtc).Distinct().OrderByDescending(t => t).Take(Math.Max(1, maxBoots)).Cast<DateTime?>().LastOrDefault();

        var kept = delays.Where(d => from is null || d.BootStartUtc >= from).Distinct()
            .OrderByDescending(d => d.BootStartUtc).ThenByDescending(d => d.DegradationMs).ToList();
        return new BootReport(boots, kept);
    }

    /// <summary>
    /// The application slowdowns (event 101) that are this row's program: same full path, or same file name when the
    /// name is specific enough. Null when Windows reported nothing for it.
    /// </summary>
    public static BootDelayMatch? Match(string? targetPath, IEnumerable<BootDelay> delays)
    {
        var target = NormalizePath(targetPath);
        var file = FileName(target);
        if (target is null || file is null) return null;

        var hits = delays.Where(d => d.IsApplication && IsSameProgram(d, target, file)).ToList();
        if (hits.Count == 0) return null;
        var latest = hits.OrderByDescending(h => h.BootStartUtc).ThenByDescending(h => h.DegradationMs).First();
        return new BootDelayMatch(latest, hits.Select(h => h.BootStartUtc).Distinct().Count());
    }

    private static bool IsSameProgram(BootDelay d, string target, string file)
    {
        var path = NormalizePath(d.Path);
        if (path is not null && string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) return true;
        var name = FileName(path) ?? FileName(NormalizePath(d.Name));
        return name is not null && string.Equals(name, file, StringComparison.OrdinalIgnoreCase) && !GenericNames.Contains(file);
    }

    /// <summary>Trimmed, unquoted, environment-expanded path with Windows separators and no <c>\\?\</c> prefix.</summary>
    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim()).Replace('/', '\\');
        foreach (var prefix in new[] { @"\\?\", @"\??\" })
            if (p.StartsWith(prefix, StringComparison.Ordinal)) p = p[prefix.Length..];
        return p.Length == 0 ? null : p;
    }

    /// <summary>Last path segment with Windows separators (works the same on every OS, unlike Path.GetFileName).</summary>
    internal static string? FileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.Length == 0 ? null : name;
    }

    private static BootSample? ToBoot(PerfEvent e)
    {
        var total = Int(e, "BootTime");
        if (total is not (> 0 and <= 3_600_000)) return null; // nothing sensible beyond an hour
        if ((ParseUtc(Get(e, "BootStartTime")) ?? e.CreatedUtc) is not { } start) return null;
        var main = Int(e, "MainPathBootTime") ?? 0;
        var post = Int(e, "BootPostBootTime") ?? 0;
        if (main < 0 || post < 0 || main + post > total + 1000) (main, post) = (0, 0);
        return new BootSample(start, total.Value, main, post, Bool(e, "BootIsDegradation"), Bool(e, "BootIsRebootAfterInstall"));
    }

    private static BootDelay? ToDelay(PerfEvent e)
    {
        var name = Get(e, "Name") is { Length: > 0 } n ? n : Get(e, "FriendlyName");
        if (string.IsNullOrWhiteSpace(name)) return null;
        if ((ParseUtc(Get(e, "StartTime")) ?? e.CreatedUtc) is not { } start) return null;
        var friendly = Get(e, "FriendlyName") is { Length: > 0 } f ? f : null;
        var path = Get(e, "Path")?.Trim().Trim('"') is { Length: > 0 } p ? p : null;
        return new BootDelay(e.Id, start, name, friendly, path, Math.Max(0, Int(e, "TotalTime") ?? 0), Math.Max(0, Int(e, "DegradationTime") ?? 0));
    }

    private static string? Get(PerfEvent e, string name) => e.Data.TryGetValue(name, out var v) ? v : null;

    private static int? Int(PerfEvent e, string name) =>
        long.TryParse(Get(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? (int)Math.Clamp(v, int.MinValue, int.MaxValue) : null;

    private static bool Bool(PerfEvent e, string name) => Get(e, name)?.Trim().ToLowerInvariant() is "true" or "1";

    /// <summary>ISO time from the event XML as UTC (the log writes up to 9 fraction digits; .NET reads 7).</summary>
    internal static DateTime? ParseUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Regex.Replace(text.Trim(), @"(\.\d{7})\d+", "$1");
        return DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            : null;
    }

    // ───────────────────────────── Texts ─────────────────────────────

    /// <summary>"41,2 с" (one decimal, Russian comma; culture-independent).</summary>
    public static string Seconds(int ms)
    {
        var tenths = (int)Math.Round(Math.Max(0, ms) / 100.0, MidpointRounding.AwayFromZero);
        return $"{tenths / 10},{tenths % 10} с";
    }

    /// <summary>The muted summary line, e.g. «Последняя загрузка (26.10 09:14): 41,2 с — …». Null when nothing was measured.</summary>
    public static string? SummaryText(BootReport? report, Func<DateTime, DateTime>? toLocal = null)
    {
        if (report?.Last is not { } last) return null;
        toLocal ??= t => t.ToLocalTime();
        var sb = new StringBuilder($"Последняя загрузка ({Stamp(toLocal(last.StartUtc))}): {Seconds(last.BootMs)}");
        if (last.MainPathMs > 0 && last.PostBootMs > 0)
            sb.Append($" — экран входа через {Seconds(last.MainPathMs)}, ещё {Seconds(last.PostBootMs)} до готовности");
        if (last.AfterInstall) sb.Append(", после установки обновлений");
        if (report.Boots.Count >= 3)
        {
            var avg = (int)Math.Round(report.Boots.Average(b => b.BootMs));
            sb.Append($" · в среднем {Seconds(avg)} за {report.Boots.Count} {Plural(report.Boots.Count, "загрузку", "загрузки", "загрузок")}");
        }
        return sb.ToString();
    }

    /// <summary>Tooltip of the summary: where the numbers come from, their limits, every boot and the slowdowns of the last one.</summary>
    public static string? SummaryTooltip(BootReport? report, Func<DateTime, DateTime>? toLocal = null)
    {
        if (report?.Last is not { } last) return null;
        toLocal ??= t => t.ToLocalTime();
        var sb = new StringBuilder();
        sb.AppendLine("Время загрузки записывает сама Windows (журнал Diagnostics-Performance, событие 100): от логотипа до экрана входа " +
                      "(или рабочего стола при автоматическом входе), затем до момента, когда система почти перестаёт быть занята — " +
                      "в это время и запускаются программы автозапуска.");
        sb.AppendLine("Windows замеряет не каждое включение: после «быстрого запуска» и выхода из гибернации замера нет. " +
                      "Разброс между загрузками большой — сравнивайте несколько загрузок.");
        sb.AppendLine();
        foreach (var b in report.Boots)
        {
            var notes = new List<string>();
            if (b.IsDegradation) notes.Add("медленнее обычного");
            if (b.AfterInstall) notes.Add("после обновлений");
            sb.AppendLine($"{Stamp(toLocal(b.StartUtc))} — {Seconds(b.BootMs)}" + (notes.Count > 0 ? $" ({string.Join(", ", notes)})" : ""));
        }

        var lastDelays = report.Delays.Where(d => d.BootStartUtc == last.StartUtc).Take(8).ToList();
        if (lastDelays.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("При последней загрузке Windows отметила задержки (сверх обычного времени):");
            foreach (var d in lastDelays) sb.AppendLine($"{KindText(d.EventId)} {d.FriendlyName ?? d.Name}: +{Seconds(d.DegradationMs)}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Tooltip of the «ЗАМЕДЛЯЕТ ЗАГРУЗКУ» tag.</summary>
    public static string DelayTooltip(BootDelayMatch match, Func<DateTime, DateTime>? toLocal = null)
    {
        toLocal ??= t => t.ToLocalTime();
        var d = match.Latest;
        var times = match.Boots > 1 ? $" Такое было в {match.Boots} {Plural(match.Boots, "загрузке", "загрузках", "загрузках")} из последних." : "";
        return $"Windows отметила, что при загрузке {Stamp(toLocal(d.BootStartUtc))} эта программа запускалась {Seconds(d.TotalMs)} — " +
               $"на {Seconds(d.DegradationMs)} дольше обычного для неё (журнал Diagnostics-Performance, событие 101).{times} " +
               "Это задержка сверх её обычного времени, а не вся её «цена». Если программа не нужна сразу после входа, " +
               "отключите автозапуск — запись не удаляется, вернуть можно здесь же.";
    }

    private static string Stamp(DateTime t) => t.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    internal static string KindText(int eventId) => eventId switch
    {
        101 => "Программа",
        102 => "Драйвер",
        103 => "Служба",
        104 => "Ядро системы",
        105 => "Предвыборка (основная)",
        106 => "Предвыборка (фоновая)",
        107 => "Политики компьютера",
        108 => "Политики пользователя",
        109 => "Устройство",
        110 => "Диспетчер сеансов",
        _ => "Компонент",
    };

    private static string Plural(int n, string one, string few, string many)
    {
        var n10 = n % 10;
        var n100 = n % 100;
        if (n10 == 1 && n100 != 11) return one;
        if (n10 is >= 2 and <= 4 && n100 is < 12 or > 14) return few;
        return many;
    }
}
