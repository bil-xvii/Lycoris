using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Monolith.Core;

namespace Monolith.Modules.Tools.DpcLatency;

/// <summary>
/// Read-only list of loaded kernel modules (name, base, size) for attributing DPC/ISR routine addresses.
/// Primary source: NtQuerySystemInformation(SystemModuleInformation = 11) — names, bases and sizes in one call.
/// Fallback: EnumDeviceDrivers + GetDeviceDriverBaseNameW (bases and names, no sizes).
/// Since Windows 11 24H2 both return zeroed addresses unless SeDebugPrivilege is enabled in the caller's token
/// (Microsoft Learn, EnumDeviceDrivers remarks; windows-internals.com «KASLR leaks restriction»), so the privilege is
/// enabled on MONOLITH's own token just for the module query (only available to an elevated process) and disabled again
/// right after it unless it was already enabled. Nothing here changes the system.
/// </summary>
public static class KernelModules
{
    public sealed record LoadResult(IReadOnlyList<KernelModule> Modules, bool DebugPrivilege, string? Note);

    public static LoadResult Load()
    {
        var (debug, why, wasEnabled) = EnableDebugPrivilege();
        if (!debug) Log.Warn("DPC/ISR: привилегия SeDebugPrivilege не включена — " + why);

        List<KernelModule> modules;
        string? note = null;
        try
        {
            try
            {
                modules = FromSystemModuleInformation();
            }
            catch (Exception ex)
            {
                Log.Warn("DPC/ISR: SystemModuleInformation недоступна — " + ex.Message);
                modules = [];
            }
            if (modules.Count == 0 || modules.All(m => m.Base == 0))
            {
                try
                {
                    modules = FromEnumDeviceDrivers();
                    if (modules.Count > 0) note = "Размеры модулей неизвестны — адреса сопоставляются по ближайшему модулю.";
                }
                catch (Exception ex)
                {
                    Log.Warn("DPC/ISR: EnumDeviceDrivers недоступна — " + ex.Message);
                    modules = [];
                }
            }
        }
        finally
        {
            // Least privilege: the token goes back to its previous state right after the module list is read, so later
            // OpenProcess calls and child processes (dism, sfc, PowerShell …) never inherit an enabled SeDebugPrivilege.
            if (debug && !wasEnabled)
            {
                var (restored, restoreWhy) = DisableDebugPrivilege();
                if (!restored) Log.Warn("DPC/ISR: не удалось снова отключить SeDebugPrivilege — " + restoreWhy);
            }
        }
        modules = modules.Where(m => m.Base != 0).ToList();
        if (modules.Count == 0)
            note = debug
                ? "Список модулей ядра пуст — драйверы будут показаны как «неизвестный модуль»."
                : "Адреса модулей ядра скрыты (нужна привилегия SeDebugPrivilege) — драйверы будут показаны как «неизвестный модуль».";
        return new LoadResult(modules, debug, note);
    }

    // ───────────────────────────── SeDebugPrivilege ─────────────────────────────

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;

