using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Monolith.Core;
using Monolith.Modules.Devices.Audio;

namespace Monolith.Modules.Games;

/// <summary>
/// PROCESS_POWER_THROTTLING_STATE (processthreadsapi.h): Version, ControlMask, StateMask. ControlMask names the mechanisms
/// the caller takes control of, StateMask switches them on or off; both 0 = the system picks the QoS itself (heuristics).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public record struct PowerThrottlingState(uint Version, uint ControlMask, uint StateMask);

/// <summary>One process a session put into efficiency mode: enough to recognise it again after a crash.</summary>
public sealed record EcoQosMarkerEntry(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("startTimeUtc")] DateTime StartTimeUtc);

/// <summary>
/// EcoQoS («Режим эффективности» of Task Manager without the priority part) for the apps of the Game Boost close list —
/// docs/research/gaming_tweaks.md §2 H `boost-ecoqos-close-list`. Microsoft (quality-of-service): Eco «always selects most
/// efficient CPU frequency and schedules to efficient cores»; SetProcessInformation: a process that enables
/// PROCESS_POWER_THROTTLING_EXECUTION_SPEED «will be classified as EcoQoS» (Windows 11; before it the level did not exist).
/// Rules: the original state is read first and written back exactly at session end (a process that already controls
/// EXECUTION_SPEED itself — Edge, Chrome — is never touched); handles carry PROCESS_SET_INFORMATION | QUERY_LIMITED only;
/// priority is never changed; a crash marker in the admin-only machine folder lets the next start reset leftovers.
/// Pure helpers (exclusions, marker JSON, restore plan) are unit-tested; the P/Invoke wrappers return null/false on failure.
/// </summary>
public static class EcoQos
{
    /// <summary>PROCESS_INFORMATION_CLASS.ProcessPowerThrottling.</summary>
    private const int ProcessPowerThrottling = 4;
    /// <summary>PROCESS_POWER_THROTTLING_CURRENT_VERSION.</summary>
    public const uint CurrentVersion = 1;
    /// <summary>PROCESS_POWER_THROTTLING_EXECUTION_SPEED: the EcoQoS / HighQoS switch.</summary>
    public const uint ExecutionSpeed = 0x1;

    /// <summary>EcoQoS on: ControlMask and StateMask both carry EXECUTION_SPEED (the Learn sample).</summary>
    public static readonly PowerThrottlingState Eco = new(CurrentVersion, ExecutionSpeed, ExecutionSpeed);
    /// <summary>System-managed (the default of a process that never called the API): both masks 0.</summary>
    public static readonly PowerThrottlingState SystemManaged = new(CurrentVersion, 0, 0);

    /// <summary>Crash marker: written when the first process of a session is tagged, deleted when the session ends.</summary>
    public static string MarkerFile => Path.Combine(AppPaths.MachineDir, "ecoqos-session.json");

    private static readonly JsonSerializerOptions MarkerJson = new() { WriteIndented = true };

    // ───────────────────────────── Pure rules ─────────────────────────────

    /// <summary>
    /// True when the process already manages EXECUTION_SPEED itself (ControlMask has the bit). Such a process is skipped: its
    /// own setting is the exact state it wants, and MONOLITH would not know when it changes it again.
    /// </summary>
    public static bool IsSelfManaged(PowerThrottlingState state) => (state.ControlMask & ExecutionSpeed) != 0;

    /// <summary>True when the state means EcoQoS (EXECUTION_SPEED controlled and on).</summary>
    public static bool IsEco(PowerThrottlingState state) =>
        (state.ControlMask & ExecutionSpeed) != 0 && (state.StateMask & ExecutionSpeed) != 0;

    /// <summary>
    /// The state to write back at session end: the original struct as read before the change (never 0/0 — a process that
    /// had HighQoS set explicitly gets exactly that back). Only the version is normalised to the current one.
    /// </summary>
    public static PowerThrottlingState PlanRestore(PowerThrottlingState original) => original with { Version = CurrentVersion };

    /// <summary>
    /// The state to write for the session: the original with EXECUTION_SPEED added to both masks. SetProcessInformation
    /// replaces the whole state, so a bit the process set itself (IGNORE_TIMER_RESOLUTION) is carried over, not dropped.
    /// Only for states that are not <see cref="IsSelfManaged"/>.
    /// </summary>
    public static PowerThrottlingState PlanApply(PowerThrottlingState original) =>
        new(CurrentVersion, original.ControlMask | ExecutionSpeed, original.StateMask | ExecutionSpeed);

