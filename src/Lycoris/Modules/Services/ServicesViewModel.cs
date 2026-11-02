using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Modules.Startup;
using Lycoris.Shell;
using Lycoris.Tweaks;
using Lycoris.UI;

namespace Lycoris.Modules.Services;

/// <summary>
/// АВТОЗАПУСК И СЛУЖБЫ: autostart (hosted <see cref="StartupViewModel"/>), curated service recommendations
/// (allowlist from research), the full service list (expert mode with locks) and scheduled tasks (Microsoft
/// data-collection tasks + third-party logon tasks from Startup). Every change goes through <see cref="ChangeSession"/>:
/// journaled + restore point. Running services are never stopped automatically. Bulk revert lives only in Restore.
/// </summary>
public sealed partial class ServicesViewModel : PageViewModel
{
    /// <summary>Journal source for service start-type changes.</summary>
    public const string ServiceSource = "services";
    /// <summary>Journal source for scheduled task changes.</summary>
    public const string TaskSource = "services-tasks";

    public override string Key => "services";
    public override string Index => "06";
    public override string Title => Loc.T("АВТОЗАПУСК И СЛУЖБЫ");
    public override string Subtitle => Loc.T("Что запускается вместе с Windows: программы, службы и задачи.");
    public override string Glyph => Glyphs.Services;

    public const int TabStartup = 0;
    public const int TabRecommendations = 1;
    public const int TabAll = 2;
    public const int TabTasks = 3;

    private bool _journalHooked;

    public ServicesViewModel()
    {
        // The services profile follows the Optimize scenario (LastProfile holds an OptimizeProfile name; unknown → Recommended).
        _selectedProfile = ProfileFromScenario(AppSettings.Current.LastProfile);
        _servicesView = CreateServicesView(_allServices);

        Startup = new StartupViewModel { IsHostBusy = () => IsBusy && !_mirroringStartup };
        Startup.PropertyChanged += OnStartupPropertyChanged;
        AppSettings.Current.PropertyChanged += OnSettingsChanged;
        Ui.FocusRequested += r =>
        {
            if (r.PageKey == Key) ApplyFocus(Ui.TakeFocusRequest(Key));
        };
    }

    /// <summary>The АВТОЗАПУСК tab (its logon tasks are shown on the ЗАДАЧИ tab).</summary>
    public StartupViewModel Startup { get; }

    /// <summary><see cref="TabStartup"/>, <see cref="TabRecommendations"/>, <see cref="TabAll"/> or <see cref="TabTasks"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProfilePicker))]
    private int _selectedTab;

