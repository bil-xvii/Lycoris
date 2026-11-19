using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Modules.Games.Sources;
using Lycoris.Modules.Startup;
using Lycoris.Shell;
using Lycoris.UI;

namespace Lycoris.Modules.Removal;

public sealed record RemovalFilter(RemovalDecision? Decision, string Title)
{
    public override string ToString() => Title;
}

public sealed partial class RemovalViewModel : PageViewModel
{
    public override string Key => "removal";
    public override string Index => "13";
    public override string Title => Loc.T("УДАЛЕНИЕ И ОТКЛЮЧЕНИЕ");
    public override string Subtitle => Loc.T("Проверьте, какие программы стоит оставить и какие записи автозапуска можно отключить.");
    public override string Glyph => Glyphs.Delete;

    private static readonly RemovalFilter All = new(null, Loc.T("ВСЕ"));
    private static readonly IReadOnlyList<RemovalFilter> ProgramFilters =
    [
        All, Choice(RemovalDecision.ConsiderRemoval), Choice(RemovalDecision.Keep), Choice(RemovalDecision.Review),
    ];
    private static readonly IReadOnlyList<RemovalFilter> StartupFilters =
    [
        All, Choice(RemovalDecision.DisableStartup), Choice(RemovalDecision.Keep),
        Choice(RemovalDecision.Review), Choice(RemovalDecision.AlreadyDisabled),
    ];

    private UninstallInventory? _programInventory;
    private UninstallReadState? _lastProgramRead;
    private IReadOnlyList<RemovalRow> _programRows = [];
    private IReadOnlyList<RemovalRow> _startupRows = [];
    private DateTime? _startupCheckedAt;
    private bool _programsCurrent;
    private bool _startupCurrent;

    [ObservableProperty] private int _tabIndex;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private RemovalFilter? _selectedFilter = All;
    [ObservableProperty] private IReadOnlyList<RemovalRow> _visibleRows = [];
    [ObservableProperty] private RemovalRow? _selectedRow;

