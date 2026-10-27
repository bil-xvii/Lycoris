using System.Management;
using Monolith.Core;
using Monolith.Modules.Drivers.Hardware;

namespace Monolith.Modules.Apps;

/// <summary>Result of the peripheral scan. <see cref="Known"/> = false: the scan failed or saw no USB/Bluetooth devices at all.</summary>
public sealed record PeripheralScanResult(bool Known, IReadOnlySet<PeripheralBrand> Brands)
{
    public static PeripheralScanResult Unknown { get; } = new(false, new HashSet<PeripheralBrand>());
}

/// <summary>
/// Read-only scan for gaming peripherals (Logitech, Razer, Corsair, SteelSeries, Elgato) by the USB vendor id in
/// Win32_PnPEntity device ids. Used only to advise whether the maker's configuration software is worth installing.
/// Device-id parsing is the Drivers page's <see cref="HardwareRules.UsbId"/> (USB/HID "VID_xxxx", Bluetooth "VID&amp;0002xxxx").
/// </summary>
public static class PeripheralScan
{
    /// <summary>USB-IF vendor ids (usb.ids: 046d Logitech, Inc. · 1532 Razer USA · 1b1c Corsair · 1038 SteelSeries ApS · 0fd9 Elgato Systems GmbH).</summary>
    public static IReadOnlyDictionary<string, PeripheralBrand> VendorIds { get; } = new Dictionary<string, PeripheralBrand>(StringComparer.OrdinalIgnoreCase)
    {
        ["046D"] = PeripheralBrand.Logitech,
        ["1532"] = PeripheralBrand.Razer,
        ["1B1C"] = PeripheralBrand.Corsair,
        ["1038"] = PeripheralBrand.SteelSeries,
        ["0FD9"] = PeripheralBrand.Elgato,
    };

    /// <summary>The USB-IF vendor id in a PnP device id ("046D"), or null (PCI ids, Bluetooth SIG company ids, no id).</summary>
    public static string? ParseVendorId(string? pnpDeviceId) => HardwareRules.UsbId(pnpDeviceId)?.Vid;

    public static PeripheralBrand? BrandOf(string? pnpDeviceId) =>
        ParseVendorId(pnpDeviceId) is { } vid && VendorIds.TryGetValue(vid, out var b) ? b : null;

    /// <summary>Makers found among the device ids. Unknown = no device id carried a USB vendor id at all.</summary>
    public static PeripheralScanResult Classify(IEnumerable<string?> pnpDeviceIds)
    {
        var any = false;
        var found = new HashSet<PeripheralBrand>();
        foreach (var id in pnpDeviceIds)
        {
            if (ParseVendorId(id) is not { } vid) continue;
            any = true;
            if (VendorIds.TryGetValue(vid, out var b)) found.Add(b);
        }
        return any ? new PeripheralScanResult(true, found) : PeripheralScanResult.Unknown;
    }

    /// <summary>Runs the WMI query off the UI thread. Any failure → <see cref="PeripheralScanResult.Unknown"/>.</summary>
    public static Task<PeripheralScanResult> ScanAsync() => Task.Run(Scan);

    private static PeripheralScanResult Scan()
    {
        try
        {
            try
            {
                return Classify(ReadIds("SELECT DeviceID, ConfigManagerErrorCode, Present FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID%'", true));
            }
            catch (ManagementException)
            {
                // Older builds without the Present property.
                return Classify(ReadIds("SELECT DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID%'", false));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Приложения: не удалось проверить подключённые устройства ({ex.Message})");
            return PeripheralScanResult.Unknown;
        }
    }

    private static List<string?> ReadIds(string wql, bool withPresent)
    {
        var ids = new List<string?>();
        using var searcher = SystemProfile.Searcher(@"\\.\root\cimv2", wql);
        foreach (var mo in searcher.Get())
            using (mo)
            {
                if (withPresent && mo["Present"] is false) continue;
                // 45 = CM_PROB_PHANTOM: remembered by Windows but not connected now.
                if (Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0) == 45) continue;
                ids.Add(mo["DeviceID"]?.ToString());
            }
        return ids;
    }
}
