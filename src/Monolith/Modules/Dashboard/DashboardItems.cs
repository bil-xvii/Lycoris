using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Dashboard.Checks;
using Monolith.UI;

namespace Monolith.Modules.Dashboard;

/// <summary>One row of the health checklist.</summary>
public sealed partial class CheckItemViewModel : ObservableObject
{
    private readonly Func<CheckItemViewModel, Task> _run;

    public CheckItemViewModel(HealthCheckResult result, Func<CheckItemViewModel, Task> run)
    {
        Result = result;
        _run = run;
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
        CheckSeverity.Warning => "ВНИМАНИЕ",
        CheckSeverity.Info => "ИНФО",
        _ => "НОРМА",
    };

    public string Glyph => Severity switch
    {
        CheckSeverity.Warning => Glyphs.Warning,
        CheckSeverity.Info => Glyphs.Info,
        _ => Glyphs.Check,
    };

    public bool HasAction => Result.Action is not null;
    public string ActionText => Result.Action?.Label ?? "";
    public bool ActionNeedsAdmin => Result.Action?.RequiresAdmin == true;
    public bool ActionAvailable => HasAction && (!ActionNeedsAdmin || Elevation.IsElevated);
    public bool ShowAdminHint => ActionNeedsAdmin && !Elevation.IsElevated;

    public string ActionTooltip => Result.Action?.Kind switch
    {
        CheckActionKind.Navigate => "Открыть раздел MONOLITH",
        CheckActionKind.OpenUri => "Открыть параметры Windows — сама по себе кнопка ничего не меняет",
        CheckActionKind.OpenPowerOptions => "Открыть «Электропитание» Windows — сама по себе кнопка ничего не меняет",
        _ when ShowAdminHint => "Нужны права администратора: перезапустите MONOLITH кнопкой «Права администратора»",
        _ => "Перед изменением MONOLITH покажет, что именно будет сделано",
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
