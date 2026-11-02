using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Lycoris.Modules.Restore;

public partial class RestoreView : UserControl
{
    private RestoreViewModel? _vm;

    public RestoreView()
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
        _vm = DataContext as RestoreViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            if (_vm.FocusedSection is not null) ScrollToSection();
        }
    }

    private void Release()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RestoreViewModel.FocusedSection) && _vm?.FocusedSection is not null) ScrollToSection();
    }

    /// <summary>
    /// «section:&lt;name&gt;» (palette / other pages): brings that part of the page into view — the list scrolls to it
    /// (deferred: the batches are rebuilt first; never inside a CollectionChanged handler).
    /// </summary>
    private void ScrollToSection() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (_vm?.FocusedSection is not { } section) return;
        _vm.FocusedSection = null; // one-shot
        FrameworkElement? target = section switch
        {
            "last-batch" => Sec_last_batch,
            "restore-points" => Sec_restore_points,
            "drift" => Sec_last_batch,
            _ => null, // "journal": the toolbar above the list is always visible
        };
        if (target is null) return;
        UpdateLayout();
        target.BringIntoView();
    });
}
