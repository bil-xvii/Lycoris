using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Startup;

/// <summary>
/// АВТОЗАПУСК: Run / Run32 / Startup-folder entries (enabled or disabled exactly like Task Manager, never deleted)
/// and third-party scheduled tasks that start at sign-in or boot.
/// Not a top-level page: <see cref="Services.ServicesViewModel"/> owns it (АВТОЗАПУСК tab; logon tasks on the ЗАДАЧИ tab)
/// and forwards activation. Key/Title are only used for logs.
/// </summary>
public sealed partial class StartupViewModel : PageViewModel
{
    public override string Key => "startup";
    public override string Index => "07";
    public override string Title => "АВТОЗАПУСК";
    public override string Subtitle => "Программы и задачи, которые запускаются вместе с Windows.";
    public override string Glyph => Glyphs.Power;

    private bool _syncingSelection;

    public ObservableCollection<StartupItem> Items { get; } = [];
    public ObservableCollection<LogonTaskItem> Tasks { get; } = [];
    public ICollectionView ItemsView { get; }

    /// <summary>all | enabled | disabled | missing.</summary>
    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private string _search = "";

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private int _disabledCount;
    [ObservableProperty] private int _missingCount;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private int _taskCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _allSelected;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _scanText = "Сканирование…";
    [ObservableProperty] private string? _tasksError;

    public StartupViewModel()
    {
        ItemsView = new ListCollectionView(Items) { Filter = o => o is StartupItem i && Matches(i) };
        ChangeJournal.Instance.Changed += () => Ui.Post(UpdateJournalState);
    }

    /// <summary>The host page is busy with its own work (changes must not overlap).</summary>
    public Func<bool>? IsHostBusy { get; set; }

    public string TasksCountText => $"{TaskCount} {Plural(TaskCount, "ЗАДАЧА", "ЗАДАЧИ", "ЗАДАЧ")}";
    public bool HasItems => TotalCount > 0;
    public bool HasTasks => TaskCount > 0;
    public bool ShowNoMatches => IsLoaded && TotalCount > 0 && VisibleCount == 0;
    public bool ShowNoItems => IsLoaded && TotalCount == 0;
    public bool ShowNoTasks => IsLoaded && TaskCount == 0;
    public bool HasSelection => SelectedCount > 0;

