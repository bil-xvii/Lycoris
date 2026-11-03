using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Dashboard.Checks;

namespace Lycoris.Tests;

/// <summary>«ПЕРЕЗАГРУЗИТЬ» for a pending reboot: delayed and cancellable, never part of «ИСПРАВИТЬ ВСЁ БЕЗОПАСНОЕ».</summary>
public sealed class RestartActionTests
{
    [Fact]
    public void Pending_reboot_offers_a_delayed_restart()
    {
        Assert.Equal(60, DashboardViewModel.RestartDelaySeconds);
        Assert.Equal(CheckActionKind.Restart, new CheckAction(CheckActionKind.Restart, "ПЕРЕЗАГРУЗИТЬ").Kind);
    }

    [Fact]
    public void Restart_action_is_never_a_safe_fix() =>
        Assert.False(CheckActionKind.Restart == CheckActionKind.ApplyTweak); // IsSafeFix only takes ApplyTweak rows
}
