using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Core.Measurements;
using Lycoris.Modules.Cleaner;
using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Maintenance;
using Lycoris.Modules.Restore;
using Lycoris.Modules.Services;
using Lycoris.Shell;
using Lycoris.UI;
using Lycoris.Tweaks;

namespace Lycoris.Modules.Home;

public sealed partial class HomeViewModel : PageViewModel
{
    public bool ShowWizardPrompt => !AppSettings.Current.FirstRunCompleted && WizardStartupPolicy.IsConfiguredUser(
        File.Exists(AppPaths.SettingsReadFile), ChangeJournal.Instance.Records.Count > 0, AppSettings.Current.ExpertMode, AppSettings.Current.LastProfile);
    [RelayCommand] private void OpenWizard() => (Ui.Host as MainViewModel)?.OpenWizard();
    private readonly DashboardViewModel _dashboard;
    private PropertyChangedEventHandler? _dashboardChangedHandler;
    private PropertyChangedEventHandler? _settingsChangedHandler;
    private Action? _journalChangedHandler;
    public override string Key => "home";
    public override string Index => "00";
    public override string Title => Loc.T("Главная");
    public override string Subtitle => Loc.T("Выберите, что хотите сделать.");
    public override string Glyph => Glyphs.Home;

    public ObservableCollection<HomeChangeItem> RecentChanges { get; } = [];
    [ObservableProperty] private bool _hasJournalEntries;
    [ObservableProperty] private bool _showLaptopGoal;
    [ObservableProperty] private bool _expertMode = AppSettings.Current.ExpertMode;
    [ObservableProperty] private bool _doctorHasData;
    [ObservableProperty] private bool _doctorHasProblems;
    [ObservableProperty] private string _doctorStatus = Loc.T("Нет данных");
    [ObservableProperty] private string _doctorChecked = "";
    public ObservableCollection<DoctorIssue> DoctorIssues { get; } = [];
    [ObservableProperty] private string? _gamingFact;
    [ObservableProperty] private string? _startupFact;
    [ObservableProperty] private string? _cleanupFact;
    [ObservableProperty] private string? _laptopFact;
    [ObservableProperty] private string? _driversFact;
    [ObservableProperty] private string _measurementSummary = Loc.T("Замеры ещё не записаны");
    [ObservableProperty] private string _measurementDetails = "";
    [ObservableProperty] private bool _hasMeasurements;
    [ObservableProperty] private string _bootTimeSourceText = "";

    // Before / after measurement: the shared Core/Measurements module (real sources, safe store, honest comparison).
    private readonly MeasurementStore _measurementStore = new();
    [ObservableProperty] private string? _measurementNote;

    [RelayCommand]
    private async Task RecordMeasurementAsync()
    {
        string? preserved = null;
        var saved = await Task.Run(() =>
        {
            var ok = _measurementStore.Save(MeasurementCollector.CreateDefault().Collect());
            preserved = _measurementStore.PreservedCorruptFile;
            return ok;
        });
        MeasurementNote = !saved ? Loc.T("Замер не сохранён: нет доступа к папке данных Lycoris")
            : preserved is not null ? Loc.Tf("Старый файл замеров не прочитан и сохранён рядом: {0}", Path.GetFileName(preserved))
            : null;
        RefreshMeasurements();
    }

    private void RefreshMeasurements()
    {
        var entries = _measurementStore.Read().TakeLast(2).ToArray();
        HasMeasurements = entries.Length > 0;
        MeasurementSummary = entries.Length switch
        {
            0 => Loc.T("Замеры ещё не записаны"),
            1 => Loc.Tf("Последний замер: {0}", entries[0].RecordedAt.ToLocalTime().ToString("g")),
            _ => Loc.Tf("Последние замеры: {0} → {1}", entries[0].RecordedAt.ToLocalTime().ToString("g"), entries[1].RecordedAt.ToLocalTime().ToString("g"))
        };
        if (entries.Length < 2)
        {
            MeasurementDetails = Loc.T("Сравнение появится после второго замера");
            BootTimeSourceText = "";
            return;
        }
        var c = MeasurementComparer.Compare(entries[0], entries[1]);
        var a = entries[0].Conditions;
        var b = entries[1].Conditions;
        static string Mb(long? bytes) => bytes is { } v ? (v / 1024 / 1024).ToString("N0") : "—";
        var boot = a.BootTimeSeconds is { } bs && b.BootTimeSeconds is { } be
            ? Loc.Tf("загрузка: {0} → {1} с", bs.ToString("0.0"), be.ToString("0.0"))
            : Loc.T("время загрузки недоступно");
        MeasurementDetails = (c.Incomparable ? Loc.Tf("Несопоставимо: {0}", c.Reason ?? "") : Loc.T("Сопоставимо"))
            + " · " + Loc.Tf("Автозапуск: {0} → {1}", a.EnabledStartupItems, b.EnabledStartupItems)
            + " · " + Loc.Tf("свободная память: {0} → {1} МБ", Mb(a.FreeMemoryBytes), Mb(b.FreeMemoryBytes))
            + " · " + boot;
        BootTimeSourceText = ExpertMode ? Loc.Tf("Источник времени загрузки: {0}", b.BootTimeSource) : "";
    }