    /// <summary>
    /// After a crash the original is lost: the state to write is the current one with EXECUTION_SPEED taken out of both masks
    /// (system-managed for the speed, like a process that never called the API), every other mechanism left as it is now.
    /// </summary>
    public static PowerThrottlingState PlanRecover(PowerThrottlingState now) =>
        new(CurrentVersion, now.ControlMask & ~ExecutionSpeed, now.StateMask & ~ExecutionSpeed);

    /// <summary>
    /// Why a close-list process must not be put into efficiency mode, or null when it may. Besides the protected names
    /// (never offered at all) this refuses voice, streaming, audio, overlay and peripheral software, anti-cheat helpers,
    /// MONOLITH itself and every process with an active audio session (Microsoft marks audible processes as HighQoS).
    /// </summary>
    public static string? ExclusionReason(string processName, int pid, int selfPid, IReadOnlySet<int> activeAudioPids)
    {
        var name = UserProcesses.NormalizeName(processName);
        if (name.Length == 0) return Loc.T("имя процесса не прочитано");
        if (pid == selfPid || name.StartsWith("monolith", StringComparison.OrdinalIgnoreCase)) return Loc.T("это сам MONOLITH");
        if (UserProcesses.IsProtectedName(name)) return Loc.T("системный процесс, драйвер, античит или лаунчер");
        if (GameProcessFinder.IsHelper(name)) return Loc.T("вспомогательный процесс игры или античита");
        if (UserProcesses.IsNeverThrottled(name)) return Loc.T("звук, голос, стрим, оверлей или периферия — не ограничивается");
        if (activeAudioPids.Contains(pid)) return Loc.T("сейчас воспроизводит звук");
        return null;
    }

    /// <summary>Marker JSON for the processes a session tagged.</summary>
    public static string SerializeMarker(IEnumerable<EcoQosMarkerEntry> entries) =>
        JsonSerializer.Serialize(entries.ToList(), MarkerJson);

