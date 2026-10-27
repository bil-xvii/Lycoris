using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Monolith.Core.Devices;
using Monolith.Modules.Drivers.Hardware;
using Monolith.UI;

namespace Monolith.Modules.Devices.Vendors;

/// <summary>
/// What a physical device is, as far as configuring it goes. <see cref="Other"/> = not an input / audio / HID device at all
/// (webcam, network adapter, serial port…): used for matching only, never a card here.
/// </summary>
internal enum InventoryKind { Mouse, Keyboard, Audio, Controller, Lighting, Peripheral, Other }

/// <summary>
/// One physical device: all its device nodes (USB parent, interfaces, HID collections) together. <see cref="InstanceIds"/>
/// may hold serial numbers — they stay in memory for matching and are never shown or logged; <see cref="CardKey"/> is a hash.
/// </summary>
internal sealed record InventoryGroup(
    string Key,
    string? Vid,
    string? Pid,
    string Name,
    InventoryKind Kind,
    IReadOnlyList<string> InstanceIds,
    string? ContainerId,
    bool HasLampArray,
    PeripheralVendor? Vendor)
{
    /// <summary>"periph:" + 12 hex digits of SHA-256 of the group key — stable, and no serial in it.</summary>
    public string CardKey => DeviceInventory.CardKey(Key);
    public string? VidPid => Vid is null || Pid is null ? null : $"{Vid}:{Pid}";
}

/// <summary>A device that was plugged in before and is not now («раньше по кабелю подключалась: …»).</summary>
internal sealed record HistoryHint(string Vid, string Pid, string Name, DateTime LastArrivalUtc);

/// <summary>
/// The device inventory (devices_vendors.md §2): present USB / HID / Bluetooth device nodes from CfgMgr32, one group per
/// physical device. Read-only — the PnP database only, no I/O to any device. Virtual subtrees (G HUB's LGHUBDevice\…),
/// LampArray children (a «Dynamic Lighting» flag instead), hubs, storage and Bluetooth radios are not devices here.
/// </summary>
internal static partial class DeviceInventory
{
    public static readonly IReadOnlyList<string> Enumerators = ["USB", "HID", "BTHENUM", "BTHLE", "BTHLEDevice"];
    public const string LocalMachineContainer = "{00000000-0000-0000-FFFF-FFFFFFFFFFFF}";
    /// <summary>How far back the cable history counts.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(180);

