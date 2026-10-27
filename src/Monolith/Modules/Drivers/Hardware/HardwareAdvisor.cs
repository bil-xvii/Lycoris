using Monolith.Core;
using Monolith.Modules.Dashboard.Checks;

namespace Monolith.Modules.Drivers.Hardware;

/// <summary>
/// Decides, for every detected component, which OFFICIAL component it needs and why (pure: snapshot + context → report).
/// Sources, in this order of preference: the vendor's own server with a signature check (GPU drivers), verified winget
/// packages, Windows Update, and otherwise the vendor's page for a manual step. Never third-party driver databases.
/// Only clearly needed and safe items are pre-selected (missing/outdated GPU driver, missing game runtimes).
/// </summary>
internal static class HardwareAdvisor
{
    private static readonly string[] PeripheralClasses = ["HIDClass", "Keyboard", "Mouse", "MEDIA", "Camera", "Image"];

    public static HwReport Build(HardwareSnapshot s, HardwareContext ctx)
    {
        var wu = new WuIndex(ctx.Wu);
        var groups = new List<HwGroup>
        {
            Platform(s, ctx, wu),
            Gpus(s, ctx, wu),
            Memory(s),
            Storage(s),
            Network(s, ctx, wu),
            Audio(s, ctx, wu),
            Monitors(s),
            Peripherals(s, wu),
            Runtimes(s),
        };
        groups = groups.Select(g => g with { WuExtra = wu.UnmatchedCount(g.Kind) }).ToList();
        // Same package offered for several devices → installed once.
        return new HwReport(Deduplicate(groups).Where(g => g.Components.Count > 0 || g.WuExtra > 0).ToList());
    }

    // ───────────────────────────── Windows Update ─────────────────────────────

    private sealed class WuIndex(WuView view)
    {
        private readonly HashSet<string> _matched = new(StringComparer.OrdinalIgnoreCase);

        public WuViewState State => view.State;

        public List<WuDriverUpdate> Match(string? deviceId, IEnumerable<string>? hardwareIds)
        {
            var ids = hardwareIds?.ToList();
            var list = view.Updates.Where(u => HardwareRules.WuMatches(deviceId, ids, u.HardwareId)).ToList();
            foreach (var u in list) _matched.Add(u.UpdateId);
            return list;
        }

        public int UnmatchedCount(HwGroupKind kind) =>
            view.Updates.Count(u => !_matched.Contains(u.UpdateId) && HardwareRules.WuGroup(u.DriverClass) == kind);
    }

    private static HwAction WuAction(string key, IReadOnlyList<WuDriverUpdate> updates)
    {
        var n = updates.Count;
        var titles = string.Join("; ", updates.Take(2).Select(u => u.Title)) + (n > 2 ? $" и ещё {n - 2}" : "");
        var maker = updates.Select(u => u.Manufacturer ?? u.Provider).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        return new HwAction("wu:" + key, HwActionKind.GoToTab, HwSource.WindowsUpdate,
            $"{n} {HardwareRules.Plural(n, "драйвер", "драйвера", "драйверов")} в Windows Update",
            $"{titles}. Установка — на вкладке «Обновления Windows»: с точкой восстановления и копией текущего драйвера.",
            maker is null ? "Microsoft (Windows Update)" : $"{maker} через Windows Update")
        {
            Url = nameof(DriversTab.Updates),
            ButtonText = "К ОБНОВЛЕНИЯМ",
            WuCount = n,
        };
    }

    private sealed record DriverVerdict(HwStatus Status, string? StatusText, string? Note, List<HwAction> Actions);

    /// <summary>Common verdict for devices whose drivers come from Windows Update (network, audio, Bluetooth…).</summary>
    private static DriverVerdict WuVerdict(string key, PnpDevice? device, string deviceId, WuIndex wu)
    {
        var matches = wu.Match(deviceId, device?.HardwareIds);
        var actions = new List<HwAction>();
        if (matches.Count > 0) actions.Add(WuAction(key, matches));

        if (device is not null && DeviceProblemCodes.IsReportable(device.ErrorCode, device.Present))
        {
            var info = DeviceProblemCodes.Describe(device.ErrorCode);
            actions.Add(new HwAction("problem:" + key, HwActionKind.GoToTab, HwSource.Manual, "Подробности и что делать",
                $"{info.Title}. {info.Action}", "Windows")
            {
                Url = nameof(DriversTab.Problems),
                ButtonText = "ПРОБЛЕМНЫЕ УСТРОЙСТВА",
            });
            return new DriverVerdict(HwStatus.NotInstalled, device.ErrorCode == 28 ? null : $"ОШИБКА · КОД {device.ErrorCode}",
                info.Explanation, actions);
        }
        if (matches.Count > 0) return new DriverVerdict(HwStatus.UpdateAvailable, null, null, actions);
        return wu.State switch
        {
            WuViewState.Ready => new DriverVerdict(HwStatus.Current, null, null, actions),
            WuViewState.Error => new DriverVerdict(HwStatus.Unchecked, null,
                "Windows Update не ответил — свежесть драйвера не проверена.", actions),
            _ => new DriverVerdict(HwStatus.Unchecked, "ИДЁТ ПОИСК", null, actions),
        };
    }

    private static string WuDetail(WuIndex wu) => wu.State switch
    {
        WuViewState.Ready => "новых драйверов в Windows Update нет",
        WuViewState.Error => "Windows Update не проверен",
        _ => "Windows Update ищет драйверы…",
    };

    // ───────────────────────────── Shared builders ─────────────────────────────

    private static SignedDriverInfo? DriverOf(HardwareContext ctx, string deviceId) =>
        ctx.Drivers.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

    private static string DriverText(SignedDriverInfo? d)
    {
        if (d is null) return "";
        var provider = string.IsNullOrWhiteSpace(d.Provider) ? "" : d.Provider.Replace(" Corporation", "").Replace(", Inc.", "").Replace(" Inc.", "").Trim() + " ";
        var date = d.Date is { } dt ? " от " + HardwareRules.Date(dt) : "";
        return $"драйвер {provider}{d.Version}{date}{(d.IsOem ? "" : " (встроен в Windows)")}";
    }

    private static PnpDevice? Device(HardwareSnapshot s, string deviceId) =>
        s.Devices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

