using System.Collections.ObjectModel;
using System.ServiceProcess;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Services;

/// <summary>РЕКОМЕНДАЦИИ: the curated allowlist with live state, profiles and per-row revert.</summary>
public sealed partial class ServicesViewModel
{
    private readonly Dictionary<string, RecommendationItem> _recByName = new(StringComparer.OrdinalIgnoreCase);
    private bool _bulkSelect;

    [ObservableProperty] private ObservableCollection<RecommendationGroup> _recommendationGroups = [];
    /// <summary>Curated services that are not installed on this PC (shown as a footnote).</summary>
    [ObservableProperty] private string _missingServicesText = "";

    [ObservableProperty] private int _recommendationCount;
    [ObservableProperty] private int _optimalCount;
    [ObservableProperty] private int _toChangeCount;
    [ObservableProperty] private int _changedCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyRecommendationsCommand), nameof(ClearRecommendationSelectionCommand))]
    private int _selectedRecommendationCount;

    private void BuildRecommendations(IReadOnlyDictionary<string, ServiceLiveState> states)
    {
        var firstBuild = _recByName.Count == 0;
        var present = new List<RecommendationItem>();
        var missing = new List<string>();
        var can = CanModify;

        _bulkSelect = true;
        try
        {
            foreach (var entry in ServiceCatalog.Services)
            {
                if (!states.TryGetValue(entry.Name, out var state) || !state.Exists)
                {
                    missing.Add(entry.Name);
                    _recByName.Remove(entry.Name);
                    continue;
                }
                if (!_recByName.TryGetValue(entry.Name, out var item))
                {
                    item = new RecommendationItem(entry) { SelectionChanged = OnRecommendationSelectionChanged };
                    _recByName[entry.Name] = item;
                }
                item.State = state;
                item.BlockReason = BlockReasonFor(entry);
                item.CanModify = can;
                item.IsRecommended = entry.IsPreselectedIn(SelectedProfile);
                if (!item.IsSelectable) item.IsSelected = false;
                present.Add(item);
            }
        }
        finally
        {
            _bulkSelect = false;
        }

        RecommendationGroups = new ObservableCollection<RecommendationGroup>(
            present.GroupBy(i => i.Entry.Group)
                   .OrderBy(g => g.Key)
                   .Select(g => new RecommendationGroup(g.Key, g.ToList())));

        MissingServicesText = missing.Count == 0
            ? ""
            : $"Не установлены на этом ПК и скрыты: {string.Join(", ", missing)}.";

        if (firstBuild) ApplyProfileToRecommendations();
        else UpdateRecommendationStats();
    }

    private string? BlockReasonFor(CuratedService entry)
    {
        if (ServiceControl.IsProtected(entry.Name) || ServiceGuard.IsHardBlocked(entry.Name))
            return "Служба защищена MONOLITH и не изменяется.";
        return entry.Gate switch
        {
            ServiceGate.NotInsider when Machine?.IsInsider == true =>
                "ПК участвует в программе Windows Insider — ей нужны эта служба и необязательные диагностические данные.",
            ServiceGate.NoBluetoothAdapter when Machine is null || Machine.HasBluetoothAdapter =>
                "На этом ПК есть Bluetooth-адаптер — без службы перестанут работать наушники, геймпады и мыши.",
            _ => null,
        };
    }

    /// <summary>Pre-selects what the profile recommends. Conditional ("по условию") items are never pre-selected.</summary>
    private void ApplyProfileToRecommendations()
    {
        _bulkSelect = true;
        try
        {
            foreach (var item in _recByName.Values)
            {
                item.IsRecommended = item.Entry.IsPreselectedIn(SelectedProfile);
                item.IsSelected = item.IsRecommended && item.IsSelectable;
            }
        }
        finally
        {
            _bulkSelect = false;
        }
        UpdateRecommendationStats();
    }

    private void OnRecommendationSelectionChanged(RecommendationItem item)
    {
        if (_bulkSelect) return;
        _bulkSelect = true;
        try
        {
            // PrintNotify is only disabled together with Spooler (research §1B).
            if (item.IsSelected && item.Entry.Companion is { } companionName &&
                _recByName.TryGetValue(companionName, out var companion) && companion.IsSelectable)
                companion.IsSelected = true;

            if (!item.IsSelected && !item.IsAtTarget)
                foreach (var dependent in _recByName.Values.Where(d =>
                             d.IsSelected && string.Equals(d.Entry.Companion, item.Name, StringComparison.OrdinalIgnoreCase)))
                    dependent.IsSelected = false;
        }
        finally
        {
            _bulkSelect = false;
        }
        UpdateRecommendationStats();
    }

    private void UpdateRecommendationStats()
    {
        var items = _recByName.Values.ToList();
        RecommendationCount = items.Count;
        OptimalCount = items.Count(i => i.IsAtTarget || i.IsAlreadyManual);
        ToChangeCount = items.Count(i => i.Entry.IsPreselectedIn(SelectedProfile) && i.IsSelectable);
        ChangedCount = items.Count(i => i.IsChanged);
        SelectedRecommendationCount = items.Count(i => i.IsSelected && i.IsSelectable);
    }

    private bool CanApplyRecommendations() => !IsBusy && CanModify && SelectedRecommendationCount > 0;

    [RelayCommand(CanExecute = nameof(CanApplyRecommendations))]
    private async Task ApplyRecommendationsAsync()
    {
        if (!RequireAdmin("изменение служб")) return;
        var selected = _recByName.Values.Where(i => i.IsSelected && i.CanSelect).ToList();
        if (selected.Count == 0) return;

        // Build the plan from a fresh read: show exactly "current → new".
        var live = await Task.Run(() => selected.ToDictionary(i => i.Name, i => ServiceScanner.ReadLiveState(i.Name), StringComparer.OrdinalIgnoreCase));
        var plan = selected
            .Where(i => live[i.Name].Exists)
            .Select(i => (Item: i, State: live[i.Name]))
            .Where(p => !(p.State.StartMode == p.Item.Entry.Target ||
                          (p.Item.Entry.Target == ServiceStartMode.Manual && p.State.StartMode == ServiceStartMode.Disabled)))
            .ToList();
        if (plan.Count == 0)
        {
            Ui.Toast("Выбранные службы уже настроены как нужно.", ToastKind.Info);
            await RefreshStatesAsync();
            return;
        }

        var details = plan.Select(p =>
            $"{p.Item.Title} ({p.Item.Name}): {ServiceText.Start(p.State.StartMode, p.State.Delayed, p.State.HasTriggers)} → {p.Item.TargetText}").ToList();
        var disabling = plan.Count(p => p.Item.Entry.Target == ServiceStartMode.Disabled);
        var message = "Изменится только тип запуска. Работающие службы не останавливаются — изменения вступят в силу после перезагрузки. " +
                      "Перед изменением создаётся точка восстановления, исходные значения сохраняются в журнале.";
        if (disabling > 0)
            message += " Отключённую службу Windows не запустит даже по требованию — убедитесь, что условия отмеченных пунктов выполняются.";

        if (!await Ui.ConfirmAsync("ИЗМЕНИТЬ ТИП ЗАПУСКА?", message, details, "ПРИМЕНИТЬ", danger: disabling > 0,
                footnote: "Откат: кнопка ОТКАТИТЬ в строке или раздел «Восстановление»."))
            return;

        await RunBusyAsync("Изменение служб…", async () =>
        {
            var session = await BeginSessionAsync(BatchTitle("Службы", plan.Select(p => p.Item.Title).ToList()));
            if (session is null) return;
            await Task.Run(() =>
            {
                foreach (var (item, _) in plan)
                    session.SetServiceStart(item.Name, item.Entry.Target, ServiceSource, $"Служба «{item.Title}»");
                session.Complete();
            });
            await ReportSessionAsync(session, $"Изменено служб: {session.Applied}. Новый тип запуска сработает после перезагрузки.");
            await RefreshStatesAsync();
        });
    }

    [RelayCommand(CanExecute = nameof(CanClearRecommendationSelection))]
    private void ClearRecommendationSelection()
    {
        _bulkSelect = true;
        try
        {
            foreach (var item in _recByName.Values) item.IsSelected = false;
        }
        finally
        {
            _bulkSelect = false;
        }
        UpdateRecommendationStats();
    }

    private bool CanClearRecommendationSelection() => SelectedRecommendationCount > 0;

    /// <summary>Per-row ОТКАТИТЬ (both the РЕКОМЕНДАЦИИ and ВСЕ СЛУЖБЫ tabs). Asks first when the start type drifted.</summary>
    [RelayCommand(CanExecute = nameof(CanRowAction))]
    private async Task RevertServiceAsync(string? name)
    {
        if (string.IsNullOrEmpty(name) || !RequireAdmin("откат изменения службы")) return;
        var record = ChangeJournal.Instance.FindActive("svc:" + name);
        if (record is null)
        {
            RefreshJournal();
            return;
        }

        var live = await Task.Run(() => ServiceControl.Query(name));
        if (live is null)
        {
            await Ui.AlertAsync("СЛУЖБА НЕ НАЙДЕНА",
                $"Службы {name} больше нет в системе — возвращать нечего. Запись можно удалить из журнала в разделе «Восстановление».");
            return;
        }

        var current = live.Describe();
        var drift = !string.Equals(current, record.NewValue, StringComparison.Ordinal);
        var message = drift
            ? $"Тип запуска уже изменён без MONOLITH (обновлением Windows, политикой или вручную): сейчас «{current}», MONOLITH ставил «{record.NewValue}». Всё равно вернуть исходное значение?"
            : "Служба вернётся к исходному типу запуска из журнала. Работающая служба не останавливается.";
        if (!await Ui.ConfirmAsync("ОТКАТИТЬ ИЗМЕНЕНИЕ?", message, [$"{record.Title}: {current} → {record.OldValue}"], "ОТКАТИТЬ", danger: drift))
            return;

        await RunBusyAsync("Откат изменения службы…", async () =>
        {
            var result = await Task.Run(() => ChangeJournal.Instance.RevertAsync([record]));
            await ReportRevertAsync(result);
            await RefreshStatesAsync();
        });
    }
}