    /// <summary>Board / case RGB controllers that are only lighting (verified ids).</summary>
    private static readonly HashSet<string> LightingIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "0DB0:0076", // MSI «MYSTIC LIGHT» (internal USB header) — dev PC
        "1B1C:0C0B", // Corsair Lighting Node Pro (usb.ids)
    };

    // ───────────────────────────── Live reads (worker thread) ─────────────────────────────

    /// <summary>Present devices, grouped. Never throws (a failed enumerator only means fewer devices).</summary>
    public static IReadOnlyList<InventoryGroup> Read()
    {
        var nodes = Enumerators.SelectMany(e => PnpTree.Enumerate(e, presentOnly: true))
            .DistinctBy(n => n.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var cache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        return Group(nodes, id => cache.TryGetValue(id, out var chain) ? chain : cache[id] = PnpTree.Ancestors(id));
    }

    /// <summary>Devices of one vendor seen by cable before (non-present nodes), newest first.</summary>
    public static IReadOnlyList<HistoryHint> ReadHistory(string vid, InventoryKind kind, IReadOnlySet<string> presentVidPids)
    {
        var nodes = PnpTree.Enumerate("USB", presentOnly: false).Concat(PnpTree.Enumerate("HID", presentOnly: false));
        return History(nodes, vid, kind, DateTime.UtcNow, presentVidPids);
    }

    // ───────────────────────────── Pure rules ─────────────────────────────

    [GeneratedRegex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbDeviceNodeRegex();

    /// <summary>The USB device node itself ("USB\VID_x&amp;PID_y\…"), not an interface (&amp;MI_) or a LampArray child.</summary>
    internal static bool IsUsbDeviceNode(string instanceId) => UsbDeviceNodeRegex().IsMatch(instanceId);

    internal static bool IsLampArray(PnpNode n) =>
        n.InstanceId.Contains("&LAMPARRAY", StringComparison.OrdinalIgnoreCase)
        || string.Equals(n.Service, "logi_lamparray", StringComparison.OrdinalIgnoreCase);

    private static bool IsHub(PnpNode n) =>
        n.Service is { } s && (s.Equals("usbhub", StringComparison.OrdinalIgnoreCase) || s.Equals("usbhub3", StringComparison.OrdinalIgnoreCase))
        || n.InstanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase);

    private static bool IsStorage(PnpNode n) =>
        n.Class is { } c && (c.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase) || c.Equals("WPD", StringComparison.OrdinalIgnoreCase)
                             || c.Equals("CDROM", StringComparison.OrdinalIgnoreCase) || c.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase))
        || n.Service is { } s && (s.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase) || s.Equals("UASPStor", StringComparison.OrdinalIgnoreCase))
        || n.InstanceId.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bluetooth radios and their protocol nodes (the radio is a network part, not a peripheral).</summary>
    private static bool IsBluetoothRadio(PnpNode n) => string.Equals(n.Class, "Bluetooth", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalMachine(PnpNode n) =>
        n.InLocalMachineContainer || n.ContainerId is null || string.Equals(n.ContainerId, LocalMachineContainer, StringComparison.OrdinalIgnoreCase)
        || string.Equals(n.ContainerId, Guid.Empty.ToString("B"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Groups device nodes into physical devices. Key = the container, except the local-machine / missing container (internal
    /// devices such as the MSI RGB controller): then the nearest USB device node above it. <paramref name="ancestors"/> gives
    /// a node's parents, nearest first.
    /// </summary>
    internal static IReadOnlyList<InventoryGroup> Group(IEnumerable<PnpNode> nodes, Func<string, IReadOnlyList<string>> ancestors)
    {
        var lamp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, List<PnpNode>>(StringComparer.OrdinalIgnoreCase);
        var containerKeyed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var n in nodes)
        {
            if (string.IsNullOrWhiteSpace(n.InstanceId)) continue;
            if (IsLampArray(n))
            {
                if (HardwareRules.UsbId(n.InstanceId) is { } lid) lamp.Add($"{lid.Vid}:{lid.Pid}");
                continue;
            }
            IReadOnlyList<string> chain;
            try { chain = ancestors(n.InstanceId); }
            catch { chain = []; }
            if (PnpTree.IsVirtualChain(chain.Prepend(n.InstanceId))) continue;
            if (IsHub(n) || IsStorage(n) || IsBluetoothRadio(n)) continue;

            string key;
            if (!IsLocalMachine(n))
            {
                key = n.ContainerId!;
                containerKeyed.Add(key);
            }
            else
            {
                key = (IsUsbDeviceNode(n.InstanceId) ? n.InstanceId : chain.FirstOrDefault(IsUsbDeviceNode)) ?? n.InstanceId;
            }
            if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = [];
            list.Add(n);
        }

        var groups = new List<InventoryGroup>();
        foreach (var (key, list) in byKey)
        {
            var usb = list.FirstOrDefault(n => IsUsbDeviceNode(n.InstanceId));
            var id = (usb is null ? null : HardwareRules.UsbId(usb.InstanceId))
                     ?? list.Select(n => HardwareRules.UsbId(n.InstanceId)).FirstOrDefault(x => x is not null);
            var vid = id?.Vid;
            var pid = id?.Pid;
            var vendor = HardwareCatalog.PeripheralByVid(vid);
            var kind = KindOf(list, vid, pid);
            groups.Add(new InventoryGroup(
                key, vid, pid, NameOf(list, usb, vid, pid, kind, vendor), kind,
                list.Select(n => n.InstanceId).ToList(),
                containerKeyed.Contains(key) ? key : null,
                vid is not null && pid is not null && lamp.Contains($"{vid}:{pid}"),
                vendor));
        }
        return groups.OrderBy(g => g.Kind).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Lighting (known RGB controllers) › gamepad › audio › mouse › keyboard › other HID device › anything else. A receiver is a
    /// mouse (it also exposes a keyboard collection for macros).
    /// </summary>
    internal static InventoryKind KindOf(IReadOnlyCollection<PnpNode> nodes, string? vid, string? pid)
    {
        if (vid is not null && pid is not null && LightingIds.Contains($"{vid}:{pid}")) return InventoryKind.Lighting;
        if (nodes.Any(IsGameController)) return InventoryKind.Controller;
        if (nodes.Any(n => ClassIs(n, "MEDIA") || ClassIs(n, "AudioEndpoint"))) return InventoryKind.Audio;
        if (ReceiverHints.IsReceiver(vid, pid) || nodes.Any(n => ClassIs(n, "Mouse"))) return InventoryKind.Mouse;
        if (nodes.Any(n => ClassIs(n, "Keyboard"))) return InventoryKind.Keyboard;
        if (nodes.Any(n => ClassIs(n, "HIDClass"))) return InventoryKind.Peripheral;
        return InventoryKind.Other;
    }

    private static bool ClassIs(PnpNode n, string cls) => string.Equals(n.Class, cls, StringComparison.OrdinalIgnoreCase);

    /// <summary>HID game controller: "…&amp;IG_" (XInput) in its ids, HID_DEVICE_SYSTEM_GAME, or an Xbox composite class.</summary>
    private static bool IsGameController(PnpNode n) =>
        n.InstanceId.Contains("&IG_", StringComparison.OrdinalIgnoreCase)
        || n.HardwareIds.Any(h => h.Contains("&IG_", StringComparison.OrdinalIgnoreCase)
                                  || h.Equals("HID_DEVICE_SYSTEM_GAME", StringComparison.OrdinalIgnoreCase))
        || ClassIs(n, "XnaComposite") || ClassIs(n, "XboxComposite");

    /// <summary>A product string worth showing: not empty, not a class name, not «USB Receiver»-like.</summary>
    internal static bool IsGoodName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !ReceiverHints.IsGenericProductName(name) && !HardwareRules.IsGenericDeviceName(name);

    /// <summary>
    /// The model name (devices_vendors.md §2.2): the USB device's own product string (bus-reported, not localized, no vendor
    /// software needed) › an interface's › the receiver table › a non-generic Windows name › vendor + kind.
    /// </summary>
    internal static string NameOf(IReadOnlyList<PnpNode> nodes, PnpNode? usb, string? vid, string? pid, InventoryKind kind, PeripheralVendor? vendor)
    {
        if (IsGoodName(usb?.BusReportedName)) return usb!.BusReportedName!.Trim();
        var bus = nodes.OrderBy(n => ClassIs(n, "HIDClass") ? 0 : 1).Select(n => n.BusReportedName).FirstOrDefault(IsGoodName);
        if (bus is not null) return bus.Trim();
        if (ReceiverHints.Name(vid, pid) is { } receiver) return receiver;
        var friendly = new[] { usb }.Concat(nodes).Select(n => n?.FriendlyName).FirstOrDefault(IsGoodName);
        if (friendly is not null) return friendly.Trim();
        var word = kind switch
        {
            InventoryKind.Mouse => "Мышь",
            InventoryKind.Keyboard => "Клавиатура",
            InventoryKind.Audio => "Аудиоустройство",
            InventoryKind.Controller => "Геймпад",
            InventoryKind.Lighting => "Контроллер подсветки",
            _ => "Устройство",
        };
        return vendor is null ? word : $"{word} {vendor.Name}";
    }

    /// <summary>
    /// The cable history of a vendor: non-present devices of <paramref name="kind"/> (mouse / keyboard nodes), named by their own
    /// product string (bootloaders excluded), last plugged in within <see cref="HistoryWindow"/>, not present now, not receivers.
    /// </summary>
    internal static IReadOnlyList<HistoryHint> History(IEnumerable<PnpNode> nodes, string vid, InventoryKind kind, DateTime nowUtc,
        IReadOnlySet<string>? presentVidPids = null)
    {
        var cls = kind == InventoryKind.Keyboard ? "Keyboard" : "Mouse";
        var result = new List<HistoryHint>();
        foreach (var g in nodes.Where(n => !n.Present)
                     .Select(n => (Node: n, Id: HardwareRules.UsbId(n.InstanceId)))
                     .Where(x => x.Id is { } id && string.Equals(id.Vid, vid, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(x => x.Id!.Value.Pid, StringComparer.OrdinalIgnoreCase))
        {
            var pid = g.Key;
            var list = g.Select(x => x.Node).ToList();
            if (ReceiverHints.IsReceiver(vid, pid) || presentVidPids?.Contains($"{vid}:{pid}") == true) continue;
            if (!list.Any(n => ClassIs(n, cls))) continue;
            var usb = list.FirstOrDefault(n => IsUsbDeviceNode(n.InstanceId));
            var name = new[] { usb }.Concat(list).Select(n => n?.BusReportedName?.Trim()).FirstOrDefault(IsGoodName);
            if (name is null || name.StartsWith("Bootloader", StringComparison.OrdinalIgnoreCase)) continue;
            var last = list.Select(n => n.LastArrival).Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty().Max();
            if (last == default || last < nowUtc - HistoryWindow || last > nowUtc + TimeSpan.FromDays(1)) continue;
            result.Add(new HistoryHint(vid.ToUpperInvariant(), pid.ToUpperInvariant(), name, last));
        }
        return result.OrderByDescending(h => h.LastArrivalUtc).ToList();
    }

    /// <summary>"periph:" + the first 12 hex digits of SHA-256(group key, upper case).</summary>
    internal static string CardKey(string groupKey) =>
        "periph:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(groupKey.ToUpperInvariant())))[..12].ToLowerInvariant();

    /// <summary>A card title from a product string: all-caps strings in sentence case («MYSTIC LIGHT» → «Mystic Light»), plus the vendor.</summary>
    internal static string DisplayTitle(InventoryGroup g)
    {
        var name = g.Name.Any(char.IsLower) ? g.Name : SentenceCaseConverter.Apply(g.Name);
        return g.Vendor is { } v && !name.Contains(v.Name, StringComparison.OrdinalIgnoreCase) ? $"{name} ({v.Name})" : name;
    }
}
