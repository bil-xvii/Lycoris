using System.Text.RegularExpressions;

namespace Monolith.Modules.Dashboard.Monitoring;

/// <summary>Pure helpers for turning raw counter arrays into dashboard numbers (unit-tested).</summary>
public static partial class MonitorMath
{
    [GeneratedRegex(@"^pid_(\d+)_luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EngineInstanceRx();

    [GeneratedRegex(@"^luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdapterInstanceRx();

    /// <summary>Adapter LUID in the canonical (lower-case) form used by GPU counter instance names.</summary>
    public static string LuidKey(int high, uint low) => $"0x{unchecked((uint)high):x8}_0x{low:x8}";

    /// <summary>
    /// GPU 3D load per adapter, aggregated like Task Manager: each engine's utilization is summed over all
    /// processes, and the adapter shows its busiest 3D engine (capped at 100 %).
    /// Input: instances of <c>\GPU Engine(*)\Utilization Percentage</c>. Key: canonical LUID.
    /// </summary>
    public static Dictionary<string, double> Gpu3DLoadByAdapter(IEnumerable<(string Instance, double Value)> engines)
    {
        var perEngine = new Dictionary<(string Luid, int Phys, int Engine), double>();
        foreach (var (instance, value) in engines)
        {
            var m = EngineInstanceRx().Match(instance);
            if (!m.Success) continue;
            if (!m.Groups[5].Value.Equals("3D", StringComparison.OrdinalIgnoreCase)) continue;
            var key = (m.Groups[2].Value.ToLowerInvariant(), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
            perEngine[key] = perEngine.GetValueOrDefault(key) + Math.Max(0, value);
        }
        return perEngine
            .GroupBy(e => e.Key.Luid)
            .ToDictionary(g => g.Key, g => Math.Min(100, g.Max(e => e.Value)));
    }

    /// <summary>Bytes per adapter from <c>\GPU Adapter Memory(*)\Dedicated Usage</c> (summed over physical parts).</summary>
    public static Dictionary<string, double> BytesByAdapter(IEnumerable<(string Instance, double Value)> instances)
    {
        var result = new Dictionary<string, double>();
        foreach (var (instance, value) in instances)
        {
            var m = AdapterInstanceRx().Match(instance);
            if (!m.Success) continue;
            var luid = m.Groups[1].Value.ToLowerInvariant();
            result[luid] = result.GetValueOrDefault(luid) + Math.Max(0, value);
        }
        return result;
    }

    /// <summary>
    /// Value for the adapter we display: the known primary adapter when it reports, otherwise the maximum
    /// over all adapters; null when nothing reports.
    /// </summary>
    public static double? PickAdapterValue(IReadOnlyDictionary<string, double> byAdapter, string? primaryLuid)
    {
        if (byAdapter.Count == 0) return null;
        if (primaryLuid is not null && byAdapter.TryGetValue(primaryLuid, out var v)) return v;
        return byAdapter.Values.Max();
    }

    /// <summary>Busy % from PhysicalDisk "% Idle Time" (100 − idle, clamped).</summary>
    public static double? DiskBusyFromIdle(double? idle) => idle is null ? null : Math.Clamp(100 - idle.Value, 0, 100);

    /// <summary>Effective CPU clock: nominal frequency × "% Processor Performance" / 100.</summary>
    public static double? EffectiveClockMhz(double? performancePercent, double? baseMhz)
    {
        if (performancePercent is not { } p || baseMhz is not { } b || p <= 0 || b <= 0) return null;
        var mhz = b * p / 100;
        return mhz is > 100 and < 20000 ? mhz : null;
    }

    /// <summary>Same GPU model, ignoring case, trademark marks and repeated spaces.</summary>
    public static bool SameGpuName(string? a, string? b)
    {
        var x = NormalizeName(a);
        var y = NormalizeName(b);
        if (x.Length == 0 || y.Length == 0) return false;
        return x == y || x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal);
    }

    private static string NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
             .Replace("™", "").Replace("®", "").Trim().ToLowerInvariant();
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ");
        return s;
    }

    /// <summary>Appends a point to a fixed-size history (oldest points drop out).</summary>
    public static void Push(IList<double> history, double value, int capacity = 60)
    {
        history.Add(value);
        while (history.Count > capacity) history.RemoveAt(0);
    }
}
