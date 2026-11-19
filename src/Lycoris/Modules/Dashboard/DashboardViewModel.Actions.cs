using System.IO;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Modules.Dashboard.Doctor;
using Lycoris.Modules.Maintenance;
using Lycoris.Modules.Optimize;
using Lycoris.Modules.Tools;
using Lycoris.Tweaks;
using Lycoris.UI;

namespace Lycoris.Modules.Dashboard;

// The buttons of the problem rows and «ИСПРАВИТЬ ВСЁ БЕЗОПАСНОЕ».
public sealed partial class DashboardViewModel
{
    private Task RunActionAsync(CheckItemViewModel item) => item.Result.Action?.Kind switch
    {
        CheckActionKind.ApplyTweak or CheckActionKind.Restart or CheckActionKind.MemoryDiagnostic => RunPrimaryActionAsync(item),
        CheckActionKind.Clean when item.ShowPrimaryAction => RunPrimaryActionAsync(item),
        _ => OpenAdviceAsync(item),
    };

    [RelayCommand]
    private Task OpenAdviceAsync(CheckItemViewModel? item) =>
        RunReadOnlyActionAsync(item?.Guidance?.OpenAction, OpenPage, OpenShellAsync, DiagnoseAsync);

    [RelayCommand]
    private Task RunSecondaryActionAsync(CheckItemViewModel? item) =>
        RunReadOnlyActionAsync(item?.Result.SecondaryAction, OpenPage, OpenShellAsync, DiagnoseAsync);

    /// <summary>
    /// Presses the button of one check by its id — what the elevated copy repeats after «ПЕРЕЗАПУСТИТЬ С ПРАВАМИ»
    /// (the same command, so the same confirmation).
    /// </summary>
    [RelayCommand]
    private Task RunCheckAsync(string? parameter) => FindCheckForReplay(_all, parameter) switch
    {
        { Result.Action.Kind: CheckActionKind.Clean } item => OpenAdviceAsync(item),
        { } item => RunActionAsync(item),
        _ => Task.CompletedTask,
    };

    internal static CheckItemViewModel? FindCheckForReplay(IEnumerable<CheckItemViewModel> items, string? parameter)
    {
        if (string.IsNullOrEmpty(parameter)) return null;
        var split = parameter.IndexOf('|');
        if (split < 0) return items.FirstOrDefault(i => !i.IsOk && i.Result.Id == parameter);
        var id = parameter[..split];
        var target = parameter[(split + 1)..];
        return items.FirstOrDefault(i => !i.IsOk && i.Result.Id == id &&
            i.Result.Action is { Kind: CheckActionKind.ApplyTweak } action && action.Target == target);
    }

    private async Task RunPrimaryActionAsync(CheckItemViewModel item)
    {
        if (item.IsOk || item.Result.Action is not { } action) return;
        if (action.RequiresAdmin && IsBusy)
        {
            Ui.Toast("Дождитесь окончания текущей операции.", ToastKind.Info);
            return;
        }
        switch (action.Kind)
        {
            case CheckActionKind.Clean:
                await CleanJunkAsync(cleanOnly: true);
                break;
            case CheckActionKind.ApplyTweak when action.Target is { } id:
                await ApplyTweakAsync(id, item.Result.Id);
                break;
            case CheckActionKind.Restart:
                await ScheduleRestartAsync(item.Result.Id == "pending-reboot" || action.Focus == DashboardAdvice.WindowsUpdateUri);
                break;
            case CheckActionKind.MemoryDiagnostic:
                if (!await RunMemoryDiagnosticAdviceAsync(Main?.Maintenance)) OpenPage("tools", null);
                break;
        }
    }

    private void OpenPage(string page, string? focus)
    {
        _lastCheck = DateTime.MinValue; // re-check after the user returns from the destination
        if (focus is not null) Ui.RequestFocus(page, focus);
        Ui.Navigate(page);
    }

