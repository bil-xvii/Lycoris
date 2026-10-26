using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Shell;

/// <summary>
/// "--selftest &lt;dir&gt;": opens every page, exercises tabs, filters, radio groups, expanders, search boxes and
/// scrolling, and records crashes, error log entries, WPF binding errors and UI freezes. Writes report.md/json
/// plus a screenshot per page. It is harmless by construction: no admin rights, every confirmation is
/// answered "Cancel", buttons are never pressed, and all system-changing APIs are locked.
/// </summary>
public sealed class SelfTestRunner
{
    private sealed class PageReport
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public long LoadMs { get; set; }
        public double MaxUiLagMs { get; set; }
        public List<string> Errors { get; set; } = [];
        public List<string> Warnings { get; set; } = [];
        public List<string> BindingErrors { get; set; } = [];
        public List<string> Unhandled { get; set; } = [];
        public List<string> Dialogs { get; set; } = [];
        public List<string> Exercised { get; set; } = [];
        public bool StillBusy { get; set; }
        public string Status => Unhandled.Count > 0 || Errors.Count > 0 || BindingErrors.Count > 0 || MaxUiLagMs > 1500 || StillBusy
            ? "FAIL" : Warnings.Count > 0 ? "WARN" : "OK";
    }

    private sealed class BindingListener : TraceListener
    {
        public readonly ConcurrentQueue<string> Messages = new();
        private readonly StringBuilder _line = new();
        public override void Write(string? message) { lock (_line) _line.Append(message); }
        public override void WriteLine(string? message)
        {
            lock (_line)
            {
                _line.Append(message);
                var text = _line.ToString();
                _line.Clear();
                if (text.Contains("Error", StringComparison.OrdinalIgnoreCase)) Messages.Enqueue(Shorten(text));
            }
        }
    }

    private static readonly ConcurrentQueue<string> UnhandledQueue = new();
    public static bool IsRunning { get; private set; }
    public static void ReportUnhandled(Exception ex) => UnhandledQueue.Enqueue(ex.GetType().Name + ": " + ex.Message);

    private readonly MainWindow _window;
    private readonly MainViewModel _vm;
    private readonly string _outDir;
    private readonly ConcurrentQueue<LogEntry> _log = new();
    private readonly BindingListener _bindings = new();
    private double _currentMaxLag;
    private volatile bool _lagRunning;

    public SelfTestRunner(MainWindow window, MainViewModel vm, string outDir)
    {
        _window = window;
        _vm = vm;
        _outDir = outDir;
    }

    public async Task<int> RunAsync()
    {
        IsRunning = true;
        Directory.CreateDirectory(_outDir);
        ChangeSession.Locked = true;
        Ui.AutoAnswer = false;
        var settingsBackup = File.Exists(AppPaths.SettingsFile) ? await File.ReadAllTextAsync(AppPaths.SettingsFile) : null;

        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(_bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        Log.Written += OnLog;
        var lagThread = StartLagMonitor();

        var reports = new List<PageReport>();
        var total = Stopwatch.StartNew();
        var startMem = Process.GetCurrentProcess().WorkingSet64;

        foreach (var page in _vm.Pages)
        {
            var r = new PageReport { Key = page.Key, Title = page.Title };
            Drain();
            var sw = Stopwatch.StartNew();
            _vm.CurrentPage = page;
            await WaitIdleAsync(page, TimeSpan.FromSeconds(60));
            r.LoadMs = sw.ElapsedMilliseconds;

            await ExerciseAsync(page, r);
            await WaitIdleAsync(page, TimeSpan.FromSeconds(90));
            r.StillBusy = page.IsBusy;

            try { MainWindow.SaveVisual(_window.RootBorder, Path.Combine(_outDir, $"{page.Index}-{page.Key}.png")); }
            catch (Exception ex) { r.Warnings.Add("screenshot: " + ex.Message); }

            Collect(r);
            reports.Add(r);
        }

        // Stress: fast navigation back and forth (catches races between page loads)
        var stress = new PageReport { Key = "stress", Title = "БЫСТРАЯ НАВИГАЦИЯ" };
        Drain();
        for (var round = 0; round < 3; round++)
        {
            foreach (var page in _vm.Pages)
            {
                _vm.CurrentPage = page;
                await Task.Delay(120);
            }
        }
        _vm.CurrentPage = _vm.Pages[0];
        foreach (var p in _vm.Pages) await WaitIdleAsync(p, TimeSpan.FromSeconds(60));
        await Task.Delay(1500);
        Collect(stress);
        reports.Add(stress);

        _lagRunning = false;
        lagThread.Join(2000);
        Log.Written -= OnLog;
        PresentationTraceSources.DataBindingSource.Listeners.Remove(_bindings);
        Ui.AutoAnswer = null;

        var endMem = Process.GetCurrentProcess().WorkingSet64;
        WriteReport(reports, total.Elapsed, startMem, endMem);

        if (settingsBackup is not null)
        {
            try { await File.WriteAllTextAsync(AppPaths.SettingsFile, settingsBackup); } catch { }
        }
        IsRunning = false;
        return reports.Count(r => r.Status == "FAIL");
    }

    private void OnLog(LogEntry e)
    {
        if (e.Level is LogLevel.Error or LogLevel.Warning) _log.Enqueue(e);
    }

    private void Drain()
    {
        while (_log.TryDequeue(out _)) { }
        while (_bindings.Messages.TryDequeue(out _)) { }
        while (UnhandledQueue.TryDequeue(out _)) { }
        lock (Ui.AutoAnswered) Ui.AutoAnswered.Clear();
        _currentMaxLag = 0;
    }

    private void Collect(PageReport r)
    {
        while (_log.TryDequeue(out var e))
        {
            // Expected in self-test: actions blocked because there are no admin rights / the lock.
            if (e.Message.Contains("самопроверки", StringComparison.OrdinalIgnoreCase)) { r.Warnings.Add(e.Message); continue; }
            if (e.Level == LogLevel.Error) r.Errors.Add(e.Message); else r.Warnings.Add(e.Message);
        }
        while (_bindings.Messages.TryDequeue(out var b)) if (!r.BindingErrors.Contains(b)) r.BindingErrors.Add(b);
        while (UnhandledQueue.TryDequeue(out var u)) r.Unhandled.Add(u);
        lock (Ui.AutoAnswered) { r.Dialogs.AddRange(Ui.AutoAnswered); Ui.AutoAnswered.Clear(); }
        r.MaxUiLagMs = Math.Round(_currentMaxLag);
    }

    private Thread StartLagMonitor()
    {
        _lagRunning = true;
        var dispatcher = _window.Dispatcher;
        var t = new Thread(() =>
        {
            while (_lagRunning)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var op = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                    op.Wait(TimeSpan.FromSeconds(10));
                }
                catch { }
                var ms = sw.Elapsed.TotalMilliseconds;
                if (ms > _currentMaxLag) _currentMaxLag = ms;
                Thread.Sleep(100);
            }
        }) { IsBackground = true, Name = "selftest-lag" };
        t.Start();
        return t;
    }

    private static async Task WaitIdleAsync(PageViewModel page, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var idleSince = page.IsBusy ? (TimeSpan?)null : sw.Elapsed;
        while (sw.Elapsed < timeout)
        {
            await Task.Delay(250);
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (page.IsBusy) idleSince = null;
            else idleSince ??= sw.Elapsed;
            if (idleSince is { } s && sw.Elapsed - s > TimeSpan.FromSeconds(1.5)) return;
        }
    }

    private async Task ExerciseAsync(PageViewModel page, PageReport r)
    {
        // Tabs
        foreach (var tabs in Find<TabControl>().ToList())
        {
            var original = tabs.SelectedIndex;
            for (var i = 0; i < tabs.Items.Count; i++)
            {
                if (tabs.Items[i] is TabItem { IsEnabled: false }) continue;
                tabs.SelectedIndex = i;
                r.Exercised.Add($"tab {i}");
                await SettleAsync(page, 45);
                await ExerciseSurfaceAsync(page, r);
            }
            tabs.SelectedIndex = original;
            await SettleAsync(page, 10);
        }
        await ExerciseSurfaceAsync(page, r);
    }

    /// <summary>Radio groups, expanders, search boxes, list checkboxes and scrolling in the visible content.</summary>
    private async Task ExerciseSurfaceAsync(PageViewModel page, PageReport r)
    {
        // Radio buttons (filters, profile tiles): click each, then restore the original choice.
        var radios = Find<RadioButton>().Where(x => x.IsVisible && x.IsEnabled).ToList();
        var originallyChecked = radios.Where(x => x.IsChecked == true).ToList();
        foreach (var rb in radios)
        {
            rb.IsChecked = true;
            r.Exercised.Add("radio " + Describe(rb));
            await SettleAsync(page, 15);
        }
        foreach (var rb in originallyChecked) rb.IsChecked = true;
        await SettleAsync(page, 10);

        foreach (var ex in Find<Expander>().Where(x => x.IsVisible).Take(20).ToList())
        {
            var was = ex.IsExpanded;
            ex.IsExpanded = !was;
            await SettleAsync(page, 5);
            ex.IsExpanded = was;
        }

        // Search boxes: type, wait for filtering, restore.
        foreach (var tb in Find<TextBox>().Where(x => x.IsVisible && x.IsEnabled && !x.IsReadOnly).ToList())
        {
            var original = tb.Text;
            tb.Text = "a";
            await SettleAsync(page, 5);
            tb.Text = "zzzz-нет-такого";
            await SettleAsync(page, 5);
            tb.Text = original;
            r.Exercised.Add("textbox " + Describe(tb));
            await SettleAsync(page, 5);
        }

        // Plain checkboxes in lists (selection only — toggles bound to actions use the Toggle style and are skipped).
        var toggleStyle = Application.Current.TryFindResource("Toggle") as Style;
        foreach (var cb in Find<CheckBox>().Where(x => x.IsVisible && x.IsEnabled && x.Style != toggleStyle).Take(12).ToList())
        {
            var was = cb.IsChecked;
            cb.IsChecked = !(was ?? false);
            await SettleAsync(page, 3);
            cb.IsChecked = was;
            await SettleAsync(page, 3);
        }

        foreach (var sv in Find<ScrollViewer>().Where(x => x.IsVisible && x.ScrollableHeight > 0).ToList())
        {
            sv.ScrollToEnd();
            await SettleAsync(page, 3);
            sv.ScrollToHome();
            await SettleAsync(page, 3);
        }
    }

    private static async Task SettleAsync(PageViewModel page, int maxSeconds)
    {
        await Task.Delay(350);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var sw = Stopwatch.StartNew();
        while (page.IsBusy && sw.Elapsed.TotalSeconds < maxSeconds) await Task.Delay(200);
    }

    private IEnumerable<T> Find<T>() where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(_window.PageHost);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is T t) yield return t;
            var n = VisualTreeHelper.GetChildrenCount(d);
            for (var i = 0; i < n; i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    private static string Describe(FrameworkElement e) =>
        e is ContentControl { Content: string s } ? s : !string.IsNullOrEmpty(e.Name) ? e.Name : e.GetType().Name;

    private static string Shorten(string s) => s.Length > 400 ? s[..400] + "…" : s;

    private void WriteReport(List<PageReport> reports, TimeSpan elapsed, long startMem, long endMem)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MONOLITH — отчёт самопроверки");
        sb.AppendLine();
        sb.AppendLine($"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss} · длительность {elapsed.TotalSeconds:0} с · права администратора: {(Elevation.IsElevated ? "да" : "нет")}");
        sb.AppendLine($"Память: {startMem / 1048576} МБ → {endMem / 1048576} МБ");
        sb.AppendLine();
        sb.AppendLine("| Страница | Статус | Загрузка, мс | Макс. задержка UI, мс | Ошибки | Привязки | Падения | Диалоги |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in reports)
            sb.AppendLine($"| {r.Key} | {r.Status} | {r.LoadMs} | {r.MaxUiLagMs} | {r.Errors.Count} | {r.BindingErrors.Count} | {r.Unhandled.Count} | {r.Dialogs.Count} |");
        sb.AppendLine();
        foreach (var r in reports.Where(x => x.Status != "OK"))
        {
            sb.AppendLine($"## {r.Key} — {r.Status}");
            if (r.StillBusy) sb.AppendLine("- страница всё ещё занята после 90 с");
            foreach (var e in r.Unhandled) sb.AppendLine($"- ПАДЕНИЕ: {e}");
            foreach (var e in r.Errors) sb.AppendLine($"- ОШИБКА: {e}");
            foreach (var e in r.BindingErrors) sb.AppendLine($"- ПРИВЯЗКА: {e}");
            foreach (var e in r.Warnings.Distinct().Take(15)) sb.AppendLine($"- предупреждение: {e}");
            if (r.MaxUiLagMs > 1500) sb.AppendLine($"- интерфейс зависал на {r.MaxUiLagMs} мс");
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(_outDir, "report.md"), sb.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_outDir, "report.json"),
            JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));
    }
}
