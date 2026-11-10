using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace Lycoris.Modules.Assistant;

public partial class AssistantView : UserControl
{
    public AssistantView()
    {
        InitializeComponent();
        // The newest message stays in view while the answer streams in, unless the user scrolled up to read.
        Conversation.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged && Scroll.VerticalOffset + Scroll.ViewportHeight >= e.PreviousSize.Height - 40) Scroll.ScrollToEnd();
        };
        ((INotifyCollectionChanged)Conversation.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add) Dispatcher.BeginInvoke(Scroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) Input.Focus(); };
    }
}
