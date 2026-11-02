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
/// АВТОЗАПУСК: Run / Run32 / Startup-folder entries and packaged (MSIX) apps' startup tasks (enabled or disabled exactly
/// like Task Manager, never deleted) and third-party scheduled tasks that start at sign-in or boot. The last measured boot
/// time and the programs Windows reported as slowing boot come read-only from the Diagnostics-Performance log.
/// Not a top-level page: <see cref="Services.ServicesViewModel"/> owns it (АВТОЗАПУСК tab; logon tasks on the ЗАДАЧИ tab)
/// and forwards activation. Key/Title are only used for logs.
/// </summary>
public sealed partial class StartupViewModel : PageViewModel
{
    public override string Key => "startup";
    public override string Index => "07";
    public override string Title => Loc.T("АВТОЗАПУСК");
    public override string Subtitle => Loc.T("Программы и задачи, которые запускаются вместе с Windows.");
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
    /// <summary>Enabled entries Windows measured as slowing boot (the sidebar badge and the «ЗАМЕДЛЯЕТ ЗАГРУЗКУ» chip).</summary>
    [ObservableProperty] private int _slowCount;
    /// <summary>The N of «ОТКЛЮЧИТЬ ЛИШНЕЕ · N»: slowing boot or pointing to a missing file, and allowed to switch off.</summary>
    [ObservableProperty] private int _extraCount;
    [ObservableProperty] private int _taskCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _allSelected;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _scanText = Loc.T("Сканирование…");
    [ObservableProperty] private string? _tasksError;
    /// <summary>« · Последняя загрузка …» appended to the tab's muted line; empty without data / admin rights.</summary>
    [ObservableProperty] private string _bootText = "";
    [ObservableProperty] private string? _bootTooltip;

    public StartupViewModel()
    {
        ItemsView = new ListCollectionView(Items) { Filter = o => o is StartupItem i && Matches(i) };
        ChangeJournal.Instance.Changed += () => Ui.Post(UpdateJournalState);
    }

    /// <summary>The host page is busy with its own work (changes must not overlap).</summary>
    public Func<bool>? IsHostBusy { get; set; }

    public string TasksCountText => Loc.N(TaskCount, "{0} ЗАДАЧА", "{0} ЗАДАЧИ", "{0} ЗАДАЧ");
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
    private Task RefreshAsync() => RunBusyAsync(OperationClass.Measure, Loc.T("Сканирование автозапуска…"), ScanAsync);

