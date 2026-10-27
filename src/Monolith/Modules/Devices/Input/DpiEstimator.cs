using System.Globalization;

namespace Monolith.Modules.Devices.Input;

/// <summary>One drag over a known distance: the summed raw counts of the mouse while the pad was held.</summary>
internal readonly record struct DpiRun(long CountsX, long CountsY, double DistanceCm)
{
    /// <summary>The straight-line motion in counts: √((ΣdX)² + (ΣdY)²).</summary>
    public double Counts => Math.Sqrt((double)CountsX * CountsX + (double)CountsY * CountsY);

    /// <summary>Counts per inch over the distance.</summary>
    public double Dpi => DistanceCm > 0 ? Counts / (DistanceCm / 2.54) : 0;
}

/// <summary>The DPI of a mouse from three drags: the median, and how far the runs are from it.</summary>
internal sealed record DpiEstimate(double Dpi, double SpreadPercent, IReadOnlyList<double> Runs)
{
    /// <summary>The runs disagree too much to trust the number (the user did not move straight along the ruler).</summary>
    public bool Inconsistent => SpreadPercent > DpiEstimator.MaxSpreadPercent;
}

/// <summary>
/// DPI from raw counts over a known distance (research devices_mouse-keyboard.md §6): DPI = √((ΣdX)² + (ΣdY)²) / (cm / 2,54),
/// three runs, median ± spread. Pure.
/// </summary>
internal static class DpiEstimator
{
    /// <summary>A ruler: 10 cm.</summary>
    public const double RulerCm = 10;

    /// <summary>The long edge of a bank card (ISO/IEC 7810 ID-1): 85,6 mm.</summary>
    public const double CardCm = 8.56;

    public const int RunsNeeded = 3;

    /// <summary>
    /// A run below this is too short: even the lowest DPI of current mice (100) gives more counts over the distance, so
    /// the user moved only a part of it (or just clicked).
    /// </summary>
    public const double MinDpi = 100;

    /// <summary>Above every sensor on sale (44 000): the mouse travelled much further than the distance.</summary>
    public const double MaxDpi = 50000;

    /// <summary>Runs further than this from the median make the result «inconsistent».</summary>
    public const double MaxSpreadPercent = 10;

    /// <summary>Null when the run counts; otherwise why not (Russian, one line).</summary>
    public static string? RunProblem(DpiRun run) =>
        run.Dpi < MinDpi ? "Проход слишком короткий — проведите мышь на всё расстояние, не отрывая её от стола."
        : run.Dpi > MaxDpi ? "Проход слишком длинный — остановитесь у отметки и только потом отпустите кнопку."
        : null;

    public static bool IsValid(DpiRun run) => RunProblem(run) is null;

    /// <summary>The median of the valid runs and the largest deviation from it (± %); null without a valid run.</summary>
    public static DpiEstimate? Combine(IEnumerable<double> runDpis)
    {
        var runs = runDpis.Where(d => d is >= MinDpi and <= MaxDpi && double.IsFinite(d)).ToList();
        if (runs.Count == 0) return null;
        var sorted = runs.OrderBy(d => d).ToList();
        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        var spread = median > 0 ? runs.Max(d => Math.Abs(d - median)) / median * 100 : 0;
        return new DpiEstimate(median, spread, runs);
    }

    /// <summary>«≈ 1600 DPI (±3 %)».</summary>
    public static string ResultText(DpiEstimate e) =>
        $"{InputAdvice.DpiText(e.Dpi)} (±{Math.Max(1, Math.Round(e.SpreadPercent)).ToString("0", CultureInfo.InvariantCulture)} %)";

    /// <summary>«Проходы: 1592, 1601, 1610 DPI» — the raw runs for the detail line.</summary>
    public static string RunsText(DpiEstimate e) =>
        "Проходы: " + string.Join(", ", e.Runs.Select(r => Math.Round(r).ToString("0", CultureInfo.InvariantCulture))) + " DPI";
}
