namespace Monolith.Modules.Drivers;

internal enum ChipsetPlatform { Amd, Intel, Other }

internal enum ComponentState
{
    /// <summary>Vendor driver (oemNN.inf) installed.</summary>
    Installed,
    /// <summary>Device works on the driver that ships with Windows.</summary>
    Inbox,
    /// <summary>Device found with a problem code (28 = no driver).</summary>
    Problem,
    /// <summary>Device not found on this PC.</summary>
    Absent,
}

internal sealed record ChipsetComponent(
    string Title,
    string Description,
    ComponentState State,
    bool Required,
    string? Version,
    DateTime? Date,
    string? InfName,
    int? ProblemCode);

internal sealed record ChipsetReport(
    ChipsetPlatform Platform,
    string CpuName,
    bool? DriverOk,
    string Verdict,
    string Recommendation,
    IReadOnlyList<ChipsetComponent> Components,
    string? Note,
    string? PackageText);

/// <summary>
/// Chipset driver detection by PnP hardware ids (docs/research/drivers.md §4). AmdPPM / cpu.inf is the inbox
/// "AMD Processor" driver and is never used as evidence. Pure: takes inventory rows, returns a report.
/// </summary>
internal static class ChipsetAdvisor
{
    private sealed record AmdPart(string Title, string Description, string[] HwidPrefixes, string? NameKeyword, Func<string, bool> Required, bool ShowWhenAbsent);

    private static readonly AmdPart[] AmdParts =
    [
        new("PPM Provisioning File", "Профили питания ядер Ryzen для Windows.",
            [@"ACPI\VEN_AMDI&DEV_0052", @"ACPI\AMDI0052"], null, _ => true, true),
        new("3D V-Cache Performance Optimizer", "Переводит игры на ядра с 3D-кэшем (X3D).",
            [@"ACPI\VEN_AMDI&DEV_0101", @"ACPI\AMDI0101"], null, IsX3D, false),
        new("Application Compatibility Database", "База совместимости приложений AMD.",
            [@"ACPI\VEN_AMDI&DEV_0204", @"ACPI\AMDI0204"], null, _ => false, false),
        new("SMBus", "Шина управления: датчики и память.",
            [@"PCI\VEN_1022&DEV_790B"], "SMBus", _ => false, false),
        new("PSP", "Процессор безопасности платформы (fTPM).",
            [@"PCI\VEN_1022&DEV_1649"], "PSP", _ => false, false),
        new("GPIO", "Линии ввода-вывода платформы.",
            [@"ACPI\VEN_AMDI&DEV_F031", @"ACPI\AMDIF031"], "GPIO", _ => false, false),
        new("I2C", "Шина I2C: тачпады и датчики.",
            [@"ACPI\VEN_AMDI&DEV_0010", @"ACPI\AMDI0010"], "I2C", _ => false, false),
    ];

    private static readonly string[] DualCcdX3D = ["7900X3D", "7950X3D", "9900X3D", "9950X3D"];

