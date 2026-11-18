using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Lycoris.Core;

namespace Lycoris.Shell;

/// <summary>Owns the notification icon, its menu and cross-instance activation hook on the primary window's HWND.</summary>
internal sealed class BackgroundTray : IDisposable
{
    private const int Callback = 0x8000 + 40; // WM_APP + 40
    private const int Destroy = 0x0002; // WM_DESTROY
    private const int Select = 0x0400, KeySelect = 0x0401, ContextMenuRequested = 0x007b;
    private const uint Add = 0, Delete = 2, SetFocus = 3, SetVersion = 4;
    private readonly HwndSource _source;
    private readonly nint _window;
    private readonly BackgroundWindowActivation _activation;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly Action _unavailable;
    private readonly int _taskbarCreated;
    private readonly bool _canReceive;
    private ContextMenu? _menu;
    private nint _icon;
    private bool _disposed;

    public BackgroundTray(HwndSource source, BackgroundWindowActivation activation, Action show, Action exit, Action unavailable)
    {
        _source = source;
        _window = source.Handle;
        _activation = activation;
        _show = show;
        _exit = exit;
        _unavailable = unavailable;
        _source.AddHook(HandleMessage);
        var activationReady = _activation.Attach(_window);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _canReceive = activationReady && _taskbarCreated != 0
            && BackgroundWindowActivation.AllowMessage(_window, _taskbarCreated)
            && BackgroundWindowActivation.AllowMessage(_window, Callback);
    }

    public bool Ready { get; private set; }

    public bool ShowIcon()
    {
        if (Ready) return true;
        if (_disposed || !_canReceive) return false;
        if (_icon == 0 && Environment.ProcessPath is { } path)
            ExtractIconEx(path, 0, 0, out _icon, 1);
        if (_icon == 0) return false;
        var data = IconData();
        if (!ShellNotifyIcon(Add, ref data)) return false;
        data.Version = 4; // NOTIFYICON_VERSION_4, including keyboard selection and WM_CONTEXTMENU.
        Ready = ShellNotifyIcon(SetVersion, ref data);
        if (!Ready) ShellNotifyIcon(Delete, ref data);
        return Ready;
    }

    public void RemoveIcon()
    {
        if (_menu is not null) _menu.IsOpen = false;
        if (!Ready) return;
        Ready = false;
        var data = IconData();
        ShellNotifyIcon(Delete, ref data);
    }

    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed) return 0;
        if (message == Destroy)
        {
            RemoveIcon();
            return 0;
        }
        if (_activation.IsRequest(message, wParam, lParam))
        {
            Queue(_show);
            handled = true;
            return _activation.Accept();
        }
        if (message == _taskbarCreated && Ready)
        {
            Queue(() =>
            {
                if (!Ready) return;
                // Re-add after Explorer restarts. If Windows still has the icon (e.g. a DPI change), refresh it.
                RemoveIcon();
                if (!ShowIcon()) _unavailable();
            });
        }
        if (message != Callback || !Ready) return 0;
        var notification = unchecked((uint)(long)lParam);
        if (notification >> 16 != 1) return 0; // the icon's id
        switch (notification & 0xffff)
        {
            case Select or KeySelect: Queue(_show); break;
            case ContextMenuRequested: Queue(OpenMenu); break;
            default: return 0;
        }
        handled = true;
        return 0;
    }

    private void Queue(Action action) => _source.Dispatcher.BeginInvoke(() =>
    {
        if (!_disposed) action();
    }, DispatcherPriority.Normal);

    private void OpenMenu()
    {
        if (!Ready || _menu?.IsOpen == true) return;
        if (_menu is null)
        {
            var open = new MenuItem { Header = Loc.T("Открыть Lycoris") };
            open.Click += (_, _) => _show();
            var exit = new MenuItem { Header = Loc.T("Выйти из Lycoris") };
            exit.Click += (_, _) => _exit();
            _menu = new ContextMenu { Placement = PlacementMode.MousePoint, Items = { open, exit } };
            _menu.Closed += (_, _) =>
            {
                if (!Ready) return;
                var data = IconData();
                ShellNotifyIcon(SetFocus, ref data);
            };
        }
        _menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveIcon();
        _activation.Detach(_window);
        if (!_source.IsDisposed) _source.RemoveHook(HandleMessage);
        if (_icon != 0) DestroyIcon(_icon);
    }

    private NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1,
        Flags = 0x01 | 0x02 | 0x04 | 0x80, // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP
        CallbackMessage = Callback, Icon = _icon, Tip = "Lycoris", Info = "", InfoTitle = "",
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, nint large, out nint small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
