using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Apps;

public enum WingetState { Checking, Available, Missing }

/// <summary>
/// "ПРИЛОЖЕНИЯ": one-click install / update of launchers, game runtimes and programs through winget.
/// Installed state comes from "winget export" (+ documented read-only cross-checks); installs run one at a time
/// in a queue with live output. Packages that must not run elevated are started de-elevated via the desktop shell.
/// </summary>
public sealed partial class AppsViewModel : PageViewModel
{
    private const int MaxLogLines = 800;

    private readonly List<AppItemViewModel> _items;
    private CancellationTokenSource? _queueCts;
    private int _busyDepth;
    private DateTime _lastCheck = DateTime.MinValue;
    private bool _detecting;

    // Inputs of the install advice (docs/research/apps_catalog.md §14); unknown until read.
    private bool _gpuKnown;
    private IReadOnlySet<GpuVendor> _gpuVendors = new HashSet<GpuVendor>();
    private int _osBuild;
    private int _osUbr;
    private PeripheralScanResult _peripherals = PeripheralScanResult.Unknown;
    private bool _scanningPeripherals;
    /// <summary>A full detection result (or the cache) is applied: Store apps were checked too, not only the fast pass.</summary>
    private bool _storeKnown;

    public override string Key => "apps";
    public override string Index => "08";
    public override string Title => "ПРИЛОЖЕНИЯ";
    public override string Subtitle => "Установка и обновление программ через winget и Microsoft Store.";
    public override string Glyph => Glyphs.Download;

    public AppsViewModel()
    {
        var build = SystemProfile.Current.OsBuild;
        _items = AppCatalog.ForBuild(build).Select(e => new AppItemViewModel(e)).ToList();
        foreach (var item in _items) item.PropertyChanged += OnItemPropertyChanged;

        foreach (var info in AppCatalog.Categories)
        {
            var members = _items.Where(i => i.Entry.Category == info.Category).ToList();
            if (members.Count == 0) continue;
            Groups.Add(new AppGroupViewModel(info, members));
        }

        Categories.Add(new CategoryFilterViewModel(null, "ВСЕ") { IsSelected = true });
        Categories.Add(new CategoryFilterViewModel(null, "РЕКОМЕНДУЕМ", isRecommended: true));
        foreach (var g in Groups) Categories.Add(new CategoryFilterViewModel(g.Info.Category, g.Info.TabTitle));
        _selectedCategory = Categories[0];

        TotalCount = _items.Count;
        ApplyProfile(SystemProfile.Current);
        UpdateAdvice();
    }

    // ─────────────────────────── collections ───────────────────────────

    public ObservableCollection<AppGroupViewModel> Groups { get; } = new();
    public ObservableCollection<CategoryFilterViewModel> Categories { get; } = new();
    public ObservableCollection<QueueItemViewModel> Queue { get; } = new();
    public ObservableCollection<string> QueueLog { get; } = new();

    /// <summary>"НАБОР ГЕЙМЕРА": runtime libraries this PC needs (<see cref="AppAdvisor.Kit"/>), recomputed with the advice.</summary>
    public IReadOnlyList<AppItemViewModel> KitItems { get; private set; } = [];

    // ─────────────────────────── state ───────────────────────────

