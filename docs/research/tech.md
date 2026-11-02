# MONOLITH: implementation research for the C# WPF / .NET 10 build

I checked these facts on the dev machine with read-only calls (Windows 11 Pro 26300, ru-RU system locale, OEM CP 866, ANSI CP 1251, RTX 5070 Ti + AMD iGPU). The calls were PDH, D3DKMT and powrprof run through in-memory `Add-Type` C#, plus CIM, `reg query` and raw stdout byte dumps. Facts confirmed this way are marked ✅.

## 0. The most important findings

1. ✅ **`PdhAddEnglishCounterW` with an `(*)` instance wildcard and `PdhGetFormattedCounterArrayW` works directly.** You don't need the expand-wildcard procedure from the docs. One `PdhCollectQueryData` call for 5 counters, including 337 `GPU Engine` instances, took **0.65 ms**.
2. ✅ **D3DKMT GPU temperature works without admin and without vendor SDKs.** It returned RTX 5070 Ti = 40.9 °C, which matches nvidia-smi's 40 °C, and AMD iGPU = 39 °C. `KMTQAITYPE_ADAPTERPERFDATA = 62`, not 61. The Microsoft docs enum has no explicit numbers, and value 14 is skipped in the header.
3. ✅ **HAGS: the `HwSchMode` registry value is missing, yet D3DKMT `WDDM_2_7_CAPS` reports HAGS as enabled (0xB).** The registry alone is misleading here.
4. ✅ **RAM: `Speed` = `ConfiguredClockSpeed` = 6000 on G.Skill F5‑6000J3636F16G.** Comparing the two can't reliably detect XMP/EXPO being off. Use a part-number heuristic instead.
5. ✅ **External tool output is localized, and the encoding differs per tool:**
   - powercfg, dism, schtasks and fsutil write **CP866**.
   - sfc writes **UTF‑16LE** (`0D 00 0D 00 0A 00 …`).
   - winget writes **UTF‑8**, with `\r` spinner lines and truncated `…` columns.
   - `powercfg /list` names are Russian (e.g. «Сбалансированная»).
6. ✅ **No ACPI thermal zones on this desktop.** The `Thermal Zone Information(*)` counter set has 0 instances, so CPU temperature is unavailable without a driver.
7. **Task Manager changed its CPU formula in 2025 (KB5064081, 26100.5074+).** Section 1.2 covers what to display.

---

## 1. Live monitoring without a kernel driver

### 1.1 Use direct PDH, not `PerformanceCounter`

| | `System.Diagnostics.PerformanceCounter` (NuGet) | Direct PDH P/Invoke |
|---|---|---|
| Names | Tries English (LCID 009) first, then falls back to the current culture | `PdhAddEnglishCounterW` is language-neutral by design |
| Cost | One object per instance; reads registry `HKEY_PERFORMANCE_DATA` blobs; first use builds name tables (hundreds of ms) | One query, one collect per tick (✅ sub-ms) |
| Wildcards | Needs `GetInstanceNames()` plus a new object per instance; GPU Engine instances change with every PID | `(*)` returns an array and picks up new instances automatically |

**Rule:** never call `PdhAddCounter` or `Get-Counter` with English paths. They need localized paths («\Сведения о процессоре(*)\…»), and even PDH error text is localized (✅ «Указанный элемент не найден.»). Always use `PdhAddEnglishCounterW`.

If PDH fails with `PDH_CSTATUS_NO_OBJECT`, the counters are corrupt or disabled. Fall back to `GetSystemTimes`, `GetPerformanceInfo` and `NetworkInterface`. Offer `lodctr /R` only as a user-started repair action.

```csharp
static class Pdh {
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQueryW(string? src, nint user, out nint q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounterW(nint q, string path, nint user, out nint c);
    [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(nint q);
    [DllImport("pdh.dll")] public static extern uint PdhGetFormattedCounterValue(nint c, uint fmt, out uint type, out FMT v);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhGetFormattedCounterArrayW(nint c, uint fmt, ref uint bytes, out uint n, nint buf);
    [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(nint q);
    public const uint DOUBLE = 0x200, NOCAP100 = 0x8000, MORE_DATA = 0x800007D2;
    [StructLayout(LayoutKind.Sequential)] public struct FMT { public uint CStatus; public double Value; }      // 16 B (union @8)
    [StructLayout(LayoutKind.Sequential)] public struct ITEM { public nint szName; public FMT V; }            // 24 B ✅
}

public sealed unsafe class PdhSampler : IDisposable {
    readonly nint _q; readonly Dictionary<string, nint> _c = new();
    public PdhSampler(IEnumerable<string> englishPaths) {
        Pdh.PdhOpenQueryW(null, 0, out _q);
        foreach (var p in englishPaths)
            if (Pdh.PdhAddEnglishCounterW(_q, p, 0, out var h) == 0) _c[p] = h;  // missing on old builds → feature off
        Pdh.PdhCollectQueryData(_q);                                              // prime: rate counters need 2 samples
    }
    public void Collect() => Pdh.PdhCollectQueryData(_q);                          // call once per 1 s tick (background thread)
    public double? Single(string p) => _c.TryGetValue(p, out var h)
        && Pdh.PdhGetFormattedCounterValue(h, Pdh.DOUBLE | Pdh.NOCAP100, out _, out var v) == 0 && v.CStatus <= 1 ? v.Value : null;
    public List<(string Inst, double Val)> Array(string p) {
        var res = new List<(string, double)>();
        if (!_c.TryGetValue(p, out var h)) return res;
        uint size = 0, st = Pdh.PdhGetFormattedCounterArrayW(h, Pdh.DOUBLE | Pdh.NOCAP100, ref size, out _, 0);
        while (st == Pdh.MORE_DATA) {
            nint buf = Marshal.AllocHGlobal((int)size);
            try {
                st = Pdh.PdhGetFormattedCounterArrayW(h, Pdh.DOUBLE | Pdh.NOCAP100, ref size, out uint n, buf);
                if (st == Pdh.MORE_DATA) continue;                // instance count grew between calls
                var it = (Pdh.ITEM*)buf;
                for (int i = 0; i < n; i++)
                    if (it[i].V.CStatus <= 1)                      // VALID_DATA/NEW_DATA; new instances are invalid on 1st sample
                        res.Add((Marshal.PtrToStringUni(it[i].szName)!, it[i].V.Value));
            } finally { Marshal.FreeHGlobal(buf); }
        }
        return res;
    }
    public void Dispose() => Pdh.PdhCloseQuery(_q);
}
```

**Counter set (English paths, all ✅ present on 26300):**

