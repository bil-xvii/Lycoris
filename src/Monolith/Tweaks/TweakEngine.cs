using Monolith.Core;

namespace Monolith.Tweaks;

public sealed record TweakRunResult(int Applied, int Skipped, IReadOnlyList<string> Errors, string? RestorePoint, RestartRequirement Restart)
{
    /// <summary>
    /// Tweaks left out of this run because a dependency (<see cref="Tweak.Requires"/>) did not get applied first, as
    /// ready-to-show lines: «{title}: зависит от «…», которое не применилось». Not counted in <see cref="Skipped"/>.
    /// </summary>
    public IReadOnlyList<string> SkippedReasons { get; init; } = [];
    /// <summary>Ids of the tweaks in <see cref="SkippedReasons"/>.</summary>
    public IReadOnlySet<string> SkippedIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>Applies and reverts tweaks. All work runs off the UI thread.</summary>
public static class TweakEngine
{
    /// <summary>Detects the state of every tweak in parallel-safe sequence (registry/service reads are cheap).</summary>
    public static Task<Dictionary<string, TweakState>> DetectAllAsync(IEnumerable<Tweak> tweaks) => Task.Run(async () =>
    {
        var sp = await SystemProfile.LoadAsync().ConfigureAwait(false);
        var result = new Dictionary<string, TweakState>();
        foreach (var t in tweaks)
        {
            try
            {
                result[t.Id] = Applicability(t, sp) switch
                {
                    true => t.Detect(),
                    false => TweakState.NotApplicable,
                    null => TweakState.Unknown, // hardware facts missing: never judge by the defaults
                };
            }
            catch
            {
                result[t.Id] = TweakState.Unknown;
            }
        }
        return result;
    });

