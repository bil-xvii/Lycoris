using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Tweaks;

/// <summary>
/// Storage, Windows Update (pause/defer — never disable), background apps, scheduler, file system, page file, memory integrity
/// (general_tweaks.md §2, §3, §6, §9, §10; gaming_tweaks.md §2 E–H).
/// </summary>
public static partial class TweakCatalog
{
    private const string StoragePolicy = @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy";
    private const string BackgroundAccess = @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";
    private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    /// <summary>Inbox consumer apps that may safely be denied background work (never Xbox, Game Bar or Windows Security).</summary>
    internal static readonly string[] ConsumerBackgroundApps =
    [
        "Microsoft.BingNews_8wekyb3d8bbwe",
        "Microsoft.BingWeather_8wekyb3d8bbwe",
        "Clipchamp.Clipchamp_yxz26nhyzhsrt",
        "Microsoft.GetHelp_8wekyb3d8bbwe",
        "Microsoft.MicrosoftSolitaireCollection_8wekyb3d8bbwe",
    ];

    private static IEnumerable<Tweak> SystemTweaks()
    {
        // SAFE-DEFAULT (Normal, Recommended). Never enables Downloads cleanup; keeps any values the user already chose.
        yield return new CustomTweak
        {
            Id = "storage-sense-on",
            Title = "Контроль памяти",
            Description = "Windows сама удаляет временные файлы и старое содержимое корзины, когда на системном диске заканчивается место.",
            Details = "Предустановка MONOLITH: запуск при нехватке места, удаление временных файлов приложений, очистка корзины от файлов " +
                      "старше 30 дней. Папка «Загрузки» автоматически не очищается никогда. Если вы уже настраивали Контроль памяти, ваши " +
                      "значения сохраняются — MONOLITH только включает его и заполняет пустые параметры. Отмена — откатом или в Параметрах → " +
                      "Система → Память.",
            Category = TweakCategory.System,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            AppliesTo = _ => LmInt(@"SOFTWARE\Policies\Microsoft\Windows\StorageSense", "AllowStorageSenseGlobal") is null or 1,
            NotApplicableReason = "Контроль памяти отключён групповой политикой.",
            DetectFunc = () => CuInt(StoragePolicy, "01") == 1 ? TweakState.Applied : TweakState.NotApplied,
            ApplyAction = ctx =>
            {
                if (CuInt(StoragePolicy, "01") != 1) SetDword(ctx, RegHive.HKCU, StoragePolicy, "01", 1);
                foreach (var (name, value) in StorageSensePreset)
                    if (CuInt(StoragePolicy, name) is null) SetDword(ctx, RegHive.HKCU, StoragePolicy, name, value);
            },
        };

        yield return new RegistryTweak
        {
            Id = "ads-silent-app-installs-off",
            Title = "Автоустановка рекламных приложений",
            Description = "Запрещает Windows самостоятельно загружать продвигаемые приложения и игры из Microsoft Store.",
            Details = "Предотвращает будущие установки; уже установленные приложения не удаляются — их можно убрать в разделе «Приложения». " +
                      "Меньше фонового трафика и нагрузки на диск после крупных обновлений.",
            Category = TweakCategory.System,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Values =
            [
                Cu(ContentDelivery, "SilentInstalledAppsEnabled", 0),
                Cu(ContentDelivery, "PreInstalledAppsEnabled", 0),
                Cu(ContentDelivery, "OemPreInstalledAppsEnabled", 0),
                Cu(ContentDelivery, "PreInstalledAppsEverEnabled", 0),
            ],
        };

        // SAFE-OPTIONAL only as a fix 0x18 → 0x26; other values are DO-NOT-INCLUDE.
        yield return new CustomTweak
        {
            Id = "win32-priority-programs",
            Title = "Приоритет активных программ",
            Description = "Если Windows переключена в режим «Оптимизировать работу служб в фоновом режиме», возвращает приоритет программам на переднем плане.",
            Details = "Параметр из Свойств системы → Быстродействие → Дополнительно. На обычной системе он уже правильный, и пункт ничего " +
                      "не меняет. «Игровые» значения Win32PrioritySeparation из интернета (0x28, 0x2A, 0x16) MONOLITH не ставит: их польза " +
                      "не подтверждена. Действует сразу.",
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Optional,
            DetectFunc = () => LmInt(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation") == 0x18
                ? TweakState.NotApplied
                : TweakState.Applied,
            ApplyAction = ctx =>
            {
                if (LmInt(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation") == 0x18)
                    SetDword(ctx, RegHive.HKLM, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26);
            },
        };

        yield return new RegistryTweak
        {
            Id = "startup-delay-off",
            Title = "Без задержки автозагрузки",
            Description = "Программы из автозагрузки запускаются сразу после входа, без искусственной паузы Windows.",
            Details = "Параметр не документирован Microsoft. На играх не сказывается; на медленных дисках рабочий стол сразу после входа " +
                      "может откликаться хуже, пока стартуют программы. Действует со следующего входа.",
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Optional,
            Values =
            [
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "WaitForIdleState", 0),
            ],
        };

        yield return new CustomTweak
        {
            Id = "background-apps-consumer-off",
            Title = "Фоновая работа ненужных приложений",
            Description = "Запрещает фоновую работу предустановленным приложениям: Новости, Погода, Clipchamp, «Техническая поддержка», Solitaire.",
            Details = "Действует только на установленные приложения из этого списка; Xbox, Game Bar и «Безопасность Windows» не затрагиваются. " +
                      "Выигрыш небольшой — Windows и так ограничивает такие приложения, — но они перестают обновляться и присылать уведомления " +
                      "в фоне. Приложения работают как обычно, когда вы их открываете.",
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Optional,
            AppliesTo = _ => InstalledConsumerApps().Count > 0,
            NotApplicableReason = "Приложения из списка не установлены.",
            DetectFunc = () =>
            {
                var apps = InstalledConsumerApps();
                return StateOf(apps.Count(a => CuInt($@"{BackgroundAccess}\{a}", "Disabled") == 1 && CuInt($@"{BackgroundAccess}\{a}", "DisabledByUser") == 1), apps.Count);
            },
            ApplyAction = ctx =>
            {
                foreach (var app in InstalledConsumerApps())
                {
                    var key = $@"{BackgroundAccess}\{app}";
                    if (CuInt(key, "Disabled") != 1) SetDword(ctx, RegHive.HKCU, key, "Disabled", 1);
                    if (CuInt(key, "DisabledByUser") != 1) SetDword(ctx, RegHive.HKCU, key, "DisabledByUser", 1);
                }
            },
        };

        yield return new RegistryTweak
        {
            Id = "background-apps-global-off",
            Title = "Запретить фоновые приложения",
            Description = "Глобальный переключатель Windows 10: приложения из Microsoft Store не работают в фоне.",
            Details = "Касается только приложений Store; обычные программы (Steam, Discord, лаунчеры) не затрагиваются. Уведомления и " +
                      "синхронизация таких приложений приходят, только пока они открыты. Действует после выхода из системы.",
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.SignOut,
            AppliesTo = sp => !sp.IsWindows11,
            NotApplicableReason = "Только для Windows 10 — в Windows 11 этот переключатель удалён.",
            Values =
            [
                Cu(BackgroundAccess, "GlobalUserDisabled", 1),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0),
            ],
        };

        // ───── Windows Update: schedule and defer, never disable ─────

        yield return new RegistryTweak
        {
            Id = "wu-active-hours",
            Title = "Период активности 08:00–02:00",
            Description = "Центр обновления не будет перезагружать компьютер с 8:00 утра до 2:00 ночи.",
            Details = "Отключает автоматический подбор периода активности и задаёт максимальный разрешённый интервал — 18 часов. Обновления " +
                      "по-прежнему скачиваются и устанавливаются; перезагрузка переносится на ночь. Время можно изменить в Параметрах → " +
                      "Центр обновления Windows → Дополнительные параметры. Игровой режим Windows и так не даёт перезагружаться во время игры.",
            Category = TweakCategory.System,
            Risk = TweakRisk.Optional,
            Values =
            [
                Lm(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "SmartActiveHoursState", 0),
                Lm(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "ActiveHoursStart", 8),
                Lm(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "ActiveHoursEnd", 2),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "wu-defer-feature-updates",
            Title = "Отложить крупные обновления на 90 дней",
            Description = "Ежегодные обновления функций (например, 24H2 → 25H2) приходят через 90 дней после выхода; обновления безопасности — как обычно.",
            Details = "За это время Microsoft и производители драйверов исправляют проблемы новой версии, в том числе с играми и античитами. " +
                      "Используется политика Центра обновления, поэтому в Параметрах появится надпись «Некоторыми параметрами управляет ваша " +
                      "организация». Работает в Pro и старших редакциях.",
            Category = TweakCategory.System,
            Risk = TweakRisk.Optional,
            AppliesTo = sp => !IsHomeEdition(sp.OsEdition),
            NotApplicableReason = "Windows Home не поддерживает отсрочку обновлений.",
            Values =
            [
                Lm(WindowsUpdatePolicy, "DeferFeatureUpdates", 1),
                Lm(WindowsUpdatePolicy, "DeferFeatureUpdatesPeriodInDays", 90),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "wu-exclude-drivers",
            Title = "Не ставить драйверы через Центр обновления",
            Description = "Центр обновления перестаёт устанавливать драйверы вместе с ежемесячными обновлениями.",
            Details = "Полезно, если Windows Update заменяет установленный вами драйвер видеокарты старым. Обновления самой Windows " +
                      "продолжают приходить. Драйверы обновляйте вручную — в разделе «Железо и драйверы» MONOLITH или на сайте производителя.",
            Category = TweakCategory.System,
            Risk = TweakRisk.Advanced,
            Warning = "Windows перестанет сама ставить любые драйверы, включая полезные (чипсет, Wi-Fi, принтеры, исправления безопасности). " +
                      "Следить за драйверами придётся вам.",
            AppliesTo = sp => !IsHomeEdition(sp.OsEdition),
            NotApplicableReason = "Windows Home не поддерживает эту политику.",
            Values = [Lm(WindowsUpdatePolicy, "ExcludeWUDriversInQualityUpdate", 1)],
        };

        // ───── File system, memory, search ─────

        yield return new CustomTweak
        {
            Id = "ntfs-last-access-off",
            Title = "Не обновлять время последнего доступа",
            Description = "NTFS перестаёт записывать время последнего открытия файлов (как fsutil behavior set disablelastaccess 1).",
            Details = "На SSD/NVMe выигрыш ничтожный: NTFS и так обновляет это время пакетно, не чаще раза в час. Исходное значение " +
                      "параметра сохраняется и возвращается при откате. Нужна перезагрузка.",
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = "Программы резервного копирования, удалённого хранения и очистки «давно не открытых» файлов могут работать неверно. " +
                      "Ускорения на SSD практически нет.",
            // Bit 0 = last access updates disabled (0x1, 0x80000001 user managed; 0x80000003 system managed).
            DetectFunc = () => LastAccessDisabled() ? TweakState.Applied : TweakState.NotApplied,
            ApplyAction = ctx =>
            {
                if (LastAccessDisabled()) return;
                // 0x80000001 = user managed, last access updates disabled (what fsutil writes for value 1).
                SetDword(ctx, RegHive.HKLM, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", unchecked((int)0x80000001));
            },
        };

        yield return new RegistryTweak
        {
            Id = "pagefile-system-managed",
            Title = "Файл подкачки: автоматический размер",
            Description = "Возвращает стандартную настройку «Автоматически выбирать объём файла подкачки».",
            Details = "Исправление после «оптимизаторов», которые уменьшили или отключили файл подкачки. Он задаёт предел выделяемой памяти: " +
                      "без него игры и браузеры падают с ошибкой нехватки памяти даже при свободной ОЗУ, а дампы сбоев не сохраняются. " +
                      "Если вы сознательно перенесли файл подкачки на другой диск, этот пункт не нужен. Прежняя конфигурация запоминается " +
                      "для отката. Нужна перезагрузка.",
            Category = TweakCategory.System,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Reboot,
            Values =
            [
                new RegSet(RegHive.HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles",
                    RegistryValueKind.MultiString, new[] { @"?:\pagefile.sys" }),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "search-index-on-battery-off",
            Title = "Не индексировать файлы от батареи",
            Description = "Служба поиска Windows приостанавливает индексацию, пока ноутбук работает от батареи.",
            Details = "Немного продлевает работу от батареи; при подключении к сети индексация продолжается. Используется официальная " +
                      "политика Windows Search.",
            Category = TweakCategory.Power,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Reboot,
            AppliesTo = sp => sp.IsLaptop,
            NotApplicableReason = "Только для ноутбуков.",
            Values = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "PreventIndexOnBattery", 1)],
        };

        // ADVANCED, red warning, never in a profile. VBS itself is left alone.
        yield return new CustomTweak
        {
            Id = "hvci-off",
            Title = "Отключить целостность памяти (HVCI)",
            Description = "Отключает «Целостность памяти» (Безопасность Windows → Безопасность устройства → Изоляция ядра).",
            Details = "Microsoft признаёт, что целостность памяти может снижать производительность в играх «в некоторых сценариях»: в тестах " +
                      "потери 0–10 %, в среднем около 5 %, на новых процессорах меньше. Сама виртуализация (VBS) не отключается. " +
                      "Включить обратно можно откатом или переключателем в Безопасности Windows. Нужна перезагрузка.",
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = "Снижает защиту ядра от эксплойтов и отключает блокировку уязвимых драйверов. Некоторые античиты (Riot Vanguard, " +
                      "FACEIT) требуют включённую целостность памяти — такие игры могут перестать запускаться. Включайте, только если " +
                      "понимаете риск.",
            AppliesTo = _ => LmInt(HvciKey, "Locked") != 1 && !RegistryHelper.KeyExists(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard"),
            NotApplicableReason = "Целостность памяти заблокирована в UEFI или управляется политикой.",
            DetectFunc = () => LmInt(HvciKey, "Enabled") == 1 ? TweakState.NotApplied : TweakState.Applied,
            ApplyAction = ctx =>
            {
                if (LmInt(HvciKey, "Enabled") == 1) SetDword(ctx, RegHive.HKLM, HvciKey, "Enabled", 0);
            },
        };
    }

    private static bool LastAccessDisabled() =>
        ((LmInt(@"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate") ?? 0) & 1) != 0;

    private static readonly (string Name, int Value)[] StorageSensePreset =
    [
        ("2048", 0), // run when free space is low
        ("04", 1),   // delete temporary files apps are not using
        ("08", 1),   // empty the Recycle Bin…
        ("256", 30), // …of files older than 30 days
        ("32", 0),   // never clean the Downloads folder
        ("512", 0),
    ];

    /// <summary>Package family names from <see cref="ConsumerBackgroundApps"/> that are installed for the current user.</summary>
    internal static IReadOnlyList<string> InstalledConsumerApps()
    {
        try
        {
            using var root = RegistryHelper.OpenBase(RegHive.HKCU);
            using var packages = root.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packages is null) return [];
            var names = packages.GetSubKeyNames();
            return ConsumerBackgroundApps.Where(pfn => IsPackageInstalled(pfn, names)).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>A package full name is "Name_Version_Arch_ResourceId_PublisherId"; the family name is "Name_PublisherId".</summary>
    internal static bool IsPackageInstalled(string familyName, IEnumerable<string> packageFullNames)
    {
        var split = familyName.LastIndexOf('_');
        if (split <= 0) return false;
        var name = familyName[..split];
        var publisher = familyName[(split + 1)..];
        return packageFullNames.Any(p =>
            p.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase) &&
            p.EndsWith("_" + publisher, StringComparison.OrdinalIgnoreCase));
    }
}
