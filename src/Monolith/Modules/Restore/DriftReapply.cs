using Microsoft.Win32;
using Monolith.Core;
using Monolith.Modules.Startup;
using Monolith.Tweaks;

namespace Monolith.Modules.Restore;

public sealed record ReapplyResult(int Applied, IReadOnlyList<string> Errors);

/// <summary>
/// «ПРИМЕНИТЬ СНОВА» for drifted records. Every write goes through <see cref="ChangeSession"/> and becomes a new layer
/// of the target in its own batch: undoing that batch goes back to the value Windows (or another program) had set,
/// undoing everything still ends at the value from before Lycoris. Tweaks are re-applied through
/// <see cref="TweakEngine"/>, exactly as the Optimize page does. Call off the UI thread, after the user confirmed.
/// </summary>
public static class DriftReapply
{
    public const string BatchTitle = "Повторное применение: изменено вне Lycoris"; // loc:ignore stored as the journal batch title (Russian on disk); shown through Loc.Sink

    public static bool CanReapply(DriftItem d) => d.Fix != DriftFix.None;

    /// <summary>Confirmation lines: exactly what will be written.</summary>
    public static List<string> DescribeForConfirm(IReadOnlyList<DriftItem> items, Func<string, string?> tweakTitle)
    {
        var lines = new List<string>();
        foreach (var g in items.Where(CanReapply).GroupBy(d => d.Fix == DriftFix.Tweak ? d.TweakId : null))
        {
            if (g.Key is { } id)
                lines.Add(Loc.Tf("Пункт оптимизации «{0}» применяется заново целиком (те же значения, что при первом применении)", tweakTitle(id) ?? id));
            foreach (var d in g)
                lines.Add($"{Loc.Sink(d.Record.Title)} · {RestoreFormat.DescribeTarget(d.Record.Target)}: «{Loc.Sink(d.Live)}» → «{Loc.Sink(d.Applied)}»");
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
            // Under the journal gate: a packaged task's live state is checked right before it is written.
            foreach (var d in direct) session.Exclusive(() => ApplyDirect(session, d));
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
                if (JournalTrust.IsPackagedStartupMark(loc))
                {
                    // A packaged app's startup task: DWORD State 2 or 1, as Task Manager writes it — never over a state set
                    // by policy (3/4), and never into a task that is gone (the key would be created).
                    if (PackagedRefusal(loc) is { } refusal) session.Errors.Add($"{r.Title}: {refusal}.");
                    else session.SetRegistry(loc.Hive, loc.Path, loc.Name, RegistryValueKind.DWord, PackagedStartup.TargetState(on), source, r.Title);
                }
                else if (DriftDetector.PackagedStateOf(r.NewValue) is null)
                    session.SetRegistry(loc.Hive, loc.Path, loc.Name, RegistryValueKind.Binary, StartupApproved.Build(on), source, r.Title);
                else
                    session.Errors.Add(Loc.Tf("{0}: не удалось определить вид отметки автозапуска — значение не записано.", Loc.Sink(r.Title)));
                break;
        }
    }

    /// <summary>Why a packaged task's State must not be written now (live value re-read), or null.</summary>
    private static string? PackagedRefusal(RegistryValueSnapshot loc) =>
        PackagedStartup.WriteRefusal(RegistryHelper.Read(loc.Hive, loc.Path, loc.Name) is int state ? state : null);
}
