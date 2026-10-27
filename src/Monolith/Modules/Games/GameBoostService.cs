using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Games;

public enum BoostChangeKind { Changed, Skipped, Info }

/// <summary>One line of "what the session changed" in the boost panel.</summary>
public sealed record BoostChange(string Title, string Detail, BoostChangeKind Kind)
{
    public bool IsChanged => Kind == BoostChangeKind.Changed;
    public bool IsSkipped => Kind == BoostChangeKind.Skipped;
}

/// <summary>
/// Game Boost session — «Ускорение игры» in the UI (the Windows feature «Игровой режим» is a different thing). Temporary changes only:
///  • power plan → High performance on desktops (journaled under source "gameboost" so the Restore page can undo it after a crash;
///    released at once if the PC switches to battery);
///  • close the user's chosen background apps (window close first, kill only after explicit confirmation) — never reopened;
///  • game process priority → High (never Realtime), restored when the session ends;
///  • optional one-time standby-list purge (opt-in, honest explanation, nothing to undo).
/// The game is started as the signed-in user (never with MONOLITH's admin token) and found by its install folder.
/// Stops manually or automatically when the game process exits; the journaled plan and the priority are reverted.
/// All public members are called on the UI thread.
/// </summary>
public sealed partial class GameBoostService : ObservableObject
{
    public const string Source = "gameboost";
    public const string DisplayName = "Ускорение игры";
    private const string SessionTitle = "Ускорение игры (сеанс)";
    private const string PowerRecordTitle = "Ускорение игры: план питания";
    private const string PowerTarget = "power:active";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PowerPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DetectTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(8);

    private readonly Dictionary<int, (SafeProcessHandle Handle, uint Original)> _raised = new();
    private readonly List<string> _closedApps = [];
    private CancellationTokenSource? _watchCts;
    /// <summary>This session switched the plan and still owns that change.</summary>
    private bool _sessionSwitchedPlan;
    /// <summary>The plan row in <see cref="Changes"/>, replaced when the plan is released early.</summary>
    private BoostChange? _powerChange;
    /// <summary>An in-flight early release of the plan (battery); Stop waits for it so nothing is reverted twice.</summary>
    private Task? _powerRelease;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isTransitioning;
    [ObservableProperty] private bool _hasGame;
    [ObservableProperty] private string _gameName = "";
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string? _processText;
    [ObservableProperty] private DateTime _startedAt;

    public ObservableCollection<BoostChange> Changes { get; } = [];

    // Publish the session state app-wide (Cleaner refuses to run during a game; startup checks skip recovery).
    partial void OnIsActiveChanged(bool value) => GameBoostState.Set(value);

    /// <summary>Raised on the UI thread after a session has ended (manually or automatically).</summary>
    public event Action? Ended;

    // ───────────────────────────── Plan ─────────────────────────────

    /// <summary>Laptop / tablet: WMI (battery, chassis) or the power manager (lid, long-term battery). Blocking.</summary>
    public static bool IsPortable(SystemProfile sp) => sp.IsLaptop || GamesNative.IsPortable();

    /// <summary>Decides the power plan action for this PC right now. Blocking.</summary>
    public static (PowerPlanAction Action, string Text) DecidePower(SystemProfile sp, bool enabled)
    {
        var active = PowerSchemes.GetActive();
        var activeName = active == Guid.Empty ? "текущая схема" : PowerSchemes.GetName(active);
        var ultimate = enabled ? PowerSchemes.FindUltimateCopy() : null;
        var alreadyFast = active == PowerSchemes.HighPerformance || (ultimate is { } u && u == active);
        var journaled = ChangeJournal.Instance.FindActive(PowerTarget);
        var action = BoostPolicy.DecidePowerPlan(new PowerFacts(
            Enabled: enabled,
            DualCcdX3D: BoostPolicy.IsDualCcdX3D(sp.CpuName, sp.CpuCores),
            Laptop: IsPortable(sp),
            OnBattery: GamesNative.IsOnBattery(),
            ModernStandby: GamesNative.IsModernStandby(),
            ManagedElsewhere: journaled is not null && journaled.Source != Source,
            HighPerformanceExists: enabled && PowerSchemes.Exists(PowerSchemes.HighPerformance),
            AlreadyFast: alreadyFast));
        return (action, BoostPolicy.Describe(action, activeName, sp.CpuName));
    }

