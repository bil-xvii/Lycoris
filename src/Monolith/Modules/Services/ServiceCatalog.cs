using System.ServiceProcess;
using Monolith.Tweaks;

namespace Monolith.Modules.Services;

/// <summary>Sections of the РЕКОМЕНДАЦИИ tab, in display order.</summary>
public enum ServiceGroup { Telemetry, Xbox, Printing, RemoteAccess, Devices, Other }

/// <summary>A runtime check that can make a curated entry unavailable on this PC.</summary>
public enum ServiceGate
{
    None,
    /// <summary>Blocked when the PC is enrolled in Windows Insider (it needs optional diagnostic data and wisvc).</summary>
    NotInsider,
    /// <summary>Blocked when any Bluetooth adapter is present (headsets, controllers, mice depend on it).</summary>
    NoBluetoothAdapter,
}

/// <summary>
/// One service from the curated allowlist (docs/research/services.md §1).
/// MONOLITH changes only the start type: it never stops a running service and never touches triggers.
/// </summary>
public sealed record CuratedService
{
    public required string Name { get; init; }
    /// <summary>Short Russian title shown as the row heading.</summary>
    public required string Title { get; init; }
    /// <summary>What the service does, in plain Russian.</summary>
    public required string Description { get; init; }
    /// <summary>When the change is appropriate ("если вы не пользуетесь принтером").</summary>
    public required string Condition { get; init; }
    /// <summary>What stops working after the change (the risk, in plain words).</summary>
    public required string Impact { get; init; }
    public required ServiceGroup Group { get; init; }
    /// <summary>The start type MONOLITH sets: Manual (preferred) or Disabled where research says so.</summary>
    public required ServiceStartMode Target { get; init; }
    /// <summary>Profiles in which the entry is recommended.</summary>
    public ProfileSet Profiles { get; init; }
    /// <summary>
    /// "D?" / "M?" in the research: only after the user confirms the condition. Never pre-selected by a profile.
    /// </summary>
    public bool Conditional { get; init; }
    public TweakRisk Risk { get; init; } = TweakRisk.Safe;
    /// <summary>Another curated service that must be changed together with this one (PrintNotify → Spooler).</summary>
    public string? Companion { get; init; }
    public ServiceGate Gate { get; init; }

    /// <summary>True when a profile pre-selects this entry.</summary>
    public bool IsPreselectedIn(ProfileSet profile) => !Conditional && profile != ProfileSet.None && (Profiles & profile) == profile;
}

/// <summary>A telemetry scheduled task from research §4.1. MONOLITH only disables it (and re-enables on revert).</summary>
public sealed record CuratedTask
{
    /// <summary>Full task path ("\Microsoft\Windows\…\Name"). For <see cref="IsPrefix"/> entries: folder + name prefix.</summary>
    public required string Path { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Impact { get; init; }
    public ProfileSet Profiles { get; init; }
    public bool Conditional { get; init; }
    public TweakRisk Risk { get; init; } = TweakRisk.Safe;
    /// <summary>The last path segment is a name prefix (version/GUID-suffixed tasks such as NvTmRep_CrashReport1_{…}).</summary>
    public bool IsPrefix { get; init; }

    public bool IsPreselectedIn(ProfileSet profile) => !Conditional && profile != ProfileSet.None && (Profiles & profile) == profile;
}

/// <summary>
/// The curated allowlist. Source: docs/research/services.md (verified on Windows 11 build 26300).
/// Deliberately NOT included (research: keep / never / do not offer / debated): SysMain, iphlpsvc, edgeupdate*,
/// Google updaters, StorSvc (protected by Core), WerSvc (prefer the WER policy), lfsvc (use the Settings toggle),
/// smart-card services (Рутокен/JaCarta), WbioSrvc (Windows Hello), SharedAccess (Hyper-V Default Switch),
/// Remote Desktop services (use the Settings toggle), vmic* (zero gain), Compatibility Appraiser tasks (feature updates).
/// </summary>
public static class ServiceCatalog
{
    private const ProfileSet RG = ProfileSet.Recommended | ProfileSet.Gaming;