    public int TotalCount { get; }
    public bool ShowReadOnlyNotice => IsReadOnly;
    public string KitCaption => AppTexts.KitCaption;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private CategoryFilterViewModel? _selectedCategory;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private int _installedCount;
    [ObservableProperty] private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WingetMissing), nameof(WingetReady), nameof(WingetChecking))]
    private WingetState _wingetState = WingetState.Checking;

    [ObservableProperty] private string _wingetVersionText = "—";
    [ObservableProperty] private bool _wingetOutdated;
    [ObservableProperty] private string _detectionText = "Проверка ещё не выполнялась";
    [ObservableProperty] private bool _hasDetection;

    [ObservableProperty] private int _kitInstalled;
    [ObservableProperty] private int _kitTotal;
    [ObservableProperty] private string _kitToolTip = AppTexts.KitToolTip([], []);
    [ObservableProperty] private bool _kitComplete;
    [ObservableProperty] private string _kitStatusText = "";
    [ObservableProperty] private string _kitButtonText = "УСТАНОВИТЬ НАБОР";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartQueue), nameof(CanCloseQueue))]
    private bool _isQueueRunning;

    [ObservableProperty] private bool _hasQueue;
    [ObservableProperty] private double _queueProgress;
    [ObservableProperty] private string _queueTitle = "";
    [ObservableProperty] private string _queueCounter = "";
    [ObservableProperty] private bool _stopRequested;

    public bool WingetMissing => WingetState == WingetState.Missing;
    public bool WingetReady => WingetState == WingetState.Available;
    public bool WingetChecking => WingetState == WingetState.Checking;
    public bool CanStartQueue => !IsQueueRunning && !IsVendorDownloadRunning;
    public bool CanCloseQueue => !IsQueueRunning && !IsVendorDownloadRunning;
    public bool HasSelection => SelectedCount > 0;
    public bool HasNoResults => VisibleCount == 0;
    public bool HasSearch => SearchText.Trim().Length > 0;

    /// <summary>
    /// The «РЕКОМЕНДУЕМ» tab without a search is empty: everything it would show is installed — or nothing is known yet
    /// (the advice needs the list of installed programs).
    /// </summary>
    private bool EmptyRecommendedTab => SelectedCategory is { IsRecommended: true } && !HasSearch;

    public string NoResultsTitle => !EmptyRecommendedTab ? "Ничего не найдено"
        : HasDetection ? "Всё рекомендуемое уже установлено"
        : WingetMissing ? "Советов пока нет"
        : "Советы появятся после проверки";

    public string NoResultsText => !EmptyRecommendedTab
        ? "Попробуйте другое название или ID пакета либо выберите вкладку «ВСЕ». В каталоге только проверенные пакеты winget и Microsoft Store."
        : HasDetection
            ? "Программ с пометкой НУЖНО или РЕКОМЕНДУЕТСЯ, которых нет на этом ПК, не осталось. Остальное — во вкладке «ВСЕ»."
            : WingetMissing
                ? "Без winget MONOLITH не может проверить, что уже установлено. Установите «Установщик приложений» и нажмите «Проверить снова»."
                : "MONOLITH ещё не знает, что уже установлено на этом ПК: советы появятся, когда winget закончит проверку установленных программ.";

    partial void OnHasDetectionChanged(bool value) => NotifyNoResultsText();
    public string VisibleText => VisibleCount == TotalCount ? AppTexts.Programs(TotalCount) : $"{VisibleCount} из {TotalCount}";
    public string SelectedText => SelectedCount == 0 ? "НИЧЕГО НЕ ВЫБРАНО" : $"ВЫБРАНО: {SelectedCount}";

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(HasSearch));
        NotifyNoResultsText();
    }

    partial void OnVisibleCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(VisibleText));
    }

    partial void OnSelectedCategoryChanged(CategoryFilterViewModel? value) => NotifyNoResultsText();

    private void NotifyNoResultsText()
    {
        OnPropertyChanged(nameof(NoResultsTitle));
        OnPropertyChanged(nameof(NoResultsText));
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedText));
        InstallSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnWingetStateChanged(WingetState value)
    {
        foreach (var item in _items) item.WingetReady = value == WingetState.Available;
        RefreshCommandStates();
        NotifyNoResultsText();
    }

    partial void OnIsQueueRunningChanged(bool value) => UpdateBusyItems();

    /// <summary>Install buttons wait while the queue or a direct vendor download runs.</summary>
    private void UpdateBusyItems()
    {
        var busy = IsQueueRunning || IsVendorDownloadRunning;
        foreach (var item in _items) item.QueueBusy = busy;
        RefreshCommandStates();
    }

    private void RefreshCommandStates()
    {
        InstallSelectedCommand.NotifyCanExecuteChanged();
        InstallKitCommand.NotifyCanExecuteChanged();
        UpdateAllCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        RunFallbackCommand.NotifyCanExecuteChanged();
        CloseQueueCommand.NotifyCanExecuteChanged();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppItemViewModel.IsSelected))
            SelectedCount = _items.Count(i => i.IsSelected);
    }

    // ─────────────────────────── lifecycle ───────────────────────────

    protected override async Task LoadAsync()
    {
        var cache = await Task.Run(InstalledDetector.LoadCache);
        if (cache is not null) ApplySnapshot(cache, partial: false);
        _ = LoadProfileAsync();
        _ = ScanPeripheralsAsync();
        await CheckWingetAndDetectAsync();
    }

    protected override async Task OnReturnAsync()
    {
        // The profile may have been incomplete on the first visit (slow WMI after a cold boot): read it again (cheap: cached).
        if (!_gpuKnown || _osUbr == 0) _ = LoadProfileAsync();
        if (IsQueueRunning || _detecting || WingetState == WingetState.Checking) return;
        if (DateTime.Now - _lastCheck > TimeSpan.FromMinutes(3))
        {
            _ = ScanPeripheralsAsync();
            await CheckWingetAndDetectAsync();
        }
    }

    // ─────────────────────────── install advice ───────────────────────────

    /// <summary>Graphics cards and Windows build for the advice (the profile is collected once, in the background).</summary>
    private async Task LoadProfileAsync()
    {
        try
        {
            var loaded = await SystemProfile.LoadAsync();
            // A retry that read less than an earlier pass does not replace it: Current is the richer profile.
            ApplyProfile(SystemProfile.Current.IsLoaded ? SystemProfile.Current : loaded);
            UpdateAdvice();
        }
        catch (Exception ex)
        {
            Log.Warn($"Приложения: не удалось определить видеокарту и сборку Windows ({ex.Message})");
        }
    }

    private void ApplyProfile(SystemProfile profile)
    {
        // The build number is known before the profile is collected (Environment.OSVersion); the rest is not.
        _osBuild = profile.OsBuild;
        if (!profile.IsLoaded) return;
        var missing = profile.MissingFacts;
        _gpuKnown = !missing.Contains("gpu") && profile.Gpus.Count > 0;
        _gpuVendors = profile.Gpus.Select(g => g.Vendor).Where(v => v is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel).ToHashSet();
        _osBuild = profile.OsBuild;
        _osUbr = missing.Contains("os") ? 0 : profile.OsUbr;
    }

    /// <summary>Read-only USB/Bluetooth scan for Logitech, Razer, Corsair, SteelSeries and Elgato devices.</summary>
    private async Task ScanPeripheralsAsync()
    {
        if (_scanningPeripherals) return;
        _scanningPeripherals = true;
        try
        {
            _peripherals = await PeripheralScan.ScanAsync();
            UpdateAdvice();
        }
        finally
        {
            _scanningPeripherals = false;
        }
    }

    private AdviceContext BuildAdviceContext() => new()
    {
        GpuKnown = _gpuKnown,
        GpuVendors = _gpuVendors,
        OsBuild = _osBuild,
        OsUbr = _osUbr,
        InstalledKnown = HasDetection,
        StoreKnown = _storeKnown,
        // Every id of an installed entry (regional variants too), including components found outside winget.
        InstalledIds = _items.Where(i => i.IsInstalled).SelectMany(i => i.Entry.AllIds).ToHashSet(StringComparer.OrdinalIgnoreCase),
        PeripheralsKnown = _peripherals.Known,
        Peripherals = _peripherals.Brands,
        Today = DateOnly.FromDateTime(DateTime.Today),
    };

    /// <summary>Recomputes the advice of every card, the gamer kit and the tab counts (pure and cheap).</summary>
    private void UpdateAdvice()
    {
        var ctx = BuildAdviceContext();
        foreach (var item in _items) item.Advice = AppAdvisor.Assess(item.Entry, ctx);
        var kit = AppAdvisor.Kit(_items.Select(i => i.Entry), ctx);
        KitItems = kit.Select(e => _items.First(i => ReferenceEquals(i.Entry, e))).ToList();
        var leftOut = _items.Select(i => i.Entry).Where(e => e.Category == AppCategory.Runtimes && e.CanInstall && !kit.Contains(e));
        KitToolTip = AppTexts.KitToolTip(kit, leftOut);
        UpdateKit();
        ApplyFilter();
    }

    // ─────────────────────────── busy indicator ───────────────────────────

    private void BeginBusy(string text)
    {
        _busyDepth++;
        IsBusy = true;
        BusyText = text;
    }

    private void SetBusyText(string text)
    {
        if (_busyDepth > 0) BusyText = text;
    }

    private void EndBusy()
    {
        _busyDepth = Math.Max(0, _busyDepth - 1);
        if (_busyDepth != 0) return;
        IsBusy = false;
        BusyText = null;
    }

    // ─────────────────────────── detection ───────────────────────────

    private async Task CheckWingetAndDetectAsync()
    {
        if (_detecting) return;
        _detecting = true;
        BeginBusy("Проверка winget…");
        try
        {
            // A re-check keeps the current state (and the user's selection) until the answer is known.
            var version = await Task.Run(WingetClient.GetVersionAsync);
            if (version is null)
            {
                WingetState = WingetState.Missing;
                WingetVersionText = "winget не найден";
                DetectionText = "winget не найден — установка недоступна";
                Log.Warn("Приложения: winget (Установщик приложений) не найден.");
                return;
            }

            WingetVersionText = $"winget v{version} · источники winget и msstore";
            WingetOutdated = WingetClient.IsClientOutdated(version);
            WingetState = WingetState.Available;

            SetBusyText("Проверка установленных программ…");
            await DetectCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Приложения: проверка winget", ex);
            Ui.Toast(ex.Message, ToastKind.Error, "Проверка установленных программ");
            if (WingetState == WingetState.Checking) WingetState = WingetState.Missing;
        }
        finally
        {
            _detecting = false;
            EndBusy();
        }
    }

    /// <summary>Re-reads installed packages (used after the queue and by CheckWingetAndDetectAsync).</summary>
    private async Task DetectCoreAsync()
    {
        DetectionText = "Проверяем установленные программы…";
        try
        {
            var catalog = _items.Select(i => i.Entry).ToList();
            var snapshot = await Task.Run(() => InstalledDetector.DetectAsync(catalog,
                partial => Ui.Post(() => ApplySnapshot(partial, partial: true)), CancellationToken.None));
            ApplySnapshot(snapshot, partial: false);
        }
        catch (Exception ex)
        {
            Log.Error("Приложения: проверка установленных программ", ex);
            DetectionText = "Не удалось проверить установленные программы";
            Ui.Toast(ex.Message, ToastKind.Error, "Проверка установленных программ");
        }
    }

    /// <summary>Applies a detection result. Partial results skip Store entries (they are checked one by one later).</summary>
    private void ApplySnapshot(InstalledSnapshot snapshot, bool partial)
    {
        foreach (var item in _items)
        {
            if (partial && item.Entry.Source == PackageSource.MsStore && item.IsKnown) continue;

            string? found = null;
            AppVariant? variant = null;
            var unmanaged = false;
            foreach (var id in item.Entry.AllIds)
            {
                if (!snapshot.Packages.TryGetValue(id, out var version)) continue;
                found = InstalledDetector.CleanVersion(version);
                variant = item.Variants.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
                unmanaged = snapshot.Unmanaged.Contains(id);
                break;
            }
            item.IsUnmanaged = unmanaged;
            item.InstalledVersion = found;
            if (variant is not null) item.SelectedVariant = variant;
            item.IsKnown = true;
        }

        foreach (var g in Groups) g.InstalledCount = g.Items.Count(i => i.IsVisible && i.IsInstalled);
        InstalledCount = _items.Count(i => i.IsInstalled);
        SelectedCount = _items.Count(i => i.IsSelected);
        HasDetection = true;
        if (!partial) _storeKnown = true;
        UpdateAdvice();

        if (!partial)
        {
            _lastCheck = snapshot.FromCache ? DateTime.MinValue : snapshot.CheckedAt;
            DetectionText = snapshot.FromCache
                ? $"По данным проверки {snapshot.CheckedAt:dd.MM HH:mm} · обновляется"
                : $"Проверено в {snapshot.CheckedAt:HH:mm}";
        }
        RefreshCommandStates();
    }

    private void UpdateKit()
    {
        KitTotal = KitItems.Count;
        KitInstalled = KitItems.Count(i => i.IsInstalled);
        KitComplete = KitTotal > 0 && KitInstalled == KitTotal;
        var left = KitTotal - KitInstalled;
        KitStatusText = KitComplete ? "Все библиотеки на месте" : $"Не хватает: {AppTexts.Packages(left)}";
        KitButtonText = KitComplete ? "НАБОР УСТАНОВЛЕН" : $"УСТАНОВИТЬ НАБОР · {left}";
        InstallKitCommand.NotifyCanExecuteChanged();
    }

    // ─────────────────────────── filtering ───────────────────────────

    [RelayCommand]
    private void SelectCategory(CategoryFilterViewModel? filter)
    {
        if (filter is null) return;
        foreach (var c in Categories) c.IsSelected = ReferenceEquals(c, filter);
        SelectedCategory = filter;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    private void ApplyFilter()
    {
        var query = SearchText;
        var tab = SelectedCategory ?? Categories[0];
        var total = 0;
        foreach (var g in Groups)
        {
            var visible = 0;
            foreach (var item in g.Items)
            {
                item.IsVisible = tab.Includes(item) && AppTexts.Matches(item.Entry, query);
                if (item.IsVisible) visible++;
            }
            g.VisibleCount = visible;
            g.InstalledCount = g.Items.Count(i => i.IsVisible && i.IsInstalled);
            g.IsVisible = visible > 0;
            total += visible;
        }
        VisibleCount = total;

        foreach (var c in Categories)
            c.Count = _items.Count(i => c.Includes(i) && AppTexts.Matches(i.Entry, query));
    }

    // ─────────────────────────── selection ───────────────────────────

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in _items) item.IsSelected = false;
    }

    // ─────────────────────────── actions ───────────────────────────

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh()
    {
        if (!_gpuKnown || _osUbr == 0) _ = LoadProfileAsync();
        _ = ScanPeripheralsAsync();
        return CheckWingetAndDetectAsync();
    }

    private bool CanRefresh() => !IsQueueRunning;

    [RelayCommand]
    private async Task RunAction(AppItemViewModel? item)
    {
        if (item is null) return;
        switch (item.Action)
        {
            case AppAction.Website:
                await OpenSite(item);
                return;
            case AppAction.VendorDownload:
                await DownloadFromVendorAsync(item, null, afterHashMismatch: false);
                return;
            case AppAction.Install:
                await StartQueueAsync([(item, QueueOperation.Install)], QueueKind.Single);
                return;
            case AppAction.Upgrade:
                await StartQueueAsync([(item, QueueOperation.Upgrade)], QueueKind.Single);
                return;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private async Task InstallSelected()
    {
        var picked = _items.Where(i => i.IsSelected && i.CanSelect)
            .Select(i => (i, i.IsInstalled ? QueueOperation.Upgrade : QueueOperation.Install))
            .ToList();
        await StartQueueAsync(picked, QueueKind.Selection);
    }

    private bool CanInstallSelected() => SelectedCount > 0 && WingetReady && !IsQueueRunning;

    [RelayCommand(CanExecute = nameof(CanInstallKit))]
    private async Task InstallKit()
    {
        var missing = KitItems.Where(i => !i.IsInstalled && i.Entry.CanInstall).Select(i => (i, QueueOperation.Install)).ToList();
        if (missing.Count == 0)
        {
            Ui.Toast("Все библиотеки набора уже установлены.", ToastKind.Success, "НАБОР ГЕЙМЕРА");
            return;
        }
        await StartQueueAsync(missing, QueueKind.Kit);
    }

    private bool CanInstallKit() => WingetReady && !IsQueueRunning && HasDetection && !KitComplete;

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAll()
    {
        // Only packages winget itself manages: a runtime that came with the .NET SDK is "not found" for winget upgrade.
        var candidates = _items
            .Where(i => i.CanUpgrade
                        && !string.Equals(i.PackageId, AppCatalog.AppInstallerId, StringComparison.OrdinalIgnoreCase))
            .Select(i => (i, QueueOperation.Upgrade))
            .ToList();
        if (candidates.Count == 0)
        {
            Ui.Toast("Из каталога не установлено ни одной программы, которую можно обновить через winget.", ToastKind.Info, "ОБНОВИТЬ ВСЁ");
            return;
        }
        await StartQueueAsync(candidates, QueueKind.UpdateAll);
    }

    private bool CanUpdateAll() => WingetReady && !IsQueueRunning && HasDetection;

    [RelayCommand]
    private async Task OpenSite(AppItemViewModel? item)
    {
        if (item is null) return;
        await OpenLinkAsync(item.Entry.Website);
    }

    [RelayCommand]
    private Task OpenAppInstallerStore() => OpenLinkAsync(AppCatalog.AppInstallerStoreUri);

    private static async Task OpenLinkAsync(string link)
    {
        if (await UnelevatedShell.OpenAsync(link)) return;
        try
        {
            Clipboard.SetText(link);
            Ui.Toast($"Не удалось открыть ссылку без прав администратора. Адрес скопирован в буфер обмена: {link}", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Warn($"Буфер обмена недоступен: {ex.Message}");
            Ui.Toast($"Не удалось открыть ссылку: {link}", ToastKind.Warning);
        }
    }

    // ─────────────────────────── queue ───────────────────────────

    private enum QueueKind { Single, Selection, Kit, UpdateAll }

    private async Task StartQueueAsync(IReadOnlyList<(AppItemViewModel Item, QueueOperation Op)> plan, QueueKind kind)
    {
        if (plan.Count == 0) return;
        if (IsQueueRunning || IsVendorDownloadRunning)
        {
            Ui.Toast(IsVendorDownloadRunning
                ? "Дождитесь окончания загрузки установщика у производителя."
                : "Дождитесь окончания текущей очереди установки.", ToastKind.Warning);
            return;
        }
        if (!WingetReady)
        {
            Ui.Toast("winget недоступен — установите «Установщик приложений» из Microsoft Store.", ToastKind.Warning);
            return;
        }

        if (!await ConfirmAsync(plan, kind)) return;

        foreach (var old in Queue) if (ReferenceEquals(old.App.Job, old)) old.App.Job = null;
        Queue.Clear();
        QueueLog.Clear();
        foreach (var (item, op) in plan)
        {
            var job = new QueueItemViewModel(item, op);
            item.Job = job;
            item.IsSelected = false;
            Queue.Add(job);
        }
        HasQueue = true;
        QueueTitle = kind switch
        {
            QueueKind.Kit => "НАБОР ГЕЙМЕРА",
            QueueKind.UpdateAll => "ОБНОВЛЕНИЕ ВСЕГО",
            _ => plan.All(p => p.Op == QueueOperation.Upgrade) ? "ОБНОВЛЕНИЕ" : "УСТАНОВКА",
        };
        await RunQueueAsync();
    }

    private static async Task<bool> ConfirmAsync(IReadOnlyList<(AppItemViewModel Item, QueueOperation Op)> plan, QueueKind kind)
    {
        // Packages MONOLITH does not advise installing now go first, with the reason; the install is never blocked.
        var installs = plan.Where(p => p.Op == QueueOperation.Install).Select(p => (p.Item.Entry, p.Item.ShownAdvice)).ToList();
        var notes = AppTexts.AdviceNotes(installs);
        var details = notes.ToList();
        details.AddRange(plan.Select(p => AppTexts.QueueLine(p.Item.Entry, p.Item.PackageId, p.Op, p.Item.InstalledVersion)));
        details.AddRange(AppTexts.Warnings(plan.Select(p => p.Item), AppTexts.NotedEntries(installs)));

        var upgradeOnly = plan.All(p => p.Op == QueueOperation.Upgrade);
        var title = kind switch
        {
            QueueKind.Kit => "НАБОР ГЕЙМЕРА",
            QueueKind.UpdateAll => "ОБНОВИТЬ ВСЁ",
            _ => upgradeOnly ? "ОБНОВЛЕНИЕ ПРОГРАММ" : "УСТАНОВКА ПРОГРАММ",
        };

        var lead = kind switch
        {
            QueueKind.Kit => $"Будут установлены недостающие библиотеки ({AppTexts.Packages(plan.Count)}). Уже установленные пропускаются. ",
            QueueKind.UpdateAll => $"winget проверит обновления установленных программ из каталога ({plan.Count}) и поставит новые версии. " +
                                   "Программы, которые обновляются сами (Discord, игры Riot, Edge, WebView2), компоненты, поставленные не через winget " +
                                   "(например, среда .NET из .NET SDK), и «Установщик приложений» MONOLITH не трогает. ",
            _ => $"По очереди будет обработано: {AppTexts.Packages(plan.Count)}. ",
        };
        var how = AppTexts.VerificationText(plan.Select(p => p.Item.Entry)) + " ";
        var elevation = Elevation.IsElevated
            ? "Программы для всех пользователей ставятся с правами администратора, пакеты для текущего пользователя и из Microsoft Store — без них. "
            : "MONOLITH работает без прав администратора: установщик каждой программы для всех пользователей сам покажет запрос UAC. ";
        var undo = "Удалить программу можно в «Параметры → Приложения».";

        string footnote;
        if (!Elevation.IsElevated) footnote = "Без прав администратора точка восстановления не создаётся.";
        else if (!AppSettings.Current.CreateRestorePoints) footnote = "Точки восстановления перед изменениями выключены (раздел «Восстановление»).";
        else if (!await Task.Run(RestorePointService.IsEnabled))
            footnote = "Защита системы отключена — точка восстановления создана не будет.";
        else footnote = "Перед началом будет создана точка восстановления.";

        var danger = plan.Any(p => AppTexts.IsRisky(p.Item.Entry));
        var confirm = upgradeOnly ? "ОБНОВИТЬ" : "УСТАНОВИТЬ";
        // winget gets --accept-package-agreements / --accept-source-agreements: say so before the user agrees.
        var license = " " + AppTexts.LicenseText(plan.Select(p => p.Item.Entry), upgradeOnly ? "Обновить" : "Установить");
        return await Ui.ConfirmAsync(title, AppTexts.AdviceLead(notes.Count) + lead + how + elevation + undo + license, details, confirm, "ОТМЕНА",
            danger, footnote);
    }

    private async Task RunQueueAsync()
    {
        IsQueueRunning = true;
        StopRequested = false;
        QueueProgress = 0;
        _queueCts = new CancellationTokenSource();
        var ct = _queueCts.Token;
        BeginBusy($"{QueueTitle}: подготовка…");
        var total = Queue.Count;
        try
        {
            if (Elevation.IsElevated && AppSettings.Current.CreateRestorePoints)
            {
                AppendLog("Создание точки восстановления…");
                SetBusyText("Создание точки восстановления…");
                var rp = await RestorePointService.CreateAsync($"MONOLITH: {QueueTitle.ToLowerInvariant()}");
                AppendLog(rp.Message);
                if (rp.Outcome == RestorePointOutcome.Failed && !await Ui.ConfirmAsync(
                        "ТОЧКА ВОССТАНОВЛЕНИЯ НЕ СОЗДАНА",
                        $"{rp.Message} Без неё откатить установку можно будет только удалением программы в «Параметры → Приложения». Продолжить?",
                        confirmText: "ПРОДОЛЖИТЬ", cancelText: "ОТМЕНИТЬ", danger: true))
                {
                    AppendLog("Очередь отменена: точка восстановления не создана.");
                    CancelRemaining();
                    return;
                }
            }

            for (var i = 0; i < total; i++)
            {
                var job = Queue[i];
                if (StopRequested || ct.IsCancellationRequested)
                {
                    CancelRemaining();
                    break;
                }

                QueueCounter = $"{i + 1} / {total}";
                var verb = job.Operation == QueueOperation.Install ? "УСТАНОВКА" : "ОБНОВЛЕНИЕ";
                job.State = QueueState.Running;
                job.StatusText = $"{verb}…";
                SetBusyText($"{(job.Operation == QueueOperation.Install ? "Установка" : "Обновление")} {i + 1}/{total}: {job.Name}");
                AppendLog("");
                AppendLog($"▌ {job.Name} · {job.PackageId} · {job.SourceName}");

                var outcome = await ExecuteAsync(job, ct);
                ApplyOutcome(job, outcome);
                QueueProgress = (i + 1) * 100.0 / total;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Приложения: очередь установки", ex);
            AppendLog("Ошибка очереди: " + ex.Message);
            Ui.Toast(ex.Message, ToastKind.Error, "Очередь установки");
            CancelRemaining();
        }
        finally
        {
            QueueCounter = $"{Queue.Count(q => q.IsFinished)} / {total}";
            QueueProgress = 100;
            IsQueueRunning = false;
            _queueCts.Dispose();
            _queueCts = null;
            EndBusy();
        }

        AppendLog("");
        AppendLog("Проверка установленных программ…");
        BeginBusy("Проверка установленных программ…");
        try
        {
            await DetectCoreAsync();
        }
        finally
        {
            EndBusy();
        }
        VerifyDirectX();
        Summarize();
    }

    private void CancelRemaining()
    {
        foreach (var q in Queue.Where(q => q.State == QueueState.Queued))
        {
            q.State = QueueState.Cancelled;
            q.StatusText = "ОТМЕНЕНО";
        }
    }

    /// <param name="Error">MONOLITH itself failed to run winget for this package (the queue goes on with the next one).</param>
    private sealed record RunOutcome(UnelevatedOutcome Outcome, WingetResult Result, string? Error = null);

    /// <summary>Never throws: a failure of one package must not stop (or crash) the queue.</summary>
    private async Task<RunOutcome> ExecuteAsync(QueueItemViewModel job, CancellationToken ct)
    {
        try
        {
            return await ExecuteCoreAsync(job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new RunOutcome(UnelevatedOutcome.Cancelled, new WingetResult(-1, "", Cancelled: true));
        }
        catch (Exception ex)
        {
            Log.Error($"Приложения: {job.Name} ({job.PackageId})", ex);
            return new RunOutcome(UnelevatedOutcome.LaunchFailed, new WingetResult(-1, ex.Message), ex.Message);
        }
    }

    private async Task<RunOutcome> ExecuteCoreAsync(QueueItemViewModel job, CancellationToken ct)
    {
        var entry = job.App.Entry;
        var request = entry.CreateRequest(job.PackageId);
        if (job.Operation == QueueOperation.Upgrade && request.Location is { } known && !LocationExists(known))
        {
            // The program was installed into another folder: the standard one would put a second copy next to it.
            request = request with { Location = null };
        }
        if (WingetClient.ExpandLocation(request.Location) is { } folder) AppendLog($"Папка установки: {folder}");

        var outcome = await RunWingetAsync(job, request, ct);

        // INSTALL_LOCATION_REQUIRED for a package the catalog has no folder for: winget may not ask with
        // --disable-interactivity, so retry once with a standard folder (and say which one).
        if (outcome.Outcome == UnelevatedOutcome.Completed && outcome.Result.ExitCode == WingetClient.InstallLocationRequired
            && job.Operation == QueueOperation.Install && request.Location is null && !ct.IsCancellationRequested)
        {
            var retry = request with { Location = entry.DefaultLocationTemplate };
            var shown = Elevation.IsElevated && entry.RunsUnelevated
                ? retry.Location!
                : WingetClient.ExpandLocation(retry.Location) ?? retry.Location!;
            AppendLog($"Установщик требует папку установки — повтор с папкой «{shown}».");
            Log.Info($"winget install {job.PackageId}: пакет требует папку установки (0x8A15005F) — повтор с --location «{shown}».");
            outcome = await RunWingetAsync(job, retry, ct);
        }
        return outcome;
    }

    private static bool LocationExists(string template) =>
        WingetClient.ExpandLocation(template) is { } path && System.IO.Directory.Exists(path);

    /// <summary>One winget run with the elevation rules: de-elevated for user-scope packages, retried de-elevated on 0x8A150056.</summary>
    private async Task<RunOutcome> RunWingetAsync(QueueItemViewModel job, WingetPackageRequest request, CancellationToken ct)
    {
        var entry = job.App.Entry;
        var install = job.Operation == QueueOperation.Install;
        void Output(string line) => Ui.Post(() => AppendLog(line));

        if (Elevation.IsElevated && entry.RunsUnelevated)
        {
            AppendLog("Запуск без прав администратора — пакет ставится в профиль текущего пользователя.");
            return await RunUnelevatedAsync(request, install, Output, ct);
        }

        var result = await Task.Run(() => install
            ? WingetClient.InstallPackageAsync(request, Output, ct)
            : WingetClient.UpgradePackageAsync(request, Output, ct), CancellationToken.None);

        if (result.ExitCode == WingetClient.ElevationProhibited && Elevation.IsElevated && !ct.IsCancellationRequested)
        {
            AppendLog("Установщик запрещает запуск от администратора — повтор без прав администратора…");
            return await RunUnelevatedAsync(request, install, Output, ct);
        }
        return new RunOutcome(result.Cancelled ? UnelevatedOutcome.Cancelled
            : result.StillRunning ? UnelevatedOutcome.Untracked
            : UnelevatedOutcome.Completed, result);
    }

    private static async Task<RunOutcome> RunUnelevatedAsync(WingetPackageRequest request, bool install, Action<string> output, CancellationToken ct)
    {
        // --location stays "%LOCALAPPDATA%\…": the signed-in user's cmd expands it to that user's profile, not the admin's.
        var args = install
            ? WingetClient.BuildInstallArgs(request, keepLocationTemplate: true)
            : WingetClient.BuildUpgradeArgs(request, keepLocationTemplate: true);
        var r = await Task.Run(() => UnelevatedWinget.RunAsync(args, output, ct), CancellationToken.None);
        return new RunOutcome(r.Outcome, r.ToWinget());
    }

    private void ApplyOutcome(QueueItemViewModel job, RunOutcome outcome)
    {
        switch (outcome.Outcome)
        {
            case UnelevatedOutcome.LaunchFailed when outcome.Error is not null:
                job.State = QueueState.Failed;
                job.StatusText = "ОШИБКА: MONOLITH не смог запустить winget";
                job.Detail = $"{outcome.Error} Повторите установку; если ошибка повторяется — установите программу с официального сайта.";
                job.Fallback = QueueFallback.Website;
                AppendLog(job.Detail);
                return;
            case UnelevatedOutcome.LaunchFailed:
                job.State = QueueState.Failed;
                job.StatusText = "ОШИБКА: не удалось запустить без прав администратора";
                job.Detail = "Рабочий стол Windows недоступен для запуска. Откройте сайт программы (кнопка «ОТКРЫТЬ САЙТ») " +
                             "или запустите MONOLITH без прав администратора и повторите.";
                job.Fallback = QueueFallback.Website;
                AppendLog(job.Detail);
                return;
            case UnelevatedOutcome.Untracked when outcome.Result.StillRunning:
                job.State = QueueState.Unknown;
                job.StatusText = "ЕЩЁ ИДЁТ: результат проверим заново";
                job.Detail = "Установка идёт дольше 30 минут. MONOLITH перестал ждать, но не прерывал её, чтобы программа не осталась " +
                             "установленной наполовину. Список установленного будет перечитан после очереди; если программа не появится, " +
                             "проверьте её позже или установите заново.";
                AppendLog(job.Detail);
                if (Queue.Any(q => q.State == QueueState.Queued))
                {
                    // Starting the next installer while this one still runs would make them collide.
                    StopRequested = true;
                    CancelRemaining();
                    AppendLog("Остальные пакеты отменены, чтобы не запускать установки одновременно. Повторите их позже.");
                }
                return;
            case UnelevatedOutcome.Untracked:
                job.State = QueueState.Unknown;
                job.StatusText = "ЗАПУЩЕНО: результат проверим заново";
                job.Detail = "winget запущен отдельно, но его ход отследить не удалось. Список установленного будет перечитан после очереди.";
                AppendLog(job.Detail);
                return;
            case UnelevatedOutcome.Cancelled:
                job.Detail = "Установка прервана вами. Программа могла остаться установленной не полностью — повторите установку " +
                             "или удалите её в «Параметры → Приложения».";
                job.State = QueueState.Cancelled;
                job.StatusText = "ПРЕРВАНО";
                AppendLog("Прервано пользователем.");
                return;
        }

        var code = outcome.Result.ExitCode;
        var (state, text, reboot) = AppTexts.DescribeResult(code, job.Operation);
        if (state != QueueState.Done)
        {
            // Set before the state, so the row shows the explanation and the way forward together.
            job.Detail = AppTexts.ResultDetail(code, job.Operation, job.App.Entry);
            job.Fallback = AppTexts.FallbackFor(code, job.Operation, job.App.Entry);
        }
        job.State = state;
        job.StatusText = text;
        job.NeedsReboot = reboot || (state == QueueState.Done && job.Operation == QueueOperation.Install && job.App.Entry.Has(AppFlags.Reboot));
        AppendLog(state == QueueState.Done ? $"Готово: {WingetClient.Describe(code)}." : $"Итог: {WingetClient.Describe(code)}.");
        if (state != QueueState.Done && job.Detail is { } detail) AppendLog(detail);
    }

    /// <summary>
    /// winget may report Microsoft.DirectX as present because of the MSIX "DirectXRuntime" framework;
    /// only the classic D3DX9 / XAudio 2.7 files prove the June 2010 runtime is installed.
    /// </summary>
    private void VerifyDirectX()
    {
        var job = Queue.FirstOrDefault(q => q.App.Entry.Has(AppFlags.ForceExe) && q.State == QueueState.Done);
        if (job is null || job.App.IsInstalled) return;
        job.Detail = "winget сообщил об успехе, но классических файлов DirectX (d3dx9_43.dll, xaudio2_7.dll) нет. " +
                     "Скачайте установщик с сайта Microsoft (кнопка «ОТКРЫТЬ САЙТ»).";
        job.Fallback = QueueFallback.Website;
        job.State = QueueState.Failed;
        job.StatusText = "ОШИБКА: библиотеки D3DX9 не появились";
        AppendLog(job.Detail);
    }

    private void Summarize()
    {
        var done = Queue.Count(q => q.State == QueueState.Done);
        var failed = Queue.Count(q => q.State == QueueState.Failed);
        var skipped = Queue.Count(q => q.State == QueueState.Skipped);
        var cancelled = Queue.Count(q => q.State == QueueState.Cancelled);
        var unknown = Queue.Count(q => q.State == QueueState.Unknown);
        var reboot = Queue.Any(q => q.NeedsReboot);

        var parts = new List<string> { $"готово: {done}" };
        if (failed > 0) parts.Add($"ошибок: {failed}");
        if (skipped > 0) parts.Add($"пропущено: {skipped}");
        if (cancelled > 0) parts.Add($"отменено: {cancelled}");
        if (unknown > 0) parts.Add($"без отчёта: {unknown}");
        var message = string.Join(" · ", parts) + (reboot ? ". Для завершения нужна перезагрузка." : ".");
        AppendLog(message);
        Ui.Toast(message, failed > 0 ? ToastKind.Warning : ToastKind.Success, $"{QueueTitle}: ОЧЕРЕДЬ ЗАВЕРШЕНА");
    }

    [RelayCommand]
    private void StopQueue()
    {
        if (!IsQueueRunning || StopRequested) return;
        StopRequested = true;
        CancelRemaining();
        AppendLog("Остановка: текущий пакет будет доведён до конца, остальные отменены.");
    }

    [RelayCommand]
    private async Task AbortQueue()
    {
        if (!IsQueueRunning || _queueCts is null) return;
        var ok = await Ui.ConfirmAsync("ПРЕРВАТЬ СЕЙЧАС?",
            "Процесс winget и запущенный им установщик будут принудительно завершены. Программа может остаться " +
            "установленной не полностью — тогда установите её заново или удалите в «Параметры → Приложения».",
            null, "ПРЕРВАТЬ", "ПРОДОЛЖИТЬ УСТАНОВКУ", danger: true);
        if (!ok || _queueCts is null) return;
        StopRequested = true;
        CancelRemaining();
        AppendLog("Прерывание текущей установки…");
        _queueCts.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanCloseQueue))]
    private void CloseQueue()
    {
        if (IsQueueRunning || IsVendorDownloadRunning) return;
        foreach (var q in Queue) if (ReferenceEquals(q.App.Job, q)) q.App.Job = null;
        Queue.Clear();
        QueueLog.Clear();
        HasQueue = false;
        QueueProgress = 0;
        QueueCounter = "";
    }

    private void AppendLog(string line)
    {
        if (QueueLog.Count > 0 && line.Length > 0 && QueueLog[^1] == line) return;
        QueueLog.Add(line);
        while (QueueLog.Count > MaxLogLines) QueueLog.RemoveAt(0);
    }
}
