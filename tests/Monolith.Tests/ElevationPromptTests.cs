using Monolith.Shell;
using Xunit;

namespace Monolith.Tests;

public class ElevationPromptTests
{
    [Fact]
    public void Prompt_names_the_action_UAC_and_the_same_page()
    {
        var text = PageViewModel.ElevationPromptText("Применение оптимизаций");
        Assert.Contains("«Применение оптимизаций»", text);
        Assert.Contains("UAC", text);
        Assert.Contains("этой же странице", text);
    }

    [Fact]
    public void Elevation_at_startup_is_off_by_default()
    {
        Assert.False(new Monolith.Core.AppSettings().ElevateAtStartup);
    }
}
