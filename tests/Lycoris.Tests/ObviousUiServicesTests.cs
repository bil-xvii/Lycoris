using System.Reflection;
using System.ServiceProcess;
using System.Windows.Input;
using Lycoris.Modules.Services;
using Lycoris.Modules.Startup;
using Lycoris.Shell;
using Xunit;

namespace Lycoris.Tests;

/// <summary>«Obvious UI» for Автозапуск и службы: the extra-autostart rule, intent parameters, the replay targets.</summary>
public class ObviousUiServicesTests
{
    private static StartupItem Item(string name, bool enabled = true, bool exists = true, StartupRisk risk = StartupRisk.Normal, bool slow = false)
    {
        var entry = new StartupEntry
        {
            Source = StartupSource.RunUser,
            Name = name,
            DisplayName = name,
            Command = $@"C:\Apps\{name}.exe",
            TargetPath = $@"C:\Apps\{name}.exe",
            TargetExists = exists,
            Enabled = enabled,
            Risk = risk,
        };
        var item = new StartupItem(entry);
        if (slow) item.BootDelay = new BootDelayMatch(new BootDelay(101, DateTime.UtcNow, name, null, entry.TargetPath, 9000, 6000), 3);
        return item;
    }

    [Fact]
    public void Extra_autostart_is_enabled_allowed_and_slow_or_missing()
    {
        Assert.True(StartupViewModel.IsExtra(Item("a", slow: true)));
        Assert.True(StartupViewModel.IsExtra(Item("b", exists: false)));
        Assert.False(StartupViewModel.IsExtra(Item("c")));
    }

    [Fact]
    public void Extra_autostart_never_includes_disabled_or_protected_entries()
    {
        Assert.False(StartupViewModel.IsExtra(Item("d", enabled: false, slow: true)));
        Assert.False(StartupViewModel.IsExtra(Item("e", risk: StartupRisk.Security, slow: true)));
        Assert.False(StartupViewModel.IsExtra(Item("f", risk: StartupRisk.Security, exists: false)));
    }

    [Fact]
    public void Read_only_mode_does_not_disable_the_autostart_switch()
    {
        // The switch is offered whatever the rights; a click calls RequireAdmin and the relaunched copy repeats it.
        Assert.True(Item("g").CanToggle);
        Assert.Null(Item("g").LockNote);
    }

    [Fact]
    public void A_protected_entry_says_why_in_text_next_to_the_switch()
    {
        var item = Item("h", risk: StartupRisk.Security);
        Assert.False(item.CanToggle);
        Assert.False(string.IsNullOrWhiteSpace(item.LockNote));
    }

    [Fact]
    public void Intent_list_round_trips_ids_with_odd_characters()
    {
        string[] ids = [@"RunUser|Discord", @"RunMachine|Мой\Путь (x86)", "a=b&c|d"];
        var packed = IntentList.Pack(ids);
        Assert.NotNull(packed);
        Assert.Matches("^[A-Za-z0-9+/=]+$", packed);
        Assert.Equal(ids, IntentList.Unpack(packed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    public void Intent_list_unpacks_nothing_from_empty_or_damaged_text(string? text) =>
        Assert.Empty(IntentList.Unpack(text));

    [Fact]
    public void Intent_list_drops_empty_and_duplicate_ids()
    {
        Assert.Null(IntentList.Pack([]));
        Assert.Equal(["a", "b"], IntentList.Unpack(IntentList.Pack(["a", "", "A", "b"])));
    }

    [Fact]
    public void Start_type_intent_parameter_round_trips_even_with_a_pipe_in_the_name()
    {
        var spec = ServicesViewModel.StartSpec("Some|Svc", ServiceStartMode.Manual, true);
        var parsed = ServicesViewModel.ParseStartSpec(spec);
        Assert.Equal(("Some|Svc", ServiceStartMode.Manual, true), parsed);
        Assert.Null(ServicesViewModel.ParseStartSpec("Svc|NoSuchMode|0"));
        Assert.Null(ServicesViewModel.ParseStartSpec(null));
    }

    [Theory]
    [InlineData("ToggleStartup")]
    [InlineData("ToggleLogonTask")]
    [InlineData("DisableSelectedStartup")]
    [InlineData("EnableSelectedStartup")]
    [InlineData("DisableExtraStartup")]
    [InlineData("ApplyRecommendations")]
    [InlineData("ApplyTasks")]
    [InlineData("RevertService")]
    [InlineData("RevertTask")]
    [InlineData("ApplyStartType")]
    [InlineData("StartService")]
    [InlineData("StopService")]
    [InlineData("Refresh")]
    public void Services_page_has_every_command_the_intents_and_the_dashboard_call(string command)
    {
        var property = typeof(ServicesViewModel).GetProperty(command + "Command", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        Assert.True(typeof(ICommand).IsAssignableFrom(property!.PropertyType));
    }

    [Fact]
    public void Startup_intents_resolve_to_the_services_page()
    {
        Assert.Equal("services", MainViewModel.ResolvePageKey(new AdminIntent("startup", "ToggleStartup", "k").PageKey));
        Assert.Equal("services", MainViewModel.ResolvePageKey(new AdminIntent("services", "ApplyRecommendations").PageKey));
    }
}

public sealed class StartupOptionalAtLogonTests
{
    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", "", true)]
    [InlineData(@"C:\Users\u\AppData\Local\Discord\Update.exe", @"Update.exe --processStart Discord.exe", true)]
    [InlineData(@"C:\Program Files\Spotify\Spotify.exe", "", true)]
    [InlineData(@"C:\Program Files\Realtek\RtkAudUService64.exe", "", false)]
    [InlineData(@"C:\Apps\MyTool.exe", "", false)]
    public void Optional_programs_are_recognised(string target, string command, bool optional) =>
        Assert.Equal(optional, Lycoris.Modules.Startup.StartupClassifier.IsOptionalAtLogon(target, command, Lycoris.Modules.Startup.StartupRisk.Normal));

    [Theory]
    [InlineData(Lycoris.Modules.Startup.StartupRisk.Microsoft)]
    [InlineData(Lycoris.Modules.Startup.StartupRisk.Driver)]
    [InlineData(Lycoris.Modules.Startup.StartupRisk.Security)]
    [InlineData(Lycoris.Modules.Startup.StartupRisk.Updater)]
    public void Protected_kinds_are_never_optional(Lycoris.Modules.Startup.StartupRisk risk) =>
        Assert.False(Lycoris.Modules.Startup.StartupClassifier.IsOptionalAtLogon(@"C:\Steam\steam.exe", "", risk));
}
