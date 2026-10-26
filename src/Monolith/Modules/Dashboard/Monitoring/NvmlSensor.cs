using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Monolith.Modules.Dashboard.Monitoring;

/// <summary>
/// Optional NVIDIA temperature source (nvml.dll ships with the NVIDIA driver in System32).
/// Loaded dynamically and only from System32, so a planted DLL can never be picked up.
/// Used only when D3DKMT does not report a temperature for an NVIDIA adapter.
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

    public void Dispose()
    {
        if (_library == IntPtr.Zero) return;
        try { _shutdown(); } catch { }
        try { NativeLibrary.Free(_library); } catch { }
        _library = IntPtr.Zero;
    }
}
