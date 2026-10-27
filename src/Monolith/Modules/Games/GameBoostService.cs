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
/// Game Boost session. Temporary changes only:
///  • power plan → High performance (journaled under source "gameboost" so the Restore page can undo it after a crash);
///  • close the user's chosen background apps (window close first, kill only after explicit confirmation);
///  • game process priority → High (never Realtime), restored when the session ends;
///  • optional one-time standby-list purge (opt-in, honest explanation).
/// Stops manually or automatically when the game process exits; everything journaled is reverted.
/// All public members are called on the UI thread.
/// </summary>
public sealed partial class GameBoostService : ObservableObject
{
    public const string Source = "gameboost";
    private const string SessionTitle = "Игровой сеанс";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DetectTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Raised game processes. Guarded by <see cref="_raisedLock"/>: filled on a pool thread, emptied by every stop path.</summary>
    private readonly Dictionary<int, (SafeProcessHandle Handle, uint Original)> _raised = new();
    private readonly object _raisedLock = new();
    private readonly List<string> _closedApps = [];
    private CancellationTokenSource? _watchCts;
    /// <summary>
    /// True from the first change of a session until it was reverted — also during the start phase, before
    /// <see cref="IsActive"/> (the power plan is already switched while apps are still being closed).
    /// </summary>
    private bool _sessionOpen;
    /// <summary>Power-plan switch or priority raise still running on a pool thread; an exit waits for it before reverting.</summary>
    private Task? _pendingChange;

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

    /// <summary>Decides the power plan action for this PC right now. Blocking.</summary>
    public static (PowerPlanAction Action, string Text) DecidePower(SystemProfile sp, bool enabled)
    {
        var active = PowerSchemes.GetActive();
        var activeName = active == Guid.Empty ? "текущая схема" : PowerSchemes.GetName(active);
        var ultimate = enabled ? PowerSchemes.FindUltimateCopy() : null;
        var alreadyFast = active == PowerSchemes.HighPerformance || (ultimate is { } u && u == active);
        var journaled = ChangeJournal.Instance.FindActive("power:active");
        var managedElsewhere = journaled is not null && journaled.Source != Source;
        // Unknown hardware (a WMI query failed): never switch blindly — it could be a laptop or a dual-CCD X3D.
        var action = enabled && !sp.HardwareFactsKnown ? PowerPlanAction.SkipUnknownHardware : BoostPolicy.DecidePowerPlan(
            enabled,
            BoostPolicy.IsDualCcdX3D(sp.CpuName, sp.CpuCores),
            GamesNative.IsOnBattery(),
            sp.IsLaptop,
            GamesNative.IsModernStandby(),
            managedElsewhere,
            enabled && PowerSchemes.Exists(PowerSchemes.HighPerformance),
            alreadyFast);
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
        Elevation.Demand("Игровой сеанс");
        IsTransitioning = true;
        _sessionOpen = true;
        Changes.Clear();
        _closedApps.Clear();
        HasGame = game is not null;
        GameName = game?.Name ?? "Без привязки к игре";
        ProcessText = null;
        PhaseText = "ЗАПУСК";
        try
        {
            if (plan.PowerAction == PowerPlanAction.Switch) await SwitchPowerPlanAsync();
            else Changes.Add(new BoostChange("План питания не изменён", plan.PowerText.Replace("План питания не меняется: ", ""), BoostChangeKind.Skipped));

            if (plan.AppsToClose.Count > 0) await CloseAppsAsync(plan, game);

            if (plan.PurgeStandby) await PurgeStandbyAsync();

            IsActive = true;
            StartedAt = DateTime.Now;
            Log.Success($"Игровой сеанс начат{(game is null ? "" : $" для «{game.Name}»")}.");

            if (game is null)
            {
                PhaseText = "АКТИВЕН";
                return true;
            }

            int? pid;
            try
            {
                pid = await Task.Run(() => GameLauncher.LaunchAsync(game));
            }
            catch (Exception ex)
            {
                Log.Error($"Не удалось запустить «{game.Name}»", ex);
                await StopCoreAsync($"«{game.Name}» не запустилась: {ex.Message}", ToastKind.Error);
                return false;
            }

            PhaseText = "ОЖИДАНИЕ ИГРЫ";
            Changes.Add(new BoostChange("Игра запущена", "Ожидание процесса игры (до 3 минут) — затем приоритет и слежение за выходом.", BoostChangeKind.Info));
            _watchCts = new CancellationTokenSource();
            _ = WatchAsync(game, pid, _watchCts.Token);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Игровой сеанс: ошибка запуска", ex);
            Ui.Toast(ex.Message, ToastKind.Error, "ИГРОВОЙ СЕАНС");
            if (IsActive || ChangeJournal.Instance.HasActiveSource(Source))
                await StopCoreAsync("Запуск игрового сеанса прерван — изменения отменены.", ToastKind.Warning);
            return false;
        }
        finally
        {
            IsTransitioning = false;
            // Not started (or already stopped above): nothing of this session is left to revert.
            if (!IsActive) _sessionOpen = false;
        }
    }

