using System.Collections.ObjectModel;
using System.ComponentModel;
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
    [ObservableProperty] private double _enabledShare;
    [ObservableProperty] private int _taskCount;
    [ObservableProperty] private int _taskEnabledCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _journalCount;
    [ObservableProperty] private bool _allSelected;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _scanText = "Сканирование…";
    [ObservableProperty] private string? _tasksError;

    /// <summary>What happens with the restore point before a change of several entries (setting + System Protection state).</summary>
    [ObservableProperty] private string _restorePointNote = StartupTexts.RestorePointNote(settingOn: true, protectionOn: true);

    public StartupViewModel()
    {
        ItemsView = new ListCollectionView(Items) { Filter = o => o is StartupItem i && Matches(i) };
        ChangeJournal.Instance.Changed += () => Ui.Post(UpdateJournalState);
    }

    public string TotalCaption => MissingCount > 0 ? $"без файла: {MissingCount}" : "Run, Run32 и папки";
    public string TaskCaption => TaskCount == 0 ? "сторонних не найдено" : $"включено: {TaskEnabledCount}";
    public string ItemsCountText => $"{TotalCount} {StartupTexts.Plural(TotalCount, "ЗАПИСЬ", "ЗАПИСИ", "ЗАПИСЕЙ")}";
    public string TasksCountText => $"{TaskCount} {StartupTexts.Plural(TaskCount, "ЗАДАЧА", "ЗАДАЧИ", "ЗАДАЧ")}";
    public bool HasItems => TotalCount > 0;
    public bool HasTasks => TaskCount > 0;
    public bool ShowNoMatches => IsLoaded && TotalCount > 0 && VisibleCount == 0;
    public bool ShowNoItems => IsLoaded && TotalCount == 0;
    public bool ShowNoTasks => IsLoaded && TaskCount == 0;
    public bool HasSelection => SelectedCount > 0;
    public bool HasJournal => JournalCount > 0;

    partial void OnFilterChanged(string value) => RefreshView();
    partial void OnSearchChanged(string value) => RefreshView();
    partial void OnMissingCountChanged(int value) => OnPropertyChanged(nameof(TotalCaption));
    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(ItemsCountText));
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
        OnPropertyChanged(nameof(TaskCaption));
    }
    partial void OnTaskEnabledCountChanged(int value) => OnPropertyChanged(nameof(TaskCaption));
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnJournalCountChanged(int value) => OnPropertyChanged(nameof(HasJournal));
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
        RestorePointNote = await Task.Run(ReadRestorePointNote);

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
            if (!enable && !await ConfirmEntriesAsync([item], enable: false, restorePointNote: null)) return;
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
        var multiple = targets.Count > 1;
        var note = multiple ? await RefreshRestorePointNoteAsync() : null;
        if (!await ConfirmEntriesAsync(targets, enable: false, note, skippedLocked: locked.Count)) return;
        await ApplyEntriesAsync(targets, enable: false, createRestorePoint: multiple);
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
        var multiple = targets.Count > 1;
        if (multiple && !await ConfirmEntriesAsync(targets, enable: true, await RefreshRestorePointNoteAsync())) return;
        await ApplyEntriesAsync(targets, enable: true, createRestorePoint: multiple);
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

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        if (!EnsureIdle() || !RequireAdmin("откат изменений автозапуска")) return;
        var all = StartupChanges.ActiveRecords();
        if (all.Count == 0)
        {
            Ui.Toast("Изменений автозапуска в журнале нет.", ToastKind.Info);
            return;
        }

        // A task removed by its program's uninstaller has nothing to put back — its undo would only fail.
        var vanished = await Task.Run(() => all.Where(r => r.Kind == ChangeKind.ScheduledTask && r.TaskPath is { } p && !ScheduledTasks.Query(p).Exists).ToList());
        var records = all.Except(vanished).ToList();
        var gone = vanished.Select(r => $"{r.Title}: задачи больше нет в планировщике — пропускается").ToList();
        if (records.Count == 0)
        {
            await Ui.AlertAsync("ВОЗВРАЩАТЬ НЕЧЕГО",
                "Задач из журнала этой страницы больше нет в планировщике (их удалила программа или обновление), поэтому возвращать нечего.",
                gone.Append("Записи об изменениях остаются в разделе «Восстановление»: если задача появится снова, её можно будет вернуть там или здесь."));
            return;
        }

        var ok = await Ui.ConfirmAsync("ОТКАТИТЬ ИЗМЕНЕНИЯ АВТОЗАПУСКА?",
            StartupTexts.RevertAllMessage(records.Count),
            records.Select(r => $"{r.Title}: {r.NewValue} → {r.OldValue}").Concat(gone),
            confirmText: "ОТКАТИТЬ",
            footnote: "Ничего не удаляется. Отметки автозапуска сработают при следующем входе в Windows, задачи планировщика — сразу.");
        if (!ok) return;

        await RunBusyAsync("Откат изменений автозапуска…", async () =>
        {
            var result = await Task.Run(() => ChangeJournal.Instance.RevertAsync(records));
            if (result.Success)
                Ui.Toast(StartupTexts.RevertDone(result.Reverted), ToastKind.Success, "АВТОЗАПУСК");
            else
                Ui.Toast(StartupTexts.RevertPartial(result.Reverted, result.Failed, result.Errors.FirstOrDefault()), ToastKind.Error, "АВТОЗАПУСК");
            await ScanAsync();
        });
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
            ReportOutcome(outcome, StartupTexts.EntriesResult(items.Select(i => i.DisplayName).ToList(), enable), enable);
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
            ReportOutcome(outcome, StartupTexts.TaskResult(item.DisplayName, enable), enable);
        });
    }

    private static void ReportOutcome(StartupChangeOutcome outcome, string successText, bool enable)
    {
        if (outcome.Success && outcome.Applied > 0)
            Ui.Toast(successText, ToastKind.Success, enable ? "АВТОЗАПУСК ВКЛЮЧЁН" : "АВТОЗАПУСК ОТКЛЮЧЁН");
        else if (outcome.Success)
            Ui.Toast("Изменений не потребовалось.", ToastKind.Info, "АВТОЗАПУСК");
        else
            Ui.Toast(outcome.Errors[0], ToastKind.Error, outcome.Applied > 0 ? $"ПРИМЕНЕНО ЧАСТИЧНО ({outcome.Applied})" : "НЕ ПРИМЕНЕНО");

        // The confirmation already said when System Protection is off; report only an unexpected failure.
        if (outcome.RestorePoint is { Outcome: RestorePointOutcome.Failed or RestorePointOutcome.Skipped } rp && outcome.Applied > 0)
            Ui.Toast($"{rp.Message} Изменения записаны в журнал и откатываются из MONOLITH.", ToastKind.Warning, "ТОЧКА ВОССТАНОВЛЕНИЯ");
    }

    private static string ReadRestorePointNote() =>
        StartupTexts.RestorePointNote(AppSettings.Current.CreateRestorePoints, RestorePointService.IsEnabled());

    /// <summary>Re-reads the setting and System Protection right before a confirmation (registry read off the UI thread).</summary>
    private async Task<string> RefreshRestorePointNoteAsync()
    {
        RestorePointNote = await Task.Run(ReadRestorePointNote);
        return RestorePointNote;
    }

    private static Task<bool> ConfirmEntriesAsync(IReadOnlyList<StartupItem> items, bool enable, string? restorePointNote, int skippedLocked = 0)
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
        if (restorePointNote is not null)
            details.Add(restorePointNote);

        return Ui.ConfirmAsync(
            StartupTexts.ConfirmTitle(items.Count, enable),
            StartupTexts.ConfirmMessage(items.Select(i => i.DisplayName).ToList(), enable),
            details,
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

    /// <summary>
    /// Shows the file in Explorer. The Explorer window is started as the signed-in user (rule 8): a file manager with
    /// MONOLITH's admin token would start everything opened from it as administrator.
    /// </summary>
    [RelayCommand]
    private async Task OpenLocationAsync(IStartupRow? row)
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
                if (!await UnelevatedShell.StartAsUserAsync(explorer, $"/select,\"{file}\""))
                    Ui.Toast($"Не удалось открыть Проводник без прав администратора. Файл: {file}", ToastKind.Error, "ОТКРЫТЬ РАСПОЛОЖЕНИЕ");
                return;
            }

            var dir = row.TargetPath is { } tp && Path.IsPathFullyQualified(tp) ? Path.GetDirectoryName(tp) : null;
            if (dir is not null && FileProbe.DirectoryExists(dir))
            {
                if (await UnelevatedShell.StartAsUserAsync(explorer, $"\"{dir}\""))
                    Ui.Toast($"Файл не найден, открыта папка: {dir}", ToastKind.Warning);
                else
                    Ui.Toast($"Не удалось открыть Проводник без прав администратора. Папка: {dir}", ToastKind.Error, "ОТКРЫТЬ РАСПОЛОЖЕНИЕ");
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

    /// <summary>Searches the file name on the web. The browser never gets MONOLITH's admin token (rule 8).</summary>
    [RelayCommand]
    private async Task SearchOnlineAsync(IStartupRow? row)
    {
        if (row is null) return;
        var term = row.TargetPath is { Length: > 0 } t ? Path.GetFileName(t) : row.DisplayName;
        if (string.IsNullOrWhiteSpace(term)) term = row.DisplayName;
        var url = "https://www.google.com/search?q=" + Uri.EscapeDataString(term);
        if (!await UnelevatedShell.OpenAsync(url))
            Ui.Toast("Не удалось открыть браузер без прав администратора. Поищите имя файла вручную: " + term, ToastKind.Error, "ПОИСК В СЕТИ");
    }

    // ───────────────────────────── State ─────────────────────────────

    private bool EnsureIdle()
    {
        if (!IsBusy) return true;
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
        JournalCount = records.Count;
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
        EnabledShare = TotalCount == 0 ? 0 : EnabledCount * 100.0 / TotalCount;
        TaskCount = Tasks.Count;
        TaskEnabledCount = Tasks.Count(t => t.IsEnabled);
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
}
