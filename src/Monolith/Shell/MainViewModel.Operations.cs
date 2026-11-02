using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Shell;

/// <summary>One row of the «ЗАДАЧИ» list: a running or queued operation.</summary>
public sealed partial class OperationItemViewModel : ObservableObject
{
    public OperationItemViewModel(OperationInfo info) => Apply(info);

    public long Id { get; private set; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _classText = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _isQueued;
    /// <summary>Why the cancel button is missing («прервать нельзя»), as text next to the row.</summary>
    [ObservableProperty] private string? _noCancelText;

    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool HasNoCancelText => !string.IsNullOrEmpty(NoCancelText);
    partial void OnNoteChanged(string? value) => OnPropertyChanged(nameof(HasNote));
    partial void OnNoCancelTextChanged(string? value) => OnPropertyChanged(nameof(HasNoCancelText));

    public void Apply(OperationInfo i)
    {
        Id = i.Id;
        Name = i.Name;
        ClassText = i.ClassText;
        IsQueued = i.State == OperationState.Queued;
        StateText = i.State switch
        {
            OperationState.Queued => "В ОЧЕРЕДИ",
            OperationState.Cancelling => "ОСТАНАВЛИВАЕТСЯ",
            _ => "ИДЁТ",
        };
        Note = i.WaitingFor ?? i.Detail;
        CanCancel = i.CanCancel && i.State != OperationState.Cancelling;
        NoCancelText = !i.CanCancel && i.State == OperationState.Running ? "прервать нельзя — дождитесь окончания" : null;
    }
}

public sealed partial class MainViewModel
{
    private readonly OperationCoordinator _coordinator = OperationCoordinator.Instance;
    private int _operationsRefreshPending;

    /// <summary>The «ЗАДАЧИ» list: running and queued operations (changed on the UI thread only).</summary>
    public ObservableCollection<OperationItemViewModel> Operations { get; } = new();

    /// <summary>The popup above the status bar is open.</summary>
    [ObservableProperty] private bool _tasksOpen;

    /// <summary>«ЗАДАЧИ · 2» on the status-bar button.</summary>
    public string TasksButtonText => $"ЗАДАЧИ · {Operations.Count}";
    public bool HasOperations => Operations.Count > 0;
    public string TasksSummary => Operations.Count == 0
        ? "Сейчас ничего не выполняется и ничего не ждёт."
        : $"Идёт: {Operations.Count(o => !o.IsQueued)} · в очереди: {Operations.Count(o => o.IsQueued)}. Задачи, которые мешают друг другу, идут по очереди.";

