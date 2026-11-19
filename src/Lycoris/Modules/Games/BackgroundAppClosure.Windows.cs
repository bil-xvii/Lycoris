using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Lycoris.Core;
using Microsoft.Win32.SafeHandles;

namespace Lycoris.Modules.Games;

internal sealed class BackgroundCloseWindows : IBackgroundClosePlatform
{
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const int AccessDenied = 5;
    private const int InvalidParameter = 87;
    private const int NotFound = 1168;
    private const uint WmClose = 0x0010;
    private const uint GwOwner = 4;

    public bool CanClose() => Elevation.IsElevated && !SafetyLock.IsLocked;

    public BackgroundCloseContext? GetContext()
    {
        try
        {
            var pid = Environment.ProcessId;
            using var process = GamesNative.Open(pid, GamesNative.PROCESS_QUERY_LIMITED_INFORMATION);
            if (process.IsInvalid || GamesNative.ReadCloseToken(process) is not { } token ||
                token.Sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") return null;

            var windows = new StringBuilder(260);
            var length = GetSystemWindowsDirectoryW(windows, (uint)windows.Capacity);
            if (length == 0 || length >= windows.Capacity) return null;
            var directory = GamePaths.NormalizeDir(windows.ToString(0, (int)length));
            return directory.Length > 0 ? new(token.Sid, token.Session, pid, directory) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public BackgroundCloseGameProtection? GetGameProtection() => BackgroundCloseGameProtection.ReadCurrent();

    public IReadOnlyList<BackgroundCloseCandidate>? Enumerate()
    {
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return null; }

        var candidates = new List<BackgroundCloseCandidate>(processes.Length);
        try
        {
            foreach (var process in processes)
            {
                // These names are search hints only. Every selected image and identity comes from a native handle.
                try { candidates.Add(new(process.Id, process.ProcessName, process.WorkingSet64)); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    return null; // Cannot prove the selected name list is complete.
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return candidates;
    }

    public IBackgroundCloseHandle? Open(int pid, bool terminate, out BackgroundCloseStatus failure)
    {
        var access = GamesNative.PROCESS_QUERY_LIMITED_INFORMATION | Synchronize;
        if (terminate) access |= GamesNative.PROCESS_TERMINATE;
        var process = GamesNative.Open(pid, access);
        if (!process.IsInvalid)
        {
            failure = BackgroundCloseStatus.StillRunning;
            return new WindowsHandle(process, pid, terminate);
        }
        failure = Marshal.GetLastWin32Error() switch
        {
            InvalidParameter or NotFound => BackgroundCloseStatus.Stale,
            AccessDenied => BackgroundCloseStatus.Denied,
            _ => BackgroundCloseStatus.Error,
        };
        process.Dispose();
        return null;
    }

    private sealed class WindowsHandle(SafeProcessHandle process, int pid, bool canTerminate) : IBackgroundCloseHandle
    {
        public BackgroundCloseIdentity? ReadIdentity()
        {
            try { return GamesNative.ReadCloseIdentity(process); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { return null; }
        }

        public BackgroundProcessState GetState() => WaitForSingleObject(process, 0) switch
        {
            WaitObject0 => BackgroundProcessState.Exited,
            WaitTimeout => BackgroundProcessState.Running,
            _ => BackgroundProcessState.Unknown,
        };

        public BackgroundCloseRequest RequestClose()
        {
            if (SafetyLock.IsLocked) return BackgroundCloseRequest.Denied;
            if (GetState() != BackgroundProcessState.Running) return BackgroundCloseRequest.Error;

            nint mainWindow = 0;
            var enumerated = EnumWindows((window, _) =>
            {
                if (GetWindowThreadProcessId(window, out var owner) == 0 || owner != (uint)pid ||
                    !IsWindowVisible(window) || GetWindow(window, GwOwner) != 0) return true;
                mainWindow = window;
                return false;
            }, 0);
            if (mainWindow == 0)
                return enumerated ? BackgroundCloseRequest.NoWindow : BackgroundCloseRequest.Error;

            if (!IsWindowEnabled(mainWindow)) return BackgroundCloseRequest.NoWindow;

            // A live query handle prevents PID reuse. Re-check the enumerated HWND just before posting.
            if (GetState() != BackgroundProcessState.Running ||
                GetWindowThreadProcessId(mainWindow, out var currentOwner) == 0 || currentOwner != (uint)pid)
                return BackgroundCloseRequest.Error;
            return PostMessageW(mainWindow, WmClose, 0, 0) ? BackgroundCloseRequest.Sent : LastRequestError();
        }

        public BackgroundCloseRequest Terminate()
        {
            if (!canTerminate || SafetyLock.IsLocked) return BackgroundCloseRequest.Denied;
            return TerminateProcess(process, 1) ? BackgroundCloseRequest.Sent : LastRequestError();
        }

        public void Dispose() => process.Dispose();
    }

    private static BackgroundCloseRequest LastRequestError() =>
        Marshal.GetLastWin32Error() == AccessDenied ? BackgroundCloseRequest.Denied : BackgroundCloseRequest.Error;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetSystemWindowsDirectoryW(StringBuilder buffer, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
