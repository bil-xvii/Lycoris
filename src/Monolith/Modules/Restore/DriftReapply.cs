using Microsoft.Win32;
using Monolith.Core;
using Monolith.Modules.Startup;
using Monolith.Tweaks;

namespace Monolith.Modules.Restore;

public sealed record ReapplyResult(int Applied, IReadOnlyList<string> Errors);

/// <summary>
/// «ПРИМЕНИТЬ СНОВА» for drifted records. Every write goes through <see cref="ChangeSession"/>: the journal already
/// holds the true original, so a re-apply only updates the record's new value (ChangeJournal.MergeInto) and the
/// record stays revertable to the pre-MONOLITH value. Tweaks are re-applied through <see cref="TweakEngine"/>,
/// exactly as the Optimize page does. Call off the UI thread, after the user confirmed.
/// </summary>
public static class DriftReapply
{
    public const string BatchTitle = "Повторное применение: изменено вне MONOLITH";

    public static bool CanReapply(DriftItem d) => d.Fix != DriftFix.None;

    /// <summary>Confirmation lines: exactly what will be written.</summary>
    public static List<string> DescribeForConfirm(IReadOnlyList<DriftItem> items, Func<string, string?> tweakTitle)
    {
        var lines = new List<string>();
        foreach (var g in items.Where(CanReapply).GroupBy(d => d.Fix == DriftFix.Tweak ? d.TweakId : null))
        {
            if (g.Key is { } id)
                lines.Add($"Пункт оптимизации «{tweakTitle(id) ?? id}» применяется заново целиком (те же значения, что при первом применении)");
            foreach (var d in g)
                lines.Add($"{d.Record.Title} · {RestoreFormat.DescribeTarget(d.Record.Target)}: «{d.Live}» → «{d.Applied}»");
        }
        return lines;
    }

    public static async Task<ReapplyResult> ApplyAsync(IReadOnlyList<DriftItem> items)
    {
        var errors = new List<string>();
        var applied = 0;

        var direct = items.Where(d => d.Fix is DriftFix.Service or DriftFix.Task or DriftFix.StartupEntry).ToList();
        if (direct.Count > 0)
        {
            var session = await ChangeSession.BeginAsync(BatchTitle).ConfigureAwait(false);
            foreach (var d in direct) ApplyDirect(session, d);
            session.Complete();
            applied += session.Applied;
            errors.AddRange(session.Errors);
        }

        var tweaks = items.Where(d => d.Fix == DriftFix.Tweak && d.TweakId is not null)
                          .Select(d => d.TweakId!).Distinct(StringComparer.Ordinal)
                          .Select(TweakCatalog.Find).OfType<Tweak>().ToList();
        if (tweaks.Count > 0)
        {
            var r = await TweakEngine.ApplyAsync(tweaks, BatchTitle).ConfigureAwait(false);
            applied += r.Applied;
            errors.AddRange(r.Errors);
        }
        return new ReapplyResult(applied, errors);
    }

    private static void ApplyDirect(ChangeSession session, DriftItem d)
    {
        var r = d.Record;
        var source = DriftDetector.LatestSource(r);
        switch (d.Fix)
        {
            case DriftFix.Service when DriftDetector.TryParseServiceStart(r.NewValue, out var mode, out var delayed):
                var name = r.Service?.Name ?? (r.Target.StartsWith("svc:", StringComparison.OrdinalIgnoreCase) ? r.Target[4..] : "");
                if (name.Length > 0) session.SetServiceStart(name, mode, source, r.Title, delayed);
                break;
            case DriftFix.Task when DriftDetector.TaskEnabledOf(r.NewValue) is { } enabled:
                var path = r.TaskPath ?? (r.Target.StartsWith("task:", StringComparison.OrdinalIgnoreCase) ? r.Target[5..] : "");
                if (path.Length > 0) session.SetTaskEnabled(path, enabled, source, r.Title);
                break;
            case DriftFix.StartupEntry when r.Registry is { } loc && DriftDetector.StartupEnabledOf(r.NewValue) is { } on:
                session.SetRegistry(loc.Hive, loc.Path, loc.Name, RegistryValueKind.Binary, StartupApproved.Build(on), source, r.Title);
                break;
        }
    }
}
