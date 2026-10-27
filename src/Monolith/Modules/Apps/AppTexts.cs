using System.Globalization;
using Monolith.Core;

namespace Monolith.Modules.Apps;

/// <summary>Restore point before an install queue: what the confirmation promises and the queue then does.</summary>
public enum RestorePointPlan
{
    /// <summary>MONOLITH is not elevated: no restore point possible.</summary>
    NotElevated,
    /// <summary>Turned off in MONOLITH settings.</summary>
    OffInSettings,
    /// <summary>Windows System Protection is off (or blocked by policy).</summary>
    ProtectionOff,
    /// <summary>A restore point is created first; if Windows does not create one, the user decides whether to go on.</summary>
    WillTry,
}

/// <summary>Plain-language texts of the page (tags, tooltips, confirmation lines) and small pure helpers.</summary>
public static class AppTexts
{
    public const string RemoteAccessWarning =
        "Программа даёт другому человеку полный доступ к вашему компьютеру. Мошенники часто просят установить её " +
        "под видом «службы поддержки» или «банка». Ставьте её, только если сами знаете зачем, и никогда — по просьбе незнакомцев.";

    /// <summary>Static tags of a card: category, store, then flags from the research (risky ones hatched).</summary>
    public static IReadOnlyList<AppTagInfo> BuildTags(CatalogEntry e)
    {
        var tags = new List<AppTagInfo> { new(AppCatalog.Info(e.Category).Tag, TagKind.Muted) };

        if (e.Source == PackageSource.MsStore)
            tags.Add(new("MS STORE", TagKind.Normal,
                "Пакет из Microsoft Store (источник msstore). Ставится для текущего пользователя, дальше его обновляет сам Store."));
        if (e.Has(AppFlags.RemoteAccess))
            tags.Add(new("УДАЛЁННЫЙ ДОСТУП", TagKind.Hatch, RemoteAccessWarning));
        if (e.Has(AppFlags.KernelDriver))
            tags.Add(new("ДРАЙВЕР ЯДРА", TagKind.Hatch,
                "Устанавливает драйвер режима ядра: он работает с максимальными правами и может конфликтовать с античитами. " +
                "Удаляется вместе с программой через «Параметры → Приложения»."));
        if (e.Has(AppFlags.Anticheat))
            tags.Add(new("АНТИЧИТ", TagKind.Hatch, e.Has(AppFlags.Region)
                ? "Вместе с игрой ставится античит Riot Vanguard (драйвер ядра). Он работает постоянно и требует перезагрузки."
                : "Может вызвать срабатывание античита: оверлеи, внедрение в игры и низкоуровневый доступ к железу иногда блокируются онлайн-играми."));
        if (e.Has(AppFlags.Expert))
            tags.Add(new("ЭКСПЕРТ", TagKind.Hatch,
                "Для опытных пользователей. Неправильное использование может привести к сбоям, перегреву или потере данных."));
        if (e.Has(AppFlags.Reboot))
            tags.Add(new("ПЕРЕЗАГРУЗКА", TagKind.Muted, "После установки нужна перезагрузка. MONOLITH никогда не перезагружает компьютер сам."));
        if (e.Has(AppFlags.Eol))
            tags.Add(new("УСТАРЕВАЕТ", TagKind.Muted,
                "Microsoft прекращает выпуск исправлений безопасности для этой версии (.NET 6 — уже без поддержки, .NET 8 и 9 — до 10.11.2026). " +
                "Ставьте, только если её требует конкретная игра или программа."));
        if (e.Has(AppFlags.Paid))
            tags.Add(new("ПЛАТНАЯ", TagKind.Muted, "Платная программа: после пробного периода нужна лицензия."));
        if (e.Has(AppFlags.Trialware))
            tags.Add(new("ПРОБНЫЙ ПЕРИОД", TagKind.Muted, "Бесплатно на время пробного периода, дальше программа просит купить лицензию."));
        if (e.Has(AppFlags.Preinstalled))
            tags.Add(new("ВСТРОЕНО В WINDOWS", TagKind.Muted, e.SystemComponent
                ? "Системный компонент Windows: обновляется через Microsoft Edge Update. MONOLITH его не устанавливает и не удаляет."
                : "Обычно уже есть в Windows и обновляется сам через Microsoft Edge Update. Если его нет (бывает на Windows 10) — можно установить отсюда."));
        if (e.Has(AppFlags.Win10Only))
            tags.Add(new("ДЛЯ WINDOWS 10", TagKind.Muted, "В Windows 11 22H2 и новее уже встроено — там этот пакет не нужен."));
        if (e.Has(AppFlags.ExplicitUpgrade))
            tags.Add(new("ОБНОВЛЯЕТСЯ САМА", TagKind.Muted, "Программа обновляет себя сама, поэтому «Обновить всё» её пропускает."));
        if (e.Has(AppFlags.Portable))
            tags.Add(new("ПОРТАТИВНАЯ", TagKind.Muted, e.WebsiteOnly
                ? "winget только скачивает установщик Roblox — запускать его всё равно придётся вручную. Надёжнее скачать с официального сайта."
                : "Без классического установщика: winget распакует программу в папку WinGet\\Packages и добавит команду запуска. Ярлыка в меню «Пуск» может не быть."));
        if (e.Has(AppFlags.NvidiaOnly))
            tags.Add(new("NVIDIA", TagKind.Muted, "Только для видеокарт NVIDIA."));
        return tags;
    }