    /// <summary>The one profile picker drives both recommendations and Microsoft tasks.</summary>
    public bool ShowProfilePicker => SelectedTab is TabRecommendations or TabTasks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileDescription))]
    private ProfileSet _selectedProfile;

    public string ProfileDescription => ServiceCatalog.ProfileDescription(SelectedProfile);

    /// <summary>Domain / MDM / Insider / Bluetooth facts; null until the first scan.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManaged), nameof(CanModify), nameof(ManagedText))]
    private ServiceEnvironment? _machine;

    public bool IsManaged => Machine?.IsManaged == true;

    /// <summary>
    /// Changes are offered: not an organization-managed PC. Read-only mode does NOT disable anything — a click asks for
    /// admin rights (<c>RequireAdmin</c>) and the relaunched copy repeats the action.
    /// </summary>
    public bool CanModify => !IsManaged;

    public string ManagedText => Machine switch
    {
        { IsDomainJoined: true } => Loc.T("Компьютер входит в домен: групповые политики организации вернут изменения служб. Чтобы не спорить с администратором, службы и задачи Microsoft здесь только для просмотра; автозапуск программ менять можно."),
        { IsMdmEnrolled: true } => Loc.T("Компьютер управляется организацией через MDM (Intune): политики вернут изменения служб. Службы и задачи Microsoft здесь только для просмотра; автозапуск программ менять можно."),
        _ => "",
    };

    protected override async Task LoadAsync()
    {
        if (!_journalHooked)
        {
            _journalHooked = true;
            ChangeJournal.Instance.Changed += () => Ui.Post(RefreshJournal);
        }
        // Start the service scan first: it marks the page busy before the autostart scan (and a focused tab) begins.
        var services = ReloadAsync();
        ApplyFocus(Ui.TakeFocusRequest(Key));
        // The autostart scan always runs on the first visit (read-only and quick): the АВТОЗАПУСК and ЗАДАЧИ tab
        // badges count its results even when the page opens on another tab.
        await Task.WhenAll(services, EnsureStartupAsync());
    }

    /// <summary>Changes made elsewhere (Restore center, other pages) show up when the user comes back.</summary>
    protected override async Task OnReturnAsync()
    {
        ApplyFocus(Ui.TakeFocusRequest(Key));
        if (StartupTabShown) await EnsureStartupAsync();
        if (IsBusy) return;
        // Expert mode was toggled elsewhere: re-apply the locks to the last scan (no rescan needed).
        if (_builtExpertMode != AppSettings.Current.ServicesExpertMode) BuildAllServices(_scannedServices);
        await RefreshStatesAsync();
    }

    public override void Deactivate()
    {
        Startup.Leave();
        _startupStale = true;
    }

    // ───────────────────────────── Hosted АВТОЗАПУСК ─────────────────────────────

    private bool _startupShown;
    private bool _startupStale;
    private bool _mirroringStartup;
    private Task? _startupTask;

    private bool StartupTabShown => SelectedTab is TabStartup or TabTasks;

    /// <summary>Loads the autostart scan on first show, rescans when the page is shown again.</summary>
    private Task EnsureStartupAsync()
    {
        // A scan already running (e.g. started by a tab switch a moment ago) is awaited, not skipped.
        if (_startupTask is { IsCompleted: false } running) return running;
        if (_startupShown && !_startupStale) return Task.CompletedTask;
        _startupShown = true;
        _startupStale = false;
        return _startupTask = Startup.ActivateAsync();
    }

    partial void OnSelectedTabChanged(int value)
    {
        RaisePrimary();
        if (StartupTabShown) _ = EnsureStartupAsync();
    }

    private void OnStartupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy) or nameof(BusyText)) SyncStartupBusy();
        else if (e.PropertyName == nameof(OperationNote)) OperationNote = Startup.OperationNote;
        else if (e.PropertyName == nameof(StartupViewModel.TaskCount))
        {
            OnPropertyChanged(nameof(AllTaskCount));
            OnPropertyChanged(nameof(StartupTabCount));
        }
        else if (e.PropertyName == nameof(StartupViewModel.TotalCount)) OnPropertyChanged(nameof(StartupTabCount));
        else if (e.PropertyName == nameof(BadgeText)) BadgeText = Startup.BadgeText;
        else if (e.PropertyName == nameof(CheckedAt)) CheckedAt = Startup.CheckedAt;
        else if (e.PropertyName == nameof(StartupViewModel.ExtraCount)) RaisePrimary();
    }

    // ───────────────────────────── Shortcut targets (Dashboard tiles, admin-intent replay) ─────────────────────────────
    // The replay looks for «{Command}Command» on this top-level page; the logic stays in the hosted pages.

    /// <summary>One autostart switch by its entry key («ToggleStartup»).</summary>
    public ICommand ToggleStartupCommand => Startup.ToggleKeyCommand;
    /// <summary>One logon-task switch by its task path («ToggleLogonTask»).</summary>
    public ICommand ToggleLogonTaskCommand => Startup.ToggleTaskKeyCommand;
    /// <summary>The checked autostart entries off («DisableSelectedStartup»; the parameter is an <see cref="IntentList"/>).</summary>
    public ICommand DisableSelectedStartupCommand => Startup.DisableSelectedCommand;
    /// <summary>The checked autostart entries on («EnableSelectedStartup»).</summary>
    public ICommand EnableSelectedStartupCommand => Startup.EnableSelectedCommand;
    /// <summary>«ОТКЛЮЧИТЬ ЛИШНЕЕ · N»: entries slowing boot or pointing to missing files, after a confirmation with the list.</summary>
    public ICommand DisableExtraStartupCommand => Startup.DisableExtraCommand;

    /// <summary>Autostart tab badge: entries plus third-party logon tasks (they live on that tab now).</summary>
    public int StartupTabCount => Startup.TotalCount + Startup.TaskCount;

    // ───────────────────────────── Header primary (changes with the tab) ─────────────────────────────

    /// <summary>The header's one main button: on АВТОЗАПУСК «ОТКЛЮЧИТЬ ЛИШНЕЕ · N» (hidden when there is nothing extra).</summary>
    public string PrimaryText => Loc.Tf("ОТКЛЮЧИТЬ ЛИШНЕЕ · {0}", Startup.ExtraCount);
    public bool HasPrimary => SelectedTab == TabStartup && Startup.ExtraCount > 0;

    private void RaisePrimary()
    {
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(HasPrimary));
    }

    /// <summary>
    /// The shell shows the current page's busy state: mirror the hosted page's work into ours (it also blocks this
    /// page's commands meanwhile, and <see cref="StartupViewModel.IsHostBusy"/> blocks the other direction).
    /// </summary>
    private void SyncStartupBusy()
    {
        if (Startup.IsBusy)
        {
            if (!IsBusy)
            {
                _mirroringStartup = true;
                IsBusy = true;
            }
            if (_mirroringStartup) BusyText = Startup.BusyText;
        }
        else if (_mirroringStartup)
        {
            _mirroringStartup = false;
            BusyText = null;
            IsBusy = false;
        }
    }

    // ───────────────────────────── Focus (navigation / command palette) ─────────────────────────────

    /// <summary>A service name requested before the list was read; applied after the scan.</summary>
    private string? _pendingServiceFocus;

    /// <summary>Set (null → item) to make the view scroll ВСЕ СЛУЖБЫ to this service.</summary>
    [ObservableProperty] private ServiceItem? _focusedService;

    /// <summary>"tab:startup" / "tab:services" / "tab:all" / "tab:tasks" switch tabs; anything else is a service name.</summary>
    private void ApplyFocus(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var tab = FocusTab(id);
        if (tab is not null)
        {
            SelectedTab = tab.Value;
            return;
        }
        // «startup:<entry key>» from the search: the АВТОЗАПУСК tab, filtered to that entry (when the scan knows it).
        if (id.StartsWith(CommandPaletteIndex.StartupFocusPrefix, StringComparison.Ordinal))
        {
            SelectedTab = TabStartup;
            var key = id[CommandPaletteIndex.StartupFocusPrefix.Length..];
            if (Startup.Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)) is { } entry)
                Startup.Search = entry.DisplayName;
            return;
        }

        SelectedTab = TabAll;
        var item = AllServices.FirstOrDefault(s => string.Equals(s.Name, id, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            // Not scanned yet: remember it. After a scan an unknown name is ignored.
            _pendingServiceFocus = AllServices.Count == 0 ? id : null;
            return;
        }
        _pendingServiceFocus = null;
        if (!ServiceFilter.Matches(item.Facts, Filter, SearchText))
        {
            SearchText = "";
            Filter = ServiceFilterKind.All;
        }
        SelectedService = item;
        FocusedService = null;
        FocusedService = item;
    }

    /// <summary>Tab for a "tab:…" focus id; null = the id is a service name.</summary>
    public static int? FocusTab(string id) => id switch
    {
        "tab:startup" => TabStartup,
        "tab:services" => TabRecommendations,
        "tab:all" => TabAll,
        "tab:tasks" => TabTasks,
        _ => null,
    };

    // ───────────────────────────── Expert mode (ВСЕ СЛУЖБЫ toolbar) ─────────────────────────────

    private bool _confirmingExpertMode;

    /// <summary>
    /// Expert mode of ВСЕ СЛУЖБЫ (services.md §0.4): turning it on asks first (danger); turning it off is immediate.
    /// </summary>
    public bool ServicesExpertMode
    {
        get => AppSettings.Current.ServicesExpertMode;
        set
        {
            if (value == AppSettings.Current.ServicesExpertMode) return;
            if (!value)
            {
                AppSettings.Current.ServicesExpertMode = false;
                Log.Info("Экспертный режим служб выключен: службы вне списка Lycoris снова только для просмотра.");
                return;
            }
            // Keep the toggle off until the user confirms (re-raised after the binding finishes its update).
            Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                OnPropertyChanged(nameof(ServicesExpertMode));
                await ConfirmEnableExpertModeAsync();
            });
        }
    }

    private async Task ConfirmEnableExpertModeAsync()
    {
        if (_confirmingExpertMode) return;
        _confirmingExpertMode = true;
        try
        {
            var ok = await Ui.ConfirmAsync(
                "Включить экспертный режим служб?",
                "На вкладке «Все службы» станет можно менять тип запуска, запускать и останавливать службы, которых нет в проверенном списке Lycoris.",
                [
                    Loc.T("Эти службы Lycoris не проверял: отключение может сломать Wi-Fi с корпоративным входом (802.1X), режим «в самолёте», синхронизацию времени, VPN, резервное копирование и программы сторонних производителей"),
                    Loc.T("Критичные службы Windows, драйверы, защищённые и пользовательские службы остаются заблокированными и в этом режиме"),
                    Loc.T("Каждое изменение типа запуска по-прежнему записывается в журнал и откатывается из «Восстановления»"),
                    Loc.T("Выключить режим можно здесь же в любой момент — уже сделанные изменения при этом не откатываются"),
                ],
                confirmText: "ВКЛЮЧИТЬ", danger: true);
            if (ok)
            {
                AppSettings.Current.ServicesExpertMode = true;
                Log.Warn("Экспертный режим служб включён: службы вне списка Lycoris можно менять на свой риск.");
            }
        }
        finally
        {
            _confirmingExpertMode = false;
            OnPropertyChanged(nameof(ServicesExpertMode));
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.ServicesExpertMode)) return;
        Ui.Post(() =>
        {
            if (!_confirmingExpertMode) OnPropertyChanged(nameof(ServicesExpertMode));
            // Re-apply the locks to the last scan (no rescan needed).
            if (_scannedServices.Count > 0 && _builtExpertMode != AppSettings.Current.ServicesExpertMode)
                BuildAllServices(_scannedServices);
        });
    }

    /// <summary>«→ ОПТИМИЗАЦИЯ» on a service that only Optimize changes (DiagTrack).</summary>
    [RelayCommand]
    private void OpenOptimizeTweak(string? tweakId)
    {
        if (!string.IsNullOrEmpty(tweakId)) Ui.RequestFocus("optimize", tweakId);
        Ui.Navigate("optimize");
    }

    // ───────────────────────────── Loading ─────────────────────────────

    private Task ReloadAsync() => RunBusyAsync(OperationClass.Measure, Loc.T("Чтение служб и задач…"), async () =>
    {
        var elevated = Elevation.IsElevated;
        var machineTask = Task.Run(ServiceScanner.ReadEnvironment);
        var curatedTask = Task.Run(() => ServiceCatalog.Services.ToDictionary(s => s.Name, s => ServiceScanner.ReadLiveState(s.Name), StringComparer.OrdinalIgnoreCase));
        var tasksTask = Task.Run(() => TaskScanner.Read(ServiceCatalog.Tasks));
        var allTask = Task.Run(() => ServiceScanner.ScanAll(elevated));

        IsServicesLoading = true;
        try
        {
            Machine = await machineTask;
            BuildRecommendations(await curatedTask);
            BuildTasks(await tasksTask);
            BuildAllServices(await allTask);
        }
        finally
        {
            IsServicesLoading = false;
        }
        if (_pendingServiceFocus is { } focus) ApplyFocus(focus);

        RefreshJournal();
        MarkChecked();
        Log.Info("Службы: найдено {0} служб, рекомендаций {1}, задач {2}.", AllServices.Count, RecommendationCount, TaskItems.Count);
        if (Machine?.IsManaged == true) Log.Warn("Службы: ПК управляется организацией — раздел только для просмотра.");
    });

    /// <summary>Cheap refresh of start types, states and task flags (after changes or when the page is shown again).</summary>
    private async Task RefreshStatesAsync()
    {
        var names = _recByName.Keys.Concat(AllServices.Select(s => s.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var taskPaths = TaskItems.Select(t => t.Path).ToList();
        if (names.Count == 0 && taskPaths.Count == 0) return;

        var (states, tasks) = await Task.Run(() =>
        {
            var s = ServiceScanner.ReadLiveStates(names);
            var t = taskPaths.Select(ScheduledTasks.Query).ToList();
            return (s, t);
        });

        _bulkSelect = _bulkTaskSelect = true;
        try
        {
            foreach (var item in _recByName.Values)
            {
                if (states.TryGetValue(item.Name, out var st) && st.Exists)
                    item.State = st with { DisplayName = st.DisplayName ?? item.State?.DisplayName };
                if (!item.IsSelectable) item.IsSelected = false;
            }

            foreach (var info in tasks)
            {
                var item = TaskItems.FirstOrDefault(t => string.Equals(t.Path, info.Path, StringComparison.OrdinalIgnoreCase));
                if (item is null || !info.Exists) continue;
                item.Enabled = info.Enabled;
                if (!item.IsSelectable) item.IsSelected = false;
            }
        }
        finally
        {
            _bulkSelect = _bulkTaskSelect = false;
        }

        foreach (var item in AllServices)
            if (states.TryGetValue(item.Name, out var st))
                item.Apply(st);

        RefreshJournal();
        UpdateRecommendationStats();
        UpdateTaskStats();
        UpdateFilterCounts();
        ServicesView.Refresh();
        UpdateShownCount();
    }

    /// <summary>Re-reads which rows have an active (not reverted) journal record.</summary>
    private void RefreshJournal()
    {
        var journal = ChangeJournal.Instance;
        foreach (var item in _recByName.Values) item.Journal = journal.FindActive("svc:" + item.Name);
        foreach (var item in AllServices) item.Journal = journal.FindActive("svc:" + item.Name);
        foreach (var item in TaskItems) item.Journal = journal.FindActive("task:" + item.Path);
        UpdateRecommendationStats();
        NotifyCommands();
    }

    // ───────────────────────────── Shared change plumbing ─────────────────────────────

    /// <summary>
    /// Starts a journaled batch. Research §3.2: when the restore point could not be created the user must
    /// explicitly agree to continue without it.
    /// </summary>
    private async Task<ChangeSession?> BeginSessionAsync(string title, string? shownTitle = null)
    {
        if (!AppSettings.Current.CreateRestorePoints)
            return await ChangeSession.BeginAsync(title, createRestorePoint: false);

        BusyText = Loc.T("Создание точки восстановления…");
        var rp = await RestorePointService.CreateAsync($"Lycoris: {title}");
        if (!rp.Created)
        {
            var go = await Ui.ConfirmAsync("ПРОДОЛЖИТЬ БЕЗ ТОЧКИ ВОССТАНОВЛЕНИЯ?",
                Loc.Tf("{0} Исходные значения всё равно сохраняются в журнале Lycoris, и каждое изменение можно откатить из приложения.", rp.Message),
                confirmText: "ПРОДОЛЖИТЬ", danger: true);
            if (!go)
            {
                Log.Info("{0}: отменено — точка восстановления не создана.", shownTitle ?? title);
                return null;
            }
        }

        var session = await ChangeSession.BeginAsync(title, createRestorePoint: false);
        session.Batch.RestorePoint = rp.Message;
        ChangeJournal.Instance.UpdateBatch(session.Batch);
        BusyText = shownTitle ?? title;
        return session;
    }

    private static string BatchTitle(string prefix, IReadOnlyList<string> names, bool localized = false) => names.Count switch
    {
        0 => prefix,
        1 => $"{prefix}: {names[0]}",
        2 => $"{prefix}: {names[0]}, {names[1]}",
        _ => localized
            ? Loc.Tf("{0}: {1}, {2} и ещё {3}", prefix, names[0], names[1], names.Count - 2)
            : string.Format(Loc.Keep("{0}: {1}, {2} и ещё {3}"), prefix, names[0], names[1], names.Count - 2),
    };

    private static async Task ReportSessionAsync(ChangeSession session, string successText)
    {
        if (session.Errors.Count == 0)
        {
            Ui.Toast(successText, ToastKind.Success, "ГОТОВО");
            return;
        }
        var title = session.Applied > 0 ? Loc.T("ПРИМЕНЕНО ЧАСТИЧНО") : Loc.T("НЕ ПРИМЕНЕНО");
        await Ui.AlertAsync(title, Loc.Tf("Изменено: {0}, ошибок: {1}. Подробности — в консоли и журнале.", session.Applied, session.Errors.Count), session.Errors);
    }

    private static async Task ReportRevertAsync(RevertResult result)
    {
        // Hand-overs are not values returned: count only what changed back, and say what was left and why.
        var left = await Task.Run(() => Lycoris.Modules.Restore.RestoreFormat.LeftText(result.Left, UserScope.CurrentSid, UserScope.AccountName));
        var counts = Lycoris.Modules.Restore.RestoreFormat.RevertCounts(result);
        if (result.Success && string.IsNullOrEmpty(left))
        {
            Ui.Toast(counts, ToastKind.Success, "ОТКАТ");
            return;
        }
        var lines = result.Errors.ToList();
        if (!string.IsNullOrEmpty(left)) lines.Insert(0, left);
        await Ui.AlertAsync("ОТКАТ ВЫПОЛНЕН НЕ ПОЛНОСТЬЮ", Loc.Tf("{0} Ошибок: {1}. Записи, которые не откатились, остались в «Восстановлении».", counts, result.Failed), lines);
    }

    // ───────────────────────────── Refresh ─────────────────────────────

    private bool CanRefresh() => !IsBusy;

    /// <summary>ОБНОВИТЬ: re-reads services and tasks, and autostart once it has been shown.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        // Sequential: while the hosted scan runs this page is busy (mirrored) and would skip its own reload.
        if (_startupShown)
        {
            _startupStale = false;
            await Startup.RefreshCommand.ExecuteAsync(null);
        }
        await ReloadAsync();
    }

    private bool CanRowAction(string? key) => !IsBusy && CanModify && !string.IsNullOrEmpty(key);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsBusy) or nameof(Machine)) NotifyCommands();
        // Our own work ended while the hosted scan still runs: keep the shell busy (after RunBusyAsync clears the text).
        if (e.PropertyName == nameof(IsBusy) && !IsBusy && !_mirroringStartup && Startup.IsBusy)
            Application.Current?.Dispatcher.BeginInvoke(SyncStartupBusy);
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyRecommendationsCommand.NotifyCanExecuteChanged();
        ClearRecommendationSelectionCommand.NotifyCanExecuteChanged();
        RevertServiceCommand.NotifyCanExecuteChanged();
        ApplyStartTypeCommand.NotifyCanExecuteChanged();
        ResetStartTypeCommand.NotifyCanExecuteChanged();
        StartServiceCommand.NotifyCanExecuteChanged();
        StopServiceCommand.NotifyCanExecuteChanged();
        ApplyTasksCommand.NotifyCanExecuteChanged();
        ClearTaskSelectionCommand.NotifyCanExecuteChanged();
        RevertTaskCommand.NotifyCanExecuteChanged();
    }

    partial void OnMachineChanged(ServiceEnvironment? value)
    {
        var can = CanModify;
        foreach (var i in _recByName.Values) i.CanModify = can;
        foreach (var i in AllServices) i.CanModify = can;
        foreach (var i in TaskItems) i.CanModify = can;
    }

    partial void OnSelectedProfileChanged(ProfileSet value)
    {
        ApplyProfileToRecommendations();
        ApplyProfileToTasks();
    }
}
