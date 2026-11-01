using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Apps;
using Monolith.Modules.Dashboard;
using Monolith.Modules.Devices;
using Monolith.Modules.Drivers;
using Monolith.Modules.Games;
using Monolith.Modules.Maintenance;
using Monolith.Modules.Optimize;
using Monolith.Modules.Restore;
using Monolith.Modules.Services;
using Monolith.Modules.Settings;
using Monolith.UI;

namespace Monolith.Shell;

public sealed partial class MainViewModel : ObservableObject, IUiHost
{
    /// <summary>Every top-level page (the self-test and snapshots walk this list).</summary>
    public ObservableCollection<PageViewModel> Pages { get; }
    /// <summary>Pages in the sidebar list: all but Settings, which opens from the gear in the sidebar footer.</summary>
    public IReadOnlyList<PageViewModel> NavPages { get; }
    /// <summary>The sidebar list grouped by <see cref="PageViewModel.NavGroup"/> (ОБЗОР · УСКОРЕНИЕ · ОБСЛУЖИВАНИЕ · ЗАЩИТА).</summary>
    public ICollectionView NavView { get; }
    public MaintenanceViewModel Maintenance { get; }
    public DevicesViewModel Devices { get; }
    public RestoreViewModel Restore { get; }
    public ObservableCollection<ToastItem> Toasts { get; } = new();
    public ObservableCollection<LogEntry> LogEntries => Log.Entries;

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private DialogViewModel? _dialog;
    [ObservableProperty] private bool _consoleExpanded;
    [ObservableProperty] private int _activeChanges;
    /// <summary>Title of the newest batch that still has changes to revert («ОТКАТИТЬ ПОСЛЕДНИЙ ПАКЕТ «…»»), or null.</summary>
    [ObservableProperty] private string? _lastBatchTitle;

    public bool HasLastBatch => LastBatchTitle is not null;
    public string LastBatchMenuText => LastBatchTitle is { } t ? $"ОТКАТИТЬ ПОСЛЕДНИЙ ПАКЕТ «{t}»" : "ОТКАТИТЬ ПОСЛЕДНИЙ ПАКЕТ — ПАКЕТОВ НЕТ";
    /// <summary>«ОТКАТ · 71» in the title bar.</summary>
    public string UndoButtonText => ActiveChanges > 0 ? $"ОТКАТ · {ActiveChanges}" : "ОТКАТ";

