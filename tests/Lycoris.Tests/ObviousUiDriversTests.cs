using System.Reflection;
using System.Windows.Input;
using Lycoris.Modules.Drivers;
using Lycoris.Shell;
using Xunit;

namespace Lycoris.Tests;

/// <summary>«Obvious UI» for Драйверы: the admin-intent targets and shortcut members exist on the page.</summary>
public class ObviousUiDriversTests
{
    [Theory]
    [InlineData("DownloadDriver")]
    [InlineData("CheckGpu")]
    [InlineData("InstallHardware")]
    [InlineData("InstallUpdates")]
    [InlineData("ExportBackup")]
    [InlineData("RestoreBackup")]
    [InlineData("DeleteBackup")]
    [InlineData("EnableDevice")]
    [InlineData("UseGpuTool")]
    [InlineData("UseIntelDsa")]
    [InlineData("RunPrimary")]
    [InlineData("Refresh")]
    public void Drivers_page_has_every_command_the_intents_and_the_dashboard_call(string command)
    {
        var p = typeof(DriversViewModel).GetProperty(command + "Command", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(p);
        Assert.True(typeof(ICommand).IsAssignableFrom(p!.PropertyType));
    }

    [Theory]
    [InlineData("GpuTileText")]
    [InlineData("GpuUpdateAvailable")]
    [InlineData("PrimaryText")]
    [InlineData("HasPrimary")]
    public void Drivers_page_exposes_the_dashboard_tile_members(string member) =>
        Assert.NotNull(typeof(DriversViewModel).GetProperty(member, BindingFlags.Public | BindingFlags.Instance));

    [Fact]
    public void Drivers_intents_resolve_to_the_drivers_page() =>
        Assert.Equal("drivers", MainViewModel.ResolvePageKey(new AdminIntent("drivers", "InstallHardware", "x").PageKey));

    [Fact]
    public void Four_tabs_old_links_still_select_a_tab()
    {
        // The enum keeps every old value (palette links, --tab, Hardware actions); Overview/Backup fold into two of the four tabs.
        foreach (var name in new[] { "Hardware", "Overview", "Updates", "Problems", "Installed", "Backup" })
            Assert.True(Enum.TryParse<DriversTab>(name, true, out _));
    }
}
