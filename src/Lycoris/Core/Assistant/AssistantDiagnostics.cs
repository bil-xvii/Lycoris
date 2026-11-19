using Lycoris.Core.Diagnostics;
using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Shell;
using Lycoris.Tweaks;

namespace Lycoris.Core.Assistant;

internal sealed record AssistantDiagnosticSnapshot(
    DateTime ObservedAt, IReadOnlyList<HealthCheckResult> Checks, IReadOnlySet<string> VerifiedFixIds)
{
    public IReadOnlyList<HealthReportRow> System { get; init; } = [];
}

internal enum AssistantFixState { RefreshUnavailable, NoLongerEligible, NotApplied, Failed, PendingRestart, Unverified, Resolved }

internal sealed record AssistantFixResult(AssistantFixState State, AssistantDiagnosticSnapshot? Snapshot);

public sealed record AssistantDiagnosticCard
{
    internal AssistantDiagnosticCard(HealthCheckResult check, string nextStep)
    {
        Id = check.Id;
        Title = check.Title;
        Detail = check.Detail;
        IsWarning = check.Severity == CheckSeverity.Warning;
        NextStep = nextStep;
    }

    internal string Id { get; }
    internal string? PageKey { get; init; }
    internal string? FocusId { get; init; }
    public string Title { get; }
    public string Detail { get; }
    public string NextStep { get; init; }
    public bool IsWarning { get; }
    public string SeverityLabel => IsWarning ? Loc.T("Внимание") : Loc.T("Совет");
    public string PathText { get; init; } = "";
    public string OpenLabel { get; init; } = "";
    public string FixLabel { get; init; } = "";
    public bool CanOpen => PageKey is not null;
    public bool CanFix => FixLabel.Length > 0;
}

/// <summary>Guidance is derived from known health rules and catalog entries, independently of the local model.</summary>
internal static class AssistantDiagnostics
{
    private sealed record Fix(string Target, string Label);

    private static Fix? Candidate(string? checkId) => checkId switch
    {
        "game-mode" => new(HealthRules.TweakGameMode, Loc.T("Включить игровой режим")),
        "gamedvr" => new(HealthRules.TweakGameDvr, Loc.T("Выключить фоновую запись")),
        "trim" => new(HealthRules.TweakTrim, Loc.T("Включить TRIM")),
        "hags" => new(HealthRules.TweakHags, Loc.T("Включить планирование GPU")),
        _ => null,
    };

    public static bool IsKnownFix(string? id) => Candidate(id) is not null;

    public static Tweak? FixCandidate(HealthCheckResult check) =>
        check.Severity == CheckSeverity.Warning
        && check.Action is { Kind: CheckActionKind.ApplyTweak, Target: { } target }
        && Candidate(check.Id)?.Target == target
        && TweakCatalog.Find(target) is { Risk: TweakRisk.Safe } tweak
            ? tweak : null;

    public static Tweak? FindVerifiedFix(AssistantDiagnosticSnapshot snapshot, string id) =>
        snapshot.VerifiedFixIds.Contains(id) && snapshot.Checks.FirstOrDefault(c => c.Id == id) is { } check
            ? FixCandidate(check) : null;

    public static async Task<AssistantFixResult> FixAsync(string id,
        Func<Task<AssistantDiagnosticSnapshot?>> refresh, Func<Task<AssistantDiagnosticSnapshot?>> refreshAfterBoundary,
        Func<Tweak, Func<Task<bool>>, Task<TweakRunResult?>> apply)
    {
        var fresh = await refresh();
        if (fresh is null) return new(AssistantFixState.RefreshUnavailable, null);
        if (FindVerifiedFix(fresh, id) is not { } tweak) return new(AssistantFixState.NoLongerEligible, fresh);

        AssistantFixState? rejected = null;
        var result = await apply(tweak, async () =>
        {
            fresh = await refreshAfterBoundary();
            if (fresh is null) rejected = AssistantFixState.RefreshUnavailable;
            else if (FindVerifiedFix(fresh, id)?.Id != tweak.Id) rejected = AssistantFixState.NoLongerEligible;
            return rejected is null;
        });
        if (rejected is { } reason) return new(reason, fresh);
        if (result is null) return new(AssistantFixState.NotApplied, fresh);

        fresh = await refreshAfterBoundary();
        return new(VerifyFix(id, result, fresh), fresh);
    }

