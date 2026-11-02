using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Modules.Tools.DpcLatency;
using Lycoris.Shell;
using Lycoris.Tweaks;
using Lycoris.UI;

namespace Lycoris.Modules.Tools;

public sealed partial class ToolsViewModel : PageViewModel
{
    private const int MaxConsoleLines = 4000;
    private const string ChkdskOfflineCommand = "chkdsk {0} /f";
    private static string IdleCommandText => Loc.T("> выберите инструмент слева");

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _clockTimer;
    private CancellationTokenSource? _cts;
    private DateTime _startedAt;

    public override string Key => "tools";
    public override string Index => "09";
    public override string Title => Loc.T("РЕМОНТ И ДИСКИ");
    public override string Subtitle => Loc.T("Проверка и восстановление Windows, диски, сеть и системные утилиты.");
    public override string Glyph => Glyphs.Repair;

    public string SystemDrive { get; } = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\').ToUpperInvariant();
    public string ChkdskCommandText => string.Format(ChkdskOfflineCommand, SystemDrive);

    [ObservableProperty] private IReadOnlyList<ToolSection> _sections = [];
    public IReadOnlyList<QuickLink> QuickLinks { get; }
    public ObservableCollection<ConsoleLine> ConsoleLines { get; } = new();

    // ── console state ──
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoCancelHint))]
    [NotifyPropertyChangedFor(nameof(ShowConsole))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(RunToolCommand), nameof(RepairWindowsCommand), nameof(RefreshCommand), nameof(CancelRunCommand), nameof(ClearConsoleCommand))]
    private bool _isRunning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoCancelHint)), NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    private bool _canCancelRun;
    public bool ShowNoCancelHint => IsRunning && !CanCancelRun;
    /// <summary>The console takes the right column only while a tool runs (and until the user closes its result).</summary>
    public bool ShowConsole => IsRunning || HasConsoleLines || HasResult;
    [ObservableProperty] private bool _cancelRequested;
    [ObservableProperty] private string _runTitle = Loc.T("НЕТ АКТИВНОЙ ОПЕРАЦИИ");
    [ObservableProperty] private string _runCommandText = IdleCommandText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RunStateIsActive), nameof(RunStateIsProblem), nameof(RunStateIsPlain))]
    private string _runState = Loc.Key("ОЖИДАНИЕ");
    /// <summary>Which status tag the console header shows (filled / hatched / muted — never white).</summary>
    public bool RunStateIsActive => RunState == Loc.Key("ВЫПОЛНЯЕТСЯ") || RunState == Loc.Key("ГОТОВО");
    public bool RunStateIsProblem => RunState == Loc.Key("ОШИБКА") || RunState == Loc.Key("ВНИМАНИЕ");
    public bool RunStateIsPlain => !RunStateIsActive && !RunStateIsProblem;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowConsole))] private bool _hasResult;
    [ObservableProperty] private bool _resultIsProblem;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private string _progressText = "—";
    [ObservableProperty] private string _elapsedText = "0:00";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowConsole))] private bool _hasConsoleLines;

    // ── disks ──
    /// <summary>Fixed volumes for the defrag card (one button per drive; free space is shown on the cleaning tab).</summary>
    private IReadOnlyList<VolumeInfo> _volumes = [];
    private bool _hasBattery;
    /// <summary>Modern Standby (S0 low power idle): the only platform where powercfg /sleepstudy has data.</summary>
    private bool _modernStandby;
    [ObservableProperty] private string _bootCheckText = Loc.T("Чтение параметров загрузки…");
    [ObservableProperty] private bool _bootCheckScheduled;

    public ToolsViewModel()
    {
        Sections = BuildSections();
        QuickLinks = BuildQuickLinks();

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _flushTimer.Tick += (_, _) => FlushPending();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ElapsedText = ToolOutput.FormatElapsed(DateTime.Now - _startedAt);
        ConsoleLines.CollectionChanged += (_, _) => HasConsoleLines = ConsoleLines.Count > 0;
    }

    protected override async Task LoadAsync()
    {
        var volumesTask = VolumeScanner.ScanAsync();
        var bootTask = ReadBootCheckAsync();
        var profile = await SystemProfile.LoadAsync();
        _hasBattery = profile.HasBattery;
        _modernStandby = await Task.Run(() => TweakCatalog.ReadPowerCaps()?.ModernStandby == true);
        _volumes = await volumesTask;
        Sections = BuildSections();
        await bootTask;
        MarkChecked();
    }

    /// <summary>«ОБНОВИТЬ»: re-reads the drives and the boot-time check state.</summary>
    [RelayCommand(CanExecute = nameof(CanRunPlain))]
    private async Task Refresh()
    {
        await ReadBootCheckAsync();
        var volumes = await VolumeScanner.ScanAsync();
        if (!IsRunning && !SameVolumes(volumes, _volumes))
        {
            _volumes = volumes;
            Sections = BuildSections();
        }
        MarkChecked();
    }

    protected override async Task OnReturnAsync()
    {
        if (IsRunning) return;
        await ReadBootCheckAsync();
        MarkChecked();
        // Drives may have been attached or removed meanwhile; rebuild the cards only when the set changed.
        var volumes = await VolumeScanner.ScanAsync();
        if (IsRunning || SameVolumes(volumes, _volumes)) return;
        _volumes = volumes;
        Sections = BuildSections();
    }

    internal static bool SameVolumes(IReadOnlyList<VolumeInfo> a, IReadOnlyList<VolumeInfo> b) =>
        a.Select(v => (v.Letter, v.MediaType)).SequenceEqual(b.Select(v => (v.Letter, v.MediaType)));

    /// <summary>Reads HKLM\…\Session Manager\BootExecute (read-only) to tell whether autochk will run at the next boot.</summary>
    private async Task ReadBootCheckAsync()
    {
        var entries = await Task.Run(() => RegistryHelper.Read(RegHive.HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager", "BootExecute") switch
        {
            string[] a => a,
            string s => [s],
            _ => Array.Empty<string>(),
        });
        BootCheckScheduled = ToolOutput.IsBootCheckScheduled(entries);
        BootCheckText = BootCheckScheduled
            ? Loc.T("Запланирована дополнительная проверка диска — она выполнится при следующей загрузке Windows.")
            : Loc.T("Не запланирована. В стандартном режиме Windows проверяет диск при загрузке, только если он помечен как повреждённый.");
    }

    // ═════════════════════════════ Commands ═════════════════════════════

    // Read-only mode does not disable the buttons: a click asks for rights and repeats the action after the relaunch.
    private bool CanRun(ToolCardAction? action) => action is not null && !IsRunning;

    /// <summary>The stable text id of a button (the admin intent carries it across the relaunch): «Sfc», «DefragVolume:C:».</summary>
    internal static string ActionId(ToolCardAction action) =>
        action.Op == ToolOp.DefragVolume && action.Volume is { } v ? $"{action.Op}:{v.Letter}" : action.Op.ToString();

    private ToolCardAction? FindAction(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Sections.SelectMany(s => s.Cards).SelectMany(c => c.Actions)
            .FirstOrDefault(a => string.Equals(ActionId(a), id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs a tool button by its text id — the command the admin-intent replay uses («RunTool»).</summary>
    [RelayCommand(CanExecute = nameof(CanRunById))]
    private Task RunTool(string? id) => FindAction(id) is { } action ? RunAsync(action) : Task.CompletedTask;

    private bool CanRunById(string? id) => !IsRunning && FindAction(id) is not null;

    /// <summary>The page's one main button: DISM restore, then SFC, with one confirmation listing both steps.</summary>
    [RelayCommand(CanExecute = nameof(CanRunPlain))]
    private async Task RepairWindows()
    {
        if (!RequireAdmin(Loc.T("Проверка и восстановление Windows"), "RepairWindows")) return;
        if (!await Ui.ConfirmAsync(
                Loc.T("Проверить и восстановить Windows?"),
                Loc.T("Lycoris выполнит два штатных шага Windows один за другим: DISM восстановит хранилище компонентов, затем SFC сверит и заменит повреждённые системные файлы."),
                [
                    Loc.T("Шаг 1: DISM /Online /Cleanup-Image /RestoreHealth — 10–30 минут, нужен интернет (Центр обновления Windows)"),
                    Loc.T("Шаг 2: sfc /scannow — 5–20 минут, на HDD дольше"),
                    Loc.T("Личные файлы, программы и настройки не затрагиваются; заменяются только оригинальные файлы Microsoft"),
                    Loc.T("Прерывать нельзя: дождитесь окончания. Если шаг 1 завершится с ошибкой, шаг 2 не запустится"),
                ],
                confirmText: Loc.T("ЗАПУСТИТЬ"), footnote: Loc.T("Журнал SFC: %WINDIR%\\Logs\\CBS\\CBS.log")))
            return;
        await RunDismAsync(ToolOutput.DismMode.RestoreHealth, confirmed: true);
        if (_lastStatus is not ToolStatus.Success)
        {
            Ui.Toast(Loc.T("Шаг 1 не завершился успешно — проверка системных файлов не запущена."), ToastKind.Warning, Loc.T("ПРОВЕРИТЬ И ВОССТАНОВИТЬ WINDOWS"));
            return;
        }
        await RunSfcAsync(confirmed: true);
    }

    private bool CanRunPlain() => !IsRunning;
    // (RefreshCommand uses CanRunPlain too: notified together with the run commands.)

    private ToolStatus? _lastStatus;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync(ToolCardAction? action)
    {
        if (action is null) return;
        if (action.RequiresAdmin && !RequireAdmin(action.Label, "RunTool", ActionId(action))) return;
        switch (action.Op)
        {
            case ToolOp.DismCheck: await RunDismAsync(ToolOutput.DismMode.CheckHealth); break;
            case ToolOp.DismScan: await RunDismAsync(ToolOutput.DismMode.ScanHealth); break;
            case ToolOp.DismRestore: await RunDismAsync(ToolOutput.DismMode.RestoreHealth); break;
            case ToolOp.Sfc: await RunSfcAsync(); break;
            case ToolOp.ChkdskScan: await RunChkdskAsync(); break;
            case ToolOp.ChkntfsQuery: await RunChkntfsAsync(); break;
            case ToolOp.CopyChkdskCommand: CopyToClipboard(ChkdskCommandText, Loc.T("Команда скопирована")); break;
            case ToolOp.DefragAll: await RunDefragAsync(_volumes); break;
            case ToolOp.DefragVolume when action.Volume is { } volume: await RunDefragAsync([volume]); break;
            case ToolOp.FlushDns: await RunFlushDnsAsync(); break;
            case ToolOp.NetworkReset: await RunNetworkResetAsync(); break;
            case ToolOp.StoreReset: await RunStoreResetAsync(); break;
            case ToolOp.IconCache: await RunIconCacheAsync(); break;
            case ToolOp.BatteryReport: await RunPowerReportAsync(BatteryReport); break;
            case ToolOp.MemoryDiagnostic: await RunMemoryDiagnosticAsync(); break;
            case ToolOp.SleepWakeDiagnostics: await RunSleepWakeDiagnosticsAsync(); break;
            case ToolOp.EnergyReport: await RunEnergyReportAsync(); break;
            case ToolOp.SleepStudyReport: await RunPowerReportAsync(SleepStudyReport); break;
            case ToolOp.SystemSleepDiagnostics: await RunPowerReportAsync(SleepDiagnosticsReport); break;
            case ToolOp.DpcLatencyTest: await RunDpcLatencyAsync(); break;
        }
    }

    private bool CanCancel() => IsRunning && CanCancelRun && !CancelRequested;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelRun()
    {
        if (_cts is null) return;
        CancelRequested = true;
        CancelRunCommand.NotifyCanExecuteChanged();
        Emit(Loc.T("Отмена… процесс будет остановлен."), ConsoleLineKind.Info);
        _cts.Cancel();
    }

    [RelayCommand]
    private void CopyOutput()
    {
        if (ConsoleLines.Count == 0)
        {
            Ui.Toast(Loc.T("Консоль пуста — копировать нечего."), ToastKind.Info);
            return;
        }
        var sb = new StringBuilder();
        foreach (var l in ConsoleLines)
            sb.Append(l.Time).Append("  ").Append(l.Marker.Length > 0 ? l.Marker + " " : "").AppendLine(l.Text);
        CopyToClipboard(sb.ToString(), Loc.T("Вывод консоли скопирован"));
    }

    private bool CanClear() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void ClearConsole()
    {
        ConsoleLines.Clear();
        HasResult = false;
        ResultText = null;
        HasProgress = false;
        Progress = 0;
        ProgressText = "—";
        ElapsedText = "0:00";
        RunState = Loc.Key("ОЖИДАНИЕ");
        RunTitle = Loc.T("НЕТ АКТИВНОЙ ОПЕРАЦИИ");
        RunCommandText = IdleCommandText;
    }

    [RelayCommand]
    private void OpenQuickLink(QuickLink? link)
    {
        if (link is null) return;
        ShellLauncher.Open(link.Target, link.Arguments, link.Title);
    }

    // ═════════════════════════════ Tools ═════════════════════════════

    private async Task RunDismAsync(ToolOutput.DismMode mode, bool confirmed = false)
    {
        if (!CanStartNow(OperationClass.SystemRepair, Loc.T("Образ Windows (DISM)"))) return;
        var (arg, title, cancellable) = mode switch
        {
            ToolOutput.DismMode.CheckHealth => ("/CheckHealth", Loc.T("ОБРАЗ WINDOWS · БЫСТРАЯ ПРОВЕРКА"), true),
            ToolOutput.DismMode.ScanHealth => ("/ScanHealth", Loc.T("ОБРАЗ WINDOWS · СКАНИРОВАНИЕ"), true),
            _ => ("/RestoreHealth", Loc.T("ОБРАЗ WINDOWS · ВОССТАНОВЛЕНИЕ"), false),
        };
        var display = $"DISM /Online /Cleanup-Image {arg}";

        if (!confirmed && mode == ToolOutput.DismMode.RestoreHealth && !await Ui.ConfirmAsync(
                Loc.T("Восстановить образ Windows?"),
                Loc.T("DISM сверит хранилище компонентов Windows с эталоном и заменит повреждённые компоненты исправными, скачав их через Центр обновления Windows."),
                [
                    Loc.Tf("Команда: {0}", display),
                    Loc.T("Длительность: обычно 10–30 минут, индикатор может подолгу стоять на 62% — это нормально"),
                    Loc.T("Нужен интернет: источник исправных компонентов — Windows Update"),
                    Loc.T("Личные файлы, программы и настройки не затрагиваются"),
                    Loc.T("Прерывать нельзя: остановка посреди восстановления может оставить хранилище в незавершённом состоянии"),
                ],
                confirmText: Loc.T("ЗАПУСТИТЬ"), footnote: Loc.T("После завершения запустите «Проверку системных файлов».")))
            return;

        await ExecuteAsync(title, display, cancellable, Loc.T("Восстановление образа Windows…"), async ct =>
        {
            var r = await RunProcessAsync(ProcessRunner.Sys("dism.exe"), ["/Online", "/Cleanup-Image", arg], OutputEncoding.Oem,
                cancellable ? TimeSpan.FromHours(2) : null, ct);
            return r.Cancelled ? null : ToolOutput.ClassifyDism(mode, r.ExitCode, r.Combined);
        });
    }

    private async Task RunSfcAsync(bool confirmed = false)
    {
        if (!CanStartNow(OperationClass.SystemRepair, Loc.T("Проверка системных файлов (SFC)"))) return;
        if (!confirmed && !await Ui.ConfirmAsync(
                Loc.T("Проверить системные файлы?"),
                Loc.T("SFC сверит все защищённые системные файлы Windows с эталонными копиями и заменит повреждённые."),
                [
                    Loc.T("Команда: sfc /scannow"),
                    Loc.T("Длительность: 5–20 минут, на HDD дольше"),
                    Loc.T("Заменяются только оригинальные файлы Microsoft — личные данные и программы не затрагиваются"),
                    Loc.T("Прерывать нельзя: дождитесь 100%"),
                    Loc.T("Если найдены неисправимые файлы — сначала «Восстановление образа» (DISM), затем повторите проверку"),
                ],
                confirmText: Loc.T("ЗАПУСТИТЬ"), footnote: Loc.T("Журнал проверки: %WINDIR%\\Logs\\CBS\\CBS.log")))
            return;

        await ExecuteAsync(Loc.T("ПРОВЕРКА СИСТЕМНЫХ ФАЙЛОВ"), "sfc /scannow", cancellable: false, Loc.T("Проверка системных файлов…"), async ct =>
        {
            var r = await RunProcessAsync(ProcessRunner.Sys("sfc.exe"), ["/scannow"], OutputEncoding.Unicode, null, ct);
            return ToolOutput.ClassifySfc(r.ExitCode, r.Combined);
        });
    }

    private Task RunChkdskAsync() =>
        ExecuteAsync(Loc.Tf("ПРОВЕРКА ДИСКА {0}", SystemDrive), $"chkdsk {SystemDrive} /scan", cancellable: true, Loc.T("Проверка диска…"), async ct =>
        {
            var r = await RunProcessAsync(ProcessRunner.Sys("chkdsk.exe"), [SystemDrive, "/scan"], OutputEncoding.Oem, TimeSpan.FromHours(3), ct);
            if (r.Cancelled) return null;
            var outcome = ToolOutput.ClassifyChkdsk(r.ExitCode, r.Combined);
            await ReadBootCheckAsync();
            return outcome;
        });

    private Task RunChkntfsAsync() =>
        ExecuteMeasureAsync(Loc.Tf("СОСТОЯНИЕ ДИСКА {0}", SystemDrive), $"chkntfs {SystemDrive}", cancellable: true, Loc.T("Чтение состояния диска…"), async ct =>
        {
            var r = await RunProcessAsync(ProcessRunner.Sys("chkntfs.exe"), [SystemDrive], OutputEncoding.Oem, TimeSpan.FromMinutes(1), ct);
            if (r.Cancelled) return null;
            await ReadBootCheckAsync();
            return ToolOutput.ClassifyChkntfs(r.ExitCode, r.Combined, SystemDrive);
        });

    private async Task RunDefragAsync(IReadOnlyList<VolumeInfo> volumes)
    {
        if (!CanStartNow(OperationClass.SystemRepair, Loc.T("Оптимизация дисков"))) return;
        if (volumes.Count == 0)
        {
            Ui.Toast(Loc.T("Не найдено дисков, которые можно оптимизировать."), ToastKind.Warning);
            return;
        }
        var title = volumes.Count == 1 ? Loc.Tf("ОПТИМИЗАЦИЯ ДИСКА {0}", volumes[0].Letter) : Loc.Tf("ОПТИМИЗАЦИЯ ДИСКОВ · {0}", volumes.Count);
        var display = volumes.Count == 1 ? $"defrag {volumes[0].Letter} /O /U /V" : $"defrag <{string.Join(" ", volumes.Select(v => v.Letter))}> /O /U /V";

        await ExecuteAsync(title, display, cancellable: true, Loc.T("Оптимизация дисков…"), async ct =>
        {
            int ok = 0, failed = 0;
            var errors = new List<string>();
            foreach (var v in volumes)
            {
                ct.ThrowIfCancellationRequested();
                Emit(Loc.Tf("{0} {1} — {2}: {3}", v.Letter, v.DisplayName, v.MediaText, v.IsSsd ? "TRIM" : v.IsHdd ? Loc.T("дефрагментация") : Loc.T("Windows выберет операцию сама")), ConsoleLineKind.Info);
                ResetProgress();
                var r = await RunProcessAsync(ProcessRunner.Sys("defrag.exe"), [v.Letter, "/O", "/U", "/V"], OutputEncoding.Oem, TimeSpan.FromHours(8), ct);
                if (r.Cancelled) return null;
                if (r.ExitCode == 0) ok++;
                else
                {
                    failed++;
                    errors.Add(Loc.Tf("{0} (код {1})", v.Letter, r.ExitCode));
                }
            }
            return failed == 0
                ? ToolOutcome.Ok(volumes.Count == 1 ? Loc.Tf("Диск {0} оптимизирован.", volumes[0].Letter) : Loc.Tf("Оптимизировано дисков: {0}.", ok))
                : ToolOutcome.Fail(Loc.Tf("Оптимизировано: {0}, с ошибкой: {1}.", ok, string.Join(", ", errors)));
        });
    }

    private Task RunFlushDnsAsync() =>
        ExecuteAsync(Loc.T("СБРОС DNS-КЭША"), "ipconfig /flushdns", cancellable: false, Loc.T("Сброс DNS-кэша…"), async ct =>
        {
            var r = await RunProcessAsync(ProcessRunner.Sys("ipconfig.exe"), ["/flushdns"], OutputEncoding.Oem, TimeSpan.FromMinutes(1), ct);
            return ToolOutput.ClassifyExit(r.ExitCode, Loc.T("Кэш DNS очищен. Имена сайтов будут заново запрошены у DNS-сервера."), Loc.T("Не удалось очистить кэш DNS"));
        });

    private async Task RunNetworkResetAsync()
    {
        if (!CanStartNow(OperationClass.SystemRepair, Loc.T("Сброс сетевых настроек"))) return;
        var restoreNote = AppSettings.Current.CreateRestorePoints
            ? Loc.T("Перед сбросом будет создана точка восстановления Windows — только через неё можно вернуть прежние сетевые параметры")
            : Loc.T("Создание точек восстановления выключено в разделе «Восстановление» — вернуть прежние параметры будет нельзя");
        if (!await Ui.ConfirmAsync(
                Loc.T("Сбросить сетевые настройки?"),
                Loc.T("Каталог Winsock и стек TCP/IP вернутся к состоянию чистой установки Windows. Это крайняя мера, когда сеть не работает и другие способы не помогли."),
                [
                    Loc.T("netsh winsock reset — сброс каталога Winsock; сторонние сетевые модули (VPN, прокси, сетевые фильтры антивирусов) отключатся"),
                    Loc.T("netsh int ip reset — сброс параметров TCP/IP; вручную заданные IP-адреса, шлюзы и DNS-серверы будут потеряны — запишите их заранее"),
                    Loc.T("VPN-клиенты и сетевое ПО может понадобиться переустановить"),
                    Loc.T("Потребуется перезагрузка компьютера"),
                    restoreNote,
                    Loc.T("Копии разделов реестра Winsock и TCP/IP сохраняются в папку резервных копий Lycoris"),
                ],
                confirmText: Loc.T("СБРОСИТЬ"), danger: true, footnote: Loc.T("Кнопкой «Откатить» в журнале это действие не отменяется.")))
            return;

        await ExecuteAsync(Loc.T("СБРОС СЕТЕВЫХ НАСТРОЕК"), "netsh winsock reset · netsh int ip reset", cancellable: false, Loc.T("Сброс сетевых настроек…"), async ct =>
        {
            RestorePointResult? rp = null;
            if (AppSettings.Current.CreateRestorePoints)
            {
                Emit(Loc.T("Создание точки восстановления…"), ConsoleLineKind.Info);
                rp = await RestorePointService.CreateAsync("Lycoris: сброс сетевых настроек"); // loc:ignore the restore point name is stored by Windows
                Emit(rp.Message, rp.Created ? ConsoleLineKind.Success : ConsoleLineKind.Info);
                if (rp.Outcome is RestorePointOutcome.Disabled or RestorePointOutcome.Failed &&
                    !await AskWithoutLeaseAsync(() => Ui.ConfirmAsync(Loc.T("Продолжить без точки восстановления?"), rp.Message,
                        [Loc.T("Без точки восстановления прежние сетевые параметры вернуть будет нельзя")],
                        confirmText: Loc.T("ПРОДОЛЖИТЬ"), danger: true)))
                    return ToolOutcome.Cancel(Loc.T("Сброс отменён: точка восстановления не создана."));
            }

            var session = await ChangeSession.BeginAsync("Сброс сетевых настроек", createRestorePoint: false); // loc:ignore the journal title stays Russian (translated where shown)
            session.Batch.RestorePoint = rp?.Message ?? "Создание точек восстановления выключено в разделе «Восстановление»."; // loc:ignore stored in the journal
            ChangeJournal.Instance.UpdateBatch(session.Batch);

            AppPaths.TryEnsureDir(session.BackupDir);
            Emit(Loc.Tf("Резервные копии реестра → {0}", session.BackupDir), ConsoleLineKind.Info);
            await RegistryHelper.ExportKeyAsync(RegHive.HKLM, @"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters",
                Path.Combine(session.BackupDir, "WinSock2_Parameters.reg"));
            await RegistryHelper.ExportKeyAsync(RegHive.HKLM, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                Path.Combine(session.BackupDir, "Tcpip_Parameters.reg"));

            var netsh = ProcessRunner.Sys("netsh.exe");
            var winsock = await RunProcessAsync(netsh, ["winsock", "reset"], OutputEncoding.Oem, TimeSpan.FromMinutes(3), ct);
            var ip = await RunProcessAsync(netsh, ["int", "ip", "reset", Path.Combine(session.BackupDir, "netsh-int-ip-reset.log")],
                OutputEncoding.Oem, TimeSpan.FromMinutes(3), ct);

            if (winsock.ExitCode == 0 && ip.ExitCode == 0)
            {
                Log.Success("Сетевые настройки сброшены. Требуется перезагрузка.");
                return ToolOutcome.Warn(Loc.T("Сетевые настройки сброшены. Перезагрузите компьютер, чтобы изменения вступили в силу."));
            }
            return ToolOutcome.Fail(Loc.Tf("Сброс выполнен не полностью: winsock — код {0}, TCP/IP — код {1}. Если часть параметров сбросить не удалось, перезагрузитесь и повторите.", winsock.ExitCode, ip.ExitCode));
        });
    }

    private Task RunStoreResetAsync() =>
        ExecuteAsync(Loc.T("СБРОС КЭША MICROSOFT STORE"), "wsreset.exe", cancellable: true, Loc.T("Сброс кэша Microsoft Store…"), async ct =>
        {
            var exe = ProcessRunner.Sys("WSReset.exe");
            if (!File.Exists(exe)) return ToolOutcome.Fail(Loc.T("WSReset.exe не найден — Microsoft Store в этой системе отсутствует."));
            var r = await RunProcessAsync(exe, [], OutputEncoding.Oem, TimeSpan.FromMinutes(3), ct);
            if (r.Cancelled) return null;
            return ToolOutput.ClassifyExit(r.ExitCode, Loc.T("Кэш Microsoft Store очищен. Store откроется автоматически."), Loc.T("Не удалось сбросить кэш Store"));
        });

    private Task RunIconCacheAsync() =>
        ExecuteAsync(Loc.T("ПЕРЕСОЗДАНИЕ КЭША ЗНАЧКОВ"), "ie4uinit.exe -show", cancellable: true, Loc.T("Обновление кэша значков…"), async ct =>
        {
            var exe = ProcessRunner.Sys("ie4uinit.exe");
            if (!File.Exists(exe)) return ToolOutcome.Fail(Loc.T("ie4uinit.exe не найден в System32."));
            var r = await RunProcessAsync(exe, ["-show"], OutputEncoding.Oem, TimeSpan.FromMinutes(1), ct);
            if (r.Cancelled) return null;
            return ToolOutput.ClassifyExit(r.ExitCode,
                Loc.T("Кэш значков обновлён. Если значки не изменились — перезайдите в систему."), Loc.T("Не удалось обновить кэш значков"));
        });

    // ═════════════════════════════ Power & sleep (read-only) ═════════════════════════════

    /// <summary>
    /// A powercfg HTML report: written to the Lycoris logs folder and opened without administrator rights,
    /// exactly like the battery report. <see cref="NonZeroExitMeansProblemsFound"/>: powercfg /energy exits non-zero when
    /// the report lists problems (the code's exact meaning is undocumented, so it is shown as is and never as a count) —
    /// with a created report that is a warning, not a failure.
    /// </summary>
    internal sealed record PowerReportSpec(string Title, string Display, string Busy, string Name, string FileName,
        Func<string, string[]> Arguments, TimeSpan Timeout, bool NonZeroExitMeansProblemsFound = false);

    internal static readonly PowerReportSpec BatteryReport = new(Loc.Key("ОТЧЁТ О БАТАРЕЕ"), "powercfg /batteryreport",
        Loc.Key("Создание отчёта о батарее…"), Loc.Key("Отчёт о батарее"), "battery-report.html",
        file => ["/batteryreport", "/output", file], TimeSpan.FromMinutes(2));

    internal static readonly PowerReportSpec EnergyReport = new(Loc.Key("ОТЧЁТ ОБ ЭНЕРГОЭФФЕКТИВНОСТИ"), "powercfg /energy /duration 60",
        Loc.Key("Отчёт об энергоэффективности: 60 секунд наблюдения…"), Loc.Key("Отчёт об энергоэффективности"), "energy-report.html",
        file => ["/energy", "/output", file, "/duration", "60"], TimeSpan.FromMinutes(3), NonZeroExitMeansProblemsFound: true);

    internal static readonly PowerReportSpec SleepStudyReport = new(Loc.Key("ОТЧЁТ SLEEPSTUDY"), "powercfg /sleepstudy /duration 3",
        Loc.Key("Создание отчёта SleepStudy…"), Loc.Key("Отчёт SleepStudy"), "sleepstudy-report.html",
        file => ["/sleepstudy", "/output", file, "/duration", "3"], TimeSpan.FromMinutes(2));

    internal static readonly PowerReportSpec SleepDiagnosticsReport = new(Loc.Key("ДИАГНОСТИКА ПЕРЕХОДОВ В СОН"), "powercfg /systemsleepdiagnostics",
        Loc.Key("Диагностика переходов в сон…"), Loc.Key("Отчёт о переходах в сон"), "sleep-diagnostics.html",
        file => ["/systemsleepdiagnostics", "/output", file], TimeSpan.FromMinutes(2));

    /// <summary>The five documented read-only powercfg queries of the «Сон и пробуждение» card, in run order.</summary>
    internal static readonly IReadOnlyList<PowercfgQuery> SleepWakeQueries =
    [
        new("/lastwake", Loc.Key("Почему ПК проснулся в последний раз"), RequiresAdmin: false),
        new("/waketimers", Loc.Key("Активные таймеры пробуждения (программы и задачи)"), RequiresAdmin: true),
        new("/requests", Loc.Key("Запросы, которые не дают ПК уснуть"), RequiresAdmin: true),
        new("/devicequery wake_armed", Loc.Key("Устройства, которым разрешено будить ПК"), RequiresAdmin: false),
        new("/availablesleepstates", Loc.Key("Доступные состояния сна"), RequiresAdmin: false),
    ];

    internal static string SleepWakeCommandText { get; } = "powercfg " + string.Join(" · ", SleepWakeQueries.Select(q => q.Arguments));

    /// <summary>Id of the Optimize tweak the «Сон и пробуждение» note points to (TweakCatalog.Power.cs).</summary>
    internal const string WakeTimersTweakId = "wake-timers-important-only";

    /// <summary>
    /// The card note names the Optimize item by its catalog title, so the text can never drift from the tweak it sends the
    /// user to; if that tweak is ever removed, the sentence about it disappears instead of pointing nowhere.
    /// </summary>
    internal static string SleepWakeNote()
    {
        var tweakTitle = TweakCatalog.Find(WakeTimersTweakId)?.Title;
        var tweakHint = tweakTitle is null
            ? ""
            : " " + Loc.Tf("Чтобы ПК не просыпался по расписанию программ, есть пункт «{0}» в разделе «Оптимизация».", tweakTitle);
        return Loc.T("Ничего не меняет. Чтобы устройство не будило ПК: Диспетчер устройств → устройство → Управление электропитанием → снять «Разрешить этому устройству выводить компьютер из ждущего режима».") + tweakHint +
               " " + Loc.T("Без прав администратора /requests и /waketimers пропускаются.");
    }

    private const string MaintenanceTaskPath = @"\Microsoft\Windows\TaskScheduler\Maintenance Configurator";
    private const string MaintenancePolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\Task Scheduler\Maintenance";
    private const string MaintenanceSettingKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";

    private Task RunSleepWakeDiagnosticsAsync() =>
        ExecuteMeasureAsync(Loc.T("СОН И ПРОБУЖДЕНИЕ"), SleepWakeCommandText, cancellable: true, Loc.T("Проверка сна и пробуждения…"), async ct =>
        {
            var elevated = Elevation.IsElevated;
            if (!elevated)
                Emit(Loc.T("/requests и /waketimers требуют прав администратора — эти две проверки пропущены, остальные выполняются."), ConsoleLineKind.Info);

            var failed = 0;
            foreach (var q in SleepWakeQueries)
            {
                if (q.RequiresAdmin && !elevated) continue;
                ct.ThrowIfCancellationRequested();
                Emit("", ConsoleLineKind.Output);
                Emit(Loc.T(q.Title), ConsoleLineKind.Info);
                Emit("powercfg " + q.Arguments, ConsoleLineKind.Command);
                // Output is shown as is (OEM encoding); localized text is never parsed.
                var r = await RunProcessAsync(ProcessRunner.Sys("powercfg.exe"), q.Arguments.Split(' '), OutputEncoding.Oem, TimeSpan.FromMinutes(1), ct);
                if (r.Cancelled) return null;
                if (r.ExitCode != 0)
                {
                    failed++;
                    Emit(Loc.Tf("powercfg завершился с кодом {0}.", r.ExitCode), ConsoleLineKind.Info);
                }
            }

            Emit("", ConsoleLineKind.Output);
            Emit(await Task.Run(ReadMaintenanceWake), ConsoleLineKind.Info);
            return failed == 0
                ? ToolOutcome.Ok(Loc.T("Проверка завершена — см. вывод выше."))
                : ToolOutcome.Warn(Loc.Tf("Проверка завершена, команд с ошибкой: {0} — см. вывод выше.", failed));
        });

    /// <summary>Read-only: may Automatic Maintenance wake the PC? Group Policy wins over the Task Scheduler setting.</summary>
    private static string ReadMaintenanceWake()
    {
        var task = ScheduledTasks.Query(MaintenanceTaskPath);
        var policy = RegistryHelper.ReadInt(RegHive.HKLM, MaintenancePolicyKey, "WakeUp");
        var setting = RegistryHelper.ReadInt(RegHive.HKLM, MaintenanceSettingKey, "WakeUp");
        return DescribeMaintenanceWake(task.Exists, task.Enabled, policy, setting);
    }

    internal static string DescribeMaintenanceWake(bool taskExists, bool taskEnabled, int? policy, int? setting)
    {
        var (allowed, source) = policy is { } p ? (p != 0, Loc.T("политика"))
            : setting is { } v ? (v != 0, Loc.T("настройка"))
            : (true, Loc.T("значение не задано — умолчание Windows"));
        var task = !taskExists ? Loc.T("задача «Maintenance Configurator» не найдена")
            : taskEnabled ? Loc.T("задача «Maintenance Configurator» включена")
            : Loc.T("задача «Maintenance Configurator» отключена");
        return Loc.Tf("Пробуждение для обслуживания: {0} ({1}); {2}.", allowed ? Loc.T("разрешено") : Loc.T("запрещено"), source, task);
    }

    private async Task RunEnergyReportAsync()
    {
        if (!CanStartNow(OperationClass.Measure, Loc.T("Отчёт об энергоэффективности"), quiet: true)) return;
        if (GameBoostState.IsActive)
        {
            Ui.Toast(Loc.T("Дождитесь окончания игрового сеанса — отчёт энергоэффективности снимается при простое ПК."), ToastKind.Warning);
            return;
        }
        if (!await Ui.ConfirmAsync(
                Loc.T("Снять отчёт об энергоэффективности?"),
                Loc.T("powercfg /energy 60 секунд наблюдает за системой и составляет HTML-отчёт Windows: что мешает простою и USB-сну, кто запрашивает повышенное разрешение таймера, какова нагрузка в простое."),
                [
                    Loc.T("60 секунд ПК должен простаивать: закройте программы и не трогайте мышь и клавиатуру"),
                    Loc.T("Команда: powercfg /energy /output … /duration 60"),
                    Loc.T("Ничего не меняет в системе — только наблюдение"),
                    Loc.T("Отчёт сохраняется в папку журналов Lycoris и открывается в браузере без прав администратора"),
                ],
                confirmText: Loc.T("ЗАПУСТИТЬ"), footnote: Loc.T("«Ошибки» в отчёте — это причины повышенного расхода энергии, а не поломки.")))
            return;
        await RunPowerReportAsync(EnergyReport);
    }

    private Task RunPowerReportAsync(PowerReportSpec spec) =>
        ExecuteQuietAsync(Loc.T(spec.Title), spec.Display, cancellable: true, Loc.T(spec.Busy), async ct =>
        {
            if (!AppPaths.TryEnsureDir(AppPaths.LogsDir)) return ToolOutcome.Fail(Loc.Tf("Не удалось создать папку для отчёта: {0}", AppPaths.LogsDir));
            var file = Path.Combine(AppPaths.LogsDir, spec.FileName);
            TryDeleteStaleReport(file); // an older report must never pass for the new one
            var r = await RunProcessAsync(ProcessRunner.Sys("powercfg.exe"), spec.Arguments(file), OutputEncoding.Oem, spec.Timeout, ct);
            if (r.Cancelled) return null;
            var exists = File.Exists(file);
            if (exists) Emit(Loc.Tf("Отчёт: {0}", file), ConsoleLineKind.Info);
            // The browser must never inherit Lycoris's administrator rights.
            var opened = exists && await UnelevatedShell.OpenFileAsync(file);
            return ClassifyPowerReport(spec, r.ExitCode, exists, opened, file, Elevation.IsElevated);
        });

    internal static ToolOutcome ClassifyPowerReport(PowerReportSpec spec, int exitCode, bool fileExists, bool opened, string file, bool elevated)
    {
        if (!fileExists)
            return ToolOutcome.Fail(elevated || exitCode == 0
                ? Loc.Tf("Отчёт не создан (код {0}).", exitCode)
                : Loc.Tf("Отчёт не создан (код {0}) — скорее всего, нужны права администратора.", exitCode));
        var openNote = opened
            ? Loc.T("открыт в браузере.")
            : Loc.Tf("открыть его без прав администратора не удалось. Откройте файл вручную: {0}", file);
        var name = Loc.T(spec.Name);
        if (exitCode != 0 && spec.NonZeroExitMeansProblemsFound)
            return ToolOutcome.Warn(Loc.Tf("Найдены проблемы энергоэффективности — см. отчёт (powercfg вернул код {0}). Отчёт создан, {1}", exitCode, openNote));
        if (exitCode != 0)
            return ToolOutcome.Warn(Loc.Tf("{0} создан, но powercfg вернул код {1} — отчёт может быть неполным; {2}", name, exitCode, openNote));
        return opened
            ? ToolOutcome.Ok(Loc.Tf("{0} создан и открыт в браузере.", name))
            : ToolOutcome.Warn(Loc.Tf("{0} создан, но {1}", name, openNote));
    }

    private static void TryDeleteStaleReport(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Warn("Старый отчёт не удалён ({0}): {1}", file, ex.Message);
        }
    }

    private async Task RunMemoryDiagnosticAsync()
    {
        if (!await Ui.ConfirmAsync(
                Loc.T("Запустить диагностику памяти?"),
                Loc.T("Откроется средство проверки памяти Windows. Сам тест выполняется при перезагрузке, до входа в систему."),
                [
                    Loc.T("В окне Windows выберите: «Выполнить перезагрузку и проверку» или «Выполнить проверку при следующем включении»"),
                    Loc.T("Перед перезагрузкой сохраните открытые документы"),
                    Loc.T("Стандартный тест занимает 15–60 минут в зависимости от объёма ОЗУ"),
                    Loc.T("Результат: «Просмотр событий» → журнал «Система» → источник MemoryDiagnostics-Results"),
                ],
                confirmText: Loc.T("ОТКРЫТЬ")))
            return;

        var exe = ProcessRunner.Sys("MdSched.exe");
        if (!File.Exists(exe))
        {
            Ui.Toast(Loc.T("Средство проверки памяти Windows (MdSched.exe) не найдено."), ToastKind.Error);
            return;
        }
        if (ShellLauncher.Open(exe, displayName: Loc.T("Диагностика памяти Windows")))
        {
            Emit(Loc.T("Открыто средство проверки памяти Windows — выберите вариант перезагрузки в его окне."), ConsoleLineKind.Info);
            Ui.Toast(Loc.T("Выберите вариант перезагрузки в окне «Средство проверки памяти Windows»."), ToastKind.Info);
        }
    }

    // ═════════════════════════════ DPC/ISR latency (read-only) ═════════════════════════════

    internal static readonly TimeSpan DpcDuration = TimeSpan.FromSeconds(60);
    internal static string DpcCommandText => Loc.T("ETW PerfInfo: DPC + ISR · 60 с");
    internal static string DpcCardTitle => Loc.T("ЗАДЕРЖКИ ДРАЙВЕРОВ (DPC/ISR)");

    /// <summary>
    /// 60-second private ETW kernel session (EtwSession): DPC/ISR durations per driver module. Read-only, admin only,
    /// never during a game session — a game would both distort the result and suffer from the extra interrupt logging.
    /// </summary>
    private async Task RunDpcLatencyAsync()
    {
        if (!CanStartNow(OperationClass.Measure, Loc.T("Измерение задержек драйверов"), quiet: true)) return;
        if (GameBoostState.IsActive)
        {
            Ui.Toast(Loc.T("Дождитесь окончания игрового сеанса — измерение задержек драйверов во время игры не запускается."), ToastKind.Warning);
            return;
        }
        if (!EtwSession.IsSupported)
        {
            Ui.Toast(Loc.T("Измерение доступно начиная с Windows 8."), ToastKind.Warning);
            return;
        }
        if (!await Ui.ConfirmAsync(
                Loc.T("Измерить задержки драйверов?"),
                Loc.T("60 секунд обычной работы: можно открыть браузер или видео, но не игру. Lycoris откроет собственный сеанс трассировки ядра и посчитает, сколько времени каждый драйвер проводит в прерываниях (ISR) и отложенных вызовах (DPC)."),
                [
                    Loc.T("Длительность: ровно 60 секунд, можно прервать"),
                    Loc.T("Ничего не меняет: трассировка только читает события ядра, файл не записывается"),
                    Loc.T("Нагрузка небольшая, но не нулевая — не запускайте игры и тесты производительности во время измерения"),
                    Loc.T("Результат: таблица драйверов с самыми долгими вызовами и подсказка, куда смотреть дальше"),
                ],
                confirmText: Loc.T("ИЗМЕРИТЬ"), footnote: Loc.T("Пороги (500 и 1000 мкс) — практика сообщества LatencyMon, не стандарт Microsoft.")))
            return;

        await ExecuteQuietAsync(DpcCardTitle, DpcCommandText, cancellable: true, Loc.T("Измерение задержек драйверов…"), async ct =>
        {
            try
            {
                return await MeasureDpcAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ToolOutcome.Cancel(DpcCancelledText);
            }
        });
    }

    internal static string DpcCancelledText => Loc.T("Измерение отменено — ничего не изменено, сеанс трассировки остановлен.");
    internal static string DpcGameStoppedText =>
        Loc.T("Измерение остановлено: начался игровой сеанс. Ничего не изменено; неполные данные не оцениваются — повторите после игры.");

    private async Task<ToolOutcome> MeasureDpcAsync(CancellationToken ct)
    {
        var modules = await Task.Run(KernelModules.Load, ct);
        Emit(modules.DebugPrivilege ? Loc.Tf("Модулей ядра: {0}.", modules.Modules.Count) : Loc.Tf("Модулей ядра: {0} (без SeDebugPrivilege).", modules.Modules.Count), ConsoleLineKind.Info);
        if (modules.Note is not null) Emit(modules.Note, ConsoleLineKind.Info);

        var aggregator = new DpcAggregator(new ModuleMap(modules.Modules));
        var result = await Task.Run(() =>
        {
            var stale = EtwSession.StopStaleSessions();
            if (stale > 0) _pending.Enqueue(Loc.Tf("Остановлено незавершённых сеансов Lycoris: {0}.", stale));
            return EtwSession.Run(DpcDuration, aggregator.Add,
                percent => _pending.Enqueue(Loc.Tf("Измерение {0}% завершено.", percent)), ct);
        }, ct);

        if (!result.Started) return ToolOutcome.Fail(Loc.Tf("Измерение не запущено: {0}", result.Error));
        if (result.StoppedForGame) return ToolOutcome.Cancel(DpcGameStoppedText);
        if (result.Cancelled) return ToolOutcome.Cancel(DpcCancelledText);
        if (result.EventsLost > 0 || result.BuffersLost > 0)
            Emit(Loc.Tf("Потеряно событий: {0}, буферов: {1} — результат может быть неполным.", result.EventsLost, result.BuffersLost), ConsoleLineKind.Info);
        if (result.Error is not null) Emit(result.Error, ConsoleLineKind.Info);

        Emit("", ConsoleLineKind.Output);
        foreach (var line in aggregator.FormatReport(result.Elapsed))
            Emit(line, ConsoleLineKind.Output);
        return ClassifyDpc(aggregator, result.Elapsed);
    }

    internal static ToolOutcome ClassifyDpc(DpcAggregator aggregator, TimeSpan elapsed)
    {
        if (aggregator.EventCount == 0) return ToolOutcome.Fail(aggregator.Summary(elapsed));
        return aggregator.Verdict == DpcVerdict.Normal
            ? ToolOutcome.Ok(aggregator.Summary(elapsed))
            : ToolOutcome.Warn(aggregator.Summary(elapsed));
    }

    // ═════════════════════════════ Runner ═════════════════════════════

    /// <summary>
    /// Runs one tool with the console: header, elapsed clock, streamed output, progress, outcome.
    /// <paramref name="work"/> returns null when it was cancelled.
    /// </summary>
    private Task ExecuteAsync(string title, string command, bool cancellable, string busyText, Func<CancellationToken, Task<ToolOutcome?>> work) =>
        ExecuteCoreAsync(OperationClass.SystemRepair, false, title, command, cancellable, busyText, work);

    /// <summary>A read-only measurement (never blocks anything; <paramref name="quiet"/> = needs an idle system).</summary>
    private Task ExecuteMeasureAsync(string title, string command, bool cancellable, string busyText, Func<CancellationToken, Task<ToolOutcome?>> work,
        bool quiet = false) =>
        ExecuteCoreAsync(OperationClass.Measure, quiet, title, command, cancellable, busyText, work);

    /// <summary>A measurement that needs a quiet system (DPC latency, energy report): not while an installer or repair runs.</summary>
    private Task ExecuteQuietAsync(string title, string command, bool cancellable, string busyText, Func<CancellationToken, Task<ToolOutcome?>> work) =>
        ExecuteMeasureAsync(title, command, cancellable, busyText, work, quiet: true);

    private async Task ExecuteCoreAsync(OperationClass kind, bool quiet, string title, string command, bool cancellable, string busyText,
        Func<CancellationToken, Task<ToolOutcome?>> work)
    {
        if (IsRunning) return;
        if (kind == OperationClass.SystemRepair && GameBoostState.IsActive)
        {
            OperationNote = Loc.T("Идёт игровой сеанс — инструменты ремонта и дисков запустятся после его остановки, чтобы не мешать игре.");
            Ui.Toast(OperationNote, ToastKind.Warning, title);
            return;
        }
        _cts = new CancellationTokenSource();
        IsRunning = true;
        CanCancelRun = cancellable;
        CancelRequested = false;
        RunTitle = title;
        RunCommandText = command;
        RunState = Loc.Key("ВЫПОЛНЯЕТСЯ");
        HasResult = false;
        ResultText = null;
        ResetProgress();
        _startedAt = DateTime.Now;
        ElapsedText = "0:00";
        _clockTimer.Start();
        _flushTimer.Start();

        if (ConsoleLines.Count > 0) Emit("", ConsoleLineKind.Output);
        Emit(command, ConsoleLineKind.Command);

        ToolOutcome? outcome = null;
        Exception? error = null;
        var token = _cts.Token;
        try
        {
            // Errors are handled here (not by RunBusyAsync) so the user gets exactly one message.
            var started = false;
            await RunBusyAsync(kind, busyText, async lease =>
            {
                started = true;
                if (cancellable) lease.SetCancelHandler(() => Ui.Post(() => { if (CancelRunCommand.CanExecute(null)) CancelRunCommand.Execute(null); }));
                try
                {
                    outcome = await work(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    outcome = null;
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }, quiet);
            if (!started)
            {
                // Refused or taken out of the queue: RunBusyAsync already told the user why (OperationNote + toast).
                outcome = ToolOutcome.Cancel(OperationNote ?? Loc.T("Не запущено: другая задача ещё выполняется."));
            }
            else if (error is not null)
            {
                Log.Error(title, error);
                outcome = ToolOutcome.Fail(error.Message);
            }
            else if (outcome is null && !token.IsCancellationRequested)
            {
                outcome = ToolOutcome.Fail(Loc.T("Операция не выполнена — подробности в журнале."));
            }
        }
        finally
        {
            FlushPending();
            outcome ??= ToolOutcome.Cancel(Loc.T("Операция отменена. Изменения, которые инструмент успел сделать, сохраняются — это безопасно."));
            Finish(outcome);
            _clockTimer.Stop();
            _flushTimer.Stop();
            ElapsedText = ToolOutput.FormatElapsed(DateTime.Now - _startedAt);
            _cts.Dispose();
            _cts = null;
            CanCancelRun = false;
            CancelRequested = false;
            IsRunning = false;
        }
    }

    private void Finish(ToolOutcome outcome)
    {
        _lastStatus = outcome.Status;
        RunState = ToolOutput.StatusText(outcome.Status);
        ResultText = outcome.Summary;
        ResultIsProblem = outcome.Status is ToolStatus.Failed or ToolStatus.Warning;
        HasResult = true;
        if (outcome.Status == ToolStatus.Success && HasProgress)
        {
            Progress = 100;
            ProgressText = "100";
        }
        Emit(outcome.Summary, outcome.Status switch
        {
            ToolStatus.Success => ConsoleLineKind.Success,
            ToolStatus.Failed => ConsoleLineKind.Error,
            _ => ConsoleLineKind.Info,
        });

        switch (outcome.Status)
        {
            case ToolStatus.Success:
                Log.Success($"{RunTitle}: {outcome.Summary}");
                Ui.Toast(outcome.Summary, ToastKind.Success, RunTitle);
                break;
            case ToolStatus.Warning:
                Log.Warn($"{RunTitle}: {outcome.Summary}");
                Ui.Toast(outcome.Summary, ToastKind.Warning, RunTitle);
                break;
            case ToolStatus.Cancelled:
                Log.Info($"{RunTitle}: {outcome.Summary}");
                break;
            default:
                Log.Error($"{RunTitle}: {outcome.Summary}");
                Ui.Toast(outcome.Summary, ToastKind.Error, RunTitle);
                break;
        }
    }

    private async Task<ProcessResult> RunProcessAsync(string exe, IEnumerable<string> args, OutputEncoding encoding, TimeSpan? timeout, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, args, new ProcessRunOptions
        {
            Encoding = encoding,
            Timeout = timeout,
            OnOutput = line => _pending.Enqueue(line),
        }, ct);
        if (r.TimedOut) _pending.Enqueue(Loc.T("Превышено время ожидания — процесс остановлен."));
        if (r.ExitCode == -1 && !r.Cancelled && !r.TimedOut && r.StdOut.Length == 0 && r.StdErr.Length > 0)
            _pending.Enqueue(r.StdErr.Trim());
        return r;
    }

    private void ResetProgress()
    {
        Ui.Post(() =>
        {
            HasProgress = false;
            Progress = 0;
            ProgressText = "—";
        });
    }

    /// <summary>Adds a line written by Lycoris itself (not by the tool). Safe from any thread.</summary>
    private void Emit(string text, ConsoleLineKind kind) => Ui.Post(() =>
    {
        FlushPending();
        AddLine(new ConsoleLine(DateTime.Now.ToString("HH:mm:ss"), text, kind));
    });

    private void FlushPending()
    {
        var now = DateTime.Now.ToString("HH:mm:ss");
        while (_pending.TryDequeue(out var raw))
        {
            var isProgress = ToolOutput.IsProgressLine(raw);
            if (isProgress && ToolOutput.TryParsePercent(raw, out var pct))
            {
                Progress = pct;
                ProgressText = ToolOutput.FormatPercent(pct);
                HasProgress = true;
            }
            var line = new ConsoleLine(now, isProgress ? ToolOutput.NormalizeProgressLine(raw) : raw, ConsoleLineKind.Output, isProgress);
            if (isProgress && ConsoleLines.Count > 0 && ConsoleLines[^1].IsProgress)
                ConsoleLines[^1] = line;
            else
                AddLine(line);
        }
    }

    private void AddLine(ConsoleLine line)
    {
        ConsoleLines.Add(line);
        if (ConsoleLines.Count > MaxConsoleLines)
            for (var i = ConsoleLines.Count - MaxConsoleLines; i > 0; i--) ConsoleLines.RemoveAt(0);
    }

    private static void CopyToClipboard(string text, string toast)
    {
        try
        {
            Clipboard.SetText(text);
            Ui.Toast(toast, ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Warn("Буфер обмена недоступен: {0}", ex.Message);
            Ui.Toast(Loc.T("Буфер обмена занят другой программой — попробуйте ещё раз."), ToastKind.Warning);
        }
    }

    // ═════════════════════════════ Catalog ═════════════════════════════

    private IReadOnlyList<ToolSection> BuildSections() => BuildSections(_volumes, SystemDrive, _hasBattery, _modernStandby);

    /// <summary>Pure catalog of the page: volumes, system drive and power facts come in as inputs (unit-tested).</summary>
    internal static IReadOnlyList<ToolSection> BuildSections(IReadOnlyList<VolumeInfo> volumes, string systemDrive, bool hasBattery, bool modernStandby)
    {
        var chkdskCommandText = string.Format(ChkdskOfflineCommand, systemDrive);
        var defragActions = new List<ToolCardAction>
        {
            new() { Op = ToolOp.DefragAll, Label = Loc.T("ВСЕ ДИСКИ"), IsPrimary = true, RequiresAdmin = true },
        };
        defragActions.AddRange(volumes.Select(DefragAction));

        // Power reports: battery only with a battery, SleepStudy only on Modern Standby (elsewhere the report is empty).
        var reports = new List<ToolCardAction>();
        if (hasBattery) reports.Add(new ToolCardAction { Op = ToolOp.BatteryReport, Label = Loc.T("БАТАРЕЯ") });
        reports.Add(new ToolCardAction { Op = ToolOp.EnergyReport, Label = Loc.T("ЭНЕРГИЯ, 60 С"), IsPrimary = true, RequiresAdmin = true });
        if (modernStandby) reports.Add(new ToolCardAction { Op = ToolOp.SleepStudyReport, Label = "SLEEPSTUDY", RequiresAdmin = true });
        reports.Add(new ToolCardAction { Op = ToolOp.SystemSleepDiagnostics, Label = Loc.T("ДИАГНОСТИКА СНА"), RequiresAdmin = true });
        var reportCommands = new List<string>();
        if (hasBattery) reportCommands.Add("/batteryreport");
        reportCommands.Add("/energy");
        if (modernStandby) reportCommands.Add("/sleepstudy");
        reportCommands.Add("/systemsleepdiagnostics");

        var diagnostics = new List<ToolCard>
        {
            new ToolCard
            {
                Glyph = Glyphs.Power,
                Title = Loc.T("СОН И ПРОБУЖДЕНИЕ"),
                Command = SleepWakeCommandText,
                Description = Loc.T("Отвечает на три вопроса средствами самой Windows: почему ПК проснулся последним (lastwake), какие таймеры и программы будят его или не дают уснуть (waketimers, requests), каким устройствам разрешено будить ПК (devicequery wake_armed) и какие состояния сна доступны."),
                Note = SleepWakeNote(),
                Duration = Loc.T("~3 С"),
                Actions = [new ToolCardAction { Op = ToolOp.SleepWakeDiagnostics, Label = Loc.T("ПРОВЕРИТЬ"), IsPrimary = true }],
            },
            new ToolCard
            {
                Glyph = ToolGlyphs.Battery,
                Title = Loc.T("ОТЧЁТЫ О ПИТАНИИ И СНЕ"),
                Command = "powercfg " + string.Join(" · ", reportCommands),
                Description = Loc.T("HTML-отчёты Windows: батарея (износ, циклы, прогноз), энергоэффективность (/energy — 60 секунд наблюдения: кто мешает простою и USB-сну, кто запрашивает повышенное разрешение таймера, нагрузка в простое), для Modern Standby — расход в режиме ожидания за 3 дня (/sleepstudy), а также диагностика переходов в сон (/systemsleepdiagnostics)."),
                Note = Loc.T("Отчёт энергоэффективности снимается при простое: закройте программы и не трогайте ПК 60 секунд; во время игрового сеанса он не запускается. Отчёты сохраняются в папку журналов Lycoris и открываются в браузере без прав администратора. Ничего не меняет."),
                Duration = Loc.T("5 С – 1 МИН"),
                RequiresAdmin = true,
                Actions = reports,
            },
        };
        diagnostics.Add(new ToolCard
        {
            Glyph = Glyphs.Memory,
            Title = Loc.T("ДИАГНОСТИКА ПАМЯТИ"),
            Command = "mdsched.exe",
            Description = Loc.T("Средство проверки памяти Windows тестирует ОЗУ при перезагрузке, до запуска системы. Нужно при синих экранах, случайных вылетах игр и после включения XMP/EXPO или разгона памяти."),
            Note = Loc.T("Откроется окно Windows с выбором: перезагрузить сейчас или проверить при следующем включении. Результат — в «Просмотре событий» (журнал «Система», источник MemoryDiagnostics-Results)."),
            Duration = Loc.T("15–60 МИН"),
            RequiresAdmin = true,
            NeedsReboot = true,
            Actions = [new ToolCardAction { Op = ToolOp.MemoryDiagnostic, Label = Loc.T("ОТКРЫТЬ"), IsPrimary = true, RequiresAdmin = true }],
        });
        diagnostics.Add(new ToolCard
        {
            Glyph = Glyphs.Diagnostic,
            Title = DpcCardTitle,
            Command = DpcCommandText,
            Description = Loc.T("60 секунд измеряет, сколько времени драйверы проводят в прерываниях (ISR) и отложенных вызовах (DPC). Один драйвер с долгими DPC — частая причина треска звука, рывков ввода и периодических микрофризов. Это измерение вместо гаданий и «твиков»."),
            Note = Loc.T("Измерение почти не нагружает систему, но не бесплатно: во время теста не играйте. Следующий шаг при высоких значениях — обновить этот драйвер в разделе «Драйверы»; исправление не гарантируется. Ничего не меняет."),
            Duration = Loc.T("60 С"),
            RequiresAdmin = true,
            Actions = [new ToolCardAction { Op = ToolOp.DpcLatencyTest, Label = Loc.T("ИЗМЕРИТЬ"), IsPrimary = true, RequiresAdmin = true }],
        });

        return
        [
            new ToolSection
            {
                Title = Loc.T("ЦЕЛОСТНОСТЬ WINDOWS"),
                Caption = Loc.T("Порядок важен: сначала DISM восстанавливает хранилище компонентов, затем SFC берёт из него исправные копии системных файлов."),
                Cards =
                [
                    new ToolCard
                    {
                        Glyph = Glyphs.Repair,
                        Title = Loc.T("ВОССТАНОВЛЕНИЕ ОБРАЗА WINDOWS"),
                        Command = "DISM /Online /Cleanup-Image",
                        Description = Loc.T("Проверяет хранилище компонентов — эталон, из которого Windows восстанавливает свои файлы. «Проверить» читает флаг повреждения за секунды, «Сканировать» проверяет хранилище целиком, «Восстановить» заменяет повреждённые компоненты исправными из Центра обновления Windows."),
                        Note = Loc.T("Шаг 1 из 2. Если проверка нашла повреждения — «Восстановить», затем «Проверка системных файлов». Личные файлы и программы не затрагиваются. Восстановление нельзя прерывать."),
                        Duration = Loc.T("1–30 МИН"),
                        RequiresAdmin = true,
                        Actions =
                        [
                            new ToolCardAction { Op = ToolOp.DismCheck, Label = Loc.T("ПРОВЕРИТЬ"), RequiresAdmin = true },
                            new ToolCardAction { Op = ToolOp.DismScan, Label = Loc.T("СКАНИРОВАТЬ"), RequiresAdmin = true },
                            new ToolCardAction { Op = ToolOp.DismRestore, Label = Loc.T("ВОССТАНОВИТЬ"), IsPrimary = true, RequiresAdmin = true },
                        ],
                    },
                    new ToolCard
                    {
                        Glyph = Glyphs.Shield,
                        Title = Loc.T("ПРОВЕРКА СИСТЕМНЫХ ФАЙЛОВ"),
                        Command = "sfc /scannow",
                        Description = Loc.T("Сверяет все защищённые системные файлы Windows с эталонными копиями и заменяет повреждённые. Помогает при сбоях встроенных программ, ошибках «файл повреждён» и после неудачных обновлений."),
                        Note = Loc.T("Шаг 2 из 2 — запускайте после «Восстановления образа». Прерывать нельзя. Откатывать нечего: возвращаются только оригинальные файлы Microsoft. Журнал: %WINDIR%\\Logs\\CBS\\CBS.log."),
                        Duration = Loc.T("5–20 МИН"),
                        RequiresAdmin = true,
                        Actions = [new ToolCardAction { Op = ToolOp.Sfc, Label = Loc.T("ЗАПУСТИТЬ"), IsPrimary = true, RequiresAdmin = true }],
                    },
                ],
            },
            new ToolSection
            {
                Title = Loc.T("ДИСКИ"),
                Caption = Loc.T("Проверка файловой системы и оптимизация накопителей — теми же средствами, что использует сама Windows."),
                Cards =
                [
                    new ToolCard
                    {
                        Glyph = ToolGlyphs.HardDrive,
                        Title = Loc.T("ПРОВЕРКА ДИСКА"),
                        Command = $"chkdsk {systemDrive} /scan",
                        Description = Loc.Tf("Онлайн-проверка файловой системы диска {0} без перезагрузки — диск остаётся доступным. Покажет, есть ли ошибки, которые требуют полной проверки при загрузке.", systemDrive),
                        Note = Loc.Tf("Полную проверку с исправлением Lycoris не планирует. Если она нужна: откройте «Терминал (администратор)», выполните «{0}», ответьте Y и перезагрузитесь — Windows проверит диск до входа в систему.", chkdskCommandText),
                        Duration = Loc.T("1–10 МИН"),
                        RequiresAdmin = true,
                        Actions =
                        [
                            new ToolCardAction { Op = ToolOp.ChkdskScan, Label = Loc.T("ПРОВЕРИТЬ"), IsPrimary = true, RequiresAdmin = true },
                            new ToolCardAction { Op = ToolOp.ChkntfsQuery, Label = Loc.T("СОСТОЯНИЕ"), RequiresAdmin = true },
                            new ToolCardAction { Op = ToolOp.CopyChkdskCommand, Label = Loc.T("КОПИРОВАТЬ КОМАНДУ") },
                        ],
                        Extra = ToolExtra.BootCheck,
                    },
                    new ToolCard
                    {
                        Glyph = ToolGlyphs.Speed,
                        Title = Loc.T("ОПТИМИЗАЦИЯ ДИСКОВ"),
                        Command = Loc.T("defrag <диск>: /O /U /V"),
                        Description = Loc.T("Windows сама выбирает правильную операцию: для SSD — TRIM (сообщает накопителю, какие блоки свободны), для HDD — дефрагментацию. То же самое раз в неделю делает плановая задача «Оптимизация дисков»."),
                        Note = Loc.T("SSD не дефрагментируется — ключ /O это исключает. Операцию можно прервать в любой момент без вреда для данных."),
                        Duration = Loc.T("SSD < 1 МИН · HDD ДО 1 Ч"),
                        RequiresAdmin = true,
                        Actions = defragActions,
                    },
                ],
            },
            new ToolSection
            {
                Title = Loc.T("СЕТЬ"),
                Caption = Loc.T("От безопасного к радикальному: начните со сброса DNS-кэша, полный сброс — только если сеть не работает."),
                Cards =
                [
                    new ToolCard
                    {
                        Glyph = Glyphs.Globe,
                        Title = Loc.T("СБРОС DNS-КЭША"),
                        Command = "ipconfig /flushdns",
                        Description = Loc.T("Очищает локальный кэш DNS-имён. Помогает, когда сайт сменил адрес, а компьютер продолжает обращаться к старому, или после смены DNS-сервера."),
                        Note = Loc.T("Безопасно: кэш заполняется заново при следующих обращениях к сайтам."),
                        Duration = Loc.T("МГНОВЕННО"),
                        Actions = [new ToolCardAction { Op = ToolOp.FlushDns, Label = Loc.T("ОЧИСТИТЬ"), IsPrimary = true }],
                    },
                    new ToolCard
                    {
                        Glyph = Glyphs.Network,
                        Title = Loc.T("СБРОС СЕТЕВЫХ НАСТРОЕК"),
                        Command = "netsh winsock reset · netsh int ip reset",
                        Description = Loc.T("Возвращает каталог Winsock и стек TCP/IP к состоянию чистой установки. Крайняя мера при «подключено, без доступа к интернету», ошибках сокетов и после удаления VPN или антивирусов, сломавших сеть."),
                        Note = Loc.T("Сбрасывает вручную заданные IP-адреса, шлюзы и DNS; VPN и сетевые фильтры может понадобиться переустановить. Перед сбросом создаётся точка восстановления — вернуть прежнее состояние можно только через неё."),
                        Duration = Loc.T("< 1 МИН"),
                        RequiresAdmin = true,
                        NeedsReboot = true,
                        IsDanger = true,
                        Actions = [new ToolCardAction { Op = ToolOp.NetworkReset, Label = Loc.T("СБРОСИТЬ"), RequiresAdmin = true, IsDanger = true }],
                    },
                ],
            },
            new ToolSection
            {
                Title = Loc.T("КЭШИ"),
                Caption = Loc.T("Точечное лечение частых неполадок интерфейса без удаления данных."),
                Cards =
                [
                    new ToolCard
                    {
                        Glyph = ToolGlyphs.Store,
                        Title = Loc.T("СБРОС КЭША MICROSOFT STORE"),
                        Command = "wsreset.exe",
                        Description = Loc.T("Очищает кэш Microsoft Store. Приложения, покупки и вход в учётную запись сохраняются. Помогает, когда Store не открывается, не скачивает или зависает на загрузке."),
                        Note = Loc.T("После сброса Store откроется сам."),
                        Duration = Loc.T("~10 С"),
                        Actions = [new ToolCardAction { Op = ToolOp.StoreReset, Label = Loc.T("СБРОСИТЬ"), IsPrimary = true }],
                    },
                    new ToolCard
                    {
                        Glyph = ToolGlyphs.Picture,
                        Title = Loc.T("ПЕРЕСОЗДАНИЕ КЭША ЗНАЧКОВ"),
                        Command = "ie4uinit.exe -show",
                        Description = Loc.T("Заставляет Проводник обновить кэш значков. Помогает, когда у файлов и ярлыков пустые, белые или чужие значки."),
                        Note = Loc.T("Безопасный способ: файлы кэша не удаляются, Проводник не перезапускается."),
                        Duration = Loc.T("МГНОВЕННО"),
                        Actions = [new ToolCardAction { Op = ToolOp.IconCache, Label = Loc.T("ОБНОВИТЬ"), IsPrimary = true }],
                    },
                ],
            },
            new ToolSection
            {
                Title = Loc.T("ДИАГНОСТИКА"),
                Caption = Loc.T("Встроенные средства Windows для поиска проблем со сном, питанием и оборудованием."),
                Cards = diagnostics,
            },
        ];
    }

    /// <summary>Per-drive button of the defrag card: "C: · TRIM", details (name, media, free space) in the tooltip.</summary>
    internal static ToolCardAction DefragAction(VolumeInfo v) => new()
    {
        Op = ToolOp.DefragVolume,
        Label = $"{v.Letter} · {v.OperationText}",
        RequiresAdmin = true,
        Volume = v,
        Hint = $"{v.DisplayName} · {v.MediaText} · {v.SpaceText}",
    };

    /// <summary>
    /// Only Windows tools that no Lycoris page covers: services, updates, devices, storage, power, game settings,
    /// programs and restore points have their own pages.
    /// </summary>
    internal static IReadOnlyList<QuickLink> BuildQuickLinks() =>
        [
            new QuickLink { Glyph = ToolGlyphs.HardDrive, Title = Loc.T("УПРАВЛЕНИЕ ДИСКАМИ"), Command = "diskmgmt.msc", Target = ProcessRunner.Sys("diskmgmt.msc") },
            new QuickLink { Glyph = ToolGlyphs.List, Title = Loc.T("ПРОСМОТР СОБЫТИЙ"), Command = "eventvwr.msc", Target = ProcessRunner.Sys("eventvwr.msc") },
            new QuickLink { Glyph = Glyphs.Chart, Title = Loc.T("МОНИТОР РЕСУРСОВ"), Command = "resmon.exe", Target = ProcessRunner.Sys("resmon.exe") },
            new QuickLink { Glyph = Glyphs.Processing, Title = Loc.T("ДИСПЕТЧЕР ЗАДАЧ"), Command = "taskmgr.exe", Target = ProcessRunner.Sys("taskmgr.exe") },
            new QuickLink { Glyph = Glyphs.Info, Title = Loc.T("СВЕДЕНИЯ О СИСТЕМЕ"), Command = "msinfo32.exe", Target = ProcessRunner.Sys("msinfo32.exe") },
        ];
}
