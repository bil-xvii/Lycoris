using System.Management;
using Lycoris.Core;
using Lycoris.Modules.Apps;
using Lycoris.Modules.Dashboard.Checks;

namespace Lycoris.Modules.Drivers.Hardware;

/// <summary>
/// Read-only hardware scan (WMI with timeouts via <see cref="SystemProfile.Searcher"/>, registry, display API).
/// Call from a background thread. Every section is independent: a failing provider only empties its own part.
/// Static facts (CPU, RAM size, VRAM) come from the already loaded <see cref="SystemProfile"/>; everything that an
/// installation can change (GPU driver versions, runtimes, programs, devices) is re-read on every scan.
/// </summary>
internal static class HardwareScanner
{
    private const string Cimv2 = @"\\.\root\cimv2";
    private const string Storage = @"\\.\root\Microsoft\Windows\Storage";

    public static HardwareSnapshot Scan(SystemProfile profile, Action<string>? stage = null)
    {
        stage?.Invoke(Loc.T("Процессор и плата…"));
        var board = Safe(Loc.T("плата и BIOS"), ReadBoard, new BoardInfo("", "", "", "", null, profile.Manufacturer, profile.Model));
        var cpu = new CpuInfo(profile.CpuName, profile.CpuVendor, profile.CpuCores, profile.CpuThreads, profile.CpuMaxClockMhz);

        stage?.Invoke(Loc.T("Устройства PnP…"));
        var devices = Safe(Loc.T("устройства"), ReadDevices, []);

        stage?.Invoke(Loc.T("Видеокарты…"));
        var gpus = Safe(Loc.T("видеокарты"), () => ReadGpus(profile), profile.Gpus.ToList());
        var regs = new Dictionary<string, GpuRegistryInfo?>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in gpus.Where(g => g.PnpDeviceId.Length > 0)) regs[g.PnpDeviceId] = DriverInventory.ReadGpuRegistry(g);

        stage?.Invoke(Loc.T("Память и накопители…"));
        var ram = Safe(Loc.T("память"), ReadRam, []);
        var disks = Safe(Loc.T("накопители"), ReadDisks, []);

        stage?.Invoke(Loc.T("Сеть и звук…"));
        var network = Safe(Loc.T("сетевые адаптеры"), () => ReadNetwork(devices), []);
        var audio = Safe(Loc.T("звуковые устройства"), ReadAudio, []);

        stage?.Invoke(Loc.T("Мониторы…"));
        var displays = Safe(Loc.T("мониторы"), DisplayProbe.Read, []);

        stage?.Invoke(Loc.T("Библиотеки и программы…"));
        var apps = Safe(Loc.T("список программ"), DriverInventory.ReadUninstallEntries, []);
        var runtimes = Safe(Loc.T("библиотеки для игр"), () => ReadRuntimes(apps), new RuntimeInfo(null, null, false, new Dictionary<int, string>()));

