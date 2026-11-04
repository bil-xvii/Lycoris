using Lycoris.Modules.Dashboard.Checks;

namespace Lycoris.Modules.Dashboard.Doctor;

/// <summary>One program (all its processes grouped by name) and what it used during the measurement.</summary>
public sealed record ProcessUse(string Name, double CpuPercent, long MemoryBytes);

/// <summary>What «ПОЧЕМУ ТОРМОЗИТ?» measured: averages over the window and the heaviest programs. Null = not measured.</summary>
public sealed record DoctorSample(
    TimeSpan Window,
    double? CpuAvg,
    double? RamPercent,
    double? DiskAvg,
    double? GpuTemperature,
    IReadOnlyList<ProcessUse> TopCpu,
    IReadOnlyList<ProcessUse> TopMemory,
    int Samples = 10,
    int HiddenPrograms = 0);

/// <summary>
/// «Доктор ПК» (roadmap item 2): explains in plain words what slows the PC down right now. Pure: a measurement and the
/// «Панель» snapshot in, findings out — the same <see cref="HealthCheckResult"/> rows with the same buttons as the checks, so a
/// fix is the one existing command. It only reads; it never closes a program: the user decides, Task Manager is one click away.
/// Rules follow Microsoft's «Tips to improve PC performance in Windows» (<see cref="MicrosoftTipsUrl"/>).
/// </summary>
public static class PcDoctor
{
    public const string MicrosoftTipsUrl =
        "https://support.microsoft.com/en-us/windows/tips-to-improve-pc-performance-in-windows-b3b3ef5b-5953-fb6a-2528-4bbed82fba96";

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    // Thresholds: sustained load over the window, not a single spike.
    internal const double CpuBusy = 80, CpuNoticeable = 50, ProcessHeavyCpu = 25;
    internal const double RamBusy = 85, DiskBusy = 80, GpuHot = 85;
    internal const double LowSpaceFraction = 0.10, LowSpaceGb = 15;
    internal const int UptimeDays = 7;

    /// <summary>Fewer live samples than this (page left or window minimized during the window) = no diagnosis, only «repeat».</summary>
    internal const int MinSamples = 3;

    public static string TaskManagerPath => System.IO.Path.Combine(Environment.SystemDirectory, "Taskmgr.exe");

    private static CheckAction TaskManager => new(CheckActionKind.OpenTaskManager, Loc.T("ДИСПЕТЧЕР ЗАДАЧ"), TaskManagerPath);

