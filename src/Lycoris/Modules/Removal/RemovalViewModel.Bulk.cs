using System.Collections.Frozen;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Shell;
using Lycoris.UI;

namespace Lycoris.Modules.Removal;

public sealed partial class RemovalBatchPreviewRow : ObservableObject
{
    internal RemovalBatchPreviewRow(RemovalBatchItem item) => Item = item;

    internal RemovalBatchItem Item { get; }
    public string Name => Item.Name;
    public string Metadata => string.Join(" · ", new[] { Item.Version, Item.Scope, Item.NativeInstallerId }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
    public string Note => Item.Note;
    [ObservableProperty] private bool _isSelected = true;
}

public sealed partial class RemovalViewModel
{
    private readonly IRemovalBatchAction _uninstallBatch;
    private readonly IRemovalBatchAction _disableBatch;
    private static readonly TimeSpan MaximumPreviewAge = TimeSpan.FromMinutes(5);
    private readonly Func<string, string?, bool> _requireBatchAdmin;
    private readonly Func<RemovalBatchKind, IReadOnlyList<string>, Task<bool>> _confirmBatch;
    private readonly TimeProvider _time;
    private readonly Func<string?> _userSid;
    private (IRemovalBatchAction Action, RemovalBatchPlan Plan)? _batchPreview;
    private int _batchGeneration;

    [ObservableProperty] private string? _batchMessage;
    public bool HasBatchMessage => BatchMessage is not null;
    partial void OnBatchMessageChanged(string? value) => OnPropertyChanged(nameof(HasBatchMessage));

    public IReadOnlyList<RemovalBatchPreviewRow> BatchRows { get; private set; } = [];
    public bool HasBatchPreview => _batchPreview is not null;
    public bool IsUninstallPreview => _batchPreview?.Action.Kind == RemovalBatchKind.Uninstall;
    public bool CanEditBatchPreview => HasBatchPreview && !IsBusy;
    public int BatchSelectedCount => BatchRows.Count(row => row.IsSelected);
    public string BatchSelectionText => Loc.Tf("Выбрано {0} из {1}", BatchSelectedCount, BatchRows.Count);
    public string BatchPreviewTitle => IsUninstallPreview
        ? Loc.T("Программы для удаления")
        : Loc.T("Записи для отключения автозапуска");
    public string BatchPreviewDescription => BatchEffect(IsUninstallPreview ? RemovalBatchKind.Uninstall : RemovalBatchKind.DisableStartup);
    public string BatchExecuteText => BatchActionText(IsUninstallPreview ? RemovalBatchKind.Uninstall : RemovalBatchKind.DisableStartup,
        BatchSelectedCount);
    public string? BatchRightsNotice => SafetyLock.IsLocked
        ? Loc.T("В режиме самопроверки доступен просмотр списка.")
        : IsReadOnly
            ? Loc.T("Для выполнения потребуются права администратора. После перезапуска проверьте выбор заново.")
            : null;

    private bool CanAutoBatch() => !IsBusy && !SafetyLock.IsLocked;

    [RelayCommand(CanExecute = nameof(CanAutoBatch))]
    private Task AutoUninstallAsync() => StartBatchAsync(_uninstallBatch, AdminIntent.AutoUninstall);

    [RelayCommand(CanExecute = nameof(CanAutoBatch))]
    private Task AutoDisableStartupAsync() => StartBatchAsync(_disableBatch, AdminIntent.AutoDisableStartup);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task PrepareUninstallAsync() => StartBatchAsync(_uninstallBatch);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task PrepareDisableStartupAsync() => StartBatchAsync(_disableBatch);

    private Task StartBatchAsync(IRemovalBatchAction action, string? automaticCommand = null)
    {
        if (IsBusy || (automaticCommand is not null && !CanAutoBatch())) return Task.CompletedTask;
        return RunBatchFlowAsync(async () =>
        {
            if (automaticCommand is not null && !_requireBatchAdmin(BatchOperationTitle(action.Kind), automaticCommand)) return;
            TabIndex = action.Kind == RemovalBatchKind.DisableStartup ? 1 : 0;
            InvalidateBatchPreview();
            BatchMessage = null;
            var generation = _batchGeneration;
            var mode = ExpertMode;
            var plan = await ReadBatchPlanAsync(action);
            if (plan is null) return;
            if (!BatchContextMatches(action.Kind, generation, mode)) { ShowStaleBatch(); return; }

            ShowAssessedRows(action.Kind, plan);
            BatchMessage = plan.Summary;
            if (!BatchContextMatches(action.Kind, generation, mode)) { ShowStaleBatch(); return; }
            if (plan.UnavailableReason is not null || plan.Items.Count == 0)
            {
                AppendBatchMessage(plan.UnavailableReason ?? (action.Kind == RemovalBatchKind.Uninstall
                    ? Loc.T("Программ, подходящих для пакетного удаления, не найдено.")
                    : Loc.T("Записей, подходящих для пакетного отключения, не найдено.")));
                return;
            }

            _batchPreview = (action, plan);
            BatchRows = Array.AsReadOnly(plan.Items.Select(item => new RemovalBatchPreviewRow(item)).ToArray());
            foreach (var row in BatchRows) row.PropertyChanged += OnBatchRowChanged;
            NotifyBatchPreviewChanged();
            if (automaticCommand is null) return;
            if (!BatchContextMatches(action.Kind, generation, mode)) { ShowStaleBatch(); return; }
            await ConfirmAndExecuteBatchAsync(action, plan);
        });
    }

