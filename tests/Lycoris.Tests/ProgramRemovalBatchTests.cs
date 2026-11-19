using System.IO;
using System.Text.Json;
using Lycoris.Core;
using Lycoris.Modules.Apps;
using Lycoris.Modules.Games.Sources;
using Lycoris.Modules.Removal;

namespace Lycoris.Tests;

[Collection("Registry")]
public sealed class ProgramRemovalBatchTests : IDisposable
{
    private const string Vlc = "VideoLAN.VLC";
    private const string Obs = "OBSProject.OBSStudio";
    private const string SevenZip = "7zip.7zip";
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
    public async Task Preview_requires_allow_list_and_strictly_unique_source_and_registration()
    {
        var h = Create();
        h.Add(Obs, "OBS Studio", "OBS Project", UninstallScope.DesktopUser);
        h.Add(SevenZip, "7-Zip 25.01 (x64)", "Igor Pavlov");
        h.Add("Valve.Steam", "Steam", "Valve Corporation");
        h.Add("EpicGames.EpicGamesLauncher", "Epic Games Launcher", "Epic Games, Inc.");
        h.Add("Test.Runtime", "Microsoft Visual C++ Runtime", "Microsoft Corporation");
        h.Add("Test.Driver", "Device Driver", "Contoso");
        h.ExtraExports.Add((SevenZip, "25.01", "machine"));
        h.Entries.Add(h.Entries[0] with { Scope = UninstallScope.Machine32, Publisher = "Another publisher", Version = "different" });

        var plan = await h.Action.PrepareAsync(default);

        Assert.Equal(Obs, Assert.Single(plan.Items).Key);
        Assert.Empty(h.Dispatches);
        Assert.Empty(h.Journal.ProgramRemovals);
    }

    [Fact]
    public async Task Preview_refuses_same_product_code_in_other_machine_view_even_with_a_different_display_name()
    {
        var h = Create();
        h.Entries.Add(h.Entries[0] with { Scope = UninstallScope.Machine32, DisplayName = "Unrelated product" });

        Assert.Empty((await h.Action.PrepareAsync(default)).Items);
    }

    [Fact]
    public void Official_source_uses_live_command_shape_and_export_scope_is_optional()
    {
        const string liveSource = """
            {"Arg":"https://cdn.winget.microsoft.com/cache","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","Explicit":false,"Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Name":"winget","TrustLevel":["Trusted","StoreOrigin"],"Type":"Microsoft.PreIndexed.Package"}
            """;
        const string installedExport = """
            {"Sources":[{"SourceDetails":{"Argument":"https://cdn.winget.microsoft.com/cache","Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Name":"winget","Type":"Microsoft.PreIndexed.Package"},"Packages":[{"PackageIdentifier":"VideoLAN.VLC","Version":"3.0.21"}]}]}
            """;

        Assert.True(ProgramRemovalExport.IsOfficialSource(liveSource));
        Assert.False(ProgramRemovalExport.IsOfficialSource(liveSource.Replace("\"Arg\":", "\"Argument\":")));
        var export = Assert.IsType<ProgramRemovalExport>(ProgramRemovalExport.Parse(installedExport));
        var match = Assert.IsType<ProgramRemovalPackage>(export.Match(Vlc, "3.0.21", ProgramRemovalScope.Machine));
        Assert.Null(match.ExportedScope);
        Assert.Null(export.Match(Vlc, "3.0.22", ProgramRemovalScope.Machine));
    }

    [Theory]
    [InlineData("VLC media player", true)]
    [InlineData("7-Zip", true)]
    [InlineData("{12345678-1234-1234-1234-123456789ABC}", true)]
    [InlineData("--force", false)]
    [InlineData("VLC&echo", false)]
    [InlineData("%TEMP%", false)]
    [InlineData("empty\"argument", false)]
    public void Only_bounded_product_codes_can_enter_the_strict_command(string code, bool expected) =>
        Assert.Equal(expected, UnelevatedWinget.IsSafeProductCode(code));

    [Fact]
    public async Task Preview_fails_closed_for_partial_inventory_wrong_source_and_inconsistent_scope()
    {
        var h = Create();
        h.State = UninstallReadState.Partial;
        Assert.NotNull((await h.Action.PrepareAsync(default)).UnavailableReason);
        h.State = UninstallReadState.Complete;
        h.SourceArgument = "https://other.example/cache";
        Assert.NotNull((await h.Action.PrepareAsync(default)).UnavailableReason);
        h.SourceArgument = OfficialSource;
        h.ListOverride = (_, _) => 0; // Same exact ID in both scopes.
        Assert.Empty((await h.Action.PrepareAsync(default)).Items);
        h.ListOverride = (_, scope) => scope == "machine" ? 0 : WingetClient.InternalError;
        Assert.Empty((await h.Action.PrepareAsync(default)).Items);
    }

