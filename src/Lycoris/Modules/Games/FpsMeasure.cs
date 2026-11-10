using System.Globalization;
using System.IO;

namespace Lycoris.Modules.Games;

/// <summary>What one FPS capture measured. Frame times come from PresentMon's MsBetweenPresents (time between two Present() calls).</summary>
/// <param name="Frames">Frames that went into the numbers.</param>
/// <param name="Seconds">Sum of their frame times, in seconds (the real length of the sample).</param>
/// <param name="AvgFps">Frames per second over the sample: frames / time.</param>
/// <param name="Low1Fps">«1% low»: the FPS of the 99th-percentile frame time (99 % of frames were at least this fast).</param>
/// <param name="AvgFrameMs">Average frame time, ms.</param>
/// <param name="P99FrameMs">99th-percentile frame time, ms.</param>
public sealed record FpsStats(int Frames, double Seconds, double AvgFps, double Low1Fps, double AvgFrameMs, double P99FrameMs);

/// <summary>A saved capture: «до» or «после», which game, when, how long.</summary>
public sealed record FpsRecord
{
    public const string BeforeKind = "before";
    public const string AfterKind = "after";

    public required string Kind { get; init; }
    /// <summary>The captured process, «game.exe».</summary>
    public required string Process { get; init; }
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>The requested capture length (15 / 30 / 60 s).</summary>
    public int RequestedSeconds { get; init; }
    public required FpsStats Stats { get; init; }
}

/// <summary>One row of the comparison: value before, after, change in percent, and whether the change is real or noise.</summary>
public sealed record FpsDelta(string Label, double Before, double After, double Percent, bool HigherIsBetter, string Unit)
{
    /// <summary>Changes inside ±3 % are within run-to-run noise of a game scene — said honestly, not counted as a gain.</summary>
    public const double NoisePercent = 3.0;
    public bool IsNoise => Math.Abs(Percent) < NoisePercent;
    public bool IsBetter => !IsNoise && (HigherIsBetter ? Percent > 0 : Percent < 0);
    public bool IsWorse => !IsNoise && !IsBetter;
}

public sealed record FpsComparison(IReadOnlyList<FpsDelta> Rows, string? Warning);

/// <summary>Pure math and parsing for the FPS capture — no IO, no processes; unit-tested.</summary>
public static class FpsMeasure
{
    /// <summary>Fewer frames than this are not a measurement (the game was minimised, paused or not drawing).</summary>
    public const int MinFrames = 30;
    /// <summary>Frame times outside (0, 5000] ms are glitches (first frame, loading stall, clock jump) and are skipped.</summary>
    public const double MaxFrameMs = 5000;
    /// <summary>Upper bound of rows read from a CSV (60 s at 2000 FPS fits; a bigger file is cut, never read whole).</summary>
    public const int MaxRows = 200_000;

    public static readonly int[] DurationChoices = [15, 30, 60];

    /// <summary>Statistics of frame times in ms; null when there are fewer than <see cref="MinFrames"/> usable frames.</summary>
    public static FpsStats? Compute(IEnumerable<double> frameMs)
    {
        var ms = frameMs.Where(v => double.IsFinite(v) && v > 0 && v <= MaxFrameMs).ToArray();
        if (ms.Length < MinFrames) return null;
        var total = ms.Sum();
        var sorted = (double[])ms.Clone();
        Array.Sort(sorted);
        // Nearest-rank percentile: the smallest value with at least 99 % of the samples at or below it.
        var rank = (int)Math.Ceiling(0.99 * sorted.Length) - 1;
        var p99 = sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        return new FpsStats(ms.Length, total / 1000.0, ms.Length * 1000.0 / total, 1000.0 / p99, total / ms.Length, p99);
    }

