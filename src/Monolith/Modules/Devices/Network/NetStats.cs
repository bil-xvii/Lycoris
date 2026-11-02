using System.Globalization;

namespace Monolith.Modules.Devices.Network;

// Pure statistics of the network measurements (research network_measure.md §3–4, §8). No I/O here: everything is
// unit-tested with recorded sample arrays.

/// <summary>Latency of one target: what was sent, what came back, and the spread (milliseconds).</summary>
public sealed record LatencyStats(int Sent, int Received, double? Min, double? P50, double? P95, double? Max, double? Jitter)
{
    public static readonly LatencyStats Empty = new(0, 0, null, null, null, null, null);

    public int Lost => Sent - Received;
    public double LossPercent => Sent == 0 ? 0 : Lost * 100.0 / Sent;
    public bool HasData => Received > 0;
}

/// <summary>How a metric moved between two measurements.</summary>
public enum ChangeVerdict { Better, Worse, NoDifference }

/// <summary>Severity of a finding (the same scale as the Dashboard checks; Critical sorts first).</summary>
public enum NetSeverity { Ok, Info, Warning, Critical }

public static class NetStats
{
    private static CultureInfo Ru => Loc.Culture;

    /// <summary>Statistics of samples in send order; null = no reply.</summary>
    public static LatencyStats Compute(IReadOnlyList<double?> samples)
    {
        if (samples.Count == 0) return LatencyStats.Empty;
        var received = samples.Where(s => s is not null).Select(s => s!.Value).ToList();
        if (received.Count == 0) return new LatencyStats(samples.Count, 0, null, null, null, null, null);
        var sorted = received.OrderBy(x => x).ToList();
        return new LatencyStats(samples.Count, received.Count, sorted[0], Percentile(sorted, 0.5), Percentile(sorted, 0.95), sorted[^1], Jitter(received));
    }

