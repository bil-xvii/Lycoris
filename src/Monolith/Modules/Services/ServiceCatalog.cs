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
        // DiagTrack is not here on purpose: its only place is Optimize (diagtrack-manual, ADVANCED) — see ServiceGuard.OptimizeTweakFor.
        new()
        {
            Name = "dmwappushservice", Title = Loc.Key("Push-сообщения MDM"), Group = ServiceGroup.Telemetry,
            Description = Loc.T("Доставляет push-сообщения систем управления устройствами (MDM) и пакетов подготовки."),
            Condition = Loc.T("ПК не подключён к Intune/MDM организации, вы не используете пакеты подготовки и фильтр записи (UWF)."),
            Impact = Loc.T("На ПК организации перестанут приходить команды и политики MDM."),
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Optional,
        },
        new()
        {
            Name = "diagnosticshub.standardcollector.service", Title = Loc.Key("Сборщик диагностики Visual Studio"), Group = ServiceGroup.Telemetry,
            Description = Loc.T("Собирает данные профилировщика для средств диагностики Visual Studio. Устанавливается вместе с Visual Studio."),
            Condition = Loc.T("Вы не профилируете приложения в Visual Studio."),
            Impact = Loc.T("Средства диагностики Visual Studio перестанут собирать данные."),
            Target = ServiceStartMode.Disabled, Profiles = RG, Conditional = true,
        },

        // ───────────── XBOX И ИГРЫ ─────────────
        new()
        {
            Name = "XblAuthManager", Title = Loc.Key("Вход в Xbox Live"), Group = ServiceGroup.Xbox,
            Description = Loc.T("Проверяет вход в учётную запись Xbox для игр, Game Pass и приложения Xbox."),
            Condition = Loc.T("Вы не пользуетесь приложением Xbox, Game Pass и играми из Microsoft Store."),
            Impact = Loc.T("Не получится войти в Xbox в играх Game Pass и в приложении Xbox."),
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced,
        },
        new()
        {
            Name = "XblGameSave", Title = Loc.Key("Облачные сохранения Xbox"), Group = ServiceGroup.Xbox,
            Description = Loc.T("Синхронизирует сохранения игр Xbox и Game Pass с облаком."),
            Condition = Loc.T("Вы не играете в игры Xbox, Game Pass и Microsoft Store."),
            Impact = Loc.T("Сохранения перестанут синхронизироваться — прогресс на другом ПК или консоли будет потерян."),
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced,
        },
        new()
        {
            Name = "XboxNetApiSvc", Title = Loc.Key("Сеть Xbox Live"), Group = ServiceGroup.Xbox,
            Description = Loc.T("Сетевые функции Xbox: подключение к сетевым играм, проверка типа NAT, чат группы."),
            Condition = Loc.T("Вы не играете по сети через сервисы Xbox и не пользуетесь чатом группы."),
            Impact = Loc.T("Сетевая игра и чат группы Xbox могут перестать работать."),
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced,
        },
        new()
        {
            Name = "XboxGipSvc", Title = Loc.Key("Аксессуары Xbox"), Group = ServiceGroup.Xbox,
            Description = Loc.T("Обновляет прошивку и хранит настройки геймпадов и аксессуаров Xbox."),
            Condition = Loc.T("Вы не подключаете к ПК геймпад или аксессуары Xbox."),
            Impact = Loc.T("Перестанут обновляться прошивка и настройки геймпадов Xbox."),
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced,
        },

        // ───────────── ПЕЧАТЬ И ФАКС ─────────────
        new()
        {
            Name = "Spooler", Title = Loc.Key("Диспетчер печати"), Group = ServiceGroup.Printing,
            Description = Loc.T("Очередь печати. Нужна для любого принтера, а также для «Microsoft Print to PDF» и печати из OneNote."),
            Condition = Loc.T("У вас нет принтера и вы не сохраняете документы в PDF через печать."),
            Impact = Loc.T("Печать и «Microsoft Print to PDF» не будут работать, пока служба отключена. Плюс: закрывается уязвимость PrintNightmare."),
            Target = ServiceStartMode.Disabled, Profiles = ProfileSet.Gaming, Conditional = true, Risk = TweakRisk.Advanced,
        },
        new()
        {
            Name = "PrintNotify", Title = Loc.Key("Расширения и уведомления принтеров"), Group = ServiceGroup.Printing,
            Description = Loc.T("Окна и уведомления драйверов принтеров: уровень чернил, ошибки печати."),
            Condition = Loc.T("Только вместе с отключением «Диспетчера печати»."),
            Impact = Loc.T("Пропадут уведомления и окна драйверов принтеров."),
            Target = ServiceStartMode.Disabled, Conditional = true, Companion = "Spooler",
        },
        new()
        {
            Name = "Fax", Title = Loc.Key("Факс"), Group = ServiceGroup.Printing,
            Description = Loc.T("Отправка и приём факсов через модем или факс-сервер."),
            Condition = Loc.T("Вы не отправляете и не принимаете факсы на этом ПК."),
            Impact = Loc.T("Отправка и приём факсов станут недоступны."),
            Target = ServiceStartMode.Disabled, Profiles = ProfileSet.All, Conditional = true,
        },

        // ───────────── УДАЛЁННЫЙ ДОСТУП ─────────────
        new()
        {
            Name = "RemoteRegistry", Title = Loc.Key("Удалённый реестр"), Group = ServiceGroup.RemoteAccess,
            Description = Loc.T("Позволяет изменять реестр этого ПК по сети. В Windows по умолчанию отключена."),
            Condition = Loc.T("Подходит всем: удалённая правка реестра нужна только администраторам корпоративных сетей."),
            Impact = Loc.T("Ничего не ломается — это стандартная настройка Windows."),
            Target = ServiceStartMode.Disabled, Profiles = ProfileSet.All,
        },
        new()
        {
            Name = "LanmanServer", Title = Loc.Key("Сервер (общий доступ SMB)"), Group = ServiceGroup.RemoteAccess,
            Description = Loc.T("Открывает общий доступ к папкам и принтерам этого ПК по сети. Запускается по триггеру, когда нужен."),
            Condition = Loc.T("Вы не раздаёте папки и принтеры с этого ПК и не администрируете его удалённо."),
            Impact = Loc.T("Общие папки и принтеры этого ПК могут стать недоступны по сети."),
            Target = ServiceStartMode.Manual, Profiles = ProfileSet.Gaming, Conditional = true, Risk = TweakRisk.Advanced,
        },
        new()
        {
            Name = "CscService", Title = Loc.Key("Автономные файлы"), Group = ServiceGroup.RemoteAccess,
            Description = Loc.T("Хранит локальные копии файлов из сетевых папок, чтобы с ними можно было работать без сети."),
            Condition = Loc.T("Вы не используете автономные файлы сетевых папок (обычно они нужны только в организациях)."),
            Impact = Loc.T("Автономные копии сетевых файлов станут недоступны."),
            Target = ServiceStartMode.Disabled, Profiles = ProfileSet.Gaming, Conditional = true,
        },

        // ───────────── BLUETOOTH И УСТРОЙСТВА ─────────────
        new()
        {
            Name = "CDPSvc", Title = Loc.Key("Платформа подключённых устройств"), Group = ServiceGroup.Devices,
            Description = Loc.T("«Связь с телефоном», «Обмен с устройствами поблизости» и общий буфер обмена между устройствами."),
            Condition = Loc.T("Вы не пользуетесь «Связью с телефоном», обменом с устройствами поблизости и синхронизацией буфера обмена."),
            Impact = Loc.T("Эти функции будут запускаться медленнее или перестанут работать."),
            Target = ServiceStartMode.Manual, Profiles = ProfileSet.Gaming, Conditional = true, Risk = TweakRisk.Optional,
        },
        new()
        {
            Name = "bthserv", Title = Loc.Key("Поддержка Bluetooth"), Group = ServiceGroup.Devices,
            Description = Loc.T("Обнаружение и подключение Bluetooth-устройств."),
            Condition = Loc.T("На ПК вообще нет Bluetooth-адаптера."),
            Impact = Loc.T("Bluetooth-наушники, геймпады и мыши перестанут подключаться."),
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced, Gate = ServiceGate.NoBluetoothAdapter,
        },
        new()
        {
            Name = "BTAGService", Title = Loc.Key("Аудиошлюз Bluetooth"), Group = ServiceGroup.Devices,
            Description = Loc.T("Микрофон и звонки через Bluetooth-гарнитуры (профиль Hands-Free)."),
            Condition = Loc.T("На ПК вообще нет Bluetooth-адаптера."),
            Impact = Loc.T("Микрофон Bluetooth-гарнитур перестанет работать."),
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced, Gate = ServiceGate.NoBluetoothAdapter,
        },
        new()
        {
            Name = "BthAvctpSvc", Title = Loc.Key("Управление воспроизведением Bluetooth"), Group = ServiceGroup.Devices,
            Description = Loc.T("Кнопки воспроизведения и громкости на Bluetooth-наушниках и колонках (AVRCP)."),
            Condition = Loc.T("На ПК вообще нет Bluetooth-адаптера."),
            Impact = Loc.T("Кнопки на Bluetooth-наушниках перестанут работать."),
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced, Gate = ServiceGate.NoBluetoothAdapter,
        },
        new()
        {
            Name = "PhoneSvc", Title = Loc.Key("Телефонная связь"), Group = ServiceGroup.Devices,
            Description = Loc.T("Состояние телефонии для звонков через «Связь с телефоном» и VoIP-приложения."),
            Condition = Loc.T("Вы не звоните с этого ПК через «Связь с телефоном»."),
            Impact = Loc.T("Звонки через «Связь с телефоном» перестанут работать."),
            Target = ServiceStartMode.Disabled, Conditional = true,
        },
        new()
        {
            Name = "SEMgrSvc", Title = Loc.Key("Платежи и NFC"), Group = ServiceGroup.Devices,
            Description = Loc.T("Управляет NFC-платежами и защищёнными элементами (Secure Element)."),
            Condition = Loc.T("На ПК нет NFC или вы не платите им."),
            Impact = Loc.T("NFC-платежи станут недоступны."),
            Target = ServiceStartMode.Disabled, Conditional = true,
        },

        // ───────────── ПРОЧЕЕ ─────────────
        new()
        {
            Name = "MapsBroker", Title = Loc.Key("Диспетчер скачанных карт"), Group = ServiceGroup.Other,
            Description = Loc.T("Даёт приложениям доступ к скачанным офлайн-картам."),
            Condition = Loc.T("Подходит всем: приложение «Карты» запустит службу само, когда понадобится."),
            Impact = Loc.T("Заметных последствий нет."),
            Target = ServiceStartMode.Manual, Profiles = ProfileSet.All,
        },
        new()
        {
            Name = "TrkWks", Title = Loc.Key("Отслеживание изменившихся связей"), Group = ServiceGroup.Other,
            Description = Loc.T("Следит за перемещением файлов NTFS, чтобы ярлыки и OLE-ссылки на них не ломались."),
            Condition = Loc.T("Вы не перемещаете файлы в общих папках доменной сети."),
            Impact = Loc.T("Ярлыки на перемещённые по сети файлы могут перестать находить цель."),
            Target = ServiceStartMode.Manual, Profiles = RG,
        },
        new()
        {
            Name = "PcaSvc", Title = Loc.Key("Помощник по совместимости программ"), Group = ServiceGroup.Other,
            Description = Loc.T("Отслеживает старые программы и предлагает для них режимы совместимости."),
            Condition = Loc.T("Подходит всем: польза службы спорна, она запускается по триггеру."),
            Impact = Loc.T("Windows реже будет предлагать режимы совместимости для старых программ."),
            Target = ServiceStartMode.Manual, Profiles = ProfileSet.Gaming,
        },
        new()
        {
            Name = "RetailDemo", Title = Loc.Key("Демонстрационный режим"), Group = ServiceGroup.Other,
            Description = Loc.T("Демо-режим для ПК на витринах магазинов."),
            Condition = Loc.T("Подходит всем: на домашнем ПК не используется."),
            Impact = Loc.T("Заметных последствий нет."),
            Target = ServiceStartMode.Disabled, Profiles = RG,
        },
        new()
        {
            Name = "WMPNetworkSvc", Title = Loc.Key("Сетевая медиатека Windows Media"), Group = ServiceGroup.Other,
            Description = Loc.T("Раздаёт медиатеку классического проигрывателя Windows Media по сети (DLNA)."),
            Condition = Loc.T("Вы не раздаёте музыку и видео из классического проигрывателя Windows Media на телевизор или другие устройства."),
            Impact = Loc.T("Медиатека классического проигрывателя перестанет раздаваться по сети."),
            Target = ServiceStartMode.Disabled, Profiles = RG,
        },
        new()
        {
            Name = "wisvc", Title = Loc.Key("Программа предварительной оценки"), Group = ServiceGroup.Other,
            Description = Loc.T("Служба программы Windows Insider: получение предварительных сборок Windows."),
            Condition = Loc.T("ПК не участвует в программе Windows Insider."),
            Impact = Loc.T("Не получится вступить в программу Windows Insider, пока служба отключена."),
            Target = ServiceStartMode.Disabled, Profiles = RG, Conditional = true, Gate = ServiceGate.NotInsider,
        },
        new()
        {
            Name = "WpcMonSvc", Title = Loc.Key("Родительский контроль"), Group = ServiceGroup.Other,
            Description = Loc.T("Семейная безопасность Microsoft: ограничения и отчёты для детских учётных записей."),
            Condition = Loc.T("На ПК нет детских учётных записей Microsoft Family."),
            Impact = Loc.T("Ограничения и отчёты Microsoft Family перестанут работать."),
            Target = ServiceStartMode.Disabled, Conditional = true,
        },
        new()
        {
            Name = "icssvc", Title = Loc.Key("Мобильный хот-спот"), Group = ServiceGroup.Other,
            Description = Loc.T("Раздача интернета с этого ПК другим устройствам по Wi-Fi."),
            Condition = Loc.T("Вы никогда не раздаёте интернет с этого ПК."),
            Impact = Loc.T("Мобильный хот-спот перестанет включаться."),
            Target = ServiceStartMode.Disabled, Conditional = true,
        },
        new()
        {
            Name = "WSearch", Title = "Windows Search", Group = ServiceGroup.Other,
            Description = Loc.T("Индексирует файлы и почту для быстрого поиска в «Пуске», Проводнике и Outlook."),
            Condition = Loc.T("Вы не ищете файлы по содержимому и не пользуетесь классическим Outlook. Мягче — сократить индексируемые папки в Параметрах."),
            Impact = Loc.T("Поиск файлов в «Пуске» и Проводнике станет медленным, поиск в классическом Outlook перестанет работать."),
            Target = ServiceStartMode.Disabled, Conditional = true, Risk = TweakRisk.Advanced,
        },
    ];

    public static IReadOnlyList<CuratedTask> Tasks { get; } =
    [
        new()
        {
            Path = @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
            Title = Loc.Key("Программа улучшения качества (CEIP)"),
            Description = Loc.T("Собирает и отправляет в Microsoft данные программы улучшения качества (wsqmcons.exe)."),
            Impact = Loc.T("Заметных последствий нет."), Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
            Title = Loc.Key("CEIP: данные USB"),
            Description = Loc.T("Собирает сведения об использовании USB-устройств для программы улучшения качества."),
            Impact = Loc.T("Заметных последствий нет."), Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask",
            Title = Loc.Key("CEIP: данные ядра"),
            Description = Loc.T("Собирает данные ядра для программы улучшения качества (есть в Windows 10)."),
            Impact = Loc.T("Заметных последствий нет."), Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
            Title = Loc.Key("Инвентаризация программ (CEIP)"),
            Description = Loc.T("Собирает сведения об установленных программах для программы улучшения качества (Windows 10 и ранние сборки 11)."),
            Impact = Loc.T("Заметных последствий нет."), Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Application Experience\MareBackup",
            Title = Loc.Key("Резервная копия данных совместимости"),
            Description = Loc.T("Резервирует данные оценщика совместимости (compattelrunner, aemarebackup.dll)."),
            Impact = Loc.T("Заметных последствий нет."), Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Feedback\Siuf\DmClient",
            Title = Loc.Key("Опросы «Центра отзывов»"),
            Description = Loc.T("Загружает опросы «Центра отзывов» и показывает их пользователю."),
            Impact = Loc.T("Windows перестанет предлагать опросы."), Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
            Title = Loc.Key("Сценарии опросов «Центра отзывов»"),
            Description = Loc.T("Загружает сценарии опросов «Центра отзывов»."),
            Impact = Loc.T("Windows перестанет предлагать опросы."), Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Autochk\Proxy",
            Title = Loc.Key("Данные проверки диска (Autochk)"),
            Description = Loc.T("Собирает данные проверки диска autochk для отправки в Microsoft (acproxy.dll)."),
            Impact = Loc.T("Сама проверка диска продолжит работать."), Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
            Title = Loc.Key("Сбор диагностики дисков"),
            Description = Loc.T("Собирает сведения о дисках и отправляет их в Microsoft."),
            Impact = Loc.T("Windows не будет заранее отправлять сведения о проблемах дисков; SMART и проверка дисков работают как раньше."),
            Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Maps\MapsToastTask",
            Title = Loc.Key("Уведомления «Карт»"),
            Description = Loc.T("Показывает уведомления приложения «Карты»."),
            Impact = Loc.T("Пропадут уведомления «Карт»."), Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Maps\MapsUpdateTask",
            Title = Loc.Key("Обновление офлайн-карт"),
            Description = Loc.T("Автоматически обновляет скачанные офлайн-карты."),
            Impact = Loc.T("Офлайн-карты перестанут обновляться автоматически."), Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Sustainability\SustainabilityTelemetry",
            Title = Loc.Key("Телеметрия энергопотребления"),
            Description = Loc.T("Отправляет данные функций «устойчивого развития» (энергопотребление) в Microsoft."),
            Impact = Loc.T("Заметных последствий нет."), Profiles = RG,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem",
            Title = Loc.Key("Анализ энергоэффективности"),
            Description = Loc.T("Периодически анализирует энергоэффективность системы."),
            Impact = Loc.T("Отчёт перестанет собираться сам; вручную его по-прежнему можно получить через powercfg /energy."),
            Profiles = ProfileSet.Gaming,
        },
        new()
        {
            Path = @"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
            Title = Loc.Key("Отправка отчётов об ошибках"),
            Description = Loc.T("Отправляет в Microsoft накопленные отчёты о сбоях (WER)."),
            Impact = Loc.T("Отчёты о сбоях перестанут уходить в Microsoft. Мягче — отключить отчёты политикой."),
            Profiles = ProfileSet.Gaming, Conditional = true, Risk = TweakRisk.Optional,
        },
        new()
        {
            Path = @"\Microsoft\Office\OfficeTelemetryAgentLogOn2016",
            Title = Loc.Key("Телеметрия Office (вход)"),
            Description = Loc.T("Агент телеметрии Office, запускается при входе в систему."),
            Impact = Loc.T("Заметных последствий нет."), Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\Microsoft\Office\OfficeTelemetryAgentFallBack2016",
            Title = Loc.Key("Телеметрия Office (резервная)"),
            Description = Loc.T("Резервный запуск агента телеметрии Office."),
            Impact = Loc.T("Заметных последствий нет."), Profiles = ProfileSet.All,
        },
        new()
        {
            Path = @"\NvTmRep_CrashReport", IsPrefix = true,
            Title = Loc.Key("Телеметрия NVIDIA (сбои)"),
            Description = Loc.T("Старая телеметрия отчётов о сбоях драйвера NVIDIA."),
            Impact = Loc.T("Заметных последствий нет."), Profiles = ProfileSet.All,
        },
    ];

    public static CuratedService? Find(string name) =>
        Services.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsCurated(string name) => Find(name) is not null;

    public static string GroupTitle(ServiceGroup group) => group switch
    {
        ServiceGroup.Telemetry => Loc.T("ТЕЛЕМЕТРИЯ"),
        ServiceGroup.Xbox => Loc.T("XBOX И ИГРЫ"),
        ServiceGroup.Printing => Loc.T("ПЕЧАТЬ И ФАКС"),
        ServiceGroup.RemoteAccess => Loc.T("УДАЛЁННЫЙ ДОСТУП"),
        ServiceGroup.Devices => Loc.T("BLUETOOTH И УСТРОЙСТВА"),
        _ => Loc.T("ПРОЧЕЕ"),
    };

    /// <summary>Warning shown above a group (null = none).</summary>
    public static string? GroupNote(ServiceGroup group) => group switch
    {
        ServiceGroup.Xbox =>
            Loc.T("Играете через приложение Xbox, Game Pass, Microsoft Store или с геймпадом Xbox — оставьте эти службы как есть. " +
            "Без них не работают вход, облачные сохранения, сетевая игра и настройка геймпада. По умолчанию они и так запускаются только по требованию."),
        ServiceGroup.Printing =>
            Loc.T("«Диспетчер печати» нужен не только принтерам, но и «Microsoft Print to PDF». На ПК без принтера его отключение — официальная мера Microsoft против уязвимости PrintNightmare."),
        ServiceGroup.Devices =>
            Loc.T("Службы Bluetooth доступны для изменения только на ПК без Bluetooth-адаптера: иначе отвалятся наушники, геймпады и мыши."),
        _ => null,
    };

    /// <summary>
    /// The three services profiles, named like the Optimize scenarios they follow («Работа и учёба» = Normal, «Игры» = Gaming;
    /// «Стриминг и запись» maps onto Gaming, the efficiency scenario onto Recommended — OptimizeLogic.ServicesProfileFor).
    /// </summary>
    public static string ProfileTitle(ProfileSet profile) => profile switch
    {
        ProfileSet.Normal => Loc.T("РАБОТА И УЧЁБА"),
        ProfileSet.Gaming => Loc.T("ИГРЫ"),
        _ => Loc.T("РЕКОМЕНДУЕМЫЙ"),
    };

    public static string ProfileDescription(ProfileSet profile) => profile switch
    {
        ProfileSet.Normal => Loc.T("Только изменения без риска: то, что Windows и так запустит сама при необходимости."),
        ProfileSet.Gaming => Loc.T("Рекомендуемый набор плюс службы, которые редко нужны на игровом ПК. Пункты «по условию» отметьте сами."),
        _ => Loc.T("Сбалансированный набор для большинства домашних ПК: телеметрия и редко нужные службы — «вручную»."),
    };
}
