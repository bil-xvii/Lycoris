using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Tweaks;

namespace Monolith.Modules.Optimize;

/// <summary>
/// Scenario choice on the Optimize page (the profile tiles). Persisted by NAME in <see cref="AppSettings.LastProfile"/>, so the
/// order here is free; <see cref="OptimizeLogic.ParseProfile"/> keeps older names («Normal», «Gaming», «Recommended», «Custom»)
/// and maps anything unknown to Recommended. Normal = «Работа и учёба» (the former «Обычный»), Streaming = «Стриминг и запись»,
/// Efficiency = «Тихий ПК» on a desktop / «Ноутбук: автономность» on a laptop.
/// </summary>
public enum OptimizeProfile { Normal, Gaming, Recommended, Streaming, Efficiency, Custom }

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
        OptimizeProfile.Streaming => ProfileKind.Streaming,
        OptimizeProfile.Efficiency => ProfileKind.Efficiency,
        _ => null,
    };

    /// <summary>The scenarios that have a catalog set (everything but «Свой набор»).</summary>
    public static readonly IReadOnlyList<OptimizeProfile> CatalogProfiles =
        [OptimizeProfile.Normal, OptimizeProfile.Gaming, OptimizeProfile.Recommended, OptimizeProfile.Streaming, OptimizeProfile.Efficiency];

    /// <summary>
    /// The Services recommendations profile that follows an Optimize scenario (ServiceCatalog knows only the three original
    /// sets): streaming needs the gaming services set, the efficiency scenario and a custom set get the balanced one.
    /// </summary>
    public static ProfileSet ServicesProfileFor(OptimizeProfile profile) => profile switch
    {
        OptimizeProfile.Normal => ProfileSet.Normal,
        OptimizeProfile.Gaming or OptimizeProfile.Streaming => ProfileSet.Gaming,
        _ => ProfileSet.Recommended,
    };

    // ───────────────────────────── Scenario tiles (static texts; the efficiency tile adapts to laptop/desktop) ─────────────────────────────

    /// <summary>Tile title (UPPER CASE; the tile shows it through the Sentence converter).</summary>
    public static string ScenarioTitle(OptimizeProfile profile, bool laptop) => profile switch
    {
        OptimizeProfile.Normal => Loc.T("РАБОТА И УЧЁБА"),
        OptimizeProfile.Gaming => Loc.T("ИГРЫ"),
        OptimizeProfile.Recommended => Loc.T("РЕКОМЕНДУЕМЫЙ"),
        OptimizeProfile.Streaming => Loc.T("СТРИМИНГ И ЗАПИСЬ"),
        OptimizeProfile.Efficiency => laptop ? Loc.T("НОУТБУК: АВТОНОМНОСТЬ") : Loc.T("ТИХИЙ ПК"),
        _ => Loc.T("СВОЙ НАБОР"),
    };

    /// <summary>Name for running text and the batch title: «Игры», «Тихий ПК», «Ноутбук: автономность».</summary>
    public static string ScenarioName(OptimizeProfile profile, bool laptop) => profile switch
    {
        OptimizeProfile.Normal => Loc.T("Работа и учёба"),
        OptimizeProfile.Gaming => Loc.T("Игры"),
        OptimizeProfile.Recommended => Loc.T("Рекомендуемый"),
        OptimizeProfile.Streaming => Loc.T("Стриминг и запись"),
        OptimizeProfile.Efficiency => laptop ? Loc.T("Ноутбук: автономность") : Loc.T("Тихий ПК"),
        _ => Loc.T("Свой набор"),
    };

    /// <summary>One short line under the tile title (trimmed to the tile width; the full story is the tooltip).</summary>
    public static string ScenarioDescription(OptimizeProfile profile, bool laptop) => profile switch
    {
        OptimizeProfile.Normal => Loc.T("Без рекламы, перезагрузки — ночью."),
        OptimizeProfile.Gaming => Loc.T("Приоритет игре, без фоновой записи."),
        OptimizeProfile.Recommended => Loc.T("Безопасный баланс для любого ПК."),
        OptimizeProfile.Streaming => Loc.T("Без второго кодировщика и утечек."),
        OptimizeProfile.Efficiency => laptop ? Loc.T("Дольше от батареи, тише.") : Loc.T("Тише и холоднее без нагрузки."),
        _ => Loc.T("Только то, что отметили сами."),
    };

    /// <summary>The tile tooltip: what the scenario does and what it deliberately does NOT touch.</summary>
    public static string ScenarioHint(OptimizeProfile profile, bool laptop) => profile switch
    {
        OptimizeProfile.Normal =>
            Loc.T("Без рекламы и подсказок, без перезагрузок с 8:00 до 2:00, локальный поиск без Bing. " +
            "Не трогает: телеметрию и удалённый помощник, игровые настройки."),
        OptimizeProfile.Gaming =>
            Loc.T("Windows отдаёт приоритет игре и меньше отвлекает во время игры: игровой режим, HAGS для DLSS на RTX 40/50, без фоновой " +
            "записи Game Bar. Только проверенные пункты без компромиссов в безопасности."),
        OptimizeProfile.Recommended =>
            Loc.T("Проверенные настройки без компромиссов: подходят и для работы, и для игр. Выбран по умолчанию."),
        OptimizeProfile.Streaming =>
            Loc.T("Нет второго кодировщика Game Bar рядом с OBS, нет конфликтов Win+Alt+R, недавние файлы не попадают в эфир. " +
            "Звук других программ не приглушается при звонках (нужен повторный вход в Windows). Не трогает HAGS; при проблемах с захватом есть пункт «Отключить HAGS». По желанию: «Без отключения USB-устройств»."),
        OptimizeProfile.Efficiency => laptop
            ? Loc.T("Ниже частоты и нагрев под лёгкой нагрузкой — тише; на ноутбуке дольше от батареи. Эффект зависит от производителя — измерьте. " +
                    "Кривые вентиляторов, лимиты мощности и предел заряда MONOLITH не трогает (утилита производителя). " +
                    "Режим «Максимальная эффективность» от сети — по желанию, отдельным пунктом. По желанию: «Без турбобуста процессора от сети».")
            : Loc.T("Ниже частоты и нагрев под лёгкой нагрузкой — тише. Эффект зависит от производителя — измерьте. " +
                    "Кривые вентиляторов, лимиты мощности и предел заряда MONOLITH не трогает (утилита производителя). " +
                    "По желанию: «Без турбобуста процессора от сети»."),
        _ => Loc.T("Ваш собственный набор: отметьте нужные пункты в списке ниже. Выбор запоминается между запусками."),
    };

    /// <summary>The journal batch title of an apply: «Оптимизация: сценарий «Игры»» or «Оптимизация: свой набор».</summary>
    public static string BatchTitle(OptimizeProfile profile, bool laptop) =>
        BatchTitleFor(profile, laptop);

    // The batch title goes into the journal (data on disk): always Russian, the Restore page translates it when shown.
    private static string BatchTitleFor(OptimizeProfile profile, bool laptop)
    {
        using var _ = Loc.UseLanguage(Loc.Ru);
        return profile == OptimizeProfile.Custom
            ? Loc.T("Оптимизация: свой набор")
            : Loc.Tf("Оптимизация: сценарий «{0}»", ScenarioName(profile, laptop));
    }

    /// <summary>
    /// What the scenario cannot do safely and the user does by hand (read-only text for the apply-result dialog). Only for
    /// the scenario just applied; empty for the rest («Игры»: the X3D row's Details already name Win+G).
    /// </summary>
    public static IReadOnlyList<string> ManualSteps(OptimizeProfile profile, bool laptop) => profile switch
    {
        OptimizeProfile.Streaming =>
        [
            Loc.T("Не беспокоить → «Во время игры» — проверьте в Параметрах → Уведомления (MONOLITH не пишет эту настройку)."),
        ],
        OptimizeProfile.Efficiency when laptop =>
        [
            Loc.T("Предел заряда батареи — в утилите производителя ноутбука; динамическую частоту обновления экрана оставьте включённой."),
        ],
        OptimizeProfile.Efficiency =>
        [
            Loc.T("Ограничение FPS — в панели NVIDIA/AMD; кривые вентиляторов — в BIOS или утилите производителя."),
        ],
        _ => [],
    };

    /// <summary>Header line of the manual-steps block in the apply-result dialog.</summary>
    public static readonly string ManualStepsHeader = Loc.T("ВРУЧНУЮ");

    /// <summary>
    /// Whether the efficiency tile («Тихий ПК» / «Ноутбук: автономность») is shown: only once the hardware facts are known, and
    /// only when at least one of its members on this PC is more than a plan switch (a tile that could only switch to Balanced —
    /// Windows 10, dual-CCD X3D — would make nothing quieter).
    /// </summary>
    public static bool EfficiencyTileVisible(bool hardwareFactsKnown, IEnumerable<Tweak> members) =>
        hardwareFactsKnown && members.Any(m => m.ExclusiveGroup != TweakGroups.PowerPlan);

    /// <summary>
    /// Tweaks whose apply deletes user data that undo cannot bring back (local list: Tweak has no such property yet).
    /// They are never bulk-selected and always get a danger confirmation, even with ConfirmBeforeApply off.
    /// </summary>
    private static readonly Dictionary<string, string> DataLossNotes = new(StringComparer.Ordinal)
    {
        ["recall-snapshots-off"] = Loc.T("Уже сохранённые снимки Recall будут удалены — откат их не вернёт, он только снова разрешит сохранение."),
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
        "services" or "services-tasks" => Loc.T("Службы"),
        "startup" or "startup-tasks" => Loc.T("Автозапуск"),
        "gameboost" => Loc.T("Игровой сеанс"),
        "games" => Loc.T("Игры"),
        "drivers" => Loc.T("Драйверы"),
        "devices" => Loc.T("Устройства и сеть"),
        _ => source,
    };

    /// <summary>States in which "apply" would actually change something.</summary>
    public static bool WillChange(TweakState state) => state is TweakState.NotApplied or TweakState.Partial or TweakState.Unknown;

    // ───────────────────────────── Exclusive groups & dependencies (selection rules) ─────────────────────────────

    /// <summary>Why a not-applicable row cannot be applied: the tweak's own reason, or the generic one.</summary>
    public static string NotApplicableText(Tweak tweak) => string.IsNullOrWhiteSpace(tweak.NotApplicableReason)
        ? Loc.T("Не подходит для этого компьютера или этой версии Windows: нужного компонента нет в системе.")
        : tweak.NotApplicableReason!;

    /// <summary>The note of a dependent row whose dependency is checked and pending in the same apply.</summary>
    public static string DependencyNote(string requiredTitle) => Loc.Tf("Применится после «{0}».", requiredTitle);

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
        return titles.Count == 0 ? null : Loc.T("заменит: ") + string.Join(", ", titles);
    }

    public static string StateText(TweakState? state) => state switch
    {
        null => Loc.T("ПРОВЕРКА…"),
        TweakState.Applied => Loc.T("ПРИМЕНЕНО"),
        TweakState.NotApplied => Loc.T("НЕ ПРИМЕНЕНО"),
        TweakState.Partial => Loc.T("ЧАСТИЧНО"),
        TweakState.NotApplicable => Loc.T("НЕПРИМЕНИМО"),
        _ => Loc.T("НЕИЗВЕСТНО"),
    };

    public static string CategoryTitle(TweakCategory category) => category switch
    {
        TweakCategory.Gaming => Loc.T("ИГРЫ"),
        TweakCategory.Performance => Loc.T("ПРОИЗВОДИТЕЛЬНОСТЬ"),
        TweakCategory.Power => Loc.T("ПИТАНИЕ"),
        TweakCategory.Network => Loc.T("СЕТЬ"),
        TweakCategory.Privacy => Loc.T("КОНФИДЕНЦИАЛЬНОСТЬ"),
        TweakCategory.Interface => Loc.T("ИНТЕРФЕЙС"),
        TweakCategory.Input => Loc.T("ВВОД"),
        _ => Loc.T("СИСТЕМА"),
    };

    public static string CategoryDescription(TweakCategory category) => category switch
    {
        TweakCategory.Gaming => Loc.T("Как Windows обращается с запущенными играми: приоритет, захват, вывод кадров."),
        TweakCategory.Performance => Loc.T("Фоновая нагрузка и отзывчивость системы."),
        TweakCategory.Power => Loc.T("Схемы и параметры электропитания."),
        TweakCategory.Network => Loc.T("Сетевые параметры и фоновая передача данных."),
        TweakCategory.Privacy => Loc.T("Реклама, рекомендации и сбор диагностических данных."),
        TweakCategory.Interface => Loc.T("Проводник, меню «Пуск», панель задач и визуальные эффекты."),
        TweakCategory.Input => Loc.T("Мышь, клавиатура и другие устройства ввода."),
        _ => Loc.T("Обслуживание, хранилище и служебные параметры Windows."),
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
    public static string Plural(int n, string one, string few, string many) => Loc.Plural(n, one, few, many);

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
            "svc" => Loc.Tf("Служба {0}", rest),
            "task" => Loc.Tf("Задача {0}", rest),
            "power" => Loc.T("Активная схема питания"),
            "cmd" => Loc.Tf("Параметр {0}", rest),
            _ => target,
        };
    }

    public static string RegistryTarget(RegHive hive, string path, string name) =>
        $"{hive}\\{path}\\{(name.Length == 0 ? Loc.T("(По умолчанию)") : name)}";

    /// <summary>Formats a value MONOLITH is going to write.</summary>
    public static string FormatNewValue(RegistryValueKind kind, object? value)
    {
        if (value is null) return Loc.T("удалить значение");
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
        null => Loc.T("нет значения"),
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
        b.Length == 0 ? Loc.T("пусто") : BitConverter.ToString(b.Length > 24 ? b[..24] : b).Replace('-', ' ') + (b.Length > 24 ? " …" : "");

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
                Loc.Tf("Служба {0} · тип запуска", x.Service),
                ServiceControl.DescribeStart(x.Mode, false),
                () => ServiceControl.Query(x.Service)?.Describe() ?? Loc.T("служба не установлена")))
            .ToList(),
        ScheduledTaskTweak t => t.Tasks
            .Select(path => new PlannedChange(
                Loc.Tf("Задача {0}", path),
                t.Enable ? Loc.T("включена") : Loc.T("отключена"),
                () => ScheduledTasks.Query(path) is { Exists: true } info ? (info.Enabled ? Loc.T("включена") : Loc.T("отключена")) : Loc.T("нет в системе")))
            .ToList(),
        _ => [],
    };
}
