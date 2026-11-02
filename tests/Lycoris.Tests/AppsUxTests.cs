using Lycoris.Modules.Apps;
using Xunit;

namespace Lycoris.Tests;

/// <summary>Obvious UI for the Apps page: states are text, only real actions are buttons; the sidebar badge counts recommendations.</summary>
public sealed class AppsUxTests
{
    private static CatalogEntry E(string id) => AppCatalog.Find(id) ?? throw new InvalidOperationException(id);

    [Fact]
    public void An_installable_program_has_a_button_with_an_icon()
    {
        var steam = new AppItemViewModel(E("Valve.Steam")) { IsKnown = true, WingetReady = true };
        Assert.True(steam.HasMainAction);
        Assert.Equal("УСТАНОВИТЬ", steam.ActionText);
        Assert.NotEmpty(steam.ActionGlyph);
        Assert.Equal("", steam.StateCaption);
    }

    [Fact]
    public void A_built_in_component_shows_a_state_not_a_greyed_button()
    {
        var edge = new AppItemViewModel(E("Microsoft.Edge")) { IsKnown = true, WingetReady = true };
        Assert.False(edge.HasMainAction);
        Assert.Equal("ВСТРОЕН В WINDOWS", edge.StateCaption);
    }

    [Fact]
    public void Badge_counts_recommended_programs_and_is_empty_for_none()
    {
        var plain = new AppItemViewModel(E("Valve.Steam")) { IsKnown = true };
        Assert.Null(AppsViewModel.BadgeFor([plain]));

        var advised = new AppItemViewModel(E("Valve.Steam")) { IsKnown = true, Advice = new AppAdvice(AdviceLevel.Recommended, "Тест.") };
        Assert.Equal("1", AppsViewModel.BadgeFor([plain, advised]));
    }
}
