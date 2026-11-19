using Lycoris.Core;

namespace Lycoris.Tweaks;

public sealed record TweakRunResult(int Applied, int Skipped, IReadOnlyList<string> Errors, string? RestorePoint, RestartRequirement Restart)
{
    /// <summary>
    /// Ready-to-show reasons for skipped tweaks whose prerequisite tweak or power plan could not be confirmed.
    /// Not counted in <see cref="Skipped"/>.
    /// </summary>
    public IReadOnlyList<string> SkippedReasons { get; init; } = [];
    /// <summary>Ids of the tweaks in <see cref="SkippedReasons"/>.</summary>
    public IReadOnlySet<string> SkippedIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>Applies and reverts tweaks. All work runs off the UI thread.</summary>
public static class TweakEngine
{
    /// <summary>Detects the state of every tweak in parallel-safe sequence (registry/service reads are cheap).</summary>
    public static async Task<Dictionary<string, TweakState>> DetectAllAsync(IEnumerable<Tweak> tweaks) =>
        await DetectAllAsync(tweaks, await SystemProfile.LoadAsync().ConfigureAwait(false)).ConfigureAwait(false);

    internal static Task<Dictionary<string, TweakState>> DetectAllAsync(IEnumerable<Tweak> tweaks, SystemProfile sp) => Task.Run(() =>
    {
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

    public static Task<TweakRunResult> ApplyAsync(IReadOnlyList<Tweak> tweaks, string title, IProgress<string>? progress = null) =>
        ApplyWithPowerPlanAsync(tweaks, title, CaptureBatchPowerPlan(tweaks, PowerSchemes.GetActive), progress);

    internal static async Task<TweakRunResult> ApplyWithPowerPlanAsync(IReadOnlyList<Tweak> tweaks, string title,
        PowerPlanIntent? powerPlan, IProgress<string>? progress = null, Action? prepareJournal = null)
    {
        var sp = await SystemProfile.LoadAsync().ConfigureAwait(false);
        return await Task.Run(() => ApplyCapturedAsync(tweaks, sp, powerPlan, PowerSchemes.GetActive, () =>
        {
            prepareJournal?.Invoke();
            return ChangeSession.BeginAsync(title, createRestorePoint: true, progress);
        }, progress)).ConfigureAwait(false);
    }

    internal static async Task<TweakRunResult> ApplyCapturedAsync(IReadOnlyList<Tweak> tweaks, SystemProfile sp,
        PowerPlanIntent? powerPlan, Func<Guid> readActiveScheme, Func<Task<ChangeSession>> beginSession,
        IProgress<string>? progress = null)
    {
        var blocked = new List<Tweak>();
        if (tweaks.Any(IsPowerPlanOperation) && !IsCurrentScheme(powerPlan?.OriginalScheme, readActiveScheme))
        {
            blocked.AddRange(tweaks.Where(IsPowerPlanOperation));
            tweaks = tweaks.Where(t => !IsPowerPlanOperation(t)).ToList();
        }
        if (tweaks.Count == 0 && blocked.Count > 0)
        {
            return new TweakRunResult(0, 0, [], Loc.Key("Точка восстановления не создавалась: изменения схемы питания пропущены."),
                RestartRequirement.None)
            {
                SkippedReasons = blocked.Select(PowerPlanDriftReason).ToList(),
                SkippedIds = blocked.Select(t => t.Id).ToHashSet(StringComparer.Ordinal),
            };
        }

        var session = await beginSession().ConfigureAwait(false);
        var (skipped, restart, reasons, ids) = ApplyInSession(session, tweaks, sp, powerPlan, readActiveScheme, progress);
        session.Complete();
        foreach (var tweak in blocked)
        {
            reasons.Add(PowerPlanDriftReason(tweak));
            ids.Add(tweak.Id);
        }
        return new TweakRunResult(session.Applied, skipped, session.Errors, session.Batch.RestorePoint, restart)
        {
            SkippedReasons = reasons,
            SkippedIds = ids,
        };
    }

    internal static PowerPlanIntent? CapturePowerPlanIntent(IReadOnlyList<Tweak> targets, Tweak? selectedPlan,
        Func<Guid> readActiveScheme)
    {
        if (!targets.Any(IsPowerPlanOperation)) return null;
        var switches = targets.Where(t => t.ExclusiveGroup == TweakGroups.PowerPlan).ToList();
        var switchPending = switches.Count == 1 && ReferenceEquals(switches[0], selectedPlan);
        var active = switches.Count == 0 || switchPending ? ReadActiveScheme(readActiveScheme) : Guid.Empty;
        if (active != Guid.Empty && !switchPending && selectedPlan is not null
            && (DetectSafe(selectedPlan) != TweakState.Applied || !IsCurrentScheme(active, readActiveScheme)))
            active = Guid.Empty;
        return new PowerPlanIntent(active, selectedPlan, switchPending);
    }

    private static PowerPlanIntent? CaptureBatchPowerPlan(IReadOnlyList<Tweak> tweaks, Func<Guid> readActiveScheme) =>
        CapturePowerPlanIntent(tweaks, tweaks.FirstOrDefault(t => t.ExclusiveGroup == TweakGroups.PowerPlan), readActiveScheme);

    private static bool IsPowerPlanOperation(Tweak tweak) =>
        tweak.UsesActivePowerPlan || tweak.ExclusiveGroup == TweakGroups.PowerPlan;

    private static Guid ReadActiveScheme(Func<Guid> readActiveScheme)
    {
        try { return readActiveScheme(); }
        catch { return Guid.Empty; }
    }

    private static bool IsCurrentScheme(Guid? expected, Func<Guid> readActiveScheme) =>
        expected is { } scheme && scheme != Guid.Empty && ReadActiveScheme(readActiveScheme) == scheme;

    private static string PowerPlanDriftReason(Tweak tweak) =>
        Loc.Tf("{0}: активная схема питания изменилась или её не удалось подтвердить. Обновите список и повторите.", tweak.Title);

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
        ChangeSession session, IReadOnlyList<Tweak> tweaks, SystemProfile sp, IProgress<string>? progress = null) =>
        ApplyInSession(session, tweaks, sp, PowerSchemes.GetActive, progress);

    internal static (int Skipped, RestartRequirement Restart, List<string> SkippedReasons, HashSet<string> SkippedIds) ApplyInSession(
        ChangeSession session, IReadOnlyList<Tweak> tweaks, SystemProfile sp, Func<Guid> readActiveScheme, IProgress<string>? progress = null)
    {
        return ApplyInSession(session, tweaks, sp, CaptureBatchPowerPlan(tweaks, readActiveScheme), readActiveScheme, progress);
    }

    internal static (int Skipped, RestartRequirement Restart, List<string> SkippedReasons, HashSet<string> SkippedIds) ApplyInSession(
        ChangeSession session, IReadOnlyList<Tweak> tweaks, SystemProfile sp, PowerPlanIntent? powerPlan,
        Func<Guid> readActiveScheme, IProgress<string>? progress = null)
    {
        tweaks = OrderForApply(tweaks);
        var pendingPlan = powerPlan?.SwitchPending == true ? powerPlan.SelectedPlan : null;
        var confirmedScheme = pendingPlan is null ? powerPlan?.OriginalScheme : null;
        var planDrifted = false;
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
            progress?.Report(t.Title);
            var before = session.Applied;
            try
            {
                // The journal gate covers the whole tweak: it reads the live state (DNS servers, the active plan…)
                // before it calls the session, and no revert may change that state in between.
                session.Exclusive(() =>
                {
                    if (t.Risk == TweakRisk.Advanced && !AppSettings.Current.ExpertMode)
                    {
                        skipped++;
                        Log.Warn("{0} — пропущено: требуется режим «Эксперт».", t.Title);
                        return;
                    }
                    var switchesPlan = ReferenceEquals(t, pendingPlan);
                    Guid? requiredScheme = null;
                    if (IsPowerPlanOperation(t))
                    {
                        if (!planDrifted && t.UsesActivePowerPlan && pendingPlan is not null && confirmedScheme is null)
                        {
                            Skip(t, Loc.Tf("{0}: не подтверждено включение выбранной схемы «{1}»", t.Title, pendingPlan.Title));
                            return;
                        }

                        requiredScheme = switchesPlan ? powerPlan!.OriginalScheme : confirmedScheme;
                        if (planDrifted || (!switchesPlan && t.ExclusiveGroup == TweakGroups.PowerPlan)
                            || !IsCurrentScheme(requiredScheme, readActiveScheme))
                        {
                            planDrifted = true;
                            Skip(t, PowerPlanDriftReason(t));
                            return;
                        }
                    }
                    if (t.Requires.Count > 0 && DetectSafe(t) == TweakState.NotApplicable)
                    {
                        Skip(t, DependencySkipReason(t, tweaks, AnyDependencyApplied(t, tweaks)));
                        return;
                    }
                    var errorsBefore = session.Errors.Count;
                    var context = new TweakContext
                    {
                        Session = session, System = sp, Tweak = t,
                        ConfirmedPowerScheme = t.UsesActivePowerPlan ? requiredScheme : null,
                        IsPowerSchemeCurrent = requiredScheme is { } schemeBeforeWrite
                            ? () => IsCurrentScheme(schemeBeforeWrite, readActiveScheme) : null,
                    };
                    t.Apply(context);
                    if (context.PowerSchemeDrifted)
                    {
                        planDrifted = true;
                        Skip(t, PowerPlanDriftReason(t));
                        return;
                    }
                    if (switchesPlan && session.Errors.Count == errorsBefore)
                    {
                        // A no-op may keep the original OEM plan. Only a recorded request can confirm another GUID.
                        var expected = context.RequestedPowerScheme ?? requiredScheme;
                        var scheme = ReadActiveScheme(readActiveScheme);
                        if (scheme != Guid.Empty && scheme == expected && DetectSafe(t) == TweakState.Applied
                            && IsCurrentScheme(scheme, readActiveScheme))
                            confirmedScheme = scheme;
                    }
                });
            }
            catch (ActivePowerSchemeChangedException)
            {
                planDrifted = true;
                Skip(t, PowerPlanDriftReason(t));
            }
            catch (Exception ex)
            {
                session.Errors.Add(Loc.Tf("{0}: {1}", t.Title, ex.Message));
                Log.Error("{0} — ошибка", ex, t.Title);
            }
            if (session.Applied > before && t.Restart > restart) restart = t.Restart;
        }
        return (skipped, restart, skippedReasons, skippedIds);

        void Skip(Tweak tweak, string reason)
        {
            skippedReasons.Add(reason);
            skippedIds.Add(tweak.Id);
            Log.Warn("{0} — пропущено.", reason);
        }
    }

    /// <summary>
    /// Whether <paramref name="t"/> applies to this PC: null = cannot tell, because the tweak has an
    /// applicability rule and hardware facts could not be read, or the strict computer identity is unknown/conflicting.
    /// The profile defaults (e.g. IsLaptop = false) are never used to decide.
    /// </summary>
    public static bool? Applicability(Tweak t, SystemProfile sp)
    {
        if (t.AppliesTo is null) return true;
        if (!sp.HardwareFactsKnown || !sp.Identity.IsKnown) return null;
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
