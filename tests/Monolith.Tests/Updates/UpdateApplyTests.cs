using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Monolith.Core;
using Monolith.Core.Updates;

namespace Monolith.Tests.Updates;

/// <summary>An in-memory file system and process table for <see cref="UpdateApplier"/>.</summary>
internal class FakeEnv : IUpdateEnvironment
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Log { get; } = [];
    public bool OldProcessExits { get; set; } = true;
    public ProcessWatch NewVersionBehaviour { get; set; } = ProcessWatch.StillRunning;
    public int NewExitCode { get; set; } = 0;
    public bool FolderWritable { get; set; } = true;
    public DriveKindInfo Drive { get; set; } = DriveKindInfo.Fixed;
    public HashSet<string> LockedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FailCopyOnce { get; set; }
    public int MoveAttemptsBeforeSuccess { get; set; }
    public List<string> Started { get; } = [];

    public bool FileExists(string p) => Files.ContainsKey(p);
    public long FileLength(string p) => Files[p].Length;

    public void Move(string from, string to)
    {
        Log.Add($"move {Path.GetFileName(from)} -> {Path.GetFileName(to)}");
        if (LockedFiles.Contains(from)) throw new IOException("locked");
        if (MoveAttemptsBeforeSuccess > 0) { MoveAttemptsBeforeSuccess--; throw new IOException("busy"); }
        if (Files.ContainsKey(to)) throw new IOException("exists");
        Files[to] = Files[from];
        Files.Remove(from);
    }

    public void Copy(string from, string to)
    {
        Log.Add($"copy {Path.GetFileName(from)} -> {Path.GetFileName(to)}");
        if (FailCopyOnce) { FailCopyOnce = false; Files[to] = Files[from][..3]; throw new IOException("disk full"); }
        if (Files.ContainsKey(to)) throw new IOException("exists");
        Files[to] = Files[from];
    }

    public void Delete(string p)
    {
        Log.Add("delete " + Path.GetFileName(p));
        if (LockedFiles.Contains(p)) throw new UnauthorizedAccessException("locked");
        Files.Remove(p);
    }

    public bool WaitForExit(int pid, TimeSpan timeout) => OldProcessExits;

    public ProcessWatch StartAndWatch(string exe, TimeSpan watch, out int exitCode)
    {
        Started.Add(Files.GetValueOrDefault(exe) ?? "?");
        Log.Add("start " + Path.GetFileName(exe));
        exitCode = NewExitCode;
        return NewVersionBehaviour;
    }

    public void StartDetached(string exe)
    {
        Started.Add("restart:" + (Files.GetValueOrDefault(exe) ?? "?"));
        Log.Add("restart " + Path.GetFileName(exe));
    }

    public bool CanWriteFolder(string folder) => FolderWritable;
    public DriveKindInfo DriveKind(string path) => Drive;
    public void Delay(TimeSpan time) { }
}

public sealed class UpdateApplyTests
{
    private const string Old = @"C:\Apps\MONOLITH\Lycoris.exe";
    private const string Self = @"C:\Apps\MONOLITH\Lycoris.exe.new";

    private static FakeEnv Env()
    {
        var e = new FakeEnv();
        e.Files[Old] = "OLD-VERSION";
        e.Files[Self] = "NEW-VERSION";
        return e;
    }

    [Fact]
    public void Success_old_becomes_dot_old_and_new_takes_the_name()
    {
        var e = Env();
        Assert.Equal(ApplyOutcome.Updated, UpdateApplier.Apply(e, Self, Old, 123));
        Assert.Equal("NEW-VERSION", e.Files[Old]);
        Assert.Equal("OLD-VERSION", e.Files[Old + ".old"]);
        Assert.Equal("NEW-VERSION", e.Files[Self]); // the running updater file is only copied, never moved
        Assert.Equal(["NEW-VERSION"], e.Started);
    }

    [Fact]
    public void Only_one_backup_is_kept()
    {
        var e = Env();
        e.Files[Old + ".old"] = "ANCIENT";
        Assert.Equal(ApplyOutcome.Updated, UpdateApplier.Apply(e, Self, Old, 1));
        Assert.Equal("OLD-VERSION", e.Files[Old + ".old"]);
    }

