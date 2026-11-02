using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Shell;
using Xunit;

namespace Lycoris.Tests;

/// <summary>
/// The «remember what the user clicked» round-trip of a relaunch with admin rights: the intent travels as launch
/// arguments and is replayed through the page's own command, exactly once, never as raw content.
/// </summary>
public class AdminIntentTests
{
    private sealed class FakePage
    {
        public List<string?> Ran { get; } = [];
        public bool CanRun { get; set; } = true;
        public IRelayCommand<string?> ApplyCommand { get; }
        public string ApplyText { get; } = "not a command";
        public ICommand Run => ApplyCommand; // an ICommand without the «Command» suffix is not addressable

        public FakePage()
        {
            ApplyCommand = new RelayCommand<string?>(p => Ran.Add(p), _ => CanRun);
        }
    }

    [Fact]
    public void Args_round_trip_keeps_page_command_and_parameter()
    {
        var intent = new AdminIntent("restore", "RevertLastBatch", "batch-7");
        var args = intent.ToArgs();
        Assert.Equal(["--replay-page", "restore", "--replay-command", "RevertLastBatch", "--replay-param", "batch-7"], args);
        Assert.Equal(intent, AdminIntent.Parse(args));
    }

    [Fact]
    public void Parameter_is_optional_and_empty_means_none()
    {
        var intent = new AdminIntent("optimize", "Apply");
        Assert.Equal(["--replay-page", "optimize", "--replay-command", "Apply"], intent.ToArgs());
        Assert.Equal(intent, AdminIntent.Parse(intent.ToArgs()));
        Assert.Equal(intent, AdminIntent.Parse(["--replay-page", "optimize", "--replay-command", "Apply", "--replay-param", ""]));
    }

    [Fact]
    public void Parse_ignores_unrelated_arguments_and_order()
    {
        var parsed = AdminIntent.Parse(["--elevated", "--replay-command", "Apply", "--page", "optimize", "--replay-page", "optimize", "--parent", "123"]);
        Assert.Equal(new AdminIntent("optimize", "Apply"), parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--replay-page|optimize")]
    [InlineData("--replay-command|Apply")]
    [InlineData("--replay-page||--replay-command|Apply")]
    [InlineData("--replay-page|optimize|--replay-command|Apply.Secret")]
    [InlineData("--replay-page|optimize|--replay-command|Apply Command")]
    [InlineData("--replay-page|optimize|--replay-command")]
    public void Incomplete_or_malformed_arguments_give_no_intent(string args) =>
        Assert.Null(AdminIntent.Parse(args.Length == 0 ? [] : args.Split('|')));

    [Theory]
    [InlineData("Apply", true)]
    [InlineData("RevertBatch2", true)]
    [InlineData("", false)]
    [InlineData("Apply.Secret", false)]
    [InlineData("get_Item", false)]
    [InlineData("Применить", false)]
    public void Command_names_are_ascii_identifiers(string name, bool ok) => Assert.Equal(ok, AdminIntent.IsCommandName(name));

    [Fact]
    public void Finds_the_Command_property_and_runs_it_once_with_the_parameter()
    {
        var page = new FakePage();
        var intent = new AdminIntent("optimize", "Apply", "profile-gaming");
        Assert.Same(page.ApplyCommand, intent.FindCommand(page));
        Assert.True(intent.TryExecuteOn(page));
        Assert.Equal(["profile-gaming"], page.Ran);
    }

    [Fact]
    public void Does_not_run_when_the_command_cannot_execute_or_does_not_exist()
    {
        var page = new FakePage { CanRun = false };
        Assert.False(new AdminIntent("optimize", "Apply").TryExecuteOn(page));
        Assert.False(new AdminIntent("optimize", "Missing").TryExecuteOn(page));
        Assert.Null(new AdminIntent("optimize", "Missing").FindCommand(page));
        // Only «{Name}Command» properties of ICommand type count — never arbitrary members.
        Assert.Null(new AdminIntent("optimize", "ApplyText").FindCommand(page));
        Assert.Null(new AdminIntent("optimize", "Run").FindCommand(page));
        Assert.Empty(page.Ran);
    }

    [Fact]
    public void Relaunch_drops_this_runs_page_tab_and_replay_arguments_and_adds_the_new_ones()
    {
        var current = new[] { "--page", "dashboard", "--tab", "auto", "--replay-page", "x", "--replay-command", "Old", "--replay-param", "p", "--no-elevate", "--elevated", "--parent", "5" };
        var extra = new AdminIntent("restore", "RevertLastBatch").ToArgs().Prepend("restore").Prepend("--page");
        var args = Elevation.RelaunchArguments(current, extra, 42);
        Assert.Equal(["--no-elevate", "--page", "restore", "--replay-page", "restore", "--replay-command", "RevertLastBatch", "--elevated", "--parent", "42"], args);
        Assert.Equal(new AdminIntent("restore", "RevertLastBatch"), AdminIntent.Parse(args));
    }

    [Fact]
    public void Every_replay_argument_is_a_per_run_argument()
    {
        foreach (var a in new[] { AdminIntent.PageArg, AdminIntent.CommandArg, AdminIntent.ParameterArg })
            Assert.Contains(a, Elevation.PerRunArgs);
        Assert.Same(Elevation.PerRunArgs, AdminIntent.VolatileArgs);
    }

    [Fact]
    public void Prompt_text_says_whether_the_action_continues_by_itself()
    {
        Assert.Contains("продолжится само", PageViewModel.ElevationPromptText("Откат", replays: true));
        Assert.Contains("повторите действие", PageViewModel.ElevationPromptText("Откат"));
        Assert.DoesNotContain("продолжится само", PageViewModel.ElevationPromptText("Откат"));
    }

    [Theory]
    [InlineData("home", MainViewModel.GroupOverview)]
    [InlineData("dashboard", MainViewModel.GroupOverview)]
    [InlineData("optimize", MainViewModel.GroupBoost)]
    [InlineData("games", MainViewModel.GroupBoost)]
    [InlineData("services", MainViewModel.GroupBoost)]
    [InlineData("devices", MainViewModel.GroupBoost)]
    [InlineData("maintenance", MainViewModel.GroupMaintenance)]
    [InlineData("drivers", MainViewModel.GroupMaintenance)]
    [InlineData("apps", MainViewModel.GroupMaintenance)]
    [InlineData("restore", MainViewModel.GroupProtection)]
    [InlineData("settings", "")]
    public void Sidebar_groups_cover_every_page(string key, string group) => Assert.Equal(group, MainViewModel.NavGroupOf(key));
}
