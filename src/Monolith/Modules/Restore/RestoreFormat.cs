using System.Globalization;
using Monolith.Core;

namespace Monolith.Modules.Restore;

public enum JournalFilter { All, Active, Reverted, Drifted }

public enum RecordState { Active, Reverted, Failed }

/// <summary>A batch with the records that pass the current filter. Counts always cover the whole batch.</summary>
public sealed record JournalGroup(ChangeBatch Batch, IReadOnlyList<ChangeRecord> Records, int ActiveCount, int TotalCount)
{
    public bool IsSynthetic => Batch.Id == Guid.Empty;
    /// <summary>Active records «ОТКАТИТЬ ПАКЕТ» reverts (not another account's, not an older per-user record without an account).</summary>
    public int RevertibleCount { get; init; }
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
        ChangeKind.UserParameter => "ПАРАМЕТР",
        ChangeKind.DisplayMode => "ЭКРАН",
        ChangeKind.DisplayHdr => "HDR",
        ChangeKind.MonitorSetting => "МОНИТОР",
        ChangeKind.AudioLevel => "ЗВУК",
        ChangeKind.NicProperty => "СЕТЬ",
        _ => "ИЗМЕНЕНИЕ",
    };

    /// <summary>"reg:HKCU\…\Name" → "HKCU\…\Name", "svc:DiagTrack" → "DiagTrack", "power:active" → "Активная схема питания".</summary>
    public static string DescribeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "—";
        if (Monolith.Core.Devices.DeviceTargets.DescribeTarget(target) is { } device) return device;
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
    /// "Drifted" keeps the active records whose id is in <paramref name="drifted"/>.
    /// </summary>
    public static IReadOnlyList<JournalGroup> Group(IEnumerable<ChangeBatch> batches, IEnumerable<ChangeRecord> records, JournalFilter filter,
        IReadOnlySet<Guid>? drifted = null, Func<ChangeRecord, bool>? bulkRevertible = null)
    {
        bulkRevertible ??= r => !r.Reverted;
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
                JournalFilter.Drifted => all.Where(r => !r.Reverted && drifted?.Contains(r.Id) == true).ToList(),
                _ => all,
            };
            if (filter != JournalFilter.All && visible.Count == 0) continue;
            result.Add(new JournalGroup(b, visible, all.Count(r => !r.Reverted), all.Count)
            {
                RevertibleCount = all.Count(r => !r.Reverted && bulkRevertible(r)),
            });
        }
        return result;
    }

    /// <summary>
    /// For every active record that is an older layer (a newer active change of the same target and account exists):
    /// the record that sets the live value now. An older build's per-user record maps to the current account's own
    /// change of the same value (<see cref="ChangeJournal.ShadowOf"/>). Records that set their target themselves are not in the map.
    /// </summary>
    public static IReadOnlyDictionary<Guid, ChangeRecord> CoveredBy(IReadOnlyList<ChangeRecord> records, string? currentSid)
    {
        var active = records.Where(r => !r.Reverted).ToList();
        var tops = ChangeJournal.TopLayers(active, currentSid);
        var topSet = new HashSet<ChangeRecord>(tops, ChangeJournal.ByReference);
        var byKey = new Dictionary<string, ChangeRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tops.Where(t => t.Target.Length > 0)) byKey[ChangeJournal.ScopeOf(t) + "|" + t.Target] = t;
        var result = new Dictionary<Guid, ChangeRecord>();
        foreach (var r in active)
        {
            if (topSet.Contains(r) || r.Target.Length == 0) continue;
            if (ChangeJournal.ShadowOf(active, r, currentSid) is { } shadow) result[r.Id] = shadow;
            else if (byKey.TryGetValue(ChangeJournal.ScopeOf(r) + "|" + r.Target, out var top)) result[r.Id] = top;
        }
        return result;
    }

    /// <summary>
    /// What a revert put back: «Возвращено значений: N.», plus the records handed over to a later change, which left the
    /// system as it was (<see cref="RevertResult.HandedOver"/>).
    /// </summary>
    public static string RevertCounts(RevertResult r, string returned = "Возвращено значений") =>
        $"{returned}: {r.Restored}." + (r.HandedOver > 0 ? $" Оставлено действующим более позднее изменение: {r.HandedOver}." : "");

    /// <summary>
    /// Why a revert left records active on purpose (<see cref="RevertResult.Left"/>, or the records a bulk revert skips),
    /// one sentence per reason; empty when nothing was left. On the UI thread pass a non-blocking
    /// <paramref name="accountName"/> (<see cref="UserScope.CachedAccountName"/>).
    /// </summary>
    public static string LeftText(IEnumerable<ChangeRecord> left, string? currentSid, Func<string, string> accountName)
    {
        var list = left.Where(r => !r.Reverted).ToList();
        var parts = new List<string>();
        var others = list.Where(r => ChangeJournal.IsOtherUser(r, currentSid)).ToList();
        var legacy = list.Count(r => !ChangeJournal.IsOtherUser(r, currentSid) && r.IsLegacyPerUser);
        if (legacy > 0)
            parts.Add($"{legacy} {Plural(legacy, "изменение сделано", "изменения сделаны", "изменений сделано")} старой версией MONOLITH " +
                      $"для неизвестной учётной записи — откатите {(legacy == 1 ? "его" : "их")} по одной в журнале, войдя под той учётной записью.");
        if (others.Count > 0)
        {
            var names = others.Select(r => accountName(r.UserSid!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            parts.Add($"{others.Count} {Plural(others.Count, "изменение сделано", "изменения сделаны", "изменений сделано")} " +
                      $"для {(names.Count == 1 ? "учётной записи" : "учётных записей")} {string.Join(", ", names)} — " +
                      $"{(others.Count == 1 ? "его" : "их")} можно откатить, только войдя под {(names.Count == 1 ? "ней" : "ними")}.");
        }
        return string.Join(" ", parts);
    }

    /// <summary>A journal source as the user knows it: the tweak's title, or the page that made the change.</summary>
    public static string SourceTitle(string source)
    {
        const string prefix = "tweak:";
        if (source.StartsWith(prefix, StringComparison.Ordinal))
            return Tweaks.TweakCatalog.Find(source[prefix.Length..])?.Title is { } t ? $"«{t}»" : $"«{source[prefix.Length..]}»";
        return $"изменение раздела «{Optimize.OptimizeLogic.SourceLabel(source)}»";
    }

    /// <summary>
    /// The explanation under a record: whose change it is, whether a newer change now sets the value, how the revert
    /// went and whether it was confirmed by reading the value back. Empty when there is nothing to add.
    /// </summary>
    public static string RecordNote(ChangeRecord r, string? currentSid, ChangeRecord? coveredBy, string? handedToTitle,
        Func<string, string>? accountName = null)
    {
        accountName ??= UserScope.AccountName;
        var parts = new List<string>();
        if (!r.Reverted)
        {
            if (ChangeJournal.IsOtherUser(r, currentSid))
                parts.Add($"Изменение сделано для учётной записи {accountName(r.UserSid!)}; войдите под ней, чтобы отменить.");
            else if (r.IsLegacyPerUser)
                parts.Add("Записано старой версией MONOLITH без учётной записи: откат запишет исходное значение в профиль текущей " +
                          "учётной записи, поэтому откатывайте под той, где применяли. «ОТКАТИТЬ ВСЁ» и откат пакета эту запись пропускают.");
            if (coveredBy is not null)
                parts.Add(r.IsLegacyPerUser && coveredBy.UserSid is not null // the account's own change, not a newer old-build layer
                    ? $"Сейчас значение в профиле этой учётной записи задаёт более позднее изменение «{coveredBy.Title}» — сначала откатите его."
                    : $"Сейчас значение задаёт более позднее изменение «{coveredBy.Title}»: откат этой записи систему не изменит, " +
                      "а исходное значение перейдёт к нему.");
            if (r.IsLegacyMerged)
                parts.Add($"Запись старой версии: откат вернёт значение до MONOLITH и отменит также {string.Join(", ", r.AlsoSources.Distinct().Select(SourceTitle))}.");
            return string.Join(" ", parts);
        }

        if (r.HandedTo is not null)
            parts.Add(handedToTitle is null
                ? "Откат передан более позднему изменению той же настройки: система не менялась, его откат вернёт исходное значение."
                : $"Откат передан: значение задаёт более позднее изменение «{handedToTitle}», его откат вернёт исходное значение.");
        if (!string.IsNullOrWhiteSpace(r.RevertNote)) parts.Add(r.RevertNote!);
        if (r.RevertMismatch is { } mismatch) parts.Add($"Не совпадает: {mismatch}.");
        else if (r.RevertVerifiedUtc is not null) parts.Add("Откат подтверждён: значение совпадает с исходным.");
        return string.Join(" ", parts);
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
}
