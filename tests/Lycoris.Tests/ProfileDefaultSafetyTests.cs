using Lycoris.Modules.Optimize;
using Lycoris.Tweaks;

namespace Lycoris.Tests;

public sealed class ProfileDefaultSafetyTests
{
    [Theory]
    [InlineData("power-plan-performance")]
    [InlineData("fast-startup-off")]
    [InlineData("wifi-power-saving-off")]
    [InlineData("gamedvr-capture-off")]
    [InlineData("wu-active-hours")]
    [InlineData("storage-sense-on")]
    public void Preferences_requiring_individual_choice_are_optional_and_excluded_from_every_preset(string id)
    {
        var tweak = TweakCatalog.Find(id)!;
        Assert.Equal(TweakRisk.Optional, tweak.Risk);
        Assert.Equal(ProfileSet.None, tweak.DefaultIn);
        foreach (var profile in Enum.GetValues<ProfileKind>())
            Assert.DoesNotContain(TweakCatalog.DefaultsFor(profile), t => t.Id == id);
    }

    [Fact]
    public void Storage_sense_warns_of_future_data_loss_and_is_excluded_from_bulk_selection()
    {
        var tweak = TweakCatalog.Find("storage-sense-on")!;
        var row = new TweakItemViewModel(tweak, new OptimizeViewModel()) { State = TweakState.NotApplied };

        Assert.True(row.HasDataLoss);
        Assert.False(SectionRow.IsSelectable(row));
        Assert.Contains("временные файлы", row.UndoText);
        Assert.Contains("корзины", row.UndoText);
        Assert.Contains("«Загрузок»", row.UndoText);
        Assert.Contains("не восстановит удалённые файлы", row.UndoText);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, false)]
    public void Hags_requires_confirmed_system_support(bool? support, bool expected)
    {
        Assert.Equal(expected, TweakCatalog.HagsSupported(Caps(support)));
    }

    [Theory]
    [InlineData(null, null, false, TweakState.Unknown)]
    [InlineData(1, null, false, TweakState.Unknown)]
    [InlineData(2, null, false, TweakState.Unknown)]
    [InlineData(null, false, false, TweakState.NotApplicable)]
    [InlineData(2, false, false, TweakState.NotApplicable)]
    [InlineData(null, true, false, TweakState.NotApplied)]
    [InlineData(null, true, true, TweakState.Applied)]
    [InlineData(1, true, true, TweakState.NotApplied)]
    [InlineData(2, true, false, TweakState.Applied)]
    public void Hags_detection_distinguishes_unknown_support_from_disabled(int? mode, bool? support, bool enabled, TweakState expected)
    {
        Assert.Equal(expected, TweakCatalog.DetectHags(mode, Caps(support, enabled)));
    }

    [Theory]
    [InlineData(null, null, false, true, 0)]
    [InlineData(1, false, false, true, 0)]
    [InlineData(2, null, false, false, 0)]
    [InlineData(2, false, false, false, 0)]
    [InlineData(null, true, false, false, 1)]
    [InlineData(null, true, true, false, 0)]
    [InlineData(1, true, true, false, 1)]
    [InlineData(2, true, false, false, 0)]
    public void Hags_apply_writes_only_when_support_is_confirmed_and_enablement_is_needed(
        int? mode, bool? support, bool enabled, bool rejects, int expectedWrites)
    {
        int writes = 0;
        var error = Record.Exception(() => TweakCatalog.ApplyHags(mode, Caps(support, enabled), () => writes++));

        if (rejects) Assert.IsType<InvalidOperationException>(error);
        else Assert.Null(error);
        Assert.Equal(expectedWrites, writes);
    }

    private static TweakCatalog.GpuSchedulingCaps? Caps(bool? support, bool enabled = false) =>
        support is { } supported ? new TweakCatalog.GpuSchedulingCaps(supported, enabled) : null;
}
