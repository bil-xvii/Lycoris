using System.Collections.Concurrent;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace Lycoris.Core;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Microsoft, Other }

public sealed record GpuInfo(string Name, GpuVendor Vendor, string DriverVersion, DateTime? DriverDate, ulong DedicatedMemoryBytes, string PnpDeviceId)
{
    public bool IsIntegrated => Vendor == GpuVendor.Intel && DedicatedMemoryBytes < 2UL * 1024 * 1024 * 1024
                                || Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
                                || Name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase)
                                || Vendor == GpuVendor.Microsoft;
}

public sealed record DiskInfo(string Model, string MediaType, ulong SizeBytes, string BusType)
{
    public bool IsSsd => MediaType.Equals("SSD", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Static facts about this PC (collected once, in the background): OS, CPU, GPUs, RAM, disks, form factor.
/// Tweaks and recommendations use it to decide what applies.
/// </summary>
public sealed class SystemProfile
{
    public static SystemProfile Current { get; private set; } = new();
    private static readonly object LoadLock = new();
    private static Task<SystemProfile>? _loading;
    /// <summary>The Collect worker of the last load; it may outlive <see cref="CollectLimit"/> when WMI hangs.</summary>
    private static Task? _collecting;
    private static DateTime _loadedAtUtc;
    /// <summary>Overall bound for one collection (each WMI query also has its own timeout).</summary>
    private static readonly TimeSpan CollectLimit = TimeSpan.FromSeconds(45);
    /// <summary>An incomplete profile is collected again on a later request, at most this often.</summary>
    private static readonly TimeSpan RetryIncompleteAfter = TimeSpan.FromSeconds(60);

    /// <summary>Sections of <see cref="Collect"/>: os, cpu, memory, gpu, disks, chassis, board, battery → true = read without errors.</summary>
    private readonly ConcurrentDictionary<string, bool> _sections = new();
    private static readonly string[] AllSections = ["os", "cpu", "memory", "gpu", "disks", "chassis", "board", "battery"];
    /// <summary>Sections that tweak applicability rules depend on (laptop/battery/chassis, CPU model, GPUs).</summary>
    private static readonly string[] HardwareSections = ["cpu", "gpu", "chassis", "battery"];

    public string OsName { get; private set; } = "Windows";
    public string OsDisplayVersion { get; private set; } = "";
    public string OsEdition { get; private set; } = "";
    public int OsBuild { get; private set; } = Environment.OSVersion.Version.Build;
    public int OsUbr { get; private set; }
    public bool IsWindows11 => OsBuild >= 22000;
    public bool IsServer { get; private set; }

    public string CpuName { get; private set; } = "";
    public string CpuVendor { get; private set; } = "";
    public bool CpuIsAmd => CpuVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
    public bool CpuIsIntel => CpuVendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);
    public bool CpuIsX3D => CpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);
    public int CpuCores { get; private set; } = Environment.ProcessorCount;
    public int CpuThreads { get; private set; } = Environment.ProcessorCount;
    public int CpuMaxClockMhz { get; private set; }

    public ulong RamTotalBytes { get; private set; }
    /// <summary>Rated module speed (MT/s) vs. the speed it actually runs at. Lower configured speed = XMP/EXPO off.</summary>
    public int RamRatedSpeed { get; private set; }
    public int RamConfiguredSpeed { get; private set; }
    public int RamModules { get; private set; }

    public IReadOnlyList<GpuInfo> Gpus { get; private set; } = [];
    public GpuInfo? PrimaryGpu => Gpus.Where(g => !g.IsIntegrated).OrderByDescending(g => g.DedicatedMemoryBytes).FirstOrDefault()
                                  ?? Gpus.FirstOrDefault();
    public bool HasNvidia => Gpus.Any(g => g.Vendor == GpuVendor.Nvidia);
    public bool HasAmdGpu => Gpus.Any(g => g.Vendor == GpuVendor.Amd);
    public bool HasIntelGpu => Gpus.Any(g => g.Vendor == GpuVendor.Intel);

    public IReadOnlyList<DiskInfo> Disks { get; private set; } = [];
    public bool SystemDriveIsSsd { get; private set; } = true;

    public bool HasBattery { get; private set; }
    public bool IsLaptop => HasBattery || ChassisIsPortable;
    private bool ChassisIsPortable { get; set; }
    /// <summary>Modern Standby (S0 low-power idle, SYSTEM_POWER_CAPABILITIES.AoAc): only the Balanced plan exists there. Read-only fact.</summary>
    public bool IsModernStandby { get; private set; }

