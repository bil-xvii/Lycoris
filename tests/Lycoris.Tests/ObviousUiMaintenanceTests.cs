using System.Reflection;
using System.Windows.Input;
using Lycoris.Modules.Cleaner;
using Lycoris.Modules.Maintenance;
using Lycoris.Modules.Tools;
using Lycoris.Shell;
using Xunit;

namespace Lycoris.Tests;

/// <summary>«Obvious UI» for Обслуживание: shortcuts and the admin-intent replay find their commands on the host page.</summary>
public class ObviousUiMaintenanceTests
{
    private static bool HasCommand(Type page, string name) =>
        page.GetProperty(name + "Command", BindingFlags.Public | BindingFlags.Instance) is { } p && typeof(ICommand).IsAssignableFrom(p.PropertyType);

    [Theory]
    [InlineData("Clean")]
    [InlineData("Analyze")]
    [InlineData("RunTool")]
    [InlineData("RepairWindows")]
    public void Maintenance_page_exposes_the_commands_the_intents_and_dashboard_call(string command) =>
        Assert.True(HasCommand(typeof(MaintenanceViewModel), command));

    [Theory]
    [InlineData("Clean")]
    [InlineData("GetRights")]
    [InlineData("Refresh")]
    public void Cleaner_has_its_header_and_row_commands(string command) =>
        Assert.True(HasCommand(typeof(CleanerViewModel), command));

    [Theory]
    [InlineData("RunTool")]
    [InlineData("RepairWindows")]
    [InlineData("Refresh")]
    [InlineData("Run")]
    public void Tools_has_its_commands(string command) =>
        Assert.True(HasCommand(typeof(ToolsViewModel), command));

    [Fact]
    public void Cleaner_admin_intent_resolves_to_the_maintenance_page_and_its_clean_command()
    {
        var intent = new AdminIntent("cleaner", "Clean");
        Assert.Equal("maintenance", MainViewModel.ResolvePageKey(intent.PageKey));
        Assert.NotNull(intent.FindCommandOnType(typeof(MaintenanceViewModel)));
    }

    [Theory]
    [InlineData("666,6", "МБ", "666 МБ")]
    [InlineData("4,7", "ГБ", "4,7 ГБ")]
    [InlineData("12,3", "ГБ", "12 ГБ")]
    [InlineData("980", "КБ", "980 КБ")]
    public void Sidebar_badge_is_short(string value, string unit, string expected) =>
        Assert.Equal(expected, CleanerViewModel.BadgeFor(value, unit));
}

internal static class AdminIntentTestExtensions
{
    /// <summary>The «{Command}Command» property of a page type (the replay finds it by reflection on the instance).</summary>
    public static PropertyInfo? FindCommandOnType(this AdminIntent intent, Type page) =>
        page.GetProperty(intent.Command + "Command", BindingFlags.Public | BindingFlags.Instance);
}
