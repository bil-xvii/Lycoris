using Lycoris.Core;
using Lycoris.Modules.Dashboard.Checks;

namespace Lycoris.Tests;

/// <summary>Pure disk-space advice from synthetic identity and file-size facts; no Windows operations.</summary>
public sealed class HibernationIdentityAdviceTests
{
    private const ulong Gb = 1UL << 30;

    private static SystemSnapshot LowSpace(ComputerIdentity identity) => new()
    {
        Identity = identity,
        HardwareFactsKnown = true,
        IsLaptop = false, // Deliberately simulate the old false default: identity must also be verified.
        SystemDriveFree = 12 * Gb,
        SystemDriveTotal = 238 * Gb,
        HiberfilBytes = 13 * Gb,
    };

    private static void AssertNoHibernationAdvice(SystemSnapshot snapshot)
    {
        using var _ = Loc.UseLanguage("ru");
        var result = HealthRules.CheckDiskSpace(snapshot);
        Assert.NotNull(result);
        Assert.Equal(CheckSeverity.Warning, result.Severity);
        Assert.Contains("hiberfil.sys — 13,0 ГБ", result.Detail);
        Assert.DoesNotContain("Отключить гибернацию", result.Detail);
        Assert.Equal("cleaner", result.Action!.Target);
        Assert.Null(result.SecondaryAction);
        Assert.Null(HealthRules.SystemFileLever(snapshot));
    }

    [Fact]
    public void Unknown_identity_never_offers_hibernation_off_even_when_legacy_facts_succeeded() =>
        AssertNoHibernationAdvice(LowSpace(ComputerIdentity.Unknown));

    [Fact]
    public void Conflicting_identity_never_offers_hibernation_off_even_when_legacy_facts_succeeded() =>
        AssertNoHibernationAdvice(LowSpace(ComputerIdentity.FromFacts("Vendor", "Conflicting", 1, [9])));

    [Theory]
    [InlineData("Vendor", "All-in-one", 1, 13)]
    [InlineData("Microsoft Corporation", "Virtual Machine", 1, 3)]
    [InlineData("Vendor", "Notebook", 2, 9)]
    [InlineData("Vendor", "Tablet", 2, 30)]
    [InlineData("Vendor", "Convertible", 2, 31)]
    [InlineData("Vendor", "Detachable", 2, 32)]
    [InlineData("Vendor", "Server", 7, 23)]
    public void Confirmed_non_desktops_do_not_receive_hibernation_off_advice(string manufacturer, string model,
        int pcType, int chassis)
    {
        var identity = ComputerIdentity.FromFacts(manufacturer, model, (ushort)pcType, [(ushort)chassis]);
        AssertNoHibernationAdvice(LowSpace(identity));
    }

    [Fact]
    public void Confirmed_desktop_retains_the_hibernation_shortcut_and_low_space_warning()
    {
        var snapshot = LowSpace(ComputerIdentity.FromFacts("Vendor", "Desktop", 1, [3]));
        var result = HealthRules.CheckDiskSpace(snapshot)!;
        Assert.Equal(CheckSeverity.Warning, result.Severity);
        Assert.Equal("cleaner", result.Action!.Target);
        Assert.Equal(HealthRules.TweakHibernationOff, result.SecondaryAction!.Target);
        Assert.Equal(CheckActionKind.FocusTweak, result.SecondaryAction.Kind);
        Assert.False(result.SecondaryAction.RequiresAdmin);
        Assert.Equal(HealthRules.TweakHibernationOff, HealthRules.SystemFileLever(snapshot)!.Target);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Known_desktop_still_needs_successful_hardware_facts_and_no_laptop_flag(bool hardwareKnown, bool laptop)
    {
        var snapshot = LowSpace(ComputerIdentity.FromFacts("Vendor", "Desktop", 1, [3])) with
        {
            HardwareFactsKnown = hardwareKnown,
            IsLaptop = laptop,
        };
        Assert.Null(HealthRules.SystemFileLever(snapshot));
        Assert.Null(HealthRules.CheckDiskSpace(snapshot)!.SecondaryAction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verified_manual_pagefile_problem_keeps_its_existing_repair_when_hibernation_is_ineligible(bool conflict)
    {
        var identity = conflict ? ComputerIdentity.FromFacts("Vendor", "Conflicting", 1, [9]) : ComputerIdentity.Unknown;
        var snapshot = LowSpace(identity) with { PagefileBytes = 2 * Gb, PagefileSystemManaged = false, PagefileOnOtherDrive = false };

        var result = HealthRules.CheckDiskSpace(snapshot)!;

        Assert.Equal(CheckSeverity.Warning, result.Severity);
        Assert.Equal(HealthRules.TweakPagefileSystemManaged, result.SecondaryAction!.Target);
        Assert.Equal(HealthRules.TweakPagefileSystemManaged, HealthRules.SystemFileLever(snapshot)!.Target);
        Assert.Equal(CheckActionKind.FocusTweak, result.SecondaryAction.Kind);
    }
}
