using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Cleaner;

/// <summary>A line of the collapsed «ЧТО НИКОГДА НЕ УДАЛЯЕТСЯ» block.</summary>
public sealed record GuaranteeLine(string Title, string Reason);

public sealed partial class CleanerViewModel : PageViewModel
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly List<CleanItem> _items = [];
    private CancellationTokenSource? _cts;
    private CleanEnvironment? _env;
    private PathGuard? _guard;
    private bool _lastAnalysisCancelled;
    private long _lastProgressTicks;

    public override string Key => "cleaner";
    public override string Index => "04";
    public override string Title => "ОЧИСТКА";
    public override string Subtitle => "Анализ и удаление временных файлов, кэшей и мусора — только из безопасных мест.";
    public override string Glyph => Glyphs.Delete;

    public ObservableCollection<CleanGroup> Groups { get; } = [];
    public ObservableCollection<DriveCard> Drives { get; } = [];

    public IReadOnlyList<GuaranteeLine> NeverTouched { get; } =
    [
        new("Пароли, cookies, история, вкладки", "Из браузеров удаляется только кэш — вход на сайты и данные остаются."),
        new("Документы, рабочий стол, загрузки", "Личные папки не входят ни в один пункт и защищены от удаления."),
        new("Prefetch", "Его очистка только замедляет запуск Windows и программ."),
        new("WinSxS, Installer, DriverStore", "Нужны для обновлений и удаления программ. WinSxS — только штатным DISM."),
        new("Шейдеры установленных игр Steam", "Их удаление вызывает статтеры в играх."),
        new("Windows.old и установочные файлы обновления Windows", "Удаляются только через «Параметры → Память» самой Windows. Скачанные пакеты Центра обновления и кэш оптимизации доставки — только отдельными пунктами, Windows при необходимости скачает их заново."),
    ];

    // ───────────────────────────── State ─────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(CleanCommand), nameof(ResetSelectionCommand), nameof(CancelCommand))]
    private bool _isWorking;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CancelCommand))] private bool _canCancel;
    [ObservableProperty] private string _workTitle = "ГОТОВО К АНАЛИЗУ";
    [ObservableProperty] private string _workDetail = "Анализ ничего не удаляет — он только считает, сколько места занимает каждый пункт.";
    [ObservableProperty] private double _workProgress;
    [ObservableProperty] private bool _workIndeterminate;
    [ObservableProperty] private string _workPercent = "";

    [ObservableProperty] private string _foundValue = "—";
    [ObservableProperty] private string _foundUnit = "";
    [ObservableProperty] private string _foundCaption = "запустите анализ";
    [ObservableProperty] private string _selectedValue = "—";
    [ObservableProperty] private string _selectedUnit = "";
    [ObservableProperty] private string _selectedCaption = "";
    [ObservableProperty] private string _freedValue = "—";
    [ObservableProperty] private string _freedUnit = "";
    [ObservableProperty] private string _freedCaption = "появится после очистки";

    [ObservableProperty] private string? _servicingText;
    [ObservableProperty] private string? _foreignUserText;

    // ───────────────────────────── Lifecycle ─────────────────────────────

    protected override async Task LoadAsync()
    {
        _ = RefreshDrivesAsync();
        // The first visit runs a dry-run analysis right away: it only reads, never deletes.
        await RunWorkAsync("Анализ мусора…", async () =>
        {
            await PrepareAsync();
            await AnalyzeItemsAsync(_items.ToList());
        });
    }

    protected override Task OnReturnAsync() => RefreshDrivesAsync();

    /// <summary>
    /// Leaving the page: drop the file lists of analyses that can no longer be used (stale or already cleaned —
    /// <see cref="CleanItem.NeedsFreshAnalysis"/> forces a new analysis before any clean). Never while working.
    /// </summary>
    public override void Deactivate()
    {
        base.Deactivate();
        if (IsWorking) return;
        foreach (var i in _items)
            if (i.NeedsFreshAnalysis(StaleAfter)) i.ReleaseManifests();
    }

    // ───────────────────────────── Commands ─────────────────────────────

    private bool CanRun() => !IsWorking;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task AnalyzeAsync()
    {
        await RunWorkAsync("Анализ мусора…", async () =>
        {
            await PrepareAsync();
            await AnalyzeItemsAsync(_items.ToList());
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CleanAsync()
    {
        if (GameBoostState.IsActive)
        {
            Ui.Toast("Идёт игровая сессия — очистка запустится после выхода из игры, чтобы не мешать ей.", ToastKind.Warning);
            return;
        }
        var selected = _items.Where(i => i.IsChecked && i.IsSelectable).ToList();
        if (selected.Count == 0)
        {
            Ui.Toast("Отметьте хотя бы один пункт для очистки.", ToastKind.Warning);
            return;
        }

        // 1. The approved list must come from a fresh dry run (never analyse-and-delete in one pass).
        var stale = selected.Where(i => i.NeedsFreshAnalysis(StaleAfter)).ToList();
        if (stale.Count > 0)
        {
            var ran = await RunWorkAsync("Анализ мусора…", async () =>
            {
                await PrepareAsync();
                await AnalyzeItemsAsync(stale);
            });
            if (!ran || _lastAnalysisCancelled) return;
        }

        selected = _items.Where(i => i.IsChecked && i.IsSelectable).ToList();
        var work = selected.Where(i => i.Analysis is { } a && a.HasWork(i.Definition.Method)).ToList();
        var idle = selected.Except(work).ToList();

        if (work.Count == 0)
        {
            await Ui.AlertAsync("НЕЧЕГО УДАЛЯТЬ",
                "В выбранных пунктах сейчас нет файлов, которые можно удалить.",
                idle.Select(i => $"{i.Name} — {SkipReason(i)}"));
            return;
        }

        // 2. Confirmation listing exactly what will be deleted.
        var total = work.Sum(i => i.CleanableBytes);
        var details = work.Select(DescribeForConfirmation).ToList();
        details.AddRange(idle.Select(i => $"Будет пропущено: {i.Name} — {SkipReason(i)}"));
        // Tool-driven items (DISM, Delivery Optimization, developer tools) decide the exact amount themselves.
        var hasTools = work.Any(i => i.Definition.Method is CleanMethod.ComponentStore or CleanMethod.DeliveryOptimization or CleanMethod.Tools);
        var places = $"{work.Count} {CleanFormat.Plural(work.Count, "места", "мест", "мест")}";
        var message =
            (total > 0
                ? $"Будет удалено {(hasTools ? "около " : "")}{CleanFormat.Bytes(total)} из {places}. "
                : $"Будет выполнена очистка {places}. ") +
            "Удалённые файлы нельзя восстановить: они не попадают в корзину, а точки восстановления Windows не хранят кэши и временные файлы. " +
            "Файлы, занятые программами, будут пропущены.";
        var confirmed = await Ui.ConfirmAsync("ОЧИСТИТЬ ВЫБРАННОЕ?", message, details, "УДАЛИТЬ", "ОТМЕНА", danger: true,
            footnote: "Кэши и временные папки программы создадут заново сами.");
        if (!confirmed) return;

        foreach (var expert in work.Where(i => i.Tier == CleanTier.Expert).ToList())
        {
            var ok = await Ui.ConfirmAsync(
                "ЗАПУСТИТЬ ОЧИСТКУ WINSXS?",
                "DISM удалит устаревшие версии компонентов Windows сразу, без 30-дневного периода ожидания. Это штатная операция Microsoft, " +
                "но она необратима. Режим /ResetBase не используется.",
                [
                    "Сначала DISM проверит хранилище и очистит его, только если сам это рекомендует.",
                    "Длится от нескольких минут до часа и сильно нагружает диск — не запускайте во время игры.",
                    "Прервать запущенную очистку нельзя: остановка посередине опаснее ожидания.",
                ],
                "ЗАПУСТИТЬ DISM", "БЕЗ DISM", danger: true);
            if (!ok) work.Remove(expert);
        }
        if (work.Count == 0) return;

        // 3. Delete.
        List<(CleanItem Item, ItemCleanResult Result)> results = [];
        await RunWorkAsync("Очистка…", async () => results = await CleanItemsAsync(work));

        // 4. Result.
        if (results.Count > 0) await ShowResultAsync(results);
        _ = RefreshDrivesAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCancelWork))]
    private void Cancel()
    {
        _cts?.Cancel();
        WorkDetail = "Останавливаем — текущий файл будет обработан до конца…";
    }

    private bool CanCancelWork() => IsWorking && CanCancel;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void ResetSelection()
    {
        foreach (var i in _items) i.IsChecked = i.Definition.DefaultChecked && i.IsSelectable;
        RecalcSummary();
    }

    // ───────────────────────────── Work wrapper ─────────────────────────────

    /// <summary>Runs one analysis or cleaning job with the shell busy indicator; items are locked meanwhile.</summary>
    private async Task<bool> RunWorkAsync(string busyText, Func<Task> work)
    {
        if (IsWorking) return false;
        IsWorking = true;
        foreach (var i in _items) i.IsLocked = true;
        try
        {
            await RunBusyAsync(busyText, work);
        }
        finally
        {
            foreach (var i in _items)
            {
                i.IsLocked = false;
                i.IsWorking = false;
            }
            CanCancel = false;
            WorkIndeterminate = false;
            IsWorking = false;
            RecalcSummary();
        }
        return true;
    }

    // ───────────────────────────── Discovery ─────────────────────────────

    /// <summary>Captures the environment and builds / updates the item list (keeps existing rows and their checkboxes).</summary>
    private async Task PrepareAsync()
    {
        WorkTitle = "ПОДГОТОВКА";
        WorkDetail = "Поиск браузеров, игровых лаунчеров и системных папок…";
        WorkIndeterminate = true;
        var (env, guard, presence) = await Task.Run(() =>
        {
            var e = CleanEnvironment.Capture();
            var g = PathGuard.Default;
            var p = CleanCatalog.All.Select(d => (Definition: d, Present: d.IsPresent(e))).ToList();
            return (e, g, p);
        });
        _env = env;
        _guard = guard;
        ApplyEnvironment(env);
        SyncItems(presence, env);
    }

    private void ApplyEnvironment(CleanEnvironment env)
    {
        ForeignUserText = env.ForeignSessionUser is { } user
            ? $"MONOLITH запущен от имени другой учётной записи ({user}) — очистка пользовательских кэшей отключена, запустите его под своей."
            : null;
        ServicingText = env.Servicing.Any
            ? $"{Capitalize(env.Servicing.Describe())}. Системные временные папки, журналы и очистка WinSxS будут пропущены, " +
              "пока Windows не завершит обслуживание — перезагрузите ПК и повторите анализ."
            : null;
    }

    private void SyncItems(List<(CleanDefinition Definition, bool Present)> presence, CleanEnvironment env)
    {
        foreach (var category in Enum.GetValues<CleanCategory>())
        {
            var defs = presence.Where(p => p.Definition.Category == category).ToList();
            var present = defs.Where(p => p.Present).Select(p => p.Definition).ToList();
            var group = Groups.FirstOrDefault(g => g.Category == category);

            if (present.Count == 0)
            {
                if (group is not null)
                {
                    foreach (var i in group.Items) Detach(i);
                    Groups.Remove(group);
                }
                continue;
            }

            if (group is null)
            {
                group = new CleanGroup(category, (int)category + 1);
                var at = Groups.Count(g => g.Category < category);
                Groups.Insert(at, group);
            }

            // Remove rows that disappeared (browser uninstalled…), insert new ones in catalog order.
            foreach (var gone in group.Items.Where(i => !present.Contains(i.Definition)).ToList())
            {
                Detach(gone);
                group.Items.Remove(gone);
            }
            for (var k = 0; k < present.Count; k++)
            {
                var def = present[k];
                var item = group.Items.FirstOrDefault(i => i.Definition == def);
                if (item is null)
                {
                    item = new CleanItem(def);
                    item.ApplyAccess(env);
                    item.IsChecked = def.DefaultChecked && item.IsSelectable;
                    item.PropertyChanged += OnItemPropertyChanged;
                    _items.Add(item);
                    group.Items.Insert(Math.Min(k, group.Items.Count), item);
                }
                else
                {
                    item.ApplyAccess(env);
                    var current = group.Items.IndexOf(item);
                    if (current != k) group.Items.Move(current, k);
                }
            }

            var missing = defs.Where(p => !p.Present && p.Definition.ProductName is not null)
                .Select(p => p.Definition.ProductName!)
                .Distinct()
                .ToList();
            group.MissingText = missing.Count == 0 ? null : "НЕ НАЙДЕНО НА ЭТОМ ПК: " + string.Join(" · ", missing);
            group.Refresh();
        }
        // Keep _items in display order.
        _items.Sort((a, b) => Order(a).CompareTo(Order(b)));

        int Order(CleanItem i)
        {
            for (var k = 0; k < CleanCatalog.All.Count; k++)
                if (CleanCatalog.All[k] == i.Definition)
                    return k;
            return int.MaxValue;
        }
    }

    private void Detach(CleanItem item)
    {
        item.PropertyChanged -= OnItemPropertyChanged;
        _items.Remove(item);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanItem.IsChecked) or nameof(CleanItem.CleanableBytes))
            RecalcSummary();
    }

    // ───────────────────────────── Analysis ─────────────────────────────

    private async Task AnalyzeItemsAsync(List<CleanItem> items)
    {
        if (_env is not { } env || _guard is not { } guard || items.Count == 0) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _lastAnalysisCancelled = false;
        CanCancel = true;
        WorkTitle = "АНАЛИЗ";
        WorkIndeterminate = false;
        WorkProgress = 0;
        WorkPercent = "0%";
        foreach (var i in items) i.SetAnalyzing();

        var needsDo = items.Any(i => i.Definition.Method == CleanMethod.DeliveryOptimization);
        var doSize = needsDo ? Task.Run(() => SystemCleanup.GetDeliveryOptimizationCacheSizeAsync(ct)) : null;
        var done = new HashSet<CleanItem>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await CleanWorker.RunAsync(() =>
            {
                for (var k = 0; k < items.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = items[k];
                    var index = k;
                    ReportProgress(index, items.Count, 0, item.Name, null, force: true);
                    ItemAnalysis a;
                    try
                    {
                        a = CleanRunner.Analyze(item.Definition, env, guard, ct,
                            seen => ReportProgress(index, items.Count, 0.5, item.Name, $"просмотрено {CleanFormat.Files(seen)}", force: false),
                            doSize is null ? null : () => doSize.GetAwaiter().GetResult());
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // One broken location must not stop the analysis of the others.
                        Log.Error($"Очистка: анализ «{item.Name}»", ex);
                        a = new ItemAnalysis { Unavailable = $"Ошибка анализа: {ex.Message}" };
                    }
                    Ui.Post(() =>
                    {
                        item.ApplyAnalysis(a, env);
                        done.Add(item);
                        RecalcSummary();
                    });
                }
            });

            var places = items.Count(i => i.FoundBytes > 0);
            WorkTitle = "АНАЛИЗ ЗАВЕРШЁН";
            WorkDetail = $"{DateTime.Now:HH:mm} · проверено {items.Count} {CleanFormat.Plural(items.Count, "пункт", "пункта", "пунктов")} за {stopwatch.Elapsed.TotalSeconds:0.0} с · " +
                         "ничего не удалено. Отметьте пункты и нажмите «ОЧИСТИТЬ».";
            WorkProgress = 100;
            WorkPercent = "";
            Log.Info($"Очистка: анализ завершён — найдено {CleanFormat.Bytes(_items.Sum(i => i.FoundBytes))} в {places} местах.");
        }
        catch (OperationCanceledException)
        {
            _lastAnalysisCancelled = true;
            foreach (var i in items.Where(i => !done.Contains(i))) i.SetCancelled();
            WorkTitle = "АНАЛИЗ ОТМЕНЁН";
            WorkDetail = "Результаты показаны для уже проверенных пунктов.";
            WorkPercent = "";
            Ui.Toast("Анализ отменён.", ToastKind.Info);
        }
        catch
        {
            _lastAnalysisCancelled = true;
            foreach (var i in items.Where(i => !done.Contains(i))) i.SetCancelled();
            WorkTitle = "АНАЛИЗ ПРЕРВАН";
            WorkDetail = "Произошла ошибка — подробности в консоли.";
            WorkPercent = "";
            throw;
        }
    }

    /// <summary>Throttled progress update from a worker thread.</summary>
    private void ReportProgress(int index, int count, double fraction, string name, string? detail, bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastProgressTicks) < 120) return;
        Interlocked.Exchange(ref _lastProgressTicks, now);
        var percent = Math.Clamp((index + fraction) / Math.Max(1, count) * 100, 0, 100);
        Ui.Post(() =>
        {
            WorkProgress = percent;
            WorkPercent = $"{percent:0}%";
            WorkDetail = detail is null ? name : $"{name} · {detail}";
        });
    }

    // ───────────────────────────── Cleaning ─────────────────────────────

    private async Task<List<(CleanItem Item, ItemCleanResult Result)>> CleanItemsAsync(List<CleanItem> work)
    {
        var results = new List<(CleanItem, ItemCleanResult)>();
        if (_guard is not { } guard) return results;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        WorkTitle = "ОЧИСТКА";
        WorkDetail = "Проверка состояния системы…";
        WorkIndeterminate = true;
        CanCancel = true;

        // Fresh state: processes, servicing, Windows Update — things may have changed while the dialog was open.
        var env = await Task.Run(CleanEnvironment.Capture);
        _env = env;
        ApplyEnvironment(env);
        var started = DateTime.UtcNow;
        var freeBefore = await Task.Run(SystemCleanup.SystemDriveFree);
        WorkIndeterminate = false;
        Log.Info($"Очистка: начало — {work.Count} {CleanFormat.Plural(work.Count, "пункт", "пункта", "пунктов")}.");

        for (var k = 0; k < work.Count; k++)
        {
            var item = work[k];
            if (ct.IsCancellationRequested)
            {
                foreach (var rest in work.Skip(k)) rest.SetCancelled();
                break;
            }
            item.SetCleaning();
            var index = k;
            var total = Math.Max(1, (int)Math.Min(int.MaxValue, item.Analysis?.CleanableFiles ?? 0));
            ReportProgress(index, work.Count, 0, item.Name, null, force: true);

            ItemCleanResult r;
            try
            {
                var a = item.Analysis!;
                switch (item.Definition.Method)
                {
                    case CleanMethod.Files:
                        r = await CleanWorker.RunAsync(() => CleanRunner.CleanFiles(item.Definition, a, env, guard, ct,
                            n => ReportProgress(index, work.Count, Math.Min(1, (double)n / total), item.Name,
                                $"обработано {CleanFormat.Files(n)}", force: false)));
                        break;
                    case CleanMethod.Tools:
                        WorkIndeterminate = true;
                        r = await CleanRunner.CleanToolsAsync(item.Definition, a, env, guard, ct);
                        break;
                    case CleanMethod.RecycleBin:
                        WorkIndeterminate = true;
                        r = await CleanRunner.CleanRecycleBinAsync(item.Definition, a, env);
                        break;
                    case CleanMethod.DeliveryOptimization:
                        WorkIndeterminate = true;
                        r = await CleanRunner.CleanDeliveryOptimizationAsync(item.Definition, a, env, ct);
                        break;
                    case CleanMethod.ComponentStore:
                        CanCancel = false;
                        WorkIndeterminate = true;
                        r = await CleanRunner.CleanComponentStoreAsync(item.Definition, env, (text, pct) => Ui.Post(() =>
                        {
                            WorkDetail = text;
                            WorkIndeterminate = pct is null;
                            if (pct is { } p)
                            {
                                WorkProgress = p;
                                WorkPercent = $"{p:0}%";
                            }
                        }), ct);
                        CanCancel = true;
                        break;
                    default:
                        r = new ItemCleanResult { Skipped = "Неизвестный способ очистки." };
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                r = new ItemCleanResult();
                r.Tally.Cancelled = true;
                r.Notes.Add("Очистка прервана по вашей команде.");
            }
            catch (Exception ex)
            {
                Log.Error($"Очистка: {item.Name}", ex);
                r = new ItemCleanResult { Error = ex.Message };
            }
            finally
            {
                WorkIndeterminate = false;
                CanCancel = true;
            }

            item.ApplyCleanResult(r);
            results.Add((item, r));
            LogResult(item, r);
            RecalcSummary();
        }

        var freeAfter = await Task.Run(SystemCleanup.SystemDriveFree);
        var freed = results.Sum(x => x.Item2.Freed);
        var deleted = results.Sum(x => (long)x.Item2.Tally.Deleted);
        (FreedValue, FreedUnit) = CleanFormat.Split(freed);
        FreedCaption = $"{CleanFormat.Files(deleted)} удалено · {DateTime.Now:HH:mm}";
        WorkTitle = ct.IsCancellationRequested ? "ОЧИСТКА ПРЕРВАНА" : "ОЧИСТКА ЗАВЕРШЕНА";
        WorkDetail = $"Освобождено {CleanFormat.Bytes(freed)} · на системном диске свободно {CleanFormat.Bytes(freeAfter)}";
        WorkProgress = 100;
        WorkPercent = "";

        var report = await Task.Run(() => SystemCleanup.WriteReport(started, results.Select(x => ToReport(x.Item1, x.Item2)).ToList(), freeBefore, freeAfter));
        if (report is not null) Log.Info($"Очистка: отчёт сохранён — {report}");
        Log.Success($"Очистка завершена: освобождено {CleanFormat.Bytes(freed)}, удалено {CleanFormat.Files(deleted)}.");
        return results;
    }

    private static void LogResult(CleanItem item, ItemCleanResult r)
    {
        if (r.Skipped is { } skipped) Log.Warn($"Очистка: {item.Name} — пропущено: {skipped}");
        else if (r.Error is { } error) Log.Warn($"Очистка: {item.Name} — {error}");
        else
            Log.Info($"Очистка: {item.Name} — освобождено {CleanFormat.Bytes(r.Freed)}, удалено {CleanFormat.Files(r.Tally.Deleted)}" +
                     (r.Tally.InUse > 0 ? $", занято {r.Tally.InUse}" : "") + ".");
    }

    private static SystemCleanup.ReportItem ToReport(CleanItem item, ItemCleanResult r) => new(
        item.Id, item.Name, r.Freed, r.Tally.Deleted, r.Tally.InUse, r.Tally.Denied, r.Tally.Changed, r.Tally.Errors,
        r.Tally.DirsRemoved, r.Skipped, r.Error, r.Notes.Concat(r.Tally.ErrorSamples).ToList(),
        r.Tally.DeletedFiles.Select(f => new SystemCleanup.ReportFile(f.Path, f.Size)).ToList());

    private static async Task ShowResultAsync(List<(CleanItem Item, ItemCleanResult Result)> results)
    {
        var freed = results.Sum(x => x.Result.Freed);
        var deleted = results.Sum(x => (long)x.Result.Tally.Deleted);
        var inUse = results.Sum(x => (long)x.Result.Tally.InUse);
        var errors = results.Count(x => x.Result.Error is not null);
        var cancelled = results.Any(x => x.Result.Tally.Cancelled);

        var details = new List<string>();
        foreach (var (item, r) in results)
        {
            if (r.Skipped is { } s) details.Add($"{item.Name} — пропущено: {s}");
            else if (r.Error is { } e && r.Freed == 0) details.Add($"{item.Name} — ошибка: {e}");
            else
            {
                var line = $"{item.Name} — освобождено {(r.Approximate ? "≈" : "")}{CleanFormat.Bytes(r.Freed)}";
                if (r.Tally.InUse > 0) line += $", занято программами: {CleanFormat.Count(r.Tally.InUse)}";
                if (r.Error is { } e2) line += $" ({e2})";
                details.Add(line);
            }
        }

        var title = cancelled ? "ОЧИСТКА ПРЕРВАНА" : errors > 0 ? "ОЧИСТКА ЗАВЕРШЕНА С ОШИБКАМИ" : "ОЧИСТКА ЗАВЕРШЕНА";
        var message = $"Освобождено {CleanFormat.Bytes(freed)}. Удалено: {CleanFormat.Files(deleted)}." +
                      (inUse > 0 ? $" {CleanFormat.Count(inUse)} {CleanFormat.Plural(inUse, "файл был занят программами и пропущен", "файла были заняты программами и пропущены", "файлов были заняты программами и пропущены")}." : "") +
                      (errors > 0 ? $" Ошибок: {errors} — подробности в консоли." : "");
        await Ui.AlertAsync(title, message, details);
    }

    // ───────────────────────────── Summary ─────────────────────────────

    private void RecalcSummary()
    {
        var analysed = _items.Where(i => i.Analysis is not null).ToList();
        if (analysed.Count == 0)
        {
            FoundValue = "—";
            FoundUnit = "";
            FoundCaption = "запустите анализ";
        }
        else
        {
            var found = analysed.Sum(i => i.FoundBytes);
            var files = analysed.Sum(i => i.FoundFiles);
            var places = analysed.Count(i => i.FoundBytes > 0);
            (FoundValue, FoundUnit) = CleanFormat.Split(found);
            FoundCaption = $"{CleanFormat.Files(files)} в {places} {CleanFormat.Plural(places, "месте", "местах", "местах")}";
        }

        var selected = _items.Where(i => i.IsChecked && i.IsSelectable).ToList();
        if (selected.Any(i => i.Analysis is not null))
            (SelectedValue, SelectedUnit) = CleanFormat.Split(selected.Sum(i => i.CleanableBytes));
        else
            (SelectedValue, SelectedUnit) = ("—", "");
        var dism = selected.Any(i => i.Definition.Method == CleanMethod.ComponentStore) ? " · + DISM" : "";
        SelectedCaption = $"{selected.Count} {CleanFormat.Plural(selected.Count, "пункт выбран", "пункта выбрано", "пунктов выбрано")}{dism}";

        foreach (var g in Groups) g.Refresh();
    }

    private static string DescribeForConfirmation(CleanItem i)
    {
        var a = i.Analysis!;
        return i.Definition.Method switch
        {
            CleanMethod.ComponentStore => $"{i.Name} — размер определит DISM, может занять до часа",
            CleanMethod.DeliveryOptimization => $"{i.Name} — {(a.SizeUnknown ? "размер неизвестен" : CleanFormat.Bytes(a.CleanableBytes))}, штатной командой Windows",
            CleanMethod.RecycleBin => $"{i.Name} — {CleanFormat.Bytes(a.CleanableBytes)} · {CleanFormat.Count(a.Items ?? 0)} {CleanFormat.Plural(a.Items ?? 0, "объект", "объекта", "объектов")} (восстановить будет нельзя)",
            CleanMethod.Tools => $"{i.Name} — до {CleanFormat.Bytes(a.CleanableBytes)}, командой самого инструмента",
            _ => $"{i.Name} — {CleanFormat.Bytes(a.CleanableBytes)} · {CleanFormat.Files(a.CleanableFiles)}" +
                 (a.HasRunningApps ? $" (без {string.Join(", ", a.RunningApps)} — запущено)" : ""),
        };
    }

    private static string SkipReason(CleanItem i)
    {
        if (i.Analysis is not { } a) return "не проверено";
        if (a.Unavailable is { } u) return u;
        if (a.Blocker is { } b) return b;
        if (a.HasRunningApps) return $"запущено: {string.Join(", ", a.RunningApps)}";
        return "удалять нечего";
    }

    // ───────────────────────────── Drives ─────────────────────────────

    private async Task RefreshDrivesAsync()
    {
        try
        {
            var drives = await Task.Run(() =>
            {
                var sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
                var list = new List<DriveCard>();
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                        var isSystem = string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase);
                        var free = d.AvailableFreeSpace;
                        var total = d.TotalSize;
                        var used = Math.Clamp((total - d.TotalFreeSpace) * 100.0 / total, 0, 100);
                        var label = isSystem ? "СИСТЕМНЫЙ"
                            : string.IsNullOrWhiteSpace(d.VolumeLabel) ? "ЛОКАЛЬНЫЙ ДИСК" : d.VolumeLabel.ToUpperInvariant();
                        list.Add(new DriveCard(d.Name.TrimEnd('\\'), label, CleanFormat.Bytes(free),
                            $"свободно из {CleanFormat.Bytes(total)} · занято {used:0}%", used, isSystem));
                    }
                    catch
                    {
                        // A drive that disappears or refuses queries is simply not shown.
                    }
                }
                return list.OrderByDescending(x => x.IsSystem).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            });

            Drives.Clear();
            foreach (var d in drives) Drives.Add(d);
        }
        catch (Exception ex)
        {
            Log.Warn($"Очистка: не удалось прочитать диски: {ex.Message}");
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}

/// <summary>Runs blocking file work on a dedicated thread in Windows background mode (low I/O and CPU priority).</summary>
internal static class CleanWorker
{
    public static Task RunAsync(Action work) => RunAsync<object?>(() =>
    {
        work();
        return null;
    });

    public static Task<T> RunAsync<T>(Func<T> work) =>
        Task.Factory.StartNew(() =>
        {
            var background = NativeCleaner.EnterBackgroundMode();
            try
            {
                return work();
            }
            finally
            {
                if (background) NativeCleaner.LeaveBackgroundMode();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
