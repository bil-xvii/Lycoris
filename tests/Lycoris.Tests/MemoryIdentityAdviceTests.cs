using Lycoris.Core;
using Lycoris.Modules.Dashboard.Checks;

namespace Lycoris.Tests;

/// <summary>Pure memory advice from synthetic snapshots; no WMI queries or firmware/system changes.</summary>
public sealed class MemoryIdentityAdviceTests
{
    private static SystemSnapshot SlowMemory(ComputerIdentity identity) => new()
    {
        Identity = identity,
        IsLaptop = identity.IsPortable,
        RamModules = [new RamModule("F5-6000J3636F16G", 6000, 4800, 34, 16UL << 30)],
    };

    [Theory]
    [InlineData("Vendor", "All-in-one", 1, 13)]
    [InlineData("Microsoft Corporation", "Virtual Machine", 1, 3)]
    [InlineData("", "Unknown", 0, 0)]
    [InlineData("Vendor", "Conflicting", 1, 9)]
    [InlineData("Vendor", "Notebook", 2, 9)]
    [InlineData("Vendor", "Tablet", 2, 30)]
    [InlineData("Vendor", "Convertible", 2, 31)]
    [InlineData("Vendor", "Detachable", 2, 32)]
    [InlineData("Vendor", "Server", 7, 23)]
    public void Other_or_uncertain_computer_types_show_factual_speeds_without_firmware_instructions(
        string manufacturer, string model, int pcType, int chassis)
    {
        var identity = ComputerIdentity.FromFacts(manufacturer, model, (ushort)pcType, [(ushort)chassis]);
        var result = HealthRules.CheckRam(SlowMemory(identity));

        Assert.NotNull(result);
        Assert.Equal(CheckSeverity.Info, result.Severity);
        Assert.Contains("6000", result.Detail);
        Assert.Contains("4800", result.Detail);
        foreach (var instruction in new[] { "XMP", "EXPO", "F10", "BIOS", "UEFI" })
            Assert.DoesNotContain(instruction, result.Detail);
        Assert.Null(result.Action);
        Assert.Null(result.SecondaryAction);
    }

    [Fact]
    public void Confirmed_desktop_has_conditional_manual_advice_and_no_automatic_action()
    {
        using var _ = Loc.UseLanguage("ru");
        var desktop = ComputerIdentity.FromFacts("Vendor", "Desktop", 1, [3]);
        var result = HealthRules.CheckRam(SlowMemory(desktop));

        Assert.NotNull(result);
        Assert.Equal(CheckSeverity.Warning, result.Severity);
        Assert.Contains("6000", result.Detail);
        Assert.Contains("4800", result.Detail);
        Assert.Contains("Если производители", result.Detail);
        Assert.Contains("XMP/EXPO", result.Detail);
        Assert.Contains("вручную", result.Detail);
        Assert.DoesNotContain("выключен профиль", result.Detail);
        Assert.DoesNotContain("F10", result.Detail);
        Assert.Null(result.Action);
        Assert.Null(result.SecondaryAction);
    }

    [Fact]
    public void Snapshot_without_identity_does_not_assume_desktop_and_frequency_analysis_keeps_capacity()
    {
        var snapshot = SlowMemory(ComputerIdentity.Unknown) with
        {
            RamModules = [new RamModule("", 6000, 4800, 34, 16UL << 30), new RamModule("", 6000, 4800, 34, 16UL << 30)],
        };
        Assert.False(new SystemSnapshot().Identity.IsKnown);
        var verdict = HealthRules.AnalyzeRam(snapshot.RamModules);
        Assert.NotNull(verdict);
        Assert.Equal(6000, verdict.Rated);
        Assert.Equal(4800, verdict.Configured);
        Assert.Equal(32UL << 30, verdict.Capacity);
        Assert.Equal(2, verdict.Modules);
        Assert.Equal(CheckSeverity.Info, HealthRules.CheckRam(snapshot)!.Severity);
    }

    [Fact]
    public void Nominal_speed_verdict_is_preserved_for_unknown_computer_type()
    {
        var snapshot = new SystemSnapshot { RamModules = [new RamModule("", 6000, 6000, 34, 16UL << 30)] };
        Assert.Equal(CheckSeverity.Ok, HealthRules.CheckRam(snapshot)!.Severity);
    }

    [Fact]
    public void Neutral_and_manual_advice_are_translated_in_english()
    {
        using var _ = Loc.UseLanguage("en");
        foreach (var identity in new[] { ComputerIdentity.Unknown, ComputerIdentity.FromFacts("Vendor", "Desktop", 1, [3]) })
        {
            var result = HealthRules.CheckRam(SlowMemory(identity))!;
            Assert.DoesNotMatch("[А-Яа-яЁё]", result.Detail);
            Assert.Contains("6000", result.Detail);
            Assert.Contains("4800", result.Detail);
        }
    }
}
