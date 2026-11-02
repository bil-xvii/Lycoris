using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lycoris.Modules.Devices;

public partial class DevicesView : UserControl
{
    /// <summary>Visible when the row can be applied (not read-only) but its value was not changed: the reason ПРИМЕНИТЬ is grey.</summary>
    public static System.Windows.Data.IMultiValueConverter NotDirtyEditableToVisibility { get; } =
        new BoolsConverter(v => v is [false, false] ? Visibility.Visible : Visibility.Collapsed);

    /// <summary>Visible when a row has a link and no measurement of its own (a measurement row has the «ЗАМЕРИТЬ» button instead).</summary>
    public static System.Windows.Data.IMultiValueConverter LinkWithoutToolToVisibility { get; } =
        new BoolsConverter(v => v is [{ } link, false] ? Visibility.Visible : Visibility.Collapsed, allowObjects: true);

    /// <summary>
    /// The panel under a plan row for its measurement (options, progress, result): visible only when there is something to
    /// show — the tool is running, has a result, or has options to choose first. Values: has tool, running, result, has options.
    /// </summary>
    public static System.Windows.Data.IMultiValueConverter ToolPanelToVisibility { get; } =
        new BoolsConverter(v => v is [true, var running, var result, var options] && (running is true || result is not null || options is true)
            ? Visibility.Visible : Visibility.Collapsed, allowObjects: true);

    private sealed class BoolsConverter(Func<object?[], Visibility> pick, bool allowObjects = false) : System.Windows.Data.IMultiValueConverter
    {
        public object Convert(object?[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            // An unset binding (DependencyProperty.UnsetValue) counts as null / false.
            var normalized = values.Select(v => v == DependencyProperty.UnsetValue ? null : v).ToArray();
            if (!allowObjects) normalized = normalized.Select(v => (object?)(v is true)).ToArray();
            else normalized = normalized.Select(v => v is bool b ? b : v).ToArray();
            return pick(normalized);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture) =>
            targetTypes.Select(_ => System.Windows.Data.Binding.DoNothing).ToArray();
    }

    private DevicesViewModel? _vm;

    public DevicesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (IsLoaded) Sync(); };
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => { Release(); ReleaseClip(); };
    }

    /// <summary>The view subscribes to its page view-model only while it is on screen (see OptimizeView): a subscription left behind would keep every closed copy of this view alive.</summary>
    private void Sync()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as DevicesViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            if (_vm.FocusTarget is not null) ScrollToFocus();
        }
    }

    private void Release()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
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
        if (e.ClickCount >= 2) text.Text = Loc.T("Двойной щелчок засчитан");
        else if (e.ClickCount == 1 && text.Text != Loc.T("Дважды щёлкните здесь")) text.Text = Loc.T("Дважды щёлкните здесь");
    }

    // ───────────────────────────── DPI drag pad ─────────────────────────────

    private UIElement? _padCapture;

    /// <summary>
    /// The press lands on the pad: capture the mouse (no other control reacts) and keep the cursor inside Lycoris's window
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
