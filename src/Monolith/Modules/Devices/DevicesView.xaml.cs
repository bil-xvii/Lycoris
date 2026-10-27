using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Monolith.Modules.Devices;

public partial class DevicesView : UserControl
{
    private DevicesViewModel? _vm;

    public DevicesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as DevicesViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                if (_vm.FocusTarget is not null) ScrollToFocus();
            }
        };
        Unloaded += (_, _) => ReleaseClip();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DevicesViewModel.FocusTarget) && _vm?.FocusTarget is not null) ScrollToFocus();
    }

    /// <summary>
    /// Brings the focused card / row / plan line into view once the tab has laid it out. Deferred (Background): never a
    /// layout pass inside a collection change.
    /// </summary>
    private void ScrollToFocus() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (_vm?.FocusTarget is not { } target) return;
        if (target is PlanItem item && _vm.OptionalItems.Contains(item)) _vm.OptionalExpanded = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var root = (DependencyObject)(_vm.IsManualMode ? ManualScroll : AutoScroll);
            if (FindByContext(root, target) is { } element) element.BringIntoView(new Rect(0, 0, element.ActualWidth, Math.Min(element.ActualHeight, 160)));
            _vm.FocusTarget = null; // one-shot
        });
    });

    private static FrameworkElement? FindByContext(DependencyObject parent, object context)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border { DataContext: var dc } border && ReferenceEquals(dc, context) && border.IsVisible) return border;
            if (FindByContext(child, context) is { } nested) return nested;
        }
        return null;
    }

    // ───────────────────────────── Test areas ─────────────────────────────

    /// <summary>The double-click test target: counts a double click at the speed Windows uses right now.</summary>
    private void OnDoubleClickTest(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Child: TextBlock text }) return;
        if (e.ClickCount >= 2) text.Text = "Двойной щелчок засчитан";
        else if (e.ClickCount == 1 && text.Text != "Дважды щёлкните здесь") text.Text = "Дважды щёлкните здесь";
    }

    // ───────────────────────────── DPI drag pad ─────────────────────────────

    private UIElement? _padCapture;

    /// <summary>
    /// The press lands on the pad: capture the mouse (no other control reacts) and keep the cursor inside MONOLITH's window
    /// while the button is held, so a long drag can never move files or windows elsewhere.
    /// </summary>
    private void OnPadDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceTool { IsRunning: true } tool } pad) return;
        e.Handled = true;
        if (!pad.CaptureMouse()) return;
        _padCapture = pad;
        ClipToWindow();
        tool.PadHeld = true;
    }

    private void OnPadUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DeviceTool tool } pad) return;
        e.Handled = true;
        if (pad.IsMouseCaptured) pad.ReleaseMouseCapture();
        EndPad(tool);
    }

    private void OnPadLostCapture(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceTool tool }) EndPad(tool);
    }

    private void EndPad(DeviceTool tool)
    {
        _padCapture = null;
        ReleaseClip();
        if (tool.PadHeld) tool.PadHeld = false;
    }

    private void ClipToWindow()
    {
        try
        {
            if (Window.GetWindow(this) is not { } window) return;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (GetWindowRect(hwnd, out var rect)) ClipCursor(ref rect);
        }
        catch { }
    }

    private void ReleaseClip()
    {
        try { ClipCursor(IntPtr.Zero); }
        catch { }
        if (_padCapture is { IsMouseCaptured: true } pad) pad.ReleaseMouseCapture();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClipCursor(ref RECT rect);
    [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);
}
