using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Monolith.Core;

namespace Monolith.Modules.Dashboard.Monitoring;

/// <summary>One reading of every live metric. Null = not available on this PC (shown as "—").</summary>
public sealed record MonitorSample
{
    public DateTime Time { get; init; } = DateTime.Now;
    public double? CpuPercent { get; init; }
    public double? CpuMhz { get; init; }
    public ulong RamTotal { get; init; }
    public ulong RamUsed { get; init; }
    public double? RamPercent { get; init; }
    public double? GpuPercent { get; init; }
    public double? GpuTemperature { get; init; }
    public double? VramUsed { get; init; }
    public double? VramTotal { get; init; }
    public double? DiskPercent { get; init; }
    public double? DiskReadBps { get; init; }
    public double? DiskWriteBps { get; init; }
    public double? NetDownBps { get; init; }
    public double? NetUpBps { get; init; }
}

/// <summary>
/// Background sampler for the dashboard. PDH (English counter names) for CPU / GPU / disk / network,
/// GlobalMemoryStatusEx for RAM, D3DKMT for GPU temperature (NVML fallback on NVIDIA).
/// Samples every <see cref="AppSettings.MonitorIntervalMs"/> on a thread-pool loop and raises
/// <see cref="Sampled"/> on that background thread (subscribers marshal to the UI themselves).
/// </summary>
public sealed class LiveMonitor : IDisposable
{
    private const string CpuUtility = @"\Processor Information(_Total)\% Processor Utility";
    private const string CpuTime = @"\Processor Information(_Total)\% Processor Time";
    private const string CpuPerformance = @"\Processor Information(_Total)\% Processor Performance";
    private const string CpuFrequency = @"\Processor Information(_Total)\Processor Frequency";
    private const string GpuEngine = @"\GPU Engine(*)\Utilization Percentage";
    private const string GpuDedicated = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string DiskIdle = @"\PhysicalDisk(_Total)\% Idle Time";
    private const string DiskRead = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    private const string DiskWrite = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
    private const string NetDown = @"\Network Interface(*)\Bytes Received/sec";
    private const string NetUp = @"\Network Interface(*)\Bytes Sent/sec";

    /// <summary>Guards the sampling resources; held for a whole Initialize / Sample (can take seconds).</summary>
    private readonly object _gate = new();
    /// <summary>Guards only <see cref="_cts"/>, so Start / Stop / IsRunning on the UI thread never wait for a sample.</summary>
    private readonly object _ctsGate = new();
    private CancellationTokenSource? _cts;
    private volatile bool _disposed;

    // Sampling resources: created lazily on the sampling thread, used only under _gate.
    private bool _initialized;
    private PdhQuery? _pdh;
    private List<KmtAdapter> _adapters = [];
    private KmtAdapter? _primary;
    private bool _primaryFromProfile;
    private NvmlSensor? _nvml;
    private bool _nvmlTried;
    private (long Rx, long Tx, DateTime Time)? _netPrev;

    /// <summary>Raised on a background thread after every sample.</summary>
    public event Action<MonitorSample>? Sampled;

    public bool IsRunning
    {
        get { lock (_ctsGate) return _cts is not null; }
    }

    /// <summary>Starts sampling (no-op when already running). A loop that is still winding down after
    /// <see cref="Stop"/> exits on its own cancelled token, so a quick Stop → Start is safe.</summary>
    public void Start()
    {
        lock (_ctsGate)
        {
            if (_disposed || _cts is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(token));
        }
    }

