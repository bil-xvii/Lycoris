using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Monolith.Modules.Startup;

public partial class StartupView : UserControl
{
    public StartupView()
    {
        InitializeComponent();
    }

    /// <summary>«ЕЩЁ ▾» on a row: opens its labelled menu under the button (open the folder, copy the command).</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