    /// <summary>
    /// Reads PresentMon's CSV (v1 or v2 metrics: both have Application, ProcessID, SwapChainAddress, MsBetweenPresents) and keeps
    /// the swap chain with the most frames — a game may own a second, idle one (launcher overlay, a video). Lines starting with
    /// «//» (PresentMon's error notes) and malformed rows are skipped. Null when the header lacks MsBetweenPresents.
    /// </summary>
    public static FpsStats? ParseCsv(TextReader reader)
    {
        var header = reader.ReadLine();
        if (header is null) return null;
        var cols = header.Split(',').Select(c => c.Trim().Trim('"')).ToArray();
        var iMs = Array.FindIndex(cols, c => c.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
        if (iMs < 0) return null;
        var iApp = Array.FindIndex(cols, c => c.Equals("Application", StringComparison.OrdinalIgnoreCase));
        var iPid = Array.FindIndex(cols, c => c.Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
        var iChain = Array.FindIndex(cols, c => c.Equals("SwapChainAddress", StringComparison.OrdinalIgnoreCase));

        var chains = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var rows = 0;
        while (reader.ReadLine() is { } line && rows < MaxRows)
        {
            rows++;
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var f = line.Split(',');
            if (f.Length <= iMs) continue;
            if (!double.TryParse(f[iMs].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
            string Field(int i) => i >= 0 && i < f.Length ? f[i].Trim() : "";
            var key = Field(iApp) + "|" + Field(iPid) + "|" + Field(iChain);
            if (!chains.TryGetValue(key, out var list)) chains[key] = list = [];
            list.Add(v);
        }
        var best = chains.Values.OrderByDescending(l => l.Count).FirstOrDefault();
        return best is null ? null : Compute(best);
    }

    /// <summary>A process name PresentMon may be pointed at: «name» or «name.exe», a plain file name. Returns «name.exe» or null.</summary>
    public static string? NormalizeProcess(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0 || n.Length > 100) return null;
        if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
        // Letters (any script), digits and a few safe signs only: no paths, quotes, switches or wildcards.
        if (n[0] == '-' || n[0] == '.' || !n.All(c => char.IsLetterOrDigit(c) || c is '.' || c is '_' || c is '-' || c is ' ' || c is '+' || c is '(' || c is ')'))
            return null;
        return n;
    }

    /// <summary>The PresentMon console command line (each item one argument; no shell involved).</summary>
    public static IReadOnlyList<string> BuildArguments(string processExe, int seconds, string outputCsv) =>
    [
        "--process_name", processExe,
        "--output_file", outputCsv,
        "--timed", seconds.ToString(CultureInfo.InvariantCulture),
        "--terminate_after_timed",
        "--no_console_stats",
        "--session_name", "LycorisFps",
        "--stop_existing_session",
    ];

    /// <summary>Before → after for the three numbers; a warning when the two captures are not of the same thing.</summary>
    public static FpsComparison Compare(FpsRecord before, FpsRecord after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        static double Pct(double a, double b) => a > 0 ? (b - a) / a * 100.0 : 0;
        var (a, b) = (before.Stats, after.Stats);
        var rows = new List<FpsDelta>
        {
            new(Loc.T("СРЕДНИЙ FPS"), a.AvgFps, b.AvgFps, Pct(a.AvgFps, b.AvgFps), true, ""),
            new(Loc.T("1% LOW"), a.Low1Fps, b.Low1Fps, Pct(a.Low1Fps, b.Low1Fps), true, ""),
            new(Loc.T("FRAMETIME"), a.AvgFrameMs, b.AvgFrameMs, Pct(a.AvgFrameMs, b.AvgFrameMs), false, Loc.T("мс")),
        };
        string? warning = null;
        if (!string.Equals(before.Process, after.Process, StringComparison.OrdinalIgnoreCase))
            warning = Loc.Tf("Замеры сделаны в разных программах ({0} и {1}) — сравнение не имеет смысла.", before.Process, after.Process);
        else if (before.RequestedSeconds != after.RequestedSeconds)
            warning = Loc.T("Замеры разной длительности — сравнивайте замеры одной длины.");
        return new FpsComparison(rows, warning);
    }

    /// <summary>«123» for FPS, «8.1» for ms; the change as «+4.2 %».</summary>
    public static string FormatValue(double v, string unit) =>
        unit.Length == 0 ? v.ToString("0", Loc.Culture) : v.ToString("0.0", Loc.Culture) + " " + unit;

    public static string FormatPercent(double p) =>
        (p >= 0 ? "+" : "−") + Math.Abs(p).ToString("0.0", Loc.Culture) + " %";

    /// <summary>One line for the PC report and the log: «до 98 FPS (1% low 61) → после 104 FPS (1% low 70), game.exe».</summary>
    public static string Summary(FpsRecord? before, FpsRecord? after)
    {
        string One(FpsRecord r) => Loc.Tf("{0} FPS (1% low {1})", r.Stats.AvgFps.ToString("0", Loc.Culture), r.Stats.Low1Fps.ToString("0", Loc.Culture));
        if (before is not null && after is not null)
            return Loc.Tf("FPS в {0}: до — {1}, после — {2}", after.Process, One(before), One(after));
        if (before is not null) return Loc.Tf("FPS в {0}: до — {1}, замера «после» ещё нет", before.Process, One(before));
        if (after is not null) return Loc.Tf("FPS в {0}: после — {1}", after.Process, One(after));
        return "";
    }
}
