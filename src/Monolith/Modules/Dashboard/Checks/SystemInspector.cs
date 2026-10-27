using System.IO;
using System.Management;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Modules.Dashboard.Monitoring;
using Monolith.Modules.Games;

namespace Monolith.Modules.Dashboard.Checks;

/// <summary>
/// Collects a <see cref="SystemSnapshot"/> with read-only calls only (WMI, registry reads, user32/gdi32 queries,
/// "fsutil behavior query", event log queries, tbs/kernel32/NVML queries, "CiTool -lp -json" with admin rights).
/// Runs entirely on background threads; every part has a time limit and degrades to "unknown" on failure.
/// </summary>
public static class SystemInspector
{
    private const string StartupApproved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    /// <summary>Power saver GUID (tech.md §4).</summary>
    private static readonly Guid PowerSaverScheme = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    /// <summary>Drive SMART data is re-read at most this often: reading it can spin up sleeping HDDs.</summary>
    private static readonly TimeSpan DiskHealthCacheFor = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan EventsCacheFor = TimeSpan.FromMinutes(5);

    public static async Task<SystemSnapshot> CollectAsync()
    {
        var profile = await SystemProfile.LoadAsync().ConfigureAwait(false);
        var elevated = Elevation.IsElevated;
        var trimTask = ReadTrimDisabledAsync();
        var bootTask = Probe.RunAsync("загрузка и TPM", () => FirmwareProbe.ReadAsync(elevated), TimeSpan.FromSeconds(25),
            (FirmwareKind.Unknown, (bool?)null, TpmState.Unknown));
        var kernelTask = Probe.RunAsync("защита ядра", () => KernelProtectionProbe.ReadAsync(elevated), TimeSpan.FromSeconds(20),
            ((bool?)null, SmartAppControlState.Unknown));
        var disksTask = Probe.RunAsync<IReadOnlyList<DiskHealth>?>(elevated ? "накопители (админ)" : "накопители",
            () => DiskHealthProbe.Read(elevated), TimeSpan.FromSeconds(30), null, DiskHealthCacheFor);
        var eventsTask = Probe.RunAsync("журнал событий", () => StabilityProbe.Read(DateTime.Now), TimeSpan.FromSeconds(20), null, EventsCacheFor);
        var linkTask = profile.PrimaryGpu is { Vendor: GpuVendor.Nvidia } nv
            ? Probe.RunAsync("NVML", () => ReadGpuLink(nv.Name), TimeSpan.FromSeconds(10), null)
            : Task.FromResult<GpuLinkFacts?>(null);

        var snapshot = await Task.Run(() => Collect(profile)).ConfigureAwait(false);
        var trim = await trimTask.ConfigureAwait(false);
        var (firmware, secureBoot, tpm) = await bootTask.ConfigureAwait(false);
        var (blocklist, sac) = await kernelTask.ConfigureAwait(false);
        return snapshot with
        {
            TrimDisabled = trim,
            Elevated = elevated,
            Firmware = firmware,
            SecureBootOn = secureBoot,
            Tpm = tpm,
            DriverBlocklistEnforced = blocklist,
            SmartAppControl = sac,
            Disks = await disksTask.ConfigureAwait(false),
            Stability = await eventsTask.ConfigureAwait(false),
            GpuLink = await linkTask.ConfigureAwait(false),
        };
    }

