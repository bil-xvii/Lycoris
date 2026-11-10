using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Xml.Linq;
using Lycoris.Core.Security;

namespace Lycoris.Core.Maintenance;

/// <summary>When the weekly maintenance runs: one day of the week and a local time.</summary>
public sealed record MaintenanceSchedule(DayOfWeek Day, int Hour, int Minute)
{
    /// <summary>Sunday evening: the PC is usually on, nobody is in the middle of work.</summary>
    public static MaintenanceSchedule Default { get; } = new(DayOfWeek.Sunday, 19, 0);

    public bool IsValid => Enum.IsDefined(Day) && Hour is >= 0 and <= 23 && Minute is >= 0 and <= 59;

    /// <summary>«Воскресенье, 19:00» / «Sunday, 19:00».</summary>
    public string Describe() => Loc.Tf("{0}, {1}", DayName(Day), TimeText);

    public string TimeText => string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", Hour, Minute);

    /// <summary>The day in the interface language with a capital letter («Воскресенье», «Sunday»).</summary>
    public static string DayName(DayOfWeek day)
    {
        var name = Loc.Culture.DateTimeFormat.GetDayName(day);
        return name.Length == 0 ? name : char.ToUpper(name[0], Loc.Culture) + name[1..];
    }
}

/// <summary>
/// The state of Lycoris's maintenance task — what the journal keeps as the original of a change and what an undo puts back.
/// It holds only data, never a task definition: the task is always rebuilt by <see cref="ScheduledMaintenanceTask.BuildXml"/>,
/// so even a forged journal record can only ever create the same harmless task (normal rights, runs «Lycoris.exe --maintain»
/// as the recorded user).
/// </summary>
public sealed record MaintenanceTaskSnapshot
{
    public bool Exists { get; init; }
    public DayOfWeek Day { get; init; }
    public int Hour { get; init; }
    public int Minute { get; init; }
    /// <summary>Full path of the Lycoris.exe the task starts.</summary>
    public string? ExePath { get; init; }
    /// <summary>SID of the account the task runs as (always with normal rights).</summary>
    public string? UserSid { get; init; }

    public static MaintenanceTaskSnapshot Absent { get; } = new();

    public static MaintenanceTaskSnapshot For(MaintenanceSchedule schedule, string exePath, string userSid) => new()
    {
        Exists = true, Day = schedule.Day, Hour = schedule.Hour, Minute = schedule.Minute, ExePath = exePath, UserSid = userSid,
    };

    public MaintenanceSchedule? Schedule => Exists ? new MaintenanceSchedule(Day, Hour, Minute) : null;

    /// <summary>Null when Lycoris could have written this state; otherwise a short Russian reason (the journal refuses it).</summary>
    public string? Problem()
    {
        if (!Exists) return ExePath is null && UserSid is null ? null : Loc.T("у отсутствующей задачи указаны данные");
        if (Schedule is not { IsValid: true }) return Loc.T("некорректное расписание задачи");
        if (!ScheduledMaintenanceTask.IsPlausibleExe(ExePath)) return Loc.T("некорректный путь программы в задаче");
        if (UserSid is null || !UserScope.SidPattern.IsMatch(UserSid)) return Loc.T("некорректная учётная запись задачи");
        return null;
    }
}

/// <summary>What the maintenance task looks like now (read-only, for the Cleaner card and the startup notice).</summary>
public enum MaintenanceTaskStatus
{
    /// <summary>No task: scheduled maintenance is off.</summary>
    Off,
    /// <summary>The task exists and starts this Lycoris.exe.</summary>
    On,
    /// <summary>The task starts a Lycoris.exe that no longer exists — Windows reports an error, nothing runs.</summary>
    ExeMissing,
    /// <summary>The task starts another copy of Lycoris (the program was moved or a second copy exists).</summary>
    OtherCopy,
    /// <summary>A task with this name that Lycoris did not create in this form (other program, other account or admin rights).</summary>
    Foreign,
    /// <summary>The Task Scheduler did not answer.</summary>
    Unknown,
}

/// <summary>The task as read from the Task Scheduler.</summary>
public sealed record MaintenanceTaskInfo(MaintenanceTaskStatus Status, MaintenanceTaskSnapshot? Snapshot, string? Detail = null)
{
    public bool NeedsFix => Status is MaintenanceTaskStatus.ExeMissing or MaintenanceTaskStatus.OtherCopy;
}

/// <summary>The Task Scheduler side of the maintenance task (replaced in unit tests: nothing real is registered there).</summary>
public interface IMaintenanceTaskStore
{
    /// <summary>The task's XML, or null when there is no such task. Throws when the scheduler does not answer.</summary>
    string? ReadXml();
    void Register(string xml);
    /// <summary>Deletes the task; no task = nothing to do.</summary>
    void Delete();
}