    [Theory]
    [InlineData(ProcessWatch.ExitedFailed, 0xC0000005)]
    [InlineData(ProcessWatch.StartFailed, 0)]
    public void A_new_version_that_crashes_or_does_not_start_rolls_back(ProcessWatch behaviour, long code)
    {
        var e = Env();
        e.NewVersionBehaviour = behaviour;
        e.NewExitCode = (int)code;
        Assert.Equal(ApplyOutcome.RolledBack, UpdateApplier.Apply(e, Self, Old, 1));
        Assert.Equal("OLD-VERSION", e.Files[Old]);
        Assert.False(e.Files.ContainsKey(Old + ".old"));
        Assert.Equal(["NEW-VERSION", "restart:OLD-VERSION"], e.Started);
    }

    [Fact]
    public void A_new_version_that_exits_quietly_counts_as_started()
    {
        // e.g. it handed over to a copy that was already running
        var e = Env();
        e.NewVersionBehaviour = ProcessWatch.ExitedOk;
        Assert.Equal(ApplyOutcome.Updated, UpdateApplier.Apply(e, Self, Old, 1));
    }

    [Fact]
    public void A_copy_that_fails_halfway_puts_the_old_file_back()
    {
        var e = Env();
        e.FailCopyOnce = true;
        Assert.Equal(ApplyOutcome.RolledBack, UpdateApplier.Apply(e, Self, Old, 1));
        Assert.Equal("OLD-VERSION", e.Files[Old]);
        Assert.DoesNotContain("NEW-VERSION", e.Started);
    }

    [Fact]
    public void If_the_old_process_does_not_close_nothing_is_touched()
    {
        var e = Env();
        e.OldProcessExits = false;
        Assert.Equal(ApplyOutcome.NothingChanged, UpdateApplier.Apply(e, Self, Old, 1));
        Assert.Equal("OLD-VERSION", e.Files[Old]);
        Assert.Empty(e.Log);
    }

    [Fact]
    public void A_locked_old_file_leaves_everything_as_it_was()
    {
        var e = Env();
        e.LockedFiles.Add(Old);
        Assert.Equal(ApplyOutcome.NothingChanged, UpdateApplier.Apply(e, Self, Old, 1));
        Assert.Equal("OLD-VERSION", e.Files[Old]);
        Assert.False(e.Files.ContainsKey(Old + ".old"));
        Assert.Empty(e.Started);
    }

    [Fact]
    public void A_briefly_busy_file_is_retried()
    {
        var e = Env();
        e.MoveAttemptsBeforeSuccess = 2;
        Assert.Equal(ApplyOutcome.Updated, UpdateApplier.Apply(e, Self, Old, 1));
    }

    [Fact]
    public void The_updater_never_replaces_itself()
    {
        var e = Env();
        Assert.Equal(ApplyOutcome.NothingChanged, UpdateApplier.Apply(e, Old, Old, 1));
        Assert.DoesNotContain(e.Log, l => l.StartsWith("move"));
    }

    [Theory]
    [InlineData(ApplyOutcome.Updated, 0)]
    [InlineData(ApplyOutcome.RolledBack, 10)]
    public void The_headless_mode_reports_through_its_exit_code(ApplyOutcome expected, int code)
    {
        var e = Env();
        e.NewVersionBehaviour = expected == ApplyOutcome.Updated ? ProcessWatch.StillRunning : ProcessWatch.ExitedFailed;
        e.NewExitCode = 1;
        Assert.Equal(code, UpdateApplyMode.Run([UpdateApplyMode.Switch, Old, "42"], e, Self));
    }

    [Theory]
    [InlineData("--apply-update")]
    [InlineData("--apply-update", "relative.txt", "5")]
    [InlineData("--apply-update", @"C:\x\Lycoris.exe", "abc")]
    [InlineData("--apply-update", @"C:\x\Lycoris.exe", "0")]
    public void Bad_arguments_change_nothing(params string[] args)
    {
        var e = Env();
        Assert.Equal(2, UpdateApplyMode.Run(args, e, Self));
        Assert.Empty(e.Log);
    }

