using Monolith.Core;

namespace Monolith.Modules.Dashboard.Checks;

/// <summary>One installed memory module (Win32_PhysicalMemory).</summary>
public sealed record RamModule(string PartNumber, int Speed, int ConfiguredSpeed, int SmbiosMemoryType, ulong Capacity, string Manufacturer = "");

/// <summary>One display mode reported by EnumDisplaySettings.</summary>
public readonly record struct DisplayMode(int Width, int Height, int Frequency, bool Interlaced = false);

/// <summary>
/// An active monitor: friendly name, current mode and the best refresh rate it offers at that resolution.
/// <see cref="AdapterKey"/> is the LUID of the graphics adapter that drives it ("" = unknown), in <see cref="Monitoring.MonitorMath.LuidKey"/> form.
/// </summary>
public sealed record DisplayInfo(string Name, int Width, int Height, int CurrentHz, int MaxHz, double? ExactHz = null,
    string AdapterKey = "", bool IsInternal = false, bool IsPrimary = false);

/// <summary>A graphics adapter as the display stack sees it: LUID key, driver name and the number of display outputs it has.</summary>
public sealed record GpuAdapter(string Key, string Name, int DisplayTargets);

/// <summary>How the PC boots (GetFirmwareType).</summary>
public enum FirmwareKind { Unknown, LegacyBios, Uefi }

/// <summary>TPM as reported by the TPM Base Services (Tbsi_GetDeviceInfo).</summary>
public enum TpmState { Unknown, NotFound, V12, V20 }

/// <summary>MSFT_PhysicalDisk.HealthStatus: 0 Healthy, 1 Warning, 2 Unhealthy, 5 / missing = Unknown.</summary>
public enum DiskHealthStatus { Unknown, Healthy, Warning, Unhealthy }

/// <summary>
/// One physical drive: Windows' own health verdict plus the reliability counters (MSFT_StorageReliabilityCounter,
/// read with administrator rights only). Null counters mean "not reported" — never "zero".
/// </summary>
public sealed record DiskHealth(string Name, string BusType, string MediaType, DiskHealthStatus Status,
    int? WearPercent = null, int? TemperatureC = null, int? TemperatureMaxC = null,
    ulong? ReadErrorsUncorrected = null, ulong? WriteErrorsUncorrected = null)
{
    public bool HasCounters => WearPercent is not null || TemperatureC is not null || ReadErrorsUncorrected is not null || WriteErrorsUncorrected is not null;
}

/// <summary>System log events the stability check looks at.</summary>
public enum StabilityEventKind
{
    /// <summary>Kernel-Power 41: the previous session ended without a clean shutdown.</summary>
    KernelPower41,
    /// <summary>WER-SystemErrorReporting 1001: a bug check (blue screen) was recorded.</summary>
    BugCheckReport,
    /// <summary>EventLog 6008: the previous shutdown was unexpected.</summary>
    UnexpectedShutdown,
    /// <summary>Display 4101: the graphics driver stopped responding and recovered (TDR).</summary>
    GpuReset,
    /// <summary>Microsoft-Windows-WHEA-Logger: a hardware error (corrected or fatal).</summary>
    Whea,
}

/// <summary>One event: time, kind, event id and level (1 critical, 2 error, 3 warning, 4 information), bug-check code (0 = none).</summary>
public sealed record StabilityEvent(DateTime Time, StabilityEventKind Kind, int EventId = 0, int Level = 0, uint BugCheck = 0, bool PowerButton = false);

/// <summary>The events of the last <c>Since…now</c> window. <see cref="OldestRecord"/> = first event the System log still holds.</summary>
public sealed record StabilityLog(DateTime Since, DateTime? OldestRecord, IReadOnlyList<StabilityEvent> Events, bool Truncated = false);

/// <summary>Smart App Control («Интеллектуальное управление приложениями»).</summary>
public enum SmartAppControlState { Unknown, Off, Evaluation, On }

/// <summary>
/// Read-only NVML facts for the primary NVIDIA card: BAR1 window vs. VRAM (Resizable BAR) and the PCIe link.
/// MaxGen / MaxWidth are "possible with this device and system" (they already include the slot);
/// GpuMaxGen is what the card alone supports. <see cref="UnderLoad"/> = the current values were sampled at ≥ 50 % GPU load.
/// </summary>
public sealed record GpuLinkFacts(string GpuName, ulong? Bar1Total = null, ulong? VramTotal = null,
    int? CurrentGen = null, int? CurrentWidth = null, int? MaxGen = null, int? MaxWidth = null, int? GpuMaxGen = null, bool UnderLoad = false);

/// <summary>A device that reports a Device Manager error code.</summary>
public sealed record ProblemDevice(string Name, int Code);

public enum HagsState { Unknown, Unsupported, Off, On }

public enum VbsState { Unknown, Off, VbsOnly, MemoryIntegrity }

public enum PowerPlanKind { Unknown, Balanced, HighPerformance, Ultimate, PowerSaver, Other }

/// <summary>The system drive: model, bus/media and space.</summary>
public sealed record SystemDisk(string Model, string MediaType, string BusType, ulong Free, ulong Total)
{
    public bool IsSsd => MediaType is "SSD" or "SCM" || BusType == "NVMe";
}

