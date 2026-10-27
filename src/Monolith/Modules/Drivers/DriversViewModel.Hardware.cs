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
    [ObservableProperty] private string _hardwareScanCaption = "анализ ещё не выполнялся";
    [ObservableProperty] private bool _hardwareScanned;

    [ObservableProperty] private string _hwComponentsStat = "—";
    [ObservableProperty] private string _hwComponentsCaption = "идёт анализ";
    [ObservableProperty] private string _hwRecommendationsStat = "—";
    [ObservableProperty] private string _hwRecommendationsCaption = "идёт анализ";
    [ObservableProperty] private bool _hwHasRecommendations;
    [ObservableProperty] private string _hwCurrentStat = "—";
    [ObservableProperty] private string _hwCurrentCaption = "идёт анализ";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HwInstallButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallHardwareCommand))]
    private int _hwSelectedCount;

    [ObservableProperty] private string _hwSelectionText = "Отметьте компоненты, которые нужно установить.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallHardwareCommand), nameof(CancelHardwareInstallCommand), nameof(RescanHardwareCommand))]
    private bool _isHwInstalling;

    [ObservableProperty] private string _hwInstallStage = "";
    [ObservableProperty] private string _hwInstallDetail = "";
    [ObservableProperty] private double _hwInstallPercent;
    [ObservableProperty] private bool _hwInstallIndeterminate;
    [ObservableProperty] private string? _hwResultsSummary;

    public string HwInstallButtonText => HwSelectedCount > 0 ? $"УСТАНОВИТЬ ВЫБРАННОЕ · {HwSelectedCount}" : "УСТАНОВИТЬ ВЫБРАННОЕ";
    public bool HasHardwareResults => HardwareResults.Count > 0;

    /// <summary>Called from LoadAsync (fire-and-forget): scans the hardware and builds the recommendations.</summary>
    private partial Task StartHardwareScanAsync()
    {
        HookHardwareEvents();
        return ScanHardwareAsync();
    }

    private void HookHardwareEvents()
    {
        if (_hwHooked) return;
        _hwHooked = true;
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
        HardwareScanStage = "Сбор сведений о железе…";
        HardwareScanCaption = "идёт анализ";
        var sw = Stopwatch.StartNew();
        try
        {
            var profile = await SystemProfile.LoadAsync();
            IProgress<string> stage = new Progress<string>(t => HardwareScanStage = t);
            var snapshot = await Task.Run(() => HardwareScanner.Scan(profile, stage.Report), ct);

            HardwareScanStage = "Ожидание списка драйверов…";
            await WaitForInventoryAsync(ct);

            _hwSnapshot = snapshot;
            _hwOnline = OnlineInfo.NotStarted;
            RebuildHardware();
            HardwareScanned = true;
            HardwareScanTime = snapshot.When.ToString("HH:mm");

            HardwareScanStage = "Проверка версий у производителей…";
            _hwOnline = await CheckHardwareOnlineAsync(snapshot, ct);
            RebuildHardware();

            HardwareScanCaption = $"анализ занял {Math.Max(1, (int)Math.Round(sw.Elapsed.TotalSeconds))} с";
            var report = LastReport;
            Log.Info($"Подбор для ПК: компонентов {report?.ComponentCount ?? 0}, рекомендаций {report?.RecommendationCount ?? 0}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            HardwareScanCaption = "анализ прерван";
        }
        catch (Exception ex)
        {
            Log.Error("Анализ железа", ex);
            HardwareScanCaption = "анализ не выполнен";
            Ui.Toast($"Анализ железа не выполнен: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsHardwareScanning = false;
            HardwareScanStage = "";
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
        HwComponentsCaption = $"{report.Groups.Count} {DriverFormat.Plural(report.Groups.Count, "группа", "группы", "групп")} устройств и библиотек";

        var recommendations = report.RecommendationCount;
        var installable = AllHardwareActions().Count(a => a.IsInstallable && !a.IsOptional);
        HwRecommendationsStat = recommendations.ToString();
        HwHasRecommendations = recommendations > 0;
        HwRecommendationsCaption = recommendations == 0
            ? "действий не требуется"
            : installable > 0 ? $"установить здесь: {installable}" : "только вручную или через Windows Update";

        HwCurrentStat = report.CurrentCount.ToString();
        var rest = new List<string>();
        if (report.InstalledCount > 0) rest.Add($"установлено: {report.InstalledCount}");
        if (report.UncheckedCount > 0) rest.Add($"не проверено: {report.UncheckedCount}");
        HwCurrentCaption = rest.Count > 0 ? string.Join(" · ", rest) : "проверено у производителей";

        RecountHardwareSelection();
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
        HwSelectedCount = selected.Count;
        HwSelectionText = selected.Count == 0
            ? "Отметьте компоненты, которые нужно установить."
            : string.Join(", ", selected.Take(3).Select(a => a.Title)) + (selected.Count > 3 ? $" и ещё {selected.Count - 3}" : "");
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
            case HwActionKind.OpenSettings:
                if (!target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return;
                if (!await UnelevatedShell.StartAsUserAsync(target))
                    Ui.Toast("Не удалось открыть параметры Windows. Откройте их вручную: Параметры → Дисплей.", ToastKind.Warning);
                return;
            default:
                // Web pages and Store links: never with MONOLITH's administrator token.
                if (!await UnelevatedShell.OpenAsync(target))
                    Ui.Toast($"Не удалось открыть страницу без прав администратора. Адрес: {target}", ToastKind.Warning, "ССЫЛКА НЕ ОТКРЫТА");
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

    private bool CanInstallHardware() =>
        Elevation.IsElevated && HwSelectedCount > 0 && !IsHwInstalling && !IsHardwareScanning && !IsBusy && !IsInstalling && !IsBackupRunning;

    [RelayCommand(CanExecute = nameof(CanInstallHardware))]
    private async Task InstallHardwareAsync()
    {
        if (!RequireAdmin("установка компонентов")) return;
        if (IsBusy || IsHwInstalling) return;
        var selected = SelectedHardwareActions();
        if (selected.Count == 0)
        {
            Ui.Toast("Отметьте компоненты, которые нужно установить.", ToastKind.Warning);
            return;
        }

        var hasDrivers = selected.Any(a => a.Action.Kind == HwActionKind.DriverPackage);
        var details = selected.Select(DescribeForConfirm).ToList();
        details.Add("Сначала: точка восстановления системы");
        if (hasDrivers)
        {
            details.Add("Перед драйвером видеокарты: копия её текущего драйвера (pnputil) в папку резервных копий MONOLITH");
            details.Add("Установщики драйверов открываются по одному — пройдите их шаги; MONOLITH ждёт завершения каждого. Экран может мигнуть");
        }
        if (selected.Any(a => a.Action.Kind == HwActionKind.Winget))
            details.Add("Программы через winget ставятся по очереди без вопросов (--silent), без --force и без перезагрузки");
        details.Add("Компьютер не будет перезагружен автоматически");

        var n = selected.Count;
        var confirmed = await Ui.ConfirmAsync(
            $"УСТАНОВИТЬ {n} {DriverFormat.Plural(n, "КОМПОНЕНТ", "КОМПОНЕНТА", "КОМПОНЕНТОВ")}?",
            "Всё загружается только из официальных источников и устанавливается по очереди. Нажимая «Установить», вы принимаете лицензии издателей этих программ и драйверов.",
            details,
            confirmText: "УСТАНОВИТЬ",
            footnote: "Откат: точка восстановления; драйвер — «Откатить» в диспетчере устройств или резервная копия на вкладке «Резервная копия»; программы — «Параметры → Приложения».");
        if (!confirmed) return;

        foreach (var a in AllHardwareActions()) a.IsLocked = true;
        foreach (var a in selected) a.SetJob(HwJobState.Queued, "В ОЧЕРЕДИ");
        HardwareResults.Clear();
        HwResultsSummary = null;

        var attempted = false;
        await RunBusyAsync("Установка компонентов…", async () =>
        {
            _hwInstallCts?.Dispose();
            _hwInstallCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var ct = _hwInstallCts.Token;
            IsHwInstalling = true;
            HwInstallPercent = 0;
            HwInstallIndeterminate = true;
            try
            {
                HwInstallStage = "ТОЧКА ВОССТАНОВЛЕНИЯ";
                HwInstallDetail = "Создание точки восстановления системы…";
                if (!await CreateHardwareRestorePointAsync())
                {
                    foreach (var a in selected) a.SetJob(HwJobState.Skipped, "ОТМЕНЕНО: НЕТ ТОЧКИ ВОССТАНОВЛЕНИЯ");
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
                        item.SetJob(HwJobState.Skipped, "ОТМЕНЕНО");
                        HardwareResults.Add(new HwResultItem(item.Title, "ОТМЕНЕНО", false));
                        continue;
                    }
                    HwInstallStage = $"{i + 1} ИЗ {selected.Count} · {item.Title.ToUpperInvariant()}";
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
        HwResultsSummary = HardwareResults.Count == 0 ? null : failed == 0 ? $"Готово: {ok} из {HardwareResults.Count}." : $"Успешно: {ok}, с ошибками или отменено: {failed}.";
        if (HwResultsSummary is not null)
            Ui.Toast(HwResultsSummary, failed == 0 ? ToastKind.Success : ToastKind.Warning, "ПОДБОР ДЛЯ ПК");

        if (!attempted)
        {
            if (_hwRebuildAfterInstall) RebuildHardware();
            _hwRebuildAfterInstall = false;
            return;
        }
        // Drivers and programs changed: re-read the driver list, then the hardware, and check the driver versions.
        _hwRebuildAfterInstall = false;
        await RunBusyAsync("Обновление сведений о драйверах…", LoadInventoryAsync);
        await ScanHardwareAsync();
        VerifyDriverResults();
    }

    private static string DescribeForConfirm(HwActionItem item)
    {
        var a = item.Action;
        var optional = a.Optional ? " · по желанию" : "";
        return a.Kind switch
        {
            HwActionKind.DriverPackage when a.Package is { } p =>
                $"{a.Title} — {a.Publisher} · официальный сервер {p.Url.Host}{(string.IsNullOrWhiteSpace(a.SizeText) ? "" : $" ({a.SizeText})")}, " +
                $"подпись «{p.Signer}» проверяется, затем откроется установщик производителя",
            HwActionKind.Winget when a.Winget is { } w =>
                $"{a.Title} — {a.Publisher} · winget, пакет {w.Id}{optional}",
            _ => $"{a.Title} — {a.Publisher}",
        };
    }

    private async Task<bool> CreateHardwareRestorePointAsync()
    {
        var rp = await RestorePointService.CreateAsync("MONOLITH: драйверы и компоненты для ПК");
        HardwareResults.Add(new HwResultItem("Точка восстановления", rp.Created ? "СОЗДАНА" : "НЕ СОЗДАНА", rp.Created, rp.Message));
        if (rp.Created)
        {
            Ui.Toast(rp.Message, ToastKind.Success, "ТОЧКА ВОССТАНОВЛЕНИЯ");
            return true;
        }
        Log.Warn(rp.Message);
        return await Ui.ConfirmAsync(
            "ТОЧКА ВОССТАНОВЛЕНИЯ НЕ СОЗДАНА",
            $"{rp.Message} Без неё откатить драйвер можно будет только через диспетчер устройств или резервную копию драйверов, а программы — удалением. Продолжить?",
            confirmText: "ПРОДОЛЖИТЬ", cancelText: "ОТМЕНИТЬ", danger: true);
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
                    item.SetJob(HwJobState.Skipped, "ПРОПУЩЕНО");
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            item.SetJob(HwJobState.Skipped, "ОТМЕНЕНО");
            HardwareResults.Add(new HwResultItem(item.Title, "ОТМЕНЕНО", false));
        }
        catch (Exception ex)
        {
            Log.Error($"Установка «{item.Title}»", ex);
            item.SetJob(HwJobState.Failed, "ОШИБКА: " + ex.Message);
            HardwareResults.Add(new HwResultItem(item.Title, "ОШИБКА", false, ex.Message));
        }
    }

    private async Task RunWingetItemAsync(HwActionItem item, WingetPackageRequest request, CancellationToken ct)
    {
        item.SetJob(HwJobState.Running, "УСТАНОВКА");
        HwInstallDetail = $"winget install {request.Id}";
        Log.Info($"Установка {item.Title} ({request.Id}, источник {request.Source})");
        var r = await WingetClient.InstallPackageAsync(request, line => Ui.Post(() => HwInstallDetail = line), ct);
        if (r.Cancelled)
        {
            item.SetJob(HwJobState.Skipped, "ОТМЕНЕНО");
            HardwareResults.Add(new HwResultItem(item.Title, "ОТМЕНЕНО", false));
            return;
        }
        if (!r.Success)
        {
            item.SetJob(HwJobState.Failed, "ОШИБКА: " + r.Message);
            HardwareResults.Add(new HwResultItem(item.Title, "ОШИБКА", false, r.Message));
            return;
        }
        // winget may match the MSIX "DirectXRuntime" framework: the classic DLLs decide (Apps research §4).
        if (string.Equals(request.Id, HardwareCatalog.DirectX, StringComparison.OrdinalIgnoreCase) &&
            !await Task.Run(InstalledDetector.HasLegacyDirectX))
        {
            const string msg = "winget сообщил об успехе, но файлов d3dx9_43.dll и xaudio2_7.dll нет — скачайте установщик на сайте Microsoft";
            item.SetJob(HwJobState.Failed, "ОШИБКА: файлы DirectX не появились");
            HardwareResults.Add(new HwResultItem(item.Title, "ОШИБКА", false, msg));
            Log.Warn("DirectX: " + msg);
            return;
        }
        var reboot = WingetClient.IsRebootRequired(r.ExitCode);
        item.SetJob(HwJobState.Done, reboot ? "ГОТОВО · НУЖНА ПЕРЕЗАГРУЗКА" : "ГОТОВО");
        HardwareResults.Add(new HwResultItem(item.Title, reboot ? "ГОТОВО, НУЖНА ПЕРЕЗАГРУЗКА" : "ГОТОВО", true, r.Message));
    }

    private async Task RunDriverItemAsync(HwActionItem item, DriverPackage package, HashSet<string> backedUp, CancellationToken ct)
    {
        // 1. Copy of the GPU's current vendor driver (non-fatal: the restore point is the main safety net).
        if (item.Action.GpuPnpId is { } pnp && backedUp.Add(pnp) &&
            _signedDrivers.FirstOrDefault(d => string.Equals(d.DeviceId, pnp, StringComparison.OrdinalIgnoreCase)) is { IsOem: true } current)
        {
            item.SetJob(HwJobState.Running, "КОПИЯ ТЕКУЩЕГО ДРАЙВЕРА");
            HwInstallDetail = $"Сохранение {current.InfName} ({current.Version})…";
            try
            {
                var (count, folder) = await DriverBackupService.ExportPackagesAsync([current.InfName], $"Перед установкой: {item.Title}", ct);
                if (folder is not null) Log.Success($"Текущий драйвер видеокарты сохранён ({count}): {folder}");
                _ = ReloadBackupsAsync();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось сохранить текущий драйвер видеокарты: {ex.Message}");
            }
        }

        // 2. Download from the vendor's server + Authenticode check (DriverDownloads).
        item.SetJob(HwJobState.Running, "ЗАГРУЗКА 0%");
        HwInstallIndeterminate = false;
        var progress = new Progress<DownloadProgress>(p =>
        {
            HwInstallDetail = DriverDownloads.FormatProgress(p);
            HwInstallIndeterminate = p.Total is not > 0;
            HwInstallPercent = p.Percent;
            item.JobText = p.Total is > 0 && p.Done >= p.Total
                ? "ПРОВЕРКА ПОДПИСИ"
                : p.Total is > 0 ? $"ЗАГРУЗКА {p.Percent:0}%" : $"ЗАГРУЗКА · {SystemProfile.FormatBytes(p.Done)}";
        });
        var path = await DriverDownloads.DownloadAsync(package, progress, ct);

        // 3. The vendor's own installer, interactively; wait until the user finishes it.
        item.SetJob(HwJobState.Running, "УСТАНОВКА · ОКНО УСТАНОВЩИКА");
        HwInstallIndeterminate = true;
        HwInstallDetail = "Пройдите шаги установщика производителя. MONOLITH ждёт его завершения.";
        var code = await VendorInstallerRunner.RunAndWaitAsync(path, ct);
        if (code is null)
        {
            item.SetJob(HwJobState.Skipped, "ОЖИДАНИЕ ПРЕКРАЩЕНО");
            HardwareResults.Add(new HwResultItem(item.Title, "НЕ ДОЖДАЛИСЬ", false, "Установщик продолжает работу — дождитесь его окончания."));
            return;
        }
        item.SetJob(HwJobState.Done, $"УСТАНОВЩИК ЗАВЕРШЁН · КОД {code}");
        HardwareResults.Add(new HwResultItem(item.Title, "УСТАНОВЩИК ЗАВЕРШЁН", true, $"Код завершения {code}. Версия драйвера проверяется повторным анализом.")
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
                r.ResultText = "ДРАЙВЕР ОБНОВЛЁН";
                r.IsOk = true;
                r.Detail = $"Сейчас установлен {now}.";
            }
            else
            {
                r.ResultText = "ВЕРСИЯ НЕ ИЗМЕНИЛАСЬ";
                r.IsOk = false;
                r.Detail = $"Сейчас {now}, ожидалась {r.ExpectedVersion}: установку отменили, или нужна перезагрузка.";
            }
        }
        var ok = HardwareResults.Count(r => r.IsOk);
        var failed = HardwareResults.Count - ok;
        if (HardwareResults.Count > 0)
            HwResultsSummary = failed == 0 ? $"Готово: {ok} из {HardwareResults.Count}." : $"Успешно: {ok}, с ошибками или отменено: {failed}.";
    }

    [RelayCommand(CanExecute = nameof(IsHwInstalling))]
    private void CancelHardwareInstall()
    {
        if (_hwInstallCts is null || _hwInstallCts.IsCancellationRequested) return;
        HwInstallDetail = "Отмена… Текущий шаг завершается; уже открытый установщик драйвера не прерывается.";
        _hwInstallCts.Cancel();
    }
}
