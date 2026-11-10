namespace Lycoris.Tweaks;

/// <summary>
/// Expert-only settings: changes Lycoris does not recommend (docs/research/rejected_tweaks.md explains why) but an expert may
/// still want. Each one is ADVANCED (closed in «Простой»), reversible through the journal and carries an honest warning.
/// Nothing here turns off Windows protection. Ids start with <see cref="ExpertPrefix"/>.
/// </summary>
public static partial class TweakCatalog
{
    public const string ExpertPrefix = "expert-";

    private static IEnumerable<Tweak> ExpertTweaks()
    {
        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "power-throttling-off",
            Title = Loc.Key("Отключить Power Throttling для всех программ"),
            Description = Loc.T("Windows перестаёт замедлять фоновые программы ради экономии энергии."),
            Details = Loc.T("Записывает PowerThrottlingOff=1. Игре в активном окне это не помогает: Windows не замедляет активную программу. " +
                      "Фоновые программы начинают потреблять больше. Нужна перезагрузка. Отмена — откатом в Lycoris."),
            Category = TweakCategory.Power,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = Loc.T("Lycoris это не рекомендует: игре не помогает, на ноутбуке батарея садится быстрее."),
            Values = [Lm(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1)],
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "global-timer-resolution",
            Title = Loc.Key("Общий системный таймер, как в Windows 10"),
            Description = Loc.T("Запрос точного таймера одной программой снова действует на все программы."),
            Details = Loc.T("Записывает GlobalTimerResolutionRequests=1 (Windows 11). Игры сами запрашивают точный таймер для себя, поэтому им это " +
                      "не нужно. Пригодится только старым программам, которые рассчитывали на общий таймер. Нужна перезагрузка. Отмена — откатом в Lycoris."),
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = Loc.T("Lycoris это не рекомендует: FPS не растёт, компьютер тратит больше энергии."),
            Values = [Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1)],
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "s3-override",
            Title = Loc.Key("Сон S3 вместо Modern Standby"),
            Description = Loc.T("Просит Windows использовать старый режим сна S3 вместо современного режима ожидания."),
            Details = Loc.T("Записывает PlatformAoAcOverride=0. Работает только если прошивка компьютера поддерживает S3; на многих новых ноутбуках " +
                      "S3 нет. Нужна перезагрузка. Отмена — откатом в Lycoris."),
            Category = TweakCategory.Power,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = Loc.T("Lycoris это не рекомендует: ноутбук может перестать засыпать или просыпаться. Если так случилось — сделайте откат."),
            Values = [Lm(@"SYSTEM\CurrentControlSet\Control\Power", "PlatformAoAcOverride", 0)],
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "bg-apps-global-off",
            Title = Loc.Key("Запретить фоновую работу всем приложениям из Store"),
            Description = Loc.T("Политика Windows: приложения из Microsoft Store не работают в фоне."),
            Details = Loc.T("Записывает политику LetAppsRunInBackground=2. Переключатели фоновой работы в Параметрах станут недоступны. " +
                      "Отмена — откатом в Lycoris."),
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Advanced,
            Warning = Loc.T("Lycoris это не рекомендует: перестанут приходить уведомления и срабатывать будильники, не будут работать «Связь с телефоном» и Xbox."),
            Values = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2)],
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "qos-reserve-off",
            Title = Loc.Key("Снять «резерв 20 % полосы» (QoS)"),
            Description = Loc.T("Политика Windows: планировщик пакетов не резервирует полосу для приоритетного трафика."),
            Details = Loc.T("Записывает политику NonBestEffortLimit=0. Резерв используется только когда программа сама просит приоритет, " +
                      "в остальное время вся полоса и так доступна. Отмена — откатом в Lycoris."),
            Category = TweakCategory.Network,
            Risk = TweakRisk.Advanced,
            Warning = Loc.T("Lycoris это не рекомендует: скорость и пинг не меняются."),
            Values = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", 0)],
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "notifications-off",
            Title = Loc.Key("Выключить все уведомления Windows"),
            Description = Loc.T("Всплывающие уведомления всех программ перестают показываться."),
            Details = Loc.T("Записывает ToastEnabled=0 для текущего пользователя. Мягче — режим «Не беспокоить» в Windows: он сам включается в играх. " +
                      "Отмена — откатом в Lycoris."),
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Advanced,
            Warning = Loc.T("Lycoris это не рекомендует: пропадут все уведомления, включая будильники и напоминания."),
            Values = [Cu(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0)],
        };
    }
}
