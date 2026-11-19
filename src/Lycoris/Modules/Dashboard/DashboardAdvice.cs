using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Tweaks;

namespace Lycoris.Modules.Dashboard;

internal sealed record CheckGuidance(string PathText, string NextStep, CheckAction? OpenAction = null)
{
    public string? PageKey => OpenAction?.Kind switch
    {
        CheckActionKind.Navigate => OpenAction.Target,
        CheckActionKind.FocusTweak => "optimize",
        CheckActionKind.FocusDevice => "devices",
        _ => null,
    };

    public string? FocusId => OpenAction?.Kind switch
    {
        CheckActionKind.Navigate => OpenAction.Focus,
        CheckActionKind.FocusTweak or CheckActionKind.FocusDevice => OpenAction.Target,
        _ => null,
    };
}

/// <summary>The paths and instructions shared by the Panel, PC Doctor, and the local Assistant.</summary>
internal static class DashboardAdvice
{
    internal const string WindowsUpdateUri = "ms-settings:windowsupdate";

    internal static CheckGuidance? Catalog(CheckAction? action)
    {
        if (action is not { Kind: CheckActionKind.ApplyTweak or CheckActionKind.FocusTweak, Target: { } id }
            || TweakCatalog.Find(id) is not { } tweak)
            return null;

        return new(Loc.Tf("Оптимизация → {0}", Loc.T(tweak.Title)),
            id == HealthRules.TweakUpdatePin
                ? Loc.T("Откройте настройку и проверьте, нужна ли закреплённая версия Windows. Если закрепление больше не нужно, отмените его в настройке.")
                : Loc.T("Откройте настройку. Проверьте её описание, отметьте пункт и нажмите «ПРИМЕНИТЬ» внизу."),
            new(CheckActionKind.FocusTweak, Loc.T("Открыть настройку"), id));
    }