    internal static Task RunReadOnlyActionAsync(CheckAction? action, Action<string, string?> openPage,
        Func<string, string?, string, Task> openShell, Func<Task> diagnose)
    {
        switch (action)
        {
            case { Kind: CheckActionKind.Navigate, Target: { } page }:
                openPage(page, action.Focus);
                break;
            case { Kind: CheckActionKind.FocusTweak, Target: { } tweakId }:
                openPage("optimize", tweakId);
                break;
            case { Kind: CheckActionKind.FocusDevice, Target: { } deviceId }:
                openPage("devices", deviceId);
                break;
            case { Kind: CheckActionKind.OpenUri, Target: HealthRules.PowerSettingsUri or HealthRules.WindowsSecurityUri
                or DashboardAdvice.WindowsUpdateUri or DashboardAdvice.DisplaySettingsUri }:
                return openShell(action.Target!, null, Loc.T("параметры Windows"));
            case { Kind: CheckActionKind.OpenTaskManager }:
                return openShell(PcDoctor.TaskManagerPath, null, Loc.T("диспетчер задач"));
            case { Kind: CheckActionKind.OpenReliabilityMonitor }:
                return openShell(ProcessRunner.Sys("perfmon.exe"), "/rel", Loc.T("журнал надёжности"));
            case { Kind: CheckActionKind.OpenSystemInformation }:
                return openShell(ProcessRunner.Sys("msinfo32.exe"), null, Loc.T("сведения о системе"));
            case { Kind: CheckActionKind.DiagnoseAgain }:
                return diagnose();
        }
        return Task.CompletedTask;
    }

    internal static async Task<bool> RunMemoryDiagnosticAdviceAsync(MaintenanceViewModel? maintenance,
        Func<ToolsViewModel, IAsyncRelayCommand<ToolCardAction?>>? commandFactory = null)
    {
        if (maintenance?.Tools is not { } tools) return false;
        var action = tools.Sections.SelectMany(s => s.Cards).SelectMany(c => c.Actions)
            .FirstOrDefault(a => a.Op == ToolOp.MemoryDiagnostic);
        var command = commandFactory?.Invoke(tools) ?? tools.RunCommand;
        if (action is null || !command.CanExecute(action)) return false;
        await command.ExecuteAsync(action);
        return true;
    }

    private static Task OpenShellAsync(string target, string? args, string what) => Task.Run(async () =>
    {
        try
        {
            // A protocol handler (ms-settings:) lives in HKCU, which any program of the user can rewrite: never hand it to
            // ShellExecute with Lycoris's administrator token — open it as the desktop user.
            if (Elevation.IsElevated)
            {
                if (!await UnelevatedShell.StartAsUserAsync(target, args ?? ""))
                    Ui.Toast(Loc.Tf("Не удалось открыть {0}", what), ToastKind.Warning);
                return;
            }
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (args is not null) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть {0}", ex, what);
            Ui.Toast(Loc.Tf("Не удалось открыть {0}: {1}", what, ex.Message), ToastKind.Error);
        }
    });

    // ───────────────────────────── Safe fixes (tweaks through the journal) ─────────────────────────────

    /// <summary>A row whose button applies a catalog tweak by itself («ВКЛЮЧИТЬ», «ВЫКЛЮЧИТЬ») — what «ИСПРАВИТЬ ВСЁ БЕЗОПАСНОЕ» collects.</summary>
    internal static bool IsSafeFix(CheckItemViewModel item) =>
        FindFixTweak(item.Result, safeOnly: true) is not null;

    private static Tweak? FindFixTweak(HealthCheckResult check, bool safeOnly) =>
        check.Severity != CheckSeverity.Ok && check.Action is { Kind: CheckActionKind.ApplyTweak, Target: { } id }
            && IsAllowedRisk(TweakCatalog.Find(id), safeOnly) is { } tweak ? tweak : null;