    private static SystemSnapshot Collect(SystemProfile p)
    {
        var (free, total) = SystemProfile.SystemDriveSpace();
        return new SystemSnapshot
        {
            Now = DateTime.Now,
            OsBuild = p.OsBuild,
            Gpus = p.Gpus,
            Adapters = Safe(ReadAdapters, []),
            InstalledGames = Safe(ReadGameNames, []),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            IsLaptop = p.IsLaptop,
            CpuName = p.CpuName,
            CpuCores = p.CpuCores,
            RamModules = Safe(ReadRamModules, []),
            Displays = Safe(DisplayProbe.Read, []),
            RestoreEnabled = Safe<bool?>(() => RestorePointService.IsEnabled(), null),
            RestoreBlockedByPolicy = Safe(() => RestorePointService.IsBlockedByPolicy, false),
            SystemDisk = Safe(() => ReadSystemDisk(free, total), null),
            SystemDriveFree = free,
            SystemDriveTotal = total,
            ProblemDevices = Safe<IReadOnlyList<ProblemDevice>?>(ReadProblemDevices, null),
            GameModeOn = Safe(ReadGameMode, null),
            BackgroundRecordingOn = Safe(ReadBackgroundRecording, null),
            Hags = Safe(ReadHags, HagsState.Unknown),
            PowerPlan = Safe(ReadPowerPlanKind, PowerPlanKind.Unknown),
            PowerPlanName = Safe(() => PowerSchemes.GetName(PowerSchemes.GetActive()), ""),
            PendingReboot = Safe(ReadPendingReboot, []),
            PrimaryGpu = p.PrimaryGpu,
            StartupCount = Safe<int?>(() => CountStartupEntries(), null),
            Vbs = Safe(ReadVbs, VbsState.Unknown),
        };
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex)
        {
            Log.Warn($"Проверка системы: {ex.Message}");
            return fallback;
        }
    }

    // ───────────────────────────── WMI ─────────────────────────────

    private static List<ManagementBaseObject> Query(string wql, string scope = @"\\.\root\cimv2")
    {
        var list = new List<ManagementBaseObject>();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));
        searcher.Options.Timeout = TimeSpan.FromSeconds(20);
        foreach (var mo in searcher.Get()) list.Add(mo);
        return list;
    }

    public static IReadOnlyList<RamModule> ReadRamModules() =>
        Query("SELECT PartNumber, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Capacity, Manufacturer FROM Win32_PhysicalMemory")
            .Select(mo => new RamModule(
                (mo["PartNumber"]?.ToString() ?? "").Trim(),
                ToInt(mo["Speed"]),
                ToInt(mo["ConfiguredClockSpeed"]),
                ToInt(mo["SMBIOSMemoryType"]),
                mo["Capacity"] is { } c ? Convert.ToUInt64(c) : 0,
                (mo["Manufacturer"]?.ToString() ?? "").Trim()))
            .ToList();

    private static IReadOnlyList<ProblemDevice> ReadProblemDevices() =>
        Query("SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0")
            .Select(mo => new ProblemDevice((mo["Name"]?.ToString() ?? "Неизвестное устройство").Trim(), ToInt(mo["ConfigManagerErrorCode"])))
            .ToList();

    private static VbsState ReadVbs()
    {
        foreach (var mo in Query("SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard",
                     @"\\.\root\Microsoft\Windows\DeviceGuard"))
        {
            var status = ToInt(mo["VirtualizationBasedSecurityStatus"]);
            var running = mo["SecurityServicesRunning"] is uint[] arr ? arr : [];
            if (running.Contains(2u)) return VbsState.MemoryIntegrity;
            return status == 2 ? VbsState.VbsOnly : VbsState.Off;
        }
        return VbsState.Unknown;
    }

    private static SystemDisk? ReadSystemDisk(ulong free, ulong total)
    {
        const string storage = @"\\.\root\Microsoft\Windows\Storage";
        var letter = SystemProfile.SystemDrive.TrimEnd('\\', ':');
        if (letter.Length != 1 || !char.IsLetter(letter[0])) return null;
        foreach (var part in Query($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'", storage))
        {
            var number = Convert.ToUInt32(part["DiskNumber"] ?? 0u);
            foreach (var d in Query($"SELECT FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk WHERE DeviceId = '{number}'", storage))
            {
                var media = Convert.ToUInt16(d["MediaType"] ?? (ushort)0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
                var bus = Convert.ToUInt16(d["BusType"] ?? (ushort)0) switch
                {
                    17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 3 => "ATA", 18 => "SCM", _ => "",
                };
                if (bus == "NVMe" && media.Length == 0) media = "SSD";
                return new SystemDisk((d["FriendlyName"]?.ToString() ?? "").Trim(), media, bus, free, total);
            }
        }
        return null;
    }

    private static int ToInt(object? o)
    {
        try { return o is null ? 0 : Convert.ToInt32(o); }
        catch { return 0; }
    }

    // ───────────────────────────── Registry ─────────────────────────────

    /// <summary>Game Mode: absent or 1 = on (gaming_tweaks.md, `game-mode`).</summary>
    private static bool? ReadGameMode()
    {
        const string key = @"Software\Microsoft\GameBar";
        var v = RegistryHelper.ReadInt(RegHive.HKCU, key, "AutoGameModeEnabled") ?? RegistryHelper.ReadInt(RegHive.HKCU, key, "AllowAutoGameMode");
        return v is null || v != 0;
    }

    /// <summary>"Record what happened": HistoricalCaptureEnabled = 1 means on; absent means off.</summary>
    private static bool? ReadBackgroundRecording() =>
        RegistryHelper.ReadInt(RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled") == 1;

    private static IReadOnlyList<string> ReadPendingReboot()
    {
        var list = new List<string>();
        if (RegistryHelper.KeyExists(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            list.Add("обслуживание компонентов Windows");
        if (RegistryHelper.KeyExists(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            list.Add("Центр обновления Windows");
        return list;
    }

    /// <summary>Enabled entries in the Run keys and Startup folders (StartupApproved = Task Manager's switch).</summary>
    public static int CountStartupEntries()
    {
        var n = 0;
        n += CountRunKey(RegHive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Run", StartupApproved + @"\Run");
        n += CountRunKey(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", StartupApproved + @"\Run");
        n += CountRunKey(RegHive.HKLM, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", StartupApproved + @"\Run32");
        n += CountFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegHive.HKCU);
        n += CountFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegHive.HKLM);
        return n;
    }

    private static int CountRunKey(RegHive hive, string runPath, string approvedPath)
    {
        using var root = RegistryHelper.OpenBase(hive);
        using var run = root.OpenSubKey(runPath, false);
        if (run is null) return 0;
        using var approved = root.OpenSubKey(approvedPath, false);
        var n = 0;
        foreach (var name in run.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (string.IsNullOrWhiteSpace(run.GetValue(name)?.ToString())) continue;
            if (HealthRules.IsStartupEntryEnabled(approved?.GetValue(name) as byte[])) n++;
        }
        return n;
    }

    private static int CountFolder(string folder, RegHive hive)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return 0;
        using var root = RegistryHelper.OpenBase(hive);
        using var approved = root.OpenSubKey(StartupApproved + @"\StartupFolder", false);
        var n = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            if (file.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (HealthRules.IsStartupEntryEnabled(approved?.GetValue(file.Name) as byte[])) n++;
        }
        return n;
    }

    // ───────────────────────────── GPU / power / TRIM ─────────────────────────────

    /// <summary>Hardware adapters by LUID (names as the display driver reports them) with their number of display outputs.</summary>
    private static IReadOnlyList<GpuAdapter> ReadAdapters()
    {
        var outputs = DisplayProbe.TargetsPerAdapter();
        var adapters = D3dkmt.OpenAdapters();
        try
        {
            return adapters.Where(a => !a.IsSoftware && a.Name.Length > 0)
                .Select(a => new GpuAdapter(a.LuidKey, a.Name, outputs.GetValueOrDefault(a.LuidKey)))
                .ToList();
        }
        finally
        {
            D3dkmt.Close(adapters);
        }
    }

    /// <summary>Game names from the library cache only: the dashboard never starts a library scan.</summary>
    private static IReadOnlyList<string> ReadGameNames() =>
        GameLibrary.LoadCache()?.Games.Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [];

    /// <summary>NVML (System32 only, query functions only): BAR1 vs VRAM and the PCIe link of the primary NVIDIA card.</summary>
    private static GpuLinkFacts? ReadGpuLink(string gpuName)
    {
        using var nvml = NvmlSensor.TryCreate(gpuName);
        if (nvml is null) return null;
        var l = nvml.ReadLink();
        return new GpuLinkFacts(gpuName, l.Bar1Total, l.VramTotal, l.CurrentGen, l.CurrentWidth, l.MaxGen, l.MaxWidth, l.GpuMaxGen, l.UnderLoad);
    }

    /// <summary>HAGS from the WDDM 2.7 caps of every hardware adapter; registry HwSchMode only as a fallback.</summary>
    private static HagsState ReadHags()
    {
        var adapters = D3dkmt.OpenAdapters();
        try
        {
            bool anySupported = false, anyEnabled = false, anyAnswer = false;
            foreach (var a in adapters.Where(a => !a.IsSoftware))
            {
                if (D3dkmt.QueryWddm27Caps(a.Handle) is not { } caps) continue;
                anyAnswer = true;
                if ((caps & 0x1) != 0) anySupported = true;
                if ((caps & 0x2) != 0) anyEnabled = true;
            }
            if (anyAnswer)
                return anyEnabled ? HagsState.On : anySupported ? HagsState.Off : HagsState.Unsupported;
        }
        finally
        {
            D3dkmt.Close(adapters);
        }

        if (Environment.OSVersion.Version.Build < 19041) return HagsState.Unsupported;
        return RegistryHelper.ReadInt(RegHive.HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") switch
        {
            2 => HagsState.On,
            1 => HagsState.Off,
            _ => HagsState.Unknown,
        };
    }

    private static PowerPlanKind ReadPowerPlanKind()
    {
        var active = PowerSchemes.GetActive();
        if (active == Guid.Empty) return PowerPlanKind.Unknown;
        if (active == PowerSchemes.Balanced) return PowerPlanKind.Balanced;
        if (active == PowerSchemes.HighPerformance) return PowerPlanKind.HighPerformance;
        if (active == PowerSaverScheme) return PowerPlanKind.PowerSaver;
        if (active == PowerSchemes.UltimateTemplate || PowerSchemes.FindUltimateCopy() == active) return PowerPlanKind.Ultimate;
        return PowerPlanKind.Other;
    }

    /// <summary>"fsutil behavior query DisableDeleteNotify" (read-only, works without admin); registry as a fallback.</summary>
    private static async Task<bool?> ReadTrimDisabledAsync()
    {
        try
        {
            var r = await ProcessRunner.RunAsync(ProcessRunner.Sys("fsutil.exe"), ["behavior", "query", "DisableDeleteNotify"],
                new ProcessRunOptions { Encoding = OutputEncoding.Oem, Timeout = TimeSpan.FromSeconds(15), LogCommand = false }).ConfigureAwait(false);
            if (HealthRules.ParseTrimDisabled(r.StdOut) is { } parsed) return parsed;
        }
        catch (Exception ex)
        {
            Log.Warn($"fsutil: {ex.Message}");
        }
        return RegistryHelper.ReadInt(RegHive.HKLM, @"SYSTEM\CurrentControlSet\Control\FileSystem", "DisableDeleteNotification") switch
        {
            1 => true,
            0 => false,
            _ => null,
        };
    }
}
