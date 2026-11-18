using System.IO;
using System.ServiceProcess;
using Lycoris.Modules.Services;

namespace Lycoris.Tests;

public sealed class ServicesAutomationTests
{
    [Theory]
    [InlineData("fax")]
    [InlineData("Fax Capabilities")]
    public async Task DisableWritesCanonicalNameAndReturnsExactJournalRecord(string target)
    {
        var backend = new FakeBackend();
        backend.Facts = backend.Facts with
        {
            Status = ServiceControllerStatus.Running, Start = ServiceStartMode.Automatic, RawDelayed = true,
        };

        var result = await Execute(backend, ServiceAction.Disable, target);

        Assert.Equal(ServiceActionOutcome.Applied, result.Outcome);
        Assert.Equal("Fax", result.CanonicalName);
        var record = Assert.Single(backend.Journal);
        Assert.Equal(("Fax", ServiceStartMode.Automatic, ServiceStartMode.Disabled, (bool?)true),
            (record.Name, record.Before, record.After, record.RawDelayed));
        Assert.Equal(record.Id, result.JournalRecordId);
        Assert.Equal(ServiceControllerStatus.Running, backend.Facts.Status);
        Assert.Contains(ServiceText.Status(ServiceControllerStatus.Running), result.Message);
        Assert.Equal(new[] { "confirm", "queue", "begin", "exclusive", "write", "readback", "complete" }, backend.Events);
    }

    [Fact]
    public async Task EnableUsesDemandStartAndLeavesStoppedServiceStopped()
    {
        var backend = new FakeBackend();
        backend.Facts = backend.Facts with { Start = ServiceStartMode.Disabled };

        var result = await Execute(backend, ServiceAction.Enable, "Fax");

        Assert.Equal(ServiceActionOutcome.Applied, result.Outcome);
        Assert.Equal(ServiceStartMode.Manual, Assert.Single(backend.Journal).After);
        Assert.Equal(ServiceControllerStatus.Stopped, backend.Facts.Status);
        Assert.Contains(ServiceText.Status(ServiceControllerStatus.Stopped), result.Message);
    }

    [Fact]
    public async Task EnablePreservesExistingAutomaticStartWithoutConfirmationOrWrite()
    {
        var backend = new FakeBackend();
        backend.Facts = backend.Facts with { Start = ServiceStartMode.Automatic, RawDelayed = true };

        var result = await Execute(backend, ServiceAction.Enable);

        Assert.Equal(ServiceActionOutcome.AlreadyApplied, result.Outcome);
        Assert.Empty(backend.Events);
        Assert.True(backend.Facts.RawDelayed);
    }

    [Theory]
    [InlineData("Fax Capa", false)]
    [InlineData("Fax Capabilities", true)]
    [InlineData("служба печати", false)]
    [InlineData("печать", false)]
    public async Task MissingNeverFallsBackToAnyOtherInstalledService(string target, bool exactOnly)
    {
        var backend = new FakeBackend();

        var result = await Execute(backend, ServiceAction.Disable, target, exactOnly);

        Assert.Equal(ServiceActionOutcome.Missing, result.Outcome);
        Assert.Null(result.CanonicalName);
        Assert.Empty(backend.Events);
    }

    [Fact]
    public async Task ConflictingKeyAndDisplayNameAreAmbiguousBeforeElevation()
    {
        var backend = new FakeBackend();
        backend.Identities.Add(new("RealFax", "Fax"));
        backend.Settings = backend.Settings with { Elevated = false };

        Assert.Equal(ServiceActionOutcome.Ambiguous, (await Execute(backend, ServiceAction.Disable)).Outcome);
        Assert.Empty(backend.Events);

        var replay = await Execute(backend, ServiceAction.Disable, exactOnly: true);

        Assert.Equal(ServiceActionOutcome.NeedsAdmin, replay.Outcome);
        Assert.Equal("Fax", replay.CanonicalName);
        Assert.Empty(backend.Events);
    }

    [Theory]
    [InlineData("служба печати")]
    [InlineData("печать")]
    public async Task PrintingAliasResolvesOnlyWhenSpoolerIsEnumeratedAndEligible(string alias)
    {
        var backend = new FakeBackend("Spooler", "Print Queue");
        backend.Settings = backend.Settings with { Expert = true };
        backend.Facts = backend.Facts with { Start = ServiceStartMode.Disabled };

        var result = await Execute(backend, ServiceAction.Enable, alias);

        Assert.Equal(ServiceActionOutcome.Applied, result.Outcome);
        Assert.Equal("Spooler", Assert.Single(backend.Journal).Name);
    }