| Metric | Path | Notes |
|---|---|---|
| CPU total | `\Processor Information(_Total)\% Processor Utility` and `% Processor Time` | See 1.2 |
| Per core | `\Processor Information(*)\% Processor Utility` | Instances `0,0…0,15`, `0,_Total`, `_Total` (✅ n=18). Skip names containing `_Total`; sort by (group, index) |
| CPU clock | `\Processor Information(_Total)\Processor Frequency` × `% Processor Performance` / 100 | ✅ 4700 × 1.081 = 5083 MHz. Also `\Processor Information(*)\Actual Frequency` (✅ 5067 MHz; may be missing on older builds). `Win32_Processor.CurrentClockSpeed` is stuck at base (✅ 4700) |
| Disk active % | `\PhysicalDisk(*)\% Idle Time` → 100 − idle | Instances `0 C:`, `1 D:`. Leading number = `MSFT_PhysicalDisk.DeviceId`. Avoid `% Disk Time` (can exceed 100). Plus `Disk Read/Write Bytes/sec` and `Avg. Disk sec/Transfer` |
| Standby cache | `\Memory\Standby Cache Normal Priority Bytes`, plus `…Reserve`, `…Core` | For the "cached" display |
| GPU engines | `\GPU Engine(*)\Utilization Percentage` | `pid_10248_luid_0x00000000_0x0000E1AE_phys_0_eng_0_engtype_3D` (PDH keeps case, Get-Counter lowercases → compare case-insensitively) |
| VRAM used | `\GPU Adapter Memory(*)\Dedicated Usage` (bytes); `Shared Usage` | Instance `luid_0x00000000_0x0000E1AE_phys_0` |
| Network | Prefer managed `NetworkInterface.GetAllNetworkInterfaces()` → `GetIPStatistics().BytesReceived/BytesSent` deltas | PDH `\Network Interface(*)\Bytes Total/sec` instance names are mangled descriptions (`(`→`[`, `#`→`_`) and hard to map |

### 1.2 What CPU % to display

Since Windows 8, Task Manager has used **% Processor Utility**, which scales by frequency against base clock. It exceeds 100 % under boost and Task Manager caps it (KB3200459). In 2025 (Insider 26120.3360, then KB5064081 / 26100.5074) Microsoft standardized the Processes tab to ΔCPU time / (Δt × logical CPUs) and added an optional "CPU Utility" column in Details.

**Recommendation:** show `% Processor Utility` capped at 100 as «Загрузка», with `% Processor Time` in the tooltip. At idle they're close (✅ 6.73 vs 6.42). Under all-core boost they diverge by about 5–15 %.

### 1.3 Memory (no PDH needed)

`GetPerformanceInfo` (psapi / `K32GetPerformanceInfo`) returns committed memory, cache, and handle/process/thread counts in one call. All sizes are in pages, so multiply by `PageSize`.

```csharp
[StructLayout(LayoutKind.Sequential)] struct PERFORMANCE_INFORMATION {   // 104 B on x64
  public uint cb; public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
  SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize; public uint HandleCount, ProcessCount, ThreadCount; }
[DllImport("psapi.dll", SetLastError = true)] static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pi, uint cb);
```

- RAM "used" = PhysicalTotal − PhysicalAvailable, as in Task Manager. "Available" includes standby.
- "Committed" = CommitTotal / CommitLimit.
- `GlobalMemoryStatusEx` is equivalent: `MEMORYSTATUSEX`, `dwLength = 64`; commit = `ullTotalPageFile − ullAvailPageFile`.

### 1.4 GPU utilization: aggregate like Task Manager

Microsoft's DirectX blog (Task Manager design): an adapter's GPU % is the **busiest engine**, not an average. Per engine, sum the values across PIDs.

```csharp
static readonly Regex Rx = new(@"^pid_(\d+)_luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.+)$",
                               RegexOptions.IgnoreCase | RegexOptions.Compiled);
var eng = new Dictionary<(string luid, int phys, int idx), (string type, double sum)>();
var pid3D = new Dictionary<int, double>();                       // reused for game detection (§6)
foreach (var (inst, v) in s.Array(@"\GPU Engine(*)\Utilization Percentage")) {
    var m = Rx.Match(inst); if (!m.Success) continue;
    var k = (m.Groups[2].Value.ToUpperInvariant(), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value));
    eng[k] = (m.Groups[5].Value, (eng.TryGetValue(k, out var e) ? e.sum : 0) + v);
    if (m.Groups[5].Value.Equals("3D", StringComparison.OrdinalIgnoreCase))
        pid3D[int.Parse(m.Groups[1].Value)] = pid3D.GetValueOrDefault(int.Parse(m.Groups[1].Value)) + v;
}
var gpuPct = eng.GroupBy(x => x.Key.luid).ToDictionary(g => g.Key, g => Math.Min(100, g.Max(x => x.Value.sum)));
```

To map a LUID to a name, build the string `$"0x{luid.High:X8}_0x{luid.Low:X8}"` from `D3DKMT_ADAPTERINFO`. Then get the name from `KMTQAITYPE_ADAPTERREGISTRYINFO` (8) and total VRAM from `KMTQAITYPE_GETSEGMENTSIZE` (3), both ✅ (RTX 5070 Ti: 15995 MB dedicated). No DXGI dependency is needed.

### 1.5 GPU temperature, fan, power and memory clock via D3DKMT (✅ tested)

- Exports come from `gdi32.dll`. No admin is needed.
- Needs WDDM ≥ 2.4 drivers (the same source Task Manager uses for GPU temperature).
- A value of 0 means not reported (fan 0 RPM can be zero-RPM idle).
- Skip the Microsoft Basic Render adapter. On it the queries return 0xC0000034 / 0xC000000D ✅.

```csharp
[StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
[StructLayout(LayoutKind.Sequential)] struct D3DKMT_ADAPTERINFO { public uint hAdapter; public LUID AdapterLuid; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; } // 20 B ✅
[StructLayout(LayoutKind.Sequential)] struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public nint pAdapters; }   // 16 B
[StructLayout(LayoutKind.Sequential)] struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public nint pPrivateDriverData; public uint PrivateDriverDataSize; } // 24 B ✅
[StructLayout(LayoutKind.Explicit, Size = 64)] struct D3DKMT_ADAPTER_PERFDATA {
  [FieldOffset(0)] public uint PhysicalAdapterIndex;      // in (LDA index, 0)
  [FieldOffset(8)] public ulong MemoryFrequency;          // Hz
  [FieldOffset(16)] public ulong MaxMemoryFrequency; [FieldOffset(24)] public ulong MaxMemoryFrequencyOC;
  [FieldOffset(32)] public ulong MemoryBandwidth;         // bytes since last query
  [FieldOffset(40)] public ulong PCIEBandwidth;
  [FieldOffset(48)] public uint FanRPM;
  [FieldOffset(52)] public uint Power;                    // 0.1 % of max
  [FieldOffset(56)] public uint Temperature;              // 0.1 °C
  [FieldOffset(60)] public byte PowerStateOverride; }
[StructLayout(LayoutKind.Explicit, Size = 40)] struct D3DKMT_ADAPTER_PERFDATACAPS {
  [FieldOffset(0)] public uint PhysicalAdapterIndex; [FieldOffset(8)] public ulong MaxMemoryBandwidth; [FieldOffset(16)] public ulong MaxPCIEBandwidth;
  [FieldOffset(24)] public uint MaxFanRPM; [FieldOffset(28)] public uint TemperatureMax; [FieldOffset(32)] public uint TemperatureWarning; } // 0.1 °C
const int KMTQAITYPE_GETSEGMENTSIZE = 3, KMTQAITYPE_ADAPTERREGISTRYINFO = 8, KMTQAITYPE_NODEPERFDATA = 61,
          KMTQAITYPE_ADAPTERPERFDATA = 62, KMTQAITYPE_ADAPTERPERFDATA_CAPS = 63, KMTQAITYPE_WDDM_2_7_CAPS = 70;
[DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 p);
[DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO p);
[DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref uint hAdapter);   // D3DKMT_CLOSEADAPTER { hAdapter }

static unsafe int Query<T>(uint h, int type, ref T data) where T : unmanaged {
    fixed (T* p = &data) { var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = h, Type = type, pPrivateDriverData = (nint)p, PrivateDriverDataSize = (uint)sizeof(T) };
                           return D3DKMTQueryAdapterInfo(ref q); }       // NTSTATUS 0 = OK
}
// Enumerate: 1st call with pAdapters=0 returns an UPPER BOUND (✅ 34), 2nd call sets the real count (✅ 3).
// Open handles once, poll perf data each second, D3DKMTCloseAdapter each handle on shutdown.
```

