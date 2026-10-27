using System.Diagnostics;
using System.IO;
using Monolith.Core;

namespace Monolith.Modules.Games;

/// <summary>What Game Boost does with the power plan for this session.</summary>
public enum PowerPlanAction
{
    Switch,
    SkipDisabled,
    SkipDualCcdX3D,
    SkipLaptop,
    SkipOnBattery,
    SkipModernStandby,
    SkipManagedElsewhere,
    SkipAlreadyFast,
    SkipUnavailable,
}

/// <summary>Everything the power-plan rule needs to know about this PC right now.</summary>
public readonly record struct PowerFacts(
    bool Enabled,
    bool DualCcdX3D,
    bool Laptop,
    bool OnBattery,
    bool ModernStandby,
    bool ManagedElsewhere,
    bool HighPerformanceExists,
    bool AlreadyFast);

/// <summary>Pure decision rules from docs/research/gaming_tweaks.md §1 and §C (unit-tested).</summary>
public static class BoostPolicy
{
    /// <summary>
    /// Dual-CCD Ryzen X3D (7900X3D/7950X3D/9900X3D/9950X3D…): 12+ cores. AMD's V-Cache optimizer needs the Balanced plan
    /// and Windows Game Mode, so the plan must never be switched there. Single-CCD X3D (5800X3D/7800X3D/9800X3D) is unaffected.
    /// </summary>
    public static bool IsDualCcdX3D(string? cpuName, int cores) =>
        !string.IsNullOrEmpty(cpuName) && cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase) && cores >= 12;

    /// <summary>
    /// gaming_tweaks.md `power-plan-high`: SAFE-OPTIONAL for desktops only, never on dual-CCD X3D.
    /// Laptops keep their plan (heat and battery drain for 0–2 % FPS); Modern Standby has only Balanced.
    /// </summary>
    public static PowerPlanAction DecidePowerPlan(PowerFacts f)
    {
        if (!f.Enabled) return PowerPlanAction.SkipDisabled;
        if (f.DualCcdX3D) return PowerPlanAction.SkipDualCcdX3D;
        if (f.Laptop) return PowerPlanAction.SkipLaptop;
        if (f.OnBattery) return PowerPlanAction.SkipOnBattery;
        if (f.ModernStandby) return PowerPlanAction.SkipModernStandby;
        if (f.AlreadyFast) return PowerPlanAction.SkipAlreadyFast;
        if (f.ManagedElsewhere) return PowerPlanAction.SkipManagedElsewhere;
        if (!f.HighPerformanceExists) return PowerPlanAction.SkipUnavailable;
        return PowerPlanAction.Switch;
    }

    /// <summary>
    /// During a session: the plan this session switched must go back as soon as the PC runs on battery
    /// (a UPS during a power cut, or a PC whose battery was not recognised before the session). It is not switched again
    /// when mains power returns — the rest of the session stays on the original plan.
    /// </summary>
    public static bool ShouldReleasePlanOnBattery(bool sessionSwitchedPlan, bool onBattery) => sessionSwitchedPlan && onBattery;

    /// <summary>One-line explanation of the decision for the panel and the confirmation dialog.</summary>
    public static string Describe(PowerPlanAction action, string currentName, string cpuName) => action switch
    {
        PowerPlanAction.Switch => $"План питания: «{currentName}» → «Высокая производительность». Возвращается в конце сеанса (или сразу при переходе на батарею).",
        PowerPlanAction.SkipDisabled => "План питания не меняется (выключено).",
        PowerPlanAction.SkipDualCcdX3D => $"План питания не меняется: {Short(cpuName)} — Ryzen X3D с двумя CCD, AMD рекомендует «Сбалансированную» схему и включённый игровой режим Windows.",
        PowerPlanAction.SkipLaptop => "План питания не меняется: у компьютера есть батарея (ноутбук). «Высокая производительность» добавляет 0–2 % FPS, но греет и быстрее разряжает батарею. " +
                                      "Для игр подключите зарядку и выберите режим «Максимальная производительность» в Параметрах Windows → Система → Питание и батарея.",
        PowerPlanAction.SkipOnBattery => "План питания не меняется: компьютер сейчас работает от батареи.",
        PowerPlanAction.SkipModernStandby => "План питания не меняется: на ПК с Modern Standby доступна только «Сбалансированная» схема.",
        PowerPlanAction.SkipManagedElsewhere => "План питания не меняется: он уже изменён MONOLITH в разделе «Оптимизация».",
        PowerPlanAction.SkipAlreadyFast => $"План питания не меняется: «{currentName}» уже включена.",
        _ => "План питания не меняется: схемы «Высокая производительность» на этом ПК нет.",
    };

    /// <summary>
    /// The end-of-session toast: what came back and — honestly — what did not. The only journaled boost change is the
    /// power plan; closed or force-closed programs are never reopened.
    /// </summary>
    public static string EndMessage(string reason, int revertedRecords, int failedRecords, int restoredPriority, IReadOnlyCollection<string> closedApps)
    {
        var parts = new List<string> { reason.Trim() };
        if (revertedRecords > 0) parts.Add("План питания возвращён.");
        if (restoredPriority > 0) parts.Add("Приоритет игры возвращён.");
        if (failedRecords > 0)
            parts.Add($"Не удалось вернуть: {failedRecords} {GameText.Plural(failedRecords, "изменение", "изменения", "изменений")} — откройте раздел «Восстановление».");
        if (closedApps.Count > 0)
            parts.Add($"Закрытые программы ({string.Join(", ", closedApps)}) MONOLITH заново не открывает — запустите их сами, если нужны.");
        return string.Join(" ", parts);
    }

    private static string Short(string cpu) =>
        string.IsNullOrWhiteSpace(cpu) ? "процессор" : cpu.Replace("AMD ", "", StringComparison.OrdinalIgnoreCase).Replace(" Processor", "", StringComparison.OrdinalIgnoreCase).Trim();
}

