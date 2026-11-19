using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Services;
using Lycoris.Modules.Startup;
using Lycoris.UI;
using Microsoft.Win32;

namespace Lycoris.Tests;

[Collection("ExpertAccessPolicy")]
public sealed class ServiceActionsNativeTests
{
    private const string Account = @"NT AUTHORITY\LocalService";
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    [HostedServiceActionsFact]
    public Task Assistant_actions_change_live_scm_state_and_persist_only_start_type_undo() =>
        StaWorker.Run(() =>
        {
            Assert.True(HostedServiceActionsFactAttribute.CanRun);
            using var writer = JournalWriterLease.TryAcquire(out var busy);
            Assert.False(busy);
            Assert.NotNull(writer);
            Assert.Same(writer, JournalWriterLease.Current);
            RunServiceLifecycleAsync().GetAwaiter().GetResult();
            return true;
        }, "Lycoris hosted SCM writer fixture");

    private async Task RunServiceLifecycleAsync()
    {
        var runnerTemp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")!));
        var testRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("LYCORIS_TEST_ROOT")!);
        Assert.Equal(runnerTemp, Path.GetDirectoryName(testRoot), ignoreCase: true);
        Assert.Equal(testRoot, AppPaths.TestRoot);
        Assert.Equal(Path.Combine(testRoot, "machine"), AppPaths.MachineDir);

        var executable = Path.GetFullPath(Environment.GetEnvironmentVariable("LYCORIS_SCM_FIXTURE_EXE")!);
        Assert.Equal(runnerTemp, Path.GetDirectoryName(Path.GetDirectoryName(executable)), ignoreCase: true);
        Assert.False(executable.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(executable), $"SCM fixture is missing: {executable}");

        Assert.False(Directory.Exists(AppPaths.MachineDir), "The test requires a fresh protected journal directory.");
        AppPaths.SecureMachineDir();
        Assert.True(AppPaths.MachineDirSecured);
        Assert.False(AppPaths.MachineDirWasInsecure);
        Assert.False(File.Exists(AppPaths.JournalFile));
        Assert.False(File.Exists(AppPaths.JournalFile + ".bak"));
        var journal = ChangeJournal.Instance;
        Assert.True(journal.CanWrite, journal.UnavailableReason);
        Assert.Empty(journal.Records);
        Assert.Empty(journal.Batches);

        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        var a = new Fixture(executable);
        var b = new Fixture(executable);
        await a.AssertAbsentAsync(registry);
        await b.AssertAbsentAsync(registry);

