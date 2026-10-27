using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Monolith.Core;
using Monolith.Core.Devices;

namespace Monolith.Modules.Devices.Input;

internal enum RawKind { Mouse = 0, Keyboard = 1, Hid = 2 }

/// <summary>One raw input collection: its handle, kind and HID instance id.</summary>
internal sealed record RawCollection(IntPtr Handle, RawKind Kind, string InstanceId);

/// <summary>What the PnP tree says about a collection (read once per scan).</summary>
internal sealed record CollectionNode(bool IsVirtual, string? PhysicalId, string? Bus, string? Vid, string? Pid, string? ContainerId,
    bool InLocalMachineContainer, string? Name);

/// <summary>
/// One physical input device (a mouse, a keyboard, a receiver) with every raw collection it exposes. A LIGHTSPEED
/// receiver has a mouse and a keyboard collection; <see cref="PrimaryKind"/> (the collection on the lowest interface)
/// says which card it belongs to.
/// </summary>
internal sealed record PhysicalInputDevice(string GroupKey, string? Vid, string? Pid, string? Bus, string? PhysicalId, string? Name,
    string? ContainerId, IReadOnlyList<IntPtr> MouseHandles, IReadOnlyList<IntPtr> KeyboardHandles, IReadOnlyList<string> InstanceIds,
    DeviceKind PrimaryKind)
{
    public bool HasMouse => MouseHandles.Count > 0;
    public bool HasKeyboard => KeyboardHandles.Count > 0;

    /// <summary>«USB 046D:C54D» (bus + ids), or null.</summary>
    public string? IdText => Vid is null || Pid is null ? Bus : $"{Bus ?? "USB"} {Vid}:{Pid}";

    /// <summary>«USB Receiver · USB 046D:C54D».</summary>
    public string Label => (Name, IdText) switch
    {
        ({ } n, { } id) => $"{n} · {id}",
        ({ } n, null) => n,
        (null, { } id) => id,
        _ => "устройство без названия",
    };

    /// <summary>For the vendor enrichment (matched by VID:PID, container or instance ids). Never shown with serials.</summary>
    public DeviceHardwareRef ToHardwareRef() => new(Vid, Pid, ContainerId, InstanceIds);
}

/// <summary>The mice and keyboards of this PC (virtual ones dropped). A device appears in one list unless no other exists.</summary>
internal sealed record InputInventory(IReadOnlyList<PhysicalInputDevice> Mice, IReadOnlyList<PhysicalInputDevice> Keyboards)
{
    public static readonly InputInventory Empty = new([], []);

    /// <summary>Raw handles of the physical mice (measurements ignore everything else, e.g. the G HUB virtual mouse).</summary>
    public IReadOnlySet<IntPtr> MouseHandles => Mice.SelectMany(m => m.MouseHandles).ToHashSet();

    public PhysicalInputDevice? MouseByHandle(IntPtr handle) => Mice.FirstOrDefault(m => m.MouseHandles.Contains(handle));
}

/// <summary>
/// Raw input devices → physical mice and keyboards: GetRawInputDeviceList + GetRawInputDeviceInfo(RIDI_DEVICENAME), the
/// HID instance id from the interface path, the PnP tree for the bus device, its container and the bus-reported name.
/// Read-only, no raw-input registration, no admin. Instance ids can hold serial numbers: they are never logged.
/// </summary>
internal static class InputDevices
{
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEKEYBOARD = 1;

    /// <summary>The local-machine container: built-in devices share it, so it never groups devices.</summary>
    private static readonly string LocalMachineContainer = new Guid("00000000-0000-0000-ffff-ffffffffffff").ToString("B");

    // ───────────────────────────── Pure parsing ─────────────────────────────

