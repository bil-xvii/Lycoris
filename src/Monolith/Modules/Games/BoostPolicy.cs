using System.Diagnostics;
using System.IO;
using Monolith.Core;
using Monolith.Tweaks;

namespace Monolith.Modules.Games;

/// <summary>What Game Boost does with the power plan (or the Windows 11 power mode) for this session.</summary>
public enum PowerPlanAction
{
    /// <summary>Activate the «Высокая производительность» plan.</summary>
    Switch,
    /// <summary>Keep the plan, set the AC power mode to «Максимальная производительность» (laptops, Modern Standby, no High performance plan).</summary>
    SwitchPowerMode,
    SkipDisabled,
    SkipDualCcdX3D,
    SkipOnBattery,
    SkipLaptop,
    SkipModernStandby,
    SkipManagedElsewhere,
    SkipAlreadyFast,
    SkipUnavailable,
    SkipUnknownHardware,
    /// <summary>Windows refuses the «Максимальная производительность» mode while energy saver is on.</summary>
    SkipEnergySaver,
    /// <summary>The AC power mode is already «Максимальная производительность».</summary>
    SkipModeAlreadyFast,
    /// <summary>An Optimize item holds the AC power mode (an active "powermode:ac" record of another source).</summary>
    SkipModeManagedElsewhere,
    /// <summary>The current AC power mode could not be read (the API returned nothing) — there is no exact undo to record.</summary>
    SkipModeUnreadable,
    /// <summary>Windows reported an AC power mode that is not one of the three documented ones (OEM value) — never touched.</summary>
    SkipModeUnknown,
    /// <summary>The journal would refuse the undo of the mode record (JournalTrust probe failed) — nothing is changed.</summary>
    SkipModeUntrusted,
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

    /// <remarks>
    /// The plan itself is switched on desktops only (gaming_tweaks.md: «Высокая производительность» is SAFE-OPTIONAL for
    /// desktops, the same rule as the Optimize tweak's AppliesTo): on a laptop it would also stay on after unplugging
    /// mid-game. Where the plan cannot be switched (laptop, Modern Standby, no High performance plan) the session falls
    /// back to the Windows 11 power mode — see <see cref="Decide"/> / <see cref="DecidePowerMode"/>.
    /// </remarks>
    public static PowerPlanAction DecidePowerPlan(bool enabled, bool dualCcdX3D, bool onBattery, bool laptop, bool modernStandby,
        bool managedByOtherChange, bool highPerformanceExists, bool alreadyFast)
    {
        if (!enabled) return PowerPlanAction.SkipDisabled;
        if (dualCcdX3D) return PowerPlanAction.SkipDualCcdX3D;
        if (onBattery) return PowerPlanAction.SkipOnBattery;
        if (laptop) return PowerPlanAction.SkipLaptop;
        if (modernStandby) return PowerPlanAction.SkipModernStandby;
        if (alreadyFast) return PowerPlanAction.SkipAlreadyFast;
        if (managedByOtherChange) return PowerPlanAction.SkipManagedElsewhere;
        if (!highPerformanceExists) return PowerPlanAction.SkipUnavailable;
        return PowerPlanAction.Switch;
    }

    /// <summary>
    /// The Windows 11 power mode («Максимальная производительность», AC only; gaming_tweaks.md §2 C): the plan stays, so it
    /// is the lever on laptops, Modern Standby PCs and PCs without a High performance plan — desktops included. Only the
    /// AC mode is set, so unplugging mid-game falls back to the user's own battery mode by itself.
    /// </summary>
    /// <returns>
    /// <see cref="PowerPlanAction.SwitchPowerMode"/> or the matching skip. <see cref="PowerPlanAction.SkipUnavailable"/>
    /// stands for "the mode does not exist here" (Windows 10, or a plan that is not Balanced-personality); <see cref="Decide"/>
    /// replaces it with the plan's own reason.
    /// </returns>
    public static PowerPlanAction DecidePowerMode(bool enabled, bool dualCcdX3D, bool onBattery, bool windows11, bool balancedPersonalityPlan,
        bool energySaverOn, bool acModeAlreadyBestPerformance, bool managedByOtherChange)
    {
        if (!enabled) return PowerPlanAction.SkipDisabled;
        if (dualCcdX3D) return PowerPlanAction.SkipDualCcdX3D;
        if (onBattery) return PowerPlanAction.SkipOnBattery;
        if (!windows11 || !balancedPersonalityPlan) return PowerPlanAction.SkipUnavailable;
        if (energySaverOn) return PowerPlanAction.SkipEnergySaver;
        if (acModeAlreadyBestPerformance) return PowerPlanAction.SkipModeAlreadyFast;
        if (managedByOtherChange) return PowerPlanAction.SkipModeManagedElsewhere;
        return PowerPlanAction.SwitchPowerMode;
    }