    private static Tweak? IsAllowedRisk(Tweak? tweak, bool safeOnly) => tweak?.Risk switch
    {
        TweakRisk.Safe => tweak,
        TweakRisk.Optional when !safeOnly => tweak,
        _ => null,
    };

    /// <summary>What a fix resolved to: the tweak to apply, or the reason there is none (shown as a toast).</summary>
    internal sealed record FixPlan(Tweak? Tweak, string? Message, ToastKind Kind, bool OpenOptimize);

    internal sealed record FixSelection(string CheckId, string Target, Tweak Tweak);

    /// <summary>
    /// The tweak behind a check button, matched to this PC: the check looks at the primary GPU, the catalog at every GPU,
    /// so HAGS falls back to the sibling item that fits. Never a dead button: no match = an explanation.
    /// </summary>
    internal static FixPlan ResolveFix(Tweak tweak, bool safeOnly, Func<Tweak, bool?> applicability)
    {
        if (IsAllowedRisk(tweak, safeOnly) is null)
            return new(null, Loc.T("Эта настройка находится в разделе «Оптимизация»."), ToastKind.Info, true);
        var applies = applicability(tweak);
        if (applies is null)
            return new(null, Loc.T("Не удалось определить характеристики компьютера — настройку пока нельзя проверить. Попробуйте позже."), ToastKind.Warning, false);
        if (applies is true) return new(tweak, null, ToastKind.Info, false);

        // Only the safe batch may consider a different catalog item, which will be named in its confirmation.
        var sibling = (safeOnly, tweak.Id) switch
        {
            (true, HealthRules.TweakHags) => TweakCatalog.Find(HealthRules.TweakHagsOther),
            (true, HealthRules.TweakHagsOther) => TweakCatalog.Find(HealthRules.TweakHags),
            _ => null,
        };
        if (safeOnly && IsAllowedRisk(sibling, safeOnly: true) is { } alternate && applicability(alternate) == true)
            return new(alternate, null, ToastKind.Info, false);
        return new(null, tweak.NotApplicableReason ?? Loc.T("Эта настройка не подходит для этого компьютера."), ToastKind.Warning, true);
    }

    internal static bool FixesStillMatch(IReadOnlyList<FixSelection> selected, IReadOnlyList<HealthCheckResult> current,
        bool safeOnly, Func<Tweak, bool?> applicability) =>
        selected.All(pick => current.FirstOrDefault(c => c.Id == pick.CheckId) is { } check
            && FindFixTweak(check, safeOnly) is { } requested && requested.Id == pick.Target
            && ResolveFix(requested, safeOnly, applicability).Tweak is { } found
            && found.Id == pick.Tweak.Id && found.Risk == pick.Tweak.Risk);

    internal static async Task RunConfirmedFixAsync(Func<Task<bool>> confirm, Func<Func<Task>, Task> queue,
        Func<Task<bool>> stillMatches, Func<Task> apply)
    {
        if (!await confirm()) return;
        await queue(async () =>
        {
            if (await stillMatches()) await apply();
        });
    }

    private async Task<bool> CheckDashboardFixesAsync(IReadOnlyList<FixSelection> selected, bool safeOnly)
    {
        // Collect after confirmation and after gaining the lease. A snapshot from the previous Dashboard
        // refresh may have started before either boundary (or before an earlier queued operation wrote).
        var snapshot = await InspectAsync();
        var checks = await Task.Run(() => HealthRules.Evaluate(snapshot));
        if (FixesStillMatch(selected, checks, safeOnly, tweak => TweakEngine.Applicability(tweak, SystemProfile.Current)))
            return true;
        Ui.Toast(Loc.T("Условия исправления изменились. Обновите проверку и снова выберите нужное действие."), ToastKind.Info);
        return false;
    }

    private static List<string> ConfirmDetails(Tweak tweak)
    {
        var details = new List<string> { $"{tweak.Title}: {tweak.Description}" };
        if (tweak.Restart != RestartRequirement.None) details.Add(Loc.Tf("Вступит в силу {0}", tweak.RestartWhen));
        return details;
    }

