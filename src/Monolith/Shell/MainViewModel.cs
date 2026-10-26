using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Apps;
using Monolith.Modules.Cleaner;
using Monolith.Modules.Dashboard;
using Monolith.Modules.Drivers;
using Monolith.Modules.Games;
using Monolith.Modules.Optimize;
using Monolith.Modules.Restore;
using Monolith.Modules.Services;
using Monolith.Modules.Settings;
using Monolith.Modules.Startup;
using Monolith.Modules.Tools;
using Monolith.UI;

namespace Monolith.Shell;

public sealed partial class MainViewModel : ObservableObject, IUiHost
{
    private readonly DispatcherTimer _clock;

    public ObservableCollection<PageViewModel> Pages { get; }
    public ObservableCollection<ToastItem> Toasts { get; } = new();
    public ObservableCollection<LogEntry> LogEntries => Log.Entries;

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private DialogViewModel? _dialog;
    [ObservableProperty] private bool _consoleExpanded;
    [ObservableProperty] private string _clockText = DateTime.Now.ToString("HH:mm");
    [ObservableProperty] private int _activeChanges;
    [ObservableProperty] private string _systemLine1 = "";
    [ObservableProperty] private string _systemLine2 = "";
    [ObservableProperty] private string _systemLine3 = "";
    [ObservableProperty] private bool _restoreEnabled;

    public bool IsElevated => Elevation.IsElevated;
    public string ModeText => IsElevated ? "ADMIN" : "ПРОСМОТР";
    public string Version { get; } = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    public MainViewModel()
    {
        Pages =
        [
            new DashboardViewModel(),
            new OptimizeViewModel(),
            new GamesViewModel(),
            new CleanerViewModel(),
            new DriversViewModel(),
            new ServicesViewModel(),
            new StartupViewModel(),
            new AppsViewModel(),
            new ToolsViewModel(),
            new RestoreViewModel(),
            new SettingsViewModel(),
        ];

        _consoleExpanded = AppSettings.Current.ShowConsole;
        Ui.Host = this;

        ChangeJournal.Instance.Changed += () => Ui.Post(RefreshJournalCount);
        RefreshJournalCount();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clock.Tick += (_, _) => ClockText = DateTime.Now.ToString("HH:mm");
        _clock.Start();

        _ = LoadSystemAsync();
    }

    private async Task LoadSystemAsync()
    {
        var sp = await SystemProfile.LoadAsync();
        SystemLine1 = sp.CpuName;
        SystemLine2 = sp.PrimaryGpu?.Name ?? "";
        SystemLine3 = $"{sp.RamText} RAM · {sp.OsName} {sp.OsDisplayVersion}".Trim();
        RestoreEnabled = await Task.Run(RestorePointService.IsEnabled);
    }

    private void RefreshJournalCount() => ActiveChanges = ChangeJournal.Instance.ActiveRecords.Count;

    partial void OnCurrentPageChanged(PageViewModel? oldValue, PageViewModel? newValue)
    {
        oldValue?.Deactivate();
        if (newValue is not null) _ = newValue.ActivateAsync();
    }

    partial void OnConsoleExpandedChanged(bool value) => AppSettings.Current.ShowConsole = value;

    public void NavigateTo(string pageKey)
    {
        var page = Pages.FirstOrDefault(p => p.Key == pageKey);
        if (page is not null) CurrentPage = page;
    }

    [RelayCommand]
    private void ToggleConsole() => ConsoleExpanded = !ConsoleExpanded;

    [RelayCommand]
    private void ClearConsole() => Log.Entries.Clear();

    [RelayCommand]
    private void DismissToast(ToastItem? item)
    {
        if (item is not null) Toasts.Remove(item);
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (Elevation.TryRelaunchElevated())
            Application.Current.Shutdown();
    }

    [RelayCommand]
    private void OpenRestoreCenter() => NavigateTo("restore");
}
