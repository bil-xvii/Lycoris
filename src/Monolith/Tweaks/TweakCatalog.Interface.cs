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
            DefaultIn = ProfileSet.Recommended,
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
            DefaultIn = ProfileSet.Recommended,
            Restart = RestartRequirement.Explorer,
            Values = [Cu(@"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0)],
        };

        yield return new RegistryTweak
        {
            Id = "widgets-off",
            Title = "Отключить Виджеты",
            Description = "Убирает доску виджетов, её кнопку на панели задач и фоновые процессы WebView2, которые её обслуживают.",
            Details = "Используется официальная политика Microsoft (AllowNewsAndInterests). Пакет виджетов не удаляется, поэтому всё " +
                      "возвращается откатом. Применяется после перезапуска Проводника.",
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Recommended,
            Restart = RestartRequirement.Explorer,
            AppliesTo = sp => sp.IsWindows11,
            NotApplicableReason = "Только для Windows 11.",
            Values = [Lm(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)],
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
                    // A flag rejected half-way puts the original flags back before failing, so no change is left unjournaled.
                    RunJournaled(ctx, "spi:visual-effects", PowerShellExe, PowerShellArgs(SpiApplyScript(off, 0, original, spi.MinAnimate)),
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
            DefaultIn = ProfileSet.Gaming,
            Restart = RestartRequirement.SignOut,
            Values =
            [
                RegSet.Str(RegHive.HKCU, @"Control Panel\Mouse", "MouseSpeed", "0"),
                RegSet.Str(RegHive.HKCU, @"Control Panel\Mouse", "MouseThreshold1", "0"),
                RegSet.Str(RegHive.HKCU, @"Control Panel\Mouse", "MouseThreshold2", "0"),
            ],
        };
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
