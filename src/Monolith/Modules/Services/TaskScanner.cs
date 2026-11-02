using System.Runtime.InteropServices;
using Monolith.Core;

namespace Monolith.Modules.Services;

/// <summary>A curated task resolved to a concrete path on this PC.</summary>
public sealed record ResolvedTask(CuratedTask Entry, string Path, bool Exists, bool Enabled);

/// <summary>Read-only lookup of the curated scheduled tasks (Task Scheduler COM API, locale-independent).</summary>
public static class TaskScanner
{
    public static List<ResolvedTask> Read(IEnumerable<CuratedTask> entries)
    {
        var list = new List<ResolvedTask>();
        foreach (var e in entries)
        {
            if (e.IsPrefix)
            {
                var (folder, prefix) = Split(e.Path);
                var found = FindByPrefix(folder, prefix);
                if (found.Count == 0)
                {
                    list.Add(new ResolvedTask(e, e.Path, false, false));
                    continue;
                }
                foreach (var path in found)
                {
                    var info = ScheduledTasks.Query(path);
                    list.Add(new ResolvedTask(e, path, info.Exists, info.Enabled));
                }
            }
            else
            {
                var info = ScheduledTasks.Query(e.Path);
                list.Add(new ResolvedTask(e, e.Path, info.Exists, info.Enabled));
            }
        }
        return list;
    }

    /// <summary>Tasks in <paramref name="folder"/> whose name starts with <paramref name="prefix"/> (hidden tasks included).</summary>
    public static List<string> FindByPrefix(string folder, string prefix)
    {
        var result = new List<string>();
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return result;
            service = Activator.CreateInstance(type);
            if (service is null) return result;
            dynamic svc = service;
            svc.Connect();
            dynamic f = svc.GetFolder(folder);
            dynamic tasks = f.GetTasks(1); // TASK_ENUM_HIDDEN
            int count = tasks.Count;
            for (var i = 1; i <= count; i++)
            {
                dynamic t = tasks[i];
                string name = t.Name;
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(folder == "\\" ? "\\" + name : folder + "\\" + name);
            }
        }
        catch (Exception ex)
        {
            // Root-folder tasks are not visible without admin rights; that's expected.
            if (Elevation.IsElevated) Log.Warn("Задачи: не удалось перечислить {0} — {1}", folder, ex.Message);
        }
        finally
        {
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
        return result;
    }

    public static (string Folder, string Name) Split(string taskPath)
    {
        var p = taskPath.Replace('/', '\\');
        if (!p.StartsWith('\\')) p = "\\" + p;
        var i = p.LastIndexOf('\\');
        return (i <= 0 ? "\\" : p[..i], p[(i + 1)..]);
    }
}
