using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Dashboard.Checks;
using Monolith.Modules.Dashboard.Monitoring;
using Monolith.Modules.Drivers;
using Monolith.Shell;
using Monolith.Tweaks;
using Monolith.UI;

namespace Monolith.Modules.Dashboard;

public sealed partial class DashboardViewModel : PageViewModel
{
    private const int HistoryLength = 60;
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(2);

    private readonly LiveMonitor _monitor = new();
    private DateTime _lastCheck = DateTime.MinValue;
    private bool _checking;
    private SystemRow? _uptimeRow;

    public override string Key => "dashboard";
    public override string Index => "01";
    public override string Title => "ПАНЕЛЬ";
    public override string Subtitle => "Состояние системы в реальном времени, оценка и рекомендации.";
    public override string Glyph => Glyphs.Home;

    public DashboardViewModel()
    {
        _monitor.Sampled += sample => Ui.Post(() => Apply(sample));
        if (Application.Current is { } app) app.Exit += (_, _) => _monitor.Dispose();
    }

    // ───────────────────────────── Live tiles ─────────────────────────────

    public ObservableCollection<double> CpuHistory { get; } = [];
    public ObservableCollection<double> GpuHistory { get; } = [];
    public ObservableCollection<double> RamHistory { get; } = [];
    public ObservableCollection<double> DiskHistory { get; } = [];
    public ObservableCollection<double> NetHistory { get; } = [];

    [ObservableProperty] private string _cpuText = DashFormat.Missing;
    [ObservableProperty] private double _cpuLoad;
    [ObservableProperty] private string _cpuClockText = DashFormat.Missing;
    [ObservableProperty] private string _cpuCoresText = "";

    [ObservableProperty] private string _gpuText = DashFormat.Missing;
    [ObservableProperty] private double _gpuLoad;
    [ObservableProperty] private string _gpuTempText = DashFormat.Missing;
    [ObservableProperty] private string _gpuVramText = DashFormat.Missing;
    [ObservableProperty] private string _gpuName = "";

    [ObservableProperty] private string _ramText = DashFormat.Missing;
    [ObservableProperty] private double _ramLoad;
    [ObservableProperty] private string _ramUsedText = DashFormat.Missing;
    [ObservableProperty] private string _ramFreeText = DashFormat.Missing;

    [ObservableProperty] private string _diskText = DashFormat.Missing;
    [ObservableProperty] private double _diskLoad;
    [ObservableProperty] private string _diskIoText = DashFormat.Missing;
    [ObservableProperty] private string _netDownText = DashFormat.Missing;
    [ObservableProperty] private string _netUpText = DashFormat.Missing;
    [ObservableProperty] private double _netMax = 256 * 1024;

    [ObservableProperty] private string _intervalText = "";

    // ───────────────────────────── Health ─────────────────────────────

    /// <summary>Warnings and recommendations: full rows with explanation and action.</summary>
    public ObservableCollection<CheckItemViewModel> Issues { get; } = [];
    /// <summary>Checks that passed: compact grid.</summary>
    public ObservableCollection<CheckItemViewModel> Passed { get; } = [];

    [ObservableProperty] private int _score;
    [ObservableProperty] private string _scoreText = DashFormat.Missing;
    [ObservableProperty] private string _verdict = "ПРОВЕРКА…";
    [ObservableProperty] private string _verdictSummary = "Собираем сведения о системе. Это займёт несколько секунд.";
    [ObservableProperty] private string _passedText = "";
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private int _infoCount;
    [ObservableProperty] private int _okCount;
    [ObservableProperty] private string _lastCheckText = "";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _hasIssues;
    [ObservableProperty] private bool _isChecking;

    // ───────────────────────────── System card ─────────────────────────────

    public ObservableCollection<SystemRow> SystemRows { get; } = [];

    // ───────────────────────────── Lifecycle ─────────────────────────────

