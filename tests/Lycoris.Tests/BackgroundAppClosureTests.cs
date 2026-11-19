using System.IO;
using System.Text.Json;
using Lycoris.Modules.Games;

namespace Lycoris.Tests;

public sealed class BackgroundAppClosureTests
{
    private const string Owner = "S-1-5-21-5-6-7-1001";
    private const string OtherOwner = "S-1-5-21-5-6-7-1002";
    private static readonly DateTime Start = new(2026, 11, 18, 16, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("")]
    [InlineData("../demo.exe")]
    [InlineData("C:\\Apps\\demo.exe")]
    [InlineData("demo,DEMO.exe")]
    [InlineData("demo.exe.exe")]
    [InlineData("-demo")]
    [InlineData("demo ")]
    [InlineData("demo .exe")]
    [InlineData("demo,")]
    [InlineData("demo, demo2")]
    [InlineData("demo:alternate")]
    public void Cli_rejects_invalid_and_ambiguous_basenames(string input) =>
        Assert.False(BackgroundAppCloseCommandLine.TryParseNames(input, out _));

    [Fact]
    public void Cli_parser_bounds_name_length_and_count_and_supports_unicode_and_spaces()
    {
        Assert.True(BackgroundAppCloseCommandLine.TryParseNames("Фоновое приложение.exe,good-app_2", out var names));
        Assert.Equal(["Фоновое приложение", "good-app_2"], names);
        Assert.True(BackgroundAppCloseCommandLine.TryParseNames(new string('a', 96), out _));
        Assert.False(BackgroundAppCloseCommandLine.TryParseNames(new string('a', 97), out _));
        Assert.True(BackgroundAppCloseCommandLine.TryParseNames(string.Join(",", Enumerable.Range(0, 32)), out _));
        Assert.False(BackgroundAppCloseCommandLine.TryParseNames(string.Join(",", Enumerable.Range(0, 33)), out _));
    }

    [Fact]
    public void Cli_mixed_or_repeated_switches_and_invalid_plan_cannot_reach_apply()
    {
        string[][] malformed =
        [
            ["--close-background-apps", "demo", "--force", "--force"],
            ["--close-background-apps", "demo", "--yes"],
            ["--close-background-apps", "demo", "--plan", new string('a', 64)],
            ["--close-background-apps", "demo", "--yes", "--plan", new string('g', 64)],
            ["--close-background-apps", "demo", "--lang", "auto"],
            ["--apply-update", @"C:\anything.exe", "1", "--close-background-apps", "demo"],
            ["--selftest", @"C:\reports", "--CLOSE-BACKGROUND-APPS", "demo"],
            ["--close-background-apps", "demo", "--apply-profile", "gaming"],
        ];
        foreach (var args in malformed)
        {
            Assert.True(BackgroundAppCloseCommandLine.IsRequested(args));
            Assert.Null(BackgroundAppCloseCommandLine.Parse(args));
        }
    }

    [Fact]
    public async Task Cli_without_confirmation_admin_or_console_never_sends_close()
    {
        var platform = new FakePlatform();
        var process = platform.Add(50);
        var engine = CreateEngine(platform);

        var (code, report) = await ExecuteAsync(engine, ["--close-background-apps", "demo", "--force"]);
        Assert.Equal(0, code);
        Assert.Equal("preview", report.GetProperty("mode").GetString());
        Assert.Equal("preview", report.GetProperty("status").GetString());
        Assert.False(report.ToString().Contains(Owner, StringComparison.Ordinal));
        var token = report.GetProperty("plan").GetString()!;

        (code, report) = await ExecuteAsync(engine, ["--close-background-apps", "demo", "--dry-run", "--force", "--yes"]);
        Assert.Equal(0, code);
        Assert.Equal(token, report.GetProperty("plan").GetString());
        Assert.Equal("preview", report.GetProperty("status").GetString());

        (code, report) = await ExecuteAsync(engine, Apply(token, force: true), elevated: false);
        Assert.Equal(BackgroundAppCloseCommandLine.Exit.NeedsAdmin, code);
        Assert.Equal("needsAdmin", report.GetProperty("status").GetString());
        Assert.Equal(BackgroundAppCloseCommandLine.Exit.NoConsole,
            await BackgroundAppCloseCommandLine.ExecuteAsync(Apply(token, force: true), true, engine, output: null));
        Assert.Equal(0, process.CloseRequests);
        Assert.Equal(0, process.TerminateRequests);
    }

