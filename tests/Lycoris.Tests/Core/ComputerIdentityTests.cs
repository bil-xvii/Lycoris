using Lycoris.Core;

namespace Lycoris.Tests.Core;

public sealed class ComputerIdentityTests
{
    [Theory]
    [InlineData(1, 3, ComputerKind.Desktop)]
    [InlineData(3, 7, ComputerKind.Desktop)]
    [InlineData(2, 10, ComputerKind.Laptop)]
    [InlineData(1, 13, ComputerKind.AllInOne)]
    [InlineData(2, 30, ComputerKind.Tablet)]
    [InlineData(2, 31, ComputerKind.Convertible)]
    [InlineData(2, 32, ComputerKind.Detachable)]
    [InlineData(4, 23, ComputerKind.Server)]
    [InlineData(5, 28, ComputerKind.Server)]
    [InlineData(7, 17, ComputerKind.Server)]
    public void Compatible_smbios_sources_identify_the_form_factor(int pcType, int chassis, ComputerKind expected)
    {
        var identity = ComputerIdentity.FromFacts("Vendor", "Model", (ushort)pcType, [(ushort)chassis]);
        Assert.Equal(expected, identity.Kind);
        Assert.True(identity.IsKnown);
        Assert.False(identity.HasConflict);
    }

    [Fact]
    public void Desktop_with_ups_stays_desktop_and_legacy_battery_rule_is_unchanged()
    {
        Assert.False(SystemProfile.BatteryFromCaps(present: true, shortTerm: true));
        var identity = ComputerIdentity.FromFacts("Vendor", "Desktop", 1, [3]);
        Assert.Equal(ComputerKind.Desktop, identity.Kind);
    }

