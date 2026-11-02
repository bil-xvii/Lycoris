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
    public string CodeText => Loc.Tf("КОД {0}", Code);
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
            new(1, Loc.T("Устройство настроено неправильно"),
                Loc.T("Windows не смогла правильно настроить устройство — обычно из-за неподходящего или неполного драйвера."),
                Loc.T("Обновите драйвер: найдите его в Windows Update или на сайте производителя."), ProblemRemedy.UpdateDriver),
            new(3, Loc.T("Драйвер повреждён или не хватает памяти"),
                Loc.T("Драйвер устройства, возможно, повреждён, либо системе не хватило памяти или других ресурсов."),
                Loc.T("Переустановите драйвер. Если ошибка повторяется — закройте лишние программы и перезагрузите ПК."), ProblemRemedy.ReinstallDriver),
            new(10, Loc.T("Устройство не может запуститься"),
                Loc.T("Драйвер загрузился, но устройство не запустилось. Частая причина — неподходящая версия драйвера."),
                Loc.T("Обновите драйвер. Если проблема появилась после обновления — откатите драйвер в диспетчере устройств."), ProblemRemedy.UpdateDriver),
            new(12, Loc.T("Недостаточно свободных ресурсов"),
                Loc.T("Устройству не хватило системных ресурсов (прерываний, памяти) — они заняты другими устройствами."),
                Loc.T("Проверьте настройки BIOS/UEFI: возможно, нужно включить или перераспределить ресурсы."), ProblemRemedy.Firmware),
            new(14, Loc.T("Требуется перезагрузка"),
                Loc.T("Устройство заработает только после перезагрузки компьютера."),
                Loc.T("Перезагрузите компьютер."), ProblemRemedy.Reboot),
            new(18, Loc.T("Нужно переустановить драйверы"),
                Loc.T("Драйверы устройства нужно установить заново."),
                Loc.T("Переустановите драйвер: удалите устройство в диспетчере устройств и обновите конфигурацию оборудования."), ProblemRemedy.ReinstallDriver),
            new(19, Loc.T("Повреждены сведения о конфигурации в реестре"),
                Loc.T("Сведения о настройке устройства в реестре неполные или повреждены."),
                Loc.T("Удалите устройство в диспетчере устройств и обновите конфигурацию оборудования. Реестр не «чистите» сторонними программами."), ProblemRemedy.ReinstallDriver),
            new(21, Loc.T("Устройство удаляется"),
                Loc.T("Windows сейчас удаляет это устройство."),
                Loc.T("Перезагрузите компьютер."), ProblemRemedy.Reboot),
            new(22, Loc.T("Устройство отключено"),
                Loc.T("Устройство отключено вручную — в диспетчере устройств, программой или политикой."),
                Loc.T("Если вы не отключали его намеренно — включите устройство. Включение можно отменить в «Восстановлении»."), ProblemRemedy.EnableDevice),
            new(24, Loc.T("Устройство отсутствует или установлены не все драйверы"),
                Loc.T("Устройство не найдено, работает неправильно или для него установлены не все драйверы."),
                Loc.T("Переподключите устройство и обновите драйвер."), ProblemRemedy.UpdateDriver),
            new(28, Loc.T("Драйвер не установлен"),
                Loc.T("Для устройства не установлен ни один драйвер — оно не работает."),
                Loc.T("Найдите драйвер в Windows Update. Если там нет — скачайте его с сайта производителя платы или ноутбука по ID оборудования."), ProblemRemedy.InstallDriver),
            new(29, Loc.T("Отключено в прошивке (BIOS/UEFI)"),
                Loc.T("Устройство выключено в прошивке компьютера, поэтому ему не выделены ресурсы."),
                Loc.T("Включите устройство в настройках BIOS/UEFI."), ProblemRemedy.Firmware),
            new(31, Loc.T("Windows не может загрузить драйвер"),
                Loc.T("Устройство работает неправильно: Windows не может загрузить нужные драйверы."),
                Loc.T("Переустановите драйвер устройства."), ProblemRemedy.ReinstallDriver),
            new(32, Loc.T("Служба драйвера отключена"),
                Loc.T("Служба драйвера этого устройства отключена (тип запуска «Отключена»). Часто это след «твикера»."),
                Loc.T("Верните исходный тип запуска службы драйвера или переустановите драйвер."), ProblemRemedy.ReinstallDriver),
            new(33, Loc.T("Ресурсы устройства не определены"),
                Loc.T("Windows не может определить, какие ресурсы нужны устройству."),
                Loc.T("Обновите BIOS/UEFI с сайта производителя платы. Если не помогает — возможна аппаратная неисправность."), ProblemRemedy.Firmware),
            new(34, Loc.T("Нужна ручная настройка"),
                Loc.T("Windows не может определить параметры устройства автоматически."),
                Loc.T("Обновите BIOS/UEFI с сайта производителя платы или настройте устройство по его документации."), ProblemRemedy.Firmware),
            new(35, Loc.T("В прошивке нет данных об устройстве"),
                Loc.T("Прошивка компьютера не содержит сведений, нужных для настройки устройства."),
                Loc.T("Обновите BIOS/UEFI с сайта производителя платы."), ProblemRemedy.Firmware),
            new(37, Loc.T("Ошибка инициализации драйвера"),
                Loc.T("Windows не смогла инициализировать драйвер устройства."),
                Loc.T("Переустановите драйвер."), ProblemRemedy.ReinstallDriver),
            new(38, Loc.T("Прежний драйвер ещё в памяти"),
                Loc.T("Новый драйвер не загружен, потому что предыдущий экземпляр драйвера ещё в памяти."),
                Loc.T("Перезагрузите компьютер."), ProblemRemedy.Reboot),
            new(39, Loc.T("Драйвер повреждён или отсутствует"),
                Loc.T("Windows не может загрузить драйвер: файл повреждён или отсутствует."),
                Loc.T("Переустановите драйвер."), ProblemRemedy.ReinstallDriver),
            new(40, Loc.T("Нет ключа службы драйвера"),
                Loc.T("Сведения о службе драйвера в реестре отсутствуют или записаны неправильно."),
                Loc.T("Переустановите драйвер."), ProblemRemedy.ReinstallDriver),
            new(41, Loc.T("Устройство не найдено"),
                Loc.T("Драйвер загружен, но само устройство не найдено."),
                Loc.T("Переустановите драйвер; проверьте подключение устройства."), ProblemRemedy.ReinstallDriver),
            new(42, Loc.T("Дубликат устройства"),
                Loc.T("В системе уже работает такое же устройство, поэтому драйвер не загружен."),
                Loc.T("Переустановите драйвер и перезагрузите компьютер."), ProblemRemedy.ReinstallDriver),
            new(43, Loc.T("Устройство сообщило о проблеме и остановлено"),
                Loc.T("Windows остановила устройство, потому что оно сообщило о сбое. Причина — драйвер, питание или само железо; частый код для видеокарт и USB."),
                Loc.T("Обновите или откатите драйвер, переподключите устройство (для USB — в другой порт), проверьте питание."), ProblemRemedy.Hardware),
            new(44, Loc.T("Выключено приложением или службой"),
                Loc.T("Устройство выключила программа или служба."),
                Loc.T("Проверьте программы, которые управляют этим устройством, и перезагрузите компьютер."), ProblemRemedy.CheckSoftware),
            new(45, Loc.T("Устройство сейчас не подключено"),
                Loc.T("Устройство подключалось раньше, но сейчас отсутствует. Это не ошибка."),
                Loc.T("Действий не требуется."), ProblemRemedy.Wait),
            new(47, Loc.T("Подготовлено к безопасному извлечению"),
                Loc.T("Устройство подготовлено к извлечению, но всё ещё подключено."),
                Loc.T("Отключите и снова подключите устройство или перезагрузите компьютер."), ProblemRemedy.Reboot),
            new(48, Loc.T("Драйвер заблокирован из-за проблем совместимости"),
                Loc.T("Windows заблокировала запуск драйвера, потому что он известен проблемами с этой версией системы."),
                Loc.T("Не обходите блокировку. Дождитесь новой версии драйвера от производителя и установите её."), ProblemRemedy.DoNotBypass),
            new(52, Loc.T("Не удаётся проверить цифровую подпись драйвера"),
                Loc.T("У драйвера нет действительной цифровой подписи — Windows не запускает его ради безопасности."),
                Loc.T("Не отключайте проверку подписи. Установите подписанный драйвер из Windows Update или с сайта производителя."), ProblemRemedy.DoNotBypass),
            new(54, Loc.T("Устройство перезапускается"),
                Loc.T("Устройство дало сбой и сейчас перезапускается."),
                Loc.T("Подождите. Если код не исчезает — перезагрузите компьютер."), ProblemRemedy.Wait),
            new(56, Loc.T("Windows ещё настраивает устройство"),
                Loc.T("Windows ещё настраивает класс этого устройства."),
                Loc.T("Подождите. Если код не исчезает — перезагрузите компьютер."), ProblemRemedy.Wait),
        };
        return list.ToDictionary(i => i.Code);
    }

    public static IReadOnlyCollection<int> KnownCodes => Map.Keys;

    public static DeviceProblemInfo Describe(int code) =>
        Map.TryGetValue(code, out var info)
            ? info
            : new DeviceProblemInfo(code, Loc.Tf("Неизвестная ошибка устройства (код {0})", code),
                Loc.T("Windows сообщает о проблеме с устройством, но этот код встречается редко."),
                Loc.T("Откройте диспетчер устройств и посмотрите подробности в свойствах устройства."), ProblemRemedy.Unknown);

    /// <summary>
    /// Whether the device belongs in the problem list. Devices that are not present (Present = false)
    /// and code 45 ("not connected") are not errors (research §2).
    /// </summary>
    public static bool IsReportable(int code, bool? present) =>
        code != 0 && code != NotConnected && present != false;
}
