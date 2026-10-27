using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Tweaks;

namespace Monolith.Modules.Optimize;

/// <summary>Profile choice on the Optimize page. Persisted by name in <see cref="AppSettings.LastProfile"/>.</summary>
public enum OptimizeProfile { Normal, Gaming, Recommended, Custom }

/// <summary>One line of the "what will change" preview: target, current value (read lazily) and new value.</summary>
public sealed record PlannedChange(string Target, string NewValue, Func<string>? ReadCurrent);

/// <summary>Pure helpers of the Optimize page (no UI, no writes). Covered by OptimizeTests.</summary>
internal static class OptimizeLogic
{
    public static OptimizeProfile ParseProfile(string? value) =>
        Enum.TryParse<OptimizeProfile>(value?.Trim(), true, out var p) && Enum.IsDefined(p) && !int.TryParse(value, out _)
            ? p
            : OptimizeProfile.Recommended;

    public static ProfileKind? ToKind(OptimizeProfile profile) => profile switch
    {
        OptimizeProfile.Normal => ProfileKind.Normal,
        OptimizeProfile.Gaming => ProfileKind.Gaming,
        OptimizeProfile.Recommended => ProfileKind.Recommended,
        _ => null,
    };

    /// <summary>
    /// Whether "select all" of a section may tick this item: applicable, not expert (needs its warning accepted) and not
    /// deleting user data (needs a deliberate click and its own confirmation).
    /// </summary>
    public static bool IsBulkSelectable(Tweak tweak, bool applicable) =>
        applicable && tweak.Risk != TweakRisk.Advanced && TweakCatalog.DataLossNote(tweak) is null;

    /// <summary>States in which "apply" would actually change something.</summary>
    public static bool WillChange(TweakState state) => state is TweakState.NotApplied or TweakState.Partial or TweakState.Unknown;

    public static string StateText(TweakState? state) => state switch
    {
        null => "ПРОВЕРКА…",
        TweakState.Applied => "ПРИМЕНЕНО",
        TweakState.NotApplied => "НЕ ПРИМЕНЕНО",
        TweakState.Partial => "ЧАСТИЧНО",
        TweakState.NotApplicable => "НЕПРИМЕНИМО",
        _ => "НЕИЗВЕСТНО",
    };

    public static string CategoryTitle(TweakCategory category) => category switch
    {
        TweakCategory.Gaming => "ИГРЫ",
        TweakCategory.Performance => "ПРОИЗВОДИТЕЛЬНОСТЬ",
        TweakCategory.Power => "ПИТАНИЕ",
        TweakCategory.Network => "СЕТЬ",
        TweakCategory.Privacy => "КОНФИДЕНЦИАЛЬНОСТЬ",
        TweakCategory.Interface => "ИНТЕРФЕЙС",
        TweakCategory.Input => "ВВОД",
        _ => "СИСТЕМА",
    };

    public static string CategoryDescription(TweakCategory category) => category switch
    {
        TweakCategory.Gaming => "Как Windows обращается с запущенными играми: приоритет, захват, вывод кадров.",
        TweakCategory.Performance => "Фоновая нагрузка и отзывчивость системы.",
        TweakCategory.Power => "Схемы и параметры электропитания.",
        TweakCategory.Network => "Сетевые параметры и фоновая передача данных.",
        TweakCategory.Privacy => "Реклама, рекомендации и сбор диагностических данных.",
        TweakCategory.Interface => "Проводник, меню «Пуск», панель задач и визуальные эффекты.",
        TweakCategory.Input => "Мышь, клавиатура и другие устройства ввода.",
        _ => "Обслуживание, хранилище и служебные параметры Windows.",
    };

