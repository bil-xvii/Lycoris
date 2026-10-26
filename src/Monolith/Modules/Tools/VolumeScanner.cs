using System.IO;
using System.Management;
using Monolith.Core;

namespace Monolith.Modules.Tools;

/// <summary>A fixed volume that "Оптимизация дисков" (defrag /O) can work on.</summary>
public sealed record VolumeInfo(
    string Letter, string Label, string FileSystem, ulong SizeBytes, ulong FreeBytes,
    string MediaType, string BusType, bool IsSystem)
{
    public bool IsSsd => MediaType is "SSD" or "SCM";
    public bool IsHdd => MediaType == "HDD";

    public string OperationText => IsSsd ? "TRIM" : IsHdd ? "ДЕФРАГМЕНТАЦИЯ" : "АВТОВЫБОР";

    public string MediaText => MediaType == "—" ? "ТИП НЕ ОПРЕДЕЛЁН" : BusType is "—" or "" ? MediaType : $"{MediaType} · {BusType}";

    public string SpaceText => SizeBytes == 0
        ? FileSystem
        : $"{FileSystem} · {SystemProfile.FormatBytes(FreeBytes)} свободно из {SystemProfile.FormatBytes(SizeBytes)}";

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? (IsSystem ? "Системный диск" : "Локальный диск") : Label;
}

/// <summary>Enumerates fixed NTFS/ReFS/FAT volumes with their media type (read-only: DriveInfo + Storage WMI).</summary>
public static class VolumeScanner
{
    private static readonly HashSet<string> SupportedFileSystems = new(StringComparer.OrdinalIgnoreCase) { "NTFS", "ReFS", "FAT", "FAT32" };

    public static Task<IReadOnlyList<VolumeInfo>> ScanAsync() => Task.Run<IReadOnlyList<VolumeInfo>>(Scan);

    private static IReadOnlyList<VolumeInfo> Scan()
    {
        var media = ReadMediaTypes();
        var system = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\').ToUpperInvariant();
        var list = new List<VolumeInfo>();
        foreach (var d in SafeDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                if (!SupportedFileSystems.Contains(d.DriveFormat)) continue;
                var letter = d.Name.TrimEnd('\\').ToUpperInvariant();
                var (type, bus) = media.TryGetValue(letter[0], out var m) ? m : ("—", "—");
                list.Add(new VolumeInfo(letter, d.VolumeLabel, d.DriveFormat, (ulong)d.TotalSize, (ulong)d.AvailableFreeSpace,
                    type, bus, letter == system));
            }
            catch (Exception ex)
            {
                Log.Warn($"Диск {d.Name}: {ex.Message}");
            }
        }
        return list.OrderByDescending(v => v.IsSystem).ThenBy(v => v.Letter, StringComparer.Ordinal).ToList();
    }

    private static DriveInfo[] SafeDrives()
    {
        try { return DriveInfo.GetDrives(); }
        catch { return []; }
    }

    /// <summary>Drive letter → (SSD/HDD/SCM/—, NVMe/SATA/USB/…) via MSFT_Partition → MSFT_PhysicalDisk.</summary>
    private static Dictionary<char, (string Media, string Bus)> ReadMediaTypes()
    {
        var result = new Dictionary<char, (string, string)>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            var disks = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk")))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        var bus = BusName(Convert.ToUInt16(mo["BusType"] ?? (ushort)0));
                        var type = Convert.ToUInt16(mo["MediaType"] ?? (ushort)0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "—" };
                        if (type == "—" && bus == "NVMe") type = "SSD";
                        disks[mo["DeviceId"]?.ToString() ?? ""] = (type, bus);
                    }
                }
            }
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition")))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        var letter = ToLetter(mo["DriveLetter"]);
                        if (letter == '\0') continue;
                        var disk = Convert.ToUInt32(mo["DiskNumber"] ?? 0u).ToString();
                        if (disks.TryGetValue(disk, out var info)) result[char.ToUpperInvariant(letter)] = info;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось определить тип дисков: {ex.Message}");
        }
        return result;
    }

    private static char ToLetter(object? value) => value switch
    {
        char c => c,
        ushort u => (char)u,
        short s => (char)s,
        string { Length: > 0 } str => str[0],
        _ => '\0',
    };

    private static string BusName(ushort bus) => bus switch
    {
        17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 3 => "ATA", 1 => "SCSI", 18 => "SCM", _ => "—",
    };
}
