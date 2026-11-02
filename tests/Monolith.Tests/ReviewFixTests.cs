using Monolith.Modules.Startup;
using Monolith.Shell;
using Xunit;

namespace Monolith.Tests;

/// <summary>Review fixes: only listed commands replay, and a toggle replay carries the desired state.</summary>
public class ReviewFixTests
{
    [Fact]
    public void Unknown_replay_command_is_refused()
    {
        Assert.Null(AdminIntent.Parse(["--replay-page", "optimize", "--replay-command", "SomethingNew"]));
        Assert.NotNull(AdminIntent.Parse(["--replay-page", "optimize", "--replay-command", "ApplyRecommended"]));
        Assert.False(new AdminIntent("optimize", "SomethingNew").TryExecuteOn(new object()));
    }

    [Theory]
    [InlineData(true, "off:k", false, "k")]
    [InlineData(false, "on:k", true, "k")]
    public void Toggle_parameter_round_trips(bool enabledNow, string packed, bool want, string key)
    {
        Assert.Equal(packed, StartupViewModel.ToggleParameter(enabledNow, key));
        Assert.Equal((want, key), StartupViewModel.ParseToggleParameter(packed));
    }

    [Fact]
    public void Plain_toggle_parameter_has_no_desired_state() =>
        Assert.Equal((null, "HKCU|x"), StartupViewModel.ParseToggleParameter("HKCU|x"));
}
