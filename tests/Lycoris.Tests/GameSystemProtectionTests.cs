using System.ServiceProcess;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Restore;
using Lycoris.Modules.Startup;
using Microsoft.Win32;

namespace Lycoris.Tests;

public sealed class GameSystemProtectionTests
{
    private static readonly StartupEntry OrdinaryEntry = new()
    {
        Source = StartupSource.RunUser, Name = "OptionalApp", DisplayName = "Optional App", Enabled = true,
        Command = @"C:\Apps\OptionalApp.exe -silent", TargetPath = @"C:\Apps\OptionalApp.exe", TargetExists = true,
        RegistrationKind = RegistryValueKind.String, Risk = StartupRisk.Normal,
    };

    private static readonly LogonTask OrdinaryTask = new()
    {
        Path = @"\OptionalApp", Folder = @"\", Name = "OptionalApp", Command = OrdinaryEntry.Command,
        TargetPath = OrdinaryEntry.TargetPath, TargetExists = true, Enabled = true, AtLogon = true,
        ActionCount = 1, DefinitionHash = "observed task definition",
    };

    private static (SignatureState?, string?) OrdinarySignature(string path) => (SignatureState.Valid, "Optional Company");

    [Fact]
    public void Manual_disable_requires_current_registration_and_readable_target()
    {
        Assert.Null(StartupDisableGuard.EntryRefusal(OrdinaryEntry, OrdinaryEntry, OrdinarySignature));
        Assert.Equal(StartupDisableGuard.Changed, StartupDisableGuard.EntryRefusal(OrdinaryEntry, null, OrdinarySignature));
        Assert.Equal(StartupDisableGuard.Changed, StartupDisableGuard.EntryRefusal(OrdinaryEntry,
            OrdinaryEntry with { TargetExists = false }, OrdinarySignature));
        Assert.Equal(StartupDisableGuard.Changed, StartupDisableGuard.EntryRefusal(OrdinaryEntry, OrdinaryEntry, _ => (null, null)));
        Assert.Equal(StartupDisableGuard.Changed, StartupDisableGuard.EntryRefusal(OrdinaryEntry,
            OrdinaryEntry with { Command = OrdinaryEntry.Command + " -changed" }, OrdinarySignature));
        Assert.Equal(StartupDisableGuard.Changed, StartupDisableGuard.EntryRefusal(OrdinaryEntry,
            OrdinaryEntry with { RegistrationKind = RegistryValueKind.ExpandString }, OrdinarySignature));
    }

    [Theory]
    [InlineData("Microsoft Corporation")]
    [InlineData("NVIDIA Corporation")]
    public void Live_signature_can_protect_a_row_whose_displayed_risk_is_normal(string signer) =>
        Assert.Equal(StartupDisableGuard.Protected, StartupDisableGuard.EntryRefusal(OrdinaryEntry, OrdinaryEntry,
            _ => (SignatureState.Valid, signer)));

    [Theory]
    [InlineData("vgtray.exe")]
    [InlineData("EasyAntiCheat_EOS.exe")]
    [InlineData("EAAntiCheat.GameService.exe")]
    [InlineData("EABackgroundService.exe")]
    [InlineData("steamservice.exe")]
    [InlineData("GamingServices.exe")]
    public void Known_components_are_protected_even_without_publisher_metadata(string file)
    {
        var entry = OrdinaryEntry with { TargetPath = @"C:\Games\" + file };
        Assert.Equal(StartupRisk.Security, StartupClassifier.ClassifyEntry(entry.TargetPath, null));
        Assert.Equal(StartupDisableGuard.Protected, StartupDisableGuard.EntryRefusal(entry, entry, OrdinarySignature));
    }

    [Fact]
    public void Logon_disable_refuses_task_replacement_and_windows_task_path()
    {
        Assert.Null(StartupDisableGuard.TaskRefusal(OrdinaryTask, OrdinaryTask, OrdinarySignature));
        Assert.Equal(StartupDisableGuard.Changed, StartupDisableGuard.TaskRefusal(OrdinaryTask,
            OrdinaryTask with { DefinitionHash = "updated registration" }, OrdinarySignature));
        Assert.Equal(StartupDisableGuard.Changed, StartupDisableGuard.TaskRefusal(OrdinaryTask,
            OrdinaryTask with { ActionCount = 2 }, OrdinarySignature));
        var windows = OrdinaryTask with { Path = @"\Microsoft\Windows\Example" };
        Assert.Equal(StartupDisableGuard.Protected, StartupDisableGuard.TaskRefusal(windows, windows, OrdinarySignature));
    }

    [Theory]
    [InlineData("EABackgroundService")]
    [InlineData("EAAntiCheatService")]
    [InlineData("XblAuthManager")]
    [InlineData("XblGameSave")]
    [InlineData("XboxNetApiSvc")]
    [InlineData("XboxGipSvc")]
    public void Older_service_records_can_restore_a_start_type_that_allows_running(string name)
    {
        var record = ServiceRecord(name, ServiceStartMode.Manual);
        Assert.Null(JournalTrust.Check(record));
        Assert.NotNull(JournalTrust.Check(ServiceRecord(name, ServiceStartMode.Disabled)));
        var drift = new DriftItem(record, record.NewValue, record.OldValue, DriftFix.Service, null, "services");
        Assert.False(DriftReapply.CanReapply(drift));
        Assert.NotNull(DriftReapply.RefusalReason(drift));
    }

    private static ChangeRecord ServiceRecord(string name, ServiceStartMode original) => new()
    {
        Source = "services", Title = name, Kind = ChangeKind.Service, Target = "svc:" + name,
        OldValue = ServiceControl.DescribeStartNeutral(original, false),
        NewValue = ServiceControl.DescribeStartNeutral(ServiceStartMode.Disabled, false),
        Service = new(name, original, false),
    };
}
