using System.Management;
using Lycoris.Core;

namespace Lycoris.Modules.Dashboard.Checks;

/// <summary>
/// Drive health from the Windows storage stack (root\Microsoft\Windows\Storage): MSFT_PhysicalDisk.HealthStatus for
/// every drive and, with administrator rights, its MSFT_StorageReliabilityCounter (wear, temperature, uncorrected
/// errors) through the documented MSFT_PhysicalDiskToStorageReliabilityCounter association.
/// Strictly read-only: only enumerations and property reads — never Reset, Maintenance, SetAttributes or any other
/// method of these classes. Each drive is read separately, so one that does not answer only loses its own counters.
/// </summary>
internal static class DiskHealthProbe
{
    private const string Storage = @"\\.\root\Microsoft\Windows\Storage";

    /// <summary>BusType values of virtual drives (VHD, Storage Spaces): not hardware, skipped.</summary>
    private static readonly HashSet<ushort> VirtualBuses = [14, 15, 16];

    public static IReadOnlyList<DiskHealth>? Read(bool elevated)
    {
        var list = new List<DiskHealth>();
        var scope = new ManagementScope(Storage, new ConnectionOptions { Timeout = TimeSpan.FromSeconds(10) });
        var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(20), ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"), options);
        using var disks = searcher.Get();
        foreach (ManagementObject disk in disks)
        {
            using (disk)
            {
                var bus = ToUShort(disk["BusType"]);
                if (bus is { } b && VirtualBuses.Contains(b)) continue;
                var media = ToUShort(disk["MediaType"]) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
                var busName = bus switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 3 => "ATA", 18 => "SCM", _ => "" };
                if (busName == "NVMe" && media.Length == 0) media = "SSD";
                var name = (disk["FriendlyName"]?.ToString() ?? "").Trim();
                var status = HealthRules.DiskStatusFromWmi(ToUShort(disk["HealthStatus"]));
                var health = new DiskHealth(name.Length > 0 ? name : Loc.T("Накопитель"), busName, media, status);
                if (elevated) health = ReadCounters(disk, health);
                list.Add(health);
            }
        }
        return list;
    }

    private static DiskHealth ReadCounters(ManagementObject disk, DiskHealth health)
    {
        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = true, Rewindable = false };
            using var related = disk.GetRelated("MSFT_StorageReliabilityCounter", "MSFT_PhysicalDiskToStorageReliabilityCounter",
                null, null, null, null, false, options);
            foreach (ManagementObject counter in related)
            {
                using (counter)
                {
                    var temp = ToInt(counter["Temperature"]);
                    var tempMax = ToInt(counter["TemperatureMax"]);
                    return health with
                    {
                        WearPercent = ToInt(counter["Wear"]) is { } w and >= 0 and <= 255 ? w : null,
                        TemperatureC = temp is > 0 and < 150 ? temp : null,     // 0 = not reported
                        TemperatureMaxC = tempMax is > 0 and < 150 ? tempMax : null,
                        ReadErrorsUncorrected = ToULong(counter["ReadErrorsUncorrected"]),
                        WriteErrorsUncorrected = ToULong(counter["WriteErrorsUncorrected"]),
                    };
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn("Состояние накопителя «{0}»: счётчики недоступны — {1}", health.Name, ex.Message);
        }
        return health; // USB bridges and RAID controllers often report nothing: not an error
    }

    private static ushort? ToUShort(object? o)
    {
        try { return o is null ? null : Convert.ToUInt16(o); }
        catch { return null; }
    }

    private static int? ToInt(object? o)
    {
        try { return o is null ? null : Convert.ToInt32(o); }
        catch { return null; }
    }

    private static ulong? ToULong(object? o)
    {
        try { return o is null ? null : Convert.ToUInt64(o); }
        catch { return null; }
    }
}