    public static IReadOnlyList<CuratedService> Services { get; } =
    [
        // ───────────── ТЕЛЕМЕТРИЯ ─────────────
        new()
        {
            Name = "DiagTrack", Title = "Телеметрия Windows", Group = ServiceGroup.Telemetry,
            Description = "Собирает и отправляет в Microsoft диагностические данные. По умолчанию работает постоянно с момента загрузки.",
            Condition = "ПК не участвует в программе Windows Insider и не управляется организацией (Intune, Autopatch).",
            Impact = "Заметных последствий нет: при необходимости Windows запустит службу сама.",
            Target = ServiceStartMode.Manual, Profiles = RG, Gate = ServiceGate.NotInsider,
        },
        new()
        {
            Name = "dmwappushservice", Title = "Push-сообщения MDM", Group = ServiceGroup.Telemetry,
            Description = "Доставляет push-сообщения систем управления устройствами (MDM) и пакетов подготовки.",
            Condition = "ПК не подключён к Intune/MDM организации, вы не используете пакеты подготовки и фильтр записи (UWF).",
            Impact = "На ПК организации перестанут приходить команды и политики MDM.",
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Optional,
        },
        new()
        {
            Name = "diagnosticshub.standardcollector.service", Title = "Сборщик диагностики Visual Studio", Group = ServiceGroup.Telemetry,
            Description = "Собирает данные профилировщика для средств диагностики Visual Studio. Устанавливается вместе с Visual Studio.",
            Condition = "Вы не профилируете приложения в Visual Studio.",
            Impact = "Средства диагностики Visual Studio перестанут собирать данные.",
            Target = ServiceStartMode.Disabled, Profiles = RG, Conditional = true,
        },

        // ───────────── XBOX И ИГРЫ ─────────────
        new()
        {
            Name = "XblAuthManager", Title = "Вход в Xbox Live", Group = ServiceGroup.Xbox,
            Description = "Проверяет вход в учётную запись Xbox для игр, Game Pass и приложения Xbox.",
            Condition = "Вы не пользуетесь приложением Xbox, Game Pass и играми из Microsoft Store.",
            Impact = "Не получится войти в Xbox в играх Game Pass и в приложении Xbox.",
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced,
        },
        new()
        {
            Name = "XblGameSave", Title = "Облачные сохранения Xbox", Group = ServiceGroup.Xbox,
            Description = "Синхронизирует сохранения игр Xbox и Game Pass с облаком.",
            Condition = "Вы не играете в игры Xbox, Game Pass и Microsoft Store.",
            Impact = "Сохранения перестанут синхронизироваться — прогресс на другом ПК или консоли будет потерян.",
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced,
        },
        new()
        {
            Name = "XboxNetApiSvc", Title = "Сеть Xbox Live", Group = ServiceGroup.Xbox,
            Description = "Сетевые функции Xbox: подключение к сетевым играм, проверка типа NAT, чат группы.",
            Condition = "Вы не играете по сети через сервисы Xbox и не пользуетесь чатом группы.",
            Impact = "Сетевая игра и чат группы Xbox могут перестать работать.",
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced,
        },
        new()
        {
            Name = "XboxGipSvc", Title = "Аксессуары Xbox", Group = ServiceGroup.Xbox,
            Description = "Обновляет прошивку и хранит настройки геймпадов и аксессуаров Xbox.",
            Condition = "Вы не подключаете к ПК геймпад или аксессуары Xbox.",
            Impact = "Перестанут обновляться прошивка и настройки геймпадов Xbox.",
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced,
        },

        // ───────────── ПЕЧАТЬ И ФАКС ─────────────
        new()
        {
            Name = "Spooler", Title = "Диспетчер печати", Group = ServiceGroup.Printing,
            Description = "Очередь печати. Нужна для любого принтера, а также для «Microsoft Print to PDF» и печати из OneNote.",
            Condition = "У вас нет принтера и вы не сохраняете документы в PDF через печать.",
            Impact = "Печать и «Microsoft Print to PDF» не будут работать, пока служба отключена. Плюс: закрывается уязвимость PrintNightmare.",
            Target = ServiceStartMode.Disabled, Profiles = ProfileSet.Gaming, Conditional = true, Risk = TweakRisk.Advanced,
        },
        new()
        {
            Name = "PrintNotify", Title = "Расширения и уведомления принтеров", Group = ServiceGroup.Printing,
            Description = "Окна и уведомления драйверов принтеров: уровень чернил, ошибки печати.",
            Condition = "Только вместе с отключением «Диспетчера печати».",
            Impact = "Пропадут уведомления и окна драйверов принтеров.",
            Target = ServiceStartMode.Disabled, Conditional = true, Companion = "Spooler",
        },
        new()
        {
            Name = "Fax", Title = "Факс", Group = ServiceGroup.Printing,
            Description = "Отправка и приём факсов через модем или факс-сервер.",
            Condition = "Вы не отправляете и не принимаете факсы на этом ПК.",
            Impact = "Отправка и приём факсов станут недоступны.",
            Target = ServiceStartMode.Disabled, Profiles = ProfileSet.All, Conditional = true,
        },

        // ───────────── УДАЛЁННЫЙ ДОСТУП ─────────────
        new()
        {
            Name = "RemoteRegistry", Title = "Удалённый реестр", Group = ServiceGroup.RemoteAccess,
            Description = "Позволяет изменять реестр этого ПК по сети. В Windows по умолчанию отключена.",
            Condition = "Подходит всем: удалённая правка реестра нужна только администраторам корпоративных сетей.",
            Impact = "Ничего не ломается — это стандартная настройка Windows.",
            Target = ServiceStartMode.Disabled, Profiles = ProfileSet.All,
        },
        new()
        {
            Name = "LanmanServer", Title = "Сервер (общий доступ SMB)", Group = ServiceGroup.RemoteAccess,
            Description = "Открывает общий доступ к папкам и принтерам этого ПК по сети. Запускается по триггеру, когда нужен.",
            Condition = "Вы не раздаёте папки и принтеры с этого ПК и не администрируете его удалённо.",
            Impact = "Общие папки и принтеры этого ПК могут стать недоступны по сети.",
            Target = ServiceStartMode.Manual, Profiles = ProfileSet.Gaming, Conditional = true, Risk = TweakRisk.Advanced,
        },
        new()
        {
            Name = "CscService", Title = "Автономные файлы", Group = ServiceGroup.RemoteAccess,
            Description = "Хранит локальные копии файлов из сетевых папок, чтобы с ними можно было работать без сети.",
            Condition = "Вы не используете автономные файлы сетевых папок (обычно они нужны только в организациях).",
            Impact = "Автономные копии сетевых файлов станут недоступны.",
            Target = ServiceStartMode.Disabled, Profiles = ProfileSet.Gaming, Conditional = true,
        },

        // ───────────── BLUETOOTH И УСТРОЙСТВА ─────────────
        new()
        {
            Name = "CDPSvc", Title = "Платформа подключённых устройств", Group = ServiceGroup.Devices,
            Description = "«Связь с телефоном», «Обмен с устройствами поблизости» и общий буфер обмена между устройствами.",
            Condition = "Вы не пользуетесь «Связью с телефоном», обменом с устройствами поблизости и синхронизацией буфера обмена.",
            Impact = "Эти функции будут запускаться медленнее или перестанут работать.",
            Target = ServiceStartMode.Manual, Profiles = ProfileSet.Gaming, Conditional = true, Risk = TweakRisk.Optional,
        },
        new()
        {
            Name = "bthserv", Title = "Поддержка Bluetooth", Group = ServiceGroup.Devices,
            Description = "Обнаружение и подключение Bluetooth-устройств.",
            Condition = "На ПК вообще нет Bluetooth-адаптера.",
            Impact = "Bluetooth-наушники, геймпады и мыши перестанут подключаться.",
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced, Gate = ServiceGate.NoBluetoothAdapter,
        },
        new()
        {
            Name = "BTAGService", Title = "Аудиошлюз Bluetooth", Group = ServiceGroup.Devices,
            Description = "Микрофон и звонки через Bluetooth-гарнитуры (профиль Hands-Free).",
            Condition = "На ПК вообще нет Bluetooth-адаптера.",
            Impact = "Микрофон Bluetooth-гарнитур перестанет работать.",
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced, Gate = ServiceGate.NoBluetoothAdapter,
        },
        new()
        {
            Name = "BthAvctpSvc", Title = "Управление воспроизведением Bluetooth", Group = ServiceGroup.Devices,
            Description = "Кнопки воспроизведения и громкости на Bluetooth-наушниках и колонках (AVRCP).",
            Condition = "На ПК вообще нет Bluetooth-адаптера.",
            Impact = "Кнопки на Bluetooth-наушниках перестанут работать.",
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced, Gate = ServiceGate.NoBluetoothAdapter,
        },
        new()
        {
            Name = "PhoneSvc", Title = "Телефонная связь", Group = ServiceGroup.Devices,
            Description = "Состояние телефонии для звонков через «Связь с телефоном» и VoIP-приложения.",
            Condition = "Вы не звоните с этого ПК через «Связь с телефоном».",
            Impact = "Звонки через «Связь с телефоном» перестанут работать.",
            Target = ServiceStartMode.Disabled, Conditional = true,
        },
        new()
        {
            Name = "SEMgrSvc", Title = "Платежи и NFC", Group = ServiceGroup.Devices,
            Description = "Управляет NFC-платежами и защищёнными элементами (Secure Element).",
            Condition = "На ПК нет NFC или вы не платите им.",
            Impact = "NFC-платежи станут недоступны.",
            Target = ServiceStartMode.Disabled, Conditional = true,
        },

        // ───────────── ПРОЧЕЕ ─────────────
        new()
        {
            Name = "MapsBroker", Title = "Диспетчер скачанных карт", Group = ServiceGroup.Other,
            Description = "Даёт приложениям доступ к скачанным офлайн-картам.",
            Condition = "Подходит всем: приложение «Карты» запустит службу само, когда понадобится.",
            Impact = "Заметных последствий нет.",
            Target = ServiceStartMode.Manual, Profiles = ProfileSet.All,
        },
        new()
        {
            Name = "TrkWks", Title = "Отслеживание изменившихся связей", Group = ServiceGroup.Other,
            Description = "Следит за перемещением файлов NTFS, чтобы ярлыки и OLE-ссылки на них не ломались.",
            Condition = "Вы не перемещаете файлы в общих папках доменной сети.",
            Impact = "Ярлыки на перемещённые по сети файлы могут перестать находить цель.",
            Target = ServiceStartMode.Manual, Profiles = RG,
        },
        new()
        {
            Name = "PcaSvc", Title = "Помощник по совместимости программ", Group = ServiceGroup.Other,
            Description = "Отслеживает старые программы и предлагает для них режимы совместимости.",
            Condition = "Подходит всем: польза службы спорна, она запускается по триггеру.",
            Impact = "Windows реже будет предлагать режимы совместимости для старых программ.",
            Target = ServiceStartMode.Manual, Profiles = ProfileSet.Gaming,
        },
        new()
        {
            Name = "RetailDemo", Title = "Демонстрационный режим", Group = ServiceGroup.Other,
            Description = "Демо-режим для ПК на витринах магазинов.",
            Condition = "Подходит всем: на домашнем ПК не используется.",
            Impact = "Заметных последствий нет.",
            Target = ServiceStartMode.Disabled, Profiles = RG,
        },
        new()
        {
            Name = "WMPNetworkSvc", Title = "Сетевая медиатека Windows Media", Group = ServiceGroup.Other,
            Description = "Раздаёт медиатеку классического проигрывателя Windows Media по сети (DLNA).",
            Condition = "Вы не раздаёте музыку и видео из классического проигрывателя Windows Media на телевизор или другие устройства.",
            Impact = "Медиатека классического проигрывателя перестанет раздаваться по сети.",
            Target = ServiceStartMode.Disabled, Profiles = RG,
        },
        new()
        {
            Name = "wisvc", Title = "Программа предварительной оценки", Group = ServiceGroup.Other,
            Description = "Служба программы Windows Insider: получение предварительных сборок Windows.",
            Condition = "ПК не участвует в программе Windows Insider.",
            Impact = "Не получится вступить в программу Windows Insider, пока служба отключена.",
            Target = ServiceStartMode.Disabled, Profiles = RG, Conditional = true, Gate = ServiceGate.NotInsider,
        },
        new()
        {
            Name = "WpcMonSvc", Title = "Родительский контроль", Group = ServiceGroup.Other,
            Description = "Семейная безопасность Microsoft: ограничения и отчёты для детских учётных записей.",
            Condition = "На ПК нет детских учётных записей Microsoft Family.",
            Impact = "Ограничения и отчёты Microsoft Family перестанут работать.",
            Target = ServiceStartMode.Disabled, Conditional = true,
        },
        new()
        {
            Name = "icssvc", Title = "Мобильный хот-спот", Group = ServiceGroup.Other,
            Description = "Раздача интернета с этого ПК другим устройствам по Wi-Fi.",
            Condition = "Вы никогда не раздаёте интернет с этого ПК.",
            Impact = "Мобильный хот-спот перестанет включаться.",
            Target = ServiceStartMode.Disabled, Conditional = true,
        },
        new()
        {
            Name = "WSearch", Title = "Windows Search", Group = ServiceGroup.Other,
            Description = "Индексирует файлы и почту для быстрого поиска в «Пуске», Проводнике и Outlook.",
            Condition = "Вы не ищете файлы по содержимому и не пользуетесь классическим Outlook. Мягче — сократить индексируемые папки в Параметрах.",
            Impact = "Поиск файлов в «Пуске» и Проводнике станет медленным, поиск в классическом Outlook перестанет работать.",
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced,
        },
    ];