    private void HookOperations()
    {
        _coordinator.Changed += OnOperationsChanged;
        Operations.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TasksButtonText));
            OnPropertyChanged(nameof(HasOperations));
            OnPropertyChanged(nameof(TasksSummary));
        };
    }

    /// <summary>Any thread: coalesces bursts into one refresh on the UI thread.</summary>
    private void OnOperationsChanged()
    {
        if (Interlocked.Exchange(ref _operationsRefreshPending, 1) == 1) return;
        Ui.Post(RefreshOperations);
    }

    internal void RefreshOperations()
    {
        Interlocked.Exchange(ref _operationsRefreshPending, 0);
        var listed = _coordinator.Snapshot().Where(o => o.IsListed).ToList();
        var same = listed.Count == Operations.Count && listed.Select(o => o.Id).SequenceEqual(Operations.Select(o => o.Id));
        if (same)
        {
            for (var i = 0; i < listed.Count; i++) Operations[i].Apply(listed[i]);
        }
        else
        {
            // Rebuild (the list is tiny); items that stay keep their identity so the rows do not flicker.
            var byId = Operations.ToDictionary(o => o.Id);
            Operations.Clear();
            foreach (var info in listed)
            {
                if (byId.TryGetValue(info.Id, out var existing)) { existing.Apply(info); Operations.Add(existing); }
                else Operations.Add(new OperationItemViewModel(info));
            }
        }
        OnPropertyChanged(nameof(TasksSummary));
        // A «не запущено, потому что…» note is only true while the thing it names is still going on.
        if (listed.Count == 0)
            foreach (var page in Pages)
                if (page.OperationNote is not null && !page.IsBusy) page.OperationNote = null;
    }

    /// <summary>
    /// True when a user-visible operation (install, repair, change, cleanup — not a warm-up or a measurement) started by the
    /// page <paramref name="pageKey"/> (or a tab it hosts) is running or queued.
    /// </summary>
    internal bool UserOperationRunningFor(string pageKey)
    {
        string[] owners = pageKey switch
        {
            "maintenance" => ["maintenance", "cleaner", "tools"],
            "services" => ["services", "startup"],
            _ => [pageKey],
        };
        return _coordinator.Snapshot().Any(o => o.IsListed && o.Owner is { } owner && owners.Contains(owner));
    }

    /// <summary>
    /// QA only ("--snapshot … --demo-tasks"): fills the task list with pretend operations (one running installer, two queued
    /// ones) and opens it, so the status-bar button, the popup and the note strip can be looked at. Nothing is installed or
    /// changed — the operations are only leases that are never used.
    /// </summary>
    internal async Task StartDemoTasksAsync()
    {
        var install = await _coordinator.AcquireAsync(new OperationRequest(OperationClass.Install, "Установка Steam") { Owner = "apps" });
        _ = install; // held until the process ends
        _ = Task.Run(() => _coordinator.AcquireAsync(new OperationRequest(OperationClass.JournalChange, "Применение изменений") { Owner = "optimize" }));
        _ = Task.Run(() => _coordinator.AcquireAsync(new OperationRequest(OperationClass.Cleanup, "Очистка временных файлов") { Owner = "cleaner" }));
        await Task.Delay(400);
        RefreshOperations();
        foreach (var page in Pages) page.OperationNote = "Сейчас идёт установка Steam — дождитесь окончания. Применение изменений и очистка ждут в очереди.";
        TasksOpen = true;
    }

    [RelayCommand]
    private void ToggleTasks() => TasksOpen = !TasksOpen;

    [RelayCommand]
    private void CloseTasks() => TasksOpen = false;

    /// <summary>ОТМЕНИТЬ in the list: a queued item leaves the queue; a running one stops only where that is safe.</summary>
    [RelayCommand]
    private void CancelOperation(OperationItemViewModel? item)
    {
        if (item is null) return;
        if (!_coordinator.Cancel(item.Id))
            Ui.Toast("Эту задачу нельзя прервать безопасно — дождитесь её окончания.", ToastKind.Info, item.Name);
    }

    /// <summary>The note of the current page (refused / queued) is dismissed by its «ЗАКРЫТЬ» button.</summary>
    [RelayCommand]
    private void DismissOperationNote()
    {
        if (CurrentPage is { } page) page.OperationNote = null;
    }

    /// <summary>
    /// The window is closing: with operations running, one dialog lists them (wait / cancel the safe ones and exit / exit anyway).
    /// Returns true when the window may close now. Installers and repair tools are never killed: they keep running on their own.
    /// </summary>
    public async Task<bool> ConfirmExitAsync()
    {
        var plan = ExitPlan.From(_coordinator.Snapshot());
        if (!plan.NeedsDialog) return true;
        var choice = await Ui.ChooseAsync("Задачи ещё выполняются", plan.Message, plan.Lines,
            confirmText: "ВЫЙТИ КАК ЕСТЬ", extraText: plan.SafeToCancel > 0 ? "ОТМЕНИТЬ БЕЗОПАСНЫЕ И ВЫЙТИ" : null, cancelText: "ЖДАТЬ", danger: plan.NotCancellable > 0);
        switch (choice)
        {
            case Ui.Choice.Confirm:
                _coordinator.BeginClosing();
                return true;
            case Ui.Choice.Extra when plan.SafeToCancel > 0:
                _coordinator.BeginClosing();
                _coordinator.CancelAllSafe();
                // Give the cancelled work a moment to put its state back (the journal is saved by App.OnExit).
                for (var i = 0; i < 50 && _coordinator.ListedCount > plan.NotCancellable; i++) await Task.Delay(200);
                return true;
            default:
                return false;
        }
    }
}
