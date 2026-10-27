using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Shell;
using Monolith.Tweaks;
using Monolith.UI;

namespace Monolith.Modules.Services;

/// <summary>
/// СЛУЖБЫ: curated recommendations (allowlist from research), the full service list (locks + <see cref="ServiceStartPolicy"/>:
/// Windows services outside the list are never disabled) and telemetry scheduled tasks. Every change goes through
/// <see cref="ChangeSession"/>: journaled + restore point. Running services are never stopped automatically.
/// </summary>
public sealed partial class ServicesViewModel : PageViewModel
{
    /// <summary>Journal source for service start-type changes.</summary>
    public const string ServiceSource = "services";
    /// <summary>Journal source for scheduled task changes.</summary>
    public const string TaskSource = "services-tasks";

    public override string Key => "services";
    public override string Index => "06";
    public override string Title => "СЛУЖБЫ";
    public override string Subtitle => "Службы и задачи Windows: честные рекомендации, полный список и откат любого изменения.";
    public override string Glyph => Glyphs.Services;

    private bool _journalHooked;

    public ServicesViewModel()
    {
        _selectedProfile = Enum.TryParse<ProfileSet>(AppSettings.Current.LastProfile, out var p) && p is ProfileSet.Normal or ProfileSet.Recommended or ProfileSet.Gaming
            ? p
            : ProfileSet.Recommended;
        _servicesView = CreateServicesView(_allServices);
    }