    [Fact]
    public async Task Cli_binding_includes_policy_force_full_selection_and_current_identity()
    {
        var platform = new FakePlatform();
        var original = platform.Add(50);
        var engine = CreateEngine(platform);
        var baseToken = engine.Capture(["demo"]).Fingerprint;
        Assert.Equal(baseToken, engine.Capture(["DEMO.exe"]).Fingerprint);
        platform.Context = platform.Context with { SelfPid = 4000 };
        Assert.Equal(baseToken, engine.Capture(["demo"]).Fingerprint);
        var (code, report) = await ExecuteAsync(engine, Apply(baseToken, force: true));
        Assert.Equal(BackgroundAppCloseCommandLine.Exit.StalePlan, code);
        Assert.Equal("stalePlan", report.GetProperty("status").GetString());

        platform.Add(51);
        (code, report) = await ExecuteAsync(engine, Apply(baseToken));
        Assert.Equal(BackgroundAppCloseCommandLine.Exit.StalePlan, code);
        Assert.Equal(2, report.GetProperty("targets").GetArrayLength());

        platform.Processes.Remove(51);
        var replacement = platform.Add(50, started: Start.AddSeconds(1));
        (code, report) = await ExecuteAsync(engine, Apply(baseToken));
        Assert.Equal(BackgroundAppCloseCommandLine.Exit.StalePlan, code);
        Assert.Equal(0, original.CloseRequests);
        Assert.Equal(0, replacement.CloseRequests);
    }

    [Fact]
    public async Task Cli_preview_explains_unavailable_selection_and_apply_closes_nothing()
    {
        var platform = new FakePlatform();
        var normal = platform.Add(50);
        platform.Add(51, owner: OtherOwner);
        var engine = CreateEngine(platform);
        string[] selection = ["--close-background-apps", "demo,codex"];
        var (code, report) = await ExecuteAsync(engine, selection);
        Assert.Equal(0, code);
        Assert.Single(report.GetProperty("targets").EnumerateArray());
        Assert.Collection(report.GetProperty("unavailable").EnumerateArray(),
            item => Assert.Equal("codex", item.GetProperty("name").GetString()),
            item => Assert.Equal(51, item.GetProperty("pid").GetInt32()));
        Assert.False(report.ToString().Contains(OtherOwner, StringComparison.Ordinal));

        (code, report) = await ExecuteAsync(engine, [.. selection, "--yes", "--plan", report.GetProperty("plan").GetString()!]);
        Assert.Equal(BackgroundAppCloseCommandLine.Exit.Denied, code);
        Assert.Equal("denied", report.GetProperty("status").GetString());
        Assert.Equal(0, normal.CloseRequests);
    }