    /// <summary>
    /// Enables SeDebugPrivilege on the current process token (held by administrators, disabled by default).
    /// <c>WasEnabled</c> tells whether it was already enabled before (then the caller leaves it as it was).
    /// </summary>
    public static (bool Ok, string Why, bool WasEnabled) EnableDebugPrivilege()
    {
        try
        {
            var (ok, why, previous) = SetDebugPrivilege(SE_PRIVILEGE_ENABLED);
            if (!ok) return (false, why, false);
            // PreviousState lists only the privileges the call changed: none → it was enabled already.
            var wasEnabled = previous.PrivilegeCount == 0 || (previous.Attributes & SE_PRIVILEGE_ENABLED) != 0;
            return (true, "ok", wasEnabled);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, false);
        }
    }

    /// <summary>Disables SeDebugPrivilege again (the token keeps the privilege, only its «enabled» bit is cleared).</summary>
    public static (bool Ok, string Why) DisableDebugPrivilege()
    {
        try
        {
            var (ok, why, _) = SetDebugPrivilege(0);
            return (ok, why);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool Ok, string Why, TOKEN_PRIVILEGES Previous) SetDebugPrivilege(uint attributes)
    {
        var previous = default(TOKEN_PRIVILEGES);
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return (false, $"нет доступа к токену процесса (код {Marshal.GetLastWin32Error()})", previous);
        using (token)
        {
            if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out var luid))
                return (false, $"привилегия не найдена (код {Marshal.GetLastWin32Error()})", previous);
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = attributes };
            var size = Marshal.SizeOf<TOKEN_PRIVILEGES>();
            if (!AdjustTokenPrivileges(token, false, ref tp, size, out previous, out _))
                return (false, $"AdjustTokenPrivileges: код {Marshal.GetLastWin32Error()}", previous);
            if (Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
                return (false, "у процесса нет привилегии SeDebugPrivilege (нужны права администратора)", previous);
        }
        return (true, "ok", previous);
    }

    // ───────────────────────────── SystemModuleInformation ─────────────────────────────

    private const int SystemModuleInformation = 11;
    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    private const uint STATUS_BUFFER_TOO_SMALL = 0xC0000023;
    /// <summary>sizeof(RTL_PROCESS_MODULE_INFORMATION) on x64: 3 pointers, 2 ULONG, 4 USHORT, UCHAR[256].</summary>
    internal const int ModuleInfoSize = 296;
    internal const int ModulesHeaderSize = 8; // ULONG NumberOfModules + padding to the 8-byte aligned array

    private static List<KernelModule> FromSystemModuleInformation()
    {
        var size = 1 << 16;
        for (var attempt = 0; attempt < 6; attempt++, size *= 2)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemModuleInformation, buffer, size, out var needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH || status == STATUS_BUFFER_TOO_SMALL)
                {
                    if (needed > size) size = needed / 2; // the loop doubles it
                    continue;
                }
                if (status != 0) throw new InvalidOperationException($"NTSTATUS 0x{status:X8}");
                return ParseModules(buffer, size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new InvalidOperationException("буфер списка модулей слишком мал");
    }

    private static unsafe List<KernelModule> ParseModules(IntPtr buffer, int size)
    {
        var count = (int)Marshal.ReadInt32(buffer);
        var list = new List<KernelModule>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = buffer + ModulesHeaderSize + i * ModuleInfoSize;
            if ((long)(entry - buffer) + ModuleInfoSize > size) break;
            var imageBase = (ulong)Marshal.ReadInt64(entry, 16);
            var imageSize = (uint)Marshal.ReadInt32(entry, 24);
            var offsetToFileName = (ushort)Marshal.ReadInt16(entry, 38);
            var namePtr = (byte*)(entry + 40);
            var fullLength = 0;
            while (fullLength < 256 && namePtr[fullLength] != 0) fullLength++;
            var start = Math.Min(offsetToFileName, fullLength);
            var name = Encoding.Default.GetString(namePtr + start, fullLength - start);
            if (name.Length == 0)
            {
                var full = Encoding.Default.GetString(namePtr, fullLength);
                name = full[(full.LastIndexOf('\\') + 1)..];
            }
            list.Add(new KernelModule(name, imageBase, imageSize));
        }
        return list;
    }

    // ───────────────────────────── EnumDeviceDrivers ─────────────────────────────

    private static List<KernelModule> FromEnumDeviceDrivers()
    {
        if (!EnumDeviceDrivers(null, 0, out var needed) || needed == 0) return [];
        var bases = new IntPtr[needed / IntPtr.Size + 16];
        if (!EnumDeviceDrivers(bases, (uint)(bases.Length * IntPtr.Size), out needed))
            throw new InvalidOperationException($"код {Marshal.GetLastWin32Error()}");
        var count = (int)(needed / IntPtr.Size);
        var list = new List<KernelModule>(count);
        var name = new StringBuilder(260);
        for (var i = 0; i < count && i < bases.Length; i++)
        {
            if (bases[i] == IntPtr.Zero) continue;
            name.Clear();
            var len = GetDeviceDriverBaseNameW(bases[i], name, (uint)name.Capacity);
            if (len == 0) continue;
            list.Add(new KernelModule(name.ToString(0, (int)len), (ulong)bases[i].ToInt64(), 0));
        }
        return list;
    }

    // ───────────────────────────── P/Invoke ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        int bufferLength, out TOKEN_PRIVILEGES previousState, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumDeviceDrivers([Out] IntPtr[]? imageBases, uint size, out uint needed);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetDeviceDriverBaseNameW(IntPtr imageBase, StringBuilder baseName, uint size);
}