    private async Task<RemovalBatchPlan?> ReadBatchPlanAsync(IRemovalBatchAction action)
    {
        var title = Loc.T("Подбор кандидатов…");
        BusyText = title;
        try
        {
            using var lease = await AcquireLeaseAsync(OperationClass.Measure, title);
            if (lease is null)
            {
                BatchMessage = OperationNote ?? Loc.T("Подбор кандидатов отменён.");
                return null;
            }
            using var scope = lease.Scope();
            return await action.PrepareAsync(lease.Token);
        }
        catch (OperationCanceledException)
        {
            BatchMessage = Loc.T("Подбор кандидатов отменён.");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось подобрать кандидатов. Повторите проверку.", ex);
            BatchMessage = Loc.T("Не удалось подобрать кандидатов. Повторите проверку.");
            return null;
        }
    }

    private void InvalidateBatchPreview()
    {
        _batchGeneration++;
        if (!HasBatchPreview) return;
        foreach (var row in BatchRows) row.PropertyChanged -= OnBatchRowChanged;
        _batchPreview = null;
        BatchRows = [];
        NotifyBatchPreviewChanged();
    }

    private void NotifyBatchPreviewChanged()
    {
        OnPropertyChanged(nameof(BatchRows));
        OnPropertyChanged(nameof(HasBatchPreview));
        OnPropertyChanged(nameof(IsUninstallPreview));
        OnPropertyChanged(nameof(BatchPreviewTitle));
        OnPropertyChanged(nameof(BatchPreviewDescription));
        RefreshBatchCommands();
    }

