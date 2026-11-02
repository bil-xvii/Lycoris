using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Monolith.Core;
using Monolith.Core.Devices;
using static Monolith.Modules.Devices.Displays.DisplayNative;

namespace Monolith.Modules.Devices.Displays;

/// <summary>The parsed command line of a «--display-guard» process.</summary>
public sealed record GuardArgs(Guid RecordId, DateTime DeadlineUtc, int ParentPid, DisplayGuardKind Kind, bool OriginalHdr, string? MonitorPath);

/// <summary>
/// The native side of display changes (core DisplaySafeApply owns the protocol: journal first, guard, temporary apply,
/// countdown, keep or revert). Modes come only from the driver's own list and are applied without CDS_UPDATEREGISTRY until
/// the user keeps them; position and orientation are never touched. HDR goes through the documented
/// DisplayConfigSetDeviceInfo (type 16 on 24H2+, else type 10). Never writes GraphicsDrivers keys, EDID overrides, TDR or
/// DPI registry values (DO-NOT-INCLUDE). Blocking calls: never on the UI thread.
/// </summary>
internal sealed class DisplayWriter : IDisplayWriter
{
    /// <summary>How long a temporary mode may take to show up in CCD.</summary>
    internal static TimeSpan ApplyWait = TimeSpan.FromSeconds(5);
    /// <summary>How long each revert step waits for the mode to come back.</summary>
    internal static TimeSpan RevertWait = TimeSpan.FromSeconds(3);
    /// <summary>The driver needs a moment before the new HDR state reads back.</summary>
    internal static TimeSpan HdrSettle = TimeSpan.FromMilliseconds(500);
    /// <summary>How long after the settle time the new HDR state may take to read back.</summary>
    internal static TimeSpan HdrConfirmWait = TimeSpan.FromSeconds(4);

    private static string What => Loc.T("изменение параметров экрана");

    /// <summary>The CCD configuration before the last temporary apply per monitor: the last-resort revert.</summary>
    private readonly ConcurrentDictionary<string, (PATH_INFO[] Paths, MODE_INFO[] Modes)> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    private static int Build => Environment.OSVersion.Version.Build;

    // ───────────────────────────── Reads ─────────────────────────────

    public DisplayModeSpec? ReadCurrentMode(string monitorPath) =>
        Safe(() => Ccd.Find(monitorPath) is { } p ? Ccd.CurrentMode(p) : null);

    public DisplayModeSpec? ReadPersistedMode(string monitorPath) => Safe(() =>
        Ccd.Find(monitorPath) is { GdiName: { } gdi } && Ccd.Settings(gdi, ENUM_REGISTRY_SETTINGS) is { dmPelsWidth: > 0 } dm
            ? Ccd.ToSpec(dm, null) : null);

    public bool IsModeOffered(string monitorPath, DisplayModeSpec mode) => Safe(() =>
    {
        if (Ccd.Find(monitorPath) is not { GdiName: { } gdi } p) return false;
        return Ccd.OfferedModes(gdi, Ccd.CurrentMode(p)).Any(m => m.Width == mode.Width && m.Height == mode.Height
                                                                 && (m.Hz == mode.Frequency || Math.Abs(m.Hz - mode.ExactHz) < 0.5));
    });

    public bool? ReadHdrEnabled(string monitorPath) => Safe(() =>
        Ccd.Find(monitorPath) is { } p && Ccd.Color(p.TargetAdapter, p.TargetId, Build) is { HdrSupported: true } c ? c.HdrEnabled : (bool?)null);

    public bool? ReadAcmEnabled(string monitorPath) => Safe(() =>
        Build >= 26100 && Ccd.Find(monitorPath) is { } p && Ccd.Color(p.TargetAdapter, p.TargetId, Build) is { WcgSupported: true } c
            ? c.WcgEnabled : (bool?)null);

    public string? GdiName(string monitorPath) => Safe(() => Ccd.Find(monitorPath)?.GdiName);

