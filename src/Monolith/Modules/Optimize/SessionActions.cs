using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Monolith.Core;

namespace Monolith.Modules.Optimize;

internal sealed record SessionActionResult(bool Success, string Message);

/// <summary>
/// "Make it take effect" actions offered after applying tweaks: restarting Explorer and scheduling a reboot.
/// Neither changes any setting; both run only after the user confirmed them.
/// </summary>
internal static class SessionActions
{
    private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    /// <summary>Unelevated per-user processes whose token a new Explorer may inherit (never our admin token).</summary>
    private static readonly string[] ShellParentCandidates =
        ["sihost", "ctfmon", "taskhostw", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "RuntimeBroker"];

    private static string ExplorerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    // ───────────────────────────── Explorer ─────────────────────────────

    /// <summary>
    /// Restarts Explorer in the current session without taskkill:
    ///  1. ends the explorer.exe processes of this session only;
    ///  2. waits for Windows to bring the shell back itself (Winlogon AutoRestartShell, with the user's normal token);
    ///  3. if it doesn't, starts explorer.exe with the token of an unelevated process of the same session,
    ///     so the shell never runs with Lycoris's administrator rights.
    /// </summary>
    public static Task<SessionActionResult> RestartExplorerAsync() => Task.Run(RestartExplorerCore);