    private static string Join(params string?[] parts) =>
        string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>Install / open action for a vendor program (winget only for verified ids).</summary>
    private static HwAction? SoftwareAction(VendorSoftware sw, HardwareSnapshot s, string? reason = null, bool optional = true)
    {
        var why = reason is null ? sw.Description : $"{reason} {sw.Description}";
        if (sw.IsInstalled(s.Apps))
        {
            return new HwAction("sw:" + sw.Name, sw.Url is null ? HwActionKind.Note : HwActionKind.OpenPage, HwSource.Manual,
                sw.Name, "Уже установлено на этом ПК. " + sw.Description, sw.Publisher)
            {
                AlreadyInstalled = true,
                Optional = optional,
                Url = sw.Url,
                ButtonText = sw.Url is null ? null : "ОФИЦИАЛЬНЫЙ САЙТ",
            };
        }
        if (sw.WingetId is { } id && s.WingetAvailable && !DriverSources.IsBlocked(id))
        {
            return new HwAction("winget:" + id, HwActionKind.Winget, HwSource.Winget, sw.Name, why, sw.Publisher)
            {
                Optional = optional,
                Winget = new WingetPackageRequest(id),
            };
        }
        if (sw.StoreId is { } store)
        {
            return new HwAction("store:" + store, HwActionKind.OpenStore, HwSource.Manual, sw.Name,
                why + " Установка — в Microsoft Store (откроется страница программы).", sw.Publisher)
            {
                Optional = optional,
                Url = $"ms-windows-store://pdp/?productid={store}",
                ButtonText = "MICROSOFT STORE",
            };
        }
        if (sw.Url is { } url)
        {
            return new HwAction("page:" + sw.Name, HwActionKind.OpenPage, HwSource.Manual, sw.Name, why, sw.Publisher)
            {
                Optional = optional,
                Url = url,
                ButtonText = "ОФИЦИАЛЬНЫЙ САЙТ",
            };
        }
        return null;
    }

    private static HwAction RuntimeAction(string id, HardwareSnapshot s, string reason, bool preselect, bool optional)
    {
        var entry = HardwareCatalog.Runtime(id);
        var name = entry?.Name ?? id;
        if (entry is not null && entry.CanInstall && s.WingetAvailable)
        {
            return new HwAction("winget:" + id, HwActionKind.Winget, HwSource.Winget, name, reason, "Microsoft Corporation")
            {
                Winget = entry.CreateRequest(entry.Id),
                PreSelect = preselect,
                Optional = optional,
                Url = entry.Website,
            };
        }
        return new HwAction("page:" + id, HwActionKind.OpenPage, HwSource.Manual, name,
            reason + (s.WingetAvailable ? "" : " winget не найден — скачайте установщик на сайте Microsoft."), "Microsoft Corporation")
        {
            Optional = optional,
            Url = entry?.Website ?? "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist",
            ButtonText = "САЙТ MICROSOFT",
        };
    }