    private static string RestorePointLine() => AppSettings.Current.CreateRestorePoints
        ? Loc.T("Перед изменением будет создана точка восстановления (если защита системы включена)")
        : Loc.T("Точка восстановления не создаётся — это выключено в разделе «Восстановление»");

    private async Task<TweakRunResult?> ApplyTweakAsync(string id, string checkId, Func<Task<bool>>? checkCurrent = null,
        Func<string, bool>? requestAdmin = null)
    {
        var safeOnly = checkCurrent is not null;
        if (IsAllowedRisk(TweakCatalog.Find(id), safeOnly) is not { } tweak)
        {
            Ui.Toast("Эта настройка находится в разделе «Оптимизация».", ToastKind.Info);
            Ui.Navigate("optimize");
            return null;
        }
        // Read-only mode: relaunch with rights and press this same button again.
        if (!(requestAdmin?.Invoke(tweak.Title) ?? RequireAdmin(tweak.Title, "RunCheck", checkId + "|" + id))) return null;

        var profile = await SystemProfile.LoadAsync();
        // An individual action (including Assistant) is tied to the exact catalog setting shown in the row.
        var plan = ResolveFix(tweak, safeOnly: false, t => TweakEngine.Applicability(t, profile));
        if (plan.Tweak is not { } resolved)
        {
            Ui.Toast(plan.Message!, plan.Kind, tweak.Title);
            if (plan.OpenOptimize) OpenPage("optimize", id);
            return null;
        }
        tweak = resolved;

        var details = ConfirmDetails(tweak);
        details.Add(RestorePointLine());
        details.Add(Loc.T("Исходное значение сохраняется в журнале — отменить можно в разделе «Восстановление»"));

        TweakRunResult? result = null;
        await RunConfirmedFixAsync(
            () => Ui.ConfirmAsync("ПРИМЕНИТЬ НАСТРОЙКУ?",
                Loc.Tf("Lycoris применит настройку «{0}» через журнал изменений. Будет сделано следующее:", tweak.Title),
                details, footnote: "Изменение обратимо."),
            work => RunBusyAsync(OperationClass.JournalChange, Loc.Tf("Применение: {0}…", tweak.Title), work),
            checkCurrent ?? (() => CheckDashboardFixesAsync([new(checkId, id, tweak)], safeOnly: false)),
            async () => result = await TweakEngine.ApplyAsync([tweak], tweak.Title));
        if (result is { } r)
        {
            if (r.Errors.Count > 0)
                Ui.Toast(string.Join(Environment.NewLine, r.Errors), ToastKind.Error, "Не удалось применить");
            else if (r.Applied > 0)
                Ui.Toast(r.Restart == RestartRequirement.None
                        ? Loc.T("Готово. Отменить можно в разделе «Восстановление».")
                        : Loc.Tf("Готово. Изменение вступит в силу {0}.", tweak.RestartWhen),
                    ToastKind.Success, tweak.Title);
            else
                Ui.Toast("Настройка уже действует — менять нечего.", ToastKind.Info, tweak.Title);
        }
        if (checkCurrent is null && result is not null) await RefreshAsync(showBusy: false);
        return result;
    }

