using Monolith.Modules.Dashboard;

namespace Monolith.Tests;

/// <summary>Live polling must stop whenever nobody can see it (docs/perf-2026-11.md).</summary>
public sealed class PerfPollingTests
{
    [Fact]
    public void Dashboard_monitor_stops_when_the_window_is_minimized_and_when_the_page_is_left()
    {
        var vm = new DashboardViewModel();
        try
        {
            vm.StartMonitor();
            Assert.True(vm.IsMonitorRunning);

            vm.SuspendForMinimize();
            Assert.False(vm.IsMonitorRunning);

            vm.StartMonitor();
            Assert.True(vm.IsMonitorRunning);
            vm.Deactivate();
            Assert.False(vm.IsMonitorRunning);
        }
        finally { vm.Deactivate(); }
    }

    [Fact]
    public void Restoring_the_window_does_not_start_the_monitor_before_the_first_load()
    {
        var vm = new DashboardViewModel();
        vm.ResumeFromMinimize();
        Assert.False(vm.IsMonitorRunning);
    }
}
