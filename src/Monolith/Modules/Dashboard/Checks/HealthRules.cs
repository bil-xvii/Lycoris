using System.Text.RegularExpressions;
using Monolith.Core;

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
    public const string TweakGameDvr = "gamedvr-off";

    /// <summary>Free space is "low" below this share of the system drive…</summary>
    public const double LowSpaceShare = 0.15;
    /// <summary>…or below this many bytes.</summary>
    public const ulong LowSpaceBytes = 20UL * 1024 * 1024 * 1024;
    public const int UptimeDaysLimit = 7;
    public const int DriverAgeDaysLimit = 183;
    public const int StartupLimit = 10;

    /// <summary>JEDEC base speeds: a module stuck at one of these while rated higher has XMP/EXPO off.</summary>
    private static readonly int[] JedecSpeeds = [2133, 2400, 2666, 2933, 3200, 4800, 5200, 5600];

    public static IReadOnlyList<HealthCheckResult> Evaluate(SystemSnapshot s)
    {
        var checks = new Func<SystemSnapshot, HealthCheckResult?>[]
        {
            CheckRestore, CheckDiskSpace, CheckDevices, CheckRefreshRate, CheckRam, CheckTrim, CheckPendingReboot,
            CheckGpuDriver, CheckGameMode, CheckHags, CheckGameDvr, CheckStartup, CheckUptime, CheckPowerPlan, CheckVbs,
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
                Log.Warn($"Проверка «{check.Method.Name}» пропущена: {ex.Message}");
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
        var kit = $"{v.Configured} МТ/с{(v.Type.Length > 0 ? " · " + v.Type : "")} · {DashFormat.Count(v.Modules, "модуль", "модуля", "модулей")}";
        if (!v.BelowRated)
            return new("ram-speed", "ПАМЯТЬ НА НОМИНАЛЬНОЙ ЧАСТОТЕ", $"{kit} — частота соответствует маркировке модулей.", CheckSeverity.Ok, 10);

        var detail = $"Модули рассчитаны на {v.Rated} МТ/с, а работают на {v.Configured} МТ/с. Скорее всего, в BIOS выключен профиль " +
                     "XMP (Intel) или EXPO (AMD) — в играх, чувствительных к памяти, это заметная потеря FPS. Включите профиль в BIOS/UEFI " +
                     "(раздел настроек памяти → XMP/EXPO → сохранить F10). MONOLITH настройки BIOS не меняет; если система станет " +
                     "нестабильной, верните там значение «Auto».";
        if (s.IsLaptop)
            detail += " На ноутбуках профиль часто недоступен — тогда это нормальная работа.";
        return new("ram-speed", "ПАМЯТЬ РАБОТАЕТ НИЖЕ НОМИНАЛА", detail, s.IsLaptop ? CheckSeverity.Info : CheckSeverity.Warning, 10);
    }

    // ───────────────────────────── Refresh rate ─────────────────────────────

    /// <summary>Highest progressive refresh rate offered at the given resolution (0 when none).</summary>
    public static int MaxRefreshAt(IEnumerable<DisplayMode> modes, int width, int height) =>
        modes.Where(m => m.Width == width && m.Height == height && !m.Interlaced).Select(m => m.Frequency).DefaultIfEmpty(0).Max();

    /// <summary>True when the display could run faster. 1 Hz of slack: 239 vs 240 Hz is the same fractional timing.</summary>
    public static bool IsBelowMaxRefresh(int currentHz, int maxHz) => currentHz > 1 && maxHz > currentHz + 1;

    public static HealthCheckResult? CheckRefreshRate(SystemSnapshot s)
    {
        if (s.Displays.Count == 0) return null;
        var slow = s.Displays.Where(d => IsBelowMaxRefresh(d.CurrentHz, d.MaxHz)).ToList();
        if (slow.Count == 0)
        {
            var list = string.Join("; ", s.Displays.Select(d => $"{d.Name} — {d.CurrentHz} Гц при {d.Width}×{d.Height}"));
            return new("refresh-rate", "ЧАСТОТА ОБНОВЛЕНИЯ МАКСИМАЛЬНАЯ", list + ".", CheckSeverity.Ok, 10);
        }
        var what = string.Join("; ", slow.Select(d => $"{d.Name}: сейчас {d.CurrentHz} Гц, доступно {d.MaxHz} Гц при {d.Width}×{d.Height}"));
        var detail = what + ". Выберите максимальную частоту: «Параметры → Дисплей → Расширенные параметры дисплея → " +
                     "Частота обновления». Игры и интерфейс станут плавнее; изменение мгновенное, вернуть можно там же.";
        return new("refresh-rate", "МОНИТОР НЕ НА МАКСИМАЛЬНОЙ ЧАСТОТЕ", detail, CheckSeverity.Warning, 10,
            new CheckAction(CheckActionKind.OpenUri, "ОТКРЫТЬ ПАРАМЕТРЫ", "ms-settings:display-advanced"));
    }

    // ───────────────────────────── System Restore ─────────────────────────────

    public static HealthCheckResult? CheckRestore(SystemSnapshot s)
    {
        if (s.RestoreBlockedByPolicy)
            return new("restore", "ЗАЩИТА СИСТЕМЫ ЗАПРЕЩЕНА ПОЛИТИКОЙ",
                "Точки восстановления отключены групповой политикой (обычно это решение администратора организации). " +
                "MONOLITH политики не обходит; собственные изменения по-прежнему откатываются в разделе «Восстановление».",
                CheckSeverity.Info, 15);
        if (s.RestoreEnabled is null) return null;
        if (s.RestoreEnabled == true)
            return new("restore", "ЗАЩИТА СИСТЕМЫ ВКЛЮЧЕНА",
                "Точки восстановления создаются — систему можно откатить штатными средствами Windows.", CheckSeverity.Ok, 15);
        return new("restore", "ЗАЩИТА СИСТЕМЫ ВЫКЛЮЧЕНА",
            "Windows не создаёт точки восстановления: после неудачного обновления или драйвера откатиться будет не к чему, " +
            "а MONOLITH не сможет сделать точку перед своими изменениями. Защита займёт часть диска C: (старые точки удаляются " +
            "автоматически). Выключить её можно в «Свойства системы → Защита системы».",
            CheckSeverity.Warning, 15, new CheckAction(CheckActionKind.EnableRestore, "ВКЛЮЧИТЬ"));
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
        var text = $"Свободно {DashFormat.Size(free)} из {DashFormat.Size(total)} ({DashFormat.Share((double)free / total)}).";
        if (!IsLowSpace(free, total))
            return new("disk-space", "МЕСТА НА СИСТЕМНОМ ДИСКЕ ДОСТАТОЧНО", text, CheckSeverity.Ok, 15);
        return new("disk-space", "НА СИСТЕМНОМ ДИСКЕ МАЛО МЕСТА",
            text + " Когда места мало, Windows не может установить обновления, а кэш шейдеров и файл подкачки работают хуже. " +
            "Удалите временные файлы в разделе «Очистка» — перед удалением он покажет, что именно будет удалено.",
            CheckSeverity.Warning, IsCriticalSpace(free, total) ? 25 : 15,
            new CheckAction(CheckActionKind.Navigate, "К ОЧИСТКЕ", "cleaner"));
    }

    // ───────────────────────────── Devices ─────────────────────────────

    /// <summary>Device Manager error codes that are not problems: 22 = disabled by the user, 45 = not connected now.</summary>
    public static bool IsProblemCode(int code) => code is not (0 or 22 or 45);

    public static string DeviceErrorText(int code) => code switch
    {
        1 => "настроено неправильно",
        3 => "драйвер повреждён",
        10 => "не удаётся запустить",
        12 => "не хватает ресурсов",
        14 => "нужна перезагрузка",
        18 => "нужно переустановить драйвер",
        19 => "ошибка конфигурации в реестре",
        21 => "удаляется",
        24 => "устройство отсутствует или не готово",
        28 => "драйвер не установлен",
        31 => "работает неправильно",
        32 => "служба драйвера отключена",
        37 => "драйвер не инициализировался",
        39 => "драйвер повреждён или отсутствует",
        41 => "драйвер загружен, но устройство не найдено",
        43 => "устройство сообщило о сбое",
        47 => "подготовлено к безопасному извлечению",
        48 => "драйвер заблокирован",
        52 => "подпись драйвера не проверена",
        _ => $"ошибка {code}",
    };

    public static HealthCheckResult? CheckDevices(SystemSnapshot s)
    {
        if (s.ProblemDevices is null) return null;
        var problems = s.ProblemDevices.Where(d => IsProblemCode(d.Code)).ToList();
        if (problems.Count == 0)
            return new("devices", "ВСЕ УСТРОЙСТВА РАБОТАЮТ", "Диспетчер устройств не сообщает об ошибках.", CheckSeverity.Ok, 10);
        var shown = problems.Take(3).Select(d => $"{d.Name} (код {d.Code}: {DeviceErrorText(d.Code)})");
        var more = problems.Count > 3 ? $" и ещё {problems.Count - 3}" : "";
        return new("devices", "УСТРОЙСТВА С ОШИБКАМИ",
            $"{string.Join("; ", shown)}{more}. Чаще всего помогает установка или обновление драйвера в разделе «Драйверы».",
            CheckSeverity.Warning, 10, new CheckAction(CheckActionKind.Navigate, "К ДРАЙВЕРАМ", "drivers"));
    }

    // ───────────────────────────── Gaming settings ─────────────────────────────

    public static bool IsDualCcdX3D(string cpuName, int cores) =>
        cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase) && cores >= 12;

    public static HealthCheckResult? CheckGameMode(SystemSnapshot s)
    {
        if (s.GameModeOn is null) return null;
        if (s.GameModeOn == true)
            return new("game-mode", "ИГРОВОЙ РЕЖИМ ВКЛЮЧЁН",
                "Windows отдаёт приоритет запущенной игре и не ставит драйверы во время игры.", CheckSeverity.Ok, 6);
        var x3d = IsDualCcdX3D(s.CpuName, s.CpuCores);
        return new("game-mode", "ИГРОВОЙ РЕЖИМ ВЫКЛЮЧЕН",
            "Игровой режим отдаёт приоритет игре и не даёт Центру обновления ставить драйверы и показывать уведомления о " +
            "перезагрузке во время игры." + (x3d ? " На двухкристальных Ryzen X3D он нужен, чтобы игра попадала на кристалл с 3D V-Cache." : "") +
            " Включается без перезагрузки; отменить можно в разделе «Восстановление».",
            CheckSeverity.Warning, x3d ? 10 : 6, new CheckAction(CheckActionKind.ApplyTweak, "ВКЛЮЧИТЬ", TweakGameMode));
    }

    [GeneratedRegex(@"RTX\s*[45]0\d{2}", RegexOptions.IgnoreCase)] private static partial Regex Rtx4050Rx();

    /// <summary>RTX 40/50 series: HAGS is required for DLSS Frame Generation there.</summary>
    public static bool IsRtx40Or50(string? gpuName) => gpuName is not null && Rtx4050Rx().IsMatch(gpuName);

    public static HealthCheckResult? CheckHags(SystemSnapshot s)
    {
        switch (s.Hags)
        {
            case HagsState.On:
                return new("hags", "АППАРАТНОЕ ПЛАНИРОВАНИЕ GPU ВКЛЮЧЕНО",
                    "Видеокарта сама управляет очередью кадров; доступна генерация кадров DLSS / FSR.", CheckSeverity.Ok, 6);
            case HagsState.Off:
                var rtx = IsRtx40Or50(s.PrimaryGpu?.Name);
                return new("hags", "АППАРАТНОЕ ПЛАНИРОВАНИЕ GPU ВЫКЛЮЧЕНО",
                    "Видеокарта может сама планировать очередь кадров и разгрузить процессор." +
                    (rtx ? " На RTX 40/50 это обязательно для генерации кадров DLSS." : "") +
                    " Прирост FPS обычно в пределах ±5 %; при редких микрофризах его можно выключить обратно. Нужна перезагрузка.",
                    rtx ? CheckSeverity.Warning : CheckSeverity.Info, 6,
                    new CheckAction(CheckActionKind.ApplyTweak, "ВКЛЮЧИТЬ", TweakHags));
            default:
                return null; // unsupported by the GPU / driver, or unknown
        }
    }

    public static HealthCheckResult? CheckGameDvr(SystemSnapshot s)
    {
        if (s.BackgroundRecordingOn is null) return null;
        if (s.BackgroundRecordingOn == false)
            return new("gamedvr", "ФОНОВАЯ ЗАПИСЬ ИГР ВЫКЛЮЧЕНА", "Game Bar не тратит ресурсы на постоянную запись видео.", CheckSeverity.Ok, 5);
        return new("gamedvr", "ФОНОВАЯ ЗАПИСЬ ИГР ВКЛЮЧЕНА",
            "Xbox Game Bar постоянно записывает последние минуты игры. Кодирование видео отнимает 1–5 % FPS и часть видеопамяти. " +
            "После выключения скриншоты, Game Bar и игровой режим продолжают работать; отменить можно в разделе «Восстановление».",
            CheckSeverity.Warning, 5, new CheckAction(CheckActionKind.ApplyTweak, "ВЫКЛЮЧИТЬ", TweakGameDvr));
    }

    // ───────────────────────────── Power plan (informational) ─────────────────────────────

    public static HealthCheckResult? CheckPowerPlan(SystemSnapshot s)
    {
        if (s.PowerPlan == PowerPlanKind.Unknown) return null;
        var name = string.IsNullOrWhiteSpace(s.PowerPlanName) ? "—" : s.PowerPlanName;
        var title = $"ПЛАН ПИТАНИЯ «{name.ToUpperInvariant()}»";
        var open = new CheckAction(CheckActionKind.OpenPowerOptions, "ЭЛЕКТРОПИТАНИЕ");
        var isX3D = s.CpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        var balanced = s.PowerPlan == PowerPlanKind.Balanced;

        if (isX3D && IsDualCcdX3D(s.CpuName, s.CpuCores))
        {
            return balanced
                ? new("power-plan", title, "Рекомендация AMD для двухкристальных Ryzen X3D: драйвер правильно переносит игры на кристалл с 3D V-Cache.", CheckSeverity.Ok, 8)
                : new("power-plan", title,
                    "На двухкристальных Ryzen X3D драйвер AMD переносит игры на кристалл с 3D V-Cache только в «Сбалансированном» " +
                    "плане — так рекомендует AMD. В текущем плане эта логика может не срабатывать. Выберите «Сбалансированный» в «Электропитании».",
                    CheckSeverity.Info, 8, open);
        }
        if (isX3D)
        {
            return balanced
                ? new("power-plan", title, "Рекомендация AMD для Ryzen X3D: меньше нагрев и энергопотребление в простое без потери FPS.", CheckSeverity.Ok, 0)
                : new("power-plan", title,
                    "У Ryzen X3D с одним кристаллом выбор плана почти не влияет на FPS. AMD рекомендует «Сбалансированный» — " +
                    "он снижает нагрев и энергопотребление в простое; «Высокая производительность» вреда не наносит.",
                    CheckSeverity.Info, 0, open);
        }
        if (s.IsLaptop)
        {
            return s.PowerPlan is PowerPlanKind.HighPerformance or PowerPlanKind.Ultimate
                ? new("power-plan", title,
                    "На ноутбуке этот план быстрее разряжает батарею и сильнее греет корпус, а прирост в играх обычно 0–2 %. " +
                    "Для работы от батареи лучше «Сбалансированный».", CheckSeverity.Info, 5, open)
                : new("power-plan", title, "Подходит для ноутбука: баланс производительности и времени работы от батареи.", CheckSeverity.Ok, 5);
        }
        return s.PowerPlan switch
        {
            PowerPlanKind.PowerSaver => new("power-plan", title,
                "«Экономия энергии» ограничивает частоту процессора — в играх и тяжёлых программах это заметно. " +
                "Для настольного ПК подойдёт «Сбалансированный» или «Высокая производительность».", CheckSeverity.Info, 8, open),
            PowerPlanKind.HighPerformance or PowerPlanKind.Ultimate => new("power-plan", title,
                "Процессор держит высокие частоты без задержки на разгон; энергопотребление в простое выше.", CheckSeverity.Ok, 5),
            _ => new("power-plan", title,
                "Подходит для большинства задач. «Высокая производительность» на настольном ПК даёт 0–2 % FPS ценой энергопотребления в простое.",
                CheckSeverity.Ok, 5),
        };
    }

    // ───────────────────────────── Restart / uptime ─────────────────────────────

    public static HealthCheckResult? CheckUptime(SystemSnapshot s)
    {
        if (s.Uptime <= TimeSpan.Zero) return null;
        var days = (int)s.Uptime.TotalDays;
        if (days < UptimeDaysLimit)
            return new("uptime", "ВРЕМЯ РАБОТЫ В НОРМЕ", $"Система работает {DashFormat.Duration(s.Uptime)} без перезагрузки.", CheckSeverity.Ok, 4);
        return new("uptime", $"БЕЗ ПЕРЕЗАГРУЗКИ {DashFormat.Count(days, "ДЕНЬ", "ДНЯ", "ДНЕЙ")}",
            "За долгую работу копятся утечки памяти в драйверах и программах, а часть обновлений ждёт перезапуска. " +
            "Сохраните работу и выберите именно «Перезагрузка»: при включённом быстром запуске «Завершение работы» " +
            "систему полностью не перезапускает.", CheckSeverity.Warning, 4);
    }

    public static HealthCheckResult? CheckPendingReboot(SystemSnapshot s)
    {
        if (s.PendingReboot.Count == 0)
            return new("pending-reboot", "НЕЗАВЕРШЁННЫХ ОБНОВЛЕНИЙ НЕТ", "Windows не ждёт перезагрузки для установки обновлений.", CheckSeverity.Ok, 6);
        return new("pending-reboot", "ОЖИДАЕТСЯ ПЕРЕЗАГРУЗКА",
            $"Перезапуска ждут: {string.Join(", ", s.PendingReboot)}. До перезагрузки часть исправлений не действует, " +
            "а установка драйверов и обновлений может завершиться ошибкой.", CheckSeverity.Warning, 6);
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
            return new("trim", "TRIM ВКЛЮЧЁН", "SSD получает команды очистки освобождённых блоков — скорость записи не деградирует.", CheckSeverity.Ok, 10);
        return new("trim", "TRIM ВЫКЛЮЧЕН",
            "SSD не получает команды очистки освобождённых блоков: со временем запись замедляется, а износ растёт. " +
            "Обычно его выключают сторонние «твикеры». Включить можно командой «fsutil behavior set DisableDeleteNotify 0» " +
            "в терминале от имени администратора.", CheckSeverity.Warning, 10);
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
            return new("gpu-driver", "ДРАЙВЕР ВИДЕОКАРТЫ СВЕЖИЙ",
                $"{gpu.Name}: версия {version} от {DashFormat.Date(date)}.", CheckSeverity.Ok, 6);
        return new("gpu-driver", "ДРАЙВЕР ВИДЕОКАРТЫ УСТАРЕЛ",
            $"{gpu.Name}: версия {version} от {DashFormat.Date(date)} — ему {DashFormat.Age(date, s.Now)}. " +
            "Новые драйверы исправляют ошибки и ускоряют свежие игры. Проверьте обновление в разделе «Драйверы».",
            CheckSeverity.Warning, 6, new CheckAction(CheckActionKind.Navigate, "К ДРАЙВЕРАМ", "drivers"));
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
        var count = DashFormat.Count(n, "программа", "программы", "программ");
        if (n <= StartupLimit)
            return new("startup", "АВТОЗАГРУЗКА В НОРМЕ", $"Вместе с Windows запускается {count}.", CheckSeverity.Ok, 6);
        return new("startup", "МНОГО ПРОГРАММ В АВТОЗАГРУЗКЕ",
            $"Вместе с Windows запускается {count}. Каждая замедляет вход в систему и работает в фоне. Отключите лишние " +
            "в разделе «Автозагрузка» — это обратимо, программы не удаляются.",
            CheckSeverity.Warning, 6, new CheckAction(CheckActionKind.Navigate, "К АВТОЗАГРУЗКЕ", "startup"));
    }

    // ───────────────────────────── VBS / Memory Integrity (information only) ─────────────────────────────

    public static HealthCheckResult? CheckVbs(SystemSnapshot s) => s.Vbs switch
    {
        VbsState.MemoryIntegrity => new("vbs", "ЦЕЛОСТНОСТЬ ПАМЯТИ ВКЛЮЧЕНА",
            "Изоляция ядра защищает Windows от вредоносных и уязвимых драйверов. В части игр она стоит 0–10 % FPS (в среднем " +
            "около 5 %), а античиты Riot Vanguard и FACEIT могут её требовать. MONOLITH защиту не отключает: это снижает " +
            "безопасность. Настройка — «Безопасность Windows → Безопасность устройства → Изоляция ядра».",
            CheckSeverity.Info, 0),
        VbsState.VbsOnly => new("vbs", "ВИРТУАЛИЗАЦИЯ БЕЗОПАСНОСТИ АКТИВНА",
            "Windows изолирует часть ядра в защищённой виртуальной среде (VBS). Это защита от атак на учётные данные и ядро; " +
            "влияние на игры обычно небольшое. MONOLITH её не отключает.", CheckSeverity.Info, 0),
        _ => null,
    };
}
