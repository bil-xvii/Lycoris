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
    private bool _firstSample;
    private DateTime _lastCheck = DateTime.MinValue;
    private bool _checking;
    private SystemRow? _uptimeRow;

    public override string Key => "dashboard";
    public override string Index => "01";
    public override string Title => Loc.Key("ПАНЕЛЬ");
    public override string Subtitle => Loc.Key("Что нажать, чтобы ПК был готов к игре.");
    public override string Glyph => Glyphs.Home;

    public DashboardViewModel()
    {
        _monitor.Sampled += sample => Ui.Post(() => Apply(sample));
        if (Application.Current is { } app) app.Exit += (_, _) => _monitor.Dispose();
        Tiles = BuildTiles();
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

    /// <summary>Every check of the last run, worst first.</summary>
    private readonly List<CheckItemViewModel> _all = [];
    /// <summary>Warnings and recommendations: full rows with explanation and action.</summary>
    public IEnumerable<CheckItemViewModel> Issues => _all.Where(i => !i.IsOk);
    /// <summary>The list under the score: the checks of the chosen chip (ВНИМАНИЕ · СОВЕТЫ · В НОРМЕ).</summary>
    public ObservableCollection<CheckItemViewModel> Visible { get; } = [];

    [ObservableProperty] private int _score;
    [ObservableProperty] private string _scoreText = DashFormat.Missing;
    /// <summary>Colour tier of the score for the view: None / Good (>= 90) / Fair (>= 70) / Poor (>= 50) / Bad. The words beside the number say the same.</summary>
    [ObservableProperty] private string _scoreTier = "None";
    [ObservableProperty] private string _verdict = Loc.T("ПРОВЕРКА…");
    [ObservableProperty] private string _verdictSummary = Loc.T("Собираем сведения о системе. Это займёт несколько секунд.");
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
        AttachHost();
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
        IntervalText = Loc.Tf("ОПРОС {0} С", (ms / 1000d).ToString(ms % 1000 == 0 ? "0" : "0.0#", Loc.Culture));
        _monitor.Start();
    }

    private void Apply(MonitorSample s)
    {
        if (!_firstSample) { _firstSample = true; StartupTiming.Mark("dashboard first monitor sample"); }
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
            : Loc.Tf("Чтение {0} · запись {1}", DashFormat.Rate(s.DiskReadBps), DashFormat.Rate(s.DiskWriteBps));
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
            if (showBusy) await RunBusyAsync(OperationClass.Measure, Loc.Key("Проверка системы…"), CheckCoreAsync);
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

        _all.Clear();
        foreach (var r in HealthScore.Ordered(results))
            _all.Add(new CheckItemViewModel(r, RunActionAsync, ActionLabel));

        WarningCount = results.Count(r => r.Severity == CheckSeverity.Warning);
        InfoCount = results.Count(r => r.Severity == CheckSeverity.Info);
        OkCount = results.Count(r => r.Severity == CheckSeverity.Ok);
        Score = HealthScore.Calculate(results);
        ScoreText = Score.ToString();
        ScoreTier = results.Count == 0 ? "None" : Score >= 90 ? "Good" : Score >= 70 ? "Fair" : Score >= 50 ? "Poor" : "Bad";
        Verdict = results.Count == 0 ? Loc.T("НЕТ ДАННЫХ") : HealthScore.Verdict(Score);
        VerdictSummary = results.Count == 0
            ? Loc.T("Не удалось прочитать сведения о системе. Подробности — в консоли.")
            : HealthScore.Summary(WarningCount, InfoCount);
        PassedText = Loc.Tf("{0} ИЗ {1}", OkCount, results.Count);
        LastCheckText = Loc.Tf("Проверено в {0}", Loc.TimeText(snapshot.Now));
        HasResults = results.Count > 0;
        HasIssues = Issues.Any();
        _lastCheck = DateTime.Now;
        MarkChecked();
        BadgeText = LaunchpadLogic.Badge(WarningCount, InfoCount);
        SafeFixCount = _all.Count(IsSafeFix);
        if (!_filterChosen) Filter = LaunchpadLogic.DefaultFilter(WarningCount, InfoCount);
        RebuildVisible();
        UpdateGpuInstalled(profile);

        BuildSystemRows(profile, snapshot);
        UpdateSystemSummary();
        CpuCoresText = CoresThreads(profile.CpuCores, profile.CpuThreads);
        GpuName = profile.PrimaryGpu?.Name ?? "";

        StartupTiming.Mark("dashboard checks done");
        if (WarningCount > 0) Log.Info("Панель: оценка {0}/100, проблем: {1}.", Score, WarningCount);
        else Log.Success("Панель: оценка {0}/100, проблем не найдено.", Score);
    }

    private void BuildSystemRows(SystemProfile p, SystemSnapshot s)
    {
        SystemRows.Clear();

        var clock = p.CpuMaxClockMhz > 0 ? Loc.Tf(" · база {0}", DashFormat.Clock(p.CpuMaxClockMhz)) : "";
        SystemRows.Add(new SystemRow(Loc.T("ПРОЦЕССОР"), Fallback(p.CpuName), CoresThreads(p.CpuCores, p.CpuThreads) + clock));

        var gpus = p.Gpus.OrderBy(g => g.IsIntegrated).ThenByDescending(g => g.DedicatedMemoryBytes).ToList();
        for (var i = 0; i < gpus.Count; i++)
        {
            var g = gpus[i];
            var parts = new List<string>();
            if (g.IsIntegrated) parts.Add(Loc.T("встроенная"));
            else if (g.DedicatedMemoryBytes > 0) parts.Add(DashFormat.WholeGb(g.DedicatedMemoryBytes));
            var version = HealthRules.DriverVersionText(g);
            if (version.Length > 0)
                parts.Add(g.DriverDate is { Year: > 2000 } d ? Loc.Tf("драйвер {0} от {1}", version, DashFormat.Date(d)) : Loc.Tf("драйвер {0}", version));
            SystemRows.Add(new SystemRow(i == 0 ? Loc.T("ВИДЕОКАРТА") : Loc.Tf("ВИДЕОКАРТА {0}", i + 1), g.Name, string.Join(" · ", parts)));
        }

        var ram = HealthRules.AnalyzeRam(s.RamModules);
        // Installed size = sum of the modules (the OS reports slightly less as usable).
        var installed = ram is { Capacity: > 0 } ? ram.Capacity : p.RamTotalBytes;
        var ramValue = installed > 0 ? DashFormat.WholeGb(installed) : DashFormat.Missing;
        if (ram is not null)
        {
            if (ram.Type.Length > 0) ramValue += " " + ram.Type;
            ramValue += " · " + Loc.N(ram.Modules, "{0} модуль", "{0} модуля", "{0} модулей");
            var speed = ram.Configured == ram.Rated
                ? Loc.Tf("{0} МТ/с — номинал", ram.Configured)
                : Loc.Tf("{0} МТ/с · номинал {1} МТ/с", ram.Configured, ram.Rated);
            SystemRows.Add(new SystemRow(Loc.T("ПАМЯТЬ"), ramValue, speed));
        }
        else
        {
            SystemRows.Add(new SystemRow(Loc.T("ПАМЯТЬ"), ramValue));
        }

        var board = p.IsLaptop && !string.IsNullOrWhiteSpace(p.Model) ? $"{HealthRules.ShortVendor(p.Manufacturer)} {p.Model}".Trim() : HealthRules.ShortVendor(p.Motherboard);
        SystemRows.Add(new SystemRow(p.IsLaptop ? Loc.T("УСТРОЙСТВО") : Loc.T("МАТ. ПЛАТА"), Fallback(board),
            string.IsNullOrWhiteSpace(p.BiosVersion) ? "" : $"BIOS {p.BiosVersion}"));

        var build = Loc.Tf("сборка {0}", $"{p.OsBuild}{(p.OsUbr > 0 ? "." + p.OsUbr : "")}");
        SystemRows.Add(new SystemRow(Loc.T("СИСТЕМА"), $"{p.OsName} {p.OsDisplayVersion}".Trim(), build));

        for (var i = 0; i < s.Displays.Count && i < 4; i++)
        {
            var d = s.Displays[i];
            var hz = d.ExactHz is { } exact ? (int)Math.Round(exact) : d.CurrentHz;
            var max = HealthRules.IsBelowMaxRefresh(d.CurrentHz, d.MaxHz) ? Loc.Tf(" (до {0} Гц)", d.MaxHz) : "";
            SystemRows.Add(new SystemRow(i == 0 ? Loc.T("ДИСПЛЕЙ") : Loc.Tf("ДИСПЛЕЙ {0}", i + 1), d.Name, Loc.Tf("{0}×{1} · {2} Гц", d.Width, d.Height, hz) + max));
        }

        if (s.SystemDriveTotal > 0)
        {
            var space = Loc.Tf("свободно {0} из {1} ({2})", DashFormat.Size(s.SystemDriveFree), DashFormat.Size(s.SystemDriveTotal),
                DashFormat.Share((double)s.SystemDriveFree / s.SystemDriveTotal));
            var kind = s.SystemDisk is { } sd ? string.Join(" ", new[] { sd.BusType, sd.MediaType }.Where(x => x.Length > 0)) : "";
            var model = s.SystemDisk?.Model is { Length: > 0 } m ? m : SystemProfile.SystemDrive.TrimEnd('\\');
            SystemRows.Add(new SystemRow(Loc.T("СИСТЕМНЫЙ ДИСК"), model, kind.Length > 0 ? $"{kind} · {space}" : space));
        }

        var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        _uptimeRow = new SystemRow(Loc.T("ВРЕМЯ РАБОТЫ"), DashFormat.Duration(s.Uptime), Loc.Tf("запущена {0}", Loc.DateTimeText(boot)));
        SystemRows.Add(_uptimeRow);
    }

    private static string Fallback(string? s) => string.IsNullOrWhiteSpace(s) ? DashFormat.Missing : s.Trim();

    private static string CoresThreads(int cores, int threads) =>
        Loc.Tf("{0} · {1}", Loc.N(cores, "{0} ядро", "{0} ядра", "{0} ядер"), Loc.N(threads, "{0} поток", "{0} потока", "{0} потоков"));

}
