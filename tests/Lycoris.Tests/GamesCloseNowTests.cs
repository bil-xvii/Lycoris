using Lycoris.Core;
using Lycoris.Modules.Games;
using Lycoris.Shell;

namespace Lycoris.Tests;

[CollectionDefinition(nameof(GamesCloseNowTests), DisableParallelization = true)]
public sealed class GamesCloseNowCollection { }

[Collection(nameof(GamesCloseNowTests))]
public sealed class GamesCloseNowTests
{
    [Fact]
    public void Close_now_becomes_available_when_an_editor_selection_is_made()
    {
        var games = new GamesViewModel { IsEditorOpen = true };
        var command = games.CloseSelectedNowCommand;
        var rechecked = 0;
        command.CanExecuteChanged += (_, _) => rechecked++;

        Assert.False(command.CanExecute(null));
        games.EditorList.Add("ReviewTool");
        Assert.Equal(1, rechecked);
        Assert.True(command.CanExecute(null));
        games.EditorList.Clear();
        Assert.Equal(2, rechecked);
        Assert.False(command.CanExecute(null));
    }

    [Fact]
    public void Elevation_round_trips_the_unsaved_selection_instead_of_the_saved_list()
    {
        var games = new GamesViewModel { IsEditorOpen = true };
        var saved = games.Settings.GameBoostCloseList;
        games.EditorList.Add("ReviewTool");
        games.EditorList.Add("Example App");
        Assert.True(games.TryGetCloseSelection(null, out var selected));
        Assert.Equal(["ReviewTool", "Example App"], selected);

        var intent = new AdminIntent("games", AdminIntent.CloseSelectedNow, string.Join(',', selected));
        var replay = Assert.IsType<AdminIntent>(AdminIntent.Parse(intent.ToArgs()));
        var elevatedCopy = new GamesViewModel();
        Assert.Same(elevatedCopy.CloseSelectedNowCommand, replay.FindCommand(elevatedCopy));
        Assert.True(elevatedCopy.CloseSelectedNowCommand.CanExecute(replay.Parameter));
        Assert.True(elevatedCopy.TryGetCloseSelection(replay.Parameter, out var replayed));
        Assert.Equal(["ReviewTool", "Example App"], replayed);
        Assert.Same(saved, games.Settings.GameBoostCloseList);
        Assert.Equal(["ReviewTool", "Example App"], games.EditorList);
    }

    [Theory]
    [InlineData("games", null)]
    [InlineData("games", "C:\\Temp\\ExampleApp.exe")]
    [InlineData("games", "ExampleApp.exe")]
    [InlineData("games", "ExampleApp,exampleapp")]
    [InlineData("games", "ExampleApp,--force")]
    [InlineData("games", "ExampleApp,")]
    [InlineData("optimize", "ExampleApp")]
    public void Malformed_or_wrong_page_replay_cannot_reach_the_command(string page, string? parameter)
    {
        var intent = new AdminIntent(page, AdminIntent.CloseSelectedNow, parameter);
        Assert.Null(AdminIntent.Parse(intent.ToArgs()));
        Assert.Null(intent.FindCommand(new GamesViewModel()));
    }

    [Fact]
    public void Replay_refuses_duplicate_or_unfinished_flags_and_a_selection_beyond_the_limit()
    {
        var intent = new AdminIntent("games", AdminIntent.CloseSelectedNow, "ReviewTool");
        Assert.Null(AdminIntent.Parse([.. intent.ToArgs(), AdminIntent.ParameterArg, "DifferentApp"]));
        Assert.Null(AdminIntent.Parse([.. intent.ToArgs(), AdminIntent.ParameterArg]));
        var many = intent with { Parameter = string.Join(',', Enumerable.Range(0, 33).Select(i => $"ExampleApp{i}")) };
        Assert.Null(AdminIntent.Parse(many.ToArgs()));
    }

    [Theory]
    [InlineData("ExampleApp.exe")]
    [InlineData("ExampleApp,OtherApp")]
    public void Selection_cannot_expand_a_single_editor_entry_to_different_process_names(string name)
    {
        var games = new GamesViewModel();
        games.EditorList.Add(name);
        Assert.False(games.TryGetCloseSelection(null, out _));
    }

    [Fact]
    public async Task Self_test_blocks_direct_and_replayed_closure_without_touching_session_settings()
    {
        var wasLocked = ChangeSession.Locked;
        ChangeSession.Locked = true;
        try
        {
            var games = new GamesViewModel { IsEditorOpen = true };
            games.EditorList.Add("ReviewTool");
            var saved = games.Settings.GameBoostCloseList;
            var settings = (games.Settings.GameBoostPowerPlan, games.Settings.GameBoostPriority,
                games.Settings.GameBoostPurgeStandby, games.Settings.GameBoostCloseMode);
            var replay = new AdminIntent("games", AdminIntent.CloseSelectedNow, "OtherReviewTool");

            Assert.False(games.CloseSelectedNowCommand.CanExecute(null));
            Assert.False(replay.TryExecuteOn(games));
            await games.CloseSelectedNowCommand.ExecuteAsync(null);
            await games.CloseSelectedNowCommand.ExecuteAsync(replay.Parameter);

            Assert.Equal(["ReviewTool"], games.EditorList);
            Assert.Same(saved, games.Settings.GameBoostCloseList);
            Assert.Equal(settings, (games.Settings.GameBoostPowerPlan, games.Settings.GameBoostPriority,
                games.Settings.GameBoostPurgeStandby, games.Settings.GameBoostCloseMode));
            Assert.False(games.Boost.IsActive);
            Assert.False(games.Boost.IsTransitioning);
        }
        finally
        {
            ChangeSession.Locked = wasLocked;
        }
    }

    [Fact]
    public void Preview_shows_the_pid_full_path_and_exact_start_time()
    {
        using var _ = Loc.UseLanguage("en");
        var target = new BackgroundCloseTarget(100, "ReviewTool", DateTime.UnixEpoch.AddTicks(12345),
            @"C:\Apps\ReviewTool.exe", 0, "S-1-5-21-1", 1);

        Assert.Equal("ReviewTool · PID 100\nStarted 1970-01-01 00:00:00.0012345 UTC\nC:\\Apps\\ReviewTool.exe",
            GamesViewModel.DescribeCloseTarget(target));
    }

    [Fact]
    public void Outcome_distinguishes_closure_remaining_processes_skips_and_errors()
    {
        using var _ = Loc.UseLanguage("en");
        var statuses = new[] { BackgroundCloseStatus.Closed, BackgroundCloseStatus.Forced, BackgroundCloseStatus.StillRunning,
            BackgroundCloseStatus.Stale, BackgroundCloseStatus.Denied, BackgroundCloseStatus.Error };
        var results = statuses.Select((status, index) => new BackgroundCloseResult(
            new BackgroundCloseTarget(100 + index, "ReviewTool", DateTime.UnixEpoch, @"C:\Apps\ReviewTool.exe", 0, "S-1-5-21-1", 1),
            status)).ToArray();

        Assert.Equal("Closed 2 · still running 1 · skipped 4 · errors 2.", GamesViewModel.DescribeCloseResults(results, unavailable: 3));
    }
}
