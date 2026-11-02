using Lycoris.Modules.Optimize;
using Lycoris.Tweaks;

namespace Lycoris.Tests;

/// <summary>
/// «Obvious UI» logic of the Optimize page: a tile's own «ПРИМЕНИТЬ» button, the always-visible «сейчас → станет» line,
/// the «ЭКСПЕРТ» segment, the chip filter and buttons that stay enabled without admin rights. Synthetic catalog, nothing is written.
/// Windows only (the page owns WPF timers), like TweakSelectionTests.
/// </summary>
public sealed class OptimizeUxTests
{
    private static CustomTweak Tweak(string id, TweakCategory category = TweakCategory.Power, TweakRisk risk = TweakRisk.Safe) => new()
    {
        Id = id,
        Title = "Пункт " + id.ToUpperInvariant(),
        Description = "",
        Category = category,
        Risk = risk,
        DetectFunc = () => TweakState.NotApplied,
        ApplyAction = _ => { },
    };

    private static OptimizeViewModel Page(params Tweak[] tweaks)
    {
        var vm = new OptimizeViewModel();
        vm.BuildCatalog(tweaks);
        foreach (var i in vm.Items) i.State = TweakState.NotApplied;
        return vm;
    }

    [Fact]
    public void A_tile_button_says_what_it_applies_and_only_selects_when_there_is_nothing_to_change()
    {
        var vm = Page(Tweak("a"), Tweak("b"), Tweak("c"));
        vm.SetProfileDefaults(OptimizeProfile.Gaming, ["a", "b"]);
        vm.SetProfileDefaults(OptimizeProfile.Normal, []);
        vm.MarkDetected();

        var gaming = vm.Profiles.Single(t => t.Kind == OptimizeProfile.Gaming);
        Assert.Equal("ПРИМЕНИТЬ · 2", gaming.ActText);
        Assert.True(gaming.ActApplies);

        var normal = vm.Profiles.Single(t => t.Kind == OptimizeProfile.Normal);
        Assert.Equal("ВЫБРАТЬ", normal.ActText);
        Assert.False(normal.ActApplies);

        var custom = vm.Profiles.Single(t => t.Kind == OptimizeProfile.Custom);
        Assert.Equal("ВЫБРАТЬ", custom.ActText);

        // The current tile shows everything pending, not only its own members.
        vm.SelectProfile(OptimizeProfile.Gaming);
        Assert.Equal("ПРИМЕНИТЬ · 2", gaming.ActText);
        Assert.Equal("ПРИМЕНИТЬ · 2", vm.ApplyButtonText);
    }

    [Fact]
    public void Apply_buttons_are_not_disabled_by_missing_admin_rights()
    {
        var vm = Page(Tweak("a"));
        vm.SetProfileDefaults(OptimizeProfile.Gaming, ["a"]);
        vm.MarkDetected();
        vm.SelectProfile(OptimizeProfile.Gaming);

        Assert.True(vm.CanModify, "the click asks for admin rights (RequireAdmin) and repeats itself after the relaunch");
        Assert.True(vm.ApplyCommand.CanExecute(null));
        Assert.True(vm.ApplyProfileCommand.CanExecute("Gaming"));
        Assert.True(vm.ApplyRecommendedCommand.CanExecute(null));
        Assert.All(vm.Items, i => Assert.True(i.RevertCommand.CanExecute(null)));
    }

    [Fact]
    public void The_preview_line_shows_only_for_a_row_that_would_change_and_has_its_current_value()
    {
        var vm = Page(Tweak("a"));
        var row = vm.Items[0];
        Assert.False(row.HasPreview, "values are not read yet");

        row.Changes = [new ChangeLineViewModel("HKLM\\x", "чтение…", "1")];
        Assert.False(row.HasPreview);

        row.Changes = [new ChangeLineViewModel("HKLM\\x", "0", "1"), new ChangeLineViewModel("HKLM\\y", "0", "1")];
        Assert.True(row.HasPreview);
        Assert.Equal("0", row.PreviewCurrent);
        Assert.Equal("1", row.PreviewNew);
        Assert.Contains("+1", row.PreviewMore);

        row.State = TweakState.Applied;
        Assert.False(row.HasPreview, "nothing to change on an applied row");
    }

    [Fact]
    public void Expert_items_live_in_their_own_segment_and_a_search_still_finds_them()
    {
        var vm = Page(Tweak("a"), Tweak("e1", risk: TweakRisk.Advanced), Tweak("e2", risk: TweakRisk.Advanced));
        Assert.Equal(["ВСЕ", "ПИТАНИЕ", "ЭКСПЕРТ"], vm.Categories.Select(c => c.Title).ToArray());
        Assert.Equal(1, vm.Categories[0].Count);
        Assert.Equal(2, vm.Categories[^1].Count);

        vm.SelectCategory(null);
        Assert.DoesNotContain(vm.Rows.OfType<TweakItemViewModel>(), i => i.IsAdvanced);

        vm.SelectExpertTab();
        Assert.True(vm.ExpertTab);
        Assert.Equal(["e1", "e2"], vm.Rows.OfType<TweakItemViewModel>().Select(i => i.Id).ToArray());
        Assert.Single(vm.Rows.OfType<ExpertRow>());
        Assert.True(vm.Categories[^1].IsSelected);
        Assert.False(vm.Categories[0].IsSelected);

        vm.SelectCategory(null);
        vm.SearchText = "Пункт E1";
        vm.ClearSearchCommand.Execute(null);
        Assert.False(vm.ExpertTab);
    }

    [Fact]
    public void The_chip_keeps_only_rows_that_would_change_and_clear_selection_unchecks_everything()
    {
        var vm = Page(Tweak("a"), Tweak("b"));
        vm.Items[1].State = TweakState.Applied;
        vm.SelectCategory(null);
        Assert.Equal(2, vm.Rows.OfType<TweakItemViewModel>().Count());

        vm.OnlyPending = true;
        Assert.Equal(["a"], vm.Rows.OfType<TweakItemViewModel>().Select(i => i.Id).ToArray());

        vm.ClearFilterCommand.Execute(null);
        Assert.False(vm.OnlyPending);
        Assert.Equal(2, vm.Rows.OfType<TweakItemViewModel>().Count());

        vm.Items[0].IsChecked = true;
        Assert.Equal(1, vm.SelectedCount);
        vm.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.DoesNotContain(vm.Items, i => i.IsChecked);
    }

    [Fact]
    public void A_section_has_a_visible_select_all_caption()
    {
        var vm = Page(Tweak("a"), Tweak("b"));
        vm.SelectCategory(null);
        var section = vm.Rows.OfType<SectionRow>().Single();
        Assert.Equal("ОТМЕТИТЬ ВСЕ", section.ToggleAllText);
        vm.ToggleSection(section);
        Assert.Equal("СНЯТЬ ВСЕ", section.ToggleAllText);
    }
}
