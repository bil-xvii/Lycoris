using Lycoris.Core.Measurements;

namespace Lycoris.Modules.Home;

/// <summary>
/// QA only («--demo-measurements», honoured only together with --snapshot): an in-memory measurement history so the
/// filled «Замер до / после» card can be looked at on a screenshot. Nothing is read from or written to measurements.json.
/// </summary>
public static class MeasurementDemo
{
    public const string Switch = "--demo-measurements";

    /// <summary>The demo history for this run, or null when the switch is absent or this is not a --snapshot run.</summary>
    public static IReadOnlyList<Measurement>? Resolve(bool snapshot, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return snapshot && args.Any(a => a.Equals(Switch, StringComparison.OrdinalIgnoreCase)) ? Build(DateTimeOffset.Now) : null;
    }

    /// <summary>
    /// Six measurements, oldest first: the first row, a «within noise» row, a clear difference after applied changes,
    /// an incomparable row (Windows build changed), noise with a same boot time, and a difference beyond noise.
    /// </summary>
    public static IReadOnlyList<Measurement> Build(DateTimeOffset now)
    {
        const long gb = 1024L * 1024 * 1024;
        static Measurement Row(DateTimeOffset at, string build, int startup, long freeBytes, double boot, int? applied, int? reverted) => new()
        {
            RecordedAt = at,
            ChangesApplied = applied,
            ChangesReverted = reverted,
            Conditions = new MeasurementConditions
            {
                WindowsVersion = "Windows 11 Pro", WindowsBuild = build, LycorisVersion = "1.0.23.0",
                EnabledStartupItems = startup, FreeMemoryBytes = freeBytes, BootTimeSeconds = boot,
                BootTimeSource = "Microsoft-Windows-Diagnostics-Performance/Operational / ID 100 BootDuration",
            }
        };
        return
        [
            Row(now.AddDays(-6), "26100", 14, 6 * gb, 42.0, null, null),
            Row(now.AddDays(-5), "26100", 14, 6 * gb + 100L * 1024 * 1024, 41.0, 0, 0),
            Row(now.AddDays(-4), "26100", 9, 7 * gb + 400L * 1024 * 1024, 33.5, 6, 0),
            Row(now.AddDays(-3), "26200", 9, 7 * gb + 300L * 1024 * 1024, 34.0, 0, 0),
            Row(now.AddDays(-2), "26200", 9, 7 * gb + 350L * 1024 * 1024, 34.0, 2, 1),
            Row(now.AddHours(-1), "26200", 8, 7 * gb + 900L * 1024 * 1024, 31.0, 1, 0),
        ];
    }
}
