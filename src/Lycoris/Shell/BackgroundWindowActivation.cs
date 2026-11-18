using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Lycoris.Shell;

/// <summary>Only the primary window advertises an operation: show itself. No arguments cross the process boundary.</summary>
internal sealed class BackgroundWindowActivation(string instanceName)
{
    private const int Acknowledged = 1;
    private const uint AbortIfHungAndBlock = 0x0003;
    private const uint MessageTimeoutMs = 200;
    private readonly string _property = instanceName + ".Window";
    private readonly int _message = RegisterWindowMessage(instanceName + ".ShowWindow");

    public bool Attach(nint window) => _message != 0 && AllowMessage(window, _message)
        && SetProp(window, _property, Acknowledged);

    public void Detach(nint window) => RemoveProp(window, _property);

    public bool IsRequest(int message, nint wParam, nint lParam) =>
        _message != 0 && message == _message && wParam == 0 && lParam == 0;

    public nint Accept() => Acknowledged;

    public bool TryShow(Process process)
    {
        var window = FindWindow(process, requireCurrentImage: true);
        if (window == 0 || _message == 0) return false;
        AllowSetForegroundWindow(process.Id);
        return SendMessageTimeout(window, _message, 0, 0, AbortIfHungAndBlock, MessageTimeoutMs, out var result) != 0
            && result == Acknowledged;
    }

    public bool CanOfferWindowlessRecovery(Process process) =>
        CanOfferWindowlessRecovery(FindWindow(process, requireCurrentImage: false), MayBeResponsive);

    public bool HasMarkedWindow(Process process) => FindWindow(process, requireCurrentImage: false) != 0;

    internal static bool CanOfferWindowlessRecovery(nint markedWindow, Func<nint, bool> mayBeResponsive) =>
        markedWindow == 0 || !mayBeResponsive(markedWindow);

    private static bool MayBeResponsive(nint window)
    {
        // WM_NULL only probes responsiveness, even for a copy at a different path. Access denied is inconclusive.
        const int wmNull = 0, accessDenied = 5;
        return SendMessageTimeout(window, wmNull, 0, 0, AbortIfHungAndBlock, MessageTimeoutMs, out _) != 0
            || Marshal.GetLastPInvokeError() == accessDenied;
    }

    private nint FindWindow(Process process, bool requireCurrentImage)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            if (process.SessionId != current.SessionId || (requireCurrentImage && !HasCurrentImage(process.Id))) return 0;
            nint found = 0;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var pid);
                if (pid != process.Id || GetProp(window, _property) != Acknowledged) return true;
                found = window;
                return false;
            }, 0);
            return found;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    internal static bool IsSameImage(string? own, string? candidate) => own is not null
        && string.Equals(own, candidate, StringComparison.OrdinalIgnoreCase);

    private static bool HasCurrentImage(int pid)
    {
        const uint queryLimited = 0x1000;
        var process = OpenProcess(queryLimited, false, pid);
        if (process == 0) return false;
        try
        {
            var path = new StringBuilder(32768);
            var length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length)
                && IsSameImage(Environment.ProcessPath, path.ToString());
        }
        finally { CloseHandle(process); }
    }

    // Only the show request and the tray's shell notifications are allowed across UIPI. They never mutate the system.
    internal static bool AllowMessage(nint window, int message) => ChangeWindowMessageFilterEx(window, message, 1, 0);

    private delegate bool EnumWindowCallback(nint hwnd, nint arg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(nint window, int message, uint action, nint info);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint arg);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SendMessageTimeout(nint window,
        int message, nint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process,
        int flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
