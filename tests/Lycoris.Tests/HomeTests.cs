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
        Assert.Equal("Нет данных", home.DoctorStatus);
        Assert.False(home.DoctorHasData);
    }

    [Fact]
    public void Doctor_sorts_by_severity_then_weight_and_limits_to_three()
    {
        using var _ = Loc.UseLanguage("ru");
        var source = Enumerable.Range(0, 5).Select(i => new Lycoris.Modules.Dashboard.CheckItemViewModel(
            new Lycoris.Modules.Dashboard.Checks.HealthCheckResult($"rule-{i}", $"Заголовок {i}", "Причина", i == 4 ? Lycoris.Modules.Dashboard.Checks.CheckSeverity.Info : Lycoris.Modules.Dashboard.Checks.CheckSeverity.Warning, i),
            _ => Task.CompletedTask)).ToArray();
        Assert.Equal(["rule-3", "rule-2", "rule-1"], HomeViewModel.BuildDoctorIssues(source).Select(i => i.RuleId));
        Assert.Empty(HomeViewModel.BuildDoctorIssues([]));
    }

    [Fact]
    public async Task Doctor_button_dispatches_the_existing_rule_command_with_its_rule_id()
    {
        string? invoked = null;
        var home = new HomeViewModel(new DashboardViewModel(), id => { invoked = id; return Task.CompletedTask; });
        var issue = new DoctorIssue("startup", "Заголовок", "Причина", "Подробности", "Warning", "Dot.Amber", "Открыть", "ПО ЖЕЛАНИЮ", "Правило: startup");
        await home.RunDoctorActionCommand.ExecuteAsync(issue);
        Assert.Equal("startup", invoked);
    }

    [Theory]
    [InlineData("pending-reboot", "До перезагрузки часть изменений не вступит в силу.")]
    [InlineData("startup", "Программы в фоне могут замедлять вход в Windows.")]
    public void Doctor_reason_is_short_and_plain(string ruleId, string expected) =>
        Assert.Equal(expected, HomeViewModel.DoctorReason(ruleId, "Технические подробности. Дополнительные сведения."));
}
