using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Services;

/// <summary>ЗАДАЧИ ПЛАНИРОВЩИКА: telemetry tasks from research §4.1, disabled through the journal.</summary>
public sealed partial class ServicesViewModel
{
    private bool _bulkTaskSelect;

    [ObservableProperty] private ObservableCollection<TaskItem> _taskItems = [];
    [ObservableProperty] private string _missingTasksText = "";
    [ObservableProperty] private int _taskEnabledCount;
    [ObservableProperty] private int _taskDisabledCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyTasksCommand), nameof(ClearTaskSelectionCommand))]
    private int _selectedTaskCount;

    private void BuildTasks(List<ResolvedTask> resolved)
    {
        var firstBuild = TaskItems.Count == 0;
        var previous = TaskItems.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        var items = new List<TaskItem>();
        var missing = new List<string>();
        var can = CanModify;

        _bulkTaskSelect = true;
        try
        {
            foreach (var r in resolved)
            {
                if (!r.Exists)
                {
                    missing.Add(TaskScanner.Split(r.Path).Name + (r.Entry.IsPrefix ? "*" : ""));
                    continue;
                }
                if (!previous.TryGetValue(r.Path, out var item))
                    item = new TaskItem(r.Entry, r.Path) { SelectionChanged = OnTaskSelectionChanged };
                item.Enabled = r.Enabled;
                item.CanModify = can;
                item.IsRecommended = r.Entry.IsPreselectedIn(SelectedProfile);
                item.Journal = ChangeJournal.Instance.FindActive("task:" + item.Path);
                if (!item.IsSelectable) item.IsSelected = false;
                items.Add(item);
            }
        }
        finally
        {
            _bulkTaskSelect = false;
        }

        TaskItems = new ObservableCollection<TaskItem>(items);
        MissingTasksText = missing.Count == 0
            ? ""
            : $"Нет на этой сборке Windows (или скрыты без прав администратора): {string.Join(", ", missing)}.";

        if (firstBuild) ApplyProfileToTasks();
        else UpdateTaskStats();
    }

    private void ApplyProfileToTasks()
    {
        _bulkTaskSelect = true;
        try
        {
            foreach (var t in TaskItems)
            {
                t.IsRecommended = t.Entry.IsPreselectedIn(SelectedProfile);
                t.IsSelected = t.IsRecommended && t.IsSelectable;
            }
        }
        finally
        {
            _bulkTaskSelect = false;
        }
        UpdateTaskStats();
    }

    private void OnTaskSelectionChanged(TaskItem item)
    {
        if (_bulkTaskSelect) return;
        UpdateTaskStats();
    }

    private void UpdateTaskStats()
    {
        TaskEnabledCount = TaskItems.Count(t => t.Enabled);
        TaskDisabledCount = TaskItems.Count(t => !t.Enabled);
        SelectedTaskCount = TaskItems.Count(t => t.IsSelected && t.IsSelectable);
    }

    private bool CanApplyTasks() => !IsBusy && CanModify && SelectedTaskCount > 0;

    [RelayCommand(CanExecute = nameof(CanApplyTasks))]
    private async Task ApplyTasksAsync()
    {
        if (!RequireAdmin("отключение задач планировщика")) return;
        var selected = TaskItems.Where(t => t.IsSelected && t.CanSelect).ToList();
        if (selected.Count == 0) return;

        var live = await Task.Run(() => selected.Select(t => (Item: t, Info: ScheduledTasks.Query(t.Path))).ToList());
        var plan = live.Where(p => p.Info.Exists && p.Info.Enabled).Select(p => p.Item).ToList();
        if (plan.Count == 0)
        {
            Ui.Toast("Выбранные задачи уже отключены.", ToastKind.Info);
            await RefreshStatesAsync();
            return;
        }

        var details = plan.Select(t => $"{t.Title}: Включена → Отключена").ToList();
        if (!await Ui.ConfirmAsync("ОТКЛЮЧИТЬ ЗАДАЧИ?",
                "Задачи перестанут запускаться по расписанию. Сами задачи не удаляются — их можно включить обратно. " +
                "Перед изменением создаётся точка восстановления, исходное состояние сохраняется в журнале.",
                details, "ОТКЛЮЧИТЬ",
                footnote: "Крупные обновления Windows иногда включают задачи заново — просто отключите их снова."))
            return;

        await RunBusyAsync("Отключение задач…", async () =>
        {
            var session = await BeginSessionAsync(BatchTitle("Задачи планировщика", plan.Select(t => t.Title).ToList()));
            if (session is null) return;
            await Task.Run(() =>
            {
                foreach (var t in plan)
                    session.SetTaskEnabled(t.Path, false, TaskSource, $"Задача «{t.Title}»");
                session.Complete();
            });
            await ReportSessionAsync(session, $"Отключено задач: {session.Applied}.");
            await RefreshStatesAsync();
        });
    }

    private bool CanClearTaskSelection() => SelectedTaskCount > 0;

    [RelayCommand(CanExecute = nameof(CanClearTaskSelection))]
    private void ClearTaskSelection()
    {
        _bulkTaskSelect = true;
        try
        {
            foreach (var t in TaskItems) t.IsSelected = false;
        }
        finally
        {
            _bulkTaskSelect = false;
        }
        UpdateTaskStats();
    }

    [RelayCommand(CanExecute = nameof(CanRowAction))]
    private async Task RevertTaskAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !RequireAdmin("откат изменения задачи")) return;
        var record = ChangeJournal.Instance.FindActive("task:" + path);
        if (record is null)
        {
            RefreshJournal();
            return;
        }

        var info = await Task.Run(() => ScheduledTasks.Query(path));
        if (!info.Exists)
        {
            await Ui.AlertAsync("ЗАДАЧА НЕ НАЙДЕНА",
                "Задачи больше нет в планировщике (её удалило обновление Windows) — возвращать нечего. Запись можно удалить в разделе «Восстановление».");
            return;
        }

        var current = info.Enabled ? "Включена" : "Отключена";
        var drift = !string.Equals(current, record.NewValue, StringComparison.Ordinal);
        var message = drift
            ? $"Состояние задачи уже изменено без MONOLITH: сейчас «{current}». Всё равно вернуть исходное «{record.OldValue}»?"
            : "Задача вернётся в исходное состояние из журнала.";
        if (!await Ui.ConfirmAsync("ОТКАТИТЬ ИЗМЕНЕНИЕ?", message, [$"{record.Title}: {current} → {record.OldValue}"], "ОТКАТИТЬ", danger: drift))
            return;

        await RunBusyAsync("Откат изменения задачи…", async () =>
        {
            var result = await Task.Run(() => ChangeJournal.Instance.RevertAsync([record]));
            await ReportRevertAsync(result);
            await RefreshStatesAsync();
        });
    }
}
