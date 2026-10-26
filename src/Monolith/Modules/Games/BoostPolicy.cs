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
    SkipOnBattery,
    SkipModernStandby,
    SkipManagedElsewhere,
    SkipAlreadyFast,
    SkipUnavailable,
}

/// <summary>Pure decision rules from docs/research/gaming_tweaks.md §1 and §C (unit-tested).</summary>
public static class BoostPolicy
{
    /// <summary>
    /// Dual-CCD Ryzen X3D (7900X3D/7950X3D/9900X3D/9950X3D…): 12+ cores. AMD's V-Cache optimizer needs the Balanced plan
    /// and Game Mode, so the plan must never be switched there. Single-CCD X3D (5800X3D/7800X3D/9800X3D) is unaffected.
    /// </summary>
    public static bool IsDualCcdX3D(string? cpuName, int cores) =>
        !string.IsNullOrEmpty(cpuName) && cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase) && cores >= 12;

    public static PowerPlanAction DecidePowerPlan(bool enabled, bool dualCcdX3D, bool onBattery, bool modernStandby,
        bool managedByOtherChange, bool highPerformanceExists, bool alreadyFast)
    {
        if (!enabled) return PowerPlanAction.SkipDisabled;
        if (dualCcdX3D) return PowerPlanAction.SkipDualCcdX3D;
        if (onBattery) return PowerPlanAction.SkipOnBattery;
        if (modernStandby) return PowerPlanAction.SkipModernStandby;
        if (alreadyFast) return PowerPlanAction.SkipAlreadyFast;
        if (managedByOtherChange) return PowerPlanAction.SkipManagedElsewhere;
        if (!highPerformanceExists) return PowerPlanAction.SkipUnavailable;
        return PowerPlanAction.Switch;
    }

    /// <summary>One-line explanation of the decision for the panel and the confirmation dialog.</summary>
    public static string Describe(PowerPlanAction action, string currentName, string cpuName) => action switch
    {
        PowerPlanAction.Switch => $"План питания: «{currentName}» → «Высокая производительность». Возвращается после игры.",
        PowerPlanAction.SkipDisabled => "План питания не меняется (выключено).",
        PowerPlanAction.SkipDualCcdX3D => $"План питания не меняется: {Short(cpuName)} — X3D с двумя кристаллами, AMD рекомендует «Сбалансированную» схему и игровой режим Windows.",
        PowerPlanAction.SkipOnBattery => "План питания не меняется: ноутбук работает от батареи.",
        PowerPlanAction.SkipModernStandby => "План питания не меняется: на ПК с Modern Standby доступна только «Сбалансированная» схема.",
        PowerPlanAction.SkipManagedElsewhere => "План питания не меняется: он уже изменён MONOLITH в разделе «Оптимизация».",
        PowerPlanAction.SkipAlreadyFast => $"План питания не меняется: «{currentName}» уже включена.",
        _ => "План питания не меняется: схемы «Высокая производительность» на этом ПК нет.",
    };

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
                      ". Сначала им отправляется обычная команда «закрыть окно»; принудительное завершение — только после отдельного вопроса. Сами они после игры не откроются.");
        else if (CloseListNotRunning.Count > 0)
            lines.Add("Приложения из списка закрытия сейчас не запущены — ничего не закрывается.");
        else
            lines.Add("Приложения не закрываются (список закрытия пуст).");

        if (game is not null)
            lines.Add(RaisePriority
                ? "Приоритет процесса игры: «Высокий» (никогда «Реального времени»), как только игра запустится. Сбрасывается вместе с игрой."
                : "Приоритет игры не меняется (выключено).");
        if (PurgeStandby)
            lines.Add("Однократная очистка списка ожидания (standby) памяти. Обычно эффекта на FPS нет; первые загрузки могут стать дольше.");
        if (game is not null)
            lines.Add($"Запустить «{game.Name}» и следить за процессом игры: после выхода из игры всё вернётся автоматически.");
        else
            lines.Add("Сеанс без привязки к игре: изменения действуют до нажатия «СТОП».");
        return lines;
    }
}

/// <summary>A process that belongs to the game being boosted.</summary>
public sealed record GameProcess(int Pid, string Name, string Path, long WorkingSet);

/// <summary>Finds the game's processes by exe path / install-folder prefix (docs/research/tech.md §6).</summary>
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

    /// <summary>Game processes in this session, the launched one first, then by memory use. Blocking.</summary>
    public static IReadOnlyList<GameProcess> Find(GameEntry game, int? launchedPid)
    {
        var dir = GamePaths.NormalizeDir(game.InstallDir);
        var byFolder = GamePaths.IsSpecificDir(dir);
        // Exact exe match only for the game's own exe (the Riot client that launches Riot games lives elsewhere).
        var exe = game.ExePath is { } e && (game.Platform == GamePlatform.Custom || GamePaths.IsUnder(e, dir)) ? e : null;
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
                    var match = (exe is not null && string.Equals(path, exe, StringComparison.OrdinalIgnoreCase))
                                || (byFolder && GamePaths.IsUnder(path, dir));
                    if (!match) continue;
                    long ws;
                    try { ws = p.WorkingSet64; } catch { ws = 0; }
                    list.Add(new GameProcess(p.Id, name, path, ws));
                }
                catch { /* exited */ }
            }
        }
        return list.OrderByDescending(g => g.Pid == launchedPid).ThenByDescending(g => g.WorkingSet).ToList();
    }

    public static string ExeName(GameProcess p) => Path.GetFileName(p.Path);
}
