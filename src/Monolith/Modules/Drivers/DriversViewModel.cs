using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Drivers;

/// <summary>
/// ДРАЙВЕРЫ: official, safe driver management — Windows Update (WUA), vendor tools (winget), vendor sites,
/// problem devices, installed drivers and pnputil backups. No third-party driver databases (research §6).
/// </summary>
public sealed partial class DriversViewModel : PageViewModel
{
    public override string Key => "drivers";
    public override string Index => "06";
    public override string Title => "ДРАЙВЕРЫ";
    public override string Subtitle => "Устройства, обновления драйверов через Windows Update и официальные утилиты производителей.";
    public override string Glyph => Glyphs.Devices;

    private const string Source = "drivers";

    private List<SignedDriverInfo> _signedDrivers = [];
    private List<UninstallEntry> _apps = [];
    private readonly CancellationTokenSource _lifetime = new();

    [ObservableProperty] private DriversTab _selectedTab = DriversTab.Overview;
    [ObservableProperty] private bool _inventoryLoaded;
    [ObservableProperty] private string _inventoryTime = "";

    // ── Overview stats ──
    [ObservableProperty] private string _driversStat = "—";
    [ObservableProperty] private string _driversCaption = "загрузка…";
    [ObservableProperty] private string _problemsStat = "—";
    [ObservableProperty] private string _problemsCaption = "загрузка…";
    [ObservableProperty] private string _updatesStat = "—";
    [ObservableProperty] private string _updatesCaption = "ещё не проверялось";
    [ObservableProperty] private string _gpuStat = "—";
    [ObservableProperty] private string _gpuCaption = "загрузка…";
    [ObservableProperty] private string _backupsStat = "—";
    [ObservableProperty] private string _backupsCaption = "загрузка…";

    // ── Tab badges ──
    [ObservableProperty] private string _updatesBadge = "";
    [ObservableProperty] private string _problemsBadge = "";
    [ObservableProperty] private string _installedBadge = "";
    [ObservableProperty] private string _backupsBadge = "";

