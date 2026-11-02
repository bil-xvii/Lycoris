using System.Globalization;
using System.Text;

namespace Lycoris.Modules.Tools.DpcLatency;

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
    public static readonly string Unknown = Loc.Key("неизвестный модуль"); // a key: translated where shown (Loc.T)

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
        DpcVerdict.Normal => Loc.T("в норме"),
        DpcVerdict.Elevated => Loc.T("повышенные"),
        _ => Loc.T("высокие: возможны треск звука и рывки"),
    };

    public static string KindText(RoutineKind kind) => kind switch
    {
        RoutineKind.Isr => "ISR",
        RoutineKind.ThreadedDpc => Loc.T("DPC (поток)"),
        RoutineKind.TimerDpc => Loc.T("DPC (таймер)"),
        _ => "DPC",
    };

    /// <summary>Known driver families: what the module is and where to look next. Keys are compared case-insensitively.</summary>
    private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ndis.sys"] = Loc.Key("сетевой стек: проверьте драйвер сетевой карты/Wi-Fi"),
        ["tcpip.sys"] = Loc.Key("стек TCP/IP: проверьте драйвер сетевой карты/Wi-Fi и сетевые фильтры (VPN, антивирус)"),
        ["nvlddmkm.sys"] = Loc.Key("драйвер NVIDIA"),
        ["amdkmdag.sys"] = Loc.Key("драйвер AMD Radeon"),
        ["igdkmd64.sys"] = Loc.Key("драйвер графики Intel"),
        ["dxgkrnl.sys"] = Loc.Key("графическая подсистема: обычно отражает работу драйвера видеокарты"),
        ["acpi.sys"] = Loc.Key("прошивка/BIOS: обновите BIOS и драйверы чипсета"),
        ["storport.sys"] = Loc.Key("накопители: драйвер контроллера/SSD"),
        ["stornvme.sys"] = Loc.Key("накопители: NVMe-контроллер"),
        ["storahci.sys"] = Loc.Key("накопители: SATA/AHCI-контроллер"),
        ["wdf01000.sys"] = Loc.Key("драйвер устройства на WDF (USB, звук, периферия) — виновник скрыт за платформой"),
        ["usbxhci.sys"] = Loc.Key("контроллер USB 3: проверьте драйвер чипсета и USB-устройства"),
        ["usbport.sys"] = Loc.Key("контроллер USB 2: проверьте драйвер чипсета и USB-устройства"),
        ["hdaudbus.sys"] = Loc.Key("шина HD Audio: драйвер звука"),
        ["portcls.sys"] = Loc.Key("звуковая подсистема: драйвер звуковой карты"),
        ["ntoskrnl.exe"] = Loc.Key("ядро Windows: таймеры и системные DPC — обычно следствие другого драйвера или энергосбережения"),
        ["hal.dll"] = Loc.Key("уровень аппаратных абстракций: прерывания платформы, прошивка"),
        ["wdfilter.sys"] = Loc.Key("Microsoft Defender: фильтр файловой системы"),
        ["rtwlane.sys"] = "Wi-Fi Realtek",
        ["netwtw08.sys"] = "Wi-Fi Intel",
        ["netwtw10.sys"] = "Wi-Fi Intel",
        ["rt640x64.sys"] = Loc.Key("сетевая карта Realtek"),
        ["e1d68x64.sys"] = Loc.Key("сетевая карта Intel"),
        ["bthport.sys"] = "Bluetooth",
        ["cldflt.sys"] = Loc.Key("OneDrive (файлы по запросу)"),
    };

    public static string? Family(string moduleName) =>
        Families.TryGetValue(moduleName, out var text) ? Loc.T(text) : null;

    /// <summary>Console lines of the result: a table of the top modules, a hint per flagged module and the next step.</summary>
    public IReadOnlyList<string> FormatReport(TimeSpan duration, int top = 10)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>();
        var modules = Modules;
        lines.Add(UnknownCount > 0
            ? Loc.Tf("Событий DPC/ISR за {0} с: {1}, без модуля: {2}.", duration.TotalSeconds.ToString("0", inv), EventCount.ToString("N0", inv), UnknownCount.ToString("N0", inv))
            : Loc.Tf("Событий DPC/ISR за {0} с: {1}.", duration.TotalSeconds.ToString("0", inv), EventCount.ToString("N0", inv)));
        if (EventCount == 0)
        {
            lines.Add(Loc.T("События не получены — трассировка не дала данных."));
            return lines;
        }
        lines.Add(Loc.Tf("Самый долгий вызов: {0} мкс ({1}, {2}); DPC до {3} мкс, ISR до {4} мкс.",
            MaxMicros.ToString("0", inv), KindText(MaxKind), MaxModule is null ? "" : Loc.T(MaxModule), MaxDpcMicros.ToString("0", inv), MaxIsrMicros.ToString("0", inv)));
        lines.Add("");
        lines.Add(string.Format("{0,-22} {1,10} {2,10} {3,9} {4,10}", Loc.T("МОДУЛЬ"), Loc.T("ВЫЗОВОВ"), Loc.T("МАКС МКС"), Loc.T("P99 МКС"), Loc.T("ВСЕГО МС")));
        foreach (var m in modules.Take(top))
        {
            var p99 = m.P99 >= ModuleStats.BucketCount ? "≥10000" : m.P99.ToString(inv);
            lines.Add($"{Trim(Loc.T(m.Name), 22),-22} {m.Count.ToString("N0", inv),10} {m.MaxMicros.ToString("0", inv),10} {p99,9} {m.TotalMillis.ToString("0.0", inv),10}");
        }
        if (modules.Count > top) lines.Add(Loc.Tf("… ещё модулей: {0}.", modules.Count - top));

        var flagged = modules.Where(m => m.MaxMicros >= ElevatedMicros).ToList();
        if (flagged.Count > 0)
        {
            lines.Add("");
            foreach (var m in flagged)
            {
                var family = Family(m.Name);
                var what = family is null
                    ? m.Name == ModuleMap.Unknown ? Loc.T("адрес не удалось сопоставить с модулем") : Loc.T("драйвер, см. его файл в Диспетчере устройств")
                    : family;
                lines.Add(Loc.Tf("{0} — {1}: до {2} мкс ({3}), p99 {4} мкс.", Loc.T(m.Name), what, m.MaxMicros.ToString("0", inv), KindText(m.MaxKind), m.P99));
            }
            lines.Add(Loc.T("Следующий шаг: раздел «Драйверы» — обновите драйвер отмеченного устройства; исправление не гарантируется."));
        }
        return lines;
    }

    private static string Trim(string s, int width) => s.Length <= width ? s : s[..(width - 1)] + "…";

    /// <summary>One-line summary for the console footer and the toast.</summary>
    public string Summary(TimeSpan duration)
    {
        var inv = CultureInfo.InvariantCulture;
        if (EventCount == 0) return Loc.T("Трассировка не дала событий DPC/ISR — измерение не удалось.");
        var verdict = VerdictText(Verdict);
        var module = MaxModule is not null && MaxModule != ModuleMap.Unknown ? " (" + MaxModule + ")" : "";
        var text = Loc.Tf("Задержки драйверов {0}: самый долгий вызов {1} мкс{2} за {3} с.", verdict, MaxMicros.ToString("0", inv), module, duration.TotalSeconds.ToString("0", inv));
        return Verdict != DpcVerdict.Normal ? text + " " + Loc.T("Следующий шаг — раздел «Драйверы».") : text;
    }
}
