using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Monolith.Modules.Settings;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _vm;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as SettingsViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
                if (_vm.FocusedRejectedId is not null) ScrollToFocusedRejected();
            }
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.FocusedRejectedId) && _vm?.FocusedRejectedId is not null) ScrollToFocusedRejected();
    }

    /// <summary>
    /// Brings the requested «Что MONOLITH не делает» row into view once the expander has opened and laid out its rows
    /// (deferred at Background priority; never inside a CollectionChanged handler).
    /// </summary>
    private void ScrollToFocusedRejected() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (_vm?.FocusedRejectedId is not { } id) return;
        _vm.FocusedRejectedId = null; // one-shot: later plain visits to the page must not jump to this row again
        var row = _vm.Rejected.FirstOrDefault(r => r.Id == id);
        if (row is null) return;
        RejectedList.UpdateLayout();
        if (RejectedList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container) container.BringIntoView();
    });
}
