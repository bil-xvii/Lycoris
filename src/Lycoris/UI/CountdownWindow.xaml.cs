using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Lycoris.Core.Devices;

namespace Lycoris.UI;

/// <summary>
/// The «keep or go back» prompt of a device change (Ui.CountdownConfirmAsync). Topmost, on a monitor other than the one
/// being changed, «go back» is the default and focused button; Esc, Alt+F4 and closing also go back. The real deadline is
/// a thread-pool timer in DeviceConfirm (it works even when this UI hangs); the countdown here is only the display.
/// </summary>
public partial class CountdownWindow : Window
{
    private readonly CountdownRequest _request;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _startUtc = DateTime.UtcNow;

    /// <summary>True only when the user pressed the keep button.</summary>
    public bool Kept { get; private set; }

    public CountdownWindow(CountdownRequest request)
    {
        _request = request;
        InitializeComponent();
        TitleText.Text = SentenceCaseConverter.Apply(Loc.Sink(request.Title));
        MessageText.Text = Loc.Sink(request.Message);
        KeepButton.Content = Loc.Sink(request.KeepText);
        RevertButton.Content = Loc.Sink(request.RevertText);
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        Tick();
        SourceInitialized += (_, _) => ApplyDwmStyle();
        Loaded += (_, _) =>
        {
            PlaceAwayFrom(request.AvoidGdiDevice);
            RevertButton.Focus();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Tick()
    {
        var total = _request.Duration;
        var left = total - (DateTime.UtcNow - _startUtc);
        var seconds = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        CountdownText.Text = Loc.Tf("Возврат к прежним параметрам через {0} с", seconds);
        Remaining.Value = total.TotalMilliseconds <= 0 ? 0 : Math.Clamp(left.TotalMilliseconds / total.TotalMilliseconds, 0, 1);
        // DeviceConfirm closes it at the deadline; this is only a fallback when nobody does.
        if (left < TimeSpan.FromSeconds(-2)) CloseAsRevert();
    }

    private void OnKeep(object sender, RoutedEventArgs e)
    {
        Kept = true;
        Close();
    }

    private void OnRevert(object sender, RoutedEventArgs e) => CloseAsRevert();

    /// <summary>Closes with the answer «go back» (deadline, cancellation).</summary>
    public void CloseAsRevert()
    {
        if (!IsLoaded && !IsVisible) return;
        Kept = false;
        try { Close(); }
        catch (InvalidOperationException) { } // already closing
    }

    // ───────────────────────────── Placement ─────────────────────────────

    private void PlaceAwayFrom(string? avoidGdi)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var monitors = new List<(IntPtr Handle, RECT Work, string Device, bool Primary)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfoW(h, ref info)) monitors.Add((h, info.rcWork, info.szDevice, (info.dwFlags & 1) != 0));
                return true;
            }, IntPtr.Zero);
            if (monitors.Count == 0) return;

            bool Allowed((IntPtr Handle, RECT Work, string Device, bool Primary) m) =>
                avoidGdi is null || !string.Equals(m.Device, avoidGdi, StringComparison.OrdinalIgnoreCase);

            var main = Application.Current?.MainWindow is { } mw ? MonitorFromWindow(new WindowInteropHelper(mw).Handle, 2) : IntPtr.Zero;
            var target = monitors.FirstOrDefault(m => m.Handle == main && Allowed(m));
            if (target.Handle == IntPtr.Zero) target = monitors.FirstOrDefault(m => m.Primary && Allowed(m));
            if (target.Handle == IntPtr.Zero) target = monitors.FirstOrDefault(Allowed);
            if (target.Handle == IntPtr.Zero) target = monitors[0]; // one monitor only: the changed one is all there is

            // Move onto the monitor first (its DPI may resize the window), then center with the final size.
            var work = target.Work;
            SetWindowPos(hwnd, HWND_TOPMOST, work.Left + 40, work.Top + 40, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            if (!GetWindowRect(hwnd, out var r)) return;
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            var x = work.Left + Math.Max(0, (work.Right - work.Left - w) / 2);
            var y = work.Top + Math.Max(0, (work.Bottom - work.Top - h) / 2);
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Core.Log.Warn("Окно подтверждения: не удалось выбрать монитор ({0}).", ex.Message);
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - ActualWidth) / 2;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - ActualHeight) / 2;
        }
    }

    private void ApplyDwmStyle()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int corner = 2; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch
        {
            // Older Windows: square corners.
        }
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
