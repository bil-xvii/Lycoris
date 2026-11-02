using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Apps;
using Monolith.Modules.Drivers.Hardware;
using Monolith.UI;

namespace Monolith.Modules.Drivers;

/// <summary>
/// "ПОДБОР ДЛЯ ВАШЕГО ПК": the program analyses the hardware (read-only, off the UI thread) and picks the official
/// components it needs — GPU drivers from the vendor's server, game runtimes and vendor tools via verified winget ids,
/// device drivers via Windows Update, and manual steps (BIOS, XMP/EXPO, refresh rate) as advice only.
/// Nothing is installed without the confirmation dialog; a scan never starts an installation.
/// </summary>
public sealed partial class DriversViewModel
{
    private HardwareSnapshot? _hwSnapshot;
    private OnlineInfo _hwOnline = OnlineInfo.NotStarted;
    private CancellationTokenSource? _hwScanCts;
    private CancellationTokenSource? _hwInstallCts;
    private bool _hwHooked;
    private bool _hwRebuildQueued;
    private bool _hwRebuildAfterInstall;

    /// <summary>Component groups shown as cards, in a fixed order.</summary>
    public ObservableCollection<HwGroupItem> HardwareGroups { get; } = [];

    /// <summary>Outcome of the last installation run (restore point, every item).</summary>
    public ObservableCollection<HwResultItem> HardwareResults { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanHardwareCommand), nameof(InstallHardwareCommand))]
    private bool _isHardwareScanning;

    [ObservableProperty] private string _hardwareScanStage = "";
    [ObservableProperty] private string _hardwareScanTime = "—";
    [ObservableProperty] private string _hardwareScanCaption = Loc.T("анализ ещё не выполнялся");
    [ObservableProperty] private bool _hardwareScanned;

    [ObservableProperty] private string _hwComponentsStat = "—";
    [ObservableProperty] private string _hwComponentsCaption = Loc.T("идёт анализ");
    [ObservableProperty] private string _hwRecommendationsStat = "—";
    [ObservableProperty] private string _hwRecommendationsCaption = Loc.T("идёт анализ");
    [ObservableProperty] private bool _hwHasRecommendations;
    [ObservableProperty] private string _hwCurrentStat = "—";
    [ObservableProperty] private string _hwCurrentCaption = Loc.T("идёт анализ");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HwInstallButtonText), nameof(PrimaryText), nameof(HasPrimary))]
    [NotifyCanExecuteChangedFor(nameof(InstallHardwareCommand))]
    private int _hwSelectedCount;

    /// <summary>The checked set is exactly what the analysis recommends by default (the header then says «РЕКОМЕНДОВАННОЕ»).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PrimaryText))] private bool _hwSelectionIsRecommended = true;

    // ── The header's ONE main button (changes with the tab) ──

    /// <summary>ПОДБОР: «УСТАНОВИТЬ РЕКОМЕНДОВАННОЕ · N» / «УСТАНОВИТЬ ВЫБРАННОЕ · N»; ОБНОВЛЕНИЯ WINDOWS: «ЗАГРУЗИТЬ И УСТАНОВИТЬ · N».</summary>
    public string PrimaryText => SelectedTab switch
    {
        DriversTab.Hardware or DriversTab.Overview => $"{(HwSelectionIsRecommended ? Loc.T("УСТАНОВИТЬ РЕКОМЕНДОВАННОЕ") : Loc.T("УСТАНОВИТЬ ВЫБРАННОЕ"))} · {HwSelectedCount}",
        DriversTab.Updates => InstallButtonText,
        _ => "",
    };

    /// <summary>The main button is shown only when it has something to do (checked items exist, nothing runs).</summary>
    public bool HasPrimary => !IsHwInstalling && !IsInstalling && SelectedTab switch
    {
        DriversTab.Hardware or DriversTab.Overview => HwSelectedCount > 0,
        DriversTab.Updates => SelectedUpdatesCount > 0,
        _ => false,
    };

    private void RaisePrimary()
    {
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(HasPrimary));
    }

    [RelayCommand]
    private Task RunPrimaryAsync() => SelectedTab switch
    {
        DriversTab.Hardware or DriversTab.Overview => InstallHardwareCommand.ExecuteAsync(null),
        DriversTab.Updates => InstallUpdatesCommand.ExecuteAsync(null),
        _ => Task.CompletedTask,
    };

    // ── Summary cells act as filters ──

    /// <summary>"all" · "recommend" (needs attention) · "current" (checked or installed) — set by the summary cells.</summary>
    [ObservableProperty] private string _hwFilter = "all";

    [RelayCommand]
    private void SetHwFilter(string? filter)
    {
        HwFilter = filter is "recommend" or "current" ? filter : "all";
    }

    partial void OnHwFilterChanged(string value) => ApplyHwFilter();

    private void ApplyHwFilter()
    {
        foreach (var group in HardwareGroups)
        {
            var any = false;
            foreach (var c in group.Components)
            {
                c.IsShown = HwFilter switch
                {
                    "recommend" => HardwareRules.NeedsAttention(c.Status),
                    "current" => c.Status is HwStatus.Current or HwStatus.Installed,
                    _ => true,
                };
                any |= c.IsShown;
            }
            group.HasShown = any;
        }
        OnPropertyChanged(nameof(HwNothingShown));
    }

    /// <summary>A filter is on and no component matches it (the list says so in text).</summary>
    public bool HwNothingShown => HwFilter != "all" && HardwareGroups.Count > 0 && HardwareGroups.All(g => !g.HasShown);

    [ObservableProperty] private string _hwSelectionText = Loc.T("Отметьте компоненты, которые нужно установить.");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallHardwareCommand), nameof(CancelHardwareInstallCommand), nameof(RescanHardwareCommand))]
    private bool _isHwInstalling;

    [ObservableProperty] private string _hwInstallStage = "";
    [ObservableProperty] private string _hwInstallDetail = "";
    [ObservableProperty] private double _hwInstallPercent;
    [ObservableProperty] private bool _hwInstallIndeterminate;
    [ObservableProperty] private string? _hwResultsSummary;

    public string HwInstallButtonText => HwSelectedCount > 0 ? Loc.Tf("УСТАНОВИТЬ ВЫБРАННОЕ · {0}", HwSelectedCount) : Loc.T("УСТАНОВИТЬ ВЫБРАННОЕ");
    public bool HasHardwareResults => HardwareResults.Count > 0;

    /// <summary>Called from LoadAsync (fire-and-forget): scans the hardware and builds the recommendations.</summary>
    private partial Task StartHardwareScanAsync()
    {
        HookHardwareEvents();
        ApplyDriversFocus(Ui.TakeFocusRequest(Key));
        return ScanHardwareAsync();
    }

    private void HookHardwareEvents()
    {
        if (_hwHooked) return;
        _hwHooked = true;
        // Links from other pages: «УСТАНОВИТЬ В «ДРАЙВЕРАХ»» on «Устройства и сеть» asks for "periph:<VID>", --tab for "tab:<name>".
        Ui.FocusRequested += r =>
        {
            if (r.PageKey == Key) ApplyDriversFocus(Ui.TakeFocusRequest(Key));
        };
        PropertyChanged += OnHardwarePageChanged;
        Updates.CollectionChanged += (_, _) => QueueHardwareRebuild();
        HardwareResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHardwareResults));
    }

    private void OnHardwarePageChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UpdatesState):
            case nameof(InventoryLoaded):
            case nameof(InventoryTime): // "ОБНОВИТЬ" re-read the driver list
                QueueHardwareRebuild();
                break;
            case nameof(IsInstalling):
                // A Windows Update installation finished: installed drivers drop out of the recommendations.
                QueueHardwareRebuild();
                InstallHardwareCommand.NotifyCanExecuteChanged();
                break;
            case nameof(IsBusy):
            case nameof(IsBackupRunning):
                InstallHardwareCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    // ───────────────────────────── Scan ─────────────────────────────

    private bool CanRescanHardware() => !IsHardwareScanning && !IsHwInstalling;

    [RelayCommand(CanExecute = nameof(CanRescanHardware))]
    private Task RescanHardwareAsync() => ScanHardwareAsync();

    private async Task ScanHardwareAsync()
    {
        if (IsHardwareScanning) return;
        _hwScanCts?.Dispose();
        _hwScanCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _hwScanCts.Token;
        IsHardwareScanning = true;
        HardwareScanStage = Loc.T("Сбор сведений о железе…");
        HardwareScanCaption = Loc.T("идёт анализ");
        var sw = Stopwatch.StartNew();
        try
        {
            var profile = await SystemProfile.LoadAsync();
            IProgress<string> stage = new Progress<string>(t => HardwareScanStage = t);
            var snapshot = await Task.Run(() => HardwareScanner.Scan(profile, stage.Report), ct);

            HardwareScanStage = Loc.T("Ожидание списка драйверов…");
            await WaitForInventoryAsync(ct);

            _hwSnapshot = snapshot;
            _hwOnline = OnlineInfo.NotStarted;
            RebuildHardware();
            HardwareScanned = true;
            HardwareScanTime = snapshot.When.ToString("HH:mm");

            HardwareScanStage = Loc.T("Проверка версий у производителей…");
            _hwOnline = await CheckHardwareOnlineAsync(snapshot, ct);
            RebuildHardware();

            HardwareScanCaption = Loc.Tf("анализ занял {0} с", Math.Max(1, (int)Math.Round(sw.Elapsed.TotalSeconds)));
            var report = LastReport;
            Log.Info("Подбор для ПК: компонентов {0}, рекомендаций {1}.", report?.ComponentCount ?? 0, report?.RecommendationCount ?? 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            HardwareScanCaption = Loc.T("анализ прерван");
        }
        catch (Exception ex)
        {
            Log.Error("Анализ железа", ex);
            HardwareScanCaption = Loc.T("анализ не выполнен");
            Ui.Toast(Loc.Tf("Анализ железа не выполнен: {0}", ex.Message), ToastKind.Error);
        }
        finally
        {
            IsHardwareScanning = false;
            HardwareScanStage = "";
            _hwPendingFocus = null; // the row never appeared (e.g. the device was unplugged): the tab switch stays
        }
    }

    /// <summary>The driver list (chipset analysis, driver versions per device) is loaded by the page itself — wait for it.</summary>
    private async Task WaitForInventoryAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!InventoryLoaded && sw.Elapsed < TimeSpan.FromSeconds(120))
        {
            // The inventory failed (busy indicator gone, no data): continue without driver details.
            if (!IsBusy && sw.Elapsed > TimeSpan.FromSeconds(3)) return;
            await Task.Delay(200, ct);
        }
    }

    /// <summary>Latest official GPU driver versions (NVIDIA lookup, AMD installer page). Failures leave the component "НЕ ПРОВЕРЕНО".</summary>
    private static async Task<OnlineInfo> CheckHardwareOnlineAsync(HardwareSnapshot snapshot, CancellationToken ct)
    {
        var primary = snapshot.PrimaryGpu;
        var nvidia = new Dictionary<string, NvidiaRelease?>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<Task>();
        foreach (var gpu in snapshot.Gpus.Where(g => g.Vendor == GpuVendor.Nvidia && !g.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)))
        {
            tasks.Add(Task.Run(async () =>
            {
                var release = await NvidiaLookup.GetLatestAsync(gpu.Name, ct).ConfigureAwait(false);
                lock (nvidia) nvidia[gpu.PnpDeviceId] = release;
            }, ct));
        }

        // AMD: only when an AMD adapter drives the display or has no driver (a secondary iGPU is informational).
        var needAmd = snapshot.Gpus.Any(g => g.Vendor == GpuVendor.Amd &&
            (g.PnpDeviceId == primary?.PnpDeviceId || !g.IsIntegrated || g.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)));
        DriverPackage? amd = null;
        if (needAmd) tasks.Add(Task.Run(async () => amd = await DriverDownloads.GetAmdInstallerAsync(ct).ConfigureAwait(false), ct));

        await Task.WhenAll(tasks);
        return new OnlineInfo(false, nvidia, needAmd, amd);
    }

    private void QueueHardwareRebuild()
    {
        if (_hwSnapshot is null || _hwRebuildQueued) return;
        if (IsHwInstalling)
        {
            _hwRebuildAfterInstall = true;
            return;
        }
        _hwRebuildQueued = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _hwRebuildQueued = false;
            RebuildHardware();
            return;
        }
        dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _hwRebuildQueued = false;
            if (!IsHwInstalling) RebuildHardware();
        });
    }

    private HwReport? LastReport { get; set; }

    private WuView CurrentWuView()
    {
        var state = UpdatesState switch
        {
            UpdatesState.Searching => WuViewState.Searching,
            UpdatesState.Ready => WuViewState.Ready,
            UpdatesState.Error => WuViewState.Error,
            _ => WuViewState.NotSearched,
        };
        var pending = Updates.Where(u => !u.ResultOk && !u.IsFirmware).Select(u => u.Update).ToList();
        return new WuView(state, pending);
    }

    private void RebuildHardware()
    {
        if (_hwSnapshot is null) return;
        var report = HardwareAdvisor.Build(_hwSnapshot, new HardwareContext(_signedDrivers, CurrentWuView(), _hwOnline, DateTime.Now));
        LastReport = report;

        HwItemSync.Sync(HardwareGroups, report.Groups, g => g.Kind.ToString(), g => g.Kind.ToString(),
            g => new HwGroupItem(g, 0, OnHardwareSelectionChanged), (item, g) => { });
        for (var i = 0; i < report.Groups.Count; i++) HardwareGroups[i].Update(report.Groups[i], i + 1);

        var components = report.ComponentCount;
        HwComponentsStat = components.ToString();
        HwComponentsCaption = Loc.N(report.Groups.Count, "{0} группа устройств и библиотек", "{0} группы устройств и библиотек", "{0} групп устройств и библиотек");

        var recommendations = report.RecommendationCount;
        var installable = AllHardwareActions().Count(a => a.IsInstallable && !a.IsOptional);
        HwRecommendationsStat = recommendations.ToString();
        HwHasRecommendations = recommendations > 0;
        HwRecommendationsCaption = recommendations == 0
            ? Loc.T("действий не требуется")
            : installable > 0 ? Loc.Tf("установить здесь: {0}", installable) : Loc.T("только вручную или через Windows Update");

        HwCurrentStat = report.CurrentCount.ToString();
        var rest = new List<string>();
        if (report.InstalledCount > 0) rest.Add(Loc.Tf("установлено: {0}", report.InstalledCount));
        if (report.UncheckedCount > 0) rest.Add(Loc.Tf("не проверено: {0}", report.UncheckedCount));
        HwCurrentCaption = rest.Count > 0 ? string.Join(" · ", rest) : Loc.T("проверено у производителей");

        RecountHardwareSelection();
        ApplyHwFilter();
        BadgeText = recommendations > 0 ? recommendations.ToString() : null;

        // A link that arrived before the analysis finished: its row exists now.
        if (_hwPendingFocus is { } pending && FindHardwareComponent(pending) is not null)
        {
            _hwPendingFocus = null;
            ApplyDriversFocus(pending);
        }
    }

    // ───────────────────────────── Focus (links from other pages) ─────────────────────────────

    private string? _hwPendingFocus;
    private HwComponentItem? _hwHighlighted;
    private DispatcherTimer? _hwHighlightTimer;

    /// <summary>The row the hardware panel brings into view (one-shot: the panel clears it).</summary>
    [ObservableProperty] private HwComponentItem? _hardwareFocusTarget;

    private HwComponentItem? FindHardwareComponent(string key) =>
        HardwareGroups.SelectMany(g => g.Components).FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// "tab:&lt;name&gt;" switches the Drivers tab. A component key ("periph:046D" from «УСТАНОВИТЬ В «ДРАЙВЕРАХ»», "net:…")
    /// opens «Подбор для вашего ПК», scrolls to that row and frames it for 2 s — after the analysis when it is still running.
    /// </summary>
    private void ApplyDriversFocus(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (id.StartsWith("tab:", StringComparison.OrdinalIgnoreCase))
        {
            GoToTab(id[4..]);
            return;
        }
        SelectedTab = DriversTab.Hardware;
        if (FindHardwareComponent(id) is not { } item)
        {
            // Not there yet (first analysis running) — remembered until the row appears; otherwise the tab is enough.
            _hwPendingFocus = HardwareScanned && !IsHardwareScanning ? null : id;
            return;
        }

        if (_hwHighlighted is not null && !ReferenceEquals(_hwHighlighted, item)) _hwHighlighted.IsHighlighted = false;
        _hwHighlighted = item;
        item.IsHighlighted = true;
        HardwareFocusTarget = null; // re-raise for the same row
        HardwareFocusTarget = item;

        _hwHighlightTimer?.Stop();
        _hwHighlightTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hwHighlightTimer.Tick += (_, _) =>
        {
            _hwHighlightTimer?.Stop();
            if (_hwHighlighted is not null) _hwHighlighted.IsHighlighted = false;
            _hwHighlighted = null;
        };
        _hwHighlightTimer.Start();
    }

    private IEnumerable<HwActionItem> AllHardwareActions() =>
        HardwareGroups.SelectMany(g => g.Components).SelectMany(c => c.Actions);

    private void OnHardwareSelectionChanged(HwActionItem item) => RecountHardwareSelection();

    private List<HwActionItem> SelectedHardwareActions()
    {
        // Runtimes first, then programs, GPU driver installers last (they may ask to restart the PC).
        var ordered = new List<HwActionItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in HardwareGroups.OrderBy(g => g.Kind == HwGroupKind.Runtimes ? 0 : 1))
        foreach (var a in group.Components.SelectMany(c => c.Actions))
        {
            if (!a.IsSelected || !a.IsInstallable) continue;
            if (seen.Add(a.Key)) ordered.Add(a);
        }
        return ordered.OrderBy(a => a.Action.Kind == HwActionKind.DriverPackage ? 1 : 0).ToList();
    }

    private void RecountHardwareSelection()
    {
        var selected = SelectedHardwareActions();
        // «Рекомендованное» = exactly the pre-checked items of the analysis; anything else the user changed.
        var recommended = AllHardwareActions().Where(a => a.IsInstallable && a.Action.PreSelect).Select(a => a.Key).ToHashSet();
        HwSelectionIsRecommended = recommended.SetEquals(selected.Select(a => a.Key));
        HwSelectedCount = selected.Count;
        HwSelectionText = selected.Count == 0
            ? Loc.T("Отметьте компоненты, которые нужно установить.")
            : string.Join(", ", selected.Take(3).Select(a => a.Title)) + (selected.Count > 3 ? Loc.Tf(" и ещё {0}", selected.Count - 3) : "");
    }

    // ───────────────────────────── Buttons of single actions ─────────────────────────────

    [RelayCommand]
    private async Task HardwareActionAsync(HwActionItem? item)
    {
        if (item?.Action is not { } a || a.Url is not { Length: > 0 } target) return;
        switch (a.Kind)
        {
            case HwActionKind.GoToTab:
                GoToTab(target);
                return;
            case HwActionKind.GoToPage:
                // Only «Устройства и сеть» (where display modes and vendor apps live); the click itself changes nothing.
                if (!string.Equals(target, "devices", StringComparison.Ordinal)) return;
                if (a.FocusId is { Length: > 0 } focus) Ui.RequestFocus(target, focus);
                Ui.Navigate(target);
                return;
            case HwActionKind.OpenSettings:
                if (!target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return;
                if (!await UnelevatedShell.StartAsUserAsync(target))
                    Ui.Toast("Не удалось открыть параметры Windows. Откройте их вручную: Параметры → Дисплей.", ToastKind.Warning);
                return;
            default:
                // Web pages and Store links: never with Lycoris's administrator token.
                if (!await UnelevatedShell.OpenAsync(target))
                    Ui.Toast(Loc.Tf("Не удалось открыть страницу без прав администратора. Адрес: {0}", target), ToastKind.Warning, Loc.T("ССЫЛКА НЕ ОТКРЫТА"));
                return;
        }
    }

    [RelayCommand]
    private void ClearHardwareSelection()
    {
        foreach (var a in AllHardwareActions()) a.IsSelected = false;
    }

    [RelayCommand]
    private void DismissHardwareResults()
    {
        HardwareResults.Clear();
        HwResultsSummary = null;
    }

    // ───────────────────────────── Install ─────────────────────────────

    // A replayed intent carries the checked keys (IntentList) and waits for the analysis; a click needs checked items.
    private bool CanInstallHardware(string? keys) =>
        !IsHwInstalling && !IsBusy && !IsInstalling && !IsBackupRunning && (keys is not null || (HwSelectedCount > 0 && !IsHardwareScanning));

    private string? SelectedHardwareKeys() => Startup.IntentList.Pack(SelectedHardwareActions().Select(a => a.Key));

    [RelayCommand(CanExecute = nameof(CanInstallHardware))]
    private async Task InstallHardwareAsync(string? keys)
    {
        if (!RequireAdmin(Loc.T("установка компонентов"), "InstallHardware", SelectedHardwareKeys())) return;
        if (IsBusy || IsHwInstalling) return;
        if (Startup.IntentList.Unpack(keys) is { Count: > 0 } replayed)
        {
            // After a relaunch the analysis starts from scratch: wait for it, then check exactly what was checked before.
            var sw = Stopwatch.StartNew();
            while ((!HardwareScanned || IsHardwareScanning) && sw.Elapsed < TimeSpan.FromSeconds(120) && !_lifetime.IsCancellationRequested)
                await Task.Delay(300);
            var set = replayed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var a in AllHardwareActions()) a.IsSelected = a.IsInstallable && set.Contains(a.Key);
            RecountHardwareSelection();
        }
        var selected = SelectedHardwareActions();
        if (selected.Count == 0)
        {
            Ui.Toast("Отметьте компоненты, которые нужно установить.", ToastKind.Warning);
            return;
        }

        var hasDrivers = selected.Any(a => a.Action.Kind == HwActionKind.DriverPackage);
        var details = selected.Select(DescribeForConfirm).ToList();
        details.Add(Loc.T("Сначала: точка восстановления системы"));
        if (hasDrivers)
        {
            details.Add(Loc.T("Перед драйвером видеокарты: копия её текущего драйвера (pnputil) в папку резервных копий Lycoris"));
            details.Add(Loc.T("Установщики драйверов открываются по одному — пройдите их шаги; Lycoris ждёт завершения каждого. Экран может мигнуть"));
        }
        if (selected.Any(a => a.Action.Kind == HwActionKind.Winget))
            details.Add(Loc.T("Программы через winget ставятся по очереди без вопросов (--silent), без --force и без перезагрузки"));
        details.Add(Loc.T("Компьютер не будет перезагружен автоматически"));

        var n = selected.Count;
        var confirmed = await Ui.ConfirmAsync(
            Loc.N(n, "УСТАНОВИТЬ {0} КОМПОНЕНТ?", "УСТАНОВИТЬ {0} КОМПОНЕНТА?", "УСТАНОВИТЬ {0} КОМПОНЕНТОВ?"),
            Loc.T("Всё загружается только из официальных источников и устанавливается по очереди. Нажимая «Установить», вы принимаете лицензии издателей этих программ и драйверов."),
            details,
            confirmText: Loc.T("УСТАНОВИТЬ"),
            footnote: Loc.T("Откат: точка восстановления; драйвер — «Откатить» в диспетчере устройств или резервная копия на вкладке «Резервная копия»; программы — «Параметры → Приложения»."));
        if (!confirmed) return;

        foreach (var a in AllHardwareActions()) a.IsLocked = true;
        foreach (var a in selected) a.SetJob(HwJobState.Queued, Loc.T("В ОЧЕРЕДИ"));
        HardwareResults.Clear();
        HwResultsSummary = null;

        var attempted = false;
        await RunBusyAsync(OperationClass.Install, Loc.T("Установка компонентов…"), async () =>
        {
            _hwInstallCts?.Dispose();
            _hwInstallCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var ct = _hwInstallCts.Token;
            IsHwInstalling = true;
            HwInstallPercent = 0;
            HwInstallIndeterminate = true;
            try
            {
                HwInstallStage = Loc.T("ТОЧКА ВОССТАНОВЛЕНИЯ");
                HwInstallDetail = Loc.T("Создание точки восстановления системы…");
                if (!await CreateHardwareRestorePointAsync())
                {
                    foreach (var a in selected) a.SetJob(HwJobState.Skipped, Loc.T("ОТМЕНЕНО: НЕТ ТОЧКИ ВОССТАНОВЛЕНИЯ"));
                    Log.Info("Установка компонентов отменена: нет точки восстановления.");
                    return;
                }

                attempted = true;
                var backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < selected.Count; i++)
                {
                    var item = selected[i];
                    if (ct.IsCancellationRequested)
                    {
                        item.SetJob(HwJobState.Skipped, Loc.T("ОТМЕНЕНО"));
                        HardwareResults.Add(new HwResultItem(item.Title, Loc.T("ОТМЕНЕНО"), false));
                        continue;
                    }
                    HwInstallStage = Loc.Tf("{0} ИЗ {1} · {2}", i + 1, selected.Count, item.Title.ToUpperInvariant());
                    HwInstallPercent = 0;
                    HwInstallIndeterminate = true;
                    await RunHardwareItemAsync(item, backedUp, ct);
                }
            }
            finally
            {
                IsHwInstalling = false;
                HwInstallIndeterminate = false;
                foreach (var a in AllHardwareActions()) a.IsLocked = false;
            }
        });

        var ok = HardwareResults.Count(r => r.IsOk);
        var failed = HardwareResults.Count - ok;
        HwResultsSummary = HardwareResults.Count == 0 ? null : failed == 0 ? Loc.Tf("Готово: {0} из {1}.", ok, HardwareResults.Count) : Loc.Tf("Успешно: {0}, с ошибками или отменено: {1}.", ok, failed);
        if (HwResultsSummary is not null)
            Ui.Toast(HwResultsSummary, failed == 0 ? ToastKind.Success : ToastKind.Warning, Loc.T("ПОДБОР ДЛЯ ПК"));

        if (!attempted)
        {
            if (_hwRebuildAfterInstall) RebuildHardware();
            _hwRebuildAfterInstall = false;
            return;
        }
        // Drivers and programs changed: re-read the driver list, then the hardware, and check the driver versions.
        _hwRebuildAfterInstall = false;
        await RunBusyAsync(OperationClass.Measure, Loc.T("Обновление сведений о драйверах…"), LoadInventoryAsync);
        await ScanHardwareAsync();
        VerifyDriverResults();
    }

    private static string DescribeForConfirm(HwActionItem item)
    {
        var a = item.Action;
        var optional = a.Optional ? Loc.T(" · по желанию") : "";
        return a.Kind switch
        {
            HwActionKind.DriverPackage when a.Package is { } p =>
                Loc.Tf("{0} — {1} · официальный сервер {2}{3}, подпись «{4}» проверяется, затем откроется установщик производителя", a.Title, a.Publisher, p.Url.Host, string.IsNullOrWhiteSpace(a.SizeText) ? "" : " (" + a.SizeText + ")", p.Signer),
            HwActionKind.Winget when a.Winget is { } w =>
                Loc.Tf("{0} — {1} · winget, пакет {2}{3}", a.Title, a.Publisher, w.Id, optional),
            _ => $"{a.Title} — {a.Publisher}",
        };
    }

    private async Task<bool> CreateHardwareRestorePointAsync()
    {
        var rp = await RestorePointService.CreateAsync(Loc.T("Lycoris: драйверы и компоненты для ПК"));
        HardwareResults.Add(new HwResultItem(Loc.T("Точка восстановления"), rp.Created ? Loc.T("СОЗДАНА") : Loc.T("НЕ СОЗДАНА"), rp.Created, rp.Message));
        if (rp.Created)
        {
            Ui.Toast(rp.Message, ToastKind.Success, Loc.T("ТОЧКА ВОССТАНОВЛЕНИЯ"));
            return true;
        }
        Log.Warn(rp.Message);
        return await Ui.ConfirmAsync(
            "ТОЧКА ВОССТАНОВЛЕНИЯ НЕ СОЗДАНА",
            Loc.Tf("{0} Без неё откатить драйвер можно будет только через диспетчер устройств или резервную копию драйверов, а программы — удалением. Продолжить?", rp.Message),
            confirmText: Loc.T("ПРОДОЛЖИТЬ"), cancelText: Loc.T("ОТМЕНИТЬ"), danger: true);
    }

    private async Task RunHardwareItemAsync(HwActionItem item, HashSet<string> backedUp, CancellationToken ct)
    {
        var a = item.Action;
        try
        {
            switch (a.Kind)
            {
                case HwActionKind.Winget when a.Winget is { } request:
                    await RunWingetItemAsync(item, request, ct);
                    break;
                case HwActionKind.DriverPackage when a.Package is { } package:
                    await RunDriverItemAsync(item, package, backedUp, ct);
                    break;
                default:
                    item.SetJob(HwJobState.Skipped, Loc.T("ПРОПУЩЕНО"));
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            item.SetJob(HwJobState.Skipped, Loc.T("ОТМЕНЕНО"));
            HardwareResults.Add(new HwResultItem(item.Title, Loc.T("ОТМЕНЕНО"), false));
        }
        catch (Exception ex)
        {
            Log.Error("Установка «{0}»", ex, item.Title);
            item.SetJob(HwJobState.Failed, Loc.Tf("ОШИБКА: {0}", ex.Message));
            HardwareResults.Add(new HwResultItem(item.Title, Loc.T("ОШИБКА"), false, ex.Message));
        }
    }

    private async Task RunWingetItemAsync(HwActionItem item, WingetPackageRequest request, CancellationToken ct)
    {
        item.SetJob(HwJobState.Running, Loc.T("УСТАНОВКА"));
        HwInstallDetail = $"winget install {request.Id}";
        Log.Info("Установка {0} ({1}, источник {2})", item.Title, request.Id, request.Source);
        if (ct.IsCancellationRequested)
        {
            item.SetJob(HwJobState.Skipped, Loc.T("ОТМЕНЕНО"));
            HardwareResults.Add(new HwResultItem(item.Title, Loc.T("ОТМЕНЕНО"), false));
            return;
        }
        // The token is only checked BETWEEN steps: cancelling a running winget would kill the installer it started and
        // leave a half-installed program (OperationCoordinator rule: never kill an installer).
        var r = await WingetClient.InstallPackageAsync(request, line => Ui.Post(() => HwInstallDetail = line), CancellationToken.None);
        if (r.Cancelled)
        {
            item.SetJob(HwJobState.Skipped, Loc.T("ОТМЕНЕНО"));
            HardwareResults.Add(new HwResultItem(item.Title, Loc.T("ОТМЕНЕНО"), false));
            return;
        }
        if (!r.Success)
        {
            item.SetJob(HwJobState.Failed, Loc.Tf("ОШИБКА: {0}", r.Message));
            HardwareResults.Add(new HwResultItem(item.Title, Loc.T("ОШИБКА"), false, r.Message));
            return;
        }
        // winget may match the MSIX "DirectXRuntime" framework: the classic DLLs decide (Apps research §4).
        if (string.Equals(request.Id, HardwareCatalog.DirectX, StringComparison.OrdinalIgnoreCase) &&
            !await Task.Run(InstalledDetector.HasLegacyDirectX))
        {
            var msg = Loc.T("winget сообщил об успехе, но файлов d3dx9_43.dll и xaudio2_7.dll нет — скачайте установщик на сайте Microsoft");
            item.SetJob(HwJobState.Failed, Loc.T("ОШИБКА: файлы DirectX не появились"));
            HardwareResults.Add(new HwResultItem(item.Title, Loc.T("ОШИБКА"), false, msg));
            Log.Warn("DirectX: " + msg);
            return;
        }
        var reboot = WingetClient.IsRebootRequired(r.ExitCode);
        item.SetJob(HwJobState.Done, reboot ? Loc.T("ГОТОВО · НУЖНА ПЕРЕЗАГРУЗКА") : Loc.T("ГОТОВО"));
        HardwareResults.Add(new HwResultItem(item.Title, reboot ? Loc.T("ГОТОВО, НУЖНА ПЕРЕЗАГРУЗКА") : Loc.T("ГОТОВО"), true, r.Message));
    }

    private async Task RunDriverItemAsync(HwActionItem item, DriverPackage package, HashSet<string> backedUp, CancellationToken ct)
    {
        // 1. Copy of the GPU's current vendor driver (non-fatal: the restore point is the main safety net).
        if (item.Action.GpuPnpId is { } pnp && backedUp.Add(pnp) &&
            _signedDrivers.FirstOrDefault(d => string.Equals(d.DeviceId, pnp, StringComparison.OrdinalIgnoreCase)) is { IsOem: true } current)
        {
            item.SetJob(HwJobState.Running, Loc.T("КОПИЯ ТЕКУЩЕГО ДРАЙВЕРА"));
            HwInstallDetail = Loc.Tf("Сохранение {0} ({1})…", current.InfName, current.Version);
            try
            {
                var (count, folder) = await DriverBackupService.ExportPackagesAsync([current.InfName], Loc.Tf("Перед установкой: {0}", item.Title), ct);
                if (folder is not null) Log.Success("Текущий драйвер видеокарты сохранён ({0}): {1}", count, folder);
                _ = ReloadBackupsAsync();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("Не удалось сохранить текущий драйвер видеокарты: {0}", ex.Message);
            }
        }

        // 2. Download from the vendor's server + Authenticode check (DriverDownloads).
        item.SetJob(HwJobState.Running, Loc.T("ЗАГРУЗКА 0%"));
        HwInstallIndeterminate = false;
        var progress = new Progress<DownloadProgress>(p =>
        {
            HwInstallDetail = DriverDownloads.FormatProgress(p);
            HwInstallIndeterminate = p.Total is not > 0;
            HwInstallPercent = p.Percent;
            item.JobText = p.Total is > 0 && p.Done >= p.Total
                ? Loc.T("ПРОВЕРКА ПОДПИСИ")
                : p.Total is > 0 ? Loc.Tf("ЗАГРУЗКА {0:0}%", p.Percent) : Loc.Tf("ЗАГРУЗКА · {0}", SystemProfile.FormatBytes(p.Done));
        });
        var path = await DriverDownloads.DownloadAsync(package, progress, ct);

        // 3. The vendor's own installer, interactively; wait until the user finishes it.
        item.SetJob(HwJobState.Running, Loc.T("УСТАНОВКА · ОКНО УСТАНОВЩИКА"));
        HwInstallIndeterminate = true;
        HwInstallDetail = Loc.T("Пройдите шаги установщика производителя. Lycoris ждёт его завершения.");
        var code = await VendorInstallerRunner.RunAndWaitAsync(path, ct);
        if (code is null)
        {
            item.SetJob(HwJobState.Skipped, Loc.T("ОЖИДАНИЕ ПРЕКРАЩЕНО"));
            HardwareResults.Add(new HwResultItem(item.Title, Loc.T("НЕ ДОЖДАЛИСЬ"), false, Loc.T("Установщик продолжает работу — дождитесь его окончания.")));
            return;
        }
        item.SetJob(HwJobState.Done, Loc.Tf("УСТАНОВЩИК ЗАВЕРШЁН · КОД {0}", code));
        HardwareResults.Add(new HwResultItem(item.Title, Loc.T("УСТАНОВЩИК ЗАВЕРШЁН"), true, Loc.Tf("Код завершения {0}. Версия драйвера проверяется повторным анализом.", code))
        {
            ExpectedVersion = package.Version,
            GpuPnpId = item.Action.GpuPnpId,
        });
    }

    /// <summary>After the rescan: did the vendor installer really change the driver version?</summary>
    private void VerifyDriverResults()
    {
        if (_hwSnapshot is null) return;
        foreach (var r in HardwareResults.Where(r => r.GpuPnpId is not null && r.ExpectedVersion is not null))
        {
            var gpu = _hwSnapshot.Gpus.FirstOrDefault(g => string.Equals(g.PnpDeviceId, r.GpuPnpId, StringComparison.OrdinalIgnoreCase));
            if (gpu is null) continue;
            _hwSnapshot.GpuRegistry.TryGetValue(gpu.PnpDeviceId, out var reg);
            var now = GpuDriverVersions.DisplayVersion(gpu.Vendor, gpu.DriverVersion, reg?.RadeonSoftwareVersion,
                gpu.Vendor == GpuVendor.Nvidia ? _hwSnapshot.NvidiaPackageVersion : null);
            var cmp = gpu.Vendor == GpuVendor.Nvidia
                ? GpuDriverVersions.CompareNvidia(now, r.ExpectedVersion)
                : HardwareRules.CompareVersions(now, r.ExpectedVersion);
            if (cmp >= 0)
            {
                r.ResultText = Loc.T("ДРАЙВЕР ОБНОВЛЁН");
                r.IsOk = true;
                r.Detail = Loc.Tf("Сейчас установлен {0}.", now);
            }
            else
            {
                r.ResultText = Loc.T("ВЕРСИЯ НЕ ИЗМЕНИЛАСЬ");
                r.IsOk = false;
                r.Detail = Loc.Tf("Сейчас {0}, ожидалась {1}: установку отменили, или нужна перезагрузка.", now, r.ExpectedVersion);
            }
        }
        var ok = HardwareResults.Count(r => r.IsOk);
        var failed = HardwareResults.Count - ok;
        if (HardwareResults.Count > 0)
            HwResultsSummary = failed == 0 ? Loc.Tf("Готово: {0} из {1}.", ok, HardwareResults.Count) : Loc.Tf("Успешно: {0}, с ошибками или отменено: {1}.", ok, failed);
    }

    [RelayCommand(CanExecute = nameof(IsHwInstalling))]
    private void CancelHardwareInstall()
    {
        if (_hwInstallCts is null || _hwInstallCts.IsCancellationRequested) return;
        HwInstallDetail = Loc.T("Отмена… Текущий шаг завершается; уже открытый установщик драйвера не прерывается.");
        _hwInstallCts.Cancel();
    }
}
