using System.Globalization;
using System.Text;

namespace Monolith.Modules.Tools.DpcLatency;

/// <summary>Kind of a PerfInfo routine event (the ETW opcode: 67 ISR, 68 DPC, 66 threaded DPC, 69 timer DPC).</summary>
public enum RoutineKind { Isr, Dpc, ThreadedDpc, TimerDpc }

/// <summary>A loaded kernel module: base name, load address and image size (0 when the size is unknown).</summary>
public sealed record KernelModule(string Name, ulong Base, ulong Size);

/// <summary>
/// Maps a routine address to the kernel module that contains it. Modules are sorted by base address; a lookup is a
/// binary search for the greatest base ≤ address. When the module's size is known the address must lie inside the image,
/// otherwise (sizes unavailable) the nearest module below is accepted. Pure, unit-tested.
/// </summary>
public sealed class ModuleMap
{
    public const string Unknown = "неизвестный модуль";

    private readonly KernelModule[] _sorted;

    public ModuleMap(IEnumerable<KernelModule> modules)
    {
        _sorted = modules.Where(m => m.Base != 0 && !string.IsNullOrWhiteSpace(m.Name)).OrderBy(m => m.Base).ToArray();
    }

    public int Count => _sorted.Length;

    public string Resolve(ulong address)
    {
        if (_sorted.Length == 0 || address == 0) return Unknown;
        int lo = 0, hi = _sorted.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_sorted[mid].Base <= address) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return Unknown;
        var m = _sorted[found];
        if (m.Size != 0 && address >= m.Base + m.Size) return Unknown;
        return m.Name;
    }
}

/// <summary>Per-module statistics of one measurement.</summary>
public sealed class ModuleStats
{
    /// <summary>1 µs buckets from 0 to 10 ms; the last bucket collects everything ≥ 10 ms.</summary>
    public const int BucketCount = 10_000;

    private readonly int[] _histogram = new int[BucketCount + 1];

    public ModuleStats(string name) => Name = name;

    public string Name { get; }
    public long Count { get; private set; }
    public long IsrCount { get; private set; }
    public long DpcCount { get; private set; }
    public double TotalMicros { get; private set; }
    public double MaxMicros { get; private set; }
    public RoutineKind MaxKind { get; private set; }
    public double MaxDpcMicros { get; private set; }
    public double MaxIsrMicros { get; private set; }

    public void Add(RoutineKind kind, double micros)
    {
        if (micros < 0) micros = 0;
        Count++;
        if (kind == RoutineKind.Isr)
        {
            IsrCount++;
            if (micros > MaxIsrMicros) MaxIsrMicros = micros;
        }
        else
        {
            DpcCount++;
            if (micros > MaxDpcMicros) MaxDpcMicros = micros;
        }
        TotalMicros += micros;
        if (micros > MaxMicros)
        {
            MaxMicros = micros;
            MaxKind = kind;
        }
        var bucket = micros >= BucketCount ? BucketCount : (int)micros;
        _histogram[bucket]++;
    }

    /// <summary>The p-th percentile in whole microseconds (the lower bound of its histogram bucket); ≥ 10 000 means "10 ms or more".</summary>
    public int Percentile(double p)
    {
        if (Count == 0) return 0;
        var rank = (long)Math.Ceiling(Math.Clamp(p, 0, 1) * Count);
        if (rank < 1) rank = 1;
        long seen = 0;
        for (var i = 0; i < _histogram.Length; i++)
        {
            seen += _histogram[i];
            if (seen >= rank) return i;
        }
        return BucketCount;
    }

    public int P99 => Percentile(0.99);
    public double TotalMillis => TotalMicros / 1000d;
}

public enum DpcVerdict { Normal, Elevated, High }

/// <summary>
/// Collects DPC/ISR durations per routine address and attributes them to kernel modules. The hot path (one call per event,
/// up to tens of thousands per second) is a dictionary lookup per routine address; module resolution is cached per address.
/// Pure: nothing here touches ETW or the system.
/// </summary>
public sealed class DpcAggregator
{
    /// <summary>LatencyMon community practice (Resplendence): a single DPC/ISR above ~1 ms is where audio drops and input stutters start.</summary>
    public const double ElevatedMicros = 500;
    public const double HighMicros = 1000;

