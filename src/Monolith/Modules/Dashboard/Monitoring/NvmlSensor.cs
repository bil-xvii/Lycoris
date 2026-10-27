using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Monolith.Modules.Dashboard.Monitoring;

/// <summary>
/// NVML facts for one card (all read-only queries). Null = not reported. Max values are "possible with this device and
/// system" per NVML (they already include the slot); <see cref="GpuMaxGen"/> is what the card alone supports.
/// <see cref="UnderLoad"/> = every sample of the current link was taken at ≥ 50 % GPU use (the link slows down at idle).
/// </summary>
internal readonly record struct NvmlLink(ulong? Bar1Total, ulong? VramTotal, int? CurrentGen, int? CurrentWidth,
    int? MaxGen, int? MaxWidth, int? GpuMaxGen, bool UnderLoad);

/// <summary>
/// Optional NVIDIA temperature source (nvml.dll ships with the NVIDIA driver in System32).
/// Loaded dynamically and only from System32, so a planted DLL can never be picked up.
/// Used only when D3DKMT does not report a temperature for an NVIDIA adapter, and by the dashboard checks for the
/// Resizable BAR / PCIe link facts (<see cref="ReadLink"/>). Only query functions are ever called — never a setter.
/// </summary>
internal sealed unsafe class NvmlSensor : IDisposable
{
    private const int NVML_SUCCESS = 0;
    private const int NVML_TEMPERATURE_GPU = 0;

    private IntPtr _library;
    private readonly IntPtr _device;
    private readonly delegate* unmanaged<int> _shutdown;
    private readonly delegate* unmanaged<IntPtr, int, uint*, int> _getTemperature;

    private NvmlSensor(IntPtr library, IntPtr device, delegate* unmanaged<int> shutdown,
        delegate* unmanaged<IntPtr, int, uint*, int> getTemperature)
    {
        _library = library;
        _device = device;
        _shutdown = shutdown;
        _getTemperature = getTemperature;
    }

