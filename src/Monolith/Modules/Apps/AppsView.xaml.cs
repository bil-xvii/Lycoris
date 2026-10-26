using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Monolith.Modules.Apps;

public partial class AppsView : UserControl
{
    /// <summary>Visible only when every bound value is true (queue footer buttons).</summary>
    public static IMultiValueConverter AllTrueToVisibility { get; } = new AllTrueConverter();

    private AppsViewModel? _vm;

    public AppsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as AppsViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as AppsViewModel);
    }

    private void Attach(AppsViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null) _vm.QueueLog.CollectionChanged -= OnLogChanged;
        _vm = vm;
        if (_vm is not null) _vm.QueueLog.CollectionChanged += OnLogChanged;
    }

    /// <summary>
    /// The package list may take at most ~40 % of the queue panel, so the live winget output below it always has room
    /// (the shell console can leave the page with little height).
    /// </summary>
    private void QueuePanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double chrome = 190; // header, progress, log caption, footer
        QueueScroll.MaxHeight = Math.Max(96, (e.NewSize.Height - chrome) * 0.42);
    }

    /// <summary>Keeps the newest winget output line in view.</summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0) return;
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private sealed class AllTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
            values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }
}
