using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Tools;

public sealed partial class ToolsViewModel : PageViewModel
{
    private const int MaxConsoleLines = 4000;
    private const string ChkdskOfflineCommand = "chkdsk {0} /f";
    private const string IdleCommandText = "> выберите инструмент слева";

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _clockTimer;
    private CancellationTokenSource? _cts;
    private DateTime _startedAt;

    public override string Key => "tools";
    public override string Index => "09";
    public override string Title => "ИНСТРУМЕНТЫ";
    public override string Subtitle => "Проверка и восстановление Windows, диски, сеть и системные утилиты.";
    public override string Glyph => Glyphs.Repair;

    public string SystemDrive { get; } = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\').ToUpperInvariant();
    public string ChkdskCommandText => string.Format(ChkdskOfflineCommand, SystemDrive);

    [ObservableProperty] private IReadOnlyList<ToolSection> _sections = [];
    public IReadOnlyList<QuickLink> QuickLinks { get; }
    public ObservableCollection<VolumeItem> Volumes { get; } = new();
    public ObservableCollection<ConsoleLine> ConsoleLines { get; } = new();

    // ── console state ──
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoCancelHint))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(DefragVolumeCommand), nameof(CancelRunCommand), nameof(ClearConsoleCommand), nameof(RefreshVolumesCommand))]
    private bool _isRunning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoCancelHint)), NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    private bool _canCancelRun;
    public bool ShowNoCancelHint => IsRunning && !CanCancelRun;
    [ObservableProperty] private bool _cancelRequested;
    [ObservableProperty] private string _runTitle = "НЕТ АКТИВНОЙ ОПЕРАЦИИ";
    [ObservableProperty] private string _runCommandText = IdleCommandText;
    [ObservableProperty] private string _runState = "ОЖИДАНИЕ";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _resultIsProblem;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private string _progressText = "—";
    [ObservableProperty] private string _elapsedText = "0:00";
    [ObservableProperty] private bool _hasConsoleLines;

    // ── disks ──
    [ObservableProperty] private bool _volumesLoading = true;
    [ObservableProperty] private string _bootCheckText = "Чтение параметров загрузки…";
    [ObservableProperty] private bool _bootCheckScheduled;

    public ToolsViewModel()
    {
        Sections = BuildSections(hasBattery: false);
        QuickLinks = BuildQuickLinks();

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _flushTimer.Tick += (_, _) => FlushPending();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ElapsedText = ToolOutput.FormatElapsed(DateTime.Now - _startedAt);
        ConsoleLines.CollectionChanged += (_, _) => HasConsoleLines = ConsoleLines.Count > 0;
    }

    protected override async Task LoadAsync()
    {
        var volumesTask = ReloadVolumesAsync();
        var bootTask = ReadBootCheckAsync();
        var profile = await SystemProfile.LoadAsync();
        if (profile.HasBattery) Sections = BuildSections(hasBattery: true);
        await Task.WhenAll(volumesTask, bootTask);
    }

    protected override Task OnReturnAsync() => IsRunning ? Task.CompletedTask : ReadBootCheckAsync();

    private async Task ReloadVolumesAsync()
    {
        VolumesLoading = true;
        try
        {
            var list = await VolumeScanner.ScanAsync();
            Volumes.Clear();
            foreach (var v in list) Volumes.Add(new VolumeItem(v));
        }
        finally
        {
            VolumesLoading = false;
        }
    }

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
            ? "Запланирована дополнительная проверка диска — она выполнится при следующей загрузке Windows."
            : "Не запланирована. В стандартном режиме Windows проверяет диск при загрузке, только если он помечен как повреждённый.";
    }

    // ═════════════════════════════ Commands ═════════════════════════════

    private bool CanRun(ToolCardAction? action) =>
        action is not null && !IsRunning && (!action.RequiresAdmin || Elevation.IsElevated);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync(ToolCardAction? action)
    {
        if (action is null) return;
        if (action.RequiresAdmin && !RequireAdmin(action.Label)) return;
        switch (action.Op)
        {
            case ToolOp.DismCheck: await RunDismAsync(ToolOutput.DismMode.CheckHealth); break;
            case ToolOp.DismScan: await RunDismAsync(ToolOutput.DismMode.ScanHealth); break;
            case ToolOp.DismRestore: await RunDismAsync(ToolOutput.DismMode.RestoreHealth); break;
            case ToolOp.Sfc: await RunSfcAsync(); break;
            case ToolOp.ChkdskScan: await RunChkdskAsync(); break;
            case ToolOp.ChkntfsQuery: await RunChkntfsAsync(); break;
            case ToolOp.CopyChkdskCommand: CopyToClipboard(ChkdskCommandText, "Команда скопирована"); break;
            case ToolOp.DefragAll: await RunDefragAsync(Volumes.Select(v => v.Info).ToList()); break;
            case ToolOp.FlushDns: await RunFlushDnsAsync(); break;
            case ToolOp.NetworkReset: await RunNetworkResetAsync(); break;
            case ToolOp.StoreReset: await RunStoreResetAsync(); break;
            case ToolOp.IconCache: await RunIconCacheAsync(); break;
            case ToolOp.BatteryReport: await RunBatteryReportAsync(); break;
            case ToolOp.MemoryDiagnostic: await RunMemoryDiagnosticAsync(); break;
        }
    }

    private bool CanDefragVolume(VolumeItem? volume) => volume is not null && !IsRunning && Elevation.IsElevated;

    [RelayCommand(CanExecute = nameof(CanDefragVolume))]
    private async Task DefragVolumeAsync(VolumeItem? volume)
    {
        if (volume is null || !RequireAdmin("Оптимизация дисков")) return;
        await RunDefragAsync([volume.Info]);
    }

    private bool CanCancel() => IsRunning && CanCancelRun && !CancelRequested;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelRun()
    {
        if (_cts is null) return;
        CancelRequested = true;
        CancelRunCommand.NotifyCanExecuteChanged();
        Emit("Отмена… процесс будет остановлен.", ConsoleLineKind.Info);
        _cts.Cancel();
    }

    [RelayCommand]
    private void CopyOutput()
    {
        if (ConsoleLines.Count == 0)
        {
            Ui.Toast("Консоль пуста — копировать нечего.", ToastKind.Info);
            return;
        }
        var sb = new StringBuilder();
        foreach (var l in ConsoleLines)
            sb.Append(l.Time).Append("  ").Append(l.Marker.Length > 0 ? l.Marker + " " : "").AppendLine(l.Text);
        CopyToClipboard(sb.ToString(), "Вывод консоли скопирован");
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
        RunState = "ОЖИДАНИЕ";
        RunTitle = "НЕТ АКТИВНОЙ ОПЕРАЦИИ";
        RunCommandText = IdleCommandText;
    }

    [RelayCommand]
    private void OpenQuickLink(QuickLink? link)
    {
        if (link is null) return;
        ShellLauncher.Open(link.Target, link.Arguments, link.Title);
    }

    private bool CanRefreshVolumes() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRefreshVolumes))]
    private Task RefreshVolumesAsync() => ReloadVolumesAsync();

    // ═════════════════════════════ Tools ═════════════════════════════

    private async Task RunDismAsync(ToolOutput.DismMode mode)
    {
        var (arg, title, cancellable) = mode switch
        {
            ToolOutput.DismMode.CheckHealth => ("/CheckHealth", "ОБРАЗ WINDOWS · БЫСТРАЯ ПРОВЕРКА", true),
            ToolOutput.DismMode.ScanHealth => ("/ScanHealth", "ОБРАЗ WINDOWS · СКАНИРОВАНИЕ", true),
            _ => ("/RestoreHealth", "ОБРАЗ WINDOWS · ВОССТАНОВЛЕНИЕ", false),
        };
        var display = $"DISM /Online /Cleanup-Image {arg}";

        if (mode == ToolOutput.DismMode.RestoreHealth && !await Ui.ConfirmAsync(
                "Восстановить образ Windows?",
                "DISM сверит хранилище компонентов Windows с эталоном и заменит повреждённые компоненты исправными, скачав их через Центр обновления Windows.",
                [
                    $"Команда: {display}",
                    "Длительность: обычно 10–30 минут, индикатор может подолгу стоять на 62% — это нормально",
                    "Нужен интернет: источник исправных компонентов — Windows Update",
                    "Личные файлы, программы и настройки не затрагиваются",
                    "Прерывать нельзя: остановка посреди восстановления может оставить хранилище в незавершённом состоянии",
                ],
                confirmText: "ЗАПУСТИТЬ", footnote: "После завершения запустите «Проверку системных файлов»."))
            return;

        await ExecuteAsync(title, display, cancellable, "Восстановление образа Windows…", async ct =>
        {
            var r = await RunProcessAsync(ProcessRunner.Sys("dism.exe"), ["/Online", "/Cleanup-Image", arg], OutputEncoding.Oem,
                cancellable ? TimeSpan.FromHours(2) : null, ct);
            return r.Cancelled ? null : ToolOutput.ClassifyDism(mode, r.ExitCode, r.Combined);
        });
    }

    private async Task RunSfcAsync()
    {
        if (!await Ui.ConfirmAsync(
                "Проверить системные файлы?",
                "SFC сверит все защищённые системные файлы Windows с эталонными копиями и заменит повреждённые.",
                [
                    "Команда: sfc /scannow",
                    "Длительность: 5–20 минут, на HDD дольше",
                    "Заменяются только оригинальные файлы Microsoft — личные данные и программы не затрагиваются",
                    "Прерывать нельзя: дождитесь 100%",
                    "Если найдены неисправимые файлы — сначала «Восстановление образа» (DISM), затем повторите проверку",
                ],
                confirmText: "ЗАПУСТИТЬ", footnote: "Журнал проверки: %WINDIR%\\Logs\\CBS\\CBS.log"))
            return;

        await ExecuteAsync("ПРОВЕРКА СИСТЕМНЫХ ФАЙЛОВ", "sfc /scannow", cancellable: false, "Проверка системных файлов…", async ct =>
        {
            var r = await RunProcessAsync(ProcessRunner.Sys("sfc.exe"), ["/scannow"], OutputEncoding.Unicode, null, ct);
            return ToolOutput.ClassifySfc(r.ExitCode, r.Combined);
        });
    }

    private Task RunChkdskAsync() =>
        ExecuteAsync("ПРОВЕРКА ДИСКА " + SystemDrive, $"chkdsk {SystemDrive} /scan", cancellable: true, "Проверка диска…", async ct =>
        {
            var r = await RunProcessAsync(ProcessRunner.Sys("chkdsk.exe"), [SystemDrive, "/scan"], OutputEncoding.Oem, TimeSpan.FromHours(3), ct);
            if (r.Cancelled) return null;
            var outcome = ToolOutput.ClassifyChkdsk(r.ExitCode, r.Combined);
            await ReadBootCheckAsync();
            return outcome;
        });

    private Task RunChkntfsAsync() =>
        ExecuteAsync("СОСТОЯНИЕ ДИСКА " + SystemDrive, $"chkntfs {SystemDrive}", cancellable: true, "Чтение состояния диска…", async ct =>
        {
            var r = await RunProcessAsync(ProcessRunner.Sys("chkntfs.exe"), [SystemDrive], OutputEncoding.Oem, TimeSpan.FromMinutes(1), ct);
            if (r.Cancelled) return null;
            await ReadBootCheckAsync();
            return ToolOutput.ClassifyChkntfs(r.ExitCode, r.Combined, SystemDrive);
        });

    private async Task RunDefragAsync(IReadOnlyList<VolumeInfo> volumes)
    {
        if (volumes.Count == 0)
        {
            Ui.Toast("Не найдено дисков, которые можно оптимизировать.", ToastKind.Warning);
            return;
        }
        var title = volumes.Count == 1 ? $"ОПТИМИЗАЦИЯ ДИСКА {volumes[0].Letter}" : $"ОПТИМИЗАЦИЯ ДИСКОВ · {volumes.Count}";
        var display = volumes.Count == 1 ? $"defrag {volumes[0].Letter} /O /U /V" : $"defrag <{string.Join(" ", volumes.Select(v => v.Letter))}> /O /U /V";

        await ExecuteAsync(title, display, cancellable: true, "Оптимизация дисков…", async ct =>
        {
            int ok = 0, failed = 0;
            var errors = new List<string>();
            foreach (var v in volumes)
            {
                ct.ThrowIfCancellationRequested();
                Emit($"{v.Letter} {v.DisplayName} — {v.MediaText}: {(v.IsSsd ? "TRIM" : v.IsHdd ? "дефрагментация" : "Windows выберет операцию сама")}", ConsoleLineKind.Info);
                ResetProgress();
                var r = await RunProcessAsync(ProcessRunner.Sys("defrag.exe"), [v.Letter, "/O", "/U", "/V"], OutputEncoding.Oem, TimeSpan.FromHours(8), ct);
                if (r.Cancelled) return null;
                if (r.ExitCode == 0) ok++;
                else
                {
                    failed++;
                    errors.Add($"{v.Letter} (код {r.ExitCode})");
                }
            }
            return failed == 0
                ? ToolOutcome.Ok(volumes.Count == 1 ? $"Диск {volumes[0].Letter} оптимизирован." : $"Оптимизировано дисков: {ok}.")
                : ToolOutcome.Fail($"Оптимизировано: {ok}, с ошибкой: {string.Join(", ", errors)}.");
        });
    }

    private Task RunFlushDnsAsync() =>
        ExecuteAsync("СБРОС DNS-КЭША", "ipconfig /flushdns", cancellable: false, "Сброс DNS-кэша…", async ct =>
        {
            var r = await RunProcessAsync(ProcessRunner.Sys("ipconfig.exe"), ["/flushdns"], OutputEncoding.Oem, TimeSpan.FromMinutes(1), ct);
            return ToolOutput.ClassifyExit(r.ExitCode, "Кэш DNS очищен. Имена сайтов будут заново запрошены у DNS-сервера.", "Не удалось очистить кэш DNS");
        });

    private async Task RunNetworkResetAsync()
    {
        var restoreNote = AppSettings.Current.CreateRestorePoints
            ? "Перед сбросом будет создана точка восстановления Windows — только через неё можно вернуть прежние сетевые параметры"
            : "Создание точек восстановления выключено в настройках — вернуть прежние параметры будет нельзя";
        if (!await Ui.ConfirmAsync(
                "Сбросить сетевые настройки?",
                "Каталог Winsock и стек TCP/IP вернутся к состоянию чистой установки Windows. Это крайняя мера, когда сеть не работает и другие способы не помогли.",
                [
                    "netsh winsock reset — сброс каталога Winsock; сторонние сетевые модули (VPN, прокси, сетевые фильтры антивирусов) отключатся",
                    "netsh int ip reset — сброс параметров TCP/IP; вручную заданные IP-адреса, шлюзы и DNS-серверы будут потеряны — запишите их заранее",
                    "VPN-клиенты и сетевое ПО может понадобиться переустановить",
                    "Потребуется перезагрузка компьютера",
                    restoreNote,
                    "Копии разделов реестра Winsock и TCP/IP сохраняются в папку резервных копий MONOLITH",
                ],
                confirmText: "СБРОСИТЬ", danger: true, footnote: "Кнопкой «Откатить» в журнале это действие не отменяется."))
            return;

        await ExecuteAsync("СБРОС СЕТЕВЫХ НАСТРОЕК", "netsh winsock reset · netsh int ip reset", cancellable: false, "Сброс сетевых настроек…", async ct =>
        {
            RestorePointResult? rp = null;
            if (AppSettings.Current.CreateRestorePoints)
            {
                Emit("Создание точки восстановления…", ConsoleLineKind.Info);
                rp = await RestorePointService.CreateAsync("MONOLITH: сброс сетевых настроек");
                Emit(rp.Message, rp.Created ? ConsoleLineKind.Success : ConsoleLineKind.Info);
                if (rp.Outcome is RestorePointOutcome.Disabled or RestorePointOutcome.Failed &&
                    !await Ui.ConfirmAsync("Продолжить без точки восстановления?", rp.Message,
                        ["Без точки восстановления прежние сетевые параметры вернуть будет нельзя"],
                        confirmText: "ПРОДОЛЖИТЬ", danger: true))
                    return ToolOutcome.Cancel("Сброс отменён: точка восстановления не создана.");
            }

            var session = await ChangeSession.BeginAsync("Сброс сетевых настроек", createRestorePoint: false);
            session.Batch.RestorePoint = rp?.Message ?? "Создание точек восстановления выключено в настройках.";
            ChangeJournal.Instance.UpdateBatch(session.Batch);

            AppPaths.TryEnsureDir(session.BackupDir);
            Emit("Резервные копии реестра → " + session.BackupDir, ConsoleLineKind.Info);
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
                return ToolOutcome.Warn("Сетевые настройки сброшены. Перезагрузите компьютер, чтобы изменения вступили в силу.");
            }
            return ToolOutcome.Fail($"Сброс выполнен не полностью: winsock — код {winsock.ExitCode}, TCP/IP — код {ip.ExitCode}. " +
                                    "Если часть параметров сбросить не удалось, перезагрузитесь и повторите.");
        });
    }

    private Task RunStoreResetAsync() =>
        ExecuteAsync("СБРОС КЭША MICROSOFT STORE", "wsreset.exe", cancellable: true, "Сброс кэша Microsoft Store…", async ct =>
        {
            var exe = ProcessRunner.Sys("WSReset.exe");
            if (!File.Exists(exe)) return ToolOutcome.Fail("WSReset.exe не найден — Microsoft Store в этой системе отсутствует.");
            var r = await RunProcessAsync(exe, [], OutputEncoding.Oem, TimeSpan.FromMinutes(3), ct);
            if (r.Cancelled) return null;
            return ToolOutput.ClassifyExit(r.ExitCode, "Кэш Microsoft Store очищен. Store откроется автоматически.", "Не удалось сбросить кэш Store");
        });

    private Task RunIconCacheAsync() =>
        ExecuteAsync("ПЕРЕСОЗДАНИЕ КЭША ЗНАЧКОВ", "ie4uinit.exe -show", cancellable: true, "Обновление кэша значков…", async ct =>
        {
            var exe = ProcessRunner.Sys("ie4uinit.exe");
            if (!File.Exists(exe)) return ToolOutcome.Fail("ie4uinit.exe не найден в System32.");
            var r = await RunProcessAsync(exe, ["-show"], OutputEncoding.Oem, TimeSpan.FromMinutes(1), ct);
            if (r.Cancelled) return null;
            return ToolOutput.ClassifyExit(r.ExitCode,
                "Кэш значков обновлён. Если значки не изменились — перезайдите в систему.", "Не удалось обновить кэш значков");
        });

    private Task RunBatteryReportAsync() =>
        ExecuteAsync("ОТЧЁТ О БАТАРЕЕ", "powercfg /batteryreport", cancellable: true, "Создание отчёта о батарее…", async ct =>
        {
            if (!AppPaths.TryEnsureDir(AppPaths.LogsDir)) return ToolOutcome.Fail("Не удалось создать папку для отчёта: " + AppPaths.LogsDir);
            var file = Path.Combine(AppPaths.LogsDir, "battery-report.html");
            var r = await RunProcessAsync(ProcessRunner.Sys("powercfg.exe"), ["/batteryreport", "/output", file], OutputEncoding.Oem, TimeSpan.FromMinutes(2), ct);
            if (r.Cancelled) return null;
            if (r.ExitCode != 0 || !File.Exists(file)) return ToolOutcome.Fail($"Отчёт не создан (код {r.ExitCode}).");
            Emit("Отчёт: " + file, ConsoleLineKind.Info);
            ShellLauncher.Open(file, displayName: "Отчёт о батарее");
            return ToolOutcome.Ok("Отчёт о батарее создан и открыт в браузере.");
        });

    private async Task RunMemoryDiagnosticAsync()
    {
        if (!await Ui.ConfirmAsync(
                "Запустить диагностику памяти?",
                "Откроется средство проверки памяти Windows. Сам тест выполняется при перезагрузке, до входа в систему.",
                [
                    "В окне Windows выберите: «Выполнить перезагрузку и проверку» или «Выполнить проверку при следующем включении»",
                    "Перед перезагрузкой сохраните открытые документы",
                    "Стандартный тест занимает 15–60 минут в зависимости от объёма ОЗУ",
                    "Результат: «Просмотр событий» → журнал «Система» → источник MemoryDiagnostics-Results",
                ],
                confirmText: "ОТКРЫТЬ"))
            return;

        var exe = ProcessRunner.Sys("MdSched.exe");
        if (!File.Exists(exe))
        {
            Ui.Toast("Средство проверки памяти Windows (MdSched.exe) не найдено.", ToastKind.Error);
            return;
        }
        if (ShellLauncher.Open(exe, displayName: "Диагностика памяти Windows"))
        {
            Emit("Открыто средство проверки памяти Windows — выберите вариант перезагрузки в его окне.", ConsoleLineKind.Info);
            Ui.Toast("Выберите вариант перезагрузки в окне «Средство проверки памяти Windows».", ToastKind.Info);
        }
    }

    // ═════════════════════════════ Runner ═════════════════════════════

    /// <summary>
    /// Runs one tool with the console: header, elapsed clock, streamed output, progress, outcome.
    /// <paramref name="work"/> returns null when it was cancelled.
    /// </summary>
    private async Task ExecuteAsync(string title, string command, bool cancellable, string busyText, Func<CancellationToken, Task<ToolOutcome?>> work)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        CanCancelRun = cancellable;
        CancelRequested = false;
        RunTitle = title;
        RunCommandText = command;
        RunState = "ВЫПОЛНЯЕТСЯ";
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
            await RunBusyAsync(busyText, async () =>
            {
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
            });
            if (error is not null)
            {
                Log.Error(title, error);
                outcome = ToolOutcome.Fail(error.Message);
            }
            else if (outcome is null && !token.IsCancellationRequested)
            {
                outcome = ToolOutcome.Fail("Операция не выполнена — подробности в журнале.");
            }
        }
        finally
        {
            FlushPending();
            outcome ??= ToolOutcome.Cancel("Операция отменена. Изменения, которые инструмент успел сделать, сохраняются — это безопасно.");
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
        if (r.TimedOut) _pending.Enqueue("Превышено время ожидания — процесс остановлен.");
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

    /// <summary>Adds a line written by MONOLITH itself (not by the tool). Safe from any thread.</summary>
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
            Log.Warn($"Буфер обмена недоступен: {ex.Message}");
            Ui.Toast("Буфер обмена занят другой программой — попробуйте ещё раз.", ToastKind.Warning);
        }
    }

    // ═════════════════════════════ Catalog ═════════════════════════════

    private IReadOnlyList<ToolSection> BuildSections(bool hasBattery)
    {
        var diagnostics = new List<ToolCard>();
        if (hasBattery)
        {
            diagnostics.Add(new ToolCard
            {
                Glyph = ToolGlyphs.Battery,
                Title = "ОТЧЁТ О БАТАРЕЕ",
                Command = "powercfg /batteryreport",
                Description = "HTML-отчёт Windows о батарее: паспортная и текущая ёмкость (степень износа), число циклов заряда, история использования и прогноз времени работы.",
                Note = "Отчёт сохраняется в папку журналов MONOLITH и открывается в браузере. Ничего не меняет в системе.",
                Duration = "~5 С",
                Actions = [new ToolCardAction { Op = ToolOp.BatteryReport, Label = "СОЗДАТЬ ОТЧЁТ", IsPrimary = true }],
            });
        }
        diagnostics.Add(new ToolCard
        {
            Glyph = Glyphs.Memory,
            Title = "ДИАГНОСТИКА ПАМЯТИ",
            Command = "mdsched.exe",
            Description = "Средство проверки памяти Windows тестирует ОЗУ при перезагрузке, до запуска системы. Нужно при синих экранах, случайных вылетах игр и после включения XMP/EXPO или разгона памяти.",
            Note = "Откроется окно Windows с выбором: перезагрузить сейчас или проверить при следующем включении. Результат — в «Просмотре событий» (журнал «Система», источник MemoryDiagnostics-Results).",
            Duration = "15–60 МИН",
            RequiresAdmin = true,
            NeedsReboot = true,
            Actions = [new ToolCardAction { Op = ToolOp.MemoryDiagnostic, Label = "ОТКРЫТЬ", IsPrimary = true, RequiresAdmin = true }],
        });

        return
        [
            new ToolSection
            {
                Number = "01",
                Title = "ЦЕЛОСТНОСТЬ WINDOWS",
                Caption = "Порядок важен: сначала DISM восстанавливает хранилище компонентов, затем SFC берёт из него исправные копии системных файлов.",
                Cards =
                [
                    new ToolCard
                    {
                        Glyph = Glyphs.Repair,
                        Title = "ВОССТАНОВЛЕНИЕ ОБРАЗА WINDOWS",
                        Command = "DISM /Online /Cleanup-Image",
                        Description = "Проверяет хранилище компонентов — эталон, из которого Windows восстанавливает свои файлы. «Проверить» читает флаг повреждения за секунды, «Сканировать» проверяет хранилище целиком, «Восстановить» заменяет повреждённые компоненты исправными из Центра обновления Windows.",
                        Note = "Шаг 1 из 2. Если проверка нашла повреждения — «Восстановить», затем «Проверка системных файлов». Личные файлы и программы не затрагиваются. Восстановление нельзя прерывать.",
                        Duration = "1–30 МИН",
                        RequiresAdmin = true,
                        Actions =
                        [
                            new ToolCardAction { Op = ToolOp.DismCheck, Label = "ПРОВЕРИТЬ", RequiresAdmin = true },
                            new ToolCardAction { Op = ToolOp.DismScan, Label = "СКАНИРОВАТЬ", RequiresAdmin = true },
                            new ToolCardAction { Op = ToolOp.DismRestore, Label = "ВОССТАНОВИТЬ", IsPrimary = true, RequiresAdmin = true },
                        ],
                    },
                    new ToolCard
                    {
                        Glyph = Glyphs.Shield,
                        Title = "ПРОВЕРКА СИСТЕМНЫХ ФАЙЛОВ",
                        Command = "sfc /scannow",
                        Description = "Сверяет все защищённые системные файлы Windows с эталонными копиями и заменяет повреждённые. Помогает при сбоях встроенных программ, ошибках «файл повреждён» и после неудачных обновлений.",
                        Note = "Шаг 2 из 2 — запускайте после «Восстановления образа». Прерывать нельзя. Откатывать нечего: возвращаются только оригинальные файлы Microsoft. Журнал: %WINDIR%\\Logs\\CBS\\CBS.log.",
                        Duration = "5–20 МИН",
                        RequiresAdmin = true,
                        Actions = [new ToolCardAction { Op = ToolOp.Sfc, Label = "ЗАПУСТИТЬ", IsPrimary = true, RequiresAdmin = true }],
                    },
                ],
            },
            new ToolSection
            {
                Number = "02",
                Title = "ДИСКИ",
                Caption = "Проверка файловой системы и оптимизация накопителей — теми же средствами, что использует сама Windows.",
                Cards =
                [
                    new ToolCard
                    {
                        Glyph = ToolGlyphs.HardDrive,
                        Title = "ПРОВЕРКА ДИСКА",
                        Command = $"chkdsk {SystemDrive} /scan",
                        Description = $"Онлайн-проверка файловой системы диска {SystemDrive} без перезагрузки — диск остаётся доступным. Покажет, есть ли ошибки, которые требуют полной проверки при загрузке.",
                        Note = $"Полную проверку с исправлением MONOLITH не планирует. Если она нужна: откройте «Терминал (администратор)», выполните «{ChkdskCommandText}», ответьте Y и перезагрузитесь — Windows проверит диск до входа в систему.",
                        Duration = "1–10 МИН",
                        RequiresAdmin = true,
                        Actions =
                        [
                            new ToolCardAction { Op = ToolOp.ChkdskScan, Label = "ПРОВЕРИТЬ", IsPrimary = true, RequiresAdmin = true },
                            new ToolCardAction { Op = ToolOp.ChkntfsQuery, Label = "СОСТОЯНИЕ", RequiresAdmin = true },
                            new ToolCardAction { Op = ToolOp.CopyChkdskCommand, Label = "КОПИРОВАТЬ КОМАНДУ" },
                        ],
                        Extra = ToolExtra.BootCheck,
                    },
                    new ToolCard
                    {
                        Glyph = ToolGlyphs.Speed,
                        Title = "ОПТИМИЗАЦИЯ ДИСКОВ",
                        Command = "defrag <диск>: /O /U /V",
                        Description = "Windows сама выбирает правильную операцию: для SSD — TRIM (сообщает накопителю, какие блоки свободны), для HDD — дефрагментацию. То же самое раз в неделю делает плановая задача «Оптимизация дисков».",
                        Note = "SSD не дефрагментируется — ключ /O это исключает. Операцию можно прервать в любой момент без вреда для данных.",
                        Duration = "SSD < 1 МИН · HDD ДО 1 Ч",
                        RequiresAdmin = true,
                        Actions = [new ToolCardAction { Op = ToolOp.DefragAll, Label = "ВСЕ ДИСКИ", IsPrimary = true, RequiresAdmin = true }],
                        Extra = ToolExtra.Volumes,
                    },
                ],
            },
            new ToolSection
            {
                Number = "03",
                Title = "СЕТЬ",
                Caption = "От безопасного к радикальному: начните со сброса DNS-кэша, полный сброс — только если сеть не работает.",
                Cards =
                [
                    new ToolCard
                    {
                        Glyph = Glyphs.Globe,
                        Title = "СБРОС DNS-КЭША",
                        Command = "ipconfig /flushdns",
                        Description = "Очищает локальный кэш DNS-имён. Помогает, когда сайт сменил адрес, а компьютер продолжает обращаться к старому, или после смены DNS-сервера.",
                        Note = "Безопасно: кэш заполняется заново при следующих обращениях к сайтам.",
                        Duration = "МГНОВЕННО",
                        Actions = [new ToolCardAction { Op = ToolOp.FlushDns, Label = "ОЧИСТИТЬ", IsPrimary = true }],
                    },
                    new ToolCard
                    {
                        Glyph = Glyphs.Network,
                        Title = "СБРОС СЕТЕВЫХ НАСТРОЕК",
                        Command = "netsh winsock reset · netsh int ip reset",
                        Description = "Возвращает каталог Winsock и стек TCP/IP к состоянию чистой установки. Крайняя мера при «подключено, без доступа к интернету», ошибках сокетов и после удаления VPN или антивирусов, сломавших сеть.",
                        Note = "Сбрасывает вручную заданные IP-адреса, шлюзы и DNS; VPN и сетевые фильтры может понадобиться переустановить. Перед сбросом создаётся точка восстановления — вернуть прежнее состояние можно только через неё.",
                        Duration = "< 1 МИН",
                        RequiresAdmin = true,
                        NeedsReboot = true,
                        IsDanger = true,
                        Actions = [new ToolCardAction { Op = ToolOp.NetworkReset, Label = "СБРОСИТЬ", RequiresAdmin = true, IsDanger = true }],
                    },
                ],
            },
            new ToolSection
            {
                Number = "04",
                Title = "КЭШИ",
                Caption = "Точечное лечение частых неполадок интерфейса без удаления данных.",
                Cards =
                [
                    new ToolCard
                    {
                        Glyph = ToolGlyphs.Store,
                        Title = "СБРОС КЭША MICROSOFT STORE",
                        Command = "wsreset.exe",
                        Description = "Очищает кэш Microsoft Store. Приложения, покупки и вход в учётную запись сохраняются. Помогает, когда Store не открывается, не скачивает или зависает на загрузке.",
                        Note = "После сброса Store откроется сам.",
                        Duration = "~10 С",
                        Actions = [new ToolCardAction { Op = ToolOp.StoreReset, Label = "СБРОСИТЬ", IsPrimary = true }],
                    },
                    new ToolCard
                    {
                        Glyph = ToolGlyphs.Picture,
                        Title = "ПЕРЕСОЗДАНИЕ КЭША ЗНАЧКОВ",
                        Command = "ie4uinit.exe -show",
                        Description = "Заставляет Проводник обновить кэш значков. Помогает, когда у файлов и ярлыков пустые, белые или чужие значки.",
                        Note = "Безопасный способ: файлы кэша не удаляются, Проводник не перезапускается.",
                        Duration = "МГНОВЕННО",
                        Actions = [new ToolCardAction { Op = ToolOp.IconCache, Label = "ОБНОВИТЬ", IsPrimary = true }],
                    },
                ],
            },
            new ToolSection
            {
                Number = "05",
                Title = "ДИАГНОСТИКА",
                Caption = "Встроенные средства Windows для поиска аппаратных проблем.",
                Cards = diagnostics,
            },
        ];
    }

    private static IReadOnlyList<QuickLink> BuildQuickLinks()
    {
        var control = ProcessRunner.Sys("control.exe");
        return
        [
            new QuickLink { Glyph = Glyphs.Devices, Title = "ДИСПЕТЧЕР УСТРОЙСТВ", Command = "devmgmt.msc", Target = ProcessRunner.Sys("devmgmt.msc") },
            new QuickLink { Glyph = ToolGlyphs.HardDrive, Title = "УПРАВЛЕНИЕ ДИСКАМИ", Command = "diskmgmt.msc", Target = ProcessRunner.Sys("diskmgmt.msc") },
            new QuickLink { Glyph = Glyphs.Services, Title = "СЛУЖБЫ", Command = "services.msc", Target = ProcessRunner.Sys("services.msc") },
            new QuickLink { Glyph = ToolGlyphs.List, Title = "ПРОСМОТР СОБЫТИЙ", Command = "eventvwr.msc", Target = ProcessRunner.Sys("eventvwr.msc") },
            new QuickLink { Glyph = Glyphs.Chart, Title = "МОНИТОР РЕСУРСОВ", Command = "resmon.exe", Target = ProcessRunner.Sys("resmon.exe") },
            new QuickLink { Glyph = Glyphs.Processing, Title = "ДИСПЕТЧЕР ЗАДАЧ", Command = "taskmgr.exe", Target = ProcessRunner.Sys("taskmgr.exe") },
            new QuickLink { Glyph = Glyphs.Shield, Title = "ЗАЩИТА СИСТЕМЫ", Command = "SystemPropertiesProtection", Target = ProcessRunner.Sys("SystemPropertiesProtection.exe") },
            new QuickLink { Glyph = Glyphs.Power, Title = "ЭЛЕКТРОПИТАНИЕ", Command = "powercfg.cpl", Target = control, Arguments = "powercfg.cpl" },
            new QuickLink { Glyph = Glyphs.Game, Title = "ПАРАМЕТРЫ ИГР", Command = "ms-settings:gaming-gamemode", Target = "ms-settings:gaming-gamemode" },
            new QuickLink { Glyph = Glyphs.Monitor, Title = "ДИСПЛЕЙ", Command = "ms-settings:display", Target = "ms-settings:display" },
            new QuickLink { Glyph = Glyphs.Sync, Title = "ЦЕНТР ОБНОВЛЕНИЯ", Command = "ms-settings:windowsupdate", Target = "ms-settings:windowsupdate" },
            new QuickLink { Glyph = Glyphs.Save, Title = "ХРАНИЛИЩЕ", Command = "ms-settings:storagesense", Target = "ms-settings:storagesense" },
            new QuickLink { Glyph = ToolGlyphs.AllApps, Title = "ПРОГРАММЫ И КОМПОНЕНТЫ", Command = "appwiz.cpl", Target = control, Arguments = "appwiz.cpl" },
            new QuickLink { Glyph = Glyphs.Info, Title = "СВЕДЕНИЯ О СИСТЕМЕ", Command = "msinfo32.exe", Target = ProcessRunner.Sys("msinfo32.exe") },
        ];
    }
}
