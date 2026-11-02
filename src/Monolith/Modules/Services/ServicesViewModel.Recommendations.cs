using System.Collections.ObjectModel;
using System.ServiceProcess;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Startup;
using Monolith.Modules.Optimize;
using Monolith.Tweaks;
using Monolith.UI;

namespace Monolith.Modules.Services;

/// <summary>РЕКОМЕНДАЦИИ: the curated allowlist with live state, profiles and per-row revert.</summary>
public sealed partial class ServicesViewModel
{
    private readonly Dictionary<string, RecommendationItem> _recByName = new(StringComparer.OrdinalIgnoreCase);
    private bool _bulkSelect;
    private bool _followingScenario;

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
            : Loc.Tf("Не установлены на этом ПК и скрыты: {0}.", string.Join(", ", missing));

        if (firstBuild) FollowScenario();
        if (firstBuild) ApplyProfileToRecommendations();
        else UpdateRecommendationStats();
    }

    // ───────────────────────────── The profile follows the Optimize scenario ─────────────────────────────

    /// <summary>
    /// The services profile the Optimize scenario saved in <see cref="AppSettings.LastProfile"/> maps to («Стриминг и запись» →
    /// Gaming, the efficiency scenario and «Свой набор» → Recommended; unknown names → Recommended, as on the Optimize page).
    /// </summary>
    internal static ProfileSet ProfileFromScenario(string? lastProfile) =>
        OptimizeLogic.ServicesProfileFor(OptimizeLogic.ParseProfile(lastProfile));

    /// <summary>
    /// One muted sentence next to the counters: the profile follows the scenario, or was picked by hand on this page
    /// («Свой набор» names no scenario, so the picker is simply the user's choice).
    /// </summary>
    internal static string ProfileCaptionFor(ProfileSet selected, string? lastProfile, bool laptop)
    {
        var scenario = OptimizeLogic.ParseProfile(lastProfile);
        if (scenario == OptimizeProfile.Custom) return Loc.T("Свой набор — профиль выбирается здесь.");
        var name = OptimizeLogic.ScenarioName(scenario, laptop);
        var mapped = OptimizeLogic.ServicesProfileFor(scenario);
        return mapped == selected
            ? Loc.Tf("Следует сценарию «{0}» из раздела «Оптимизация».", name)
            : Loc.Tf("Выбран вручную; сценарию «{0}» из раздела «Оптимизация» соответствует профиль «{1}».", name, Sentence(ServiceCatalog.ProfileTitle(mapped)));
    }

    private static string Sentence(string upper) => upper.Length == 0 ? upper : upper[0] + upper[1..].ToLowerInvariant();

    public string ProfileCaption => ProfileCaptionFor(SelectedProfile, AppSettings.Current.LastProfile, SystemProfile.Current is { HardwareFactsKnown: true, IsLaptop: true });

    /// <summary>The picker tooltip: what the profile does, and that it follows the scenario of the Optimize page.</summary>
    public string ProfileTooltip => ProfileDescription + " " + ProfileCaption;

    /// <summary>
    /// First scan: the picker takes the scenario's profile and keeps following it while the app runs (a tile picked on the Optimize
    /// page re-selects it here); the user may still pick another of the three for this session. Subscribed once.
    /// </summary>
    private void FollowScenario()
    {
        if (_followingScenario) return;
        _followingScenario = true;
        SelectedProfile = ProfileFromScenario(AppSettings.Current.LastProfile);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectedProfile)) NotifyProfileCaption();
        };
        AppSettings.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppSettings.LastProfile)) return;
            Ui.Post(() =>
            {
                SelectedProfile = ProfileFromScenario(AppSettings.Current.LastProfile);
                NotifyProfileCaption();
            });
        };
    }

    private void NotifyProfileCaption()
    {
        OnPropertyChanged(nameof(ProfileCaption));
        OnPropertyChanged(nameof(ProfileTooltip));
    }

    private string? BlockReasonFor(CuratedService entry)
    {
        if (ServiceControl.IsProtected(entry.Name) || ServiceGuard.IsHardBlocked(entry.Name))
            return Loc.T("Служба защищена Lycoris и не изменяется.");
        return entry.Gate switch
        {
            ServiceGate.NotInsider when Machine?.IsInsider == true =>
                Loc.T("ПК участвует в программе Windows Insider — ей нужны эта служба и необязательные диагностические данные."),
            ServiceGate.NoBluetoothAdapter when Machine is null || Machine.HasBluetoothAdapter =>
                Loc.T("На этом ПК есть Bluetooth-адаптер — без службы перестанут работать наушники, геймпады и мыши."),
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

    // A replayed intent carries the checked service names (IntentList) — they are re-checked before the confirmation.
    private bool CanApplyRecommendations(string? names) => !IsBusy && CanModify && (names is not null || SelectedRecommendationCount > 0);

    private string? SelectedRecommendationNames() => IntentList.Pack(_recByName.Values.Where(i => i.IsSelected && i.CanSelect).Select(i => i.Name));

    [RelayCommand(CanExecute = nameof(CanApplyRecommendations))]
    private async Task ApplyRecommendationsAsync(string? names)
    {
        if (!RequireAdmin(Loc.T("изменение служб"), "ApplyRecommendations", SelectedRecommendationNames())) return;
        if (IntentList.Unpack(names) is { Count: > 0 } replayed)
        {
            var set = replayed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _bulkSelect = true;
            try
            {
                foreach (var i in _recByName.Values) i.IsSelected = set.Contains(i.Name) && i.IsSelectable;
            }
            finally
            {
                _bulkSelect = false;
            }
            UpdateRecommendationStats();
        }
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
        var message = Loc.T("Изменится только тип запуска. Работающие службы не останавливаются — изменения вступят в силу после перезагрузки. " +
                      "Перед изменением создаётся точка восстановления, исходные значения сохраняются в журнале.");
        if (disabling > 0)
            message += Loc.T(" Отключённую службу Windows не запустит даже по требованию — убедитесь, что условия отмеченных пунктов выполняются.");

        if (!await Ui.ConfirmAsync("ИЗМЕНИТЬ ТИП ЗАПУСКА?", message, details, "ПРИМЕНИТЬ", danger: disabling > 0,
                footnote: "Откат: кнопка ОТКАТИТЬ в строке или раздел «Восстановление»."))
            return;

        await RunBusyAsync(OperationClass.JournalChange, Loc.T("Изменение служб…"), async () =>
        {
            var session = await BeginSessionAsync(BatchTitle(Loc.Keep("Службы"), plan.Select(p => p.Item.RuTitle).ToList()),
                BatchTitle(Loc.T("Службы"), plan.Select(p => p.Item.Title).ToList(), localized: true));
            if (session is null) return;
            await Task.Run(() =>
            {
                foreach (var (item, _) in plan)
                    session.SetServiceStart(item.Name, item.Entry.Target, ServiceSource, string.Format(Loc.Keep("Служба «{0}»"), item.RuTitle));
                session.Complete();
            });
            await ReportSessionAsync(session, Loc.Tf("Изменено служб: {0}. Новый тип запуска сработает после перезагрузки.", session.Applied));
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
        if (string.IsNullOrEmpty(name) || !RequireAdmin(Loc.T("откат изменения службы"), "RevertService", name)) return;
        // The row means «back to how it was before Lycoris»: every active change of this service, newest first.
        var layers = ChangeJournal.Instance.ActiveLayers("svc:" + name);
        var record = layers.LastOrDefault();
        if (record is null)
        {
            RefreshJournal();
            return;
        }
        var original = layers[0].OldValue;

        var live = await Task.Run(() => ServiceControl.Query(name));
        if (live is null)
        {
            await Ui.AlertAsync("СЛУЖБА НЕ НАЙДЕНА",
                Loc.Tf("Службы {0} больше нет в системе — возвращать нечего. При откате такая запись будет пропущена.", name));
            return;
        }

        var current = live.Describe();
        var drift = !string.Equals(live.DescribeNeutral(), ServiceControl.NormalizeStart(record.NewValue), StringComparison.Ordinal);
        var message = drift
            ? Loc.Tf("Тип запуска уже изменён без Lycoris (обновлением Windows, политикой или вручную): сейчас «{0}», Lycoris ставил «{1}». Всё равно вернуть исходное значение?", current, Loc.T(ServiceControl.NormalizeStart(record.NewValue)))
            : Loc.T("Служба вернётся к исходному типу запуска из журнала. Работающая служба не останавливается.");
        if (!await Ui.ConfirmAsync("ОТКАТИТЬ ИЗМЕНЕНИЕ?", message, [$"{record.Title}: {current} → {Loc.T(ServiceControl.NormalizeStart(original))}"], "ОТКАТИТЬ", danger: drift))
            return;

        await RunBusyAsync(OperationClass.JournalChange, Loc.T("Откат изменения службы…"), async () =>
        {
            var result = await Task.Run(() => ChangeJournal.Instance.RevertAsync(layers));
            await ReportRevertAsync(result);
            await RefreshStatesAsync();
        });
    }
}
