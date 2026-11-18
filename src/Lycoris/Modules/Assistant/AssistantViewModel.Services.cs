using CommunityToolkit.Mvvm.Input;
using Lycoris.Core.Assistant;
using Lycoris.Modules.Services;
using Lycoris.Shell;

namespace Lycoris.Modules.Assistant;

public sealed partial class AssistantViewModel
{
    private readonly Func<ServiceActionRequest, CancellationToken, Task<ServiceActionResult>>? _executeServiceAction;

    private async Task AnswerServiceRequestAsync(AssistantMessage answer, ParsedServiceRequest parsed, CancellationToken ct)
    {
        if (parsed.Request is not { } request)
        {
            answer.Text = parsed.Kind == ServiceRequestKind.MissingTarget
                ? Loc.T("Какую службу? Укажите её отображаемое или системное имя, например Spooler.")
                : Loc.T("Укажите одну службу и одно действие. Например, «отключи службу Spooler».");
            return;
        }

        if (Demo || _executeServiceAction is null)
        {
            answer.Text = Demo ? Loc.T("В демонстрации изменения недоступны.") : Loc.T("Управление службами сейчас недоступно.");
            return;
        }

        // Await the actual result, including readback after a write. Cancel can stop preparation, but not undo a committed change.
        var result = await _executeServiceAction(request, ct);
        answer.Text = result.Message;
        if (result.Outcome == ServiceActionOutcome.NeedsAdmin && !request.ExactNameOnly && !ct.IsCancellationRequested &&
            ServiceRequestParser.FormatReplay(request.Action, result.CanonicalName) is { } payload)
        {
            var action = request.Action switch
            {
                ServiceAction.Start => Loc.Tf("Запуск службы {0}", result.CanonicalName),
                ServiceAction.Stop => Loc.Tf("Остановка службы {0}", result.CanonicalName),
                _ => Loc.Tf("Изменение типа запуска службы {0}", result.CanonicalName),
            };
            RequireAdmin(action, AdminIntent.RunServiceAction, payload);
        }

        if (result.JournalRecordId is { } id && id != Guid.Empty)
            answer.Actions.Add(new PaletteEntry
            {
                Kind = PaletteKind.Page, Title = Loc.T("Запись изменения"), PageKey = "restore",
                FocusId = CommandPaletteIndex.RestoreSectionPrefix + $"record:{id:D}",
            });

        if (result.CanonicalName is { } name &&
            result.Outcome is ServiceActionOutcome.Applied or ServiceActionOutcome.AlreadyApplied or ServiceActionOutcome.Unverified)
            answer.Actions.Add(new PaletteEntry
            {
                Kind = PaletteKind.Service, Title = Loc.Tf("Служба {0}", name), PageKey = "services", FocusId = name,
            });
    }

    private bool CanRunServiceAction(string? parameter) =>
        !Demo && !IsAnswering && _executeServiceAction is not null && ServiceRequestParser.ParseReplay(parameter) is not null;

    [RelayCommand(CanExecute = nameof(CanRunServiceAction))]
    private async Task RunServiceActionAsync(string? parameter)
    {
        if (!CanRunServiceAction(parameter) || ServiceRequestParser.ParseReplay(parameter) is not { } request) return;
        IsChatOpen = true;
        var question = request.Action switch
        {
            ServiceAction.Enable => Loc.Tf("Включи службу {0}", request.Target),
            ServiceAction.Disable => Loc.Tf("Отключи службу {0}", request.Target),
            ServiceAction.Start => Loc.Tf("Запусти службу {0}", request.Target),
            _ => Loc.Tf("Останови службу {0}", request.Target),
        };
        await AnswerAsync(question, (answer, ct) => AnswerServiceRequestAsync(answer, new(ServiceRequestKind.Ready, request), ct));
    }
}