✅ Results on this machine:
- RTX 5070 Ti: `Temperature=409` → 40.9 °C, `Power=83` → 8.3 %, memory clock 810 MHz, `TemperatureMax` 88 °C, `MaxFanRPM` 3000.
- iGPU: 39 °C.

### 1.6 NVIDIA NVML (optional extra: watts, clocks, fan %, BAR1)

`nvml.dll` sits in `C:\Windows\System32` on DCH drivers (✅). Load it only from System32 to avoid DLL hijacking: `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`, or `NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "nvml.dll"))`. All calls return `nvmlReturn_t` (0 = success).

```csharp
[DllImport("nvml.dll")] static extern int nvmlInit_v2();                 [DllImport("nvml.dll")] static extern int nvmlShutdown();
[DllImport("nvml.dll")] static extern int nvmlDeviceGetCount_v2(out uint n);
[DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out nint dev);
[DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(nint dev, int sensor /*0=GPU*/, out uint c); // deprecated in newer NVML in favour of nvmlDeviceGetTemperatureV, still exported
[DllImport("nvml.dll")] static extern int nvmlDeviceGetClockInfo(nint dev, int type /*0 GFX,1 SM,2 MEM,3 VIDEO*/, out uint mhz);
[DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(nint dev, out uint milliwatts);
[DllImport("nvml.dll")] static extern int nvmlDeviceGetFanSpeed(nint dev, out uint pct);
[DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(nint dev, out NvUtil u); // struct { uint gpu, memory; }
[DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(nint dev, out NvMem m);        // struct { ulong total, free, used; }
[DllImport("nvml.dll")] static extern int nvmlDeviceGetBAR1MemoryInfo(nint dev, out NvBar1 b);   // struct { ulong bar1Total, bar1Free, bar1Used; }
```

### 1.7 CPU temperature without ring0 (classified)

- **`MSAcpi_ThermalZoneTemperature` (root\WMI): UNRELIABLE.**
  - Needs admin.
  - Values are in tenths of Kelvin: `x / 10 − 273.15`.
  - It reads the ACPI `_TMP` of a firmware thermal zone (board or PCH), not CPU Tctl/Tdie. It's often static (for example a constant 27.8 °C) or missing.
  - ✅ This desktop has **zero** thermal-zone instances. `\Thermal Zone Information(*)\High Precision Temperature` (no admin, tenths of K) reads the same source and has the same problem.
  - Show it only if the value changes over time, labelled «ACPI‑зона».
- **AMD/Intel Tctl requires MSR/SMN access through a driver.** WinRing0 is flagged by Defender (Vigorf.A, 2025). LibreHardwareMonitor and Fan Control moved to **PawnIO**, a signed driver installed separately, which is still kernel code.
- **Recommendation:**
  - Default to «CPU: недоступно без драйвера».
  - Optional *external providers* only if the user already runs them: the HWiNFO shared-memory sensor interface, and the LHM WMI namespace `root\LibreHardwareMonitor` (`Sensor` class).
  - Never bundle a driver.

---

## 2. System Restore

| Item | Detail |
|---|---|
| Global state | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore` `RPSessionInterval` (DWORD, ✅ = 1 when enabled). WMI `root\default:SystemRestoreConfig` gives `RPSessionInterval` and `DiskPercent` (✅ 15). Both readable without admin |
| Policy (respect it, never override) | `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore` `DisableSR`=1 / `DisableConfig`=1. `DisableSR` under the non-policy key is legacy (XP) |
| Per-volume (admin) | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients` value `{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}` (REG_MULTI_SZ, entries like `\\?\Volume{…}\:(C:%)`). Not documented, so treat it as a hint. Access was denied without admin ✅. Shadow storage: `Win32_ShadowStorage.MaxSpace` |
| Enable C: | `SystemRestore.Enable(Drive="C:\\", WaitTillEnabled=true)`, the same as `Enable-ComputerRestore`. Needs the VSS service. Uses disk space, so ask the user first |
| Create | `SystemRestore.CreateRestorePoint(Description, RestorePointType=12 MODIFY_SETTINGS, EventType=100 BEGIN_SYSTEM_CHANGE)`. Other types: 0 APP_INSTALL, 1 APP_UNINSTALL, 10 DRIVER_INSTALL, 13 CANCELLED_OPERATION; event 101 END |
| 24 h limit (Win8+) | If any point exists from the last 24 h, the call returns **success but creates nothing**. `HKLM\…\SystemRestore\SystemRestorePointCreationFrequency` (DWORD, minutes): 0 means never skip. Microsoft documents this value for apps to create; it doesn't exist by default |
| List (admin) | `SELECT * FROM SystemRestore` (root\default): `SequenceNumber`, `CreationTime` (CIM datetime → `ManagementDateTimeConverter.ToDateTime`), `Description`, `RestorePointType`, `EventType` |
| UI | `%SystemRoot%\System32\rstrui.exe` (ShellExecute). Settings: `SystemPropertiesProtection.exe` |

