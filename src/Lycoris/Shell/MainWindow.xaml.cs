using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lycoris.Modules.Maintenance;
using Lycoris.Core.Localization;
using Lycoris.UI;

namespace Lycoris.Shell;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly CommandPaletteViewModel _palette;
    public WizardViewModel Wizard { get; }

    public async Task WizardDemoShotsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        Wizard.OpenDemo();
        for (var i = 1; i <= 3; i++)
        {
            Wizard.Step = i;
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateLayout();
                var language = Loc.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
                var size = $"{(int)ActualWidth}x{(int)ActualHeight}";
                SaveVisual(RootBorder, Path.Combine(directory, $"wizard-implemented-{language}-{size}-{i}.png"));
                if (language == "ru" && ActualWidth >= 1300 && ActualHeight >= 800)
                    SaveVisual(RootBorder, Path.Combine(directory, $"wizard-implemented-ru-{i}.png"));
            }, DispatcherPriority.Render);
        }
        Wizard.IsOpen = false;
    }

    public MainWindow(MainViewModel vm)
    {
        UiA11y.Install();
        InitializeComponent();
        _vm = vm;
        Wizard = new WizardViewModel(() => { }, _ => Ui.Navigate("home"));
        DataContext = vm;
        WizardLayer.DataContext = Wizard;
        vm.WizardRequested += Wizard.Open;
        FitToWorkArea();

        _palette = new CommandPaletteViewModel(() => CommandPaletteIndex.Build(_vm.Pages), action =>
        {
            var settings = _vm.Pages.OfType<Lycoris.Modules.Settings.SettingsViewModel>().FirstOrDefault();
            if (settings is not null) _ = settings.CreateProblemReportCommand.ExecuteAsync(null);
        });
        PaletteOverlay.DataContext = _palette;
        _palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.Selected)) ScrollPaletteSelectionIntoView();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        // The visible «ПОИСК» button in the title bar opens the same palette as Ctrl+K.
        vm.SearchRequested += () =>
        {
            if (_vm.Dialog is null && !_palette.IsOpen) OpenPalette();
        };

        StateChanged += (_, _) => ApplyWindowState();
        SourceInitialized += (_, _) => ApplyDwmStyle();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage)) PlayPageEnter();
        };
        // Scroll after the list has finished handling the change (never a layout pass inside CollectionChanged).
        ((INotifyCollectionChanged)ConsoleList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                Dispatcher.BeginInvoke(() =>
                {
                    if (ConsoleList.Items.Count > 0) ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
                }, DispatcherPriority.Background);
        };
    }

    private bool _wasMinimized;
    private bool _exitConfirmed;

    /// <summary>
    /// Closing with operations running (an install, a repair, a queued change): one dialog lists them — wait / cancel the safe
    /// ones and exit / exit anyway. Nothing is killed: installers and repair tools keep running on their own, the journal is
    /// saved by App.OnExit and the exit watchdog still ends this process 20 s after the window is gone.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitConfirmed && !Dispatcher.HasShutdownStarted && Lycoris.UI.Ui.AutoAnswer is null)
        {
            var plan = Lycoris.Core.ExitPlan.From(Lycoris.Core.OperationCoordinator.Instance.Snapshot());
            if (plan.NeedsDialog)
            {
                e.Cancel = true;
                _ = ConfirmExitAndCloseAsync();
                return;
            }
        }
        base.OnClosing(e);
    }

    private async Task ConfirmExitAndCloseAsync()
    {
        bool leave;
        try { leave = await _vm.ConfirmExitAsync(); }
        catch (Exception ex)
        {
            Lycoris.Core.Log.Warn("Диалог выхода не показан: {0}", ex.Message);
            leave = true;
        }
        if (!leave) return;
        _exitConfirmed = true;
        Close();
    }

    /// <summary>Laptops with 125–150 % scaling have ~1536×816 usable: never open larger than the screen.</summary>
    private void FitToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        if (wa.Width <= 0 || wa.Height <= 0) return;
        Width = Math.Min(Width, wa.Width - 32);
        Height = Math.Min(Height, wa.Height - 32);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
    }

    private void PlayPageEnter()
    {
        if (TryFindResource("PageEnter") is Storyboard sb)
            sb.Begin(PageHost);
    }

    private void ApplyWindowState()
    {
        // Minimized: stop live polling of the current page; restored: start it again.
        if (WindowState == WindowState.Minimized) _vm.CurrentPage?.SuspendForMinimize();
        else if (_wasMinimized) _vm.CurrentPage?.ResumeFromMinimize();
        _wasMinimized = WindowState == WindowState.Minimized;
        if (WindowState == WindowState.Maximized)
        {
            // A chrome-less window overflows the screen by the resize border when maximized.
            var t = SystemParameters.WindowResizeBorderThickness;
            RootBorder.Margin = new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4);
            RootBorder.BorderThickness = new Thickness(0);
            MaxButton.Content = "";
            MaxButton.ToolTip = Loc.T("Восстановить");
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            RootBorder.BorderThickness = new Thickness(1);
            MaxButton.Content = "";
            MaxButton.ToolTip = Loc.T("Развернуть");
        }
    }

    /// <summary>Rounded window corners (the system radius) and a dark hairline border on Windows 11; Windows 10 stays square.</summary>
    private void ApplyDwmStyle()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int corner = 2; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND
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

    // ───────────── Command palette ─────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Wizard.IsOpen) { Wizard.SkipCommand.Execute(null); e.Handled = true; return; }
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (_palette.IsOpen) ClosePalette();
            else if (_vm.Dialog is null) OpenPalette(); // a confirmation dialog always wins
            return;
        }
        if (!_palette.IsOpen || _vm.Dialog is not null) return;
        switch (e.Key)
        {
            case Key.Escape: ClosePalette(); e.Handled = true; break;
            case Key.Down: _palette.Move(1); e.Handled = true; break;
            case Key.Up: _palette.Move(-1); e.Handled = true; break;
            case Key.Enter: _palette.OpenSelected(); e.Handled = true; break;
            case Key.Tab: e.Handled = true; break; // keep focus in the search box
        }
    }

    private void OpenPalette()
    {
        _palette.Open();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            PaletteBox.Focus();
            Keyboard.Focus(PaletteBox);
        });
    }

    private void ClosePalette() => _palette.Close();

    private void ScrollPaletteSelectionIntoView()
    {
        if (_palette.Selected is not { } item) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (PaletteList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement fe) fe.BringIntoView();
            else if (_palette.Results.IndexOf(item) == 0) PaletteScroll.ScrollToTop();
        });
    }

    /// <summary>Click on the dimmed backdrop closes the palette.</summary>
    private void PaletteOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PaletteOverlay)) ClosePalette();
    }

    private void PaletteRow_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PaletteResultItem item)
        {
            _palette.Selected = item;
            _palette.OpenSelected();
            e.Handled = true;
        }
    }

    /// <summary>«ОТКАТ · N ▾»: a left click opens the dropdown under the button (a context menu placed at the bottom).</summary>
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// QA helper: renders every page to PNG (used with --snapshot). Pages get <paramref name="waitMs"/> to load data.
    /// With <paramref name="full"/> the page's longest scrolled list is also saved whole ("…-full.png").
    /// </summary>
    public async Task SnapshotPagesAsync(string outDir, IReadOnlyCollection<string>? only, int waitMs, bool full = false)
    {
        Directory.CreateDirectory(outDir);
        Lycoris.Modules.Devices.DevicesViewModel.RememberTab = false; // shooting both tabs must not change the user's last tab
        var n = 0;
        foreach (var page in _vm.Pages)
        {
            n++;
            // Host pages are shot once per tab ("05-maintenance-cleaner.png", "08-devices-manual.png"); a tab key in --pages
            // ("cleaner", "devices-manual") selects that tab only.
            var tabs = page switch
            {
                MaintenanceViewModel host => host.Tabs.Select(t => t.Key).ToList(),
                Lycoris.Modules.Devices.DevicesViewModel => Lycoris.Modules.Devices.DevicesViewModel.TabKeys.ToList(),
                _ => [],
            };
            if (tabs.Count == 0) tabs.Add("");
            foreach (var tab in tabs)
            {
                if (only is { Count: > 0 } && !only.Contains(page.Key) && !(tab.Length > 0 && (only.Contains(tab) || only.Contains($"{page.Key}-{tab}"))))
                    continue;
                if (page is MaintenanceViewModel m) m.SelectTab(tab);
                if (page is Lycoris.Modules.Devices.DevicesViewModel dev) dev.SelectedTab = tab;
                _vm.CurrentPage = page;
                await Task.Delay(waitMs);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                var file = Path.Combine(outDir, $"{n:00}-{page.Key}{(tab.Length > 0 ? "-" + tab : "")}.png");
                SaveVisual(RootBorder, file);
                if (full && LongestScrolledContent(RootBorder) is { } content)
                    SaveVisual(content, Path.ChangeExtension(file, null) + "-full.png");
            }
        }
    }

    /// <summary>
    /// QA helper ("--snapshot … --demo-shell"): renders the shell's own surfaces that no page shows — toasts, the command palette,
    /// the confirmation / three-button / elevation dialogs, the task list — so their layout can be checked in each language
    /// (add "--lang en"). Nothing is applied: the dialogs are answered «cancel».
    /// </summary>
    public async Task ShellDemoShotsAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        _vm.NavigateTo("dashboard");
        await Task.Delay(1500);

        Ui.Toast("Начат новый журнал изменений.", ToastKind.Success);
        Ui.Toast("Эта страница уже выполняет задачу — повтор действия не нужен. Проверьте список «ЗАДАЧИ».", ToastKind.Info, "ПРОДОЛЖИТЬ", navigateTo: "restore");
        Ui.Toast("Старые файлы журнала не удалось отложить. Подробности — в консоли Lycoris.", ToastKind.Error, "НЕ УДАЛОСЬ НАЧАТЬ НОВЫЙ ЖУРНАЛ");
        await Task.Delay(700);
        SaveVisual(RootBorder, Path.Combine(outDir, "shell-1-toasts.png"));
        _vm.Toasts.Clear();

        OpenPalette();
        await Task.Delay(500);
        SaveVisual(RootBorder, Path.Combine(outDir, "shell-2-palette-empty.png"));
        _palette.Query = "set";
        await Task.Delay(500);
        SaveVisual(RootBorder, Path.Combine(outDir, "shell-3-palette-set.png"));
        _palette.Query = "диск"; // loc:ignore (a Russian query must keep working in the English UI)
        await Task.Delay(500);
        SaveVisual(RootBorder, Path.Combine(outDir, "shell-4-palette-russian-query.png"));
        ClosePalette();

        var reset = Ui.ConfirmAsync("Сбросить настройки?", "Параметры Lycoris вернутся к значениям по умолчанию.",
            Lycoris.Modules.Settings.SettingsDefaults.Describe(), confirmText: "СБРОСИТЬ");
        await Task.Delay(600);
        SaveVisual(RootBorder, Path.Combine(outDir, "shell-5-dialog-confirm.png"));
        _vm.Dialog?.CancelCommand.Execute(null);
        await reset;

        var rights = Ui.ConfirmOptionAsync("Нужны права администратора", PageViewModel.ElevationPromptText(Loc.T("Применение оптимизаций"), true),
            PageViewModel.ElevationAlwaysOption, confirmText: "ПЕРЕЗАПУСТИТЬ С ПРАВАМИ", cancelText: "НЕ СЕЙЧАС");
        await Task.Delay(600);
        SaveVisual(RootBorder, Path.Combine(outDir, "shell-6-dialog-admin.png"));
        _vm.Dialog?.CancelCommand.Execute(null);
        await rights;

        await _vm.StartDemoTasksAsync();
        await Task.Delay(500);
        SaveVisual(RootBorder, Path.Combine(outDir, "shell-7-tasks.png"));
        var exit = _vm.ConfirmExitAsync();
        await Task.Delay(600);
        SaveVisual(RootBorder, Path.Combine(outDir, "shell-8-dialog-exit.png"));
        _vm.Dialog?.CancelCommand.Execute(null);
        await exit;

        await Ui.AlertAsync("ЖУРНАЛ НЕДОСТУПЕН", "Старые файлы журнала не удалось отложить. Подробности — в консоли Lycoris.")
            .WaitAsync(TimeSpan.FromMilliseconds(700)).ContinueWith(_ => { });
        SaveVisual(RootBorder, Path.Combine(outDir, "shell-9-dialog-alert.png"));
        _vm.Dialog?.ConfirmCommand.Execute(null);
    }

    /// <summary>The content of the visible ScrollViewer that hides the most (the page's main list), or null when nothing scrolls.</summary>
    private static FrameworkElement? LongestScrolledContent(DependencyObject root)
    {
        System.Windows.Controls.ScrollViewer? best = null;
        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is System.Windows.Controls.ScrollViewer { IsVisible: true } sv && sv.ExtentHeight - sv.ViewportHeight > 1
                && (best is null || sv.ExtentHeight - sv.ViewportHeight > best.ExtentHeight - best.ViewportHeight))
                best = sv;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
        return best?.Content as FrameworkElement;
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
