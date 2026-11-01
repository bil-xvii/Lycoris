using System.ServiceProcess;
using Monolith.Core;

namespace Monolith.Tweaks;

/// <summary>Diagnostic data, advertising, activity history, AI features (general_tweaks.md §4, §5, §10).</summary>
public static partial class TweakCatalog
{
    private const string WindowsAiPolicy = @"Software\Policies\Microsoft\Windows\WindowsAI";
    private const string PaintPolicy = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint";

    private static IEnumerable<Tweak> PrivacyTweaks()
    {
        // SAFE-DEFAULT (Recommended) through the Settings-backed value; never raises a lower level.
        yield return new CustomTweak
        {
            Id = "telemetry-required",
            Title = "Только обязательная диагностика",
            Description = "Windows отправляет в Microsoft только обязательные диагностические данные, без необязательных.",
            Details = "Тот же переключатель, что в Параметрах → Конфиденциальность → Диагностика и отзывы. В Home и Pro это минимальный " +
                      "уровень: полностью отключить диагностику можно только в Enterprise и Education. Если уровень уже ниже, MONOLITH его " +
                      "не повышает. Участникам программы Windows Insider пункт не предлагается — ей нужны необязательные данные.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Recommended,
            AppliesTo = _ => !IsInsider() && LmInt(DataCollectionPolicy, "AllowTelemetry") is null or <= 1,
            NotApplicableReason = "Уровень диагностики задан политикой или ПК участвует в программе Windows Insider.",
            DetectFunc = () =>
            {
                if (LmInt(DataCollectionPolicy, "AllowTelemetry") is { } policy) return policy <= 1 ? TweakState.Applied : TweakState.NotApplicable;
                return LmInt(DataCollectionSettings, "AllowTelemetry") is <= 1 ? TweakState.Applied : TweakState.NotApplied;
            },
            ApplyAction = ctx =>
            {
                if (LmInt(DataCollectionSettings, "AllowTelemetry") is <= 1) return;
                SetDword(ctx, RegHive.HKLM, DataCollectionSettings, "AllowTelemetry", 1);
            },
        };

        // ADVANCED: the policy route locks the Settings toggle.
        yield return new CustomTweak
        {
            Id = "telemetry-policy",
            Title = "Закрепить обязательную диагностику политикой",
            Description = "Тот же уровень «только обязательные данные», но через групповую политику — его нельзя будет поднять из Параметров.",
            Details = "Нужен, если обновления или программы снова включают необязательную диагностику. В Параметрах появится надпись " +
                      "«Некоторыми параметрами управляет ваша организация» — для политики это нормально. Откат удаляет политику.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Advanced,
            Warning = "В Параметрах появится сообщение «Некоторыми параметрами управляет ваша организация», а переключатель уровня " +
                      "диагностики станет недоступен до отката.",
            AppliesTo = _ => !IsInsider(),
            NotApplicableReason = "ПК участвует в программе Windows Insider — ей нужны необязательные диагностические данные.",
            DetectFunc = () => LmInt(DataCollectionPolicy, "AllowTelemetry") is <= 1 ? TweakState.Applied : TweakState.NotApplied,
            ApplyAction = ctx =>
            {
                if (LmInt(DataCollectionPolicy, "AllowTelemetry") is <= 1) return;
                SetDword(ctx, RegHive.HKLM, DataCollectionPolicy, "AllowTelemetry", 1);
            },
        };

        // SAFE-OPTIONAL (Windows 11).
        yield return new RegistryTweak
        {
            Id = "diagnostic-logs-limit",
            Title = "Ограничить диагностические журналы и дампы",
            Description = "Политики Windows ограничивают сбор дополнительных журналов и дампов памяти для отправки в Microsoft.",
            Details = "Касается только данных, которые уходят в Microsoft; отчёты о сбоях и журналы на самом ПК остаются. Уровень обязательной " +
                      "диагностики не меняется.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Optional,
            AppliesTo = sp => sp.IsWindows11,
            NotApplicableReason = "Только для Windows 11.",
            Values =
            [
                Lm(DataCollectionPolicy, "LimitDiagnosticLogCollection", 1),
                Lm(DataCollectionPolicy, "LimitDumpCollection", 1),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "advertising-id-off",
            Title = "Рекламный идентификатор",
            Description = "Отключает рекламный идентификатор, по которому приложения показывают персональную рекламу.",
            Details = "Параметры → Конфиденциальность → Общие. Реклама в приложениях остаётся, но перестаёт быть персонализированной.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Values = [Cu(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)],
        };

        yield return new RegistryTweak
        {
            Id = "tailored-experiences-off",
            Title = "Персонализированные возможности",
            Description = "Microsoft перестаёт использовать ваши диагностические данные для персональных советов, рекламы и рекомендаций.",
            Details = "Параметры → Конфиденциальность → Диагностика и отзывы → «Персонализированные возможности». На работу Windows не влияет.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Recommended,
            Values = [Cu(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)],
        };

        yield return new RegistryTweak
        {
            Id = "activity-history-off",
            Title = "Журнал действий",
            Description = "Windows перестаёт собирать и отправлять журнал открытых приложений, файлов и сайтов.",
            Details = "Используются официальные политики (PublishUserActivities, UploadUserActivities, EnableActivityFeed). " +
                      "Полностью вступает в силу после выхода из системы.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Recommended,
            Restart = RestartRequirement.SignOut,
            Values =
            [
                Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0),
                Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "feedback-requests-off",
            Title = "Запросы отзывов",
            Description = "Windows больше не спрашивает «Как вам Windows?» и не просит оставить отзыв.",
            Details = "Соответствует значению «Никогда» в Параметрах → Конфиденциальность → Диагностика и отзывы → Частота формирования отзывов. " +
                      "Центр отзывов работает, если открыть его самостоятельно.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Values =
            [
                Cu(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
                RegSet.Delete(RegHive.HKCU, @"Software\Microsoft\Siuf\Rules", "PeriodInNanoSeconds"),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "language-list-access-off",
            Title = "Список языков для сайтов",
            Description = "Сайты не получают список языков Windows для подбора локализованного содержимого.",
            Details = "Параметры → Конфиденциальность → Общие. Браузеры по-прежнему используют собственные языковые настройки.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Recommended,
            Values = [Cu(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1)],
        };

        yield return new RegistryTweak
        {
            Id = "inking-typing-personalization-off",
            Title = "Персонализация рукописного ввода и набора",
            Description = "Windows перестаёт собирать примеры рукописного ввода, набранного текста и контакты для личного словаря.",
            Details = "Подсказки сенсорной клавиатуры и распознавание рукописного ввода станут менее персональными. " +
                      "Действует после выхода из системы.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.SignOut,
            Values =
            [
                Cu(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
                Cu(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
                Cu(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0),
                Cu(@"Software\Microsoft\Input\TIPC", "Enabled", 0),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "location-off",
            Title = "Отключить определение местоположения",
            Description = "Запрещает Windows и приложениям доступ к местоположению устройства.",
            Details = "Перестанут работать «Поиск устройства», автоматический часовой пояс, погода и карты по местоположению. " +
                      "На ноутбуках обычно лучше оставить включённым.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Optional,
            Values = [RegSet.Str(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny")],
        };

        yield return new RegistryTweak
        {
            Id = "remote-assistance-off",
            Title = "Отключить удалённого помощника",
            Description = "Запрещает входящие подключения «Удалённого помощника Windows» (msra.exe).",
            Details = "Закрывает редко используемый способ удалённого доступа к ПК. Приложение «Быстрая помощь» (Quick Assist) " +
                      "продолжает работать.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Recommended,
            Values = [Lm(@"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", 0)],
        };

        // Research marks it SAFE-DEFAULT, but the policy deletes existing Recall snapshots (user data):
        // MONOLITH never deletes user data by default, so it is offered unchecked.
        yield return new RegistryTweak
        {
            Id = "recall-snapshots-off",
            Title = "Отключить снимки Recall",
            Description = "Запрещает Windows Recall сохранять снимки экрана. Уже сохранённые снимки будут удалены.",
            Details = "Recall есть только на Copilot+ PC с NPU; на остальных компьютерах политика ничего не меняет. Работает в Windows 11 24H2 " +
                      "и новее. Удалённые снимки откат не вернёт — он лишь снова разрешит их сохранение. Действует после выхода из системы.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.SignOut,
            AppliesTo = sp => sp.OsBuild >= 26100,
            NotApplicableReason = "Recall появился в Windows 11 24H2.",
            Values = [Cu(WindowsAiPolicy, "DisableAIDataAnalysis", 1)],
        };

        yield return new RegistryTweak
        {
            Id = "click-to-do-off",
            Title = "Отключить Click to Do",
            Description = "Отключает Click to Do — анализ содержимого экрана с помощью ИИ.",
            Details = "Есть только на Copilot+ PC с Windows 11 24H2 и новее; на других ПК ничего не меняет. Действует после выхода из системы.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.SignOut,
            AppliesTo = sp => sp.OsBuild >= 26100,
            NotApplicableReason = "Click to Do появился в Windows 11 24H2.",
            Values = [Cu(WindowsAiPolicy, "DisableClickToDo", 1)],
        };

        // SAFE-OPTIONAL (Policy CSP WindowsAI, device scope; officially Pro and higher).
        yield return new RegistryTweak
        {
            Id = "paint-ai-off",
            Title = "Paint без функций ИИ",
            Description = "Отключает в Paint облачные функции ИИ: Cocreator, Image Creator и генеративную заливку.",
            Details = "Официальные политики Microsoft (DisableCocreator, DisableImageCreator, DisableGenerativeFill) для всех пользователей " +
                      "ПК. Paint работает как прежде — пропадают только кнопки ИИ, отправлять рисунки в облачный сервис ИИ из него больше нельзя. Официально " +
                      "поддерживается в Pro и старших редакциях Windows 11 (22H2 со сборки 22621.4870, 24H2 — 26100.3360); Paint в Home " +
                      "обычно тоже соблюдает политики, но Microsoft это не гарантирует. Откат удаляет политики. Действует после " +
                      "перезапуска Paint.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Optional,
            AppliesTo = sp => sp.OsBuild >= 22621,
            NotApplicableReason = "Функции ИИ в Paint есть только в Windows 11 22H2 и новее.",
            Values =
            [
                Lm(PaintPolicy, "DisableCocreator", 1),
                Lm(PaintPolicy, "DisableGenerativeFill", 1),
                Lm(PaintPolicy, "DisableImageCreator", 1),
            ],
        };

        // SAFE-OPTIONAL (documented Notepad policy value, machine-wide, Notepad 11.2503.16+).
        yield return new RegistryTweak
        {
            Id = "notepad-ai-off",
            Title = "Блокнот без функций ИИ",
            Description = "Отключает в Блокноте инструменты письма Copilot: «Переписать», «Кратко изложить» и другие функции ИИ.",
            Details = "Официальный способ Microsoft — значение DisableAIFeatures в политиках Блокнота, для всех пользователей ПК. Блокнот " +
                      "работает как прежде, пропадают только функции ИИ, отправлять текст в облачный сервис ИИ из него больше нельзя. Действует в Блокноте " +
                      "11.2503.16 и новее на Windows 11 после его перезапуска. Откат удаляет значение.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Optional,
            AppliesTo = sp => sp.OsBuild >= 22621,
            NotApplicableReason = "Функции ИИ в Блокноте есть только в Windows 11 22H2 и новее.",
            Values = [Lm(@"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1)],
        };

        // ADVANCED. Manual (not Disabled): the service can still be started on demand.
        yield return new ServiceTweak
        {
            Id = "diagtrack-manual",
            Title = "Служба телеметрии — вручную",
            Description = "Служба «Функциональные возможности для подключённых пользователей и телеметрия» (DiagTrack) больше не запускается автоматически.",
            Details = "После перезагрузки фоновая отправка диагностики не стартует сама. Поддерживаемый способ уменьшить телеметрию — пункт " +
                      "«Только обязательная диагностика»; служба — дополнительная мера. Служба остаётся в системе и может быть запущена " +
                      "по требованию. Откат возвращает прежний тип запуска.",
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = "Перестанут нормально работать программа предварительной оценки Windows (Insider), часть функций Центра отзывов " +
                      "и средств диагностики. Используйте вместе с пунктом «Только обязательная диагностика», а не вместо него.",
            AppliesTo = _ => !IsInsider(),
            NotApplicableReason = "ПК участвует в программе Windows Insider — ей нужна служба телеметрии.",
            Services = [("DiagTrack", ServiceStartMode.Manual)],
        };
    }
}