/// <summary>
/// Everything the health checks look at, collected once (off the UI thread) by <see cref="SystemInspector"/>.
/// The checks themselves (<see cref="HealthRules"/>) are pure functions of this snapshot.
/// Null / Unknown values mean "could not be read" and the related check is skipped.
/// </summary>
public sealed record SystemSnapshot
{
    public DateTime Now { get; init; } = DateTime.Now;
    public TimeSpan Uptime { get; init; }
    public bool IsLaptop { get; init; }
    /// <summary>
    /// False when the battery/chassis facts behind <see cref="IsLaptop"/> could not be read (SystemProfile.HardwareFactsKnown):
    /// the default «desktop» must then not be used to offer laptop-forbidden levers such as hibernation off.
    /// </summary>
    public bool HardwareFactsKnown { get; init; } = true;
    /// <summary>A laptop running on battery right now.</summary>
    public bool OnBattery { get; init; }
    public string CpuName { get; init; } = "";
    public int CpuCores { get; init; }

    public IReadOnlyList<RamModule> RamModules { get; init; } = [];
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = [];

    public bool? RestoreEnabled { get; init; }
    public bool RestoreBlockedByPolicy { get; init; }

    public SystemDisk? SystemDisk { get; init; }
    public ulong SystemDriveFree { get; init; }
    public ulong SystemDriveTotal { get; init; }

    /// <summary>Null when the device list could not be read.</summary>
    public IReadOnlyList<ProblemDevice>? ProblemDevices { get; init; }

    public bool? GameModeOn { get; init; }
    public bool? BackgroundRecordingOn { get; init; }
    public HagsState Hags { get; init; }

    public PowerPlanKind PowerPlan { get; init; }
    public string PowerPlanName { get; init; } = "";

    /// <summary>GetSystemPowerStatus.ACLineStatus: true = mains power, false = battery. Null = unknown (255).</summary>
    public bool? OnAcPower { get; init; }
    /// <summary>GetSystemPowerStatus.SystemStatusFlag: true = energy saver («Энергосбережение») is on. Null = unknown.</summary>
    public bool? EnergySaverOn { get; init; }
    /// <summary>
    /// Windows 11 user-configured AC power mode (PowerGetUserConfiguredACPowerMode): Guid.Empty = balanced,
    /// <see cref="Tweaks.TweakCatalog.PowerModeBestEfficiency"/> / <see cref="Tweaks.TweakCatalog.PowerModeBestPerformance"/>.
    /// Null = unknown or a build without the function (pre-22H2).
    /// </summary>
    public Guid? AcPowerMode { get; init; }

    /// <summary>Size of hiberfil.sys on the system drive (attributes only; the file is never opened). Null = absent or unknown.</summary>
    public ulong? HiberfilBytes { get; init; }
    /// <summary>Allocated size of the page files on the system drive (Win32_PageFileUsage). Null = none there or unknown.</summary>
    public ulong? PagefileBytes { get; init; }
    /// <summary>True when a page file lives on another drive (a deliberate move — the page-file repair must not be offered). Null = unknown.</summary>
    public bool? PagefileOnOtherDrive { get; init; }
    /// <summary>Win32_ComputerSystem.AutomaticManagedPagefile. Null = unknown.</summary>
    public bool? PagefileSystemManaged { get; init; }
    /// <summary>
    /// Get-WindowsReservedStorageState: "Enabled" / "Disabled". Read only with administrator rights and only when the
    /// system drive is already low on space (it is a servicing query, not something to run on every visit). Null = unknown.
    /// </summary>
    public string? ReservedStorageState { get; init; }

    /// <summary>Sources that report a pending restart (empty = none).</summary>
    public IReadOnlyList<string> PendingReboot { get; init; } = [];

    /// <summary>NTFS DisableDeleteNotify: true = TRIM off. Null = unknown.</summary>
    public bool? TrimDisabled { get; init; }

    public GpuInfo? PrimaryGpu { get; init; }

    /// <summary>Enabled startup entries (Run keys + Startup folders). Null = unknown.</summary>
    public int? StartupCount { get; init; }

    public VbsState Vbs { get; init; }

    /// <summary>True when MONOLITH runs with administrator rights (some sources are readable only then).</summary>
    public bool Elevated { get; init; }
    /// <summary>Windows build number (22000+ = Windows 11). 0 = unknown.</summary>
    public int OsBuild { get; init; }

    /// <summary>Every graphics adapter Windows reports (Win32_VideoController).</summary>
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    /// <summary>Adapters by LUID with their display outputs (D3DKMT + QueryDisplayConfig). Empty = unknown.</summary>
    public IReadOnlyList<GpuAdapter> Adapters { get; init; } = [];

    public FirmwareKind Firmware { get; init; }
    /// <summary>UEFI Secure Boot on/off. Null = unknown (or Legacy BIOS, where it does not exist).</summary>
    public bool? SecureBootOn { get; init; }
    public TpmState Tpm { get; init; }
    /// <summary>Names from the cached game library (the dashboard never starts a scan). Empty = none or unknown.</summary>
    public IReadOnlyList<string> InstalledGames { get; init; } = [];

    /// <summary>Physical drives with their health. Null = could not be read.</summary>
    public IReadOnlyList<DiskHealth>? Disks { get; init; }

    /// <summary>Crash / hardware-error events of the last 30 days. Null = the System log could not be read.</summary>
    public StabilityLog? Stability { get; init; }

    /// <summary>Microsoft vulnerable driver blocklist enforced (CiTool, admin only). Null = unknown.</summary>
    public bool? DriverBlocklistEnforced { get; init; }
    public SmartAppControlState SmartAppControl { get; init; }

    /// <summary>NVIDIA only: Resizable BAR and PCIe link facts. Null = not an NVIDIA card or NVML unavailable.</summary>
    public GpuLinkFacts? GpuLink { get; init; }
}
