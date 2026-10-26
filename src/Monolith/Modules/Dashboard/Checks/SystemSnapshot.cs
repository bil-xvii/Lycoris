using Monolith.Core;

namespace Monolith.Modules.Dashboard.Checks;

/// <summary>One installed memory module (Win32_PhysicalMemory).</summary>
public sealed record RamModule(string PartNumber, int Speed, int ConfiguredSpeed, int SmbiosMemoryType, ulong Capacity, string Manufacturer = "");

/// <summary>One display mode reported by EnumDisplaySettings.</summary>
public readonly record struct DisplayMode(int Width, int Height, int Frequency, bool Interlaced = false);

/// <summary>An active monitor: friendly name, current mode and the best refresh rate it offers at that resolution.</summary>
public sealed record DisplayInfo(string Name, int Width, int Height, int CurrentHz, int MaxHz, double? ExactHz = null);

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

    /// <summary>Sources that report a pending restart (empty = none).</summary>
    public IReadOnlyList<string> PendingReboot { get; init; } = [];

    /// <summary>NTFS DisableDeleteNotify: true = TRIM off. Null = unknown.</summary>
    public bool? TrimDisabled { get; init; }

    public GpuInfo? PrimaryGpu { get; init; }

    /// <summary>Enabled startup entries (Run keys + Startup folders). Null = unknown.</summary>
    public int? StartupCount { get; init; }

    public VbsState Vbs { get; init; }
}
