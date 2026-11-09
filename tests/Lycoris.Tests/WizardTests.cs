using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Shell;

namespace Lycoris.Tests;

/// <summary>Both wizard test classes change the global AppSettings.Current (FirstRun*, UserGoal): one collection runs them one at a time.</summary>
[CollectionDefinition("WizardSettings", DisableParallelization = true)]
public sealed class WizardSettingsCollection { }

[Collection("WizardSettings")]
public sealed class WizardTests
{
    [Fact]
    public void Steps_back_forward_skip_and_finish_save_preferences()
    {
        var settings = AppSettings.Current;
        var oldGoal = settings.UserGoal;
        var oldCompleted = settings.FirstRunCompleted;
        var oldInProgress = settings.FirstRunWizardInProgress;
        var closed = 0;
        string? navigated = null;
        try
        {
            settings.FirstRunCompleted = false;
            var vm = new WizardViewModel(() => closed++, page => navigated = page);
            vm.Open();
            Assert.True(settings.FirstRunWizardInProgress);
            Assert.Equal(1, vm.Step);
            Assert.False(vm.CanGoNext);
            vm.Goal = UserGoal.Games;
            vm.NextCommand.Execute(null);
            Assert.Equal(2, vm.Step);
            vm.BackCommand.Execute(null);
            Assert.Equal(1, vm.Step);
            Assert.Equal(UserGoal.Games, settings.UserGoal);
            vm.NextCommand.Execute(null);
            vm.NextCommand.Execute(null);
            vm.FinishCommand.Execute(null);
            Assert.True(settings.FirstRunCompleted);
            Assert.False(settings.FirstRunWizardInProgress);
            Assert.Equal("home", navigated);
            Assert.Equal(1, closed);
        }
        finally { settings.UserGoal = oldGoal; settings.FirstRunCompleted = oldCompleted; settings.FirstRunWizardInProgress = oldInProgress; }
    }

    [Fact]
    public void Skip_completes_without_navigating() 
    {
        var settings = AppSettings.Current;
        var old = settings.FirstRunCompleted;
        var oldInProgress = settings.FirstRunWizardInProgress;
        try
        {
            settings.FirstRunCompleted = false;
            var vm = new WizardViewModel(() => { }, _ => throw new Xunit.Sdk.XunitException("skip must not navigate"));
            vm.Open();
            vm.SkipCommand.Execute(null);
            Assert.True(settings.FirstRunCompleted);
            Assert.False(settings.FirstRunWizardInProgress);
            Assert.False(vm.IsOpen);
        }
        finally { settings.FirstRunCompleted = old; settings.FirstRunWizardInProgress = oldInProgress; }
    }

    [Theory]
    [InlineData(false, false, false, false, false, "Recommended", false, false, true)]
    [InlineData(false, false, true, false, false, "Recommended", false, false, false)]
    [InlineData(false, false, false, true, false, "Recommended", false, false, false)]
    [InlineData(false, false, false, false, false, "Recommended", true, false, false)]
    [InlineData(false, false, false, false, false, "Gaming", false, false, false)]
    [InlineData(false, false, false, false, false, "Recommended", false, true, false)]
    [InlineData(false, false, false, false, false, "Recommended", false, true, true, true)]
    [InlineData(true, true, true, false, false, "Gaming", false, false, true)]
    public void Startup_policy_only_auto_opens_for_new_or_interrupted_user(bool completed, bool inProgress, bool settings, bool journal, bool expert, string profile, bool replay, bool qa, bool expected, bool demo = false) =>
        Assert.Equal(expected, WizardStartupPolicy.ShouldAutoShow(completed, inProgress, settings, journal, expert, profile, replay, qa, demo));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Laptop_goal_requires_known_portable_facts(bool known, bool laptop, bool expected) =>
        Assert.Equal(expected, WizardChoices.ShowLaptop(known, laptop));

    [Theory]
    [InlineData(true, false, false, "Recommended", true)]
    [InlineData(false, true, false, "Recommended", true)]
    [InlineData(false, false, true, "Recommended", true)]
    [InlineData(false, false, false, "Gaming", true)]
    [InlineData(false, false, false, "Recommended", false)]
    public void Existing_configuration_gets_a_soft_prompt(bool settings, bool journal, bool expert, string profile, bool expected) =>
        Assert.Equal(expected, WizardStartupPolicy.IsConfiguredUser(settings, journal, expert, profile));

    [Fact]
    public void Settings_validator_rejects_unknown_goal()
    {
        var settings = new AppSettings { UserGoal = (UserGoal)999 };
        Assert.Equal(1, SettingsValidator.Sanitize(settings));
        Assert.Equal(UserGoal.Recommended, settings.UserGoal);
    }
}

[Collection("WizardSettings")]
public sealed class WizardGamesLinkTests
{
    [Fact]
    public void Games_goal_links_to_games_page()
    {
        var s = AppSettings.Current;
        var (g, c, p) = (s.UserGoal, s.FirstRunCompleted, s.FirstRunWizardInProgress);
        string? page = null;
        try
        {
            var vm = new WizardViewModel(() => { }, x => page = x);
            vm.Open();
            vm.Goal = UserGoal.Games;
            vm.Step = 3;
            Assert.True(vm.ShowGamesLink);
            vm.OpenGamesCommand.Execute(null);
            Assert.Equal("games", page);
            Assert.True(s.FirstRunCompleted);
        }
        finally { s.UserGoal = g; s.FirstRunCompleted = c; s.FirstRunWizardInProgress = p; }
    }
}
