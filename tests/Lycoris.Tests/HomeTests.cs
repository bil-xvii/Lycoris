using Lycoris.Core;
using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Home;
using Xunit;

namespace Lycoris.Tests;

public sealed class HomeTests
{
    [Fact]
    public void Home_goals_use_only_existing_destinations_and_fall_back_to_driver_picker()
    {
        var home = new HomeViewModel(new DashboardViewModel());
        Assert.Equal(["gaming", "startup", "cleanup", "drivers"], home.Goals.Select(g => g.Id));
        Assert.DoesNotContain(home.Goals, g => g.Id == "work" || g.Id == "quiet");
        Assert.All(home.Goals, goal => Assert.False(string.IsNullOrWhiteSpace(goal.Action)));

        home.ShowLaptopGoal = true;
        Assert.Equal("laptop", home.Goals[^1].Id);
        Assert.DoesNotContain(home.Goals, g => g.Id == "drivers");
        Assert.True(HomeViewModel.ShouldShowLaptopGoal(profileComplete: true, isLaptop: true, hasAutonomyScenario: true));
        Assert.False(HomeViewModel.ShouldShowLaptopGoal(profileComplete: true, isLaptop: false, hasAutonomyScenario: true));
        Assert.False(HomeViewModel.ShouldShowLaptopGoal(profileComplete: false, isLaptop: true, hasAutonomyScenario: true));
        Assert.False(HomeViewModel.ShouldShowLaptopGoal(profileComplete: true, isLaptop: true, hasAutonomyScenario: false));
    }

    [Fact]
    public void Home_omits_cleanup_fact_without_cached_analysis()
    {
        var home = new HomeViewModel(new DashboardViewModel());
        Assert.Null(home.CleanupFact);
    }

    [Fact]
    public void Recent_journal_rows_are_read_only_sorted_and_limited_to_five()
    {
        var empty = HomeViewModel.RecentItems([]);
        Assert.Empty(empty);

        var records = Enumerable.Range(0, 7).Select(i => new ChangeRecord
        {
            TimestampUtc = DateTime.UnixEpoch.AddMinutes(i),
            Title = $"Изменение {i}",
            Target = "svc:Test" + i,
        }).ToArray();

        Assert.Equal(["Изменение 6", "Изменение 5", "Изменение 4", "Изменение 3", "Изменение 2"],
            HomeViewModel.RecentItems(records).Select(r => r.Title));
    }

    [Fact]
    public void Doctor_uses_existing_dashboard_state_and_navigation_key_is_stable()
    {
        var dashboard = new DashboardViewModel();
        var home = new HomeViewModel(dashboard);

        Assert.Equal("home", home.Key);
        Assert.Equal("Доктор собирает сведения при первом открытии Панели.", home.DoctorText);
        Assert.Equal("Ok", home.DoctorSeverity);
    }
}
