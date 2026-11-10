using System.IO;
using Lycoris.Core;
using Lycoris.Core.Maintenance;
using Lycoris.Modules.Cleaner;
using Lycoris.UI;

namespace Lycoris.Tests;

/// <summary>A Task Scheduler in memory: nothing real is ever registered.</summary>
internal sealed class FakeMaintenanceStore : IMaintenanceTaskStore
{
    public string? Xml { get; set; }
    public bool Fail { get; set; }
    public int Registers { get; private set; }
    public int Deletes { get; private set; }

    public string? ReadXml() => Fail ? throw new InvalidOperationException("scheduler down") : Xml;

    public void Register(string xml)
    {
        Registers++;
        Xml = xml;
    }

    public void Delete()
    {
        Deletes++;
        Xml = null;
    }
}

/// <summary>
/// «Обслуживание по расписанию»: the task definition (normal rights only), reading it back, the journal with undo, the trust
/// checks on forged records, the headless run and its result file, the startup notice. No real task, no real cleanup.
/// </summary>
public sealed class ScheduledMaintenanceTests : IDisposable
{
    private const string Exe = @"C:\Tools\Lycoris\Lycoris.exe";
    private readonly FakeMaintenanceStore _store = new();
    private readonly IDisposable _scope;
    private readonly ChangeJournal _journal = ChangeJournal.ForTests();
    private readonly string _sid = ScheduledMaintenanceTask.CurrentUserSid() ?? "S-1-5-21-1-2-3-1001";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "lycoris-maint-" + Guid.NewGuid().ToString("N"));

    public ScheduledMaintenanceTests()
    {
        _scope = ScheduledMaintenanceTask.OverrideForTests(_store);
        ChangeSession.BypassElevationForTests = true;
    }

    public void Dispose()
    {
        _scope.Dispose();
        ChangeSession.BypassElevationForTests = false;
        try { Directory.Delete(_temp, true); } catch { }
    }

    private MaintenanceTaskSnapshot State(DayOfWeek day = DayOfWeek.Sunday, int hour = 19, int minute = 0, string exe = Exe) =>
        MaintenanceTaskSnapshot.For(new MaintenanceSchedule(day, hour, minute), exe, _sid);

    private Task<ChangeSession> Begin() => ChangeSession.BeginAsync("test", createRestorePoint: false, journal: _journal);

    // ───────────────────────────── Task definition ─────────────────────────────

    [Fact]
    public void Task_runs_with_normal_rights_only_and_round_trips()
    {
        var xml = ScheduledMaintenanceTask.BuildXml(State(DayOfWeek.Wednesday, 7, 30), new DateTime(2026, 11, 10));

        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.DoesNotContain("HighestAvailable", xml);
        Assert.Contains("<Arguments>--maintain</Arguments>", xml);
        Assert.Contains("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<ExecutionTimeLimit>PT30M</ExecutionTimeLimit>", xml);
        Assert.Contains("2026-11-11T07:30:00", xml); // the next Wednesday after Tue 10.11

        var back = ScheduledMaintenanceTask.Parse(xml);
        Assert.Equal(State(DayOfWeek.Wednesday, 7, 30), back);
    }

    [Theory]
    [InlineData("<RunLevel>LeastPrivilege</RunLevel>", "<RunLevel>HighestAvailable</RunLevel>")]
    [InlineData("<LogonType>InteractiveToken</LogonType>", "<LogonType>Password</LogonType>")]
    [InlineData("<Arguments>--maintain</Arguments>", "<Arguments>--maintain --e2e x</Arguments>")]
    [InlineData("<Author>Lycoris</Author>", "<Author>Someone</Author>")]
    [InlineData(@"<Command>C:\Tools\Lycoris\Lycoris.exe</Command>", @"<Command>\\server\share\Lycoris.exe</Command>")]
    [InlineData(@"<Command>C:\Tools\Lycoris\Lycoris.exe</Command>", @"<Command>C:\Tools\cmd.bat</Command>")]
    [InlineData("</Exec>", "</Exec><Exec><Command>C:\\x.exe</Command></Exec>")]
    public void A_task_in_any_other_form_is_not_Lycoris_s(string from, string to)
    {
        var xml = ScheduledMaintenanceTask.BuildXml(State()).Replace(from, to);
        Assert.Null(ScheduledMaintenanceTask.Parse(xml));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<!DOCTYPE x [<!ENTITY a \"b\">]><Task/>")]
    public void Hostile_task_xml_is_refused(string xml) => Assert.Null(ScheduledMaintenanceTask.Parse(xml));

    [Theory]
    [InlineData(@"Lycoris.exe")]
    [InlineData(@"\\server\share\Lycoris.exe")]
    [InlineData("C:\\a\"b\\Lycoris.exe")]
    [InlineData(@"C:\Tools\Lycoris.cmd")]
    [InlineData("C:\\Tools\\Lycoris\n.exe")]
    public void Implausible_exe_paths_are_refused(string exe)
    {
        Assert.False(ScheduledMaintenanceTask.IsPlausibleExe(exe));
        Assert.NotNull(State(exe: exe).Problem());
    }

    [Fact]
    public void Snapshot_problems()
    {
        Assert.Null(MaintenanceTaskSnapshot.Absent.Problem());
        Assert.Null(State().Problem());
        Assert.NotNull((State() with { UserSid = "S-1-5-21-x" }).Problem());
        Assert.NotNull((State() with { Hour = 24 }).Problem());
        Assert.NotNull((State() with { Day = (DayOfWeek)9 }).Problem());
        Assert.NotNull((MaintenanceTaskSnapshot.Absent with { ExePath = Exe }).Problem());
    }

    // ───────────────────────────── Reading the state ─────────────────────────────

    [Fact]
    public void Inspect_tells_the_states_apart()
    {
        Assert.Equal(MaintenanceTaskStatus.Off, ScheduledMaintenanceTask.Inspect(Exe, _ => true).Status);

        _store.Xml = ScheduledMaintenanceTask.BuildXml(State());
        Assert.Equal(MaintenanceTaskStatus.On, ScheduledMaintenanceTask.Inspect(Exe, _ => true).Status);
        var missing = ScheduledMaintenanceTask.Inspect(Exe, _ => false);
        Assert.Equal(MaintenanceTaskStatus.ExeMissing, missing.Status);
        Assert.True(missing.NeedsFix);
        Assert.Equal(MaintenanceTaskStatus.OtherCopy, ScheduledMaintenanceTask.Inspect(@"D:\Moved\Lycoris.exe", _ => true).Status);

        _store.Xml = _store.Xml.Replace("LeastPrivilege", "HighestAvailable");
        Assert.Equal(MaintenanceTaskStatus.Foreign, ScheduledMaintenanceTask.Inspect(Exe, _ => true).Status);

        _store.Fail = true;
        Assert.Equal(MaintenanceTaskStatus.Unknown, ScheduledMaintenanceTask.Inspect(Exe, _ => true).Status);
    }

    [Fact]
    public void No_such_task_through_late_bound_COM_counts_as_absent()
    {
        // The runtime maps 0x80070002 / 0x80070003 to these exceptions; they are «no task», not «scheduler silent».
        Assert.True(ScheduledTasks.IsNotFound(new FileNotFoundException("x")));
        Assert.True(ScheduledTasks.IsNotFound(new DirectoryNotFoundException("x")));
        Assert.False(ScheduledTasks.IsNotFound(new FileNotFoundException("x") { HResult = unchecked((int)0x80070005) }));
    }

    // ───────────────────────────── Journal and undo ─────────────────────────────

    [Fact]
    public async Task Turning_on_is_journaled_and_undo_removes_the_task()
    {
        var s = await Begin();
        Assert.True(s.SetMaintenanceTask(State(), "on"));

        var r = Assert.Single(_journal.ActiveRecords);
        Assert.Equal(ScheduledMaintenanceTask.Source, r.Source);
        Assert.Equal(ChangeKind.ScheduledTask, r.Kind);
        Assert.Equal(ScheduledMaintenanceTask.Target, r.Target);
        Assert.False(r.MaintenanceTask!.Exists);
        Assert.Equal("Нет задачи", r.OldValue);
        Assert.Equal("Задача по расписанию", r.NewValue);
        Assert.Equal(State(), ScheduledMaintenanceTask.Parse(_store.Xml!));

        var result = await _journal.RevertAsync(_journal.ActiveRecords);
        Assert.True(result.Success);
        Assert.Null(_store.Xml);
        Assert.Empty(_journal.ActiveRecords);
    }

    [Fact]
    public async Task Each_change_is_a_layer_and_undo_puts_the_previous_schedule_back()
    {
        // One batch per click, as on the page.
        Assert.True((await Begin()).SetMaintenanceTask(State(DayOfWeek.Sunday, 19, 0), "on"));
        Assert.True((await Begin()).SetMaintenanceTask(State(DayOfWeek.Friday, 8, 30), "move"));
        Assert.True((await Begin()).SetMaintenanceTask(MaintenanceTaskSnapshot.Absent, "off"));
        Assert.Equal(3, _journal.ActiveRecords.Count);
        Assert.Null(_store.Xml);

        // Undo «off»: the Friday task comes back, rebuilt by Lycoris's own code.
        var off = _journal.ActiveRecords.Last();
        Assert.True((await _journal.RevertAsync([off])).Success);
        Assert.Equal(State(DayOfWeek.Friday, 8, 30), ScheduledMaintenanceTask.Parse(_store.Xml!));

        // Undo the move: Sunday again.
        var move = _journal.ActiveRecords.Last();
        Assert.True((await _journal.RevertAsync([move])).Success);
        Assert.Equal(State(DayOfWeek.Sunday, 19, 0), ScheduledMaintenanceTask.Parse(_store.Xml!));

        // Undo everything: no task.
        Assert.True((await _journal.RevertAsync(_journal.ActiveRecords)).Success);
        Assert.Null(_store.Xml);
    }

    [Fact]
    public async Task A_foreign_task_with_the_same_name_is_never_touched()
    {
        var foreign = ScheduledMaintenanceTask.BuildXml(State()).Replace("LeastPrivilege", "HighestAvailable");
        _store.Xml = foreign;
        var s = await Begin();

        Assert.False(s.SetMaintenanceTask(MaintenanceTaskSnapshot.Absent, "off"));
        Assert.False(s.SetMaintenanceTask(State(), "on"));
        Assert.Equal(foreign, _store.Xml);
        Assert.Equal(0, _store.Registers + _store.Deletes);
        Assert.Empty(_journal.ActiveRecords);
        Assert.Equal(2, s.Errors.Count);
    }

    [Fact]
    public async Task A_failed_register_leaves_no_record()
    {
        var s = await Begin();
        Assert.False(s.SetMaintenanceTask(State(exe: "relative.exe"), "bad"));
        Assert.Empty(_journal.ActiveRecords);
        Assert.Null(_store.Xml);
    }

    [Fact]
    public async Task Forged_records_are_refused_by_the_journal()
    {
        var s = await Begin();
        Assert.True(s.SetMaintenanceTask(State(), "on"));
        var r = Assert.Single(_journal.ActiveRecords);

        // The original a revert would put back is a task: must be a valid one.
        r.MaintenanceTask = State(exe: @"\\evil\share\x.exe");
        Assert.False((await _journal.RevertAsync([r])).Success);
        r.MaintenanceTask = State() with { UserSid = "S-1-5-18 x" };
        Assert.False((await _journal.RevertAsync([r])).Success);
        Assert.NotNull(_store.Xml); // nothing was changed by the refused undos

        // Another source must not carry maintenance task data; this source only its own task path.
        var other = new ChangeRecord
        {
            Source = "services-tasks", Kind = ChangeKind.ScheduledTask, Target = ScheduledMaintenanceTask.Target,
            TaskPath = ScheduledMaintenanceTask.TaskPath, MaintenanceTask = MaintenanceTaskSnapshot.Absent,
        };
        Assert.NotNull(JournalTrust.Check(other));
        var wrongPath = new ChangeRecord
        {
            Source = ScheduledMaintenanceTask.Source, Kind = ChangeKind.ScheduledTask, Target = @"task:\Microsoft\Windows\Defrag\ScheduledDefrag",
            TaskPath = @"\Microsoft\Windows\Defrag\ScheduledDefrag", MaintenanceTask = MaintenanceTaskSnapshot.Absent,
        };
        Assert.NotNull(JournalTrust.Check(wrongPath));
        var wrongKind = new ChangeRecord
        {
            Source = ScheduledMaintenanceTask.Source, Kind = ChangeKind.Command, Target = ScheduledMaintenanceTask.Target,
        };
        Assert.NotNull(JournalTrust.Check(wrongKind));
    }

    // ───────────────────────────── Headless run ─────────────────────────────

    private sealed class FakeSteps(IReadOnlyList<CleanDefinition> items, Func<CleanDefinition, ItemCleanResult> clean,
        Func<Task<(int, IReadOnlyList<string>)?>>? health = null) : IMaintenanceSteps
    {
        public int Cleaned { get; private set; }
        public IReadOnlyList<CleanDefinition> Items() => items;

        public ItemCleanResult Clean(CleanDefinition item, CancellationToken ct)
        {
            Cleaned++;
            return clean(item);
        }

        public Task<(int Score, IReadOnlyList<string> Warnings)?> CheckHealthAsync(CancellationToken ct) =>
            health is null ? Task.FromResult<(int, IReadOnlyList<string>)?>((90, ["МАЛО МЕСТА"])) : health();
    }

    private static ItemCleanResult Freed(long bytes, int files)
    {
        var r = new ItemCleanResult();
        r.Tally.Deleted = files;
        r.Tally.FreedBytes = bytes;
        return r;
    }

    [Fact]
    public void Only_safe_per_user_file_items_run_unattended()
    {
        var items = ScheduledMaintenanceRunner.SafeItems(CleanCatalog.All);
        Assert.Contains(items, d => d.Id == "temp.user");
        Assert.All(items, d =>
        {
            Assert.Equal(CleanTier.Safe, d.Tier);
            Assert.False(d.RequiresAdmin);
            Assert.True(d.DefaultChecked);
            Assert.Equal(CleanMethod.Files, d.Method);
        });
        Assert.DoesNotContain(items, d => d.Id is "recyclebin" or "temp.windows" or "wu.download" or "do.cache" or "wu.cbs");
    }

    [Fact]
    public async Task Elevated_or_disabled_runs_do_nothing()
    {
        var steps = new FakeSteps(ScheduledMaintenanceRunner.SafeItems(CleanCatalog.All), _ => Freed(1, 1));
        MaintenanceRunResult? written = null;

        Assert.Equal(ScheduledMaintenanceRunner.ExitElevated, await ScheduledMaintenanceRunner.RunAsync(true, true, steps, r => written = r));
        Assert.Equal(ScheduledMaintenanceRunner.ExitDisabled, await ScheduledMaintenanceRunner.RunAsync(false, false, steps, r => written = r));
        Assert.Equal(0, steps.Cleaned);
        Assert.Null(written);
    }

    [Fact]
    public async Task A_run_cleans_counts_and_writes_the_result()
    {
        var items = ScheduledMaintenanceRunner.SafeItems(CleanCatalog.All).Take(3).ToList();
        var n = 0;
        var steps = new FakeSteps(items, _ => ++n switch
        {
            1 => Freed(1000, 3),
            2 => new ItemCleanResult { Skipped = "Discord запущен" },
            _ => throw new IOException("locked"),
        });
        MaintenanceRunResult? written = null;

        Assert.Equal(0, await ScheduledMaintenanceRunner.RunAsync(false, true, steps, r => written = r));

        Assert.NotNull(written);
        Assert.Equal(1000, written!.FreedBytes);
        Assert.Equal(3, written.FilesDeleted);
        Assert.Equal(1, written.ItemsCleaned);
        Assert.Equal(2, written.ItemsSkipped);
        Assert.Equal(90, written.HealthScore);
        Assert.Equal(["МАЛО МЕСТА"], written.Warnings);
        Assert.Null(written.Error);
        Assert.NotNull(written.FinishedAt);
    }

    [Fact]
    public async Task A_failing_health_check_still_writes_the_cleanup_result()
    {
        var steps = new FakeSteps([], _ => Freed(0, 0), () => throw new InvalidOperationException("WMI"));
        MaintenanceRunResult? written = null;
        Assert.Equal(0, await ScheduledMaintenanceRunner.RunAsync(false, true, steps, r => written = r));
        Assert.Null(written!.HealthScore);
    }

    // ───────────────────────────── Result file (user-writable input) ─────────────────────────────

    [Fact]
    public void Result_file_round_trips_and_hostile_values_are_clamped()
    {
        var path = Path.Combine(_temp, "maintenance-last.json");
        var result = new MaintenanceRunResult
        {
            StartedUtc = DateTime.UtcNow.ToString("o"), FinishedUtc = DateTime.UtcNow.ToString("o"),
            FreedBytes = -5, FilesDeleted = -1, HealthScore = 400, HealthWarnings = 2,
            Warnings = ["a", "b\u0000c", new string('x', 500), "d", "e", "f", "g"], Error = "line1\nline2",
        };
        MaintenanceResultStore.Write(result, path);
        var back = MaintenanceResultStore.Read(path)!;

        Assert.Equal(0, back.FreedBytes);
        Assert.Equal(0, back.FilesDeleted);
        Assert.Equal(100, back.HealthScore);
        Assert.Equal(["a", "d", "e", "f", "g"], back.Warnings);
        Assert.Null(back.Error);
    }

    [Fact]
    public void Unusable_result_files_read_as_none()
    {
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, "r.json");
        Assert.Null(MaintenanceResultStore.Read(path)); // missing
        File.WriteAllText(path, "{ not json");
        Assert.Null(MaintenanceResultStore.Read(path));
        File.WriteAllText(path, "{\"FinishedUtc\":\"yesterday\"}");
        Assert.Null(MaintenanceResultStore.Read(path));
        File.WriteAllText(path, new string(' ', MaintenanceResultStore.MaxBytes + 10) + "{}");
        Assert.Null(MaintenanceResultStore.Read(path));
    }

    // ───────────────────────────── Startup notice ─────────────────────────────

    [Fact]
    public void A_new_result_is_reported_once_and_resets_the_reminder()
    {
        var settings = new AppSettings { ScheduledMaintenance = true, LastMaintenanceUtc = DateTime.UtcNow.AddDays(-20).ToString("o") };
        var at = DateTime.UtcNow.AddHours(-1);
        var last = new MaintenanceRunResult { FinishedUtc = at.ToString("o"), FreedBytes = 2048, HealthScore = 88 };
        var on = new MaintenanceTaskInfo(MaintenanceTaskStatus.On, State());

        var first = ScheduledMaintenanceNotice.Take(settings, last, on);
        Assert.NotNull(first);
        Assert.Equal(ToastKind.Success, first!.Value.Kind);
        Assert.Contains("88/100", first.Value.Text);
        Assert.Equal(at, DateTime.Parse(settings.LastMaintenanceUtc, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());

        Assert.Null(ScheduledMaintenanceNotice.Take(settings, last, on));
    }

    [Fact]
    public void A_task_that_cannot_start_Lycoris_is_reported()
    {
        var settings = new AppSettings { ScheduledMaintenance = true };
        var missing = new MaintenanceTaskInfo(MaintenanceTaskStatus.ExeMissing, State());
        Assert.Equal(ToastKind.Warning, ScheduledMaintenanceNotice.Take(settings, null, missing)!.Value.Kind);
        settings.ScheduledMaintenance = false;
        Assert.Null(ScheduledMaintenanceNotice.Take(settings, null, missing));
    }

    [Fact]
    public void An_interrupted_run_does_not_count_as_maintenance()
    {
        var settings = new AppSettings { LastMaintenanceUtc = "" };
        var last = new MaintenanceRunResult { FinishedUtc = DateTime.UtcNow.ToString("o"), Error = "Прервано" };
        var notice = ScheduledMaintenanceNotice.Take(settings, last, new MaintenanceTaskInfo(MaintenanceTaskStatus.On, State()));
        Assert.Equal(ToastKind.Warning, notice!.Value.Kind);
        Assert.Equal("", settings.LastMaintenanceUtc);
    }

    // ───────────────────────────── Replay parameter ─────────────────────────────

    [Theory]
    [InlineData("on:0:19:00", DayOfWeek.Sunday, 19, 0)]
    [InlineData("on:3:07:30", DayOfWeek.Wednesday, 7, 30)]
    public void Replay_parameter_round_trips(string text, DayOfWeek day, int hour, int minute)
    {
        var s = CleanerViewModel.ParseOn(text);
        Assert.Equal(new MaintenanceSchedule(day, hour, minute), s);
        Assert.Equal(text, CleanerViewModel.OnParameter(s!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("on:7:19:00")]
    [InlineData("on:0:24:00")]
    [InlineData("on:0:19")]
    [InlineData("on:-1:19:00")]
    [InlineData("on:0:19:00:extra")]
    [InlineData("off")]
    public void Bad_replay_parameters_are_not_schedules(string? text) => Assert.Null(CleanerViewModel.ParseOn(text));

    [Fact]
    public void ScheduleMaintenance_is_replayable() => Assert.True(Lycoris.Shell.AdminIntent.IsReplayable("ScheduleMaintenance"));
}
