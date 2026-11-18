using Lycoris.Core.Diagnostics;
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
        AssistantDiagnosticCard Manual(string step, string path = "") => new(check, step) { PathText = path };
        AssistantDiagnosticCard Link(string page, string? focus, string path, string open, string step) =>
            new(check, step) { PageKey = page, FocusId = focus, PathText = path, OpenLabel = open };

        var action = check.Action;
        if (action is { Kind: CheckActionKind.FocusTweak or CheckActionKind.ApplyTweak, Target: { } id }
            && TweakCatalog.Find(id) is { } tweak)
        {
            var card = Link("optimize", id, Loc.Tf("Оптимизация → {0}", Loc.T(tweak.Title)), Loc.T("Открыть настройку"),
                id == HealthRules.TweakUpdatePin
                    ? Loc.T("Откройте настройку и проверьте, нужна ли закреплённая версия Windows. Если закрепление больше не нужно, отмените его в настройке.")
                    : Loc.T("Откройте настройку. Проверьте её описание, отметьте пункт и нажмите «ПРИМЕНИТЬ» внизу."));
            if (canFix && Candidate(check.Id) is { } fix)
                card = card with
                {
                    FixLabel = fix.Label,
                    NextStep = tweak.Restart == RestartRequirement.Reboot
                        ? Loc.T("Нажмите кнопку исправления и проверьте изменение в окне подтверждения. Затем сохраните работу, перезагрузите ПК и обновите диагностику.")
                        : Loc.T("Нажмите кнопку исправления и проверьте изменение в окне подтверждения. Для ручной настройки откройте её в «Оптимизации»."),
                };
            return card;
        }

        return (check.Id, action?.Kind, action?.Target, action?.Focus) switch
        {
            ("startup", CheckActionKind.Navigate, "startup", _) =>
                Link("services", "tab:startup", Loc.T("Автозапуск и службы → Автозапуск"), Loc.T("Открыть автозапуск"),
                    Loc.T("Просмотрите список и выключите переключатель только у знакомой программы, которую не нужно запускать вместе с Windows.")),
            ("restore", CheckActionKind.Navigate, "restore", _) =>
                Link("restore", "section:restore-points", Loc.T("Восстановление → Защита системы Windows"), Loc.T("Открыть защиту системы"),
                    Loc.T("В карточке «Защита системы Windows» нажмите «ВКЛЮЧИТЬ ЗАЩИТУ» и проверьте окно подтверждения.")),
            ("drift", CheckActionKind.Navigate, "restore", _) =>
                Link("restore", "section:drift", Loc.T("Восстановление → Изменено вне Lycoris"), Loc.T("Открыть изменения"),
                    Loc.T("Сравните «было» и «стало» у каждой записи. Решите, какие изменения нужно применить снова.")),
            ("disk-space" or "maintenance", CheckActionKind.Navigate, "cleaner", _) =>
                Link("cleaner", null, Loc.T("Обслуживание → Очистка"), Loc.T("Открыть очистку"),
                    Loc.T("Дождитесь анализа, проверьте отмеченные пункты и нажмите «ОЧИСТИТЬ». Lycoris покажет список для подтверждения.")),
            ("devices", CheckActionKind.Navigate, "drivers", "tab:problems") =>
                Link("drivers", "tab:problems", Loc.T("Драйверы → Проблемные устройства"), Loc.T("Открыть проблемные устройства"),
                    Loc.T("Найдите устройство по названию и коду ошибки. Откройте рекомендацию рядом с этим устройством.")),
            ("gpu-driver", CheckActionKind.Navigate, "drivers", "tab:overview") =>
                Link("drivers", "tab:overview", Loc.T("Драйверы → Подбор для вашего ПК → Видеокарта"), Loc.T("Открыть драйвер видеокарты"),
                    Loc.T("Сравните установленную и доступную версии в карточке «Видеокарта». Кнопка обновления появится, если есть подходящий драйвер.")),
            ("stability", CheckActionKind.Navigate, "drivers", "tab:updates") =>
                Link("drivers", "tab:updates", Loc.T("Драйверы → Обновления Windows"), Loc.T("Открыть обновления драйверов"),
                    Loc.T("Нажмите «НАЙТИ ОБНОВЛЕНИЯ» и проверьте, предлагается ли обновление для устройства, указанного в результате.")),
            ("stability", CheckActionKind.Navigate, "tools", _) =>
                Link("tools", null, Loc.T("Обслуживание → Ремонт и диски"), Loc.T("Открыть инструменты"),
                    Loc.T("Сохраните работу, затем запустите «Диагностику памяти Windows» и прочитайте варианты проверки.")),
            ("refresh-rate", CheckActionKind.FocusDevice, "kind:monitor", _) =>
                Link("devices", "kind:monitor", Loc.T("Устройства и сеть → Монитор → Частота обновления"), Loc.T("Открыть настройки монитора"),
                    Loc.T("Выберите нужный монитор и частоту обновления, затем нажмите «ПРИМЕНИТЬ» и подтвердите изображение.")),
            ("pending-reboot" or "uptime", _, _, _) =>
                Manual(Loc.T("Сохраните работу и выберите «Перезагрузка» в меню питания Windows. После входа обновите диагностику."),
                    Loc.T("Пуск → Питание → Перезагрузка")),
            ("power-plan", CheckActionKind.OpenUri, HealthRules.PowerSettingsUri, _) =>
                Manual(Loc.T("Откройте параметры питания Windows и проверьте переключатель энергосбережения. Если режим включён намеренно, оставьте его."),
                    Loc.T("Параметры Windows → Система → Питание")),
            ("vbs", CheckActionKind.OpenUri, HealthRules.WindowsSecurityUri, _) =>
                Manual(Loc.T("Откройте раздел «Изоляция ядра» и проверьте состояние списка блокировки уязвимых драйверов Microsoft."),
                    Loc.T("Безопасность Windows → Безопасность устройства → Изоляция ядра")),
            ("ram-speed" or "rebar" or "pcie-link" or "boot-security", _, _, _) =>
                Manual(Loc.T("Перед изменением откройте руководство для вашей модели ПК или материнской платы. Сверьте доступность и название настройки.")),
            ("monitor-gpu", _, _, _) =>
                Manual(Loc.T("Проверьте, куда подключён кабель монитора. Если нужна дискретная видеокарта, подключите монитор к её видеовыходу.")),
            ("disk-health", _, _, _) =>
                Manual(Loc.T("Скопируйте важные файлы на другой накопитель, затем проверьте состояние указанного диска утилитой его производителя.")),
            _ => Manual(Loc.T("Изучите результат проверки выше. Если данных недостаточно, обновите диагностику.")),
        };
    }
}
