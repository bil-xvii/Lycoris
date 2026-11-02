using System.Collections.Specialized;
using System.Windows.Controls;

namespace Monolith.Modules.Drivers;

public partial class DriversUpdatesPanel : UserControl
{
    public DriversUpdatesPanel() => InitializeComponent();
}

public partial class DriversProblemsPanel : UserControl
{
    public DriversProblemsPanel() => InitializeComponent();
}

public partial class DriversInstalledPanel : UserControl
{
    public DriversInstalledPanel()
    {
        InitializeComponent();
        // Keep the newest pnputil line in view while output streams in.
        ((INotifyCollectionChanged)OutputList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                Dispatcher.BeginInvoke(() =>
                {
                    if (OutputList.Items.Count > 0) OutputList.ScrollIntoView(OutputList.Items[^1]);
                }, System.Windows.Threading.DispatcherPriority.Background);
        };
    }
}
