using System.Runtime.InteropServices;

namespace Lycoris.Core;

/// <summary>Unreadable: the scheduler did not answer (error other than «no such task») — Exists = false then says nothing about the task.</summary>
public sealed record ScheduledTaskInfo(string Path, string Name, bool Exists, bool Enabled, bool Unreadable = false);

/// <summary>Windows Task Scheduler through its COM API (locale-independent, unlike schtasks output).</summary>
public static class ScheduledTasks
{
    public static ScheduledTaskInfo Query(string taskPath)
    {
        var (folder, name) = Split(taskPath);
        object? service = null;
        try
        {
            service = CreateService();
            dynamic svc = service;
            dynamic f = svc.GetFolder(folder);
            dynamic task = f.GetTask(name);
            bool enabled = task.Enabled;
            return new ScheduledTaskInfo(taskPath, name, true, enabled);
        }
        catch (Exception ex)
        {
            return new ScheduledTaskInfo(taskPath, name, false, false, Unreadable: !IsNotFound(ex));
        }
        finally
        {
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>
    /// ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND from the Task Scheduler: the task (or its folder) really is not there.
    /// Through late-bound COM the runtime turns these HRESULTs into FileNotFoundException / DirectoryNotFoundException.
    /// </summary>
    internal static bool IsNotFound(Exception ex) =>
        ex is COMException or System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException
        && ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);

    public static void SetEnabled(string taskPath, bool enabled)
    {
        SafetyLock.ThrowIfLocked(Loc.T("изменение задачи планировщика"));
        Elevation.Demand(Loc.T("изменение задач планировщика"));
        var (folder, name) = Split(taskPath);
        object? service = null;
        try
        {
            service = CreateService();
            dynamic svc = service;
            dynamic f = svc.GetFolder(folder);
            dynamic task = f.GetTask(name);
            task.Enabled = enabled;
        }
        finally
        {
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>The task's XML definition; null when there is no such task. Throws when the scheduler does not answer.</summary>
    public static string? ReadXml(string taskPath)
    {
        var (folder, name) = Split(taskPath);
        object? service = null;
        try
        {
            service = CreateService();
            dynamic svc = service;
            dynamic f = svc.GetFolder(folder);
            dynamic task = f.GetTask(name);
            return (string)task.Xml;
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
        finally
        {
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>
    /// Creates or replaces a task from its XML (TASK_CREATE_OR_UPDATE). The principal comes from the XML; the logon type is
    /// InteractiveToken: the task runs only while that user is signed in, with no stored password.
    /// </summary>
    public static void RegisterXml(string taskPath, string xml)
    {
        SafetyLock.ThrowIfLocked(Loc.T("изменение задачи планировщика"));
        Elevation.Demand(Loc.T("изменение задач планировщика"));
        var (folder, name) = Split(taskPath);
        object? service = null;
        try
        {
            service = CreateService();
            dynamic svc = service;
            dynamic f = svc.GetFolder(folder);
            const int CreateOrUpdate = 6, InteractiveToken = 3;
            f.RegisterTask(name, xml, CreateOrUpdate, null, null, InteractiveToken, null);
        }
        finally
        {
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
    }

    /// <summary>Deletes a task; a task that is not there is fine.</summary>
    public static void Delete(string taskPath)
    {
        SafetyLock.ThrowIfLocked(Loc.T("изменение задачи планировщика"));
        Elevation.Demand(Loc.T("изменение задач планировщика"));
        var (folder, name) = Split(taskPath);
        object? service = null;
        try
        {
            service = CreateService();
            dynamic svc = service;
            dynamic f = svc.GetFolder(folder);
            f.DeleteTask(name, 0);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            // already gone
        }
        finally
        {
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
    }

    private static object CreateService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException(Loc.T("Планировщик заданий недоступен."));
        var service = Activator.CreateInstance(type)!;
        ((dynamic)service).Connect();
        return service;
    }

    private static (string Folder, string Name) Split(string taskPath)
    {
        var p = taskPath.Replace('/', '\\');
        if (!p.StartsWith('\\')) p = "\\" + p;
        var i = p.LastIndexOf('\\');
        var folder = i <= 0 ? "\\" : p[..i];
        return (folder, p[(i + 1)..]);
    }
}