    /// <summary>The same installable package offered twice (Intel DSA for GPU + Wi-Fi…) is installed once: later copies become a note.</summary>
    private static IEnumerable<HwGroup> Deduplicate(IReadOnlyList<HwGroup> groups)
    {
        // The copy that stays installable carries the strongest need of all copies (required beats optional).
        var all = groups.SelectMany(g => g.Components).SelectMany(c => c.Actions).Where(a => a.IsInstallable).ToList();
        var required = all.Where(a => !a.Optional).Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preselect = all.Where(a => a.PreSelect).Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var comps = g.Components.Select(c => c with
            {
                Actions = c.Actions.Select(a =>
                {
                    if (!a.IsInstallable) return a;
                    if (seen.Add(a.Key)) return a with { Optional = !required.Contains(a.Key), PreSelect = preselect.Contains(a.Key) };
                    return a with
                    {
                        Key = a.Key + "@" + c.Key,
                        Kind = HwActionKind.Note,
                        Reason = "Этот же пакет уже предложен выше — достаточно установить его один раз.",
                        PreSelect = false,
                        Winget = null,
                        Package = null,
                    };
                }).ToList(),
            }).ToList();
            yield return g with { Components = comps };
        }
    }

    // ───────────────────────────── 01 Processor and platform ─────────────────────────────

    private static HwGroup Platform(HardwareSnapshot s, HardwareContext ctx, WuIndex wu)
    {
        var list = new List<HwComponent>();
        var cpu = s.Cpu;

        // CPU
        var x3d = ChipsetAdvisor.IsX3D(cpu.Name);
        string? cpuNote = null;
        if (ChipsetAdvisor.IsDualCcdX3D(cpu.Name))
            cpuNote = "Двухблочный X3D: драйвер чипсета AMD переводит игры на блок ядер с 3D V-Cache. Для этого нужны драйвер чипсета, «Игровой режим» и Xbox Game Bar — не отключайте их.";
        else if (x3d)
            cpuNote = "X3D с одним блоком ядер: кэш 3D V-Cache доступен всем ядрам, специальная настройка не нужна. Оптимизатор V-Cache из пакета чипсета безвреден — не отключайте его службы.";
        list.Add(new HwComponent("cpu", "ПРОЦЕССОР", cpu.Name.Length > 0 ? cpu.Name : "Процессор не определён",
            Join(cpu.Cores > 0 ? $"{cpu.Cores} {HardwareRules.Plural(cpu.Cores, "ядро", "ядра", "ядер")}" : null,
                cpu.Threads > 0 ? $"{cpu.Threads} {HardwareRules.Plural(cpu.Threads, "поток", "потока", "потоков")}" : null,
                HardwareRules.Ghz(cpu.MaxMhz),
                x3d ? "3D V-Cache (X3D)" : null),
            HwStatus.Info, [])
        { Note = cpuNote });

        // Board + BIOS
        var b = s.Board;
        var vendor = HardwareCatalog.Board(b.Manufacturer) ?? HardwareCatalog.Board(b.SystemManufacturer);
        var boardName = $"{vendor?.ShortName ?? b.Manufacturer} {b.Product}".Trim();
        if (s.IsLaptop || boardName.Length == 0) boardName = $"{b.SystemManufacturer} {b.SystemModel}".Trim();
        var chipsetVendor = ChipsetVendor(s);
        var biosAge = HardwareRules.BiosAgeMonths(b.BiosDate, ctx.Now);
        var details = Join(
            b.BiosVersion.Length > 0 ? $"BIOS {b.BiosVersion}" : null,
            b.BiosDate is { } bd ? $"от {HardwareRules.Date(bd)} ({DriverAge.Format(biosAge)})" : null,
            b.BiosVendor,
            chipsetVendor is null ? null : $"чипсет {chipsetVendor}");
        var boardActions = new List<HwAction>();
        HwStatus boardStatus;
        string? boardNote;
        if (HardwareRules.BiosIsOld(b.BiosDate, ctx.Now))
        {
            boardStatus = HwStatus.ManualOnly;
            boardNote = null;
            boardActions.Add(new HwAction("bios", vendor is null ? HwActionKind.Note : HwActionKind.OpenPage, HwSource.Manual,
                "Проверить обновление BIOS",
                $"BIOS выпущен {DriverAge.Format(biosAge)} назад. Новые версии исправляют совместимость с процессорами и памятью и закрывают уязвимости. " +
                "Обновление BIOS — только вручную с сайта производителя платы, строго по его инструкции: сбой прошивки может вывести плату из строя. MONOLITH BIOS не прошивает.",
                vendor?.ShortName ?? (b.Manufacturer.Length > 0 ? b.Manufacturer : "производитель платы"))
            {
                Url = vendor?.SupportUrl,
                ButtonText = vendor is null ? null : $"САЙТ {vendor.ShortName.ToUpperInvariant()}",
            });
        }
        else
        {
            boardStatus = HwStatus.Info;
            boardNote = b.BiosDate is null
                ? "Дата BIOS неизвестна. MONOLITH не проверяет новые версии BIOS и никогда не прошивает его."
                : "BIOS моложе двух лет. MONOLITH не проверяет новые версии BIOS и никогда не прошивает его — обновление только вручную с сайта производителя платы.";
            if (vendor is not null)
                boardActions.Add(new HwAction("board-page", HwActionKind.OpenPage, HwSource.Manual, "Страница поддержки платы",
                    $"Драйверы, утилиты и BIOS для {boardName} — на странице поддержки производителя (найдите свою модель).", vendor.ShortName)
                {
                    Optional = true,
                    Url = vendor.SupportUrl,
                    ButtonText = $"САЙТ {vendor.ShortName.ToUpperInvariant()}",
                });
        }
        if (boardName.Length > 0 || b.BiosVersion.Length > 0)
            list.Add(new HwComponent("board", s.IsLaptop ? "НОУТБУК И BIOS" : "ПЛАТА И BIOS", boardName.Length > 0 ? boardName : "Плата не определена",
                details, boardStatus, boardActions) { Note = boardNote });

        list.Add(Chipset(s, ctx, wu));
        return new HwGroup(HwGroupKind.Platform, "ПРОЦЕССОР И ПЛАТФОРМА", list);
    }

    private static string? ChipsetVendor(HardwareSnapshot s)
    {
        int amd = 0, intel = 0;
        foreach (var d in s.Devices)
        {
            if (!string.Equals(d.PnpClass, "System", StringComparison.OrdinalIgnoreCase)) continue;
            if (d.DeviceId.StartsWith(@"PCI\VEN_1022", StringComparison.OrdinalIgnoreCase)) amd++;
            else if (d.DeviceId.StartsWith(@"PCI\VEN_8086", StringComparison.OrdinalIgnoreCase)) intel++;
        }
        if (amd == 0 && intel == 0) return null;
        return amd >= intel ? "AMD" : "Intel";
    }

    private static bool IsPlatformDevice(PnpDevice d) =>
        string.Equals(d.PnpClass, "System", StringComparison.OrdinalIgnoreCase) &&
        (d.DeviceId.StartsWith(@"PCI\VEN_1022", StringComparison.OrdinalIgnoreCase) || d.DeviceId.StartsWith(@"PCI\VEN_8086", StringComparison.OrdinalIgnoreCase) ||
         d.DeviceId.StartsWith(@"ACPI\AMDI", StringComparison.OrdinalIgnoreCase) || d.DeviceId.StartsWith(@"ACPI\VEN_AMDI", StringComparison.OrdinalIgnoreCase) ||
         d.DeviceId.StartsWith(@"ACPI\INT", StringComparison.OrdinalIgnoreCase));

    private static HwComponent Chipset(HardwareSnapshot s, HardwareContext ctx, WuIndex wu)
    {
        var cpu = s.Cpu;
        var problems = s.Devices.Where(d => DeviceProblemCodes.IsReportable(d.ErrorCode, d.Present))
            .Select(d => new ProblemDeviceInfo(d.Name, d.DeviceId, d.PnpClass, d.HardwareIds, d.ErrorCode, d.Present, d.Manufacturer))
            .ToList();
        var amdPackage = s.Apps.FirstOrDefault(a => a.DisplayName.StartsWith("AMD Chipset Software", StringComparison.OrdinalIgnoreCase))?.Version;
        var report = ChipsetAdvisor.Analyze(cpu.Name, cpu.IsAmd, cpu.IsIntel, ctx.Drivers, problems, amdPackage);

        var wuMatches = s.Devices.Where(IsPlatformDevice).SelectMany(d => wu.Match(d.DeviceId, d.HardwareIds))
            .DistinctBy(u => u.UpdateId).ToList();

        var actions = new List<HwAction>();
        var noInventory = ctx.Drivers.Count == 0;
        var status = noInventory ? HwStatus.Unchecked : report.DriverOk switch
        {
            true => HwStatus.Installed,
            false => HwStatus.NotInstalled,
            _ => HwStatus.Unchecked,
        };
        if (wuMatches.Count > 0)
        {
            actions.Add(WuAction("chipset", wuMatches));
            if (status is HwStatus.Installed or HwStatus.Unchecked) status = HwStatus.UpdateAvailable;
        }

        var installedParts = report.Components.Where(c => c.State == ComponentState.Installed)
            .Select(c => string.IsNullOrWhiteSpace(c.Version) ? c.Title : $"{c.Title} {c.Version}").Take(4).ToList();
        var missingParts = report.Components.Where(c => c.Required && c.State != ComponentState.Installed).Select(c => c.Title).ToList();

        string name, kindNote;
        switch (report.Platform)
        {
            case ChipsetPlatform.Amd:
                name = "Драйвер чипсета AMD";
                kindNote = report.DriverOk == true
                    ? "Новые версии пакета выходят на amd.com (Chipsets → сокет платы) и приходят через Windows Update. Автоматической загрузки с проверкой подписи для пакета чипсета нет — только вручную."
                    : report.Recommendation;
                actions.Add(new HwAction("amd-chipset", HwActionKind.OpenPage, HwSource.Manual, "AMD Chipset Software",
                    kindNote, "Advanced Micro Devices")
                {
                    Optional = report.DriverOk == true,
                    Url = DriverSources.AmdDriversPage,
                    ButtonText = "AMD.COM",
                });
                break;
            case ChipsetPlatform.Intel:
                name = "Драйверы платформы Intel";
                kindNote = report.Recommendation;
                if (SoftwareAction(HardwareCatalog.IntelDsa, s, "Драйверы чипсета Intel приходят через Windows Update; проверить их поможет помощник Intel.",
                        optional: report.DriverOk == true) is { } dsa)
                    actions.Add(dsa);
                break;
            default:
                name = "Драйвер чипсета";
                kindNote = report.Recommendation;
                break;
        }

        var details = noInventory
            ? "список драйверов ещё не загружен"
            : Join(installedParts.Count > 0 ? string.Join(" · ", installedParts) : null,
                missingParts.Count > 0 ? "нет: " + string.Join(", ", missingParts) : null,
                WuDetail(wu));
        var note = report.PackageText ?? (report.Platform == ChipsetPlatform.Intel ? null : kindNote);
        return new HwComponent("chipset", "ЧИПСЕТ", name, details, status, actions)
        {
            Note = note,
            StatusText = noInventory ? "ЗАГРУЗКА ДАННЫХ" : null,
        };
    }

    // ───────────────────────────── 02 GPUs ─────────────────────────────

    private static HwGroup Gpus(HardwareSnapshot s, HardwareContext ctx, WuIndex wu)
    {
        var primary = s.PrimaryGpu;
        var list = new List<HwComponent>();
        foreach (var gpu in s.Gpus.OrderByDescending(g => g.PnpDeviceId == primary?.PnpDeviceId))
            list.Add(Gpu(gpu, gpu.PnpDeviceId == primary?.PnpDeviceId, s, ctx, wu));
        return new HwGroup(HwGroupKind.Gpu, "ВИДЕОКАРТЫ", list);
    }

    private static string VendorText(GpuVendor v) => v switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        GpuVendor.Microsoft => "Microsoft",
        _ => "",
    };

    private static HwComponent Gpu(GpuInfo gpu, bool isPrimary, HardwareSnapshot s, HardwareContext ctx, WuIndex wu)
    {
        s.GpuRegistry.TryGetValue(gpu.PnpDeviceId, out var reg);
        var informational = !isPrimary && gpu.IsIntegrated;
        var missing = gpu.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(reg?.InfPath, "display.inf", StringComparison.OrdinalIgnoreCase);
        var version = missing ? "—" : GpuDriverVersions.DisplayVersion(gpu.Vendor, gpu.DriverVersion, reg?.RadeonSoftwareVersion,
            gpu.Vendor == GpuVendor.Nvidia ? s.NvidiaPackageVersion : null);
        var kind = isPrimary ? "ОСНОВНАЯ ВИДЕОКАРТА" : gpu.IsIntegrated ? "ВСТРОЕННАЯ ГРАФИКА" : "ДОП. ВИДЕОКАРТА";
        var vram = !gpu.IsIntegrated && gpu.DedicatedMemoryBytes >= 1UL << 30 ? HardwareRules.Gb(gpu.DedicatedMemoryBytes) + " видеопамяти" : null;
        var device = Device(s, gpu.PnpDeviceId);
        var wuMatches = wu.Match(gpu.PnpDeviceId, device?.HardwareIds);
        var key = "gpu:" + gpu.PnpDeviceId;

        var actions = new List<HwAction>();
        HwStatus status;
        string? statusText = null, note = null, latestText = null;

        if (missing)
        {
            status = HwStatus.NotInstalled;
            note = "Видеокарта работает на стандартном драйвере Windows: нет 3D-ускорения, игр и высоких частот обновления. Нужен драйвер производителя.";
            switch (gpu.Vendor)
            {
                case GpuVendor.Amd when ctx.Online.AmdPackage is { } amdPkg:
                    actions.Add(AmdPackageAction(amdPkg, gpu, isPrimary, "Драйвер не установлен."));
                    break;
                case GpuVendor.Amd:
                    actions.Add(PageAction("amd-page", "AMD Software: Adrenalin Edition", "Advanced Micro Devices", DriverSources.AmdDriversPage,
                        "Официальный установщик с автоопределением видеокарты — на amd.com.", optional: false, "AMD.COM"));
                    break;
                case GpuVendor.Nvidia:
                    actions.Add(PageAction("nvidia-page", "Драйвер NVIDIA", "NVIDIA Corporation", DriverSources.NvidiaDriversPage,
                        "Без драйвера Windows не сообщает модель видеокарты, поэтому выберите её на nvidia.com (или установите драйвер через Windows Update).",
                        optional: false, "NVIDIA.COM"));
                    break;
                case GpuVendor.Intel:
                    if (SoftwareAction(HardwareCatalog.IntelDsa, s, "Драйвер видеоядра Intel установит помощник Intel.", optional: false) is { } dsa) actions.Add(dsa);
                    break;
            }
        }
        else if (informational)
        {
            status = HwStatus.Info;
            note ="Изображение выводит основная видеокарта. Драйвер встроенной графики приходит через Windows Update — отдельных действий не нужно.";
        }
        else
        {
            switch (gpu.Vendor)
            {
                case GpuVendor.Nvidia:
                {
                    if (!ctx.Online.NvidiaByGpu.TryGetValue(gpu.PnpDeviceId, out var release))
                    {
                        status = HwStatus.Unchecked;
                        statusText = ctx.Online.Pending ? "ПРОВЕРКА ВЕРСИИ" : null;
                        break;
                    }
                    var cmp = release is null ? null : GpuDriverVersions.CompareNvidia(release.Version, version);
                    if (release is null || cmp is null)
                    {
                        status = HwStatus.Unchecked;
                        note = "nvidia.com не ответил или не знает эту модель — последнюю версию проверьте в NVIDIA App или на сайте NVIDIA.";
                        actions.Add(PageAction("nvidia-page", "Драйверы NVIDIA", "NVIDIA Corporation", DriverSources.NvidiaDriversPage,
                            "Официальная страница загрузки драйверов NVIDIA.", optional: true, "NVIDIA.COM"));
                        break;
                    }
                    latestText = $"последний на nvidia.com: {release.Version}" + (release.Released is { } rd ? $" от {HardwareRules.Date(rd)}" : "");
                    if (cmp > 0)
                    {
                        status = HwStatus.UpdateAvailable;
                        actions.Add(NvidiaAction(release, gpu, version, isPrimary));
                    }
                    else
                    {
                        status = HwStatus.Current;
                    }
                    break;
                }
                case GpuVendor.Amd:
                {
                    if (!ctx.Online.AmdChecked)
                    {
                        status = HwStatus.Unchecked;
                        statusText = ctx.Online.Pending ? "ПРОВЕРКА ВЕРСИИ" : null;
                        break;
                    }
                    if (ctx.Online.AmdPackage is not { } pkg)
                    {
                        status = HwStatus.Unchecked;
                        note = "amd.com не ответил — последнюю версию проверьте на сайте AMD.";
                        actions.Add(PageAction("amd-page", "AMD Software: Adrenalin Edition", "Advanced Micro Devices", DriverSources.AmdDriversPage,
                            "Официальный установщик с автоопределением видеокарты — на amd.com.", optional: true, "AMD.COM"));
                        break;
                    }
                    var cmp = HardwareRules.CompareVersions(pkg.Version, reg?.RadeonSoftwareVersion);
                    latestText = pkg.Version != "—" ? $"последний на amd.com: {pkg.Version}" : null;
                    if (cmp is null)
                    {
                        status = HwStatus.Unchecked;
                        note = "Версию установленного AMD Software сравнить не удалось. Официальный установщик сам определит видеокарту и предложит нужный драйвер.";
                        actions.Add(AmdPackageAction(pkg, gpu, preselect: false, "Версии сравнить не удалось.") with { Optional = true });
                    }
                    else if (cmp > 0)
                    {
                        status = HwStatus.UpdateAvailable;
                        actions.Add(AmdPackageAction(pkg, gpu, isPrimary, $"Установлен {version}."));
                    }
                    else
                    {
                        status = HwStatus.Current;
                    }
                    break;
                }
                case GpuVendor.Intel:
                    status = HwStatus.Unchecked;
                    statusText = "ПРОВЕРЯЕТ INTEL DSA";
                    note = "У Intel нет открытого способа узнать последнюю версию — её проверяет Intel Driver & Support Assistant.";
                    if (SoftwareAction(HardwareCatalog.IntelDsa, s, optional: false) is { } dsa) actions.Add(dsa);
                    break;
                default:
                    status = HwStatus.Unchecked;
                    break;
            }
        }

        if (wuMatches.Count > 0)
        {
            // An alternative when the vendor's own driver is already known; the main route when the driver is missing.
            actions.Add(WuAction(key, wuMatches) with { Optional = informational || status is HwStatus.Current or HwStatus.UpdateAvailable });
            if (status == HwStatus.Unchecked && statusText is null) status = HwStatus.UpdateAvailable;
        }

        var details = Join(VendorText(gpu.Vendor),
            missing ? "драйвер производителя не установлен" : $"драйвер {version}",
            gpu.DriverDate is { } dd && !missing ? $"от {HardwareRules.Date(dd)}" : null,
            vram, latestText);
        return new HwComponent(key, kind, gpu.Name, details, status, actions) { Note = note, StatusText = statusText };
    }

    private static HwAction PageAction(string key, string title, string publisher, string url, string reason, bool optional, string button) =>
        new(key, HwActionKind.OpenPage, HwSource.Manual, title, reason, publisher)
        {
            Optional = optional,
            Url = url,
            ButtonText = button,
        };

    private static HwAction NvidiaAction(NvidiaRelease release, GpuInfo gpu, string installed, bool isPrimary)
    {
        var flags = string.Join(", ", new[] { release.IsWhql ? "WHQL" : null, release.IsBeta ? "бета" : null,
            release.Released is { } d ? "от " + HardwareRules.Date(d) : null }.Where(x => x is not null));
        if (DriverDownloads.NvidiaPackage(release) is not { } pkg)
        {
            return PageAction("nvidia-page", $"Драйвер NVIDIA {release.Version}", "NVIDIA Corporation",
                release.DetailsUrl ?? DriverSources.NvidiaDriversPage,
                $"Установлен {installed}, доступен {release.Version} ({flags}). Прямую ссылку на установщик NVIDIA не дала — скачайте его на nvidia.com.",
                optional: false, "NVIDIA.COM");
        }
        return new HwAction("nvidia:" + gpu.PnpDeviceId, HwActionKind.DriverPackage, HwSource.VendorServer,
            $"Драйвер NVIDIA {release.Version}",
            $"Установлен {installed}, доступен {release.Version}{(flags.Length > 0 ? $" ({flags})" : "")}. Загрузка с сервера NVIDIA" +
            $"{(string.IsNullOrWhiteSpace(release.FileSize) ? "" : $" ({release.FileSize})")}, подпись NVIDIA проверяется, затем откроется установщик NVIDIA.",
            "NVIDIA Corporation")
        {
            Package = pkg,
            GpuPnpId = gpu.PnpDeviceId,
            PreSelect = isPrimary,
            Url = release.DetailsUrl,
            SizeText = release.FileSize,
        };
    }

    private static HwAction AmdPackageAction(DriverPackage pkg, GpuInfo gpu, bool preselect, string prefix) =>
        new("amd:" + gpu.PnpDeviceId, HwActionKind.DriverPackage, HwSource.VendorServer,
            pkg.Version != "—" ? $"AMD Software: Adrenalin Edition {pkg.Version}" : "AMD Software: Adrenalin Edition",
            $"{prefix} Официальный установщик AMD с автоопределением видеокарты: загрузка с drivers.amd.com, подпись AMD проверяется, затем откроется установщик AMD.",
            "Advanced Micro Devices")
        {
            Package = pkg,
            GpuPnpId = gpu.PnpDeviceId,
            PreSelect = preselect,
            Url = DriverSources.AmdDriversPage,
        };

    // ───────────────────────────── 03 Memory ─────────────────────────────

    private static HwGroup Memory(HardwareSnapshot s)
    {
        var list = new List<HwComponent>();
        var installed = s.Ram.Aggregate(0UL, (sum, m) => sum + m.Capacity);
        if (s.Ram.Count == 0) return new HwGroup(HwGroupKind.Memory, "ПАМЯТЬ", list);

        var v = HealthRules.AnalyzeRam(s.Ram);
        var type = v?.Type ?? "";
        var sizes = s.Ram.GroupBy(m => m.Capacity).Select(g => $"{g.Count()} × {HardwareRules.Gb(g.Key)}");
        var parts = s.Ram.Select(m => m.PartNumber).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(2);
        var name = $"{HardwareRules.Gb(installed)} {type}".Trim();
        var profile = s.Cpu.IsAmd ? (type == "DDR5" ? "EXPO (или XMP)" : "XMP (DOCP / A-XMP)") : "XMP";
        var details = Join(string.Join(", ", sizes),
            v is null ? null : $"{v.Configured} МТ/с",
            v is null ? null : $"номинал {v.Rated} МТ/с",
            string.Join(", ", parts));

        if (v is null)
        {
            list.Add(new HwComponent("ram", "ОПЕРАТИВНАЯ ПАМЯТЬ", name, details, HwStatus.Info, [])
            { Note = "Частоту модулей прочитать не удалось." });
        }
        else if (v.BelowRated)
        {
            var advice = $"Модули рассчитаны на {v.Rated} МТ/с, а работают на {v.Configured} МТ/с — скорее всего, в BIOS выключен профиль {profile}. " +
                         "Включите его в BIOS/UEFI (раздел настроек памяти → профиль → сохранить и перезагрузить). " +
                         "MONOLITH настройки BIOS не меняет; если система станет нестабильной, верните там значение «Auto».";
            if (s.IsLaptop)
                list.Add(new HwComponent("ram", "ОПЕРАТИВНАЯ ПАМЯТЬ", name, details, HwStatus.Info, [])
                { Note = advice + " На ноутбуках профиль часто недоступен — тогда это нормальная работа." });
            else
                list.Add(new HwComponent("ram", "ОПЕРАТИВНАЯ ПАМЯТЬ", name, details, HwStatus.ManualOnly,
                [
                    new HwAction("xmp", HwActionKind.Note, HwSource.Manual, $"Включить {profile} в BIOS", advice, "настройка BIOS"),
                ]));
        }
        else
        {
            list.Add(new HwComponent("ram", "ОПЕРАТИВНАЯ ПАМЯТЬ", name, details, HwStatus.Current, [])
            { Note = "Память работает на частоте, на которую рассчитана (по маркировке модулей)." });
        }
        return new HwGroup(HwGroupKind.Memory, "ПАМЯТЬ", list);
    }

    // ───────────────────────────── 04 Storage ─────────────────────────────

    private static HwGroup Storage(HardwareSnapshot s)
    {
        var list = new List<HwComponent>();
        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var d in s.Disks.OrderBy(d => d.IsUsb))
        {
            var kind = d.IsUsb ? "ВНЕШНИЙ НАКОПИТЕЛЬ"
                : d.BusType == "NVMe" ? "NVME SSD"
                : d.IsSsd ? $"{d.BusType} SSD".Replace("— ", "")
                : d.MediaType == "HDD" ? "ЖЁСТКИЙ ДИСК" : "НАКОПИТЕЛЬ";
            var details = Join(HardwareRules.DiskSize(d.SizeBytes), d.BusType != "—" ? d.BusType : null,
                d.Firmware.Length > 0 ? $"прошивка {d.Firmware}" : null, "драйвер Windows");
            var sw = HardwareRules.StorageSoftware(d);
            var actions = new List<HwAction>();
            var status = HwStatus.Info;
            string? statusText = "ДРАЙВЕР WINDOWS";
            string? note = d.IsUsb ? "Внешнему накопителю отдельные драйверы и программы не нужны." : null;
            if (sw is not null)
            {
                if (sw.IsInstalled(s.Apps))
                {
                    status = HwStatus.Installed;
                    statusText = "ПО УСТАНОВЛЕНО";
                }
                // One tool per maker even with several drives of that brand.
                if (offered.Add(sw.Name) && SoftwareAction(sw, s, "Прошивку SSD обновляет только утилита производителя.") is { } a)
                    actions.Add(a);
            }
            list.Add(new HwComponent($"disk:{i++}:{d.Model}", kind, d.Model, details, status, actions) { StatusText = statusText, Note = note });
        }
        return new HwGroup(HwGroupKind.Storage, "НАКОПИТЕЛИ", list);
    }

    // ───────────────────────────── 05 Network ─────────────────────────────

    private static HwGroup Network(HardwareSnapshot s, HardwareContext ctx, WuIndex wu)
    {
        var list = new List<HwComponent>();
        var board = HardwareCatalog.Board(s.Board.Manufacturer);
        foreach (var a in s.Network.OrderBy(n => n.IsBluetooth).ThenBy(n => HardwareRules.IsWireless(n.Name)))
        {
            var device = Device(s, a.PnpDeviceId);
            var driver = DriverOf(ctx, a.PnpDeviceId);
            var vid = HardwareRules.VendorId(a.PnpDeviceId);
            var vendorName = HardwareCatalog.NetVendorName(vid) ?? (a.Manufacturer.Length > 0 ? a.Manufacturer : null);
            if (a.Name.Contains("Killer", StringComparison.OrdinalIgnoreCase)) vendorName = "Killer";
            var kind = a.IsBluetooth ? "BLUETOOTH" : HardwareRules.IsWireless(a.Name) ? "WI-FI" : "ETHERNET";
            var key = "net:" + a.PnpDeviceId;
            var verdict = WuVerdict(key, device, a.PnpDeviceId, wu);
            var actions = verdict.Actions;

            if (HardwareRules.IsIntelNetwork(a.PnpDeviceId) &&
                SoftwareAction(HardwareCatalog.IntelDsa, s, "Свежие драйверы Wi-Fi, Bluetooth и сетевых карт Intel выходят у Intel раньше, чем в Windows Update.",
                    optional: verdict.Status != HwStatus.NotInstalled) is { } dsa)
                actions.Add(dsa);
            else if (vid == "10EC" && !a.IsBluetooth && driver is { IsOem: false } && board is not null)
                actions.Add(PageAction("realtek-net", "Драйвер Realtek с сайта платы", board.ShortName, board.SupportUrl,
                    $"Сейчас работает встроенный драйвер Windows ({driver.Version}). Он рабочий; драйвер Realtek для вашей платы — в разделе поддержки {board.ShortName} " +
                    "(или придёт через Windows Update).", optional: true, $"САЙТ {board.ShortName.ToUpperInvariant()}"));

            var details = Join(vendorName, DriverText(driver), verdict.Status == HwStatus.NotInstalled ? null : WuDetail(wu));
            list.Add(new HwComponent(key, kind, a.Name.Length > 0 ? a.Name : "Сетевой адаптер", details, verdict.Status, actions)
            { StatusText = verdict.StatusText, Note = verdict.Note });
        }
        return new HwGroup(HwGroupKind.Network, "СЕТЬ", list);
    }

    // ───────────────────────────── 06 Audio ─────────────────────────────

    private static HwGroup Audio(HardwareSnapshot s, HardwareContext ctx, WuIndex wu)
    {
        var list = new List<HwComponent>();
        var board = HardwareCatalog.Board(s.Board.Manufacturer);
        foreach (var a in s.Audio)
        {
            var device = Device(s, a.PnpDeviceId);
            var driver = DriverOf(ctx, a.PnpDeviceId);
            var vid = HardwareRules.VendorId(a.PnpDeviceId);
            var isUsb = a.PnpDeviceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
            var isGpuAudio = !isUsb && vid is "10DE" or "1002";
            var codec = HardwareRules.RealtekCodecName(a.PnpDeviceId);

            var name = new[] { device?.Name, a.Name }.FirstOrDefault(n => !HardwareRules.IsGenericDeviceName(n) && !IsGenericAudioName(n))
                       ?? codec ?? (a.Name.Length > 0 ? a.Name : "Звуковое устройство");
            var kind = isUsb ? "USB-АУДИО" : isGpuAudio ? "ЗВУК HDMI / DP" : "ВСТРОЕННЫЙ ЗВУК";
            var key = "audio:" + a.PnpDeviceId;
            var verdict = WuVerdict(key, device, a.PnpDeviceId, wu);
            var actions = verdict.Actions;
            var note = verdict.Note;
            if (note is null)
            {
                if (isGpuAudio) note = "Звук через HDMI / DisplayPort обновляется вместе с драйвером видеокарты.";
                else if (isUsb) note = "USB-аудио работает на стандартном драйвере Windows; программа производителя (если есть) — в разделе «Периферия».";
            }
            if (codec is not null && driver is { IsOem: false } && board is not null)
                actions.Add(PageAction("realtek-audio", "Драйвер Realtek Audio с сайта платы", board.ShortName, board.SupportUrl,
                    "Сейчас работает стандартный драйвер Windows High Definition Audio: звук есть, но без панели Realtek (назначение гнёзд, эффекты). " +
                    $"Драйвер для вашей платы — в разделе поддержки {board.ShortName}.", optional: true, $"САЙТ {board.ShortName.ToUpperInvariant()}"));

            var vendorName = isUsb ? HardwareCatalog.PeripheralByVid(HardwareRules.UsbId(a.PnpDeviceId)?.Vid)?.Name : DeviceNaming.VendorName(a.PnpDeviceId);
            var details = Join(vendorName ?? (a.Manufacturer.StartsWith('(') ? null : a.Manufacturer), DriverText(driver),
                verdict.Status == HwStatus.NotInstalled ? null : WuDetail(wu));
            list.Add(new HwComponent(key, kind, name, details, verdict.Status, actions) { StatusText = verdict.StatusText, Note = note });
        }
        return new HwGroup(HwGroupKind.Audio, "ЗВУК", list);
    }

    private static bool IsGenericAudioName(string? n) =>
        n is not null && (n.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase) || n.Contains("Аудио устройств", StringComparison.OrdinalIgnoreCase));

    // ───────────────────────────── 07 Monitors ─────────────────────────────

    private static HwGroup Monitors(HardwareSnapshot s)
    {
        var list = new List<HwComponent>();
        var i = 0;
        foreach (var d in s.Displays)
        {
            var below = HealthRules.IsBelowMaxRefresh(d.CurrentHz, d.MaxHz);
            var details = Join($"{d.Width}×{d.Height}", $"{d.CurrentHz} Гц", $"максимум {d.MaxHz} Гц при этом разрешении");
            var key = $"display:{i++}:{d.Name}";
            if (below)
            {
                list.Add(new HwComponent(key, "МОНИТОР", d.Name, details, HwStatus.ManualOnly,
                [
                    new HwAction("refresh:" + key, HwActionKind.OpenSettings, HwSource.Manual, $"Включить {d.MaxHz} Гц",
                        $"Сейчас {d.CurrentHz} Гц, а монитор поддерживает {d.MaxHz} Гц при этом разрешении. " +
                        "Параметры → Дисплей → Расширенные параметры дисплея → Частота обновления. Изменение мгновенное, вернуть можно там же.",
                        "Параметры Windows")
                    {
                        Url = "ms-settings:display-advanced",
                        ButtonText = "ПАРАМЕТРЫ ДИСПЛЕЯ",
                    },
                ]));
            }
            else
            {
                list.Add(new HwComponent(key, "МОНИТОР", d.Name, details, HwStatus.Current, [])
                { Note = "Монитор работает на максимальной частоте обновления." });
            }
        }
        return new HwGroup(HwGroupKind.Monitors, "МОНИТОРЫ", list);
    }

    // ───────────────────────────── 08 Peripherals ─────────────────────────────

    private static HwGroup Peripherals(HardwareSnapshot s, WuIndex wu)
    {
        // VID:PID pairs that are real input / audio / camera devices (hubs, storage and radios are not peripherals).
        var byId = s.Devices
            .Select(d => (Device: d, Id: HardwareRules.UsbId(d.DeviceId)))
            .Where(x => x.Id is not null)
            .GroupBy(x => x.Id!.Value)
            .Where(g => g.Any(x => PeripheralClasses.Contains(x.Device.PnpClass, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        var list = new List<HwComponent>();
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vendorGroup in byId.GroupBy(g => g.Key.Vid))
        {
            var vendor = HardwareCatalog.PeripheralByVid(vendorGroup.Key);
            if (vendor is null)
            {
                foreach (var g in vendorGroup) unknown.Add($"{g.Key.Vid}:{g.Key.Pid}");
                continue;
            }

            var names = new List<string>();
            var ids = new List<string>();
            var devices = new List<PnpDevice>();
            foreach (var g in vendorGroup.OrderBy(g => g.Key.Pid, StringComparer.Ordinal))
            {
                ids.Add($"{g.Key.Vid}:{g.Key.Pid}");
                devices.AddRange(g.Select(x => x.Device));
                var best = g.Select(x => x.Device.Name).FirstOrDefault(n => !HardwareRules.IsGenericDeviceName(n));
                if (best is not null && !names.Contains(best, StringComparer.OrdinalIgnoreCase)) names.Add(best);
            }
            var wuMatches = devices.SelectMany(d => wu.Match(d.DeviceId, d.HardwareIds)).DistinctBy(u => u.UpdateId).ToList();

            var problem = devices.FirstOrDefault(d => DeviceProblemCodes.IsReportable(d.ErrorCode, d.Present));
            var softwareApplies = vendor.SoftwareNameFilter is null ||
                                  names.Any(n => n.Contains(vendor.SoftwareNameFilter, StringComparison.OrdinalIgnoreCase));
            var software = softwareApplies ? vendor.Software : [];
            var actions = software.Select(sw => SoftwareAction(sw, s)).OfType<HwAction>().ToList();
            if (wuMatches.Count > 0) actions.Insert(0, WuAction("periph:" + vendor.Vid, wuMatches));
            var anyInstalled = software.Any(sw => sw.IsInstalled(s.Apps));

            HwStatus status;
            string? statusText;
            string? note = null;
            if (problem is not null)
            {
                status = HwStatus.NotInstalled;
                statusText = $"ОШИБКА · КОД {problem.ErrorCode}";
                note = $"{problem.Name}: {DeviceProblemCodes.Describe(problem.ErrorCode).Title}. Подробности — на вкладке «Проблемные устройства».";
            }
            else if (wuMatches.Count > 0)
            {
                status = HwStatus.UpdateAvailable;
                statusText = null;
            }
            else if (anyInstalled)
            {
                status = HwStatus.Installed;
                statusText = "ПО УСТАНОВЛЕНО";
            }
            else
            {
                status = HwStatus.Info;
                statusText = "ДРАЙВЕР WINDOWS";
                note = software.Count == 0
                    ? vendor.NoSoftwareNote
                    : "Устройства уже работают на драйверах Windows. Программа производителя нужна только для настройки кнопок, подсветки и прошивок — ставьте её по желанию.";
            }

            var count = vendorGroup.Count();
            var title = names.Count > 0 ? string.Join(" · ", names.Take(3)) : $"{count} {HardwareRules.Plural(count, "устройство", "устройства", "устройств")} {vendor.Name}";
            list.Add(new HwComponent("periph:" + vendor.Vid, vendor.Name.ToUpperInvariant(), title,
                Join("USB " + string.Join(", ", ids.Take(4)) + (ids.Count > 4 ? $" и ещё {ids.Count - 4}" : ""), "драйверы Windows (HID / USB-аудио)"),
                status, actions)
            { StatusText = statusText, Note = note });
        }

        var footer = unknown.Count == 0 ? null
            : $"Ещё {unknown.Count} {HardwareRules.Plural(unknown.Count, "устройство", "устройства", "устройств")} других производителей " +
              $"{HardwareRules.Plural(unknown.Count, "работает", "работают", "работают")} на драйверах Windows — отдельные программы им не нужны.";
        return new HwGroup(HwGroupKind.Peripherals, "ПЕРИФЕРИЯ", list) { Footer = footer };
    }

    // ───────────────────────────── 09 Game runtimes ─────────────────────────────

    private static HwGroup Runtimes(HardwareSnapshot s)
    {
        var r = s.Runtimes;
        var list = new List<HwComponent>
        {
            VcComponent(s, HardwareCatalog.VcX64, "x64", r.VcX64),
            VcComponent(s, HardwareCatalog.VcX86, "x86", r.VcX86),
        };

        // DirectX End-User Runtime (June 2010): a frozen runtime — installed means up to date.
        var dxEntry = HardwareCatalog.Runtime(HardwareCatalog.DirectX);
        list.Add(r.DirectX
            ? new HwComponent("rt:dx", "DIRECTX 9–11", dxEntry?.Name ?? "DirectX End-User Runtime", "d3dx9_43.dll и xaudio2_7.dll на месте  ·  июнь 2010 — последняя версия",
                HwStatus.Current, [])
            : new HwComponent("rt:dx", "DIRECTX 9–11", dxEntry?.Name ?? "DirectX End-User Runtime", "нет d3dx9_43.dll / xaudio2_7.dll",
                HwStatus.NotInstalled,
                [
                    RuntimeAction(HardwareCatalog.DirectX, s,
                        "Старым играм на DirectX 9–11 нужны d3dx9, xinput 1.3 и XAudio 2.7 — в Windows их нет. Без них игра пишет «d3dx9_43.dll не найден».",
                        preselect: true, optional: false),
                ]));

        // .NET Desktop Runtime
        var majors = r.DesktopRuntimes.OrderByDescending(kv => kv.Key).ToList();
        var lts = HardwareCatalog.DesktopRuntimeLts;
        var ltsId = HardwareCatalog.DesktopRuntimePrefix + lts;
        if (majors.Count == 0)
        {
            list.Add(new HwComponent("rt:net", ".NET DESKTOP", $".NET Desktop Runtime {lts}", "не установлен ни один .NET Desktop Runtime",
                HwStatus.NotInstalled,
                [
                    RuntimeAction(ltsId, s, "Нужен программам на .NET: лаунчерам модов, утилитам, части лаунчеров игр. Без него они просят скачать .NET при запуске.",
                        preselect: false, optional: false),
                ]));
        }
        else
        {
            var actions = new List<HwAction>();
            if (!r.DesktopRuntimes.ContainsKey(lts))
                actions.Add(RuntimeAction(ltsId, s, $"Текущая версия с долгой поддержкой. Нужна, только если программа просит .NET {lts}: среды разных версий не заменяют друг друга.",
                    preselect: false, optional: true));
            list.Add(new HwComponent("rt:net", ".NET DESKTOP", ".NET Desktop Runtime",
                "установлены: " + string.Join(", ", majors.Select(kv => kv.Value.Length > 0 ? kv.Value : kv.Key + ".x")),
                HwStatus.Installed, actions));
        }
        return new HwGroup(HwGroupKind.Runtimes, "БИБЛИОТЕКИ ДЛЯ ИГР", list);
    }

    private static HwComponent VcComponent(HardwareSnapshot s, string id, string arch, string? version)
    {
        var entry = HardwareCatalog.Runtime(id);
        var name = entry?.Name ?? $"Visual C++ 2015-2022 ({arch})";
        var kind = $"VISUAL C++ {arch.ToUpperInvariant()}";
        if (version is null)
        {
            return new HwComponent("rt:vc" + arch, kind, name, "не установлен", HwStatus.NotInstalled,
            [
                RuntimeAction(id, s, $"Без неё многие игры и лаунчеры не запускаются (ошибка «MSVCP140.dll / VCRUNTIME140.dll не найден»)." +
                    (arch == "x86" ? " Версия x86 нужна 32-битным играм даже в 64-битной Windows." : ""), preselect: true, optional: false),
            ]);
        }
        if (version.Length == 0)
            return new HwComponent("rt:vc" + arch, kind, name, "установлен, версия неизвестна", HwStatus.Installed, []);
        if (HardwareRules.VcIsOutdated(version))
        {
            return new HwComponent("rt:vc" + arch, kind, name, $"версия {version}", HwStatus.UpdateAvailable,
            [
                RuntimeAction(id, s, $"Установлена {version}. Программы, собранные Visual Studio 2022 17.10 и новее, требуют {HardwareRules.VcMinimum.Major}.{HardwareRules.VcMinimum.Minor} или новее — со старой версией они могут вылетать при запуске. Обновление ставится поверх.",
                    preselect: false, optional: false),
            ]);
        }
        return new HwComponent("rt:vc" + arch, kind, name, $"версия {version}", HwStatus.Installed, []);
    }
}
