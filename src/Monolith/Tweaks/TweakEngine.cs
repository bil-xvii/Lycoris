using Monolith.Core;

namespace Monolith.Tweaks;

public sealed record TweakRunResult(int Applied, int Skipped, IReadOnlyList<string> Errors, string? RestorePoint, RestartRequirement Restart);

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
            var (skipped, restart) = ApplyInSession(session, tweaks, sp, progress);
            session.Complete();
            return new TweakRunResult(session.Applied, skipped, session.Errors, session.Batch.RestorePoint, restart);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies tweaks inside a session the caller opened and completes (a page that applies tweaks together with its own
    /// changes in one batch — «Устройства и сеть» → АВТОНАСТРОЙКА). Same rules as <see cref="ApplyAsync"/>: each tweak with
    /// its own source "tweak:&lt;id&gt;", inapplicable ones skipped. Blocking: call off the UI thread.
    /// </summary>
    public static (int Skipped, RestartRequirement Restart) ApplyInSession(ChangeSession session, IReadOnlyList<Tweak> tweaks, SystemProfile sp,
        IProgress<string>? progress = null)
    {
        int skipped = 0;
        var restart = RestartRequirement.None;
        foreach (var t in tweaks)
        {
            if (Applicability(t, sp) is not true)
            {
                if (!sp.HardwareFactsKnown && t.AppliesTo is not null)
                    Log.Warn($"{t.Title} — пропущено: не удалось определить характеристики компьютера.");
                skipped++;
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
                session.Errors.Add($"{t.Title}: {ex.Message}");
                Log.Error($"{t.Title} — ошибка", ex);
            }
            if (session.Applied > before && t.Restart > restart) restart = t.Restart;
        }
        return (skipped, restart);
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
        return t.IsApplicable(sp);
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
