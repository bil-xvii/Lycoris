using Monolith.Modules.Games;
using Monolith.Modules.Optimize;
using Monolith.Shell;

namespace Monolith.Tests;

/// <summary>«Obvious UI» logic of the Games page: no button is disabled by missing admin rights, the reason is text.</summary>
public sealed class GamesUxTests
{
    [Fact]
    public void Boost_buttons_stay_enabled_without_admin_rights_and_the_reason_is_text()
    {
        var vm = new GamesViewModel();
        Assert.True(vm.CanStartBoost);
        Assert.True(vm.CanLaunchBoosted);
        Assert.True(vm.StartBoostCommand.CanExecute(null));
        Assert.True(vm.PlayBoostedGameCommand.CanExecute("steam:570"));
        Assert.Equal("ВЫКЛЮЧЕН", vm.BoostStatus);
        if (!vm.IsElevated)
        {
            Assert.True(vm.HasStartNote);
            Assert.Contains("права администратора", vm.StartNote);
        }
        Assert.False(vm.HasBoostActiveNote);
    }

    [Fact]
    public void Replay_commands_exist_under_the_names_the_intent_uses()
    {
        var games = new GamesViewModel();
        foreach (var name in new[] { "StartBoost", "PlayBoostedGame", "ToggleGpuGame", "RestoreStale" })
            Assert.NotNull(new AdminIntent("games", name).FindCommand(games));

        var optimize = new OptimizeViewModel();
        foreach (var name in new[] { "Apply", "ApplyProfile", "ApplyRecommended", "RevertItem" })
            Assert.NotNull(new AdminIntent("optimize", name).FindCommand(optimize));
    }
}
