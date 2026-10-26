using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Monolith.Modules.Optimize;

public partial class OptimizeView : UserControl
{
    private OptimizeViewModel? _vm;

    public OptimizeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as OptimizeViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                if (_vm.FocusedItem is not null) ScrollToFocused();
            }
        };
    }

    /// <summary>«ПРОФИЛЬ…» opens its export/import menu right under the button on a normal click.</summary>
    private void OnProfileMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OptimizeViewModel.FocusedItem) && _vm?.FocusedItem is not null) ScrollToFocused();
    }

    /// <summary>Brings the focused row into view once the list has laid out its rows (it is virtualized).</summary>
    private void ScrollToFocused() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        if (_vm?.FocusedItem is not { } item) return;
        var index = _vm.Rows.IndexOf(item);
        if (index < 0) return;
        if (FindChild<VirtualizingStackPanel>(RowsList) is { } panel) panel.BringIndexIntoViewPublic(index);
        RowsList.UpdateLayout();
        if (RowsList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container) container.BringIntoView();
    });

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
