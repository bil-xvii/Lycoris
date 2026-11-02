using System.IO;
using System.Runtime.InteropServices;
using Lycoris.Core;

namespace Lycoris.Modules.Startup;

/// <summary>A scheduled task that starts when a user signs in or when Windows boots.</summary>
public sealed record LogonTask
{
    /// <summary>Full task path, e.g. <c>\Vendor\Updater</c>.</summary>
    public required string Path { get; init; }
    public required string Name { get; init; }
    /// <summary>Folder of the task, <c>\</c> for the root.</summary>
    public required string Folder { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public bool AtLogon { get; init; }
    public bool AtBoot { get; init; }
    /// <summary>Account whose sign-in starts the task (null = any user).</summary>
    public string? LogonUser { get; init; }
    /// <summary>Account the task runs as.</summary>
    public string? RunAs { get; init; }
    public bool RunsElevated { get; init; }
    /// <summary>Enabled triggers that are neither logon nor boot (schedules, idle…): disabling the task stops them too.</summary>
    public int OtherTriggers { get; init; }
    /// <summary>First action as a command line, or a description of a non-exec action.</summary>
    public required string Command { get; init; }
    public int ActionCount { get; init; }
    public string? TargetPath { get; init; }
    public bool TargetExists { get; init; }
    public string? Publisher { get; init; }
    public DateTime? LastRun { get; init; }
    public StartupRisk Risk { get; init; }
}

/// <summary>
/// Enumerates Task Scheduler (COM, locale-independent) for tasks with an enabled logon (9) or boot (8) trigger.
/// Windows' own tasks (<c>\Microsoft\…</c>, Microsoft-authored, Edge updater) are skipped. Read-only.
/// </summary>
public static class LogonTaskScanner
{
    private const int TriggerBoot = 8;
    private const int TriggerLogon = 9;
    private const int ActionExec = 0;
    private const int ActionComHandler = 5;
    private const int EnumHidden = 1;
    private const int RunLevelHighest = 1;

    public static List<LogonTask> Scan(bool includeSystem = false)
    {
        var result = new List<LogonTask>();
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException(Loc.T("Планировщик заданий недоступен."));
        object? service = null;
        try
        {
            service = Activator.CreateInstance(type) ?? throw new InvalidOperationException(Loc.T("Планировщик заданий недоступен."));
            dynamic svc = service;
            svc.Connect();
            object root = svc.GetFolder("\\");
            Walk(root, result, includeSystem, 0);
        }
        finally
        {
            if (service is not null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    private static void Walk(object comFolder, List<LogonTask> result, bool includeSystem, int depth)
    {
        dynamic folder = comFolder;
        string folderPath = folder.Path;
        if (!includeSystem && (folderPath.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase) ||
                               folderPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)))
            return;

        try
        {
            dynamic tasks = folder.GetTasks(EnumHidden);
            int count = tasks.Count;
            for (var i = 1; i <= count; i++)
            {
                try
                {
                    object item = tasks[i];
                    var task = Read(item, folderPath);
                    if (task is not null && (includeSystem || !StartupClassifier.IsSystemTask(task.Path, task.Author)))
                        result.Add(task);
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidCastException
                                               or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // One unreadable task (broken XML, no access) must not hide the rest.
                }
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Log.Warn("Автозапуск: нет доступа к папке планировщика {0}: {1}", folderPath, ex.Message);
        }

        if (depth >= 24) return;
        try
        {
            dynamic folders = folder.GetFolders(0);
            int n = folders.Count;
            for (var i = 1; i <= n; i++)
            {
                object sub = folders[i];
                Walk(sub, result, includeSystem, depth + 1);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Log.Warn("Автозапуск: не удалось перечислить вложенные папки {0}: {1}", folderPath, ex.Message);
        }
    }

    private static LogonTask? Read(object comTask, string folderPath)
    {
        dynamic task = comTask;
        dynamic def = task.Definition;

        bool atLogon = false, atBoot = false;
        string? logonUser = null;
        var other = 0;
        dynamic triggers = def.Triggers;
        int triggerCount = triggers.Count;
        for (var i = 1; i <= triggerCount; i++)
        {
            dynamic tr = triggers[i];
            int kind = tr.Type;
            bool on = tr.Enabled;
            if (!on) continue;
            if (kind == TriggerLogon)
            {
                atLogon = true;
                string? user = tr.UserId;
                if (!string.IsNullOrWhiteSpace(user)) logonUser = user;
            }
            else if (kind == TriggerBoot) atBoot = true;
            else other++;
        }
        if (!atLogon && !atBoot) return null;

        string path = task.Path;
        string name = task.Name;
        bool enabled = task.Enabled;

        dynamic reg = def.RegistrationInfo;
        string? author = reg.Author;
        string? description = reg.Description;

        string? runAs = null;
        var elevated = false;
        try
        {
            dynamic principal = def.Principal;
            string? userId = principal.UserId;
            string? groupId = principal.GroupId;
            runAs = !string.IsNullOrWhiteSpace(userId) ? userId : groupId;
            int level = principal.RunLevel;
            elevated = level == RunLevelHighest;
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }

        dynamic actions = def.Actions;
        int actionCount = actions.Count;
        var command = actionCount == 0 ? Loc.T("Нет действий") : "";
        string? target = null;
        for (var i = 1; i <= actionCount && target is null && command.Length == 0; i++)
        {
            dynamic action = actions[i];
            int actionType = action.Type;
            if (actionType == ActionExec)
            {
                string program = action.Path ?? "";
                string? args = action.Arguments;
                command = CommandLineParser.Join(program, args);
                target = CommandLineParser.Parse(command)?.Target;
            }
            else if (actionType == ActionComHandler)
            {
                string? clsid = action.ClassId;
                command = Loc.Tf("COM-обработчик {0}", clsid).Trim();
            }
            else
            {
                command = Loc.Tf("Действие типа {0}", actionType);
            }
        }
        if (command.Length == 0) command = Loc.T("Нет действий");

        target = StartupScanner.ResolveAppPath(target);
        var exists = target is null || FileProbe.FileExists(target);
        var (company, _) = exists ? StartupScanner.ReadVersion(target) : (null, null);

        DateTime? lastRun = null;
        try
        {
            DateTime lr = task.LastRunTime;
            if (lr.Year >= 2000) lastRun = lr;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { }

        return new LogonTask
        {
            Path = path,
            Name = name,
            Folder = folderPath,
            Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Enabled = enabled,
            AtLogon = atLogon,
            AtBoot = atBoot,
            LogonUser = logonUser,
            RunAs = runAs,
            RunsElevated = elevated,
            OtherTriggers = other,
            Command = command,
            ActionCount = actionCount,
            TargetPath = target,
            TargetExists = exists,
            Publisher = company,
            LastRun = lastRun,
            Risk = StartupClassifier.ClassifyTask(name, target, company),
        };
    }

    /// <summary>Current enabled state of a task (null when it no longer exists).</summary>
    public static bool? ReadEnabled(string taskPath)
    {
        var info = ScheduledTasks.Query(taskPath);
        return info.Exists ? info.Enabled : null;
    }

    internal static string FolderLabel(string folder) => folder is "\\" or "" ? Loc.T("Корневая папка") : folder;

    internal static string FileLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return System.IO.Path.GetFileName(path); }
        catch (ArgumentException) { return path; }
    }
}
