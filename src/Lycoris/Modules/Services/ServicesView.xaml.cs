using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Lycoris.Modules.Services;

public partial class ServicesView : UserControl
{
    private ServicesViewModel? _vm;

    public ServicesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach(DataContext as ServicesViewModel);
        Unloaded += (_, _) => Attach(null);
        DataContextChanged += (_, e) =>
        {
            if (IsLoaded) Attach(e.NewValue as ServicesViewModel);
        };
    }

    private void Attach(ServicesViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = vm;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        // A focus request applied before this view was loaded (e.g. on navigation): scroll now.
        if (_vm.FocusedService is { } pending) ScrollTo(pending);
    }

    /// <summary>Focus request (palette / navigation): scroll ВСЕ СЛУЖБЫ to the selected service.</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServicesViewModel.FocusedService) && _vm?.FocusedService is { } item) ScrollTo(item);
    }

    private void ScrollTo(ServiceItem item)
    {
        void Scroll() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ServiceList.ScrollIntoView(item);
            // One-shot: consumed once the list has scrolled.
            if (_vm is not null && ReferenceEquals(_vm.FocusedService, item)) _vm.FocusedService = null;
        });

        // The tab may have just been switched: wait until the list is in the visual tree.
        if (ServiceList.IsLoaded)
        {
            Scroll();
            return;
        }
        RoutedEventHandler? onLoaded = null;
        onLoaded = (_, _) =>
        {
            ServiceList.Loaded -= onLoaded;
            Scroll();
        };
        ServiceList.Loaded += onLoaded;
    }
}