    /// <summary>Parses a marker file. Untrusted input: anything but a list of pid + start time is an empty list.</summary>
    public static IReadOnlyList<EcoQosMarkerEntry> ParseMarker(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<EcoQosMarkerEntry>>(json, MarkerJson) ?? [];
            return list.Where(e => e.Pid > 4 && e.StartTimeUtc != default).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// A marker entry names a running process only when both the PID and the start time match: a PID is reused by Windows
    /// after the process exits, the creation time is not.
    /// </summary>
    public static bool Matches(EcoQosMarkerEntry entry, int pid, DateTime startTimeUtc) =>
        entry.Pid == pid && entry.StartTimeUtc.ToUniversalTime() == startTimeUtc.ToUniversalTime();

    // ───────────────────────────── Marker file (admin-only machine folder) ─────────────────────────────

    /// <summary>Writes the marker; false (logged) when the admin-only folder is not writable (never elsewhere).</summary>
    public static bool WriteMarker(IEnumerable<EcoQosMarkerEntry> entries)
    {
        try
        {
            if (!Elevation.IsElevated || !AppPaths.TryEnsureDir(AppPaths.MachineDir)) return false;
            var tmp = MarkerFile + ".tmp";
            File.WriteAllText(tmp, SerializeMarker(entries));
            File.Move(tmp, MarkerFile, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Режим эффективности: не удалось записать отметку сеанса — {0}", ex.Message);
            return false;
        }
    }

    public static void DeleteMarker()
    {
        try
        {
            if (File.Exists(MarkerFile)) File.Delete(MarkerFile);
        }
        catch (Exception ex)
        {
            Log.Warn("Режим эффективности: не удалось удалить отметку сеанса — {0}", ex.Message);
        }
    }

    /// <summary>The entries of an existing marker (empty when there is none or it is unreadable).</summary>
    public static IReadOnlyList<EcoQosMarkerEntry> ReadMarker()
    {
        try
        {
            return File.Exists(MarkerFile) ? ParseMarker(File.ReadAllText(MarkerFile)) : [];
        }
        catch
        {
            return [];
        }
    }

    // ───────────────────────────── Process state (P/Invoke) ─────────────────────────────

    /// <summary>The process's current throttling state; null when Windows cannot report it (then it is never tagged).</summary>
    public static PowerThrottlingState? ReadState(SafeProcessHandle process)
    {
        try
        {
            var state = new PowerThrottlingState(CurrentVersion, 0, 0);
            return GetProcessInformation(process, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>())
                ? state
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool WriteState(SafeProcessHandle process, PowerThrottlingState state)
    {
        try
        {
            var s = state;
            return SetProcessInformation(process, ProcessPowerThrottling, ref s, (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Creation time (UTC) from GetProcessTimes; null when unreadable.</summary>
    public static DateTime? StartTimeUtc(SafeProcessHandle process)
    {
        try
        {
            if (!GetProcessTimes(process, out var creation, out _, out _, out _)) return null;
            var ticks = ((long)creation.dwHighDateTime << 32) | creation.dwLowDateTime;
            return ticks <= 0 ? null : DateTime.FromFileTimeUtc(ticks);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True while the process has not exited (GetProcessTimes reports no exit time).</summary>
    public static bool IsRunning(SafeProcessHandle process)
    {
        try
        {
            if (!GetProcessTimes(process, out _, out var exit, out _, out _)) return false;
            return exit.dwHighDateTime == 0 && exit.dwLowDateTime == 0;
        }
        catch
        {
            return false;
        }
    }

    // ───────────────────────────── Active audio sessions ─────────────────────────────

    /// <summary>
    /// PIDs with an active audio session on any active render endpoint (IAudioSessionManager2 → IAudioSessionEnumerator →
    /// IAudioSessionControl2.GetProcessId, state AudioSessionStateActive). Read-only; empty on any failure. Blocking (COM).
    /// </summary>
    public static IReadOnlySet<int> ActiveAudioPids()
    {
        var pids = new HashSet<int>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCoClass();
            if (enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out var devices) != 0 || devices is null) return pids;
            if (devices.GetCount(out var count) != 0) return pids;
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    if (devices.Item(i, out var device) != 0 || device is null) continue;
                    var iid = typeof(IAudioSessionManager2).GUID;
                    if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var managerObj) != 0 || managerObj is not IAudioSessionManager2 manager) continue;
                    if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions is null) continue;
                    if (sessions.GetCount(out var n) != 0) continue;
                    for (var j = 0; j < n; j++)
                    {
                        if (sessions.GetSession(j, out var session) != 0 || session is not IAudioSessionControl2 s2) continue;
                        if (s2.GetState(out var state) != 0 || state != AudioSessionStateActive) continue;
                        if (s2.GetProcessId(out var pid) == 0 && pid > 0) pids.Add((int)pid);
                    }
                }
                catch
                {
                    // one endpoint failed: the others still count
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Режим эффективности: список звуковых сессий не прочитан — {0}", ex.Message);
        }
        return pids;
    }

    private const uint DeviceStateActive = 0x1;
    private const uint ClsctxAll = 0x17;
    private const int AudioSessionStateActive = 1;

    #region P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(SafeProcessHandle process, int infoClass, ref PowerThrottlingState state, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(SafeProcessHandle process, int infoClass, ref PowerThrottlingState state, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);
    #endregion
}

// Core Audio session API (audiopolicy.h) — read-only enumeration of sessions and their owning PIDs. Same conventions as
// Modules/Devices/Audio/CoreAudioInterop.cs: [PreserveSig] int HRESULTs, vtable order of the SDK header.
[ComImport, Guid("BFA971F1-4D5E-40BB-935E-967039BFBEE4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, [MarshalAs(UnmanagedType.IUnknown)] out object audioVolume);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2 : IAudioSessionManager
{
    [PreserveSig] new int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
    [PreserveSig] new int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, [MarshalAs(UnmanagedType.IUnknown)] out object audioVolume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    /// <summary>Declared for the vtable; MONOLITH registers no notifications.</summary>
    [PreserveSig] int RegisterSessionNotification(IntPtr notification);
    [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string? sessionId, IntPtr notification);
    [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    /// <summary>Setters declared for the vtable only; MONOLITH never calls them.</summary>
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid grouping);
    [PreserveSig] int SetGroupingParam(ref Guid grouping, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2 : IAudioSessionControl
{
    [PreserveSig] new int GetState(out int state);
    [PreserveSig] new int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eventContext);
    [PreserveSig] new int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr eventContext);
    [PreserveSig] new int GetGroupingParam(out Guid grouping);
    [PreserveSig] new int SetGroupingParam(ref Guid grouping, IntPtr eventContext);
    [PreserveSig] new int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] new int UnregisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}
