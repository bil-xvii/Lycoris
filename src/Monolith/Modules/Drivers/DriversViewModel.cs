using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    public override string Index => "05";
    public override string Title => Loc.T("ДРАЙВЕРЫ");
    public override string Subtitle => Loc.T("Драйверы и утилиты производителей — только из официальных источников.");
    public override string Glyph => Glyphs.Devices;

    private const string Source = "drivers";

    private List<SignedDriverInfo> _signedDrivers = [];
    private List<UninstallEntry> _apps = [];
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// The shown tab. The page has FOUR tabs — ПОДБОР (Hardware + Overview: the GPU cards are pinned first), ОБНОВЛЕНИЯ WINDOWS,
    /// ПРОБЛЕМНЫЕ УСТРОЙСТВА, УСТАНОВЛЕННЫЕ (Installed + Backup). The old Overview / Backup values still work as links and
    /// select the merged tab.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TabIsHardware), nameof(TabIsUpdates), nameof(TabIsProblems), nameof(TabIsInstalled), nameof(HasPrimary))]
    private DriversTab _selectedTab = DriversTab.Hardware;

    public bool TabIsHardware { get => SelectedTab is DriversTab.Hardware or DriversTab.Overview; set { if (value) SelectedTab = DriversTab.Hardware; } }
    public bool TabIsUpdates { get => SelectedTab == DriversTab.Updates; set { if (value) SelectedTab = DriversTab.Updates; } }
    public bool TabIsProblems { get => SelectedTab == DriversTab.Problems; set { if (value) SelectedTab = DriversTab.Problems; } }
    public bool TabIsInstalled { get => SelectedTab is DriversTab.Installed or DriversTab.Backup; set { if (value) SelectedTab = DriversTab.Installed; } }
    [ObservableProperty] private bool _inventoryLoaded;
    [ObservableProperty] private string _inventoryTime = "";

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
    [ObservableProperty] private string _intelDsaButtonText = Loc.T("УСТАНОВИТЬ INTEL DSA");
    [ObservableProperty] private bool _intelDsaInstalled;
    [ObservableProperty] private bool _gpusEmpty;

    /// <summary>Two GPU cards side by side at most.</summary>
    public int GpuColumns => Math.Clamp(Gpus.Count, 1, 2);

    public string AmdDriversUrl => DriverSources.AmdDriversPage;
    public string IntelSupportUrl => DriverSources.IntelDetectPage;
    public string DduUrl => DriverSources.DduPage;

    public DriversViewModel()
    {
        Updates.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasUpdates));
        Gpus.CollectionChanged += (_, _) => OnPropertyChanged(nameof(GpuColumns));
    }

    protected override async Task LoadAsync()
    {
        // The Windows Update search (and the hardware-analysis hook, hidden tab) run on their own and do not block the page.
        _ = StartHardwareScanAsync();
        _ = SearchUpdatesAsync();
        await RunBusyAsync(OperationClass.Measure, Loc.T("Сбор сведений о драйверах…"), LoadInventoryAsync);
    }

    /// <summary>Implemented in DriversViewModel.Hardware.cs.</summary>
    private partial Task StartHardwareScanAsync();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsBusy) or nameof(IsInstalling) or nameof(IsBackupRunning) or nameof(IsDriverDownloadRunning))
            RefreshCommandStates();
        if (e.PropertyName is nameof(IsInstalling) or nameof(IsHwInstalling)) RaisePrimary();
    }

    partial void OnSelectedTabChanged(DriversTab value)
    {
        OnPropertyChanged(nameof(PrimaryText));
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
        DownloadDriverCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// One system-changing operation at a time: WU install, backup/restore, direct driver download. Read-only mode does NOT
    /// disable the buttons — a click calls <c>RequireAdmin(action, command, parameter)</c> and the relaunched copy repeats it.
    /// </summary>
    private bool CanModify() => !IsBusy && !IsInstalling && !IsBackupRunning && !IsDriverDownloadRunning;

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
    private Task RefreshAsync() => RunBusyAsync(OperationClass.Measure, Loc.T("Обновление сведений о драйверах…"), LoadInventoryAsync);

    /// <summary>A refresh rebuilds the GPU cards, so it waits while a card is downloading a driver.</summary>
    private bool CanRefresh() => !IsBusy && !IsInstalling && !IsBackupRunning && !IsDriverDownloadRunning;

    private async Task LoadInventoryAsync()
    {
        var profile = await SystemProfile.LoadAsync();
        var data = await Task.Run(() => Collect(profile));
        ApplyInventory(profile, data);
        InventoryLoaded = true;
        InventoryTime = Loc.Tf("ДАННЫЕ ОТ {0:HH:mm}", DateTime.Now);
        MarkChecked();
        _ = CheckNvidiaLatestAsync();
        _ = OfferAmdInstallerAsync();
    }

    private static InventoryData Collect(SystemProfile profile)
    {
        List<SignedDriverInfo> drivers = [];
        string? driversError = null;
        try { drivers = DriverInventory.LoadSignedDrivers(); }
        catch (Exception ex)
        {
            driversError = Loc.Tf("Не удалось получить список драйверов (WMI): {0}", ex.Message);
            Log.Warn(driversError);
        }

        List<ProblemDeviceInfo> problems = [];
        string? problemsError = null;
        try { problems = DriverInventory.LoadProblemDevices(); }
        catch (Exception ex)
        {
            problemsError = Loc.Tf("Не удалось проверить устройства (WMI): {0}", ex.Message);
            Log.Warn(problemsError);
        }

        List<UninstallEntry> apps = [];
        try { apps = DriverInventory.ReadUninstallEntries(); } catch { }

        List<DriverBackupInfo> backups = [];
        try { backups = DriverBackupService.List(); }
        catch (Exception ex) { Log.Warn("Резервные копии драйверов: {0}", ex.Message); }

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
        ChipsetCpu = string.IsNullOrWhiteSpace(profile.CpuName) ? Loc.T("Процессор не определён") : profile.CpuName;
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
            DownloadDriverCommand.NotifyCanExecuteChanged(); // the card may now offer a download
            if (card.Package is { SizeBytes: null } package) await FillSizeAsync(card, package);
        }
        UpdateStats();
    }

    /// <summary>Counts shown in the tab badges.</summary>
    private void UpdateStats()
    {
        OnPropertyChanged(nameof(GpuTileText));
        OnPropertyChanged(nameof(GpuUpdateAvailable));
        if (InventoryLoaded || _signedDrivers.Count > 0)
        {
            InstalledBadge = _signedDrivers.Count.ToString();
            ProblemsBadge = Problems.Count > 0 ? Problems.Count.ToString() : "";
            BackupsBadge = Backups.Count > 0 ? Backups.Count.ToString() : "";
        }

        UpdatesBadge = UpdatesState switch
        {
            UpdatesState.Searching => "…",
            UpdatesState.Error => "!",
            UpdatesState.Ready when Updates.Count > 0 => Updates.Count.ToString(),
            _ => "",
        };
    }

    // ───────────────────────────── Navigation & shell helpers ─────────────────────────────

    [RelayCommand]
    private void GoToTab(string? tab)
    {
        if (Enum.TryParse<DriversTab>(tab, true, out var t)) SelectedTab = t;
    }

    [RelayCommand]
    private void OpenDeviceManager() => ShellOpen(ProcessRunner.Sys("devmgmt.msc"), Loc.T("диспетчер устройств"));

    [RelayCommand]
    private async Task OpenUrlAsync(string? url)
    {
        if (!DriverSources.IsAllowedUrl(url))
        {
            Log.Warn("Ссылка не открыта (не официальный источник): {0}", url);
            return;
        }
        if (!url!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // ms-settings: pages open in the Settings app, which never runs with administrator rights.
            ShellOpen(url, url);
            return;
        }
        // Vendor sites: the browser must not inherit Lycoris's administrator token (drivers downloaded there
        // would otherwise run elevated without a UAC prompt).
        if (!await UnelevatedShell.OpenAsync(url))
            Ui.Toast(Loc.Tf("Не удалось открыть страницу без прав администратора. Откройте её в браузере вручную: {0}", url), ToastKind.Warning);
    }

    private static void ShellOpen(string target, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть {0}", ex, what);
            Ui.Toast(Loc.Tf("Не удалось открыть {0}: {1}", what, ex.Message), ToastKind.Error);
        }
    }

    // ───────────────────────────── Vendor tools ─────────────────────────────

    /// <summary>The card a command parameter names: the card itself (a click) or its PnP id as text (the admin-intent replay).</summary>
    private GpuCardItem? CardFor(object? target) => target switch
    {
        GpuCardItem card => card,
        string key when key.Length > 0 => Gpus.FirstOrDefault(g => string.Equals(g.Gpu.PnpDeviceId, key, StringComparison.OrdinalIgnoreCase)),
        _ => Gpus.FirstOrDefault(g => g.IsPrimary) ?? Gpus.FirstOrDefault(),
    };

    [RelayCommand]
    private async Task UseGpuToolAsync(object? target)
    {
        var card = CardFor(target);
        if (card?.Tool is not { } tool) return;
        if (tool.WingetId is null)
        {
            await OpenUrlAsync(tool.SiteUrl);
            return;
        }
        if (card.ToolInstalled)
        {
            if (card.ToolExePath is not null) ShellOpen(card.ToolExePath, tool.Name);
            else await OpenUrlAsync(tool.SiteUrl);
            return;
        }
        if (await InstallToolAsync(tool, "UseGpuTool", card.Gpu.PnpDeviceId))
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
            await OpenUrlAsync(DriverSources.IntelDsa.SiteUrl);
            return;
        }
        if (await InstallToolAsync(DriverSources.IntelDsa, "UseIntelDsa", null))
        {
            UpdateIntelDsaState();
            foreach (var card in Gpus.Where(g => g.Vendor == GpuVendor.Intel)) card.MarkToolInstalled(_apps);
        }
    }

    private void UpdateIntelDsaState()
    {
        IntelDsaInstalled = _apps.Any(a => a.DisplayName.Contains("Driver & Support Assistant", StringComparison.OrdinalIgnoreCase));
        IntelDsaButtonText = IntelDsaInstalled ? Loc.T("ОТКРЫТЬ INTEL DSA") : Loc.T("УСТАНОВИТЬ INTEL DSA");
    }

    /// <summary>Installs an official vendor utility with winget after a confirmation that names publisher and source.</summary>
    private async Task<bool> InstallToolAsync(VendorTool tool, string intentCommand, string? intentParameter)
    {
        if (tool.WingetId is not { } id || DriverSources.IsBlocked(id)) return false;
        if (!RequireAdmin(Loc.Tf("установка {0}", tool.Name), intentCommand, intentParameter)) return false;
        if (IsBusy) return false;

        var store = tool.WingetSource == "msstore";
        var confirmed = await Ui.ConfirmAsync(
            Loc.Tf("УСТАНОВИТЬ {0}?", tool.Name.ToUpperInvariant()),
            tool.Description,
            [
                Loc.Tf("Программа: {0}", tool.Name),
                Loc.Tf("Издатель: {0}", tool.Publisher),
                Loc.Tf("Источник: {0}", (store ? Loc.T("Microsoft Store через winget (msstore)") : Loc.T("официальный репозиторий winget"))),
                Loc.Tf("Пакет: {0}", id),
                Loc.T("Будет выполнено: winget install --id … --exact — без --force и без перезагрузки"),
                store
                    ? Loc.T("Нажимая «Установить», вы принимаете лицензию издателя и условия Microsoft Store")
                    : Loc.T("Нажимая «Установить», вы принимаете лицензию издателя"),
            ],
            confirmText: Loc.T("УСТАНОВИТЬ"),
            footnote: Loc.T("Удалить программу можно в «Параметры → Приложения»."));
        if (!confirmed) return false;

        var success = false;
        await RunBusyAsync(OperationClass.Install, Loc.Tf("Установка {0}…", tool.Name), async () =>
        {
            Log.Info("Установка {0} ({1}, источник {2})", tool.Name, id, tool.WingetSource);
            var r = await WingetClient.InstallAsync(id, tool.WingetSource, line => Log.Info("winget: " + line));
            if (r.Success)
            {
                success = true;
                Ui.Toast($"{tool.Name}: {r.Message}.", ToastKind.Success);
            }
            else
            {
                Ui.Toast($"{tool.Name}: {r.Message}.", ToastKind.Error, Loc.T("УСТАНОВКА НЕ УДАЛАСЬ"));
            }
            _apps = await Task.Run(DriverInventory.ReadUninstallEntries);
        });
        return success;
    }

    /// <summary>Creates a restore point; when Windows does not create one, the user decides whether to continue.</summary>
    private async Task<bool> EnsureRestorePointAsync(string description)
    {
        var rp = await RestorePointService.CreateAsync(description);
        if (rp.Created)
        {
            Ui.Toast(rp.Message, ToastKind.Success, Loc.T("ТОЧКА ВОССТАНОВЛЕНИЯ"));
            return true;
        }
        Log.Warn(rp.Message);
        return await AskWithoutLeaseAsync(() => Ui.ConfirmAsync(
            "ТОЧКА ВОССТАНОВЛЕНИЯ НЕ СОЗДАНА",
            Loc.Tf("{0} Без неё откатить драйверы можно будет только через диспетчер устройств или резервную копию драйверов. Продолжить?", rp.Message),
            confirmText: Loc.T("ПРОДОЛЖИТЬ"), cancelText: Loc.T("ОТМЕНИТЬ"), danger: true));
    }
}