    /// <summary>
    /// The session's power action: the plan switch where it is allowed; where the plan is skipped only because it cannot be
    /// switched here (<see cref="PowerPlanAction.SkipLaptop"/>, <see cref="PowerPlanAction.SkipModernStandby"/>,
    /// <see cref="PowerPlanAction.SkipUnavailable"/>) the power-mode decision takes over. When the mode does not exist
    /// either, the plan's own reason is reported — so a desktop is never told «на ноутбуке».
    /// </summary>
    public static PowerPlanAction Decide(PowerPlanAction plan, PowerPlanAction mode) =>
        PlanFallsBackToMode(plan) && mode != PowerPlanAction.SkipUnavailable ? mode : plan;

    /// <summary>
    /// <see cref="Decide(PowerPlanAction, PowerPlanAction)"/> plus the state of the mode itself: the mode is switched only when
    /// the current AC mode was read and is one of the three documented GUIDs — otherwise there is no exact undo to record, so
    /// the decision is a skip that says so (<see cref="ModeProblem"/>). The other skips (energy saver, battery…) stay as they are.
    /// </summary>
    public static PowerPlanAction Decide(PowerPlanAction plan, PowerPlanAction mode, Guid? currentMode)
    {
        var action = Decide(plan, mode);
        return action == PowerPlanAction.SwitchPowerMode ? ModeProblem(currentMode) ?? action : action;
    }

    /// <summary>
    /// Why the current AC power mode cannot be the original of a session record: null (unreadable) →
    /// <see cref="PowerPlanAction.SkipModeUnreadable"/>; a GUID that is not Balanced / Best efficiency / Best performance →
    /// <see cref="PowerPlanAction.SkipModeUnknown"/>; a documented mode → null (fine).
    /// </summary>
    public static PowerPlanAction? ModeProblem(Guid? currentMode) =>
        currentMode is not { } m ? PowerPlanAction.SkipModeUnreadable
        : !TweakCatalog.IsDocumentedPowerMode(m) ? PowerPlanAction.SkipModeUnknown
        : null;

    /// <summary>The plan decisions that mean "cannot be switched here" rather than "must not": the mode is tried instead.</summary>
    public static bool PlanFallsBackToMode(PowerPlanAction plan) =>
        plan is PowerPlanAction.SkipLaptop or PowerPlanAction.SkipModernStandby or PowerPlanAction.SkipUnavailable;

    /// <summary>True when the session changes something (the plan or the mode).</summary>
    public static bool IsSwitch(PowerPlanAction action) => action is PowerPlanAction.Switch or PowerPlanAction.SwitchPowerMode;

    /// <summary>Prefix of every skip text; the panel and the session log strip it.</summary>
    public static string SkipPrefix => Loc.T("План питания не меняется: ");
    /// <summary>Prefix of the plan-switch text.</summary>
    public static string SwitchPrefix => Loc.T("План питания: ");
    /// <summary>Prefix of the mode-switch text.</summary>
    public static string SwitchModePrefix => Loc.T("Режим питания от сети: ");

    /// <summary>
    /// One-line explanation of the decision for the panel and the confirmation dialog. <paramref name="currentMode"/> is the
    /// current AC power mode in words (for <see cref="PowerPlanAction.SwitchPowerMode"/>); <paramref name="laptop"/> adds the
    /// battery note there — a desktop never reads about batteries or «на ноутбуке». <paramref name="windows11"/> picks the
    /// laptop reason: on Windows 10 the mode does not exist at all, on Windows 11 it needs a Balanced-personality plan.
    /// </summary>
    public static string Describe(PowerPlanAction action, string currentName, string cpuName, string? currentMode = null, bool laptop = false,
        bool windows11 = true) => action switch
    {
        PowerPlanAction.Switch => SwitchPrefix + Loc.Tf("«{0}» → «Высокая производительность». Возвращается после игры.", currentName),
        PowerPlanAction.SwitchPowerMode => SwitchModePrefix + Loc.Tf("«{0}» → «Максимальная производительность» (схема остаётся сбалансированной).", Cap(currentMode ?? Loc.T("текущий"))) +
                                           " " + Loc.T("Возвращается после игры") + (laptop ? Loc.T("; от батареи ничего не меняется.") : "."),
        PowerPlanAction.SkipDisabled => Loc.T("План питания не меняется (выключено)."),
        PowerPlanAction.SkipDualCcdX3D => SkipPrefix + Loc.Tf("{0} — X3D с двумя CCD, AMD рекомендует «Сбалансированную» схему и игровой режим Windows.", Short(cpuName)),
        PowerPlanAction.SkipOnBattery => SkipPrefix + Loc.T("ноутбук работает от батареи."),
        // Reached only when the power mode is not available either: Windows 10, or a plan that is not Balanced-personality.
        PowerPlanAction.SkipLaptop when !windows11 => SkipPrefix + Loc.T("на ноутбуке MONOLITH меняет только режим питания, а он есть только в Windows 11."),
        PowerPlanAction.SkipLaptop => SkipPrefix + Loc.T("на ноутбуке MONOLITH меняет только режим питания Windows 11 на сбалансированной схеме — включите её в «Оптимизации»."),
        PowerPlanAction.SkipModernStandby => SkipPrefix + Loc.T("на ПК с Modern Standby доступна только «Сбалансированная» схема, а режим питания «Максимальная производительность» MONOLITH включает только в Windows 11."),
        PowerPlanAction.SkipManagedElsewhere => SkipPrefix + Loc.T("он уже изменён MONOLITH в разделе «Оптимизация»."),
        PowerPlanAction.SkipAlreadyFast => SkipPrefix + Loc.Tf("«{0}» уже включена.", currentName),
        PowerPlanAction.SkipUnknownHardware => SkipPrefix + Loc.T("не удалось определить, ноутбук это или ПК. Нажмите «ОБНОВИТЬ» позже."),
        PowerPlanAction.SkipEnergySaver => SkipPrefix + Loc.T("включено энергосбережение — Windows не даёт выбрать режим «Максимальная производительность». Отключите его в Параметрах → Питание."),
        PowerPlanAction.SkipModeAlreadyFast => SkipPrefix + Loc.T("режим питания от сети уже «Максимальная производительность»."),
        PowerPlanAction.SkipModeManagedElsewhere => SkipPrefix + Loc.T("режим питания уже изменён MONOLITH в разделе «Оптимизация»."),
        PowerPlanAction.SkipModeUnreadable => SkipPrefix + Loc.T("не удалось прочитать текущий режим питания Windows — без него нет точного отката."),
        PowerPlanAction.SkipModeUnknown => SkipPrefix + Loc.T("Windows сообщила неизвестный режим питания — MONOLITH его не меняет."),
        PowerPlanAction.SkipModeUntrusted => SkipPrefix + Loc.T("журнал MONOLITH не принял бы отмену смены режима питания — ничего не меняется."),
        _ => SkipPrefix + Loc.T("схемы «Высокая производительность» на этом ПК нет, а режим питания MONOLITH меняет только в Windows 11 на сбалансированной схеме."),
    };

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Short(string cpu) =>
        string.IsNullOrWhiteSpace(cpu) ? Loc.T("процессор") : cpu.Replace("AMD ", "", StringComparison.OrdinalIgnoreCase).Replace(" Processor", "", StringComparison.OrdinalIgnoreCase).Trim();
}

