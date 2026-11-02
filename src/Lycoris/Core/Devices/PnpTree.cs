using System.Runtime.InteropServices;
using System.Text;

namespace Lycoris.Core.Devices;

/// <summary>One device node (read with CfgMgr32). Instance ids may hold serial numbers: log them through <see cref="PnpTree.Redact"/>.</summary>
public sealed record PnpNode(string InstanceId, string? ParentId, string? ContainerId, bool InLocalMachineContainer, string? BusReportedName,
    string? FriendlyName, string? Description, string? Class, string? Service, bool Present, DateTime? LastArrival, IReadOnlyList<string> HardwareIds);

/// <summary>
/// Read-only view of the Plug and Play device tree (CfgMgr32). Never sends I/O to a device and never changes anything;
/// every failure returns null or an empty list.
/// </summary>
public static class PnpTree
{
    private const uint CR_SUCCESS = 0;
    private const uint CR_BUFFER_SMALL = 0x1A;
    private const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    private const uint CM_LOCATE_DEVNODE_PHANTOM = 1;

    private const uint DEVPROP_TYPE_GUID = 0x0D;
    private const uint DEVPROP_TYPE_FILETIME = 0x10;
    private const uint DEVPROP_TYPE_BOOLEAN = 0x11;
    private const uint DEVPROP_TYPE_STRING = 0x12;
    private const uint DEVPROP_TYPE_STRING_LIST = 0x2012;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
        public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; }
    }

    private static readonly DEVPROPKEY BusReportedDeviceDesc = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
    private static readonly DEVPROPKEY IsPresentKey = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 5);
    private static readonly DEVPROPKEY ContainerIdKey = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
    private static readonly DEVPROPKEY InLocalMachineContainerKey = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 4);
    private static readonly DEVPROPKEY ParentKey = new("4340a6c5-93fa-4706-972c-7b648008a5a7", 8);
    private static readonly DEVPROPKEY DeviceDescKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    private static readonly DEVPROPKEY HardwareIdsKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 3);
    private static readonly DEVPROPKEY ServiceKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 6);
    private static readonly DEVPROPKEY ClassKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 9);
    private static readonly DEVPROPKEY FriendlyNameKey = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    private static readonly DEVPROPKEY LastArrivalDateKey = new("83da6326-97a6-4088-9453-a1923f573b29", 102);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey, out uint propertyType,
        [Out] byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    /// <summary>Device ids under one enumerator ("USB", "HID", "BTHENUM"…), with their properties.</summary>
    public static IReadOnlyList<PnpNode> Enumerate(string enumerator, bool presentOnly)
    {
        var result = new List<PnpNode>();
        try
        {
            foreach (var id in DeviceIds(enumerator, presentOnly))
                if (Get(id) is { } node) result.Add(node);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            Log.Warn("Список устройств {0} не прочитан: {1}", enumerator, ex.Message);
        }
        return result;
    }

    private static List<string> DeviceIds(string enumerator, bool presentOnly)
    {
        var flags = CM_GETIDLIST_FILTER_ENUMERATOR | (presentOnly ? CM_GETIDLIST_FILTER_PRESENT : 0);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var len, enumerator, flags) != CR_SUCCESS || len == 0) return [];
            var buffer = new char[len];
            var cr = CM_Get_Device_ID_ListW(enumerator, buffer, len, flags);
            if (cr == CR_BUFFER_SMALL) continue; // a device arrived meanwhile
            if (cr != CR_SUCCESS) return [];
            return SplitMulti(buffer);
        }
        return [];
    }

    /// <summary>The node of one instance id (present or not), or null.</summary>
    public static PnpNode? Get(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        try
        {
            if (CM_Locate_DevNodeW(out var dev, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS
                && CM_Locate_DevNodeW(out dev, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
                return null;
            var present = ReadBool(dev, IsPresentKey) ?? false;
            return new PnpNode(
                instanceId,
                ReadString(dev, ParentKey),
                ReadGuid(dev, ContainerIdKey)?.ToString("B"),
                ReadBool(dev, InLocalMachineContainerKey) ?? false,
                ReadString(dev, BusReportedDeviceDesc),
                ReadString(dev, FriendlyNameKey),
                ReadString(dev, DeviceDescKey),
                ReadString(dev, ClassKey),
                ReadString(dev, ServiceKey),
                present,
                ReadFileTime(dev, LastArrivalDateKey),
                ReadStringList(dev, HardwareIdsKey));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return null;
        }
    }

    /// <summary>Parent, grandparent… of a device (nearest first), at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<string> Ancestors(string instanceId, int max = 16)
    {
        var chain = new List<string>();
        var current = instanceId;
        for (var i = 0; i < max; i++)
        {
            var parent = Get(current)?.ParentId;
            if (string.IsNullOrEmpty(parent) || parent.StartsWith(@"HTREE\ROOT", StringComparison.OrdinalIgnoreCase)
                || chain.Contains(parent, StringComparer.OrdinalIgnoreCase)) break;
            chain.Add(parent);
            current = parent;
        }
        return chain;
    }

    /// <summary>
    /// A software device: neither it nor any ancestor sits on a physical bus (USB, Bluetooth, PCI, HD Audio) — e.g. the
    /// G HUB virtual mouse (LGHUBDevice\…).
    /// </summary>
    public static bool IsVirtual(string instanceId) => IsVirtualChain(Ancestors(instanceId).Prepend(instanceId));

    private static readonly string[] PhysicalBuses = [@"USB\", @"BTHENUM\", @"BTHLE\", @"BTHLEDevice\", @"PCI\", @"HDAUDIO\"];

    /// <summary>The pure rule of <see cref="IsVirtual"/> for a chain «device, parent, grandparent…».</summary>
    internal static bool IsVirtualChain(IEnumerable<string> chain) =>
        !chain.Any(id => PhysicalBuses.Any(bus => id.StartsWith(bus, StringComparison.OrdinalIgnoreCase)));

    /// <summary>An instance id without its last segment (serial numbers live there): «USB\VID_046D&amp;PID_C54D\…».</summary>
    public static string Redact(string instanceId)
    {
        var slash = instanceId.LastIndexOf('\\');
        return slash < 0 ? instanceId : instanceId[..(slash + 1)] + "…";
    }

    // ───────────────────────────── Property reads ─────────────────────────────

    private static byte[]? ReadRaw(uint dev, DEVPROPKEY key, uint expectedType)
    {
        var k = key;
        uint size = 0;
        var cr = CM_Get_DevNode_PropertyW(dev, ref k, out var type, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0 || size > 1 << 20) return null;
        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(dev, ref k, out type, buffer, ref size, 0) != CR_SUCCESS || type != expectedType) return null;
        return buffer;
    }

    private static string? ReadString(uint dev, DEVPROPKEY key)
    {
        var raw = ReadRaw(dev, key, DEVPROP_TYPE_STRING);
        if (raw is null) return null;
        var s = Encoding.Unicode.GetString(raw).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }

    private static IReadOnlyList<string> ReadStringList(uint dev, DEVPROPKEY key)
    {
        var raw = ReadRaw(dev, key, DEVPROP_TYPE_STRING_LIST);
        return raw is null ? [] : SplitMulti(Encoding.Unicode.GetString(raw).ToCharArray());
    }

    private static Guid? ReadGuid(uint dev, DEVPROPKEY key)
    {
        var raw = ReadRaw(dev, key, DEVPROP_TYPE_GUID);
        return raw is { Length: 16 } ? new Guid(raw) : null;
    }

    private static bool? ReadBool(uint dev, DEVPROPKEY key)
    {
        var raw = ReadRaw(dev, key, DEVPROP_TYPE_BOOLEAN);
        return raw is { Length: >= 1 } ? raw[0] != 0 : null;
    }

    private static DateTime? ReadFileTime(uint dev, DEVPROPKEY key)
    {
        var raw = ReadRaw(dev, key, DEVPROP_TYPE_FILETIME);
        if (raw is not { Length: 8 }) return null;
        var ft = BitConverter.ToInt64(raw, 0);
        try { return ft > 0 ? DateTime.FromFileTimeUtc(ft) : null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static List<string> SplitMulti(char[] buffer) =>
        new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
}
