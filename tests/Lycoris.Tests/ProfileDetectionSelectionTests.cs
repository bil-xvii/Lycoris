using System.Collections.Concurrent;
using System.Reflection;
using Lycoris.Core;
using Lycoris.Modules.Optimize;
using Lycoris.Tweaks;

namespace Lycoris.Tests;

[Collection("WizardSettings")]
public sealed class ProfileDetectionSelectionTests
{
    private const string Generic = "power-plan-balanced";
    private const string DualCcd = "power-plan-balanced-x3d";
    private const string Personal = "manual-choice";

    [Theory]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", 16, true, DualCcd)]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", 8, true, Generic)]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", 16, false, null)]
    public void Saved_profile_reconciles_to_the_same_native_plan_as_reselect_and_cli(
        string cpu, int cores, bool factsKnown, string? expectedPlan)
    {
        using var settings = new SavedProfileScope();
        var system = Hardware(cpu, cores, factsKnown);
        var page = Page();
        page.SelectProfile(OptimizeProfile.Gaming);
        Assert.Equal([Generic], Checked(page)); // provisional catalog-order winner before facts arrive
        var (states, excluded) = DetectPlans(page, system);

        page.ApplyDetectedStates(states, excluded, factsKnown, laptop: false);

        var expected = expectedPlan is null ? Array.Empty<string>() : [expectedPlan];
        Assert.Equal(expected, Checked(page));
        Assert.Equal(expected, Pending(page));
        page.SelectProfile(OptimizeProfile.Gaming);
        Assert.Equal(expected, Checked(page));
        Assert.Equal(expected, Pending(page));

        var cli = Page().PendingForProfile(OptimizeProfile.Gaming, states, excluded);
        Assert.Equal(expected, cli.Select(i => i.Id));
    }

    [Fact]
    public void A_manual_selection_made_while_detection_runs_is_preserved()
    {
        using var settings = new SavedProfileScope();
        var system = Hardware("AMD Ryzen 9 9950X3D 16-Core Processor", 16, known: true);
        var page = Page();
        page.SelectProfile(OptimizeProfile.Gaming);
        page.Items.Single(i => i.Id == Personal).IsChecked = true;
        Assert.Equal(OptimizeProfile.Custom, page.Profile);
        var (states, excluded) = DetectPlans(page, system);

        page.ApplyDetectedStates(states, excluded, hardwareFactsKnown: true, laptop: false);

        // The inapplicable provisional plan is removed. The hardware-specific preset that the user left stays unchecked.
        Assert.Equal(OptimizeProfile.Custom, page.Profile);
        Assert.Equal([Personal], Checked(page));
        Assert.Equal([Personal], Pending(page));
    }

    [Fact]
    public void The_newly_selected_preset_wins_when_detection_finishes()
    {
        using var settings = new SavedProfileScope();
        var system = Hardware("AMD Ryzen 9 9950X3D 16-Core Processor", 16, known: true);
        var page = Page();
        page.SetProfileDefaults(OptimizeProfile.Normal, [Generic, DualCcd, Personal]);
        page.SelectProfile(OptimizeProfile.Gaming);
        page.SelectProfile(OptimizeProfile.Normal);
        var (states, excluded) = DetectPlans(page, system);

        page.ApplyDetectedStates(states, excluded, hardwareFactsKnown: true, laptop: false);

        Assert.Equal(OptimizeProfile.Normal, page.Profile);
        Assert.Equal([DualCcd, Personal], Pending(page));
    }

    [Fact]
    public void Cli_planning_leaves_the_saved_tile_untouched()
    {
        using var settings = new SavedProfileScope();
        AppSettings.Current.LastProfile = nameof(OptimizeProfile.Normal);
        var system = Hardware("AMD Ryzen 7 9800X3D 8-Core Processor", 8, known: true);
        var page = Page();
        var (states, excluded) = DetectPlans(page, system);

        var pending = page.PendingForProfile(OptimizeProfile.Gaming, states, excluded);

        Assert.Equal([Generic], pending.Select(i => i.Id));
        Assert.Equal(nameof(OptimizeProfile.Normal), AppSettings.Current.LastProfile);
    }

    private static OptimizeViewModel Page()
    {
        var page = new OptimizeViewModel();
        page.BuildCatalog([
            TweakCatalog.Find(Generic)!, TweakCatalog.Find(DualCcd)!,
            new CustomTweak
            {
                Id = Personal, Title = "Manual", Description = "",
                DetectFunc = () => TweakState.NotApplied, ApplyAction = _ => throw new InvalidOperationException()
            }
        ]);
        page.SetProfileDefaults(OptimizeProfile.Gaming, [Generic, DualCcd]);
        return page;
    }

    private static (Dictionary<string, TweakState> States, string[] Excluded) DetectPlans(OptimizeViewModel page, SystemProfile system)
    {
        var applicability = page.Items.ToDictionary(i => i.Id, i => TweakEngine.Applicability(i.Tweak, system));
        // State probes are fixtures. Only catalog hardware predicates run, so no live power plan is detected.
        return (applicability.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            true => TweakState.NotApplied,
            false => TweakState.NotApplicable,
            null => TweakState.Unknown,
        }), applicability.Where(pair => pair.Value is not true).Select(pair => pair.Key).ToArray());
    }

    private static SystemProfile Hardware(string cpu, int cores, bool known)
    {
        var system = new SystemProfile();
        typeof(SystemProfile).GetProperty(nameof(SystemProfile.Identity))!.SetValue(system, ComputerIdentity.FromFacts("Vendor", "Desktop", 1, [3]));
        typeof(SystemProfile).GetProperty(nameof(SystemProfile.CpuName))!.SetValue(system, cpu);
        typeof(SystemProfile).GetProperty(nameof(SystemProfile.CpuCores))!.SetValue(system, cores);
        if (known)
        {
            var sections = (ConcurrentDictionary<string, bool>)typeof(SystemProfile)
                .GetField("_sections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system)!;
            foreach (var section in new[] { "cpu", "gpu", "chassis", "battery" }) sections[section] = true;
        }
        return system;
    }

    private static string[] Checked(OptimizeViewModel page) => page.Items.Where(i => i.IsChecked).Select(i => i.Id).ToArray();
    private static string[] Pending(OptimizeViewModel page) => page.Items.Where(i => i.IsPending).Select(i => i.Id).ToArray();

    private sealed class SavedProfileScope : IDisposable
    {
        private readonly string _before = AppSettings.Current.LastProfile;
        public void Dispose() => AppSettings.Current.LastProfile = _before;
    }
}