/// <summary>What a session does with the apps of the close list.</summary>
public enum BoostCloseMode
{
    /// <summary>Close them (window close first, kill only after a question).</summary>
    Close,
    /// <summary>Windows 11: keep them running in efficiency mode (EcoQoS) for the session, exact per-process restore afterwards.</summary>
    EcoQos,
}

/// <summary>Everything a Game Boost session would do right now. Built off the UI thread.</summary>
public sealed record BoostPlan(
    PowerPlanAction PowerAction,
    string PowerText,
    IReadOnlyList<RunningApp> AppsToClose,
    IReadOnlyList<string> CloseListNotRunning,
    bool RaisePriority,
    bool PurgeStandby,
    BoostCloseMode CloseMode = BoostCloseMode.Close)
{
    /// <summary>The close-list line of the confirmation: closing or efficiency mode, running or not, empty list.</summary>
    public string DescribeCloseList()
    {
        var names = string.Join(", ", AppsToClose.Select(a => a.Count > 1 ? $"{a.Name} ({a.Count})" : a.Name));
        if (CloseMode == BoostCloseMode.EcoQos)
            return AppsToClose.Count > 0
                ? Loc.Tf("Режим эффективности для: {0}. Программы не закрываются; Windows ограничит им частоты процессора на время игры, " +
                  "звук и голосовые программы исключены. После игры режим возвращается. Эффект небольшой, честно: без измерения на вашем ПК не обещаем.", names)
                : CloseListNotRunning.Count > 0
                    ? Loc.T("Приложения из списка сейчас не запущены — режим эффективности никому не назначается.")
                    : Loc.T("Режим эффективности никому не назначается (список приложений пуст).");
        if (AppsToClose.Count > 0)
            return Loc.Tf("Закрыть приложения: {0}. Сначала им отправляется обычная команда «закрыть окно»; принудительное завершение — только после отдельного вопроса. Сами они после игры не откроются.", names);
        return CloseListNotRunning.Count > 0
            ? Loc.T("Приложения из списка закрытия сейчас не запущены — ничего не закрывается.")
            : Loc.T("Приложения не закрываются (список закрытия пуст).");
    }

    /// <summary>The exact list shown in the confirmation dialog.</summary>
    public IReadOnlyList<string> Describe(GameEntry? game)
    {
        var lines = new List<string> { PowerText, DescribeCloseList() };

        if (game is not null)
            lines.Add(RaisePriority
                ? Loc.T("Приоритет процесса игры: «Высокий» (никогда «Реального времени»), как только игра запустится. Сбрасывается вместе с игрой.")
                : Loc.T("Приоритет игры не меняется (выключено)."));
        if (PurgeStandby)
            lines.Add(Loc.T("Однократная очистка списка ожидания (standby) памяти. Обычно эффекта на FPS нет; первые загрузки могут стать дольше."));
        if (game is not null)
            lines.Add(Loc.Tf("Запустить «{0}» и следить за процессом игры: после выхода из игры всё вернётся автоматически.", game.Name));
        else
            lines.Add(Loc.T("Сеанс без привязки к игре: изменения действуют до нажатия «СТОП»."));
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