    public static AssistantFixState VerifyFix(string id, TweakRunResult result, AssistantDiagnosticSnapshot? snapshot)
    {
        if (result.Errors.Count > 0 || result.SkippedReasons.Count > 0) return AssistantFixState.Failed;
        if (result.Applied == 0) return AssistantFixState.NotApplied;
        if (result.Restart != RestartRequirement.None) return AssistantFixState.PendingRestart;
        return snapshot?.Checks.Any(c => c.Id == id && c.Severity == CheckSeverity.Ok) == true
            ? AssistantFixState.Resolved : AssistantFixState.Unverified;
    }

    public static IReadOnlyList<AssistantDiagnosticCard> Cards(AssistantDiagnosticSnapshot snapshot) =>
        HealthScore.Ordered(snapshot.Checks).Where(c => c.Severity != CheckSeverity.Ok)
            .Select(check => Card(check, FindVerifiedFix(snapshot, check.Id) is not null)).ToArray();

    public static AssistantReply Explain(AssistantDiagnosticSnapshot snapshot)
    {
        var cards = Cards(snapshot);
        var sections = new List<string>();
        var warnings = cards.Where(card => card.IsWarning).ToArray();
        if (warnings.Length == 0)
            sections.Add(Loc.T("Среди доступных проверок предупреждений нет."));
        else
            AppendSection(Loc.T("Предупреждения по результатам проверок:"), warnings);
        AppendSection(Loc.T("Рекомендации по результатам проверок:"), cards.Where(card => !card.IsWarning));

        var destinations = cards.Where(card => card.CanOpen).DistinctBy(card => (card.PageKey, card.FocusId))
            .Select(card => new PaletteEntry
            {
                Kind = PaletteKind.Page, PageKey = card.PageKey!, FocusId = card.FocusId,
                Title = card.PathText, Description = card.NextStep,
            }).ToArray();
        return new AssistantReply(string.Join("\n\n", sections), destinations);

        void AppendSection(string title, IEnumerable<AssistantDiagnosticCard> items)
        {
            var details = string.Join("\n", items.Select(card => $"• {card.Title}\n{card.Detail}"));
            if (details.Length > 0) sections.Add(title + "\n" + details);
        }
    }

    private static AssistantDiagnosticCard Card(HealthCheckResult check, bool canFix)
    {
        var guidance = DashboardAdvice.For(check);
        var card = new AssistantDiagnosticCard(check,
            guidance?.NextStep ?? Loc.T("Изучите результат проверки выше. Если данных недостаточно, обновите диагностику."))
        {
            PathText = guidance?.PathText ?? "",
            PageKey = guidance?.PageKey,
            FocusId = guidance?.FocusId,
            OpenLabel = guidance?.PageKey is not null ? guidance.OpenAction!.Label : "",
        };
        if (canFix && Candidate(check.Id) is { } fix && FixCandidate(check) is { } tweak)
            card = card with
            {
                FixLabel = fix.Label,
                NextStep = tweak.Restart == RestartRequirement.Reboot
                    ? Loc.T("Нажмите кнопку исправления и проверьте изменение в окне подтверждения. Затем сохраните работу, перезагрузите ПК и обновите диагностику.")
                    : Loc.T("Нажмите кнопку исправления и проверьте изменение в окне подтверждения. Для ручной настройки откройте её в «Оптимизации»."),
            };
        return card;
    }
}
