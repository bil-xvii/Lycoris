using System.Runtime.InteropServices;

namespace Monolith.Core;

public sealed record ScheduledTaskInfo(string Path, string Name, bool Exists, bool Enabled);

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
        catch
        {
            return new ScheduledTaskInfo(taskPath, name, false, false);
        }
        finally
        {
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
    }

    public static void SetEnabled(string taskPath, bool enabled)
    {
        Elevation.Demand("изменение задач планировщика");
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

    private static object CreateService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Планировщик заданий недоступен.");
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
