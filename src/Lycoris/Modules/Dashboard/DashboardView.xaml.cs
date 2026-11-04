using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lycoris.Modules.Dashboard;

public partial class DashboardView : UserControl
{
    private INotifyPropertyChanged? _vm;

    public DashboardView()
    {
        InitializeComponent();
        // Subscribe only while loaded: the page VM outlives this view, so a permanent handler would keep the view alive.
        Loaded += (_, _) => Attach(DataContext);
        Unloaded += (_, _) => Attach(null);
        DataContextChanged += (_, e) => { if (IsLoaded) Attach(e.NewValue); };
        DoctorCard.IsVisibleChanged += OnDoctorCardVisibleChanged;
    }

    private void Attach(object? dataContext)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = dataContext as INotifyPropertyChanged;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DashboardViewModel.DoctorStatus)) return;
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        // After the binding has pushed the new text into the TextBlock, tell screen readers the live region changed.
        Dispatcher.InvokeAsync(() =>
            UIElementAutomationPeer.FromElement(DoctorStatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged), DispatcherPriority.Background);
    }

    /// <summary>«ЗАКРЫТЬ» hides the card: keyboard focus returns to «ПОЧЕМУ ТОРМОЗИТ?» instead of falling to the window.</summary>
    private void OnDoctorCardVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true || !IsVisible) return; // the whole page hiding (navigation) is not «closed»
        var focusedInside = DoctorCard.IsKeyboardFocusWithin
                            || (Keyboard.FocusedElement is Visual v && v.IsDescendantOf(DoctorCard));
        if (focusedInside && FindByAutomationId(this, "DiagnoseButton") is { } button) Keyboard.Focus(button);
    }

    /// <summary>The header button lives inside PageHeader's own name scope (x:Name is not allowed there): found by its automation id.</summary>
    private static IInputElement? FindByAutomationId(DependencyObject root, string id)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement e && System.Windows.Automation.AutomationProperties.GetAutomationId(e) == id) return e;
            if (FindByAutomationId(child, id) is { } found) return found;
        }
        return null;
    }
}
