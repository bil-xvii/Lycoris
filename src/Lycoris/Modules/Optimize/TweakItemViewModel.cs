using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Tweaks;

namespace Lycoris.Modules.Optimize;

/// <summary>One line of the "what will change" preview, with the value read from the system right now.</summary>
public sealed record ChangeLineViewModel(string Target, string Current, string New);

/// <summary>A button that opens one official source of a tweak.</summary>
public sealed record SourceLinkViewModel(string Url, string Host)
{
    /// <summary>Screen-reader name without the decorative «·» and «↗»: «Источник: learn.microsoft.com».</summary>
    public string AutomationName => Loc.Tf("Источник: {0}", Host.ToLowerInvariant());
}

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
    public bool HasDetails => _owner.ExpertMode && !string.IsNullOrWhiteSpace(Tweak.Details);
    public bool ShowRiskDetails => IsAdvanced && _owner.ExpertMode;
    internal void RefreshExpertMode() { OnPropertyChanged(nameof(HasDetails)); OnPropertyChanged(nameof(ShowRiskDetails)); }
    public TweakCategory Category => Tweak.Category;
    public string CategoryText => Tweak.CategoryText;
    public string RiskText => Tweak.RiskText;
    /// <summary>"Safe" / "Optional" / "Advanced" — picks the tag style in XAML.</summary>
    public string RiskKey => Tweak.Risk.ToString();
    public bool IsAdvanced => Tweak.Risk == TweakRisk.Advanced;
    public bool HasRestart => Tweak.Restart != RestartRequirement.None;
    public string RestartTag => Tweak.Restart switch
    {
        RestartRequirement.Explorer => Loc.T("ПЕРЕЗАПУСК ПРОВОДНИКА"),
        RestartRequirement.SignOut => Loc.T("ВЫХОД ИЗ СИСТЕМЫ"),
        RestartRequirement.Reboot => Loc.T("ПЕРЕЗАГРУЗКА"),
        _ => "",
    };
    public string WarningText => string.IsNullOrWhiteSpace(Tweak.Warning)
        ? Loc.T("Это настройка с реальным компромиссом (безопасность или совместимость). Она не входит ни в один профиль.")
        : Tweak.Warning!;
    /// <summary>Irreversible user-data loss caused by applying this tweak, or null.</summary>
    public string? DataLossNote => OptimizeLogic.DataLossNote(Tweak.Id);
    public bool HasDataLoss => DataLossNote != null;
    public string UndoText => DataLossNote is { } loss
        ? Loc.Tf("Кнопка «ОТКАТИТЬ» у этого пункта или раздел «Восстановление» вернут настройку. {0}", loss)
        : Tweak.Restart == RestartRequirement.None
        ? Loc.T("Кнопка «ОТКАТИТЬ» у этого пункта или раздел «Восстановление». Перед изменением Lycoris сохраняет исходные значения и возвращает их точно такими, какими они были.")
        : Loc.Tf("Кнопка «ОТКАТИТЬ» у этого пункта или раздел «Восстановление». Исходные значения сохраняются перед изменением. После отката тоже {0}.", Tweak.RestartText);

    /// <summary>Detected state; null while detection is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateKey), nameof(StateText), nameof(IsApplicable), nameof(WillChange), nameof(IsPending),
        nameof(NotApplicableText), nameof(IsApplied), nameof(IsApplicableNow), nameof(DependencySatisfiedByPending), nameof(StatusNote),
        nameof(HasPreview))]
    private TweakState? _state;

    /// <summary>Lycoris has an active journal entry for this tweak, so it can be undone.</summary>
    [ObservableProperty] private bool _canRevert;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Briefly framed after a focus request (command palette, Dashboard check).</summary>
    [ObservableProperty] private bool _isHighlighted;

    /// <summary>First visible row of its section (draws the top border of the card).</summary>
    [ObservableProperty] private bool _isFirstInGroup;

    /// <summary>Last visible row of its section (rounds the bottom corners of the card).</summary>
    [ObservableProperty] private bool _isLastInGroup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(PreviewCurrent), nameof(PreviewNew), nameof(PreviewMore), nameof(HasPreviewMore))]
    private IReadOnlyList<ChangeLineViewModel> _changes = [];

    /// <summary>The row would change something and the current value is known: the line «сейчас X → станет Y» is shown.</summary>
    public bool HasPreview => WillChange && Changes.Count > 0 && Changes[0].Current != Loc.T("чтение…");
    public string PreviewCurrent => Changes.Count > 0 ? Changes[0].Current : "";
    public string PreviewNew => Changes.Count > 0 ? Changes[0].New : "";
    public bool HasPreviewMore => Changes.Count > 1;
    public string PreviewMore => Changes.Count > 1 ? Loc.N(Changes.Count - 1, "  +{0} параметр", "  +{0} параметра", "  +{0} параметров") : "";
    [ObservableProperty] private bool _hasPlannedChanges = true;

    public string StateKey => State?.ToString() ?? "Checking";
    public string StateText => OptimizeLogic.StateText(State);
    public bool IsApplied => State == TweakState.Applied;
    public bool IsApplicable => State != TweakState.NotApplicable;
    /// <summary>
    /// Not applicable by its own detection, but a tweak it requires (<see cref="Tweak.Requires"/>) is checked and pending
    /// in the same apply — e.g. a power mode while the Balanced plan is about to be switched on. The row can then be
    /// selected; the engine applies it after the dependency.
    /// </summary>
    public bool DependencySatisfiedByPending => State == TweakState.NotApplicable && PendingDependency is not null;
    /// <summary>
    /// Can be checked and applied right now: applicable, or made applicable by a pending dependency (<see cref="OptimizeLogic.IsApplicableNow"/>).
    /// A row the PC's hardware/OS rules out (AppliesTo) never becomes applicable through a dependency.
    /// </summary>
    public bool IsApplicableNow => OptimizeLogic.CanApplyRisk(Tweak.Risk, _owner.ExpertMode)
        && !_owner.IsHardwareExcluded(Id) && OptimizeLogic.IsApplicableNow(State, Tweak, _owner.IsPendingRow);
    public bool WillChange => State is { } s && (OptimizeLogic.WillChange(s) || DependencySatisfiedByPending);
    public bool IsPending => IsChecked && WillChange;

    /// <summary>The first required tweak that is checked and pending in this same apply, or null.</summary>
    internal TweakItemViewModel? PendingDependency => _owner.PendingDependencyOf(this);

    public string? NotApplicableText => State == TweakState.NotApplicable ? OptimizeLogic.NotApplicableText(Tweak) : null;

    /// <summary>
    /// The caption under the row: «Применится после «…»» while a dependency is pending, else the not-applicable reason,
    /// null when the row is applicable.
    /// </summary>
    public string? StatusNote => OptimizeLogic.StatusNote(Tweak, State, PendingDependency?.Title);

    /// <summary>
    /// Bound two-way to the row's checkbox. Checking an expert tweak first shows its warning; until the user
    /// accepts it the box stays unchecked. Not-applicable tweaks can't be checked (unless a dependency is pending).
    /// Checking a member of an exclusive group unchecks the other members.
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            if (value && !IsApplicableNow)
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
            if (value) _owner.EnforceExclusive(this);
            _owner.OnSelectionEditedByUser();
        }
    }

    /// <summary>A dependency or a group peer changed: re-read everything that depends on other rows.</summary>
    internal void NotifyDependencyChanged()
    {
        OnPropertyChanged(nameof(DependencySatisfiedByPending));
        OnPropertyChanged(nameof(IsApplicableNow));
        OnPropertyChanged(nameof(WillChange));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(StatusNote));
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

    private bool CanRevertNow() => _owner.CanModify; // read-only: the click asks for admin rights (RequireAdmin) and repeats itself

    [RelayCommand]
    private void ToggleDetails() => IsExpanded = !IsExpanded;

    // «ОСНОВАНИЕ» (roadmap item 10): the official source behind this tweak; the page opens in the user's browser without admin rights.
    // Built once per row (the language changes only with a restart): the row template reads these ~7 times per created row.
    private TweakEvidence? _evidence;
    private bool _evidenceRead;
    private IReadOnlyList<SourceLinkViewModel>? _sourceLinks;
    public TweakEvidence? Evidence
    {
        get
        {
            if (!_evidenceRead) { _evidence = TweakSources.For(Id); _evidenceRead = true; }
            return _evidence;
        }
    }
    public bool HasEvidence => Evidence is not null;
    public string EvidenceBasis => Evidence?.Basis ?? "";
    public string EvidenceLimits => Evidence?.Limits ?? "";
    public bool HasEvidenceLimits => !string.IsNullOrWhiteSpace(EvidenceLimits);
    public string EvidenceMeta => Evidence is not { } e ? ""
        : e.IsStale(DateOnly.FromDateTime(DateTime.Today))
            ? Loc.Tf("{0} · источник проверен {1}, сведения могли устареть", e.Windows, Loc.DateText(e.Reviewed.ToDateTime(TimeOnly.MinValue)))
            : Loc.Tf("{0} · источник проверен {1}", e.Windows, Loc.DateText(e.Reviewed.ToDateTime(TimeOnly.MinValue)));
    public IReadOnlyList<SourceLinkViewModel> SourceLinks =>
        _sourceLinks ??= Evidence?.Urls.Select(u => new SourceLinkViewModel(u, TweakEvidence.HostOf(u))).ToList() ?? [];
    public bool HasNoOfficialSource => Evidence is { HasOfficialSource: false };

    [RelayCommand]
    private async Task OpenSourceAsync(string? url)
    {
        if (url is null || !TweakEvidence.IsAllowedUrl(url)) return;
        if (await UnelevatedShell.OpenAsync(url)) return;
        Log.Warn("Не удалось открыть источник: {0}", url);
        Lycoris.UI.Ui.Toast(Loc.Tf("Не удалось открыть {0}", TweakEvidence.HostOf(url).ToLowerInvariant()), Lycoris.UI.ToastKind.Warning);
    }

    internal void RefreshCommands() => RevertCommand.NotifyCanExecuteChanged();
}