    public void Stop()
    {
        lock (_ctsGate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (!_initialized) Initialize();
            }
            while (!ct.IsCancellationRequested)
            {
                var started = DateTime.UtcNow;
                MonitorSample? sample = null;
                lock (_gate)
                {
                    if (_disposed) return;
                    sample = Sample();
                }
                if (ct.IsCancellationRequested) return;
                try { Sampled?.Invoke(sample); } catch (Exception ex) { Log.Warn("Мониторинг: {0}", ex.Message); }

                var interval = Math.Clamp(AppSettings.Current.MonitorIntervalMs, 250, 10_000);
                var wait = interval - (int)(DateTime.UtcNow - started).TotalMilliseconds;
                await Task.Delay(Math.Max(50, wait), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Мониторинг остановлен из-за ошибки", ex);
        }
    }

    private void Initialize()
    {
        _initialized = true;
        _pdh = PdhQuery.TryCreate([CpuUtility, CpuTime, CpuPerformance, CpuFrequency, GpuEngine, GpuDedicated,
                                   DiskIdle, DiskRead, DiskWrite, NetDown, NetUp]);
        if (_pdh is null) Log.Warn("Счётчики производительности Windows недоступны — часть показателей будет скрыта.");

        try
        {
            _adapters = D3dkmt.OpenAdapters().Where(a => !a.IsSoftware).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось получить список видеоадаптеров: {0}", ex.Message);
            _adapters = [];
        }
        ResolvePrimaryAdapter();
    }

    /// <summary>Primary adapter = the one SystemProfile calls primary (by name), otherwise the one with most VRAM.</summary>
    private void ResolvePrimaryAdapter()
    {
        if (_adapters.Count == 0) return;
        var profile = SystemProfile.Current;
        if (profile.IsLoaded && profile.PrimaryGpu is { } gpu)
        {
            var match = _adapters.FirstOrDefault(a => MonitorMath.SameGpuName(a.Name, gpu.Name));
            if (match is not null)
            {
                _primary = match;
                _primaryFromProfile = true;
                return;
            }
            _primaryFromProfile = true; // profile loaded but no name match: keep the VRAM-based choice
        }
        _primary ??= _adapters.OrderByDescending(a => a.DedicatedVideoMemory).First();
    }

    private MonitorSample Sample()
    {
        if (!_primaryFromProfile && SystemProfile.Current.IsLoaded) ResolvePrimaryAdapter();

        var pdh = _pdh;
        if (pdh is not null && !pdh.Collect()) pdh = null;

        // CPU
        double? cpu = null, mhz = null;
        if (pdh is not null)
        {
            cpu = pdh.Value(CpuUtility) ?? pdh.Value(CpuTime);
            if (cpu is not null) cpu = Math.Clamp(cpu.Value, 0, 100);
            var nominal = pdh.Value(CpuFrequency);
            if (nominal is null or <= 0 && SystemProfile.Current.CpuMaxClockMhz > 0) nominal = SystemProfile.Current.CpuMaxClockMhz;
            mhz = MonitorMath.EffectiveClockMhz(pdh.Value(CpuPerformance), nominal);
        }

        // RAM
        ulong ramTotal = 0, ramUsed = 0;
        double? ramPercent = null;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms) && ms.ullTotalPhys > 0)
        {
            ramTotal = ms.ullTotalPhys;
            ramUsed = ms.ullTotalPhys - Math.Min(ms.ullAvailPhys, ms.ullTotalPhys);
            ramPercent = ramUsed * 100d / ramTotal;
        }

        // GPU
        double? gpu = null, vram = null, vramTotal = null, temp = null;
        var primaryLuid = _primary?.LuidKey;
        if (pdh is not null)
        {
            if (pdh.Has(GpuEngine))
                gpu = MonitorMath.PickAdapterValue(MonitorMath.Gpu3DLoadByAdapter(pdh.Array(GpuEngine)), primaryLuid) ?? 0;
            if (pdh.Has(GpuDedicated))
                vram = MonitorMath.PickAdapterValue(MonitorMath.BytesByAdapter(pdh.Array(GpuDedicated)), primaryLuid);
        }
        if (_primary is { DedicatedVideoMemory: > 0 }) vramTotal = _primary.DedicatedVideoMemory;
        else if (SystemProfile.Current.PrimaryGpu is { DedicatedMemoryBytes: > 0 } pg) vramTotal = pg.DedicatedMemoryBytes;

        if (_primary is not null) temp = D3dkmt.QueryTemperature(_primary.Handle);
        if (temp is null) temp = ReadNvmlTemperature();

        // Disk
        double? disk = null, diskRead = null, diskWrite = null;
        if (pdh is not null)
        {
            disk = MonitorMath.DiskBusyFromIdle(pdh.Value(DiskIdle));
            diskRead = pdh.Value(DiskRead);
            diskWrite = pdh.Value(DiskWrite);
        }

        // Network
        double? down = pdh?.Sum(NetDown), up = pdh?.Sum(NetUp);
        if (down is null || up is null) (down, up) = ReadNetworkFallback();

        return new MonitorSample
        {
            CpuPercent = cpu, CpuMhz = mhz,
            RamTotal = ramTotal, RamUsed = ramUsed, RamPercent = ramPercent,
            GpuPercent = gpu, GpuTemperature = temp, VramUsed = vram, VramTotal = vramTotal,
            DiskPercent = disk, DiskReadBps = diskRead, DiskWriteBps = diskWrite,
            NetDownBps = down, NetUpBps = up,
        };
    }

    private double? ReadNvmlTemperature()
    {
        var profile = SystemProfile.Current;
        if (!profile.IsLoaded) return null;
        var gpu = profile.PrimaryGpu;
        if (gpu is null || gpu.Vendor != GpuVendor.Nvidia) return null;
        if (!_nvmlTried)
        {
            _nvmlTried = true;
            _nvml = NvmlSensor.TryCreate(gpu.Name);
        }
        return _nvml?.ReadTemperature();
    }

    /// <summary>Managed fallback when the PDH network counters are missing: byte counter deltas of active NICs.</summary>
    private (double?, double?) ReadNetworkFallback()
    {
        try
        {
            long rx = 0, tx = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            var now = DateTime.UtcNow;
            var prev = _netPrev;
            _netPrev = (rx, tx, now);
            if (prev is null) return (null, null);
            var seconds = (now - prev.Value.Time).TotalSeconds;
            if (seconds <= 0) return (null, null);
            return (Math.Max(0, rx - prev.Value.Rx) / seconds, Math.Max(0, tx - prev.Value.Tx) / seconds);
        }
        catch
        {
            return (null, null);
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pdh?.Dispose();
            _pdh = null;
            D3dkmt.Close(_adapters);
            _adapters = [];
            _primary = null;
            _nvml?.Dispose();
            _nvml = null;
        }
    }

    #region P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    #endregion
}
