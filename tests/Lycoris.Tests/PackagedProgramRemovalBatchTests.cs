using System.IO;
using Lycoris.Core;
using Lycoris.Modules.Removal;

namespace Lycoris.Tests;

[Collection("Registry")]
public sealed class PackagedProgramRemovalBatchTests : IDisposable
{
    private const string User = "S-1-5-21-1-2-3-1001";
    private const string OtherUser = "S-1-5-21-1-2-3-1002";
    private static readonly InstalledPackageRegistration App = Package("Microsoft.Example");
    private static readonly InstalledPackageRegistration Other = Package("ThirdParty.Example");
    private static readonly InstalledPackageRegistration Runtime = Package("Example.Runtime") with { Protected = true };
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"LycPackagedRemoval-{Guid.NewGuid():N}.json");
    private readonly bool _previousBypass = ChangeSession.BypassElevationForTests;

    public PackagedProgramRemovalBatchTests() => ChangeSession.BypassElevationForTests = true;

    public void Dispose()
    {
        ChangeSession.BypassElevationForTests = _previousBypass;
        File.Delete(_path);
        File.Delete(_path + ".bak");
    }

    [Fact]
    public async Task Preview_requires_proven_optional_store_packages_and_exact_identity()
    {
        var protectedApp = Package("Microsoft.Keep") with { Protected = true };
        var nonRemovable = Package("Microsoft.System");
        var missingFlag = Package("ThirdParty.Unknown");
        var otherSignature = Package("ThirdParty.Sideload") with { StoreSigned = false };
        var extension = Package("ThirdParty.Codec") with { HasAppListEntry = false };
        var h = Create(Snapshot(App, protectedApp, nonRemovable, missingFlag, otherSignature, extension));
        h.Current = h.Current with
        {
            Appx = h.Current.Appx.Select(entry => entry.PackageFullName == nonRemovable.FullName ? entry with { NonRemovable = true }
                    : entry.PackageFullName == missingFlag.FullName ? entry with { NonRemovable = null } : entry).ToArray(),
        };

        var plan = await h.Action.PrepareAsync(default);

        var row = Assert.Single(plan.Items);
        Assert.Equal(App.FullName, row.NativeInstallerId);
        Assert.Equal(User, plan.UserSid);
        Assert.Empty(h.Calls);
    }

    [Theory]
    [InlineData("Microsoft.GamingApp")]
    [InlineData("Microsoft.XboxGamingOverlay")]
    [InlineData("Microsoft.XboxGameOverlay")]
    public async Task Gaming_family_is_excluded_even_when_the_store_registration_is_removable(string name)
    {
        var gaming = MicrosoftStoreApp(name);
        var optional = MicrosoftStoreApp("Microsoft.BingWeather");
        var h = Create(Snapshot(gaming, optional));

        var plan = await h.Action.PrepareAsync(default);

        Assert.Equal(optional.FullName, Assert.Single(plan.Items).Key);
        var rejected = await h.Execute(plan, gaming.FullName, optional.FullName);
        Assert.Equal(0, rejected.Applied);
        Assert.Equal(2, rejected.Skipped);
        Assert.Empty(h.Calls);
        Assert.Empty(h.Journal.ProgramRemovals);

        var currentPlan = await h.Action.PrepareAsync(default);
        h.Remove = _ =>
        {
            h.Current = Snapshot(gaming);
            return 0;
        };
        var allowed = await h.Execute(currentPlan, optional.FullName);
        Assert.Equal(1, allowed.Applied);
        Assert.Equal(optional.FullName, Assert.Single(h.Calls));
    }

    private static InstalledPackageRegistration MicrosoftStoreApp(string name)
    {
        const string microsoftPublisherId = "8wekyb3d8bbwe";
        var family = $"{name}_{microsoftPublisherId}";
        return Package(name) with
        {
            Family = family, FullName = $"{name}_1.0.0.0_x64__{microsoftPublisherId}",
            Protected = PackagedRemovalInventory.IsProtectedFamily(family, null),
        };
    }

    [Fact]
    public async Task Dependencies_require_a_registered_consumer_outside_the_selection()
    {
        var app = App with { Dependencies = [Runtime.FullName] };
        var other = Other with { Dependencies = [Runtime.FullName] };
        var incoming = Package("Example.Related") with { Protected = true, Dependencies = [Other.FullName, Runtime.FullName] };
        var h = Create(Snapshot(app, Runtime));
        Assert.Empty((await h.Action.PrepareAsync(default)).Items);
        h.Current = Snapshot(app, other, Runtime, incoming);
        Assert.Equal(App.FullName, Assert.Single((await h.Action.PrepareAsync(default)).Items).Key);
        h.Current = Snapshot(app, other, Runtime);
        var plan = await h.Action.PrepareAsync(default);

        Assert.Equal(2, plan.Items.Count);
        var result = await h.Execute(plan, App.FullName, Other.FullName);
        Assert.Equal(2, result.Skipped);
        Assert.Empty(h.Calls);
        Assert.Empty(h.Journal.ProgramRemovals);
    }

    [Fact]
    public async Task Independent_reference_retains_shared_runtime_and_audit_precedes_exact_dispatch()
    {
        var other = Other with { Protected = true, Dependencies = [Runtime.FullName] };
        var h = Create(Snapshot(App with { Dependencies = [Runtime.FullName] }, other, Runtime));
        h.Remove = key =>
        {
            Assert.Equal(App.FullName, key);
            var attempt = Assert.Single(ChangeJournal.ForTests(_path).ProgramRemovals);
            Assert.Equal(ProgramRemovalAuditOutcome.Attempted, attempt.Outcome);
            Assert.Equal(ProgramRemovalScope.User, attempt.Scope);
            Assert.EndsWith(App.FullName, attempt.PackageId);
            h.Current = Snapshot(other, Runtime);
            return 0;
        };

        var result = await h.Execute(await h.Action.PrepareAsync(default), App.FullName);

        Assert.Equal(1, result.Applied);
        Assert.Equal(ProgramRemovalAuditOutcome.Removed, Assert.Single(ChangeJournal.ForTests(_path).ProgramRemovals).Outcome);
        Assert.Empty(h.Journal.Records);
        Assert.Empty(h.Journal.Batches);
    }

    [Fact]
    public async Task Store_family_removes_only_the_confirmed_main_and_allows_residual_bookkeeping()
    {
        var (main, bundle, resource) = BundledApp();
        var h = Create(Snapshot(main, bundle, resource));
        h.Remove = key =>
        {
            Assert.Equal(main.FullName, key);
            h.Current = Snapshot(bundle, resource);
            return 0;
        };
        var plan = await h.Action.PrepareAsync(default);
        var item = Assert.Single(plan.Items);
        Assert.Equal(main.FullName, item.NativeInstallerId);
        Assert.Equal(new[] { bundle.FullName, resource.FullName }.Order(StringComparer.OrdinalIgnoreCase), item.RelatedPackageIds);

        var result = await h.Execute(plan, main.FullName);

        Assert.Equal(1, result.Applied);
        Assert.Equal(main.FullName, Assert.Single(h.Calls));
        Assert.Equal(ProgramRemovalAuditOutcome.Removed, Assert.Single(h.Journal.ProgramRemovals).Outcome);
    }

    [Theory]
    [InlineData("second-main")]
    [InlineData("other-kind")]
    [InlineData("different-version")]
    [InlineData("non-removable-resource")]
    [InlineData("external-resource-consumer")]
    [InlineData("new-sibling")]
    public async Task Store_family_rejects_unconfirmed_sibling_changes_and_external_dependents(string change)
    {
        var (main, bundle, resource) = BundledApp();
        var h = Create(Snapshot(main, bundle, resource));
        var plan = await h.Action.PrepareAsync(default);
        h.Current = change switch
        {
            "second-main" => Snapshot(main, bundle with { Kind = PackagedRegistrationKind.Main }, resource),
            "other-kind" => Snapshot(main, bundle, resource with { Kind = PackagedRegistrationKind.Other }),
            "different-version" => Snapshot(main, bundle with { Version = "2.0.0.0" }, resource),
            "external-resource-consumer" => Snapshot(main, bundle, resource, Other with { Dependencies = [resource.FullName] }),
            "new-sibling" => Snapshot(main, bundle with { FullName = bundle.FullName.Replace("_neutral_", "_x64_") }, resource),
            _ => h.Current with
            {
                Appx = h.Current.Appx.Select(row => row.PackageFullName == resource.FullName ? row with { NonRemovable = true } : row).ToArray(),
            },
        };

        var result = await h.Execute(plan, main.FullName);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(h.Calls);
        Assert.Empty(h.Journal.ProgramRemovals);
        Assert.DoesNotContain((await h.Action.PrepareAsync(default)).Items, item => item.Key == main.FullName && change != "new-sibling");
    }

    [Fact]
    public async Task Main_replacement_stays_unknown_even_with_original_bundle_and_resource()
    {
        var (main, bundle, resource) = BundledApp();
        var h = Create(Snapshot(main, bundle, resource));
        h.Remove = _ =>
        {
            h.Current = Snapshot(main with { FullName = main.FullName.Replace("1.0.0.0", "2.0.0.0"), Version = "2.0.0.0" },
                bundle, resource);
            return 0;
        };

        var result = await h.Execute(await h.Action.PrepareAsync(default), main.FullName);

        Assert.True(result.UnknownOutcome);
        Assert.Equal(0, result.Applied);
        Assert.Equal(ProgramRemovalAuditOutcome.Attempted, Assert.Single(h.Journal.ProgramRemovals).Outcome);
    }

    [Theory]
    [InlineData("Microsoft.LanguageExperiencePackru-RU_8wekyb3d8bbwe", true)]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Singleton_8wekyb3d8bbwe", true)]
    [InlineData("MicrosoftCorporationII.WinAppRuntime.Main.2_8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.WindowsStore_8wekyb3d8bbwe", true)]
    [InlineData("Lycoris_localpublisher", true)]
    [InlineData("NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj", true)]
    [InlineData("RealtekSemiconductorCorp.RealtekAudioControl_dt26b99r8h8gj", true)]
    [InlineData("Microsoft.GamingServices_8wekyb3d8bbwe", true)]
    [InlineData("MicrosoftWindows.Client.WebExperience_cw5n1h2txyewy", true)]
    [InlineData("Microsoft.BingWeather_8wekyb3d8bbwe", false)]
    public void Infrastructure_is_protected_even_if_Windows_marks_its_registration_removable(string family, bool expected) =>
        Assert.Equal(expected, PackagedRemovalInventory.IsProtectedFamily(family, null));

    [Theory]
    [InlineData("changed-sid")]
    [InlineData("snapshot-sid")]
    [InlineData("replaced")]
    [InlineData("parallel-family")]
    [InlineData("incomplete")]
    [InlineData("missing-appx")]
    [InlineData("different-edges")]
    [InlineData("missing-app-entry")]
    [InlineData("protected-registration")]
    [InlineData("expired")]
    [InlineData("runtime-unsupported")]
    [InlineData("safety-lock")]
    public async Task Freshness_and_scope_changes_prevent_dispatch(string change)
    {
        var h = Create(Snapshot(App));
        var plan = await h.Action.PrepareAsync(default);
        var wasLocked = SafetyLock.IsLocked;
        try
        {
            switch (change)
            {
                case "changed-sid": h.DesktopSid = OtherUser; break;
                case "snapshot-sid": h.Current = h.Current with { UserSid = OtherUser }; break;
                case "replaced": h.Current = Snapshot(Replacement()); break;
                case "parallel-family": h.Current = Snapshot(App, Replacement()); break;
                case "incomplete": h.Current = h.Current with { Native = h.Current.Native with { Complete = false } }; break;
                case "missing-appx": h.Current = h.Current with { Appx = [] }; break;
                case "different-edges":
                    h.Current = h.Current with { Appx = [h.Current.Appx[0] with { Dependencies = [Runtime.FullName] }] };
                    break;
                case "missing-app-entry": h.Current = Snapshot(App with { HasAppListEntry = false }); break;
                case "protected-registration": h.Current = Snapshot(App with { Protected = true }); break;
                case "expired": h.Clock.Now = h.Clock.Now.AddMinutes(6); break;
                case "runtime-unsupported": h.Supported = false; break;
                case "safety-lock": ChangeSession.Locked = true; break;
            }
            var result = await h.Execute(plan, App.FullName);
            Assert.Equal(1, result.Skipped);
            Assert.Empty(h.Calls);
            Assert.Empty(h.Journal.ProgramRemovals);
        }
        finally { ChangeSession.Locked = wasLocked; }
    }

    [Fact]
    public async Task Cancel_after_durable_attempt_before_dispatch_marks_not_started()
    {
        using var cancel = new CancellationTokenSource();
        var h = Create(Snapshot(App));
        var plan = await h.Action.PrepareAsync(default);
        h.OnRead = () =>
        {
            if (h.Journal.ProgramRemovals.Count > 0) cancel.Cancel();
        };

        var result = await h.Action.ExecuteAsync(plan, new HashSet<string> { App.FullName }, null, cancel.Token);

        Assert.Equal(1, result.Skipped);
        Assert.Empty(h.Calls);
        Assert.Equal(ProgramRemovalAuditOutcome.NotStarted, Assert.Single(h.Journal.ProgramRemovals).Outcome);
    }

    [Fact]
    public async Task Audit_failure_prevents_dispatch()
    {
        var h = new Harness(ChangeJournal.ForTests(), Snapshot(App));

        var result = await h.Execute(await h.Action.PrepareAsync(default), App.FullName);

        Assert.Single(result.Errors);
        Assert.Empty(h.Calls);
    }

    [Theory]
    [InlineData("deferred")]
    [InlineData("replacement")]
    [InlineData("cancel")]
    [InlineData("invalid-snapshot")]
    public async Task Deferred_or_unverified_attempt_stops_batch_and_survives_restart(string disposition)
    {
        using var cancel = new CancellationTokenSource();
        var h = Create(Snapshot(App, Other));
        h.Remove = _ =>
        {
            if (disposition == "replacement") h.Current = Snapshot(Replacement(), Other);
            if (disposition == "cancel") cancel.Cancel();
            if (disposition == "invalid-snapshot") h.Current = h.Current with { UserSid = OtherUser };
            return 0;
        };
        var plan = await h.Action.PrepareAsync(default);

        var result = await h.Action.ExecuteAsync(plan, new HashSet<string> { App.FullName, Other.FullName }, null, cancel.Token);

        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(disposition == "deferred" ? 1 : 0, result.Pending);
        Assert.Equal(disposition != "deferred", result.UnknownOutcome);
        Assert.Equal(App.FullName, Assert.Single(h.Calls));
        var reopened = ChangeJournal.ForTests(_path);
        Assert.Equal(ProgramRemovalAuditOutcome.Attempted, Assert.Single(reopened.ProgramRemovals).Outcome);
        var restarted = new Harness(reopened, Snapshot(Replacement(), Other));
        Assert.Equal(Other.FullName, Assert.Single((await restarted.Action.PrepareAsync(default)).Items).Key);
        Assert.Empty(restarted.Calls);
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("""[{"PackageFullName":"Foo_1_x64__abc","PackageFamilyName":"Foo_abc","NonRemovable":false,"Dependencies":[]}]""", true)]
    [InlineData("""[{"PackageFullName":"Foo_1_x64__abc","PackageFamilyName":"Foo_abc","Dependencies":[]}]""", true)]
    [InlineData("""[{"PackageFullName":"Foo_1_neutral_~_abc","PackageFamilyName":"Foo_abc","NonRemovable":false,"Dependencies":[]}]""", true)]
    [InlineData("""[{"PackageFullName":"Foo~_1_x64__abc","PackageFamilyName":"Foo_abc","NonRemovable":false,"Dependencies":[]}]""", false)]
    [InlineData("""[{"PackageFullName":"Foo_1_x64__abc","PackageFamilyName":"Foo_abc","NonRemovable":false}]""", false)]
    [InlineData("""[{"PackageFullName":"Foo_1_x64__abc","PackageFamilyName":"Foo_abc","NonRemovable":"False","Dependencies":[]}]""", false)]
    [InlineData("""[{"PackageFullName":"Foo_1_x64__abc","PackageFamilyName":"Foo/abc","NonRemovable":false,"Dependencies":[]}]""", false)]
    [InlineData("[null]", false)]
    public void Appx_metadata_does_not_infer_false_from_unknown_or_malformed_evidence(string json, bool valid)
    {
        var entries = PackagedRemovalInventory.ParseAppxMetadata(json);
        Assert.Equal(valid, entries is not null);
        if (valid && !json.Contains("NonRemovable", StringComparison.Ordinal) && entries?.Count == 1)
            Assert.Null(entries[0].NonRemovable);
    }

    private Harness Create(PackagedUninstallSnapshot snapshot) => new(ChangeJournal.ForTests(_path), snapshot);

    private static InstalledPackageRegistration Package(string name) =>
        new($"{name}_1.0.0.0_x64__publisher", $"{name}_publisher", name, null, "1.0.0.0", false, true, []) { HasAppListEntry = true };

    private static (InstalledPackageRegistration Main, InstalledPackageRegistration Bundle, InstalledPackageRegistration Resource) BundledApp()
    {
        var main = Package("Microsoft.BingWeather");
        var bundle = main with
        {
            FullName = "Microsoft.BingWeather_1.0.0.0_neutral_~_publisher", Kind = PackagedRegistrationKind.Bundle, HasAppListEntry = false,
        };
        var resource = main with
        {
            FullName = "Microsoft.BingWeather_1.0.0.0_neutral_split.language-ru_publisher", Kind = PackagedRegistrationKind.Resource, HasAppListEntry = false,
        };
        return (main with { Dependencies = [resource.FullName] }, bundle, resource);
    }

    private static InstalledPackageRegistration Replacement() => App with
    {
        FullName = App.FullName.Replace("1.0.0.0", "2.0.0.0"),
        Version = "2.0.0.0",
    };

    private static PackagedUninstallSnapshot Snapshot(params InstalledPackageRegistration[] packages) =>
        new(User, new([], true) { Registrations = packages },
            packages.Select(row => new AppxRemovalRegistration(row.FullName, row.Family, false, row.Dependencies.ToArray())).ToArray());

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness
    {
        public Harness(ChangeJournal journal, PackagedUninstallSnapshot current)
        {
            Journal = journal;
            Current = current;
            Action = new(() => DesktopSid, (_, _) =>
            {
                OnRead?.Invoke();
                return Task.FromResult<PackagedUninstallSnapshot?>(Current);
            }, () => Supported, (key, _) =>
            {
                Calls.Add(key);
                return Task.FromResult(Remove(key));
            }, Clock, Journal);
        }

        public ChangeJournal Journal { get; }
        public PackagedUninstallSnapshot Current { get; set; }
        public PackagedProgramRemovalBatchAction Action { get; }
        public List<string> Calls { get; } = [];
        public TestClock Clock { get; } = new();
        public string? DesktopSid { get; set; } = User;
        public bool Supported { get; set; } = true;
        public Action? OnRead { get; set; }
        public Func<string, int> Remove { get; set; } = _ => 0;

        public Task<RemovalBatchResult> Execute(RemovalBatchPlan plan, params string[] keys) =>
            Action.ExecuteAsync(plan, keys.ToHashSet(), null, default);
    }
}