    private readonly ModuleMap _modules;
    private readonly Dictionary<ulong, ModuleStats> _byRoutine = new();
    private readonly Dictionary<string, ModuleStats> _byModule = new(StringComparer.OrdinalIgnoreCase);

    public DpcAggregator(ModuleMap modules) => _modules = modules;

    public long EventCount { get; private set; }
    public long UnknownCount { get; private set; }
    public double MaxMicros { get; private set; }
    public RoutineKind MaxKind { get; private set; }
    public string? MaxModule { get; private set; }
    public double MaxDpcMicros { get; private set; }
    public double MaxIsrMicros { get; private set; }

    public void Add(RoutineKind kind, ulong routine, double micros)
    {
        if (double.IsNaN(micros) || micros < 0) return;
        if (!_byRoutine.TryGetValue(routine, out var stats))
        {
            var name = _modules.Resolve(routine);
            if (!_byModule.TryGetValue(name, out stats))
            {
                stats = new ModuleStats(name);
                _byModule[name] = stats;
            }
            _byRoutine[routine] = stats;
        }
        stats.Add(kind, micros);
        EventCount++;
        if (stats.Name == ModuleMap.Unknown) UnknownCount++;
        if (kind == RoutineKind.Isr) { if (micros > MaxIsrMicros) MaxIsrMicros = micros; }
        else if (micros > MaxDpcMicros) MaxDpcMicros = micros;
        if (micros > MaxMicros)
        {
            MaxMicros = micros;
            MaxKind = kind;
            MaxModule = stats.Name;
        }
    }

    /// <summary>Modules sorted by their worst single routine, then by total time.</summary>
    public IReadOnlyList<ModuleStats> Modules =>
        _byModule.Values.OrderByDescending(m => m.MaxMicros).ThenByDescending(m => m.TotalMicros).ToList();

    public DpcVerdict Verdict => VerdictFor(MaxMicros);

    public static DpcVerdict VerdictFor(double maxMicros) =>
        maxMicros > HighMicros ? DpcVerdict.High : maxMicros >= ElevatedMicros ? DpcVerdict.Elevated : DpcVerdict.Normal;

    public static string VerdictText(DpcVerdict verdict) => verdict switch
    {
        DpcVerdict.Normal => "в норме",
        DpcVerdict.Elevated => "повышенные",
        _ => "высокие: возможны треск звука и рывки",
    };

    public static string KindText(RoutineKind kind) => kind switch
    {
        RoutineKind.Isr => "ISR",
        RoutineKind.ThreadedDpc => "DPC (поток)",
        RoutineKind.TimerDpc => "DPC (таймер)",
        _ => "DPC",
    };

