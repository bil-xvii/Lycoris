using Lycoris.Core.Security;
using Lycoris.Modules.Games.Sources;
using Lycoris.Modules.Removal;
using Lycoris.Modules.Startup;
using Lycoris.Shell;

namespace Lycoris.Tests;

public sealed class RemovalPolicyTests
{
    [Theory]
    [InlineData("VLC media player", "VideoLAN")]
    [InlineData("7-Zip 25.01 (x64)", "Igor Pavlov")]
    [InlineData("Steam", "Valve Corporation")]
    public void Installer_labels_alone_never_confirm_a_candidate(string name, string publisher)
    {
        var result = RemovalPolicy.Program(Entry(name, publisher), current: true);
        Assert.Equal(RemovalDecision.Review, result.Decision);
        Assert.Equal(name, result.Name);
        Assert.Equal(publisher, result.Publisher);
    }

    [Theory]
    [InlineData("VLC media player", "someone else")]
    [InlineData("Ordinary unknown program", "Contoso")]
    public void Unrecognized_or_lookalike_installer_details_require_review(string name, string publisher)
    {
        Assert.Equal(RemovalDecision.Review, RemovalPolicy.Program(Entry(name, publisher), current: true).Decision);
    }

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Corporation")]
    [InlineData("Microsoft Windows Desktop Runtime - 10.0.0 (x86)", "Microsoft Corporation")]
    [InlineData("Microsoft Edge WebView2 Runtime", "Microsoft Corporation")]
    [InlineData("NVIDIA Graphics Driver 555.55", "NVIDIA Corporation")]
    [InlineData("ESET Endpoint Security", "ESET, spol. s r.o.")]
    [InlineData("7-Zip 25.01 (x64) runtime", "Igor Pavlov")]
    [InlineData("Some driver", "Contoso")]
    [InlineData("Unknown SDK", "Contoso")]
    [InlineData("VLC media player codec", "VideoLAN")]
    [InlineData("Known Product", "IDRIX")]
    public void Shared_runtimes_codecs_drivers_and_security_stay_out_of_removal(string name, string publisher)
    {
        Assert.Equal(RemovalDecision.Keep, RemovalPolicy.Program(Entry(name, publisher), current: true).Decision);
    }

    [Fact]
    public void System_flags_and_missing_metadata_override_a_known_program()
    {
        var item = Entry();
        Assert.Equal(RemovalDecision.Keep, RemovalPolicy.Program(item with { SystemComponent = true }, true).Decision);
        Assert.Equal(RemovalDecision.Keep, RemovalPolicy.Program(item with { NoRemove = true }, true).Decision);
        Assert.Equal(RemovalDecision.Keep, RemovalPolicy.Program(item with { ParentOrUpdate = true }, true).Decision);
        Assert.Equal(RemovalDecision.Review, RemovalPolicy.Program(item with { Version = null }, true).Decision);
        Assert.Equal(RemovalDecision.Review, RemovalPolicy.Program(item with { Publisher = " " }, true).Decision);
        Assert.Equal(RemovalDecision.Review, RemovalPolicy.Program(item with { NoRemove = null }, true).Decision);
        Assert.Equal(RemovalDecision.Review, RemovalPolicy.Program(item with { SystemComponent = null }, true).Decision);
        Assert.Equal(RemovalDecision.Review, RemovalPolicy.Program(item with { HasUninstallRegistration = false }, true).Decision);
    }

