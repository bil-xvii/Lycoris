using System.Runtime.InteropServices;

namespace Monolith.Modules.Dashboard.Monitoring;

/// <summary>
/// One PDH query with counters added through <c>PdhAddEnglishCounterW</c> (counter names are localized on
/// Russian Windows; the English API is language-neutral). Not thread-safe: use from one sampling thread.
/// Counters that do not exist on this Windows build are silently skipped (the metric shows "—").
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_CSTATUS_NEW_DATA = 1;
    private const int ItemSize = 24; // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer + {CStatus, pad, double}

    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new(StringComparer.OrdinalIgnoreCase);

    private PdhQuery(IntPtr query) => _query = query;

    /// <summary>Opens a query and adds every counter that exists. Returns null when PDH itself is unusable.</summary>
    public static PdhQuery? TryCreate(IEnumerable<string> englishPaths)
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out var q) != 0 || q == IntPtr.Zero) return null;
            var pq = new PdhQuery(q);
            foreach (var path in englishPaths)
            {
                if (PdhAddEnglishCounterW(q, path, IntPtr.Zero, out var c) == 0 && c != IntPtr.Zero)
                    pq._counters[path] = c;
            }
            if (pq._counters.Count == 0)
            {
                pq.Dispose();
                return null;
            }
            PdhCollectQueryData(q); // prime: rate counters need two samples
            return pq;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool Has(string path) => _counters.ContainsKey(path);

    public bool Collect() => _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;

    /// <summary>Single-instance counter value, or null when unavailable.</summary>
    public double? Value(string path)
    {
        if (!_counters.TryGetValue(path, out var c)) return null;
        if (PdhGetFormattedCounterValue(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) != 0) return null;
        if (v.CStatus > PDH_CSTATUS_NEW_DATA) return null;
        return double.IsFinite(v.Value) ? v.Value : null;
    }

    /// <summary>All instances of a wildcard counter (instances that are new in this sample are skipped).</summary>
    public List<(string Instance, double Value)> Array(string path)
    {
        var result = new List<(string, double)>();
        if (!_counters.TryGetValue(path, out var c)) return result;

        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        for (var attempt = 0; attempt < 4 && status == PDH_MORE_DATA && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buffer);
                if (status == PDH_MORE_DATA) continue; // instance count grew between the two calls
                if (status != 0) break;
                for (var i = 0; i < count; i++)
                {
                    var item = buffer + i * ItemSize;
                    var cstatus = (uint)Marshal.ReadInt32(item + 8);
                    if (cstatus > PDH_CSTATUS_NEW_DATA) continue;
                    var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + 16));
                    if (!double.IsFinite(value)) continue;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    result.Add((name, value));
                }
                break;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }

    /// <summary>Sum over all instances (e.g. network bytes/s); null when the counter is missing.</summary>
    public double? Sum(string path)
    {
        if (!_counters.ContainsKey(path)) return null;
        var items = Array(path);
        return items.Count == 0 ? null : items.Sum(i => i.Value);
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero) return;
        try { PdhCloseQuery(_query); } catch { }
        _query = IntPtr.Zero;
        _counters.Clear();
    }

    #region P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double Value; // union at offset 8
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PdhCloseQuery(IntPtr query);
    #endregion
}