    protected override async Task LoadAsync()
    {
        StartMonitor();
        await RefreshAsync(showBusy: true);
    }

    protected override async Task OnReturnAsync()
    {
        StartMonitor();
        if (DateTime.Now - _lastCheck > RecheckAfter) await RefreshAsync(showBusy: false);
    }

    public override void Deactivate() => _monitor.Stop();

    private void StartMonitor()
    {
        var ms = Math.Clamp(AppSettings.Current.MonitorIntervalMs, 250, 10_000);
        IntervalText = $"ОПРОС {(ms / 1000d).ToString(ms % 1000 == 0 ? "0" : "0.0#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))} С";
        _monitor.Start();
    }

    private void Apply(MonitorSample s)
    {
        CpuText = DashFormat.Percent(s.CpuPercent);
        CpuLoad = s.CpuPercent ?? 0;
        CpuClockText = DashFormat.Clock(s.CpuMhz);
        if (s.CpuPercent is { } cpu) MonitorMath.Push(CpuHistory, cpu, HistoryLength);

        GpuText = DashFormat.Percent(s.GpuPercent);
        GpuLoad = s.GpuPercent ?? 0;
        GpuTempText = DashFormat.Temperature(s.GpuTemperature);
        GpuVramText = DashFormat.UsedOfCapacityGb(s.VramUsed, s.VramTotal);
        if (s.GpuPercent is { } gpu) MonitorMath.Push(GpuHistory, gpu, HistoryLength);

        if (s.RamPercent is { } ram)
        {
            RamText = DashFormat.Percent(ram);
            RamLoad = ram;
            RamUsedText = DashFormat.UsedOfTotalGb(s.RamUsed, s.RamTotal);
            RamFreeText = DashFormat.Size(s.RamTotal - s.RamUsed);
            MonitorMath.Push(RamHistory, ram, HistoryLength);
        }

        DiskText = DashFormat.Percent(s.DiskPercent);
        DiskLoad = s.DiskPercent ?? 0;
        DiskIoText = s.DiskReadBps is null && s.DiskWriteBps is null
            ? DashFormat.Missing
            : $"Чтение {DashFormat.Rate(s.DiskReadBps)} · запись {DashFormat.Rate(s.DiskWriteBps)}";
        if (s.DiskPercent is { } disk) MonitorMath.Push(DiskHistory, disk, HistoryLength);

        NetDownText = DashFormat.Rate(s.NetDownBps);
        NetUpText = DashFormat.Rate(s.NetUpBps);
        if (s.NetDownBps is not null || s.NetUpBps is not null)
        {
            MonitorMath.Push(NetHistory, (s.NetDownBps ?? 0) + (s.NetUpBps ?? 0), HistoryLength);
            NetMax = Math.Max(256 * 1024, NetHistory.Max() * 1.15);
        }

        if (_uptimeRow is not null) _uptimeRow.Value = DashFormat.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    // ───────────────────────────── Checks ─────────────────────────────

    [RelayCommand]
    private Task RefreshChecks() => RefreshAsync(showBusy: true);

    private async Task RefreshAsync(bool showBusy)
    {
        if (_checking) return;
        _checking = true;
        IsChecking = true;
        try
        {
            if (showBusy) await RunBusyAsync("Проверка системы…", CheckCoreAsync);
            else
            {
                try { await CheckCoreAsync(); }
                catch (Exception ex) { Log.Error("Проверка системы", ex); }
            }
        }
        finally
        {
            _checking = false;
            IsChecking = false;
        }
    }

    private async Task CheckCoreAsync()
    {
        var snapshot = await SystemInspector.CollectAsync();
        var profile = await SystemProfile.LoadAsync();
        // Buttons that apply a catalog tweak are matched against the catalog (applicability + current state),
        // so a check never shows a button that would do nothing on this PC.
        var results = await Task.Run(() => HealthRules.Evaluate(snapshot).Select(r => ResolveTweakAction(r, profile)).ToList());

        Issues.Clear();
        Passed.Clear();
        foreach (var r in HealthScore.Ordered(results))
            (r.Severity == CheckSeverity.Ok ? Passed : Issues).Add(new CheckItemViewModel(r, RunActionAsync));

        WarningCount = results.Count(r => r.Severity == CheckSeverity.Warning);
        InfoCount = results.Count(r => r.Severity == CheckSeverity.Info);
        OkCount = results.Count(r => r.Severity == CheckSeverity.Ok);
        Score = HealthScore.Calculate(results);
        ScoreText = Score.ToString();
        Verdict = results.Count == 0 ? "НЕТ ДАННЫХ" : HealthScore.Verdict(Score);
        VerdictSummary = results.Count == 0
            ? "Не удалось прочитать сведения о системе. Подробности — в консоли."
            : HealthScore.Summary(WarningCount, InfoCount);
        PassedText = $"{OkCount} ИЗ {results.Count}";
        LastCheckText = $"Проверено в {snapshot.Now:HH:mm}";
        HasResults = results.Count > 0;
        HasIssues = Issues.Count > 0;
        _lastCheck = DateTime.Now;

        BuildSystemRows(profile, snapshot);
        CpuCoresText = $"{DashFormat.Count(profile.CpuCores, "ядро", "ядра", "ядер")} · {DashFormat.Count(profile.CpuThreads, "поток", "потока", "потоков")}";
        GpuName = profile.PrimaryGpu?.Name ?? "";

        if (WarningCount > 0) Log.Info($"Панель: оценка {Score}/100, проблем: {WarningCount}.");
        else Log.Success($"Панель: оценка {Score}/100, проблем не найдено.");
    }

    private void BuildSystemRows(SystemProfile p, SystemSnapshot s)
    {
        SystemRows.Clear();

        var clock = p.CpuMaxClockMhz > 0 ? " · база " + DashFormat.Clock(p.CpuMaxClockMhz) : "";
        SystemRows.Add(new SystemRow("ПРОЦЕССОР", Fallback(p.CpuName),
            $"{DashFormat.Count(p.CpuCores, "ядро", "ядра", "ядер")} · {DashFormat.Count(p.CpuThreads, "поток", "потока", "потоков")}{clock}"));

        var gpus = p.Gpus.OrderBy(g => g.IsIntegrated).ThenByDescending(g => g.DedicatedMemoryBytes).ToList();
        for (var i = 0; i < gpus.Count; i++)
        {
            var g = gpus[i];
            var parts = new List<string>();
            if (g.IsIntegrated) parts.Add("встроенная");
            else if (g.DedicatedMemoryBytes > 0) parts.Add(DashFormat.WholeGb(g.DedicatedMemoryBytes));
            var version = HealthRules.DriverVersionText(g);
            if (version.Length > 0)
                parts.Add(g.DriverDate is { Year: > 2000 } d ? $"драйвер {version} от {DashFormat.Date(d)}" : $"драйвер {version}");
            SystemRows.Add(new SystemRow(i == 0 ? "ВИДЕОКАРТА" : $"ВИДЕОКАРТА {i + 1}", g.Name, string.Join(" · ", parts)));
        }

        var ram = HealthRules.AnalyzeRam(s.RamModules);
        // Installed size = sum of the modules (the OS reports slightly less as usable).
        var installed = ram is { Capacity: > 0 } ? ram.Capacity : p.RamTotalBytes;
        var ramValue = installed > 0 ? DashFormat.WholeGb(installed) : DashFormat.Missing;
        if (ram is not null)
        {
            if (ram.Type.Length > 0) ramValue += " " + ram.Type;
            ramValue += " · " + DashFormat.Count(ram.Modules, "модуль", "модуля", "модулей");
            var speed = ram.Configured == ram.Rated
                ? $"{ram.Configured} МТ/с — номинал"
                : $"{ram.Configured} МТ/с · номинал {ram.Rated} МТ/с";
            SystemRows.Add(new SystemRow("ПАМЯТЬ", ramValue, speed));
        }
        else
        {
            SystemRows.Add(new SystemRow("ПАМЯТЬ", ramValue));
        }

        var board = p.IsLaptop && !string.IsNullOrWhiteSpace(p.Model) ? $"{HealthRules.ShortVendor(p.Manufacturer)} {p.Model}".Trim() : HealthRules.ShortVendor(p.Motherboard);
        SystemRows.Add(new SystemRow(p.IsLaptop ? "УСТРОЙСТВО" : "МАТ. ПЛАТА", Fallback(board),
            string.IsNullOrWhiteSpace(p.BiosVersion) ? "" : $"BIOS {p.BiosVersion}"));

        var build = $"сборка {p.OsBuild}{(p.OsUbr > 0 ? "." + p.OsUbr : "")}";
        SystemRows.Add(new SystemRow("СИСТЕМА", $"{p.OsName} {p.OsDisplayVersion}".Trim(), build));

        for (var i = 0; i < s.Displays.Count && i < 4; i++)
        {
            var d = s.Displays[i];
            var hz = d.ExactHz is { } exact ? (int)Math.Round(exact) : d.CurrentHz;
            var max = HealthRules.IsBelowMaxRefresh(d.CurrentHz, d.MaxHz) ? $" (до {d.MaxHz} Гц)" : "";
            SystemRows.Add(new SystemRow(i == 0 ? "ДИСПЛЕЙ" : $"ДИСПЛЕЙ {i + 1}", d.Name, $"{d.Width}×{d.Height} · {hz} Гц{max}"));
        }

        if (s.SystemDriveTotal > 0)
        {
            var space = $"свободно {DashFormat.Size(s.SystemDriveFree)} из {DashFormat.Size(s.SystemDriveTotal)} " +
                        $"({DashFormat.Share((double)s.SystemDriveFree / s.SystemDriveTotal)})";
            var kind = s.SystemDisk is { } sd ? string.Join(" ", new[] { sd.BusType, sd.MediaType }.Where(x => x.Length > 0)) : "";
            var model = s.SystemDisk?.Model is { Length: > 0 } m ? m : SystemProfile.SystemDrive.TrimEnd('\\');
            SystemRows.Add(new SystemRow("СИСТЕМНЫЙ ДИСК", model, kind.Length > 0 ? $"{kind} · {space}" : space));
        }

        var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        _uptimeRow = new SystemRow("ВРЕМЯ РАБОТЫ", DashFormat.Duration(s.Uptime), $"запущена {boot:dd.MM.yyyy HH:mm}");
        SystemRows.Add(_uptimeRow);
    }

    private static string Fallback(string? s) => string.IsNullOrWhiteSpace(s) ? DashFormat.Missing : s.Trim();

    /// <summary>Reads the catalog for an ApplyTweak button (background thread: Detect reads the registry / driver caps).</summary>
    private static HealthCheckResult ResolveTweakAction(HealthCheckResult r, SystemProfile profile)
    {
        if (r.Action is not { Kind: CheckActionKind.ApplyTweak, Target: { } id }) return r;
        var tweak = TweakCatalog.Find(id);
        TweakAvailability availability;
        if (tweak is null || tweak.Risk == TweakRisk.Advanced) availability = TweakAvailability.OptimizeOnly;
        else if (!tweak.IsApplicable(profile)) availability = TweakAvailability.NotApplicable;
        else
        {
            TweakState state;
            try { state = tweak.Detect(); }
            catch (Exception ex)
            {
                Log.Warn($"Панель: не удалось прочитать состояние «{tweak.Title}»: {ex.Message}");
                state = TweakState.Unknown;
            }
            availability = state switch
            {
                TweakState.Applied => TweakAvailability.AlreadyApplied,
                TweakState.NotApplicable => TweakAvailability.NotApplicable,
                _ => TweakAvailability.Available,
            };
        }
        return HealthRules.ResolveTweakAction(r, availability, tweak is null ? "" : DashFormat.RestartWhen(tweak.Restart));
    }

    // ───────────────────────────── Check actions ─────────────────────────────

    private async Task RunActionAsync(CheckItemViewModel item)
    {
        var action = item.Result.Action;
        if (action is null) return;
        if (action.RequiresAdmin && IsBusy)
        {
            Ui.Toast("Дождитесь окончания текущей операции.", ToastKind.Info);
            return;
        }
        switch (action.Kind)
        {
            case CheckActionKind.Navigate when action.Target is { } page:
                NavigateTo(page, action.Section);
                break;
            case CheckActionKind.OpenUri when action.Target is { } uri:
                if (uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
                    await OpenShellAsync(uri, null, "параметры Windows");
                else if (!await UnelevatedShell.OpenAsync(uri)) // web pages: never in a browser with admin rights
                    Ui.Toast("Не удалось открыть ссылку без прав администратора.", ToastKind.Warning);
                break;
            case CheckActionKind.OpenPowerOptions:
                await OpenShellAsync(ProcessRunner.Sys("control.exe"), "/name Microsoft.PowerOptions", "«Электропитание»");
                break;
            case CheckActionKind.EnableRestore:
                await EnableRestoreAsync();
                break;
            case CheckActionKind.ApplyTweak when action.Target is { } id:
                await ApplyTweakAsync(id);
                break;
        }
    }

    private static Task OpenShellAsync(string target, string? args, string what) => Task.Run(() =>
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (args is not null) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось открыть {what}", ex);
            Ui.Toast($"Не удалось открыть {what}: {ex.Message}", ToastKind.Error);
        }
    });