/// <summary>
/// «Обслуживание по расписанию»: one Windows Task Scheduler task, «\Lycoris Scheduled Maintenance», that starts
/// «Lycoris.exe --maintain» once a week. SECURITY: the task always runs as the desktop user with normal rights
/// (LeastPrivilege + InteractiveToken) — never with highest privileges, because Lycoris.exe usually lies in a folder the user
/// can write, and a task with admin rights would let any program of the user swap the exe and get those rights. The task
/// therefore does only what needs no admin rights (the user's own temp files and caches, a read-only health check).
/// Creating, changing and removing the task goes through <see cref="ChangeSession.SetMaintenanceTask"/> (journal + undo).
/// </summary>
public static class ScheduledMaintenanceTask
{
    public const string TaskName = "Lycoris Scheduled Maintenance";
    public const string TaskPath = @"\" + TaskName;
    public const string Argument = "--maintain";
    public const string Author = "Lycoris";
    /// <summary>The journal source of the task records.</summary>
    public const string Source = "maintenance";
    public const string Target = "task:" + TaskPath;

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static IMaintenanceTaskStore? _override;
    private static readonly IMaintenanceTaskStore Real = new SchedulerStore();

    public static IMaintenanceTaskStore Store => _override ?? Real;

    /// <summary>TESTS ONLY: a fake scheduler for the duration of the returned scope.</summary>
    internal static IDisposable OverrideForTests(IMaintenanceTaskStore store)
    {
        _override = store;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => _override = null;
    }