    /// <summary>Lower case, trimmed, «ё» folded to «е» — so search ignores case and the ё/е difference.</summary>
    public static string NormalizeSearch(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLower(CultureInfo.GetCultureInfo("ru-RU")).Replace('ё', 'е');

    /// <summary>Every word of the query must occur in the title, description, details, category or id.</summary>
    public static bool Matches(Tweak tweak, string? query)
    {
        var q = NormalizeSearch(query);
        if (q.Length == 0) return true;
        var haystack = NormalizeSearch(string.Join('\n', tweak.Title, tweak.Description, tweak.Details, tweak.CategoryText, tweak.Id, tweak.RiskText));
        foreach (var word in q.Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!haystack.Contains(word, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>Russian plural: 1 пункт, 2 пункта, 5 пунктов, 21 пункт, 11 пунктов.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var a = Math.Abs(n);
        var mod100 = a % 100;
        if (mod100 is >= 11 and <= 14) return many;
        return (a % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="desired"/> with the fewest collection operations,
    /// keeping item identity (so containers, focus and scroll position survive filtering).
    /// </summary>
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var keep = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item)) continue;

            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], item)) { existing = j; break; }
            }
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, item);
        }

        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }

    /// <summary>Journal target ("reg:HKCU\…", "svc:Name", "task:\…", "power:active", "cmd:x") → readable Russian.</summary>
    public static string DescribeTarget(string target)
    {
        if (string.IsNullOrEmpty(target)) return "—";
        var colon = target.IndexOf(':');
        if (colon <= 0) return target;
        var kind = target[..colon];
        var rest = target[(colon + 1)..];
        return kind switch
        {
            "reg" => rest,
            "svc" => $"Служба {rest}",
            "task" => $"Задача {rest}",
            "power" => "Активная схема питания",
            "cmd" => $"Параметр {rest}",
            _ => target,
        };
    }

    // ───────────────────────────── Shared journal records ─────────────────────────────

    /// <summary>Journal source of an Optimize item: "tweak:&lt;id&gt;".</summary>
    public static string SourceOf(string tweakId) => "tweak:" + tweakId;

    /// <summary>Active records that undoing these sources reverts: every record any of them touched (first owner or merged in).</summary>
    public static IReadOnlyList<ChangeRecord> RecordsTouchedBy(IEnumerable<ChangeRecord> records, IReadOnlyCollection<string> sources) =>
        records.Where(r => !r.Reverted && sources.Any(r.TouchedBy)).ToList();

    /// <summary>
    /// Other sources that reverting these records also undoes. Two items that changed the same setting share one record
    /// (it keeps the true original value), so restoring it takes back both changes.
    /// </summary>
    public static IReadOnlyList<string> SharedSources(IEnumerable<ChangeRecord> records, IReadOnlyCollection<string> own) =>
        records.SelectMany(r => r.AlsoSources.Prepend(r.Source))
               .Where(s => !string.IsNullOrWhiteSpace(s) && !own.Contains(s))
               .Distinct(StringComparer.Ordinal)
               .ToList();

    /// <summary>A journal source in words, for "также откатится: …" lines.</summary>
    public static string DescribeSource(string source, Func<string, string?> tweakTitle)
    {
        if (source.StartsWith("tweak:", StringComparison.Ordinal))
        {
            var id = source["tweak:".Length..];
            return tweakTitle(id) is { } title ? $"«{title}»" : $"пункт {id}";
        }
        return source switch
        {
            "services" => "изменение в разделе «Службы»",
            "services-tasks" => "изменение задач в разделе «Службы»",
            "startup" or "startup-tasks" => "изменение в разделе «Автозапуск»",
            "gameboost" => "игровой сеанс MONOLITH",
            "drivers" => "изменение в разделе «Железо и драйверы»",
            _ => $"изменение «{source}»",
        };
    }

    /// <summary>
    /// The row's «КАК ОТКАТИТЬ» text. Never promises more than the journal can do: when the item deletes data,
    /// the text says plainly that only the setting comes back.
    /// </summary>
    public static string UndoText(string? restartText, string? dataLossNote)
    {
        const string where = "Кнопка «ОТКАТИТЬ» у этого пункта или раздел «Восстановление».";
        var restart = restartText is null ? "" : $" После отката тоже {restartText}.";
        if (dataLossNote is not null)
            return $"{where} Откат вернёт прежнее значение настройки, но не удалённые данные.{restart} {dataLossNote}";
        return restartText is null
            ? $"{where} Перед изменением MONOLITH сохраняет исходные значения и возвращает их точно такими, какими они были."
            : $"{where} Исходные значения сохраняются перед изменением.{restart}";
    }

    // ───────────────────────────── Restore point wording ─────────────────────────────

    /// <summary>
    /// Action-bar note about the restore point. Honest about what will happen: nothing when the setting is off or System
    /// Protection is off; otherwise MONOLITH only asks Windows for one (Windows may still decline).
    /// </summary>
    public static string RestorePointBarNote(bool settingOn, bool? protectionOn) =>
        !settingOn ? "точка восстановления отключена в настройках"
        : protectionOn == false ? "защита системы выключена — точки восстановления не будет, откат через журнал"
        : "перед изменениями MONOLITH запросит точку восстановления Windows";

    /// <summary>Footnote of the apply confirmation.</summary>
    public static string RestorePointFootnote(bool settingOn, bool protectionOn) =>
        !settingOn ? "Точка восстановления отключена в настройках. Откат настроек — через журнал MONOLITH."
        : !protectionOn ? "Защита системы выключена — точки восстановления не будет. Откат настроек — через журнал MONOLITH."
        : "Перед изменениями MONOLITH запросит у Windows точку восстановления; создана ли она, покажет итог. " +
          "Откат настроек — через журнал MONOLITH в любом случае.";

    /// <summary>First line of the apply result: what really happened with the restore point.</summary>
    public static string RestorePointResultLine(string? outcome, bool settingOn)
    {
        if (string.IsNullOrWhiteSpace(outcome))
            return settingOn
                ? "Точка восстановления не создавалась. Откат настроек — через журнал MONOLITH."
                : "Точка восстановления не создавалась: отключено в настройках. Откат настроек — через журнал MONOLITH.";
        var text = outcome.Trim();
        return Monolith.Modules.Restore.RestoreFormat.IsRestorePointCreated(text)
            ? text
            : $"{text.TrimEnd('.')}. Откат настроек — через журнал MONOLITH.";
    }

    public static string RegistryTarget(RegHive hive, string path, string name) =>
        $"{hive}\\{path}\\{(name.Length == 0 ? "(По умолчанию)" : name)}";

    /// <summary>Formats a value MONOLITH is going to write.</summary>
    public static string FormatNewValue(RegistryValueKind kind, object? value)
    {
        if (value is null) return "удалить значение";
        return kind switch
        {
            RegistryValueKind.DWord => FormatNumber(Convert.ToInt64(value, CultureInfo.InvariantCulture) & 0xFFFFFFFFL) + " (DWORD)",
            RegistryValueKind.QWord => FormatNumber(Convert.ToInt64(value, CultureInfo.InvariantCulture)) + " (QWORD)",
            RegistryValueKind.Binary when value is byte[] b => FormatBytes(b),
            RegistryValueKind.MultiString when value is string[] a => string.Join("; ", a.Select(s => $"«{s}»")),
            _ => $"«{value}»",
        };
    }

    /// <summary>Formats a value as read back from the registry (runtime type decides).</summary>
    public static string FormatCurrentValue(object? value) => value switch
    {
        null => "нет значения",
        int i => FormatNumber(unchecked((uint)i)),
        long l => FormatNumber(l),
        byte[] b => FormatBytes(b),
        string[] a => string.Join("; ", a.Select(s => $"«{s}»")),
        _ => $"«{value}»",
    };

    private static string FormatNumber(long v) => v is >= 0 and < 10
        ? v.ToString(CultureInfo.InvariantCulture)
        : $"{v.ToString(CultureInfo.InvariantCulture)} (0x{v:X})";

    private static string FormatBytes(byte[] b) =>
        b.Length == 0 ? "пусто" : BitConverter.ToString(b.Length > 24 ? b[..24] : b).Replace('-', ' ') + (b.Length > 24 ? " …" : "");

    /// <summary>
    /// What applying the tweak changes, line by line. Building the list is pure; reading the current value
    /// happens only when <see cref="PlannedChange.ReadCurrent"/> is called (read-only registry/service/task queries).
    /// </summary>
    public static IReadOnlyList<PlannedChange> PlannedChanges(Tweak tweak) => tweak switch
    {
        RegistryTweak r => r.Values
            .Select(v => new PlannedChange(
                RegistryTarget(v.Hive, v.Path, v.Name),
                FormatNewValue(v.Kind, v.Value),
                () => FormatCurrentValue(RegistryHelper.Read(v.Hive, v.Path, v.Name))))
            .ToList(),
        ServiceTweak s => s.Services
            .Select(x => new PlannedChange(
                $"Служба {x.Service} · тип запуска",
                ServiceControl.DescribeStart(x.Mode, false),
                () => ServiceControl.Query(x.Service)?.Describe() ?? "служба не установлена"))
            .ToList(),
        ScheduledTaskTweak t => t.Tasks
            .Select(path => new PlannedChange(
                $"Задача {path}",
                t.Enable ? "включена" : "отключена",
                () => ScheduledTasks.Query(path) is { Exists: true } info ? (info.Enabled ? "включена" : "отключена") : "нет в системе"))
            .ToList(),
        _ => [],
    };
}