    /// <summary>
    /// \\?\HID#VID_046D&amp;PID_C54D&amp;MI_00#7&amp;abc&amp;0&amp;0000#{guid} → HID\VID_046D&amp;PID_C54D&amp;MI_00\7&amp;abc&amp;0&amp;0000.
    /// Null for anything that is not a device interface path.
    /// </summary>
    public static string? InterfacePathToInstanceId(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().TrimEnd('\0');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        else return null;
        // …#{interface class guid}, optionally followed by a reference string (\KBD on some keyboards).
        var guid = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid <= 0 || !InterfaceClassTail.IsMatch(p[(guid + 1)..])) return null;
        p = p[..guid];
        var parts = p.Split('#');
        if (parts.Length < 2 || parts.Any(s => s.Length == 0) || p.Any(c => char.IsControl(c) || c == '\\')) return null;
        return string.Join('\\', parts);
    }

    private static readonly Regex InterfaceClassTail =
        new(@"^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}(\\[^\\]*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UsbStyle = new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Bluetooth: …_VID&0002046d_PID&b023… (BTHENUM / HID over BT) or …_VID&02046d_PID&b023… (BTHLE).
    private static readonly Regex BtStyle = new(@"VID&[0-9A-F]{2}([0-9A-F]{4})_PID&([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>VID and PID (upper-case hex) from a USB, HID or Bluetooth instance id.</summary>
    public static (string Vid, string Pid)? ParseVidPid(string? id)
    {
        if (id is null) return null;
        var m = UsbStyle.Match(id);
        if (!m.Success) m = BtStyle.Match(id);
        return m.Success ? (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant()) : null;
    }

    private static readonly Regex UsbDevice = new(@"^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The USB device node itself (USB\VID_x&amp;PID_y\serial), not one of its interfaces (…&amp;MI_00\…).</summary>
    public static bool IsUsbDeviceId(string id) => UsbDevice.IsMatch(id);

    public static bool IsBluetoothId(string id) =>
        id.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"BTHLE\", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith(@"BTHLEDevice\", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex InterfaceNumber = new(@"&MI_([0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CollectionNumber = new(@"&Col([0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>(interface, collection) of a HID instance id: MI_01&amp;Col01 → (1, 1); none → (0, 0).</summary>
    public static (int Interface, int Collection) InterfaceOrder(string instanceId)
    {
        var device = instanceId.Split('\\') is { Length: >= 2 } parts ? parts[1] : instanceId;
        var mi = InterfaceNumber.Match(device);
        var col = CollectionNumber.Match(device);
        return (mi.Success ? int.Parse(mi.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0,
            col.Success ? int.Parse(col.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0);
    }

    // ───────────────────────────── Grouping (pure over a resolver) ─────────────────────────────

    /// <summary>
    /// Physical devices from raw collections. Virtual collections (G HUB's LGHUBDevice mouse and keyboard, RDP) and ones the
    /// PnP tree does not know are dropped. Grouped by ContainerId (one physical device), or — for the local-machine
    /// container or none — by the USB / Bluetooth device node.
    /// </summary>
    public static IReadOnlyList<PhysicalInputDevice> Group(IEnumerable<RawCollection> raw, Func<string, CollectionNode?> resolve)
    {
        var entries = new List<(RawCollection Raw, CollectionNode Node, string Key)>();
        foreach (var r in raw)
        {
            if (r.Handle == IntPtr.Zero || r.Kind == RawKind.Hid) continue;
            CollectionNode? node;
            try { node = resolve(r.InstanceId); }
            catch { node = null; }
            if (node is null || node.IsVirtual) continue;
            var container = node.ContainerId is { Length: > 0 } c && !node.InLocalMachineContainer
                                                                   && !string.Equals(c, LocalMachineContainer, StringComparison.OrdinalIgnoreCase)
                ? c : null;
            var key = container is not null ? "container:" + container.ToUpperInvariant()
                : "node:" + (node.PhysicalId ?? r.InstanceId).ToUpperInvariant();
            entries.Add((r, node with { ContainerId = container }, key));
        }

        var devices = new List<PhysicalInputDevice>();
        foreach (var g in entries.GroupBy(e => e.Key, StringComparer.Ordinal))
        {
            var items = g.ToList();
            var first = items.Find(i => i.Node.Vid is not null);
            if (first.Raw is null) first = items[0];
            var primary = items.OrderBy(i => InterfaceOrder(i.Raw.InstanceId)).ThenBy(i => i.Raw.Kind).First();
            var instanceIds = items.Select(i => i.Node.PhysicalId).Concat(items.Select(i => i.Raw.InstanceId))
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            devices.Add(new PhysicalInputDevice(
                g.Key, first.Node.Vid, first.Node.Pid, first.Node.Bus, items.Select(i => i.Node.PhysicalId).FirstOrDefault(id => id is not null),
                items.Select(i => i.Node.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim(),
                first.Node.ContainerId,
                items.Where(i => i.Raw.Kind == RawKind.Mouse).Select(i => i.Raw.Handle).ToList(),
                items.Where(i => i.Raw.Kind == RawKind.Keyboard).Select(i => i.Raw.Handle).ToList(),
                instanceIds,
                primary.Raw.Kind == RawKind.Mouse ? DeviceKind.Mouse : DeviceKind.Keyboard));
        }
        return devices;
    }

    /// <summary>
    /// Mice and keyboards. A device with both kinds of collections (a LIGHTSPEED receiver also exposes a keyboard for
    /// macros; some keyboards expose a mouse) belongs to the card of its primary collection only — unless the other list
    /// would be empty without it.
    /// </summary>
    public static InputInventory Classify(IReadOnlyList<PhysicalInputDevice> devices)
    {
        var mice = devices.Where(d => d.HasMouse && (d.PrimaryKind == DeviceKind.Mouse || !d.HasKeyboard)).ToList();
        if (mice.Count == 0) mice = devices.Where(d => d.HasMouse).ToList();
        var keyboards = devices.Where(d => d.HasKeyboard && (d.PrimaryKind == DeviceKind.Keyboard || !d.HasMouse)).ToList();
        if (keyboards.Count == 0) keyboards = devices.Where(d => d.HasKeyboard).ToList();
        return new InputInventory(mice, keyboards);
    }

    // ───────────────────────────── Native ─────────────────────────────

    /// <summary>Every mouse and keyboard of this PC (read-only). Empty on any failure.</summary>
    public static InputInventory Detect()
    {
        try
        {
            var cache = new Dictionary<string, CollectionNode?>(StringComparer.OrdinalIgnoreCase);
            return Classify(Group(EnumerateRaw(), id => cache.TryGetValue(id, out var n) ? n : cache[id] = Resolve(id)));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException or InvalidOperationException)
        {
            Log.Warn($"Мышь и клавиатура: список устройств не прочитан — {ex.Message}");
            return InputInventory.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, [Out] char[]? pData, ref uint pcbSize);

    /// <summary>Mouse and keyboard collections with their HID instance ids (GetRawInputDeviceList; no registration).</summary>
    internal static IReadOnlyList<RawCollection> EnumerateRaw()
    {
        var size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        RAWINPUTDEVICELIST[] list = [];
        uint count = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            count = 0;
            if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue) return [];
            if (count == 0) return [];
            list = new RAWINPUTDEVICELIST[count + 4]; // devices may arrive in between
            var n = (uint)list.Length;
            var got = GetRawInputDeviceList(list, ref n, size);
            if (got == uint.MaxValue) continue;
            count = got;
            break;
        }

        var result = new List<RawCollection>();
        for (var i = 0; i < count && i < list.Length; i++)
        {
            var kind = list[i].dwType switch
            {
                RIM_TYPEMOUSE => RawKind.Mouse,
                RIM_TYPEKEYBOARD => RawKind.Keyboard,
                _ => RawKind.Hid,
            };
            if (kind == RawKind.Hid || list[i].hDevice == IntPtr.Zero) continue;
            if (InterfacePathToInstanceId(DeviceName(list[i].hDevice)) is { } id) result.Add(new RawCollection(list[i].hDevice, kind, id));
        }
        return result;
    }

    /// <summary>The device interface path of a raw input handle (RIDI_DEVICENAME), or null.</summary>
    internal static string? DeviceName(IntPtr handle)
    {
        uint chars = 0;
        if (GetRawInputDeviceInfo(handle, RIDI_DEVICENAME, null, ref chars) != 0 || chars is 0 or > 4096) return null;
        var buffer = new char[chars];
        var got = GetRawInputDeviceInfo(handle, RIDI_DEVICENAME, buffer, ref chars);
        if (got == uint.MaxValue || got == 0) return null;
        return new string(buffer, 0, (int)Math.Min(got, (uint)buffer.Length)).TrimEnd('\0');
    }

    /// <summary>The PnP side of a HID collection: physical bus device, ids, container, name.</summary>
    private static CollectionNode? Resolve(string instanceId)
    {
        var node = PnpTree.Get(instanceId);
        if (node is null) return null;
        var ancestors = PnpTree.Ancestors(instanceId);
        var chain = ancestors.Prepend(instanceId).ToList();
        var isVirtual = PnpTree.IsVirtualChain(chain);
        var usb = chain.FirstOrDefault(IsUsbDeviceId);
        var bt = usb is null ? chain.FirstOrDefault(IsBluetoothId) : null;
        var physicalId = usb ?? bt;
        var physical = physicalId is null ? null : PnpTree.Get(physicalId);
        var ids = ParseVidPid(usb) ?? ParseVidPid(instanceId) ?? ParseVidPid(bt);
        var name = physical?.BusReportedName ?? node.BusReportedName ?? (bt is not null ? physical?.FriendlyName : null);
        return new CollectionNode(isVirtual, physicalId, usb is not null ? "USB" : bt is not null ? "Bluetooth" : null, ids?.Vid, ids?.Pid,
            physical?.ContainerId ?? node.ContainerId, physical?.InLocalMachineContainer ?? node.InLocalMachineContainer, name);
    }
}