        return new HardwareSnapshot
        {
            When = DateTime.Now,
            Cpu = cpu,
            IsLaptop = profile.IsLaptop,
            Board = board,
            Gpus = gpus,
            NvidiaPackageVersion = Safe(Loc.T("версия пакета NVIDIA"), DriverInventory.ReadNvidiaPackageVersion, null),
            GpuRegistry = regs,
            Ram = ram,
            Disks = disks,
            Network = network,
            Audio = audio,
            Displays = displays,
            Devices = devices,
            Runtimes = runtimes,
            Apps = apps,
            WingetAvailable = WingetClient.ExePath is not null,
        };
    }

    private static T Safe<T>(string what, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Log.Warn("Анализ железа: не удалось прочитать {0} ({1})", what, ex.Message);
            return fallback;
        }
    }

    private static IEnumerable<ManagementBaseObject> Query(string wql, string scope = Cimv2)
    {
        using var searcher = SystemProfile.Searcher(scope, wql);
        var list = new List<ManagementBaseObject>();
        foreach (var mo in searcher.Get()) list.Add(mo);
        return list;
    }

    private static string Str(object? o) => o?.ToString()?.Trim() ?? "";

    private static DateTime? CimDate(object? o)
    {
        var s = o?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s).Date; } catch { return null; }
    }

    // ───────────────────────────── Board / BIOS ─────────────────────────────

    private static BoardInfo ReadBoard()
    {
        string boardMaker = "", product = "", biosVendor = "", biosVersion = "", sysMaker = "", sysModel = "";
        DateTime? biosDate = null;
        foreach (var mo in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            using (mo)
            {
                boardMaker = Str(mo["Manufacturer"]);
                product = Str(mo["Product"]);
            }
        foreach (var mo in Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            using (mo)
            {
                biosVendor = Str(mo["Manufacturer"]);
                biosVersion = Str(mo["SMBIOSBIOSVersion"]);
                biosDate = CimDate(mo["ReleaseDate"]);
            }
        foreach (var mo in Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            using (mo)
            {
                sysMaker = Str(mo["Manufacturer"]);
                sysModel = Str(mo["Model"]);
            }
        return new BoardInfo(boardMaker, product, biosVendor, biosVersion, biosDate, sysMaker, sysModel);
    }

    // ───────────────────────────── PnP devices ─────────────────────────────

    private static List<PnpDevice> ReadDevices()
    {
        try
        {
            return ReadDevices("SELECT Name, DeviceID, PNPClass, Manufacturer, Service, ConfigManagerErrorCode, Present, HardwareID FROM Win32_PnPEntity", true);
        }
        catch (ManagementException)
        {
            // Older builds without the Present property.
            return ReadDevices("SELECT Name, DeviceID, PNPClass, Manufacturer, Service, ConfigManagerErrorCode, HardwareID FROM Win32_PnPEntity", false);
        }
    }

    private static List<PnpDevice> ReadDevices(string wql, bool withPresent)
    {
        var list = new List<PnpDevice>();
        foreach (var mo in Query(wql))
            using (mo)
            {
                var id = Str(mo["DeviceID"]);
                if (id.Length == 0) continue;
                bool? present = withPresent && mo["Present"] is bool p ? p : null;
                if (present == false) continue;
                list.Add(new PnpDevice(Str(mo["Name"]), id, Str(mo["PNPClass"]), Str(mo["Manufacturer"]), Str(mo["Service"]),
                    Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0), present, mo["HardwareID"] as string[] ?? [])
                {
                    Virtual = IsVirtual(id),
                });
            }
        return list;
    }

    /// <summary>
    /// G HUB's virtual mouse / keyboard (HID\VID_046D&amp;PID_C231 under LGHUBDevice\…) look like USB peripherals by id. Only ids
    /// with a USB vendor id are checked (CfgMgr32, read-only); any failure counts as "not virtual".
    /// </summary>
    private static bool IsVirtual(string deviceId)
    {
        if (HardwareRules.UsbId(deviceId) is null) return false;
        try
        {
            // A node whose parent cannot be read is not judged (an unreadable chain would look "virtual").
            return Core.Devices.PnpTree.Get(deviceId)?.ParentId is { Length: > 0 } && Core.Devices.PnpTree.IsVirtual(deviceId);
        }
        catch { return false; }
    }

    // ───────────────────────────── GPUs ─────────────────────────────

    /// <summary>Fresh driver version/date per adapter (SystemProfile is collected once per app run); VRAM from the profile.</summary>
    private static List<GpuInfo> ReadGpus(SystemProfile profile)
    {
        var list = new List<GpuInfo>();
        foreach (var mo in Query("SELECT Name, DriverVersion, DriverDate, PNPDeviceID, AdapterRAM FROM Win32_VideoController"))
            using (mo)
            {
                var name = Str(mo["Name"]);
                var pnp = Str(mo["PNPDeviceID"]);
                if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
                var known = profile.Gpus.FirstOrDefault(g => string.Equals(g.PnpDeviceId, pnp, StringComparison.OrdinalIgnoreCase));
                var vram = known?.DedicatedMemoryBytes ?? Convert.ToUInt64(mo["AdapterRAM"] ?? 0UL);
                DateTime? date = null;
                try { date = ManagementDateTimeConverter.ToDateTime(Str(mo["DriverDate"])); } catch { }
                list.Add(new GpuInfo(name.Length > 0 ? name : Loc.T("Видеоадаптер"), SystemProfile.VendorFromPnp(pnp, name),
                    Str(mo["DriverVersion"]), date, vram, pnp));
            }
        return list;
    }

    // ───────────────────────────── Memory / disks ─────────────────────────────

    private static List<RamModule> ReadRam()
    {
        var list = new List<RamModule>();
        foreach (var mo in Query("SELECT PartNumber, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Capacity, Manufacturer FROM Win32_PhysicalMemory"))
            using (mo)
            {
                list.Add(new RamModule(Str(mo["PartNumber"]), Convert.ToInt32(mo["Speed"] ?? 0), Convert.ToInt32(mo["ConfiguredClockSpeed"] ?? 0),
                    Convert.ToInt32(mo["SMBIOSMemoryType"] ?? 0), Convert.ToUInt64(mo["Capacity"] ?? 0UL), Str(mo["Manufacturer"])));
            }
        return list;
    }

    private static List<DiskDetail> ReadDisks()
    {
        var list = new List<DiskDetail>();
        foreach (var mo in Query("SELECT FriendlyName, Model, MediaType, BusType, Size, FirmwareVersion FROM MSFT_PhysicalDisk", Storage))
            using (mo)
            {
                var media = Convert.ToUInt16(mo["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "—" };
                var bus = Convert.ToUInt16(mo["BusType"] ?? 0) switch
                {
                    17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 3 => "ATA", 12 => "SD", 13 => "MMC", 9 => "iSCSI", 15 => "Virtual",
                    _ => "—",
                };
                if (bus == "NVMe" && media == "—") media = "SSD";
                if (bus == "Virtual") continue; // VHDs, Storage Spaces virtual disks
                var model = Str(mo["FriendlyName"]);
                if (model.Length == 0) model = Str(mo["Model"]);
                list.Add(new DiskDetail(model.Length > 0 ? model : Loc.T("Накопитель"), media, bus, Convert.ToUInt64(mo["Size"] ?? 0UL), Str(mo["FirmwareVersion"])));
            }
        return list;
    }

    // ───────────────────────────── Network / audio ─────────────────────────────

    private static List<NetAdapter> ReadNetwork(IReadOnlyList<PnpDevice> devices)
    {
        var list = new List<NetAdapter>();
        foreach (var mo in Query("SELECT Name, Manufacturer, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
            using (mo)
            {
                var pnp = Str(mo["PNPDeviceID"]);
                // Real hardware only: PCI / USB / SDIO. Bluetooth PAN, VPN and virtual adapters are skipped.
                if (!IsHardwareBus(pnp)) continue;
                list.Add(new NetAdapter(Str(mo["Name"]), Str(mo["Manufacturer"]), pnp, false));
            }
        // Bluetooth radios (the USB / PCI device, not the BTHENUM profile children).
        foreach (var d in devices.Where(d => string.Equals(d.PnpClass, "Bluetooth", StringComparison.OrdinalIgnoreCase) && IsHardwareBus(d.DeviceId)))
            list.Add(new NetAdapter(d.Name, d.Manufacturer, d.DeviceId, true));
        return list;
    }

    private static bool IsHardwareBus(string pnp) =>
        pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
        pnp.StartsWith(@"SD\", StringComparison.OrdinalIgnoreCase);

    private static List<SoundDevice> ReadAudio()
    {
        var list = new List<SoundDevice>();
        foreach (var mo in Query("SELECT Name, Manufacturer, PNPDeviceID FROM Win32_SoundDevice"))
            using (mo)
            {
                var pnp = Str(mo["PNPDeviceID"]);
                // Virtual / software audio devices (ROOT\…, SWD\…) are not hardware.
                if (!(pnp.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
                      pnp.StartsWith(@"INTELAUDIO\", StringComparison.OrdinalIgnoreCase) || pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add(new SoundDevice(Str(mo["Name"]), Str(mo["Manufacturer"]), pnp));
            }
        return list;
    }

    // ───────────────────────────── Runtimes ─────────────────────────────

    /// <summary>
    /// Same documented read-only checks as the Apps page (VC++ v14 runtime keys, .NET shared-framework folders,
    /// D3DX9 / XAudio 2.7 files), plus the "Apps &amp; features" entries for VC++ as a second source.
    /// </summary>
    private static RuntimeInfo ReadRuntimes(IReadOnlyList<UninstallEntry> apps)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        InstalledDetector.ApplyCrossChecks(map);
        map.TryGetValue(HardwareCatalog.VcX64, out var vc64);
        map.TryGetValue(HardwareCatalog.VcX86, out var vc86);
        vc64 = Combine(vc64, HardwareRules.ParseVcRedist(apps, "x64"));
        vc86 = Combine(vc86, HardwareRules.ParseVcRedist(apps, "x86"));

        var desktop = new Dictionary<int, string>();
        foreach (var (id, version) in map)
        {
            if (!id.StartsWith(HardwareCatalog.DesktopRuntimePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(id[HardwareCatalog.DesktopRuntimePrefix.Length..], out var major)) desktop[major] = version;
        }
        return new RuntimeInfo(vc64, vc86, map.ContainsKey(HardwareCatalog.DirectX), desktop);

        // The registry key may be present with an empty version ("" = installed, version unknown).
        static string? Combine(string? key, string? listed) =>
            key is null ? listed : key.Length == 0 ? listed ?? "" : HardwareRules.MaxVersion(key, listed);
    }
}
