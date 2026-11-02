using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Lycoris.Modules.Cleaner;

public partial class CleanerView : UserControl
{
    public CleanerView()
    {
        InitializeComponent();
    }

    /// <summary>A click anywhere on a row toggles it (the checkbox itself handles its own clicks).</summary>
    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        for (var d = e.OriginalSource as DependencyObject; d is not null && !ReferenceEquals(d, sender); d = ParentOf(d))
        {
            if (d is ButtonBase) return;
        }
        if ((sender as FrameworkElement)?.DataContext is CleanItem item && item.ToggleCommand.CanExecute(null))
        {
            item.ToggleCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
