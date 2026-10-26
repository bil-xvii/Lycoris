using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Monolith.Modules.Games;

/// <summary>Win32 / NT calls used by the Games module. All wrappers catch failures and return null/false.</summary>
internal static class GamesNative
{
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public const uint NORMAL_PRIORITY_CLASS = 0x20;
    public const uint IDLE_PRIORITY_CLASS = 0x40;
    public const uint HIGH_PRIORITY_CLASS = 0x80;
    public const uint REALTIME_PRIORITY_CLASS = 0x100;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
    public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x8000;

    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const int TokenUser = 1;

    // ───────────────────────────── Processes ─────────────────────────────

    public static SafeProcessHandle Open(int pid, uint access) => OpenProcess(access, false, pid);

    /// <summary>Full image path through QueryFullProcessImageNameW (works for other users' processes too, needs only QUERY_LIMITED).</summary>
    public static string? GetImagePath(int pid)
    {
        try
        {
            using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h.IsInvalid) return null;
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>SID (S-1-5-21-…) of the account that owns the process; null when it cannot be read (protected or foreign process).</summary>
    public static string? GetOwnerSid(int pid)
    {
        try
        {
            using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h.IsInvalid) return null;
            if (!OpenProcessToken(h.DangerousGetHandle(), TOKEN_QUERY, out var token)) return null;
            using (token)
            {
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var len);
                if (len <= 0) return null;
                var buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (!GetTokenInformation(token, TokenUser, buf, len, out _)) return null;
                    var sid = Marshal.ReadIntPtr(buf); // TOKEN_USER.User.Sid
                    return new SecurityIdentifier(sid).Value;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when Windows marks the process as critical (killing it bug-checks the system). Unknown → true (treat as critical).</summary>
    public static bool IsCritical(int pid)
    {
        try
        {
            using var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h.IsInvalid) return true;
            return !IsProcessCritical(h, out var critical) || critical;
        }
        catch (EntryPointNotFoundException)
        {
            return false; // pre-8.1 only; module targets 10+
        }
        catch
        {
            return true;
        }
    }

    public static string PriorityName(uint cls) => cls switch
    {
        IDLE_PRIORITY_CLASS => "Низкий",
        BELOW_NORMAL_PRIORITY_CLASS => "Ниже среднего",
        NORMAL_PRIORITY_CLASS => "Обычный",
        ABOVE_NORMAL_PRIORITY_CLASS => "Выше среднего",
        HIGH_PRIORITY_CLASS => "Высокий",
        REALTIME_PRIORITY_CLASS => "Реального времени",
        _ => "Неизвестно",
    };

    // ───────────────────────────── Power ─────────────────────────────

    /// <summary>Laptop running on battery right now (ACLineStatus = 0 and a system battery exists).</summary>
    public static bool IsOnBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return false;
            if ((s.BatteryFlag & 128) != 0) return false; // no system battery
            return s.ACLineStatus == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Modern Standby (S0 low power idle): SYSTEM_POWER_CAPABILITIES.AoAc (byte offset 20).</summary>
    public static bool IsModernStandby()
    {
        try
        {
            var caps = new byte[128]; // SYSTEM_POWER_CAPABILITIES is 76 bytes
            return GetPwrCapabilities(caps) && caps[20] != 0;
        }
        catch
        {
            return false;
        }
    }

    // ───────────────────────────── Memory lists ─────────────────────────────

    /// <summary>
    /// Purges the standby list: SeProfileSingleProcessPrivilege + NtSetSystemInformation(SystemMemoryListInformation=80, MemoryPurgeStandbyList=4).
    /// Returns the NTSTATUS (0 = success) or a Win32 error description.
    /// </summary>
    public static (bool Ok, string Message) PurgeStandbyList()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
                return (false, $"нет доступа к токену процесса (код {Marshal.GetLastWin32Error()})");
            using (token)
            {
                if (!LookupPrivilegeValueW(null, "SeProfileSingleProcessPrivilege", out var luid))
                    return (false, $"привилегия не найдена (код {Marshal.GetLastWin32Error()})");
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
                if (!AdjustTokenPrivileges(token, false, ref tp, Marshal.SizeOf<TOKEN_PRIVILEGES>(), IntPtr.Zero, IntPtr.Zero))
                    return (false, $"не удалось включить привилегию (код {Marshal.GetLastWin32Error()})");
                if (Marshal.GetLastWin32Error() == 1300) // ERROR_NOT_ALL_ASSIGNED
                    return (false, "у процесса нет привилегии SeProfileSingleProcessPrivilege (нужны права администратора)");
            }

            var cmd = 4; // MemoryPurgeStandbyList
            var status = NtSetSystemInformation(80, ref cmd, sizeof(int));
            return status == 0 ? (true, "ok") : (false, $"NTSTATUS 0x{status:X8}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ───────────────────────────── HAGS (D3DKMT) ─────────────────────────────

    public enum HagsState { Unknown, NotSupported, Off, On }

    /// <summary>
    /// Hardware-accelerated GPU scheduling from D3DKMTQueryAdapterInfo(KMTQAITYPE_WDDM_2_7_CAPS = 70):
    /// bit 0 HwSchSupported, bit 1 HwSchEnabled (docs/research/tech.md §7). The registry alone is misleading.
    /// </summary>
    public static HagsState QueryHags()
    {
        try
        {
            var e = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return HagsState.Unknown;
            var size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            var buffer = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
            try
            {
                e.pAdapters = buffer;
                if (D3DKMTEnumAdapters2(ref e) != 0) return HagsState.Unknown;
                bool anySupported = false, anyEnabled = false, anyAnswered = false;
                for (var i = 0; i < e.NumAdapters; i++)
                {
                    var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buffer + i * size);
                    var caps = 0u;
                    var pCaps = Marshal.AllocHGlobal(sizeof(uint));
                    try
                    {
                        Marshal.WriteInt32(pCaps, 0);
                        var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = info.hAdapter, Type = 70, pPrivateDriverData = pCaps, PrivateDriverDataSize = sizeof(uint) };
                        if (D3DKMTQueryAdapterInfo(ref q) == 0)
                        {
                            anyAnswered = true;
                            caps = (uint)Marshal.ReadInt32(pCaps);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pCaps);
                        var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                        D3DKMTCloseAdapter(ref close);
                    }
                    if ((caps & 1) != 0) anySupported = true;
                    if ((caps & 2) != 0) anyEnabled = true;
                }
                if (!anyAnswered) return HagsState.Unknown;
                return anyEnabled ? HagsState.On : anySupported ? HagsState.Off : HagsState.NotSupported;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return HagsState.Unknown;
        }
    }

    #region P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public LUID AdapterLuid;
        public uint NumOfSources;
        public int bPrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetPriorityClass(SafeProcessHandle process, uint priorityClass);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities([Out] byte[] capabilities);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 enumAdapters);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO queryAdapterInfo);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER closeAdapter);
    #endregion
}