    public static IReadOnlyList<HealthCheckResult> Diagnose(DoctorSample m, SystemSnapshot s)
    {
        var list = new List<HealthCheckResult>();

        // No honest diagnosis without the live samples: say so instead of «nothing is overloaded».
        if (m.Samples < MinSamples)
            return [new("doctor-interrupted", Loc.T("ЗАМЕР ПРЕРВАН"),
                Loc.T("Монитор нагрузки не успел собрать данные за время замера. Нажмите «ПОЧЕМУ ТОРМОЗИТ?» ещё раз."),
                CheckSeverity.Info, 0)];

        // 1. Processor: who keeps it busy. Without admin rights the system services (Windows Update, antivirus, indexing)
        // cannot be read, so a busy processor with no visible culprit is still reported — and the text says why.
        if (m.CpuAvg is { } cpu && cpu >= CpuNoticeable)
        {
            var who = Names(m.TopCpu.Where(p => p.CpuPercent >= 3), p => Loc.Tf("{0} — {1} %", p.Name, Math.Round(p.CpuPercent)));
            var culprit = m.TopCpu.Any(p => p.CpuPercent >= ProcessHeavyCpu);
            list.Add(new("doctor-cpu", Loc.Tf("ПРОЦЕССОР ЗАГРУЖЕН НА {0} %", Math.Round(cpu)),
                (who.Length > 0 ? Loc.Tf("Больше всего его занимают: {0}. ", who) : "") +
                (culprit
                    ? Loc.T("Закройте программу, если она сейчас не нужна, или дождитесь, пока она закончит работу (обновление, проверка, сжатие видео).")
                    : Loc.T("Заметной программы-виновника не видно: вероятно, это службы Windows (обновления, антивирус, индексирование). " +
                            "Без прав администратора Lycoris их не видит — подробности на вкладке «Процессы» диспетчера задач.")),
                cpu >= CpuBusy ? CheckSeverity.Warning : CheckSeverity.Info, 0, TaskManager));
        }

        // 2. Memory.
        if (m.RamPercent is { } ram && ram >= RamBusy)
        {
            var who = Names(m.TopMemory, p => Loc.Tf("{0} — {1}", p.Name, DashFormat.Size(p.MemoryBytes)));
            list.Add(new("doctor-ram", Loc.Tf("ПАМЯТЬ ЗАНЯТА НА {0} %", Math.Round(ram)),
                (who.Length > 0 ? Loc.Tf("Больше всего памяти у программ: {0}. ", who) : "") +
                Loc.T("Когда память кончается, Windows переносит данные на диск и всё замедляется. Закройте ненужные программы и вкладки браузера."),
                CheckSeverity.Warning, 0, TaskManager));
        }

        // 3. Disk activity.
        if (m.DiskAvg is { } disk && disk >= DiskBusy)
            list.Add(new("doctor-disk", Loc.Tf("ДИСК ЗАНЯТ {0} % ВРЕМЕНИ", Math.Round(disk)),
                Loc.T("Пока диск занят, программы открываются медленно. Обычно это установка обновлений, проверка антивирусом или индексирование — " +
                      "они заканчиваются сами. На вкладке «Производительность» диспетчера задач видно, кто читает и пишет."),
                CheckSeverity.Warning, 0, TaskManager));

        // 4. Free space on the system drive.
        if (s.SystemDriveTotal > 0)
        {
            var free = s.SystemDriveFree;
            if (free < s.SystemDriveTotal * LowSpaceFraction || free < LowSpaceGb * 1024d * 1024 * 1024)
                list.Add(new("doctor-space", Loc.Tf("НА СИСТЕМНОМ ДИСКЕ СВОБОДНО {0}", DashFormat.Size(free)),
                    Loc.T("Windows нужно место для обновлений, файла подкачки и временных файлов; когда его мало, система работает медленнее."),
                    CheckSeverity.Warning, 0, new(CheckActionKind.Navigate, Loc.T("К ОЧИСТКЕ"), "cleaner")));
        }

        // 5. Restart: pending updates or a long uptime.
        if (s.PendingReboot.Count > 0)
            list.Add(new("doctor-restart", Loc.T("WINDOWS ЖДЁТ ПЕРЕЗАГРУЗКИ"),
                Loc.T("Обновления установлены не до конца; до перезагрузки часть служб работает в переходном состоянии."),
                CheckSeverity.Warning, 0, new(CheckActionKind.Restart, Loc.T("ПЕРЕЗАГРУЗИТЬ"))));
        else if (s.Uptime.TotalDays >= UptimeDays)
            list.Add(new("doctor-restart", Loc.Tf("БЕЗ ПЕРЕЗАГРУЗКИ {0}", Loc.N((int)s.Uptime.TotalDays, "{0} ДЕНЬ", "{0} ДНЯ", "{0} ДНЕЙ")),
                Loc.T("Перезагрузка закрывает зависшие фоновые процессы и освобождает память — Microsoft советует её, если ПК стал медленным."),
                CheckSeverity.Info, 0, new(CheckActionKind.Restart, Loc.T("ПЕРЕЗАГРУЗИТЬ"))));

        // 6. Power saving while on mains.
        // Energy saver is a Windows mode no tweak controls: the button opens Settings (as the Panel check does).
        if (s.OnAcPower == true && s.EnergySaverOn == true)
            list.Add(new("doctor-power", Loc.T("ВКЛЮЧЕНО ЭНЕРГОСБЕРЕЖЕНИЕ"),
                Loc.T("Режим энергосбережения Windows включён, хотя компьютер работает от сети: он ограничивает производительность. " +
                      "Отключается в Параметрах → Система → Питание."),
                CheckSeverity.Warning, 0, new(CheckActionKind.OpenUri, Loc.T("ОТКРЫТЬ ПАРАМЕТРЫ ПИТАНИЯ"), HealthRules.PowerSettingsUri)));
        // The «Power saver» plan: the same button as the Panel check (it knows which plan fits, Ryzen X3D included).
        else if (s.OnAcPower != false && s.PowerPlan == PowerPlanKind.PowerSaver)
            list.Add(new("doctor-power", Loc.T("ВКЛЮЧЕНА СХЕМА «ЭКОНОМИЯ ЭНЕРГИИ»"),
                Loc.T("Схема питания ограничивает частоту процессора ради экономии энергии; от сети она замедляет программы и игры."),
                CheckSeverity.Warning, 0, HealthRules.CheckPowerPlan(s)?.Action));

        // 7. Many startup apps (slow sign-in and background load).
        if (s.StartupCount is { } n && n > HealthRules.StartupLimit)
            list.Add(new("doctor-startup", Loc.Tf("В АВТОЗАПУСКЕ {0}", Loc.N(n, "{0} ПРОГРАММА", "{0} ПРОГРАММЫ", "{0} ПРОГРАММ")),
                Loc.T("Каждая запускается при входе и работает в фоне. Отключите те, что не нужны сразу, — они не удаляются и включаются обратно."),
                CheckSeverity.Warning, 0, new(CheckActionKind.Navigate, Loc.T("ОТКЛЮЧИТЬ ЛИШНЕЕ"), "startup")));

        // 8. A hot graphics card lowers its clocks.
        if (m.GpuTemperature is { } t && t >= GpuHot)
            list.Add(new("doctor-gpu-hot", Loc.Tf("ВИДЕОКАРТА НАГРЕТА ДО {0} °C", Math.Round(t)),
                Loc.T("При перегреве видеокарта снижает частоты. Проверьте, не забиты ли пылью радиаторы и свободен ли приток воздуха к корпусу."),
                CheckSeverity.Warning, 0));

        if (list.Count == 0)
            list.Add(new("doctor-none", Loc.T("ЯВНЫХ ПРИЧИН СЕЙЧАС НЕТ"),
                Loc.T("Процессор, память и диск не перегружены, места достаточно. Если тормозит время от времени, нажмите «ПОЧЕМУ ТОРМОЗИТ?» " +
                      "в тот момент; если тормозит только в игре — откройте страницу «Игры»."),
                CheckSeverity.Ok, 0));

        return list.OrderByDescending(r => r.Severity).ToList();
    }

    private static string Names(IEnumerable<ProcessUse> items, Func<ProcessUse, string> text) =>
        string.Join(", ", items.Take(3).Select(text));

    /// <summary>
    /// Groups per-process measurements by program name and turns CPU time into a share of the whole processor
    /// (100 % = all logical cores busy for the whole window), heaviest first. Idle and Lycoris itself are excluded by process id before this (see ReadProcessTimes).
    /// </summary>
    public static IReadOnlyList<ProcessUse> Group(IEnumerable<(string Name, TimeSpan Cpu, long MemoryBytes)> items, TimeSpan window, int cores)
    {
        var denominator = window.TotalMilliseconds * Math.Max(1, cores);
        return items
            // No filtering by name here: a program chooses its own name (a miner called «LycorisHelper.exe» must still show up).
            // The System Idle Process and Lycoris itself are left out by process id when the list is read.
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProcessUse(g.Key,
                denominator > 0 ? Math.Clamp(g.Sum(d => d.Cpu.TotalMilliseconds) / denominator * 100, 0, 100) : 0,
                g.Sum(d => d.MemoryBytes)))
            .ToList();
    }
}
