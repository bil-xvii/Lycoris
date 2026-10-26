using System.Runtime.InteropServices;

namespace Monolith.Modules.Dashboard.Monitoring;

/// <summary>One graphics adapter opened through D3DKMT (gdi32). Close it with <see cref="D3dkmt.Close"/>.</summary>
internal sealed record KmtAdapter(uint Handle, uint LuidLow, int LuidHigh, string Name, ulong DedicatedVideoMemory)
{
    /// <summary>LUID in the form used by the GPU performance counter instances: "0x00000000_0x0000E1AE".</summary>
    public string LuidKey => MonitorMath.LuidKey(LuidHigh, LuidLow);

    public bool IsSoftware => Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
                              || Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Kernel-mode thunks for GPU facts that need no admin rights and no vendor SDK (the same source Task Manager uses):
/// adapter list, names, dedicated VRAM, temperature (WDDM 2.4+) and the hardware-scheduling (HAGS) capability bits.
/// Struct layouts and query type numbers follow docs/research/tech.md (verified on the dev machine).
/// </summary>
internal static class D3dkmt
{
    private const int KMTQAITYPE_GETSEGMENTSIZE = 3;
    private const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;
    private const int KMTQAITYPE_WDDM_2_7_CAPS = 70;
    private const int AdapterInfoSize = 20;      // D3DKMT_ADAPTERINFO
    private const int RegistryInfoSize = 4 * 260 * 2;
    private const int PerfDataSize = 64;         // D3DKMT_ADAPTER_PERFDATA
    private const int PerfTemperatureOffset = 56;

    /// <summary>Opens every adapter. Returns an empty list when the API is unavailable.</summary>
    public static List<KmtAdapter> OpenAdapters()
    {
        var list = new List<KmtAdapter>();
        try
        {
            var e = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return list;
            var capacity = (int)Math.Min(e.NumAdapters, 64u);
            var buffer = Marshal.AllocHGlobal(capacity * AdapterInfoSize);
            try
            {
                Zero(buffer, capacity * AdapterInfoSize);
                e.NumAdapters = (uint)capacity;
                e.pAdapters = buffer;
                if (D3DKMTEnumAdapters2(ref e) != 0) return list;
                var count = (int)Math.Min(e.NumAdapters, (uint)capacity);
                for (var i = 0; i < count; i++)
                {
                    var p = buffer + i * AdapterInfoSize;
                    var handle = (uint)Marshal.ReadInt32(p);
                    var low = (uint)Marshal.ReadInt32(p + 4);
                    var high = Marshal.ReadInt32(p + 8);
                    if (handle == 0) continue;
                    list.Add(new KmtAdapter(handle, low, high, QueryName(handle) ?? "", QueryDedicatedMemory(handle) ?? 0));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return list;
    }

    public static void Close(KmtAdapter adapter) => Close(adapter.Handle);

    public static void Close(IEnumerable<KmtAdapter> adapters)
    {
        foreach (var a in adapters) Close(a.Handle);
    }

    private static void Close(uint handle)
    {
        try
        {
            var c = new D3DKMT_CLOSEADAPTER { hAdapter = handle };
            D3DKMTCloseAdapter(ref c);
        }
        catch { }
    }

    /// <summary>GPU temperature in °C (the driver reports tenths of a degree); null when not reported.</summary>
    public static double? QueryTemperature(uint handle)
    {
        uint raw = 0;
        if (!TryQuery(handle, KMTQAITYPE_ADAPTERPERFDATA, PerfDataSize, b => raw = (uint)Marshal.ReadInt32(b + PerfTemperatureOffset))) return null;
        if (raw == 0) return null;
        var c = raw / 10d;
        return c is > 0 and < 150 ? c : null;
    }

    /// <summary>WDDM 2.7 caps: bit 0 = HAGS supported, bit 1 = HAGS enabled, bit 2 = enabled by default.</summary>
    public static uint? QueryWddm27Caps(uint handle)
    {
        uint caps = 0;
        return TryQuery(handle, KMTQAITYPE_WDDM_2_7_CAPS, 4, b => caps = (uint)Marshal.ReadInt32(b)) ? caps : null;
    }

    private static string? QueryName(uint handle)
    {
        string? name = null;
        TryQuery(handle, KMTQAITYPE_ADAPTERREGISTRYINFO, RegistryInfoSize, b => name = Marshal.PtrToStringUni(b)?.Trim());
        return name;
    }

    private static ulong? QueryDedicatedMemory(uint handle)
    {
        ulong bytes = 0;
        return TryQuery(handle, KMTQAITYPE_GETSEGMENTSIZE, 24, b => bytes = (ulong)Marshal.ReadInt64(b)) ? bytes : null;
    }

    /// <summary>Runs D3DKMTQueryAdapterInfo with a zeroed private buffer; <paramref name="read"/> runs only on success.</summary>
    private static bool TryQuery(uint handle, int type, int size, Action<IntPtr> read)
    {
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Zero(buffer, size);
            var q = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = handle, Type = type, pPrivateDriverData = buffer, PrivateDriverDataSize = (uint)size,
            };
            if (D3DKMTQueryAdapterInfo(ref q) != 0) return false;
            read(buffer);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
    private static void Zero(IntPtr buffer, int size) => Marshal.Copy(new byte[size], 0, buffer, size);

    #region P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 enumAdapters);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO queryAdapterInfo);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER closeAdapter);
    #endregion
}