    /// <summary>0 = РЕКОМЕНДАЦИИ, 1 = ВСЕ СЛУЖБЫ, 2 = ЗАДАЧИ ПЛАНИРОВЩИКА.</summary>
    [ObservableProperty] private int _selectedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileDescription))]
    private ProfileSet _selectedProfile;

    public string ProfileDescription => ServiceCatalog.ProfileDescription(SelectedProfile);

    /// <summary>Domain / MDM / Insider / Bluetooth facts; null until the first scan.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManaged), nameof(CanModify), nameof(ManagedText))]
    private ServiceEnvironment? _machine;

    public bool IsManaged => Machine?.IsManaged == true;

    /// <summary>Changes are allowed: admin rights and not an organization-managed PC.</summary>
    public bool CanModify => IsElevated && !IsManaged;

    public string ManagedText => Machine switch
    {
        { IsDomainJoined: true } => "Компьютер входит в домен: групповые политики организации вернут изменения служб. Чтобы не спорить с администратором, раздел работает только для просмотра.",
        { IsMdmEnrolled: true } => "Компьютер управляется организацией через MDM (Intune): политики вернут изменения служб. Раздел работает только для просмотра.",
        _ => "",
    };

    /// <summary>Active journal records made by this page (services + tasks).</summary>
    [ObservableProperty] private int _activeChangeCount;

    protected override async Task LoadAsync()
    {
        if (!_journalHooked)
        {
            _journalHooked = true;
            ChangeJournal.Instance.Changed += () => Ui.Post(RefreshJournal);
        }
        await ReloadAsync();
    }

    /// <summary>Changes made elsewhere (Restore center, other pages) show up when the user comes back.</summary>
    protected override Task OnReturnAsync() => IsBusy ? Task.CompletedTask : RefreshStatesAsync();

    // ───────────────────────────── Loading ─────────────────────────────

    private Task ReloadAsync() => RunBusyAsync("Чтение служб и задач…", async () =>
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

        RefreshJournal();
        Log.Info($"Службы: найдено {AllServices.Count} служб, рекомендаций {RecommendationCount}, задач {TaskItems.Count}.");
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
                item.IsRecommended = item.RecommendedIn(SelectedProfile);
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
        ActiveChangeCount = journal.ActiveRecords.Count(r => r.Source is ServiceSource or TaskSource);
        UpdateRecommendationStats();
        NotifyCommands();
    }

    // ───────────────────────────── Shared change plumbing ─────────────────────────────

    /// <summary>
    /// Starts a journaled batch. Research §3.2: when the restore point could not be created the user must
    /// explicitly agree to continue without it.
    /// </summary>
    private async Task<ChangeSession?> BeginSessionAsync(string title)
    {
        if (!AppSettings.Current.CreateRestorePoints)
            return await ChangeSession.BeginAsync(title, createRestorePoint: false);

        BusyText = "Создание точки восстановления…";
        var rp = await RestorePointService.CreateAsync($"MONOLITH: {title}");
        if (!rp.Created)
        {
            var go = await Ui.ConfirmAsync("ПРОДОЛЖИТЬ БЕЗ ТОЧКИ ВОССТАНОВЛЕНИЯ?",
                $"{rp.Message} Исходные значения всё равно сохраняются в журнале MONOLITH, и каждое изменение можно откатить из приложения.",
                confirmText: "ПРОДОЛЖИТЬ", danger: true);
            if (!go)
            {
                Log.Info($"{title}: отменено — точка восстановления не создана.");
                return null;
            }
        }

        var session = await ChangeSession.BeginAsync(title, createRestorePoint: false);
        session.Batch.RestorePoint = rp.Message;
        ChangeJournal.Instance.UpdateBatch(session.Batch);
        BusyText = title;
        return session;
    }

    /// <summary>Restore-point sentence for a confirmation, from the setting and the live System Protection state (registry read).</summary>
    private static string RestorePointLine(string journaled) =>
        ServiceText.RestorePointLine(AppSettings.Current.CreateRestorePoints, RestorePointService.IsEnabled(), journaled);

    /// <summary>
    /// The service or task a journal record points to is gone (removed by a Windows update or an uninstaller):
    /// there is nothing to put back, and the undo would only fail.
    /// </summary>
    private static bool TargetVanished(ChangeRecord r) => r.Kind switch
    {
        ChangeKind.Service when r.Service is not null => ServiceControl.Query(r.Service.Name) is null,
        ChangeKind.ScheduledTask when r.TaskPath is not null => !ScheduledTasks.Query(r.TaskPath).Exists,
        _ => false,
    };

    /// <summary>Real ways out for a record whose service or task no longer exists (journal records are never deleted).</summary>
    private static IReadOnlyList<string> VanishedHelp(string what) =>
    [
        "Запись об изменении остаётся в разделе «Восстановление» — MONOLITH не удаляет записи журнала.",
        $"Если {what} появится снова (например, после переустановки программы), исходное значение можно будет вернуть кнопкой ОТКАТИТЬ здесь или в разделе «Восстановление».",
    ];

    private static string BatchTitle(string prefix, IReadOnlyList<string> names) => names.Count switch
    {
        0 => prefix,
        1 => $"{prefix}: {names[0]}",
        2 => $"{prefix}: {names[0]}, {names[1]}",
        _ => $"{prefix}: {names[0]}, {names[1]} и ещё {names.Count - 2}",
    };

    private static async Task ReportSessionAsync(ChangeSession session, string successText)
    {
        if (session.Errors.Count == 0)
        {
            Ui.Toast(successText, ToastKind.Success, "ГОТОВО");
            return;
        }
        var title = session.Applied > 0 ? "ПРИМЕНЕНО ЧАСТИЧНО" : "НЕ ПРИМЕНЕНО";
        await Ui.AlertAsync(title, $"Изменено: {session.Applied}, ошибок: {session.Errors.Count}. Подробности — в консоли и журнале.", session.Errors);
    }

    private static async Task ReportRevertAsync(RevertResult result)
    {
        if (result.Success)
        {
            Ui.Toast(result.Reverted == 1 ? "Исходное значение восстановлено." : $"Восстановлено исходных значений: {result.Reverted}.", ToastKind.Success, "ОТКАТ");
            return;
        }
        await Ui.AlertAsync("ОТКАТ ВЫПОЛНЕН НЕ ПОЛНОСТЬЮ",
            $"Восстановлено: {result.Reverted}, ошибок: {result.Failed}. Записи с ошибками остались активными в разделе «Восстановление» — " +
            "откат можно повторить там или кнопкой ОТКАТИТЬ на этой странице.", result.Errors);
    }

    // ───────────────────────────── Revert all ─────────────────────────────

    private bool CanRevertAll() => !IsBusy && CanModify && ActiveChangeCount > 0;

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private async Task RevertAllAsync()
    {
        if (!RequireAdmin("откат изменений служб")) return;
        var all = ChangeJournal.Instance.ActiveRecords.Where(r => r.Source is ServiceSource or TaskSource).ToList();
        if (all.Count == 0) return;

        // A service or task that no longer exists has nothing to put back — its undo would only fail.
        var vanished = await Task.Run(() => all.Where(TargetVanished).ToList());
        var records = all.Except(vanished).ToList();
        var gone = vanished.Select(r => $"{r.Title}: больше нет в системе — пропускается").ToList();
        if (records.Count == 0)
        {
            await Ui.AlertAsync("ВОЗВРАЩАТЬ НЕЧЕГО",
                "Служб и задач из журнала этой страницы больше нет в системе (их удалило обновление Windows или программа), поэтому возвращать нечего.",
                gone.Concat(VanishedHelp("служба или задача")));
            return;
        }

        var details = records.Select(r => $"{r.Title}: {r.NewValue} → {r.OldValue}").Concat(gone).ToList();
        if (!await Ui.ConfirmAsync("ОТКАТИТЬ ВСЕ ИЗМЕНЕНИЯ СЛУЖБ?",
                "Службы и задачи, изменённые на этой странице, вернутся к исходным значениям из журнала. Работающие службы не останавливаются.",
                details, "ОТКАТИТЬ ВСЁ",
                footnote: vanished.Count == 0
                    ? "Изменения из других разделов не затрагиваются."
                    : "Изменения из других разделов не затрагиваются. Записи об исчезнувших службах и задачах остаются в разделе «Восстановление»."))
            return;

        await RunBusyAsync("Откат изменений служб…", async () =>
        {
            var result = await Task.Run(() => ChangeJournal.Instance.RevertAsync(records));
            await ReportRevertAsync(result);
            await RefreshStatesAsync();
        });
    }

    // ───────────────────────────── Refresh ─────────────────────────────

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => ReloadAsync();

    private bool CanRowAction(string? key) => !IsBusy && CanModify && !string.IsNullOrEmpty(key);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsBusy) or nameof(Machine)) NotifyCommands();
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        RevertAllCommand.NotifyCanExecuteChanged();
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
