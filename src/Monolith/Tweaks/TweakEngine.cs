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
                result[t.Id] = t.IsApplicable(sp) ? t.Detect() : TweakState.NotApplicable;
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
            int skipped = 0;
            var restart = RestartRequirement.None;
            foreach (var t in tweaks)
            {
                if (!t.IsApplicable(sp))
                {
                    skipped++;
                    continue;
                }
                progress?.Report(t.Title);
                var before = session.Applied;
                try
                {
                    t.Apply(new TweakContext { Session = session, System = sp, Tweak = t });
                }
                catch (Exception ex)
                {
                    session.Errors.Add($"{t.Title}: {ex.Message}");
                    Log.Error($"{t.Title} — ошибка", ex);
                }
                if (session.Applied > before && t.Restart > restart) restart = t.Restart;
            }
            session.Complete();
            return new TweakRunResult(session.Applied, skipped, session.Errors, session.Batch.RestorePoint, restart);
        }).ConfigureAwait(false);
    }

    /// <summary>Reverts everything a tweak changed (from the journal), restoring the original values.</summary>
    public static Task<RevertResult> RevertAsync(Tweak tweak) => ChangeJournal.Instance.RevertSourceAsync("tweak:" + tweak.Id);

    public static async Task<RevertResult> RevertAsync(IEnumerable<Tweak> tweaks)
    {
        var sources = tweaks.Select(t => "tweak:" + t.Id).ToHashSet();
        var records = ChangeJournal.Instance.ActiveRecords.Where(r => sources.Contains(r.Source) || r.AlsoSources.Any(sources.Contains));
        return await ChangeJournal.Instance.RevertAsync(records).ConfigureAwait(false);
    }

    /// <summary>True if MONOLITH has an active (not reverted) change for this tweak — i.e. "Undo" is available.</summary>
    public static bool CanRevert(Tweak tweak) => ChangeJournal.Instance.HasActiveSource("tweak:" + tweak.Id);
}
