using System.IO;
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
            Title = Loc.Key("Контроль памяти"),
            Description = Loc.T("Windows сама удаляет временные файлы и старое содержимое корзины, когда на системном диске заканчивается место."),
            Details = Loc.T("Предустановка Lycoris: запуск при нехватке места, удаление временных файлов приложений, очистка корзины от файлов " +
                      "старше 30 дней. Папка «Загрузки» автоматически не очищается никогда. Если вы уже настраивали Контроль памяти, ваши " +
                      "значения сохраняются — Lycoris только включает его и заполняет пустые параметры. Отмена — откатом или в Параметрах → " +
                      "Система → Память."),
            Category = TweakCategory.System,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            AppliesTo = _ => LmInt(@"SOFTWARE\Policies\Microsoft\Windows\StorageSense", "AllowStorageSenseGlobal") is null or 1,
            NotApplicableReason = Loc.T("Контроль памяти отключён групповой политикой."),
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
            Title = Loc.Key("Автоустановка рекламных приложений"),
            Description = Loc.T("Запрещает Windows самостоятельно загружать продвигаемые приложения и игры из Microsoft Store."),
            Details = Loc.T("Предотвращает будущие установки; уже установленные приложения не удаляются — их можно убрать в разделе «Приложения». " +
                      "Меньше фонового трафика и нагрузки на диск после крупных обновлений."),
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
            Title = Loc.Key("Приоритет активных программ"),
            Description = Loc.T("Если Windows переключена в режим «Оптимизировать работу служб в фоновом режиме», возвращает приоритет программам на переднем плане."),
            Details = Loc.T("Параметр из Свойств системы → Быстродействие → Дополнительно. На обычной системе он уже правильный, и пункт ничего " +
                      "не меняет. «Игровые» значения Win32PrioritySeparation из интернета (0x28, 0x2A, 0x16) Lycoris не ставит: их польза " +
                      "не подтверждена. Действует сразу."),
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
            Title = Loc.Key("Без задержки автозагрузки"),
            Description = Loc.T("Программы из автозагрузки запускаются сразу после входа, без искусственной паузы Windows."),
            Details = Loc.T("Параметр не документирован Microsoft. На играх не сказывается; на медленных дисках рабочий стол сразу после входа " +
                      "может откликаться хуже, пока стартуют программы. Действует со следующего входа."),
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
            Title = Loc.Key("Фоновая работа ненужных приложений"),
            Description = Loc.T("Запрещает фоновую работу предустановленным приложениям: Новости, Погода, Clipchamp, «Техническая поддержка», Solitaire."),
            Details = Loc.T("Действует только на установленные приложения из этого списка; Xbox, Game Bar и «Безопасность Windows» не затрагиваются. " +
                      "Выигрыш небольшой — Windows и так ограничивает такие приложения, — но они перестают обновляться и присылать уведомления " +
                      "в фоне. Приложения работают как обычно, когда вы их открываете."),
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Optional,
            AppliesTo = _ => InstalledConsumerApps().Count > 0,
            NotApplicableReason = Loc.T("Приложения из списка не установлены."),
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

        // SAFE-OPTIONAL, never in a profile: documented Edge policies (Edge reads them without a restart).
        yield return new RegistryTweak
        {
            Id = "edge-background-off",
            Title = Loc.Key("Microsoft Edge без фонового запуска"),
            Description = Loc.T("Политики Edge запрещают «ускорение запуска» (процессы Edge при входе в Windows) и работу Edge в фоне после " +
                          "закрытия последнего окна."),
            Details = Loc.T("Официальные политики Microsoft StartupBoostEnabled и BackgroundModeEnabled. Сам Edge, его обновления и WebView2 " +
                      "(виджеты, Teams, лаунчеры игр) не затрагиваются. Эффект скромный и на FPS не влияет: Microsoft называет нагрузку от " +
                      "ускорения запуска ограниченной и включает его автоматически только тем, кто часто открывает Edge, а фоновый режим и " +
                      "так выключен по умолчанию — политика лишь закрепляет это. Цена: первый запуск Edge после входа медленнее, сайты и " +
                      "установленные веб-приложения не присылают уведомления, пока Edge закрыт, а в edge://settings/system эти переключатели " +
                      "станут недоступны с пометкой «Управляется вашей организацией». Откат удаляет политики — Edge вернётся к вашим " +
                      "настройкам."),
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Optional,
            AppliesTo = _ => IsEdgeInstalled(),
            NotApplicableReason = Loc.T("Microsoft Edge не установлен."),
            Values =
            [
                Lm(EdgePolicy, "StartupBoostEnabled", 0),
                Lm(EdgePolicy, "BackgroundModeEnabled", 0),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "background-apps-global-off",
            Title = Loc.Key("Запретить фоновые приложения"),
            Description = Loc.T("Глобальный переключатель Windows 10: приложения из Microsoft Store не работают в фоне."),
            Details = Loc.T("Касается только приложений Store; обычные программы (Steam, Discord, лаунчеры) не затрагиваются. Уведомления и " +
                      "синхронизация таких приложений приходят, только пока они открыты. Действует после выхода из системы."),
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.SignOut,
            AppliesTo = sp => !sp.IsWindows11,
            NotApplicableReason = Loc.T("Только для Windows 10 — в Windows 11 этот переключатель удалён."),
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
            Title = Loc.Key("Период активности 08:00–02:00"),
            Description = Loc.T("Центр обновления не будет перезагружать компьютер с 8:00 утра до 2:00 ночи."),
            Details = Loc.T("Отключает автоматический подбор периода активности и задаёт максимальный разрешённый интервал — 18 часов. Обновления " +
                      "по-прежнему скачиваются и устанавливаются; перезагрузка переносится на ночь. Время можно изменить в Параметрах → " +
                      "Центр обновления Windows → Дополнительные параметры. Игровой режим и так не даёт перезагружаться во время игры."),
            Category = TweakCategory.System,
            Risk = TweakRisk.Optional,
            DefaultIn = ProfileSet.Normal, // SAFE-OPTIONAL pre-check of «Работа и учёба»: no restarts during the working day
            Values =
            [
                Lm(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "SmartActiveHoursState", 0),
                Lm(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "ActiveHoursStart", 8),
                Lm(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "ActiveHoursEnd", 2),
            ],
        };

        // A CustomTweak over two plain registry values: the deferral and the version pin below are one choice, and the two
        // policies write different values, so the group alone cannot keep them apart — each one refuses while the other's
        // journal record is active (the pin checks the same way in ApplyUpdatePin).
        yield return new CustomTweak
        {
            Id = DeferFeatureUpdatesId,
            Title = Loc.Key("Отложить крупные обновления на 90 дней"),
            Description = Loc.T("Ежегодные обновления функций (например, 24H2 → 25H2) приходят через 90 дней после выхода; обновления безопасности — как обычно."),
            Details = Loc.T("За это время Microsoft и производители драйверов исправляют проблемы новой версии, в том числе с играми и античитами. " +
                      "Используется политика Центра обновления, поэтому в Параметрах появится надпись «Некоторыми параметрами управляет ваша " +
                      "организация». Работает в Pro и старших редакциях. Выбирается вместо пункта «Остаться на текущей версии Windows 11» — " +
                      "если тот уже применён, сначала откатите его."),
            Category = TweakCategory.System,
            Risk = TweakRisk.Optional,
            ExclusiveGroup = WuFeatureUpdatesGroup,
            AppliesTo = sp => !IsHomeEdition(sp.OsEdition),
            NotApplicableReason = Loc.T("Windows Home не поддерживает отсрочку обновлений."),
            DetectFunc = () => StateOf(DeferFeatureUpdatesValues.Count(Matches), DeferFeatureUpdatesValues.Count),
            ApplyAction = ApplyDeferFeatureUpdates,
        };

        // SAFE-OPTIONAL (general_tweaks.md §9, «version pin»): TargetReleaseVersion keeps the installed version while monthly
        // security updates keep coming. Pro+ only, only for a released version whose end of servicing is known and not past.
        // The value depends on the installed version, hence a CustomTweak; one choice with wu-defer-feature-updates.
        // Title and Details name the installed version read straight from the registry (DisplayVersion cannot change without
        // a reboot): SystemProfile.Current is empty until its WMI collection finishes, and the catalog is built once.
        var installedVersion = ReadInstalledDisplayVersion();
        yield return new CustomTweak
        {
            Id = UpdatePinId,
            Title = Ru(() => TitleFor(installedVersion)),
            TitleDisplay = () => TitleFor(installedVersion),
            Description = Loc.T("Ежемесячные обновления безопасности приходят как обычно, а ежегодное обновление функций ждёт, пока вы не снимете пункт."),
            Details = UpdatePinDetails(installedVersion),
            Category = TweakCategory.System,
            Risk = TweakRisk.Optional,
            ExclusiveGroup = WuFeatureUpdatesGroup,
            // The row stays while a pin is in place (whoever set it), so an applied pin keeps its state and its ОТКАТИТЬ after the
            // version's end of servicing — exactly when the Dashboard sends the user here; a fresh pin is refused in ApplyUpdatePin.
            AppliesTo = sp => IsUpdatePinRowShown(sp.OsEdition, sp.OsDisplayVersion, UpdatePinToday(), ReadUpdatePinnedTo()),
            NotApplicableReason = Loc.T("Только для Pro и старших редакций и для выпущенных версий Windows 11 с известной и ещё не наступившей " +
                                  "датой окончания поддержки."),
            DetectFunc = DetectUpdatePin,
            ApplyAction = ApplyUpdatePin,
        };

        yield return new RegistryTweak
        {
            Id = "wu-exclude-drivers",
            Title = Loc.Key("Не ставить драйверы через Центр обновления"),
            Description = Loc.T("Центр обновления перестаёт устанавливать драйверы вместе с ежемесячными обновлениями."),
            Details = Loc.T("Полезно, если Windows Update заменяет установленный вами драйвер видеокарты старым. Обновления самой Windows " +
                      "продолжают приходить. Драйверы обновляйте вручную — в разделе «Драйверы» Lycoris или на сайте производителя."),
            Category = TweakCategory.System,
            Risk = TweakRisk.Advanced,
            Warning = Loc.T("Windows перестанет сама ставить любые драйверы, включая полезные (чипсет, Wi-Fi, принтеры, исправления безопасности). " +
                      "Следить за драйверами придётся вам."),
            AppliesTo = sp => !IsHomeEdition(sp.OsEdition),
            NotApplicableReason = Loc.T("Windows Home не поддерживает эту политику."),
            Values = [Lm(WindowsUpdatePolicy, "ExcludeWUDriversInQualityUpdate", 1)],
        };

        // ───── File system, memory, search ─────

        // SAFE-DEFAULT repair (Recommended): NTFS only, writes only when someone turned TRIM off. ReFS is never touched.
        yield return new CustomTweak
        {
            Id = "trim-on",
            Title = Loc.Key("TRIM для SSD включён"),
            Description = Loc.T("Если TRIM (уведомления об удалении) для NTFS кто-то отключил, включает его обратно: SSD снова узнаёт об " +
                          "освобождённых блоках."),
            Details = Loc.T("TRIM в Windows включён по умолчанию; его часто выключают сторонние «оптимизаторы». Без него SSD со временем " +
                      "медленнее записывает данные. На исправной системе пункт ничего не меняет. Меняется только параметр NTFS " +
                      "(DisableDeleteNotification = 0, то же, что «fsutil behavior set DisableDeleteNotify 0»); параметр ReFS, в том числе " +
                      "для Dev Drive, Lycoris не трогает — там TRIM выключен Microsoft намеренно. Microsoft отмечает, что на отдельных " +
                      "накопителях с TRIM производительность падает: если вы выключали его из-за этого, откатите пункт — прежнее значение " +
                      "вернётся. Если TRIM задан групповой политикой, пункт не предлагается. Перезагрузка не нужна."),
            Category = TweakCategory.System,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            AppliesTo = sp => sp.Disks.Any(d => d.IsSsd) && LmInt(FileSystemPolicy, "DisableDeleteNotification") is null,
            NotApplicableReason = Loc.T("SSD не найден, либо TRIM задан групповой политикой."),
            DetectFunc = () => NtfsTrimDisabled() ? TweakState.NotApplied : TweakState.Applied,
            ApplyAction = ctx =>
            {
                if (NtfsTrimDisabled()) SetDword(ctx, RegHive.HKLM, FileSystemKey, "DisableDeleteNotification", 0);
            },
        };

        // SAFE-OPTIONAL repair, never in a profile: turning the schedule off is a legitimate choice in the official UI.
        yield return new ScheduledTaskTweak
        {
            Id = "drive-optimize-schedule-on",
            Title = Loc.Key("Оптимизация дисков по расписанию"),
            Description = Loc.T("Если стандартную задачу «Оптимизация дисков» по расписанию отключили, включает её обратно."),
            Details = Loc.T("Это задача Windows \\Microsoft\\Windows\\Defrag\\ScheduledDefrag, по умолчанию включена: по расписанию (обычно раз " +
                      "в неделю) Windows оптимизирует диски — HDD дефрагментируются, а SSD раз в месяц получают повторный TRIM и, по правилам " +
                      "Microsoft, при необходимости обычную дефрагментацию (чаще всего при включённых точках восстановления) — так задумано " +
                      "Windows. Задачу отключают некоторые «оптимизаторы», но выключить расписание можно и штатно в окне " +
                      "«Оптимизация дисков», поэтому пункт не входит в профили — включайте, если отключали не вы. На исправной системе " +
                      "пункт ничего не меняет. Откат возвращает прежнее состояние задачи."),
            Category = TweakCategory.System,
            Risk = TweakRisk.Optional,
            NotApplicableReason = Loc.T("Задачи «Оптимизация дисков» нет в этой системе."),
            Enable = true,
            Tasks = [@"\Microsoft\Windows\Defrag\ScheduledDefrag"],
        };

        yield return new CustomTweak
        {
            Id = "ntfs-last-access-off",
            Title = Loc.Key("Не обновлять время последнего доступа"),
            Description = Loc.T("NTFS перестаёт записывать время последнего открытия файлов (как fsutil behavior set disablelastaccess 1)."),
            Details = Loc.T("На SSD/NVMe выигрыш ничтожный: NTFS и так обновляет это время пакетно, не чаще раза в час. Исходное значение " +
                      "параметра сохраняется и возвращается при откате. Нужна перезагрузка."),
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = Loc.T("Программы резервного копирования, удалённого хранения и очистки «давно не открытых» файлов могут работать неверно. " +
                      "Ускорения на SSD практически нет."),
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
            Title = Loc.Key("Файл подкачки: автоматический размер"),
            Description = Loc.T("Возвращает стандартную настройку «Автоматически выбирать объём файла подкачки»."),
            Details = Loc.T("Исправление после «оптимизаторов», которые уменьшили или отключили файл подкачки. Он задаёт предел выделяемой памяти: " +
                      "без него игры и браузеры падают с ошибкой нехватки памяти даже при свободной ОЗУ, а дампы сбоев не сохраняются. " +
                      "Если вы сознательно перенесли файл подкачки на другой диск, этот пункт не нужен. Прежняя конфигурация запоминается " +
                      "для отката. Нужна перезагрузка."),
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
            Title = Loc.Key("Не индексировать файлы от батареи"),
            Description = Loc.T("Служба поиска Windows приостанавливает индексацию, пока ноутбук работает от батареи."),
            Details = Loc.T("Немного продлевает работу от батареи; при подключении к сети индексация продолжается. Используется официальная " +
                      "политика Windows Search."),
            Category = TweakCategory.Power,
            Risk = TweakRisk.Optional,
            DefaultIn = ProfileSet.Efficiency, // «Ноутбук: автономность» (laptop-only by AppliesTo)
            Restart = RestartRequirement.Reboot,
            AppliesTo = sp => sp.IsLaptop,
            NotApplicableReason = Loc.T("Только для ноутбуков."),
            Values = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "PreventIndexOnBattery", 1)],
        };

        // ADVANCED, red warning, never in a profile. VBS itself is left alone.
        yield return new CustomTweak
        {
            Id = "hvci-off",
            Title = Loc.Key("Отключить целостность памяти (HVCI)"),
            Description = Loc.T("Отключает «Целостность памяти» (Безопасность Windows → Безопасность устройства → Изоляция ядра)."),
            Details = Loc.T("Microsoft признаёт, что целостность памяти может снижать производительность в играх «в некоторых сценариях»: в тестах " +
                      "потери 0–10 %, в среднем около 5 %, на новых процессорах меньше. Сама виртуализация (VBS) не отключается. " +
                      "Включить обратно можно откатом или переключателем в Безопасности Windows. Нужна перезагрузка."),
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = Loc.T("Снижает защиту ядра от эксплойтов и отключает блокировку уязвимых драйверов. Некоторые античиты (Riot Vanguard, " +
                      "FACEIT) требуют включённую целостность памяти — такие игры могут перестать запускаться. Включайте, только если " +
                      "понимаете риск."),
            AppliesTo = _ => LmInt(HvciKey, "Locked") != 1 && !RegistryHelper.KeyExists(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard"),
            NotApplicableReason = Loc.T("Целостность памяти заблокирована в UEFI или управляется политикой."),
            DetectFunc = () => LmInt(HvciKey, "Enabled") == 1 ? TweakState.NotApplied : TweakState.Applied,
            ApplyAction = ctx =>
            {
                if (LmInt(HvciKey, "Enabled") == 1) SetDword(ctx, RegHive.HKLM, HvciKey, "Enabled", 0);
            },
        };
    }

    private const string EdgePolicy = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    /// <summary>ADMX_FileSys «Disable delete notifications on all volumes»: when set, the machine is managed.</summary>
    private const string FileSystemPolicy = @"SYSTEM\CurrentControlSet\Policies";

    /// <summary>NTFS delete notifications (TRIM) are off: DisableDeleteNotification is set and not 0. Absent = on (default).</summary>
    private static bool NtfsTrimDisabled() => LmInt(FileSystemKey, "DisableDeleteNotification") is { } v && v != 0;

    /// <summary>Microsoft Edge (Stable) is installed machine-wide.</summary>
    private static bool IsEdgeInstalled()
    {
        try
        {
            return new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
                .Select(Environment.GetFolderPath)
                .Where(dir => !string.IsNullOrEmpty(dir))
                .Any(dir => File.Exists(Path.Combine(dir, "Microsoft", "Edge", "Application", "msedge.exe")));
        }
        catch
        {
            return false;
        }
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

    // ───────────────────────────── Windows Update: version pin (TargetReleaseVersion) ─────────────────────────────

    public const string UpdatePinId = "wu-target-version";
    public const string DeferFeatureUpdatesId = "wu-defer-feature-updates";
    /// <summary>The yearly feature update: deferred for 90 days OR pinned to the installed version — never both.</summary>
    public const string WuFeatureUpdatesGroup = "wu-feature-updates";
    private const string NtCurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string UpdatePinProduct = "Windows 11";
    /// <summary>Microsoft updates a pinned device on its own only this long after the version's end of servicing.</summary>
    public const int UpdatePinGraceDays = 60;
    /// <summary>Today's date for the pin rules (tests pin it so that a version's end of servicing does not age them).</summary>
    internal static Func<DateOnly> UpdatePinToday { get; set; } = () => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// End of updates for Windows 11 Home / Pro / Pro Education / Pro for Workstations by version (24 months after release),
    /// from learn.microsoft.com/windows/release-health/windows11-release-information (verified 01.11.2026). Enterprise and
    /// Education get 12 months more, so these dates are the conservative ones for every edition. A version missing here
    /// (an Insider build, a future release) is never offered for pinning.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, DateOnly> Windows11HomeProEndOfServicing =
        new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase)
        {
            ["23H2"] = new(2025, 11, 11),
            ["24H2"] = new(2026, 10, 13),
            ["25H2"] = new(2027, 10, 12),
            ["26H1"] = new(2028, 3, 14), // new devices only (not offered as an in-place update), still a GA version
            ["26H2"] = new(2028, 10, 10),
        };

    /// <summary>End of updates for a released Windows 11 version, or null when the version is unknown to the table.</summary>
    internal static DateOnly? KnownRelease(string? displayVersion) =>
        !string.IsNullOrWhiteSpace(displayVersion) && Windows11HomeProEndOfServicing.TryGetValue(displayVersion.Trim(), out var eos) ? eos : null;

    /// <summary>A version can be pinned when its end of servicing is known and still ahead on <paramref name="today"/>.</summary>
    internal static bool IsPinnable(string? displayVersion, DateOnly today) => KnownRelease(displayVersion) is { } eos && eos >= today;

    /// <summary>Pro and higher editions only (Home ignores TargetReleaseVersion), and only a pinnable version.</summary>
    internal static bool IsUpdatePinApplicable(string edition, string? displayVersion, DateOnly today) =>
        !IsHomeEdition(edition) && IsPinnable(displayVersion, today);

    /// <summary>
    /// Whether the pin row is offered at all: <see cref="IsUpdatePinApplicable"/>, or — on Pro+ — a pin already in place
    /// (<paramref name="pinnedTo"/> from <see cref="ReadUpdatePinnedTo"/>), so the row never turns «неприменимо» on a PC whose
    /// pinned version has just run out of updates and still shows ПРИМЕНЕНО / ЧАСТИЧНО with its undo.
    /// </summary>
    internal static bool IsUpdatePinRowShown(string edition, string? displayVersion, DateOnly today, string? pinnedTo) =>
        !IsHomeEdition(edition) && (IsPinnable(displayVersion, today) || !string.IsNullOrWhiteSpace(pinnedTo));

    /// <summary>The two values of «Отложить крупные обновления на 90 дней».</summary>
    internal static readonly IReadOnlyList<RegSet> DeferFeatureUpdatesValues =
    [
        Lm(WindowsUpdatePolicy, "DeferFeatureUpdates", 1),
        Lm(WindowsUpdatePolicy, "DeferFeatureUpdatesPeriodInDays", 90),
    ];

    /// <summary>Writes the deferral like a RegistryTweak would, but refuses while Lycoris's version pin is active (one of two).</summary>
    internal static void ApplyDeferFeatureUpdates(TweakContext ctx)
    {
        if (ctx.Session.Journal.HasActiveSource("tweak:" + UpdatePinId))
            throw new InvalidOperationException(Loc.T("сначала откатите «Остаться на текущей версии Windows 11» — выбирается один из двух пунктов"));
        foreach (var v in DeferFeatureUpdatesValues)
        {
            if (Matches(v)) continue;
            ctx.SetRegistry(v.Hive, v.Path, v.Name, v.Kind, v.Value);
        }
    }

    internal static string TitleFor(string? displayVersion) =>
        string.IsNullOrWhiteSpace(displayVersion)
            ? Loc.T("Остаться на текущей версии Windows 11")
            : Loc.Tf("Остаться на текущей версии Windows 11 ({0})", displayVersion.Trim());

    /// <summary>dd.MM.yyyy, as the dashboard prints dates.</summary>
    internal static string DateText(DateOnly date) => date.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);

    internal static string UpdatePinDetails(string? displayVersion)
    {
        var v = string.IsNullOrWhiteSpace(displayVersion) ? null : displayVersion.Trim();
        // One whole sentence per variant: word order of the translation must not depend on the pieces.
        return KnownRelease(v) is { } eos
            ? Loc.Tf("Политика TargetReleaseVersion. Текущая версия {0} получает обновления до {1}; " +
                     "по данным Microsoft закреплённое устройство автоматически обновится лишь через {2} дней после этой даты — " +
                     "снимите пункт заранее, иначе до двух месяцев без обновлений безопасности. Lycoris предупредит об этом на Дашборде за 90 дней. " +
                     "В Параметрах появится «Некоторыми параметрами управляет ваша организация». Работает в Pro и старших редакциях; " +
                     "выбирается вместо «Отложить крупные обновления на 90 дней».", v, DateText(eos), UpdatePinGraceDays)
            : Loc.Tf("Политика TargetReleaseVersion. Текущая версия получает обновления до своей даты окончания поддержки (24 месяца с выхода для Home и Pro); " +
                     "по данным Microsoft закреплённое устройство автоматически обновится лишь через {0} дней после этой даты — " +
                     "снимите пункт заранее, иначе до двух месяцев без обновлений безопасности. Lycoris предупредит об этом на Дашборде за 90 дней. " +
                     "В Параметрах появится «Некоторыми параметрами управляет ваша организация». Работает в Pro и старших редакциях; " +
                     "выбирается вместо «Отложить крупные обновления на 90 дней».", UpdatePinGraceDays);
    }

    /// <summary>The installed version (DisplayVersion of Windows NT\CurrentVersion), read live.</summary>
    internal static string? ReadInstalledDisplayVersion() => RegistryHelper.ReadString(RegHive.HKLM, NtCurrentVersion, "DisplayVersion");

    /// <summary>TargetReleaseVersionInfo when the TargetReleaseVersion policy is on (whoever set it), else null.</summary>
    internal static string? ReadUpdatePinnedTo()
    {
        if (LmInt(WindowsUpdatePolicy, "TargetReleaseVersion") != 1) return null;
        var info = RegistryHelper.ReadString(RegHive.HKLM, WindowsUpdatePolicy, "TargetReleaseVersionInfo");
        return string.IsNullOrWhiteSpace(info) ? null : info.Trim();
    }

    /// <summary>Applied = all three policy values match (TargetReleaseVersionInfo = the installed version); Partial = some.</summary>
    internal static TweakState DetectUpdatePin()
    {
        var installed = ReadInstalledDisplayVersion();
        var matches = 0;
        if (string.Equals(RegistryHelper.ReadString(RegHive.HKLM, WindowsUpdatePolicy, "ProductVersion"), UpdatePinProduct, StringComparison.OrdinalIgnoreCase)) matches++;
        if (LmInt(WindowsUpdatePolicy, "TargetReleaseVersion") == 1) matches++;
        var info = RegistryHelper.ReadString(RegHive.HKLM, WindowsUpdatePolicy, "TargetReleaseVersionInfo");
        if (!string.IsNullOrWhiteSpace(installed) && string.Equals(info?.Trim(), installed.Trim(), StringComparison.OrdinalIgnoreCase)) matches++;
        return StateOf(matches, 3);
    }

    /// <summary>
    /// Writes the three policy values through the session (absent → the journal deletes them on undo, and the policy key
    /// Lycoris created goes with them). Refuses while the 90-day deferral is active: the two are one choice.
    /// </summary>
    internal static void ApplyUpdatePin(TweakContext ctx)
    {
        if (ctx.Session.Journal.HasActiveSource("tweak:" + DeferFeatureUpdatesId))
            throw new InvalidOperationException(Loc.T("сначала откатите «Отложить крупные обновления на 90 дней» — выбирается один из двух пунктов"));
        var installed = ReadInstalledDisplayVersion()?.Trim();
        if (string.IsNullOrWhiteSpace(installed))
            throw new InvalidOperationException(Loc.T("не удалось прочитать версию Windows (DisplayVersion)"));
        if (KnownRelease(installed) is not { } eos)
            throw new InvalidOperationException(Loc.Tf("версия {0} не входит в список выпущенных версий Windows 11 с известной датой окончания поддержки", installed));
        if (eos < UpdatePinToday())
            throw new InvalidOperationException(Loc.Tf("поддержка версии {0} закончилась {1} — закреплять её нельзя", installed, DateText(eos)));

        if (!string.Equals(RegistryHelper.ReadString(RegHive.HKLM, WindowsUpdatePolicy, "ProductVersion"), UpdatePinProduct, StringComparison.Ordinal))
            ctx.SetRegistry(RegHive.HKLM, WindowsUpdatePolicy, "ProductVersion", RegistryValueKind.String, UpdatePinProduct);
        if (LmInt(WindowsUpdatePolicy, "TargetReleaseVersion") != 1)
            SetDword(ctx, RegHive.HKLM, WindowsUpdatePolicy, "TargetReleaseVersion", 1);
        if (!string.Equals(RegistryHelper.ReadString(RegHive.HKLM, WindowsUpdatePolicy, "TargetReleaseVersionInfo"), installed, StringComparison.Ordinal))
            ctx.SetRegistry(RegHive.HKLM, WindowsUpdatePolicy, "TargetReleaseVersionInfo", RegistryValueKind.String, installed);
    }
}