    // ── GPU / chipset ──
    public ObservableCollection<GpuCardItem> Gpus { get; } = [];
    public ObservableCollection<ChipsetComponentItem> ChipsetComponents { get; } = [];
    [ObservableProperty] private string _chipsetCpu = "";
    [ObservableProperty] private string _chipsetVerdict = "";
    [ObservableProperty] private string _chipsetState = "unknown";
    [ObservableProperty] private string _chipsetRecommendation = "";
    [ObservableProperty] private string? _chipsetNote;
    [ObservableProperty] private string? _chipsetPackage;
    [ObservableProperty] private bool _chipsetIsAmd;
    [ObservableProperty] private bool _chipsetIsIntel;
    [ObservableProperty] private string _intelDsaButtonText = "УСТАНОВИТЬ INTEL DSA";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IntelDsaEnabled))]
    private bool _intelDsaInstalled;
    [ObservableProperty] private bool _gpusEmpty;

    /// <summary>Two GPU cards side by side at most.</summary>
    public int GpuColumns => Math.Clamp(Gpus.Count, 1, 2);
    public bool IntelDsaEnabled => IntelDsaInstalled || Elevation.IsElevated;

    public string AmdDriversUrl => DriverSources.AmdDriversPage;
    public string IntelSupportUrl => DriverSources.IntelDetectPage;
    public string DduUrl => DriverSources.DduPage;

    /// <summary>Safe clean-install order for DDU (research §6) — information only, MONOLITH never runs it.</summary>
    public IReadOnlyList<StepItem> DduSteps { get; } =
    [
        new("01", "Создайте точку восстановления системы."),
        new("02", "Заранее скачайте новый драйвер с сайта производителя."),
        new("03", "Отключите интернет, чтобы Windows Update не поставил свой драйвер."),
        new("04", "Загрузитесь в безопасном режиме и запустите DDU."),
        new("05", "Установите скачанный драйвер и снова подключите интернет."),
    ];

    public DriversViewModel()
    {
        Updates.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasUpdates));
        Gpus.CollectionChanged += (_, _) => OnPropertyChanged(nameof(GpuColumns));
    }

    protected override async Task LoadAsync()
    {
        // Windows Update search runs on its own thread and does not block the rest of the page.
        _ = SearchUpdatesAsync();
        await RunBusyAsync("Сбор сведений о драйверах…", LoadInventoryAsync);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy)) RefreshCommandStates();
    }

    partial void OnSelectedTabChanged(DriversTab value)
    {
        if (value == DriversTab.Updates && UpdatesState == UpdatesState.NotSearched)
            _ = SearchUpdatesAsync();
    }

    private void RefreshCommandStates()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        SearchUpdatesCommand.NotifyCanExecuteChanged();
        InstallUpdatesCommand.NotifyCanExecuteChanged();
        EnableDeviceCommand.NotifyCanExecuteChanged();
        ExportBackupCommand.NotifyCanExecuteChanged();
        RestoreBackupCommand.NotifyCanExecuteChanged();
        DeleteBackupCommand.NotifyCanExecuteChanged();
    }

    private bool CanModify() => Elevation.IsElevated && !IsBusy && !IsInstalling && !IsBackupRunning;

    // ───────────────────────────── Inventory ─────────────────────────────

    private sealed record InventoryData(
        List<SignedDriverInfo> Drivers,
        string? DriversError,
        List<ProblemDeviceInfo> Problems,
        string? ProblemsError,
        List<UninstallEntry> Apps,
        WuPolicyInfo Policy,
        List<DriverBackupInfo> Backups,
        int OemPackages,
        string? NvidiaPackage,
        List<(GpuInfo Gpu, GpuRegistryInfo? Reg)> GpuRegs);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RunBusyAsync("Обновление сведений о драйверах…", LoadInventoryAsync);

    private bool CanRefresh() => !IsBusy && !IsInstalling && !IsBackupRunning;

    private async Task LoadInventoryAsync()
    {
        var profile = await SystemProfile.LoadAsync();
        var data = await Task.Run(() => Collect(profile));
        ApplyInventory(profile, data);
        InventoryLoaded = true;
        InventoryTime = $"ДАННЫЕ ОТ {DateTime.Now:HH:mm}";
        _ = CheckNvidiaLatestAsync();
    }

    private static InventoryData Collect(SystemProfile profile)
    {
        List<SignedDriverInfo> drivers = [];
        string? driversError = null;
        try { drivers = DriverInventory.LoadSignedDrivers(); }
        catch (Exception ex)
        {
            driversError = "Не удалось получить список драйверов (WMI): " + ex.Message;
            Log.Warn(driversError);
        }

        List<ProblemDeviceInfo> problems = [];
        string? problemsError = null;
        try { problems = DriverInventory.LoadProblemDevices(); }
        catch (Exception ex)
        {
            problemsError = "Не удалось проверить устройства (WMI): " + ex.Message;
            Log.Warn(problemsError);
        }

        List<UninstallEntry> apps = [];
        try { apps = DriverInventory.ReadUninstallEntries(); } catch { }

        List<DriverBackupInfo> backups = [];
        try { backups = DriverBackupService.List(); }
        catch (Exception ex) { Log.Warn($"Резервные копии драйверов: {ex.Message}"); }

        var regs = profile.Gpus.Select(g => (g, DriverInventory.ReadGpuRegistry(g))).ToList();
        return new InventoryData(drivers, driversError, problems, problemsError, apps, DriverInventory.ReadWuPolicy(),
            backups, DriverInventory.CountOemPackages(), DriverInventory.ReadNvidiaPackageVersion(), regs);
    }

    private void ApplyInventory(SystemProfile profile, InventoryData data)
    {
        _signedDrivers = data.Drivers;
        _apps = data.Apps;
        var now = DateTime.Now;

        // GPUs: primary first.
        Gpus.Clear();
        var primary = profile.PrimaryGpu;
        foreach (var (gpu, reg) in data.GpuRegs.OrderByDescending(x => ReferenceEquals(x.Gpu, primary)))
            Gpus.Add(new GpuCardItem(gpu, reg, ReferenceEquals(gpu, primary), data.NvidiaPackage, data.Apps, now));
        GpusEmpty = Gpus.Count == 0;

        // Chipset.
        var reportable = data.Problems.Where(p => DeviceProblemCodes.IsReportable(p.Code, p.Present)).ToList();
        var amdPackage = data.Apps.FirstOrDefault(a => a.DisplayName.StartsWith("AMD Chipset Software", StringComparison.OrdinalIgnoreCase))?.Version;
        var chipset = ChipsetAdvisor.Analyze(profile.CpuName, profile.CpuIsAmd, profile.CpuIsIntel, data.Drivers, reportable, amdPackage);
        ChipsetCpu = string.IsNullOrWhiteSpace(profile.CpuName) ? "Процессор не определён" : profile.CpuName;
        ChipsetVerdict = chipset.Verdict;
        ChipsetState = chipset.DriverOk switch { true => "ok", false => "bad", _ => "unknown" };
        ChipsetRecommendation = chipset.Recommendation;
        ChipsetNote = chipset.Note;
        ChipsetPackage = chipset.PackageText;
        ChipsetIsAmd = chipset.Platform == ChipsetPlatform.Amd;
        ChipsetIsIntel = chipset.Platform == ChipsetPlatform.Intel;
        var vendorLabel = ChipsetIsIntel ? "INTEL" : "AMD";
        ChipsetComponents.Clear();
        foreach (var c in chipset.Components) ChipsetComponents.Add(new ChipsetComponentItem(c, vendorLabel));
        UpdateIntelDsaState();

        ApplyProblems(reportable, data.ProblemsError);
        ApplyInstalled(data.Drivers, data.DriversError);
        ApplyBackups(data.Backups, data.OemPackages);
        ApplyPolicy(data.Policy);
        UpdateStats();
    }

    private async Task CheckNvidiaLatestAsync()
    {
        foreach (var card in Gpus.Where(c => c.SupportsLatestLookup).ToList())
        {
            card.IsCheckingLatest = true;
            NvidiaRelease? release = null;
            try { release = await NvidiaLookup.GetLatestAsync(card.Name, _lifetime.Token); }
            catch (OperationCanceledException) { }
            card.ApplyLatest(release, DateTime.Now);
        }
        UpdateStats();
    }

    private void UpdateStats()
    {
        // Drivers
        if (InventoryLoaded || _signedDrivers.Count > 0)
        {
            var oem = _signedDrivers.Count(d => !d.IsMicrosoft);
            DriversStat = _signedDrivers.Count.ToString();
            DriversCaption = $"от производителей: {oem}";
            InstalledBadge = _signedDrivers.Count.ToString();

            ProblemsStat = Problems.Count.ToString();
            ProblemsCaption = Problems.Count == 0 ? "все устройства работают" : $"{DriverFormat.Plural(Problems.Count, "требует", "требуют", "требуют")} внимания";
            ProblemsBadge = Problems.Count > 0 ? Problems.Count.ToString() : "";

            var main = Gpus.FirstOrDefault();
            if (main is null)
            {
                GpuStat = "—";
                GpuCaption = "видеоадаптер не найден";
            }
            else
            {
                GpuStat = main.VersionText;
                GpuCaption = main.UpdateAvailable ? $"{main.VendorText} · доступен {main.LatestVersion}" : $"{main.VendorText} · возраст {main.AgeText}";
            }

            BackupsStat = Backups.Count.ToString();
            BackupsCaption = Backups.Count == 0 ? "ещё не создавались" : $"последняя {Backups[0].DateText}";
            BackupsBadge = Backups.Count > 0 ? Backups.Count.ToString() : "";
        }

        // Windows Update
        switch (UpdatesState)
        {
            case UpdatesState.Searching:
                UpdatesStat = "…";
                UpdatesCaption = "поиск в Windows Update";
                UpdatesBadge = "…";
                break;
            case UpdatesState.Error:
                UpdatesStat = "—";
                UpdatesCaption = _lastSearchHResult == WuText.ServiceDisabled ? "служба отключена"
                    : WuText.IsNetworkError(_lastSearchHResult) ? "нет связи" : "ошибка поиска";
                UpdatesBadge = "!";
                break;
            case UpdatesState.Ready:
                var recommended = Updates.Count(u => !u.IsOptional && !u.IsFirmware);
                var optional = Updates.Count - recommended;
                UpdatesStat = Updates.Count.ToString();
                UpdatesCaption = Updates.Count == 0 ? "новых драйверов нет"
                    : optional == 0 ? $"рекомендуемых: {recommended}"
                    : recommended == 0 ? $"необязательных: {optional}"
                    : $"{recommended} рекомендуемых, {optional} необяз.";
                UpdatesBadge = Updates.Count > 0 ? Updates.Count.ToString() : "";
                break;
            default:
                UpdatesStat = "—";
                UpdatesCaption = "ещё не проверялось";
                UpdatesBadge = "";
                break;
        }
    }

    // ───────────────────────────── Navigation & shell helpers ─────────────────────────────

    [RelayCommand]
    private void GoToTab(string? tab)
    {
        if (Enum.TryParse<DriversTab>(tab, true, out var t)) SelectedTab = t;
    }

    [RelayCommand]
    private void OpenDeviceManager() => ShellOpen(ProcessRunner.Sys("devmgmt.msc"), "диспетчер устройств");

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (!DriverSources.IsAllowedUrl(url))
        {
            Log.Warn($"Ссылка не открыта (не официальный источник): {url}");
            return;
        }
        ShellOpen(url!, url!);
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (Elevation.TryRelaunchElevated(["--page", Key]))
            Application.Current.Shutdown();
    }

    private static void ShellOpen(string target, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось открыть {what}", ex);
            Ui.Toast($"Не удалось открыть {what}: {ex.Message}", ToastKind.Error);
        }
    }

    // ───────────────────────────── Vendor tools ─────────────────────────────

    [RelayCommand]
    private async Task UseGpuToolAsync(GpuCardItem? card)
    {
        if (card?.Tool is not { } tool) return;
        if (tool.WingetId is null)
        {
            OpenUrl(tool.SiteUrl);
            return;
        }
        if (card.ToolInstalled)
        {
            if (card.ToolExePath is not null) ShellOpen(card.ToolExePath, tool.Name);
            else OpenUrl(tool.SiteUrl);
            return;
        }
        if (await InstallToolAsync(tool))
        {
            card.MarkToolInstalled(_apps);
            UpdateIntelDsaState();
        }
    }

    [RelayCommand]
    private async Task UseIntelDsaAsync()
    {
        if (IntelDsaInstalled)
        {
            OpenUrl(DriverSources.IntelDsa.SiteUrl);
            return;
        }
        if (await InstallToolAsync(DriverSources.IntelDsa))
        {
            UpdateIntelDsaState();
            foreach (var card in Gpus.Where(g => g.Vendor == GpuVendor.Intel)) card.MarkToolInstalled(_apps);
        }
    }

    private void UpdateIntelDsaState()
    {
        IntelDsaInstalled = _apps.Any(a => a.DisplayName.Contains("Driver & Support Assistant", StringComparison.OrdinalIgnoreCase));
        IntelDsaButtonText = IntelDsaInstalled ? "ОТКРЫТЬ INTEL DSA" : "УСТАНОВИТЬ INTEL DSA";
    }

    /// <summary>Installs an official vendor utility with winget after a confirmation that names publisher and source.</summary>
    private async Task<bool> InstallToolAsync(VendorTool tool)
    {
        if (tool.WingetId is not { } id || DriverSources.IsBlocked(id)) return false;
        if (!RequireAdmin($"установка {tool.Name}")) return false;
        if (IsBusy) return false;

        var store = tool.WingetSource == "msstore";
        var confirmed = await Ui.ConfirmAsync(
            $"УСТАНОВИТЬ {tool.Name.ToUpperInvariant()}?",
            tool.Description,
            [
                $"Программа: {tool.Name}",
                $"Издатель: {tool.Publisher}",
                $"Источник: {(store ? "Microsoft Store через winget (msstore)" : "официальный репозиторий winget")}",
                $"Пакет: {id}",
                "Будет выполнено: winget install --id … --exact — без --force и без перезагрузки",
                store
                    ? "Нажимая «Установить», вы принимаете лицензию издателя и условия Microsoft Store"
                    : "Нажимая «Установить», вы принимаете лицензию издателя",
            ],
            confirmText: "УСТАНОВИТЬ",
            footnote: "Удалить программу можно в «Параметры → Приложения».");
        if (!confirmed) return false;

        var success = false;
        await RunBusyAsync($"Установка {tool.Name}…", async () =>
        {
            Log.Info($"Установка {tool.Name} ({id}, источник {tool.WingetSource})");
            var r = await WingetClient.InstallAsync(id, tool.WingetSource, line => Log.Info("winget: " + line));
            if (r.Success)
            {
                success = true;
                Ui.Toast($"{tool.Name}: {r.Message}.", ToastKind.Success);
            }
            else
            {
                Ui.Toast($"{tool.Name}: {r.Message}.", ToastKind.Error, "УСТАНОВКА НЕ УДАЛАСЬ");
            }
            _apps = await Task.Run(DriverInventory.ReadUninstallEntries);
        });
        return success;
    }

    /// <summary>Creates a restore point; when Windows does not create one, the user decides whether to continue.</summary>
    private static async Task<bool> EnsureRestorePointAsync(string description)
    {
        var rp = await RestorePointService.CreateAsync(description);
        if (rp.Created)
        {
            Ui.Toast(rp.Message, ToastKind.Success, "ТОЧКА ВОССТАНОВЛЕНИЯ");
            return true;
        }
        Log.Warn(rp.Message);
        return await Ui.ConfirmAsync(
            "ТОЧКА ВОССТАНОВЛЕНИЯ НЕ СОЗДАНА",
            $"{rp.Message} Без неё откатить драйверы можно будет только через диспетчер устройств или резервную копию драйверов. Продолжить?",
            confirmText: "ПРОДОЛЖИТЬ", cancelText: "ОТМЕНИТЬ", danger: true);
    }
}
