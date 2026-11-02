using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.UI;

namespace Lycoris.Shell;

public static class WizardStartupPolicy
{
    public static bool IsConfiguredUser(bool settingsExist, bool hasJournal, bool expertMode, string lastProfile) =>
        settingsExist || hasJournal || expertMode || !string.Equals(lastProfile, "Recommended", StringComparison.Ordinal);
    public static bool ShouldAutoShow(bool completed, bool inProgress, bool settingsExist, bool hasJournal, bool expertMode, string lastProfile, bool replayIntent, bool qaMode, bool demoWizard = false) =>
        !replayIntent && (!qaMode || demoWizard) && (inProgress || !completed && !IsConfiguredUser(settingsExist, hasJournal, expertMode, lastProfile));
}
public static class WizardChoices
{
    public static bool ShowLaptop(bool factsKnown, bool isLaptop) => factsKnown && isLaptop;
}

public sealed partial class WizardViewModel : ObservableObject
{
    private readonly Action _close;
    private readonly Action<string> _navigate;
    public WizardViewModel(Action close, Action<string> navigate) { _close = close; _navigate = navigate; }
    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private bool _isOpen;
    private bool _demoMode;
    private UserGoal _demoGoal;
    public bool IsLaptop => WizardChoices.ShowLaptop(SystemProfile.Current.IsLoaded && SystemProfile.Current.HardwareFactsKnown, SystemProfile.Current.IsLaptop);
    public UserGoal Goal { get => _demoMode ? _demoGoal : AppSettings.Current.UserGoal; set { if (_demoMode) _demoGoal = value; else AppSettings.Current.UserGoal = value; OnPropertyChanged(); OnPropertyChanged(nameof(GoalName)); OnPropertyChanged(nameof(ShowGamesLink)); OnPropertyChanged(nameof(CanGoNext)); NextCommand.NotifyCanExecuteChanged(); } }
    public bool Expert { get => AppSettings.Current.ExpertMode; set { AppSettings.Current.ExpertMode = value; OnPropertyChanged(); } }
    public string GoalName => Goal switch { UserGoal.Games => Loc.T("Игры"), UserGoal.Work => Loc.T("Работа"), UserGoal.LaptopBattery => Loc.T("Ноутбук от батареи"), UserGoal.Quiet => Loc.T("Тихо и прохладно"), _ => Loc.T("Не выбрано") };
    public string StepTitle => Step switch { 1 => Loc.T("Для чего этот ПК?"), 2 => Loc.T("Как показывать?"), _ => Loc.T("Готово") };
    public bool CanGoNext => Step < 3 && (Step != 1 || Goal != UserGoal.Recommended);
    partial void OnStepChanged(int value) { OnPropertyChanged(nameof(StepTitle)); OnPropertyChanged(nameof(ShowGamesLink)); OnPropertyChanged(nameof(CanGoNext)); NextCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanGoNext))] private void Next() { if (CanGoNext) Step++; }
    [RelayCommand] private void Back() { if (Step > 1) Step--; }
    [RelayCommand] private void Skip() { AppSettings.Current.FirstRunWizardInProgress = false; AppSettings.Current.FirstRunCompleted = true; IsOpen = false; _close(); }
    [RelayCommand] private void Finish() { AppSettings.Current.FirstRunWizardInProgress = false; AppSettings.Current.FirstRunCompleted = true; IsOpen = false; _close(); _navigate("home"); }
    public bool ShowGamesLink => Step == 3 && Goal == UserGoal.Games;
    [RelayCommand] private void OpenGames() { AppSettings.Current.FirstRunWizardInProgress = false; AppSettings.Current.FirstRunCompleted = true; IsOpen = false; _close(); _navigate("games"); }
    public void Open() { if (!_demoMode) AppSettings.Current.FirstRunWizardInProgress = true; Step = 1; IsOpen = true; }
    public void OpenDemo() { _demoMode = true; _demoGoal = UserGoal.Games; OnPropertyChanged(nameof(Goal)); OnPropertyChanged(nameof(GoalName)); Open(); }
}