    private static SessionActionResult RestartExplorerCore()
    {
        var session = CurrentSessionId();
        var running = FindExplorers(session);
        var oldIds = running.Select(p => p.Id).ToHashSet();
        Log.Info("Перезапуск Проводника: процессов в сеансе {0} — {1}.", session, running.Count);

        foreach (var p in running)
        {
            using (p)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Проводник (PID {0}) не завершён: {1}", p.Id, ex.Message);
                }
            }
        }

        var stillRunning = FindExplorers(session);
        var survivors = stillRunning.Count(p => oldIds.Contains(p.Id));
        DisposeAll(stillRunning);
        if (survivors > 0)
            return new(false, Loc.T("Не удалось завершить Проводник. Изменения вступят в силу после выхода из системы или перезагрузки."));

        if (oldIds.Count > 0 && AutoRestartShellEnabled() && WaitForNewExplorer(session, oldIds, TimeSpan.FromSeconds(8)))
        {
            Log.Success("Проводник перезапущен.");
            return new(true, Loc.T("Проводник перезапущен — изменения интерфейса применены."));
        }

        if (StartUnelevated(ExplorerPath, session) && WaitForNewExplorer(session, oldIds, TimeSpan.FromSeconds(8)))
        {
            Log.Success("Проводник запущен заново.");
            return new(true, Loc.T("Проводник перезапущен — изменения интерфейса применены."));
        }

        Log.Warn("Проводник не запустился автоматически.");
        return new(false, Loc.T("Проводник не запустился сам. Нажмите Ctrl+Shift+Esc → «Запустить новую задачу» → введите explorer и нажмите Enter."));
    }

    private static int CurrentSessionId()
    {
        using var me = Process.GetCurrentProcess();
        return me.SessionId;
    }

    private static List<Process> FindExplorers(int session)
    {
        var result = new List<Process>();
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            bool mine;
            try { mine = p.SessionId == session; }
            catch { mine = false; }
            if (mine) result.Add(p);
            else p.Dispose();
        }
        return result;
    }

    private static void DisposeAll(IEnumerable<Process> list)
    {
        foreach (var p in list) p.Dispose();
    }

    private static bool WaitForNewExplorer(int session, IReadOnlySet<int> oldIds, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var list = FindExplorers(session);
            var found = list.Any(p => !oldIds.Contains(p.Id));
            DisposeAll(list);
            if (found) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    private static bool AutoRestartShellEnabled() => (RegistryHelper.ReadInt(RegHive.HKLM, WinlogonKey, "AutoRestartShell") ?? 1) != 0;

    private static bool StartUnelevated(string exe, int session)
    {
        if (!Elevation.IsElevated)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
                return p is not null;
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось запустить Проводник: {0}", ex.Message);
                return false;
            }
        }

        foreach (var name in ShellParentCandidates)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        if (p.SessionId != session) continue;
                        if (StartWithParent(p.Id, exe)) return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Запуск Проводника через {0}: {1}", name, ex.Message);
                    }
                }
            }
        }
        return false;
    }

    /// <summary>Starts <paramref name="exe"/> as a child of <paramref name="parentPid"/>, inheriting its (unelevated) token.</summary>
    private static bool StartWithParent(int parentPid, string exe)
    {
        var hParent = OpenProcess(ProcessCreateProcess | ProcessQueryLimitedInformation, false, parentPid);
        if (hParent == IntPtr.Zero) return false;

        IntPtr hToken = IntPtr.Zero, env = IntPtr.Zero, attrList = IntPtr.Zero, pValue = IntPtr.Zero;
        var attrInitialized = false;
        try
        {
            if (!OpenProcessToken(hParent, TokenQuery | TokenDuplicate, out hToken)) return false;
            if (!GetTokenInformation(hToken, TokenElevationClass, out var elevated, sizeof(int), out _) || elevated != 0)
                return false;
            if (!CreateEnvironmentBlock(out env, hToken, false)) env = IntPtr.Zero;

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == IntPtr.Zero) return false;
            attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) return false;
            attrInitialized = true;

            pValue = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(pValue, hParent);
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)ProcThreadAttributeParentProcess, pValue, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                return false;

            var si = new StartupInfoEx { lpAttributeList = attrList };
            si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            var flags = ExtendedStartupInfoPresent | (env != IntPtr.Zero ? CreateUnicodeEnvironment : 0);
            var cmd = new StringBuilder($"\"{exe}\"");
            if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, env, Path.GetDirectoryName(exe), ref si, out var pi))
            {
                Log.Warn("Запуск Проводника: {0}", new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return false;
            }
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
        finally
        {
            if (attrInitialized) DeleteProcThreadAttributeList(attrList);
            if (attrList != IntPtr.Zero) Marshal.FreeHGlobal(attrList);
            if (pValue != IntPtr.Zero) Marshal.FreeHGlobal(pValue);
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            CloseHandle(hParent);
        }
    }

    // ───────────────────────────── Reboot ─────────────────────────────

    public const int RebootDelaySeconds = 10;

    /// <summary>Schedules a normal reboot (shutdown /r /t 10). Windows shows its own notice; "shutdown /a" cancels it.</summary>
    public static async Task<SessionActionResult> ScheduleRebootAsync()
    {
        var r = await ProcessRunner.RunAsync(ProcessRunner.Sys("shutdown.exe"),
            ["/r", "/t", RebootDelaySeconds.ToString(CultureInfo.InvariantCulture), "/c", Loc.T("Lycoris: перезагрузка для применения настроек")],
            new ProcessRunOptions { Encoding = OutputEncoding.Oem, Timeout = TimeSpan.FromSeconds(30) }).ConfigureAwait(false);
        if (r.Success)
        {
            Log.Warn("Перезагрузка запланирована через {0} с.", RebootDelaySeconds);
            return new(true, Loc.Tf("Компьютер перезагрузится через {0} секунд.", RebootDelaySeconds));
        }
        if (r.ExitCode == 1190) // ERROR_SHUTDOWN_IS_SCHEDULED
            return new(true, Loc.T("Перезагрузка уже запланирована."));
        var details = r.Combined.Trim();
        Log.Error("Не удалось запланировать перезагрузку (код {0}). {1}", r.ExitCode, details);
        return new(false, Loc.Tf("Не удалось запланировать перезагрузку (код {0}). Перезагрузите компьютер вручную.", r.ExitCode));
    }

    // ───────────────────────────── Win32 ─────────────────────────────

    private const uint ProcessCreateProcess = 0x0080;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const int TokenElevationClass = 20;
    private const int ProcThreadAttributeParentProcess = 0x00020000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
}