    public static async Task<TweakRunResult> ApplyAsync(IReadOnlyList<Tweak> tweaks, string title, IProgress<string>? progress = null)
    {
        var sp = await SystemProfile.LoadAsync().ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            var session = await ChangeSession.BeginAsync(title, createRestorePoint: true, progress).ConfigureAwait(false);
            var (skipped, restart, skippedReasons, skippedIds) = ApplyInSession(session, tweaks, sp, progress); // ordered inside
            session.Complete();
            return new TweakRunResult(session.Applied, skipped, session.Errors, session.Batch.RestorePoint, restart)
            {
                SkippedReasons = skippedReasons,
                SkippedIds = skippedIds,
            };
        }).ConfigureAwait(false);
    }

    private static TweakState DetectSafe(Tweak t)
    {
        try { return t.Detect(); }
        catch { return TweakState.Unknown; } // Apply decides for itself; an unreadable state is not a reason to skip
    }

    /// <summary>
    /// The order one batch is applied in. Pure. Input order (= catalog order) is kept, except:
    /// (1) every <see cref="TweakGroups.PowerPlan"/> switch comes first — plan-scoped power settings write to the ACTIVE
    /// plan, so the plan must be in place before them; (2) every tweak with <see cref="Tweak.Requires"/> moves right after
    /// the last of its dependencies present in the batch (chains are resolved; a cycle is left in input order).
    /// </summary>
    public static List<Tweak> OrderForApply(IReadOnlyList<Tweak> tweaks)
    {
        var order = tweaks.Where(t => t.ExclusiveGroup == TweakGroups.PowerPlan)
            .Concat(tweaks.Where(t => t.ExclusiveGroup != TweakGroups.PowerPlan))
            .ToList();
        for (var pass = 0; pass < order.Count; pass++)
        {
            var moved = false;
            for (var i = 0; i < order.Count; i++)
            {
                var t = order[i];
                if (t.Requires.Count == 0) continue;
                var last = -1;
                for (var j = 0; j < order.Count; j++)
                {
                    if (j != i && t.Requires.Contains(order[j].Id, StringComparer.Ordinal)) last = Math.Max(last, j);
                }
                if (last <= i) continue;
                order.RemoveAt(i);
                order.Insert(last, t); // the dependency shifted one slot down: this lands right after it
                moved = true;
            }
            if (!moved) break;
        }
        return order;
    }

    /// <summary>
    /// Ids of tweaks whose <see cref="Tweak.Requires"/> chain leads back to themselves (A → B → A), in input order; empty
    /// when the dependency graph is acyclic. Pure. A cycle must never exist in the catalog: the Optimize page follows the
    /// chain to decide whether a dependent row is pending, and <see cref="OrderForApply"/> cannot place its members.
    /// </summary>
    public static IReadOnlyList<string> RequiresCycles(IEnumerable<Tweak> tweaks)
    {
        var byId = new Dictionary<string, Tweak>(StringComparer.Ordinal);
        foreach (var t in tweaks) byId.TryAdd(t.Id, t);
        var inCycle = new List<string>();
        foreach (var start in byId.Values)
        {
            // Depth-first from each tweak: reaching the start again through Requires means it sits on a cycle.
            var stack = new Stack<string>(start.Requires);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id == start.Id) { inCycle.Add(start.Id); break; }
                if (!seen.Add(id) || !byId.TryGetValue(id, out var t)) continue;
                foreach (var r in t.Requires) stack.Push(r);
            }
        }
        return inCycle;
    }

    /// <summary>
    /// The line for a tweak skipped because its dependency did not apply: «{title}: зависит от «{dependency}», которое не
    /// применилось». The dependency named is the first of <see cref="Tweak.Requires"/> found in the batch, else in the catalog.
    /// </summary>
    public static string DependencySkipReason(Tweak t, IReadOnlyList<Tweak> batch, bool dependencyApplied = false)
    {
        var dependency = t.Requires.Select(id => batch.FirstOrDefault(b => b.Id == id)).FirstOrDefault(b => b is not null)?.Title
                         ?? t.Requires.Select(id => TweakCatalog.Find(id)?.Title).FirstOrDefault(x => x is not null)
                         ?? t.Requires.FirstOrDefault() ?? "";
        return dependencyApplied
            ? Loc.Tf("{0}: «{1}» применено, но пункт по-прежнему неприменим на этом ПК", t.Title, dependency)
            : Loc.Tf("{0}: зависит от «{1}», которое не применилось", t.Title, dependency);
    }

    /// <summary>True when any dependency of <paramref name="t"/> (from the batch, else the catalog) detects as applied.</summary>
    private static bool AnyDependencyApplied(Tweak t, IReadOnlyList<Tweak> batch) =>
        t.Requires.Select(id => batch.FirstOrDefault(b => b.Id == id) ?? TweakCatalog.Find(id))
            .Any(d => d is not null && DetectSafe(d) == TweakState.Applied);

    /// <summary>
    /// Applies tweaks inside a session the caller opened and completes (a page that applies tweaks together with its own
    /// changes in one batch — «Устройства и сеть» → АВТОНАСТРОЙКА). Same rules as <see cref="ApplyAsync"/>: each tweak with
    /// its own source "tweak:&lt;id&gt;", inapplicable ones skipped. The batch is applied in <see cref="OrderForApply"/> order
    /// whatever order the caller collected it in (plan switches first, dependencies before dependents). Blocking: call off the UI thread.
    /// </summary>
    public static (int Skipped, RestartRequirement Restart, List<string> SkippedReasons, HashSet<string> SkippedIds) ApplyInSession(
        ChangeSession session, IReadOnlyList<Tweak> tweaks, SystemProfile sp, IProgress<string>? progress = null)
    {
        tweaks = OrderForApply(tweaks);
        int skipped = 0;
        var skippedReasons = new List<string>();
        var skippedIds = new HashSet<string>(StringComparer.Ordinal);
        var restart = RestartRequirement.None;
        foreach (var t in tweaks)
        {
            if (Applicability(t, sp) is not true)
            {
                if (!sp.HardwareFactsKnown && t.AppliesTo is not null)
                    Log.Warn("{0} — пропущено: не удалось определить характеристики компьютера.", t.Title);
                skipped++;
                continue;
            }
            // A dependent tweak was selected on the promise that its dependency gets applied first (e.g. a power mode
            // on the Balanced plan). Its dependency has run by now (OrderForApply): read the live state again and skip
            // instead of forcing it when the dependency failed.
            if (t.Requires.Count > 0 && DetectSafe(t) == TweakState.NotApplicable)
            {
                var reason = DependencySkipReason(t, tweaks, AnyDependencyApplied(t, tweaks));
                skippedReasons.Add(reason);
                skippedIds.Add(t.Id);
                Log.Warn("{0} — пропущено.", reason);
                continue;
            }
            progress?.Report(t.Title);
            var before = session.Applied;
            try
            {
                // The journal gate covers the whole tweak: it reads the live state (DNS servers, the active plan…)
                // before it calls the session, and no revert may change that state in between.
                session.Exclusive(() => t.Apply(new TweakContext { Session = session, System = sp, Tweak = t }));
            }
            catch (Exception ex)
            {
                session.Errors.Add(Loc.Tf("{0}: {1}", t.Title, ex.Message));
                Log.Error("{0} — ошибка", ex, t.Title);
            }
            if (session.Applied > before && t.Restart > restart) restart = t.Restart;
        }
        return (skipped, restart, skippedReasons, skippedIds);
    }

    /// <summary>
    /// Whether <paramref name="t"/> applies to this PC: null = cannot tell, because the tweak has an
    /// applicability rule and the hardware facts (laptop/battery/chassis, CPU, GPUs) could not be read.
    /// The profile defaults (e.g. IsLaptop = false) are never used to decide.
    /// </summary>
    public static bool? Applicability(Tweak t, SystemProfile sp)
    {
        if (t.AppliesTo is null) return true;
        if (!sp.HardwareFactsKnown) return null;
        try
        {
            return t.IsApplicable(sp);
        }
        catch (Exception ex)
        {
            // A rule that throws must not abort the whole batch half-way (restore point made, some tweaks applied): cannot tell = skip.
            Log.Warn("{0} — пропущено: правило применимости не отработало ({1}).", t.Title, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reverts exactly what this tweak changed (its own journal records, for this account), restoring the state from
    /// before it. Where another change was made later to the same setting, that later change keeps its value and takes
    /// over the original (see <see cref="ChangeJournal.RevertAsync"/>). <paramref name="verify"/> reads each target back.
    /// Older per-user records the account's own later change covers are left (<see cref="RevertResult.Left"/>).
    /// </summary>
    public static Task<RevertResult> RevertAsync(Tweak tweak, Func<ChangeRecord, RevertCheck>? verify = null) =>
        ChangeJournal.Instance.RevertSourceAsync("tweak:" + tweak.Id, verify);

    public static Task<RevertResult> RevertAsync(IEnumerable<Tweak> tweaks, Func<ChangeRecord, RevertCheck>? verify = null) =>
        ChangeJournal.Instance.RevertSourcesAsync(tweaks.Select(t => "tweak:" + t.Id).ToHashSet(), verify);

    /// <summary>True if this account has an active (not reverted) change of this tweak — i.e. "Undo" is available.</summary>
    public static bool CanRevert(Tweak tweak) => ChangeJournal.Instance.HasActiveSource("tweak:" + tweak.Id);
}
