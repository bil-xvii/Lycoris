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
/// MONOLITH changes none of them. Sources: gaming_tweaks.md §I (gpu-panel-reco, rebar-detect), tech.md §7.
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
            return new("monitor-gpu", "МОНИТОР ПОДКЛЮЧЁН К ВИДЕОКАРТЕ",
                list + ". Кадры выводятся напрямую, без копирования через встроенную графику.", CheckSeverity.Ok, 12);
        }

        // The cable can only go into a card that has display outputs (compute cards and hybrid mobile chips have none).
        var card = s.Adapters.Where(a => a.DisplayTargets > 0)
            .Select(a => s.Gpus.FirstOrDefault(g => MonitorMath.SameGpuName(g.Name, a.Name)))
            .Where(g => g is not null && IsDiscreteCard(g))
            .OrderByDescending(g => g!.DedicatedMemoryBytes)
            .FirstOrDefault();
        if (card is null) return null;

        var (display, igpu) = onboard[0];
        return new("monitor-gpu", "МОНИТОР ПОДКЛЮЧЁН КО ВСТРОЕННОЙ ГРАФИКЕ",
            $"{display.Name} подключён ко встроенной графике ({igpu!.Name}), а видеокарта {card.Name} простаивает или копирует " +
            "кадры через неё — меньше FPS и больше задержка. Подключите кабель монитора к разъёму на видеокарте: это " +
            "горизонтальный ряд портов в нижней части задней панели, под разъёмами материнской платы. Встроенную графику " +
            "выключать не нужно, а второй монитор на ней — нормальная схема. MONOLITH здесь ничего не меняет.",
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
            problems.Add("Windows отмечает его как неисправный");
            severe = true;
        }
        else if (d.Status == DiskHealthStatus.Warning)
        {
            problems.Add("Windows предупреждает о проблеме с ним");
        }
        if (d.WearPercent is { } wear && wear >= 100)
        {
            problems.Add($"ресурс записи исчерпан (износ {wear} %)");
            severe = true;
        }
        else if (d.WearPercent is { } w && w >= WearWarnPercent)
        {
            problems.Add($"износ {w} %");
        }
        var errors = UncorrectedErrors(d);
        if (errors > 0)
            problems.Add(DashFormat.Count((long)Math.Min(errors, long.MaxValue), "неисправленная ошибка", "неисправленные ошибки", "неисправленных ошибок") +
                         " чтения или записи");
        var hot = IsDiskHot(d);
        if (hot) problems.Add($"температура {d.TemperatureC} °C при пределе {d.TemperatureMaxC} °C");
        return new DiskVerdict(d, severe, hot, problems);
    }

    /// <summary>"Samsung SSD 990 PRO 2TB: износ 3 %, 41 °C, ошибок нет" — only the values the drive reports.</summary>
    public static string DiskSummary(DiskHealth d)
    {
        var parts = new List<string>();
        if (d.WearPercent is { } w) parts.Add($"износ {w} %");
        if (d.TemperatureC is { } t) parts.Add($"{t} °C");
        if (d.ReadErrorsUncorrected is not null && d.WriteErrorsUncorrected is not null)
            parts.Add(UncorrectedErrors(d) == 0 ? "ошибок нет" : $"неисправленных ошибок: {UncorrectedErrors(d)}");
        if (parts.Count == 0)
            parts.Add(d.Status switch
            {
                DiskHealthStatus.Healthy => "исправен по данным Windows",
                DiskHealthStatus.Warning => "Windows предупреждает о проблеме",
                DiskHealthStatus.Unhealthy => "Windows отмечает его как неисправный",
                _ => "состояние не сообщает",
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
                detail += " Сделайте резервную копию важных файлов на другой диск или в облако: заранее сказать, когда накопитель " +
                          "откажет, нельзя.";
            if (bad.Any(v => v.Hot))
                detail += " Перегрев сокращает срок службы и замедляет запись: проверьте обдув корпуса и радиатор M.2.";
            detail += " MONOLITH накопители не «ремонтирует» и ничего на них не меняет.";
            return new("disk-health", bad.Count == 1 ? "НАКОПИТЕЛЬ ТРЕБУЕТ ВНИМАНИЯ" : "НАКОПИТЕЛИ ТРЕБУЮТ ВНИМАНИЯ",
                detail, CheckSeverity.Warning, bad.Any(v => v.Severe) ? 20 : 12);
        }

        var text = string.Join("; ", disks.Select(DiskSummary)) + ".";
        if (!s.Elevated)
            text += " Износ, температура и счётчики ошибок доступны при запуске MONOLITH с правами администратора.";
        else if (disks.Any(d => !d.HasCounters))
            text += " Часть накопителей не сообщает счётчики износа (так бывает с USB-адаптерами и RAID) — это не ошибка.";
        return new("disk-health", "НАКОПИТЕЛИ: ПРОБЛЕМ НЕ НАЙДЕНО", text, CheckSeverity.Ok, 10);
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

    public static HealthCheckResult? CheckRebar(SystemSnapshot s)
    {
        if (s.GpuLink is not { } link) return null;
        switch (ClassifyRebar(link.Bar1Total, link.VramTotal))
        {
            case RebarState.On:
                return new("rebar", "RESIZABLE BAR ВКЛЮЧЁН",
                    $"{link.GpuName}: процессору доступна вся видеопамять (окно BAR1 — {DashFormat.WholeGb(link.Bar1Total)}). " +
                    "Драйвер NVIDIA использует это в тех играх, где оно даёт прирост.", CheckSeverity.Ok, 4);
            case RebarState.NotActive:
                var bios = s.Firmware switch
                {
                    FirmwareKind.Uefi => " Включите в BIOS/UEFI «Above 4G Decoding» и «Re-Size BAR Support»; если там включён CSM, " +
                                         "выключите его — Windows на этом ПК уже загружается в режиме UEFI.",
                    FirmwareKind.LegacyBios => " Resizable BAR работает только при загрузке в режиме UEFI, а Windows сейчас загружается " +
                                               "в режиме Legacy (CSM). Не выключайте CSM, пока системный диск размечен как MBR, — " +
                                               "Windows перестанет загружаться.",
                    _ => " Включите в BIOS/UEFI «Above 4G Decoding» и «Re-Size BAR Support».",
                };
                return new("rebar", "RESIZABLE BAR НЕ АКТИВЕН",
                    $"{link.GpuName}: процессору доступно только окно 256 МБ видеопамяти. Resizable BAR ускоряет часть игр — " +
                    "драйвер NVIDIA включает его только для игр из своего списка. Возможные причины: функция выключена в BIOS, " +
                    "у карты старая прошивка без поддержки (ранним RTX 30 нужно обновление VBIOS с сайта производителя карты), " +
                    "платформа её не поддерживает" + (s.IsLaptop ? " (на ноутбуке это решает производитель)." : ".") + bios +
                    " При включённом BitLocker сначала сохраните ключ восстановления. MONOLITH настройки BIOS не меняет.",
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

    public static HealthCheckResult? CheckPcieLink(SystemSnapshot s)
    {
        if (s.GpuLink is not { MaxGen: { } maxGen, MaxWidth: { } maxWidth } link) return null;
        if (IsLinkDegraded(link))
            return new("pcie-link", "ЛИНИЯ PCIE ВИДЕОКАРТЫ СУЖЕНА",
                $"{link.GpuName} под нагрузкой работает на PCIe ×{link.CurrentWidth}, хотя карта и слот поддерживают ×{maxWidth}, — " +
                "линия работает ниже возможностей карты. Частые причины: карта не до конца вставлена в слот или перекошена, " +
                "загрязнены контакты, некачественный райзер-кабель. Выключите ПК, отключите питание и переустановите карту до " +
                "щелчка фиксатора. MONOLITH ничего не меняет.", CheckSeverity.Warning, 6);

        var text = $"{link.GpuName}: PCIe {maxGen}.0 ×{maxWidth}" + (link.UnderLoad
            ? " — под нагрузкой линия работает полностью."
            : " — столько поддерживают карта и слот; в простое скорость линии снижается для экономии энергии, это нормально.");
        if (link.GpuMaxGen is { } cardGen && cardGen > maxGen)
            text += $" Сама карта поддерживает PCIe {cardGen}.0 — это ограничение платы или слота, а не неисправность.";
        if (maxWidth < 16)
            text += " Если карта рассчитана на ×16 (это указано в её характеристиках), проверьте, что она стоит в верхнем " +
                    "полноразмерном слоте: нижние слоты и линии, общие с M.2, часто дают только ×4 или ×8.";
        return new("pcie-link", $"ЛИНИЯ PCIE ВИДЕОКАРТЫ: {maxGen}.0 ×{maxWidth}", text, CheckSeverity.Ok, 4);
    }
}
