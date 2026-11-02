using System.Globalization;

namespace Lycoris.Modules.Devices.Input;

/// <summary>One relative mouse report: arrival time (Stopwatch ticks) and motion counts.</summary>
internal readonly record struct MotionSample(long Ticks, int Dx, int Dy);

/// <summary>
/// The estimated polling rate of one mouse. <see cref="Hz"/> is the snapped rate (125 … 8000) or null (not enough motion,
/// or a rate that matches no standard one — then <see cref="RawHz"/> says what was seen).
/// </summary>
internal sealed record PollingEstimate(int? Hz, double RawHz, double Stability, double Jitter, bool Saturated, int Bins, int Reports)
{
    public bool EnoughMotion => Bins >= PollingRateEstimator.MinBins;
}

/// <summary>
/// The polling rate from report arrival times (research devices_mouse-keyboard.md §5): motion segments split at gaps over
/// 30 ms; 50-ms bins inside segments; rate = 90th percentile of the bin rates (slow parts of a circle send fewer reports,
/// two queued messages delivered back to back inflate a maximum); snapped to a standard rate within ±12 %. Pure.
/// </summary>
internal static class PollingRateEstimator
{
    public static readonly IReadOnlyList<int> StandardRates = [125, 250, 500, 1000, 2000, 4000, 8000];
    public const double GapMs = 30;
    public const double BinMs = 50;
    public const int MinBins = 20;
    public const double SnapTolerance = 0.12;
    public const double StableTolerance = 0.10;

    public static PollingEstimate Estimate(IReadOnlyList<MotionSample> samples, long frequency)
    {
        var reports = samples.Count;
        var saturated = reports > 0 && samples.Count(s => Math.Abs((long)s.Dx) + Math.Abs((long)s.Dy) <= 1) * 2 > reports;
        if (reports < 2 || frequency <= 0) return new PollingEstimate(null, 0, 0, 0, saturated, 0, reports);

        var t = samples.Select(s => s.Ticks).ToArray();
        Array.Sort(t);
        var gap = frequency * GapMs / 1000.0;
        var bin = frequency * BinMs / 1000.0;

        var rates = new List<double>();
        var intervals = new List<double>();
        var start = 0;
        for (var i = 1; i <= t.Length; i++)
        {
            if (i < t.Length && t[i] - t[i - 1] <= gap)
            {
                intervals.Add((t[i] - t[i - 1]) / (double)frequency);
                continue;
            }
            AddBins(t, start, i - 1, bin, frequency, rates);
            start = i;
        }

        if (rates.Count == 0) return new PollingEstimate(null, 0, 0, Jitter(intervals), saturated, 0, reports);
        rates.Sort();
        var raw = Percentile(rates, 0.90);
        int? snapped = null;
        foreach (var r in StandardRates)
        {
            if (Math.Abs(raw - r) / r > SnapTolerance) continue;
            if (snapped is not { } best || Math.Abs(raw - r) / r < Math.Abs(raw - best) / best) snapped = r;
        }
        var reference = snapped ?? raw;
        var stability = rates.Count(r => Math.Abs(r - reference) / reference <= StableTolerance) / (double)rates.Count;
        var hz = rates.Count >= MinBins ? snapped : null;
        return new PollingEstimate(hz, raw, stability, Jitter(intervals), saturated, rates.Count, reports);
    }

    /// <summary>
    /// The 50-ms bins that lie wholly inside one segment [first..last]. A bin's rate is (reports − 1) / (last − first
    /// arrival) inside it — exact at low rates, where counting reports per 50 ms is off by up to ±20 Hz; a bin whose reports
    /// cover less than half of it falls back to count / 50 ms (a burst of queued messages cannot fake a high rate).
    /// </summary>
    private static void AddBins(long[] t, int first, int last, double bin, long frequency, List<double> rates)
    {
        if (last <= first) return;
        var t0 = t[first];
        var end = t[last];
        var i = first;
        for (var k = 0; t0 + (k + 1) * bin <= end; k++)
        {
            var lo = t0 + k * bin;
            var hi = lo + bin;
            while (i <= last && t[i] < lo) i++;
            var j = i;
            while (j <= last && t[j] < hi) j++;
            var count = j - i;
            if (count < 2) continue;
            var span = t[j - 1] - t[i];
            double rate;
            if (count >= 4 && span >= bin / 2) rate = (count - 1) / (span / (double)frequency);
            else rate = count / (bin / frequency);
            rates.Add(rate);
        }
    }

    private static double Jitter(List<double> intervals)
    {
        if (intervals.Count < 2) return 0;
        intervals.Sort();
        var median = Percentile(intervals, 0.50);
        return median > 0 ? Percentile(intervals, 0.99) / median : 0;
    }

    /// <summary>Nearest-rank percentile of a sorted list.</summary>
    private static double Percentile(List<double> sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    /// <summary>The device with the most reports (the mouse that was moved), with its estimate; null when nothing moved.</summary>
    public static (TKey Device, PollingEstimate Estimate)? EstimateBusiest<TKey>(IReadOnlyDictionary<TKey, IReadOnlyList<MotionSample>> devices, long frequency)
        where TKey : notnull
    {
        if (devices.Count == 0) return null;
        var busiest = devices.MaxBy(d => d.Value.Count);
        return busiest.Value.Count == 0 ? null : (busiest.Key, Estimate(busiest.Value, frequency));
    }

    // ───────────────────────────── Texts ─────────────────────────────

    /// <summary>«≈ 1000 Гц · стабильность 98 % · разброс ×1,3», or why there is no number.</summary>
    public static string ResultText(PollingEstimate e)
    {
        if (!e.EnoughMotion)
            return Loc.T("Недостаточно движения — нажмите «ИЗМЕРИТЬ» ещё раз и все 5 секунд водите мышью быстрыми кругами.");
        var stability = Loc.Tf("стабильность {0} %", (e.Stability * 100).ToString("0", CultureInfo.InvariantCulture));
        var jitter = e.Jitter > 0 ? Loc.Tf(" · разброс ×{0}", e.Jitter.ToString("0.0", InputAdvice.Ru)) : "";
        return e.Hz is { } hz
            ? Loc.Tf("≈ {0} Гц · {1}{2}", hz.ToString(CultureInfo.InvariantCulture), stability, jitter)
            : Loc.Tf("≈ {0} Гц (нестабильно) · {1}{2}", Math.Round(e.RawHz).ToString("0", CultureInfo.InvariantCulture), stability, jitter);
    }

    public static string SaturationHint =>
        Loc.T("Водите быстрее: на высокой частоте опроса медленное движение не заполняет все отчёты, и результат занижается.");
}
