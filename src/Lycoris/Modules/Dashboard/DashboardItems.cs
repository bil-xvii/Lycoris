using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Modules.Dashboard.Checks;
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
    }

    /// <summary>Re-reads the live button texts (the cleanup size arrives after the row was built).</summary>
    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(SecondaryActionText));
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
    public string ActionText => Result.Action is { } a ? _label?.Invoke(a) ?? a.Label : "";
    public string SecondaryActionText => Result.SecondaryAction is { } b ? _label?.Invoke(b) ?? b.Label : "";
    public bool ActionNeedsAdmin => Result.Action?.RequiresAdmin == true;
    /// <summary>A button is never disabled for missing rights: the click offers the relaunch with admin rights and continues by itself.</summary>
    public bool ActionAvailable => HasAction;
    public bool ShowAdminHint => ActionNeedsAdmin && !Elevation.IsElevated;

    public string ActionTooltip => Result.Action?.Kind switch
    {
        CheckActionKind.Navigate => Loc.T("Открыть раздел Lycoris"),
        CheckActionKind.OpenUri => Loc.T("Открыть параметры Windows — сама по себе кнопка ничего не меняет"),
        CheckActionKind.OpenTaskManager => Loc.T("Открыть диспетчер задач Windows — сама по себе кнопка ничего не закрывает"),
        CheckActionKind.FocusTweak => Loc.T("Показать настройку в разделе «Оптимизация» — сама по себе кнопка ничего не меняет"),
        CheckActionKind.FocusDevice => Loc.T("Показать монитор в разделе «Устройства и сеть» — сама по себе кнопка ничего не меняет"),
        _ when ShowAdminHint => Loc.T("Нужны права администратора: Lycoris предложит перезапуск и продолжит действие сам"),
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