    /// <summary>Known driver families: what the module is and where to look next. Keys are compared case-insensitively.</summary>
    private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ndis.sys"] = "сетевой стек: проверьте драйвер сетевой карты/Wi-Fi",
        ["tcpip.sys"] = "стек TCP/IP: проверьте драйвер сетевой карты/Wi-Fi и сетевые фильтры (VPN, антивирус)",
        ["nvlddmkm.sys"] = "драйвер NVIDIA",
        ["amdkmdag.sys"] = "драйвер AMD Radeon",
        ["igdkmd64.sys"] = "драйвер графики Intel",
        ["dxgkrnl.sys"] = "графическая подсистема: обычно отражает работу драйвера видеокарты",
        ["acpi.sys"] = "прошивка/BIOS: обновите BIOS и драйверы чипсета",
        ["storport.sys"] = "накопители: драйвер контроллера/SSD",
        ["stornvme.sys"] = "накопители: NVMe-контроллер",
        ["storahci.sys"] = "накопители: SATA/AHCI-контроллер",
        ["wdf01000.sys"] = "драйвер устройства на WDF (USB, звук, периферия) — виновник скрыт за платформой",
        ["usbxhci.sys"] = "контроллер USB 3: проверьте драйвер чипсета и USB-устройства",
        ["usbport.sys"] = "контроллер USB 2: проверьте драйвер чипсета и USB-устройства",
        ["hdaudbus.sys"] = "шина HD Audio: драйвер звука",
        ["portcls.sys"] = "звуковая подсистема: драйвер звуковой карты",
        ["ntoskrnl.exe"] = "ядро Windows: таймеры и системные DPC — обычно следствие другого драйвера или энергосбережения",
        ["hal.dll"] = "уровень аппаратных абстракций: прерывания платформы, прошивка",
        ["wdfilter.sys"] = "Microsoft Defender: фильтр файловой системы",
        ["rtwlane.sys"] = "Wi-Fi Realtek",
        ["netwtw08.sys"] = "Wi-Fi Intel",
        ["netwtw10.sys"] = "Wi-Fi Intel",
        ["rt640x64.sys"] = "сетевая карта Realtek",
        ["e1d68x64.sys"] = "сетевая карта Intel",
        ["bthport.sys"] = "Bluetooth",
        ["cldflt.sys"] = "OneDrive (файлы по запросу)",
    };

    public static string? Family(string moduleName) =>
        Families.TryGetValue(moduleName, out var text) ? text : null;

    /// <summary>Console lines of the result: a table of the top modules, a hint per flagged module and the next step.</summary>
    public IReadOnlyList<string> FormatReport(TimeSpan duration, int top = 10)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        var modules = Modules;
        lines.Add($"Событий DPC/ISR за {duration.TotalSeconds:0} с: {EventCount.ToString("N0", inv)}" +
                  (UnknownCount > 0 ? $", без модуля: {UnknownCount.ToString("N0", inv)}" : "") + ".");
        if (EventCount == 0)
        {
            lines.Add("События не получены — трассировка не дала данных.");
            return lines;
        }
        lines.Add($"Самый долгий вызов: {MaxMicros.ToString("0", inv)} мкс ({KindText(MaxKind)}, {MaxModule}); DPC до {MaxDpcMicros.ToString("0", inv)} мкс, ISR до {MaxIsrMicros.ToString("0", inv)} мкс.");
        lines.Add("");
        lines.Add($"{"МОДУЛЬ",-22} {"ВЫЗОВОВ",10} {"МАКС МКС",10} {"P99 МКС",9} {"ВСЕГО МС",10}");
        foreach (var m in modules.Take(top))
        {
            var p99 = m.P99 >= ModuleStats.BucketCount ? "≥10000" : m.P99.ToString(inv);
            lines.Add($"{Trim(m.Name, 22),-22} {m.Count.ToString("N0", inv),10} {m.MaxMicros.ToString("0", inv),10} {p99,9} {m.TotalMillis.ToString("0.0", inv),10}");
        }
        if (modules.Count > top) lines.Add($"… ещё модулей: {modules.Count - top}.");

        var flagged = modules.Where(m => m.MaxMicros >= ElevatedMicros).ToList();
        if (flagged.Count > 0)
        {
            lines.Add("");
            foreach (var m in flagged)
            {
                var family = Family(m.Name);
                var what = family is null
                    ? m.Name == ModuleMap.Unknown ? "адрес не удалось сопоставить с модулем" : "драйвер, см. его файл в Диспетчере устройств"
                    : family;
                lines.Add($"{m.Name} — {what}: до {m.MaxMicros.ToString("0", inv)} мкс ({KindText(m.MaxKind)}), p99 {m.P99} мкс.");
            }
            lines.Add("Следующий шаг: раздел «Драйверы» — обновите драйвер отмеченного устройства; исправление не гарантируется.");
        }
        return lines;
    }

    private static string Trim(string s, int width) => s.Length <= width ? s : s[..(width - 1)] + "…";

    /// <summary>One-line summary for the console footer and the toast.</summary>
    public string Summary(TimeSpan duration)
    {
        var inv = CultureInfo.InvariantCulture;
        if (EventCount == 0) return "Трассировка не дала событий DPC/ISR — измерение не удалось.";
        var verdict = VerdictText(Verdict);
        var sb = new StringBuilder();
        sb.Append("Задержки драйверов ").Append(verdict).Append(": самый долгий вызов ")
          .Append(MaxMicros.ToString("0", inv)).Append(" мкс");
        if (MaxModule is not null && MaxModule != ModuleMap.Unknown) sb.Append(" (").Append(MaxModule).Append(')');
        sb.Append(" за ").Append(duration.TotalSeconds.ToString("0", inv)).Append(" с.");
        if (Verdict != DpcVerdict.Normal) sb.Append(" Следующий шаг — раздел «Драйверы».");
        return sb.ToString();
    }
}