    /// <summary>Initializes NVML and picks the device whose name matches <paramref name="preferredName"/> (or the first one).</summary>
    public static NvmlSensor? TryCreate(string? preferredName)
    {
        var path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var lib)) return null;
        try
        {
            if (!NativeLibrary.TryGetExport(lib, "nvmlInit_v2", out var pInit) ||
                !NativeLibrary.TryGetExport(lib, "nvmlShutdown", out var pShutdown) ||
                !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetCount_v2", out var pCount) ||
                !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetHandleByIndex_v2", out var pHandle) ||
                !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetTemperature", out var pTemp))
            {
                NativeLibrary.Free(lib);
                return null;
            }
            NativeLibrary.TryGetExport(lib, "nvmlDeviceGetName", out var pName);

            var init = (delegate* unmanaged<int>)pInit;
            var shutdown = (delegate* unmanaged<int>)pShutdown;
            var getCount = (delegate* unmanaged<uint*, int>)pCount;
            var getHandle = (delegate* unmanaged<uint, IntPtr*, int>)pHandle;
            var getName = (delegate* unmanaged<IntPtr, byte*, uint, int>)pName;

            if (init() != NVML_SUCCESS)
            {
                NativeLibrary.Free(lib);
                return null;
            }

            uint count = 0;
            if (getCount(&count) != NVML_SUCCESS || count == 0)
            {
                shutdown();
                NativeLibrary.Free(lib);
                return null;
            }

            var chosen = IntPtr.Zero;
            var buffer = stackalloc byte[96];
            for (uint i = 0; i < Math.Min(count, 16u); i++)
            {
                IntPtr dev;
                if (getHandle(i, &dev) != NVML_SUCCESS) continue;
                if (chosen == IntPtr.Zero) chosen = dev;
                if (string.IsNullOrWhiteSpace(preferredName) || pName == IntPtr.Zero) break;
                new Span<byte>(buffer, 96).Clear();
                if (getName(dev, buffer, 96) != NVML_SUCCESS) continue;
                var name = Encoding.UTF8.GetString(buffer, Math.Max(0, IndexOfZero(buffer, 96)));
                if (MonitorMath.SameGpuName(name, preferredName))
                {
                    chosen = dev;
                    break;
                }
            }

            if (chosen == IntPtr.Zero)
            {
                shutdown();
                NativeLibrary.Free(lib);
                return null;
            }
            return new NvmlSensor(lib, chosen, shutdown, (delegate* unmanaged<IntPtr, int, uint*, int>)pTemp);
        }
        catch
        {
            try { NativeLibrary.Free(lib); } catch { }
            return null;
        }
    }

    private static int IndexOfZero(byte* buffer, int length)
    {
        for (var i = 0; i < length; i++)
            if (buffer[i] == 0) return i;
        return length;
    }

    /// <summary>Core temperature in °C, or null.</summary>
    public double? ReadTemperature()
    {
        if (_library == IntPtr.Zero) return null;
        uint t = 0;
        return _getTemperature(_device, NVML_TEMPERATURE_GPU, &t) == NVML_SUCCESS && t is > 0 and < 150 ? t : null;
    }

    /// <summary>Utilisation (%) at or above which the current PCIe link is taken as the working link.</summary>
    public const int LoadThreshold = 50;

    /// <summary>
    /// BAR1 window, VRAM and PCIe link of the chosen card. Every export is optional: a missing one (older driver)
    /// only leaves its value null. The current link is sampled <paramref name="samples"/> times, <paramref name="intervalMs"/> apart.
    /// </summary>
    public NvmlLink ReadLink(int samples = 3, int intervalMs = 300)
    {
        if (_library == IntPtr.Zero) return default;
        var triple = stackalloc ulong[3];

        ulong? bar1 = null, vram = null;
        if (NativeLibrary.TryGetExport(_library, "nvmlDeviceGetBAR1MemoryInfo", out var pBar1))
        {
            new Span<ulong>(triple, 3).Clear();
            if (((delegate* unmanaged<IntPtr, ulong*, int>)pBar1)(_device, triple) == NVML_SUCCESS && triple[0] > 0) bar1 = triple[0];
        }
        if (NativeLibrary.TryGetExport(_library, "nvmlDeviceGetMemoryInfo", out var pMem))
        {
            new Span<ulong>(triple, 3).Clear();
            if (((delegate* unmanaged<IntPtr, ulong*, int>)pMem)(_device, triple) == NVML_SUCCESS && triple[0] > 0) vram = triple[0];
        }

        var maxGen = Gen(ReadUInt("nvmlDeviceGetMaxPcieLinkGeneration"));
        var maxWidth = Width(ReadUInt("nvmlDeviceGetMaxPcieLinkWidth"));
        var gpuMaxGen = Gen(ReadUInt("nvmlDeviceGetGpuMaxPcieLinkGeneration"));

        int? curGen = null, curWidth = null;
        var loaded = NativeLibrary.TryGetExport(_library, "nvmlDeviceGetUtilizationRates", out var pUtil) && samples > 0;
        var util = stackalloc uint[2];
        for (var i = 0; i < Math.Max(1, samples); i++)
        {
            if (i > 0) Thread.Sleep(intervalMs);
            if (loaded)
            {
                util[0] = util[1] = 0;
                if (((delegate* unmanaged<IntPtr, uint*, int>)pUtil)(_device, util) != NVML_SUCCESS || util[0] < LoadThreshold) loaded = false;
            }
            // Keep the best value seen: a momentary dip must never look like a narrower link.
            if (Gen(ReadUInt("nvmlDeviceGetCurrPcieLinkGeneration")) is { } g) curGen = Math.Max(curGen ?? 0, g);
            if (Width(ReadUInt("nvmlDeviceGetCurrPcieLinkWidth")) is { } w) curWidth = Math.Max(curWidth ?? 0, w);
        }
        return new NvmlLink(bar1, vram, curGen, curWidth, maxGen, maxWidth, gpuMaxGen, loaded && curWidth is not null);

        static int? Gen(uint? v) => v is >= 1 and <= 6 ? (int)v : null;
        static int? Width(uint? v) => v is >= 1 and <= 32 ? (int)v : null;
    }

    /// <summary>Calls an optional <c>nvmlReturn_t f(nvmlDevice_t, unsigned int*)</c> query; null when missing or failed.</summary>
    private uint? ReadUInt(string export)
    {
        if (!NativeLibrary.TryGetExport(_library, export, out var p)) return null;
        uint v = 0;
        return ((delegate* unmanaged<IntPtr, uint*, int>)p)(_device, &v) == NVML_SUCCESS ? v : null;
    }

    public void Dispose()
    {
        if (_library == IntPtr.Zero) return;
        try { _shutdown(); } catch { }
        try { NativeLibrary.Free(_library); } catch { }
        _library = IntPtr.Zero;
    }
}
