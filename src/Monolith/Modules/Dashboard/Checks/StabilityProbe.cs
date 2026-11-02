using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace Monolith.Modules.Dashboard.Checks;

/// <summary>
/// Reads crash and hardware-error events of the last 30 days from the System log (readable without administrator
/// rights): Kernel-Power 41, WER-SystemErrorReporting 1001, EventLog 6008, Display 4101 and WHEA-Logger.
/// Each query is filtered by provider, id and time on the event log service side and capped, so a WHEA flood cannot
/// stall the check. Read-only: logs are never cleared or changed. Any failed query makes the whole result unknown
/// (null), so a partial read can never turn into a false "no crashes".
/// </summary>
internal static class StabilityProbe
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);
    /// <summary>Newest records kept per query.</summary>
    private const int MaxRecordsPerQuery = 400;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    public static StabilityLog? Read(DateTime now)
    {
        var clock = Stopwatch.StartNew();
        var events = new List<StabilityEvent>();
        var truncated = false;
        var ms = (long)Window.TotalMilliseconds;
        string Query(string provider, string ids) =>
            $"*[System[Provider[@Name='{provider}']{ids} and TimeCreated[timediff(@SystemTime) <= {ms}]]]";

        using var kp41 = new EventLogPropertySelector([
            "Event/EventData/Data[@Name='BugcheckCode']",
            "Event/EventData/Data[@Name='PowerButtonTimestamp']",
        ]);
        using var wer = new EventLogPropertySelector(["Event/EventData/Data[@Name='param1']"]);

        var ok = ReadAll(Query("Microsoft-Windows-Kernel-Power", " and (EventID=41)"), kp41, (r, p) =>
                     new StabilityEvent(Time(r), StabilityEventKind.KernelPower41, r.Id, Level(r),
                         HealthRules.NormalizeBugCheck(ToUInt(p?.ElementAtOrDefault(0))), ToULong(p?.ElementAtOrDefault(1)) is > 0))
                 && ReadAll(Query("Microsoft-Windows-WER-SystemErrorReporting", " and (EventID=1001)"), wer, (r, p) =>
                     new StabilityEvent(Time(r), StabilityEventKind.BugCheckReport, r.Id, Level(r),
                         HealthRules.ParseWerBugCheck(p?.ElementAtOrDefault(0)?.ToString())))
                 && ReadAll(Query("EventLog", " and (EventID=6008)"), null, (r, _) =>
                     new StabilityEvent(Time(r), StabilityEventKind.UnexpectedShutdown, r.Id, Level(r)))
                 && ReadAll(Query("Display", " and (EventID=4101)"), null, (r, _) =>
                     new StabilityEvent(Time(r), StabilityEventKind.GpuReset, r.Id, Level(r)))
                 && ReadAll(Query("Microsoft-Windows-WHEA-Logger", ""), null, (r, _) =>
                     new StabilityEvent(Time(r), StabilityEventKind.Whea, r.Id, Level(r)));
        if (!ok) return null;

        return new StabilityLog(now - Window, ReadOldest(), events, truncated);

        bool ReadAll(string xpath, EventLogPropertySelector? selector, Func<EventLogRecord, IList<object>?, StabilityEvent> map)
        {
            if (clock.Elapsed > Budget) return false;
            try
            {
                using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true });
                for (var n = 0; ; n++)
                {
                    using var record = reader.ReadEvent(ReadTimeout);
                    if (record is null) return true;
                    if (n >= MaxRecordsPerQuery || clock.Elapsed > Budget)
                    {
                        truncated = true;
                        return true;
                    }
                    if (record is not EventLogRecord r || r.TimeCreated is null) continue;
                    IList<object>? values = null;
                    if (selector is not null)
                    {
                        try { values = r.GetPropertyValues(selector); }
                        catch (EventLogException) { }
                    }
                    events.Add(map(r, values));
                }
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
            {
                Monolith.Core.Log.Warn("Журнал событий: {0}", ex.Message);
                return false;
            }
        }
    }

    /// <summary>Time of the oldest event the System log still holds (the default 20 MB log can roll over in under 30 days).</summary>
    private static DateTime? ReadOldest()
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, "*") { ReverseDirection = false });
            using var first = reader.ReadEvent(ReadTimeout);
            return first?.TimeCreated;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static DateTime Time(EventRecord r) => r.TimeCreated ?? DateTime.MinValue;
    private static int Level(EventRecord r) => r.Level ?? 0;

    private static uint ToUInt(object? o)
    {
        try { return o is null ? 0 : Convert.ToUInt32(o); }
        catch { return 0; }
    }

    private static ulong ToULong(object? o)
    {
        try { return o is null ? 0 : Convert.ToUInt64(o); }
        catch { return 0; }
    }
}