    internal static CheckGuidance? For(HealthCheckResult check)
    {
        if (check.Severity == CheckSeverity.Ok) return null;
        if (Catalog(check.Action) is { } catalog) return catalog;

        var action = check.Action;
        return (check.Id, action?.Kind, action?.Target, action?.Focus) switch
        {
            ("startup" or "doctor-startup", CheckActionKind.Navigate, "services", "tab:startup") =>
                new(Loc.T("Автозапуск и службы → Автозапуск"),
                    Loc.T("Просмотрите список и выключите переключатель только у знакомой программы, которую не нужно запускать вместе с Windows."), action),
            ("restore", CheckActionKind.Navigate, "restore", "section:restore-points") =>
                new(Loc.T("Восстановление → Защита системы Windows"),
                    Loc.T("В карточке «Защита системы Windows» нажмите «ВКЛЮЧИТЬ ЗАЩИТУ» и проверьте окно подтверждения."), action),
            ("restore", null, _, _) =>
                new(Loc.T("Восстановление → Защита системы Windows"),
                    Loc.T("Проверьте сообщение о политике в карточке «Защита системы Windows». Для изменения политики обратитесь к администратору."),
                    new(CheckActionKind.Navigate, Loc.T("Открыть защиту системы"), "restore", "section:restore-points")),
            ("drift", CheckActionKind.Navigate, "restore", "section:drift") =>
                new(Loc.T("Восстановление → Изменено вне Lycoris"),
                    Loc.T("Сравните «было» и «стало» у каждой записи. Решите, какие изменения нужно применить снова."), action),
            ("disk-space" or "doctor-space" or "maintenance", _, _, _) =>
                new(Loc.T("Обслуживание → Очистка"),
                    Loc.T("Дождитесь анализа, проверьте отмеченные пункты и нажмите «ОЧИСТИТЬ». Lycoris покажет список для подтверждения."),
                    new(CheckActionKind.Navigate, Loc.T("Открыть очистку"), "cleaner")),
            ("devices", CheckActionKind.Navigate, "drivers", "tab:problems") =>
                new(Loc.T("Драйверы → Проблемные устройства"),
                    Loc.T("Найдите устройство по названию и коду ошибки. Откройте рекомендацию рядом с этим устройством."),
                    action! with { Label = Loc.T("Открыть проблемные устройства") }),
            ("gpu-driver", CheckActionKind.Navigate, "drivers", { } focus) =>
                new(GpuPath(focus),
                    Loc.T("Сравните установленную и доступную версии в карточке «Видеокарта». Кнопка обновления появится, если есть подходящий драйвер."),
                    action! with { Label = Loc.T("Открыть драйвер видеокарты") }),
            ("stability", CheckActionKind.Navigate, "drivers", "tab:updates") =>
                new(Loc.T("Драйверы → Обновления Windows"),
                    Loc.T("Нажмите «НАЙТИ ОБНОВЛЕНИЯ» и проверьте, предлагается ли обновление для устройства, указанного в результате."),
                    action! with { Label = Loc.T("Открыть обновления драйверов") }),
            ("stability", CheckActionKind.MemoryDiagnostic, _, _) =>
                new(Loc.T("Обслуживание → Ремонт и диски → Диагностика памяти"),
                    Loc.T("Сохраните работу. В карточке «Диагностика памяти» нажмите «ОТКРЫТЬ» и выберите, когда Windows выполнит проверку."),
                    new(CheckActionKind.Navigate, Loc.T("Открыть инструменты"), "tools")),
            ("stability", CheckActionKind.OpenReliabilityMonitor, _, _) =>
                new(Loc.T("Windows → Просмотр журнала надёжности → Сведения о событии"),
                    Loc.T("Выберите день сбоя и откройте технические подробности записи около указанного времени. Windows может записать внезапное выключение при следующем запуске."),
                    action),
            ("refresh-rate", CheckActionKind.FocusDevice, "kind:monitor", _) =>
                new(Loc.T("Устройства и сеть → Монитор → Частота обновления"),
                    Loc.T("Выберите нужный монитор и частоту обновления, затем нажмите «ПРИМЕНИТЬ» и подтвердите изображение."),
                    action! with { Label = Loc.T("Открыть настройки монитора") }),
            ("pending-reboot", _, _, _) or ("doctor-restart", CheckActionKind.Restart, _, WindowsUpdateUri) =>
                new(Loc.T("Параметры Windows → Центр обновления Windows"),
                    Loc.T("Проверьте, ожидают ли обновления перезагрузки. Сохраните работу и выберите «Перезагрузка» в меню питания Windows."),
                    new(CheckActionKind.OpenUri, Loc.T("Открыть Центр обновления"), WindowsUpdateUri)),
            ("uptime" or "doctor-restart", _, _, _) =>
                TaskManager(Loc.T("Диспетчер задач → Производительность → ЦП → Время работы"),
                    Loc.T("Проверьте время работы. Сохраните файлы и выберите «Перезагрузка» в меню питания Windows.")),
            ("power-plan" or "doctor-power", CheckActionKind.OpenUri, HealthRules.PowerSettingsUri, _) =>
                new(Loc.T("Параметры Windows → Система → Питание"),
                    Loc.T("Откройте параметры питания Windows и проверьте переключатель энергосбережения. Если режим включён намеренно, оставьте его."), action),
            ("vbs", CheckActionKind.OpenUri, HealthRules.WindowsSecurityUri, _) =>
                new(Loc.T("Безопасность Windows → Безопасность устройства → Изоляция ядра"),
                    check.Severity == CheckSeverity.Warning
                        ? Loc.T("Откройте раздел «Изоляция ядра» и проверьте состояние списка блокировки уязвимых драйверов Microsoft.")
                        : Loc.T("Проверьте состояния «Целостность памяти» и «Список блокировки уязвимых драйверов Microsoft»."),
                    action),
            ("ram-speed", _, _, _) =>
                SystemInformation(Loc.T("Сведения о системе → Модель системы / Модель основной платы"),
                    Loc.T("Найдите руководство производителя по точной модели. Проверьте, доступны ли профили XMP или EXPO и поддерживает ли их комплект памяти.")),
            ("boot-security", _, _, _) =>
                SystemInformation(Loc.T("Сведения о системе → Режим BIOS / Состояние безопасной загрузки"),
                    Loc.T("Сверьте режим загрузки и модель ПК. По руководству производителя проверьте поддержку Secure Boot и TPM для этой модели.")),
            ("rebar", _, _, _) =>
                SystemInformation(Loc.T("Сведения о системе → Модель системы / Модель основной платы"),
                    Loc.T("В руководстве для вашей платы найдите Resizable BAR. Сверьте требования производителя платы и видеокарты.")),
            ("pcie-link", _, _, _) =>
                SystemInformation(Loc.T("Сведения о системе → Модель основной платы"),
                    Loc.T("Найдите в руководстве платы схему слотов PCIe. Сверьте слот видеокарты и проверьте, делит ли он линии с другими разъёмами.")),
            ("monitor-gpu", _, _, _) =>
                new(Loc.T("Параметры Windows → Система → Дисплей → Расширенный дисплей"),
                    Loc.T("В настройках дисплея откройте «Расширенный дисплей». Выберите монитор и проверьте, к какому адаптеру он подключён. Если нужна дискретная видеокарта, проверьте подключение кабеля к её порту."),
                    new(CheckActionKind.OpenUri, Loc.T("Открыть параметры дисплея"), DisplaySettingsUri)),
            ("disk-health", _, _, _) =>
                SystemInformation(Loc.T("Сведения о системе → Компоненты → Запоминающие устройства → Диски"),
                    Loc.T("Скопируйте важные файлы на другой накопитель. Сверьте модель указанного диска и проверьте его утилитой производителя.")),
            ("doctor-interrupted", CheckActionKind.DiagnoseAgain, _, _) =>
                new(Loc.T("Панель → Почему тормозит?"),
                    Loc.T("Повторите замер при заметном замедлении и оставьте окно Lycoris открытым на время замера."), action),
            ("doctor-cpu", _, _, _) =>
                TaskManager(Loc.T("Диспетчер задач → Процессы → ЦП"),
                    Loc.T("Нажмите заголовок «ЦП», чтобы отсортировать список. Проверьте, какая программа стабильно занимает больше всего процессора.")),
            ("doctor-ram", _, _, _) =>
                TaskManager(Loc.T("Диспетчер задач → Процессы → Память"),
                    Loc.T("Нажмите заголовок «Память» и проверьте программы в начале списка. Сохраните работу, прежде чем закрыть ненужную программу.")),
            ("doctor-disk", _, _, _) =>
                TaskManager(Loc.T("Диспетчер задач → Процессы → Диск"),
                    Loc.T("Нажмите заголовок «Диск», чтобы увидеть, какие приложения сейчас больше всего читают или записывают данные.")),
            ("doctor-gpu-hot", CheckActionKind.Navigate, "drivers", { } focus) =>
                new(GpuPath(focus),
                    Loc.T("Сверьте модель видеокарты с рекомендациями её производителя. Проверьте, свободны ли вентиляционные отверстия корпуса."), action),
            _ => null,
        };
    }

    internal const string DisplaySettingsUri = "ms-settings:display-advanced";

    private static string GpuPath(string focus) => focus == "tab:hardware"
        ? Loc.T("Драйверы → Подбор для вашего ПК")
        : Loc.T("Драйверы → Подбор для вашего ПК → Видеокарта");

    private static CheckGuidance SystemInformation(string path, string step) =>
        new(path, step, new(CheckActionKind.OpenSystemInformation, Loc.T("Открыть сведения о системе")));

    private static CheckGuidance TaskManager(string path, string step) =>
        new(path, step, new(CheckActionKind.OpenTaskManager, Loc.T("Открыть диспетчер задач")));
}
