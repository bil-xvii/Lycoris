using System.Globalization;
using System.Text.RegularExpressions;

namespace Monolith.Modules.Dashboard.Checks;

/// <summary>Likely area behind a crash, for plain-language advice.</summary>
public enum CrashCause { Unknown, Driver, Gpu, Memory, Processor, Disk, Pcie }

/// <summary>WHEA-Logger component by event id: 17 PCIe, 18/19 processor (core, cache, bus), 46/47 memory.</summary>
public enum WheaComponent { Other, Processor, Memory, Pcie }

/// <summary>WHEA event severity by level: Warning = corrected, Error/Critical = fatal, anything else is not counted.</summary>
public enum WheaSeverity { Ignored, Corrected, Fatal }

/// <summary>One unexpected end of a Windows session (Kernel-Power 41 + WER 1001 + EventLog 6008 merged). BugCheck 0 = no code.</summary>
public sealed record CrashIncident(DateTime Time, uint BugCheck, bool PowerButton);

/// <summary>Counted incidents of the stability window. <see cref="CoveredFrom"/> is set when the log is shorter than the window.</summary>
public sealed record StabilitySummary(int Days, DateTime? CoveredFrom, IReadOnlyList<CrashIncident> Crashes, int GpuResets,
    int WheaFatal, IReadOnlyList<WheaComponent> FatalComponents, int WheaProcessor, int WheaMemory, int WheaPcie, int WheaOther, bool Truncated)
{
    public IReadOnlyList<CrashIncident> BlueScreens => Crashes.Where(c => c.BugCheck != 0).ToList();
    public IReadOnlyList<CrashIncident> UnexpectedRestarts => Crashes.Where(c => c.BugCheck == 0).ToList();
}