    /// <summary>
    /// «ИСПРАВИТЬ ВСЁ БЕЗОПАСНОЕ · N»: every row whose button is a safe catalog tweak, in ONE confirmation and ONE journal batch
    /// (so one restore point and one «ОТКАТИТЬ ПОСЛЕДНЕЕ»). Items that do not fit this PC are skipped and listed.
    /// </summary>
    [RelayCommand]
    private async Task FixSafeAsync()
    {
        var items = _all.Where(IsSafeFix).ToList();
        if (items.Count == 0)
        {
            Ui.Toast("Безопасных исправлений сейчас нет. Остальные пункты списка требуют вашего решения — у каждого есть своя кнопка.", ToastKind.Info, "ПАНЕЛЬ");
            return;
        }
        if (IsBusy)
        {
            Ui.Toast("Дождитесь окончания текущей операции.", ToastKind.Info);
            return;
        }
        if (!RequireAdmin(Loc.Key("Исправить всё безопасное"), "FixSafe")) return;

        var selections = new List<FixSelection>();
        var skipped = new List<string>();
        var profile = await SystemProfile.LoadAsync();
        foreach (var item in items)
        {
            if (FindFixTweak(item.Result, safeOnly: true) is not { } found) continue;
            var plan = ResolveFix(found, safeOnly: true, tweak => TweakEngine.Applicability(tweak, profile));
            if (plan.Tweak is not { } fix)
            {
                skipped.Add(Loc.Tf("{0}: {1}", item.Title, plan.Message));
                continue;
            }
            selections.Add(new(item.Result.Id, found.Id, fix));
        }
        var tweaks = selections.Select(selection => selection.Tweak).DistinctBy(t => t.Id).ToList();
        if (tweaks.Count == 0)
        {
            Ui.Toast(string.Join(Environment.NewLine, skipped), ToastKind.Warning, "НЕЧЕГО ИСПРАВЛЯТЬ");
            return;
        }

        var details = tweaks.Select(t => string.Join(" · ", ConfirmDetails(t))).ToList();
        details.AddRange(skipped.Select(s => Loc.Tf("Будет пропущено: {0}", s)));
        details.Add(RestorePointLine());
        details.Add(Loc.T("Исходные значения сохраняются в журнале — отменить можно одной кнопкой «ОТКАТИТЬ ПОСЛЕДНЕЕ» или в разделе «Восстановление»"));
        var title = Loc.Tf("ПРИМЕНИТЬ {0}?", Loc.N(tweaks.Count, "{0} ИСПРАВЛЕНИЕ", "{0} ИСПРАВЛЕНИЯ", "{0} ИСПРАВЛЕНИЙ"));
        TweakRunResult? result = null;
        await RunConfirmedFixAsync(
            () => Ui.ConfirmAsync(title,
                "Lycoris применит только перечисленные безопасные настройки через журнал изменений. Будет сделано следующее:",
                details, footnote: "Изменения обратимы."),
            work => RunBusyAsync(OperationClass.JournalChange, Loc.Key("Исправление безопасных пунктов…"), work),
            () => CheckDashboardFixesAsync(selections, safeOnly: true),
            async () => result = await TweakEngine.ApplyAsync(tweaks, Loc.Key("Панель: безопасные исправления")));
        if (result is { } r)
        {
            if (r.Errors.Count > 0)
                Ui.Toast(string.Join(Environment.NewLine, r.Errors), ToastKind.Error, "Не удалось применить");
            else if (r.Applied > 0)
                Ui.Toast(r.Restart == RestartRequirement.None
                        ? Loc.T("Готово. Отменить можно кнопкой «ОТКАТИТЬ ПОСЛЕДНЕЕ».")
                        : Loc.T("Готово. Часть изменений вступит в силу после перезапуска. Отменить можно кнопкой «ОТКАТИТЬ ПОСЛЕДНЕЕ»."),
                    ToastKind.Success, "ПАНЕЛЬ");
            else
                Ui.Toast("Всё уже применено — менять нечего.", ToastKind.Info, "ПАНЕЛЬ");
        }
        if (result is not null) await RefreshAsync(showBusy: false);
    }

    // ───────────────────────────── «ПЕРЕЗАГРУЗИТЬ» (pending reboot) ─────────────────────────────

    internal const int RestartDelaySeconds = 60;
    private DateTime? _restartAt;
    private System.Windows.Threading.DispatcherTimer? _restartTimer;

    /// <summary>«Перезагрузка через 45 с» while a restart from Lycoris is scheduled; null otherwise.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _restartCountdown;