    [Fact]
    public void Notebook_without_battery_is_known_from_one_successful_smbios_source()
    {
        var identity = ComputerIdentity.FromFacts("Dell Inc.", "Latitude", 2, null, enclosureRead: false);
        Assert.Equal(ComputerKind.Laptop, identity.Kind);
        Assert.True(identity.IsKnown);
        Assert.False(SystemProfile.BatteryFromCaps(present: false, shortTerm: false));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 3)]
    [InlineData(4, 13)]
    [InlineData(3, 32)]
    public void Contradicting_sources_are_unknown_and_explain_the_conflict(int pcType, int chassis)
    {
        var identity = ComputerIdentity.FromFacts("Vendor", "Model", (ushort)pcType, [(ushort)chassis]);
        Assert.Equal(ComputerKind.Unknown, identity.Kind);
        Assert.False(identity.IsKnown);
        Assert.True(identity.HasConflict);
        using var _ = Loc.UseLanguage("ru");
        Assert.Contains("противоречат", identity.Reason);
        Assert.Contains(identity.Reason, identity.Summary);
        Assert.Contains(pcType.ToString(), identity.EvidenceText);
        Assert.Contains(chassis.ToString(), identity.EvidenceText);
    }

    [Fact]
    public void Multiple_conflicting_chassis_are_not_resolved_by_picking_first_or_last()
    {
        foreach (var chassis in new ushort[][] { [3, 10], [10, 3], [30, 32], [31, 32], [13, 9] })
        {
            var identity = ComputerIdentity.FromFacts("Vendor", "Model", null, chassis);
            Assert.Equal(ComputerKind.Unknown, identity.Kind);
            Assert.True(identity.HasConflict);
        }
    }

    [Theory]
    [InlineData(1, 3, 13, ComputerKind.AllInOne)]
    [InlineData(2, 9, 30, ComputerKind.Tablet)]
    [InlineData(2, 9, 31, ComputerKind.Convertible)]
    [InlineData(2, 9, 32, ComputerKind.Detachable)]
    public void Broad_chassis_and_compatible_subtype_use_the_more_precise_kind(int pcType, int broad, int subtype,
        ComputerKind expected)
    {
        foreach (var chassis in new ushort[][] { [(ushort)broad, (ushort)subtype], [(ushort)subtype, (ushort)broad] })
        {
            var identity = ComputerIdentity.FromFacts("Vendor", "Model", (ushort)pcType, chassis);
            Assert.Equal(expected, identity.Kind);
            Assert.True(identity.IsKnown);
            Assert.False(identity.HasConflict);
        }
    }

    [Theory]
    [InlineData(null, ComputerKind.Unknown)]
    [InlineData(1, ComputerKind.Desktop)]
    [InlineData(7, ComputerKind.Server)]
    public void Main_system_chassis_does_not_imply_a_server(int? pcType, ComputerKind expected)
    {
        var identity = ComputerIdentity.FromFacts("Vendor", "Model", pcType is { } value ? (ushort)value : null, [17]);
        Assert.Equal(expected, identity.Kind);
        Assert.False(identity.HasConflict);
        Assert.Equal(expected != ComputerKind.Unknown, identity.IsKnown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(999)]
    public void Missing_or_unrecognized_values_do_not_prove_desktop(int? pcType)
    {
        var identity = ComputerIdentity.FromFacts(null, null, pcType is { } value ? (ushort)value : null, [0, 1, 2, 999]);
        Assert.Equal(ComputerKind.Unknown, identity.Kind);
        Assert.False(identity.IsKnown);
        Assert.False(identity.HasConflict);
    }

    [Fact]
    public void Empty_or_failed_queries_are_not_positive_evidence()
    {
        Assert.False(ComputerIdentity.FromFacts("", "", null, []).IsKnown);
        var failed = ComputerIdentity.FromFacts("Microsoft Corporation", "Virtual Machine", 1, [3], false, false);
        Assert.False(failed.IsKnown);
        Assert.Empty(failed.Manufacturer);
        Assert.Empty(failed.Model);
        var enclosureOnly = ComputerIdentity.FromFacts("Old data", "Old data", 2, [3], false, true);
        Assert.Equal(ComputerKind.Desktop, enclosureOnly.Kind);
        Assert.False(enclosureOnly.HasConflict);
    }

    [Fact]
    public void Unrecognized_chassis_codes_do_not_override_recognized_values()
    {
        var identity = ComputerIdentity.FromFacts(null, null, 1, [999, 3, 0, 3]);
        Assert.Equal(ComputerKind.Desktop, identity.Kind);
        Assert.False(identity.HasConflict);
    }

    [Theory]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("System Product Name")]
    [InlineData("Default string")]
    [InlineData("Defaultstring")]
    [InlineData("Unknown")]
    [InlineData("  To be filled by O.E.M.  ")]
    public void Firmware_placeholders_are_not_presented_as_product_identity(string placeholder)
    {
        var identity = ComputerIdentity.FromFacts(placeholder, placeholder, 1, [3]);
        Assert.Empty(identity.Manufacturer);
        Assert.Empty(identity.Model);
        Assert.Equal(ComputerKind.Desktop, identity.Kind);
    }

    [Theory]
    [InlineData("VMware, Inc.", "VMware Virtual Platform")]
    [InlineData("Microsoft Corporation", "Virtual Machine")]
    [InlineData("innotek GmbH", "VirtualBox")]
    [InlineData("Oracle Corporation", "VirtualBox")]
    [InlineData("QEMU", "Standard PC (Q35 + ICH9, 2009)")]
    [InlineData("QEMU", "Standard PC (i440FX + PIIX, 1996)")]
    [InlineData("QEMU", "KVM")]
    [InlineData("Xen", "HVM domU")]
    [InlineData("Parallels International GmbH", "Parallels Virtual Platform")]
    public void Known_virtual_manufacturer_and_model_pairs_identify_guests(string manufacturer, string model)
    {
        Assert.Equal(ComputerKind.VirtualMachine, ComputerIdentity.FromFacts(manufacturer, model, 1, [3]).Kind);
    }

    [Theory]
    [InlineData("Microsoft Corporation", "Surface Laptop", 2, 10, ComputerKind.Laptop)]
    [InlineData("Microsoft Corporation", "Surface Pro", 2, 32, ComputerKind.Detachable)]
    [InlineData("ASUSTeK COMPUTER INC.", "Desktop Hyper-V host", 1, 3, ComputerKind.Desktop)]
    [InlineData("Unrelated vendor", "Virtual Machine", 1, 3, ComputerKind.Desktop)]
    [InlineData("VMware, Inc.", "Unrecognized model", 1, 3, ComputerKind.Desktop)]
    public void Physical_hosts_and_lookalike_names_do_not_prove_virtualization(string manufacturer, string model,
        int pcType, int chassis, ComputerKind expected)
    {
        Assert.Equal(expected, ComputerIdentity.FromFacts(manufacturer, model, (ushort)pcType, [(ushort)chassis]).Kind);
    }

    [Fact]
    public void Identity_does_not_retain_mutable_input_and_text_is_localized_at_display_time()
    {
        ushort[] chassis = [10];
        var identity = ComputerIdentity.FromFacts("  Dell   Inc. ", " Latitude ", 2, chassis);
        chassis[0] = 3;
        Assert.Equal("Dell Inc.", identity.Manufacturer);
        Assert.Equal(ComputerKind.Laptop, identity.Kind);
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("Laptop", identity.TypeText);
        Assert.Contains("ChassisTypes = 10", identity.EvidenceText);
        Assert.DoesNotContain("батареи", identity.Reason);
    }

    [Fact]
    public void New_profile_identity_starts_unknown_without_affecting_legacy_defaults()
    {
        var profile = new SystemProfile();
        Assert.False(profile.Identity.IsKnown);
        Assert.False(profile.IsLaptop);
        Assert.False(profile.HardwareFactsKnown);
    }
}