    public static bool IsX3D(string cpuName) => cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase);

    /// <summary>Two-CCD X3D parts, where the optimizer parks the non-cache CCD for games (research §4).</summary>
    public static bool IsDualCcdX3D(string cpuName) =>
        DualCcdX3D.Any(m => cpuName.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static ChipsetReport Analyze(string cpuName, bool cpuIsAmd, bool cpuIsIntel,
        IReadOnlyList<SignedDriverInfo> drivers, IReadOnlyList<ProblemDeviceInfo> problems, string? amdPackageVersion)
    {
        if (cpuIsAmd) return AnalyzeAmd(cpuName, drivers, problems, amdPackageVersion);
        if (cpuIsIntel) return AnalyzeIntel(cpuName, drivers, problems);
        return new ChipsetReport(ChipsetPlatform.Other, cpuName, null, "ПЛАТФОРМА НЕ ОПРЕДЕЛЕНА",
            "Драйверы чипсета для этой платформы приходят через Windows Update или с сайта производителя платы.",
            [], null, null);
    }

    private static bool IsAmdProvider(SignedDriverInfo d) =>
        d.Provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
        d.Provider.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
        d.DeviceName.StartsWith("AMD", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string? id, string[] prefixes) =>
        !string.IsNullOrEmpty(id) && prefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static ChipsetReport AnalyzeAmd(string cpuName, IReadOnlyList<SignedDriverInfo> drivers,
        IReadOnlyList<ProblemDeviceInfo> problems, string? packageVersion)
    {
        var components = new List<ChipsetComponent>();
        var requiredMissing = new List<string>();
        var anyRequiredFound = false;

        foreach (var part in AmdParts)
        {
            var required = part.Required(cpuName);
            var problem = problems.FirstOrDefault(p => Matches(p.DeviceId, part.HwidPrefixes) || p.HardwareIds.Any(h => Matches(h, part.HwidPrefixes)));
            var driver = drivers.FirstOrDefault(d => Matches(d.HardwareId, part.HwidPrefixes) || Matches(d.DeviceId, part.HwidPrefixes))
                         ?? (part.NameKeyword is null ? null : drivers.FirstOrDefault(d =>
                             d.DeviceName.Contains(part.NameKeyword, StringComparison.OrdinalIgnoreCase) && IsAmdProvider(d)));

            ComponentState state;
            if (problem is not null) state = ComponentState.Problem;
            else if (driver is null) state = ComponentState.Absent;
            else state = driver.IsOem ? ComponentState.Installed : ComponentState.Inbox;

            if (state == ComponentState.Absent && !part.ShowWhenAbsent && !required) continue;

            if (required)
            {
                if (state != ComponentState.Absent) anyRequiredFound = true;
                if (state != ComponentState.Installed) requiredMissing.Add(part.Title);
            }

            components.Add(new ChipsetComponent(part.Title, part.Description, state, required,
                driver?.Version, driver?.Date, driver?.InfName, problem?.Code));
        }

        // Required device present without a vendor INF (or with a problem code) → missing.
        // Required devices not enumerated at all → we cannot tell for sure.
        bool? ok = requiredMissing.Count == 0 ? true : anyRequiredFound ? false : null;

        string verdict, recommendation;
        if (ok == true)
        {
            verdict = "ДРАЙВЕР ЧИПСЕТА УСТАНОВЛЕН";
            recommendation = "Ключевые компоненты AMD на месте. Обновляйте пакет чипсета с amd.com (раздел «Chipsets» → ваш сокет) или получайте его через Windows Update — твики не нужны.";
        }
        else if (ok == false)
        {
            verdict = "УСТАНОВИТЕ ДРАЙВЕР ЧИПСЕТА AMD";
            recommendation = $"Не установлено: {string.Join(", ", requiredMissing)}. Скачайте пакет AMD Chipset Software на amd.com (раздел «Chipsets» → ваш сокет) или найдите драйверы в Windows Update.";
        }
        else
        {
            verdict = "СОСТОЯНИЕ НЕ ОПРЕДЕЛЕНО";
            recommendation = "Устройства чипсета AMD не найдены в списке драйверов. Проверьте Windows Update или установите пакет AMD Chipset Software с amd.com.";
        }

        string? note = null;
        if (IsDualCcdX3D(cpuName))
            note = "Двухблочный X3D: оптимизатор V-Cache переводит игры на блок ядер с кэшем. Для этого нужны драйвер чипсета, включённые игровой режим Windows («Параметры → Игры») и Xbox Game Bar — не отключайте их.";
        else if (IsX3D(cpuName))
            note = "На X3D с одним блоком ядер весь кэш доступен всем ядрам, поэтому оптимизатору, по всей видимости, почти нечего делать. Он безвреден — не отключайте его службы.";

        var package = string.IsNullOrWhiteSpace(packageVersion)
            ? "Пакет «AMD Chipset Software» не найден среди программ — компоненты получены через Windows Update или установлены по отдельности."
            : $"Установлен пакет «AMD Chipset Software» {packageVersion}.";

        return new ChipsetReport(ChipsetPlatform.Amd, cpuName, ok, verdict, recommendation, components, note, package);
    }

    private static ChipsetReport AnalyzeIntel(string cpuName, IReadOnlyList<SignedDriverInfo> drivers, IReadOnlyList<ProblemDeviceInfo> problems)
    {
        var intelProblems = problems.Where(p => p.HardwareIds.Any(h => h.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase))).ToList();
        var system = drivers
            .Where(d => d.HardwareId.StartsWith(@"PCI\VEN_8086", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(d.DeviceClass, "System", StringComparison.OrdinalIgnoreCase))
            .GroupBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(d => d.IsOem)
            .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .Select(d => new ChipsetComponent(d.DeviceName, DeviceNaming.ClassName(d.DeviceClass),
                d.IsOem ? ComponentState.Installed : ComponentState.Inbox, false, d.Version, d.Date, d.InfName, null))
            .ToList();
        foreach (var p in intelProblems.Take(3))
            system.Insert(0, new ChipsetComponent(p.Name, DeviceProblemCodes.Describe(p.Code).Title, ComponentState.Problem, true, null, null, null, p.Code));

        var ok = intelProblems.Count == 0;
        return new ChipsetReport(ChipsetPlatform.Intel, cpuName, ok,
            ok ? "УСТРОЙСТВА ПЛАТФОРМЫ РАБОТАЮТ" : "ЕСТЬ ПРОБЛЕМЫ С УСТРОЙСТВАМИ INTEL",
            "Драйверы чипсета Intel поставляются через Windows Update. Проверить их и драйверы видеоядра, Wi-Fi и Bluetooth Intel поможет официальный Intel Driver & Support Assistant.",
            system, null, null);
    }
}