    public string Manufacturer { get; private set; } = "";
    public string Model { get; private set; } = "";
    public string Motherboard { get; private set; } = "";
    public string BiosVersion { get; private set; } = "";
    public string ComputerName { get; } = Environment.MachineName;
    public string UserName { get; } = Environment.UserName;
    public DateTime BootTime { get; private set; } = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
    public bool IsLoaded { get; private set; }

    /// <summary>Sections that failed, timed out or were not read yet (empty when everything was collected).</summary>
    public IReadOnlyList<string> MissingFacts => AllSections.Where(x => !_sections.TryGetValue(x, out var ok) || !ok).ToList();
    /// <summary>True when every section was read without errors.</summary>
    public bool IsComplete => MissingFacts.Count == 0;
    /// <summary>
    /// True when the facts that tweak applicability depends on (laptop/battery/chassis, CPU, GPUs) are known.
    /// When false, the defaults (e.g. IsLaptop = false) must not be used to decide whether a tweak applies.
    /// </summary>
    public bool HardwareFactsKnown => HardwareSections.All(x => _sections.TryGetValue(x, out var ok) && ok);

    /// <summary>Sum of installed modules (what the user bought); RamTotalBytes is what Windows can use.</summary>
    public ulong RamInstalledBytes { get; private set; }

    public string RamText
    {
        get
        {
            var bytes = RamInstalledBytes > 0 ? RamInstalledBytes : RamTotalBytes;
            return bytes == 0 ? "—" : Loc.Tf("{0} ГБ", Math.Round(bytes / 1024d / 1024 / 1024));
        }
    }
    public string OsText => Loc.Tf("{0} {1} (сборка {2}{3})", OsName, OsDisplayVersion, OsBuild, OsUbr > 0 ? "." + OsUbr : "").Trim();

    /// <summary>
    /// Collects the profile once and caches it. A failed, timed-out or incomplete collection is retried on a
    /// later call (incomplete ones at most once a minute), but never while the previous worker is still stuck.
    /// </summary>
    public static Task<SystemProfile> LoadAsync()
    {
        lock (LoadLock)
        {
            if (_loading is { } t && !NeedsReload(t)) return t;
            return _loading = LoadCoreAsync();
        }
    }

    private static bool NeedsReload(Task<SystemProfile> t)
    {
        if (!t.IsCompleted) return false;
        if (_collecting is { IsCompleted: false }) return false; // a stuck WMI call: don't pile up workers
        if (!t.IsCompletedSuccessfully) return true;
        return !t.Result.IsComplete && DateTime.UtcNow - _loadedAtUtc >= RetryIncompleteAfter;
    }

    /// <summary>Runs synchronously up to the first await, i.e. inside <see cref="LoadLock"/>.</summary>
    private static async Task<SystemProfile> LoadCoreAsync()
    {
        var p = new SystemProfile();
        var work = Task.Run(p.Collect);
        _collecting = work;
        var finished = await Task.WhenAny(work, Task.Delay(CollectLimit)).ConfigureAwait(false) == work;
        if (!finished)
            Log.Warn("Сбор сведений о системе не завершился за {0:0} с — часть данных неизвестна: {1}.", CollectLimit.TotalSeconds, string.Join(", ", p.MissingFacts));
        else if (!p.IsComplete)
            Log.Warn("Сбор сведений о системе: не удалось прочитать {0}.", string.Join(", ", p.MissingFacts));
        _loadedAtUtc = DateTime.UtcNow;
        // Keep the richer profile: a retry that got less than the previous one does not replace it.
        if (!Current.IsLoaded || p.MissingFacts.Count <= Current.MissingFacts.Count)
            Current = p;
        return p;
    }

    private void Collect()
    {
        Safe("os", ReadOs);
        Safe("cpu", ReadCpu);
        Safe("memory", ReadMemory);
        Safe("gpu", ReadGpus);
        Safe("disks", ReadDisks);
        Safe("board", ReadBoard);
        Safe("battery", ReadBattery);
        IsLoaded = true;
    }

    private void ReadOs()
    {
        const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        OsDisplayVersion = RegistryHelper.ReadString(RegHive.HKLM, cv, "DisplayVersion") ?? RegistryHelper.ReadString(RegHive.HKLM, cv, "ReleaseId") ?? "";
        OsEdition = RegistryHelper.ReadString(RegHive.HKLM, cv, "EditionID") ?? "";
        if (int.TryParse(RegistryHelper.ReadString(RegHive.HKLM, cv, "CurrentBuildNumber"), out var b)) OsBuild = b;
        OsUbr = RegistryHelper.ReadInt(RegHive.HKLM, cv, "UBR") ?? 0;
        var installation = RegistryHelper.ReadString(RegHive.HKLM, cv, "InstallationType") ?? "";
        IsServer = installation.Contains("Server", StringComparison.OrdinalIgnoreCase);

        foreach (var mo in Query("os", "SELECT Caption, LastBootUpTime FROM Win32_OperatingSystem"))
        {
            var caption = mo["Caption"]?.ToString() ?? "Windows";
            caption = caption.Replace("Майкрософт ", "").Replace("Microsoft ", ""); // loc:ignore (Windows caption on a Russian OS)
            // Windows 11 still reports itself as "Windows 10" in some legacy fields.
            if (IsWindows11) caption = caption.Replace("Windows 10", "Windows 11");
            OsName = caption.Trim();
            try { BootTime = ManagementDateTimeConverter.ToDateTime(mo["LastBootUpTime"]?.ToString() ?? ""); } catch { }
        }
    }