    public string? BlockReason()
    {
        try
        {
            if (GetSystemMetrics(SM_REMOTESESSION) != 0) return Loc.T("удалённый сеанс: Windows не даёт менять параметры экрана");
            if (SHQueryUserNotificationState(out var state) == 0 && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
                return Loc.T("закройте полноэкранную игру или презентацию");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return null;
    }

    // ───────────────────────────── Mode ─────────────────────────────

    private static DEVMODE ModeFor(DisplayModeSpec mode)
    {
        // Size, rate and depth only: never position or orientation (the layout and a portrait monitor stay as they are).
        var dm = NewDevMode();
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;
        dm.dmPelsWidth = (uint)mode.Width;
        dm.dmPelsHeight = (uint)mode.Height;
        dm.dmDisplayFrequency = (uint)mode.Frequency;
        dm.dmBitsPerPel = mode.BitsPerPel > 0 ? (uint)mode.BitsPerPel : 32;
        return dm;
    }

    private static DisplayWriteResult Code(int rc, string what) => rc switch
    {
        DISP_CHANGE_SUCCESSFUL => DisplayWriteResult.Success,
        DISP_CHANGE_RESTART => new(DisplayWriteStatus.NeedsRestart, Loc.T("этот режим требует перезагрузки — не применён")),
        -2 => new(DisplayWriteStatus.Failed, Loc.T("драйвер видеокарты не поддерживает этот режим")),
        _ => new(DisplayWriteStatus.Failed, Loc.Tf("{0}: Windows вернула код {1}", what, rc.ToString(CultureInfo.InvariantCulture))),
    };

    public DisplayWriteResult TestMode(string monitorPath, DisplayModeSpec mode)
    {
        if (Ccd.Find(monitorPath) is not { GdiName: { } gdi }) return new(DisplayWriteStatus.NotFound, Loc.T("монитор не найден"));
        var dm = ModeFor(mode);
        return Code(ChangeDisplaySettingsExW(gdi, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero), Loc.T("проверка режима"));
    }

    public DisplayWriteResult ApplyTemporary(string monitorPath, DisplayModeSpec mode)
    {
        SafetyLock.ThrowIfLocked(What);
        if (Ccd.Find(monitorPath) is not { GdiName: { } gdi }) return new(DisplayWriteStatus.NotFound, Loc.T("монитор не найден"));
        if (Ccd.Query() is { } snapshot) _snapshots[monitorPath] = snapshot;

        var dm = ModeFor(mode);
        var result = Code(ChangeDisplaySettingsExW(gdi, ref dm, IntPtr.Zero, 0, IntPtr.Zero), Loc.T("смена режима"));
        if (!result.Ok) return result;
        return WaitFor(monitorPath, mode, ApplyWait) ? DisplayWriteResult.Success
            : new DisplayWriteResult(DisplayWriteStatus.Failed, Loc.T("режим не появился за 5 с"));
    }

    public DisplayWriteResult Persist(string monitorPath, DisplayModeSpec mode)
    {
        SafetyLock.ThrowIfLocked(What);
        if (Ccd.Find(monitorPath) is not { GdiName: { } gdi }) return new(DisplayWriteStatus.NotFound, Loc.T("монитор не найден"));
        var dm = ModeFor(mode);
        var result = Code(ChangeDisplaySettingsExW(gdi, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero), Loc.T("сохранение режима"));
        if (!result.Ok) return result;
        // Verified against the saved (registry) mode; a mode that cannot be read back is taken as Windows reported it.
        return ReadPersistedMode(monitorPath) is not { } saved
               || (saved.Width == mode.Width && saved.Height == mode.Height && (saved.SameMode(mode) || saved.Frequency <= 1))
            ? DisplayWriteResult.Success
            : new DisplayWriteResult(DisplayWriteStatus.Failed, Loc.Tf("Windows сохранила другой режим ({0})", saved.Describe()));
    }

    /// <summary>
    /// Back from a temporary mode: CDS(NULL) (return to the saved mode) → verify → SDC_USE_DATABASE_CURRENT → verify → the
    /// CCD snapshot taken before the temporary apply → verify. Every fallback is logged.
    /// </summary>
    public DisplayWriteResult RevertTemporary(string monitorPath, DisplayModeSpec original)
    {
        SafetyLock.ThrowIfLocked(What);
        ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (WaitFor(monitorPath, original, RevertWait)) return DisplayWriteResult.Success;

        Log.Warn("Возврат режима экрана: ChangeDisplaySettingsEx(NULL) не вернул прежний режим — пробую сохранённую конфигурацию Windows.");
        SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_USE_DATABASE_CURRENT);
        if (WaitFor(monitorPath, original, RevertWait)) return DisplayWriteResult.Success;

        if (_snapshots.TryGetValue(monitorPath, out var snap) && snap.Paths.Length > 0)
        {
            Log.Warn("Возврат режима экрана: сохранённая конфигурация не помогла — применяю снимок, сделанный перед изменением.");
            var rc = SetDisplayConfig((uint)snap.Paths.Length, snap.Paths, (uint)snap.Modes.Length, snap.Modes,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
            if (rc == ERROR_SUCCESS && WaitFor(monitorPath, original, RevertWait)) return DisplayWriteResult.Success;
        }
        Log.Warn("Возврат режима экрана: прежний режим не подтвердился.");
        return new DisplayWriteResult(DisplayWriteStatus.Failed, Loc.T("прежний режим не вернулся"));
    }

    /// <summary>Polls the live mode until it matches (CCD shows a modeset a moment after the call returns).</summary>
    private bool WaitFor(string monitorPath, DisplayModeSpec mode, TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        do
        {
            if (ReadCurrentMode(monitorPath) is { } live && live.SameMode(mode)) return true;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < until);
        return ReadCurrentMode(monitorPath) is { } last && last.SameMode(mode);
    }

    // ───────────────────────────── HDR ─────────────────────────────

    public DisplayWriteResult SetHdr(string monitorPath, bool enable)
    {
        SafetyLock.ThrowIfLocked(What);
        return SetHdrCore(monitorPath, enable);
    }

    private static DisplayWriteResult SetHdrCore(string monitorPath, bool enable)
    {
        if (Ccd.Find(monitorPath) is not { } p) return new(DisplayWriteStatus.NotFound, Loc.T("монитор не найден"));
        var color = Ccd.Color(p.TargetAdapter, p.TargetId, Build);
        if (color is not { HdrSupported: true }) return new(DisplayWriteStatus.Failed, Loc.T("монитор не поддерживает HDR"));
        if (color.LimitedByPolicy) return new(DisplayWriteStatus.Failed, Loc.T("HDR запрещён политикой или драйвером"));
        if (color.HdrEnabled == enable) return DisplayWriteResult.Success;

        var state = new SET_STATE();
        state.header.type = Build >= 26100 ? DEVICE_INFO_SET_HDR_STATE : DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
        state.header.size = (uint)Marshal.SizeOf<SET_STATE>();
        state.header.adapterId = p.TargetAdapter;
        state.header.id = p.TargetId;
        state.value = enable ? 1u : 0u;
        var rc = DisplayConfigSetDeviceInfo(ref state);
        if (rc != ERROR_SUCCESS) return new(DisplayWriteStatus.Failed, Loc.Tf("Windows не переключила HDR (код {0})", rc.ToString(CultureInfo.InvariantCulture)));

        // The switch is a modeset: give the driver a moment, then wait (bounded) until the new state reads back, so a slow
        // driver is never reported as «failed» while it is still switching (that change would then have no record).
        Thread.Sleep(HdrSettle);
        var until = DateTime.UtcNow + HdrConfirmWait;
        while (true)
        {
            if (Ccd.Find(monitorPath) is { } again && Ccd.Color(again.TargetAdapter, again.TargetId, Build)?.HdrEnabled == enable)
                return DisplayWriteResult.Success;
            if (DateTime.UtcNow >= until)
                return new DisplayWriteResult(DisplayWriteStatus.Failed, Loc.T("драйвер видеокарты вернул прежнее значение (настройка цвета в панели NVIDIA/AMD?)"));
            Thread.Sleep(250);
        }
    }

    // ───────────────────────────── Guard process ─────────────────────────────

    public static string KeepEventName(Guid id) => $@"Local\MONOLITH-DisplayKeep-{id:N}";
    public static string ArmedEventName(Guid id) => $@"Local\MONOLITH-DisplayArmed-{id:N}";

    /// <summary>How long the guard may take to report «armed».</summary>
    internal static TimeSpan ArmTimeout = TimeSpan.FromSeconds(5);

    /// <summary>HARDWARE TESTS ONLY: the Lycoris.exe to start as the guard when the host process is the test runner.</summary>
    internal static string? GuardExecutableForTests { get; set; }

    public IDisplayGuard? StartGuard(Guid recordId, DateTime deadlineUtc, DisplayGuardAction action)
    {
        SafetyLock.ThrowIfLocked(What);
        EventWaitHandle? keep = null, armed = null;
        Process? process = null;
        try
        {
            keep = new EventWaitHandle(false, EventResetMode.ManualReset, KeepEventName(recordId), out var keepNew);
            armed = new EventWaitHandle(false, EventResetMode.ManualReset, ArmedEventName(recordId), out var armedNew);
            if (!keepNew || !armedNew) throw new InvalidOperationException(Loc.T("событие защитного процесса уже существует"));

            // The guard is this exe started again with THIS (elevated) token and no UAC prompt: it must still be the file we started from.
            if (Elevation.IsElevated && Monolith.Core.Security.SelfIntegrity.CheckBeforeElevation() is { Allowed: false } refusal)
                throw new InvalidOperationException(refusal.Reason ?? "integrity check");
            var psi = new ProcessStartInfo(GuardExecutableForTests ?? AppPaths.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in BuildGuardArgs(recordId, deadlineUtc, Environment.ProcessId, action)) psi.ArgumentList.Add(arg);
            process = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T("процесс не запустился"));

            var until = DateTime.UtcNow + ArmTimeout;
            while (!armed.WaitOne(100))
            {
                if (process.HasExited) throw new InvalidOperationException(Loc.Tf("защитный процесс завершился с кодом {0}", process.ExitCode));
                if (DateTime.UtcNow >= until) throw new TimeoutException(Loc.T("защитный процесс не ответил за 5 с"));
            }
            return new Guard(keep, armed, process);
        }
        catch (Exception ex)
        {
            Log.Warn("Защитный процесс экрана не запущен: {0}", ex.Message);
            try
            {
                if (process is { HasExited: false }) process.Kill();
            }
            catch { /* already gone */ }
            process?.Dispose();
            keep?.Dispose();
            armed?.Dispose();
            return null;
        }
    }

    private sealed class Guard(EventWaitHandle keep, EventWaitHandle armed, Process process) : IDisplayGuard
    {
        private int _disposed;

        public void Resolve()
        {
            try { keep.Set(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            // The guard holds its own handle to the events: closing ours never cancels a Resolve it has not seen yet.
            keep.Dispose();
            armed.Dispose();
            process.Dispose();
        }
    }

    /// <summary>
    /// The guard process: waits for the keep signal, Lycoris's exit or the deadline. Kept → exit 0 without touching
    /// anything. Otherwise mode → back to the saved mode (CDS(NULL), then SDC_USE_DATABASE_CURRENT); HDR → the original
    /// state. Never touches the journal or the settings. Malformed arguments → 3, events missing → 4, nothing done.
    /// </summary>
    public int RunGuard(string[] args)
    {
        if (!TryParseGuardArgs(args, DateTime.UtcNow, out var g) || g is null) return 3;
        EventWaitHandle keep, armed;
        try
        {
            keep = EventWaitHandle.OpenExisting(KeepEventName(g.RecordId));
        }
        catch
        {
            return 4;
        }
        using (keep)
        {
            try { armed = EventWaitHandle.OpenExisting(ArmedEventName(g.RecordId)); }
            catch { return 4; }
            using (armed)
            {
                var parent = OpenParent(g.ParentPid);
                try { armed.Set(); }
                catch
                {
                    parent?.Dispose();
                    return 4;
                }

                bool kept;
                using (parent)
                {
                    kept = parent is null ? keep.WaitOne(0) : WaitKeep(keep, parent, g.DeadlineUtc);
                }
                if (kept) return 0;

                try
                {
                    if (g.Kind == DisplayGuardKind.Mode)
                    {
                        ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                        SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_USE_DATABASE_CURRENT);
                        Log.Warn("Защитный процесс: изменение экрана не подтверждено — возвращён сохранённый режим.");
                    }
                    else if (g.MonitorPath is { } path)
                    {
                        var back = SetHdrCore(path, g.OriginalHdr);
                        if (back.Ok) Log.Warn("Защитный процесс: изменение HDR не подтверждено — возвращено прежнее состояние.");
                        else Log.Warn("Защитный процесс: изменение HDR не подтверждено — вернуть не удалось: {0}.", back.Message);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Защитный процесс: возврат не удался — {0}", ex.Message);
                }
                return 1;
            }
        }
    }

    /// <summary>The parent Lycoris, or null when it is gone (or its id now belongs to another program).</summary>
    private static Process? OpenParent(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            using var self = Process.GetCurrentProcess();
            // The opt-in hardware tests (LYCORIS_HW_TESTS=1, docs/hardware-verification-2026-11.md) start the guard from the xUnit host.
            var testHost = Environment.GetEnvironmentVariable("LYCORIS_HW_TESTS") == "1"
                           && string.Equals(p.ProcessName, "testhost", StringComparison.OrdinalIgnoreCase);
            if (!p.HasExited && (testHost || string.Equals(p.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase))) return p;
            p.Dispose();
        }
        catch { /* not running */ }
        return null;
    }

    private static bool WaitKeep(EventWaitHandle keep, Process parent, DateTime deadlineUtc)
    {
        while (true)
        {
            if (keep.WaitOne(200)) return true;
            bool exited;
            try { exited = parent.HasExited; }
            catch { exited = true; }
            if (exited || DateTime.UtcNow >= deadlineUtc) return keep.WaitOne(0);
        }
    }

    // ───────────────────────────── Guard arguments (pure) ─────────────────────────────

    /// <summary>
    /// «--display-guard &lt;id:N&gt; &lt;deadline ticks&gt; &lt;pid&gt; mode» or
    /// «--display-guard &lt;id:N&gt; &lt;ticks&gt; &lt;pid&gt; hdr &lt;0|1 original&gt; &lt;base64url(UTF-8 monitor path)&gt;».
    /// </summary>
    public static string[] BuildGuardArgs(Guid recordId, DateTime deadlineUtc, int parentPid, DisplayGuardAction action)
    {
        var head = new[]
        {
            DisplayGuardProcess.Switch, recordId.ToString("N"), deadlineUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            parentPid.ToString(CultureInfo.InvariantCulture),
        };
        return action.Kind == DisplayGuardKind.Mode
            ? [.. head, "mode"]
            : [.. head, "hdr", action.OriginalHdr ? "1" : "0", Base64Url(action.MonitorPath)];
    }

    /// <summary>
    /// Strict parse: exact arity, a 32-hex id, a deadline from now to now + 2 min, a positive pid, kind mode|hdr, an HDR
    /// flag 0|1 and a canonical base64url monitor path that passes DeviceTargets.IsMonitorPath. Anything else → false.
    /// </summary>
    public static bool TryParseGuardArgs(string[]? args, DateTime nowUtc, out GuardArgs? parsed)
    {
        parsed = null;
        if (args is null || args.Length < 5 || args[0] != DisplayGuardProcess.Switch) return false;
        var id = args[1];
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigit) || !Guid.TryParseExact(id, "N", out var recordId)) return false;
        if (args[2].Length is 0 or > 19 || !args[2].All(char.IsAsciiDigit)
            || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
        if (ticks < nowUtc.Ticks || ticks > nowUtc.AddMinutes(2).Ticks) return false;
        if (args[3].Length is 0 or > 10 || !args[3].All(char.IsAsciiDigit)
            || !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0) return false;
        var deadline = new DateTime(ticks, DateTimeKind.Utc);

        switch (args[4])
        {
            case "mode" when args.Length == 5:
                parsed = new GuardArgs(recordId, deadline, pid, DisplayGuardKind.Mode, false, null);
                return true;
            case "hdr" when args.Length == 7:
                if (args[5] is not ("0" or "1")) return false;
                if (FromBase64Url(args[6]) is not { } path || !DeviceTargets.IsMonitorPath(path)) return false;
                parsed = new GuardArgs(recordId, deadline, pid, DisplayGuardKind.Hdr, args[5] == "1", path);
                return true;
            default:
                return false;
        }
    }

    internal static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string? FromBase64Url(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 1024 || !encoded.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        var b64 = encoded.Replace('-', '+').Replace('_', '/');
        b64 += (b64.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => "!" };
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(b64));
            return Base64Url(text) == encoded ? text : null; // canonical form only
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private static T Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return default!;
        }
    }
}