    [Fact]
    public void Failure_texts_exist_for_every_bad_outcome_and_none_for_success()
    {
        Assert.Null(UpdateApplyMode.FailureText(0, Old));
        foreach (var c in new[] { 10, 11, 12 }) Assert.False(string.IsNullOrWhiteSpace(UpdateApplyMode.FailureText(c, Old)));
        Assert.Contains(Old + ".old", UpdateApplyMode.FailureText(12, Old));
    }

    // ───────── planning ─────────

    [Fact]
    public void A_writable_folder_updates_in_place_without_uac()
    {
        var plan = UpdateApplier.Plan(Env(), Old, elevated: false);
        Assert.Equal(UpdateRoute.InPlace, plan.Route);
        Assert.Equal(Old + ".new", plan.StagePath);
        Assert.Equal(Old + ".old", plan.BackupPath);
    }

    [Fact]
    public void A_protected_folder_asks_for_uac_and_an_elevated_unwritable_one_is_unsupported()
    {
        var e = Env();
        e.FolderWritable = false;
        Assert.Equal(UpdateRoute.Elevated, UpdateApplier.Plan(e, Old, elevated: false).Route);
        var plan = UpdateApplier.Plan(e, Old, elevated: true);
        Assert.Equal(UpdateRoute.Unsupported, plan.Route);
        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
    }

    [Theory]
    [InlineData(DriveKindInfo.Network)]
    [InlineData(DriveKindInfo.CdRom)]
    public void Network_and_read_only_media_are_told_plainly(DriveKindInfo kind)
    {
        var e = Env();
        e.Drive = kind;
        var plan = UpdateApplier.Plan(e, Old, false);
        Assert.Equal(UpdateRoute.Unsupported, plan.Route);
        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
    }

    [Fact]
    public void A_removable_drive_and_a_portable_copy_on_the_desktop_update_in_place()
    {
        var e = Env();
        e.Drive = DriveKindInfo.Removable;
        Assert.Equal(UpdateRoute.InPlace, UpdateApplier.Plan(e, Old, false).Route);
        var desktop = @"C:\Users\Me\Desktop\Lycoris.exe";
        e.Files[desktop] = "X";
        Assert.Equal(UpdateRoute.InPlace, UpdateApplier.Plan(e, desktop, false).Route);
    }

    [Fact]
    public void A_multi_file_development_build_is_never_replaced()
    {
        var e = Env();
        e.Files[@"C:\Apps\MONOLITH\Lycoris.dll"] = "dll";
        Assert.Equal(UpdateRoute.Unsupported, UpdateApplier.Plan(e, Old, false).Route);
        Assert.Equal(UpdateRoute.Unsupported, UpdateApplier.Plan(e, @"C:\Apps\Missing.exe", false).Route);
        e.Files[@"C:\Program Files\dotnet\dotnet.exe"] = "x";
        Assert.Equal(UpdateRoute.Unsupported, UpdateApplier.Plan(e, @"C:\Program Files\dotnet\dotnet.exe", false).Route);
    }
}

/// <summary>Task-class and game-session gate («running tasks are not interrupted»).</summary>
public sealed class UpdateGateTests
{
    private static OperationInfo Op(OperationClass c, string name = "Задача", OperationState s = OperationState.Running) =>
        new(1, name, c, s, null, null, null, false, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(OperationClass.Install)]
    [InlineData(OperationClass.JournalChange)]
    [InlineData(OperationClass.Cleanup)]
    [InlineData(OperationClass.SystemRepair)]
    [InlineData(OperationClass.Disruptive)]
    public void A_running_task_of_any_system_class_blocks_the_update_with_its_name(OperationClass c)
    {
        var text = Monolith.Shell.UpdateController.BlockerText([Op(c, "Установка Steam")], false);
        Assert.NotNull(text);
        Assert.Contains("Установка Steam", text);
    }

    [Fact]
    public void A_queued_task_blocks_too()
    {
        Assert.NotNull(Monolith.Shell.UpdateController.BlockerText([Op(OperationClass.Install, s: OperationState.Queued)], false));
    }

