using System.IO;
using System.Security.AccessControl;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Core.Services;
using Microsoft.Win32;

namespace Lycoris.Tests;

[Collection("ExpertAccessPolicy")]
public sealed class ServiceRemovalNativeTests
{
    private const string Name = "Everything";
    private const string ServiceRegistryPath = @"SYSTEM\CurrentControlSet\Services\Everything";
    private const string BackupRegistryPath = @"SOFTWARE\Lycoris.ServiceRemoval.v1";

    [HostedServiceRemovalFact]
    public async Task Deletes_and_exactly_recreates_only_the_disposable_disabled_scm_fixture()
    {
        Assert.Equal("true", Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        Assert.Equal("github-hosted", Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"));
        Assert.True(Elevation.IsElevated, "The disposable Windows runner must be elevated.");
        var management = ServiceRemovalWindows.ReadManagementEvidence();
        Assert.True(management.ProvenUnmanaged, ManagementDiagnostic(management));

        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var previous = registry.OpenSubKey(BackupRegistryPath))
            Assert.Null(previous);
        using (var previous = registry.OpenSubKey(ServiceRegistryPath))
            Assert.Null(previous);
        var scm = new ServiceRemovalWindows();
        Assert.Equal(RemovalServiceState.Absent, scm.InspectService(Name));

        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "Everything.exe");
        var command = "\"" + executable + "\" -svc";
        Assert.Equal(RemovalPathState.Missing, ServiceRemovalPath.Probe(command, executable));

        var guid = Guid.NewGuid().ToString("N");
        var fixture = "Lycoris SCM fixture " + guid;
        var replacement = "Lycoris SCM replacement " + guid;
        var description = "Disposable integration test " + guid;
        var journalPath = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP")!, guid + ".json");
        var settings = AppSettings.Current;
        var oldExpert = settings.ExpertMode;
        var oldServicesExpert = settings.ServicesExpertMode;
        var oldSuspend = AppSettings.SaveSuspended;
        using var owner = ExpertAccessPolicy.OverrideFingerprintForTests(() => ExpertAccessPolicy.OwnerFingerprint);
        AppSettings.SaveSuspended = true;
        try
        {
            settings.ExpertMode = true;
            settings.ServicesExpertMode = true;
            await CreateDisabledService(fixture, command);
            var described = await Sc(["description", Name, description]);
            Assert.True(described.Success, described.Combined);

            var approved = await InspectOwnFixture(registry, fixture, command);
            Assert.Equal(fixture, approved.DisplayName);
            var journal = ChangeJournal.ForTests(journalPath);
            var session = await ChangeSession.BeginAsync(fixture, createRestorePoint: false, journal: journal);
            var result = session.RemoveService(new(Name, approved.Fingerprint, true), fixture);
            Assert.Equal(ServiceRemovalOutcome.Removed, result.Outcome);
            Assert.Equal(RemovalServiceState.Absent, scm.InspectService(Name));

            var reloaded = ChangeJournal.ForTests(journalPath);
            var record = Assert.Single(reloaded.ActiveRecords);
            Assert.Equal(record.Id, result.JournalRecordId);

            await CreateDisabledService(replacement, command);
            Assert.Equal(1, (await reloaded.RevertAsync([record])).Failed);
            Assert.Equal(replacement, (await InspectOwnFixture(registry, replacement, command)).DisplayName);
            await RemoveOwnedFixture(registry, scm, command, replacement);

            settings.ServicesExpertMode = false;
            settings.ExpertMode = false;
            var reverted = await reloaded.RevertAsync([record]);
            Assert.True(reverted.Success, string.Join(Environment.NewLine, reverted.Errors));
            Assert.Equal(1, reverted.Reverted);
            var restored = await InspectOwnFixture(registry, fixture, command);
            Assert.Equal(approved, restored);
        }
        finally
        {
            try
            {
                await RemoveOwnedFixture(registry, scm, command, fixture, replacement);
            }
            finally
            {
                settings.ServicesExpertMode = oldServicesExpert;
                settings.ExpertMode = oldExpert;
                AppSettings.SaveSuspended = oldSuspend;
            }
        }
    }

    private static string ManagementDiagnostic(ServiceManagementEvidence evidence) =>
        $"Native checks must confirm the disposable VM is unmanaged.{Environment.NewLine}"
        + $"AD | code {Code(evidence.Domain.ReturnCode)} | join status {evidence.Domain.Value} | exception {evidence.Domain.ExceptionType}{Environment.NewLine}"
        + $"Entra | code {Code(evidence.Entra.ReturnCode)} | has join info {evidence.Entra.Value} | exception {evidence.Entra.ExceptionType}{Environment.NewLine}"
        + $"MDM | code {Code(evidence.Mdm.ReturnCode)} | registered {evidence.Mdm.Value} | exception {evidence.Mdm.ExceptionType}";