    [Fact]
    public async Task Cli_exact_apply_without_force_sends_close_only_and_reports_survivors()
    {
        var platform = new FakePlatform();
        var process = platform.Add(50);
        var engine = CreateEngine(platform);

        var (code, report) = await ExecuteAsync(engine, Apply(engine.Capture(["demo"]).Fingerprint));
        Assert.Equal(BackgroundAppCloseCommandLine.Exit.Partial, code);
        Assert.Equal("apply", report.GetProperty("mode").GetString());
        Assert.Equal("stillRunning", report.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(1, process.CloseRequests);
        Assert.Equal(0, process.TerminateRequests);
    }

    [Fact]
    public async Task Cli_force_requires_its_own_matching_preview_and_reports_confirmed_exit()
    {
        var platform = new FakePlatform();
        var process = platform.Add(50);
        var engine = CreateEngine(platform);

        var (code, report) = await ExecuteAsync(engine, Apply(engine.Capture(["demo"], force: true).Fingerprint, force: true));
        Assert.Equal(0, code);
        Assert.Equal("forced", report.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(1, process.CloseRequests);
        Assert.Equal(1, process.TerminateRequests);
        Assert.True(process.Exited);
    }

    [Theory]
    [InlineData("wrong owner", "denied")]
    [InlineData("wrong session", "denied")]
    [InlineData("critical", "denied")]
    [InlineData("unknown identity", "error")]
    [InlineData("unknown path", "denied")]
    [InlineData("different path", "stale")]
    [InlineData("reused pid", "stale")]
    public async Task Apply_revalidates_identity_and_never_acts_on_unsafe_target(string change, string expected)
    {
        var platform = new FakePlatform();
        var process = platform.Add(50);
        var engine = CreateEngine(platform);
        var plan = engine.Capture(["demo"]);
        switch (change)
        {
            case "wrong owner": process.Identity = process.Identity! with { OwnerSid = OtherOwner }; break;
            case "wrong session": process.Identity = process.Identity! with { SessionId = 2 }; break;
            case "critical": process.Identity = process.Identity! with { IsCritical = true }; break;
            case "unknown identity": process.Identity = null; break;
            case "unknown path": process.Identity = process.Identity! with { ImagePath = "" }; break;
            case "different path": process.Identity = process.Identity! with { ImagePath = @"D:\Elsewhere\demo.exe" }; break;
            case "reused pid": process = platform.Add(50, started: Start.AddSeconds(1)); break;
        }

        var result = Assert.Single((await engine.CloseAsync(plan, _ => Task.FromResult(true))).Results);
        Assert.Equal(expected, result.Status.ToString().ToLowerInvariant());
        Assert.Equal(0, process.CloseRequests);
        Assert.Equal(0, process.TerminateRequests);
    }

    [Theory]
    [InlineData("codex", @"C:\Apps\codex.exe")]
    [InlineData("claude", @"C:\Apps\claude.exe")]
    [InlineData("demo", @"C:\Program Files\WindowsApps\OpenAI.Codex_1.2\demo.exe")]
    [InlineData("demo", @"C:\Apps\.claude\demo.exe")]
    [InlineData("demo", @"C:\Users\Lycoris\.codex\demo.exe")]
    [InlineData("demo", @"C:\Windows\System32\demo.exe")]
    [InlineData("demo", @"C:\Program Files\NVIDIA Corporation\demo.exe")]
    [InlineData("steam", @"C:\Apps\steam.exe")]
    [InlineData("pwsh", @"C:\Apps\pwsh.exe")]
    [InlineData("python", @"C:\Apps\python.exe")]
    [InlineData("copilot", @"C:\Apps\copilot.exe")]
    [InlineData("github-copilot-helper", @"C:\Apps\github-copilot-helper.exe")]
    public void Capture_refuses_protected_names_and_paths(string name, string path)
    {
        var platform = new FakePlatform();
        platform.Add(50, name: name, path: path);
        var plan = CreateEngine(platform).Capture([name]);
        Assert.Empty(plan.Targets);
        Assert.Equal(BackgroundCloseStatus.Denied, Assert.Single(plan.Unavailable).Status);
    }

    [Theory]
    [InlineData("CopilotApp", @"C:\Program Files (x86)\Microsoft\Copilot\Application\CopilotApp.exe")]
    [InlineData("CopilotAppHost", @"C:\Program Files (x86)\Microsoft\Copilot\Application\1.2.3\CopilotAppHost.exe")]
    public void Optional_desktop_copilot_is_eligible_unless_the_same_handle_reports_critical(string name, string path)
    {
        var platform = new FakePlatform();
        var process = platform.Add(50, name: name, path: path);
        var engine = CreateEngine(platform);
        var target = Assert.Single(engine.Capture([name]).Targets);
        Assert.Equal(path, target.ImagePath);

        process.Identity = process.Identity! with { IsCritical = true };
        var protectedPlan = engine.Capture([name]);
        Assert.Empty(protectedPlan.Targets);
        Assert.Equal(BackgroundCloseStatus.Denied, Assert.Single(protectedPlan.Unavailable).Status);
    }

    [Fact]
    public async Task Apply_prevalidates_all_targets_and_confirms_exit_on_the_original_handles()
    {
        var platform = new FakePlatform();
        var first = platform.Add(50);
        var second = platform.Add(51);
        var engine = CreateEngine(platform);
        var plan = engine.Capture(["demo"]);
        second.CloseResponse = BackgroundCloseRequest.Denied;
        FakeProcess? newInstance = null;

        first.OnClose = () =>
        {
            Assert.Contains((51, false), platform.Opens);
            newInstance = platform.Add(52, started: Start.AddSeconds(1));
            first.Exited = true;
        };
        platform.Opens.Clear();

        var outcome = await engine.CloseAsync(plan, confirmForce: null);
        Assert.False(outcome.PreflightAborted);
        Assert.True(outcome.SelectionVerified);
        var results = outcome.Results;
        Assert.Collection(results,
            result => { Assert.Equal(50, result.Target.Pid); Assert.Equal(BackgroundCloseStatus.Closed, result.Status); },
            result => { Assert.Equal(51, result.Target.Pid); Assert.Equal(BackgroundCloseStatus.Denied, result.Status); },
            result => { Assert.Equal(52, result.Target.Pid); Assert.Equal(BackgroundCloseStatus.Stale, result.Status); });
        Assert.Equal(0, newInstance!.CloseRequests);
        Assert.Equal(0, newInstance.TerminateRequests);
    }

    [Fact]
    public void Eligible_process_in_user_profile_is_not_confused_with_the_lycoris_program()
    {
        var platform = new FakePlatform();
        const string path = @"C:\Users\Lycoris\AppData\Local\Programs\SomeOptionalApp\demo.exe";
        platform.Add(50, path: path);
        var target = Assert.Single(CreateEngine(platform).Capture(["demo"]).Targets);
        Assert.Equal(path, target.ImagePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preflight_aborts_every_action_if_any_approved_process_changed(bool reusedPid)
    {
        var platform = new FakePlatform();
        var first = platform.Add(50);
        var second = platform.Add(51);
        var engine = CreateEngine(platform);
        var plan = engine.Capture(["demo"]);
        if (reusedPid) second = platform.Add(51, started: Start.AddSeconds(1));
        else second.OpenFailure = BackgroundCloseStatus.Denied;
        var askedForForce = false;

        var result = await engine.CloseAsync(plan, _ =>
        {
            askedForForce = true;
            return Task.FromResult(true);
        });
        Assert.True(result.PreflightAborted);
        Assert.False(result.SelectionVerified);
        Assert.Equal(0, first.CloseRequests);
        Assert.Equal(0, second.CloseRequests);
        Assert.False(askedForForce);
    }

    [Fact]
    public async Task Preflight_reports_added_matching_process_without_acting_on_any_target()
    {
        var platform = new FakePlatform();
        var original = platform.Add(50);
        var engine = CreateEngine(platform);
        var plan = engine.Capture(["demo"]);
        var added = platform.Add(51);
        var result = await engine.CloseAsync(plan, confirmForce: null);

        Assert.True(result.PreflightAborted);
        Assert.Collection(result.Results,
            item => { Assert.Equal(50, item.Target.Pid); Assert.Equal(BackgroundCloseStatus.StillRunning, item.Status); },
            item => { Assert.Equal(51, item.Target.Pid); Assert.Equal(BackgroundCloseStatus.Stale, item.Status); });
        Assert.Equal(0, original.CloseRequests);
        Assert.Equal(0, added.CloseRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_final_selection_failure_is_partial_even_when_the_exact_target_exit_is_confirmed(bool accessDenied)
    {
        var platform = new FakePlatform();
        var original = platform.Add(50);
        var engine = CreateEngine(platform);
        original.OnClose = () =>
        {
            original.Exited = true;
            if (accessDenied) platform.Add(51, owner: OtherOwner);
            else platform.CanEnumerate = false;
        };

        var (code, report) = await ExecuteAsync(engine, Apply(engine.Capture(["demo"]).Fingerprint));
        Assert.Equal(BackgroundAppCloseCommandLine.Exit.Partial, code);
        Assert.Equal("partial", report.GetProperty("status").GetString());
        Assert.Equal("closed", report.GetProperty("results")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Force_reopens_identity_and_will_not_close_a_replacement()
    {
        var platform = new FakePlatform();
        var process = platform.Add(50);
        var engine = CreateEngine(platform);
        var plan = engine.Capture(["demo"]);
        FakeProcess? replacement = null;

        var result = Assert.Single((await engine.CloseAsync(plan, remaining =>
        {
            Assert.Equal(50, Assert.Single(remaining).Pid);
            replacement = platform.Add(50, started: Start.AddSeconds(1));
            return Task.FromResult(true);
        })).Results);
        Assert.Equal(BackgroundCloseStatus.Stale, result.Status);
        Assert.Equal(0, process.TerminateRequests);
        Assert.Equal(0, replacement!.TerminateRequests);
    }

    [Theory]
    [InlineData(false, false, false, BackgroundCloseStatus.StillRunning)]
    [InlineData(true, true, false, BackgroundCloseStatus.Denied)]
    [InlineData(true, false, false, BackgroundCloseStatus.StillRunning)]
    [InlineData(true, false, true, BackgroundCloseStatus.Forced)]
    public async Task Force_requires_consent_and_unlocked_operation_and_only_reports_confirmed_exit(
        bool confirm, bool lockOnConfirmation, bool exits, BackgroundCloseStatus status)
    {
        var platform = new FakePlatform();
        var process = platform.Add(50);
        process.ExitOnTerminate = exits;
        var engine = CreateEngine(platform);
        var result = Assert.Single((await engine.CloseAsync(engine.Capture(["demo"]), _ =>
        {
            if (lockOnConfirmation) platform.CanMutate = false;
            return Task.FromResult(confirm);
        })).Results);
        Assert.Equal(status, result.Status);
        Assert.Equal(confirm && !lockOnConfirmation ? 1 : 0, process.TerminateRequests);
    }

    [Fact]
    public void Close_mode_confirmation_lists_snapshot_pids_and_keeps_game_directory_excluded()
    {
        var platform = new FakePlatform();
        platform.Add(50, path: @"C:\Games\Test\demo.exe");
        platform.Add(51);
        var closePlan = CreateEngine(platform).Capture(["demo"], @"C:\Games\Test");
        var plan = new BoostPlan(PowerPlanAction.SkipDisabled, "", [new("demo", "", 1, 20, true)], [], false, false)
            { ClosePlan = closePlan };
        var summary = plan.Describe(game: null);
        Assert.Contains(@"demo · PID 51 · 2026-11-18 16:00:00.0000000 UTC · C:\Apps\demo.exe", summary);
        Assert.DoesNotContain(summary, text => text.Contains("PID 50", StringComparison.Ordinal));
        Assert.DoesNotContain((plan with { CloseMode = BoostCloseMode.EcoQos }).Describe(game: null),
            text => text.Contains("PID 51", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Registered_games_protect_known_executables_and_specific_install_folders_only()
    {
        using var data = new TemporaryGameData();
        const string game = @"C:\Games\MyGame\MyGame.exe";
        const string custom = @"C:\Users\Lycoris\Downloads\MyCustomGame.exe";
        File.WriteAllText(data.CacheFile, JsonSerializer.Serialize(new
        {
            Version = 1,
            Games = new[]
            {
                new { Id = "steam:1", Name = "My Game", Platform = "Steam", ExePath = game, InstallDir = @"C:\Games\MyGame" },
                new { Id = "custom:1", Name = "Custom Game", Platform = "Custom", ExePath = custom, InstallDir = @"C:\Users\Lycoris\Downloads" },
            },
        }));
        File.WriteAllText(data.SettingsFile, """{"CustomGameFolders":["C:\\Users\\Lycoris\\Downloads\\ExtraGame.exe"]}""");
        var platform = new FakePlatform { GameProtection = BackgroundCloseGameProtection.ReadUserData(data.Directory) };
        Assert.NotNull(platform.GameProtection);
        var engine = CreateEngine(platform);
        var gameProcess = platform.Add(50, name: "MyGame", path: game);
        platform.Add(51, name: "game-helper", path: @"C:\Games\MyGame\bin\game-helper.exe");
        platform.Add(52, name: "MyCustomGame", path: custom);
        platform.Add(53, name: "ExtraGame", path: @"C:\Users\Lycoris\Downloads\ExtraGame.exe");
        platform.Add(54, path: @"C:\Users\Lycoris\Downloads\demo.exe");

        var plan = engine.Capture(["MyGame", "game-helper", "MyCustomGame", "ExtraGame", "demo"]);
        Assert.Equal(54, Assert.Single(plan.Targets).Pid);
        Assert.Equal(4, plan.Unavailable.Count);

        var (code, report) = await ExecuteAsync(engine, ["--close-background-apps", "MyGame", "--yes", "--plan",
            engine.Capture(["MyGame"]).Fingerprint]);
        Assert.Equal(BackgroundAppCloseCommandLine.Exit.Denied, code);
        Assert.Equal("denied", report.GetProperty("status").GetString());
        Assert.Equal(0, gameProcess.CloseRequests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Legacy_game_data_fills_each_missing_current_file_before_the_migration_marker(bool migrated, bool currentSettings)
    {
        using var current = new TemporaryGameData(createCache: false);
        using var legacy = new TemporaryGameData();
        System.IO.Directory.CreateDirectory(Path.Combine(current.Directory, "Logs"));
        File.WriteAllText(legacy.SettingsFile, """{"CustomGameFolders":["C:\\Games\\LegacySettingsGame.exe"]}""");
        File.WriteAllText(legacy.CacheFile, """
            {"Version":1,"Games":[{"Id":"steam:42","Name":"Legacy cache game","Platform":"Steam","InstallDir":"C:\\Games\\LegacyCache"}]}
            """);
        if (migrated)
            File.WriteAllText(current.MigrationMarker, "Migration complete");
        if (currentSettings)
            File.WriteAllText(current.SettingsFile, """{"CustomGameFolders":["C:\\Games\\CurrentSettingsGame.exe"]}""");

        var protection = Assert.IsType<BackgroundCloseGameProtection>(
            BackgroundCloseGameProtection.ReadUserData(current.Directory, legacy.Directory));
        Assert.Equal(!migrated && !currentSettings, protection.Protects(@"C:\Games\LegacySettingsGame.exe"));
        Assert.Equal(!migrated, protection.Protects(@"C:\Games\LegacyCache\Game.exe"));
        Assert.Equal(currentSettings, protection.Protects(@"C:\Games\CurrentSettingsGame.exe"));
        Assert.Equal(currentSettings, File.Exists(current.SettingsFile));
        Assert.False(File.Exists(current.CacheFile));
        Assert.Equal(migrated, File.Exists(current.MigrationMarker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_game_protection_prevents_all_initial_actions(bool damageSettings)
    {
        using var data = new TemporaryGameData();
        var platform = new FakePlatform { GameProtection = BackgroundCloseGameProtection.ReadUserData(data.Directory) };
        var original = platform.Add(50);
        var engine = CreateEngine(platform);
        var plan = engine.Capture(["demo"]);
        var damaged = damageSettings ? data.SettingsFile : data.CacheFile;
        const string malformed = """{"CustomGameFolders":["C:\\safe.exe",42]}""";
        File.WriteAllText(damaged, malformed);

        platform.GameProtection = BackgroundCloseGameProtection.ReadUserData(data.Directory);
        Assert.Null(platform.GameProtection);
        Assert.False(engine.Capture(["demo"]).HasFullSelection);
        var result = await engine.CloseAsync(plan, _ => Task.FromResult(true));
        Assert.True(result.PreflightAborted);
        Assert.Equal(0, original.CloseRequests);
        Assert.Equal(0, original.TerminateRequests);
        Assert.Equal(malformed, File.ReadAllText(damaged));
        Assert.False(File.Exists(damaged + ".bad"));
    }

    [Fact]
    public async Task Game_registration_during_force_confirmation_prevents_termination()
    {
        using var data = new TemporaryGameData();
        var platform = new FakePlatform();
        var original = platform.Add(50);
        var engine = CreateEngine(platform);

        var result = await engine.CloseAsync(engine.Capture(["demo"]), _ =>
        {
            File.WriteAllText(data.SettingsFile, """{"CustomGameFolders":["C:\\Apps\\demo.exe"]}""");
            platform.GameProtection = BackgroundCloseGameProtection.ReadUserData(data.Directory);
            return Task.FromResult(true);
        });
        Assert.Equal(BackgroundCloseStatus.Denied, Assert.Single(result.Results).Status);
        Assert.False(result.SelectionVerified);
        Assert.Equal(1, original.CloseRequests);
        Assert.Equal(0, original.TerminateRequests);
    }

    private sealed class TemporaryGameData : IDisposable
    {
        private readonly DirectoryInfo _temp = System.IO.Directory.CreateTempSubdirectory("LycorisBackgroundClosure-");

        public TemporaryGameData(bool createCache = true)
        {
            if (createCache) _temp.CreateSubdirectory("Cache");
        }

        public string Directory => _temp.FullName;
        public string CacheFile => Path.Combine(Directory, "Cache", "games.json");
        public string SettingsFile => Path.Combine(Directory, "settings.json");
        public string MigrationMarker => Path.Combine(Directory, ".migrated-from-monolith");

        public void Dispose()
        {
            if (File.Exists(CacheFile)) File.Delete(CacheFile);
            File.Delete(SettingsFile);
            File.Delete(MigrationMarker);
            foreach (var name in new[] { "Cache", "Logs" })
            {
                var subdirectory = Path.Combine(Directory, name);
                if (System.IO.Directory.Exists(subdirectory)) System.IO.Directory.Delete(subdirectory);
            }
            _temp.Delete();
        }
    }

    private static BackgroundCloseEngine CreateEngine(FakePlatform platform) => new(platform, TimeSpan.Zero, TimeSpan.Zero);

    private static string[] Apply(string plan, bool force = false) =>
        ["--close-background-apps", "demo", "--yes", "--plan", plan, .. force ? new[] { "--force" } : []];

    private static async Task<(int Code, JsonElement Report)> ExecuteAsync(BackgroundCloseEngine engine, string[] args, bool elevated = true)
    {
        using var output = new StringWriter();
        var code = await BackgroundAppCloseCommandLine.ExecuteAsync(args, elevated, engine, output);
        using var report = JsonDocument.Parse(output.ToString());
        return (code, report.RootElement.Clone());
    }

    private sealed class FakePlatform : IBackgroundClosePlatform
    {
        public bool CanMutate { get; set; } = true;
        public bool CanEnumerate { get; set; } = true;
        public BackgroundCloseGameProtection? GameProtection { get; set; } = BackgroundCloseGameProtection.Empty;
        public BackgroundCloseContext Context { get; set; } = new(Owner, 1, 1000, @"C:\Windows\");
        public Dictionary<int, FakeProcess> Processes { get; } = [];
        public List<(int Pid, bool Terminate)> Opens { get; } = [];

        public FakeProcess Add(int pid, string name = "demo", string? path = null, DateTime? started = null, string owner = Owner)
        {
            var process = new FakeProcess(name, new(pid, started ?? Start, path ?? $@"C:\Apps\{name}.exe", owner, 1, false));
            Processes[pid] = process;
            return process;
        }

        public bool CanClose() => CanMutate;
        public BackgroundCloseContext? GetContext() => Context;
        public BackgroundCloseGameProtection? GetGameProtection() => GameProtection;
        public IReadOnlyList<BackgroundCloseCandidate>? Enumerate() =>
            CanEnumerate ? Processes.Where(p => !p.Value.Exited).Select(p => new BackgroundCloseCandidate(p.Key, p.Value.Name, 20)).ToArray() : null;

        public IBackgroundCloseHandle? Open(int pid, bool terminate, out BackgroundCloseStatus failure)
        {
            Opens.Add((pid, terminate));
            if (!Processes.TryGetValue(pid, out var process))
            {
                failure = BackgroundCloseStatus.Stale;
                return null;
            }
            failure = process.OpenFailure ?? BackgroundCloseStatus.StillRunning;
            return process.OpenFailure.HasValue ? null : new FakeHandle(process, terminate);
        }
    }

    private sealed class FakeProcess(string name, BackgroundCloseIdentity identity)
    {
        public string Name { get; } = name;
        public BackgroundCloseIdentity? Identity { get; set; } = identity;
        public BackgroundCloseStatus? OpenFailure { get; set; }
        public bool Exited { get; set; }
        public bool ExitOnTerminate { get; set; } = true;
        public BackgroundCloseRequest CloseResponse { get; set; } = BackgroundCloseRequest.Sent;
        public int CloseRequests { get; set; }
        public int TerminateRequests { get; set; }
        public Action? OnClose { get; set; }
    }

    private sealed class FakeHandle(FakeProcess process, bool canTerminate) : IBackgroundCloseHandle
    {
        public BackgroundCloseIdentity? ReadIdentity() => process.Identity;
        public BackgroundProcessState GetState() => process.Exited ? BackgroundProcessState.Exited : BackgroundProcessState.Running;

        public BackgroundCloseRequest RequestClose()
        {
            process.CloseRequests++;
            process.OnClose?.Invoke();
            return process.CloseResponse;
        }

        public BackgroundCloseRequest Terminate()
        {
            Assert.True(canTerminate);
            process.TerminateRequests++;
            process.Exited = process.ExitOnTerminate;
            return BackgroundCloseRequest.Sent;
        }

        public void Dispose() { }
    }
}
