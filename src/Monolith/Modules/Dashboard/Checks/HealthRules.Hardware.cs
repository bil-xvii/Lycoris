using System.Text.RegularExpressions;
using Monolith.Core;
using Monolith.Modules.Dashboard.Monitoring;

namespace Monolith.Modules.Dashboard.Checks;

/// <summary>Resizable BAR verdict from the BAR1 window vs. VRAM (NVIDIA, NVML).</summary>
public enum RebarState { Unknown, On, NotActive }

/// <summary>One drive's problems (empty = none). <see cref="Severe"/> = Windows marks it unhealthy or its write budget is used up.</summary>
public sealed record DiskVerdict(DiskHealth Disk, bool Severe, bool Hot, IReadOnlyList<string> Problems);

/// <summary>
/// Hardware facts that cost FPS or data when wrong: which adapter drives the monitor, drive health, Resizable BAR and
/// the PCIe link of the graphics card. All read-only: the fixes are physical (cable, slot, backup) or in BIOS, and
/// Lycoris changes none of them. Sources: gaming_tweaks.md §I (gpu-panel-reco, rebar-detect), tech.md §7.
/// </summary>
public static partial class HealthRules
{
    // ───────────────────────────── Monitor on the integrated GPU ─────────────────────────────

    /// <summary>AMD APU graphics: "AMD Radeon(TM) Graphics", "AMD Radeon(TM) 780M Graphics", "Radeon RX Vega 11 Graphics".</summary>
    public static bool IsAmdApuName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Trim();
        return n.EndsWith("Graphics", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Graphics built into the CPU (Intel iGPU, AMD APU). The Microsoft Basic Display Adapter is neither.</summary>
    public static bool IsOnboardGpu(GpuInfo g) => g.Vendor switch
    {
        GpuVendor.Intel => g.IsIntegrated,
        GpuVendor.Amd => IsAmdApuName(g.Name),
        _ => false,
    };

    /// <summary>A real graphics card: NVIDIA, AMD Radeon (not an APU) or Intel Arc with its own memory.</summary>
    public static bool IsDiscreteCard(GpuInfo g) => g.Vendor switch
    {
        GpuVendor.Nvidia => true,
        GpuVendor.Amd => !IsAmdApuName(g.Name),
        GpuVendor.Intel => !g.IsIntegrated,
        _ => false,
    };

    /// <summary>The GPU behind an adapter LUID, matched by name. Null for virtual, remote or unknown adapters.</summary>
    private static GpuInfo? GpuForAdapter(SystemSnapshot s, string adapterKey)
    {
        if (adapterKey.Length == 0) return null;
        var adapter = s.Adapters.FirstOrDefault(a => a.Key.Equals(adapterKey, StringComparison.OrdinalIgnoreCase));
        return adapter is null ? null : s.Gpus.FirstOrDefault(g => MonitorMath.SameGpuName(g.Name, adapter.Name));
    }

    /// <summary>
    /// Desktop with a graphics card whose gaming monitor (the primary or the fastest one) is plugged into the
    /// motherboard: frames are copied across adapters or rendered by the iGPU. Laptops (MUX/Optimus routing is normal),
    /// built-in panels, second monitors on the iGPU, cards without outputs and unknown adapters never warn.
    /// </summary>
    public static HealthCheckResult? CheckMonitorOnGpu(SystemSnapshot s)
    {
        if (s.IsLaptop || s.Displays.Count == 0 || s.Adapters.Count == 0) return null;
        if (!s.Gpus.Any(IsDiscreteCard)) return null; // iGPU-only PC: nothing to say

        var external = s.Displays.Where(d => !d.IsInternal && d.AdapterKey.Length > 0).ToList();
        if (external.Count == 0) return null;
        var watched = new List<DisplayInfo>();
        if (external.FirstOrDefault(d => d.IsPrimary) is { } primary) watched.Add(primary);
        var fastest = external.OrderByDescending(d => d.CurrentHz).ThenByDescending(d => d.IsPrimary).First();
        if (!watched.Contains(fastest)) watched.Add(fastest);

        var resolved = watched.Select(d => (Display: d, Gpu: GpuForAdapter(s, d.AdapterKey))).ToList();
        if (resolved.Any(r => r.Gpu is null)) return null;

        var onboard = resolved.Where(r => IsOnboardGpu(r.Gpu!)).ToList();
        if (onboard.Count == 0)
        {
            if (!resolved.All(r => IsDiscreteCard(r.Gpu!))) return null;
            var list = string.Join("; ", resolved.Select(r => $"{r.Display.Name} — {r.Gpu!.Name}"));
            return new("monitor-gpu", Loc.T("МОНИТОР ПОДКЛЮЧЁН К ВИДЕОКАРТЕ"),
                Loc.Tf("{0}. Кадры выводятся напрямую, без копирования через встроенную графику.", list), CheckSeverity.Ok, 12);
        }

        // The cable can only go into a card that has display outputs (compute cards and hybrid mobile chips have none).
        var card = s.Adapters.Where(a => a.DisplayTargets > 0)
            .Select(a => s.Gpus.FirstOrDefault(g => MonitorMath.SameGpuName(g.Name, a.Name)))
            .Where(g => g is not null && IsDiscreteCard(g))
            .OrderByDescending(g => g!.DedicatedMemoryBytes)
            .FirstOrDefault();
        if (card is null) return null;

        var (display, igpu) = onboard[0];
        return new("monitor-gpu", Loc.T("МОНИТОР ПОДКЛЮЧЁН КО ВСТРОЕННОЙ ГРАФИКЕ"),
            Loc.Tf("{0} подключён ко встроенной графике ({1}), а видеокарта {2} простаивает или копирует " +
            "кадры через неё — меньше FPS и больше задержка. Подключите кабель монитора к разъёму на видеокарте: это " +
            "горизонтальный ряд портов в нижней части задней панели, под разъёмами материнской платы. Встроенную графику " +
            "выключать не нужно, а второй монитор на ней — нормальная схема. Lycoris здесь ничего не меняет.",
            display.Name, igpu!.Name, card.Name),
            CheckSeverity.Warning, 12);
    }

    // ───────────────────────────── Drive health ─────────────────────────────

    public const int WearWarnPercent = 80;
    /// <summary>A drive within this many °C of its own reported limit counts as overheating.</summary>
    public const int HotMarginC = 5;

    public static DiskHealthStatus DiskStatusFromWmi(ushort? healthStatus) => healthStatus switch
    {
        0 => DiskHealthStatus.Healthy,
        1 => DiskHealthStatus.Warning,
        2 => DiskHealthStatus.Unhealthy,
        _ => DiskHealthStatus.Unknown, // 5 = Unknown
    };

    public static bool IsDiskHot(DiskHealth d) =>
        d is { TemperatureC: { } t, TemperatureMaxC: { } max } && t > 0 && max > HotMarginC && t >= max - HotMarginC;

    private static ulong UncorrectedErrors(DiskHealth d)
    {
        ulong r = d.ReadErrorsUncorrected ?? 0, w = d.WriteErrorsUncorrected ?? 0;
        return r > ulong.MaxValue - w ? ulong.MaxValue : r + w;
    }

    public static DiskVerdict JudgeDisk(DiskHealth d)
    {
        var problems = new List<string>();
        var severe = false;
        if (d.Status == DiskHealthStatus.Unhealthy)
        {
            problems.Add(Loc.T("Windows отмечает его как неисправный"));
            severe = true;
        }
        else if (d.Status == DiskHealthStatus.Warning)
        {
            problems.Add(Loc.T("Windows предупреждает о проблеме с ним"));
        }
        if (d.WearPercent is { } wear && wear >= 100)
        {
            problems.Add(Loc.Tf("ресурс записи исчерпан (износ {0} %)", wear));
            severe = true;
        }
        else if (d.WearPercent is { } w && w >= WearWarnPercent)
        {
            problems.Add(Loc.Tf("износ {0} %", w));
        }
        var errors = UncorrectedErrors(d);
        if (errors > 0)
            problems.Add(Loc.Tf("{0} чтения или записи",
                Loc.N((long)Math.Min(errors, long.MaxValue), "{0} неисправленная ошибка", "{0} неисправленные ошибки", "{0} неисправленных ошибок")));
        var hot = IsDiskHot(d);
        if (hot) problems.Add(Loc.Tf("температура {0} °C при пределе {1} °C", d.TemperatureC, d.TemperatureMaxC));
        return new DiskVerdict(d, severe, hot, problems);
    }

    /// <summary>"Samsung SSD 990 PRO 2TB: износ 3 %, 41 °C, ошибок нет" — only the values the drive reports.</summary>
    public static string DiskSummary(DiskHealth d)
    {
        var parts = new List<string>();
        if (d.WearPercent is { } w) parts.Add(Loc.Tf("износ {0} %", w));
        if (d.TemperatureC is { } t) parts.Add($"{t} °C");
        if (d.ReadErrorsUncorrected is not null && d.WriteErrorsUncorrected is not null)
            parts.Add(UncorrectedErrors(d) == 0 ? Loc.T("ошибок нет") : Loc.Tf("неисправленных ошибок: {0}", UncorrectedErrors(d)));
        if (parts.Count == 0)
            parts.Add(d.Status switch
            {
                DiskHealthStatus.Healthy => Loc.T("исправен по данным Windows"),
                DiskHealthStatus.Warning => Loc.T("Windows предупреждает о проблеме"),
                DiskHealthStatus.Unhealthy => Loc.T("Windows отмечает его как неисправный"),
                _ => Loc.T("состояние не сообщает"),
            });
        return $"{d.Name}: {string.Join(", ", parts)}";
    }

    public static HealthCheckResult? CheckDiskHealth(SystemSnapshot s)
    {
        if (s.Disks is not { Count: > 0 } disks) return null;
        if (!disks.Any(d => d.Status != DiskHealthStatus.Unknown || d.HasCounters)) return null; // nothing reported: no verdict

        var bad = disks.Select(JudgeDisk).Where(v => v.Problems.Count > 0).ToList();
        if (bad.Count > 0)
        {
            var detail = string.Join("; ", bad.Select(v => $"{v.Disk.Name}: {string.Join(", ", v.Problems)}")) + ".";
            if (bad.Any(v => !v.Hot || v.Problems.Count > 1))
                detail += " " + Loc.T("Сделайте резервную копию важных файлов на другой диск или в облако: заранее сказать, когда накопитель " +
                          "откажет, нельзя.");
            if (bad.Any(v => v.Hot))
                detail += " " + Loc.T("Перегрев сокращает срок службы и замедляет запись: проверьте обдув корпуса и радиатор M.2.");
            detail += " " + Loc.T("Lycoris накопители не «ремонтирует» и ничего на них не меняет.");
            return new("disk-health", bad.Count == 1 ? Loc.T("НАКОПИТЕЛЬ ТРЕБУЕТ ВНИМАНИЯ") : Loc.T("НАКОПИТЕЛИ ТРЕБУЮТ ВНИМАНИЯ"),
                detail, CheckSeverity.Warning, bad.Any(v => v.Severe) ? 20 : 12);
        }

        var text = string.Join("; ", disks.Select(DiskSummary)) + ".";
        if (!s.Elevated)
            text += " " + Loc.T("Износ, температура и счётчики ошибок доступны при запуске Lycoris с правами администратора.");
        else if (disks.Any(d => !d.HasCounters))
            text += " " + Loc.T("Часть накопителей не сообщает счётчики износа (так бывает с USB-адаптерами и RAID) — это не ошибка.");
        return new("disk-health", Loc.T("НАКОПИТЕЛИ: ПРОБЛЕМ НЕ НАЙДЕНО"), text, CheckSeverity.Ok, 10);
    }

    // ───────────────────────────── Resizable BAR (NVIDIA) ─────────────────────────────

    /// <summary>BAR1 ≥ VRAM → on; BAR1 ≤ 256 MiB with more than 512 MiB of VRAM → not active; anything else → unknown.</summary>
    public static RebarState ClassifyRebar(ulong? bar1Total, ulong? vramTotal)
    {
        if (bar1Total is not { } bar || vramTotal is not { } vram || bar == 0 || vram == 0) return RebarState.Unknown;
        if (bar >= vram) return RebarState.On;
        if (bar <= 256UL << 20 && vram > 512UL << 20) return RebarState.NotActive;
        return RebarState.Unknown;
    }

    /// <summary>
    /// NVIDIA cards that never get Resizable BAR (NVIDIA supports it from RTX 30 on): GeForce GT/GTS/GTX, MX, RTX 20,
    /// TITAN, Quadro, Tesla and the Turing T-series. A deny-list, so new or unknown names still get the BIOS hint.
    /// </summary>
    public static bool NvidiaCardLacksRebar(string? gpuName) => gpuName is not null && NoRebarNvidiaRx().IsMatch(gpuName);

    [GeneratedRegex(@"\bGT[SX]?\s*\d|\b(TITAN|Quadro|Tesla)\b|GeForce\s+MX|\bRTX\s*20\d\d\b|\bT(400|500|550|600|1000|1200|2000)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoRebarNvidiaRx();

    public static HealthCheckResult? CheckRebar(SystemSnapshot s)
    {
        if (s.GpuLink is not { } link) return null;
        switch (ClassifyRebar(link.Bar1Total, link.VramTotal))
        {
            case RebarState.On:
                return new("rebar", Loc.T("RESIZABLE BAR ВКЛЮЧЁН"),
                    Loc.Tf("{0}: процессору доступна вся видеопамять (окно BAR1 — {1}). " +
                    "Драйвер NVIDIA использует это в тех играх, где оно даёт прирост.", link.GpuName, DashFormat.WholeGb(link.Bar1Total)), CheckSeverity.Ok, 4);
            case RebarState.NotActive when NvidiaCardLacksRebar(link.GpuName):
                return null; // the card itself has no Resizable BAR: no BIOS/CSM steps that cannot help
            case RebarState.NotActive:
                var bios = s.Firmware switch
                {
                    FirmwareKind.Uefi => " " + Loc.T("Включите в BIOS/UEFI «Above 4G Decoding» и «Re-Size BAR Support»; если там включён CSM, " +
                                         "выключите его — Windows на этом ПК уже загружается в режиме UEFI."),
                    FirmwareKind.LegacyBios => " " + Loc.T("Resizable BAR работает только при загрузке в режиме UEFI, а Windows сейчас загружается " +
                                               "в режиме Legacy (CSM). Не выключайте CSM, пока системный диск размечен как MBR, — " +
                                               "Windows перестанет загружаться."),
                    _ => " " + Loc.T("Включите в BIOS/UEFI «Above 4G Decoding» и «Re-Size BAR Support»."),
                };
                return new("rebar", Loc.T("RESIZABLE BAR НЕ АКТИВЕН"),
                    Loc.Tf("{0}: процессору доступно только окно 256 МБ видеопамяти. Resizable BAR ускоряет часть игр — " +
                    "драйвер NVIDIA включает его только для игр из своего списка. Возможные причины: функция выключена в BIOS, " +
                    "видеокарта её не поддерживает (у NVIDIA — только RTX 30 и новее), у карты старая прошивка без поддержки " +
                    "(ранним RTX 30 нужно обновление VBIOS с сайта производителя карты), " +
                    "платформа её не поддерживает", link.GpuName) +
                    (s.IsLaptop ? Loc.T(" (на ноутбуке это решает производитель).") : ".") + bios +
                    " " + Loc.T("При включённом BitLocker сначала сохраните ключ восстановления. Lycoris настройки BIOS не меняет."),
                    CheckSeverity.Info, 3);
            default:
                return null;
        }
    }

    // ───────────────────────────── PCIe link (NVIDIA) ─────────────────────────────

    /// <summary>
    /// The link trained narrower than the card and slot allow. Only judged under load: at idle the driver slows the
    /// link down to save power. NVML's max width already includes the slot, so a wrong slot is not "degraded".
    /// </summary>
    public static bool IsLinkDegraded(GpuLinkFacts l) =>
        l.UnderLoad && l.CurrentWidth is { } current && l.MaxWidth is { } max && current < max;

    /// <summary>Under load the link runs at a lower PCIe generation than the card and slot allow (riser, contact, BIOS).</summary>
    public static bool IsLinkGenLowered(GpuLinkFacts l) =>
        l.UnderLoad && l.CurrentGen is { } current && l.MaxGen is { } max && current < max;

    public static HealthCheckResult? CheckPcieLink(SystemSnapshot s)
    {
        if (s.GpuLink is not { MaxGen: { } maxGen, MaxWidth: { } maxWidth } link) return null;
        var genLowered = IsLinkGenLowered(link);
        if (IsLinkDegraded(link))
            return new("pcie-link", Loc.T("ЛИНИЯ PCIE ВИДЕОКАРТЫ СУЖЕНА"),
                Loc.Tf("{0} под нагрузкой работает на PCIe {1}×{2}, " +
                "хотя карта и слот поддерживают {3}×{4}, — " +
                "линия работает ниже возможностей карты. Частые причины: карта не до конца вставлена в слот или перекошена, " +
                "загрязнены контакты, некачественный райзер-кабель. Выключите ПК, отключите питание и переустановите карту до " +
                "щелчка фиксатора. Lycoris ничего не меняет.",
                link.GpuName, genLowered ? $"{link.CurrentGen}.0 " : "", link.CurrentWidth, genLowered ? $"{maxGen}.0 " : "", maxWidth),
                CheckSeverity.Warning, 6);
        if (genLowered)
            return new("pcie-link", Loc.Tf("ЛИНИЯ PCIE ВИДЕОКАРТЫ: {0}.0 ВМЕСТО {1}.0", link.CurrentGen, maxGen),
                Loc.Tf("{0} под нагрузкой работает на PCIe {1}.0 ×{2}, хотя карта и " +
                "слот поддерживают PCIe {3}.0, — пропускная способность линии ниже возможной. Частые причины: райзер-кабель " +
                "для более старой версии PCIe, плохой контакт в слоте, скорость линии, ограниченная вручную в BIOS. " +
                "Lycoris ничего не меняет.", link.GpuName, link.CurrentGen, link.CurrentWidth ?? maxWidth, maxGen), CheckSeverity.Info, 4);

        var text = Loc.Tf("{0}: PCIe {1}.0 ×{2}", link.GpuName, maxGen, maxWidth) + (!link.UnderLoad
            ? Loc.T(" — столько поддерживают карта и слот; в простое скорость линии снижается для экономии энергии, это нормально.")
            : link.CurrentGen is null
                ? Loc.T(" — под нагрузкой линия работает на полную ширину.")
                : Loc.T(" — под нагрузкой линия работает полностью."));
        if (link.GpuMaxGen is { } cardGen && cardGen > maxGen)
            text += " " + Loc.Tf("Сама карта поддерживает PCIe {0}.0 — это ограничение платы или слота, а не неисправность.", cardGen);
        if (maxWidth < 16)
            text += " " + Loc.T("Если карта рассчитана на ×16 (это указано в её характеристиках), проверьте, что она стоит в верхнем " +
                    "полноразмерном слоте: нижние слоты и линии, общие с M.2, часто дают только ×4 или ×8.");
        return new("pcie-link", Loc.Tf("ЛИНИЯ PCIE ВИДЕОКАРТЫ: {0}.0 ×{1}", maxGen, maxWidth), text, CheckSeverity.Ok, 4);
    }
}