    /// <summary>What a session would do now (power plan, which running apps would be closed…).</summary>
    public static async Task<BoostPlan> BuildPlanAsync(GameEntry? game)
    {
        var sp = await SystemProfile.LoadAsync();
        var s = AppSettings.Current;
        var closeList = (s.GameBoostCloseList ?? []).ToList();
        var powerEnabled = s.GameBoostPowerPlan;
        var priority = s.GameBoostPriority;
        var purge = s.GameBoostPurgeStandby;
        return await Task.Run(() =>
        {
            var (action, text) = DecidePower(sp, powerEnabled);
            var apps = new List<RunningApp>();
            var procs = UserProcesses.FindClosable(closeList, game?.InstallDir);
            try
            {
                foreach (var g in procs.GroupBy(SafeName, StringComparer.OrdinalIgnoreCase))
                    apps.Add(new RunningApp(g.Key, "", g.Count(), 0, true));
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
            var running = new HashSet<string>(apps.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            var notRunning = closeList.Select(UserProcesses.NormalizeName).Where(n => n.Length > 0 && !running.Contains(n)).ToList();
            return new BoostPlan(action, text, apps, notRunning, priority && game is not null, purge);
        });
    }

    // ───────────────────────────── Start ─────────────────────────────

    public async Task<bool> StartAsync(GameEntry? game, BoostPlan plan)
    {
        if (IsActive || IsTransitioning) return false;
        Elevation.Demand(DisplayName);
        IsTransitioning = true;
        Changes.Clear();
        _closedApps.Clear();
        _powerChange = null;
        _powerRelease = null;
        _sessionSwitchedPlan = false;
        HasGame = game is not null;
        GameName = game?.Name ?? "Без привязки к игре";
        ProcessText = null;
        PhaseText = "ЗАПУСК";
        _watchCts?.Cancel();
        _watchCts = new CancellationTokenSource();
        var ct = _watchCts.Token;
        try
        {
            if (plan.PowerAction == PowerPlanAction.Switch) await SwitchPowerPlanAsync();
            else SetPowerChange(new BoostChange("План питания не изменён", plan.PowerText.Replace("План питания не меняется: ", ""), BoostChangeKind.Skipped));

            if (plan.AppsToClose.Count > 0) await CloseAppsAsync(plan, game);

            if (plan.PurgeStandby) await PurgeStandbyAsync();

            IsActive = true;
            StartedAt = DateTime.Now;
            Log.Success($"{DisplayName}: сеанс начат{(game is null ? "" : $" для «{game.Name}»")}.");
            if (_sessionSwitchedPlan) _ = PowerWatchAsync(ct);

            if (game is null)
            {
                PhaseText = "АКТИВНО";
                return true;
            }

            try
            {
                await GameLauncher.LaunchAsync(game);
            }
            catch (Exception ex)
            {
                Log.Error($"Не удалось запустить «{game.Name}»", ex);
                await StopCoreAsync(ex.Message + " Ускорение выключено.", ToastKind.Error);
                return false;
            }

            PhaseText = "ОЖИДАНИЕ ИГРЫ";
            Changes.Add(new BoostChange("Игра запущена",
                "С правами пользователя, не администратора. Ожидание процесса игры (до 3 минут) — затем приоритет и слежение за выходом.",
                BoostChangeKind.Info));
            _ = WatchAsync(game, ct);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"{DisplayName}: ошибка запуска", ex);
            Ui.Toast(ex.Message, ToastKind.Error, "УСКОРЕНИЕ ИГРЫ");
            if (IsActive || ChangeJournal.Instance.HasActiveSource(Source))
                await StopCoreAsync("Запуск ускорения прерван.", ToastKind.Warning);
            return false;
        }
        finally
        {
            IsTransitioning = false;
        }
    }

    private async Task SwitchPowerPlanAsync()
    {
        // The confirmation dialog may have been open for a while: never switch once the PC runs on battery.
        if (await Task.Run(GamesNative.IsOnBattery))
        {
            SetPowerChange(new BoostChange("План питания не изменён",
                "Компьютер работает от батареи — «Высокая производительность» не включается.", BoostChangeKind.Skipped));
            return;
        }
        var (ok, from, to, error) = await Task.Run(async () =>
        {
            var fromName = PowerSchemes.GetName(PowerSchemes.GetActive());
            var session = await ChangeSession.BeginAsync(SessionTitle, createRestorePoint: false).ConfigureAwait(false);
            var r = session.SetPowerScheme(PowerSchemes.HighPerformance, Source, PowerRecordTitle);
            session.Complete();
            return (r, fromName, PowerSchemes.GetName(PowerSchemes.HighPerformance), session.Errors.FirstOrDefault());
        });
        _sessionSwitchedPlan = ok;
        SetPowerChange(ok
            ? new BoostChange("План питания", $"«{from}» → «{to}». Вернётся в конце сеанса или сразу при переходе на батарею.", BoostChangeKind.Changed)
            : new BoostChange("План питания не изменён", error ?? "Windows отклонила смену схемы.", BoostChangeKind.Skipped));
    }

    private void SetPowerChange(BoostChange change)
    {
        var index = _powerChange is null ? -1 : Changes.IndexOf(_powerChange);
        if (index >= 0) Changes[index] = change;
        else Changes.Insert(0, change);
        _powerChange = change;
    }

    private async Task CloseAppsAsync(BoostPlan plan, GameEntry? game)
    {
        var names = plan.AppsToClose.Select(a => a.Name).ToList();
        var targets = await Task.Run(() => UserProcesses.FindClosable(names, game?.InstallDir)
            .Select(p => (Process: p, Name: SafeName(p))).ToList());
        try
        {
            if (targets.Count == 0)
            {
                Changes.Add(new BoostChange("Приложения", "Приложения из списка уже закрыты.", BoostChangeKind.Info));
                return;
            }

            // 1) Polite: the same as clicking the window's close button.
            await Task.Run(() =>
            {
                foreach (var (p, _) in targets)
                {
                    try { if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); }
                    catch { /* exited */ }
                }
            });
            var deadline = DateTime.UtcNow + CloseTimeout;
            while (DateTime.UtcNow < deadline && targets.Any(t => IsAlive(t.Process)))
                await Task.Delay(500);

            // 2) Force only with explicit consent.
            var remaining = targets.Where(t => IsAlive(t.Process)).ToList();
            if (remaining.Count > 0)
            {
                var details = remaining.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Count() > 1 ? $"{g.Key} — процессов: {g.Count()}" : g.Key).ToList();
                var kill = await Ui.ConfirmAsync("ЗАВЕРШИТЬ ПРИНУДИТЕЛЬНО?",
                    "Эти приложения не закрылись за 8 секунд — многие программы при закрытии окна сворачиваются в трей. " +
                    "Завершить их принудительно? Несохранённые данные в них будут потеряны, а после игры MONOLITH их не откроет.",
                    details, "ЗАВЕРШИТЬ", "ОСТАВИТЬ ОТКРЫТЫМИ", danger: true,
                    footnote: "Ускорение игры продолжит работу при любом выборе.");
                if (kill)
                {
                    await Task.Run(() =>
                    {
                        foreach (var (p, name) in remaining)
                        {
                            try { if (!p.HasExited) p.Kill(); }
                            catch (Exception ex) { Log.Warn($"Не удалось завершить {name}: {ex.Message}"); }
                        }
                    });
                    var killDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                    while (DateTime.UtcNow < killDeadline && remaining.Any(t => IsAlive(t.Process)))
                        await Task.Delay(250);
                }
            }

            var byName = targets.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var closed = byName.Where(g => g.All(t => !IsAlive(t.Process))).Select(g => g.Key).ToList();
            var open = byName.Where(g => g.Any(t => IsAlive(t.Process))).Select(g => g.Key).ToList();
            _closedApps.AddRange(closed);
            if (closed.Count > 0)
            {
                Changes.Add(new BoostChange("Закрыто приложений: " + closed.Count,
                    string.Join(", ", closed) + ". Это не откатывается: после игры MONOLITH их не открывает — запустите их сами.", BoostChangeKind.Changed));
                Log.Info($"{DisplayName}: закрыты " + string.Join(", ", closed));
            }
            if (open.Count > 0)
                Changes.Add(new BoostChange("Остались открыты", string.Join(", ", open), BoostChangeKind.Skipped));
        }
        finally
        {
            foreach (var (p, _) in targets) p.Dispose();
        }
    }

    private async Task PurgeStandbyAsync()
    {
        var (ok, message) = await Task.Run(GamesNative.PurgeStandbyList);
        if (ok)
        {
            Log.Info($"{DisplayName}: список ожидания памяти очищен.");
            Changes.Add(new BoostChange("Память: список ожидания очищен",
                "Однократно. Свободной памяти стало больше, но «доступной» — столько же: Windows и так отдаёт её играм.", BoostChangeKind.Changed));
        }
        else
        {
            Log.Warn($"{DisplayName}: очистка списка ожидания не выполнена — {message}");
            Changes.Add(new BoostChange("Очистка памяти не выполнена", message, BoostChangeKind.Skipped));
        }
    }

    // ───────────────────────────── Watch ─────────────────────────────

    private async Task WatchAsync(GameEntry game, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        int? mainPid = null;
        var missing = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct);
                var procs = await Task.Run(() => GameProcessFinder.Find(game), ct);
                if (ct.IsCancellationRequested || !IsActive) return;

                if (mainPid is null)
                {
                    if (procs.Count > 0)
                    {
                        mainPid = procs[0].Pid;
                        await OnGameDetectedAsync(procs[0]);
                    }
                    else if (DateTime.UtcNow - started > DetectTimeout)
                    {
                        await StopAsync($"«{game.Name}» не запустилась за 3 минуты — ускорение выключено.", ToastKind.Warning);
                        return;
                    }
                    continue;
                }

                if (procs.Count == 0)
                {
                    if (++missing >= 2)
                    {
                        await StopAsync($"«{game.Name}» закрыта — ускорение выключено.", ToastKind.Success);
                        return;
                    }
                    continue;
                }

                missing = 0;
                if (procs.All(p => p.Pid != mainPid))
                {
                    // The launcher stub handed over to the real game process (or the game restarted itself).
                    mainPid = procs[0].Pid;
                    await OnGameDetectedAsync(procs[0]);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // session stopped
        }
        catch (Exception ex)
        {
            Log.Error($"{DisplayName}: ошибка слежения за игрой", ex);
        }
    }

    /// <summary>
    /// While the session owns a High performance plan: as soon as the PC runs on battery, give the original plan back
    /// (gaming_tweaks.md `power-plan-high` is desktop-only). It is not switched again when mains power returns.
    /// </summary>
    private async Task PowerWatchAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PowerPollInterval, ct);
                var onBattery = await Task.Run(GamesNative.IsOnBattery, ct);
                if (ct.IsCancellationRequested || !IsActive) return;
                if (!BoostPolicy.ShouldReleasePlanOnBattery(_sessionSwitchedPlan, onBattery)) continue;
                _powerRelease = ReleasePlanOnBatteryAsync();
                await _powerRelease;
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // session stopped
        }
        catch (Exception ex)
        {
            Log.Error($"{DisplayName}: ошибка слежения за питанием", ex);
        }
    }

    private async Task ReleasePlanOnBatteryAsync()
    {
        _sessionSwitchedPlan = false;
        var record = ChangeJournal.Instance.FindActive(PowerTarget);
        if (record is null || record.Source != Source)
        {
            // Already reverted from the Restore page, or now owned by another change — nothing of this session to undo.
            Log.Info($"{DisplayName}: питание от батареи, но план питания уже не принадлежит сеансу — не трогаю.");
            return;
        }

        RevertResult result;
        try
        {
            result = await Task.Run(() => ChangeJournal.Instance.RevertAsync([record]));
        }
        catch (Exception ex)
        {
            result = new RevertResult(0, 1, [ex.Message]);
        }

        if (result.Success)
        {
            SetPowerChange(new BoostChange("План питания возвращён",
                $"Компьютер перешёл на батарею — снова «{record.OldValue}» до конца сеанса.", BoostChangeKind.Info));
            Log.Info($"{DisplayName}: питание от батареи — план питания возвращён к «{record.OldValue}».");
            Ui.Toast($"Компьютер перешёл на батарею: план питания «{record.OldValue}» возвращён. Остальное ускорение работает до конца сеанса.",
                ToastKind.Warning, "УСКОРЕНИЕ ИГРЫ");
        }
        else
        {
            var error = string.Join("; ", result.Errors);
            Log.Warn($"{DisplayName}: питание от батареи, план питания вернуть не удалось — {error}");
            Ui.Toast($"Компьютер перешёл на батарею, но план питания вернуть не удалось: {error}. Верните его в разделе «Восстановление».",
                ToastKind.Error, "УСКОРЕНИЕ ИГРЫ");
        }
    }

    private async Task OnGameDetectedAsync(GameProcess gp)
    {
        PhaseText = "ИГРА ЗАПУЩЕНА";
        ProcessText = $"{GameProcessFinder.ExeName(gp)} · PID {gp.Pid}";
        Log.Info($"{DisplayName}: процесс игры {GameProcessFinder.ExeName(gp)} (PID {gp.Pid})");
        if (!AppSettings.Current.GameBoostPriority || _raised.ContainsKey(gp.Pid)) return;

        var r = await Task.Run(() => RaisePriority(gp.Pid));
        if (r.Handle is not null)
        {
            _raised[gp.Pid] = (r.Handle, r.Original);
            Changes.Add(new BoostChange("Приоритет игры",
                $"{GameProcessFinder.ExeName(gp)}: {GamesNative.PriorityName(r.Original)} → Высокий. Сбросится вместе с игрой.", BoostChangeKind.Changed));
            Log.Info($"{DisplayName}: приоритет {GameProcessFinder.ExeName(gp)} → Высокий");
        }
        else
        {
            Changes.Add(new BoostChange("Приоритет игры не изменён", r.Message, BoostChangeKind.Skipped));
            Log.Warn($"{DisplayName}: приоритет {GameProcessFinder.ExeName(gp)} не изменён — {r.Message}");
        }
    }

    /// <summary>Opens the game with PROCESS_SET_INFORMATION | QUERY_LIMITED only (no VM access, anti-cheat friendly).</summary>
    private static (SafeProcessHandle? Handle, uint Original, string Message) RaisePriority(int pid)
    {
        var h = GamesNative.Open(pid, GamesNative.PROCESS_SET_INFORMATION | GamesNative.PROCESS_QUERY_LIMITED_INFORMATION);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            h.Dispose();
            return (null, 0, err == 5 ? "Игра или её античит запрещают менять приоритет — игра работает как обычно." : $"Процесс недоступен (код {err}).");
        }
        var original = GamesNative.GetPriorityClass(h);
        if (original == 0)
        {
            h.Dispose();
            return (null, 0, "Не удалось прочитать текущий приоритет.");
        }
        if (original is GamesNative.HIGH_PRIORITY_CLASS or GamesNative.REALTIME_PRIORITY_CLASS)
        {
            h.Dispose();
            return (null, original, $"Приоритет уже «{GamesNative.PriorityName(original)}».");
        }
        if (!GamesNative.SetPriorityClass(h, GamesNative.HIGH_PRIORITY_CLASS))
        {
            var err = Marshal.GetLastWin32Error();
            h.Dispose();
            return (null, original, err == 5 ? "Игра или её античит запрещают менять приоритет — игра работает как обычно." : $"Windows отклонила изменение (код {err}).");
        }
        return (h, original, "ok");
    }

    // ───────────────────────────── Stop ─────────────────────────────

    public async Task StopAsync(string reason, ToastKind kind = ToastKind.Success)
    {
        if (!IsActive || IsTransitioning) return;
        IsTransitioning = true;
        try
        {
            await StopCoreAsync(reason, kind);
        }
        finally
        {
            IsTransitioning = false;
        }
    }

    private async Task StopCoreAsync(string reason, ToastKind kind)
    {
        _watchCts?.Cancel();
        _watchCts = null;
        PhaseText = "ОСТАНОВКА";
        if (_powerRelease is { } pending)
        {
            try { await pending; } catch { /* logged inside */ }
        }
        _powerRelease = null;
        _sessionSwitchedPlan = false;

        var restoredPriority = await Task.Run(RestorePriorities);
        RevertResult result;
        try
        {
            result = await Task.Run(() => ChangeJournal.Instance.RevertSourceAsync(Source));
        }
        catch (Exception ex)
        {
            Log.Error($"{DisplayName}: ошибка отката", ex);
            result = new RevertResult(0, 1, [ex.Message]);
        }

        IsActive = false;
        PhaseText = "";
        ProcessText = null;

        var msg = BoostPolicy.EndMessage(reason, result.Reverted, result.Failed, restoredPriority, _closedApps);
        Log.Info($"{DisplayName}: сеанс завершён. " + msg);
        Ui.Toast(msg, result.Failed > 0 ? ToastKind.Warning : kind, "УСКОРЕНИЕ ВЫКЛЮЧЕНО");
        Ended?.Invoke();
    }

    /// <summary>Returns the number of still-running processes whose priority was put back.</summary>
    private int RestorePriorities()
    {
        var restored = 0;
        foreach (var (_, (handle, original)) in _raised.ToList())
        {
            try
            {
                using (handle)
                {
                    // Fails harmlessly when the process has already exited.
                    if (GamesNative.GetPriorityClass(handle) == GamesNative.HIGH_PRIORITY_CLASS &&
                        GamesNative.SetPriorityClass(handle, original))
                        restored++;
                }
            }
            catch { }
        }
        _raised.Clear();
        return restored;
    }

    /// <summary>MONOLITH is closing during a session: nobody would watch the game any more, so undo everything now.</summary>
    public void StopOnExit()
    {
        if (!IsActive) return;
        try
        {
            _watchCts?.Cancel();
            RestorePriorities();
            Task.Run(() => ChangeJournal.Instance.RevertSourceAsync(Source)).Wait(TimeSpan.FromSeconds(5));
            Log.Info($"{DisplayName}: MONOLITH закрыт во время сеанса — изменения сеанса отменены.");
        }
        catch (Exception ex)
        {
            Log.Error($"{DisplayName}: не удалось отменить изменения при выходе", ex);
        }
        IsActive = false;
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return "?"; }
    }

    private static bool IsAlive(Process p)
    {
        try
        {
            return !p.HasExited;
        }
        catch
        {
            try
            {
                using var again = Process.GetProcessById(p.Id);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
