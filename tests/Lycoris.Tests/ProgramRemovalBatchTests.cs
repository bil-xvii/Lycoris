using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lycoris.Core;
using Lycoris.Modules.Apps;
using Lycoris.Modules.Games.Sources;
using Lycoris.Modules.Removal;

namespace Lycoris.Tests;

[Collection("Registry")]
public sealed class ProgramRemovalBatchTests : IDisposable
{
    private const string Photo = "Lumen Photo Editor";
    private const string Editor = "{12345678-1234-1234-1234-123456789ABC}_is1";
    private const string Notes = "BridgeNotes";
    private const string PhotoId = @"ARP\Machine\X64\" + Photo;
    private const string EditorId = @"ARP\User\X64\" + Editor;
    private const string NotesId = @"ARP\Machine\X86\" + Notes;
    private const string OfficialSource = "https://cdn.winget.microsoft.com/cache";
    private const string SourceIdentifier = "Microsoft.Winget.Source_8wekyb3d8bbwe";
    private const string SourceType = "Microsoft.PreIndexed.Package";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"LycProgramRemoval-{Guid.NewGuid():N}");
    private readonly bool _previousBypass = ChangeSession.BypassElevationForTests;

    public ProgramRemovalBatchTests()
    {
        Directory.CreateDirectory(_dir);
        ChangeSession.BypassElevationForTests = true;
    }

    public void Dispose()
    {
        ChangeSession.BypassElevationForTests = _previousBypass;
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Preview_assesses_arbitrary_desktop_apps_with_one_inspection_and_two_cli_calls()
    {
        var h = Create();
        for (var i = 0; i < 64; i++) h.Add($"DeskTool_{i}");
        h.OnSource = () => h.Clock.Now += TimeSpan.FromMinutes(6);

        var plan = await h.Action.PrepareAsync(default);

        Assert.Equal(65, plan.Items.Count);
        Assert.Equal(65, plan.AssessedRows.Count);
        var photo = Assert.Single(plan.Items, item => item.Key == PhotoId);
        Assert.Equal(PhotoId, photo.NativeInstallerId);
        Assert.Equal("25.01", photo.Version);
        Assert.Equal(h.Clock.Now.UtcDateTime, plan.ObservedUtc);
        Assert.Equal(1, h.InspectionCount);
        Assert.Equal(2, h.Calls.Count);
        Assert.Equal(new[] { "--version" }, h.Calls[0]);
        Assert.Equal(new[] { "source", "export", "--name", "winget", "--disable-interactivity" }, h.Calls[1]);
        Assert.Empty(h.Dispatches);
    }

    [Theory]
    [InlineData("Machine32")]
    [InlineData("DesktopUser")]
    public async Task Preview_refuses_same_product_code_in_another_view_or_scope_even_with_different_labels(string otherScope)
    {
        var h = Create();
        h.Entries.Add(h.Entries[0] with
        {
            Scope = Enum.Parse<UninstallScope>(otherScope),
            KeyName = Photo.ToUpperInvariant(),
            DisplayName = "Unrelated Product",
        });

        var plan = await h.Action.PrepareAsync(default);

        Assert.Empty(plan.Items);
        Assert.Equal(2, plan.AssessedRows.Count);
        Assert.All(plan.AssessedRows, row => Assert.Equal(RemovalDecision.Review, row.Decision));
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Preview_preserves_assessments_and_requires_both_proof_and_positive_decision()
    {
        var h = Create();
        h.Add(Editor, UninstallScope.DesktopUser);
        h.Add(Notes, UninstallScope.Machine32);
        h.Add("--force");
        h.Decisions[Photo] = (RemovalDecision.ConsiderRemoval, false);
        h.Decisions[Notes] = (RemovalDecision.Keep, true);

        var plan = await h.Action.PrepareAsync(default);

        Assert.Equal(EditorId, Assert.Single(plan.Items).Key);
        Assert.Equal(4, plan.AssessedRows.Count);
        Assert.Equal(RemovalDecision.Review, Assert.Single(plan.AssessedRows, row => row.Name == Photo).Decision);
        Assert.Equal(RemovalDecision.Keep, Assert.Single(plan.AssessedRows, row => row.Name == Notes).Decision);
        Assert.Equal(RemovalDecision.Review, Assert.Single(plan.AssessedRows, row => row.Name == "--force").Decision);
    }

    [Fact]
    public async Task Partial_inventory_and_source_failure_keep_assessments_with_explicit_unavailable_reason()
    {
        var h = Create();
        h.State = UninstallReadState.Partial;
        var partial = await h.Action.PrepareAsync(default);
        Assert.NotNull(partial.UnavailableReason);
        Assert.Empty(partial.Items);
        Assert.Equal(RemovalDecision.Review, Assert.Single(partial.AssessedRows).Decision);
        Assert.Empty(h.Calls);

        h.State = UninstallReadState.Complete;
        h.SourceArgument = "https://other.example/cache";
        var unavailable = await h.Action.PrepareAsync(default);
        Assert.NotNull(unavailable.UnavailableReason);
        Assert.Empty(unavailable.Items);
        Assert.Equal(RemovalDecision.Review, Assert.Single(unavailable.AssessedRows).Decision);
        Assert.Equal(new[] { "--version", "source" }, h.Calls.Select(call => call[0]));
    }

    [Theory]
    [InlineData("v1.6.3482", true)]
    [InlineData("v1.8.1762", true)]
    [InlineData("v1.8.1791 bad suffix", false)]
    [InlineData("", false)]
    public async Task Unverified_or_older_client_leaves_desktop_rows_for_review(string version, bool tooOld)
    {
        var h = Create();
        h.ClientVersion = version;

        var plan = await h.Action.PrepareAsync(default);

        Assert.Empty(plan.Items);
        Assert.Equal(RemovalDecision.Review, Assert.Single(plan.AssessedRows).Decision);
        Assert.Equal(tooOld
            ? Loc.Tf("Для пакетного удаления требуется WinGet версии {0} или новее.", "1.8.1791")
            : Loc.T("Не удалось подтвердить версию WinGet для пакетного удаления."), plan.UnavailableReason);
        Assert.Equal(new[] { "--version" }, Assert.Single(h.Calls));
    }

    [Fact]
    public async Task Version_is_rechecked_after_consent_and_older_client_never_dispatches()
    {
        var h = Create();
        h.ClientVersion = "v1.8.1791";
        var plan = await h.Action.PrepareAsync(default);
        Assert.Equal(PhotoId, Assert.Single(plan.Items).Key);
        h.ClientVersion = "v1.8.1762";
        h.Calls.Clear();

        var result = await h.Action.ExecuteAsync(plan, Keys(PhotoId), null, default);

        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.False(result.UnknownOutcome);
        Assert.Empty(h.Dispatches);
        Assert.Empty(h.Journal.ProgramRemovals);
        Assert.Equal(new[] { "--version" }, Assert.Single(h.Calls));
        Assert.Contains(Loc.Tf("Для пакетного удаления требуется WinGet версии {0} или новее.", "1.8.1791"), result.Errors);
    }

    [Fact]
    public async Task Changed_or_new_arp_after_analysis_cannot_enter_the_preview()
    {
        var h = Create();
        h.Add(Editor, UninstallScope.DesktopUser);
        h.OnSource = () =>
        {
            h.Entries[0] = h.Entries[0] with { UninstallFingerprint = "changed" };
            h.Add(Notes);
        };

        var plan = await h.Action.PrepareAsync(default);

        Assert.Equal(EditorId, Assert.Single(plan.Items).Key);
        Assert.Equal(2, plan.AssessedRows.Count);
        Assert.DoesNotContain(plan.AssessedRows, row => row.Name == Notes);
        Assert.Equal(RemovalDecision.Review, Assert.Single(plan.AssessedRows, row => row.Name == Photo).Decision);
    }

    [Fact]
    public async Task Packaged_assessments_are_retained_when_WinGet_source_is_unavailable()
    {
        var h = Create();
        h.SourceArgument = "https://other.example/cache";
        h.Packaged = new([PackagedRow("Contoso.DigitalSketch", RemovalDecision.Review),
            PackagedRow("Contoso.SharedSupport", RemovalDecision.Keep)], true);

        var plan = await h.Action.PrepareAsync(default);

        Assert.NotNull(plan.UnavailableReason);
        Assert.Empty(plan.Items);
        Assert.Equal(3, plan.AssessedRows.Count);
        Assert.Contains(plan.AssessedRows, row => row is { Key: "msix:Contoso.DigitalSketch", Decision: RemovalDecision.Review });
        Assert.Contains(plan.AssessedRows, row => row is { Key: "msix:Contoso.SharedSupport", Decision: RemovalDecision.Keep });
    }

    [Fact]
    public async Task Package_query_problem_is_explicit_and_does_not_hide_an_eligible_desktop_entry()
    {
        var h = Create();
        h.Packaged = new([PackagedRow("Contoso.DigitalSketch", RemovalDecision.Review)], false);

        var plan = await h.Action.PrepareAsync(default);

        Assert.Null(plan.UnavailableReason);
        Assert.Equal(PhotoId, Assert.Single(plan.Items).Key);
        Assert.Equal(2, plan.AssessedRows.Count);
        Assert.NotNull(plan.Summary);
        Assert.Contains(Loc.T("Не удалось проверить пакеты Microsoft Store для пользователя рабочего стола."), plan.Summary);
    }

    [Fact]
    public async Task Packaged_inventory_does_not_need_WinGet_when_no_desktop_candidate_is_found()
    {
        var h = Create();
        h.Entries.Clear();
        h.Packaged = new([PackagedRow("Contoso.DigitalSketch", RemovalDecision.Review)], true);

        var plan = await h.Action.PrepareAsync(default);

        Assert.Null(plan.UnavailableReason);
        Assert.Empty(plan.Items);
        Assert.Equal("msix:Contoso.DigitalSketch", Assert.Single(plan.AssessedRows).Key);
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Uninstall_launches_only_the_approved_native_id_after_durable_audit_without_creating_revert_records()
    {
        var h = Create();
        h.Add(Editor, UninstallScope.DesktopUser);
        var plan = await h.Action.PrepareAsync(default);
        h.DispatchOverride = id =>
        {
            Assert.Equal(EditorId, id);
            var before = ChangeJournal.ForTests(h.Path);
            var audit = Assert.Single(before.ProgramRemovals);
            Assert.Equal(plan.Id, audit.BatchId);
            Assert.Equal(EditorId, audit.PackageId);
            Assert.Equal(ProgramRemovalAuditOutcome.Attempted, audit.Outcome);
            Assert.Equal(ProgramRemovalScope.User, audit.Scope);
            Assert.Null(audit.ExitCode);
            Assert.Empty(before.Records);
            Assert.Empty(before.Batches);
            h.Remove(id);
            return Done(0);
        };

        var result = await h.Action.ExecuteAsync(plan, Keys(EditorId), null, default);

        Assert.Equal(1, result.Applied);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(new[] { "uninstall", "--id", EditorId, "--exact", "--source", "winget", "--scope", "user",
            "--disable-interactivity", "--version", "25.01", "--product-code", Editor, "--interactive" }, Assert.Single(h.Dispatches));
        var saved = ChangeJournal.ForTests(h.Path);
        var final = Assert.Single(saved.ProgramRemovals);
        Assert.Equal(ProgramRemovalAuditOutcome.Removed, final.Outcome);
        Assert.True(final.FreshVerified);
        Assert.Empty(saved.Records);
        Assert.Empty(saved.Batches);
        Assert.Equal(Photo, Assert.Single(h.Entries).KeyName);
    }

    [Fact]
    public async Task Confirmed_batch_keeps_revalidating_later_items_after_vendor_window_outlasts_plan_age()
    {
        var h = Create();
        h.Add(Editor, UninstallScope.DesktopUser);
        var plan = await h.Action.PrepareAsync(default);
        h.DispatchOverride = id =>
        {
            if (id == PhotoId) h.Clock.Now += TimeSpan.FromMinutes(12);
            h.Remove(id);
            return Done(0);
        };

        var result = await h.Action.ExecuteAsync(plan, Keys(PhotoId, EditorId), null, default);

        Assert.Equal(2, result.Applied);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(2, h.RevalidationCount);
        Assert.Equal(new[] { PhotoId, EditorId }, h.Dispatches.Select(args => args[2]));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("sid")]
    [InlineData("elevation")]
    [InlineData("unknown-selection")]
    [InlineData("cloned-plan")]
    [InlineData("cancel")]
    public async Task Confirmation_is_bound_to_original_plan_scope_age_and_selected_keys(string change)
    {
        var h = Create();
        var plan = await h.Action.PrepareAsync(default);
        var keys = Keys(PhotoId);
        using var cancel = new CancellationTokenSource();
        switch (change)
        {
            case "stale": h.Clock.Now += TimeSpan.FromMinutes(6); break;
            case "sid": h.Sid = "S-1-5-21-200"; break;
            case "elevation": h.Elevated = false; break;
            case "unknown-selection":
                h.Add(Editor, UninstallScope.DesktopUser);
                keys.Add(EditorId);
                break;
            case "cloned-plan": plan = plan with { ObservedUtc = h.Clock.Now.UtcDateTime }; break;
            case "cancel": cancel.Cancel(); break;
        }

        var result = await h.Action.ExecuteAsync(plan, keys, null, cancel.Token);

        Assert.Equal(0, result.Applied);
        Assert.False(result.UnknownOutcome);
        Assert.Empty(h.Dispatches);
        Assert.Empty(h.Journal.ProgramRemovals);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("key-case")]
    [InlineData("scope")]
    [InlineData("command")]
    [InlineData("source")]
    [InlineData("native-list")]
    [InlineData("native-list-error")]
    [InlineData("proof")]
    [InlineData("after-save")]
    public async Task Drift_or_incomplete_exact_probe_cannot_dispatch(string change)
    {
        var h = Create();
        var plan = await h.Action.PrepareAsync(default);
        switch (change)
        {
            case "version": h.Entries[0] = h.Entries[0] with { Version = "26.0" }; break;
            case "duplicate": h.Entries.Add(h.Entries[0] with { Scope = UninstallScope.DesktopUser }); break;
            case "key-case": h.Entries[0] = h.Entries[0] with { KeyName = Photo.ToLowerInvariant() }; break;
            case "scope": h.Entries[0] = h.Entries[0] with { Scope = UninstallScope.Machine32 }; break;
            case "command": h.Entries[0] = h.Entries[0] with { UninstallFingerprint = "changed" }; break;
            case "source": h.SourceArgument = "https://other.example/cache"; break;
            case "native-list":
                // WinGet may know a catalog match for this name. Only the exact local record can authorize removal.
                h.ListOverride = (id, _) => id == "Contoso.Unrelated" ? 0 : WingetClient.NoApplicationsFound;
                break;
            case "native-list-error": h.ListOverride = (_, _) => WingetClient.InternalError; break;
            case "proof": h.OnlyProof = false; break;
            case "after-save": h.BeforeTransportGuard = () => h.Entries[0] = h.Entries[0] with { UninstallFingerprint = "changed" }; break;
        }

        var result = await h.Action.ExecuteAsync(plan, Keys(PhotoId), null, default);

        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.NotEmpty(result.Errors);
        Assert.False(result.UnknownOutcome);
        Assert.Empty(h.Dispatches);
        if (change is "proof" or "after-save")
            Assert.Equal(ProgramRemovalAuditOutcome.NotStarted, Assert.Single(h.Journal.ProgramRemovals).Outcome);
        else Assert.Empty(h.Journal.ProgramRemovals);
    }

    [Fact]
    public async Task Journal_write_failure_refuses_attempt_without_launching()
    {
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "not a directory");
        var h = Create(Path.Combine(blocked, "journal.json"));
        var plan = await h.Action.PrepareAsync(default);

        var result = await h.Action.ExecuteAsync(plan, Keys(PhotoId), null, default);

        Assert.Equal(0, result.Applied);
        Assert.NotEmpty(result.Errors);
        Assert.False(result.UnknownOutcome);
        Assert.Empty(h.Dispatches);
        Assert.Empty(h.Journal.ProgramRemovals);
    }

    [Fact]
    public async Task Successful_process_and_failed_final_save_leaves_attempted_and_stops_queue()
    {
        var h = Create();
        h.Add(Editor, UninstallScope.DesktopUser);
        var plan = await h.Action.PrepareAsync(default);
        FileStream? held = null;
        h.DispatchOverride = id =>
        {
            held = File.Open(h.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            h.Remove(id);
            return Done(0);
        };

        RemovalBatchResult result;
        try { result = await h.Action.ExecuteAsync(plan, Keys(PhotoId, EditorId), null, default); }
        finally { held?.Dispose(); }

        Assert.True(result.UnknownOutcome);
        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.Single(h.Dispatches);
        Assert.Equal(ProgramRemovalAuditOutcome.Attempted, Assert.Single(ChangeJournal.ForTests(h.Path).ProgramRemovals).Outcome);
    }

    [Theory]
    [InlineData("untracked")]
    [InlineData("running")]
    [InlineData("cancelled")]
    [InlineData("registered")]
    [InlineData("partial-scan")]
    [InlineData("list-error")]
    public async Task Unknown_after_first_success_stops_queue_and_cannot_replay_plan(string change)
    {
        var h = Create();
        h.Add(Editor, UninstallScope.DesktopUser);
        h.Add(Notes, UninstallScope.Machine32);
        var plan = await h.Action.PrepareAsync(default);
        using var cancel = new CancellationTokenSource();
        h.DispatchOverride = id =>
        {
            if (id == PhotoId) { h.Remove(id); return Done(0); }
            Assert.Equal(EditorId, id);
            switch (change)
            {
                case "untracked": return new(UnelevatedOutcome.Untracked, -1, "");
                case "running": return new(UnelevatedOutcome.Completed, 0, "") { StillRunning = true };
                case "cancelled": cancel.Cancel(); break;
                case "registered": return Done(0);
                case "partial-scan": h.State = UninstallReadState.Partial; break;
                case "list-error": h.ListOverride = (_, _) => WingetClient.InternalError; break;
            }
            h.Remove(id);
            return Done(0);
        };

        var result = await h.Action.ExecuteAsync(plan, Keys(PhotoId, EditorId, NotesId), null, cancel.Token);

        Assert.True(result.UnknownOutcome);
        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(2, h.Dispatches.Count);
        Assert.Equal(new[] { ProgramRemovalAuditOutcome.Removed, ProgramRemovalAuditOutcome.Attempted },
            ChangeJournal.ForTests(h.Path).ProgramRemovals.Select(audit => audit.Outcome));
        await h.Action.ExecuteAsync(plan, Keys(NotesId), null, default);
        Assert.Equal(2, h.Dispatches.Count);
    }

    private Harness Create(string? path = null)
    {
        var h = new Harness(path ?? Path.Combine(_dir, "journal.json"));
        h.Add(Photo);
        return h;
    }

    private static RemovalRow PackagedRow(string name, RemovalDecision decision) =>
        new($"msix:{name}", name, "Contoso", "1.0", "Store", decision, "Package evidence");
    private static HashSet<string> Keys(params string[] values) => new(values, StringComparer.Ordinal);
    private static UnelevatedResult Done(int exit, string output = "") => new(UnelevatedOutcome.Completed, exit, output);

    private sealed record TestProof(string Fingerprint) : ProgramRemovalProof;

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness : IProgramRemovalInspector
    {
        public Harness(string path)
        {
            Path = path;
            Journal = ChangeJournal.ForTests(path);
            Action = new ProgramRemovalBatchAction(() => new UninstallInventory(Entries.ToArray(), State, Clock.Now.UtcDateTime),
                () => Sid, () => Elevated, this, RunAsync, Clock, Journal, (_, _) => Task.FromResult(Packaged));
        }

        public string Path { get; }
        public ChangeJournal Journal { get; }
        public ProgramRemovalBatchAction Action { get; }
        public ManualClock Clock { get; } = new();
        public string? Sid { get; set; } = "S-1-5-21-100";
        public bool Elevated { get; set; } = true;
        public bool OnlyProof { get; set; } = true;
        public int InspectionCount { get; private set; }
        public int RevalidationCount { get; private set; }
        public UninstallReadState State { get; set; } = UninstallReadState.Complete;
        public List<UninstallRegistration> Entries { get; } = [];
        public Dictionary<string, (RemovalDecision Decision, bool Proof)> Decisions { get; } = new(StringComparer.Ordinal);
        public List<string[]> Calls { get; } = [];
        public List<string[]> Dispatches { get; } = [];
        public string SourceArgument { get; set; } = OfficialSource;
        public string ClientVersion { get; set; } = "v1.29.380";
        public PackagedRemovalSnapshot Packaged { get; set; } = new([], true);
        public Action? OnSource { get; set; }
        public Func<string, string, int>? ListOverride { get; set; }
        public Func<string, UnelevatedResult>? DispatchOverride { get; set; }
        public Action? BeforeTransportGuard { get; set; }

        public void Add(string id, UninstallScope scope = UninstallScope.Machine64) =>
            Entries.Add(new(scope, id, id, "Contoso", "25.01", false, false, false, true, $"fingerprint:{id}"));

        public void Remove(string id) => Entries.RemoveAll(entry => ToNative(entry)?.Id == id);

        public Task<IReadOnlyList<ProgramRemovalAssessment>> InspectAsync(UninstallInventory inventory, CancellationToken ct)
        {
            InspectionCount++;
            return Task.FromResult<IReadOnlyList<ProgramRemovalAssessment>>(inventory.Entries.Select(entry =>
            {
                var (decision, proof) = Decisions.GetValueOrDefault(entry.KeyName, (RemovalDecision.ConsiderRemoval, true));
                var row = new RemovalRow(entry.KeyName, entry.DisplayName, entry.Publisher, entry.Version, entry.Scope.ToString(), decision, "Test evidence");
                return new ProgramRemovalAssessment(entry, row, proof ? new TestProof(entry.UninstallFingerprint!) : null);
            }).ToArray());
        }

        public bool Revalidate(UninstallRegistration registration, ProgramRemovalProof proof, CancellationToken ct)
        {
            RevalidationCount++;
            return OnlyProof && proof is TestProof expected && expected.Fingerprint == registration.UninstallFingerprint;
        }

        private Task<UnelevatedResult> RunAsync(string sid, IReadOnlyList<string> args, Func<bool>? validate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.True(UnelevatedWinget.IsStrictRemovalCommand(args));
            Calls.Add(args.ToArray());
            if (args[0] == "source") OnSource?.Invoke();
            var result = args[0] switch
            {
                "--version" => Done(0, ClientVersion),
                "source" => Done(0, JsonSerializer.Serialize(new
                    { Name = "winget", Identifier = SourceIdentifier, Arg = SourceArgument, Type = SourceType })),
                "list" => Done(ListOverride?.Invoke(args[2], args[7]) ??
                    (Entries.Select(ToNative).Any(entry => entry is not null && entry.Id == args[2] && Scope(entry.Scope) == args[7])
                        ? 0 : WingetClient.NoApplicationsFound)),
                "uninstall" => Dispatch(args, validate),
                _ => throw new InvalidOperationException(),
            };
            return Task.FromResult(result);
        }

        private static NativeArpIdentity? ToNative(UninstallRegistration entry) =>
            NativeArpIdentity.TryCreate(entry, Architecture.X64);
        private static string Scope(ProgramRemovalScope scope) => scope == ProgramRemovalScope.User ? "user" : "machine";

        private UnelevatedResult Dispatch(IReadOnlyList<string> args, Func<bool>? validate)
        {
            BeforeTransportGuard?.Invoke();
            if (validate?.Invoke() != true) return new(UnelevatedOutcome.LaunchFailed, -1, "");
            Dispatches.Add(args.ToArray());
            if (DispatchOverride is { } run) return run(args[2]);
            Remove(args[2]);
            return Done(0);
        }
    }
}
