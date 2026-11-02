using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Modules.Dashboard;
using Lycoris.Shell;
using Lycoris.UI;

namespace Lycoris.Modules.Home;

public sealed partial class HomeViewModel : PageViewModel
{
    private readonly DashboardViewModel _dashboard;
    public override string Key => "home";
    public override string Index => "00";
    public override string Title => Loc.T("Главная");
    public override string Subtitle => Loc.T("Выберите цель или посмотрите, что можно улучшить безопасно.");
    public override string Glyph => Glyphs.Home;

    public HomeViewModel(DashboardViewModel dashboard) => _dashboard = dashboard;

    public string DoctorText => !_dashboard.HasResults ? Loc.T("Доктор собирает сведения при первом открытии Панели.") :
        _dashboard.WarningCount + _dashboard.InfoCount == 0 ? Loc.T("Проблем не найдено") :
        Loc.N(_dashboard.WarningCount + _dashboard.InfoCount, "Доктор нашёл {0} проблему", "Доктор нашёл {0} проблемы", "Доктор нашёл {0} проблем");

    protected override async Task LoadAsync() { await _dashboard.EnsureLoadedAsync(); OnPropertyChanged(nameof(DoctorText)); }

    [RelayCommand] private void OpenDoctor() { Ui.RequestFocus("dashboard", "health:issues"); Ui.Navigate("dashboard"); }
    [RelayCommand] private void SelectGoal(string? profile) { if (profile is null) return; Ui.RequestFocus("optimize", "profile:" + profile); Ui.Navigate("optimize"); }
    [RelayCommand] private void OpenGames() => Ui.Navigate("games");
    [RelayCommand] private void OpenCleaner() => Ui.Navigate("cleaner");
    [RelayCommand] private void OpenRestore() => Ui.Navigate("restore");
}
