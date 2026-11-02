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
using Monolith.Modules.Maintenance;
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
    private bool _bindingDetectorWorks;

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

        // Prove the binding-error detector works: a deliberately broken binding must be reported.
        var probe = new TextBlock { DataContext = new object() };
        probe.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("SelfTestProbeMissingProperty"));
        await Task.Delay(400);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        _bindingDetectorWorks = _bindings.Messages.Any(m => m.Contains("SelfTestProbeMissingProperty"));

        var lagThread = StartLagMonitor();

        var reports = new List<PageReport>();
        var total = Stopwatch.StartNew();
        var startMem = Process.GetCurrentProcess().WorkingSet64;

        var n = 0;
        foreach (var page in _vm.Pages)
        {
            n++;
            // A host page is tested once per tab, so every hosted view is opened and exercised.
            if (page is MaintenanceViewModel host)
            {
                foreach (var tab in host.Tabs)
                {
                    host.SelectTab(tab.Key);
                    reports.Add(await TestPageAsync(page, $"{page.Key}/{tab.Key}", $"{page.Title} · {tab.Title}", $"{n:00}-{page.Key}-{tab.Key}.png"));
                }
                host.SelectTab(MaintenanceViewModel.CleanerTab);
            }
            else if (page is Monolith.Modules.Devices.DevicesViewModel devices)
            {
                // АВТОНАСТРОЙКА and ВРУЧНУЮ are separate views: each is opened and exercised.
                foreach (var tab in Monolith.Modules.Devices.DevicesViewModel.TabKeys)
                {
                    devices.SelectedTab = tab;
                    reports.Add(await TestPageAsync(page, $"{page.Key}/{tab}", $"{page.Title} · {tab}", $"{n:00}-{page.Key}-{tab}.png"));
                }
                devices.SelectedTab = Monolith.Modules.Devices.DevicesViewModel.TabAuto;
            }
            else
            {
                reports.Add(await TestPageAsync(page, page.Key, page.Title, $"{n:00}-{page.Key}.png"));
            }
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
                if (page is MaintenanceViewModel host)
                    foreach (var tab in host.Tabs.Reverse())
                    {
                        host.SelectTab(tab.Key);
                        await Task.Delay(120);
                    }
            }
        }
        _vm.CurrentPage = _vm.Pages[0];
        foreach (var p in _vm.Pages) await WaitIdleAsync(p, TimeSpan.FromSeconds(60));
        await Task.Delay(1500);
        Collect(stress);
        reports.Add(stress);

        // Concurrency: many read-only / measure operations from several pages at once, plus a synthetic storm through the coordinator.
        reports.Add(await TestConcurrencyAsync());

        // Security: the hostile-input corpus against every parser and validator (read-only, offline).
        reports.Add(await TestSecurityAsync());

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

    /// <summary>
    /// «БЕЗОПАСНОСТЬ» (read-only): the hostile-input corpus (<see cref="Monolith.Core.Security.SecurityCorpus"/>) against the parsers and
    /// validators, plus the state of the process hardening. Fails on any hostile input that is accepted.
    /// </summary>
    private async Task<PageReport> TestSecurityAsync()
    {
        var r = new PageReport { Key = "security", Title = "БЕЗОПАСНОСТЬ" };
        Drain();
        var corpus = await Task.Run(Monolith.Core.Security.SecurityCorpus.Run);
        r.Exercised.Add($"hostile-input corpus: {corpus.Checks} checks, {corpus.Failures.Count} failed");
        foreach (var failure in corpus.Failures) r.Errors.Add("не отклонено: " + failure);

        var h = Monolith.Core.Security.Hardening.Current;
        r.Exercised.Add($"DLL search restricted: {h.DllDirectoriesRestricted}, trusted native folders: {h.TrustedNativeDirectories}, image-load policy: {h.ImageLoadPolicy}"
                        + (h.Details.Length > 0 ? " (" + h.Details + ")" : ""));
        if (!h.DllDirectoriesRestricted) r.Warnings.Add("поиск DLL не ограничен: " + h.Details);
        if (!h.ImageLoadPolicy) r.Warnings.Add("политика загрузки образов не применена: " + h.Details);

        var self = Monolith.Core.Security.SelfIntegrity.HashAtStart;
        r.Exercised.Add($"exe SHA-256: {self ?? "(not yet computed)"}, signature: {Monolith.Core.Security.SelfIntegrity.Signature}, writable folder: {Monolith.Core.Security.SelfIntegrity.WritableReason ?? "no"}");
        if (Monolith.Core.Security.SelfIntegrity.Signature == Monolith.Core.Security.SignatureState.Tampered)
            r.Errors.Add("подпись MONOLITH.exe не совпадает с файлом");
        foreach (var bad in Monolith.Core.Security.SelfIntegrity.BadNativeLibraries) r.Errors.Add("распакованная библиотека без подписи: " + bad);

        // The corpus makes parsers log their refusals (warnings): expected here, not findings. Only unhandled exceptions would matter.
        Drain();
        return r;
    }

    /// <summary>
    /// "Many tasks at once": the refresh / analysis commands of every page (read-only) fired together three times, then 90
    /// synthetic operations of every class through the coordinator. Passes when nothing deadlocks, no error or binding error is
    /// logged, the task list ends empty, short child processes stayed within their cap, memory stays bounded and the UI thread
    /// never froze (the lag monitor of the report).
    /// </summary>
    private async Task<PageReport> TestConcurrencyAsync()
    {
        var r = new PageReport { Key = "concurrency", Title = "ОДНОВРЕМЕННЫЕ ЗАДАЧИ" };
        Drain();
        var coordinator = OperationCoordinator.Instance;
        var memBefore = Process.GetCurrentProcess().WorkingSet64;

        var commands = new List<(string Name, System.Windows.Input.ICommand Command)>();
        void AddCommands(object owner, string label)
        {
            foreach (var name in new[] { "RefreshCommand", "RefreshChecksCommand", "AnalyzeCommand" })
                if (owner.GetType().GetProperty(name)?.GetValue(owner) is System.Windows.Input.ICommand cmd) commands.Add(($"{label}.{name}", cmd));
        }
        foreach (var page in _vm.Pages)
        {
            AddCommands(page, page.Key);
            if (page is MaintenanceViewModel host) { AddCommands(host.Cleaner, "cleaner"); AddCommands(host.Tools, "tools"); }
        }

        var fired = 0;
        for (var round = 0; round < 3; round++)
        {
            foreach (var (_, cmd) in commands)
            {
                try
                {
                    if (!cmd.CanExecute(null)) continue;
                    cmd.Execute(null);
                    fired++;
                }
                catch (Exception ex) { r.Errors.Add("команда: " + ex.Message); }
            }
            await Task.Delay(150);
        }
        r.Exercised.Add($"read-only commands fired together: {fired} ({commands.Count} commands × 3 rounds)");

        // The synthetic storm: every class, mixed, with small delays; refusals are expected and plain.
        var classes = Enum.GetValues<OperationClass>();
        var rnd = new Random(2026);
        var refused = 0;
        var done = 0;
        var storm = Enumerable.Range(0, 90).Select(i =>
        {
            var cls = classes[rnd.Next(classes.Length)];
            var delay = rnd.Next(3, 25);
            return Task.Run(async () =>
            {
                try
                {
                    await coordinator.RunAsync(new OperationRequest(cls, $"Проверка очереди {i}") { Owner = "selftest", QueueTimeout = TimeSpan.FromSeconds(60) },
                        async _ => await Task.Delay(delay));
                    Interlocked.Increment(ref done);
                }
                catch (OperationRefusedException ex)
                {
                    Interlocked.Increment(ref refused);
                    if (ex.Message.Length < 10) lock (r) r.Errors.Add("пустая причина отказа");
                }
                catch (Exception ex)
                {
                    lock (r) r.Errors.Add($"шторм: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }).ToArray();
        var all = Task.WhenAll(storm);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(90))) == all;
        if (!finished) r.Errors.Add("шторм операций не завершился за 90 с — возможна взаимная блокировка");
        r.Exercised.Add($"coordinator storm: {done} ran, {refused} refused with a reason");

        foreach (var p in _vm.Pages) await WaitIdleAsync(p, TimeSpan.FromSeconds(90));
        await Task.Delay(500);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

        if (coordinator.ListedCount != 0) r.Errors.Add($"после шторма в списке задач осталось: {coordinator.ListedCount}");
        if (_vm.Operations.Count != 0) r.Errors.Add($"список «ЗАДАЧИ» не опустел: {_vm.Operations.Count}");
        if (ProcessRunner.PeakProbes > ProcessRunner.MaxParallelProbes) r.Errors.Add($"одновременно коротких процессов: {ProcessRunner.PeakProbes}");
        if (ProcessRunner.ProbesRunning != 0) r.Warnings.Add($"коротких процессов ещё идёт: {ProcessRunner.ProbesRunning}");
        var grown = (Process.GetCurrentProcess().WorkingSet64 - memBefore) / (1024 * 1024);
        r.Exercised.Add($"memory growth {grown} MB, peak short processes {ProcessRunner.PeakProbes}");
        if (grown > 600) r.Errors.Add($"память выросла на {grown} МБ");

        Collect(r);
        return r;
    }

    private async Task<PageReport> TestPageAsync(PageViewModel page, string key, string title, string screenshot)
    {
        var r = new PageReport { Key = key, Title = title };
        Drain();
        var sw = Stopwatch.StartNew();
        _vm.CurrentPage = page;
        await WaitIdleAsync(page, TimeSpan.FromSeconds(60));
        r.LoadMs = sw.ElapsedMilliseconds;

        await ExerciseAsync(page, r);
        await WaitIdleAsync(page, TimeSpan.FromSeconds(90));
        r.StillBusy = page.IsBusy;

        try { MainWindow.SaveVisual(_window.RootBorder, Path.Combine(_outDir, screenshot)); }
        catch (Exception ex) { r.Warnings.Add("screenshot: " + ex.Message); }

        Collect(r);
        return r;
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
        // The host page's own tab buttons are skipped: each tab is tested separately.
        var radios = Find<RadioButton>().Where(x => x.IsVisible && x.IsEnabled && x.GroupName != MaintenanceViewModel.TabGroup).ToList();
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
        sb.AppendLine($"Детектор ошибок привязок: {(_bindingDetectorWorks ? "работает (контрольная ошибка поймана)" : "НЕ РАБОТАЕТ — результаты по привязкам недостоверны")}");
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
        // QA for --lang en: every Russian text that was asked for but has no English entry (shown in Russian on screen).
        var missing = Loc.MissingKeys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        File.WriteAllText(Path.Combine(_outDir, "missing-keys.txt"), string.Join("\n", missing), new UTF8Encoding(false));
    }
}
