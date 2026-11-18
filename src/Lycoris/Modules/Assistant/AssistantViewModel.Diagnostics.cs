using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Core.Assistant;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.UI;

namespace Lycoris.Modules.Assistant;

public sealed partial class AssistantViewModel
{
    private readonly Func<Task<AssistantDiagnosticSnapshot?>> _refreshDiagnostics;
    private readonly Func<string, Func<string, bool>, Task<AssistantFixResult>> _fixDiagnostic;
    private readonly Action<string, string?> _open;

    public ObservableCollection<AssistantDiagnosticCard> DiagnosticCards { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshDiagnosticsCommand), nameof(FixDiagnosticCommand), nameof(RunCheckCommand))]
    private bool _isDiagnosticBusy;

    [ObservableProperty] private bool _hasDiagnostics;
    [ObservableProperty] private string _diagnosticSummary = "";
    [ObservableProperty] private string _diagnosticsTimestamp = "";
    [ObservableProperty] private string _diagnosticStatus = "";

    private bool CanRefreshDiagnostics() => !IsDiagnosticBusy;
    private bool CanFixDiagnostic(AssistantDiagnosticCard? card) =>
        !IsDiagnosticBusy && (Demo || !ChangeSession.Locked) && card is { CanFix: true } && DiagnosticCards.Contains(card);
    private bool CanRunCheck(string? id) => !Demo && !ChangeSession.Locked && !IsDiagnosticBusy && AssistantDiagnostics.IsKnownFix(id);

    private async Task ExplainProblemsAsync(AssistantMessage answer, CancellationToken ct)
    {
        var fresh = Demo ? null : await _refreshDiagnostics().WaitAsync(ct);
        if (fresh is not { Checks.Count: > 0 })
        {
            answer.Text = RefreshUnavailableText();
            return;
        }

        var reply = AssistantDiagnostics.Explain(fresh);
        answer.Text = reply.Text;
        answer.Note = Loc.Tf("Источник — проверка Lycoris от {0}", Loc.DateTimeText(fresh.ObservedAt));
        foreach (var destination in reply.Actions) answer.Actions.Add(destination);
    }

    [RelayCommand(CanExecute = nameof(CanRefreshDiagnostics))]
    private async Task RefreshDiagnosticsAsync()
    {
        if (!CanRefreshDiagnostics()) return;
        if (Demo) { AssistantDemo.FillDiagnostics(this); return; }

        IsDiagnosticBusy = true;
        DiagnosticStatus = Loc.T("Проверяем компьютер…");
        try
        {
            var fresh = await _refreshDiagnostics();
            ShowDiagnostics(fresh);
            DiagnosticStatus = fresh is null ? RefreshUnavailableText() : "";
        }
        finally
        {
            IsDiagnosticBusy = false;
        }
    }

    [RelayCommand]
    private void OpenDiagnostic(AssistantDiagnosticCard? card)
    {
        if (card is { PageKey: { } page } && DiagnosticCards.Contains(card))
            _open(page, card.FocusId);
    }

    [RelayCommand(CanExecute = nameof(CanFixDiagnostic))]
    private async Task FixDiagnosticAsync(AssistantDiagnosticCard? card)
    {
        if (card is null || !CanFixDiagnostic(card)) return;
        if (Demo) { DiagnosticStatus = Loc.T("В демонстрации изменения недоступны."); return; }
        await RunCheckAsync(card.Id);
    }

    /// <summary>The existing RunCheck replay intent, scoped to Assistant's allowlisted fixes and a single check ID.</summary>
    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task RunCheckAsync(string? id)
    {
        if (id is null || !CanRunCheck(id)) return;
        IsDiagnosticBusy = true;
        DiagnosticStatus = Loc.T("Перепроверяем настройку…");
        try
        {
            var result = await _fixDiagnostic(id, title => RequireAdmin(title, "RunCheck", id));
            ShowDiagnostics(result.Snapshot);
            DiagnosticStatus = result.State switch
            {
                AssistantFixState.RefreshUnavailable => RefreshUnavailableText(),
                AssistantFixState.NoLongerEligible => Loc.T("По результатам новой проверки исправление больше недоступно."),
                AssistantFixState.NotApplied => Loc.T("Изменение не применено."),
                AssistantFixState.Failed => Loc.T("Возникла ошибка при изменении. Подробности в журнале действий."),
                AssistantFixState.PendingRestart => Loc.T("Изменение записано. После перезапуска обновите диагностику, чтобы проверить результат."),
                AssistantFixState.Resolved => Loc.T("Исправление подтверждено повторной проверкой. Откат доступен в «Восстановлении»."),
                _ => Loc.T("Повторная проверка пока не подтвердила исправление. Обновите диагностику ещё раз."),
            };
        }
        finally
        {
            IsDiagnosticBusy = false;
        }
    }

    internal void ShowDiagnostics(AssistantDiagnosticSnapshot? snapshot)
    {
        DiagnosticCards.Clear();
        HasDiagnostics = snapshot is not null;
        DiagnosticSummary = "";
        DiagnosticsTimestamp = "";
        if (snapshot is null) return;

        var checks = snapshot.Checks;
        DiagnosticSummary = Loc.Tf("Среди доступных проверок · Предупреждения: {0} · Советы: {1} · В норме: {2}",
            checks.Count(c => c.Severity == CheckSeverity.Warning), checks.Count(c => c.Severity == CheckSeverity.Info),
            checks.Count(c => c.Severity == CheckSeverity.Ok));
        DiagnosticsTimestamp = Loc.Tf("Проверка от {0}", Loc.DateTimeText(snapshot.ObservedAt));
        foreach (var card in AssistantDiagnostics.Cards(snapshot)) DiagnosticCards.Add(card);
    }

    private static string RefreshUnavailableText() =>
        Loc.T("Свежие результаты недоступны. Дождитесь завершения текущей задачи или повторите проверку.");

    private static void OpenLocation(string page, string? focus)
    {
        if (focus is not null) Ui.RequestFocus(page, focus);
        Ui.Navigate(page);
    }
}
