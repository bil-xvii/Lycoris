using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lycoris.Modules.Optimize;

public partial class OptimizeView : UserControl
{
    private OptimizeViewModel? _vm;

    /// <summary>
    /// Page height (the view itself, without the shell's title bar and status bar) below which the profile tiles drop their
    /// description, second counter line and note, so the list keeps room in a short window (the window's minimum is 720).
    /// </summary>
    internal const double CompactBelowHeight = 700;

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(OptimizeView), new PropertyMetadata(false));

    /// <summary>True while the page is short: the profile tiles show only the title, «N к изменению» and their button.</summary>
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    /// <summary>Narrowest profile tile that still shows «Рекомендуемые» and its «ПО УМОЛЧАНИЮ» mark whole (1024 px screen, 10.11).</summary>
    internal const double MinTileWidth = 128;

    public static readonly DependencyProperty TileColumnsProperty =
        DependencyProperty.Register(nameof(TileColumns), typeof(int), typeof(OptimizeView), new PropertyMetadata(6));

    /// <summary>Profile tiles per row: all of them in one row, or half per row when a tile would be too narrow.</summary>
    public int TileColumns { get => (int)GetValue(TileColumnsProperty); set => SetValue(TileColumnsProperty, value); }

    internal static int ColumnsFor(double width, int count) =>
        count <= 1 || width / count >= MinTileWidth ? Math.Max(count, 1) : (count + 1) / 2;

    private void ProfileTiles_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var columns = ColumnsFor(e.NewSize.Width, ProfileTiles.Items.Count);
        if (columns != TileColumns) TileColumns = columns;
    }

    public OptimizeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (IsLoaded) Sync(); };
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Release();
        SizeChanged += (_, e) => IsCompact = e.NewSize.Height < CompactBelowHeight;
    }

    /// <summary>The view subscribes to its page view-model only while it is on screen: the view-model lives for the whole run, a subscription left behind would keep every closed copy of this view (and its rows) alive and still react to focus requests.</summary>
    private void Sync()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as OptimizeViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            if (_vm.FocusedItem is not null) ScrollToFocused();
        }
    }

    private void Release()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
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
        _vm.FocusedItem = null; // one-shot: later plain visits to the page must not jump to this row again
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
