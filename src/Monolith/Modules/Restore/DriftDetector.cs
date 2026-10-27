using System.Globalization;
using System.ServiceProcess;
using Monolith.Core;

namespace Monolith.Modules.Restore;

/// <summary>How a drifted record can be put back to the value MONOLITH set.</summary>
public enum DriftFix
{
    /// <summary>No direct re-apply: only "show" on the owning page.</summary>
    None,
    /// <summary>Re-apply the whole tweak ("tweak:&lt;id&gt;") through TweakEngine.</summary>
    Tweak,
    /// <summary>Set the service start type again (record NewValue parsed back to a start mode).</summary>
    Service,
    /// <summary>Enable/disable the scheduled task again.</summary>
    Task,
    /// <summary>Write the StartupApproved mark again (enabled/disabled), as the Startup page does.</summary>
    StartupEntry,
}

/// <summary>An active journal record whose live value no longer equals what MONOLITH set.</summary>
public sealed record DriftItem(ChangeRecord Record, string Applied, string Live, DriftFix Fix, string? TweakId, string? OwnerPage)
{
    public string IgnoreKey => DriftDetector.IgnoreKey(Record, Live);
}

/// <summary>
/// Drift detection (competitors.md §3 must-have #5): Windows feature updates and other programs silently reset
/// settings MONOLITH applied. Pure logic with an injected live reader; <see cref="DetectLive"/> uses
/// <see cref="LiveState.Read"/> and must run off the UI thread. Never changes anything.
/// </summary>
public static class DriftDetector
{
    public const string TweakPrefix = "tweak:";
    public const string GameBoostSource = "gameboost";
    public const string StartupSource = "startup";
    public const string StartupTaskSource = "startup-tasks";
    public const string ServicesSource = "services";
    public const string ServicesTaskSource = "services-tasks";

    public const string GamesSource = "games";

    /// <summary>Blocking live read of the current journal (registry, SCM, task scheduler): this account's and machine-wide records.</summary>
    public static IReadOnlyList<DriftItem> DetectLive(IReadOnlyCollection<string>? ignored = null) =>
        Detect(ChangeJournal.Instance.ActiveRecords.Where(ChangeJournal.IsMine), LiveState.Read,
            id => Tweaks.TweakCatalog.Find(id) is not null, ignored);

    /// <summary>
    /// Records that drifted: active, the newest layer of their target (an older layer's value was replaced by a later
    /// MONOLITH change on purpose), comparable (not a command, not a temporary Game Boost change), the target still
    /// exists and its live value differs from <see cref="ChangeRecord.NewValue"/>. <paramref name="ignored"/> holds
    /// <see cref="IgnoreKey"/> values the user accepted («ЗАБЫТЬ»); a later different value is reported again.
    /// </summary>
    public static IReadOnlyList<DriftItem> Detect(IEnumerable<ChangeRecord> records, Func<ChangeRecord, LiveValue> read,
        Func<string, bool> tweakExists, IReadOnlyCollection<string>? ignored = null)
    {
        var skip = ignored is null ? new HashSet<string>() : new HashSet<string>(ignored, StringComparer.Ordinal);
        var result = new List<DriftItem>();
        foreach (var r in ChangeJournal.TopLayers(records, UserScope.CurrentSid))
        {
            if (!IsCandidate(r)) continue;
            var live = read(r);
            var drifted = IsStartupEntry(r) ? StartupDrift(r.NewValue, live.Text) : live.Drifted;
            if (drifted != true) continue;
            if (skip.Contains(IgnoreKey(r, DisplayValue(r, live.Text)))) continue; // same form as DriftItem.IgnoreKey
            var tweakId = TweakIdOf(r);
            var fix = ClassifyFix(r, tweakId is not null && tweakExists(tweakId));
            result.Add(new DriftItem(r, DisplayValue(r, r.NewValue), DisplayValue(r, live.Text), fix,
                fix == DriftFix.Tweak ? tweakId : null, OwnerPage(LatestSource(r))));
        }
        return result;
    }

    public static bool IsCandidate(ChangeRecord r) =>
        !r.Reverted && r.Kind != ChangeKind.Command && !r.TouchedBy(GameBoostSource);

    /// <summary>The source of the last change of the target (its NewValue): the newest AlsoSource of an older merged record, else Source.</summary>
    public static string LatestSource(ChangeRecord r) => r.AlsoSources.Count > 0 ? r.AlsoSources[^1] : r.Source;

    public static string? TweakIdOf(ChangeRecord r)
    {
        var s = LatestSource(r);
        return s.StartsWith(TweakPrefix, StringComparison.Ordinal) && s.Length > TweakPrefix.Length ? s[TweakPrefix.Length..] : null;
    }

    public static bool IsStartupEntry(ChangeRecord r) => r.Kind == ChangeKind.Registry && LatestSource(r) == StartupSource;

