using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Monolith.Core;
using Monolith.Tweaks;
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
///  • power plan → High performance on desktops; where the plan cannot be switched (laptop, Modern Standby, no such plan)
///    the Windows 11 AC power mode → «Максимальная производительность» instead (both journaled under source "gameboost"
///    so the Restore page can undo them after a crash);
///  • close the user's chosen background apps (window close first, kill only after explicit confirmation) — or, on
///    Windows 11 when the user chose so, keep them running in efficiency mode (EcoQoS, <see cref="EcoQos"/>): the original
///    PROCESS_POWER_THROTTLING_STATE of every tagged process is written back when the session ends, a marker in the
///    admin-only machine folder lets the next start reset leftovers after a crash;
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
    /// <summary>
    /// Close-list processes put into efficiency mode, with the exact state read before the change. Guarded by
    /// <see cref="_raisedLock"/> like <see cref="_raised"/>: filled on a pool thread, emptied by every stop path.
    /// </summary>
    private readonly Dictionary<int, (SafeProcessHandle Handle, PowerThrottlingState Original, DateTime StartUtc, string Name)> _throttled = new();
    private readonly List<string> _closedApps = [];
    private readonly List<string> _throttledApps = [];
    private CancellationTokenSource? _watchCts;
    /// <summary>
    /// True from the first change of a session until it was reverted — also during the start phase, before
    /// <see cref="IsActive"/> (the power plan is already switched while apps are still being closed).
    /// </summary>
    private bool _sessionOpen;
    /// <summary>Power-plan switch or priority raise still running on a pool thread; an exit waits for it before reverting.</summary>
    private Task? _pendingChange;
    /// <summary>Journal target the session's power change wrote ("power:active" or "cmd:powermode:ac"); null when none.</summary>
    private string? _powerTarget;

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

    /// <summary>Journal target of the Windows 11 AC power mode (the same record the Optimize power-mode items write).</summary>
    public const string PowerModeTarget = "powermode:ac";
    private const string PowerModeTitle = "Игровой сеанс: режим питания";

    /// <summary>
    /// Decides the power action for this PC right now: the plan switch on desktops, else (laptop, Modern Standby, no High
    /// performance plan) the Windows 11 AC power mode on a Balanced-personality plan. Blocking.
    /// </summary>
    public static (PowerPlanAction Action, string Text) DecidePower(SystemProfile sp, bool enabled)
    {
        var active = PowerSchemes.GetActive();
        var activeName = active == Guid.Empty ? "текущая схема" : PowerSchemes.GetName(active);
        var ultimate = enabled ? PowerSchemes.FindUltimateCopy() : null;
        var alreadyFast = active == PowerSchemes.HighPerformance || (ultimate is { } u && u == active);
        var journaled = ChangeJournal.Instance.FindActive("power:active");
        var managedElsewhere = journaled is not null && journaled.Source != Source;
        var dualCcd = BoostPolicy.IsDualCcdX3D(sp.CpuName, sp.CpuCores);
        var onBattery = GamesNative.IsOnBattery();
        // Unknown hardware (a WMI query failed): never switch blindly — it could be a laptop or a dual-CCD X3D.
        if (enabled && !sp.HardwareFactsKnown)
            return (PowerPlanAction.SkipUnknownHardware, BoostPolicy.Describe(PowerPlanAction.SkipUnknownHardware, activeName, sp.CpuName));

        var plan = BoostPolicy.DecidePowerPlan(
            enabled,
            dualCcd,
            onBattery,
            sp.IsLaptop,
            GamesNative.IsModernStandby(),
            managedElsewhere,
            enabled && PowerSchemes.Exists(PowerSchemes.HighPerformance),
            alreadyFast);

        // The power mode is read only where it may be needed (the plan is not switched here).
        Guid? currentMode = null;
        var mode = PowerPlanAction.SkipUnavailable;
        if (BoostPolicy.PlanFallsBackToMode(plan))
        {
            currentMode = TweakCatalog.ReadAcPowerMode();
            var modeRecord = ChangeJournal.Instance.FindActive("cmd:" + PowerModeTarget);
            // A record of this source is a leftover session (the Games page offers to undo it); only another source "manages" the mode.
            var modeManagedElsewhere = modeRecord is not null && modeRecord.Source != Source;
            mode = BoostPolicy.DecidePowerMode(
                enabled,
                dualCcd,
                onBattery,
                sp.IsWindows11,
                active != Guid.Empty && PowerSchemes.IsBalancedPersonality(active),
                IsEnergySaverOn(),
                currentMode == TweakCatalog.PowerModeBestPerformance,
                modeManagedElsewhere);
        }
        // An unreadable or undocumented current mode is a skip here already — the confirmation never promises a switch that
        // SwitchPowerModeAsync would refuse (and never shows a raw GUID as the "current mode").
        var action = BoostPolicy.Decide(plan, mode, currentMode);
        // No change without an undo the journal will accept later: probe the record shape before anything is promised.
        if (action == PowerPlanAction.SwitchPowerMode && currentMode is { } probe && PowerModeUntrusted(probe) is { } refusal)
        {
            Log.Warn($"Игровой сеанс: журнал не принял бы отмену смены режима питания ({refusal}) — режим не меняется.");
            action = PowerPlanAction.SkipModeUntrusted;
        }
        var modeName = currentMode is { } m && TweakCatalog.IsDocumentedPowerMode(m) ? TweakCatalog.DescribePowerMode(m) : null;
        return (action, BoostPolicy.Describe(action, activeName, sp.CpuName, modeName, sp.IsLaptop, sp.IsWindows11));
    }

    /// <summary>
    /// The journaled command that sets the AC power mode to «Максимальная производительность» for the session, with its exact
    /// undo (the current mode, one of the three documented GUIDs). Built with the catalog code, so the undo is the same
    /// command the Optimize power-mode items record and the journal check accepts for this source and target.
    /// </summary>
    internal static (string Target, string File, string Args, string RevertFile, string RevertArgs, string OldValue, string NewValue, string Title)
        PowerModeCommand(Guid current)
    {
        if (!TweakCatalog.IsDocumentedPowerMode(current))
            throw new ArgumentException($"недокументированный режим питания: {current:D}", nameof(current));
        return (PowerModeTarget,
            TweakCatalog.PowerShellExe, TweakCatalog.PowerShellArgs(TweakCatalog.PowerModeScript(TweakCatalog.PowerModeBestPerformance)),
            TweakCatalog.PowerShellExe, TweakCatalog.PowerShellArgs(TweakCatalog.PowerModeScript(current)),
            $"от сети: {TweakCatalog.DescribePowerMode(current)}", $"от сети: {TweakCatalog.DescribePowerMode(TweakCatalog.PowerModeBestPerformance)}",
            PowerModeTitle);
    }

    /// <summary>
    /// The session's power-mode record exactly as <see cref="ChangeSession.RunCommandAsync"/> stores it (Target with the "cmd:"
    /// prefix, no account: the AC mode is machine-wide). Used as the JournalTrust probe before any change and by the tests.
    /// </summary>
    internal static ChangeRecord PowerModeRecord(Guid current)
    {
        var c = PowerModeCommand(current);
        return new ChangeRecord
        {
            Source = Source, Title = c.Title, Kind = ChangeKind.Command, Target = "cmd:" + c.Target,
            OldValue = c.OldValue, NewValue = c.NewValue, RevertFile = c.RevertFile, RevertArgs = c.RevertArgs,
        };
    }

    /// <summary>
    /// Null when the journal accepts the undo of a session mode record whose original is <paramref name="current"/> (one of the
    /// three documented modes); otherwise the refusal. Checked before the mode is promised and again before it is written, so a
    /// build whose journal rules do not know this record can never leave «Максимальная производительность» behind.
    /// </summary>
    internal static string? PowerModeUntrusted(Guid current) => JournalTrust.Check(PowerModeRecord(current));

    /// <summary>Energy saver (battery saver) is on: SYSTEM_POWER_STATUS.SystemStatusFlag = 1. Windows then ignores the performance mode.</summary>
    private static bool IsEnergySaverOn()
    {
        try
        {
            return GetSystemPowerStatus(out var s) && s.SystemStatusFlag == 1;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>What a session would do now (power plan, which running apps would be closed…).</summary>
    public static async Task<BoostPlan> BuildPlanAsync(GameEntry? game)
    {
        var sp = await SystemProfile.LoadAsync();
        var s = AppSettings.Current;
        var closeList = (s.GameBoostCloseList ?? []).ToList();
        var powerEnabled = s.GameBoostPowerPlan;
        var priority = s.GameBoostPriority;
        var purge = s.GameBoostPurgeStandby;
        // EcoQoS exists since Windows 11 only (before it the same call meant LowQoS): Windows 10 always closes.
        var closeMode = s.GameBoostUsesEcoQos && sp.IsWindows11 ? BoostCloseMode.EcoQos : BoostCloseMode.Close;
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
            return new BoostPlan(action, text, apps, notRunning, priority && game is not null, purge, closeMode);
        });
    }

    // ───────────────────────────── Start ─────────────────────────────

    /// <summary>Starts the session as one disruptive operation (not next to an installer, a repair or another disruptive change).</summary>
    public async Task<bool> StartAsync(GameEntry? game, BoostPlan plan)
    {
        if (IsActive || IsTransitioning) return false;
        try
        {
            return await OperationCoordinator.Instance.RunAsync(
                new OperationRequest(OperationClass.Disruptive, game is null ? "Запуск игрового сеанса" : $"Запуск игрового сеанса: {game.Name}") { Owner = "games" },
                _ => StartCoreAsync(game, plan));
        }
        catch (Exception ex) when (ex is OperationRefusedException or OperationTimeoutException)
        {
            Log.Warn("Игровой сеанс не начат: " + ex.Message);
            Ui.Toast(ex.Message, ToastKind.Warning, "ИГРОВОЙ СЕАНС");
            return false;
        }
    }

    private async Task<bool> StartCoreAsync(GameEntry? game, BoostPlan plan)
    {
        if (IsActive || IsTransitioning) return false;
        Elevation.Demand("Игровой сеанс");
        IsTransitioning = true;
        _sessionOpen = true;
        Changes.Clear();
        _closedApps.Clear();
        _throttledApps.Clear();
        _powerTarget = null;
        HasGame = game is not null;
        GameName = game?.Name ?? "Без привязки к игре";
        ProcessText = null;
        PhaseText = "ЗАПУСК";
        try
        {
            if (plan.PowerAction == PowerPlanAction.Switch) await SwitchPowerPlanAsync();
            else if (plan.PowerAction == PowerPlanAction.SwitchPowerMode) await SwitchPowerModeAsync();
            else Changes.Add(new BoostChange("План питания не изменён", plan.PowerText.Replace(BoostPolicy.SkipPrefix, ""), BoostChangeKind.Skipped));

            if (plan.AppsToClose.Count > 0)
            {
                if (plan.CloseMode == BoostCloseMode.EcoQos) await ThrottleAppsAsync(plan, game);
                else await CloseAppsAsync(plan, game);
            }

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
            if (IsActive || ChangeJournal.Instance.HasActiveSource(Source) || HasThrottled())
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
        if (ok) _powerTarget = "power:active";
        Changes.Add(ok
            ? new BoostChange("План питания", $"«{from}» → «{to}». Вернётся после игры.", BoostChangeKind.Changed)
            : new BoostChange("План питания не изменён", error ?? "Windows отклонила смену схемы.", BoostChangeKind.Skipped));
    }

    /// <summary>
    /// Sets the Windows 11 AC power mode to «Максимальная производительность» for the session (the plan stays). Journaled under
    /// <see cref="Source"/> with the current mode as the exact undo, so the stop paths and the stale-session recovery revert it
    /// like the plan. Only one of the three documented modes can be recorded; anything else is refused.
    /// </summary>
    private async Task SwitchPowerModeAsync()
    {
        var work = Task.Run(async () =>
        {
            // The plan was decided from a snapshot: re-check everything the record depends on right before writing.
            var current = TweakCatalog.ReadAcPowerMode();
            if (current is not { } from || !TweakCatalog.IsDocumentedPowerMode(from))
                return (false, "", "Windows сообщила неизвестный режим питания — MONOLITH его не меняет.");
            if (from == TweakCatalog.PowerModeBestPerformance)
                return (false, TweakCatalog.DescribePowerMode(from), "режим питания от сети уже «Максимальная производительность» — ничего не изменено.");
            if (PowerModeUntrusted(from) is { } refusal)
                return (false, "", $"журнал не принял бы отмену такого изменения ({refusal}) — ничего не изменено.");
            var cmd = PowerModeCommand(from);
            var session = await ChangeSession.BeginAsync(SessionTitle, createRestorePoint: false).ConfigureAwait(false);
            var ok = await session.RunCommandAsync(cmd.Target, cmd.File, cmd.Args, cmd.RevertFile, cmd.RevertArgs,
                cmd.OldValue, cmd.NewValue, Source, cmd.Title, OutputEncoding.Utf8).ConfigureAwait(false);
            session.Complete();
            return (ok, TweakCatalog.DescribePowerMode(from), session.Errors.FirstOrDefault());
        });
        _pendingChange = work;
        var (ok, from, error) = await work;
        if (ok) _powerTarget = "cmd:" + PowerModeTarget;
        Changes.Add(ok
            ? new BoostChange("Режим питания от сети",$"«{Cap(from)}» → «Максимальная производительность». Схема питания не меняется; вернётся после игры.", BoostChangeKind.Changed)
            : new BoostChange("Режим питания не изменён", error ?? "Windows отклонила смену режима питания.", BoostChangeKind.Skipped));
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

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

    /// <summary>
    /// Efficiency mode instead of closing (Windows 11): for every close-list process that is not excluded, reads its
    /// PROCESS_POWER_THROTTLING_STATE, skips the ones that already control EXECUTION_SPEED themselves (Edge, Chrome), adds
    /// EXECUTION_SPEED to that state (other bits kept) and keeps handle + original state for the exact restore. Priority is never touched. The crash marker is written
    /// as soon as the first process is tagged and updated with every further one.
    /// </summary>
    private async Task ThrottleAppsAsync(BoostPlan plan, GameEntry? game)
    {
        var names = plan.AppsToClose.Select(a => a.Name).ToList();
        string? recovered = null;
        var work = Task.Run(() =>
        {
            // Leftovers of a crashed session first: the new marker would replace the old one and the old processes would look self-managed.
            if (!HasThrottled())
            {
                try { recovered = RecoverStaleEcoQos(); }
                catch (Exception ex) { Log.Warn($"Игровой сеанс: не удалось снять режим эффективности прошлого сеанса — {ex.Message}"); }
            }
            return ThrottleProcesses(names, game?.InstallDir);
        });
        _pendingChange = work;
        var (tagged, skipped) = await work;
        if (recovered is not null)
            Changes.Add(new BoostChange("Режим эффективности прошлого сеанса", recovered, BoostChangeKind.Info));

        if (tagged.Count == 0 && skipped.Count == 0)
        {
            Changes.Add(new BoostChange("Режим эффективности", "Приложения из списка уже закрыты — назначать режим некому.", BoostChangeKind.Info));
            return;
        }
        foreach (var g in tagged.GroupBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            _throttledApps.Add(g.Key);
            Changes.Add(new BoostChange($"{g.Key}: режим эффективности",
                (g.Count() > 1 ? $"Процессов: {g.Count()}. " : "") + "Программа работает дальше на экономных частотах; после игры вернётся прежнее состояние.",
                BoostChangeKind.Changed));
        }
        foreach (var (name, reason) in skipped.Distinct())
            Changes.Add(new BoostChange($"{name}: пропущено — {reason}", "Программа работает как обычно.", BoostChangeKind.Skipped));
        if (tagged.Count > 0)
            Log.Info("Игровой сеанс: режим эффективности для " + string.Join(", ", tagged.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>Blocking part of <see cref="ThrottleAppsAsync"/>. Returns the tagged names (one per process) and the skipped (name, reason) pairs.</summary>
    private (List<string> Tagged, List<(string Name, string Reason)> Skipped) ThrottleProcesses(List<string> names, string? gameDir)
    {
        var tagged = new List<string>();
        var skipped = new List<(string, string)>();
        var targets = UserProcesses.FindClosable(names, gameDir);
        try
        {
            if (targets.Count == 0) return (tagged, skipped);
            var audio = EcoQos.ActiveAudioPids();
            int self;
            using (var me = Process.GetCurrentProcess()) self = me.Id;
            foreach (var p in targets)
            {
                var name = SafeName(p);
                int pid;
                try { pid = p.Id; } catch { continue; }
                if (EcoQos.ExclusionReason(name, pid, self, audio) is { } reason)
                {
                    skipped.Add((name, reason));
                    continue;
                }
                var h = GamesNative.Open(pid, GamesNative.PROCESS_SET_INFORMATION | GamesNative.PROCESS_QUERY_LIMITED_INFORMATION);
                if (h.IsInvalid)
                {
                    var err = Marshal.GetLastWin32Error();
                    h.Dispose();
                    skipped.Add((name, err == 5 ? "процесс защищён от изменений" : $"процесс недоступен (код {err})"));
                    continue;
                }
                var original = EcoQos.ReadState(h);
                var start = EcoQos.StartTimeUtc(h);
                if (original is not { } orig || start is not { } startUtc)
                {
                    h.Dispose();
                    skipped.Add((name, "Windows не сообщила текущее состояние — без него нет точного возврата"));
                    continue;
                }
                if (EcoQos.IsSelfManaged(orig))
                {
                    h.Dispose();
                    skipped.Add((name, EcoQos.IsEco(orig) ? "уже управляет режимом сам (режим эффективности включён)" : "уже управляет режимом сам"));
                    continue;
                }
                if (!EcoQos.WriteState(h, EcoQos.PlanApply(orig)))
                {
                    var err = Marshal.GetLastWin32Error();
                    h.Dispose();
                    skipped.Add((name, err == 5 ? "процесс защищён от изменений" : $"Windows отклонила изменение (код {err})"));
                    continue;
                }
                List<EcoQosMarkerEntry> marker;
                lock (_raisedLock)
                {
                    _throttled[pid] = (h, orig, startUtc, name);
                    marker = _throttled.Select(kv => new EcoQosMarkerEntry(kv.Key, kv.Value.StartUtc)).ToList();
                }
                EcoQos.WriteMarker(marker);
                tagged.Add(name);
            }
        }
        finally
        {
            foreach (var p in targets) p.Dispose();
        }
        return (tagged, skipped);
    }

    private bool HasThrottled()
    {
        lock (_raisedLock) return _throttled.Count > 0;
    }

    /// <summary>Takes every throttled process out of the session at once, so two stop paths never restore the same handle.</summary>
    private List<(SafeProcessHandle Handle, PowerThrottlingState Original, DateTime StartUtc, string Name)> TakeThrottled()
    {
        lock (_raisedLock)
        {
            var list = _throttled.Values.ToList();
            _throttled.Clear();
            return list;
        }
    }

    /// <summary>
    /// Writes the ORIGINAL throttling state back to every tagged process that is still running (the handle keeps the PID from
    /// being reused, so the start time is already proven) and whose state is still the one MONOLITH wrote: if the app changed
    /// it itself meanwhile, its newer choice stays. The marker is deleted unless a write failed.
    /// </summary>
    private static EcoRestoreResult RestoreThrottled(List<(SafeProcessHandle Handle, PowerThrottlingState Original, DateTime StartUtc, string Name)> throttled)
    {
        int restored = 0, exited = 0, failed = 0, changed = 0;
        foreach (var (handle, original, _, name) in throttled)
        {
            try
            {
                using (handle)
                {
                    if (!EcoQos.IsRunning(handle)) { exited++; continue; }
                    if (EcoQos.ReadState(handle) is { } now && now != EcoQos.PlanApply(original))
                    {
                        changed++;
                        Log.Info($"Игровой сеанс: {name} сама сменила режим — не трогаем.");
                        continue;
                    }
                    if (EcoQos.WriteState(handle, EcoQos.PlanRestore(original))) restored++;
                    else
                    {
                        failed++;
                        Log.Warn($"Игровой сеанс: не удалось вернуть режим {name} (код {Marshal.GetLastWin32Error()}).");
                    }
                }
            }
            catch { failed++; }
        }
        if (throttled.Count > 0 && failed == 0) EcoQos.DeleteMarker();
        return new EcoRestoreResult(restored, exited, failed, changed);
    }

    private readonly record struct EcoRestoreResult(int Restored, int Exited, int Failed, int Changed);

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

    /// <summary>
    /// Stops the session and puts everything back. Never refused: when something else is running (an installer) the stop
    /// waits in the visible queue — dropping it would leave the game tweaks switched on.
    /// </summary>
    public async Task StopAsync(string reason, ToastKind kind = ToastKind.Success)
    {
        if (!IsActive || IsTransitioning) return;
        try
        {
            await OperationCoordinator.Instance.RunAsync(
                new OperationRequest(OperationClass.Disruptive, "Остановка игрового сеанса") { Owner = "games", QueueIfBusy = true, QueueTimeout = TimeSpan.FromHours(4) },
                _ => StopGatedAsync(reason, kind));
        }
        catch (Exception ex) when (ex is OperationRefusedException or OperationTimeoutException)
        {
            // The window is closing or the queue never moved: the stale-session check offers the undo at the next start.
            Log.Warn("Игровой сеанс не остановлен сразу: " + ex.Message);
            Ui.Toast(ex.Message, ToastKind.Warning, "ИГРОВОЙ СЕАНС");
        }
    }

    private async Task StopGatedAsync(string reason, ToastKind kind)
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
        var throttled = TakeThrottled();
        var (restoredPriority, restoredEco) = await Task.Run(() => (RestorePriorities(raised), RestoreThrottled(throttled)));
        RevertResult result;
        ChangeRecord? planNow = null;
        try
        {
            result = await Task.Run(() => ChangeJournal.Instance.RevertSourceAsync(Source));
            // A plan or power mode chosen during the session (e.g. in Optimize) took over the session's original: it stays.
            if (result.HandedOver > 0 && _powerTarget is { } target) planNow = ChangeJournal.Instance.FindActive(target);
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
        if (KeptPlanNote(result, planNow) is { } kept) msg += " " + kept;
        if (restoredPriority > 0) msg += " Приоритет игры возвращён.";
        if (throttled.Count > 0)
        {
            if (restoredEco.Restored == throttled.Count)
                msg += $" Режим эффективности снят ({string.Join(", ", _throttledApps)}).";
            else
            {
                msg += $" Режим эффективности снят с работающих программ: {restoredEco.Restored} из {throttled.Count}";
                var rest = new List<string>();
                if (restoredEco.Exited > 0) rest.Add($"уже закрыты: {restoredEco.Exited}");
                if (restoredEco.Changed > 0) rest.Add($"программа сама сменила режим: {restoredEco.Changed}");
                if (restoredEco.Failed > 0) rest.Add($"не удалось вернуть: {restoredEco.Failed} — перезапустите программу");
                msg += rest.Count > 0 ? $" ({string.Join("; ", rest)})." : ".";
            }
        }
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
            RestoreThrottled(TakeThrottled());
            if (!Task.Run(() => ChangeJournal.Instance.RevertSourceAsync(Source)).Wait(TimeSpan.FromSeconds(5)))
                Log.Warn("Игровой сеанс: откат при выходе не успел за 5 с (журнал был занят) — при следующем запуске MONOLITH предложит вернуть настройки.");
            Log.Info("Игровой сеанс: MONOLITH закрыт во время сеанса — изменения отменены.");
        }
        catch (Exception ex)
        {
            Log.Error("Игровой сеанс: не удалось отменить изменения при выходе", ex);
        }
        _sessionOpen = false;
        IsActive = false;
    }

    // ───────────────────────────── Leftovers of a crashed session ─────────────────────────────

    /// <summary>
    /// A crash marker exists (MONOLITH died during a session with efficiency mode on): every listed process that is still
    /// running with the same PID and start time gets EXECUTION_SPEED taken out of its masks (system-managed speed, other bits
    /// untouched). The exact original is lost with the crash — the log says so. Blocking; elevated
    /// only (the marker lives in the admin-only folder). Returns the line to show, or null when there was nothing to do.
    /// Only processes of this session and user are touched (same checks as the close list), so a forged marker can at most
    /// put a process of the user back to the default state.
    /// </summary>
    public static string? RecoverStaleEcoQos()
    {
        if (!Elevation.IsElevated) return null;
        var entries = EcoQos.ReadMarker();
        if (entries.Count == 0)
        {
            EcoQos.DeleteMarker(); // an empty or unreadable marker is just removed
            return null;
        }
        var alive = UserProcesses.FindClosable(entries.Select(e => ProcessNameOf(e.Pid)).OfType<string>(), excludeDir: null);
        var reset = new List<string>();
        var gone = 0;
        try
        {
            foreach (var e in entries)
            {
                var p = alive.FirstOrDefault(x => { try { return x.Id == e.Pid; } catch { return false; } });
                if (p is null)
                {
                    gone++;
                    continue;
                }
                var h = GamesNative.Open(e.Pid, GamesNative.PROCESS_SET_INFORMATION | GamesNative.PROCESS_QUERY_LIMITED_INFORMATION);
                using (h)
                {
                    if (h.IsInvalid) continue;
                    if (EcoQos.StartTimeUtc(h) is not { } start || !EcoQos.Matches(e, e.Pid, start))
                    {
                        gone++; // the PID was reused by another process: not ours
                        continue;
                    }
                    if (EcoQos.ReadState(h) is not { } now) continue;
                    if (!EcoQos.IsEco(now)) continue; // already back (the app reset it itself)
                    if (EcoQos.WriteState(h, EcoQos.PlanRecover(now))) reset.Add(SafeName(p));
                }
            }
        }
        finally
        {
            foreach (var p in alive) p.Dispose();
        }
        EcoQos.DeleteMarker();
        if (reset.Count == 0)
        {
            Log.Info("Игровой сеанс: отметка режима эффективности прошлого сеанса закрыта — программы уже перезапущены или сами вернули режим.");
            return null;
        }
        var text = $"Прошлый игровой сеанс не завершился: режим эффективности снят с {string.Join(", ", reset.Distinct(StringComparer.OrdinalIgnoreCase))} " +
                   "(частотами снова управляет Windows: точное исходное состояние после аварийного закрытия неизвестно).";
        Log.Warn("Игровой сеанс: " + text + (gone > 0 ? $" Уже закрыты или перезапущены: {gone}." : ""));
        return text;
    }

    private static string? ProcessNameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The later change that keeps <paramref name="r"/>'s target when the <paramref name="undone"/> records are reverted
    /// together: the next newer active layer that is not undone with them. Null: r's original comes back. A plan chosen
    /// after the session (e.g. in Optimize) stays; the session's record only hands its original over to it.
    /// </summary>
    public static ChangeRecord? Keeper(ChangeRecord r, IReadOnlyCollection<ChangeRecord> undone, Func<ChangeRecord, ChangeRecord?> newerLayer)
    {
        var n = newerLayer(r);
        while (n is not null && undone.Contains(n, ChangeJournal.ByReference)) n = newerLayer(n);
        return n;
    }

    /// <summary>True when undoing the session's records changes nothing in the system: a later change keeps every target.</summary>
    public static bool AllKept(IReadOnlyList<ChangeRecord> records, Func<ChangeRecord, ChangeRecord?> newerLayer) =>
        records.Count > 0 && records.All(r => Keeper(r, records, newerLayer) is not null);

    /// <summary>One line per leftover session record for the stale-session prompts: what undoing it will do.</summary>
    public static IReadOnlyList<string> StaleLines(IReadOnlyList<ChangeRecord> records, Func<ChangeRecord, ChangeRecord?> newerLayer) =>
        records.Select(r => Keeper(r, records, newerLayer) is { } keeper
            ? $"{r.Title}: останется «{keeper.NewValue}» — его позже выбрал «{keeper.Title}»"
            // Records of older builds could carry a tweak merged into the session's record: say that it goes too.
            : $"{r.Title}: {r.NewValue} → {r.OldValue}" + (r.IsLegacyMerged
                ? $" (также откатит: {string.Join(", ", r.AlsoSources.Distinct().Select(Monolith.Modules.Restore.RestoreFormat.SourceTitle))})"
                : "")).ToList();

    /// <summary>The Games page banner about leftover session records.</summary>
    public static string StaleBanner(IReadOnlyList<ChangeRecord> records, Func<ChangeRecord, ChangeRecord?> newerLayer)
    {
        if (records.Count == 0) return "";
        var text = "Прошлый игровой сеанс не завершился (MONOLITH был закрыт аварийно).";
        var live = records.Where(r => Keeper(r, records, newerLayer) is null).ToList();
        if (live.Count > 0)
            text += " Всё ещё действует: " + string.Join("; ", live.Select(r => $"{ShortTitle(r.Title)} — «{r.NewValue}» вместо «{r.OldValue}»")) + ".";
        foreach (var r in records)
        {
            if (Keeper(r, records, newerLayer) is not { } keeper) continue;
            var what = ShortTitle(r.Title);
            if (what.Length > 0) what = char.ToUpperInvariant(what[0]) + what[1..];
            text += $" {what}: останется «{keeper.NewValue}» — его позже выбрал «{keeper.Title}».";
        }
        if (live.Count == 0) text += " Возврат только закроет запись сеанса, система не изменится.";
        return text;
    }

    /// <summary>The question of the stale-session dialog.</summary>
    public static string StalePrompt(IReadOnlyList<ChangeRecord> records, Func<ChangeRecord, ChangeRecord?> newerLayer)
    {
        const string head = "Прошлый игровой сеанс не завершился — MONOLITH был закрыт аварийно. ";
        var kept = records.Count(r => Keeper(r, records, newerLayer) is not null);
        return kept == 0 ? head + "Эти временные изменения всё ещё действуют. Вернуть исходные значения?"
            : kept == records.Count ? head + "Его изменения уже заменены более поздними, они останутся. Закрыть запись сеанса?"
            : head + "Часть временных изменений всё ещё действует, остальные заменены более поздними — те останутся. Вернуть исходные значения?";
    }

    /// <summary>The toast after the session's records were undone: what came back and what a later change keeps.</summary>
    public static string StaleRevertedText(RevertResult r)
    {
        var restored = r.Reverted - r.HandedOver;
        if (r.HandedOver == 0) return $"Исходные настройки возвращены (изменений: {restored}).";
        const string kept = "настройку питания задаёт более позднее изменение, оно остаётся";
        return restored == 0
            ? $"Запись игрового сеанса закрыта, система не изменена: {kept}."
            : $"Исходные настройки возвращены (изменений: {restored}); {kept}.";
    }

    /// <summary>
    /// For the end-of-session toast: the plan (or power mode) chosen while the session ran — a newer layer took over the
    /// session's original, so it did not go back. Null when every change of the session was undone in the system.
    /// </summary>
    public static string? KeptPlanNote(RevertResult r, ChangeRecord? planNow) =>
        r.HandedOver > 0 && planNow is not null
            ? $"{PowerSubject(planNow)} остаётся «{planNow.NewValue}» — его выбрали во время сеанса («{planNow.Title}»)."
            : null;

    /// <summary>"План питания" for a plan record, "Режим питания" for a power-mode record.</summary>
    private static string PowerSubject(ChangeRecord r) =>
        r.Target == "cmd:" + PowerModeTarget ? "Режим питания" : "План питания";

    /// <summary>"Игровой сеанс: план питания" → "план питания".</summary>
    private static string ShortTitle(string title)
    {
        var t = title.Replace("Игровой сеанс: ", "").Replace("Игровой режим: ", "");
        return t.Length == 0 ? title : t;
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
