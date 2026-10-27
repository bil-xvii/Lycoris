using System.Globalization;
using Monolith.Core;

namespace Monolith.Modules.Restore;

public enum JournalFilter { All, Active, Reverted }

public enum RecordState { Active, Reverted, Failed }

/// <summary>A batch with the records that pass the current filter. Counts always cover the whole batch.</summary>
public sealed record JournalGroup(ChangeBatch Batch, IReadOnlyList<ChangeRecord> Records, int ActiveCount, int TotalCount)
{
    public bool IsSynthetic => Batch.Id == Guid.Empty;
}

/// <summary>Pure helpers for the undo center: grouping, filtering and Russian labels.</summary>
public static class RestoreFormat
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string KindLabel(ChangeKind kind) => kind switch
    {
        ChangeKind.Registry => "РЕЕСТР",
        ChangeKind.Service => "СЛУЖБА",
        ChangeKind.PowerScheme => "ПИТАНИЕ",
        ChangeKind.ScheduledTask => "ЗАДАЧА",
        ChangeKind.Command => "КОМАНДА",
        _ => "ИЗМЕНЕНИЕ",
    };

    /// <summary>"reg:HKCU\…\Name" → "HKCU\…\Name", "svc:DiagTrack" → "DiagTrack", "power:active" → "Активная схема питания".</summary>
    public static string DescribeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "—";
        var colon = target.IndexOf(':');
        if (colon <= 0) return target;
        var prefix = target[..colon].ToLowerInvariant();
        var rest = target[(colon + 1)..];
        return prefix switch
        {
            "reg" or "svc" or "task" or "cmd" => rest.Length == 0 ? target : rest,
            "power" when rest.Equals("active", StringComparison.OrdinalIgnoreCase) => "Активная схема питания",
            "power" => "Схема питания " + rest,
            _ => target,
        };
    }

    public static RecordState StateOf(ChangeRecord r) =>
        r.Reverted ? RecordState.Reverted : string.IsNullOrEmpty(r.RevertError) ? RecordState.Active : RecordState.Failed;

    public static string StatusText(ChangeRecord r) => StateOf(r) switch
    {
        RecordState.Reverted => r.RevertedUtc is { } t ? $"ОТКАЧЕНО {t.ToLocalTime().ToString("dd.MM HH:mm", Ru)}" : "ОТКАЧЕНО",
        RecordState.Failed => "ОШИБКА ОТКАТА",
        _ => "АКТИВНО",
    };

    /// <summary>Core writes exactly "Точка восстановления создана (…)." on success and other wording otherwise.</summary>
    public static bool IsRestorePointCreated(string? message) =>
        message?.TrimStart().StartsWith("Точка восстановления создана", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>SystemRestore.RestorePointType (SRRESTOREPTAPI) → short Russian label.</summary>
    public static string RestorePointTypeLabel(uint type) => type switch
    {
        0 => "УСТАНОВКА ПРОГРАММЫ",
        1 => "УДАЛЕНИЕ ПРОГРАММЫ",
        6 => "ВОССТАНОВЛЕНИЕ",
        7 => "СИСТЕМНАЯ",
        10 => "УСТАНОВКА ДРАЙВЕРА",
        12 => "ИЗМЕНЕНИЕ НАСТРОЕК",
        13 => "ОТМЕНЁННАЯ ОПЕРАЦИЯ",
        14 => "РЕЗЕРВНОЕ КОПИРОВАНИЕ",
        18 => "ОБНОВЛЕНИЕ WINDOWS",
        _ => $"ТИП {type}",
    };

    public static bool IsMonolithPoint(string? description) =>
        description?.StartsWith("MONOLITH", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Groups journal records by batch, newest batch first, records inside a batch in the order they were made.
    /// Records whose batch is missing are collected into a synthetic batch (Id = Guid.Empty).
    /// "All" keeps empty batches (e.g. an irreversible action that only has a restore point); the other filters drop them.
    /// </summary>
    public static IReadOnlyList<JournalGroup> Group(IEnumerable<ChangeBatch> batches, IEnumerable<ChangeRecord> records, JournalFilter filter)
    {
        var batchList = batches.ToList();
        var known = batchList.Select(b => b.Id).ToHashSet();
        var byBatch = records.GroupBy(r => known.Contains(r.BatchId) ? r.BatchId : Guid.Empty)
                             .ToDictionary(g => g.Key, g => g.OrderBy(r => r.TimestampUtc).ToList());

        if (byBatch.TryGetValue(Guid.Empty, out var orphans) && orphans.Count > 0)
            batchList.Add(new ChangeBatch { Id = Guid.Empty, Title = "Изменения без пакета", TimestampUtc = orphans.Max(r => r.TimestampUtc) });

        var result = new List<JournalGroup>();
        foreach (var b in batchList.OrderByDescending(b => b.TimestampUtc))
        {
            var all = byBatch.TryGetValue(b.Id, out var list) ? list : [];
            var visible = filter switch
            {
                JournalFilter.Active => all.Where(r => !r.Reverted).ToList(),
                JournalFilter.Reverted => all.Where(r => r.Reverted).ToList(),
                _ => all,
            };
            if (filter != JournalFilter.All && visible.Count == 0) continue;
            result.Add(new JournalGroup(b, visible, all.Count(r => !r.Reverted), all.Count));
        }
        return result;
    }

    /// <summary>Russian plural: 1 изменение, 2 изменения, 5 изменений.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var n100 = Math.Abs(n) % 100;
        var n10 = n100 % 10;
        if (n100 is >= 11 and <= 14) return many;
        return n10 switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }

    public static string FormatDate(DateTime local) => local.ToString("dd.MM.yyyy · HH:mm", Ru);

    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    /// <summary>
    /// What a revert really touches, taken from the data the revert uses (not from the free-text Target),
    /// so a damaged or forged journal record cannot hide its real target behind a harmless title.
    /// </summary>
    public static string RevertTarget(ChangeRecord r) => r.Kind switch
    {
        ChangeKind.Registry when r.Registry is { } s => s.FullName,
        ChangeKind.Service when r.Service is { } svc => svc.Name,
        ChangeKind.ScheduledTask when !string.IsNullOrWhiteSpace(r.TaskPath) => r.TaskPath!,
        ChangeKind.PowerScheme => "Активная схема питания",
        _ => DescribeTarget(r.Target),
    };

    /// <summary>The undo command of a Command record as it will run: "netsh.exe interface …"; PowerShell scripts decoded.</summary>
    public static string DescribeCommand(ChangeRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.RevertFile)) return "—";
        var exe = System.IO.Path.GetFileName(r.RevertFile);
        var args = (r.RevertArgs ?? "").Trim();
        var marker = args.IndexOf("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var encoded = args[(marker + "-EncodedCommand".Length)..].Trim().Split(' ', 2)[0];
            try
            {
                var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
                // RunPowerShellAsync prepends output settings; they are not part of the action.
                var utf8 = script.IndexOf("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;", StringComparison.Ordinal);
                if (utf8 >= 0) script = script[(utf8 + "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;".Length)..];
                return $"{exe} (скрипт): {Truncate(script.Trim(), 200)}";
            }
            catch (FormatException)
            {
                return $"{exe} (скрипт не читается)";
            }
        }
        return Truncate($"{exe} {args}".Trim(), 200);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Dialog lines for reverting one record: what MONOLITH set, what is there now, what it becomes.</summary>
    public static IReadOnlyList<string> RevertLines(ChangeRecord r, LiveValue live)
    {
        var lines = new List<string> { $"{KindLabel(r.Kind)}: {RevertTarget(r)}" };
        if (live.State == LiveState.Same)
        {
            lines.Add($"Сейчас: {Show(r.NewValue)} — как установил MONOLITH");
        }
        else
        {
            lines.Add($"MONOLITH установил: {Show(r.NewValue)}");
            lines.Add(live.State == LiveState.Drifted
                ? $"Сейчас: {live.Text} — изменено после MONOLITH (вами, Windows или политикой)"
                : $"Сейчас: {live.Text}");
        }
        lines.Add($"Станет: {Show(r.OldValue)}");
        if (r.Kind == ChangeKind.Command) lines.Add("Команда отката: " + DescribeCommand(r));
        if (r.AlsoSources.Count > 0)
            lines.Add($"Эту же настройку меняли и другие действия MONOLITH ({string.Join(", ", r.AlsoSources)}) — исходное значение вернётся и для них");
        return lines;
    }

    /// <summary>One line per record in the batch / «все» dialogs, with the live value when it drifted.</summary>
    public static string BatchLine(LiveRecord l)
    {
        var r = l.Record;
        var head = $"{(string.IsNullOrWhiteSpace(r.Title) ? "Без названия" : r.Title)} · {RevertTarget(r)}";
        var line = l.Live.State switch
        {
            LiveState.Drifted => $"{head}: сейчас «{l.Live.Text}» — изменено после MONOLITH → «{Show(r.OldValue)}»",
            LiveState.Gone => $"{head}: {l.Live.Text} — возвращать нечего",
            _ => $"{head}: «{Show(r.NewValue)}» → «{Show(r.OldValue)}»",
        };
        return r.Kind == ChangeKind.Command ? $"{line} · команда: {DescribeCommand(r)}" : line;
    }

    /// <summary>"3 активных изменения MONOLITH вернутся" with correct agreement for any count.</summary>
    public static string ActiveChangesWillReturn(int n) =>
        $"{n} {Plural(n, "активное изменение", "активных изменения", "активных изменений")} MONOLITH {Plural(n, "вернётся", "вернутся", "вернутся")}";
}
