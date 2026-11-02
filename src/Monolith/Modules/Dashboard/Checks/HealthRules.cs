using System.Text.RegularExpressions;
using Monolith.Core;
using Monolith.Modules.Optimize;

namespace Monolith.Modules.Dashboard.Checks;

/// <summary>RAM speed verdict: rated (from SMBIOS and the part number) vs. the speed the modules run at.</summary>
public sealed record RamVerdict(int Rated, int Configured, string Type, int Modules, ulong Capacity, bool BelowRated);

/// <summary>
/// The dashboard health checks as pure functions of a <see cref="SystemSnapshot"/>.
/// Thresholds and interpretation follow docs/research (tech.md §7, gaming_tweaks.md, general_tweaks.md).
/// A check returns null when it does not apply to this PC or its data could not be read.
/// </summary>
public static partial class HealthRules
{
    public const string TweakGameMode = "game-mode";
    public const string TweakHags = "hags";
    public const string TweakHagsOther = "hags-other-gpu";
    public const string TweakTrim = "trim-on";
    public const string TweakGameDvr = "gamedvr-off";
    public const string TweakBalancedPlan = "power-plan-balanced";
    public const string TweakBalancedPlanX3D = "power-plan-balanced-x3d";
    public const string TweakPowerModeBestPerformance = "power-mode-best-performance";
    public const string TweakHibernationOff = "hibernation-off";
    public const string TweakPagefileSystemManaged = "pagefile-system-managed";
    public const string TweakUpdatePin = "wu-target-version";
    /// <summary>Параметры → Система → Питание: the only place that controls energy saver (no tweak does).</summary>
    public const string PowerSettingsUri = "ms-settings:powersleep";

    /// <summary>Free space is "low" below this share of the system drive…</summary>
    public const double LowSpaceShare = 0.15;
    /// <summary>…or below this many bytes.</summary>
    public const ulong LowSpaceBytes = 20UL * 1024 * 1024 * 1024;
    public const int UptimeDaysLimit = 7;
    public const int DriverAgeDaysLimit = 183;
    public const int StartupLimit = 10;
    /// <summary>A pinned Windows version is flagged this many days before its end of servicing.</summary>
    public const int UpdatePinWarnDays = 90;

    /// <summary>JEDEC base speeds: a module stuck at one of these while rated higher has XMP/EXPO off.</summary>
    private static readonly int[] JedecSpeeds = [2133, 2400, 2666, 2933, 3200, 4800, 5200, 5600];

    public static IReadOnlyList<HealthCheckResult> Evaluate(SystemSnapshot s)
    {
        var checks = new Func<SystemSnapshot, HealthCheckResult?>[]
        {
            CheckRestore, CheckDiskSpace, CheckDiskHealth, CheckDevices, CheckStability, CheckRefreshRate, CheckMonitorOnGpu,
            CheckRam, CheckTrim, CheckPendingReboot, CheckUpdatePin, CheckGpuDriver, CheckRebar, CheckPcieLink, CheckGameMode, CheckHags,
            CheckGameDvr, CheckStartup, CheckUptime, CheckPowerPlan, CheckBootSecurity, CheckVbs,
        };
        var results = new List<HealthCheckResult>();
        foreach (var check in checks)
        {
            try
            {
                if (check(s) is { } r) results.Add(r);
            }
            catch (Exception ex)
            {
                Log.Warn("Проверка «{0}» пропущена: {1}", check.Method.Name, ex.Message);
            }
        }
        return results;
    }

    // ───────────────────────────── RAM (XMP / EXPO) ─────────────────────────────

    [GeneratedRegex(@"F[345]-(\d{4})", RegexOptions.IgnoreCase)] private static partial Regex GSkillRx();
    [GeneratedRegex(@"^CM.*?M\d[A-Z](\d{4})C\d", RegexOptions.IgnoreCase)] private static partial Regex CorsairRx();
    [GeneratedRegex(@"^(?:KF|HX)[345](\d{2})C\d", RegexOptions.IgnoreCase)] private static partial Regex KingstonRx();
    [GeneratedRegex(@"(\d{2})C\d{2}U", RegexOptions.IgnoreCase)] private static partial Regex CrucialRx();
    [GeneratedRegex(@"(\d{4})HC\d{2}", RegexOptions.IgnoreCase)] private static partial Regex TeamRx();
    [GeneratedRegex(@"^AX[45]U(\d{4})", RegexOptions.IgnoreCase)] private static partial Regex AdataRx();