    public static DriftFix ClassifyFix(ChangeRecord r, bool tweakFound)
    {
        if (TweakIdOf(r) is not null) return tweakFound ? DriftFix.Tweak : DriftFix.None;
        var source = LatestSource(r);
        return r.Kind switch
        {
            ChangeKind.Service when source == ServicesSource && TryParseServiceStart(r.NewValue, out _, out _) => DriftFix.Service,
            ChangeKind.ScheduledTask when source is ServicesTaskSource or StartupTaskSource && TaskEnabledOf(r.NewValue) is not null => DriftFix.Task,
            ChangeKind.Registry when source == StartupSource && r.Registry is not null && StartupEnabledOf(r.NewValue) is not null => DriftFix.StartupEntry,
            _ => DriftFix.None,
        };
    }

    /// <summary>The page that owns a source, for «ПОКАЗАТЬ».</summary>
    public static string? OwnerPage(string source) => source switch
    {
        _ when source.StartsWith(TweakPrefix, StringComparison.Ordinal) => "optimize",
        ServicesSource or ServicesTaskSource => "services",
        StartupSource or StartupTaskSource => "startup",
        "drivers" => "drivers",
        GamesSource => "games",
        _ => null,
    };

    /// <summary>Key of an accepted drift: the record and the live value the user accepted.</summary>
    public static string IgnoreKey(ChangeRecord r, string live) => $"{r.Id:N}|{live.Trim()}";

    /// <summary>Accepted keys whose record is still active (stale ones are dropped so the list does not grow).</summary>
    public static List<string> PruneIgnored(IEnumerable<string> ignored, IEnumerable<ChangeRecord> active)
    {
        var ids = active.Where(r => !r.Reverted).Select(r => r.Id.ToString("N")).ToHashSet(StringComparer.Ordinal);
        return ignored.Where(k => k.IndexOf('|') is var i and > 0 && ids.Contains(k[..i])).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Inverse of <see cref="ServiceControl.DescribeStart"/> for the modes MONOLITH sets.</summary>
    public static bool TryParseServiceStart(string? text, out ServiceStartMode mode, out bool delayed)
    {
        foreach (var (m, d) in new[] { (ServiceStartMode.Automatic, true), (ServiceStartMode.Automatic, false), (ServiceStartMode.Manual, false), (ServiceStartMode.Disabled, false) })
        {
            if (string.Equals(text?.Trim(), ServiceControl.DescribeStart(m, d), StringComparison.OrdinalIgnoreCase))
            {
                (mode, delayed) = (m, d);
                return true;
            }
        }
        (mode, delayed) = (ServiceStartMode.Manual, false);
        return false;
    }

    public static bool? TaskEnabledOf(string? text) => text?.Trim() switch
    {
        "Включена" => true,
        "Отключена" => false,
        _ => null,
    };

    /// <summary>
    /// StartupApproved state from a value description: "удалено"/"отсутствовало" = enabled (no mark), otherwise
    /// "Binary: XX …" where an odd first byte means disabled (same rule as Task Manager). Null when unreadable.
    /// </summary>
    public static bool? StartupEnabledOf(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || t == LiveState.Unknown) return null;
        if (t is "удалено" or "отсутствовало") return true;
        const string prefix = "Binary:";
        if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = t[prefix.Length..].Trim();
        if (hex.Length == 0) return true;
        var first = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return byte.TryParse(first, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b) ? (b & 1) == 0 : null;
    }

    /// <summary>Startup marks carry a timestamp, so only the enabled/disabled state is compared.</summary>
    public static bool? StartupDrift(string? applied, string? live) =>
        StartupEnabledOf(applied) is { } a && StartupEnabledOf(live) is { } l ? a != l : null;

    /// <summary>Readable value for the row: startup marks as «включён/отключён», everything else as is.</summary>
    public static string DisplayValue(ChangeRecord r, string? value)
    {
        if (IsStartupEntry(r) && StartupEnabledOf(value) is { } enabled) return enabled ? "автозапуск включён" : "автозапуск отключён";
        return string.IsNullOrWhiteSpace(value) ? LiveState.Unknown : value.Trim();
    }

    public static string RowText(DriftItem d) => $"MONOLITH установил: {d.Applied} · сейчас: {d.Live}";

    /// <summary>Startup notice: names the Windows build change when there was one.</summary>
    public static string NoticeText(int count, string? previousBuild, string? currentBuild)
    {
        var what = $"{count} {RestoreFormat.Plural(count, "настройку", "настройки", "настроек")} MONOLITH";
        var tail = " Раздел «Восстановление» → «ИЗМЕНЕНО ВНЕ MONOLITH»: можно применить снова или оставить как есть.";
        return BuildChanged(previousBuild, currentBuild)
            ? $"После обновления Windows (сборка {previousBuild} → {currentBuild}) система изменила {what}.{tail}"
            : $"Windows или другая программа изменила {what}.{tail}";
    }

    public static bool BuildChanged(string? previous, string? current) =>
        !string.IsNullOrWhiteSpace(previous) && !string.IsNullOrWhiteSpace(current) &&
        !string.Equals(previous.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string BuildText(int build, int ubr) =>
        build <= 0 ? "" : ubr > 0 ? $"{build}.{ubr}" : build.ToString(CultureInfo.InvariantCulture);
}
