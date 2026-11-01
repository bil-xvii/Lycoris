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
    /// Tweaks whose apply deletes user data that undo cannot bring back (local list: Tweak has no such property yet).
    /// They are never bulk-selected and always get a danger confirmation, even with ConfirmBeforeApply off.
    /// </summary>
    private static readonly Dictionary<string, string> DataLossNotes = new(StringComparer.Ordinal)
    {
        ["recall-snapshots-off"] = "Уже сохранённые снимки Recall будут удалены — откат их не вернёт, он только снова разрешит сохранение.",
    };

    /// <summary>The irreversible data-loss note of a tweak, or null when everything it does can be undone.</summary>
    public static string? DataLossNote(string tweakId) => DataLossNotes.GetValueOrDefault(tweakId);

    /// <summary>
    /// Tweak ids (without <paramref name="prefix"/>) among <paramref name="sources"/> other than <paramref name="ids"/>,
    /// distinct, in first-seen order. Non-tweak sources (other modules) are kept by their full source name.
    /// </summary>
    public static List<string> OtherTweakSources(IEnumerable<string> sources, string prefix, IReadOnlyCollection<string> ids)
    {
        var result = new List<string>();
        foreach (var src in sources)
        {
            if (string.IsNullOrEmpty(src)) continue;
            var id = src.StartsWith(prefix, StringComparison.Ordinal) ? src[prefix.Length..] : src;
            if (ids.Contains(id) || result.Contains(id)) continue;
            result.Add(id);
        }
        return result;
    }

    /// <summary>Readable name of a non-tweak journal source (another MONOLITH page).</summary>
    public static string SourceLabel(string source) => source switch
    {
        "services" or "services-tasks" => "Службы",
        "startup" or "startup-tasks" => "Автозапуск",
        "gameboost" => "Игровой сеанс",
        "games" => "Игры",
        "drivers" => "Драйверы",
        "devices" => "Устройства и сеть",
        _ => source,
    };

    /// <summary>States in which "apply" would actually change something.</summary>
    public static bool WillChange(TweakState state) => state is TweakState.NotApplied or TweakState.Partial or TweakState.Unknown;

    // ───────────────────────────── Exclusive groups & dependencies (selection rules) ─────────────────────────────

    /// <summary>Why a not-applicable row cannot be applied: the tweak's own reason, or the generic one.</summary>
    public static string NotApplicableText(Tweak tweak) => string.IsNullOrWhiteSpace(tweak.NotApplicableReason)
        ? "Не подходит для этого компьютера или этой версии Windows: нужного компонента нет в системе."
        : tweak.NotApplicableReason!;

    /// <summary>The note of a dependent row whose dependency is checked and pending in the same apply.</summary>
    public static string DependencyNote(string requiredTitle) => $"Применится после «{requiredTitle}».";

    /// <summary>
    /// The caption under a row: null while applicable; otherwise «Применится после «…»» when a dependency is pending
    /// (<paramref name="pendingDependencyTitle"/>), else the not-applicable reason.
    /// </summary>
    public static string? StatusNote(Tweak tweak, TweakState? state, string? pendingDependencyTitle) =>
        state != TweakState.NotApplicable ? null
        : pendingDependencyTitle is { } title ? DependencyNote(title)
        : NotApplicableText(tweak);

    /// <summary>
    /// The first dependency of <paramref name="tweak"/> that is checked and pending in the same apply (any one satisfies
    /// it), or null. <paramref name="isPending"/> answers for an id; unknown ids are simply not pending.
    /// </summary>
    public static string? PendingDependency(Tweak tweak, Func<string, bool> isPending) =>
        tweak.Requires.FirstOrDefault(isPending);

    /// <summary>
    /// Whether a row can be checked and applied now: its own detection says applicable (not NotApplicable), or it is
    /// NotApplicable but one of the tweaks it requires is checked and pending in the same apply.
    /// </summary>
    public static bool IsApplicableNow(TweakState? state, Tweak tweak, Func<string, bool> isPending) =>
        state != TweakState.NotApplicable || PendingDependency(tweak, isPending) is not null;

    /// <summary>
    /// A default row counts for a profile tile when it is applicable, or when it depends on another default of the same
    /// profile that will be pending once the profile is selected (ApplySelection then checks it via the dependency).
    /// </summary>
    public static bool IsProfileMember(Tweak tweak, bool applicable, IReadOnlySet<string> defaults, Func<string, bool> defaultWillBePending) =>
        defaults.Contains(tweak.Id) && (applicable || tweak.Requires.Any(r => defaults.Contains(r) && defaultWillBePending(r)));

    /// <summary>Other members of the tweak's exclusive group among <paramref name="tweaks"/>, in their order.</summary>
    public static IReadOnlyList<Tweak> ExclusivePeers(Tweak tweak, IEnumerable<Tweak> tweaks) =>
        tweak.ExclusiveGroup is null ? [] : tweaks.Where(tweak.SharesGroupWith).ToList();

    /// <summary>The checked members of <paramref name="checkedNow"/>'s exclusive group that must be unchecked because it was just checked.</summary>
    public static IReadOnlyList<Tweak> MembersToUncheck(Tweak checkedNow, IEnumerable<(Tweak Tweak, bool IsChecked)> rows) =>
        rows.Where(r => r.IsChecked && checkedNow.SharesGroupWith(r.Tweak)).Select(r => r.Tweak).ToList();

    /// <summary>
    /// True when the section's selectable rows are "all selected" for the header checkbox: every row is checked, or belongs
    /// to an exclusive group one of whose members is checked (a group can never have more than one checked member, so the
    /// header must not stay indeterminate — nor re-select on the second click — because of the others).
    /// </summary>
    public static bool AllSelected(IReadOnlyCollection<(Tweak Tweak, bool IsChecked)> rows) =>
        rows.Count > 0 && rows.All(r => r.IsChecked || rows.Any(p => p.IsChecked && r.Tweak.SharesGroupWith(p.Tweak)));

    /// <summary>
    /// The members of each exclusive group to uncheck so that at most one stays checked: the first in catalog order
    /// wins (used when a saved or imported set still has two members).
    /// </summary>
    public static IReadOnlyList<Tweak> ExclusiveSurplus(IEnumerable<Tweak> checkedTweaks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var surplus = new List<Tweak>();
        foreach (var t in checkedTweaks)
        {
            if (t.ExclusiveGroup is null) continue;
            if (!seen.Add(t.ExclusiveGroup)) surplus.Add(t);
        }
        return surplus;
    }

    /// <summary>«заменит: «A», «B»» for the confirmation line, or null without titles.</summary>
    public static string? ReplacesNote(IEnumerable<string> replacedTitles)
    {
        var titles = replacedTitles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => $"«{t}»").ToList();
        return titles.Count == 0 ? null : "заменит: " + string.Join(", ", titles);
    }

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
