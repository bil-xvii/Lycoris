using Lycoris.Core;
using Lycoris.Modules.Dashboard.Checks;

namespace Lycoris.Modules.Drivers.Hardware;

// ───────────────────────────── Snapshot (what the scanner read) ─────────────────────────────

/// <summary>One present (or remembered) PnP device from Win32_PnPEntity.</summary>
internal sealed record PnpDevice(
    string Name,
    string DeviceId,
    string PnpClass,
    string Manufacturer,
    string Service,
    int ErrorCode,
    bool? Present,
    IReadOnlyList<string> HardwareIds)
{
    /// <summary>A software device (G HUB virtual mouse / keyboard…): no physical bus in its parent chain (PnpTree.IsVirtual).</summary>
    public bool Virtual { get; init; }
}

internal sealed record CpuInfo(string Name, string Vendor, int Cores, int Threads, int MaxMhz)
{
    public bool IsAmd => Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) || Name.Contains("AMD", StringComparison.OrdinalIgnoreCase);
    public bool IsIntel => Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) || Name.Contains("Intel", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Win32_BaseBoard + Win32_BIOS + Win32_ComputerSystem.</summary>
internal sealed record BoardInfo(
    string Manufacturer,
    string Product,
    string BiosVendor,
    string BiosVersion,
    DateTime? BiosDate,
    string SystemManufacturer,
    string SystemModel);

/// <summary>A physical network adapter (Win32_NetworkAdapter, PhysicalAdapter = TRUE) or a Bluetooth radio.</summary>
internal sealed record NetAdapter(string Name, string Manufacturer, string PnpDeviceId, bool IsBluetooth);

/// <summary>Win32_SoundDevice row.</summary>
internal sealed record SoundDevice(string Name, string Manufacturer, string PnpDeviceId);

/// <summary>MSFT_PhysicalDisk row.</summary>
internal sealed record DiskDetail(string Model, string MediaType, string BusType, ulong SizeBytes, string Firmware)
{
    public bool IsUsb => BusType == "USB";
    public bool IsSsd => MediaType is "SSD" or "SCM" || BusType == "NVMe";
}

/// <summary>Game runtimes: VC++ 2015+ (v14) versions, the legacy DirectX runtime, .NET Desktop runtimes (major → version).</summary>
internal sealed record RuntimeInfo(string? VcX64, string? VcX86, bool DirectX, IReadOnlyDictionary<int, string> DesktopRuntimes);

/// <summary>Everything the hardware scan read, off the UI thread. Pure input for <see cref="HardwareAdvisor"/>.</summary>
internal sealed record HardwareSnapshot
{
    public DateTime When { get; init; } = DateTime.Now;
    public CpuInfo Cpu { get; init; } = new("", "", 0, 0, 0);
    public bool IsLaptop { get; init; }
    public BoardInfo Board { get; init; } = new("", "", "", "", null, "", "");
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public string? NvidiaPackageVersion { get; init; }
    /// <summary>Display class key details by PNPDeviceID (Radeon Software version, INF).</summary>
    public IReadOnlyDictionary<string, GpuRegistryInfo?> GpuRegistry { get; init; } = new Dictionary<string, GpuRegistryInfo?>();
    public IReadOnlyList<RamModule> Ram { get; init; } = [];
    public IReadOnlyList<DiskDetail> Disks { get; init; } = [];
    public IReadOnlyList<NetAdapter> Network { get; init; } = [];
    public IReadOnlyList<SoundDevice> Audio { get; init; } = [];
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = [];
    public IReadOnlyList<PnpDevice> Devices { get; init; } = [];
    public RuntimeInfo Runtimes { get; init; } = new(null, null, false, new Dictionary<int, string>());
    public IReadOnlyList<UninstallEntry> Apps { get; init; } = [];
    public bool WingetAvailable { get; init; } = true;

    /// <summary>Discrete GPU with the most memory, otherwise the first adapter (same rule as SystemProfile.PrimaryGpu).</summary>
    public GpuInfo? PrimaryGpu =>
        Gpus.Where(g => !g.IsIntegrated).OrderByDescending(g => g.DedicatedMemoryBytes).FirstOrDefault() ?? Gpus.FirstOrDefault();
}

// ───────────────────────────── Context (other sources) ─────────────────────────────

internal enum WuViewState { NotSearched, Searching, Ready, Error }

/// <summary>The Windows Update search of the same page: its state and the driver updates it found (not yet installed).</summary>
internal sealed record WuView(WuViewState State, IReadOnlyList<WuDriverUpdate> Updates)
{
    public static WuView NotSearched { get; } = new(WuViewState.NotSearched, []);
}

/// <summary>Result of the online version checks: null value = the vendor did not answer.</summary>
internal sealed record OnlineInfo(
    bool Pending,
    IReadOnlyDictionary<string, NvidiaRelease?> NvidiaByGpu,
    bool AmdChecked,
    DriverPackage? AmdPackage)
{
    public static OnlineInfo NotStarted { get; } = new(true, new Dictionary<string, NvidiaRelease?>(), false, null);
}

internal sealed record HardwareContext(
    IReadOnlyList<SignedDriverInfo> Drivers,
    WuView Wu,
    OnlineInfo Online,
    DateTime Now);

// ───────────────────────────── Report (what the page shows) ─────────────────────────────

public enum HwStatus
{
    /// <summary>Checked against the vendor / Windows Update: nothing newer.</summary>
    Current,
    /// <summary>Installed, but there is no way to check for a newer version.</summary>
    Installed,
    UpdateAvailable,
    NotInstalled,
    /// <summary>Something to do that Lycoris never does itself (BIOS, XMP, display settings, vendor site).</summary>
    ManualOnly,
    /// <summary>Could not be checked (yet): offline, Windows Update still searching…</summary>
    Unchecked,
    /// <summary>Information only.</summary>
    Info,
}

public enum HwSource { Winget, VendorServer, WindowsUpdate, Manual }

public enum HwActionKind
{
    /// <summary>Official GPU driver installer downloaded by DriverDownloads (verified signature) and started interactively.</summary>
    DriverPackage,
    /// <summary>winget package (verified id).</summary>
    Winget,
    /// <summary>Switches the page to another tab (Url = tab name).</summary>
    GoToTab,
    /// <summary>Official web page (https).</summary>
    OpenPage,
    /// <summary>Microsoft Store product page (ms-windows-store:).</summary>
    OpenStore,
    /// <summary>Windows Settings page (ms-settings:).</summary>
    OpenSettings,
    /// <summary>Advice only (BIOS settings, …).</summary>
    Note,
    /// <summary>Another Lycoris page (Url = page key, only "devices"; <see cref="HwAction.FocusId"/> = item there). Never installs.</summary>
    GoToPage,
}

public enum HwGroupKind { Platform, Gpu, Memory, Storage, Network, Audio, Monitors, Peripherals, Runtimes }

/// <summary>One recommended action for a component.</summary>
internal sealed record HwAction(
    string Key,
    HwActionKind Kind,
    HwSource Source,
    string Title,
    string Reason,
    string Publisher)
{
    /// <summary>Not required — vendor suites can be heavy ("ПО ЖЕЛАНИЮ").</summary>
    public bool Optional { get; init; }
    /// <summary>Checked by default: only clearly needed and safe items (missing/outdated GPU driver, missing runtimes).</summary>
    public bool PreSelect { get; init; }
    /// <summary>The program is already on this PC (the action then only opens it / its page).</summary>
    public bool AlreadyInstalled { get; init; }
    public string? Url { get; init; }
    /// <summary><see cref="HwActionKind.GoToPage"/>: the item to bring into view on that page ("kind:monitor").</summary>
    public string? FocusId { get; init; }
    public string? ButtonText { get; init; }
    public WingetPackageRequest? Winget { get; init; }
    public DriverPackage? Package { get; init; }
    /// <summary>GPU this driver package is for (its current driver is backed up first).</summary>
    public string? GpuPnpId { get; init; }
    public int WuCount { get; init; }
    /// <summary>Download size as the vendor reports it ("1.2 GB").</summary>
    public string? SizeText { get; init; }

    public bool IsInstallable => Kind is HwActionKind.DriverPackage or HwActionKind.Winget && !AlreadyInstalled
                                 && (Winget is not null || Package is not null);
}

/// <summary>One detected device / component and what it needs.</summary>
internal sealed record HwComponent(
    string Key,
    string Kind,
    string Name,
    string Details,
    HwStatus Status,
    IReadOnlyList<HwAction> Actions)
{
    public string? Note { get; init; }
    /// <summary>Replaces the default status text (e.g. "ПОИСК В WINDOWS UPDATE").</summary>
    public string? StatusText { get; init; }
}

internal sealed record HwGroup(HwGroupKind Kind, string Title, IReadOnlyList<HwComponent> Components)
{
    /// <summary>Windows Update drivers for this kind of device that did not match a listed component.</summary>
    public int WuExtra { get; init; }
    public string? Footer { get; init; }
}

internal sealed record HwReport(IReadOnlyList<HwGroup> Groups)
{
    public IEnumerable<HwComponent> Components => Groups.SelectMany(g => g.Components);
    public int ComponentCount => Components.Count();
    public int RecommendationCount => Components.Count(c => HardwareRules.NeedsAttention(c.Status));
    public int CurrentCount => Components.Count(c => c.Status == HwStatus.Current);
    public int InstalledCount => Components.Count(c => c.Status == HwStatus.Installed);
    public int UncheckedCount => Components.Count(c => c.Status == HwStatus.Unchecked);
}