    [Fact]
    public void Same_name_publisher_and_version_in_two_scopes_are_kept_distinct_and_require_review()
    {
        var machine = Entry();
        var user = machine with { Scope = UninstallScope.DesktopUser };
        var rows = RemovalPolicy.Programs(Snapshot([machine, user]), current: true);

        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].Key, rows[1].Key);
        Assert.NotEqual(rows[0].Source, rows[1].Source);
        Assert.All(rows, row => Assert.Equal(RemovalDecision.Review, row.Decision));
    }

    [Fact]
    public void Same_scope_and_key_with_conflicting_data_never_yields_a_removal_candidate()
    {
        var item = Entry();
        var duplicate = item with { DisplayName = "different display name", KeyName = item.KeyName.ToLowerInvariant() };
        var row = Assert.Single(RemovalPolicy.Programs(Snapshot([item, duplicate]), current: true));
        Assert.Equal(RemovalDecision.Review, row.Decision);
    }

    [Fact]
    public void Partial_or_stale_reads_cannot_offer_removal_even_for_an_exact_program()
    {
        var live = Snapshot([Entry()]);
        Assert.Equal(RemovalDecision.Review, Assert.Single(RemovalPolicy.Programs(live, current: false)).Decision);
        var missingUser = UninstallEntries.InventoryState(UninstallReadState.Complete, UninstallReadState.Complete, UninstallReadState.Failed);
        Assert.Equal(UninstallReadState.Partial, missingUser);
        Assert.Equal(RemovalDecision.Review, Assert.Single(RemovalPolicy.Programs(live with { State = missingUser }, current: true)).Decision);
        Assert.Equal(UninstallReadState.Failed, UninstallEntries.InventoryState(
            UninstallReadState.Failed, UninstallReadState.Failed, UninstallReadState.Failed));
    }

    [Fact]
    public void Installer_flags_distinguish_absent_zero_enabled_and_unreadable_values()
    {
        Assert.False(UninstallEntries.InstallerFlag(null));
        Assert.False(UninstallEntries.InstallerFlag(0));
        Assert.True(UninstallEntries.InstallerFlag(1));
        Assert.Null(UninstallEntries.InstallerFlag("0"));
        Assert.Null(UninstallEntries.InstallerFlag(2));
        Assert.Null(UninstallEntries.InstallerFlag(new byte[] { 0 }));
        Assert.Equal("Test App Name", UninstallEntries.InventoryText("Test\u202eApp\rName"));
    }

    [Fact]
    public void Startup_signature_without_manual_launch_proof_stays_for_review()
    {
        var item = SteamStartup();
        var result = RemovalPolicy.Startup(item, unique: true);
        Assert.Equal(RemovalDecision.Review, result.Decision);
        Assert.Equal(item.Key, result.Key);
        Assert.Equal("Valve Corp.", result.Publisher);
    }

    [Theory]
    [InlineData(StartupRisk.Microsoft)]
    [InlineData(StartupRisk.Driver)]
    [InlineData(StartupRisk.Security)]
    [InlineData(StartupRisk.Updater)]
    public void Startup_protection_overrides_the_optional_program_match(StartupRisk risk)
    {
        var row = RemovalPolicy.Startup(SteamStartup() with { Risk = risk }, unique: true);
        Assert.Equal(RemovalDecision.Keep, row.Decision);
    }

    [Fact]
    public void Startup_requires_signer_match_present_target_unique_key_and_unmanaged_state()
    {
        var steam = SteamStartup();
        Assert.Equal(RemovalDecision.Review, RemovalPolicy.Startup(steam, unique: false).Decision);
        Assert.Equal(RemovalDecision.Review, RemovalPolicy.Startup(steam with { Signature = null }, true).Decision);
        Assert.Equal(RemovalDecision.Review, RemovalPolicy.Startup(steam with { SignedBy = "Someone else" }, true).Decision);
        Assert.Equal(RemovalDecision.Review, RemovalPolicy.Startup(steam with { TargetExists = false }, true).Decision);
        Assert.Equal(RemovalDecision.AlreadyDisabled, RemovalPolicy.Startup(steam with { Enabled = false }, true).Decision);
        var policy = steam with
        {
            Source = StartupSource.PackagedTask, PackageFamilyName = "Vendor.Steam_1234567890123",
            PackageState = (int)PackagedTaskState.EnabledByPolicy,
        };
        Assert.Equal(RemovalDecision.Keep, RemovalPolicy.Startup(policy, true).Decision);
        Assert.Equal(RemovalDecision.Review, RemovalPolicy.Startup(policy with { PackageState = null }, true).Decision);
    }

    [Theory]
    [InlineData("INZONEHub.exe", SignatureState.Valid, "Sony Corporation", RemovalDecision.Keep)]
    [InlineData("UnrelatedSonyApp.exe", SignatureState.Valid, "Sony Corporation", RemovalDecision.Review)]
    [InlineData("INZONEHub.exe", SignatureState.Valid, "Other Publisher", RemovalDecision.Review)]
    [InlineData("INZONEHub.exe", SignatureState.Untrusted, "Sony Corporation", RemovalDecision.Review)]
    public void Hardware_utility_role_requires_the_catalog_executable_and_verified_signer(
        string executable, SignatureState signature, string signer, RemovalDecision expected)
    {
        var entry = SteamStartup() with
        {
            Name = "INZONE Hub", DisplayName = "INZONE Hub",
            TargetPath = $@"C:\Program Files\Sony\INZONE Hub\{executable}",
            Signature = signature, SignedBy = signer, Publisher = "Sony Corporation",
        };

        var expectedRole = expected == RemovalDecision.Keep ? RemovalSoftwareRole.Protected : RemovalSoftwareRole.Ordinary;
        Assert.Equal(expectedRole, RemovalSoftwareEvidence.Role(entry.TargetPath,
            signature == SignatureState.Valid ? signer : null, entry.Name, entry.Publisher));
        Assert.Equal(expected, RemovalPolicy.Startup(entry, unique: true).Decision);
    }

    [Fact]
    public void Filter_selection_text_exposes_the_display_title()
    {
        var filter = new RemovalFilter(RemovalDecision.ConsiderRemoval, "Consider removal");
        Assert.Equal("Consider removal", filter.ToString());
    }

    [Fact]
    public void Removal_is_a_distinct_maintenance_page()
    {
        var page = new RemovalViewModel();
        Assert.Equal("removal", page.Key);
        Assert.Equal(MainViewModel.GroupMaintenance, MainViewModel.NavGroupOf(page.Key));
        Assert.Empty(page.VisibleRows);
        Assert.Null(page.EmptyMessage);
        Assert.False(page.OpenSelectedStartupCommand.CanExecute(null));
        Assert.False(page.OpenSelectedProgramCommand.CanExecute(null));
    }

    private static UninstallRegistration Entry(string name = "VLC media player", string publisher = "VideoLAN") =>
        new(UninstallScope.Machine64, "ExampleProduct", name, publisher, "3.0.24", false, false, false, true, "fingerprint");

    private static UninstallInventory Snapshot(IReadOnlyList<UninstallRegistration> items) =>
        new(items, UninstallReadState.Complete, new DateTime(2026, 11, 19, 10, 0, 0));

    private static StartupEntry SteamStartup() => new()
    {
        Source = StartupSource.RunUser, Name = "Steam", DisplayName = "Steam", Command = "\"C:\\Program Files\\Steam\\Steam.exe\" -silent",
        TargetPath = @"C:\Program Files\Steam\Steam.exe", TargetExists = true, Enabled = true, Risk = StartupRisk.Normal,
        Publisher = "Valve", Signature = SignatureState.Valid, SignedBy = "Valve Corp.",
    };
}
