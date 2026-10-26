namespace Monolith.Modules.Drivers;

/// <summary>What the user should do about a device problem code.</summary>
internal enum ProblemRemedy
{
    UpdateDriver,
    ReinstallDriver,
    Reboot,
    EnableDevice,
    Firmware,
    CheckSoftware,
    InstallDriver,
    Hardware,
    DoNotBypass,
    Wait,
    Unknown,
}

/// <summary>A Device Manager problem code (CM_PROB_*) explained in plain Russian.</summary>
internal sealed record DeviceProblemInfo(int Code, string Title, string Explanation, string Action, ProblemRemedy Remedy)
{
    public string CodeText => $"КОД {Code}";
}

/// <summary>
/// Device Manager error codes (ConfigManagerErrorCode) → Russian text and suggested action.
/// Source: docs/research/drivers.md §2 (from learn.microsoft.com "device-manager-error-messages").
/// </summary>
internal static class DeviceProblemCodes
{
    /// <summary>"Currently, this hardware device is not connected" — not an error (research: don't list it).</summary>
    public const int NotConnected = 45;
    public const int Disabled = 22;
    public const int DriverNotInstalled = 28;

    private static readonly Dictionary<int, DeviceProblemInfo> Map = Build();

    private static Dictionary<int, DeviceProblemInfo> Build()
    {
        var list = new List<DeviceProblemInfo>
        {
            new(1, "Устройство настроено неправильно",
                "Windows не смогла правильно настроить устройство — обычно из-за неподходящего или неполного драйвера.",
                "Обновите драйвер: найдите его в Windows Update или на сайте производителя.", ProblemRemedy.UpdateDriver),
            new(3, "Драйвер повреждён или не хватает памяти",
                "Драйвер устройства, возможно, повреждён, либо системе не хватило памяти или других ресурсов.",
                "Переустановите драйвер. Если ошибка повторяется — закройте лишние программы и перезагрузите ПК.", ProblemRemedy.ReinstallDriver),
            new(10, "Устройство не может запуститься",
                "Драйвер загрузился, но устройство не запустилось. Частая причина — неподходящая версия драйвера.",
                "Обновите драйвер. Если проблема появилась после обновления — откатите драйвер в диспетчере устройств.", ProblemRemedy.UpdateDriver),
            new(12, "Недостаточно свободных ресурсов",
                "Устройству не хватило системных ресурсов (прерываний, памяти) — они заняты другими устройствами.",
                "Проверьте настройки BIOS/UEFI: возможно, нужно включить или перераспределить ресурсы.", ProblemRemedy.Firmware),
            new(14, "Требуется перезагрузка",
                "Устройство заработает только после перезагрузки компьютера.",
                "Перезагрузите компьютер.", ProblemRemedy.Reboot),
            new(18, "Нужно переустановить драйверы",
                "Драйверы устройства нужно установить заново.",
                "Переустановите драйвер: удалите устройство в диспетчере устройств и обновите конфигурацию оборудования.", ProblemRemedy.ReinstallDriver),
            new(19, "Повреждены сведения о конфигурации в реестре",
                "Сведения о настройке устройства в реестре неполные или повреждены.",
                "Удалите устройство в диспетчере устройств и обновите конфигурацию оборудования. Реестр не «чистите» сторонними программами.", ProblemRemedy.ReinstallDriver),
            new(21, "Устройство удаляется",
                "Windows сейчас удаляет это устройство.",
                "Перезагрузите компьютер.", ProblemRemedy.Reboot),
            new(22, "Устройство отключено",
                "Устройство отключено вручную — в диспетчере устройств, программой или политикой.",
                "Если вы не отключали его намеренно — включите устройство. Включение можно отменить в «Восстановлении».", ProblemRemedy.EnableDevice),
            new(24, "Устройство отсутствует или установлены не все драйверы",
                "Устройство не найдено, работает неправильно или для него установлены не все драйверы.",
                "Переподключите устройство и обновите драйвер.", ProblemRemedy.UpdateDriver),
            new(28, "Драйвер не установлен",
                "Для устройства не установлен ни один драйвер — оно не работает.",
                "Найдите драйвер в Windows Update. Если там нет — скачайте его с сайта производителя платы или ноутбука по ID оборудования.", ProblemRemedy.InstallDriver),
            new(29, "Отключено в прошивке (BIOS/UEFI)",
                "Устройство выключено в прошивке компьютера, поэтому ему не выделены ресурсы.",
                "Включите устройство в настройках BIOS/UEFI.", ProblemRemedy.Firmware),
            new(31, "Windows не может загрузить драйвер",
                "Устройство работает неправильно: Windows не может загрузить нужные драйверы.",
                "Переустановите драйвер устройства.", ProblemRemedy.ReinstallDriver),
            new(32, "Служба драйвера отключена",
                "Служба драйвера этого устройства отключена (тип запуска «Отключена»). Часто это след «твикера».",
                "Верните исходный тип запуска службы драйвера или переустановите драйвер.", ProblemRemedy.ReinstallDriver),
            new(33, "Ресурсы устройства не определены",
                "Windows не может определить, какие ресурсы нужны устройству.",
                "Обновите BIOS/UEFI с сайта производителя платы. Если не помогает — возможна аппаратная неисправность.", ProblemRemedy.Firmware),
            new(34, "Нужна ручная настройка",
                "Windows не может определить параметры устройства автоматически.",
                "Обновите BIOS/UEFI с сайта производителя платы или настройте устройство по его документации.", ProblemRemedy.Firmware),
            new(35, "В прошивке нет данных об устройстве",
                "Прошивка компьютера не содержит сведений, нужных для настройки устройства.",
                "Обновите BIOS/UEFI с сайта производителя платы.", ProblemRemedy.Firmware),
            new(37, "Ошибка инициализации драйвера",
                "Windows не смогла инициализировать драйвер устройства.",
                "Переустановите драйвер.", ProblemRemedy.ReinstallDriver),
            new(38, "Прежний драйвер ещё в памяти",
                "Новый драйвер не загружен, потому что предыдущий экземпляр драйвера ещё в памяти.",
                "Перезагрузите компьютер.", ProblemRemedy.Reboot),
            new(39, "Драйвер повреждён или отсутствует",
                "Windows не может загрузить драйвер: файл повреждён или отсутствует.",
                "Переустановите драйвер.", ProblemRemedy.ReinstallDriver),
            new(40, "Нет ключа службы драйвера",
                "Сведения о службе драйвера в реестре отсутствуют или записаны неправильно.",
                "Переустановите драйвер.", ProblemRemedy.ReinstallDriver),
            new(41, "Устройство не найдено",
                "Драйвер загружен, но само устройство не найдено.",
                "Переустановите драйвер; проверьте подключение устройства.", ProblemRemedy.ReinstallDriver),
            new(42, "Дубликат устройства",
                "В системе уже работает такое же устройство, поэтому драйвер не загружен.",
                "Переустановите драйвер и перезагрузите компьютер.", ProblemRemedy.ReinstallDriver),
            new(43, "Устройство сообщило о проблеме и остановлено",
                "Windows остановила устройство, потому что оно сообщило о сбое. Причина — драйвер, питание или само железо; частый код для видеокарт и USB.",
                "Обновите или откатите драйвер, переподключите устройство (для USB — в другой порт), проверьте питание.", ProblemRemedy.Hardware),
            new(44, "Выключено приложением или службой",
                "Устройство выключила программа или служба.",
                "Проверьте программы, которые управляют этим устройством, и перезагрузите компьютер.", ProblemRemedy.CheckSoftware),
            new(45, "Устройство сейчас не подключено",
                "Устройство подключалось раньше, но сейчас отсутствует. Это не ошибка.",
                "Действий не требуется.", ProblemRemedy.Wait),
            new(47, "Подготовлено к безопасному извлечению",
                "Устройство подготовлено к извлечению, но всё ещё подключено.",
                "Отключите и снова подключите устройство или перезагрузите компьютер.", ProblemRemedy.Reboot),
            new(48, "Драйвер заблокирован из-за проблем совместимости",
                "Windows заблокировала запуск драйвера, потому что он известен проблемами с этой версией системы.",
                "Не обходите блокировку. Дождитесь новой версии драйвера от производителя и установите её.", ProblemRemedy.DoNotBypass),
            new(52, "Не удаётся проверить цифровую подпись драйвера",
                "У драйвера нет действительной цифровой подписи — Windows не запускает его ради безопасности.",
                "Не отключайте проверку подписи. Установите подписанный драйвер из Windows Update или с сайта производителя.", ProblemRemedy.DoNotBypass),
            new(54, "Устройство перезапускается",
                "Устройство дало сбой и сейчас перезапускается.",
                "Подождите. Если код не исчезает — перезагрузите компьютер.", ProblemRemedy.Wait),
            new(56, "Windows ещё настраивает устройство",
                "Windows ещё настраивает класс этого устройства.",
                "Подождите. Если код не исчезает — перезагрузите компьютер.", ProblemRemedy.Wait),
        };
        return list.ToDictionary(i => i.Code);
    }

    public static IReadOnlyCollection<int> KnownCodes => Map.Keys;

    public static DeviceProblemInfo Describe(int code) =>
        Map.TryGetValue(code, out var info)
            ? info
            : new DeviceProblemInfo(code, $"Неизвестная ошибка устройства (код {code})",
                "Windows сообщает о проблеме с устройством, но этот код встречается редко.",
                "Откройте диспетчер устройств и посмотрите подробности в свойствах устройства.", ProblemRemedy.Unknown);

    /// <summary>
    /// Whether the device belongs in the problem list. Devices that are not present (Present = false)
    /// and code 45 ("not connected") are not errors (research §2).
    /// </summary>
    public static bool IsReportable(int code, bool? present) =>
        code != 0 && code != NotConnected && present != false;
}