    public RemovalViewModel()
    {
        PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(IsBusy)) RefreshCommands();
        };
    }

    public bool IsStartupTab => TabIndex == 1;
    public IReadOnlyList<RemovalFilter> Filters => IsStartupTab ? StartupFilters : ProgramFilters;
    public bool ShowProgramAction => !IsStartupTab && SelectedRow?.Decision is RemovalDecision.ConsiderRemoval or RemovalDecision.Review;
    public bool ShowStartupAction => IsStartupTab && SelectedRow?.Decision is RemovalDecision.DisableStartup or RemovalDecision.AlreadyDisabled;
    public bool HasSelection => SelectedRow is not null;

    public string InventoryMessage => IsStartupTab
        ? _startupCurrent
            ? Loc.T("Показаны прочитанные записи автозапуска. В списке нет служб и задач планировщика.")
            : Loc.T("Автозапуск недоступен или ещё не проверен для пользователя рабочего стола.")
        : _lastProgramRead switch
        {
            UninstallReadState.Complete when _programsCurrent =>
                Loc.T("Показаны программы, зарегистрированные в реестре удаления. Для приложений Microsoft Store откройте список Windows."),
            UninstallReadState.Partial when _programInventory?.State == UninstallReadState.Complete =>
                Loc.T("Часть источников недоступна. Показан последний полный список без рекомендаций по удалению."),
            UninstallReadState.Partial =>
                Loc.T("Часть источников недоступна. Список неполный, рекомендации по удалению приостановлены."),
            UninstallReadState.Failed when _programInventory is not null =>
                Loc.T("Обновить список не удалось. Показаны ранее прочитанные данные без рекомендаций по удалению."),
            UninstallReadState.Failed => Loc.T("Не удалось прочитать список программ. Повторите проверку."),
            _ => Loc.T("Обновите список, чтобы получить рекомендации по удалению."),
        };

    public string? EmptyMessage
    {
        get
        {
            if (IsBusy || VisibleRows.Count > 0) return null;
            if ((IsStartupTab ? _startupRows : _programRows).Count > 0)
                return Loc.T("По этому фильтру ничего не найдено.");
            if (IsStartupTab)
                return _startupCurrent ? Loc.T("В доступных источниках записи автозапуска не найдены.") : null;
            return _programsCurrent ? Loc.T("Программы с регистрацией удаления не найдены.") : null;
        }
    }

    partial void OnTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsStartupTab));
        OnPropertyChanged(nameof(Filters));
        SelectedFilter = All;
        UpdatePageStatus();
        UpdateRows();
    }

    partial void OnSearchTextChanged(string value) => UpdateRows();
    partial void OnSelectedFilterChanged(RemovalFilter? value) => UpdateRows();
    partial void OnSelectedRowChanged(RemovalRow? value) => RefreshCommands();

    protected override Task LoadAsync() => RefreshAsync();
    protected override Task OnReturnAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        _programsCurrent = false;
        _startupCurrent = false;
        _startupRows = [];
        if (_programInventory is not null) _programRows = RemovalPolicy.Programs(_programInventory, current: false);
        UpdatePageStatus();
        UpdateRows();

        await RunBusyAsync(OperationClass.Measure, Loc.T("Проверка программ и автозапуска…"), async () =>
        {
            var programScan = Task.Run(UninstallEntries.ReadInventory);
            var startupScan = ReadStartupAsync();
            var latest = await programScan;
            _lastProgramRead = latest.State;
            _programsCurrent = latest.State == UninstallReadState.Complete;
            if (_programsCurrent || (_programInventory?.State != UninstallReadState.Complete && latest.State != UninstallReadState.Failed))
                _programInventory = latest;
            _programRows = _programInventory is null ? [] : RemovalPolicy.Programs(_programInventory, _programsCurrent);

            var startup = await startupScan;
            _startupCurrent = startup is not null;
            _startupCheckedAt = _startupCurrent ? DateTime.Now : null;
            _startupRows = startup is null ? [] : startup.GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => RemovalPolicy.Startup(group.First(), unique: !group.Skip(1).Any()))
                .ToList();
            UpdatePageStatus();
            UpdateRows();
        });
    }

    private static async Task<IReadOnlyList<StartupEntry>?> ReadStartupAsync()
    {
        try
        {
            // The existing scanner opens the current profile's Run keys and Startup folder. Never show another admin's entries.
            if (await Task.Run(() => Elevation.InteractiveUserState) != true) return null;
            return await StaWorker.Run(StartupScanner.Scan, "Lycoris removal startup scan");
        }
        catch (Exception ex)
        {
            Log.Warn("Автозапуск для проверки удаления: {0}", ex.GetType().Name);
            return null;
        }
    }

    private void UpdatePageStatus()
    {
        CheckedAt = IsStartupTab ? _startupCheckedAt : _programInventory?.CheckedAt;
        OnPropertyChanged(nameof(InventoryMessage));
    }

    private void UpdateRows()
    {
        var selectedKey = SelectedRow?.Key;
        var search = SearchText.Trim();
        var decision = SelectedFilter?.Decision;
        VisibleRows = (IsStartupTab ? _startupRows : _programRows)
            .Where(row => (decision is null || row.Decision == decision)
                && (row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                    || row.Metadata.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
        SelectedRow = VisibleRows.FirstOrDefault(row => row.Key == selectedKey);
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(ShowProgramAction));
        OnPropertyChanged(nameof(ShowStartupAction));
        OnPropertyChanged(nameof(HasSelection));
        OpenSelectedProgramCommand.NotifyCanExecuteChanged();
        OpenSelectedStartupCommand.NotifyCanExecuteChanged();
    }

    private bool CanOpenProgram() => !IsBusy && _programsCurrent && ShowProgramAction && !SafetyLock.IsLocked;
    private bool CanOpenStartup() => !IsBusy && _startupCurrent && ShowStartupAction;
    private static bool CanOpenSettings() => !SafetyLock.IsLocked;

    [RelayCommand(CanExecute = nameof(CanOpenProgram))]
    private Task OpenSelectedProgramAsync() => CanOpenProgram() ? OpenWindowsAppsAsync() : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanOpenStartup))]
    private void OpenSelectedStartup()
    {
        if (!CanOpenStartup() || SelectedRow is not { } row) return;
        Ui.RequestFocus("services", CommandPaletteIndex.StartupFocusPrefix + row.Key);
        Ui.Navigate("services");
    }

    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private static Task OpenWindowsAppsAsync() => OpenSettingsAsync("ms-settings:appsfeatures");

    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private static Task OpenWindowsFeaturesAsync() => OpenSettingsAsync("ms-settings:optionalfeatures");

    private static async Task OpenSettingsAsync(string target)
    {
        if (SafetyLock.IsLocked) return;
        if (!await UnelevatedShell.ShellExecuteAsync(target, "", "", UnelevatedShell.SwShowNormal))
            Ui.Toast(Loc.T("Откройте Параметры Windows и найдите нужный раздел через поиск."), ToastKind.Info);
    }

    [RelayCommand]
    private static void OpenServices()
    {
        Ui.RequestFocus("services", "tab:services");
        Ui.Navigate("services");
    }

    private static RemovalFilter Choice(RemovalDecision decision) => new(decision, RemovalPolicy.DecisionText(decision));
}