    partial void OnFilterChanged(string value) => RefreshView();
    partial void OnSearchChanged(string value) => RefreshView();
    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowNoItems));
        OnPropertyChanged(nameof(ShowNoMatches));
    }
    partial void OnVisibleCountChanged(int value) => OnPropertyChanged(nameof(ShowNoMatches));
    partial void OnTaskCountChanged(int value)
    {
        OnPropertyChanged(nameof(TasksCountText));
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(ShowNoTasks));
    }
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnIsLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoItems));
        OnPropertyChanged(nameof(ShowNoTasks));
        OnPropertyChanged(nameof(ShowNoMatches));
    }

    partial void OnAllSelectedChanged(bool value)
    {
        if (_syncingSelection) return;
        _syncingSelection = true;
        try
        {
            foreach (var item in ItemsView.Cast<StartupItem>()) item.IsSelected = value;
        }
        finally
        {
            _syncingSelection = false;
        }
        UpdateSelection();
    }

    protected override Task LoadAsync() => RefreshAsync();

    protected override Task OnReturnAsync() => RefreshAsync();

    // ───────────────────────────── Scan ─────────────────────────────

    [RelayCommand]
    private Task RefreshAsync() => RunBusyAsync("Сканирование автозапуска…", ScanAsync);

    private async Task ScanAsync()
    {
        var entriesTask = StaWorker.Run(StartupScanner.Scan, "MONOLITH startup scan");
        var tasksTask = StaWorker.Run(() => LogonTaskScanner.Scan(), "MONOLITH task scan");

        var entries = await entriesTask;
        List<LogonTask> tasks;
        try
        {
            tasks = await tasksTask;
            TasksError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Автозапуск: не удалось прочитать планировщик заданий", ex);
            TasksError = $"Не удалось прочитать планировщик заданий: {ex.Message}";
            tasks = [];
        }

        // Replace the rows only when something changed, so returning to the page does not make the list flicker.
        if (!Items.Select(i => i.Entry).SequenceEqual(entries))
        {
            var selected = Items.Where(i => i.IsSelected).Select(i => i.Key).ToHashSet();
            foreach (var old in Items) old.PropertyChanged -= OnItemPropertyChanged;
            Items.Clear();
            foreach (var e in entries)
            {
                var item = new StartupItem(e) { IsSelected = Elevation.IsElevated && selected.Contains($"{e.Source}|{e.Name}") };
                item.PropertyChanged += OnItemPropertyChanged;
                Items.Add(item);
            }
        }

        if (!Tasks.Select(t => t.Info).SequenceEqual(tasks))
        {
            Tasks.Clear();
            foreach (var t in tasks) Tasks.Add(new LogonTaskItem(t));
        }

        UpdateJournalState();
        RefreshView();
        Recount();
        ScanText = $"Проверено в {DateTime.Now:HH:mm} · реестр, папки автозагрузки, планировщик";
        IsLoaded = true;
        Log.Info($"Автозапуск: записей {TotalCount} (включено {EnabledCount}), задач при входе {TaskCount}.");
    }

    // ───────────────────────────── Toggles ─────────────────────────────

    [RelayCommand]
    private async Task ToggleAsync(StartupItem? item)
    {
        if (item is null) return;
        try
        {
            if (!EnsureIdle() || !RequireAdmin("изменение автозапуска")) return;
            var enable = !item.IsEnabled;
            if (!enable && !item.CanDisable)
            {
                Ui.Toast($"«{item.DisplayName}» — значок защиты системы. MONOLITH его не отключает.", ToastKind.Warning);
                return;
            }
            if (!enable && !await ConfirmEntriesAsync([item], enable: false)) return;
            await ApplyEntriesAsync([item], enable, createRestorePoint: false);
        }
        finally
        {
            item.SyncToggle();
        }
    }

    [RelayCommand]
    private async Task ToggleTaskAsync(LogonTaskItem? item)
    {
        if (item is null) return;
        try
        {
            if (!EnsureIdle() || !RequireAdmin("изменение задач при входе")) return;
            var enable = !item.IsEnabled;
            if (!enable && !item.CanDisable)
            {
                Ui.Toast(item.ToggleTooltip, ToastKind.Warning);
                return;
            }
            if (!enable && !await ConfirmTaskAsync(item)) return;
            await ApplyTaskAsync(item, enable);
        }
        finally
        {
            item.SyncToggle();
        }
    }

    [RelayCommand]
    private async Task DisableSelectedAsync()
    {
        if (!EnsureIdle() || !RequireAdmin("отключение автозапуска")) return;
        var chosen = Items.Where(i => i.IsSelected).ToList();
        var locked = chosen.Where(i => i.IsEnabled && !i.CanDisable).ToList();
        var targets = chosen.Where(i => i.IsEnabled && i.CanDisable).ToList();
        if (targets.Count == 0)
        {
            Ui.Toast(locked.Count > 0
                ? "Среди выбранных только защитные записи — их MONOLITH не отключает."
                : "Все выбранные записи уже отключены.", ToastKind.Info);
            return;
        }
        if (!await ConfirmEntriesAsync(targets, enable: false, skippedLocked: locked.Count)) return;
        await ApplyEntriesAsync(targets, enable: false, createRestorePoint: targets.Count > 1);
    }

    [RelayCommand]
    private async Task EnableSelectedAsync()
    {
        if (!EnsureIdle() || !RequireAdmin("включение автозапуска")) return;
        var targets = Items.Where(i => i.IsSelected && !i.IsEnabled).ToList();
        if (targets.Count == 0)
        {
            Ui.Toast("Все выбранные записи уже включены.", ToastKind.Info);
            return;
        }
        if (targets.Count > 1 && !await ConfirmEntriesAsync(targets, enable: true)) return;
        await ApplyEntriesAsync(targets, enable: true, createRestorePoint: targets.Count > 1);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _syncingSelection = true;
        try
        {
            foreach (var i in Items) i.IsSelected = false;
            AllSelected = false;
        }
        finally
        {
            _syncingSelection = false;
        }
        UpdateSelection();
    }

    private async Task ApplyEntriesAsync(IReadOnlyList<StartupItem> items, bool enable, bool createRestorePoint)
    {
        await RunBusyAsync(enable ? "Включение автозапуска…" : "Отключение автозапуска…", async () =>
        {
            var progress = new Progress<string>(t => BusyText = t);
            var entries = items.Select(i => i.Entry).ToList();
            var outcome = await Task.Run(() => StartupChanges.SetEntriesAsync(entries, enable, createRestorePoint, progress));

            foreach (var item in items)
            {
                var (on, at) = await Task.Run(() => StartupScanner.ReadState(item.Entry.Source, item.Entry.Name));
                item.Update(on, at);
            }
            UpdateJournalState();
            Recount();
            RefreshView();
            ReportOutcome(outcome, items.Select(i => i.DisplayName).ToList(), enable, "Изменение вступит в силу при следующем входе в Windows.");
        });
    }

    private async Task ApplyTaskAsync(LogonTaskItem item, bool enable)
    {
        await RunBusyAsync(enable ? "Включение задачи…" : "Отключение задачи…", async () =>
        {
            var progress = new Progress<string>(t => BusyText = t);
            var outcome = await Task.Run(() => StartupChanges.SetTasksAsync([item.Info], enable, false, progress));
            var state = await Task.Run(() => LogonTaskScanner.ReadEnabled(item.Path));
            if (state is { } on) item.Update(on);
            UpdateJournalState();
            Recount();
            ReportOutcome(outcome, [item.DisplayName], enable, "Планировщик применяет изменение сразу.");
        });
    }

    private static void ReportOutcome(StartupChangeOutcome outcome, IReadOnlyList<string> names, bool enable, string tail)
    {
        var subject = names.Count == 1 ? $"«{names[0]}»" : $"{names.Count} {Plural(names.Count, "запись", "записи", "записей")}";
        if (outcome.Success && outcome.Applied > 0)
        {
            var text = enable
                ? $"{subject} снова {(names.Count == 1 ? "будет" : "будут")} запускаться вместе с Windows. {tail}"
                : $"{subject} больше не {(names.Count == 1 ? "будет" : "будут")} запускаться при входе. Вернуть можно здесь же или в разделе «Восстановление».";
            Ui.Toast(text, ToastKind.Success, enable ? "АВТОЗАПУСК ВКЛЮЧЁН" : "АВТОЗАПУСК ОТКЛЮЧЁН");
        }
        else if (outcome.Success)
        {
            Ui.Toast("Изменений не потребовалось.", ToastKind.Info, "АВТОЗАПУСК");
        }
        else
        {
            Ui.Toast(outcome.Errors[0], ToastKind.Error, outcome.Applied > 0 ? $"ПРИМЕНЕНО ЧАСТИЧНО ({outcome.Applied})" : "НЕ ПРИМЕНЕНО");
        }
    }

    private static Task<bool> ConfirmEntriesAsync(IReadOnlyList<StartupItem> items, bool enable, int skippedLocked = 0)
    {
        var details = new List<string>();
        foreach (var i in items)
        {
            if (items.Count == 1)
            {
                details.Add($"Запись: {StartupLocations.Describe(i.Entry.Source)} → {i.Entry.Name}");
                details.Add($"Команда: {i.Command}");
                details.Add($"Отметка: {(i.IsMachine ? "HKLM" : "HKCU")}\\{StartupLocations.ApprovedKey(i.Entry.Source)} → «{i.Entry.Name}» = " +
                            (enable ? "02 00 00 00 … (включено)" : "03 00 00 00 + время отключения"));
            }
            else
            {
                details.Add($"{i.DisplayName} — {(i.IsMachine ? "для всех" : "для меня")}, {i.SourceText.ToLowerInvariant()}");
            }

            switch (i.Risk)
            {
                case StartupRisk.Microsoft when !enable:
                    details.Add($"Внимание ({i.DisplayName}): компонент Microsoft — может пропасть значок в трее или фоновая функция (синхронизация, уведомления).");
                    break;
                case StartupRisk.Driver when !enable:
                    details.Add($"Внимание ({i.DisplayName}): помощник драйвера — могут пропасть панель звука/тачпада/видеокарты, горячие клавиши или эффекты.");
                    break;
            }
        }
        if (skippedLocked > 0)
            details.Add($"Пропущено защитных записей: {skippedLocked} (Безопасность Windows и антивирусы MONOLITH не отключает).");
        if (items.Count > 1)
            details.Add("Перед изменением MONOLITH создаст точку восстановления (если это включено в разделе «Восстановление»).");

        var subject = items.Count == 1 ? $"«{items[0].DisplayName}»" : $"{items.Count} {Plural(items.Count, "запись", "записи", "записей")}";
        var message = enable
            ? $"{subject} снова {(items.Count == 1 ? "будет" : "будут")} запускаться при входе в Windows."
            : $"{subject} {Plural(items.Count, "перестанет", "перестанут", "перестанут")} запускаться при входе в Windows. Ничего не удаляется: MONOLITH ставит отметку «отключено» так же, как Диспетчер задач. " +
              "Уже запущенные программы продолжат работать до перезагрузки.";

        return Ui.ConfirmAsync(
            enable ? (items.Count == 1 ? "ВКЛЮЧИТЬ АВТОЗАПУСК?" : $"ВКЛЮЧИТЬ {items.Count} {Plural(items.Count, "ЗАПИСЬ", "ЗАПИСИ", "ЗАПИСЕЙ")}?")
                   : (items.Count == 1 ? "ОТКЛЮЧИТЬ АВТОЗАПУСК?" : $"ОТКЛЮЧИТЬ {items.Count} {Plural(items.Count, "ЗАПИСЬ", "ЗАПИСИ", "ЗАПИСЕЙ")}?"),
            message, details,
            confirmText: enable ? "ВКЛЮЧИТЬ" : "ОТКЛЮЧИТЬ",
            footnote: "Вернуть: переключатель на этой странице, Диспетчер задач → «Автозагрузка» или раздел «Восстановление». " +
                      "Изменение вступит в силу при следующем входе в Windows.");
    }

    private static Task<bool> ConfirmTaskAsync(LogonTaskItem item)
    {
        var details = new List<string>
        {
            $"Задача: {item.Path}",
            $"Действие: {item.Command}",
            $"Запуск: {item.TriggerText.ToLowerInvariant()} ({item.TriggerDetail})",
        };
        if (item.Info.OtherTriggers > 0)
            details.Add($"У задачи есть ещё триггеры ({item.Info.OtherTriggers}: по расписанию, при простое и т. п.) — задача отключается целиком, они тоже перестанут срабатывать.");
        if (item.Risk == StartupRisk.Driver)
            details.Add("Внимание: задача производителя оборудования — могут пропасть функции драйвера (панель, горячие клавиши).");
        if (item.Risk == StartupRisk.Microsoft)
            details.Add("Внимание: исполняемый файл подписан Microsoft — проверьте, что задача вам не нужна.");

        return Ui.ConfirmAsync("ОТКЛЮЧИТЬ ЗАДАЧУ?",
            $"Задача «{item.DisplayName}» больше не будет запускаться планировщиком Windows. Сама задача не удаляется.",
            details,
            confirmText: "ОТКЛЮЧИТЬ",
            footnote: "Вернуть: переключатель на этой странице, «Планировщик заданий» Windows или раздел «Восстановление».");
    }

    // ───────────────────────────── Row actions ─────────────────────────────

    [RelayCommand]
    private void OpenLocation(IStartupRow? row)
    {
        if (row is null) return;
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            string? file = null;
            if (row.TargetPath is { } t && Path.IsPathFullyQualified(t) && FileProbe.FileExists(t)) file = t;
            else if (row.ItemPath is { } p && FileProbe.FileExists(p)) file = p;

            if (file is not null)
            {
                Process.Start(new ProcessStartInfo(explorer, $"/select,\"{file}\"") { UseShellExecute = false });
                return;
            }

            var dir = row.TargetPath is { } tp && Path.IsPathFullyQualified(tp) ? Path.GetDirectoryName(tp) : null;
            if (dir is not null && FileProbe.DirectoryExists(dir))
            {
                Process.Start(new ProcessStartInfo(explorer, $"\"{dir}\"") { UseShellExecute = false });
                Ui.Toast($"Файл не найден, открыта папка: {dir}", ToastKind.Warning);
                return;
            }

            Ui.Toast(row.TargetPath is null
                ? "У этой записи нет файла на диске, открывать нечего."
                : $"Файл и его папка не найдены: {row.TargetPath}", ToastKind.Warning, "НЕТ ФАЙЛА");
        }
        catch (Exception ex)
        {
            Log.Error("Автозапуск: не удалось открыть расположение", ex);
            Ui.Toast(ex.Message, ToastKind.Error, "ОТКРЫТЬ РАСПОЛОЖЕНИЕ");
        }
    }

    [RelayCommand]
    private void CopyCommand(IStartupRow? row)
    {
        if (row is null) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(row.Command);
                Ui.Toast("Команда скопирована в буфер обмена.", ToastKind.Success);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 2)
            {
                Thread.Sleep(60); // clipboard briefly locked by another app
            }
            catch (Exception ex)
            {
                Log.Error("Автозапуск: буфер обмена недоступен", ex);
                Ui.Toast("Буфер обмена занят другой программой. Попробуйте ещё раз.", ToastKind.Error);
                return;
            }
        }
    }

    [RelayCommand]
    private void SearchOnline(IStartupRow? row)
    {
        if (row is null) return;
        var term = row.TargetPath is { Length: > 0 } t ? Path.GetFileName(t) : row.DisplayName;
        if (string.IsNullOrWhiteSpace(term)) term = row.DisplayName;
        var url = "https://www.google.com/search?q=" + Uri.EscapeDataString(term);
        try
        {
            if (Elevation.IsElevated)
            {
                // Hand the URL to the (non-elevated) shell so the browser does not run with admin rights.
                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(new ProcessStartInfo(explorer, $"\"{url}\"") { UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Log.Error("Автозапуск: не удалось открыть браузер", ex);
            Ui.Toast($"Не удалось открыть браузер: {ex.Message}", ToastKind.Error);
        }
    }

    // ───────────────────────────── State ─────────────────────────────

    private bool EnsureIdle()
    {
        if (!IsBusy && IsHostBusy?.Invoke() != true) return true;
        Ui.Toast("Дождитесь окончания текущей операции: " + (BusyText ?? "идёт работа"), ToastKind.Info);
        return false;
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartupItem.IsSelected) && !_syncingSelection) UpdateSelection();
    }

    private void UpdateSelection()
    {
        SelectedCount = Items.Count(i => i.IsSelected);
        var visible = ItemsView.Cast<StartupItem>().ToList();
        _syncingSelection = true;
        try
        {
            AllSelected = visible.Count > 0 && visible.All(i => i.IsSelected);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void UpdateJournalState()
    {
        var records = StartupChanges.ActiveRecords();
        var targets = records.Select(r => r.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var i in Items) i.IsJournaled = targets.Contains(StartupLocations.JournalTarget(i.Entry.Source, i.Entry.Name));
        foreach (var t in Tasks) t.IsJournaled = targets.Contains($"task:{t.Path}");
    }

    private void Recount()
    {
        TotalCount = Items.Count;
        EnabledCount = Items.Count(i => i.IsEnabled);
        DisabledCount = TotalCount - EnabledCount;
        MissingCount = Items.Count(i => !i.TargetExists);
        TaskCount = Tasks.Count;
        UpdateSelection();
    }

    private void RefreshView()
    {
        ItemsView.Refresh();
        VisibleCount = ItemsView.Cast<object>().Count();
        UpdateSelection();
    }

    private bool Matches(StartupItem i)
    {
        var passFilter = Filter switch
        {
            "enabled" => i.IsEnabled,
            "disabled" => !i.IsEnabled,
            "missing" => !i.TargetExists,
            _ => true,
        };
        if (!passFilter) return false;

        var q = Search?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return Contains(i.DisplayName, q) || Contains(i.Entry.Name, q) || Contains(i.Command, q) || Contains(i.Entry.Publisher, q);

        static bool Contains(string? s, string q) => s?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    internal static string Plural(int n, string one, string few, string many)
    {
        var n10 = n % 10;
        var n100 = n % 100;
        if (n10 == 1 && n100 != 11) return one;
        if (n10 is >= 2 and <= 4 && n100 is < 12 or > 14) return few;
        return many;
    }
}