    public static string ActionToolTip(AppItemViewModel item)
    {
        var e = item.Entry;
        return item.Action switch
        {
            AppAction.SystemComponent => "Системный компонент Windows: MONOLITH его не устанавливает, не обновляет и не удаляет.",
            AppAction.Website => "winget для этого пакета только скачивает установщик. Откроется официальная страница загрузки.",
            AppAction.Installed => e.Has(AppFlags.ForceExe)
                ? "Классические библиотеки DirectX (June 2010) уже установлены — обновлять нечего."
                : "Программа обновляется сама — отдельное обновление не нужно.",
            AppAction.Upgrade => "Проверить и установить новую версию через winget. Если обновлений нет, ничего не изменится.",
            _ => InstallHint(e),
        };
    }

    private static string InstallHint(CatalogEntry e)
    {
        var store = e.Source == PackageSource.MsStore;
        if (Elevation.IsElevated && e.RunsUnelevated)
            return store
                ? "Установка из Microsoft Store через winget без прав администратора — для текущего пользователя."
                : "Установка через winget без прав администратора — в профиль текущего пользователя.";
        if (!Elevation.IsElevated && e.Scope is InstallScope.Machine)
            return "Установка через winget. Установщик для всех пользователей сам покажет запрос UAC.";
        return store
            ? "Установка из Microsoft Store через winget: пакет приходит из каталога Store."
            : "Установка через winget. Установщик скачивается по ссылке из манифеста и сверяется с его SHA256.";
    }

    /// <summary>
    /// How the packages of a plan are obtained and checked. The SHA256 check of a winget manifest applies only to
    /// packages of the winget source; Microsoft Store packages come from the Store catalog.
    /// </summary>
    public static string VerificationText(IEnumerable<CatalogEntry> entries)
    {
        var list = entries.ToList();
        var store = list.Count(e => e.Source == PackageSource.MsStore);
        var winget = list.Count - store;
        const string wingetText = "Установщики пакетов winget скачиваются по ссылкам из их манифестов и сверяются с SHA256 из манифеста — при несовпадении установка блокируется.";
        const string storeText = "Пакеты Microsoft Store приходят из каталога Store, сверка SHA256 из манифеста winget к ним не относится.";
        if (store == 0) return wingetText;
        if (winget == 0) return storeText;
        return wingetText + " " + storeText;
    }

    /// <summary>
    /// winget is always called with --accept-package-agreements and --accept-source-agreements, so the confirmation
    /// must say that pressing the button accepts these terms on the user's behalf.
    /// </summary>
    public static string AgreementText(IEnumerable<CatalogEntry> entries, string confirmText)
    {
        var store = entries.Any(e => e.Source == PackageSource.MsStore);
        return $"Нажимая «{confirmText}», вы принимаете лицензионные соглашения издателей этих программ" +
               (store ? " и условия Microsoft Store" : "") +
               ": winget подтвердит их от вашего имени, отдельных окон с соглашениями не будет.";
    }

    /// <summary>What happens about a restore point before a queue runs.</summary>
    public static RestorePointPlan PlanRestorePoint(bool elevated, bool enabledInSettings, bool protectionOn) =>
        !elevated ? RestorePointPlan.NotElevated
        : !enabledInSettings ? RestorePointPlan.OffInSettings
        : !protectionOn ? RestorePointPlan.ProtectionOff
        : RestorePointPlan.WillTry;

    /// <summary>Footnote of the install confirmation — only what will really happen.</summary>
    public static string RestorePointFootnote(RestorePointPlan plan) => plan switch
    {
        RestorePointPlan.NotElevated => "Без прав администратора точка восстановления не создаётся.",
        RestorePointPlan.OffInSettings => "Точки восстановления отключены в настройках MONOLITH — точка создана не будет.",
        RestorePointPlan.ProtectionOff => "Защита системы Windows выключена — точка восстановления создана не будет.",
        _ => "Перед началом MONOLITH создаст точку восстановления. Если Windows её не создаст, установка не начнётся без вашего согласия.",
    };

    /// <summary>"Steam — Valve.Steam · winget · установка".</summary>
    public static string QueueLine(CatalogEntry e, string packageId, QueueOperation op, string? installedVersion = null)
    {
        var verb = op == QueueOperation.Install ? "установка" : "обновление";
        var line = $"{e.Name} — {packageId} · {e.SourceName} · {verb}";
        if (op == QueueOperation.Upgrade && !string.IsNullOrEmpty(installedVersion)) line += $" (сейчас {installedVersion})";
        return line;
    }