    private async Task ScanAsync()
    {
        var entriesTask = StaWorker.Run(StartupScanner.Scan, "MONOLITH startup scan");
        var tasksTask = StaWorker.Run(() => LogonTaskScanner.Scan(), "MONOLITH task scan");
        var bootTask = Task.Run(BootPerformance.Read);

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
            TasksError = Loc.Tf("Не удалось прочитать планировщик заданий: {0}", ex.Message);
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
                var item = new StartupItem(e) { IsSelected = selected.Contains(e.Key) };
                item.PropertyChanged += OnItemPropertyChanged;
                Items.Add(item);
            }
        }

        if (!Tasks.Select(t => t.Info).SequenceEqual(tasks))
        {
            Tasks.Clear();
            foreach (var t in tasks) Tasks.Add(new LogonTaskItem(t));
        }

        ApplyBootReport(await ReadBootAsync(bootTask));
        UpdateJournalState();
        RefreshView();
        Recount();
        ScanText = Loc.T("Источники: реестр, папки автозагрузки, приложения MSIX, планировщик.");
        MarkChecked();
        IsLoaded = true;
        Log.Info("Автозапуск: записей {0} (включено {1}), задач при входе {2}.", TotalCount, EnabledCount, TaskCount);
    }

    private static async Task<BootReport?> ReadBootAsync(Task<BootReport?> read)
    {
        try
        {
            return await read;
        }
        catch (Exception ex)
        {
            Log.Warn("Автозапуск: не удалось прочитать время загрузки: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>Boot summary for the muted line and «ЗАМЕДЛЯЕТ ЗАГРУЗКУ» tags (nothing at all without data).</summary>
    private void ApplyBootReport(BootReport? report)
    {
        var delays = report?.Delays ?? [];
        foreach (var i in Items) i.BootDelay = BootPerformance.Match(i.TargetPath, delays);
        foreach (var t in Tasks) t.BootDelay = BootPerformance.Match(t.TargetPath, delays);
        BootText = BootPerformance.SummaryText(report) is { } summary ? " · " + summary : "";
        BootTooltip = BootPerformance.SummaryTooltip(report);
    }

    // ───────────────────────────── Toggles ─────────────────────────────

    [RelayCommand]
    private async Task ToggleAsync(StartupItem? item)
    {
        if (item is null) return;
        try
        {
            if (!EnsureIdle() || !RequireAdmin(Loc.T("изменение автозапуска"), "ToggleStartup", ToggleParameter(item.IsEnabled, item.Key))) return;
            var enable = !item.IsEnabled;
            if (!item.CanChange)
            {
                Ui.Toast(item.ToggleTooltip, ToastKind.Warning);
                return;
            }
            if (!enable && !item.CanDisable)
            {
                Ui.Toast(Loc.Tf("«{0}» — значок защиты системы. MONOLITH его не отключает.", item.DisplayName), ToastKind.Warning);
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
            if (!EnsureIdle() || !RequireAdmin(Loc.T("изменение задач при входе"), "ToggleLogonTask", ToggleParameter(item.IsEnabled, item.Path))) return;
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

    /// <summary>The admin-intent replay of a single switch: «ToggleStartup» + the entry key.</summary>
    [RelayCommand]
    private Task ToggleKeyAsync(string? parameter)
    {
        var (wantEnabled, key) = ParseToggleParameter(parameter);
        if (Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)) is not { } item) return Task.CompletedTask;
        if (wantEnabled is { } want && item.IsEnabled == want) return AlreadyThere(item.DisplayName, want);
        return ToggleAsync(item);
    }

    /// <summary>The intent remembers the state the user asked for («on:»/«off:» + key), not a bare toggle: a replay never flips an entry back.</summary>
    internal static string ToggleParameter(bool currentlyEnabled, string key) => (currentlyEnabled ? "off:" : "on:") + key;

    internal static (bool? WantEnabled, string Key) ParseToggleParameter(string? parameter)
    {
        if (parameter is null) return (null, "");
        if (parameter.StartsWith("on:", StringComparison.Ordinal)) return (true, parameter[3..]);
        if (parameter.StartsWith("off:", StringComparison.Ordinal)) return (false, parameter[4..]);
        return (null, parameter);
    }

    private static Task AlreadyThere(string name, bool enabled)
    {
        Ui.Toast(enabled ? Loc.Tf("«{0}» уже включён — менять нечего.", name) : Loc.Tf("«{0}» уже отключён — менять нечего.", name), ToastKind.Info);
        return Task.CompletedTask;
    }

    /// <summary>The admin-intent replay of a logon-task switch: «ToggleLogonTask» + the task path.</summary>
    [RelayCommand]
    private Task ToggleTaskKeyAsync(string? parameter)
    {
        var (wantEnabled, path) = ParseToggleParameter(parameter);
        if (Tasks.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)) is not { } item) return Task.CompletedTask;
        if (wantEnabled is { } want && item.IsEnabled == want) return AlreadyThere(item.DisplayName, want);
        return ToggleTaskAsync(item);
    }

    /// <summary>Re-checks exactly the entries of a replayed intent (the checked rows before the relaunch).</summary>
    private void SelectOnly(string? packedKeys)
    {
        var keys = IntentList.Unpack(packedKeys);
        if (keys.Count == 0) return;
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _syncingSelection = true;
        try
        {
            foreach (var i in Items) i.IsSelected = set.Contains(i.Key);
        }
        finally
        {
            _syncingSelection = false;
        }
        UpdateSelection();
    }

    private string? SelectedKeys() => IntentList.Pack(Items.Where(i => i.IsSelected).Select(i => i.Key));

    /// <summary>
    /// The page's one main button: switches off what is only extra — entries Windows measured as slowing boot and entries
    /// whose file is gone. Nothing outside that list is touched; the confirmation lists every entry.
    /// </summary>
    [RelayCommand]
    private async Task DisableExtraAsync()
    {
        if (!EnsureIdle() || !RequireAdmin(Loc.T("отключение лишнего автозапуска"), "DisableExtraStartup")) return;
        var targets = Items.Where(IsExtra).ToList();
        if (targets.Count == 0)
        {
            Ui.Toast("Лишнего в автозапуске не найдено: нет записей, которые замедляют загрузку или указывают на удалённые файлы.", ToastKind.Info);
            return;
        }
        if (!await ConfirmEntriesAsync(targets, enable: false)) return;
        await ApplyEntriesAsync(targets, enable: false, createRestorePoint: targets.Count > 1);
    }

    /// <summary>Extra = enabled, allowed to switch off, and either slows boot (Windows measured it) or points to a missing file.</summary>
    internal static bool IsExtra(StartupItem i) => i.IsEnabled && i.CanDisable && i.CanChange && (i.ShowBootDelay || !i.TargetExists);

    [RelayCommand]
    private async Task DisableSelectedAsync(string? keys)
    {
        if (!EnsureIdle() || !RequireAdmin(Loc.T("отключение автозапуска"), "DisableSelectedStartup", SelectedKeys())) return;
        SelectOnly(keys);
        var chosen = Items.Where(i => i.IsSelected).ToList();
        var locked = chosen.Where(i => i.IsEnabled && (!i.CanDisable || !i.CanChange)).ToList();
        var targets = chosen.Where(i => i.IsEnabled && i.CanDisable && i.CanChange).ToList();
        if (targets.Count == 0)
        {
            Ui.Toast(locked.Count > 0
                ? Loc.T("Среди выбранных только защитные записи или задачи под управлением политики — их MONOLITH не отключает.")
                : Loc.T("Все выбранные записи уже отключены."), ToastKind.Info);
            return;
        }
        if (!await ConfirmEntriesAsync(targets, enable: false, skippedLocked: locked.Count)) return;
        await ApplyEntriesAsync(targets, enable: false, createRestorePoint: targets.Count > 1);
    }

    [RelayCommand]
    private async Task EnableSelectedAsync(string? keys)
    {
        if (!EnsureIdle() || !RequireAdmin(Loc.T("включение автозапуска"), "EnableSelectedStartup", SelectedKeys())) return;
        SelectOnly(keys);
        var targets = Items.Where(i => i.IsSelected && !i.IsEnabled && i.CanChange).ToList();
        if (targets.Count == 0)
        {
            Ui.Toast(Items.Any(i => i.IsSelected && !i.IsEnabled)
                ? Loc.T("Выбранные задачи отключены политикой или в неизвестном состоянии — MONOLITH их не меняет.")
                : Loc.T("Все выбранные записи уже включены."), ToastKind.Info);
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
        await RunBusyAsync(OperationClass.JournalChange, enable ? Loc.T("Включение автозапуска…") : Loc.T("Отключение автозапуска…"), async () =>
        {
            var progress = new Progress<string>(t => BusyText = t);
            var entries = items.Select(i => i.Entry).ToList();
            var outcome = await Task.Run(() => StartupChanges.SetEntriesAsync(entries, enable, createRestorePoint, progress));

            foreach (var item in items)
            {
                var fresh = await Task.Run(() => StartupScanner.Refresh(item.Entry));
                item.Update(fresh);
            }
            UpdateJournalState();
            Recount();
            RefreshView();
            ReportOutcome(outcome, items.Select(i => i.DisplayName).ToList(), enable, Loc.T("Изменение вступит в силу при следующем входе в Windows."));
        });
    }

    private async Task ApplyTaskAsync(LogonTaskItem item, bool enable)
    {
        await RunBusyAsync(OperationClass.JournalChange, enable ? Loc.T("Включение задачи…") : Loc.T("Отключение задачи…"), async () =>
        {
            var progress = new Progress<string>(t => BusyText = t);
            var outcome = await Task.Run(() => StartupChanges.SetTasksAsync([item.Info], enable, false, progress));
            var state = await Task.Run(() => LogonTaskScanner.ReadEnabled(item.Path));
            if (state is { } on) item.Update(on);
            UpdateJournalState();
            Recount();
            ReportOutcome(outcome, [item.DisplayName], enable, Loc.T("Планировщик применяет изменение сразу."));
        });
    }

    private static void ReportOutcome(StartupChangeOutcome outcome, IReadOnlyList<string> names, bool enable, string tail)
    {
        var subject = names.Count == 1 ? $"«{names[0]}»" : Loc.N(names.Count, "{0} запись", "{0} записи", "{0} записей");
        if (outcome.Success && outcome.Applied > 0)
        {
            var text = enable
                ? (names.Count == 1 ? Loc.Tf("{0} снова будет запускаться вместе с Windows. {1}", subject, tail) : Loc.Tf("{0} снова будут запускаться вместе с Windows. {1}", subject, tail))
                : (names.Count == 1
                    ? Loc.Tf("{0} больше не будет запускаться при входе. Вернуть можно здесь же или в разделе «Восстановление».", subject)
                    : Loc.Tf("{0} больше не будут запускаться при входе. Вернуть можно здесь же или в разделе «Восстановление».", subject));
            Ui.Toast(text, ToastKind.Success, enable ? Loc.T("АВТОЗАПУСК ВКЛЮЧЁН") : Loc.T("АВТОЗАПУСК ОТКЛЮЧЁН"));
        }
        else if (outcome.Success)
        {
            Ui.Toast("Изменений не потребовалось.", ToastKind.Info, "АВТОЗАПУСК");
        }
        else
        {
            Ui.Toast(outcome.Errors[0], ToastKind.Error, outcome.Applied > 0 ? Loc.Tf("ПРИМЕНЕНО ЧАСТИЧНО ({0})", outcome.Applied) : Loc.T("НЕ ПРИМЕНЕНО"));
        }
    }

    private static Task<bool> ConfirmEntriesAsync(IReadOnlyList<StartupItem> items, bool enable, int skippedLocked = 0)
    {
        var details = new List<string>();
        string? stateNote = null;
        var restoresMark = false;
        foreach (var i in items)
        {
            // Packaged tasks: the State the switch really leaves — back to the state from before MONOLITH restores that exact value.
            var state = i.IsPackaged ? PackagedStartup.StateAfterSwitch(enable, StartupChanges.PackagedStateBefore(i.Entry)) : (int?)null;
            if (state.HasValue)
            {
                stateNote ??= PackagedStartup.StateNote(state.Value);
                restoresMark |= state.Value != PackagedStartup.TargetState(enable);
            }
            if (items.Count == 1 && state.HasValue)
            {
                details.Add(Loc.Tf("Приложение MSIX: {0}, задача автозапуска «{1}»", i.Entry.PackageFamilyName, i.Entry.Name));
                if (i.TargetPath is not null) details.Add(Loc.Tf("Программа: {0}", i.Command));
                details.Add(Loc.Tf("Отметка: {0} = {1}", StartupLocations.DescribeMark(i.Entry), PackagedStartup.SwitchText(enable, state.Value)));
            }
            else if (items.Count == 1)
            {
                details.Add(Loc.Tf("Запись: {0} → {1}", StartupLocations.Describe(i.Entry.Source), i.Entry.Name));
                details.Add(Loc.Tf("Команда: {0}", i.Command));
                details.Add(Loc.Tf("Отметка: {0} = ", StartupLocations.DescribeMark(i.Entry)) +
                            (enable ? Loc.T("02 00 00 00 … (включено)") : Loc.T("03 00 00 00 + время отключения")));
            }
            else
            {
                details.Add($"{i.DisplayName} — {(i.IsMachine ? Loc.T("для всех") : Loc.T("для меня"))}, {LowerFirst(i.SourceText)}" +
                            (state is { } restored && restored != PackagedStartup.TargetState(enable) ? $", State = {PackagedStartup.SwitchText(enable, restored)}" : ""));
            }

            switch (i.Risk)
            {
                case StartupRisk.Microsoft when !enable:
                    details.Add(Loc.Tf("Внимание ({0}): компонент Microsoft — может пропасть значок в трее или фоновая функция (синхронизация, уведомления).", i.DisplayName));
                    break;
                case StartupRisk.Driver when !enable:
                    details.Add(Loc.Tf("Внимание ({0}): помощник драйвера — могут пропасть панель звука/тачпада/видеокарты, горячие клавиши или эффекты.", i.DisplayName));
                    break;
            }
        }
        if (stateNote is not null) details.Add(stateNote);
        if (enable && items.Any(i => i.IsPackaged))
            details.Add(Loc.T("Приложение MSIX Windows запускает при входе, только если его хотя бы раз открывали."));
        if (skippedLocked > 0)
            details.Add(Loc.Tf("Пропущено записей: {0} (Безопасность Windows, антивирусы и задачи под управлением политики MONOLITH не отключает).", skippedLocked));
        if (items.Count > 1)
            details.Add(Loc.T("Перед изменением MONOLITH создаст точку восстановления (если это включено в разделе «Восстановление»)."));

        var subject = items.Count == 1 ? $"«{items[0].DisplayName}»" : Loc.N(items.Count, "{0} запись", "{0} записи", "{0} записей");
        var markNote = restoresMark ? Loc.T(" (или возвращает отметку, что была до него)") : "";
        var message = enable
            ? (items.Count == 1 ? Loc.Tf("{0} снова будет запускаться при входе в Windows.", subject) : Loc.Tf("{0} снова будут запускаться при входе в Windows.", subject))
            : (Loc.RuForm(items.Count) == 0
                ? Loc.Tf("{0} перестанет запускаться при входе в Windows. Ничего не удаляется: MONOLITH ставит отметку «отключено» так же, как Диспетчер задач{1}. Уже запущенные программы продолжат работать до перезагрузки.", subject, markNote)
                : Loc.Tf("{0} перестанут запускаться при входе в Windows. Ничего не удаляется: MONOLITH ставит отметку «отключено» так же, как Диспетчер задач{1}. Уже запущенные программы продолжат работать до перезагрузки.", subject, markNote));

        return Ui.ConfirmAsync(
            enable ? (items.Count == 1 ? Loc.T("ВКЛЮЧИТЬ АВТОЗАПУСК?") : Loc.Tf("ВКЛЮЧИТЬ {0}?", Loc.N(items.Count, "{0} ЗАПИСЬ", "{0} ЗАПИСИ", "{0} ЗАПИСЕЙ")))
                   : (items.Count == 1 ? Loc.T("ОТКЛЮЧИТЬ АВТОЗАПУСК?") : Loc.Tf("ОТКЛЮЧИТЬ {0}?", Loc.N(items.Count, "{0} ЗАПИСЬ", "{0} ЗАПИСИ", "{0} ЗАПИСЕЙ"))),
            message, details,
            confirmText: enable ? Loc.T("ВКЛЮЧИТЬ") : Loc.T("ОТКЛЮЧИТЬ"),
            footnote: "Вернуть: переключатель на этой странице, Диспетчер задач → «Автозагрузка» или раздел «Восстановление». " +
                      "Изменение вступит в силу при следующем входе в Windows.");
    }

    private static Task<bool> ConfirmTaskAsync(LogonTaskItem item)
    {
        var details = new List<string>
        {
            Loc.Tf("Задача: {0}", item.Path),
            Loc.Tf("Действие: {0}", item.Command),
            Loc.Tf("Запуск: {0} ({1})", item.TriggerText.ToLowerInvariant(), item.TriggerDetail),
        };
        if (item.Info.OtherTriggers > 0)
            details.Add(Loc.Tf("У задачи есть ещё триггеры ({0}: по расписанию, при простое и т. п.) — задача отключается целиком, они тоже перестанут срабатывать.", item.Info.OtherTriggers));
        if (item.Risk == StartupRisk.Driver)
            details.Add(Loc.T("Внимание: задача производителя оборудования — могут пропасть функции драйвера (панель, горячие клавиши)."));
        if (item.Risk == StartupRisk.Microsoft)
            details.Add(Loc.T("Внимание: исполняемый файл подписан Microsoft — проверьте, что задача вам не нужна."));

        return Ui.ConfirmAsync("ОТКЛЮЧИТЬ ЗАДАЧУ?",
            Loc.Tf("Задача «{0}» больше не будет запускаться планировщиком Windows. Сама задача не удаляется.", item.DisplayName),
            details,
            confirmText: "ОТКЛЮЧИТЬ",
            footnote: "Вернуть: переключатель на этой странице, «Планировщик заданий» Windows или раздел «Восстановление».");
    }

    // ───────────────────────────── Row actions ─────────────────────────────

    [RelayCommand]
    private async Task OpenLocation(IStartupRow? row)
    {
        if (row is null) return;
        try
        {
            string? file = null;
            if (row.TargetPath is { } t && Path.IsPathFullyQualified(t) && FileProbe.FileExists(t)) file = t;
            else if (row.ItemPath is { } p && FileProbe.FileExists(p)) file = p;

            if (file is not null)
            {
                await OpenExplorerAsync($"/select,\"{file}\"");
                return;
            }

            var dir = row.TargetPath is { } tp && Path.IsPathFullyQualified(tp) ? Path.GetDirectoryName(tp) : null;
            if (dir is not null && FileProbe.DirectoryExists(dir))
            {
                await OpenExplorerAsync($"\"{dir}\"");
                Ui.Toast(Loc.Tf("Файл не найден, открыта папка: {0}", dir), ToastKind.Warning);
                return;
            }

            Ui.Toast(row.TargetPath is null
                ? Loc.T("У этой записи нет файла на диске, открывать нечего.")
                : Loc.Tf("Файл и его папка не найдены: {0}", row.TargetPath), ToastKind.Warning, "НЕТ ФАЙЛА");
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

    /// <summary>Explorer window as the signed-in user (never with MONOLITH's admin token, CLAUDE.md §8).</summary>
    private static async Task OpenExplorerAsync(string arguments)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!await UnelevatedShell.StartAsUserAsync(explorer, arguments))
            Ui.Toast("Не удалось открыть Проводник без прав администратора.", ToastKind.Warning);
    }

    [RelayCommand]
    private async Task SearchOnline(IStartupRow? row)
    {
        if (row is null) return;
        var term = BootPerformance.FileName(row.TargetPath) ?? row.DisplayName;
        if (string.IsNullOrWhiteSpace(term)) term = row.DisplayName;
        var url = "https://www.google.com/search?q=" + Uri.EscapeDataString(term);
        // The browser is started through the desktop shell, so it never runs with administrator rights.
        if (await UnelevatedShell.OpenAsync(url)) return;
        try
        {
            Clipboard.SetText(url);
            Ui.Toast("Не удалось открыть браузер без прав администратора. Ссылка на поиск скопирована в буфер обмена.", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Warn("Автозапуск: буфер обмена недоступен: {0}", ex.Message);
            Ui.Toast(Loc.Tf("Не удалось открыть браузер. Поиск: {0}", term), ToastKind.Warning);
        }
    }

    // ───────────────────────────── State ─────────────────────────────

    private bool EnsureIdle()
    {
        if (!IsBusy && IsHostBusy?.Invoke() != true) return true;
        Ui.Toast(Loc.T("Дождитесь окончания текущей операции: ") + (BusyText ?? Loc.T("идёт работа")), ToastKind.Info);
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
        foreach (var i in Items)
        {
            i.IsJournaled = targets.Contains(StartupLocations.JournalTarget(i.Entry));
            i.PackageStateBefore = StartupChanges.PackagedStateBefore(i.Entry);
        }
        foreach (var t in Tasks) t.IsJournaled = targets.Contains($"task:{t.Path}");
    }

    private void Recount()
    {
        TotalCount = Items.Count;
        EnabledCount = Items.Count(i => i.IsEnabled);
        DisabledCount = TotalCount - EnabledCount;
        MissingCount = Items.Count(i => !i.TargetExists);
        SlowCount = Items.Count(i => i.ShowBootDelay);
        ExtraCount = Items.Count(IsExtra);
        BadgeText = SlowCount > 0 ? SlowCount.ToString() : null;
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
            "slow" => i.ShowBootDelay,
            _ => true,
        };
        if (!passFilter) return false;

        var q = Search?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return Contains(i.DisplayName, q) || Contains(i.Entry.Name, q) || Contains(i.Command, q) || Contains(i.Entry.Publisher, q)
               || Contains(i.Entry.PackageFamilyName, q);

        static bool Contains(string? s, string q) => s?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    internal static string Plural(int n, string one, string few, string many)
    {
        var n10 = n % 10;
        var n100 = n % 100;
        if (n10 == 1 && n100 != 11) return one;
        if (n10 is >= 2 and <= 4 && n100 is < 12 or > 14) return few;
        return many;
    }
}
