using System.Management;
using Lycoris.Core;
using Lycoris.Modules.Drivers.Hardware;

namespace Lycoris.Modules.Apps;

/// <summary>Result of the peripheral scan. <see cref="Known"/> = false: the scan failed or saw no USB/Bluetooth devices at all.</summary>
public sealed record PeripheralScanResult(bool Known, IReadOnlySet<PeripheralBrand> Brands)
{
    public static PeripheralScanResult Unknown { get; } = new(false, new HashSet<PeripheralBrand>());
}

/// <summary>One Win32_PnPEntity node: its device id and device class (PNPClass; null when Windows has no driver for it).</summary>
public readonly record struct PnpNode(string? DeviceId, string? PnpClass);

/// <summary>
/// Read-only scan for gaming peripherals (Logitech, Razer, Corsair, SteelSeries, Elgato Stream Deck) by the USB vendor id
/// in Win32_PnPEntity device ids. Used only to advise whether the maker's configuration software is worth installing.
/// Device-id parsing is the Drivers page's <see cref="HardwareRules.UsbId"/> (USB/HID "VID_xxxx", Bluetooth "VID&amp;0002xxxx").
/// Only peripheral device classes count, so a maker's flash drive or hub does not make its software "РЕКОМЕНДУЕТСЯ".
/// </summary>
public static class PeripheralScan
{
    /// <summary>
    /// USB-IF vendor ids (usb.ids: 046d Logitech, Inc. · b58e Blue Microphones (Logitech; Yeti microphones are configured in
    /// G HUB) · 1532 Razer USA · 1b1c Corsair · 1038 SteelSeries ApS · 0fd9 Elgato Systems GmbH).
    /// </summary>
    public static IReadOnlyDictionary<string, PeripheralBrand> VendorIds { get; } = new Dictionary<string, PeripheralBrand>(StringComparer.OrdinalIgnoreCase)
    {
        ["046D"] = PeripheralBrand.Logitech,
        ["B58E"] = PeripheralBrand.Logitech,
        ["1532"] = PeripheralBrand.Razer,
        ["1B1C"] = PeripheralBrand.Corsair,
        ["1038"] = PeripheralBrand.SteelSeries,
        ["0FD9"] = PeripheralBrand.Elgato,
    };

    public const string LogitechVid = "046D";
    public const string ElgatoVid = "0FD9";

    /// <summary>
    /// Stream Deck product ids (usb.ids 0060/0063/006c/006d; python-elgato-streamdeck ProductIDs.py for the rest). Other
    /// Elgato hardware — capture cards, Cam Link, Facecam, Wave microphones — does not use the Stream Deck app.
    /// </summary>
    public static IReadOnlySet<string> StreamDeckPids { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "0060", "0063", "006C", "006D", "0080", "0084", "0086", "008F", "0090", "009A",
        "00A5", "00AA", "00B3", "00B8", "00B9", "00BA", "00C6",
    };

    /// <summary>Input devices: the makers' programs talk to mice, keyboards, headsets, wheels and Stream Decks over HID.</summary>
    public static IReadOnlySet<string> InputClasses { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HIDClass", "Keyboard", "Mouse" };

    /// <summary>
    /// Device classes a maker's program may configure (the Drivers page's peripheral classes): input devices, audio
    /// (headsets, microphones) and cameras. Hubs, storage ("USB", "DiskDrive", "WPD") and the rest do not count.
    /// </summary>
    public static IReadOnlySet<string> PeripheralClasses { get; } =
        new HashSet<string>(InputClasses, StringComparer.OrdinalIgnoreCase) { "MEDIA", "Camera", "Image" };

    /// <summary>The USB-IF vendor id in a PnP device id ("046D"), or null (PCI ids, Bluetooth SIG company ids, no id).</summary>
    public static string? ParseVendorId(string? pnpDeviceId) => HardwareRules.UsbId(pnpDeviceId)?.Vid;

    /// <summary>
    /// The maker whose software configures this node, or null. Logitech counts input devices only (G HUB is not for its
    /// webcams), Elgato only Stream Decks. A node without a class (no driver) is not ruled out.
    /// </summary>
    public static PeripheralBrand? BrandOf(string? pnpDeviceId, string? pnpClass)
    {
        if (HardwareRules.UsbId(pnpDeviceId) is not { } id || !VendorIds.TryGetValue(id.Vid, out var brand)) return null;
        var classes = string.Equals(id.Vid, LogitechVid, StringComparison.OrdinalIgnoreCase) ? InputClasses : PeripheralClasses;
        if (!string.IsNullOrWhiteSpace(pnpClass) && !classes.Contains(pnpClass.Trim())) return null;
        if (string.Equals(id.Vid, ElgatoVid, StringComparison.OrdinalIgnoreCase) && !StreamDeckPids.Contains(id.Pid)) return null;
        return brand;
    }

    /// <summary>Makers found among the nodes. Unknown = no device id carried a USB vendor id at all.</summary>
    public static PeripheralScanResult Classify(IEnumerable<PnpNode> nodes)
    {
        var any = false;
        var found = new HashSet<PeripheralBrand>();
        foreach (var node in nodes)
        {
            if (ParseVendorId(node.DeviceId) is null) continue;
            any = true;
            if (BrandOf(node.DeviceId, node.PnpClass) is { } b) found.Add(b);
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
                return Classify(ReadNodes("SELECT DeviceID, PNPClass, ConfigManagerErrorCode, Present FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID%'", true));
            }
            catch (ManagementException)
            {
                // Older builds without the Present property.
                return Classify(ReadNodes("SELECT DeviceID, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID%'", false));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Приложения: не удалось проверить подключённые устройства ({0})", ex.Message);
            return PeripheralScanResult.Unknown;
        }
    }

    private static List<PnpNode> ReadNodes(string wql, bool withPresent)
    {
        var nodes = new List<PnpNode>();
        using var searcher = SystemProfile.Searcher(@"\\.\root\cimv2", wql);
        foreach (var mo in searcher.Get())
            using (mo)
            {
                if (withPresent && mo["Present"] is false) continue;
                // 45 = CM_PROB_PHANTOM: remembered by Windows but not connected now.
                if (Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0) == 45) continue;
                nodes.Add(new PnpNode(mo["DeviceID"]?.ToString(), mo["PNPClass"]?.ToString()));
            }
        return nodes;
    }
}