/// <summary>Everything a Game Boost session would do right now. Built off the UI thread.</summary>
public sealed record BoostPlan(
    PowerPlanAction PowerAction,
    string PowerText,
    IReadOnlyList<RunningApp> AppsToClose,
    IReadOnlyList<string> CloseListNotRunning,
    bool RaisePriority,
    bool PurgeStandby)
{
    /// <summary>The exact list shown in the confirmation dialog.</summary>
    public IReadOnlyList<string> Describe(GameEntry? game)
    {
        var lines = new List<string> { PowerText };
        if (AppsToClose.Count > 0)
            lines.Add("Закрыть приложения: " + string.Join(", ", AppsToClose.Select(a => a.Count > 1 ? $"{a.Name} ({a.Count})" : a.Name)) +
                      ". Сначала им отправляется обычная команда «закрыть окно»; принудительное завершение — только после отдельного вопроса. " +
                      "MONOLITH их потом не открывает — после игры запустите их сами.");
        else if (CloseListNotRunning.Count > 0)
            lines.Add("Приложения из списка закрытия сейчас не запущены — ничего не закрывается.");
        else
            lines.Add("Приложения не закрываются (список закрытия пуст).");

        if (game is not null)
            lines.Add(RaisePriority
                ? "Приоритет процесса игры: «Высокий» (никогда «Реального времени»), как только игра запустится. Сбрасывается вместе с игрой."
                : "Приоритет игры не меняется (выключено).");
        if (PurgeStandby)
            lines.Add("Однократная очистка списка ожидания (standby) памяти. Обычно эффекта на FPS нет; первые загрузки могут стать дольше. Отменять тут нечего.");
        if (game is not null)
            lines.Add($"Запустить «{game.Name}» с обычными правами пользователя и следить за процессом игры: после выхода из игры схема питания и приоритет вернутся сами.");
        else
            lines.Add("Сеанс без привязки к игре: изменения действуют до нажатия «СТОП».");
        return lines;
    }
}

/// <summary>A process that belongs to the game being boosted.</summary>
public sealed record GameProcess(int Pid, string Name, string Path, long WorkingSet, bool IsGameExe);

/// <summary>
/// Finds the game's processes by exe path / install-folder prefix (docs/research/tech.md §6). Games are started through
/// the desktop shell as the signed-in user, so no PID comes back from the launch — the folder is the only reliable link.
/// </summary>
public static class GameProcessFinder
{
    /// <summary>Helpers that live in game folders but are not the game (crash reporters, anti-cheat, installers).</summary>
    private static readonly string[] HelperFragments =
    [
        "crash", "easyanticheat", "beservice", "battleye", "vc_redist", "vcredist", "dxsetup", "unins",
        "launcherpatcher", "cefprocess", "webhelper", "reporter",
    ];

    public static bool IsHelper(string processName) =>
        HelperFragments.Any(f => processName.Contains(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>The exe that identifies the game exactly: its own exe for user-added games or an exe inside the install folder.</summary>
    public static string? ExactExe(GameEntry game, string normalizedDir) =>
        // Never the Riot client or another launcher that lives outside the game folder.
        game.ExePath is { } e && (game.Platform == GamePlatform.Custom || GamePaths.IsUnder(e, normalizedDir)) ? e : null;

    /// <summary>Main process first: the game's own exe, then the biggest by memory.</summary>
    public static IReadOnlyList<GameProcess> Rank(IEnumerable<GameProcess> processes) =>
        processes.OrderByDescending(g => g.IsGameExe).ThenByDescending(g => g.WorkingSet).ToList();

    /// <summary>Game processes in this session, the main one first. Blocking.</summary>
    public static IReadOnlyList<GameProcess> Find(GameEntry game)
    {
        var dir = GamePaths.NormalizeDir(game.InstallDir);
        var byFolder = GamePaths.IsSpecificDir(dir);
        var exe = ExactExe(game, dir);
        int session;
        using (var me = Process.GetCurrentProcess()) session = me.SessionId;

        var list = new List<GameProcess>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return list; }
        foreach (var p in all)
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session || p.Id <= 4) continue;
                    var name = p.ProcessName;
                    if (IsHelper(name)) continue;
                    var path = GamesNative.GetImagePath(p.Id);
                    if (path is null) continue;
                    var isExe = exe is not null && string.Equals(path, exe, StringComparison.OrdinalIgnoreCase);
                    if (!isExe && !(byFolder && GamePaths.IsUnder(path, dir))) continue;
                    long ws;
                    try { ws = p.WorkingSet64; } catch { ws = 0; }
                    list.Add(new GameProcess(p.Id, name, path, ws, isExe));
                }
                catch { /* exited */ }
            }
        }
        return Rank(list);
    }

    public static string ExeName(GameProcess p) => Path.GetFileName(p.Path);
}