    [Fact]
    public async Task Uninstall_only_dispatches_selected_subset_after_durable_audit_and_never_creates_restore_records()
    {
        var h = Create();
        h.Add(Obs, "OBS Studio", "OBS Project", UninstallScope.DesktopUser);
        var plan = await h.Action.PrepareAsync(default);
        h.DispatchOverride = id =>
        {
            Assert.Equal(Obs, id);
            var before = ChangeJournal.ForTests(h.Path);
            var audit = Assert.Single(before.ProgramRemovals);
            Assert.Equal(plan.Id, audit.BatchId);
            Assert.Equal(ProgramRemovalAuditOutcome.Attempted, audit.Outcome);
            Assert.Equal(ProgramRemovalScope.User, audit.Scope);
            Assert.Null(audit.ExitCode);
            Assert.Empty(before.Records);
            Assert.Empty(before.Batches);
            h.Remove(id);
            return Done(0);
        };

        var result = await h.Action.ExecuteAsync(plan, Keys(Obs), null, default);

        Assert.Equal(1, result.Applied);
        Assert.Equal(0, result.Skipped);
        var args = Assert.Single(h.Dispatches);
        Assert.Equal(new[] { "uninstall", "--id", Obs, "--exact", "--source", "winget", "--scope", "user",
            "--disable-interactivity", "--version", "25.01", "--product-code", Obs, "--silent" }, args);
        var saved = ChangeJournal.ForTests(h.Path);
        var final = Assert.Single(saved.ProgramRemovals);
        Assert.Equal(ProgramRemovalAuditOutcome.Removed, final.Outcome);
        Assert.True(final.FreshVerified);
        Assert.Empty(saved.Records);
        Assert.Empty(saved.Batches);
        Assert.Equal("VLC media player", Assert.Single(h.Entries).DisplayName);
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
        var keys = Keys(Vlc);
        using var cancel = new CancellationTokenSource();
        switch (change)
        {
            case "stale": h.Clock.Now += TimeSpan.FromMinutes(6); break;
            case "sid": h.Sid = "S-1-5-21-200"; break;
            case "elevation": h.Elevated = false; break;
            case "unknown-selection": keys.Add(Obs); break;
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
    [InlineData("uninstall-command")]
    [InlineData("source")]
    [InlineData("scope")]
    [InlineData("after-save")]
    public async Task Changed_identity_source_scope_or_last_guard_refuses_dispatch(string change)
    {
        var h = Create();
        var plan = await h.Action.PrepareAsync(default);
        switch (change)
        {
            case "version": h.Entries[0] = h.Entries[0] with { Version = "26.0" }; break;
            case "duplicate": h.Entries.Add(h.Entries[0] with { Scope = UninstallScope.DesktopUser, KeyName = "second" }); break;
            case "uninstall-command": h.Entries[0] = h.Entries[0] with { UninstallFingerprint = "new-uninstall-command-fingerprint" }; break;
            case "source": h.SourceArgument = "https://other.example/cache"; break;
            case "scope": h.ListOverride = (_, scope) => scope == "user" ? 0 : WingetClient.NoApplicationsFound; break;
            case "after-save": h.BeforeTransportGuard = () => h.Entries[0] = h.Entries[0] with { Version = "changed" }; break;
        }

        var result = await h.Action.ExecuteAsync(plan, Keys(Vlc), null, default);

        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.NotEmpty(result.Errors);
        Assert.False(result.UnknownOutcome);
        Assert.Empty(h.Dispatches);
        if (change == "after-save")
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

        var result = await h.Action.ExecuteAsync(plan, Keys(Vlc), null, default);

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
        h.Add(Obs, "OBS Studio", "OBS Project");
        var plan = await h.Action.PrepareAsync(default);
        FileStream? held = null;
        h.DispatchOverride = id =>
        {
            held = File.Open(h.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            h.Remove(id);
            return Done(0);
        };

        RemovalBatchResult result;
        try { result = await h.Action.ExecuteAsync(plan, Keys(Vlc, Obs), null, default); }
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
        h.Add(Obs, "OBS Studio", "OBS Project");
        h.Add(SevenZip, "7-Zip 25.01 (x64)", "Igor Pavlov");
        var plan = await h.Action.PrepareAsync(default);
        using var cancel = new CancellationTokenSource();
        h.DispatchOverride = id =>
        {
            if (id == Vlc) { h.Remove(id); return Done(0); }
            Assert.Equal(Obs, id);
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

        var result = await h.Action.ExecuteAsync(plan, Keys(Vlc, Obs, SevenZip), null, cancel.Token);

        Assert.True(result.UnknownOutcome);
        Assert.Equal(1, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(2, h.Dispatches.Count);
        Assert.Equal(new[] { ProgramRemovalAuditOutcome.Removed, ProgramRemovalAuditOutcome.Attempted },
            ChangeJournal.ForTests(h.Path).ProgramRemovals.Select(audit => audit.Outcome));
        await h.Action.ExecuteAsync(plan, Keys(SevenZip), null, default);
        Assert.Equal(2, h.Dispatches.Count);
    }

    private Harness Create(string? path = null)
    {
        var h = new Harness(path ?? Path.Combine(_dir, "journal.json"));
        h.Add(Vlc, "VLC media player", "VideoLAN");
        return h;
    }

    private static HashSet<string> Keys(params string[] values) => new(values, StringComparer.Ordinal);
    private static UnelevatedResult Done(int exit, string output = "") => new(UnelevatedOutcome.Completed, exit, output);

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness
    {
        public Harness(string path)
        {
            Path = path;
            Journal = ChangeJournal.ForTests(path);
            Action = new ProgramRemovalBatchAction(() => new UninstallInventory(Entries.ToArray(), State, Clock.Now.UtcDateTime),
                () => Sid, () => Elevated, RunAsync, Clock, Journal);
        }

        public string Path { get; }
        public ChangeJournal Journal { get; }
        public ProgramRemovalBatchAction Action { get; }
        public ManualClock Clock { get; } = new();
        public string? Sid { get; set; } = "S-1-5-21-100";
        public bool Elevated { get; set; } = true;
        public UninstallReadState State { get; set; } = UninstallReadState.Complete;
        public List<UninstallRegistration> Entries { get; } = [];
        public List<(string Id, string Version, string Scope)> ExtraExports { get; } = [];
        public List<string[]> Dispatches { get; } = [];
        public string SourceArgument { get; set; } = OfficialSource;
        public Func<string, string, int>? ListOverride { get; set; }
        public Func<string, UnelevatedResult>? DispatchOverride { get; set; }
        public Action? BeforeTransportGuard { get; set; }

        public void Add(string id, string name, string publisher, UninstallScope scope = UninstallScope.Machine64) =>
            Entries.Add(new(scope, id, name, publisher, "25.01", false, false, false, true, "uninstall-command-fingerprint"));

        public void Remove(string id) => Entries.RemoveAll(entry => entry.KeyName == id);

        private Task<UnelevatedResult> RunAsync(string sid, IReadOnlyList<string> args, Func<bool>? validate, CancellationToken ct)
        {
            var result = args[0] switch
            {
                "source" => Done(0, JsonSerializer.Serialize(new
                    { Name = "winget", Identifier = SourceIdentifier, Arg = SourceArgument, Type = SourceType })),
                "export" => Done(0, Export()),
                "list" => Done(ListOverride?.Invoke(args[2], args[7]) ??
                    (Entries.Any(entry => entry.KeyName == args[2] && Scope(entry) == args[7]) ? 0 : WingetClient.NoApplicationsFound)),
                "uninstall" => Dispatch(args, validate),
                _ => throw new InvalidOperationException(),
            };
            return Task.FromResult(result);
        }

        private string Export() => JsonSerializer.Serialize(new
        {
            Sources = new[]
            {
                new
                {
                    SourceDetails = new { Name = "winget", Identifier = SourceIdentifier, Argument = SourceArgument, Type = SourceType },
                    Packages = Entries.Select(entry => (Id: entry.KeyName, Version: entry.Version!, Scope: Scope(entry)))
                        .Concat(ExtraExports).Select(item => new { PackageIdentifier = item.Id, Version = item.Version, Scope = item.Scope }),
                },
            },
        });

        private static string Scope(UninstallRegistration entry) => entry.Scope == UninstallScope.DesktopUser ? "user" : "machine";

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