    [Theory]
    [InlineData("RpcSs", ServiceAction.Disable)]
    [InlineData("SysMain", ServiceAction.Enable)]
    [InlineData("DiagTrack", ServiceAction.Disable)]
    [InlineData("RemoteRegistry", ServiceAction.Enable)]
    [InlineData("RemoteRegistry", ServiceAction.Disable)]
    [InlineData("RemoteRegistry", ServiceAction.Start)]
    [InlineData("VendorService_410b", ServiceAction.Disable)]
    [InlineData("PrintNotify", ServiceAction.Disable)]
    public async Task ProtectedOrNonReversibleActionCannotWriteEvenAsExpert(string name, ServiceAction action)
    {
        var backend = new FakeBackend(name, name);
        backend.Settings = backend.Settings with { Expert = true, ServicesExpert = true };

        Assert.Equal(ServiceActionOutcome.Blocked, (await Execute(backend, action, name)).Outcome);
        Assert.Empty(backend.Events);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LockOrDemoPreventsEvenLiveScanning(bool locked, bool demo)
    {
        var backend = new FakeBackend();
        backend.Settings = backend.Settings with { Locked = locked, Demo = demo };
        backend.OnScan = () => throw new InvalidOperationException("Unexpected scan");

        Assert.Equal(ServiceActionOutcome.Blocked, (await Execute(backend, ServiceAction.Disable)).Outcome);
        Assert.Equal(0, backend.ScanCount);
        Assert.Empty(backend.Events);
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("unknown-domain")]
    [InlineData("unknown-config")]
    [InlineData("blocked-catalog-gate")]
    [InlineData("unknown-catalog-gate")]
    [InlineData("unknown-dependents")]
    [InlineData("has-dependent")]
    [InlineData("no-permission")]
    [InlineData("bad-start")]
    [InlineData("driver-path")]
    public async Task IncompleteOrUnsafeFactsBlockWithoutPromptOrWrite(string invalid)
    {
        var backend = new FakeBackend();
        backend.Facts = invalid switch
        {
            "domain" => backend.Facts with { Managed = true },
            "unknown-domain" => backend.Facts with { Managed = null },
            "blocked-catalog-gate" => backend.Facts with { CatalogGatePassed = false },
            "unknown-catalog-gate" => backend.Facts with { CatalogGatePassed = null },
            "unknown-dependents" => backend.Facts with { DependentCount = null },
            "has-dependent" => backend.Facts with { DependentCount = 1 },
            "no-permission" => backend.Facts with { CanChange = false },
            "bad-start" => backend.Facts with { Start = (ServiceStartMode)900 },
            "driver-path" => backend.Facts with { ImagePath = @"C:\Windows\System32\DriverStore\FileRepository\vendor.exe" },
            _ => backend.Facts,
        };
        backend.ConfigReadable = invalid != "unknown-config";

        Assert.Equal(ServiceActionOutcome.Blocked, (await Execute(backend, ServiceAction.Disable)).Outcome);
        Assert.Empty(backend.Events);
    }

    [Fact]
    public async Task FailedEnumerationIsBlockedRatherThanTreatedAsEmpty()
    {
        var backend = new FakeBackend { OnScan = () => throw new InvalidDataException() };

        Assert.Equal(ServiceActionOutcome.Blocked, (await Execute(backend, ServiceAction.Disable)).Outcome);
        Assert.Empty(backend.Events);
    }

    [Theory]
    [InlineData("Spooler", false, true)]
    [InlineData("Uncatalogued", false, true)]
    [InlineData("Uncatalogued", true, false)]
    public async Task MissingRiskOrServiceExpertGatePreventsConfirmation(string name, bool expert, bool servicesExpert)
    {
        var backend = new FakeBackend(name, name);
        backend.Settings = backend.Settings with { Expert = expert, ServicesExpert = servicesExpert };

        Assert.Equal(ServiceActionOutcome.Blocked, (await Execute(backend, ServiceAction.Disable, name)).Outcome);
        Assert.Empty(backend.Events);
    }

    [Fact]
    public async Task DeclinedConfirmationStartsNoBatch()
    {
        var backend = new FakeBackend { Approve = _ => Task.FromResult(false) };

        Assert.Equal(ServiceActionOutcome.Declined, (await Execute(backend, ServiceAction.Disable)).Outcome);
        Assert.Equal(new[] { "confirm" }, backend.Events);
    }

    [Fact]
    public async Task ConfigurationChangeDuringAsyncConfirmationPreventsWrite()
    {
        var backend = new FakeBackend();
        var entered = new TaskCompletionSource<ServiceActionPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Approve = plan =>
        {
            entered.SetResult(plan);
            return approval.Task;
        };

        var running = Execute(backend, ServiceAction.Disable);
        var shown = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Fax", shown.Facts.Identity.Name);
        Assert.Equal(ServiceStartMode.Manual, shown.Facts.Start);
        Assert.Equal(ServiceStartMode.Disabled, shown.Destination);
        backend.Facts = backend.Facts with { ImagePath = @"C:\Changed\fax.exe", KeyVersion = 2 };
        approval.SetResult(true);

        var result = await running;

        Assert.Equal(ServiceActionOutcome.Stale, result.Outcome);
        Assert.Equal(new[] { "confirm", "queue" }, backend.Events);
        Assert.Empty(backend.Journal);
    }

    [Fact]
    public async Task RevalidationAfterQueueAndRestorePointRefusesNewDependent()
    {
        var backend = new FakeBackend();
        backend.BeforeExclusive = () => backend.Facts = backend.Facts with { DependentCount = 1 };

        var result = await Execute(backend, ServiceAction.Disable);

        Assert.Equal(ServiceActionOutcome.Stale, result.Outcome);
        Assert.Equal(new[] { "confirm", "queue", "begin", "exclusive", "complete" }, backend.Events);
        Assert.Empty(backend.Journal);
    }

    [Theory]
    [InlineData(ServiceAction.Disable)]
    [InlineData(ServiceAction.Start)]
    public async Task ReplacementSeenWhenHoldingIdentityPreventsMutationOnEitherPath(ServiceAction action)
    {
        var backend = new FakeBackend();
        backend.OnHold = () => backend.Facts = backend.Facts with
        {
            Account = "NT AUTHORITY\\LocalService", KeyVersion = 2,
        };

        var result = await Execute(backend, action);

        Assert.Equal(ServiceActionOutcome.Stale, result.Outcome);
        Assert.Equal(1, backend.Holds);
        Assert.False(backend.IdentityHeld);
        Assert.DoesNotContain("write", backend.Events);
        Assert.DoesNotContain("runtime", backend.Events);
        Assert.Empty(backend.Journal);
    }

    [Fact]
    public async Task CancellationDuringRestorePointPreventsWriteAndCompletesEmptyBatch()
    {
        using var cts = new CancellationTokenSource();
        var backend = new FakeBackend { BeforeExclusive = cts.Cancel };

        var result = await Execute(backend, ServiceAction.Disable, ct: cts.Token);

        Assert.Equal(ServiceActionOutcome.Declined, result.Outcome);
        Assert.Equal(new[] { "confirm", "queue", "begin", "exclusive", "complete" }, backend.Events);
        Assert.Empty(backend.Journal);
    }

    [Theory]
    [InlineData(false, false, ServiceActionOutcome.Failed)]
    [InlineData(true, false, ServiceActionOutcome.Unverified)]
    [InlineData(true, true, ServiceActionOutcome.Unverified)]
    public async Task SuccessfulResultRequiresWriterAcknowledgmentJournalAndMatchingReadback(bool accepted,
        bool unreadable, ServiceActionOutcome expected)
    {
        var backend = new FakeBackend { WriteAccepted = accepted, UpdateOnWrite = false, ReadBackAvailable = !unreadable };

        var result = await Execute(backend, ServiceAction.Disable);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(accepted ? Assert.Single(backend.Journal).Id : (Guid?)null, result.JournalRecordId);
    }

    [Theory]
    [InlineData(ServiceAction.Start, ServiceControllerStatus.Stopped, ServiceControllerStatus.Running)]
    [InlineData(ServiceAction.Stop, ServiceControllerStatus.Running, ServiceControllerStatus.Stopped)]
    public async Task RuntimeCommandsUseSeparatePathAndKeepStartupTypeAndJournalUntouched(ServiceAction action,
        ServiceControllerStatus initial, ServiceControllerStatus destination)
    {
        var backend = new FakeBackend();
        backend.Facts = backend.Facts with { Status = initial, Start = ServiceStartMode.Automatic };

        var result = await Execute(backend, action);

        Assert.Equal(ServiceActionOutcome.Applied, result.Outcome);
        Assert.Null(result.JournalRecordId);
        Assert.Empty(backend.Journal);
        Assert.Equal(destination, backend.Facts.Status);
        Assert.Equal(ServiceStartMode.Automatic, backend.Facts.Start);
        Assert.Equal(new[] { "confirm", "queue", "runtime", "readback" }, backend.Events);
    }

    [Theory]
    [InlineData(ServiceAction.Start, ServiceControllerStatus.Stopped, null)]
    [InlineData(ServiceAction.Start, ServiceControllerStatus.Stopped, 1)]
    [InlineData(ServiceAction.Stop, ServiceControllerStatus.Running, null)]
    [InlineData(ServiceAction.Stop, ServiceControllerStatus.Running, 1)]
    public async Task RuntimeRejectsUnknownOrAffectedDependencies(ServiceAction action, ServiceControllerStatus initial, int? dependents)
    {
        var backend = new FakeBackend();
        backend.Facts = backend.Facts with { Status = initial, DependentCount = dependents };

        Assert.Equal(ServiceActionOutcome.Blocked, (await Execute(backend, action)).Outcome);
        Assert.Empty(backend.Events);
    }

    [Fact]
    public void RecommendationPlanPreservesManualAndDetectsDriftInAnySelectedService()
    {
        var maps = new FakeBackend("MapsBroker", "Downloaded Maps Manager");
        maps.Facts = maps.Facts with { Start = ServiceStartMode.Automatic };
        var fax = new FakeBackend();
        var names = new[] { "MapsBroker", "Fax" };
        var plan = ServiceRecommendationPlan.Capture(names, fax.Settings, Read);

        Assert.NotNull(plan);
        Assert.Collection(plan.Changes,
            change => Assert.Equal(("MapsBroker", ServiceStartMode.Manual), (change.Entry.Name, change.Entry.Target)),
            change => Assert.Equal(("Fax", ServiceStartMode.Disabled), (change.Entry.Name, change.Entry.Target)));
        Assert.True(plan.Matches(ServiceRecommendationPlan.Capture(names, fax.Settings, Read)));

        fax.Facts = fax.Facts with { DependentCount = null };
        Assert.False(plan.Matches(ServiceRecommendationPlan.Capture(names, fax.Settings, Read)));

        fax.Facts = fax.Facts with { DependentCount = 0, KeyVersion = 3 };
        Assert.False(plan.Matches(ServiceRecommendationPlan.Capture(names, fax.Settings, Read)));

        ServiceActionFacts Read(string name) => name == "MapsBroker" ? maps.Facts : fax.Facts;
    }

    [Fact]
    public void RecommendationCompanionMustAlreadyBeAtTargetOrEligibleForTheSamePlan()
    {
        var child = new FakeBackend("PrintNotify", "Print Notifications");
        var spooler = new FakeBackend("Spooler", "Print Queue");
        var settings = child.Settings with { Expert = true };

        Assert.Null(ServiceRecommendationPlan.Capture(["PrintNotify"], settings, Read));

        spooler.Facts = spooler.Facts with { CanChange = false };
        Assert.Null(ServiceRecommendationPlan.Capture(["PrintNotify", "Spooler"], settings, Read));

        spooler.Facts = spooler.Facts with { Start = ServiceStartMode.Disabled };
        var plan = ServiceRecommendationPlan.Capture(["PrintNotify"], settings, Read);

        Assert.NotNull(plan);
        Assert.Equal("PrintNotify", Assert.Single(plan.Changes).Entry.Name);
        Assert.Contains(plan.Observed, observation => observation.Identity.Name == "Spooler");
        var heldNames = Array.Empty<string>();
        Assert.True(plan.ApplyIfCurrent(names =>
        {
            heldNames = names.ToArray();
            return new FakeHold(() => { });
        }, () => ServiceRecommendationPlan.Capture(["PrintNotify"], settings, Read), () => settings,
            _ => child.ObserveStartupWrite(ServiceStartMode.Disabled)));
        Assert.Equal(new[] { "PrintNotify", "Spooler" }, heldNames);

        child.Facts = child.Facts with { Start = ServiceStartMode.Manual, DependentCount = 0 };
        spooler.Facts = spooler.Facts with { Start = ServiceStartMode.Manual, CanChange = true };
        Assert.False(plan.Matches(ServiceRecommendationPlan.Capture(["PrintNotify"], settings, Read)));
        var both = ServiceRecommendationPlan.Capture(["PrintNotify", "Spooler"], settings, Read);
        Assert.NotNull(both);
        Assert.Equal(new[] { "Spooler", "PrintNotify" }, both.Changes.Select(change => change.Entry.Name));

        ServiceActionFacts Read(string name) => name == "Spooler" ? spooler.Facts : child.Facts;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecommendationBatchStopsBeforeNextWriteAfterExpertSettingsChange(bool downgradeExpert)
    {
        var first = new FakeBackend("MapsBroker", "Downloaded Maps Manager");
        first.Facts = first.Facts with { Start = ServiceStartMode.Automatic };
        var second = new FakeBackend("Spooler", "Print Queue");
        var settings = first.Settings with { Expert = true, ServicesExpert = true };
        var selected = new[] { "MapsBroker", "Spooler" };
        var plan = ServiceRecommendationPlan.Capture(selected, settings, Read);
        Assert.NotNull(plan);
        var written = new List<string>();
        var held = false;

        var completed = plan.ApplyIfCurrent(names =>
        {
            Assert.Equal(selected, names);
            held = true;
            return new FakeHold(() => held = false);
        }, () =>
        {
            Assert.True(held);
            return ServiceRecommendationPlan.Capture(selected, settings, Read);
        }, () => settings, change =>
        {
            Assert.True(held);
            written.Add(change.Entry.Name);
            settings = downgradeExpert ? settings with { Expert = false } : settings with { ServicesExpert = false };
            return first.ObserveStartupWrite(ServiceStartMode.Manual);
        });

        Assert.False(completed);
        Assert.Equal("MapsBroker", Assert.Single(written));
        Assert.False(held);

        ServiceActionFacts Read(string name) => name == "MapsBroker" ? first.Facts : second.Facts;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RecommendationBatchPreservesUnwrittenBaselineAfterItsOwnSuccessfulWrite(bool imageDrift, bool dependencyDrift)
    {
        var maps = new FakeBackend("MapsBroker", "Downloaded Maps Manager");
        maps.Facts = maps.Facts with { Start = ServiceStartMode.Automatic };
        var fax = new FakeBackend();
        var selected = new[] { "MapsBroker", "Fax" };
        var plan = ServiceRecommendationPlan.Capture(selected, maps.Settings, Read);
        Assert.NotNull(plan);
        var writes = new List<string>();

        var completed = plan.ApplyIfCurrent(_ => new FakeHold(() => { }),
            () => ServiceRecommendationPlan.Capture(selected, maps.Settings, Read), () => maps.Settings, change =>
        {
            var name = change.Facts.Identity.Name;
            writes.Add(name);
            if (name == "Fax") return fax.ObserveStartupWrite(ServiceStartMode.Disabled);

            var written = maps.ObserveStartupWrite(ServiceStartMode.Manual);
            if (imageDrift) fax.Facts = fax.Facts with { ImagePath = @"C:\Reconfigured\Fax.exe", KeyVersion = 7 };
            if (dependencyDrift) fax.Facts = fax.Facts with { DependentCount = 1 };
            return written;
        });

        if (imageDrift || dependencyDrift)
        {
            Assert.False(completed);
            Assert.Equal("MapsBroker", Assert.Single(writes));
            Assert.Equal(ServiceStartMode.Manual, fax.Facts.Start);
        }
        else
        {
            Assert.True(completed);
            Assert.Equal(selected, writes);
            Assert.Equal(ServiceStartMode.Disabled, fax.Facts.Start);
        }
        Assert.Equal(ServiceStartMode.Manual, maps.Facts.Start);

        ServiceActionFacts Read(string name) => name == "MapsBroker" ? maps.Facts : fax.Facts;
    }

    [Theory]
    [InlineData("Fax|Manual|anything")]
    [InlineData("|Manual|0")]
    [InlineData("Fax|100|0")]
    public void MalformedReplayCannotSelectTheCurrentService(string spec) => Assert.Null(ServicesViewModel.ParseStartSpec(spec));

    private static Task<ServiceActionResult> Execute(FakeBackend backend, ServiceAction action, string target = "Fax",
        bool exactOnly = false, CancellationToken ct = default) =>
        new ServiceActionWorkflow(backend).ExecuteAsync(new ServiceActionRequest(action, target) { ExactNameOnly = exactOnly }, ct);

    private sealed class FakeBackend : IServiceActionBackend, IServiceActionSession
    {
        public FakeBackend(string name = "Fax", string display = "Fax Capabilities")
        {
            var identity = new ServiceActionIdentity(name, display);
            Identities = [identity];
            Facts = new(identity, ServiceStartMode.Manual, ServiceControllerStatus.Stopped, 0x20, 1, 0,
                @"C:\Windows\system32\test.exe", "LocalSystem", null, false, 1, null, null, false, true, 0, true, null);
        }

        public ServiceActionSettings Settings { get; set; } = new(false, false, true, false, false, true);
        public ServiceActionFacts Facts { get; set; }
        public List<ServiceActionIdentity> Identities { get; }
        public List<string> Events { get; } = [];
        public List<FakeRecord> Journal { get; } = [];
        public int ScanCount { get; private set; }
        public bool ConfigReadable { get; set; } = true;
        public bool ReadBackAvailable { get; set; } = true;
        public bool WriteAccepted { get; set; } = true;
        public bool UpdateOnWrite { get; set; } = true;
        public Func<ServiceActionPlan, Task<bool>> Approve { get; set; } = _ => Task.FromResult(true);
        public Action? OnScan { get; set; }
        public Action? BeforeExclusive { get; set; }
        public Action? OnHold { get; set; }
        public bool IdentityHeld { get; private set; }
        public int Holds { get; private set; }

        public ServiceActionSettings ReadSettings() => Settings;

        public ServiceActionFacts ObserveStartupWrite(ServiceStartMode mode)
        {
            Facts = Facts with
            {
                Start = mode,
                KeyVersion = Facts.KeyVersion + 1,
                ActiveRecordId = Guid.NewGuid(),
                DependentCount = mode == ServiceStartMode.Disabled ? null : Facts.DependentCount,
            };
            return Facts;
        }

        public IReadOnlyList<ServiceActionIdentity> ScanIdentities()
        {
            ScanCount++;
            OnScan?.Invoke();
            return Identities;
        }

        public ServiceActionFacts? ReadFacts(ServiceActionIdentity identity, ServiceAction action, bool elevated) =>
            ConfigReadable && identity == Facts.Identity ? Facts : null;

        public IDisposable HoldIdentity(ServiceActionIdentity identity)
        {
            Assert.Equal(Facts.Identity, identity);
            Assert.False(IdentityHeld);
            IdentityHeld = true;
            Holds++;
            OnHold?.Invoke();
            return new FakeHold(() => IdentityHeld = false);
        }

        public Task<bool> ConfirmAsync(ServiceActionPlan plan)
        {
            Events.Add("confirm");
            return Approve(plan);
        }

        public async Task<bool> RunAsync(ServiceActionPlan plan, Func<Task> work)
        {
            Events.Add("queue");
            await work();
            return true;
        }

        public Task<IServiceActionSession?> BeginAsync(ServiceActionPlan plan)
        {
            Events.Add("begin");
            return Task.FromResult<IServiceActionSession?>(this);
        }

        public void Exclusive(Action body)
        {
            Events.Add("exclusive");
            BeforeExclusive?.Invoke();
            body();
        }

        public ServiceActionWrite Apply(ServiceActionPlan plan)
        {
            Assert.True(IdentityHeld);
            Events.Add("write");
            if (!WriteAccepted) return new(false, null);
            var record = new FakeRecord(Guid.NewGuid(), plan.Facts.Identity.Name, Facts.Start, plan.Destination, Facts.RawDelayed);
            Journal.Add(record);
            if (UpdateOnWrite) Facts = Facts with { Start = plan.Destination };
            return new(true, record.Id);
        }

        public async Task<bool> ApplyRuntimeAsync(ServiceActionPlan plan)
        {
            await Task.Yield();
            Assert.True(IdentityHeld);
            Events.Add("runtime");
            Facts = Facts with { Status = plan.DestinationStatus!.Value };
            return true;
        }

        public ServiceActionFacts? ReadBack(ServiceActionPlan plan)
        {
            Assert.True(IdentityHeld);
            Events.Add("readback");
            return ReadBackAvailable ? Facts : null;
        }

        public void Dispose() => Events.Add("complete");
    }

    private sealed class FakeHold(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private sealed record FakeRecord(Guid Id, string Name, ServiceStartMode Before, ServiceStartMode After, bool? RawDelayed);
}