/// <summary>
/// Stability of the last 30 days from the System log: blue screens, restarts without a code, WHEA hardware errors and
/// graphics driver resets (TDR). Counts incidents, not records, and gives honest advice per cause; corrected-only
/// findings are information. Sources: Microsoft "Event ID 41", "Timeout detection and recovery", "Querying the system
/// event log for hardware error events", Bug Check Code Reference.
/// </summary>
public static partial class HealthRules
{
    /// <summary>Corrected PCIe errors are normal in small numbers; only a burst is worth mentioning.</summary>
    public const int PcieBurst = 20;
    public static readonly TimeSpan CrashMergeWindow = TimeSpan.FromMinutes(5);
    /// <summary>NVIDIA writes several 4101 records per reset.</summary>
    public static readonly TimeSpan GpuResetMergeWindow = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<uint, (string Name, CrashCause Cause)> BugChecks = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", CrashCause.Driver),
        [0x19] = ("BAD_POOL_HEADER", CrashCause.Memory),
        [0x1A] = ("MEMORY_MANAGEMENT", CrashCause.Memory),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", CrashCause.Driver),
        [0x24] = ("NTFS_FILE_SYSTEM", CrashCause.Disk),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", CrashCause.Driver),
        [0x4E] = ("PFN_LIST_CORRUPT", CrashCause.Memory),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", CrashCause.Memory),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", CrashCause.Disk),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", CrashCause.Driver),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", CrashCause.Unknown),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", CrashCause.Driver),
        [0xC2] = ("BAD_POOL_CALLER", CrashCause.Driver),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", CrashCause.Driver),
        [0xEF] = ("CRITICAL_PROCESS_DIED", CrashCause.Unknown),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", CrashCause.Processor),
        [0x10E] = ("VIDEO_MEMORY_MANAGEMENT_INTERNAL", CrashCause.Gpu),
        [0x116] = ("VIDEO_TDR_FAILURE", CrashCause.Gpu),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", CrashCause.Gpu),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", CrashCause.Gpu),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", CrashCause.Processor),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", CrashCause.Driver),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", CrashCause.Driver),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", CrashCause.Disk),
    };

    /// <summary>0x1000007E is the same stop code as 0x7E (the high flag only marks the parameter layout).</summary>
    public static uint NormalizeBugCheck(uint code) => code is >= 0x10000000 and < 0x20000000 ? code & 0x0FFFFFFF : code;

    [GeneratedRegex(@"0x([0-9a-fA-F]{1,8})")] private static partial Regex HexRx();

    /// <summary>WER 1001 "param1": "0x0000009f (0xffff…, …)" → 0x9F. 0 when there is no code.</summary>
    public static uint ParseWerBugCheck(string? param1)
    {
        if (string.IsNullOrWhiteSpace(param1)) return 0;
        var m = HexRx().Match(param1);
        return m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
            ? NormalizeBugCheck(code)
            : 0;
    }

    /// <summary>"0x124 WHEA_UNCORRECTABLE_ERROR" or "0x1C4" for codes without a well-known name.</summary>
    public static string BugCheckText(uint code) =>
        BugChecks.TryGetValue(code, out var known) ? $"0x{code:X} {known.Name}" : $"0x{code:X}";

    public static CrashCause BugCheckCause(uint code) => BugChecks.TryGetValue(code, out var known) ? known.Cause : CrashCause.Unknown;

    public static (WheaComponent Component, WheaSeverity Severity) ClassifyWhea(int eventId, int level) => (
        eventId switch { 17 => WheaComponent.Pcie, 18 or 19 => WheaComponent.Processor, 46 or 47 => WheaComponent.Memory, _ => WheaComponent.Other },
        level switch { 1 or 2 => WheaSeverity.Fatal, 3 => WheaSeverity.Corrected, _ => WheaSeverity.Ignored });

    /// <summary>
    /// One crash leaves up to three records at the next boot (Kernel-Power 41, WER 1001, EventLog 6008), written within
    /// minutes of each other: records close in time join one incident unless that incident already has a record of
    /// the same kind (then it is the next crash of a crash loop).
    /// </summary>
    public static IReadOnlyList<CrashIncident> MergeCrashes(IEnumerable<StabilityEvent> events, TimeSpan? window = null)
    {
        var w = window ?? CrashMergeWindow;
        var ordered = events.Where(e => e.Kind is StabilityEventKind.KernelPower41 or StabilityEventKind.BugCheckReport or StabilityEventKind.UnexpectedShutdown)
            .OrderBy(e => e.Time)
            .ToList();
        var result = new List<CrashIncident>();
        List<StabilityEvent>? cluster = null;
        foreach (var e in ordered)
        {
            if (cluster is not null && e.Time - cluster[^1].Time <= w && cluster.All(c => c.Kind != e.Kind))
            {
                cluster.Add(e);
                continue;
            }
            if (cluster is not null) result.Add(Close(cluster));
            cluster = [e];
        }
        if (cluster is not null) result.Add(Close(cluster));
        return result;

        static CrashIncident Close(List<StabilityEvent> c) =>
            new(c[0].Time, c.Select(x => NormalizeBugCheck(x.BugCheck)).FirstOrDefault(b => b != 0), c.Any(x => x.PowerButton));
    }

    /// <summary>Graphics driver resets: 4101 records less than a minute apart are one reset.</summary>
    public static int CountGpuResets(IEnumerable<StabilityEvent> events, TimeSpan? window = null)
    {
        var w = window ?? GpuResetMergeWindow;
        var n = 0;
        DateTime? last = null;
        foreach (var t in events.Where(e => e.Kind == StabilityEventKind.GpuReset).Select(e => e.Time).OrderBy(t => t))
        {
            if (last is null || t - last.Value > w) n++;
            last = t;
        }
        return n;
    }

    public static StabilitySummary AnalyzeStability(StabilityLog log, DateTime now)
    {
        var covered = log.OldestRecord is { } oldest && oldest > log.Since ? oldest : (DateTime?)null;
        var maxDays = Math.Max(1, (int)Math.Round((now - log.Since).TotalDays));
        var days = Math.Clamp((int)Math.Ceiling((now - (covered ?? log.Since)).TotalDays), 1, maxDays);

        int fatal = 0, cpu = 0, memory = 0, pcie = 0, other = 0;
        var fatalComponents = new List<WheaComponent>();
        foreach (var e in log.Events.Where(e => e.Kind == StabilityEventKind.Whea))
        {
            var (component, severity) = ClassifyWhea(e.EventId, e.Level);
            if (severity == WheaSeverity.Fatal)
            {
                fatal++;
                if (!fatalComponents.Contains(component)) fatalComponents.Add(component);
            }
            else if (severity == WheaSeverity.Corrected)
            {
                switch (component)
                {
                    case WheaComponent.Processor: cpu++; break;
                    case WheaComponent.Memory: memory++; break;
                    case WheaComponent.Pcie: pcie++; break;
                    default: other++; break;
                }
            }
        }
        return new StabilitySummary(days, covered, MergeCrashes(log.Events), CountGpuResets(log.Events), fatal, fatalComponents,
            cpu, memory, pcie, other, log.Truncated);
    }

    private static string CauseAdvice(CrashCause cause) => cause switch
    {
        CrashCause.Memory => Loc.T("Признаки ошибок памяти: проверьте её средством «Диагностика памяти Windows» (раздел «Обслуживание» → " +
                             "«Ремонт и диски») и попробуйте поработать без профиля XMP/EXPO"),
        CrashCause.Processor => Loc.T("Аппаратные ошибки процессора (ядро, кэш, шина) обычно вызывают разгон или андервольт (PBO, Curve " +
                                "Optimizer, частота FCLK) либо нестабильный профиль памяти: верните в BIOS значения по умолчанию, " +
                                "проверьте работу памяти на штатной частоте (JEDEC) и обновите BIOS. Диагностика памяти такие ошибки " +
                                "не находит"),
        CrashCause.Gpu => Loc.T("Сбои видеокарты чаще всего вызывает её драйвер — обновите или переустановите его в разделе «Драйверы»; " +
                          "реже — разгон видеокарты или нехватка питания"),
        CrashCause.Driver => Loc.T("Такие коды обычно вызывает драйвер устройства: обновите драйверы чипсета, видеокарты, сети и звука " +
                             "в разделе «Драйверы»"),
        CrashCause.Disk => Loc.T("Код указывает на ошибку чтения с накопителя: проверьте его состояние и кабель или разъём"),
        CrashCause.Pcie => Loc.T("Ошибки шины PCIe: проверьте, плотно ли сидят видеокарта и накопители M.2, райзер-кабель и драйвер " +
                           "чипсета; при большом числе ошибок обновите BIOS"),
        _ => "",
    };

    private static CrashCause CauseOf(WheaComponent c) => c switch
    {
        WheaComponent.Processor => CrashCause.Processor,
        WheaComponent.Memory => CrashCause.Memory,
        WheaComponent.Pcie => CrashCause.Pcie,
        _ => CrashCause.Unknown,
    };

    public static HealthCheckResult? CheckStability(SystemSnapshot s)
    {
        if (s.Stability is not { } log) return null;
        var a = AnalyzeStability(log, s.Now);
        var period = Loc.Tf("За {0}", Loc.N(a.Days, "{0} день", "{0} дня", "{0} дней")) +
                     (a.CoveredFrom is { } from ? Loc.Tf(" (журнал Windows хранит события с {0})", DashFormat.Date(from)) : "");

        var findings = new List<string>();
        var causes = new List<CrashCause>();
        void Cause(CrashCause c)
        {
            if (c != CrashCause.Unknown && !causes.Contains(c)) causes.Add(c);
        }

        var blue = a.BlueScreens;
        if (blue.Count > 0)
        {
            var codes = blue.GroupBy(c => c.BugCheck).OrderByDescending(g => g.Count()).Take(3).Select(g => BugCheckText(g.Key));
            findings.Add(Loc.Tf("{0} ({1})", Loc.N(blue.Count, "{0} синий экран", "{0} синих экрана", "{0} синих экранов"), string.Join(", ", codes)));
            foreach (var c in blue) Cause(BugCheckCause(c.BugCheck));
        }
        var unexpected = a.UnexpectedRestarts;
        if (unexpected.Count > 0)
        {
            var button = unexpected.Count(c => c.PowerButton);
            findings.Add(Loc.Tf("{0} без кода ошибки{1}",
                Loc.N(unexpected.Count, "{0} внезапная перезагрузка", "{0} внезапные перезагрузки", "{0} внезапных перезагрузок"),
                button > 0 ? Loc.Tf(" (из них выключений кнопкой питания: {0})", button) : ""));
        }
        if (a.WheaFatal > 0)
        {
            findings.Add(Loc.Tf("{0} WHEA", Loc.N(a.WheaFatal, "{0} неисправимая аппаратная ошибка", "{0} неисправимые аппаратные ошибки", "{0} неисправимых аппаратных ошибок")));
            foreach (var c in a.FatalComponents) Cause(CauseOf(c));
        }
        if (a.WheaProcessor > 0)
        {
            findings.Add(Loc.Tf("{0} процессора (WHEA)", Loc.N(a.WheaProcessor, "{0} исправленная ошибка", "{0} исправленные ошибки", "{0} исправленных ошибок")));
            Cause(CrashCause.Processor);
        }
        if (a.WheaMemory > 0)
        {
            findings.Add(Loc.Tf("{0} памяти (WHEA)", Loc.N(a.WheaMemory, "{0} исправленная ошибка", "{0} исправленные ошибки", "{0} исправленных ошибок")));
            Cause(CrashCause.Memory);
        }
        if (a.WheaPcie >= PcieBurst)
        {
            findings.Add(Loc.Tf("{0} шины PCIe (WHEA)", Loc.N(a.WheaPcie, "{0} исправленная ошибка", "{0} исправленные ошибки", "{0} исправленных ошибок")));
            Cause(CrashCause.Pcie);
        }
        if (a.WheaOther > 0)
            findings.Add(Loc.Tf("{0} WHEA", Loc.N(a.WheaOther, "{0} исправленная аппаратная ошибка", "{0} исправленные аппаратные ошибки", "{0} исправленных аппаратных ошибок")));
        if (a.GpuResets > 0)
        {
            findings.Add(Loc.Tf("{0} драйвера видеокарты", Loc.N(a.GpuResets, "{0} сброс", "{0} сброса", "{0} сбросов")));
            Cause(CrashCause.Gpu);
        }

        if (findings.Count == 0)
            return new("stability", Loc.T("СИСТЕМА РАБОТАЕТ СТАБИЛЬНО"),
                a.WheaPcie > 0
                    ? Loc.Tf("{0} не было синих экранов, внезапных перезагрузок, серьёзных аппаратных ошибок и сбросов драйвера " +
                      "видеокарты. Единичные исправленные ошибки шины PCIe ({1}) — это нормально.", period, a.WheaPcie)
                    : Loc.Tf("{0} не было синих экранов, внезапных перезагрузок, аппаратных ошибок WHEA и сбросов драйвера видеокарты.", period),
                CheckSeverity.Ok, 10);

        var warning = blue.Count > 0 || a.WheaFatal > 0 || unexpected.Count >= 2 || a.GpuResets >= 2;
        var advice = causes.Take(3).Select(CauseAdvice).Where(t => t.Length > 0).ToList();
        if (unexpected.Count > 0)
            advice.Add(Loc.T("Перезагрузка без кода ошибки — это пропадание питания или слабый блок питания, принудительное выключение " +
                       "после зависания либо сбой, при котором Windows не успела записать код (например, когда отключена запись " +
                       "дампа памяти)"));
        var detail = Loc.Tf("{0}: {1}.", period, string.Join("; ", findings)) + (advice.Count > 0 ? " " + string.Join(". ", advice) + "." : "");
        if (a.Truncated) detail += " " + Loc.T("Записей очень много — подсчитаны только самые свежие.");
        if (causes.Contains(CrashCause.Processor) || causes.Contains(CrashCause.Memory)) detail += " " + Loc.T("MONOLITH настройки BIOS не меняет.");

        var action = causes.Contains(CrashCause.Memory)
            ? new CheckAction(CheckActionKind.Navigate, Loc.T("ПРОВЕРИТЬ ПАМЯТЬ"), "tools")
            : causes.Contains(CrashCause.Gpu) || causes.Contains(CrashCause.Driver)
                ? new CheckAction(CheckActionKind.Navigate, Loc.T("ПРОВЕРИТЬ ДРАЙВЕРЫ"), "drivers", "tab:updates")
                : null;
        var days = Loc.N(a.Days, "{0} ДЕНЬ", "{0} ДНЯ", "{0} ДНЕЙ");
        return warning
            ? new("stability", Loc.Tf("СБОИ ЗА {0}", days), detail, CheckSeverity.Warning, blue.Count > 0 || a.WheaFatal > 0 ? 15 : 10, action)
            : new("stability", Loc.Tf("ЗАМЕЧАНИЯ ПО СТАБИЛЬНОСТИ ЗА {0}", days), detail, CheckSeverity.Info, 5, action);
    }
}
