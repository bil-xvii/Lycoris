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
        AppSettings.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ShowConsole))
                Ui.Post(() => ConsoleExpanded = AppSettings.Current.ShowConsole);
        };

        ChangeJournal.Instance.Changed += () => Ui.Post(RefreshJournalCount);
        RefreshJournalCount();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clock.Tick += async (_, _) =>
        {
            ClockText = DateTime.Now.ToString("HH:mm");
            await RefreshRestoreStatusAsync();
        };
        _clock.Start();

        _ = LoadSystemAsync();
    }

    private async Task LoadSystemAsync()
    {
        var sp = await SystemProfile.LoadAsync();
        SystemLine1 = sp.CpuName;
        SystemLine2 = sp.PrimaryGpu?.Name ?? "";
        SystemLine3 = $"{sp.RamText} RAM · {sp.OsName} {sp.OsDisplayVersion}".Trim();
        await RefreshRestoreStatusAsync();
    }

    public async Task RefreshRestoreStatusAsync()
    {
        try { RestoreEnabled = await Task.Run(RestorePointService.IsEnabled); }
        catch { }
    }

    /// <summary>Startup safety checks, run once after the window is shown (skipped in QA modes).</summary>
    public async Task RunStartupChecksAsync()
    {
        await Task.Delay(1200);

        // 1. Elevated with a different administrator account: HKCU settings would go to that account.
        if (Elevation.IsElevated && await Task.Run(Elevation.IsRunningAsDifferentUser))
        {
            Log.Warn("MONOLITH запущен от имени другой учётной записи администратора.");
            await Ui.AlertAsync("ДРУГАЯ УЧЁТНАЯ ЗАПИСЬ",
                "MONOLITH запущен от имени другого администратора, а не пользователя, который сейчас работает в Windows. " +
                "Пользовательские настройки (игровой режим Windows, мышь, реклама, автозапуск «для меня») применятся к той учётной записи. " +
                "Чтобы настроить свой профиль, войдите в Windows под учётной записью администратора и запустите MONOLITH оттуда.");
        }

        // 2. The journal folder was writable by non-admins: its contents were set aside, never replayed.
        if (ChangeJournal.Instance.QuarantinedFile is { } quarantined)
        {
            await Ui.AlertAsync("ЖУРНАЛ ОТЛОЖЕН",
                "Папка данных MONOLITH была доступна для записи обычным пользователям, поэтому старый журнал изменений нельзя считать надёжным. " +
                "Он сохранён отдельно и не будет выполняться; права на папку исправлены. Изменения, сделанные раньше, можно отменить точкой восстановления Windows.",
                [quarantined]);
        }

        // 2b. The journal could not be read or trusted: no changes are possible until a new one is started.
        if (ChangeJournal.Instance.UnavailableReason is { } unavailable && Elevation.IsElevated)
        {
            var start = await Ui.ConfirmAsync("ЖУРНАЛ НЕДОСТУПЕН",
                $"{unavailable} Пока журнал недоступен, MONOLITH ничего не меняет в системе — иначе изменения нельзя было бы отменить. " +
                "Можно начать новый журнал: старые файлы не удаляются, а сохраняются рядом с отметкой времени. " +
                "Изменения, записанные в старом журнале, из MONOLITH отменить будет нельзя — только точкой восстановления Windows.",
                confirmText: "НАЧАТЬ НОВЫЙ ЖУРНАЛ", danger: true);
            if (start)
            {
                if (await Task.Run(ChangeJournal.Instance.StartNewJournal))
                    Ui.Toast("Начат новый журнал изменений.", ToastKind.Success);
                else
                    await Ui.AlertAsync("НЕ УДАЛОСЬ НАЧАТЬ НОВЫЙ ЖУРНАЛ",
                        "Старые файлы журнала не удалось отложить. Подробности — в консоли MONOLITH.");
            }
        }

        // 3. Game Boost changes left active (e.g. MONOLITH or the PC crashed during a game).
        var stale = ChangeJournal.Instance.ActiveRecords.Where(r => r.Source == "gameboost").ToList();
        if (stale.Count > 0 && Elevation.IsElevated && !GameBoostState.IsActive)
        {
            var ok = await Ui.ConfirmAsync("НЕЗАВЕРШЁННЫЙ ИГРОВОЙ СЕАНС",
                "Прошлая игровая сессия не завершилась штатно, и её временные изменения всё ещё действуют. Вернуть всё как было?",
                stale.Select(r => $"{r.Title}: {r.NewValue} → {r.OldValue}"), confirmText: "ВЕРНУТЬ", cancelText: "ПОЗЖЕ");
            if (ok)
            {
                var result = await ChangeJournal.Instance.RevertAsync(stale);
                Ui.Toast(result.Success ? "Изменения игрового сеанса отменены." : string.Join("\n", result.Errors),
                    result.Success ? ToastKind.Success : ToastKind.Error);
            }
        }
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
