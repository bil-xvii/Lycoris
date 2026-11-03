using System.IO;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Tweaks;
using Lycoris.UI;

namespace Lycoris.Modules.Dashboard;

// The buttons of the problem rows and «ИСПРАВИТЬ ВСЁ БЕЗОПАСНОЕ».
public sealed partial class DashboardViewModel
{
    private Task RunActionAsync(CheckItemViewModel item) => RunActionAsync(item.Result.Action, item.Result.Id);

    /// <summary>The second button of a check (e.g. the hibernation item behind a low-space warning). Same handling as the first.</summary>
    [RelayCommand]
    private Task RunSecondaryActionAsync(CheckItemViewModel? item) => RunActionAsync(item?.Result.SecondaryAction, item?.Result.Id ?? "");

    /// <summary>
    /// Presses the button of one check by its id — what the elevated copy repeats after «ПЕРЕЗАПУСТИТЬ С ПРАВАМИ»
    /// (the same command, so the same confirmation).
    /// </summary>
    [RelayCommand]
    private Task RunCheckAsync(string? id) =>
        _all.FirstOrDefault(i => i.Result.Id == id) is { } item ? RunActionAsync(item) : Task.CompletedTask;

    private async Task RunActionAsync(CheckAction? action, string checkId)
    {
        if (action is null) return;
        if (action.RequiresAdmin && IsBusy)
        {
            Ui.Toast("Дождитесь окончания текущей операции.", ToastKind.Info);
            return;
        }
        switch (action.Kind)
        {
            // The disk-space check: when the dry run already knows what can go, the button cleans right here.
            case CheckActionKind.Navigate when action.Target == "cleaner" && CleanSize is not null:
                await CleanJunkAsync();
                break;
            case CheckActionKind.Navigate when action.Target is { } page:
                _lastCheck = DateTime.MinValue; // the user may fix it there: re-check on return
                if (action.Focus is { } focus) Ui.RequestFocus(page, focus);
                Ui.Navigate(page);
                break;
            case CheckActionKind.OpenUri when action.Target is { } uri:
                await OpenShellAsync(uri, null, Loc.T("параметры Windows"));
                break;
            case CheckActionKind.FocusTweak when action.Target is { } tweakId:
                _lastCheck = DateTime.MinValue;
                Ui.RequestFocus("optimize", tweakId);
                Ui.Navigate("optimize");
                break;
            case CheckActionKind.ApplyTweak when action.Target is { } id:
                await ApplyTweakAsync(id, checkId);
                break;
            case CheckActionKind.Restart:
                await ScheduleRestartAsync();
                break;
            case CheckActionKind.FocusDevice when action.Target is { } target:
                _lastCheck = DateTime.MinValue; // the user may fix it there: re-check on return
                Ui.RequestFocus("devices", target);
                Ui.Navigate("devices");
                break;
        }
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
        !item.IsOk && item.Result.Action is { Kind: CheckActionKind.ApplyTweak, Target: not null };

    /// <summary>What a fix resolved to: the tweak to apply, or the reason there is none (shown as a toast).</summary>
    private sealed record FixPlan(Tweak? Tweak, string? Message, ToastKind Kind, bool OpenOptimize);

    /// <summary>
    /// The tweak behind a check button, matched to this PC: the check looks at the primary GPU, the catalog at every GPU,
    /// so HAGS falls back to the sibling item that fits. Never a dead button: no match = an explanation.
    /// </summary>
    private static async Task<FixPlan> ResolveFixAsync(Tweak tweak, string id)
    {
        var profile = await SystemProfile.LoadAsync();
        var applies = TweakEngine.Applicability(tweak, profile);
        if (applies is null)
            return new(null, Loc.T("Не удалось определить характеристики компьютера — настройку пока нельзя проверить. Попробуйте позже."), ToastKind.Warning, false);
        if (applies is true) return new(tweak, null, ToastKind.Info, false);

        var sibling = id switch
        {
            HealthRules.TweakHags => TweakCatalog.Find(HealthRules.TweakHagsOther),
            HealthRules.TweakHagsOther => TweakCatalog.Find(HealthRules.TweakHags),
            _ => null,
        };
        if (sibling is not null && sibling.Risk != TweakRisk.Advanced && TweakEngine.Applicability(sibling, profile) == true)
            return new(sibling, null, ToastKind.Info, false);
        return new(null, tweak.NotApplicableReason ?? Loc.T("Эта настройка не подходит для этого компьютера."), ToastKind.Warning, true);
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

    private async Task ApplyTweakAsync(string id, string checkId)
    {
        var tweak = TweakCatalog.Find(id);
        if (tweak is null || tweak.Risk == TweakRisk.Advanced)
        {
            Ui.Toast("Эта настройка находится в разделе «Оптимизация».", ToastKind.Info);
            Ui.Navigate("optimize");
            return;
        }
        // Read-only mode: relaunch with rights and press this same button again.
        if (!RequireAdmin(tweak.Title, "RunCheck", checkId)) return;

        var plan = await ResolveFixAsync(tweak, id);
        if (plan.Tweak is not { } resolved)
        {
            Ui.Toast(plan.Message!, plan.Kind, tweak.Title);
            if (plan.OpenOptimize) Ui.Navigate("optimize");
            return;
        }
        tweak = resolved;

        var details = ConfirmDetails(tweak);
        details.Add(RestorePointLine());
        details.Add(Loc.T("Исходное значение сохраняется в журнале — отменить можно в разделе «Восстановление»"));

        var confirmed = await Ui.ConfirmAsync("ПРИМЕНИТЬ НАСТРОЙКУ?",
            Loc.Tf("Lycoris применит настройку «{0}» через журнал изменений. Будет сделано следующее:", tweak.Title),
            details, footnote: "Изменение обратимо.");
        if (!confirmed) return;

        TweakRunResult? result = null;
        await RunBusyAsync(OperationClass.JournalChange, Loc.Tf("Применение: {0}…", tweak.Title), async () => result = await TweakEngine.ApplyAsync([tweak], tweak.Title));
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
        await RefreshAsync(showBusy: false);
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

        var tweaks = new List<Tweak>();
        var skipped = new List<string>();
        foreach (var item in items)
        {
            var id = item.Result.Action!.Target!;
            var found = TweakCatalog.Find(id);
            if (found is null || found.Risk == TweakRisk.Advanced)
            {
                skipped.Add(Loc.Tf("{0}: настройка находится в разделе «Оптимизация»", item.Title));
                continue;
            }
            var plan = await ResolveFixAsync(found, id);
            if (plan.Tweak is not { } fix)
            {
                skipped.Add(Loc.Tf("{0}: {1}", item.Title, plan.Message));
                continue;
            }
            if (tweaks.All(t => t.Id != fix.Id)) tweaks.Add(fix);
        }
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
        var ok = await Ui.ConfirmAsync(title,
            "Lycoris применит только перечисленные безопасные настройки через журнал изменений. Будет сделано следующее:",
            details, footnote: "Изменения обратимы.");
        if (!ok) return;

        TweakRunResult? result = null;
        await RunBusyAsync(OperationClass.JournalChange, Loc.Key("Исправление безопасных пунктов…"), async () => result = await TweakEngine.ApplyAsync(tweaks, Loc.Key("Панель: безопасные исправления")));
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
        await RefreshAsync(showBusy: false);
    }

    // ───────────────────────────── «ПЕРЕЗАГРУЗИТЬ» (pending reboot) ─────────────────────────────

    internal const int RestartDelaySeconds = 60;
    private DateTime? _restartAt;
    private System.Windows.Threading.DispatcherTimer? _restartTimer;

    /// <summary>«Перезагрузка через 45 с» while a restart from Lycoris is scheduled; null otherwise.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _restartCountdown;

    /// <summary>The same reboot as «Оптимизация» (SessionActions), with a longer delay so there is time to cancel it.</summary>
    private async Task ScheduleRestartAsync()
    {
        // A running install / DISM / cleaning or a game session would be cut off mid-way: refuse, as «Оптимизация» does.
        if (Lycoris.Modules.Optimize.SessionActions.RebootBlockerText(OperationCoordinator.Instance.Snapshot(),
                GameBoostState.IsActive) is { } blocked)
        {
            Ui.Toast(blocked, ToastKind.Warning);
            return;
        }
        var confirmed = await Ui.ConfirmAsync(
            Loc.T("ПЕРЕЗАГРУЗИТЬ КОМПЬЮТЕР?"),
            Loc.T("Windows ждёт перезагрузки, чтобы завершить обновления и изменения."),
            [
                Loc.T("Сохраните открытые файлы и закройте игры"),
                Loc.Tf("Перезагрузка начнётся через {0} секунд", RestartDelaySeconds),
                Loc.T("До этого её можно отменить кнопкой «ОТМЕНИТЬ ПЕРЕЗАГРУЗКУ» на «Панели»"),
            ],
            confirmText: Loc.T("ПЕРЕЗАГРУЗИТЬ"));
        if (!confirmed) return;
        var result = await Lycoris.Modules.Optimize.SessionActions.ScheduleRebootAsync(RestartDelaySeconds);
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
                Loc.T("Lycoris сохранит понятный отчёт о ПК на рабочий стол (страница HTML, открывается в браузере)."),
                Lycoris.Core.Diagnostics.HealthReport.Contents, confirmText: Loc.T("СОХРАНИТЬ ОТЧЁТ"))) return;
        try
        {
            var html = Lycoris.Core.Diagnostics.HealthReport.Build(BuildHealthReportData());
            var path = await Task.Run(() => Lycoris.Core.Diagnostics.ProblemReport.SaveHealthReportToDesktop(System.Text.Encoding.UTF8.GetBytes(html)));
            Log.Info("Отчёт о ПК сохранён: {0}", Path.GetFileName(path));
            Ui.Toast(Loc.T("Отчёт о ПК сохранён на рабочем столе."), ToastKind.Success, openFolderPath: Path.GetDirectoryName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Ui.Toast(ex is InvalidOperationException ? ex.Message : Loc.Tf("Отчёт не сохранён: {0}", ex.Message), ToastKind.Error);
        }
    }

    internal Lycoris.Core.Diagnostics.HealthReportData BuildHealthReportData()
    {
        var checks = _all.OrderBy(i => i.IsOk).ThenByDescending(i => i.Severity)
            .Select(i => new Lycoris.Core.Diagnostics.HealthReportCheck(i.Title, i.Detail,
                i.IsOk ? "ok" : i.Severity == CheckSeverity.Warning ? "warn" : "info"))
            .ToList();
        var system = SystemRows.Select(r => new Lycoris.Core.Diagnostics.HealthReportRow(r.Label, r.Value, r.Detail)).ToList();
        string? measurement = null;
        try
        {
            var last = new Lycoris.Core.Measurements.MeasurementStore().Read().LastOrDefault();
            if (last is not null)
                measurement = Loc.Tf("Последний замер {0}: автозапуск — {1}, свободная память — {2} МБ, время загрузки — {3}",
                    last.RecordedAt.ToLocalTime().ToString("g"), last.Conditions.EnabledStartupItems,
                    (last.Conditions.FreeMemoryBytes ?? 0) / 1024 / 1024,
                    last.Conditions.BootTimeSeconds is { } s ? Loc.Tf("{0} с", s.ToString("0.0")) : Loc.T("недоступно"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var version = typeof(DashboardViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";
        return new(DateTime.Now, version, Score, Verdict, checks, system, measurement);
    }
}
