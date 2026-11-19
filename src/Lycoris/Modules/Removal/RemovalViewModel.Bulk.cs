using System.Collections.Frozen;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.UI;

namespace Lycoris.Modules.Removal;

public sealed partial class RemovalBatchPreviewRow : ObservableObject
{
    internal RemovalBatchPreviewRow(RemovalBatchItem item) => Item = item;

    internal RemovalBatchItem Item { get; }
    public string Name => Item.Name;
    public string Scope => Item.Scope;
    public string Note => Item.Note;
    [ObservableProperty] private bool _isSelected = true;
}

public sealed partial class RemovalViewModel
{
    private readonly IRemovalBatchAction _uninstallBatch;
    private readonly IRemovalBatchAction _disableBatch;
    private readonly Func<string, bool> _requireBatchAdmin;
    private readonly Func<RemovalBatchKind, IReadOnlyList<string>, Task<bool>> _confirmBatch;
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

    private bool CanPrepareBatch() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanPrepareBatch))]
    private Task PrepareUninstallAsync() => PrepareBatchAsync(_uninstallBatch);

    [RelayCommand(CanExecute = nameof(CanPrepareBatch))]
    private Task PrepareDisableStartupAsync() => PrepareBatchAsync(_disableBatch);

    private Task PrepareBatchAsync(IRemovalBatchAction action)
    {
        if (!CanPrepareBatch()) return Task.CompletedTask;
        TabIndex = action.Kind == RemovalBatchKind.DisableStartup ? 1 : 0;
        InvalidateBatchPreview();
        BatchMessage = null;
        var generation = _batchGeneration;

        return RunBusyAsync(OperationClass.Measure, Loc.T("Подбор кандидатов…"), async lease =>
        {
            RemovalBatchPlan plan;
            try { plan = await action.PrepareAsync(lease.Token); }
            catch (OperationCanceledException)
            {
                BatchMessage = Loc.T("Подбор кандидатов отменён.");
                return;
            }
            catch (Exception ex)
            {
                Log.Error(Loc.T("Не удалось подобрать кандидатов. Повторите проверку."), ex);
                BatchMessage = Loc.T("Не удалось подобрать кандидатов. Повторите проверку.");
                return;
            }
            if (generation != _batchGeneration)
            {
                ShowStaleBatch();
                return;
            }
            if (plan.UnavailableReason is not null || plan.Items.Count == 0)
            {
                BatchMessage = plan.UnavailableReason ?? (action.Kind == RemovalBatchKind.Uninstall
                    ? Loc.T("Программ, подходящих для пакетного удаления, не найдено.")
                    : Loc.T("Записей, подходящих для пакетного отключения, не найдено."));
                return;
            }

            _batchPreview = (action, plan);
            BatchRows = Array.AsReadOnly(plan.Items.Select(item => new RemovalBatchPreviewRow(item)).ToArray());
            foreach (var row in BatchRows) row.PropertyChanged += OnBatchRowChanged;
            NotifyBatchPreviewChanged();
        });
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
    private async Task ExecuteBatchAsync()
    {
        if (!CanExecuteBatch() || _batchPreview is not { } preview) return;
        var action = preview.Action;
        var selected = BatchRows.Where(row => row.IsSelected).Select(row => row.Item).ToArray();
        var selectedKeys = selected.Select(item => item.Key).ToFrozenSet(StringComparer.Ordinal);
        if (selectedKeys.Count != selected.Length) { ShowStaleBatch(); return; }
        var details = selected.Select(item => Loc.Tf("{0} · {1}", item.Name, item.Scope)).ToArray();
        var generation = _batchGeneration;
        var mode = ExpertMode;
        bool Current() => generation == _batchGeneration && mode == ExpertMode && !SafetyLock.IsLocked;
        var title = BatchOperationTitle(action.Kind);
        var kind = action.Kind == RemovalBatchKind.Uninstall ? OperationClass.Install : OperationClass.JournalChange;
        var started = false;

        IsBusy = true;
        try
        {
            // Keep the preview local to this process. A relaunch starts on this page with a fresh selection.
            if (!_requireBatchAdmin(title) || !CanStartNow(kind, title)) return;
            if (!await _confirmBatch(action.Kind, details)) return;
            if (!Current()) { ShowStaleBatch(); return; }

            BusyText = title;
            using var lease = await AcquireLeaseAsync(kind, title);
            if (lease is null) return;
            if (!Current()) { ShowStaleBatch(); return; }

            using var scope = lease.Scope();
            InvalidateBatchPreview();
            MarkInventoryStale();
            started = true;
            var result = await action.ExecuteAsync(preview.Plan, selectedKeys, new Progress<string>(lease.Report), lease.Token);
            BatchMessage = Loc.Tf("Выполнено: {0} · Пропущено: {1} · Сообщений об ошибках: {2}",
                result.Applied, result.Skipped, result.Errors.Count);
            if (result.UnknownOutcome) BatchMessage += "\n" + UnknownBatchOutcome();
            if (result.Errors.Count > 0) BatchMessage += "\n" + string.Join("\n", result.Errors);
        }
        catch (Exception ex)
        {
            Log.Error(title, ex);
            BatchMessage = started ? UnknownBatchOutcome() : Loc.T("Не удалось запустить пакетное действие.");
        }
        finally
        {
            BusyText = null;
            IsBusy = false;
        }
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
        ? Loc.T("Штатные деинсталляторы удалят выбранные программы. Сохраните нужные данные перед удалением. Отката через журнал Lycoris нет.")
        : Loc.T("Выбранные программы перестанут запускаться при входе. Их файлы сохранятся. Изменения можно отменить в разделе «Восстановление».");
}