    internal static async Task<SessionActionResult?> ConfirmAndScheduleRestartAsync(Func<string?> blocker,
        Func<Task<bool>> confirm, Func<Task<SessionActionResult>> schedule, Action<string> showBlocker)
    {
        bool CanRestart()
        {
            if (blocker() is not { } blocked) return true;
            showBlocker(blocked);
            return false;
        }

        if (!CanRestart() || !await confirm() || !CanRestart()) return null;
        return await schedule();
    }

    /// <summary>The same reboot as «Оптимизация» (SessionActions), with a longer delay so there is time to cancel it.</summary>
    private async Task ScheduleRestartAsync(bool pending)
    {
        var outcome = await ConfirmAndScheduleRestartAsync(
            () => SessionActions.RebootBlockerText(OperationCoordinator.Instance.Snapshot(), GameBoostState.IsActive),
            () => Ui.ConfirmAsync(
                Loc.T("ПЕРЕЗАГРУЗИТЬ КОМПЬЮТЕР?"),
                pending
                    ? Loc.T("Windows ждёт перезагрузки, чтобы завершить обновления и изменения.")
                    : Loc.T("Windows завершит работу открытых программ и запустится заново."),
                [
                    Loc.T("Сохраните открытые файлы и закройте игры"),
                    Loc.Tf("Перезагрузка начнётся через {0} секунд", RestartDelaySeconds),
                    Loc.T("До этого её можно отменить кнопкой «ОТМЕНИТЬ ПЕРЕЗАГРУЗКУ» на «Панели»"),
                ],
                confirmText: Loc.T("ПЕРЕЗАГРУЗИТЬ")),
            () => SessionActions.ScheduleRebootAsync(RestartDelaySeconds),
            blocked => Ui.Toast(blocked, ToastKind.Warning));
        if (outcome is not { } result) return;
        if (!result.Success)
        {
            Ui.Toast(result.Message, ToastKind.Error);
            return;
        }
        _restartAt = DateTime.Now.AddSeconds(RestartDelaySeconds);
        _restartTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _restartTimer.Tick -= OnRestartTick;
        _restartTimer.Tick += OnRestartTick;
        _restartTimer.Start();
        OnRestartTick(null, EventArgs.Empty);
        Ui.Toast(Loc.Tf("Перезагрузка через {0} секунд. Отменить — «ОТМЕНИТЬ ПЕРЕЗАГРУЗКУ» на «Панели».", RestartDelaySeconds),
            ToastKind.Warning, navigateTo: "dashboard");
    }

    private void OnRestartTick(object? sender, EventArgs e)
    {
        var left = _restartAt is { } at ? (int)Math.Ceiling((at - DateTime.Now).TotalSeconds) : 0;
        if (left <= 0) { _restartTimer?.Stop(); RestartCountdown = null; return; }
        RestartCountdown = Loc.Tf("Перезагрузка через {0} с", left);
    }

    [RelayCommand]
    private async Task CancelRestartAsync()
    {
        var r = await ProcessRunner.RunAsync(ProcessRunner.Sys("shutdown.exe"), "/a");
        _restartTimer?.Stop(); _restartAt = null; RestartCountdown = null;
        Ui.Toast(r.ExitCode == 0 ? Loc.T("Перезагрузка отменена.") : Loc.T("Запланированной перезагрузки уже нет."), ToastKind.Info);
    }

    // ───────────────────────────── «ОТЧЁТ О ПК» (roadmap item 8) ─────────────────────────────