    public static IReadOnlyList<CuratedTask> Tasks { get; } =
    [
        new()
        {
            Path = @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
            Title = "Программа улучшения качества (CEIP)",
            Description = "Собирает и отправляет в Microsoft данные программы улучшения качества (wsqmcons.exe).",
            Impact = "Заметных последствий нет.", Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
            Title = "CEIP: данные USB",
            Description = "Собирает сведения об использовании USB-устройств для программы улучшения качества.",
            Impact = "Заметных последствий нет.", Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask",
            Title = "CEIP: данные ядра",
            Description = "Собирает данные ядра для программы улучшения качества (есть в Windows 10).",
            Impact = "Заметных последствий нет.", Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
            Title = "Инвентаризация программ (CEIP)",
            Description = "Собирает сведения об установленных программах для программы улучшения качества (Windows 10 и ранние сборки 11).",
            Impact = "Заметных последствий нет.", Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Application Experience\MareBackup",
            Title = "Резервная копия данных совместимости",
            Description = "Резервирует данные оценщика совместимости (compattelrunner, aemarebackup.dll).",
            Impact = "Заметных последствий нет.", Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Feedback\Siuf\DmClient",
            Title = "Опросы «Центра отзывов»",
            Description = "Загружает опросы «Центра отзывов» и показывает их пользователю.",
            Impact = "Windows перестанет предлагать опросы.", Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
            Title = "Сценарии опросов «Центра отзывов»",
            Description = "Загружает сценарии опросов «Центра отзывов».",
            Impact = "Windows перестанет предлагать опросы.", Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Autochk\Proxy",
            Title = "Данные проверки диска (Autochk)",
            Description = "Собирает данные проверки диска autochk для отправки в Microsoft (acproxy.dll).",
            Impact = "Сама проверка диска продолжит работать.", Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
            Title = "Сбор диагностики дисков",
            Description = "Собирает сведения о дисках и отправляет их в Microsoft.",
            Impact = "Windows не будет заранее отправлять сведения о проблемах дисков; SMART и проверка дисков работают как раньше.",
            Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Maps\MapsToastTask",
            Title = "Уведомления «Карт»",
            Description = "Показывает уведомления приложения «Карты».",
            Impact = "Пропадут уведомления «Карт».", Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Maps\MapsUpdateTask",
            Title = "Обновление офлайн-карт",
            Description = "Автоматически обновляет скачанные офлайн-карты.",
            Impact = "Офлайн-карты перестанут обновляться автоматически.", Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Sustainability\SustainabilityTelemetry",
            Title = "Телеметрия энергопотребления",
            Description = "Отправляет данные функций «устойчивого развития» (энергопотребление) в Microsoft.",
            Impact = "Заметных последствий нет.", Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem",
            Title = "Анализ энергоэффективности",
            Description = "Периодически анализирует энергоэффективность системы.",
            Impact = "Отчёт перестанет собираться сам; вручную его по-прежнему можно получить через powercfg /energy.",
            Profiles = ProfileSet.Gaming,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
            Title = "Отправка отчётов об ошибках",
            Description = "Отправляет в Microsoft накопленные отчёты о сбоях (WER).",
            Impact = "Отчёты о сбоях перестанут уходить в Microsoft. Мягче — отключить отчёты политикой.",
            Profiles = ProfileSet.Gaming, Conditional = true, Risk = TweakRisk.Optional,
        },
        new()
        {
            Path = @"\Microsoft\Office\OfficeTelemetryAgentLogOn2016",
            Title = "Телеметрия Office (вход)",
            Description = "Агент телеметрии Office, запускается при входе в систему.",
            Impact = "Заметных последствий нет.", Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Office\OfficeTelemetryAgentFallBack2016",
            Title = "Телеметрия Office (резервная)",
            Description = "Резервный запуск агента телеметрии Office.",
            Impact = "Заметных последствий нет.", Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\NvTmRep_CrashReport", IsPrefix = true,
            Title = "Телеметрия NVIDIA (сбои)",
            Description = "Старая телеметрия отчётов о сбоях драйвера NVIDIA.",
            Impact = "Заметных последствий нет.", Profiles = ProfileSet.All,
        },
    ];

