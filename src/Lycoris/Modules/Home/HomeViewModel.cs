using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
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
    private readonly DashboardViewModel _dashboard;
    public override string Key => "home";
    public override string Index => "00";
    public override string Title => Loc.T("Главная");
    public override string Subtitle => Loc.T("Выберите, что хотите сделать.");
    public override string Glyph => Glyphs.Home;

    public ObservableCollection<HomeChangeItem> RecentChanges { get; } = [];
    [ObservableProperty] private bool _hasJournalEntries;
    [ObservableProperty] private bool _showLaptopGoal;
    [ObservableProperty] private bool _expertMode = AppSettings.Current.ExpertMode;
    [ObservableProperty] private string _doctorSeverity = "Ok";
    [ObservableProperty] private string _doctorText = Loc.T("Доктор собирает сведения при первом открытии Панели.");
    [ObservableProperty] private string? _doctorDetails;
    [ObservableProperty] private string? _gamingFact;
    [ObservableProperty] private string? _startupFact;
    [ObservableProperty] private string? _cleanupFact;
    [ObservableProperty] private string? _laptopFact;
    [ObservableProperty] private string? _driversFact;

    public HomeViewModel(DashboardViewModel dashboard) => _dashboard = dashboard;
    public string DoctorDotStyle => DoctorSeverity == "Warning" ? "Dot.Amber" : "Dot.Green";

    protected override async Task LoadAsync()
    {
        _dashboard.PropertyChanged += (_, _) => Ui.Post(RefreshDoctor);
        ChangeJournal.Instance.Changed += () => Ui.Post(RefreshJournal);
        AppSettings.Current.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.ExpertMode)) Ui.Post(() => ExpertMode = AppSettings.Current.ExpertMode); };
        try
        {
            var profile = await SystemProfile.LoadAsync();
            ShowLaptopGoal = ShouldShowLaptopGoal(profile.IsComplete, profile.IsLaptop, TweakCatalog.DefaultsFor(ProfileKind.Efficiency).Any());
        }
        catch { ShowLaptopGoal = false; }
        RefreshFacts();
        RefreshJournal();
        RefreshDoctor();
    }

    protected override Task OnReturnAsync()
    {
        RefreshFacts(); RefreshJournal(); RefreshDoctor();
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
                new("gaming", Glyphs.Game, Loc.T("Игры"), Loc.T("Подобрать игровой профиль оптимизации."), GamingFact, Loc.T("БЕЗОПАСНО"), Loc.T("ПОДОБРАТЬ ИГРОВОЙ РЕЖИМ"), Loc.T("Профиль Gaming · элементы сценария")),
                new("startup", Glyphs.Rocket, Loc.T("Быстрее включается ПК"), Loc.T("Посмотреть программы, запускающиеся вместе с Windows."), StartupFact, Loc.T("ПО ЖЕЛАНИЮ"), Loc.T("ОТКРЫТЬ АВТОЗАПУСК"), Loc.T("Вкладка TabStartup · StartupViewModel.Items")),
                new("cleanup", Glyphs.Delete, Loc.T("Освободить место"), Loc.T("Проверить временные файлы перед очисткой."), CleanupFact, Loc.T("ПО ЖЕЛАНИЮ"), Loc.T("ПОСМОТРЕТЬ, ЧТО ОЧИСТИТЬ"), Loc.T("CleanerViewModel · только выбранные пункты")),
                ShowLaptopGoal
                    ? new("laptop", Glyphs.Battery, Loc.T("Ноутбук от батареи"), Loc.T("Подобрать сценарий автономности."), LaptopFact, Loc.T("ПО ЖЕЛАНИЮ"), Loc.T("ОТКРЫТЬ СЦЕНАРИЙ АВТОНОМНОСТИ"), Loc.T("Профиль Efficiency · параметры питания от батареи"))
                    : new("drivers", Glyphs.Devices, Loc.T("Проверить драйверы"), Loc.T("Открыть подбор драйверов для компонентов ПК."), DriversFact, Loc.T("БЕЗОПАСНО"), Loc.T("ПОДОБРАТЬ ДРАЙВЕРЫ"), Loc.T("DriversTab.Hardware · подбор для вашего ПК")),
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
        if (!_dashboard.HasResults) { DoctorText = Loc.T("Доктор собирает сведения при первом открытии Панели."); DoctorSeverity = "Ok"; DoctorDetails = null; return; }
        var count = _dashboard.WarningCount + _dashboard.InfoCount;
        DoctorSeverity = _dashboard.WarningCount > 0 ? "Warning" : "Ok";
        DoctorText = count == 0 ? Loc.T("Доктор не нашёл проблем.") : Loc.N(count, "Доктор нашёл {0} проблему", "Доктор нашёл {0} проблемы", "Доктор нашёл {0} проблем");
        DoctorDetails = string.Join(" · ", _dashboard.Issues.Take(2).Select(i => i.Title));
        OnPropertyChanged(nameof(DoctorDotStyle));
    }

    [RelayCommand] private void OpenDoctor() { Ui.RequestFocus("dashboard", "health:issues"); Ui.Navigate("dashboard"); }
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

public sealed class HomeChangeItem(ChangeRecord record)
{
    public string Id => record.Id.ToString();
    public string Title => Loc.Sink(record.Title);
    public string Target => RestoreFormat.DescribeTarget(record.Target);
    public string Date => record.TimestampUtc.ToLocalTime().ToString(Loc.IsEnglish ? "M/d HH:mm" : "dd.MM HH:mm", Loc.Culture);
    public bool CanRevert => !record.Reverted && ChangeJournal.IsBulkRevertible(record);
}
