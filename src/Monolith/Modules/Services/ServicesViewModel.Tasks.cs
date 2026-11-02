using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Startup;
using Monolith.UI;

namespace Monolith.Modules.Services;

/// <summary>
/// ЗАДАЧИ: Microsoft data-collection tasks from research §4.1, disabled through the journal. The tab also shows
/// third-party logon tasks of the hosted <see cref="Startup"/> page (toggled there, journaled by StartupChanges).
/// </summary>
public sealed partial class ServicesViewModel
{
    private bool _bulkTaskSelect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllTaskCount))]
    private ObservableCollection<TaskItem> _taskItems = [];

    /// <summary>Tab badge: Microsoft data-collection tasks (third-party logon tasks are on the АВТОЗАПУСК tab).</summary>
    public int AllTaskCount => TaskItems.Count;
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
            : Loc.Tf("Нет на этой сборке Windows (или скрыты без прав администратора): {0}.", string.Join(", ", missing));

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

    private bool CanApplyTasks(string? paths) => !IsBusy && CanModify && (paths is not null || SelectedTaskCount > 0);

    [RelayCommand(CanExecute = nameof(CanApplyTasks))]
    private async Task ApplyTasksAsync(string? paths)
    {
        if (!RequireAdmin(Loc.T("отключение задач планировщика"), "ApplyTasks", IntentList.Pack(TaskItems.Where(t => t.IsSelected && t.CanSelect).Select(t => t.Path)))) return;
        if (IntentList.Unpack(paths) is { Count: > 0 } replayed)
        {
            var set = replayed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _bulkTaskSelect = true;
            try
            {
                foreach (var t in TaskItems) t.IsSelected = set.Contains(t.Path) && t.IsSelectable;
            }
            finally
            {
                _bulkTaskSelect = false;
            }
            UpdateTaskStats();
        }
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

        var details = plan.Select(t => Loc.Tf("{0}: Включена → Отключена", t.Title)).ToList();
        if (!await Ui.ConfirmAsync("ОТКЛЮЧИТЬ ЗАДАЧИ?",
                "Задачи перестанут запускаться по расписанию. Сами задачи не удаляются — их можно включить обратно. " +
                "Перед изменением создаётся точка восстановления, исходное состояние сохраняется в журнале.",
                details, "ОТКЛЮЧИТЬ",
                footnote: "Крупные обновления Windows иногда включают задачи заново — просто отключите их снова."))
            return;

        await RunBusyAsync(OperationClass.JournalChange, Loc.T("Отключение задач…"), async () =>
        {
            var session = await BeginSessionAsync(BatchTitle(Loc.Keep("Задачи планировщика"), plan.Select(t => t.RuTitle).ToList()),
                BatchTitle(Loc.T("Задачи планировщика"), plan.Select(t => t.Title).ToList(), localized: true));
            if (session is null) return;
            await Task.Run(() =>
            {
                foreach (var t in plan)
                    session.SetTaskEnabled(t.Path, false, TaskSource, string.Format(Loc.Keep("Задача «{0}»"), t.RuTitle));
                session.Complete();
            });
            await ReportSessionAsync(session, Loc.Tf("Отключено задач: {0}.", session.Applied));
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
        if (string.IsNullOrEmpty(path) || !RequireAdmin(Loc.T("откат изменения задачи"), "RevertTask", path)) return;
        // The row means «back to how it was before Lycoris»: every active change of this task, newest first.
        var layers = ChangeJournal.Instance.ActiveLayers("task:" + path);
        var record = layers.LastOrDefault();
        if (record is null)
        {
            RefreshJournal();
            return;
        }
        var original = layers[0].OldValue;

        var info = await Task.Run(() => ScheduledTasks.Query(path));
        if (!info.Exists)
        {
            await Ui.AlertAsync("ЗАДАЧА НЕ НАЙДЕНА",
                "Задачи больше нет в планировщике (её удалило обновление Windows) — возвращать нечего. При откате такая запись будет пропущена.");
            return;
        }

        var current = info.Enabled ? Loc.T("Включена") : Loc.T("Отключена");
        var drift = !string.Equals(current, record.NewValue, StringComparison.Ordinal);
        var message = drift
            ? Loc.Tf("Состояние задачи уже изменено без Lycoris: сейчас «{0}». Всё равно вернуть исходное «{1}»?", current, original)
            : Loc.T("Задача вернётся в исходное состояние из журнала.");
        if (!await Ui.ConfirmAsync("ОТКАТИТЬ ИЗМЕНЕНИЕ?", message, [$"{record.Title}: {current} → {original}"], "ОТКАТИТЬ", danger: drift))
            return;

        await RunBusyAsync(OperationClass.JournalChange, Loc.T("Откат изменения задачи…"), async () =>
        {
            var result = await Task.Run(() => ChangeJournal.Instance.RevertAsync(layers));
            await ReportRevertAsync(result);
            await RefreshStatesAsync();
        });
    }
}