    private static string Code(int? result) => result is int code ? $"0x{code:X8}" : "unavailable";

    private static async Task CreateDisabledService(string display, string image)
    {
        var result = await Sc(["create", Name, "binPath=", image, "start=", "disabled", "type=", "own",
            "obj=", "LocalSystem", "error=", "normal", "DisplayName=", display]);
        Assert.True(result.Success, result.Combined);
    }

    private static async Task RemoveOwnedFixture(RegistryKey registry, ServiceRemovalWindows scm, string image, params string[] displayNames)
    {
        using (var key = registry.OpenSubKey(ServiceRegistryPath))
        {
            if (key is null) return;
            var display = key.GetValue("DisplayName") as string;
            Assert.Contains(display, displayNames);
            Assert.Equal(image, key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(4, key.GetValue("Start"));
            Assert.Equal(0x10, key.GetValue("Type"));
        }

        var result = await Sc(["delete", Name]);
        Assert.True(result.Success, result.Combined);
        Assert.Equal(RemovalServiceState.Absent, scm.InspectService(Name));
    }

    private static async Task<ServiceRemovalCandidate> InspectOwnFixture(RegistryKey registry, string display, string command)
    {
        var details = ReadOwnFixtureDetails(registry, display, command);
        var probe = ServiceRemoval.Probe(Name);
        if (probe.Candidate is { } candidate) return candidate;

        await AppendScDiagnostics(details);
        throw new Xunit.Sdk.XunitException($"Fixture did not pass the native inspection ({probe.Reason}).{Environment.NewLine}{string.Join(Environment.NewLine, details)}");
    }

    private static List<string> ReadOwnFixtureDetails(RegistryKey registry, string display, string command)
    {
        var details = new List<string>();
        using (var key = registry.OpenSubKey(ServiceRegistryPath))
        {
            if (key is null
                || !Equals(display, key.GetValue("DisplayName", null, RegistryValueOptions.DoNotExpandEnvironmentNames))
                || !Equals(command, key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames)))
                throw new Xunit.Sdk.XunitException("The test service identity could not be verified for diagnostics.");

            details.Add("Registry value names and kinds | " + Bounded(string.Join(", ",
                key.GetValueNames().Take(16).Select(name => $"{name} ({key.GetValueKind(name)})"))));
            details.Add("Registry subkey names | " + Bounded(string.Join(", ", key.GetSubKeyNames().Take(16))));
            try
            {
                const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
                details.Add("Registry owner, group and DACL SDDL | " + Bounded(key.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                details.Add("Registry SDDL unavailable | " + ex.GetType().Name);
            }
        }

        try
        {
            var descriptor = ServiceRemovalWindows.ReadSupportedServiceSecurityForDiagnostics();
            details.Add("SCM owner, group, DACL and SACL SDDL | " + Bounded(descriptor));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            details.Add($"SCM full security descriptor unavailable | Win32 {ex.NativeErrorCode}");
        }
        catch (Exception ex) when (ServiceRemoval.IsExpectedFailure(ex))
        {
            details.Add("SCM full security descriptor unavailable | " + ex.GetType().Name);
        }

        var highestNode = ServiceRemovalWindows.ReadHighestNumaNode();
        details.Add("Highest NUMA node | " + (highestNode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"));
        return details;
    }

    private static async Task AppendScDiagnostics(List<string> details)
    {
        string[] queries = ["qc", "queryex", "qfailure", "qsidtype", "qprivs", "qtriggerinfo", "qpreferrednode", "sdshow"];
        foreach (var query in queries)
        {
            var result = await Sc([query, Name], TimeSpan.FromSeconds(4));
            details.Add($"sc {query} | exit {result.ExitCode} | {Bounded(result.Combined)}");
        }
    }

    private static string Bounded(string value) => value.Length <= 1200 ? value : value[..1200] + " …";

    private static Task<ProcessResult> Sc(string[] args, TimeSpan? timeout = null) =>
        ProcessRunner.RunAsync(ProcessRunner.Sys("sc.exe"), args, new()
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
            LogCommand = false,
        });
}

public sealed class HostedServiceRemovalFactAttribute : FactAttribute
{
    public HostedServiceRemovalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LYCORIS_SERVICE_REMOVAL_INTEGRATION") != "1")
            Skip = "Opt-in SCM mutation on a disposable GitHub-hosted Windows runner only.";
    }
}

