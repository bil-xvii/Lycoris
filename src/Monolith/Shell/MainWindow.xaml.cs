using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Monolith.Shell;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        StateChanged += (_, _) => ApplyWindowState();
        SourceInitialized += (_, _) => ApplyDwmStyle();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage)) PlayPageEnter();
        };
        ((INotifyCollectionChanged)ConsoleList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && ConsoleList.Items.Count > 0)
                ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
        };
    }

    private void PlayPageEnter()
    {
        if (TryFindResource("PageEnter") is Storyboard sb)
            sb.Begin(PageHost);
    }

    private void ApplyWindowState()
    {
        if (WindowState == WindowState.Maximized)
        {
            // A chrome-less window overflows the screen by the resize border when maximized.
            var t = SystemParameters.WindowResizeBorderThickness;
            RootBorder.Margin = new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4);
            RootBorder.BorderThickness = new Thickness(0);
            MaxButton.Content = "";
            MaxButton.ToolTip = "Восстановить";
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            RootBorder.BorderThickness = new Thickness(1);
            MaxButton.Content = "";
            MaxButton.ToolTip = "Развернуть";
        }
    }

    /// <summary>Square corners and a dark hairline border on Windows 11.</summary>
    private void ApplyDwmStyle()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int corner = 1; // DWMWCP_DONOTROUND
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
            int border = 0x002B2B2B; // COLORREF 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch
        {
            // Older Windows: ignore.
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// QA helper: renders every page to PNG (used with --snapshot). Pages get <paramref name="waitMs"/> to load data.
    /// </summary>
    public async Task SnapshotPagesAsync(string outDir, IReadOnlyCollection<string>? only, int waitMs)
    {
        Directory.CreateDirectory(outDir);
        foreach (var page in _vm.Pages)
        {
            if (only is { Count: > 0 } && !only.Contains(page.Key)) continue;
            _vm.CurrentPage = page;
            await Task.Delay(waitMs);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var file = Path.Combine(outDir, $"{page.Index}-{page.Key}.png");
            SaveVisual(RootBorder, file);
        }
    }

    internal static void SaveVisual(FrameworkElement element, string file)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var w = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(9, 9, 9)), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
