using System.Globalization;
using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Tweaks;

/// <summary>
/// Tips/ads, Start and search, Explorer quality of life, visual effects and input
/// (general_tweaks.md §3, §4, §7; gaming_tweaks.md §2 D, H).
/// </summary>
public static partial class TweakCatalog
{
    private static IEnumerable<Tweak> InterfaceTweaks()
    {
        // ───── Tips, suggestions and ads (SAFE-DEFAULT in Normal and Recommended) ─────

        yield return new RegistryTweak
        {
            Id = "ads-suggestions-off",
            Title = "Советы и предложения Windows",
            Description = "Отключает советы по Windows, «экран приветствия» после обновлений и напоминания «завершить настройку устройства».",
            Details = "Это те же переключатели, что в Параметрах → Система → Уведомления → Дополнительные параметры. На производительность " +
                      "не влияет — убирает назойливые уведомления и полноэкранные предложения после обновлений. Отмена — откатом в MONOLITH " +
                      "или в Параметрах.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Values =
            [
                Cu(ContentDelivery, "SubscribedContent-338389Enabled", 0),
                Cu(ContentDelivery, "SoftLandingEnabled", 0),
                Cu(ContentDelivery, "SubscribedContent-310093Enabled", 0),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "ads-settings-suggestions-off",
            Title = "Рекомендации в Параметрах",
            Description = "Убирает рекламные блоки и «рекомендуемое содержимое» из приложения «Параметры».",
            Details = "Соответствует переключателю Параметры → Конфиденциальность → Общие → «Показывать рекомендуемое содержимое в приложении " +
                      "Параметры». Функциональность не меняется.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Values =
            [
                Cu(ContentDelivery, "SubscribedContent-338393Enabled", 0),
                Cu(ContentDelivery, "SubscribedContent-353694Enabled", 0),
                Cu(ContentDelivery, "SubscribedContent-353696Enabled", 0),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "ads-lockscreen-tips-off",
            Title = "Советы на экране блокировки",
            Description = "Убирает «забавные факты и советы» поверх изображения экрана блокировки.",
            Details = "Фоновое изображение «Windows: интересное» (Spotlight) остаётся — убирается только текстовая реклама поверх него.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Values =
            [
                Cu(ContentDelivery, "SubscribedContent-338387Enabled", 0),
                Cu(ContentDelivery, "RotatingLockScreenOverlayEnabled", 0),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "ads-start-suggestions-off",
            Title = "Реклама в меню «Пуск»",
            Description = "Убирает из меню «Пуск» рекомендации приложений и советов, а также напоминания учётной записи Microsoft.",
            Details = "В Windows 11 — «Показывать рекомендации советов, ярлыков и новых приложений» и уведомления учётной записи; " +
                      "в Windows 10 — «Иногда показывать предложения в меню «Пуск»». Недавние файлы и приложения остаются. " +
                      "Применяется после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Restart = RestartRequirement.Explorer,
            Values =
            [
                Cu(ContentDelivery, "SubscribedContent-338388Enabled", 0),
                Cu(ContentDelivery, "SystemPaneSuggestionsEnabled", 0),
                Cu(ExplorerAdvanced, "Start_IrisRecommendations", 0),
                Cu(ExplorerAdvanced, "Start_AccountNotifications", 0),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "ads-explorer-sync-notifications-off",
            Title = "Реклама в Проводнике",
            Description = "Отключает уведомления поставщиков синхронизации — рекламу OneDrive и Microsoft 365 в окнах Проводника.",
            Details = "Сам OneDrive продолжает работать как прежде. Применяется после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Restart = RestartRequirement.Explorer,
            Values = [Cu(ExplorerAdvanced, "ShowSyncProviderNotifications", 0)],
        };

        // SAFE-OPTIONAL (edition-gated policy).
        yield return new RegistryTweak
        {
            Id = "start-hide-recommended",
            Title = "Скрыть раздел «Рекомендуем» в «Пуске»",
            Description = "Политика полностью убирает раздел рекомендаций (недавние файлы и приложения) из меню «Пуск».",
            Details = "Официально политика поддерживается в Windows 11 SE и Education; в Home и Pro Windows может её игнорировать — тогда " +
                      "пункт ничего не изменит, и его можно откатить. Применяется после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Explorer,
            AppliesTo = sp => sp.IsWindows11,
            NotApplicableReason = "Только для Windows 11.",
            Values = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\Explorer", "HideRecommendedSection", 1)],
        };

        // SAFE-OPTIONAL (Policy CSP Start, User scope, Windows 11 22H2 KB5027303+).
        yield return new RegistryTweak
        {
            Id = "start-web-recommendations-off",
            Title = "«Пуск» без рекомендаций сайтов",
            Description = "Убирает из раздела «Рекомендуем» меню «Пуск» сайты, которые Windows подбирает по журналу браузера.",
            Details = "Официальная политика Windows (HideRecommendedPersonalizedSites) для текущего пользователя. Такие рекомендации " +
                      "появляются, только если журнал браузера передаётся Windows и в нём достаточно сайтов, — иначе пункт ничего заметного " +
                      "не изменит. Недавние файлы и приложения в «Рекомендуем» остаются. Переключатель «Показывать веб-сайты из журнала " +
                      "браузера» в Параметрах станет недоступен с пометкой об управлении организацией. Официально поддерживается в Pro и " +
                      "старших редакциях. Откат удаляет политику. Применяется после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Explorer,
            AppliesTo = sp => sp.OsBuild > 22621 || sp.OsBuild == 22621 && sp.OsUbr >= 1928,
            NotApplicableReason = "Нужна Windows 11 22H2 с обновлением KB5027303 или новее.",
            Values = [Cu(@"Software\Policies\Microsoft\Windows\Explorer", "HideRecommendedPersonalizedSites", 1)],
        };

        // SAFE-OPTIONAL (Windows 11). The user's own Settings toggle, not a policy.
        yield return new RegistryTweak
        {
            Id = "start-phone-panel-off",
            Title = "«Пуск» без панели телефона",
            Description = "Убирает из меню «Пуск» боковую панель «Связи с телефоном» с сообщениями, звонками и фото телефона.",
            Details = "Тот же переключатель, что Параметры → Персонализация → Пуск → «Показывать мобильное устройство в меню «Пуск»»: " +
                      "это не политика, поэтому вернуть панель можно и там. Само приложение «Связь с телефоном» работает как прежде. Пункт " +
                      "показывается, только если панель есть в вашей версии Windows и «Связи с телефоном». Применяется после перезапуска " +
                      "Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Explorer,
            AppliesTo = sp => sp.IsWindows11 && RegistryHelper.KeyExists(RegHive.HKCU, StartPhoneCompanion),
            NotApplicableReason = "Панели телефона в меню «Пуск» нет: нужна Windows 11 и новая версия «Связи с телефоном».",
            Values = [Cu(StartPhoneCompanion, "IsEnabled", 0)],
        };

        // SAFE-OPTIONAL, Pro+ (Policy CSP Experience; Home ignores it, so it is not offered there).
        yield return new RegistryTweak
        {
            Id = "spotlight-third-party-off",
            Title = "Без рекламы сторонних приложений в «Интересном»",
            Description = "«Windows: интересное» и советы Windows перестают продвигать приложения и содержимое сторонних издателей.",
            Details = "Официальная политика Windows (DisableThirdPartySuggestions) для текущего пользователя; работает в Pro и старших " +
                      "редакциях. Сами картинки «Windows: интересное» на экране блокировки и рабочем столе остаются. Эффект узкий: основную " +
                      "рекламу уже убирают пункты «Советы и предложения Windows» и «Советы на экране блокировки», этот закрывает ещё и " +
                      "продвижение сторонних приложений. Остальные политики «интересного» работают только в Enterprise и Education, поэтому " +
                      "MONOLITH их не предлагает. Откат удаляет политику. Действует после выхода из системы.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.SignOut,
            AppliesTo = sp => !IsHomeEdition(sp.OsEdition),
            NotApplicableReason = "Windows Home не поддерживает эту политику.",
            Values = [Cu(@"Software\Policies\Microsoft\Windows\CloudContent", "DisableThirdPartySuggestions", 1)],
        };

        // ───── Search, Widgets, Copilot ─────

        yield return new RegistryTweak
        {
            Id = "bing-start-search-off",
            Title = "Поиск без Bing",
            Description = "Поиск в меню «Пуск» ищет только на компьютере — без веб-результатов и подсказок Bing.",
            Details = "Поиск файлов, приложений и параметров становится быстрее и не отправляет набранный текст в интернет. Побочный эффект: " +
                      "в строке поиска Проводника перестают показываться недавние запросы. Применяется после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Restart = RestartRequirement.Explorer,
            Values =
            [
                Cu(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
                Cu(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "search-highlights-off",
            Title = "Без «подсветки» поиска",
            Description = "Убирает из поиска тематические картинки, «интересное сегодня» и новости.",
            Details = "Поле и окно поиска показывают только ваши результаты. Применяется после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Restart = RestartRequirement.Explorer,
            Values = [Cu(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0)],
        };

        yield return new RegistryTweak
        {
            Id = "widgets-off",
            Title = "Отключить Виджеты",
            Description = "Убирает доску виджетов, её кнопку на панели задач и фоновые процессы WebView2, которые её обслуживают.",
            Details = "Используется официальная политика Microsoft (AllowNewsAndInterests). По данным Microsoft, она также убирает " +
                      "«погоду и другое» с экрана блокировки. Пакет виджетов не удаляется, поэтому всё возвращается откатом. Применяется " +
                      "после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Restart = RestartRequirement.Explorer,
            AppliesTo = sp => sp.IsWindows11,
            NotApplicableReason = "Только для Windows 11.",
            Values = [Lm(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)],
        };

        // SAFE-DEFAULT (Recommended), Windows 10 only: the Windows 10 counterpart of widgets-off (Policy CSP Feeds).
        yield return new RegistryTweak
        {
            Id = "news-interests-off",
            Title = "Отключить «Новости и интересы»",
            Description = "Windows 10: убирает с панели задач ленту новостей и погоды вместе с её фоновыми процессами.",
            Details = "Официальная политика Microsoft (EnableFeeds = 0) — то же, что пункт «Отключить Виджеты» для Windows 11. По данным " +
                      "Microsoft, в Windows 10 она также убирает «погоду и другое» с экрана блокировки, а пункт «Новости и интересы» " +
                      "пропадает из меню панели задач до отката. Официально политика поддерживается в Pro и старших редакциях; Home её " +
                      "обычно тоже соблюдает, но Microsoft это не гарантирует. Откат удаляет политику, и возвращается ваша прежняя " +
                      "настройка ленты. Применяется после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Restart = RestartRequirement.Explorer,
            AppliesTo = sp => sp.OsBuild is >= 19041 and < 22000,
            NotApplicableReason = "Только для Windows 10 — в Windows 11 используется пункт «Отключить Виджеты».",
            Values = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", 0)],
        };

        yield return new RegistryTweak
        {
            Id = "copilot-legacy-off",
            Title = "Отключить панель Copilot",
            Description = "Отключает встроенную боковую панель Copilot и её кнопку на панели задач.",
            Details = "Действует только в Windows 11 22H2 и 23H2 — в новых версиях Copilot стал отдельным приложением из Store, на которое " +
                      "эта политика не влияет. Применяется после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Explorer,
            AppliesTo = sp => sp.OsBuild is 22621 or 22631,
            NotApplicableReason = "Только для Windows 11 22H2 и 23H2: в новых версиях Copilot — отдельное приложение.",
            Values =
            [
                Cu(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                Cu(ExplorerAdvanced, "ShowCopilotButton", 0),
            ],
        };

        // ───── Explorer quality of life ─────

        yield return new RegistryTweak
        {
            Id = "file-extensions-show",
            Title = "Показывать расширения файлов",
            Description = "Проводник показывает расширения всех файлов (.exe, .pdf, .zip).",
            Details = "Повышает безопасность: сразу видна подделка вроде «счёт.pdf.exe». Обновите окно Проводника (F5), чтобы увидеть изменение.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Normal | ProfileSet.Recommended,
            Restart = RestartRequirement.Explorer,
            Values = [Cu(ExplorerAdvanced, "HideFileExt", 0)],
        };

        yield return new RegistryTweak
        {
            Id = "hidden-files-show",
            Title = "Показывать скрытые файлы",
            Description = "Проводник показывает скрытые файлы и папки (например, AppData).",
            Details = "Защищённые системные файлы по-прежнему скрыты. Удобно, когда нужно вручную найти конфиги и сохранения игр.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Explorer,
            Values = [Cu(ExplorerAdvanced, "Hidden", 1)],
        };

        yield return new RegistryTweak
        {
            Id = "explorer-open-this-pc",
            Title = "Проводник открывается на «Этот компьютер»",
            Description = "Новое окно Проводника открывается со списком дисков вместо «Главной».",
            Details = "Вопрос удобства, на скорость не влияет. Действует для новых окон Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Explorer,
            Values = [Cu(ExplorerAdvanced, "LaunchTo", 1)],
        };

        // SAFE-OPTIONAL. Settings-backed user values (no policy, no UI lock); ShowFrequent stays on (Home renders blank otherwise).
        yield return new RegistryTweak
        {
            Id = "explorer-home-private",
            Title = "«Главная» Проводника без недавних файлов",
            Description = "На «Главной» Проводника (в Windows 10 — на панели быстрого доступа) не показываются недавно открытые файлы и " +
                          "файлы из учётной записи Microsoft 365 / OneDrive.",
            Details = "Те же флажки, что Параметры папок → Конфиденциальность: «Показывать недавно использовавшиеся файлы», «Показывать " +
                      "файлы с Office.com» и в новых сборках Windows 11 «Показать раздел рекомендаций» (если флажка в вашей версии нет, " +
                      "значение ни на что не влияет). Полезно при демонстрации экрана: недавние документы не видны посторонним, а Проводник " +
                      "не запрашивает облачные рекомендации. Закреплённые и часто используемые папки остаются — «часто используемые папки» " +
                      "MONOLITH не выключает, иначе «Главная» становится пустой. Журнал и списки переходов не удаляются. Политика не " +
                      "используется, поэтому флажки можно вернуть и вручную. Применяется после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            DefaultIn = ProfileSet.Streaming, // SAFE-OPTIONAL pre-check of «Стриминг и запись»: recent files never go on air
            Restart = RestartRequirement.Explorer,
            Values =
            [
                Cu(ExplorerKey, "ShowRecent", 0),
                Cu(ExplorerKey, "ShowCloudFilesInQuickAccess", 0),
                Cu(ExplorerKey, "ShowRecommendations", 0),
            ],
        };

        yield return new RegistryTweak
        {
            Id = "classic-context-menu",
            Title = "Классическое контекстное меню",
            Description = "Правый щелчок сразу открывает полное меню, без пункта «Показать дополнительные параметры».",
            Details = "Создаётся пустой COM-ключ, который отключает новое меню Windows 11 (стандартный способ, работает до сих пор). Microsoft " +
                      "может изменить это в будущих обновлениях — тогда меню просто останется новым. Откат удаляет ключ. Применяется после " +
                      "перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Explorer,
            AppliesTo = sp => sp.IsWindows11,
            NotApplicableReason = "Только для Windows 11 — в Windows 10 меню и так классическое.",
            Values = [RegSet.Str(RegHive.HKCU, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "")],
        };

        // SAFE-OPTIONAL, default in Recommended per research.
        yield return new RegistryTweak
        {
            Id = "taskbar-end-task",
            Title = "«Завершить задачу» на панели задач",
            Description = "Добавляет в меню правого клика по значку на панели задач пункт «Завершить задачу» — для зависших игр и программ.",
            Details = "Работает как снятие задачи в Диспетчере задач: процесс закрывается мгновенно, несохранённые данные теряются. Сам пункт " +
                      "ничего не закрывает — он лишь появляется в меню. Применяется после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            DefaultIn = ProfileSet.Recommended,
            Restart = RestartRequirement.Explorer,
            AppliesTo = sp => sp.OsBuild > 22621 || sp.OsBuild == 22621 && sp.OsUbr >= 2361,
            NotApplicableReason = "Нужна Windows 11 22H2 с обновлением KB5030310 или новее.",
            Values = [Cu(ExplorerAdvanced + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1)],
        };

        // ───── Look and feel ─────

        yield return new CustomTweak
        {
            Id = "visual-effects-performance",
            Title = "Визуальные эффекты: быстродействие",
            Description = "Отключает анимации и тени интерфейса (меню, подсказки, окна, панель задач), но сохраняет сглаживание шрифтов и эскизы.",
            Details = "Интерфейс ощущается быстрее, особенно на слабой встроенной графике. В играх эффекта нет: полноэкранный вывод обходит " +
                      "композицию рабочего стола. Прежнее значение каждого эффекта сохраняется и восстанавливается при откате. Анимации " +
                      "выключаются сразу, эффекты Проводника и панели задач — после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Explorer,
            DetectFunc = () =>
            {
                var spi = ReadVisualEffects();
                if (spi is null) return TweakState.Unknown;
                var registry = VisualEffectsRegistry();
                var matches = spi.Flags.Count(f => !f) + (spi.MinAnimate == 0 ? 1 : 0) + registry.Count(Matches);
                return StateOf(matches, spi.Flags.Length + 1 + registry.Length);
            },
            ApplyAction = ctx =>
            {
                var spi = ReadVisualEffects() ?? throw new InvalidOperationException("не удалось прочитать текущие визуальные эффекты");
                if (!spi.AllOff)
                {
                    var off = VisualEffectFlags.Select(f => (f.Set, false)).ToList();
                    var original = VisualEffectFlags.Select((f, i) => (f.Set, spi.Flags[i])).ToList();
                    var wasOn = VisualEffectFlags.Where((_, i) => spi.Flags[i]).Select(f => f.Name).ToList();
                    if (spi.MinAnimate != 0) wasOn.Add("анимация окон");
                    RunJournaled(ctx, "spi:visual-effects", PowerShellExe, PowerShellArgs(SpiScript(off, 0, rollback: original, rollbackMinAnimate: spi.MinAnimate)),
                        PowerShellExe, PowerShellArgs(SpiScript(original, spi.MinAnimate)),
                        "включены: " + string.Join(", ", wasOn), "анимации и тени выключены", OutputEncoding.Utf8);
                }
                foreach (var v in VisualEffectsRegistry())
                    if (!Matches(v)) ctx.SetRegistry(v.Hive, v.Path, v.Name, v.Kind, v.Value);
            },
        };

        yield return new RegistryTweak
        {
            Id = "transparency-off",
            Title = "Отключить прозрачность",
            Description = "Убирает эффекты прозрачности и размытия у меню «Пуск», панели задач и окон.",
            Details = "На дискретной видеокарте разница незаметна; на ноутбуках со встроенной графикой немного разгружает GPU. " +
                      "Полностью вступает в силу после выхода из системы.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.SignOut,
            Values = [Cu(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)],
        };

        yield return new RegistryTweak
        {
            Id = "menu-show-delay",
            Title = "Быстрые меню",
            Description = "Задержка перед открытием вложенных меню уменьшается с 400 до 150 мс.",
            Details = "Влияет только на субъективную отзывчивость интерфейса, на игры — нет. Значение 0 MONOLITH не ставит: меню начинают " +
                      "дёргаться при движении мыши. Действует после выхода из системы.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.SignOut,
            Values = [RegSet.Str(RegHive.HKCU, @"Control Panel\Desktop", "MenuShowDelay", "150")],
        };

        // ───── Input ─────

        // SAFE-OPTIONAL, "recommended for shooters" → pre-checked in the Gaming profile.
        yield return new RegistryTweak
        {
            Id = "mouse-acceleration-off",
            Title = "Отключить ускорение мыши",
            Description = "Снимает «Повышенную точность указателя»: курсор смещается строго пропорционально движению мыши.",
            Details = "Важно для шутеров и игр, которые используют системный курсор: одинаковое движение руки всегда даёт одинаковый сдвиг. " +
                      "Большинство современных игр читают мышь напрямую (Raw Input) и от этой настройки не зависят. Скорость указателя " +
                      "не меняется. Действует после выхода из системы.",
            Category = TweakCategory.Input,
            Risk = TweakRisk.Optional,
            DefaultIn = ProfileSet.Gaming | ProfileSet.Streaming,
            Restart = RestartRequirement.SignOut,
            Values =
            [
                RegSet.Str(RegHive.HKCU, @"Control Panel\Mouse", "MouseSpeed", "0"),
                RegSet.Str(RegHive.HKCU, @"Control Panel\Mouse", "MouseThreshold1", "0"),
                RegSet.Str(RegHive.HKCU, @"Control Panel\Mouse", "MouseThreshold2", "0"),
            ],
        };

        // SAFE-OPTIONAL, pre-checked in the Gaming profile (like mouse-acceleration-off). Clears only the shortcut bit.
        yield return new CustomTweak
        {
            Id = "accessibility-hotkeys-off",
            Title = "Без окна «Залипание клавиш» в играх",
            Description = "Пятикратное нажатие Shift, долгое удержание правого Shift или Num Lock больше не открывает окна «Залипание " +
                          "клавиш», «Фильтрация ввода» и «Озвучивание переключения», которые сворачивают игру.",
            Details = "Выключаются только сочетания клавиш — как переключатели «Сочетание клавиш для …» в Параметрах → Специальные " +
                      "возможности → Клавиатура. Сами функции остаются доступны там же, а если какая-то из них у вас уже включена, MONOLITH " +
                      "её не трогает. Остальные настройки этих функций сохраняются. FPS не меняется — пропадают только окна, которые " +
                      "отбирают фокус у игры. Откат возвращает прежние значения в точности; если после применения вы меняли эти функции " +
                      "в Параметрах, откат вернёт и их прежнее состояние (Восстановление покажет такие изменения). Действует после выхода " +
                      "из системы.",
            Category = TweakCategory.Input,
            Risk = TweakRisk.Optional,
            DefaultIn = ProfileSet.Gaming | ProfileSet.Streaming,
            Restart = RestartRequirement.SignOut,
            NotApplicableReason = "Залипание клавиш, фильтрация ввода и озвучивание переключения у вас включены — MONOLITH их не трогает.",
            DetectFunc = () =>
            {
                var keys = AccessibilityHotkeyTargets();
                return StateOf(keys.Count(k => (k.Flags & AccessibilityHotkeyBit) == 0), keys.Count);
            },
            ApplyAction = ctx =>
            {
                foreach (var (path, flags) in AccessibilityHotkeyTargets())
                {
                    if ((flags & AccessibilityHotkeyBit) == 0) continue;
                    // Kept as REG_SZ, the type Windows itself writes here.
                    ctx.SetRegistry(RegHive.HKCU, path, "Flags", RegistryValueKind.String,
                        ClearAccessibilityHotkey(flags).ToString(CultureInfo.InvariantCulture));
                }
            },
        };

        // ───── Sound (devices_audio.md audio-ducking: SAFE-OPTIONAL, «3 = Do nothing» in the Gaming profile) ─────

        // Also a row of «Устройства и сеть» → Звук (applied from its plan through TweakEngine, read-only there).
        yield return new RegistryTweak
        {
            Id = "audio-ducking",
            Title = "Не приглушать звук во время звонков",
            Description = "Windows перестаёт убавлять звук игры и музыки (по умолчанию на 80 %), когда открыт голосовой чат.",
            Details = "Это вариант «Ничего не делать» на вкладке «Связь» панели звука. Приглушение мешает слышать шаги и выстрелы, " +
                      "пока говорят в Discord или Teams; у Discord есть своё приглушение, оно настраивается в нём. Значение " +
                      "UserDuckingPreference = 3 хранится в профиле пользователя; Windows читает его после выхода из системы — чтобы " +
                      "сразу, выберите тот же вариант на вкладке «Связь» (mmsys.cpl). Откат возвращает прежний вариант, по умолчанию — " +
                      "«Уменьшать громкость на 80 %».",
            Category = TweakCategory.Gaming,
            Risk = TweakRisk.Optional,
            DefaultIn = ProfileSet.Gaming | ProfileSet.Streaming,
            Restart = RestartRequirement.SignOut,
            Values = [Cu(Core.Devices.DeviceTargets.DuckingPath, Core.Devices.DeviceTargets.DuckingName, 3)],
        };
    }

    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string StartPhoneCompanion = @"Software\Microsoft\Windows\CurrentVersion\Start\Companions\Microsoft.YourPhone_8wekyb3d8bbwe";

    // ───────────────────────────── Accessibility shortcuts ─────────────────────────────

    /// <summary>SKF_HOTKEYACTIVE / FKF_HOTKEYACTIVE / TKF_HOTKEYACTIVE: the keyboard shortcut that turns the feature on.</summary>
    internal const int AccessibilityHotkeyBit = 0x4;
    /// <summary>SKF_STICKYKEYSON / FKF_FILTERKEYSON / TKF_TOGGLEKEYSON: the feature itself is on.</summary>
    internal const int AccessibilityFeatureOnBit = 0x1;

    /// <summary>Sticky Keys, Filter Keys, Toggle Keys and the Windows defaults of their Flags (REG_SZ) values.</summary>
    internal static readonly (string Path, int Default)[] AccessibilityHotkeyKeys =
    [
        (@"Control Panel\Accessibility\StickyKeys", 510),
        (@"Control Panel\Accessibility\Keyboard Response", 126),
        (@"Control Panel\Accessibility\ToggleKeys", 62),
    ];

    /// <summary>Flags value as Windows reads it: an absent value means the default; anything unreadable is left alone (null).</summary>
    internal static int? ParseAccessibilityFlags(object? raw, int defaultValue) => raw switch
    {
        null => defaultValue,
        string s when int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) => v,
        int i when i >= 0 => i,
        _ => null,
    };

    /// <summary>Only the shortcut bit is cleared; every other bit (including the feature's own on/off) is kept.</summary>
    internal static int ClearAccessibilityHotkey(int flags) => flags & ~AccessibilityHotkeyBit;

    /// <summary>
    /// The Flags MONOLITH may change for one feature, or null: unreadable values and features that are switched on
    /// (the user relies on them, shortcut included) are left exactly as they are.
    /// </summary>
    internal static int? ChangeableAccessibilityFlags(object? raw, int defaultValue) =>
        ParseAccessibilityFlags(raw, defaultValue) is { } flags && (flags & AccessibilityFeatureOnBit) == 0 ? flags : null;

    /// <summary>The features MONOLITH may change, with their current Flags.</summary>
    internal static IReadOnlyList<(string Path, int Flags)> AccessibilityHotkeyTargets()
    {
        var result = new List<(string, int)>();
        foreach (var (path, defaultValue) in AccessibilityHotkeyKeys)
            if (ChangeableAccessibilityFlags(RegistryHelper.Read(RegHive.HKCU, path, "Flags"), defaultValue) is { } flags)
                result.Add((path, flags));
        return result;
    }

    /// <summary>Explorer/DWM parts of the "performance" visual preset (thumbnails, font smoothing and full-window drag are kept).</summary>
    private static RegSet[] VisualEffectsRegistry() =>
    [
        Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 3),
        Cu(ExplorerAdvanced, "TaskbarAnimations", 0),
        Cu(ExplorerAdvanced, "ListviewAlphaSelect", 0),
        Cu(ExplorerAdvanced, "ListviewShadow", 0),
        Cu(@"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0),
        Cu(@"Software\Microsoft\Windows\DWM", "AlwaysHibernateThumbnails", 0),
    ];
}