    /// <summary>Linear interpolation between the closest ranks (sorted input).</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        if (sorted.Count == 1) return sorted[0];
        var rank = Math.Clamp(p, 0, 1) * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        return sorted.Count == 0 ? double.NaN : Percentile(sorted, 0.5);
    }

    /// <summary>Mean absolute difference of consecutive replies (Cloudflare's definition); 0 for fewer than two.</summary>
    public static double Jitter(IReadOnlyList<double> received)
    {
        if (received.Count < 2) return 0;
        double sum = 0;
        for (var i = 1; i < received.Count; i++) sum += Math.Abs(received[i] - received[i - 1]);
        return sum / (received.Count - 1);
    }

    // ───────────────────────────── Text ─────────────────────────────

    /// <summary>«&lt;1 мс» under a millisecond (the LAN), «2,4 мс», «54 мс»; «—» when unknown.</summary>
    public static string Ms(double? v) => v switch
    {
        null => "—",
        < 1 => Loc.T("<1 мс"),
        < 10 => Loc.Tf("{0} мс", v.Value.ToString("0.0", Ru)),
        _ => Loc.Tf("{0} мс", v.Value.ToString("0", Ru)),
    };

    /// <summary>«+0,3 мс» / «+120 мс».</summary>
    public static string DeltaMs(double v) => Loc.Tf("{0} мс", (v >= 0 ? "+" : "−") + (Math.Abs(v) < 10 ? Math.Abs(v).ToString("0.0", Ru) : Math.Abs(v).ToString("0", Ru)));

    public static string Percent(double v) => Loc.Tf("{0} %", v < 10 && v % 1 != 0 ? v.ToString("0.0", Ru) : v.ToString("0", Ru));

    /// <summary>«42» / «1,2» (Mbit/s without the unit).</summary>
    public static string Number(double v) => v < 10 ? v.ToString("0.0", Ru) : v.ToString("0", Ru);

    public static string Mbps(double v) => Loc.Tf("{0} Мбит/с", v < 10 ? v.ToString("0.0", Ru) : v.ToString("0", Ru));

    // ───────────────────────────── Bufferbloat ─────────────────────────────

    /// <summary>
    /// Letter grade of the latency increase under load (loaded median − idle median), the scale widely reported for the
    /// Waveform test: A+ &lt; 5, A &lt; 30, B &lt; 60, C &lt; 200, D &lt; 400, F ≥ 400 ms.
    /// </summary>
    public static string Grade(double increaseMs) => increaseMs switch
    {
        < 5 => "A+",
        < 30 => "A",
        < 60 => "B",
        < 200 => "C",
        < 400 => "D",
        _ => "F",
    };

    public static NetSeverity GradeSeverity(string grade) => grade switch
    {
        "A+" or "A" => NetSeverity.Ok,
        "B" => NetSeverity.Info,
        "C" => NetSeverity.Warning,
        _ => NetSeverity.Critical,
    };

    // ───────────────────────────── Before / after ─────────────────────────────

    /// <summary>
    /// 95 % bootstrap interval of the difference of medians (after − before): 1000 resamples by default, fixed seed so
    /// the same data always gives the same verdict. Null when either side has fewer than 5 samples.
    /// </summary>
    public static (double Low, double High)? MedianDifferenceCi(IReadOnlyList<double> before, IReadOnlyList<double> after, int resamples = 1000, int seed = 7919)
    {
        if (before.Count < 5 || after.Count < 5) return null;
        var rng = new Random(seed);
        var diffs = new double[resamples];
        var a = new double[before.Count];
        var b = new double[after.Count];
        for (var i = 0; i < resamples; i++)
        {
            for (var j = 0; j < a.Length; j++) a[j] = before[rng.Next(before.Count)];
            for (var j = 0; j < b.Length; j++) b[j] = after[rng.Next(after.Count)];
            Array.Sort(a);
            Array.Sort(b);
            diffs[i] = Percentile(b, 0.5) - Percentile(a, 0.5);
        }
        Array.Sort(diffs);
        return (Percentile(diffs, 0.025), Percentile(diffs, 0.975));
    }

    /// <summary>
    /// «лучше» only when the interval excludes 0 and the change is at least 1 ms (research §3.3); the mirror for «хуже»;
    /// otherwise «без измеримой разницы».
    /// </summary>
    public static ChangeVerdict CompareMedians(IReadOnlyList<double> before, IReadOnlyList<double> after)
    {
        if (MedianDifferenceCi(before, after) is not { } ci) return ChangeVerdict.NoDifference;
        var delta = Median(after) - Median(before);
        if (ci.High < 0 && delta <= -1) return ChangeVerdict.Better;
        if (ci.Low > 0 && delta >= 1) return ChangeVerdict.Worse;
        return ChangeVerdict.NoDifference;
    }

    /// <summary>Jitter / p95: a change counts only from 20 % and at least 0,5 ms.</summary>
    public static ChangeVerdict CompareSpread(double? before, double? after)
    {
        if (before is not { } b || after is not { } a) return ChangeVerdict.NoDifference;
        var delta = a - b;
        if (Math.Abs(delta) < 0.5 || Math.Abs(delta) < Math.Abs(b) * 0.2) return ChangeVerdict.NoDifference;
        return delta < 0 ? ChangeVerdict.Better : ChangeVerdict.Worse;
    }

    /// <summary>Loss: a difference is shown only with ≥ 200 samples per side or a change of at least 1 percentage point… and 2 packets.</summary>
    public static ChangeVerdict CompareLoss(LatencyStats before, LatencyStats after)
    {
        if (before.Sent == 0 || after.Sent == 0) return ChangeVerdict.NoDifference;
        var delta = after.LossPercent - before.LossPercent;
        var enough = (before.Sent >= 200 && after.Sent >= 200) || Math.Abs(after.Lost - before.Lost) >= 2;
        if (!enough || Math.Abs(delta) < 1) return ChangeVerdict.NoDifference;
        return delta < 0 ? ChangeVerdict.Better : ChangeVerdict.Worse;
    }

    public static string VerdictText(ChangeVerdict v) => v switch
    {
        ChangeVerdict.Better => Loc.T("лучше"),
        ChangeVerdict.Worse => Loc.T("хуже"),
        _ => Loc.T("без измеримой разницы"),
    };
}