    partial void OnLastBatchTitleChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLastBatch));
        OnPropertyChanged(nameof(LastBatchMenuText));
    }

    partial void OnActiveChangesChanged(int value) => OnPropertyChanged(nameof(UndoButtonText));

    public bool IsElevated => Elevation.IsElevated;
    public bool IsReadOnly => !Elevation.IsElevated;
    public string Version { get; } = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    /// <summary>Sidebar groups in order; the key → group map is the single place that decides where a page lives.</summary>
    public const string GroupOverview = "ОБЗОР";
    public const string GroupBoost = "УСКОРЕНИЕ";
    public const string GroupMaintenance = "ОБСЛУЖИВАНИЕ";
    public const string GroupProtection = "ЗАЩИТА";

    /// <summary>Sidebar group of a page key (pure; tested).</summary>
    public static string NavGroupOf(string pageKey) => pageKey switch
    {
        "dashboard" => GroupOverview,
        "optimize" or "games" or "services" or "devices" => GroupBoost,
        "maintenance" or "drivers" or "apps" => GroupMaintenance,
        "restore" => GroupProtection,
        _ => "",
    };

    public MainViewModel()
    {
        Pages =
        [
            new DashboardViewModel(),
            new OptimizeViewModel(),
            new GamesViewModel(),
            new ServicesViewModel(),
            Maintenance = new MaintenanceViewModel(),
            new DriversViewModel(),
            Devices = new DevicesViewModel(),
            new AppsViewModel(),
            Restore = new RestoreViewModel(),
            new SettingsViewModel(),
        ];
        foreach (var p in Pages) p.NavGroup = NavGroupOf(p.Key);
        NavPages = Pages.Where(p => p.Key != SettingsKey).ToList();
        NavView = new ListCollectionView((System.Collections.IList)NavPages);
        NavView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PageViewModel.NavGroup)));

        _consoleExpanded = AppSettings.Current.ShowConsole;
        Ui.Host = this;
        // Device changes ask through the shell: the «keep or go back» countdown and yes/no questions during an undo.
        DeviceConfirm.Countdown = Ui.CountdownConfirmAsync;
        DeviceConfirm.Ask = (title, message, details) => Ui.ConfirmAsync(title, message, details, confirmText: "ВЕРНУТЬ", cancelText: "ОСТАВИТЬ");
        AppSettings.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ShowConsole))
                Ui.Post(() => ConsoleExpanded = AppSettings.Current.ShowConsole);
        };

        ChangeJournal.Instance.Changed += () => Ui.Post(RefreshJournalCount);
        RefreshJournalCount();
    }

    /// <summary>Startup safety checks, run once after the window is shown (skipped in QA modes).</summary>
    public async Task RunStartupChecksAsync()
    {
        await Task.Delay(1200);

        // 0. Display and input changes left unconfirmed by a crash (MONOLITH or the PC died during the countdown): an
        //    unconfirmed change is put back — after any running guard process finished its own job.
        if (Elevation.IsElevated && ChangeJournal.Instance is { IsUnavailable: false } journal && journal.PendingDisplayRecords().Count > 0)
        {
            try { await Task.Run(() => DisplaySafeApply.ReconcileAsync(journal)); }
            catch (Exception ex) { Log.Warn($"Незавершённые изменения экрана не проверены: {ex.Message}"); }
        }

        // 1. Elevated with a different administrator account: HKCU settings would go to that account.
        if (Elevation.IsElevated && await Task.Run(Elevation.IsRunningAsDifferentUser))
        {
            Log.Warn("MONOLITH запущен от имени другой учётной записи администратора.");
            await Ui.AlertAsync("ДРУГАЯ УЧЁТНАЯ ЗАПИСЬ",
                "MONOLITH запущен от имени другого администратора, а не пользователя, который сейчас работает в Windows. " +
                "Пользовательские настройки (игровой режим Windows, реклама, автозапуск «для меня») применятся к той учётной записи, " +
                "а настройки мыши, клавиатуры и микрофона в «Устройствах и сети» будут недоступны. " +
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
            // A plan chosen later (e.g. in Optimize) stays: the session's record only hands its original over to it.
            // Same layer-aware texts as the Games page.
            Func<ChangeRecord, ChangeRecord?> newer = r => ChangeJournal.Instance.NewerLayer(r);
            var lines = Monolith.Modules.Games.GameBoostService.StaleLines(stale, newer);
            var ok = await Ui.ConfirmAsync("НЕЗАВЕРШЁННЫЙ ИГРОВОЙ СЕАНС", Monolith.Modules.Games.GameBoostService.StalePrompt(stale, newer),
                lines, confirmText: Monolith.Modules.Games.GameBoostService.AllKept(stale, newer) ? "ЗАКРЫТЬ ЗАПИСЬ" : "ВЕРНУТЬ", cancelText: "ПОЗЖЕ");
            if (ok)
            {
                var result = await Task.Run(() => ChangeJournal.Instance.RevertAsync(stale));
                Ui.Toast(result.Success ? Monolith.Modules.Games.GameBoostService.StaleRevertedText(result) : string.Join("\n", result.Errors),
                    result.Success ? ToastKind.Success : ToastKind.Error);
            }
        }

        // 4. Drift: settings MONOLITH applied were changed afterwards (Windows feature update, another program).
        await CheckDriftAsync();

        // 5. The action the user clicked before the relaunch with admin rights: repeated once, through its own command.
        await ReplayIntentAsync();
    }

    /// <summary>One non-blocking toast when active records no longer match the live system; details on the Restore page.</summary>
    private async Task CheckDriftAsync()
    {
        try
        {
            var sp = await SystemProfile.LoadAsync();
            var build = DriftDetector.BuildText(sp.OsBuild, sp.OsUbr);
            var previous = AppSettings.Current.LastWindowsBuild;
            var accepted = AppSettings.Current.DriftAccepted.ToList();
            var drift = ChangeJournal.Instance.UnavailableReason is null
                ? await Task.Run(() => DriftDetector.DetectLive(accepted))
                : [];
            if (build.Length > 0 && build != previous) AppSettings.Current.LastWindowsBuild = build;
            if (drift.Count == 0) return;

            Log.Warn($"Изменено вне MONOLITH: {drift.Count} (сборка Windows {previous} → {build}).");
            Ui.Toast(DriftDetector.NoticeText(drift.Count, previous, build), ToastKind.Warning, "ИЗМЕНЕНО ВНЕ MONOLITH", navigateTo: "restore");
        }
        catch (Exception ex)
        {
            Log.Warn($"Проверка изменений вне MONOLITH не выполнена: {ex.Message}");
        }
    }

    // ───────────── Admin intent replay ─────────────

    /// <summary>The action to repeat once this elevated copy is up (set by App from «--replay-…»); null = nothing.</summary>
    public AdminIntent? PendingIntent { get; set; }

    /// <summary>
    /// Opens the intent's page, waits for its first load and runs «{Command}Command» there (or on the shell) exactly
    /// once. The command runs its own checks and confirmation — the replay only presses the button the user pressed.
    /// Nothing is replayed without admin rights, and a failure only tells the user to repeat the action.
    /// </summary>
    public async Task<bool> ReplayIntentAsync()
    {
        var intent = PendingIntent;
        PendingIntent = null;
        if (intent is null) return false;
        if (!Elevation.IsElevated)
        {
            Log.Warn($"Действие «{intent}» не повторено: нет прав администратора.");
            return false;
        }
        try
        {
            var page = Pages.FirstOrDefault(p => p.Key == ResolvePageKey(intent.PageKey));
            if (page is null)
            {
                Log.Warn($"Действие «{intent}» не повторено: страница не найдена.");
                return false;
            }
            NavigateTo(intent.PageKey);
            await page.EnsureLoadedAsync();
            // Let the page settle its lists (tab focus requests, collection rebuilds) before the command's CanExecute is read.
            await Task.Delay(250);
            var target = intent.FindCommand(page) is not null ? (object)page : this;
            if (intent.TryExecuteOn(target))
            {
                Log.Info($"Права получены — действие продолжено: {intent}.");
                return true;
            }
            Log.Warn($"Действие «{intent}» не повторено: команда недоступна.");
            Ui.Toast("Права администратора получены. Повторите действие — оно не выполнилось само.", ToastKind.Info, "ПРОДОЛЖИТЬ ВРУЧНУЮ");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Повтор действия после получения прав: {intent}", ex);
            Ui.Toast("Права администратора получены. Повторите действие вручную.", ToastKind.Warning);
            return false;
        }
    }

    private void RefreshJournalCount()
    {
        var journal = ChangeJournal.Instance;
        var active = journal.ActiveRecords;
        ActiveChanges = active.Count;
        var revertible = active.Where(ChangeJournal.IsBulkRevertible).Select(r => r.BatchId).ToHashSet();
        var last = journal.Batches.Where(b => revertible.Contains(b.Id)).OrderByDescending(b => b.TimestampUtc).FirstOrDefault();
        LastBatchTitle = last is null ? null : last.Title.Length > 0 ? last.Title : "Без названия";
        // The sidebar badge of «Восстановление»: the journal count is already known — no work just for the badge.
        Restore.BadgeText = ActiveChanges > 0 ? ActiveChanges.ToString() : null;
    }

    private const string SettingsKey = "settings";

    /// <summary>Sidebar selection: the current page when it is in the list, otherwise none (Settings).</summary>
    public PageViewModel? NavSelection
    {
        get => CurrentPage is { } p && NavPages.Contains(p) ? p : null;
        set { if (value is not null) CurrentPage = value; } // the list clears its selection for Settings: ignore that null
    }

    public bool IsSettingsOpen => CurrentPage?.Key == SettingsKey;

    partial void OnCurrentPageChanged(PageViewModel? oldValue, PageViewModel? newValue)
    {
        oldValue?.Deactivate();
        if (newValue is not null) _ = newValue.ActivateAsync();
        OnPropertyChanged(nameof(NavSelection));
        OnPropertyChanged(nameof(IsSettingsOpen));
    }

    partial void OnConsoleExpandedChanged(bool value) => AppSettings.Current.ShowConsole = value;

    /// <summary>
    /// Opens a page by key. Old keys keep working: "cleaner" / "tools" open the matching maintenance tab,
    /// "startup" opens the АВТОЗАПУСК tab of the services page.
    /// </summary>
    public void NavigateTo(string pageKey)
    {
        var target = ResolvePageKey(pageKey);
        if (MaintenanceViewModel.IsTabKey(pageKey)) Maintenance.SelectTab(pageKey);
        else if (pageKey == StartupAlias) Ui.RequestFocus("services", StartupFocusId);
        var page = Pages.FirstOrDefault(p => p.Key == target);
        if (page is not null) CurrentPage = page;
    }

    public const string StartupAlias = "startup";
    public const string StartupFocusId = "tab:startup";

    /// <summary>Maps a navigation key (including the old page keys) to the key of a top-level page.</summary>
    public static string ResolvePageKey(string pageKey) => pageKey switch
    {
        MaintenanceViewModel.CleanerTab or MaintenanceViewModel.ToolsTab => "maintenance",
        StartupAlias => "services",
        _ => pageKey,
    };

    [RelayCommand]
    private void Navigate(string? pageKey)
    {
        if (!string.IsNullOrEmpty(pageKey)) NavigateTo(pageKey);
    }

    [RelayCommand]
    private void ToggleConsole() => ConsoleExpanded = !ConsoleExpanded;

    [RelayCommand]
    private void ClearConsole() => Log.Entries.Clear();

    [RelayCommand]
    private void OpenToastTarget(ToastItem? item)
    {
        if (item?.NavigateTo is not { } key) return;
        Toasts.Remove(item);
        NavigateTo(key);
    }

    [RelayCommand]
    private void DismissToast(ToastItem? item)
    {
        if (item is not null) Toasts.Remove(item);
    }

    [RelayCommand]
    private void RestartAsAdmin() => RestartElevated();

    public bool RestartElevated(AdminIntent? intent = null)
    {
        var args = new List<string>();
        switch (CurrentPage)
        {
            case DevicesViewModel d: args.AddRange(["--page", d.Key, "--tab", d.SelectedTab]); break;
            case { } p: args.AddRange(["--page", p.Key]); break;
        }
        if (intent is not null) args.AddRange(intent.ToArgs());
        if (!Elevation.TryRelaunchElevated(args)) return false;
        Application.Current.Shutdown();
        return true;
    }

    /// <summary>The visible «ПОИСК Ctrl+K» button: the window opens the command palette.</summary>
    public event Action? SearchRequested;

    [RelayCommand]
    private void OpenSearch() => SearchRequested?.Invoke();

    [RelayCommand]
    private void OpenRestoreCenter() => NavigateTo("restore");

    /// <summary>«ОТКАТИТЬ ПОСЛЕДНИЙ ПАКЕТ» from the title bar: the Restore page's own command, with its confirmation.</summary>
    [RelayCommand]
    private async Task RevertLastBatchAsync()
    {
        NavigateTo("restore");
        try { await Restore.EnsureLoadedAsync(); }
        catch { return; } // reported by the page
        if (Restore.RevertLastBatchCommand.CanExecute(null)) await Restore.RevertLastBatchCommand.ExecuteAsync(null);
    }
}