    private void ReadCpu()
    {
        foreach (var mo in Query("cpu", "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
        {
            CpuName = (mo["Name"]?.ToString() ?? "").Trim();
            while (CpuName.Contains("  ")) CpuName = CpuName.Replace("  ", " ");
            CpuVendor = mo["Manufacturer"]?.ToString() ?? "";
            CpuCores = Convert.ToInt32(mo["NumberOfCores"] ?? CpuCores);
            CpuThreads = Convert.ToInt32(mo["NumberOfLogicalProcessors"] ?? CpuThreads);
            CpuMaxClockMhz = Convert.ToInt32(mo["MaxClockSpeed"] ?? 0);
            break;
        }
    }

    private void ReadMemory()
    {
        foreach (var mo in Query("memory", "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
            RamTotalBytes = Convert.ToUInt64(mo["TotalPhysicalMemory"] ?? 0UL);

        int rated = 0, configured = 0, count = 0;
        ulong installed = 0;
        foreach (var mo in Query("memory", "SELECT Speed, ConfiguredClockSpeed, Capacity FROM Win32_PhysicalMemory"))
        {
            count++;
            installed += Convert.ToUInt64(mo["Capacity"] ?? 0UL);
            rated = Math.Max(rated, Convert.ToInt32(mo["Speed"] ?? 0));
            var c = Convert.ToInt32(mo["ConfiguredClockSpeed"] ?? 0);
            configured = configured == 0 ? c : Math.Min(configured, c);
        }
        RamRatedSpeed = rated;
        RamConfiguredSpeed = configured;
        RamModules = count;
        RamInstalledBytes = installed;
    }

    private void ReadGpus()
    {
        var list = new List<GpuInfo>();
        foreach (var mo in Query("gpu", "SELECT Name, DriverVersion, DriverDate, PNPDeviceID, AdapterRAM FROM Win32_VideoController"))
        {
            var name = mo["Name"]?.ToString() ?? "GPU";
            var pnp = mo["PNPDeviceID"]?.ToString() ?? "";
            DateTime? date = null;
            try { date = ManagementDateTimeConverter.ToDateTime(mo["DriverDate"]?.ToString() ?? ""); } catch { }
            var vram = ReadDedicatedVram(name, pnp);
            if (vram == 0) vram = Convert.ToUInt64(mo["AdapterRAM"] ?? 0UL); // capped at 4 GB, fallback only
            list.Add(new GpuInfo(name, VendorFromPnp(pnp, name), mo["DriverVersion"]?.ToString() ?? "", date, vram, pnp));
        }
        Gpus = list.Where(g => !g.Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Win32_VideoController.AdapterRAM is a 32-bit value; the real size lives in the display class key.</summary>
    private static ulong ReadDedicatedVram(string name, string pnp)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return 0;
            foreach (var sub in cls.GetSubKeyNames().Where(s => s.Length == 4 && s.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k is null) continue;
                var desc = k.GetValue("DriverDesc")?.ToString();
                if (!string.Equals(desc, name, StringComparison.OrdinalIgnoreCase)) continue;
                var v = k.GetValue("HardwareInformation.qwMemorySize");
                if (v is long l) return (ulong)l;
                if (v is byte[] b && b.Length >= 8) return BitConverter.ToUInt64(b, 0);
                if (v is int i) return (uint)i;
                if (k.GetValue("HardwareInformation.MemorySize") is byte[] b4 && b4.Length >= 4) return BitConverter.ToUInt32(b4, 0);
            }
        }
        catch { }
        return 0;
    }

    public static GpuVendor VendorFromPnp(string pnp, string name)
    {
        if (pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Nvidia;
        if (pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) || pnp.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Amd;
        if (pnp.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Nvidia;
        if (name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || name.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Amd;
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;
        if (name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Microsoft;
        return GpuVendor.Other;
    }

    private void ReadDisks()
    {
        var list = new List<DiskInfo>();
        try
        {
            using var searcher = Searcher(@"\\.\root\Microsoft\Windows\Storage", "SELECT FriendlyName, MediaType, Size, BusType FROM MSFT_PhysicalDisk");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var media = Convert.ToUInt16(mo["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "—" };
                    var bus = Convert.ToUInt16(mo["BusType"] ?? 0) switch
                    {
                        17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 3 => "ATA", _ => "—",
                    };
                    if (bus == "NVMe" && media == "—") media = "SSD";
                    list.Add(new DiskInfo(mo["FriendlyName"]?.ToString() ?? Loc.T("Диск"), media, Convert.ToUInt64(mo["Size"] ?? 0UL), bus));
                }
            }
        }
        catch { _sections["disks"] = false; }
        Disks = list;

        // System drive media type
        try
        {
            var sysLetter = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd(':', '\\');
            using var searcher = Searcher(@"\\.\root\Microsoft\Windows\Storage", $"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{sysLetter}'");
            foreach (ManagementObject part in searcher.Get())
            {
                using (part)
                {
                    var num = Convert.ToUInt32(part["DiskNumber"] ?? 0u);
                    using var ds = Searcher(@"\\.\root\Microsoft\Windows\Storage", $"SELECT MediaType, BusType FROM MSFT_PhysicalDisk WHERE DeviceId = '{num}'");
                    foreach (ManagementObject d in ds.Get())
                        using (d)
                            SystemDriveIsSsd = Convert.ToUInt16(d["MediaType"] ?? 0) != 3;
                }
            }
        }
        catch { _sections["disks"] = false; }
    }

    private void ReadBoard()
    {
        foreach (var mo in Query("chassis", "SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem"))
        {
            Manufacturer = mo["Manufacturer"]?.ToString() ?? "";
            Model = mo["Model"]?.ToString() ?? "";
            ChassisIsPortable = Convert.ToUInt16(mo["PCSystemType"] ?? 0) == 2; // Mobile
        }
        _sections.TryAdd("chassis", true); // stays false when the query above failed
        foreach (var mo in Query("board", "SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            Motherboard = $"{mo["Manufacturer"]} {mo["Product"]}".Trim();
        foreach (var mo in Query("board", "SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
            BiosVersion = mo["SMBIOSBIOSVersion"]?.ToString() ?? "";
    }

    /// <summary>
    /// general_tweaks.md §11: a laptop battery is SystemBatteriesPresent &amp;&amp; !BatteriesAreShortTerm (short-term batteries
    /// are UPS batteries). Win32_Battery alone is unreliable — a USB UPS shows up there — so it is only the fallback when
    /// GetPwrCapabilities fails.
    /// </summary>
    private void ReadBattery()
    {
        var caps = PowerSchemes.ReadCapabilities();
        IsModernStandby = caps?.ModernStandby == true;
        if (caps is { } c)
        {
            HasBattery = BatteryFromCaps(c.SystemBatteriesPresent, c.BatteriesAreShortTerm);
            return;
        }
        HasBattery = Query("battery", "SELECT Name FROM Win32_Battery").Any();
    }

    /// <summary>Pure: a system battery that is not a short-term (UPS) battery.</summary>
    public static bool BatteryFromCaps(bool present, bool shortTerm) => present && !shortTerm;

    /// <summary>WMI query; a failure (incl. timeout) marks <paramref name="section"/> as unknown and returns what was read.</summary>
    private List<ManagementBaseObject> Query(string section, string wql, string scope = @"\\.\root\cimv2")
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var searcher = Searcher(scope, wql);
            foreach (var mo in searcher.Get()) list.Add(mo);
        }
        catch
        {
            _sections[section] = false;
        }
        return list;
    }

    /// <summary>WMI query with timeouts: a hung provider must not stall the app.</summary>
    internal static ManagementObjectSearcher Searcher(string scope, string wql)
    {
        var s = new ManagementScope(scope, new ConnectionOptions { Timeout = TimeSpan.FromSeconds(10) });
        return new ManagementObjectSearcher(s, new ObjectQuery(wql),
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = true, Rewindable = false });
    }

    private void Safe(string section, Action a)
    {
        try
        {
            a();
            _sections.TryAdd(section, true); // stays false if a query of this section failed
        }
        catch (Exception ex)
        {
            _sections[section] = false;
            Log.Warn("Сбор сведений о системе: {0}", ex.Message);
        }
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = [Loc.T("Б"), Loc.T("КБ"), Loc.T("МБ"), Loc.T("ГБ"), Loc.T("ТБ")];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.#} {units[i]}";
    }

    public static string SystemDrive => (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\";

    public static (ulong Free, ulong Total) SystemDriveSpace()
    {
        try
        {
            var d = new DriveInfo(SystemDrive);
            return ((ulong)d.AvailableFreeSpace, (ulong)d.TotalSize);
        }
        catch { return (0, 0); }
    }
}