    public static CuratedService? Find(string name) =>
        Services.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsCurated(string name) => Find(name) is not null;

    public static string GroupTitle(ServiceGroup group) => group switch
    {
        ServiceGroup.Telemetry => "ТЕЛЕМЕТРИЯ",
        ServiceGroup.Xbox => "XBOX И ИГРЫ",
        ServiceGroup.Printing => "ПЕЧАТЬ И ФАКС",
        ServiceGroup.RemoteAccess => "УДАЛЁННЫЙ ДОСТУП",
        ServiceGroup.Devices => "BLUETOOTH И УСТРОЙСТВА",
        _ => "ПРОЧЕЕ",
    };

    /// <summary>Warning shown above a group (null = none).</summary>
    public static string? GroupNote(ServiceGroup group) => group switch
    {
        ServiceGroup.Xbox =>
            "Играете через приложение Xbox, Game Pass, Microsoft Store или с геймпадом Xbox — оставьте эти службы как есть. " +
            "Без них не работают вход, облачные сохранения, сетевая игра и настройка геймпада. По умолчанию они и так запускаются только по требованию.",
        ServiceGroup.Printing =>
            "«Диспетчер печати» нужен не только принтерам, но и «Microsoft Print to PDF». На ПК без принтера его отключение — официальная мера Microsoft против уязвимости PrintNightmare.",
        ServiceGroup.Devices =>
            "Службы Bluetooth доступны для изменения только на ПК без Bluetooth-адаптера: иначе отвалятся наушники, геймпады и мыши.",
        _ => null,
    };

    public static string ProfileTitle(ProfileSet profile) => profile switch
    {
        ProfileSet.Normal => "ОБЫЧНЫЙ",
        ProfileSet.Gaming => "ИГРОВОЙ",
        _ => "РЕКОМЕНДУЕМЫЙ",
    };

    public static string ProfileDescription(ProfileSet profile) => profile switch
    {
        ProfileSet.Normal => "Только изменения без риска: то, что Windows и так запустит сама при необходимости.",
        ProfileSet.Gaming => "Рекомендуемый набор плюс службы, которые редко нужны на игровом ПК. Пункты «по условию» отметьте сами.",
        _ => "Сбалансированный набор для большинства домашних ПК: телеметрия и редко нужные службы — «вручную».",
    };
}