```csharp
static uint MaxSeq() { using var s = new ManagementObjectSearcher(@"root\default", "SELECT SequenceNumber FROM SystemRestore");
                       return s.Get().Cast<ManagementObject>().Select(o => (uint)o["SequenceNumber"]).DefaultIfEmpty(0u).Max(); }
static (uint rc, bool created) CreateRestorePoint(string desc) {          // run on a background thread; takes 10–60 s
    const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", val = "SystemRestorePointCreationFrequency";
    using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(key, writable: true)!;
    object? old = k.GetValue(val);                                         // journal it (undo center)
    k.SetValue(val, 0, RegistryValueKind.DWord);
    try {
        uint before = MaxSeq();
        using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
        using var p = cls.GetMethodParameters("CreateRestorePoint");
        p["Description"] = desc; p["RestorePointType"] = 12u; p["EventType"] = 100u;
        using var r = cls.InvokeMethod("CreateRestorePoint", p, null);
        return ((uint)r["ReturnValue"], MaxSeq() > before);                 // verify: rc==0 alone is NOT proof
    } finally { if (old is null) k.DeleteValue(val, false); else k.SetValue(val, old, RegistryValueKind.DWord); }
}
```

`SRSetRestorePointW` (srclient.dll) is an alternative. Its structs use `#pragma pack(1)`:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
struct RESTOREPOINTINFOW { public uint dwEventType, dwRestorePtType; public long llSequenceNumber;
                           [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szDescription; }
[StructLayout(LayoutKind.Sequential, Pack = 1)] struct STATEMGRSTATUS { public uint nStatus; public long llSequenceNumber; }
```

It requires `CoInitializeSecurity` to let System, LocalService and NetworkService call back into the process. In WPF, COM is already initialized, so that call usually fails with `RPC_E_TOO_LATE`. **Prefer WMI.**

---

## 3. Elevation and publishing

**Manifest options:**
- `asInvoker`, then relaunch with `runas` (recommended). The UI can still run read-only (monitoring, library) if the user declines UAC.
- `requireAdministrator`. This is simpler, but:
  - Autostart from the Run key is blocked. You'd need a scheduled task with `/RL HIGHEST /SC ONLOGON`, which the TaskScheduler package can create.
  - UIPI blocks drag-and-drop from Explorer.
  - **Any game or launcher you start inherits admin.**
- In both cases, launch games de-elevated:
  - Use `explorer.exe "steam://rungameid/730"`. Explorer hands the URI to the existing non-elevated shell. This is common practice but undocumented.
  - The more robust route is Raymond Chen's documented-COM technique: `IShellWindows.FindWindowSW(SWC_DESKTOP)` → `IShellView` → `IShellDispatch2.ShellExecute`.

```xml
<requestedExecutionLevel level="asInvoker" uiAccess="false" />
<!-- + <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>, <dpiAwareness>PerMonitorV2</dpiAwareness>, <longPathAware>true</longPathAware> -->
```

```csharp
bool elevated = Environment.IsPrivilegedProcess;                    // .NET 8+
// can the user elevate at all? GetTokenInformation(TokenElevationType): 3 = Limited (UAC-filtered admin)
static bool Relaunch(string args) {
    try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" }); return true; }
    catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return false; }  // ERROR_CANCELLED
}   // release the single-instance mutex before relaunch
```

**csproj (.NET 10):**

```xml
<OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF>
<AllowUnsafeBlocks>true</AllowUnsafeBlocks><ApplicationManifest>app.manifest</ApplicationManifest>
<RuntimeIdentifier>win-x64</RuntimeIdentifier><SelfContained>true</SelfContained>
<PublishSingleFile>true</PublishSingleFile><IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
<PublishTrimmed>false</PublishTrimmed>   <!-- NETSDK1168: "WPF is not supported or recommended with trimming enabled" -->
<SatelliteResourceLanguages>ru;en</SatelliteResourceLanguages><DebugType>embedded</DebugType>
<!-- optional PublishReadyToRun=true (faster start, bigger). Native AOT: not supported for WPF. -->
```

- Don't use a `net10.0-windows10.0.x` TFM unless you need WinRT. It pulls in the Microsoft.Windows.SDK.NET projection, roughly 20+ MB.
- With `IncludeNativeLibrariesForSelfExtract`, WPF's native DLLs (`wpfgfx_cor3`, `PresentationNative_cor3`, `PenImc_cor3`, `D3DCompiler_47_cor3`, `vcruntime140_cor3`) are extracted to `%TEMP%\.net\<app>\<hash>`.
- ⚠️ For an always-elevated app, a user-writable extraction directory lets same-user code plant DLLs. Prefer an installer to Program Files with native DLLs next to the exe, or point `DOTNET_BUNDLE_EXTRACT_BASE_DIR` at an ACL-protected directory. Sign the exe; it cuts antivirus false positives for "optimizer" heuristics.

---

## 4. Power plans: use P/Invoke, never parse `powercfg`

✅ `powercfg /list` printed Russian names in CP866, and the labels are localized too («GUID схемы питания»). The P/Invoke route below returns the **localized friendly name** (✅ «Высокая производительность»). Key all logic on GUIDs.

```csharp
const uint ACCESS_SCHEME = 16; const uint ERROR_NO_MORE_ITEMS = 259;
[DllImport("powrprof.dll")] static extern uint PowerEnumerate(nint root, nint scheme, nint sub, uint access, uint index, byte[]? buf, ref uint size);
[DllImport("powrprof.dll")] static extern uint PowerReadFriendlyName(nint root, in Guid scheme, nint sub, nint setting, byte[]? buf, ref uint size); // UTF-16, 2-call
[DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(nint root, out nint pGuid);            // LocalFree(pGuid)
[DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(nint root, in Guid scheme);
[DllImport("powrprof.dll")] static extern uint PowerDuplicateScheme(nint root, in Guid src, ref nint pDst); // pDst=0 → allocated, LocalFree
[DllImport("powrprof.dll")] static extern uint PowerWriteFriendlyName(nint root, in Guid scheme, nint sub, nint setting, byte[] utf16z, uint size);
[DllImport("powrprof.dll")] static extern uint PowerDeleteScheme(nint root, in Guid scheme);
[DllImport("kernel32.dll")] static extern nint LocalFree(nint p);

for (uint i = 0; ; i++) { var b = new byte[16]; uint sz = 16;
    if (PowerEnumerate(0, 0, 0, ACCESS_SCHEME, i, b, ref sz) != 0) break;            // 259 = end ✅
    var g = new Guid(b); uint n = 0; PowerReadFriendlyName(0, g, 0, 0, null, ref n);
    var nb = new byte[n]; PowerReadFriendlyName(0, g, 0, 0, nb, ref n);
    var name = Encoding.Unicode.GetString(nb).TrimEnd('\0'); }
```

- **GUIDs:**
  - Balanced `381b4222-f694-41f0-9685-ff5bb260df2e`
  - High performance `8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` (✅ active on the dev machine)
  - Power saver `a1841308-3541-4fab-bc81-f71556f20b4a`
  - Ultimate `e9a42b02-d5df-448d-aa00-03f14749eb61` (hidden template, so duplicate it)
- **Apply:**
  1. Journal `{originalActive}`.
  2. Duplicate Ultimate with `PowerDuplicateScheme(Ultimate)` (admin).
  3. Rename it «MONOLITH Ultimate» and journal `{createdGuid}`.
  4. Make it active with `PowerSetActiveScheme`.
- **Revert:** call `PowerSetActiveScheme(original)`, falling back to Balanced if the original is gone. Then call `PowerDeleteScheme(createdGuid)`, only for the GUID we created; an active scheme can't be deleted.
- **Classification: DEBATED.**
  - Desktop gain is usually ≤1 %, at the cost of more idle power and heat.
  - AMD recommends Balanced for **dual-CCD X3D** chips (7950X3D/9950X3D), because the 3D V-Cache optimizer depends on Balanced and Game Bar. Single-CCD chips like the 9800X3D aren't affected.
  - Never apply on battery or Modern Standby. On Modern Standby (`GetPwrCapabilities().AoAc`), only Balanced exists; use the Windows 11 "Power mode" overlay instead (Best performance `ded574b5-45a0-4f42-8737-46345c09c238`). It is set with the documented `PowerSetUserConfiguredACPowerMode` / read with `PowerGetUserConfiguredACPowerMode` (Microsoft Learn, three GUIDs) — MONOLITH's `power-mode-best-performance` tweak does this through the journal; see gaming_tweaks.md §2 C.

---

## 5. Memory: standby purge and EmptyWorkingSet

```csharp
[DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int cls, ref int cmd, int len);
const int SystemMemoryListInformation = 80;                 // 0x50
// SYSTEM_MEMORY_LIST_COMMAND: 0 CaptureAccessedBits,1 CaptureAndResetAccessedBits,2 EmptyWorkingSets,
//                             3 FlushModifiedList,4 PurgeStandbyList,5 PurgeLowPriorityStandbyList
// 1) OpenProcessToken(TOKEN_ADJUST_PRIVILEGES 0x20|TOKEN_QUERY 0x8) → LookupPrivilegeValueW("SeProfileSingleProcessPrivilege")
//    → AdjustTokenPrivileges(SE_PRIVILEGE_ENABLED=2); check GetLastError()!=1300 (ERROR_NOT_ALL_ASSIGNED). Admin only.
int cmd = 4; int st = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)); // 0xC0000061 = privilege not held
```

- **Standby purge (the ISLC / RAMMap approach): DEBATED, mostly placebo.**
  - Standby pages already count as "available" and are reused instantly.
  - Purging forces disk re-reads, which means slower loads.
  - The 2018 stutter reports belonged to early Windows 10 builds. ISLC's own developer concedes the purge itself can cause a hitch, and user reports conflict.
  - Offer it only as a manual button with an explanation. No timer by default; at most, consider it when Available is under 5 %.
- **EmptyWorkingSet / MemoryEmptyWorkingSets: COUNTERPRODUCTIVE.** It moves pages to standby or modified lists, which causes immediate faults and stutter. This is the classic "RAM cleaner" placebo. **Don't implement it.**

---

## 6. Game boost and game library

**Priority and affinity (session-only, auto-revert when the game exits):**
- `Process.PriorityClass = High` (0x80) or `AboveNormal`. **Never `RealTime`**: it starves input and audio. Low risk, low gain.
- `ProcessorAffinity` is expert-only and off by default. It overrides the scheduler and covers only one processor group (≤64 logical CPUs). Prefer `SetProcessDefaultCpuSets`.
- Open game processes with `PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION` only. Never request VM read access and never inject (anti-cheat). Catch access-denied.
- Avoid persistent IFEO `…\Image File Execution Options\<exe>\PerfOptions\CpuPriorityClass` (DWORD 1 Idle, 2 Normal, 3 High, 5 BelowNormal, 6 AboveNormal). It's a known persistence vector that EDR flags.
- **Better, official and reversible:** apply EcoQoS to background apps during a session. Exclude audiodg, voice chat, anti-cheat and system processes.

```csharp
[StructLayout(LayoutKind.Sequential)] struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; } // {1, 1, 1}=EcoQoS on; {1,0,0}=default
[DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessInformation(nint h, int cls /*ProcessPowerThrottling=4*/, ref PROCESS_POWER_THROTTLING_STATE s, uint size /*12*/);
```

**Detecting game launches:**
- `Win32_ProcessStartTrace` needs **admin**:
  `new ManagementEventWatcher(new WqlEventQuery("SELECT ProcessID,ProcessName FROM Win32_ProcessStartTrace"))`.
  `ProcessName` may be truncated, so always resolve the path from the PID.
- Fallback without admin: poll `Process.GetProcesses()` every 2 s and diff the PIDs. For new PIDs, call `OpenProcess(0x1000 QUERY_LIMITED)` and then `QueryFullProcessImageNameW`.
- Match a process by **path prefix** against the library install folders, case-insensitive and normalized with a trailing `\`.
- Heuristics for unknown games:
  - per-PID `engtype_3D` above roughly 30 % for more than 10 s (from 1.4);
  - `SHQueryUserNotificationState`: 2 BUSY (fullscreen or borderless), 3 RUNNING_D3D_FULL_SCREEN.

**Library sources:**

| Store | Where | Fields |
|---|---|---|
| Steam | `HKCU\Software\Valve\Steam` `SteamPath` (forward slashes); `<SteamPath>\steamapps\libraryfolders.vdf` → `"libraryfolders"{"0"{"path" …}}` | Each library: `steamapps\appmanifest_<id>.acf` → `appid`, `name`, `installdir`, `StateFlags` (4 = installed). Folder = `<lib>\steamapps\common\<installdir>`. Launch `steam://rungameid/<id>`. Skip 228980 (redistributables) |
| Epic | `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item` (JSON) | `DisplayName`, `InstallLocation`, `LaunchExecutable` (relative), `AppName`, `CatalogNamespace`, `CatalogItemId`, `bIsIncompleteInstall`. Launch `com.epicgames.launcher://apps/<ns>%3A<itemId>%3A<AppName>?action=launch&silent=true` |
| GOG | `HKLM\SOFTWARE\GOG.com\Games\<gameId>` opened via `RegistryView.Registry32` | `gameName`, `path`, `gameID` (documented by GameFinder); `exe` / `launchCommand` usually present. More reliable: `<path>\goggame-<id>.info` JSON `playTasks[]` with `isPrimary`, `path` |
| Others | Ubisoft: `HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\<id>\InstallDir`. Xbox: `PackageManager` (needs a WinRT TFM) / `C:\XboxGames` | Lower priority |

---

## 7. Detection for recommendations

| Check | API | Interpretation | Admin? |
|---|---|---|---|
| SSD/HDD | `root\Microsoft\Windows\Storage:MSFT_PhysicalDisk` `MediaType` (0 unspecified, 3 HDD, 4 SSD, 5 SCM), `BusType` (7 USB, 11 SATA, 17 NVMe) | ✅ 4/17 for both NVMe drives | No |
| TRIM | `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem` `DisableDeleteNotification` (0 = TRIM on ✅); `fsutil behavior query DisableDeleteNotify` (mixed ASCII + CP866 text; regex `=\s*(\d)`) | | No |
| RAM speed | `Win32_PhysicalMemory` `ConfiguredClockSpeed`, `Speed`, `PartNumber`, `SMBIOSMemoryType` (26 DDR4, 34 DDR5) | ✅ Speed = Configured = 6000, so the pair is useless alone. Hint «XMP/EXPO, вероятно, выключен» when Configured ≤ JEDEC base (DDR4 ≤ 2666/3200, DDR5 ≤ 4800/5600) **and** the part number encodes a higher rating (`F5-6000…`, `…6000C36`, `KF560…`). Hint only; never touch BIOS | No |
| ReBAR | NVML `nvmlDeviceGetBAR1MemoryInfo.bar1Total` ≥ VRAM total (or `nvidia-smi -q -d MEMORY` "BAR1 … Total") | ✅ 16384 MiB ≥ 16303 MiB → enabled (256 MiB = off). AMD has no simple API (D3D12 `OPTIONS16.GPUUploadHeapSupported` is a proxy) | No |
| Secure Boot | `HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State` `UEFISecureBootEnabled` (✅ 1); `GetFirmwareType` (2 = UEFI ✅). `Confirm-SecureBootUEFI` needs admin | | No |
| TPM | `Tbsi_GetDeviceInfo` (tbs.dll) → `TPM_DEVICE_INFO.tpmVersion` (2 = TPM 2.0 ✅). `Win32_Tpm` needs admin | | No |
| VBS/HVCI | `root\Microsoft\Windows\DeviceGuard:Win32_DeviceGuard` `VirtualizationBasedSecurityStatus` (0 off, 1 enabled not running, 2 running ✅); `SecurityServicesRunning` (1 Credential Guard, 2 HVCI ✅). Config: `…\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity` `Enabled` | Show it. Turning it off is **SECURITY-WEAKENING**: never in profiles; at most an advanced opt-in with a warning | No |
| HAGS | D3DKMT `KMTQAITYPE_WDDM_2_7_CAPS` (70) → UINT bits: 0 HwSchSupported, 1 HwSchEnabled, 2 EnabledByDefault, 3 IndependentVidPnVSync. Config: `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers` `HwSchMode` (2 on, 1 off, absent = default) | ✅ NVIDIA 0xB = enabled with no registry value; iGPU 0x8 = unsupported. Changes need a reboot | No |
| Refresh rate | `EnumDisplayDevicesW` (attached flag 0x1) → `EnumDisplaySettingsW(dev, ENUM_CURRENT_SETTINGS=-1)` `dmDisplayFrequency` vs max over modes with the same `dmPelsWidth/Height` (`DEVMODEW` = 220 B ✅) | ✅ 240/240 and 180/180 Hz. If current < max, flag "monitor stuck at 60 Hz" and **open `ms-settings:display-advanced`**. Don't call `ChangeDisplaySettingsEx`: the API has no 15 s auto-revert. Exact rates: `QueryDisplayConfig` | No |
| Battery | `GetSystemPowerStatus` `BatteryFlag & 128` = no battery; `GetPwrCapabilities` `SystemBatteriesPresent`, `AoAc` | ✅ no battery | No |
| Free space | `DriveInfo.AvailableFreeSpace` | | No |

---

## 8. Running external tools: encoding, streaming, cancellation

**✅ Measured raw stdout when redirected on ru-RU:**
- powercfg, dism, schtasks, fsutil → **OEM CP866**. For example `8E E8 A8 A1 AA A0` = «Ошибка», and dism returned «Ошибка: 740», meaning elevation is required.
- sfc → **UTF‑16LE** with `\r\r\n` line endings.
- winget → **UTF‑8**, with `\r   - \r` spinners, progress bars and `…`-truncated columns.
- chcp itself writes ANSI 1251.

Use `GetOEMCP()` rather than hard-coding 866: with the "UTF‑8 beta" option enabled, the OEM code page is 65001. .NET needs `CodePagesEncodingProvider` for 866; it's in-box and only needs registering.

**Avoid parsing where an API exists:**

| Tool | Use instead |
|---|---|
| powercfg | P/Invoke (section 4) |
| dism | **Microsoft.Dism** (`DismApi.CheckImageHealth` / `RestoreImageHealth` with progress callbacks) |
| schtasks | **TaskScheduler** (COM) |
| pnputil | `/enum-drivers /format xml` (✅ supported on this build) |
| services | `ServiceController` / `Win32_Service` |

```csharp
[DllImport("kernel32.dll")] static extern uint GetOEMCP();
static readonly Lazy<Encoding> Oem = new(() => { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding((int)GetOEMCP()); });
static Encoding EncFor(string tool) => tool switch { "winget.exe" => new UTF8Encoding(false), "sfc.exe" => Encoding.Unicode, _ => Oem.Value };

public static async Task<int> RunAsync(string exePath, string args, Encoding enc, Action<string, bool> onLine, CancellationToken ct) {
    var psi = new ProcessStartInfo(exePath, args) { UseShellExecute = false, CreateNoWindow = true,       // full path: Environment.SystemDirectory
        RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = enc, StandardErrorEncoding = enc };
    using var p = Process.Start(psi)!;
    var t1 = Pump(p.StandardOutput, onLine, ct); var t2 = Pump(p.StandardError, onLine, ct);
    try { await p.WaitForExitAsync(ct); }
    catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } throw; }
    await Task.WhenAll(t1, t2); return p.ExitCode;
}
// onLine(text, transient): transient=true → UI replaces this line with the next one (progress via '\r')
static async Task Pump(StreamReader r, Action<string, bool> onLine, CancellationToken ct) {
    var buf = new char[4096]; var sb = new StringBuilder(); bool cr = false; int n;
    void Emit(bool transient) { if (sb.Length > 0 || !transient) onLine(sb.ToString(), transient); sb.Clear(); }
    while ((n = await r.ReadAsync(buf.AsMemory(), ct)) > 0)
        for (int i = 0; i < n; i++) { char c = buf[i];
            if (c == '\n') { Emit(false); cr = false; }
            else if (c == '\r') cr = true;                         // "\r\n" / "\r\r\n" → normal newline
            else { if (cr) { Emit(true); cr = false; } sb.Append(c); } }
    if (sb.Length > 0) Emit(false);
}
```

- Marshal `onLine` to the UI thread with `Dispatcher.InvokeAsync` and batch updates every ~100 ms.
- **Cancellation:** **disable Cancel for sfc, DISM servicing and winget installs.** Killing them mid-operation can leave a half-installed package or an interrupted servicing operation; offer cancel for read-only commands only.
- **winget:**
  - Pass `--disable-interactivity --accept-source-agreements`.
  - Pass `--accept-package-agreements` only after the user ticks consent in the UI.
  - Exit codes are HRESULTs in the 0x8A15xxxx range (see the winget-cli `returnCodes.md`).
  - winget is a per-user App Execution Alias, so it may be missing in an over-the-shoulder admin session.
- For PowerShell child processes, prefix the command with `[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);`.

---

## 9. NuGet packages (✅ versions and licenses checked on nuget.org)

| Package | Version | License | Use |
|---|---|---|---|
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM source generators |
| System.Management | 10.0.14 | MIT | WMI (restore, CIM, DeviceGuard) |
| System.ServiceProcess.ServiceController | 10.0.14 | MIT | Services manager |
| Microsoft.Windows.CsWin32 | 0.3.367 | MIT | Generated P/Invoke (Pdh*, Power*, EnumDisplay*, Tbsi*). Write D3DKMT by hand |
| Microsoft.Dism | 6.1.0 | MIT | DISM API with progress, no text parsing |
| TaskScheduler | 2.12.2 | MIT | Elevated autostart task, schtasks replacement |
| ValveKeyValue | 0.73.0.538 | MIT | Steam `.vdf` / `.acf` |
| H.NotifyIcon.Wpf | 2.4.1 | MIT | Tray icon |
| WPF-UI (optional) | 4.3.0 | MIT | Fluent styling |
| ScottPlot.WPF (optional) | 5.1.59 | MIT | Detailed history graphs (SkiaSharp native, roughly +10–15 MB) |
| LiveChartsCore.SkiaSharpView.WPF (alternative) | 2.0.5 | MIT | |

For dashboard sparklines, a **custom control** (ring buffer of 60–120 points, `StreamGeometry` in `OnRender`) needs no dependencies and is the cheapest option.

**Avoid:**
- **GameFinder** — GPL‑3.0‑only (✅), incompatible with closed source. Use it as a reference only.
- LibreHardwareMonitorLib / OpenHardwareMonitor — kernel drivers (PawnIO / WinRing0).
- `System.Diagnostics.PerformanceCounter` — replaced by direct PDH.

---

## 10. DPC/ISR measurement (private ETW system logger, read-only)

What the «Задержки драйверов (DPC/ISR)» card on Maintenance → Ремонт и диски → Диагностика does, and the rules its code
(`Modules/Tools/DpcLatency/*`) follows. Classification: **READ-ONLY** — nothing is written or changed; admin only; a fixed
60 s; never while a game session is active (`GameBoostState.IsActive`).

### 10.1 Session: a private system logger, never the NT Kernel Logger
- `StartTraceW` with `EVENT_TRACE_PROPERTIES`: `LogFileMode = EVENT_TRACE_REAL_TIME_MODE (0x100) | EVENT_TRACE_SYSTEM_LOGGER_MODE (0x02000000)`,
  `EnableFlags = EVENT_TRACE_FLAG_INTERRUPT (0x40) | EVENT_TRACE_FLAG_DPC (0x20)`, `Wnode.ClientContext = 1` (QPC),
  `Wnode.Flags = WNODE_FLAG_TRACED_GUID`, `Wnode.Guid` = a fresh GUID of our own (never `SystemTraceControlGuid`), 64 KB buffers,
  64–256 buffers, `FlushTimer = 1`. No log file: real-time delivery only.
- `EVENT_TRACE_SYSTEM_LOGGER_MODE` exists since Windows 8 (6.2, WDK 8 header; Geoff Chappell's `LogFileMode` notes). It lets any
  uniquely named session receive SystemTraceProvider (kernel) events. Windows allows at most **8 system loggers**; the first two
  slots are the «NT Kernel Logger» and the «Circular Kernel Context Logger». A full table returns `ERROR_NO_SYSTEM_RESOURCES` (1450) —
  MONOLITH reports it («закройте LatencyMon, xperf/WPR…») and does nothing else.
- The global **«NT Kernel Logger» is never started, stopped or queried** (another tool — LatencyMon, xperf, WPR, Process Lasso —
  may own it). On start `QueryAllTracesW` lists the sessions and only names beginning with `MONOLITH-DpcLatency-` (a leftover of a
  crashed MONOLITH) are stopped with `ControlTraceW(EVENT_TRACE_CONTROL_STOP)`. Session name: `MONOLITH-DpcLatency-{GuidN}`.
- Consumer: `OpenTraceW` with a real-time `EVENT_TRACE_LOGFILEW` (`ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME 0x100 |
  PROCESS_TRACE_MODE_EVENT_RECORD 0x10000000 | PROCESS_TRACE_MODE_RAW_TIMESTAMP 0x1000`; values from `evntcons.h`), `ProcessTrace`
  on a dedicated thread. After 60 s (or cancel, or any exception) `finally` calls `ControlTraceW(STOP)` — which ends
  `ProcessTrace` once the remaining buffers are delivered — then `CloseTrace`. `EventsLost` / `RealTimeBuffersLost` from the stop
  statistics are shown when non-zero.
- Game sessions: the card refuses to start while `GameBoostState.IsActive`, and a game session that begins during the 60 s
  stops the trace at once (outcome «Измерение остановлено: начался игровой сеанс», no verdict for the partial data).
- Permissions: only administrators, «Performance Log Users» and LocalSystem services may control trace sessions;
  `ERROR_ACCESS_DENIED` (5) → «нужны права администратора». MONOLITH refuses the card without elevation (`RequireAdmin`).

### 10.2 Events: PerfInfo DPC/ISR (verified)
- Class GUID (EVENT_RECORD `ProviderId` for classic kernel events): **PerfInfo `{ce1dbfb4-137e-4da6-87b0-3f59aa102cbc}`**;
  `EventDescriptor.Opcode` = MOF event type: **66 ThreadedDPC, 67 ISR, 68 DPC, 69 TimerDPC** (Microsoft Learn «PerfInfo class»,
  «DPC class», «ISR class»; PerfView `KernelTraceEventParser` registers the same opcodes against `PerfInfoTaskGuid`).
- Payload (`DPC`, `ISR : PerfInfo`): `InitialTime` (int64, the routine's entry time in the session clock, i.e. QPC ticks) at
  offset 0, `Routine` (pointer-sized address of the DPC/ISR routine) at offset 8; ISR adds `ReturnValue` (uint8), `Vector`
  (uint8), `Reserved` (uint16). PerfView: `InitialTimeQPC = GetInt64At(0)`, `Routine = GetAddressAt(8)`,
  `ElapsedTimeMSec = TimeStamp − QPCTimeToRelMSec(InitialTime)`. The event's own `TimeStamp` is the routine's **exit**, so
  **duration = EventHeader.TimeStamp − InitialTime** in QPC ticks → µs via `QueryPerformanceFrequency` (`Stopwatch.Frequency`).
  `PROCESS_TRACE_MODE_RAW_TIMESTAMP` keeps the timestamp in QPC units instead of converting it to FILETIME. Pointer size comes
  from `EVENT_HEADER_FLAG_32_BIT_HEADER`; negative or > 10 s values are clock artefacts and are dropped.
- Byte offsets in the x64 `EVENT_RECORD` (evntcons.h / evntprov.h; same as .NET's `EventDescriptor`: `m_level` @4, `m_opcode` @5):
  `EVENT_HEADER.Flags` @4, `TimeStamp` @16, `ProviderId` @24, `EVENT_DESCRIPTOR` @40 = { Id @40, Version @42, Channel @43,
  **Level @44, Opcode @45**, Task @46, Keyword @48 }; `UserDataLength` @86, `UserData` @96; sizeof 112. Parsed by
  `EtwSession.TryParseRecord`, unit-tested with a synthetic record (a decoy opcode value in Level must not be read).

### 10.3 Module attribution and SeDebugPrivilege
- Routine address → driver: `NtQuerySystemInformation(SystemModuleInformation = 11)` gives `RTL_PROCESS_MODULES` (ImageBase, ImageSize,
  file name — 296 bytes per entry on x64); fallback `EnumDeviceDrivers` + `GetDeviceDriverBaseNameW` (bases and names, no sizes →
  the nearest module below the address). Lookup is a binary search over bases (`ModuleMap`).
- **Windows 11 24H2+**: both APIs return **zeroed kernel addresses unless `SeDebugPrivilege` is enabled** in the caller's token
  (Microsoft Learn `EnumDeviceDrivers` remarks: «Starting with Windows 11 24H2 … requires SeDebugPrivilege … otherwise every
  address is NULL»; windows-internals.com «KASLR leaks restriction»: `SystemModuleInformation` keeps `DllBase = 0`). The privilege
  belongs to administrators only and is disabled by default, so `KernelModules.EnableDebugPrivilege` turns it on for MONOLITH's own
  token (`AdjustTokenPrivileges`), logs a refusal and falls back to «неизвестный модуль». Least privilege: the previous state is
  captured (`PreviousState`) and the privilege is disabled again right after the module list is read (unless it was already
  enabled), so later `OpenProcess` calls and child processes never inherit it; a failed restore is logged. Nothing about the
  system changes.

### 10.4 Thresholds (provenance: community practice, not a Microsoft standard)
- LatencyMon (Resplendence) is the de-facto community tool; its reports flag DPC/ISR routines whose execution time exceeds roughly
  1 ms as «likely to interrupt the audio stream resulting in dropouts, clicks and pops», and bucket counts at < 250 µs / 250–500 /
  500–1000 / 1000–2000 / 2000–4000 / ≥ 4000 µs. MONOLITH states the verdict as practice, not fact:
  **max < 500 µs — «в норме»; 500–1000 — «повышенные»; > 1000 — «высокие: возможны треск звука и рывки»**. Per module the report
  shows count, max µs, p99 µs (1 µs histogram buckets up to 10 ms, overflow bucket above) and total ms; flagged modules get a
  family hint (ndis.sys → network stack, nvlddmkm.sys → NVIDIA, dxgkrnl.sys → graphics subsystem, ACPI.sys → firmware/BIOS,
  storport/stornvme → storage, Wdf01000.sys → a WDF device driver, tcpip.sys, USBXHCI.SYS …) and the next step is the Drivers page.
  A fix is never promised.
- Competitors: Process Lasso 19 (Oct 2026 changelog) added a «LatencyTest» tool; the Apps catalog lists LatencyMon. Measuring
  replaces placebo tweaks — this is the honest alternative to «DPC tweaks».

### 10.5 Verified against (sources)
- Microsoft Learn: PerfInfo / DPC / ISR classes; EVENT_TRACE_PROPERTIES; StartTraceW; ControlTraceW; EVENT_TRACE_LOGFILEW;
  EnumDeviceDrivers (24H2 note); «Configuring and starting a SystemTraceProvider session» (8 system loggers, slots 1–2 reserved).
- Geoff Chappell: `WMI_LOGGER_INFORMATION.LogFileMode` (EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000, 6.2+).
- PerfView `src/TraceEvent/Parsers/KernelTraceEventParser.cs`: `DPCTraceData` / `ISRTraceData` offsets, opcodes, `PerfInfoTaskGuid`.
- mingw-w64 `evntcons.h`: PROCESS_TRACE_MODE_* values.
- windows-internals.com «KASLR leaks restriction» (24H2 SeDebugPrivilege for kernel addresses).
- Resplendence LatencyMon documentation and community reports (thresholds); Bitsum Process Lasso 19 changelog.

---

## Sources
- https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/ns-d3dkmthk-_d3dkmt_adapter_perfdata
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/ne-d3dkmthk-_kmtqueryadapterinfotype
- https://github.com/winsiderss/systeminformer/blob/master/plugins/ExtendedTools/d3dkmt/d3dkmthk.h (explicit enum values 62/63/70)
- https://ntdoc.m417z.com/d3dkmt_adapter_perfdatacaps
- https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/
- https://www.bleepingcomputer.com/news/microsoft/windows-11-kb5064081-update-clears-up-cpu-usage-metrics-in-task-manager/
- https://support.microsoft.com/en-us/help/3200459
- https://learn.microsoft.com/en-us/windows/win32/api/srrestoreptapi/nf-srrestoreptapi-srsetrestorepointw
- https://learn.microsoft.com/en-us/windows/win32/sr/createrestorepoint-systemrestore
- https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/ (NETSDK1168)
- https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview
- https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerenumerate
- https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerduplicatescheme
- https://docs.nvidia.com/deploy/nvml-api/
- https://www.notebookcheck.net/AMD-Ryzen-9-9950X3D-Review-Ryzen-7-9800X3D-levels-of-gaming-performance-and-power-efficiency-for-50-markup-over-the-Ryzen-9-9950X.976049.0.html
- https://www.wagnardsoft.com/forums/viewtopic.php?p=7076
- https://github.com/erri120/GameFinder/wiki/GOG-Galaxy
- https://jayd.ml/games/2020/05/16/epic-games-store-steam-libraries.html
- https://github.com/LibreHardwareMonitor/LibreHardwareMonitor (PawnIO switch, PRs #1857/#1908)
- https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md
- https://learn.microsoft.com/en-us/answers/questions/3919778/changing-overlay-power-mode-with-script
- https://learn.microsoft.com/en-us/windows/win32/etw/perfinfo
- https://learn.microsoft.com/en-us/windows/win32/etw/dpc
- https://learn.microsoft.com/en-us/windows/win32/etw/isr
- https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_properties
- https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracew
- https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-controltracew
- https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_logfilew
- https://learn.microsoft.com/en-us/windows/win32/etw/configuring-and-starting-a-systemtraceprovider-session
- https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-enumdevicedrivers
- https://geoffchappell.com/studies/windows/km/ntoskrnl/api/etw/traceapi/wmi_logger_information/logfilemode.htm
- https://github.com/microsoft/perfview/blob/main/src/TraceEvent/Parsers/KernelTraceEventParser.cs
- https://windows-internals.com/kaslr-leaks-restriction/
- https://www.resplendence.com/latencymon
- https://bitsum.com/current_version_processlasso.txt
