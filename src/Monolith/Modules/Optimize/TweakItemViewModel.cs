using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Tweaks;

namespace Monolith.Modules.Optimize;

/// <summary>One line of the "what will change" preview, with the value read from the system right now.</summary>
public sealed record ChangeLineViewModel(string Target, string Current, string New);

/// <summary>One tweak in the Optimize list: selection, detected state, preview and per-row undo.</summary>
public sealed partial class TweakItemViewModel : ObservableObject
{
    private readonly OptimizeViewModel _owner;
    private bool _isChecked;

    public TweakItemViewModel(Tweak tweak, OptimizeViewModel owner)
    {
        Tweak = tweak;
        _owner = owner;
    }

    public Tweak Tweak { get; }
    public string Id => Tweak.Id;
    public string Title => Tweak.Title;
    public string Description => Tweak.Description;
    public string Details => Tweak.Details;
    public bool HasDetails => !string.IsNullOrWhiteSpace(Tweak.Details);
    public TweakCategory Category => Tweak.Category;
    public string CategoryText => Tweak.CategoryText;
    public string RiskText => Tweak.RiskText;
    /// <summary>"Safe" / "Optional" / "Advanced" — picks the tag style in XAML.</summary>
    public string RiskKey => Tweak.Risk.ToString();
    public bool IsAdvanced => Tweak.Risk == TweakRisk.Advanced;
    public bool HasRestart => Tweak.Restart != RestartRequirement.None;
    public string RestartTag => Tweak.Restart switch
    {
        RestartRequirement.Explorer => "ПЕРЕЗАПУСК ПРОВОДНИКА",
        RestartRequirement.SignOut => "ВЫХОД ИЗ СИСТЕМЫ",
        RestartRequirement.Reboot => "ПЕРЕЗАГРУЗКА",
        _ => "",
    };
    public string WarningText => string.IsNullOrWhiteSpace(Tweak.Warning)
        ? "Это настройка с реальным компромиссом (безопасность или совместимость). Она не входит ни в один профиль."
        : Tweak.Warning!;
    public string UndoText => Tweak.Restart == RestartRequirement.None
        ? "Кнопка «ОТКАТИТЬ» у этого пункта или раздел «Восстановление». Перед изменением MONOLITH сохраняет исходные значения и возвращает их точно такими, какими они были."
        : $"Кнопка «ОТКАТИТЬ» у этого пункта или раздел «Восстановление». Исходные значения сохраняются перед изменением. После отката тоже {Tweak.RestartText}.";

    /// <summary>Detected state; null while detection is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateKey), nameof(StateText), nameof(IsApplicable), nameof(WillChange), nameof(IsPending),
        nameof(NotApplicableText), nameof(IsApplied))]
    private TweakState? _state;

    /// <summary>MONOLITH has an active journal entry for this tweak, so it can be undone.</summary>
    [ObservableProperty] private bool _canRevert;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>First visible row of its section (draws the top border of the card).</summary>
    [ObservableProperty] private bool _isFirstInGroup;

    [ObservableProperty] private IReadOnlyList<ChangeLineViewModel> _changes = [];
    [ObservableProperty] private bool _hasPlannedChanges = true;

    public string StateKey => State?.ToString() ?? "Checking";
    public string StateText => OptimizeLogic.StateText(State);
    public bool IsApplied => State == TweakState.Applied;
    public bool IsApplicable => State != TweakState.NotApplicable;
    public bool WillChange => State is { } s && OptimizeLogic.WillChange(s);
    public bool IsPending => IsChecked && WillChange;

    public string? NotApplicableText => State == TweakState.NotApplicable
        ? string.IsNullOrWhiteSpace(Tweak.NotApplicableReason)
            ? "Не подходит для этого компьютера или этой версии Windows: нужного компонента нет в системе."
            : Tweak.NotApplicableReason
        : null;

    /// <summary>
    /// Bound two-way to the row's checkbox. Checking an expert tweak first shows its warning; until the user
    /// accepts it the box stays unchecked. Not-applicable tweaks can't be checked.
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            if (value && !IsApplicable)
            {
                RefreshCheckLater();
                return;
            }
            if (value && IsAdvanced)
            {
                RefreshCheckLater();
                _ = _owner.ConfirmExpertAsync(this);
                return;
            }
            SetChecked(value);
            _owner.OnSelectionEditedByUser();
        }
    }

    /// <summary>Programmatic selection (profiles, select-all, accepted warnings): no side effects.</summary>
    internal bool SetChecked(bool value)
    {
        if (_isChecked == value) return false;
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(IsPending));
        return true;
    }

    /// <summary>The checkbox already flipped itself; re-read our value after the binding update finishes.</summary>
    private void RefreshCheckLater()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        d.BeginInvoke(() => OnPropertyChanged(nameof(IsChecked)), DispatcherPriority.Background);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = _owner.LoadChangesAsync(this);
    }

    [RelayCommand(CanExecute = nameof(CanRevertNow))]
    private Task RevertAsync() => _owner.RevertOneAsync(this);

    private bool CanRevertNow() => _owner.CanModify;

    [RelayCommand]
    private void ToggleDetails() => IsExpanded = !IsExpanded;

    internal void RefreshCommands() => RevertCommand.NotifyCanExecuteChanged();
}