    [Fact]
    public void Measurements_and_background_work_do_not_block()
    {
        Assert.Null(Monolith.Shell.UpdateController.BlockerText([Op(OperationClass.Measure), Op(OperationClass.Background)], false));
        Assert.Null(Monolith.Shell.UpdateController.BlockerText([], false));
    }

    [Fact]
    public void A_game_session_blocks_and_says_so()
    {
        var text = Monolith.Shell.UpdateController.BlockerText([], true);
        Assert.NotNull(text);
        Assert.Contains("игров", text);
    }

    [Fact]
    public void The_reason_is_translated()
    {
        using var _ = Loc.UseLanguage("en");
        var text = Monolith.Shell.UpdateController.BlockerText([], true)!;
        Assert.DoesNotMatch("[а-яА-Я]", text);
    }
}

/// <summary>
/// The replacement on a real disk in a temp folder with tiny real Windows exes (never Lycoris, never the network):
/// proves the rename/copy/rollback and the launch of a process, not only the logic.
/// </summary>
public sealed class UpdateOnDiskTests : IDisposable
{
    private readonly string _dir = Kit.TempDir("disk");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Sys(string name) => Path.Combine(Environment.SystemDirectory, name);

    private static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo(Sys("hostname.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit();
        return p.Id;
    }

    [Fact]
    public void Replace_succeeds_with_a_new_exe_that_starts_and_keeps_one_backup()
    {
        var oldExe = Path.Combine(_dir, "Lycoris.exe");
        var self = Path.Combine(_dir, "Lycoris.exe.new");
        File.Copy(Sys("whoami.exe"), oldExe);
        File.Copy(Sys("hostname.exe"), self);       // starts and ends with code 0
        var oldBytes = File.ReadAllBytes(oldExe);
        var newBytes = File.ReadAllBytes(self);

        var outcome = UpdateApplier.Apply(new SystemUpdateEnvironment(), self, oldExe, DeadPid());

        Assert.Equal(ApplyOutcome.Updated, outcome);
        Assert.Equal(newBytes, File.ReadAllBytes(oldExe));
        Assert.Equal(oldBytes, File.ReadAllBytes(oldExe + ".old"));
        Assert.True(File.Exists(self));
    }

    [Fact]
    public void Rollback_restores_the_old_exe_byte_for_byte_when_the_new_one_fails()
    {
        var oldExe = Path.Combine(_dir, "Lycoris.exe");
        var self = Path.Combine(_dir, "Lycoris.exe.new");
        File.Copy(Sys("hostname.exe"), oldExe);
        File.Copy(Sys("findstr.exe"), self);        // no arguments → exits with code 2
        var oldBytes = File.ReadAllBytes(oldExe);

        var outcome = UpdateApplier.Apply(new SystemUpdateEnvironment(), self, oldExe, DeadPid());

        Assert.Equal(ApplyOutcome.RolledBack, outcome);
        Assert.Equal(oldBytes, File.ReadAllBytes(oldExe));
        Assert.False(File.Exists(oldExe + ".old"));
    }

    [Fact]
    public void A_missing_old_process_id_is_treated_as_closed_and_a_live_one_as_not()
    {
        var env = new SystemUpdateEnvironment();
        Assert.True(env.WaitForExit(DeadPid(), TimeSpan.FromSeconds(1)));
        using var sleeper = Process.Start(new ProcessStartInfo(Sys("ping.exe"), "-n 6 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try { Assert.False(env.WaitForExit(sleeper.Id, TimeSpan.FromMilliseconds(300))); }
        finally { try { sleeper.Kill(); } catch { } }
    }

    [Fact]
    public void Folder_probe_and_drive_kind_work_on_a_real_folder()
    {
        var env = new SystemUpdateEnvironment();
        Assert.True(env.CanWriteFolder(_dir));
        Assert.False(env.CanWriteFolder(Path.Combine(_dir, "does-not-exist")));
        Assert.Empty(Directory.GetFiles(_dir)); // the probe cleaned up
        Assert.Equal(DriveKindInfo.Network, env.DriveKind(@"\\server\share\Lycoris.exe"));
        Assert.Contains(env.DriveKind(Path.Combine(_dir, "x.exe")), new[] { DriveKindInfo.Fixed, DriveKindInfo.Removable });
    }

    [Fact]
    public async Task Launcher_stages_next_to_the_exe_and_starts_the_updater_with_the_right_arguments()
    {
        var exe = Path.Combine(_dir, "Lycoris.exe");
        File.Copy(Sys("hostname.exe"), exe);
        var staged = Path.Combine(_dir, "stage", "Lycoris-1.0.1.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(Sys("hostname.exe"), staged); // harmless: prints an error for unknown arguments and exits
        var sha = await UpdateVerifier.Sha256Async(staged, default);
        var plan = new UpdatePlan(UpdateRoute.InPlace, exe, null);

        var result = await UpdateLauncher.StartAsync(plan, new StagedUpdate(staged, sha, new FileInfo(staged).Length, Kit.Release()), Environment.ProcessId);

        Assert.True(result.Started, result.Error);
        Assert.True(File.Exists(plan.StagePath));
        Assert.Equal(sha, await UpdateVerifier.Sha256Async(plan.StagePath, default));
    }

    [Fact]
    public async Task Launcher_refuses_a_file_that_changed_after_verification_and_cleans_its_copy()
    {
        var exe = Path.Combine(_dir, "Lycoris.exe");
        File.Copy(Sys("hostname.exe"), exe);
        var staged = Path.Combine(_dir, "Lycoris-1.0.1.exe");
        File.Copy(Sys("hostname.exe"), staged);
        var plan = new UpdatePlan(UpdateRoute.InPlace, exe, null);

        var result = await UpdateLauncher.StartAsync(plan, new StagedUpdate(staged, new string('0', 64), new FileInfo(staged).Length, Kit.Release()), 1);

        Assert.False(result.Started);
        Assert.False(File.Exists(plan.StagePath));
    }

    [Fact]
    public async Task An_unsupported_plan_starts_nothing()
    {
        var plan = new UpdatePlan(UpdateRoute.Unsupported, Path.Combine(_dir, "Lycoris.exe"), "почему");
        var result = await UpdateLauncher.StartAsync(plan, new StagedUpdate("x", "y", 1, Kit.Release()), 1);
        Assert.False(result.Started);
        Assert.Equal("почему", result.Error);
    }

    [Fact]
    public async Task A_verified_handle_blocks_writers_while_it_is_open()
    {
        var f = Path.Combine(_dir, "a.exe");
        File.WriteAllBytes(f, Kit.FakePe(2048));
        var sha = Kit.Sha(File.ReadAllBytes(f));
        await using var locked = await UpdateLauncher.OpenVerifiedAsync(f, sha);
        Assert.Throws<IOException>(() => File.WriteAllBytes(f, [1, 2, 3]));
        Assert.Throws<IOException>(() => File.Delete(f));
    }

    [Fact]
    public void Cleanup_removes_the_leftovers_and_only_old_staged_downloads()
    {
        var exe = Path.Combine(_dir, "Lycoris.exe");
        File.WriteAllText(exe, "current");
        File.WriteAllText(exe + ".old", "old");
        File.WriteAllText(exe + ".new", "new");
        var updates = Path.Combine(_dir, "Updates");
        Directory.CreateDirectory(updates);
        File.WriteAllText(Path.Combine(updates, "Lycoris-1.0.0.exe"), "x");
        File.WriteAllText(Path.Combine(updates, "Lycoris-1.0.1.exe"), "x");
        File.WriteAllText(Path.Combine(updates, "Lycoris-1.0.2.exe"), "x");   // newer than the running build: kept
        File.WriteAllText(Path.Combine(updates, "notes.txt"), "x");

        UpdateCleanup.Run(exe, updates, new SemVer(1, 0, 1, 0, ""));

        Assert.True(File.Exists(exe));
        Assert.False(File.Exists(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));
        Assert.False(File.Exists(Path.Combine(updates, "Lycoris-1.0.0.exe")));
        Assert.False(File.Exists(Path.Combine(updates, "Lycoris-1.0.1.exe")));
        Assert.True(File.Exists(Path.Combine(updates, "Lycoris-1.0.2.exe")));
        Assert.True(File.Exists(Path.Combine(updates, "notes.txt")));
    }
}