        var settings = AppSettings.Current;
        var oldExpert = settings.ExpertMode;
        var oldServicesExpert = settings.ServicesExpertMode;
        var oldRestorePoints = settings.CreateRestorePoints;
        var oldSaveSuspended = AppSettings.SaveSuspended;
        var oldAutoAnswer = Ui.AutoAnswer;
        var oldAutoAnswered = Ui.AutoAnswered.ToArray();
        using var owner = ExpertAccessPolicy.OverrideFingerprintForTests(() => ExpertAccessPolicy.OwnerFingerprint);
        try
        {
            AppSettings.SaveSuspended = true;
            settings.CreateRestorePoints = false;
            settings.ExpertMode = true;
            settings.ServicesExpertMode = true;
            Ui.AutoAnswer = true;

            await a.CreateAsync("disabled");
            await b.CreateAsync("demand");
            var aConfig = a.AssertState(registry, ServiceStartMode.Disabled, ServiceControllerStatus.Stopped);
            var bConfig = b.AssertState(registry, ServiceStartMode.Manual, ServiceControllerStatus.Stopped);
            var viewModel = new ServicesViewModel { Coordinator = new OperationCoordinator() };

            var enabled = await ApplyAsync(viewModel, a, ServiceAction.Enable);
            Assert.Equal(aConfig, a.AssertState(registry, ServiceStartMode.Manual, ServiceControllerStatus.Stopped));
            var enableRecord = AssertRecord(journal, a, enabled, ServiceStartMode.Disabled, ServiceStartMode.Manual);
            Assert.Single(journal.Records);
            Assert.Single(journal.Batches);
            var savedEnable = File.ReadAllBytes(AppPaths.JournalFile);

            var started = await ApplyAsync(viewModel, a, ServiceAction.Start);
            Assert.Null(started.JournalRecordId);
            Assert.Equal(aConfig, a.AssertState(registry, ServiceStartMode.Manual, ServiceControllerStatus.Running));
            AssertRuntimeDidNotChangeJournal(journal, a, enableRecord, savedEnable);

            var stopped = await ApplyAsync(viewModel, a, ServiceAction.Stop);
            Assert.Null(stopped.JournalRecordId);
            Assert.Equal(aConfig, a.AssertState(registry, ServiceStartMode.Manual, ServiceControllerStatus.Stopped));
            AssertRuntimeDidNotChangeJournal(journal, a, enableRecord, savedEnable);

            var disabled = await ApplyAsync(viewModel, b, ServiceAction.Disable);
            Assert.Equal(bConfig, b.AssertState(registry, ServiceStartMode.Disabled, ServiceControllerStatus.Stopped));
            var disableRecord = AssertRecord(journal, b, disabled, ServiceStartMode.Manual, ServiceStartMode.Disabled);
            Assert.NotEqual(enableRecord.Id, disableRecord.Id);
            Assert.Equal(2, journal.Records.Count);
            Assert.Equal(2, journal.Batches.Count);

            // Finish all singleton writes before undo through a freshly loaded, protected journal.
            var reloaded = new ChangeJournal(AppPaths.JournalFile);
            Assert.True(reloaded.CanWrite, reloaded.UnavailableReason);
            Assert.Equal(2, reloaded.ActiveRecords.Count);
            await UndoAsync(reloaded, a, enabled, ServiceStartMode.Disabled, ServiceStartMode.Manual);
            Assert.Equal(aConfig, a.AssertState(registry, ServiceStartMode.Disabled, ServiceControllerStatus.Stopped));
            Assert.Equal(bConfig, b.AssertState(registry, ServiceStartMode.Disabled, ServiceControllerStatus.Stopped));
            await UndoAsync(reloaded, b, disabled, ServiceStartMode.Manual, ServiceStartMode.Disabled);
            Assert.Equal(bConfig, b.AssertState(registry, ServiceStartMode.Manual, ServiceControllerStatus.Stopped));

            var afterUndo = new ChangeJournal(AppPaths.JournalFile);
            Assert.True(afterUndo.CanWrite, afterUndo.UnavailableReason);
            Assert.Empty(afterUndo.ActiveRecords);
            Assert.Equal(2, afterUndo.Records.Count);
            Assert.Equal(oldAutoAnswered.Length + 4, Ui.AutoAnswered.Count);
        }
        finally
        {
            try
            {
                try { await a.RemoveAsync(registry); }
                finally { await b.RemoveAsync(registry); }
            }
            finally
            {
                settings.ServicesExpertMode = oldServicesExpert;
                settings.ExpertMode = oldExpert;
                settings.CreateRestorePoints = oldRestorePoints;
                AppSettings.SaveSuspended = oldSaveSuspended;
                Ui.AutoAnswer = oldAutoAnswer;
                Ui.AutoAnswered.Clear();
                Ui.AutoAnswered.AddRange(oldAutoAnswered);
            }
        }
    }

    private static async Task<ServiceActionResult> ApplyAsync(ServicesViewModel viewModel, Fixture fixture, ServiceAction action)
    {
        var result = await viewModel.ExecuteAssistantServiceActionAsync(new(action, fixture.Name) { ExactNameOnly = true });
        Assert.True(result.Outcome == ServiceActionOutcome.Applied, $"{action}: {result.Outcome}. {result.Message}");
        Assert.Equal(fixture.Name, result.CanonicalName);
        return result;
    }

    private static ChangeRecord AssertRecord(ChangeJournal journal, Fixture fixture, ServiceActionResult result,
        ServiceStartMode original, ServiceStartMode destination)
    {
        var record = Assert.Single(journal.ActiveLayers(fixture.Target));
        Assert.Equal(record.Id, Assert.IsType<Guid>(result.JournalRecordId));
        Assert.Equal(fixture.Target, record.Target);
        Assert.Equal(ChangeKind.Service, record.Kind);
        Assert.Equal(ServicesViewModel.ServiceSource, record.Source);
        Assert.Equal(fixture.Name, record.Service?.Name);
        Assert.Equal(original, record.Service?.StartMode);
        Assert.Equal(ServiceControl.DescribeStartNeutral(destination, false), record.NewValue);
        return record;
    }

    private static void AssertRuntimeDidNotChangeJournal(ChangeJournal journal, Fixture fixture, ChangeRecord enabled, byte[] savedEnable)
    {
        Assert.Equal(enabled.Id, Assert.Single(journal.ActiveLayers(fixture.Target)).Id);
        Assert.Single(journal.Records);
        Assert.Single(journal.Batches);
        Assert.Equal(savedEnable, File.ReadAllBytes(AppPaths.JournalFile));
    }

    private static async Task UndoAsync(ChangeJournal journal, Fixture fixture, ServiceActionResult result,
        ServiceStartMode original, ServiceStartMode destination)
    {
        var record = AssertRecord(journal, fixture, result, original, destination);
        var undo = await journal.RevertAsync([record]);
        Assert.True(undo.Success && undo.JournalSaved, string.Join(Environment.NewLine, undo.Errors));
        Assert.Equal(1, undo.Restored);
        Assert.Empty(journal.ActiveLayers(fixture.Target));
    }

    private sealed class Fixture(string executable)
    {
        private const int OwnProcessType = (int)ServiceType.Win32OwnProcess;

        public string Name { get; } = "LycorisScmLifecycle" + Guid.NewGuid().ToString("N");
        public string Target => "svc:" + Name;
        private string DisplayName => "Disposable Lycoris SCM fixture " + Name;
        private string ImagePath => $"\"{executable}\" {Name}";

        public async Task CreateAsync(string start)
        {
            var result = await Sc("create", Name, "binPath=", ImagePath, "start=", start,
                "type=", "own", "obj=", Account, "error=", "normal", "DisplayName=", DisplayName);
            Assert.True(result.Success, result.Combined);
        }

        public async Task AssertAbsentAsync(RegistryKey registry)
        {
            using var key = OpenServiceKey(registry);
            Assert.Null(key);
            var query = await Sc("query", Name);
            Assert.True(query.ExitCode == ErrorServiceDoesNotExist, query.Combined);
        }

        public FixtureConfig AssertState(RegistryKey registry, ServiceStartMode start, ServiceControllerStatus status)
        {
            using var controller = new ServiceController { ServiceName = Name };
            Assert.Equal(status, controller.Status);
            Assert.Equal(start, controller.StartType);
            Assert.Equal(DisplayName, controller.DisplayName);
            Assert.Equal(ServiceType.Win32OwnProcess, controller.ServiceType);

            using var key = OpenServiceKey(registry);
            Assert.NotNull(key);
            var image = AssertOwned(key);
            Assert.Equal((int)start, key.GetValue("Start"));
            Assert.Equal(1, key.GetValue("ErrorControl"));
            Assert.Empty((string[]?)key.GetValue("DependOnGroup") ?? []);
            Assert.Empty((string[]?)key.GetValue("DependOnService") ?? []);
            using var parameters = key.OpenSubKey("Parameters");
            using var triggers = key.OpenSubKey("TriggerInfo");
            Assert.Null(parameters);
            Assert.Null(triggers);
            var account = Assert.IsType<string>(key.GetValue("ObjectName"));
            Assert.Equal(Account, account, ignoreCase: true);
            return new(image, account, (int?)key.GetValue("DelayedAutostart"), (int?)key.GetValue("LaunchProtected"));
        }

        public async Task RemoveAsync(RegistryKey registry)
        {
            // Retain this SCM entry until delete is requested. It cannot be replaced while the handle is open.
            using (var identity = ServiceScanner.HoldActionServices([Name]))
            {
                if (identity is null)
                {
                    await AssertAbsentAsync(registry);
                    return;
                }

                using (var key = OpenServiceKey(registry))
                {
                    Assert.NotNull(key);
                    AssertOwned(key);
                }
                using var controller = new ServiceController { ServiceName = Name };
                try
                {
                    var status = controller.Status;
                    if (status == ServiceControllerStatus.StartPending)
                        controller.WaitForStatus(ServiceControllerStatus.Running, CleanupTimeout);
                    if (status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
                    {
                        var stop = await Sc("stop", Name);
                        Assert.True(stop.Success, stop.Combined);
                    }
                    if (status != ServiceControllerStatus.Stopped)
                        controller.WaitForStatus(ServiceControllerStatus.Stopped, CleanupTimeout);
                }
                finally
                {
                    var delete = await Sc("delete", Name);
                    Assert.True(delete.Success, delete.Combined);
                }
            }

            var timer = Stopwatch.StartNew();
            do
            {
                var query = await Sc("query", Name);
                if (query.ExitCode == ErrorServiceDoesNotExist)
                {
                    using var key = OpenServiceKey(registry);
                    Assert.Null(key);
                    return;
                }
                Assert.True(query.Success || query.ExitCode == ErrorServiceMarkedForDelete, query.Combined);
                await Task.Delay(100);
            } while (timer.Elapsed < CleanupTimeout);
            Assert.Fail($"SCM fixture {Name} was not deleted within {CleanupTimeout}.");
        }

        private RegistryKey? OpenServiceKey(RegistryKey registry) => registry.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name);

        private string AssertOwned(RegistryKey key)
        {
            Assert.Equal(DisplayName, key.GetValue("DisplayName"));
            var image = Assert.IsType<string>(key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(ImagePath, image);
            Assert.Equal(OwnProcessType, key.GetValue("Type"));
            return image;
        }
    }

    private sealed record FixtureConfig(string ImagePath, string Account, int? DelayedAutostart, int? LaunchProtected);

    private static Task<ProcessResult> Sc(params string[] args) => ProcessRunner.RunAsync(ProcessRunner.Sys("sc.exe"), args,
        new() { Timeout = TimeSpan.FromSeconds(10), LogCommand = false });
}

public sealed class HostedServiceActionsFactAttribute : FactAttribute
{
    internal static bool CanRun => OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("LYCORIS_SERVICE_ACTIONS_INTEGRATION") == "1" &&
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
        Elevation.IsElevated;

    public HostedServiceActionsFactAttribute()
    {
        if (!CanRun) Skip = "Requires the opt-in job on an elevated, disposable GitHub-hosted Windows runner.";
    }
}
