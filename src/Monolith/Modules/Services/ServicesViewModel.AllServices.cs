using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ServiceProcess;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Services;

/// <summary>
/// ВСЕ СЛУЖБЫ: every Win32 service, read-only for locked ones (Core protected list, research hard-block list,
/// per-user services, drivers, PPL, DriverStore, services owned by an Optimize tweak). Outside expert mode
/// (AppSettings.ServicesExpertMode, toggle on this tab) every non-curated service is locked too (research §0.4/§2B).
/// Start-type changes are journaled; start/stop are not.
/// </summary>
public sealed partial class ServicesViewModel
{
    [ObservableProperty] private ObservableCollection<ServiceItem> _allServices = [];
    [ObservableProperty] private ICollectionView _servicesView;
    [ObservableProperty] private bool _isServicesLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyStartTypeCommand), nameof(ResetStartTypeCommand), nameof(StartServiceCommand), nameof(StopServiceCommand))]
    private ServiceItem? _selectedService;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ServiceFilterKind _filter = ServiceFilterKind.All;

    [ObservableProperty] private int _countAll;
    [ObservableProperty] private int _countRunning;
    [ObservableProperty] private int _countAutomatic;
    [ObservableProperty] private int _countDisabled;
    [ObservableProperty] private int _countThirdParty;
    [ObservableProperty] private int _countLocked;
    [ObservableProperty] private int _shownCount;

    public IReadOnlyList<StartOption> StartOptions => StartOption.All;

    private ICollectionView CreateServicesView(ObservableCollection<ServiceItem> source)
    {
        var view = CollectionViewSource.GetDefaultView(source);
        view.Filter = o => o is ServiceItem s && ServiceFilter.Matches(s.Facts, Filter, SearchText);
        return view;
    }

    /// <summary>The last scan as read (structural locks only); the expert-mode lock is layered on in BuildAllServices.</summary>
    private List<ServiceInfo> _scannedServices = [];
    /// <summary>The expert-mode value the current ВСЕ СЛУЖБЫ list was built with.</summary>
    private bool _builtExpertMode;

    private void BuildAllServices(List<ServiceInfo> infos)
    {
        _scannedServices = infos;
        var expert = _builtExpertMode = AppSettings.Current.ServicesExpertMode;
        var previous = SelectedService?.Name;
        var can = CanModify;
        var items = infos
            .OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => i.LockReason is null &&
                         (ServiceGuard.OptimizeOwnedLockReason(i.Name) ?? ServiceGuard.NotCuratedLockReason(i.Name, i.IsThirdParty, expert)) is { } why
                ? i with { LockReason = why }
                : i)
            .Select(i => new ServiceItem(i) { CanModify = can })
            .ToList();
        foreach (var item in items) item.Journal = ChangeJournal.Instance.FindActive("svc:" + item.Name);

        AllServices = new ObservableCollection<ServiceItem>(items);
        ServicesView = CreateServicesView(AllServices);
        UpdateFilterCounts();
        UpdateShownCount();
        SelectedService = items.FirstOrDefault(i => string.Equals(i.Name, previous, StringComparison.OrdinalIgnoreCase))
                          ?? FirstVisible();
    }

    private ServiceItem? FirstVisible() => ServicesView.Cast<ServiceItem>().FirstOrDefault();

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnFilterChanged(ServiceFilterKind value) => ApplyFilter();

    private void ApplyFilter()
    {
        ServicesView.Refresh();
        UpdateShownCount();
        if (SelectedService is null || !ServiceFilter.Matches(SelectedService.Facts, Filter, SearchText))
            SelectedService = FirstVisible();
    }

    private void UpdateFilterCounts()
    {
        var facts = AllServices.Select(s => s.Facts).ToList();
        CountAll = facts.Count;
        CountRunning = facts.Count(f => ServiceFilter.Matches(f, ServiceFilterKind.Running, null));
        CountAutomatic = facts.Count(f => ServiceFilter.Matches(f, ServiceFilterKind.Automatic, null));
        CountDisabled = facts.Count(f => ServiceFilter.Matches(f, ServiceFilterKind.Disabled, null));
        CountThirdParty = facts.Count(f => ServiceFilter.Matches(f, ServiceFilterKind.ThirdParty, null));
        CountLocked = AllServices.Count(s => s.IsLocked);
    }

    private void UpdateShownCount() => ShownCount = ServicesView.Cast<object>().Count();

    partial void OnSelectedServiceChanged(ServiceItem? oldValue, ServiceItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnSelectedServicePropertyChanged;
            oldValue.ResetPending();
        }
        if (newValue is not null) newValue.PropertyChanged += OnSelectedServicePropertyChanged;
    }

    private void OnSelectedServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServiceItem.CanApplyStart) or nameof(ServiceItem.CanStart) or nameof(ServiceItem.CanStop))
        {
            ApplyStartTypeCommand.NotifyCanExecuteChanged();
            ResetStartTypeCommand.NotifyCanExecuteChanged();
            StartServiceCommand.NotifyCanExecuteChanged();
            StopServiceCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RefreshServiceAsync(ServiceItem item)
    {
        var state = await Task.Run(() => ServiceScanner.ReadLiveState(item.Name));
        item.Apply(state);
        item.Journal = ChangeJournal.Instance.FindActive("svc:" + item.Name);
        if (_recByName.TryGetValue(item.Name, out var rec) && state.Exists) rec.State = state;
        UpdateFilterCounts();
        UpdateRecommendationStats();
    }

    // ───────────────────────────── Start type ─────────────────────────────

    // A replayed intent names the service (and the chosen start type) — a service that is not selected is selected first.
    private ServiceItem? SelectForReplay(string? name)
    {
        if (string.IsNullOrEmpty(name)) return SelectedService;
        var item = AllServices.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item is not null && !ReferenceEquals(item, SelectedService)) SelectedService = item;
        return item;
    }

    /// <summary>«name|Mode|delayed» ↔ the intent parameter of «ApplyStartType»; null when the text is not of that form.</summary>
    internal static (string Name, ServiceStartMode Mode, bool Delayed)? ParseStartSpec(string? spec)
    {
        if (string.IsNullOrEmpty(spec)) return null;
        var parts = spec.Split('|');
        if (parts.Length < 3 || !Enum.TryParse<ServiceStartMode>(parts[^2], out var mode)) return null;
        return (string.Join('|', parts[..^2]), mode, parts[^1] == "1");
    }

    internal static string StartSpec(string name, ServiceStartMode mode, bool delayed) => $"{name}|{mode}|{(delayed ? 1 : 0)}";

    private bool CanApplyStartType(string? spec) => !IsBusy && (spec is not null || SelectedService?.CanApplyStart == true);

    [RelayCommand(CanExecute = nameof(CanApplyStartType))]
    private async Task ApplyStartTypeAsync(string? spec)
    {
        if (ParseStartSpec(spec) is { } replayed)
        {
            if (SelectForReplay(replayed.Name) is { } target)
                target.PendingStart = StartOptions.FirstOrDefault(o => o.Mode == replayed.Mode && o.Delayed == replayed.Delayed) ?? target.PendingStart;
        }
        var item = SelectedService;
        var option = item?.PendingStart;
        if (item is null || option is null || item.IsLocked) return;
        if (!RequireAdmin("изменение типа запуска", "ApplyStartType", StartSpec(item.Name, option.Mode, option.Delayed))) return;
        if (item.MustStayDisabled && option.Mode != ServiceStartMode.Disabled) return;

        var (live, dependents) = await Task.Run(() =>
            (ServiceControl.Query(item.Name),
             option.Mode == ServiceStartMode.Disabled ? ServiceScanner.GetDependents(item.Name) : (IReadOnlyList<DependentInfo>)[]));
        if (live is null)
        {
            Ui.Toast($"Служба {item.Name} не найдена — список устарел. Нажмите «Обновить».", ToastKind.Warning);
            return;
        }
        if (live.StartMode == option.Mode && live.DelayedAutoStart == option.Delayed)
        {
            await RefreshServiceAsync(item);
            return;
        }

        var newText = ServiceControl.DescribeStart(option.Mode, option.Delayed);
        var details = new List<string>
        {
            $"{item.DisplayName} ({item.Name}): {live.Describe()} → {newText}",
            "Перед изменением создаётся точка восстановления, исходный тип запуска сохраняется в журнале.",
            "Служба сейчас не останавливается и не запускается — новый тип запуска сработает при следующем старте Windows.",
        };
        if (option.Mode == ServiceStartMode.Disabled)
            details.Add("Отключённую службу Windows не запустит даже по требованию: всё, что от неё зависит, перестанет работать.");
        if (dependents.Count > 0)
            details.Add($"От неё зависят: {string.Join(", ", dependents.Select(d => d.DisplayName))}.");
        if (item.IsThirdParty)
            details.Add($"Служба стороннего производителя ({item.Publisher}): её программа может перестать работать.");
        if (!item.IsCurated)
            details.Add("Этой службы нет в списке рекомендаций MONOLITH — вы меняете её на свой риск.");

        var disabling = option.Mode == ServiceStartMode.Disabled;
        if (!await Ui.ConfirmAsync(disabling ? "ОТКЛЮЧИТЬ СЛУЖБУ?" : "ИЗМЕНИТЬ ТИП ЗАПУСКА?",
                disabling
                    ? "Вы собираетесь отключить службу. Проверьте, что она вам не нужна: ошибка может сломать функции Windows или программ."
                    : "Изменится тип запуска службы. Изменение записывается в журнал и откатывается.",
                details, disabling ? "ОТКЛЮЧИТЬ" : "ПРИМЕНИТЬ", danger: disabling,
                footnote: "Откат: кнопка ОТКАТИТЬ в карточке службы или раздел «Восстановление»."))
            return;

        await RunBusyAsync(OperationClass.JournalChange, "Изменение типа запуска…", async () =>
        {
            var session = await BeginSessionAsync($"Службы: {item.DisplayName} → {newText}");
            if (session is null) return;
            await Task.Run(() =>
            {
                session.SetServiceStart(item.Name, option.Mode, ServiceSource, $"Служба «{item.DisplayName}»", option.Delayed);
                session.Complete();
            });
            await ReportSessionAsync(session, $"«{item.DisplayName}»: {newText}. Сработает при следующем запуске Windows.");
            await RefreshServiceAsync(item);
        });
    }

    private bool CanResetStartType() => SelectedService?.IsStartDirty == true;

    [RelayCommand(CanExecute = nameof(CanResetStartType))]
    private void ResetStartType() => SelectedService?.ResetPending();

    // ───────────────────────────── Start / stop (not journaled) ─────────────────────────────

    private bool CanStartService(string? name) => !IsBusy && (name is not null || SelectedService?.CanStart == true);

    [RelayCommand(CanExecute = nameof(CanStartService))]
    private async Task StartServiceAsync(string? name)
    {
        var item = SelectForReplay(name);
        if (item is null || item.IsLocked || item.MustStayDisabled) return;
        if (!RequireAdmin("запуск службы", "StartService", item.Name)) return;
        if (!await Ui.ConfirmAsync("ЗАПУСТИТЬ СЛУЖБУ?",
                $"«{item.DisplayName}» будет запущена сейчас. Тип запуска не меняется.",
                [$"{item.DisplayName} ({item.Name}): {item.StatusText} → Работает",
                 "Запуск не записывается в журнал изменений: после перезагрузки служба поведёт себя согласно своему типу запуска."],
                "ЗАПУСТИТЬ"))
            return;

        await RunBusyAsync(OperationClass.JournalChange, "Запуск службы…", async () =>
        {
            var ok = await Task.Run(() => ServiceControl.StartAsync(item.Name, TimeSpan.FromSeconds(30)));
            if (ok)
            {
                Log.Success($"Служба запущена: {item.DisplayName} ({item.Name})");
                Ui.Toast($"«{item.DisplayName}» запущена.", ToastKind.Success);
            }
            else
            {
                Ui.Toast($"Не удалось запустить «{item.DisplayName}». Подробности — в консоли.", ToastKind.Error);
            }
            await RefreshServiceAsync(item);
        });
    }

    private bool CanStopService(string? name) => !IsBusy && (name is not null || SelectedService?.CanStop == true);

    [RelayCommand(CanExecute = nameof(CanStopService))]
    private async Task StopServiceAsync(string? name)
    {
        var item = SelectForReplay(name);
        if (item is null || item.IsLocked) return;
        if (!RequireAdmin("остановка службы", "StopService", item.Name)) return;

        var dependents = await Task.Run(() => ServiceScanner.GetDependents(item.Name));
        var running = dependents.Where(d => d.IsRunning).ToList();
        if (running.Count > 0)
        {
            await Ui.AlertAsync("НЕЛЬЗЯ ОСТАНОВИТЬ",
                $"От «{item.DisplayName}» зависят работающие службы. MONOLITH не останавливает службы каскадом — это может нарушить работу Windows.",
                running.Select(d => $"{d.DisplayName} ({d.Name})"));
            return;
        }

        if (!await Ui.ConfirmAsync("ОСТАНОВИТЬ СЛУЖБУ?",
                $"«{item.DisplayName}» будет остановлена сейчас. Программы, которые её используют, могут перестать работать.",
                [$"{item.DisplayName} ({item.Name}): Работает → Остановлена",
                 "Остановка не записывается в журнал изменений. Служба снова запустится по требованию, по триггеру или после перезагрузки (если не отключена).",
                 "Вернуть сразу — кнопка ЗАПУСТИТЬ."],
                "ОСТАНОВИТЬ", danger: true))
            return;

        await RunBusyAsync(OperationClass.JournalChange, "Остановка службы…", async () =>
        {
            var ok = await Task.Run(() => ServiceControl.StopAsync(item.Name, TimeSpan.FromSeconds(30)));
            if (ok)
            {
                Log.Success($"Служба остановлена: {item.DisplayName} ({item.Name})");
                Ui.Toast($"«{item.DisplayName}» остановлена.", ToastKind.Success);
            }
            else
            {
                Ui.Toast($"Не удалось остановить «{item.DisplayName}». Подробности — в консоли.", ToastKind.Error);
            }
            await RefreshServiceAsync(item);
        });
    }
}
