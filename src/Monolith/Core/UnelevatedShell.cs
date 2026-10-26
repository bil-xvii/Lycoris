using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Monolith.Core;

/// <summary>
/// Starts programs with the desktop user's normal (non-elevated) token from an elevated MONOLITH:
/// asks the desktop's Explorer window for its shell view and calls IShellDispatch2.ShellExecute on it,
/// so Explorer — not MONOLITH — creates the process (research §13). Used for elevation-prohibited, user-scope
/// and Store packages, and for opening web pages (a browser must never run as administrator).
/// </summary>
public static class UnelevatedShell
{
    public const int SwHide = 0;
    public const int SwShowNormal = 1;

    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidIShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");
    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioBackground = 0;

    /// <summary>
    /// ShellExecute through the desktop shell. Returns false when there is no Explorer desktop (custom shell,
    /// Explorer restarting) or the call failed — the caller then offers another way.
    /// </summary>
    public static Task<bool> ShellExecuteAsync(string file, string arguments, string directory, int show) =>
        RunStaAsync(() =>
        {
            object? shellWindows = null, desktop = null, browser = null, view = null, folderView = null, shellApp = null;
            try
            {
                var type = Type.GetTypeFromCLSID(ClsidShellWindows, throwOnError: true)!;
                shellWindows = Activator.CreateInstance(type);
                if (shellWindows is not IShellWindows windows) return false;

                object loc = CsidlDesktop;
                object? empty = null;
                desktop = windows.FindWindowSW(ref loc, ref empty, SwcDesktop, out _, SwfoNeedDispatch);
                if (desktop is not IComServiceProvider provider) return false;

                var sid = SidSTopLevelBrowser;
                var iid = IidIShellBrowser;
                browser = provider.QueryService(ref sid, ref iid);
                if (browser is not IShellBrowser shellBrowser) return false;

                view = shellBrowser.QueryActiveShellView();
                if (view is not IShellView shellView) return false;

                var dispIid = IidIDispatch;
                folderView = shellView.GetItemObject(SvgioBackground, ref dispIid);
                if (folderView is null) return false;

                shellApp = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null);
                if (shellApp is null) return false;

                shellApp.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, shellApp,
                    [file, arguments, directory, "open", show]);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Запуск без прав администратора через оболочку не удался: {ex.Message}");
                return false;
            }
            finally
            {
                foreach (var o in new[] { shellApp, folderView, view, browser, desktop, shellWindows })
                    if (o is not null && Marshal.IsComObject(o))
                        try { Marshal.ReleaseComObject(o); } catch { }
            }
        });

    /// <summary>
    /// Opens a web page or a store link. When MONOLITH is elevated, the browser / Store is started through the
    /// desktop shell, so it never inherits administrator rights; otherwise (or if that fails) a normal shell open.
    /// </summary>
    public static async Task<bool> OpenAsync(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "ms-windows-store"))
        {
            Log.Warn($"Отклонена ссылка: {target}");
            return false;
        }
        if (Elevation.IsElevated && await ShellExecuteAsync(uri.AbsoluteUri, "", "", SwShowNormal).ConfigureAwait(false))
            return true;
        if (Elevation.IsElevated)
        {
            // Never hand a URL to a browser that would run as administrator.
            Log.Warn($"Не удалось открыть ссылку без прав администратора: {uri.AbsoluteUri}");
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось открыть {uri.AbsoluteUri}", ex);
            return false;
        }
    }

    /// <summary>
    /// True for an existing-looking local .html/.htm file path that lies inside <paramref name="root"/>
    /// (after normalization, so "..\" tricks are refused). Pure check; does not touch the disk.
    /// </summary>
    public static bool IsAllowedLocalReport(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var full = Path.GetFullPath(path);
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            var ext = Path.GetExtension(full);
            return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                   && (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a local HTML report (e.g. powercfg /batteryreport output) in the default browser. Only existing
    /// .html files under <see cref="AppPaths.LogsDir"/> are accepted. When MONOLITH is elevated the browser is started
    /// through the desktop shell, so it never runs as administrator; there is no elevated fallback.
    /// </summary>
    public static async Task<bool> OpenFileAsync(string path)
    {
        if (!IsAllowedLocalReport(path, AppPaths.LogsDir) || !File.Exists(path))
        {
            Log.Warn($"Отклонено открытие файла: {path}");
            return false;
        }
        var full = Path.GetFullPath(path);
        if (Elevation.IsElevated)
        {
            if (await ShellExecuteAsync(full, "", Path.GetDirectoryName(full) ?? "", SwShowNormal).ConfigureAwait(false))
                return true;
            Log.Warn($"Не удалось открыть файл без прав администратора: {full}");
            return false;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось открыть {full}", ex);
            return false;
        }
    }

    /// <summary>
    /// Starts a program, game or launcher link (steam://, com.epicgames.launcher://, …) as the signed-in user:
    /// through the desktop shell when MONOLITH is elevated (games must never inherit administrator rights),
    /// a normal shell start otherwise. Returns false if it could not be started without elevation.
    /// </summary>
    public static async Task<bool> StartAsUserAsync(string fileOrUri, string arguments = "", string? directory = null)
    {
        if (Elevation.IsElevated)
        {
            if (await ShellExecuteAsync(fileOrUri, arguments, directory ?? "", SwShowNormal).ConfigureAwait(false))
                return true;
            Log.Warn($"Не удалось запустить без прав администратора: {fileOrUri}");
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo(fileOrUri) { UseShellExecute = true, Arguments = arguments };
            if (!string.IsNullOrEmpty(directory)) psi.WorkingDirectory = directory;
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось запустить {fileOrUri}", ex);
            return false;
        }
    }

    private static Task<T> RunStaAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "MONOLITH shell launcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    // ─────────────────────────── COM interfaces (exdisp.h / shobjidl.h) ───────────────────────────

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
        void RegisterPending(int lThreadId, ref object pvarloc, ref object pvarlocRoot, int swClass, out int plCookie);
        void Revoke(int lCookie);
        void OnNavigate(int lCookie, ref object pvarLoc);
        void OnActivated(int lCookie, [MarshalAs(UnmanagedType.VariantBool)] bool fActive);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW(ref object pvarLoc, ref object? pvarLocRoot, int swClass, out int pHwnd, int swfwOptions);
        void OnCreated(int lCookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);
        void ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool fAttach);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid guidService, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(int fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB(IntPtr pszStatusText);
        void EnableModelessSB(int fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wId);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        IShellView QueryActiveShellView();
        void OnViewWindowActive(IShellView pshv);
        void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(int fEnterMode);
        void TranslateAccelerator(IntPtr pmsg);
        void EnableModeless(int fEnable);
        void UIActivate(uint uState);
        void Refresh();
        void CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr pfs);
        void AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
        void SaveViewState();
        void SelectItem(IntPtr pidlItem, uint uFlags);
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetItemObject(uint uItem, ref Guid riid);
    }
}