    /// <summary>Speed (MT/s) encoded in a module's part number, e.g. G.Skill "F5-6000J3636F16G" → 6000.</summary>
    public static int? PartNumberSpeed(string? partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber)) return null;
        var p = partNumber.Trim();
        if (GSkillRx().Match(p) is { Success: true } g) return Plausible(int.Parse(g.Groups[1].Value));
        if (CorsairRx().Match(p) is { Success: true } c) return Plausible(int.Parse(c.Groups[1].Value));
        if (KingstonRx().Match(p) is { Success: true } k) return Plausible(int.Parse(k.Groups[1].Value) * 100);
        if (TeamRx().Match(p) is { Success: true } t) return Plausible(int.Parse(t.Groups[1].Value));
        if (AdataRx().Match(p) is { Success: true } a) return Plausible(int.Parse(a.Groups[1].Value));
        if (CrucialRx().Match(p) is { Success: true } u) return Plausible(int.Parse(u.Groups[1].Value) * 100);
        return null;

        static int? Plausible(int v) => v is >= 1600 and <= 12000 ? v : null;
    }

    /// <summary>Some firmware reports MHz instead of MT/s (half the value).</summary>
    public static int NormalizeSpeed(int speed, int smbiosType)
    {
        if (speed <= 0) return 0;
        if (smbiosType == 26 && speed < 1700) return speed * 2;               // DDR4
        if (smbiosType is 34 or 35 && speed < 3000) return speed * 2;         // DDR5 / LPDDR5
        return speed;
    }

    public static string MemoryTypeName(int smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "",
    };

    public static RamVerdict? AnalyzeRam(IReadOnlyList<RamModule> modules)
    {
        if (modules.Count == 0) return null;
        int rated = int.MaxValue, configured = int.MaxValue;
        ulong capacity = 0;
        foreach (var m in modules)
        {
            capacity += m.Capacity;
            var speed = NormalizeSpeed(m.Speed, m.SmbiosMemoryType);
            var conf = NormalizeSpeed(m.ConfiguredSpeed, m.SmbiosMemoryType);
            var moduleRated = Math.Max(speed, PartNumberSpeed(m.PartNumber) ?? 0);
            if (moduleRated > 0) rated = Math.Min(rated, moduleRated);     // mixed kits: the slowest module rules
            if (conf > 0) configured = Math.Min(configured, conf);
        }
        if (rated == int.MaxValue || configured == int.MaxValue) return null;
        var type = modules.Select(m => MemoryTypeName(m.SmbiosMemoryType)).FirstOrDefault(t => t.Length > 0) ?? "";
        var below = configured < 0.95 * rated && JedecSpeeds.Any(j => Math.Abs(configured - j) <= 2);
        return new RamVerdict(rated, configured, type, modules.Count, capacity, below);
    }

    public static HealthCheckResult? CheckRam(SystemSnapshot s)
    {
        var v = AnalyzeRam(s.RamModules);
        if (v is null) return null;
        var kit = Loc.Tf("{0} МТ/с{1} · {2}", v.Configured, v.Type.Length > 0 ? " · " + v.Type : "",
            Loc.N(v.Modules, "{0} модуль", "{0} модуля", "{0} модулей"));
        if (!v.BelowRated)
            return new("ram-speed", Loc.T("ПАМЯТЬ НА НОМИНАЛЬНОЙ ЧАСТОТЕ"), Loc.Tf("{0} — частота соответствует маркировке модулей.", kit), CheckSeverity.Ok, 10);

        var detail = Loc.Tf("Модули рассчитаны на {0} МТ/с, а работают на {1} МТ/с. Скорее всего, в BIOS выключен профиль " +
                     "XMP (Intel) или EXPO (AMD) — в играх, чувствительных к памяти, это заметная потеря FPS. Включите профиль в BIOS/UEFI " +
                     "(раздел настроек памяти → XMP/EXPO → сохранить F10). Lycoris настройки BIOS не меняет; если система станет " +
                     "нестабильной, верните там значение «Auto».", v.Rated, v.Configured);
        if (s.IsLaptop)
            detail += " " + Loc.T("На ноутбуках профиль часто недоступен — тогда это нормальная работа.");
        return new("ram-speed", Loc.T("ПАМЯТЬ РАБОТАЕТ НИЖЕ НОМИНАЛА"), detail, s.IsLaptop ? CheckSeverity.Info : CheckSeverity.Warning, 10);
    }

    // ───────────────────────────── Refresh rate ─────────────────────────────

    /// <summary>Highest progressive refresh rate offered at the given resolution (0 when none).</summary>
    public static int MaxRefreshAt(IEnumerable<DisplayMode> modes, int width, int height) =>
        modes.Where(m => m.Width == width && m.Height == height && !m.Interlaced).Select(m => m.Frequency).DefaultIfEmpty(0).Max();

    /// <summary>True when the display could run faster. 1 Hz of slack: 239 vs 240 Hz is the same fractional timing.</summary>
    public static bool IsBelowMaxRefresh(int currentHz, int maxHz) => currentHz > 1 && maxHz > currentHz + 1;

    /// <summary>
    /// Same rules as the monitor cards of «Устройства и сеть» (DisplayAdvice): no refresh verdict on battery, and a laptop's own
    /// panel is left to Windows (dynamic refresh rate, power mode) — so no warning the page would not back up.
    /// </summary>
    public static HealthCheckResult? CheckRefreshRate(SystemSnapshot s)
    {
        if (s.Displays.Count == 0) return null;
        var below = s.Displays.Where(d => IsBelowMaxRefresh(d.CurrentHz, d.MaxHz)).ToList();
        if (below.Count > 0 && s.OnBattery) return null;
        var slow = below.Where(d => !d.IsInternal).ToList();
        if (slow.Count == 0)
        {
            if (below.Count > 0) return null; // only the laptop panel is below its maximum: Windows decides, nothing to claim
            var list = string.Join("; ", s.Displays.Select(d => Loc.Tf("{0} — {1} Гц при {2}×{3}", d.Name, d.CurrentHz, d.Width, d.Height)));
            return new("refresh-rate", Loc.T("ЧАСТОТА ОБНОВЛЕНИЯ МАКСИМАЛЬНАЯ"), list + ".", CheckSeverity.Ok, 10);
        }
        var what = string.Join("; ", slow.Select(d => Loc.Tf("{0}: сейчас {1} Гц, доступно {2} Гц при {3}×{4}", d.Name, d.CurrentHz, d.MaxHz, d.Width, d.Height)));
        var detail = what + ". " + Loc.T("Выберите максимальную частоту в разделе «Устройства и сеть»: Lycoris переключит её с подтверждением " +
                     "на экране, а откатить можно там же или в «Восстановлении». Игры и интерфейс станут плавнее.");
        return new("refresh-rate", Loc.T("МОНИТОР НЕ НА МАКСИМАЛЬНОЙ ЧАСТОТЕ"), detail, CheckSeverity.Warning, 10,
            new CheckAction(CheckActionKind.FocusDevice, Loc.T("НАСТРОИТЬ МОНИТОР"), "kind:monitor"));
    }

    // ───────────────────────────── System Restore ─────────────────────────────

    public static HealthCheckResult? CheckRestore(SystemSnapshot s)
    {
        if (s.RestoreBlockedByPolicy)
            return new("restore", Loc.T("ЗАЩИТА СИСТЕМЫ ЗАПРЕЩЕНА ПОЛИТИКОЙ"),
                Loc.T("Точки восстановления отключены групповой политикой (обычно это решение администратора организации). " +
                "Lycoris политики не обходит; собственные изменения по-прежнему откатываются в разделе «Восстановление»."),
                CheckSeverity.Info, 15);
        if (s.RestoreEnabled is null) return null;
        if (s.RestoreEnabled == true)
            return new("restore", Loc.T("ЗАЩИТА СИСТЕМЫ ВКЛЮЧЕНА"),
                Loc.T("Точки восстановления создаются — систему можно откатить штатными средствами Windows."), CheckSeverity.Ok, 15);
        return new("restore", Loc.T("ЗАЩИТА СИСТЕМЫ ВЫКЛЮЧЕНА"),
            Loc.T("Windows не создаёт точки восстановления: после неудачного обновления или драйвера откатиться будет не к чему, " +
            "а Lycoris не сможет сделать точку перед своими изменениями. Защита займёт часть диска C: (старые точки удаляются " +
            "автоматически). Включить её можно в разделе «Восстановление»."),
            CheckSeverity.Warning, 15, new CheckAction(CheckActionKind.Navigate, Loc.T("ВКЛЮЧИТЬ ЗАЩИТУ"), "restore"));
    }

    // ───────────────────────────── Free space ─────────────────────────────

    public static bool IsLowSpace(ulong free, ulong total) =>
        total > 0 && (free < LowSpaceBytes || (double)free / total < LowSpaceShare);

    public static bool IsCriticalSpace(ulong free, ulong total) =>
        total > 0 && (free < 5UL * 1024 * 1024 * 1024 || (double)free / total < 0.05);

    public static HealthCheckResult? CheckDiskSpace(SystemSnapshot s)
    {
        if (s.SystemDriveTotal == 0) return null;
        var free = s.SystemDriveFree;
        var total = s.SystemDriveTotal;
        var text = Loc.Tf("Свободно {0} из {1} ({2}).", DashFormat.Size(free), DashFormat.Size(total), DashFormat.Share((double)free / total));
        if (!IsLowSpace(free, total))
            return new("disk-space", Loc.T("МЕСТА НА СИСТЕМНОМ ДИСКЕ ДОСТАТОЧНО"), text, CheckSeverity.Ok, 15);
        var facts = SystemFileFacts(s);
        return new("disk-space", Loc.T("НА СИСТЕМНОМ ДИСКЕ МАЛО МЕСТА"),
            text + " " + Loc.T("Когда места мало, Windows не может установить обновления, а кэш шейдеров и файл подкачки работают хуже. " +
            "Удалите временные файлы в разделе «Обслуживание» → «Очистка» — перед удалением Lycoris покажет, что именно будет удалено.") +
            (facts.Length > 0 ? " " + facts : ""),
            CheckSeverity.Warning, IsCriticalSpace(free, total) ? 25 : 15,
            new CheckAction(CheckActionKind.Navigate, Loc.T("ОЧИСТИТЬ ДИСК"), "cleaner"), SystemFileLever(s));
    }

    /// <summary>
    /// What the Windows system files on the drive take (each fact only when it is known) and why Lycoris leaves two of
    /// them alone: hibernation can go on desktops only (ADVANCED; DO-NOT-INCLUDE on laptops — and when the laptop/desktop
    /// fact is unknown the lever is not offered either), the page file is managed by Windows (general_tweaks.md §10),
    /// reserved storage is released by Windows itself when space runs low.
    /// </summary>
    public static string SystemFileFacts(SystemSnapshot s)
    {
        var pagefileRepair = Loc.T("вернуть управление Windows можно пунктом «Файл подкачки: автоматический размер» в разделе «Оптимизация».");
        var parts = new List<string>();
        if (s.HiberfilBytes is > 0 and var hib)
            parts.Add(Loc.Tf("hiberfil.sys — {0}: файл гибернации и быстрого запуска.", DashFormat.Size(hib)) + (s.IsLaptop
                ? " " + Loc.T("На ноутбуке он нужен: при разряде батареи Windows сохраняет через него работу.")
                : CanOfferHibernationOff(s) ? " " + Loc.T("Его можно убрать пунктом «Отключить гибернацию» (ЭКСПЕРТ) в разделе «Оптимизация».") : ""));
        if (s.PagefileOnOtherDrive == true)
            parts.Add(s.PagefileBytes is > 0 and var shared
                ? Loc.Tf("pagefile.sys — {0}: файл подкачки настроен вручную и размещён ещё и на другом диске — Lycoris его не трогает.", DashFormat.Size(shared))
                : Loc.T("Файл подкачки размещён на другом диске — место на системном диске он не занимает."));
        else if (s.PagefileSystemManaged == false && s.PagefileBytes is > 0 and var manual)
            parts.Add(Loc.Tf("pagefile.sys — {0}: размер задан вручную; {1}", DashFormat.Size(manual), pagefileRepair));
        else if (s.PagefileSystemManaged == false && s.PagefileOnOtherDrive == false)
            parts.Add(Loc.T("Файл подкачки отключён — так игры и браузеры падают с нехваткой памяти; вернуть автоматический размер можно пунктом " +
                      "«Файл подкачки: автоматический размер» в разделе «Оптимизация»."));
        else if (s.PagefileSystemManaged == false)
            parts.Add(Loc.Tf("Файл подкачки настроен вручную; {0} Если вы сознательно перенесли его на другой диск, этот пункт не нужен.", pagefileRepair));
        else if (s.PagefileBytes is > 0 and var pf)
            parts.Add(Loc.Tf("pagefile.sys — {0}: размером управляет Windows, Lycoris его не трогает — " +
                      "отключение или уменьшение файла подкачки приводит к вылетам игр.", DashFormat.Size(pf)));
        if (ReservedStorageOn(s.ReservedStorageState) is { } reserved)
            parts.Add(reserved
                ? Loc.T("Зарезервированное хранилище включено: Windows освобождает его сама, когда места мало — " +
                      "отключать его не нужно, это лишь повышает риск неудачных обновлений.")
                : Loc.T("Зарезервированное хранилище отключено: Windows освобождает его сама, когда места мало — " +
                      "отключать его не нужно, это лишь повышает риск неудачных обновлений."));
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The one large, safe lever behind a low-space warning: hibernation off (desktops only — on a laptop the file is
    /// needed), otherwise the page-file repair when its size was set by hand. Both buttons only open the item in «Оптимизация».
    /// </summary>
    public static CheckAction? SystemFileLever(SystemSnapshot s)
    {
        if (CanOfferHibernationOff(s) && s.HiberfilBytes is > 0)
            return new CheckAction(CheckActionKind.FocusTweak, Loc.T("ОТКЛЮЧИТЬ ГИБЕРНАЦИЮ"), TweakHibernationOff);
        if (s.PagefileSystemManaged == false && s.PagefileOnOtherDrive != true)
            return new CheckAction(CheckActionKind.FocusTweak, Loc.T("НАСТРОИТЬ ПОДКАЧКУ"), TweakPagefileSystemManaged);
        return null;
    }

    /// <summary>
    /// Hibernation off is DO-NOT-INCLUDE on laptops, and the catalog marks it not applicable when the laptop fact is
    /// unknown (SystemProfile.HardwareFactsKnown == false) — the dashboard must not point there in either case.
    /// </summary>
    private static bool CanOfferHibernationOff(SystemSnapshot s) => !s.IsLaptop && s.HardwareFactsKnown;

    /// <summary>"Enabled" → true, "Disabled" → false, anything else → null.</summary>
    public static bool? ReservedStorageOn(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "enabled" => true,
        "disabled" => false,
        _ => null,
    };

    /// <summary>The state word from "(Get-WindowsReservedStorageState).ReservedStorageState" output (ignores other lines).</summary>
    public static string? ParseReservedStorageState(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return "Enabled";
            if (line.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return "Disabled";
        }
        return null;
    }

    // ───────────────────────────── Devices ─────────────────────────────

    /// <summary>Device Manager error codes that are not problems: 22 = disabled by the user, 45 = not connected now.</summary>
    public static bool IsProblemCode(int code) => code is not (0 or 22 or 45);

    public static string DeviceErrorText(int code) => code switch
    {
        1 => Loc.T("настроено неправильно"),
        3 => Loc.T("драйвер повреждён"),
        10 => Loc.T("не удаётся запустить"),
        12 => Loc.T("не хватает ресурсов"),
        14 => Loc.T("нужна перезагрузка"),
        18 => Loc.T("нужно переустановить драйвер"),
        19 => Loc.T("ошибка конфигурации в реестре"),
        21 => Loc.T("удаляется"),
        24 => Loc.T("устройство отсутствует или не готово"),
        28 => Loc.T("драйвер не установлен"),
        31 => Loc.T("работает неправильно"),
        32 => Loc.T("служба драйвера отключена"),
        37 => Loc.T("драйвер не инициализировался"),
        39 => Loc.T("драйвер повреждён или отсутствует"),
        41 => Loc.T("драйвер загружен, но устройство не найдено"),
        43 => Loc.T("устройство сообщило о сбое"),
        47 => Loc.T("подготовлено к безопасному извлечению"),
        48 => Loc.T("драйвер заблокирован"),
        52 => Loc.T("подпись драйвера не проверена"),
        _ => Loc.Tf("ошибка {0}", code),
    };

    public static HealthCheckResult? CheckDevices(SystemSnapshot s)
    {
        if (s.ProblemDevices is null) return null;
        var problems = s.ProblemDevices.Where(d => IsProblemCode(d.Code)).ToList();
        if (problems.Count == 0)
            return new("devices", Loc.T("ВСЕ УСТРОЙСТВА РАБОТАЮТ"), Loc.T("Диспетчер устройств не сообщает об ошибках."), CheckSeverity.Ok, 10);
        var shown = problems.Take(3).Select(d => Loc.Tf("{0} (код {1}: {2})", d.Name, d.Code, DeviceErrorText(d.Code)));
        var more = problems.Count > 3 ? Loc.Tf(" и ещё {0}", problems.Count - 3) : "";
        return new("devices", Loc.T("УСТРОЙСТВА С ОШИБКАМИ"),
            Loc.Tf("{0}{1}. Чаще всего помогает установка или обновление драйвера в разделе «Драйверы».", string.Join("; ", shown), more),
            CheckSeverity.Warning, 10, new CheckAction(CheckActionKind.Navigate, Loc.T("ИСПРАВИТЬ ДРАЙВЕРЫ"), "drivers", "tab:problems"));
    }

    // ───────────────────────────── Gaming settings ─────────────────────────────

    public static bool IsDualCcdX3D(string cpuName, int cores) =>
        cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase) && cores >= 12;

    public static HealthCheckResult? CheckGameMode(SystemSnapshot s)
    {
        if (s.GameModeOn is null) return null;
        if (s.GameModeOn == true)
            return new("game-mode", Loc.T("ИГРОВОЙ РЕЖИМ WINDOWS ВКЛЮЧЁН"),
                Loc.T("Windows отдаёт приоритет запущенной игре и не ставит драйверы во время игры."), CheckSeverity.Ok, 6);
        var x3d = IsDualCcdX3D(s.CpuName, s.CpuCores);
        return new("game-mode", Loc.T("ИГРОВОЙ РЕЖИМ WINDOWS ВЫКЛЮЧЕН"),
            Loc.T("Игровой режим Windows отдаёт приоритет игре и не даёт Центру обновления ставить драйверы и показывать уведомления о " +
            "перезагрузке во время игры.") + (x3d ? " " + Loc.T("На Ryzen X3D с двумя CCD он нужен, чтобы игра попадала на CCD с 3D V-Cache.") : "") +
            " " + Loc.T("Включается без перезагрузки; отменить можно в разделе «Восстановление»."),
            CheckSeverity.Warning, x3d ? 10 : 6, new CheckAction(CheckActionKind.ApplyTweak, Loc.T("ВКЛЮЧИТЬ"), TweakGameMode));
    }

    /// <summary>RTX 40/50 series: HAGS is required for DLSS Frame Generation there. Same rule as the «hags» tweak.</summary>
    public static bool IsRtx40Or50(string? gpuName) => gpuName is not null && Monolith.Tweaks.TweakCatalog.IsRtx40Or50(gpuName);

    public static HealthCheckResult? CheckHags(SystemSnapshot s)
    {
        switch (s.Hags)
        {
            case HagsState.On:
                return new("hags", Loc.T("АППАРАТНОЕ ПЛАНИРОВАНИЕ GPU ВКЛЮЧЕНО"),
                    Loc.T("Видеокарта сама управляет очередью кадров; доступна генерация кадров DLSS / FSR."), CheckSeverity.Ok, 6);
            case HagsState.Off:
                var rtx = IsRtx40Or50(s.PrimaryGpu?.Name);
                return new("hags", Loc.T("АППАРАТНОЕ ПЛАНИРОВАНИЕ GPU ВЫКЛЮЧЕНО"),
                    Loc.T("Видеокарта может сама планировать очередь кадров и разгрузить процессор.") +
                    (rtx ? " " + Loc.T("На RTX 40/50 это обязательно для генерации кадров DLSS.") : "") +
                    " " + Loc.T("Прирост FPS обычно в пределах ±5 %; при редких микрофризах его можно выключить обратно. Нужна перезагрузка."),
                    rtx ? CheckSeverity.Warning : CheckSeverity.Info, 6,
                    new CheckAction(CheckActionKind.ApplyTweak, Loc.T("ВКЛЮЧИТЬ"), rtx ? TweakHags : TweakHagsOther));
            default:
                return null; // unsupported by the GPU / driver, or unknown
        }
    }

    public static HealthCheckResult? CheckGameDvr(SystemSnapshot s)
    {
        if (s.BackgroundRecordingOn is null) return null;
        if (s.BackgroundRecordingOn == false)
            return new("gamedvr", Loc.T("ФОНОВАЯ ЗАПИСЬ ИГР ВЫКЛЮЧЕНА"), Loc.T("Game Bar не тратит ресурсы на постоянную запись видео."), CheckSeverity.Ok, 5);
        return new("gamedvr", Loc.T("ФОНОВАЯ ЗАПИСЬ ИГР ВКЛЮЧЕНА"),
            Loc.T("Xbox Game Bar постоянно записывает последние минуты игры. Кодирование видео отнимает 1–5 % FPS и часть видеопамяти. " +
            "После выключения скриншоты, Game Bar и игровой режим продолжают работать; отменить можно в разделе «Восстановление»."),
            CheckSeverity.Warning, 5, new CheckAction(CheckActionKind.ApplyTweak, Loc.T("ВЫКЛЮЧИТЬ"), TweakGameDvr));
    }

    // ───────────────────────────── Power plan (informational) ─────────────────────────────

    public static HealthCheckResult? CheckPowerPlan(SystemSnapshot s)
    {
        // Energy saver on mains power hides a performance loss behind any plan (read from GetSystemPowerStatus; on
        // 24H2+ it can be «always on», desktops included). No tweak controls it: the button opens the Settings page.
        if (s.EnergySaverOn == true && s.OnAcPower == true)
            return new("power-plan", Loc.T("ВКЛЮЧЕНО ЭНЕРГОСБЕРЕЖЕНИЕ ПРИ ПИТАНИИ ОТ СЕТИ"),
                Loc.T("Режим энергосбережения включён, хотя компьютер работает от сети: яркость снижена на 30 %, прозрачность отключена, " +
                "режим питания заблокирован на «Максимальная эффективность». Для игр и тяжёлых программ это потеря производительности " +
                "без каких-либо признаков. В Windows 11 24H2+ его можно включить «всегда», в том числе на настольном ПК. " +
                "Отключается в Параметрах → Система → Питание."),
                s.IsLaptop ? CheckSeverity.Info : CheckSeverity.Warning, s.IsLaptop ? 5 : 8,
                new CheckAction(CheckActionKind.OpenUri, Loc.T("ОТКРЫТЬ ПАРАМЕТРЫ ПИТАНИЯ"), PowerSettingsUri));

        if (s.PowerPlan == PowerPlanKind.Unknown) return null;
        var name = string.IsNullOrWhiteSpace(s.PowerPlanName) ? "—" : s.PowerPlanName;
        var title = Loc.Tf("ПЛАН ПИТАНИЯ «{0}»", name.ToUpperInvariant());
        var isX3D = s.CpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        var balanced = s.PowerPlan == PowerPlanKind.Balanced;

        // Windows 11 power mode «Максимальная эффективность» on the balanced plan while on mains (not on battery, where
        // it is the point). Not on dual-CCD X3D: the catalog does not offer the mode there (AMD driver interplay unverified).
        // The «Тихий ПК» scenario chooses this mode on purpose: no nudge back.
        if (balanced && s.AcPowerMode == Tweaks.TweakCatalog.PowerModeBestEfficiency
            && OptimizeLogic.ParseProfile(AppSettings.Current.LastProfile) != OptimizeProfile.Efficiency && s.EnergySaverOn != true
            && s.OnAcPower != false && !(isX3D && IsDualCcdX3D(s.CpuName, s.CpuCores)))
            return new("power-plan", Loc.T("РЕЖИМ ПИТАНИЯ ОТ СЕТИ — «МАКСИМАЛЬНАЯ ЭФФЕКТИВНОСТЬ»"),
                Loc.T("На сбалансированной схеме выбран режим питания «Максимальная эффективность»: ниже частоты и фоновая активность — " +
                "тише и экономнее, но для игр это заметная потеря. Если ПК используется для игр, выберите «Максимальная производительность»."),
                CheckSeverity.Info, 5, new CheckAction(CheckActionKind.FocusTweak, Loc.T("ВЫБРАТЬ РЕЖИМ ПИТАНИЯ"), TweakPowerModeBestPerformance));
        // Every suggestion here is the balanced plan; the button only opens its item in «Оптимизация».
        var open = new CheckAction(CheckActionKind.FocusTweak, Loc.T("СМЕНИТЬ СХЕМУ ПИТАНИЯ"), TweakBalancedPlan);

        if (isX3D && IsDualCcdX3D(s.CpuName, s.CpuCores))
        {
            open = new CheckAction(CheckActionKind.FocusTweak, Loc.T("СМЕНИТЬ СХЕМУ ПИТАНИЯ"), TweakBalancedPlanX3D);
            return balanced
                ? new("power-plan", title, Loc.T("Рекомендация AMD для Ryzen X3D с двумя CCD: драйвер правильно переносит игры на CCD с 3D V-Cache."), CheckSeverity.Ok, 8)
                : new("power-plan", title,
                    Loc.T("На Ryzen X3D с двумя CCD драйвер AMD переносит игры на CCD с 3D V-Cache только в «Сбалансированном» " +
                    "плане — так рекомендует AMD. В текущем плане эта логика может не срабатывать. Включить его можно в разделе «Оптимизация»."),
                    CheckSeverity.Info, 8, open);
        }
        if (isX3D)
        {
            return balanced
                ? new("power-plan", title, Loc.T("Рекомендация AMD для Ryzen X3D: меньше нагрев и энергопотребление в простое без потери FPS."), CheckSeverity.Ok, 0)
                : new("power-plan", title,
                    Loc.T("У Ryzen X3D с одним CCD выбор плана почти не влияет на FPS. AMD рекомендует «Сбалансированный» — " +
                    "он снижает нагрев и энергопотребление в простое; «Высокая производительность» вреда не наносит."),
                    CheckSeverity.Info, 0, open);
        }
        if (s.IsLaptop)
        {
            return s.PowerPlan is PowerPlanKind.HighPerformance or PowerPlanKind.Ultimate
                ? new("power-plan", title,
                    Loc.T("На ноутбуке этот план быстрее разряжает батарею и сильнее греет корпус, а прирост в играх обычно 0–2 %. " +
                    "Для работы от батареи лучше «Сбалансированный»."), CheckSeverity.Info, 5, open)
                : new("power-plan", title, Loc.T("Подходит для ноутбука: баланс производительности и времени работы от батареи."), CheckSeverity.Ok, 5);
        }
        return s.PowerPlan switch
        {
            PowerPlanKind.PowerSaver => new("power-plan", title,
                Loc.T("«Экономия энергии» ограничивает частоту процессора — в играх и тяжёлых программах это заметно. " +
                "Для настольного ПК подойдёт «Сбалансированный» или «Высокая производительность»."), CheckSeverity.Info, 8, open),
            PowerPlanKind.HighPerformance or PowerPlanKind.Ultimate => new("power-plan", title,
                Loc.T("Процессор держит высокие частоты без задержки на разгон; энергопотребление в простое выше."), CheckSeverity.Ok, 5),
            _ => new("power-plan", title,
                Loc.T("Подходит для большинства задач. «Высокая производительность» на настольном ПК даёт 0–2 % FPS ценой энергопотребления в простое."),
                CheckSeverity.Ok, 5),
        };
    }

    // ───────────────────────────── Restart / uptime ─────────────────────────────

    public static HealthCheckResult? CheckUptime(SystemSnapshot s)
    {
        if (s.Uptime <= TimeSpan.Zero) return null;
        var days = (int)s.Uptime.TotalDays;
        if (days < UptimeDaysLimit)
            return new("uptime", Loc.T("ВРЕМЯ РАБОТЫ В НОРМЕ"), Loc.Tf("Система работает {0} без перезагрузки.", DashFormat.Duration(s.Uptime)), CheckSeverity.Ok, 4);
        return new("uptime", Loc.Tf("БЕЗ ПЕРЕЗАГРУЗКИ {0}", Loc.N(days, "{0} ДЕНЬ", "{0} ДНЯ", "{0} ДНЕЙ")),
            Loc.T("За долгую работу копятся утечки памяти в драйверах и программах, а часть обновлений ждёт перезапуска. " +
            "Сохраните работу и выберите именно «Перезагрузка»: при включённом быстром запуске «Завершение работы» " +
            "систему полностью не перезапускает."), CheckSeverity.Warning, 4);
    }

    public static HealthCheckResult? CheckPendingReboot(SystemSnapshot s)
    {
        if (s.PendingReboot.Count == 0)
            return new("pending-reboot", Loc.T("НЕЗАВЕРШЁННЫХ ОБНОВЛЕНИЙ НЕТ"), Loc.T("Windows не ждёт перезагрузки для установки обновлений."), CheckSeverity.Ok, 6);
        return new("pending-reboot", Loc.T("ОЖИДАЕТСЯ ПЕРЕЗАГРУЗКА"),
            Loc.Tf("Перезапуска ждут: {0}. До перезагрузки часть исправлений не действует, " +
            "а установка драйверов и обновлений может завершиться ошибкой.", string.Join(", ", s.PendingReboot)), CheckSeverity.Warning, 6);
    }

    // ───────────────────────────── Windows Update: pinned version ─────────────────────────────

    /// <summary>
    /// A PC pinned to one Windows 11 version (TargetReleaseVersion) stops getting the yearly update — and, once that version
    /// reaches end of servicing, the monthly security updates too, for up to 60 days until Microsoft moves it on. The warning
    /// comes <see cref="UpdatePinWarnDays"/> days ahead; the action only opens the tweak row (its undo lifts the pin).
    /// </summary>
    public static HealthCheckResult? CheckUpdatePin(SystemSnapshot s)
    {
        if (string.IsNullOrWhiteSpace(s.UpdatePinnedTo)) return null;
        var v = s.UpdatePinnedTo.Trim();
        var open = new CheckAction(CheckActionKind.FocusTweak, Loc.T("НАСТРОИТЬ ЗАКРЕПЛЕНИЕ"), TweakUpdatePin);
        // The policy names another version than the installed one (Microsoft moved the PC on after the 60-day gap, or the
        // user upgraded): the installed version is serviced on its own schedule, so no «without updates» warning — the pin
        // is simply stale and only blocks the next yearly update.
        var installed = s.OsDisplayVersion.Trim();
        if (installed.Length > 0 && !string.Equals(installed, v, StringComparison.OrdinalIgnoreCase))
            return new("update-pin", Loc.T("ЗАКРЕПЛЕНИЕ ВЕРСИИ WINDOWS УСТАРЕЛО"),
                Loc.Tf("Политика закрепляет Windows 11 {0}, а установлена {1} — закрепление больше не действует как задумано, но " +
                "задержит следующее крупное обновление. Снимите его: откат пункта в «Оптимизации» или удаление политики, если её " +
                "задали не через Lycoris.", v, installed), CheckSeverity.Info, 0, open);
        if (Tweaks.TweakCatalog.KnownRelease(v) is not { } eos)
            return new("update-pin", Loc.T("ВЕРСИЯ WINDOWS ЗАКРЕПЛЕНА"),
                Loc.Tf("Windows 11 {0} закреплена политикой, дата поддержки неизвестна (этой версии нет в списке Lycoris). Крупные обновления " +
                "не приходят, пока закрепление не снято.", v), CheckSeverity.Info, 0, open);
        var date = Tweaks.TweakCatalog.DateText(eos);
        var daysLeft = eos.DayNumber - DateOnly.FromDateTime(s.Now).DayNumber;
        if (daysLeft <= UpdatePinWarnDays)
            return new("update-pin", Loc.T("ЗАКРЕПЛЁННАЯ ВЕРСИЯ WINDOWS СКОРО БЕЗ ОБНОВЛЕНИЙ"),
                Loc.Tf("Windows 11 {0} закреплена политикой и получает обновления до {1}. После этой даты до двух месяцев обновлений " +
                "безопасности может не быть. Снимите закрепление в «Оптимизации» (откат пункта).", v, date), CheckSeverity.Warning, 10, open);
        return new("update-pin", Loc.T("ВЕРСИЯ WINDOWS ЗАКРЕПЛЕНА"),
            Loc.Tf("Windows 11 {0} закреплена политикой и получает обновления до {1}. Крупные обновления не приходят, пока закрепление не снято.", v, date),
            CheckSeverity.Info, 0, open);
    }

    // ───────────────────────────── SSD TRIM ─────────────────────────────

    [GeneratedRegex(@"NTFS\s+DisableDeleteNotify\s*=\s*(\d)", RegexOptions.IgnoreCase)] private static partial Regex NtfsTrimRx();
    [GeneratedRegex(@"DisableDeleteNotify\s*=\s*(\d)", RegexOptions.IgnoreCase)] private static partial Regex AnyTrimRx();

    /// <summary>Parses "fsutil behavior query DisableDeleteNotify": true = TRIM disabled (value 1). Null = unknown.</summary>
    public static bool? ParseTrimDisabled(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = NtfsTrimRx().Match(output);
        if (!m.Success) m = AnyTrimRx().Match(output);
        return m.Success ? m.Groups[1].Value != "0" : null;
    }

    public static HealthCheckResult? CheckTrim(SystemSnapshot s)
    {
        if (s.TrimDisabled is null) return null;
        if (s.SystemDisk is { } disk && !disk.IsSsd) return null; // TRIM matters for SSDs only
        if (s.TrimDisabled == false)
            return new("trim", Loc.T("TRIM ВКЛЮЧЁН"), Loc.T("SSD получает команды очистки освобождённых блоков — скорость записи не деградирует."), CheckSeverity.Ok, 10);
        return new("trim", Loc.T("TRIM ВЫКЛЮЧЕН"),
            Loc.T("SSD не получает команды очистки освобождённых блоков: со временем запись замедляется, а износ растёт. " +
            "Обычно его выключают сторонние «твикеры». Кнопка включает его через журнал Lycoris — откат вернёт прежнее значение."),
            CheckSeverity.Warning, 10, new CheckAction(CheckActionKind.ApplyTweak, Loc.T("ВКЛЮЧИТЬ"), TweakTrim));
    }

    // ───────────────────────────── GPU driver ─────────────────────────────

    /// <summary>NVIDIA's own version from the Windows driver version: "32.0.15.8180" → "581.80".</summary>
    public static string? NvidiaDriverVersion(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion)) return null;
        var parts = windowsVersion.Split('.');
        if (parts.Length != 4 || !parts.All(p => p.Length > 0 && p.All(char.IsDigit))) return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3])}.{last5[3..]}";
    }

    private static readonly (string Long, string Short)[] Vendors =
    [
        ("Micro-Star International Co., Ltd.", "MSI"), ("Micro-Star International Co.,Ltd.", "MSI"), ("Micro-Star International", "MSI"),
        ("ASUSTeK COMPUTER INC.", "ASUS"), ("ASUSTeK Computer Inc", "ASUS"),
        ("Gigabyte Technology Co., Ltd.", "Gigabyte"), ("Gigabyte Technology", "Gigabyte"),
        ("ASRock Incorporation", "ASRock"), ("Hewlett-Packard", "HP"), ("Dell Inc.", "Dell"), ("Acer Inc.", "Acer"),
        ("LENOVO", "Lenovo"), ("Micro Star", "MSI"),
    ];

    /// <summary>Board / PC vendor in its short form: "Micro-Star International Co., Ltd. PRO X870-P" → "MSI PRO X870-P".</summary>
    public static string ShortVendor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim();
        foreach (var (full, brand) in Vendors)
        {
            if (n.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                return (brand + " " + n[full.Length..].Trim()).Trim();
        }
        return n;
    }

    public static string DriverVersionText(GpuInfo gpu) =>
        gpu.Vendor == GpuVendor.Nvidia && NvidiaDriverVersion(gpu.DriverVersion) is { } nv ? nv : gpu.DriverVersion;

    public static bool IsDriverOutdated(DateTime? driverDate, DateTime now, int days = DriverAgeDaysLimit) =>
        driverDate is { } d && d.Year > 2000 && (now - d).TotalDays > days;

    public static HealthCheckResult? CheckGpuDriver(SystemSnapshot s)
    {
        var gpu = s.PrimaryGpu;
        if (gpu is null || gpu.Vendor is GpuVendor.Microsoft or GpuVendor.Unknown) return null;
        if (gpu.DriverDate is not { Year: > 2000 } date) return null;
        var version = DriverVersionText(gpu);
        if (!IsDriverOutdated(date, s.Now))
            return new("gpu-driver", Loc.T("ДРАЙВЕР ВИДЕОКАРТЫ СВЕЖИЙ"),
                Loc.Tf("{0}: версия {1} от {2}.", gpu.Name, version, DashFormat.Date(date)), CheckSeverity.Ok, 6);
        return new("gpu-driver", Loc.T("ДРАЙВЕР ВИДЕОКАРТЫ УСТАРЕЛ"),
            Loc.Tf("{0}: версия {1} от {2} — ему {3}. " +
            "Новые драйверы исправляют ошибки и ускоряют свежие игры. Проверьте обновление в разделе «Драйверы».",
            gpu.Name, version, DashFormat.Date(date), DashFormat.Age(date, s.Now)),
            CheckSeverity.Warning, 6, new CheckAction(CheckActionKind.Navigate, Loc.T("ПРОВЕРИТЬ ДРАЙВЕР"), "drivers", "tab:overview"));
    }

    // ───────────────────────────── Startup ─────────────────────────────

    /// <summary>
    /// StartupApproved value (Task Manager format): disabled when bit 0 of the first byte is set
    /// (02 = enabled, 03 = disabled); a missing value means enabled.
    /// </summary>
    public static bool IsStartupEntryEnabled(byte[]? approved) => approved is not { Length: > 0 } || (approved[0] & 1) == 0;

    public static HealthCheckResult? CheckStartup(SystemSnapshot s)
    {
        if (s.StartupCount is not { } n) return null;
        var count = Loc.N(n, "{0} программа", "{0} программы", "{0} программ");
        if (n <= StartupLimit)
            return new("startup", Loc.T("АВТОЗАПУСК В НОРМЕ"), Loc.Tf("Вместе с Windows запускается {0}.", count), CheckSeverity.Ok, 6);
        return new("startup", Loc.T("МНОГО ПРОГРАММ В АВТОЗАПУСКЕ"),
            Loc.Tf("Вместе с Windows запускается {0}. Каждая замедляет вход в систему и работает в фоне. Отключите лишние " +
            "в разделе «Автозапуск и службы» — это обратимо, программы не удаляются.", count),
            CheckSeverity.Warning, 6, new CheckAction(CheckActionKind.Navigate, Loc.T("ОТКЛЮЧИТЬ ЛИШНЕЕ"), "startup"));
    }
}