    /// <summary>An absolute local path of an .exe, printable and short — the only exe paths a task may name.</summary>
    public static bool IsPlausibleExe(string? path) =>
        path is { Length: > 7 and <= 260 } && !path.Any(char.IsControl) && path.IndexOfAny(['"', '<', '>', '|', '*', '?']) < 0
        && Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal)
        && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>SID of the account this process runs as.</summary>
    public static string? CurrentUserSid()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The next calendar date on or after today that falls on <paramref name="day"/> (the trigger's first run).</summary>
    internal static DateTime StartBoundary(MaintenanceSchedule s, DateTime today)
    {
        var date = today.Date;
        while (date.DayOfWeek != s.Day) date = date.AddDays(1);
        return date.AddHours(s.Hour).AddMinutes(s.Minute);
    }

    /// <summary>
    /// The task definition. Fixed in code: weekly trigger, «run as soon as possible after a missed start», not on battery,
    /// at most 30 minutes, below-normal priority, one instance, normal rights as <see cref="MaintenanceTaskSnapshot.UserSid"/>.
    /// </summary>
    public static string BuildXml(MaintenanceTaskSnapshot s, DateTime? today = null)
    {
        if (!s.Exists) throw new InvalidOperationException(Loc.T("Задача обслуживания не создана: нет расписания."));
        if (s.Problem() is { } problem) throw new InvalidOperationException(Loc.Tf("Задача обслуживания не создана: {0}.", problem));
        var schedule = s.Schedule!;
        var start = StartBoundary(schedule, today ?? DateTime.Now);
        var doc = new XDocument(
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Author", Author),
                    new XElement(Ns + "Description", Loc.T("Lycoris: еженедельная очистка временных файлов и кэшей с обычными правами и проверка состояния ПК (только чтение). Отключается в Lycoris → Обслуживание.")),
                    new XElement(Ns + "URI", TaskPath)),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "CalendarTrigger",
                        new XElement(Ns + "StartBoundary", start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "ScheduleByWeek",
                            new XElement(Ns + "DaysOfWeek", new XElement(Ns + schedule.Day.ToString())),
                            new XElement(Ns + "WeeksInterval", "1")))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", s.UserSid),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "LeastPrivilege"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "true"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "true"),
                    new XElement(Ns + "AllowHardTerminate", "true"),
                    new XElement(Ns + "StartWhenAvailable", "true"),
                    new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(Ns + "IdleSettings",
                        new XElement(Ns + "StopOnIdleEnd", "false"),
                        new XElement(Ns + "RestartOnIdle", "false")),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Hidden", "false"),
                    new XElement(Ns + "RunOnlyIfIdle", "false"),
                    new XElement(Ns + "WakeToRun", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT30M"),
                    new XElement(Ns + "Priority", "7")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", s.ExePath),
                        new XElement(Ns + "Arguments", Argument)))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Reads a task definition back. Null when the XML is not a task Lycoris builds: other action, admin rights, another
    /// logon type, several triggers or actions — such a task is reported as <see cref="MaintenanceTaskStatus.Foreign"/>.
    /// </summary>
    public static MaintenanceTaskSnapshot? Parse(string xml)
    {
        try
        {
            if (xml.Length > 64 * 1024) return null;
            var doc = SafeXml.Parse(xml);
            var root = doc.Root;
            if (root is null || root.Name.LocalName != "Task") return null;
            XElement? One(XElement? parent, string name)
            {
                var list = parent?.Elements().Where(e => e.Name.LocalName == name).ToList();
                return list is { Count: 1 } ? list[0] : null;
            }
            string? Text(XElement? parent, string name) => One(parent, name)?.Value.Trim();

            if (Text(One(root, "RegistrationInfo"), "Author") != Author) return null;
            var actions = One(root, "Actions");
            if (actions is null || actions.Elements().Count() != 1) return null;
            var exec = One(actions, "Exec");
            var exe = Text(exec, "Command")?.Trim('"');
            if (Text(exec, "Arguments") != Argument || !IsPlausibleExe(exe)) return null;

            var principal = One(One(root, "Principals"), "Principal");
            var sid = Text(principal, "UserId");
            var runLevel = Text(principal, "RunLevel");
            if (runLevel is not (null or "LeastPrivilege") || Text(principal, "LogonType") != "InteractiveToken") return null;
            if (sid is null || !UserScope.SidPattern.IsMatch(sid)) return null;

            var triggers = One(root, "Triggers");
            if (triggers is null || triggers.Elements().Count() != 1) return null;
            var calendar = One(triggers, "CalendarTrigger");
            var days = One(One(calendar, "ScheduleByWeek"), "DaysOfWeek")?.Elements().ToList();
            if (days is not { Count: 1 } || !Enum.TryParse<DayOfWeek>(days[0].Name.LocalName, out var day) || !Enum.IsDefined(day)) return null;
            if (!DateTime.TryParse(Text(calendar, "StartBoundary"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) return null;

            var snapshot = new MaintenanceTaskSnapshot
            {
                Exists = true, Day = day, Hour = start.Hour, Minute = start.Minute, ExePath = exe, UserSid = sid,
            };
            return snapshot.Problem() is null ? snapshot : null;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The task now, compared with this Lycoris.exe. Read-only; blocking (COM) — call off the UI thread.
    /// <paramref name="fileExists"/> is replaced in tests.
    /// </summary>
    public static MaintenanceTaskInfo Inspect(string currentExe, Func<string, bool>? fileExists = null)
    {
        string? xml;
        try { xml = Store.ReadXml(); }
        catch (Exception ex)
        {
            Log.Warn("Обслуживание по расписанию: планировщик не ответил — {0}", ex.Message);
            return new MaintenanceTaskInfo(MaintenanceTaskStatus.Unknown, null, ex.Message);
        }
        if (xml is null) return new MaintenanceTaskInfo(MaintenanceTaskStatus.Off, MaintenanceTaskSnapshot.Absent);
        if (Parse(xml) is not { } s) return new MaintenanceTaskInfo(MaintenanceTaskStatus.Foreign, null);
        if (s.UserSid is { } sid && CurrentUserSid() is { } me && !string.Equals(sid, me, StringComparison.OrdinalIgnoreCase))
            return new MaintenanceTaskInfo(MaintenanceTaskStatus.Foreign, s);
        fileExists ??= File.Exists;
        if (!fileExists(s.ExePath!)) return new MaintenanceTaskInfo(MaintenanceTaskStatus.ExeMissing, s);
        if (!SamePath(s.ExePath!, currentExe)) return new MaintenanceTaskInfo(MaintenanceTaskStatus.OtherCopy, s);
        return new MaintenanceTaskInfo(MaintenanceTaskStatus.On, s);
    }

    internal static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// The state the journal records as the original before a change: absent, or Lycoris's own task. A task with this
    /// name in any other form is refused — Lycoris neither replaces nor deletes what it cannot put back exactly.
    /// </summary>
    public static MaintenanceTaskSnapshot ReadSnapshot()
    {
        var xml = Store.ReadXml();
        if (xml is null) return MaintenanceTaskSnapshot.Absent;
        return Parse(xml) ?? throw new InvalidOperationException(
            Loc.Tf("задача «{0}» создана не Lycoris или изменена вручную — Lycoris её не трогает. Удалите её в планировщике заданий Windows", TaskName));
    }

    /// <summary>Makes the scheduler match <paramref name="state"/> (create / replace / delete). Used by the session and by undo.</summary>
    public static void Apply(MaintenanceTaskSnapshot state)
    {
        SafetyLock.ThrowIfLocked(Loc.T("изменение задачи планировщика"));
        if (state.Problem() is { } problem) throw new InvalidOperationException(problem);
        if (state.Exists) Store.Register(BuildXml(state));
        else Store.Delete();
    }

    /// <summary>The real Task Scheduler (COM, root folder).</summary>
    private sealed class SchedulerStore : IMaintenanceTaskStore
    {
        public string? ReadXml() => ScheduledTasks.ReadXml(TaskPath);
        public void Register(string xml) => ScheduledTasks.RegisterXml(TaskPath, xml);
        public void Delete() => ScheduledTasks.Delete(TaskPath);
    }
}