    [RelayCommand]
    private async Task CreateHealthReportAsync()
    {
        if (!HasResults)
        {
            Ui.Toast(Loc.T("Дождитесь окончания проверки — отчёт строится по её результатам."), ToastKind.Info);
            return;
        }
        if (!await Ui.ConfirmAsync(Loc.T("ОТЧЁТ О ПК"),
                Loc.T("Lycoris сохранит понятный отчёт о ПК — вы выберете, куда (страница HTML, открывается в браузере)."),
                Lycoris.Core.Diagnostics.HealthReport.Contents, confirmText: Loc.T("СОХРАНИТЬ ОТЧЁТ"))) return;

        var html = Lycoris.Core.Diagnostics.HealthReport.Build(BuildHealthReportData());

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Сохранить отчёт"),
            Filter = Loc.T("HTML-файл (*.html)") + "|*.html",
            DefaultExt = ".html",
            AddExtension = true,
            FileName = Lycoris.Core.Diagnostics.HealthReport.SuggestedFileName,
            OverwritePrompt = true,
            InitialDirectory = SafeDesktopDir(),
        };
        if (dlg.ShowDialog(System.Windows.Application.Current?.MainWindow) != true) return;

        try
        {
            await Task.Run(() => System.IO.File.WriteAllText(dlg.FileName, html, new System.Text.UTF8Encoding(false)));
            Log.Info("Отчёт о ПК сохранён: {0}", dlg.FileName);
            Ui.Toast(Loc.T("Отчёт о ПК сохранён."), ToastKind.Success, openFolderPath: System.IO.Path.GetDirectoryName(dlg.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Toast(Loc.Tf("Отчёт не сохранён: {0}", ex.Message), ToastKind.Error);
        }
    }

    // The checked Desktop of the signed-in user (as before); Documents is often closed by Windows «Controlled folder access».
    private static string SafeDesktopDir()
    {
        try { return Lycoris.Core.Diagnostics.ProblemReport.ResolveDestination().Folder; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { return ""; }
    }

    internal Lycoris.Core.Diagnostics.HealthReportData BuildHealthReportData(Lycoris.Core.Assistant.AssistantDiagnosticSnapshot? fresh = null)
    {
        var results = fresh?.Checks ?? _all.Select(i => i.Result).ToArray();
        var checks = HealthScore.Ordered(results)
            .Select(i => new Lycoris.Core.Diagnostics.HealthReportCheck(i.Title, i.Detail,
                i.Severity == CheckSeverity.Ok ? "ok" : i.Severity == CheckSeverity.Warning ? "warn" : "info"))
            .ToList();
        var system = fresh?.System ?? SystemRows.Select(r => new Lycoris.Core.Diagnostics.HealthReportRow(r.Label, r.Value, r.Detail)).ToArray();
        string? measurement = null;
        try
        {
            var last = new Lycoris.Core.Measurements.MeasurementStore().Read().LastOrDefault();
            measurement = FormatHealthReportMeasurement(last);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        // The FPS «до / после» of the Games page (PresentMon) joins the same section of the report.
        var fps = new Lycoris.Modules.Games.FpsStore().Read();
        var fpsLine = Lycoris.Modules.Games.FpsMeasure.Summary(fps.Before, fps.After);
        if (fpsLine.Length > 0) measurement = string.IsNullOrEmpty(measurement) ? fpsLine : measurement + ". " + fpsLine;
        var version = typeof(DashboardViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";
        var score = fresh is null ? Score : HealthScore.Calculate(results);
        return new(fresh?.ObservedAt ?? DateTime.Now, version, score, fresh is null ? Verdict : HealthScore.Verdict(score), checks, system, measurement);
    }

    internal static string? FormatHealthReportMeasurement(Lycoris.Core.Measurements.Measurement? measurement)
    {
        if (measurement is null) return null;
        return Loc.Tf("Последний замер {0}: автозапуск — {1}, свободная память — {2}, время загрузки — {3}",
            measurement.RecordedAt.ToLocalTime().ToString("g"), measurement.Conditions.EnabledStartupItems,
            measurement.Conditions.FreeMemoryBytes is { } bytes ? Loc.Tf("{0} МБ", bytes / 1024 / 1024) : Loc.T("недоступно"),
            measurement.Conditions.BootTimeSeconds is { } s ? Loc.Tf("{0} с", s.ToString("0.0")) : Loc.T("недоступно"));
    }
}
