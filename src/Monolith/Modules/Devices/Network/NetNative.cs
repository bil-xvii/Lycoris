using System.Net;
using System.Runtime.InteropServices;

namespace Monolith.Modules.Devices.Network;

/// <summary>iphlpapi and wlanapi, read-only except the per-connection statistics switch (transient, switched off again).</summary>
internal static class NetNative
{
    // ───────────────────────────── Routing ─────────────────────────────

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    /// <summary>The interface index the route to an IPv4 address uses; null when there is no route.</summary>
    public static int? BestInterfaceIndex(IPAddress destination)
    {
        try
        {
            var addr = BitConverter.ToUInt32(destination.GetAddressBytes(), 0);
            return GetBestInterface(addr, out var index) == 0 ? (int)index : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    // ───────────────────────────── TCP connections per process ─────────────────────────────

    private const int AfInet = 2, AfInet6 = 23;
    private const int TcpTableOwnerPidConnections = 4;
    private const int StateEstablished = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    /// <summary>An established TCP connection: owner process, remote end. Rows keep what EStats needs.</summary>
    public sealed record TcpConn(int Pid, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, bool V6, uint LocalScope, uint RemoteScope);

    public static IReadOnlyList<TcpConn> EstablishedConnections()
    {
        var list = new List<TcpConn>();
        Read(AfInet, list);
        Read(AfInet6, list);
        return list;
    }

    private static void Read(int af, List<TcpConn> into)
    {
        var size = 0;
        try
        {
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidConnections, 0);
            if (size <= 0) return;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    var rc = GetExtendedTcpTable(buf, ref size, false, af, TcpTableOwnerPidConnections, 0);
                    if (rc == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the table grew
                    if (rc != 0) return;
                    var n = Marshal.ReadInt32(buf);
                    var rowSize = af == AfInet ? 24 : 56;
                    for (var i = 0; i < n; i++)
                    {
                        var row = buf + 4 + i * rowSize;
                        if (af == AfInet)
                        {
                            if (Marshal.ReadInt32(row) != StateEstablished) continue;
                            into.Add(new TcpConn(Marshal.ReadInt32(row, 20),
                                new IPAddress((uint)Marshal.ReadInt32(row, 4)), Port(Marshal.ReadInt32(row, 8)),
                                new IPAddress((uint)Marshal.ReadInt32(row, 12)), Port(Marshal.ReadInt32(row, 16)), false, 0, 0));
                        }
                        else
                        {
                            if (Marshal.ReadInt32(row, 48) != StateEstablished) continue;
                            var local = new byte[16];
                            var remote = new byte[16];
                            Marshal.Copy(row, local, 0, 16);
                            Marshal.Copy(row + 24, remote, 0, 16);
                            into.Add(new TcpConn(Marshal.ReadInt32(row, 52),
                                new IPAddress(local), Port(Marshal.ReadInt32(row, 20)),
                                new IPAddress(remote), Port(Marshal.ReadInt32(row, 44)), true,
                                (uint)Marshal.ReadInt32(row, 16), (uint)Marshal.ReadInt32(row, 40)));
                        }
                    }
                    return;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
    private static int RawPort(int port) => ((port & 0xFF) << 8) | ((port >> 8) & 0xFF);

    // ───────────────────────────── Per-connection byte counters (admin only) ─────────────────────────────

    private const int TcpConnectionEstatsData = 1;
    private const uint RodSize = 96; // TCP_ESTATS_DATA_ROD_v0 on x64

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public int State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6Row
    {
        public int State;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcpConnectionEStats(ref MibTcpRow row, int type, IntPtr rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcpConnectionEStats(ref MibTcpRow row, int type, IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcp6ConnectionEStats(ref MibTcp6Row row, int type, IntPtr rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcp6ConnectionEStats(ref MibTcp6Row row, int type, IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);

    /// <summary>
    /// Switches the data counters of a connection on or off (TCP_ESTATS_DATA_RW_v0.EnableCollection). Needs admin rights;
    /// transient diagnostics state of one connection that disappears with it — always switched off again after sampling.
    /// </summary>
    public static bool SetCollection(TcpConn c, bool enable)
    {
        var rw = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(rw, enable ? (byte)1 : (byte)0);
            if (!c.V6)
            {
                var row = V4Row(c);
                return SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, rw, 0, 1, 0) == 0;
            }
            var row6 = V6Row(c);
            return SetPerTcp6ConnectionEStats(ref row6, TcpConnectionEstatsData, rw, 0, 1, 0) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(rw);
        }
    }

    /// <summary>Bytes in + out since the counters were switched on; null when they are not collecting.</summary>
    public static long? ReadBytes(TcpConn c)
    {
        var rw = Marshal.AllocHGlobal(1);
        var rod = Marshal.AllocHGlobal((int)RodSize);
        try
        {
            uint rc;
            if (!c.V6)
            {
                var row = V4Row(c);
                rc = GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, rw, 0, 1, IntPtr.Zero, 0, 0, rod, 0, RodSize);
            }
            else
            {
                var row6 = V6Row(c);
                rc = GetPerTcp6ConnectionEStats(ref row6, TcpConnectionEstatsData, rw, 0, 1, IntPtr.Zero, 0, 0, rod, 0, RodSize);
            }
            if (rc != 0 || Marshal.ReadByte(rw) == 0) return null; // not collecting: the numbers would be garbage
            return Marshal.ReadInt64(rod, 0) + Marshal.ReadInt64(rod, 16); // DataBytesOut + DataBytesIn
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(rw);
            Marshal.FreeHGlobal(rod);
        }
    }

    private static MibTcpRow V4Row(TcpConn c) => new()
    {
        State = StateEstablished,
        LocalAddr = BitConverter.ToUInt32(c.Local.GetAddressBytes(), 0), LocalPort = (uint)RawPort(c.LocalPort),
        RemoteAddr = BitConverter.ToUInt32(c.Remote.GetAddressBytes(), 0), RemotePort = (uint)RawPort(c.RemotePort),
    };

    private static MibTcp6Row V6Row(TcpConn c) => new()
    {
        State = StateEstablished,
        LocalAddr = c.Local.GetAddressBytes(), LocalScopeId = c.LocalScope, LocalPort = (uint)RawPort(c.LocalPort),
        RemoteAddr = c.Remote.GetAddressBytes(), RemoteScopeId = c.RemoteScope, RemotePort = (uint)RawPort(c.RemotePort),
    };

    // ───────────────────────────── Native Wi-Fi ─────────────────────────────

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode, IntPtr reserved, out uint size, out IntPtr data, IntPtr valueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    private const int OpRadioState = 4;
    private const int OpChannelNumber = 8;
    private const int OpRssi = 0x10000102;
    private const uint ErrorAccessDenied = 5;

    /// <summary>Wi-Fi interfaces: GUID → (state 1 = connected, radio off). Never a location-gated call.</summary>
    public static IReadOnlyDictionary<Guid, (int State, bool? RadioOff)> WlanInterfaces()
    {
        var result = new Dictionary<Guid, (int, bool?)>();
        if (!Open(out var h)) return result;
        try
        {
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0) return result;
            try
            {
                var n = Marshal.ReadInt32(list);
                for (var i = 0; i < n; i++)
                {
                    var item = list + 8 + i * 532;
                    var guid = Marshal.PtrToStructure<Guid>(item);
                    var state = Marshal.ReadInt32(item, 16 + 512);
                    bool? off = null;
                    if (WlanQueryInterface(h, ref guid, OpRadioState, IntPtr.Zero, out var size, out var data, IntPtr.Zero) == 0)
                    {
                        try
                        {
                            if (size >= 16 && Marshal.ReadInt32(data) > 0)
                                off = Marshal.ReadInt32(data, 8) == 2 || Marshal.ReadInt32(data, 12) == 2; // software / hardware radio state 2 = off
                        }
                        finally
                        {
                            WlanFreeMemory(data);
                        }
                    }
                    result[guid] = (state, off);
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            WlanCloseHandle(h, IntPtr.Zero);
        }
        return result;
    }

    /// <summary>RSSI (dBm) and channel of a connected Wi-Fi interface. Not the location-gated current_connection call.</summary>
    public static WifiLink? WifiLink(Guid iface)
    {
        if (!Open(out var h)) return null;
        try
        {
            int? rssi = null, channel = null;
            string? error = null;
            var rc = WlanQueryInterface(h, ref iface, OpRssi, IntPtr.Zero, out var size, out var data, IntPtr.Zero);
            if (rc == 0)
            {
                if (size >= 4) rssi = Marshal.ReadInt32(data);
                WlanFreeMemory(data);
            }
            else if (rc == ErrorAccessDenied) error = "Windows не дала прочитать сигнал Wi-Fi без доступа к расположению";
            rc = WlanQueryInterface(h, ref iface, OpChannelNumber, IntPtr.Zero, out size, out data, IntPtr.Zero);
            if (rc == 0)
            {
                if (size >= 4) channel = Marshal.ReadInt32(data);
                WlanFreeMemory(data);
            }
            return new WifiLink(rssi is < 0 and > -120 ? rssi : null, channel is > 0 and < 256 ? channel : null, error);
        }
        finally
        {
            WlanCloseHandle(h, IntPtr.Zero);
        }
    }

    private static bool Open(out IntPtr handle)
    {
        handle = IntPtr.Zero;
        try
        {
            return WlanOpenHandle(2, IntPtr.Zero, out _, out handle) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
