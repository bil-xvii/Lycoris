using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Monolith.Modules.Drivers.Hardware;

namespace Monolith.Modules.Drivers;

public partial class DriversHardwarePanel : UserControl
{
    private DriversViewModel? _vm;

    public DriversHardwarePanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (IsLoaded) Sync(); };
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Release();
    }

    /// <summary>The view subscribes to its page view-model only while it is on screen: the view-model lives for the whole run, a subscription left behind would keep every closed copy of this view (and its rows) alive and still react to focus requests.</summary>
    private void Sync()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as DriversViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        if (_vm.HardwareFocusTarget is not null) ScrollToFocus();
    }

    private void Release()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DriversViewModel.HardwareFocusTarget) && _vm?.HardwareFocusTarget is not null) ScrollToFocus();
    }

    /// <summary>
    /// Brings the row a link asked for into view once the tab is laid out. Deferred (Background, twice: the tab may only now
    /// become visible) — never a layout pass inside a collection change.
    /// </summary>
    private void ScrollToFocus() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_vm?.HardwareFocusTarget is not { } target) return;
            if (FindRow(this, target) is { } row) row.BringIntoView(new Rect(0, 0, row.ActualWidth, Math.Min(row.ActualHeight, 200)));
            _vm.HardwareFocusTarget = null; // one-shot
        }));

    private static FrameworkElement? FindRow(DependencyObject parent, HwComponentItem target)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border { DataContext: var dc } border && ReferenceEquals(dc, target) && border.IsVisible) return border;
            if (FindRow(child, target) is { } nested) return nested;
        }
        return null;
    }
}