    private async Task EnableRestoreAsync()
    {
        if (!RequireAdmin("Включить защиту системы")) return;
        var confirmed = await Ui.ConfirmAsync(
            "ВКЛЮЧИТЬ ЗАЩИТУ СИСТЕМЫ?",
            "MONOLITH включит защиту системы для системного диска — так же, как кнопка «Настроить → Включить защиту системы» в свойствах системы.",
            [
                $"Windows начнёт создавать точки восстановления для диска {SystemProfile.SystemDrive.TrimEnd('\\')}",
                "Под точки будет выделена часть диска; старые точки Windows удаляет сама",
                "Файлы, программы и настройки не затрагиваются",
                "Выключить можно в «Свойства системы → Защита системы → Настроить»",
            ],
            confirmText: "ВКЛЮЧИТЬ");
        if (!confirmed) return;

        var ok = false;
        await RunBusyAsync("Включение защиты системы…", async () => ok = await RestorePointService.EnableAsync());
        Ui.Toast(ok ? "Защита системы включена — Windows будет создавать точки восстановления."
                    : "Не удалось включить защиту системы. Подробности — в консоли.",
            ok ? ToastKind.Success : ToastKind.Error, "Защита системы");
        await RefreshAsync(showBusy: false);
    }

    private async Task ApplyTweakAsync(string id)
    {
        var tweak = TweakCatalog.Find(id);
        if (tweak is null || tweak.Risk == TweakRisk.Advanced)
        {
            Ui.Toast("Эта настройка находится в разделе «Оптимизация».", ToastKind.Info);
            Ui.Navigate("optimize");
            return;
        }
        if (!RequireAdmin(tweak.Title)) return;

        // Re-checked at click time: the button is only shown for applicable tweaks, but the PC may have changed since.
        var profile = await SystemProfile.LoadAsync();
        var (applicable, state, protectionOn) = await Task.Run(() =>
        {
            var ok = tweak.IsApplicable(profile);
            TweakState s;
            try { s = ok ? tweak.Detect() : TweakState.NotApplicable; }
            catch { s = TweakState.Unknown; }
            return (ok, s, RestorePointService.IsEnabled());
        });
        if (!applicable)
        {
            Ui.Toast(tweak.NotApplicableReason ?? "Эта настройка не подходит для этого компьютера.", ToastKind.Warning, tweak.Title);
            await RefreshAsync(showBusy: false);
            return;
        }
        var restartWhen = DashFormat.RestartWhen(tweak.Restart);
        if (state == TweakState.Applied)
        {
            Ui.Toast(restartWhen.Length > 0
                    ? $"Настройка уже записана и вступит в силу {restartWhen}."
                    : "Настройка уже действует — менять нечего.",
                ToastKind.Info, tweak.Title);
            await RefreshAsync(showBusy: false);
            return;
        }

        var details = new List<string> { $"{tweak.Title}: {tweak.Description}" };
        if (!string.IsNullOrWhiteSpace(tweak.Details)) details.Add(tweak.Details);
        if (restartWhen.Length > 0) details.Add($"Вступит в силу {restartWhen}");
        details.Add(!AppSettings.Current.CreateRestorePoints
            ? "Точка восстановления не создаётся — это выключено в настройках"
            : protectionOn
                ? "Перед изменением будет создана точка восстановления Windows"
                : "Защита системы выключена — точки восстановления не будет, отменить можно только через журнал MONOLITH");
        details.Add("Исходное значение сохраняется в журнале — отменить можно в разделе «Восстановление»");

        var confirmed = await Ui.ConfirmAsync("ПРИМЕНИТЬ НАСТРОЙКУ?",
            $"MONOLITH применит настройку «{tweak.Title}» через журнал изменений. Будет сделано следующее:",
            details, footnote: "Изменение обратимо: исходное значение хранится в журнале MONOLITH.");
        if (!confirmed) return;

        TweakRunResult? result = null;
        await RunBusyAsync($"Применение: {tweak.Title}…", async () => result = await TweakEngine.ApplyAsync([tweak], tweak.Title));
        if (result is { } r)
        {
            if (r.Errors.Count > 0)
                Ui.Toast(string.Join(Environment.NewLine, r.Errors), ToastKind.Error, "Не удалось применить");
            else if (r.Applied > 0)
                Ui.Toast(r.Restart == RestartRequirement.None
                        ? "Готово. Отменить можно в разделе «Восстановление»."
                        : $"Готово. Изменение вступит в силу {DashFormat.RestartWhen(r.Restart)}.",
                    ToastKind.Success, tweak.Title);
            else
                Ui.Toast("Настройка уже действует — менять нечего.", ToastKind.Info, tweak.Title);
        }
        await RefreshAsync(showBusy: false);
    }

    // ───────────────────────────── Navigation ─────────────────────────────

    /// <summary>Opens a page; for «Железо и драйверы» also the requested tab ("Hardware", "Overview", "Problems"…).</summary>
    private static void NavigateTo(string page, string? section)
    {
        if (page == HealthRules.DriversPage && section is not null
            && Enum.TryParse<DriversTab>(section, ignoreCase: true, out var tab)
            && Ui.Host is MainViewModel main && main.Pages.OfType<DriversViewModel>().FirstOrDefault() is { } drivers)
            drivers.SelectedTab = tab;
        Ui.Navigate(page);
    }

    // ───────────────────────────── Quick actions ─────────────────────────────

    [RelayCommand]
    private void Go(string? page)
    {
        if (!string.IsNullOrEmpty(page)) Ui.Navigate(page);
    }

    /// <summary>«Железо и драйверы» → «Подбор для вашего ПК»: components and the official drivers / utilities for them.</summary>
    [RelayCommand]
    private void OpenHardwareMatch() => NavigateTo(HealthRules.DriversPage, HealthRules.DriversTabHardware);
}