    private void OnBatchRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RemovalBatchPreviewRow.IsSelected)) return;
        _batchGeneration++;
        RefreshBatchCommands();
    }

    private void RefreshBatchCommands()
    {
        OnPropertyChanged(nameof(CanEditBatchPreview));
        OnPropertyChanged(nameof(BatchSelectionText));
        OnPropertyChanged(nameof(BatchExecuteText));
        OnPropertyChanged(nameof(BatchRightsNotice));
        SelectAllBatchCommand.NotifyCanExecuteChanged();
        ClearBatchSelectionCommand.NotifyCanExecuteChanged();
        CancelBatchPreviewCommand.NotifyCanExecuteChanged();
        ExecuteBatchCommand.NotifyCanExecuteChanged();
    }

    private bool CanSelectAllBatch() => CanEditBatchPreview && BatchRows.Any(row => !row.IsSelected);
    private bool CanClearBatchSelection() => CanEditBatchPreview && BatchRows.Any(row => row.IsSelected);

    [RelayCommand(CanExecute = nameof(CanSelectAllBatch))]
    private void SelectAllBatch()
    {
        if (!CanSelectAllBatch()) return;
        foreach (var row in BatchRows) row.IsSelected = true;
    }

    [RelayCommand(CanExecute = nameof(CanClearBatchSelection))]
    private void ClearBatchSelection()
    {
        if (!CanClearBatchSelection()) return;
        foreach (var row in BatchRows) row.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(CanCancelBatchPreview))]
    private void CancelBatchPreview()
    {
        if (CanCancelBatchPreview()) InvalidateBatchPreview();
    }

    private bool CanCancelBatchPreview() => CanEditBatchPreview;
    private bool CanExecuteBatch() => CanClearBatchSelection() && !SafetyLock.IsLocked;

    [RelayCommand(CanExecute = nameof(CanExecuteBatch))]
    private Task ExecuteBatchAsync()
    {
        if (!CanExecuteBatch() || _batchPreview is not { } preview) return Task.CompletedTask;
        return RunBatchFlowAsync(async () =>
        {
            // An individually edited selection stays in this process.
            if (_requireBatchAdmin(BatchOperationTitle(preview.Action.Kind), null))
                await ConfirmAndExecuteBatchAsync(preview.Action, preview.Plan);
        });
    }

    private async Task RunBatchFlowAsync(Func<Task> work)
    {
        try
        {
            IsBusy = true;
            await work();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось запустить пакетное действие.", ex);
            BatchMessage = Loc.T("Не удалось запустить пакетное действие.");
        }
        finally
        {
            BusyText = null;
            IsBusy = false;
        }
    }

    private async Task ConfirmAndExecuteBatchAsync(IRemovalBatchAction action, RemovalBatchPlan plan)
    {
        var generation = _batchGeneration;
        var mode = ExpertMode;
        bool Current()
        {
            var age = _time.GetUtcNow().UtcDateTime - plan.ObservedUtc;
            return ReferenceEquals(_batchPreview?.Plan, plan) && BatchContextMatches(action.Kind, generation, mode)
                && age >= TimeSpan.Zero && age < MaximumPreviewAge
                && plan.UserSid is not null && UserScope.Same(plan.UserSid, _userSid()) && !SafetyLock.IsLocked;
        }

        if (!Current()) { ShowStaleBatch(); return; }
        var selected = BatchRows.Where(row => row.IsSelected).ToArray();
        var selectedKeys = selected.Select(row => row.Item.Key).ToFrozenSet(StringComparer.Ordinal);
        if (selectedKeys.Count == 0 || selectedKeys.Count != selected.Length) { ShowStaleBatch(); return; }
        var details = selected.Select(row => Loc.Tf("{0} · {1}", row.Name, row.Metadata)).ToArray();
        var title = BatchOperationTitle(action.Kind);
        var kind = action.Kind == RemovalBatchKind.Uninstall ? OperationClass.Install : OperationClass.JournalChange;
        if (!CanStartNow(kind, title)) return;
        BusyText = title;

        var confirmed = await _confirmBatch(action.Kind, details);
        if (!Current()) { ShowStaleBatch(); return; }
        if (!confirmed) return;

        using var lease = await AcquireLeaseAsync(kind, title);
        if (lease is null) return;
        using var scope = lease.Scope();
        if (!Current()) { ShowStaleBatch(); return; }

        try
        {
            var result = await action.ExecuteAsync(plan, selectedKeys, new Progress<string>(lease.Report), lease.Token);
            BatchMessage = Loc.Tf("Выполнено: {0} · Пропущено: {1} · Сообщений об ошибках: {2}",
                result.Applied, result.Skipped, result.Errors.Count);
            if (result.UnknownOutcome) BatchMessage += "\n" + UnknownBatchOutcome();
            if (result.Errors.Count > 0) BatchMessage += "\n" + string.Join("\n", result.Errors);
        }
        catch (Exception ex)
        {
            Log.Error(title, ex);
            BatchMessage = UnknownBatchOutcome();
        }
        finally
        {
            InvalidateBatchPreview();
            MarkInventoryStale();
        }
    }

    private bool BatchContextMatches(RemovalBatchKind kind, int generation, bool mode) =>
        generation == _batchGeneration && mode == ExpertMode && IsStartupTab == (kind == RemovalBatchKind.DisableStartup);

    private void AppendBatchMessage(string message)
    {
        if (BatchMessage != message) BatchMessage = BatchMessage is null ? message : BatchMessage + "\n" + message;
    }

    private void ShowStaleBatch()
    {
        InvalidateBatchPreview();
        BatchMessage = Loc.T("Список или условия изменились. Подберите кандидатов заново.");
    }

    private static string UnknownBatchOutcome() =>
        Loc.T("Не удалось подтвердить результат. Оставшиеся действия остановлены.");

    private static Task<bool> ConfirmBatchAsync(RemovalBatchKind kind, IReadOnlyList<string> details) =>
        Ui.ConfirmAsync(BatchOperationTitle(kind), BatchEffect(kind), details,
            BatchActionText(kind, details.Count), Loc.T("ОТМЕНА"), danger: kind == RemovalBatchKind.Uninstall);

    private static string BatchOperationTitle(RemovalBatchKind kind) => kind == RemovalBatchKind.Uninstall
        ? Loc.T("Пакетное удаление программ")
        : Loc.T("Пакетное отключение автозапуска");

    private static string BatchActionText(RemovalBatchKind kind, int count) => kind == RemovalBatchKind.Uninstall
        ? Loc.Tf("Удалить выбранные · {0}", count)
        : Loc.Tf("Отключить выбранные · {0}", count);

    private static string BatchEffect(RemovalBatchKind kind) => kind == RemovalBatchKind.Uninstall
        ? Loc.T("Штатные деинсталляторы удалят выбранные программы. Некоторые откроют окно и предложат выбрать, какие данные сохранить. Сохраните нужные данные заранее. Отката через журнал Lycoris нет.")
        : Loc.T("Выбранные программы перестанут запускаться при входе. Их файлы сохранятся. Изменения можно отменить в разделе «Восстановление».");
}