    /// <summary>Warnings shown under the package list of the confirmation dialog.</summary>
    public static IReadOnlyList<string> Warnings(IEnumerable<AppItemViewModel> items)
    {
        var list = new List<string>();
        foreach (var item in items)
        {
            var e = item.Entry;
            if (e.Has(AppFlags.RemoteAccess))
                list.Add($"ВНИМАНИЕ — {e.Name}: программа удалённого доступа. Не устанавливайте её по просьбе незнакомцев, «службы поддержки» или «банка».");
            if (e.Has(AppFlags.Anticheat) && e.Has(AppFlags.KernelDriver) && e.Has(AppFlags.Region))
                list.Add($"ВНИМАНИЕ — {e.Name}: вместе с игрой ставится античит Riot Vanguard (драйвер ядра).");
            else if (e.Has(AppFlags.KernelDriver))
                list.Add($"ВНИМАНИЕ — {e.Name}: устанавливает драйвер ядра.");
            else if (e.Has(AppFlags.Anticheat))
                list.Add($"ВНИМАНИЕ — {e.Name}: может вызвать срабатывание античита в онлайн-играх.");
            if (e.Has(AppFlags.Expert))
                list.Add($"{e.Name}: инструмент для опытных пользователей.");
            if (e.Has(AppFlags.Reboot))
                list.Add($"{e.Name}: после установки понадобится перезагрузка (MONOLITH сам не перезагружает).");
            if (e.Has(AppFlags.Eol))
                list.Add($"{e.Name}: версия без поддержки или с заканчивающейся поддержкой Microsoft.");
            if (e.Has(AppFlags.Trialware) || e.Has(AppFlags.Paid))
                list.Add($"{e.Name}: платная программа или пробная версия.");
            if (e.Has(AppFlags.Portable))
                list.Add($"{e.Name}: портативная программа — ярлыка в меню «Пуск» может не быть.");
            if (item.NvidiaMissing)
                list.Add($"{e.Name}: видеокарта NVIDIA в этом компьютере не найдена.");
        }
        return list;
    }

    /// <summary>True when the entry should be dangerous-styled in the confirmation (hatched header).</summary>
    public static bool IsRisky(CatalogEntry e) =>
        e.Has(AppFlags.RemoteAccess) || e.Has(AppFlags.KernelDriver) || e.Has(AppFlags.Expert);

    /// <summary>Case-insensitive search over name, package ids, description and category (ё = е).</summary>
    public static bool Matches(CatalogEntry e, string? query)
    {
        var q = Normalize(query);
        if (q.Length == 0) return true;
        var info = AppCatalog.Info(e.Category);
        foreach (var hay in e.AllIds.Append(e.Name).Append(e.Description).Append(info.Title).Append(info.Tag))
            if (Normalize(hay).Contains(q, StringComparison.Ordinal)) return true;
        return false;
    }

    public static string Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLower(CultureInfo.GetCultureInfo("ru-RU")).Replace('ё', 'е');

    /// <summary>How a finished winget run is shown in the queue.</summary>
    public static (QueueState State, string Text, bool Reboot) DescribeResult(int code, QueueOperation op)
    {
        if (code == 0) return (QueueState.Done, "ГОТОВО", false);
        if (code == WingetClient.UpdateNotApplicable)
            return (QueueState.Done, op == QueueOperation.Upgrade ? "ГОТОВО: уже последняя версия" : "ГОТОВО: уже установлено, обновлений нет", false);
        if (code == WingetClient.AlreadyInstalled) return (QueueState.Done, "ГОТОВО: уже установлено", false);
        if (code == WingetClient.RebootRequiredToFinish) return (QueueState.Done, "ГОТОВО: нужна перезагрузка", true);
        if (code == WingetClient.RebootInitiated) return (QueueState.Done, "ГОТОВО: установщик перезагружает ПК", true);
        if (code == WingetClient.RebootRequiredToInstall) return (QueueState.Failed, "ОШИБКА: нужна перезагрузка перед установкой", true);
        if (WingetClient.IsSkipped(code) || WingetClient.IsBenign(code)) return (QueueState.Skipped, $"ПРОПУЩЕНО: {WingetClient.Describe(code)}", false);
        return (QueueState.Failed, $"ОШИБКА: {WingetClient.Describe(code)}", false);
    }

    /// <summary>"1 программа / 3 программы / 5 программ".</summary>
    public static string Programs(int n)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        var word = mod100 is >= 11 and <= 14 ? "программ" : mod10 switch { 1 => "программа", >= 2 and <= 4 => "программы", _ => "программ" };
        return $"{n} {word}";
    }

    /// <summary>"1 пакет / 3 пакета / 5 пакетов".</summary>
    public static string Packages(int n)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        var word = mod100 is >= 11 and <= 14 ? "пакетов" : mod10 switch { 1 => "пакет", >= 2 and <= 4 => "пакета", _ => "пакетов" };
        return $"{n} {word}";
    }
}
