using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Monolith.Modules.Optimize;

public partial class OptimizeView : UserControl
{
    public OptimizeView()
    {
        InitializeComponent();
    }

    /// <summary>«ПРОФИЛЬ…» opens its export/import menu right under the button on a normal click.</summary>
    private void OnProfileMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}