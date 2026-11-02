using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Lycoris.Modules.Tools;

public partial class ToolsView : UserControl
{
    private ToolsViewModel? _vm;

    public ToolsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) =>
        {
            Attach(DataContext as ToolsViewModel);
            ScrollToEnd();
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Attach(e.NewValue as ToolsViewModel);

    private void Attach(ToolsViewModel? vm)
    {
        if (_vm is not null) _vm.ConsoleLines.CollectionChanged -= OnLinesChanged;
        _vm = vm;
        if (_vm is not null) _vm.ConsoleLines.CollectionChanged += OnLinesChanged;
    }

    /// <summary>Auto-scroll: keep the newest console line in view.</summary>
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace)
            Dispatcher.BeginInvoke(ScrollToEnd, DispatcherPriority.Background);
    }

    private void ScrollToEnd()
    {
        if (OutputList.Items.Count > 0) OutputList.ScrollIntoView(OutputList.Items[^1]);
    }
}
