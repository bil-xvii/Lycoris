using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Tweaks;
using Lycoris.UI;

namespace Lycoris.Modules.Dashboard;

/// <summary>One row of the health checklist.</summary>
public sealed partial class CheckItemViewModel : ObservableObject
{
    private readonly Func<CheckItemViewModel, Task> _run;
    private readonly Func<CheckAction, string?>? _label;

    /// <param name="label">Optional live text of an action button («ОЧИСТИТЬ 654,8 МБ» instead of «ОЧИСТИТЬ ДИСК»); null = the action's own label.</param>
    public CheckItemViewModel(HealthCheckResult result, Func<CheckItemViewModel, Task> run, Func<CheckAction, string?>? label = null)
    {
        Result = result;
        _run = run;
        _label = label;
        Guidance = DashboardAdvice.For(result);
        SecondaryGuidance = DashboardAdvice.Catalog(result.SecondaryAction);
    }

    /// <summary>Re-reads the live button texts (the cleanup size arrives after the row was built).</summary>
    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(ShowPrimaryAction));
    }

    public HealthCheckResult Result { get; }
    public string Title => Result.Title;
    public string Detail => Result.Detail;
    public CheckSeverity Severity => Result.Severity;
    public bool IsWarning => Severity == CheckSeverity.Warning;
    public bool IsInfo => Severity == CheckSeverity.Info;
    public bool IsOk => Severity == CheckSeverity.Ok;

    public string SeverityText => Severity switch
    {
        CheckSeverity.Warning => Loc.T("ВНИМАНИЕ"),
        CheckSeverity.Info => Loc.T("СОВЕТ"),
        _ => Loc.T("НОРМА"),
    };

    public string Glyph => Severity switch
    {
        CheckSeverity.Warning => Glyphs.Warning,
        CheckSeverity.Info => Glyphs.Info,
        _ => Glyphs.Check,
    };

    public bool HasAction => Result.Action is not null;
    internal CheckGuidance? Guidance { get; }
    private CheckGuidance? SecondaryGuidance { get; }
    public bool HasGuidance => Guidance is not null;
    public bool HasOpenAction => Guidance?.OpenAction is not null;
    public string PathText => Guidance?.PathText ?? "";
    public string NextStep => Guidance?.NextStep ?? "";
    public string OpenText => Guidance?.OpenAction?.Label ?? "";
    public string OpenTooltip => Tooltip(Guidance?.OpenAction);
    public string SecondaryPathText => SecondaryGuidance?.PathText ?? "";
    public string SecondaryNextStep => SecondaryGuidance?.NextStep ?? "";

    public bool ShowPrimaryAction => !IsOk && Result.Action is { } action && (action.Kind switch
    {
        CheckActionKind.ApplyTweak => TweakCatalog.Find(action.Target ?? "") is { Risk: TweakRisk.Safe or TweakRisk.Optional },
        CheckActionKind.Restart or CheckActionKind.MemoryDiagnostic => true,
        CheckActionKind.Clean => _label?.Invoke(action) is not null,
        _ => false,
    });
    public string ActionText => Result.Action is { } a ? _label?.Invoke(a) ?? a.Label : "";
    public string SecondaryActionText => SecondaryGuidance?.OpenAction?.Label ?? Result.SecondaryAction?.Label ?? "";
    public bool ActionNeedsAdmin => Result.Action?.RequiresAdmin == true;
    /// <summary>A button is never disabled for missing rights: the click offers the relaunch with admin rights and continues by itself.</summary>
    public bool ActionAvailable => HasAction;
    public bool ShowAdminHint => ActionNeedsAdmin && !Elevation.IsElevated;
    public string ActionGlyph => ShowAdminHint ? Glyphs.Lock : Glyphs.Repair;

    public string ActionTooltip => Tooltip(Result.Action);
    public string SecondaryActionTooltip => Tooltip(Result.SecondaryAction);

    private static string Tooltip(CheckAction? action) => action?.Kind switch
    {
        CheckActionKind.Navigate => Loc.T("Открыть раздел Lycoris"),
        CheckActionKind.OpenUri => Loc.T("Открыть параметры Windows — сама по себе кнопка ничего не меняет"),
        CheckActionKind.OpenTaskManager => Loc.T("Открыть диспетчер задач Windows — сама по себе кнопка ничего не закрывает"),
        CheckActionKind.FocusTweak => Loc.T("Показать настройку в разделе «Оптимизация» — сама по себе кнопка ничего не меняет"),
        CheckActionKind.FocusDevice => Loc.T("Показать монитор в разделе «Устройства и сеть» — сама по себе кнопка ничего не меняет"),
        CheckActionKind.OpenReliabilityMonitor => Loc.T("Открыть журнал надёжности Windows для просмотра событий"),
        CheckActionKind.OpenSystemInformation => Loc.T("Открыть сведения о системе Windows"),
        CheckActionKind.DiagnoseAgain => Loc.T("Повторить замер нагрузки без изменения настроек"),
        _ when action?.RequiresAdmin == true && !Elevation.IsElevated =>
            Loc.T("Нужны права администратора: Lycoris предложит перезапуск и продолжит действие сам"),
        _ => Loc.T("Перед изменением Lycoris покажет, что именно будет сделано"),
    };

    [ObservableProperty] private bool _isRunning;

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning || !ActionAvailable) return;
        IsRunning = true;
        try { await _run(this); }
        finally { IsRunning = false; }
    }
}

/// <summary>One line of the СИСТЕМА card.</summary>
public sealed partial class SystemRow : ObservableObject
{
    public SystemRow(string label, string value, string detail = "")
    {
        Label = label;
        _value = value;
        _detail = detail;
    }

    public string Label { get; }
    [ObservableProperty] private string _value;
    [ObservableProperty] private string _detail;
}
