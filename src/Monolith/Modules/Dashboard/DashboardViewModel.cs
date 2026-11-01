using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Dashboard.Checks;
using Monolith.Modules.Dashboard.Monitoring;
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
        var results = await Task.Run(() => HealthRules.Evaluate(snapshot));
        var profile = SystemProfile.Current;

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

    // ───────────────────────────── Check actions ─────────────────────────────

    private Task RunActionAsync(CheckItemViewModel item) => RunActionAsync(item.Result.Action);

    /// <summary>The second button of a check (e.g. the hibernation item behind a low-space warning). Same handling as the first.</summary>
    [RelayCommand]
    private Task RunSecondaryActionAsync(CheckItemViewModel? item) => RunActionAsync(item?.Result.SecondaryAction);

    private async Task RunActionAsync(CheckAction? action)
    {
        if (action is null) return;
        if (action.RequiresAdmin && IsBusy)
        {
            Ui.Toast("Дождитесь окончания текущей операции.", ToastKind.Info);
            return;
        }
        switch (action.Kind)
        {
            case CheckActionKind.Navigate when action.Target is { } page:
                _lastCheck = DateTime.MinValue; // the user may fix it there: re-check on return
                Ui.Navigate(page);
                break;
            case CheckActionKind.OpenUri when action.Target is { } uri:
                await OpenShellAsync(uri, null, "параметры Windows");
                break;
            case CheckActionKind.FocusTweak when action.Target is { } tweakId:
                _lastCheck = DateTime.MinValue;
                Ui.RequestFocus("optimize", tweakId);
                Ui.Navigate("optimize");
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

        var profile = await SystemProfile.LoadAsync();
        if (TweakEngine.Applicability(tweak, profile) is null)
        {
            Ui.Toast("Не удалось определить характеристики компьютера — настройку пока нельзя проверить. Попробуйте позже.", ToastKind.Warning, tweak.Title);
            return;
        }
        if (TweakEngine.Applicability(tweak, profile) is not true)
        {
            // The check looks at the primary GPU, the catalog at every GPU: use the HAGS item that fits this PC.
            var sibling = id switch
            {
                HealthRules.TweakHags => TweakCatalog.Find(HealthRules.TweakHagsOther),
                HealthRules.TweakHagsOther => TweakCatalog.Find(HealthRules.TweakHags),
                _ => null,
            };
            if (sibling is not null && sibling.Risk != TweakRisk.Advanced && TweakEngine.Applicability(sibling, profile) == true)
                tweak = sibling;
            else
            {
                // Never a dead button: explain and show the full list where the user can pick a fitting item.
                Ui.Toast(tweak.NotApplicableReason ?? "Эта настройка не подходит для этого компьютера.", ToastKind.Warning, tweak.Title);
                Ui.Navigate("optimize");
                return;
            }
        }

        var details = new List<string> { $"{tweak.Title}: {tweak.Description}" };
        if (tweak.Restart != RestartRequirement.None) details.Add($"Вступит в силу {tweak.RestartWhen}");
        details.Add(AppSettings.Current.CreateRestorePoints
            ? "Перед изменением будет создана точка восстановления (если защита системы включена)"
            : "Точка восстановления не создаётся — это выключено в разделе «Восстановление»");
        details.Add("Исходное значение сохраняется в журнале — отменить можно в разделе «Восстановление»");

        var confirmed = await Ui.ConfirmAsync("ПРИМЕНИТЬ НАСТРОЙКУ?",
            $"MONOLITH применит настройку «{tweak.Title}» через журнал изменений. Будет сделано следующее:",
            details, footnote: "Изменение обратимо.");
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
                        : $"Готово. Изменение вступит в силу {tweak.RestartWhen}.",
                    ToastKind.Success, tweak.Title);
            else
                Ui.Toast("Настройка уже действует — менять нечего.", ToastKind.Info, tweak.Title);
        }
        await RefreshAsync(showBusy: false);
    }
}