    private async Task SwitchPowerPlanAsync()
    {
        var work = Task.Run(async () =>
        {
            var fromName = PowerSchemes.GetName(PowerSchemes.GetActive());
            var session = await ChangeSession.BeginAsync(SessionTitle, createRestorePoint: false).ConfigureAwait(false);
            var r = session.SetPowerScheme(PowerSchemes.HighPerformance, Source, "Игровой сеанс: план питания");
            session.Complete();
            return (r, fromName, PowerSchemes.GetName(PowerSchemes.HighPerformance), session.Errors.FirstOrDefault());
        });
        _pendingChange = work;
        var (ok, from, to, error) = await work;
        Changes.Add(ok
            ? new BoostChange("План питания", $"«{from}» → «{to}». Вернётся после игры.", BoostChangeKind.Changed)
            : new BoostChange("План питания не изменён", error ?? "Windows отклонила смену схемы.", BoostChangeKind.Skipped));
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
                    "Завершить их принудительно? Несохранённые данные в них будут потеряны.",
                    details, "ЗАВЕРШИТЬ", "ОСТАВИТЬ ОТКРЫТЫМИ", danger: true,
                    footnote: "Игровой сеанс продолжится при любом выборе.");
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
                Changes.Add(new BoostChange("Закрыто приложений: " + closed.Count, string.Join(", ", closed) + ". После игры MONOLITH их не открывает.", BoostChangeKind.Changed));
                Log.Info("Игровой сеанс: закрыты " + string.Join(", ", closed));
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
            Log.Info("Игровой сеанс: список ожидания памяти очищен.");
            Changes.Add(new BoostChange("Память: список ожидания очищен",
                "Однократно. Свободной памяти стало больше, но «доступной» — столько же: Windows и так отдаёт её играм.", BoostChangeKind.Changed));
        }
        else
        {
            Log.Warn($"Игровой сеанс: очистка списка ожидания не выполнена — {message}");
            Changes.Add(new BoostChange("Очистка памяти не выполнена", message, BoostChangeKind.Skipped));
        }
    }

    // ───────────────────────────── Watch ─────────────────────────────

    private async Task WatchAsync(GameEntry game, int? launchedPid, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        int? mainPid = null;
        var missing = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, ct);
                var procs = await Task.Run(() => GameProcessFinder.Find(game, launchedPid), ct);
                if (ct.IsCancellationRequested || !IsActive) return;

                if (mainPid is null)
                {
                    if (procs.Count > 0)
                    {
                        mainPid = procs[0].Pid;
                        await OnGameDetectedAsync(procs[0], ct);
                    }
                    else if (DateTime.UtcNow - started > DetectTimeout)
                    {
                        await StopAsync($"«{game.Name}» не запустилась за 3 минуты — игровой сеанс завершён, изменения отменены.", ToastKind.Warning);
                        return;
                    }
                    continue;
                }

                if (procs.Count == 0)
                {
                    if (++missing >= 2)
                    {
                        await StopAsync($"«{game.Name}» закрыта — игровой сеанс завершён, изменения отменены.", ToastKind.Success);
                        return;
                    }
                    continue;
                }

                missing = 0;
                if (procs.All(p => p.Pid != mainPid))
                {
                    // The launcher stub handed over to the real game process (or the game restarted itself).
                    mainPid = procs[0].Pid;
                    await OnGameDetectedAsync(procs[0], ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // session stopped
        }
        catch (Exception ex)
        {
            Log.Error("Игровой сеанс: ошибка слежения за игрой", ex);
            // Nobody would notice the game closing any more: end the session now instead of leaving its changes behind.
            if (!ct.IsCancellationRequested)
                await StopAsync("Слежение за игрой прервалось из-за ошибки — игровой сеанс завершён, изменения отменены.", ToastKind.Warning);
        }
    }

    private async Task OnGameDetectedAsync(GameProcess gp, CancellationToken ct)
    {
        PhaseText = "ИГРА ЗАПУЩЕНА";
        ProcessText = $"{GameProcessFinder.ExeName(gp)} · PID {gp.Pid}";
        Log.Info($"Игровой сеанс: процесс игры {GameProcessFinder.ExeName(gp)} (PID {gp.Pid})");
        if (!AppSettings.Current.GameBoostPriority) return;
        lock (_raisedLock)
            if (_raised.ContainsKey(gp.Pid)) return;

        var work = Task.Run(() => RaisePriority(gp.Pid, ct));
        _pendingChange = work;
        var r = await work;
        // The session ended meanwhile: RaisePriority or the stop path has already put the priority back.
        if (ct.IsCancellationRequested) return;
        if (r.Handle is not null)
        {
            Changes.Add(new BoostChange("Приоритет игры",
                $"{GameProcessFinder.ExeName(gp)}: {GamesNative.PriorityName(r.Original)} → Высокий. Сбросится вместе с игрой.", BoostChangeKind.Changed));
            Log.Info($"Игровой сеанс: приоритет {GameProcessFinder.ExeName(gp)} → Высокий");
        }
        else
        {
            Changes.Add(new BoostChange("Приоритет игры не изменён", r.Message, BoostChangeKind.Skipped));
            Log.Warn($"Игровой сеанс: приоритет {GameProcessFinder.ExeName(gp)} не изменён — {r.Message}");
        }
    }

    /// <summary>
    /// Opens the game with PROCESS_SET_INFORMATION | QUERY_LIMITED only (no VM access, anti-cheat friendly), raises it
    /// to High and registers the handle for the stop paths. Every stop cancels <paramref name="ct"/> before it empties
    /// <see cref="_raised"/>, so the check under the lock either hands the handle to that stop or puts the priority
    /// back right here: a STOP during the raise never leaves the game at High.
    /// </summary>
    private (SafeProcessHandle? Handle, uint Original, string Message) RaisePriority(int pid, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return (null, 0, "Игровой сеанс уже завершён.");
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
        lock (_raisedLock)
        {
            if (!ct.IsCancellationRequested)
            {
                _raised[pid] = (h, original);
                return (h, original, "ok");
            }
        }
        // Stopped while raising: undo at once.
        using (h) RestoreOne(h, original);
        return (null, original, "Игровой сеанс уже завершён — приоритет возвращён.");
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

        var raised = TakeRaised();
        var restoredPriority = await Task.Run(() => RestorePriorities(raised));
        RevertResult result;
        try
        {
            result = await Task.Run(() => ChangeJournal.Instance.RevertSourceAsync(Source));
        }
        catch (Exception ex)
        {
            Log.Error("Игровой сеанс: ошибка отката", ex);
            result = new RevertResult(0, 1, [ex.Message]);
        }

        IsActive = false;
        _sessionOpen = false;
        PhaseText = "";
        ProcessText = null;

        var msg = reason;
        if (restoredPriority > 0) msg += " Приоритет игры возвращён.";
        if (result.Failed > 0) msg += $" Не удалось отменить изменений: {result.Failed} — откройте раздел «Восстановление».";
        if (_closedApps.Count > 0)
            msg += $" Закрытые приложения ({string.Join(", ", _closedApps)}) MONOLITH не перезапускает — откройте их сами, если нужны.";
        Log.Info("Игровой сеанс завершён. " + msg);
        Ui.Toast(msg, result.Failed > 0 ? ToastKind.Warning : kind, "ИГРОВОЙ СЕАНС ЗАВЕРШЁН");
        Ended?.Invoke();
    }

    /// <summary>Takes every raised process out of the session at once, so two stop paths never restore the same handle.</summary>
    private List<(SafeProcessHandle Handle, uint Original)> TakeRaised()
    {
        lock (_raisedLock)
        {
            var list = _raised.Values.ToList();
            _raised.Clear();
            return list;
        }
    }

    /// <summary>Returns the number of still-running processes whose priority was put back.</summary>
    private static int RestorePriorities(List<(SafeProcessHandle Handle, uint Original)> raised)
    {
        var restored = 0;
        foreach (var (handle, original) in raised)
        {
            try
            {
                using (handle)
                    if (RestoreOne(handle, original)) restored++;
            }
            catch { }
        }
        return restored;
    }

    /// <summary>Puts the original class back while the game is still at High (fails harmlessly when it has exited).</summary>
    private static bool RestoreOne(SafeProcessHandle handle, uint original)
    {
        try
        {
            return GamesNative.GetPriorityClass(handle) == GamesNative.HIGH_PRIORITY_CLASS &&
                   GamesNative.SetPriorityClass(handle, original);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// MONOLITH is closing during a session — also during the start phase (power plan already switched, apps still
    /// being closed): nobody would watch the game any more, so undo everything now.
    /// </summary>
    public void StopOnExit()
    {
        if (!IsActive && !_sessionOpen) return;
        try
        {
            _watchCts?.Cancel();
            // A power-plan switch or priority raise still on a pool thread: let it finish, so its journal record exists
            // and its handle is registered (or already put back) before the undo below.
            try { _pendingChange?.Wait(TimeSpan.FromSeconds(5)); }
            catch { /* its own error handling reports it */ }
            RestorePriorities(TakeRaised());
            Task.Run(() => ChangeJournal.Instance.RevertSourceAsync(Source)).Wait(TimeSpan.FromSeconds(5));
            Log.Info("Игровой сеанс: MONOLITH закрыт во время сеанса — изменения отменены.");
        }
        catch (Exception ex)
        {
            Log.Error("Игровой сеанс: не удалось отменить изменения при выходе", ex);
        }
        _sessionOpen = false;
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