    private readonly Func<string, Task> _runDoctorCheck;

    public HomeViewModel(DashboardViewModel dashboard, Func<string, Task>? runDoctorCheck = null)
    {
        _dashboard = dashboard;
        _runDoctorCheck = runDoctorCheck ?? (id => dashboard.RunCheckCommand.ExecuteAsync(id));
    }
    protected override async Task LoadAsync()
    {
        _dashboardChangedHandler = (_, _) => Ui.Post(RefreshDoctor);
        _dashboard.PropertyChanged += _dashboardChangedHandler;
        _journalChangedHandler = () => Ui.Post(RefreshJournal);
        ChangeJournal.Instance.Changed += _journalChangedHandler;
        _settingsChangedHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ExpertMode)) Ui.Post(() => ExpertMode = AppSettings.Current.ExpertMode);
            if (e.PropertyName == nameof(AppSettings.FirstRunCompleted)) Ui.Post(() => OnPropertyChanged(nameof(ShowWizardPrompt)));
        };
        AppSettings.Current.PropertyChanged += _settingsChangedHandler;
        try
        {
            if (!_dashboard.HasResults) await _dashboard.RefreshChecksCommand.ExecuteAsync(null);
            var profile = await SystemProfile.LoadAsync();
            ShowLaptopGoal = ShouldShowLaptopGoal(profile.IsComplete, profile.IsLaptop, TweakCatalog.DefaultsFor(ProfileKind.Efficiency).Any());
        }
        catch { ShowLaptopGoal = false; }
        RefreshFacts();
        RefreshJournal();
        RefreshDoctor();
        RefreshMeasurements();
    }

    public override void Deactivate()
    {
        if (_dashboardChangedHandler is not null) _dashboard.PropertyChanged -= _dashboardChangedHandler;
        if (_journalChangedHandler is not null) ChangeJournal.Instance.Changed -= _journalChangedHandler;
        if (_settingsChangedHandler is not null) AppSettings.Current.PropertyChanged -= _settingsChangedHandler;
        _dashboardChangedHandler = null;
        _journalChangedHandler = null;
        _settingsChangedHandler = null;
    }

    protected override Task OnReturnAsync()
    {
        RefreshFacts(); RefreshJournal(); RefreshDoctor(); RefreshMeasurements();
        return Task.CompletedTask;
    }

    public void RefreshFacts()
    {
        GamingFact = null;
        var main = Ui.Host as MainViewModel;
        var startup = main?.Pages.OfType<ServicesViewModel>().FirstOrDefault()?.Startup;
        StartupFact = startup is { IsLoaded: true } ? Loc.N(startup.TotalCount, "{0} программа в автозапуске", "{0} программы в автозапуске", "{0} программ в автозапуске") : null;
        var cleaner = main?.Maintenance.Cleaner;
        CleanupFact = cleaner is { JunkBytes: not null } ? Loc.Tf("Можно освободить: {0}", CleanFormat.Bytes(cleaner.JunkBytes.Value)) : null;
        LaptopFact = ShowLaptopGoal ? Loc.T("Сценарий автономности доступен") : null;
        DriversFact = null;
        OnPropertyChanged(nameof(Goals));
    }

    public IReadOnlyList<HomeGoal> Goals
    {
        get
        {
            var list = new List<HomeGoal>
            {
                new("gaming", Glyphs.Game, Loc.T("Игры"), Loc.T("Подобрать игровой профиль оптимизации."), GamingFact, Loc.T("БЕЗОПАСНО"), Loc.T("ПОДОБРАТЬ ИГРОВОЙ РЕЖИМ"), Loc.T("Профиль «Игры»: набор безопасных настроек, каждую можно откатить")),
                new("startup", Glyphs.Rocket, Loc.T("Быстрее включается ПК"), Loc.T("Посмотреть программы, запускающиеся вместе с Windows."), StartupFact, Loc.T("ПО ЖЕЛАНИЮ"), Loc.T("ОТКРЫТЬ АВТОЗАПУСК"), Loc.T("Автозапуск: ничего не отключается без вашего подтверждения")),
                new("cleanup", Glyphs.Delete, Loc.T("Освободить место"), Loc.T("Проверить временные файлы перед очисткой."), CleanupFact, Loc.T("ПО ЖЕЛАНИЮ"), Loc.T("ПОСМОТРЕТЬ, ЧТО ОЧИСТИТЬ"), Loc.T("Удаляются только отмеченные вами пункты, пользовательские файлы не затрагиваются")),
                ShowLaptopGoal
                    ? new("laptop", Glyphs.Battery, Loc.T("Ноутбук от батареи"), Loc.T("Подобрать сценарий автономности."), LaptopFact, Loc.T("ПО ЖЕЛАНИЮ"), Loc.T("ОТКРЫТЬ СЦЕНАРИЙ АВТОНОМНОСТИ"), Loc.T("Профиль Efficiency · параметры питания от батареи"))
                    : new("drivers", Glyphs.Devices, Loc.T("Проверить драйверы"), Loc.T("Открыть подбор драйверов для компонентов ПК."), DriversFact, Loc.T("БЕЗОПАСНО"), Loc.T("ПОДОБРАТЬ ДРАЙВЕРЫ"), Loc.T("Подбор по вашему железу, установка только после подтверждения")),
            };
            return list;
        }
    }

    private void RefreshJournal()
    {
        RecentChanges.Clear();
        var records = RecentItems(ChangeJournal.Instance.Records);
        foreach (var record in records) RecentChanges.Add(new HomeChangeItem(record));
        HasJournalEntries = records.Count > 0;
    }

    internal static IReadOnlyList<ChangeRecord> RecentItems(IEnumerable<ChangeRecord> records) => records
        .OrderByDescending(r => r.TimestampUtc).Take(5).ToArray();

    internal static bool ShouldShowLaptopGoal(bool profileComplete, bool isLaptop, bool hasAutonomyScenario) =>
        profileComplete && isLaptop && hasAutonomyScenario;

    private void RefreshDoctor()
    {
        DoctorIssues.Clear();
        DoctorHasData = _dashboard.HasResults;
        DoctorHasProblems = false;
        if (!DoctorHasData) { DoctorStatus = Loc.T("Нет данных"); DoctorChecked = ""; return; }
        var issues = BuildDoctorIssues(_dashboard.Issues);
        foreach (var item in issues) DoctorIssues.Add(item);
        DoctorHasProblems = issues.Count > 0;
        DoctorStatus = DoctorHasProblems ? Loc.T("Что можно улучшить") : Loc.T("Проблем не найдено");
        DoctorChecked = _dashboard.LastCheckText;
    }

    internal static IReadOnlyList<DoctorIssue> BuildDoctorIssues(IEnumerable<CheckItemViewModel> source) => source
        .Where(i => !i.IsOk)
        .OrderByDescending(i => i.Severity)
        .ThenByDescending(i => i.Result.Weight)
        .Take(3)
        .Select(item => new DoctorIssue(item.Result.Id, item.Title, DoctorReason(item.Result.Id, item.Detail), item.Detail,
            item.Severity == Lycoris.Modules.Dashboard.Checks.CheckSeverity.Warning ? "Warning" : "Info",
            item.Result.Weight >= 20 && item.Severity == Lycoris.Modules.Dashboard.Checks.CheckSeverity.Warning ? "Dot.Red" : "Dot.Amber",
            DoctorActionLabel(item.Result.Action), item.Result.Action?.Kind == Lycoris.Modules.Dashboard.Checks.CheckActionKind.ApplyTweak ? Loc.T("БЕЗОПАСНО") : Loc.T("ПО ЖЕЛАНИЮ"), Loc.Tf("Правило: {0}", item.Result.Id), item.Result.Action is not null))
        .ToArray();

    internal static string DoctorReason(string ruleId, string detail) => ruleId switch
    {
        "pending-reboot" => Loc.T("До перезагрузки часть изменений не вступит в силу."),
        "startup" => Loc.T("Программы в фоне могут замедлять вход в Windows."),
        "power-plan" => Loc.T("Сбалансированный режим снижает нагрев и расход энергии в простое."),
        _ => detail.Split('.', 2, StringSplitOptions.TrimEntries)[0] is { Length: > 0 } text ? text + "." : detail,
    };

    private static string DoctorActionLabel(Lycoris.Modules.Dashboard.Checks.CheckAction? action) => action?.Kind switch
    {
        Lycoris.Modules.Dashboard.Checks.CheckActionKind.ApplyTweak => action.Label,
        Lycoris.Modules.Dashboard.Checks.CheckActionKind.FocusTweak => Loc.T("ПОСМОТРЕТЬ НАСТРОЙКУ"),
        Lycoris.Modules.Dashboard.Checks.CheckActionKind.FocusDevice => Loc.T("ОТКРЫТЬ МОНИТОР"),
        Lycoris.Modules.Dashboard.Checks.CheckActionKind.Navigate when action.Target == "startup" => Loc.T("ОТКРЫТЬ АВТОЗАПУСК"),
        Lycoris.Modules.Dashboard.Checks.CheckActionKind.Navigate when action.Target == "restore" => Loc.T("ОТКРЫТЬ ВОССТАНОВЛЕНИЕ"),
        Lycoris.Modules.Dashboard.Checks.CheckActionKind.Navigate when action.Target == "cleaner" => action.Label,
        Lycoris.Modules.Dashboard.Checks.CheckActionKind.Navigate when action.Target == "drivers" => Loc.T("ОТКРЫТЬ ДРАЙВЕРЫ"),
        Lycoris.Modules.Dashboard.Checks.CheckActionKind.Navigate when action.Target == "tools" => Loc.T("ОТКРЫТЬ ИНСТРУМЕНТЫ"),
        _ => Loc.T("ОТКРЫТЬ ПАНЕЛЬ"),
    };

    [RelayCommand] private async Task RefreshDoctorAsync()
    {
        try { await _dashboard.RefreshChecksCommand.ExecuteAsync(null); }
        catch { DoctorHasData = false; DoctorStatus = Loc.T("Нет данных"); }
        RefreshDoctor();
    }
    [RelayCommand] private void OpenDoctor() { Ui.RequestFocus("dashboard", "health:issues"); Ui.Navigate("dashboard"); }
    [RelayCommand] private async Task RunDoctorActionAsync(DoctorIssue? issue)
    {
        if (issue is null) return;
        if (!issue.HasAction)
        {
            Ui.RequestFocus("dashboard", "health:issues");
            Ui.Navigate("dashboard");
            return;
        }
        await _runDoctorCheck(issue.RuleId);
    }
    [RelayCommand] private void OpenGoal(HomeGoal? goal)
    {
        if (goal is null) return;
        switch (goal.Id)
        {
            case "gaming": Ui.RequestFocus("optimize", "profile:Gaming"); Ui.Navigate("optimize"); break;
            case "startup": Ui.RequestFocus("services", "tab:startup"); Ui.Navigate("services"); break;
            case "cleanup": Ui.Navigate("cleaner"); break;
            case "laptop": Ui.RequestFocus("optimize", "profile:Efficiency"); Ui.Navigate("optimize"); break;
            case "drivers": Ui.RequestFocus("drivers", "tab:hardware"); Ui.Navigate("drivers"); break;
        }
    }
    [RelayCommand] private void OpenRestore(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id)) Ui.RequestFocus("restore", "record:" + id);
        Ui.Navigate("restore");
    }
}

public sealed record HomeGoal(string Id, string Glyph, string Title, string Description, string? Fact, string Risk, string Action, string ExpertDetail);

public sealed record DoctorIssue(string RuleId, string Title, string Reason, string FullDetail, string Severity, string DotStyle, string Action, string Risk, string TechnicalDetail, bool HasAction = true);

public sealed class HomeChangeItem(ChangeRecord record)
{
    public string Id => record.Id.ToString();
    public string Title => Loc.Sink(record.Title);
    public string Target => RestoreFormat.DescribeTarget(record.Target);
    public string Date => record.TimestampUtc.ToLocalTime().ToString(Loc.IsEnglish ? "M/d HH:mm" : "dd.MM HH:mm", Loc.Culture);
    public bool CanRevert => !record.Reverted && ChangeJournal.IsBulkRevertible(record);
}
