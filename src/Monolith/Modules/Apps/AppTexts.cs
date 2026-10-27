using System.Globalization;
using Monolith.Core;

namespace Monolith.Modules.Apps;

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
        if (e.WebsiteOnly)
            tags.Add(new("НЕ ЧЕРЕЗ WINGET", TagKind.Muted, WebsiteOnlyReason(e)));
        else if (e.Has(AppFlags.Portable))
            tags.Add(new("ПОРТАТИВНАЯ", TagKind.Muted,
                "Без классического установщика: winget распакует программу в профиль текущего пользователя (папка WinGet\\Packages) " +
                "и добавит команду запуска. Ярлыка в меню «Пуск» может не быть."));
        if (e.Has(AppFlags.NvidiaOnly))
            tags.Add(new("NVIDIA", TagKind.Muted, "Только для видеокарт NVIDIA."));
        return tags;
    }

    /// <summary>Why the card sends the user to the vendor instead of winget.</summary>
    public static string WebsiteOnlyReason(CatalogEntry e) =>
        e.WebsiteReason ?? "Через winget эта программа сейчас не устанавливается. Надёжнее скачать её с официального сайта.";

    /// <summary>Card tag / tooltip for a component that is installed but not as a winget package.</summary>
    public const string UnmanagedExplanation =
        "Найдено в системе, но установлено не через winget (например, вместе с .NET SDK, Visual Studio или игрой). " +
        "winget не может это обновить — обновляется тем, чем было установлено.";

    public static string ActionToolTip(AppItemViewModel item)
    {
        var e = item.Entry;
        return item.Action switch
        {
            AppAction.SystemComponent => "Системный компонент Windows: MONOLITH его не устанавливает, не обновляет и не удаляет.",
            AppAction.Website => WebsiteOnlyReason(e) + " Откроется официальная страница загрузки.",
            AppAction.VendorDownload => WebsiteOnlyReason(e) + " " + VendorDownloadToolTip(e),
            AppAction.Installed => e.Has(AppFlags.ForceExe)
                ? "Классические библиотеки DirectX (June 2010) уже установлены — обновлять нечего."
                : item.IsUnmanaged
                    ? UnmanagedExplanation
                    : "Программа обновляется сама — отдельное обновление не нужно.",
            AppAction.Upgrade => "Проверить и установить новую версию через winget. Если обновлений нет, ничего не изменится.",
            _ => InstallHint(e),
        };
    }

    /// <summary>What the direct download does — named host and signer, no promises beyond that.</summary>
    public static string VendorDownloadToolTip(CatalogEntry e) => e.VendorInstaller is { } v
        ? $"MONOLITH скачает официальный установщик с сервера {v.Host} (HTTPS), проверит цифровую подпись ({SignerList(v.Signers)}) " +
          "и запустит его от вашего имени — права администратора установщик попросит сам. Без прав администратора у MONOLITH откроется сайт."
        : $"Откроется официальная страница загрузки: {e.Website}";

    private static string SignerList(IEnumerable<string> signers) => string.Join(" или ", signers);

    private static string InstallHint(CatalogEntry e)
    {
        string hint;
        if (Elevation.IsElevated && e.RunsUnelevated)
            hint = "Установка через winget без прав администратора — в профиль текущего пользователя.";
        else if (!Elevation.IsElevated && e.Scope is InstallScope.Machine)
            hint = "Установка через winget. Установщик для всех пользователей сам покажет запрос UAC.";
        else if (e.Source == PackageSource.MsStore)
            hint = "Установка через winget из Microsoft Store. " + StoreVerification;
        else
            hint = "Установка через winget. Установщик скачивается с сервера разработчика и проверяется по SHA256.";
        if (WingetClient.ExpandLocation(e.InstallLocation) is { } folder)
            hint += $" Папка установки: {folder} (без неё установщик не запускается).";
        if (e.VendorInstaller is not null)
            hint += " Производитель обновляет файл по той же ссылке: если winget откажется из-за контрольной суммы, " +
                    "MONOLITH предложит скачать установщик напрямую у производителя с проверкой цифровой подписи.";
        return hint;
    }

    public const string WingetVerification = "Установщики скачиваются с серверов разработчиков и проверяются по SHA256 из манифеста winget.";
    public const string StoreVerification = "Пакеты Microsoft Store скачиваются из Store и проверяются его подписью.";

    /// <summary>How the packages are verified: the SHA256 claim only for winget-source entries.</summary>
    public static string VerificationText(IEnumerable<CatalogEntry> entries)
    {
        var list = entries.ToList();
        var store = list.Any(e => e.Source == PackageSource.MsStore);
        var winget = list.Any(e => e.Source != PackageSource.MsStore);
        return winget && store ? $"{WingetVerification} {StoreVerification}"
            : store ? StoreVerification
            : WingetVerification;
    }

    /// <summary>
    /// winget is always called with --accept-package-agreements and --accept-source-agreements, so the confirmation
    /// must say that the user accepts the publisher licenses (and the Microsoft Store terms for msstore packages).
    /// </summary>
    public static string LicenseText(IEnumerable<CatalogEntry> entries, string confirmText) =>
        entries.Any(e => e.Source == PackageSource.MsStore)
            ? $"Нажимая «{confirmText}», вы принимаете лицензии издателей программ и условия Microsoft Store."
            : $"Нажимая «{confirmText}», вы принимаете лицензии издателей программ.";

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

    /// <summary>The queue status after winget refused an installer whose hash no longer matches its manifest.</summary>
    public const string HashMismatchStatus =
        "ОШИБКА: производитель обновил установщик, а каталог winget ещё нет — установка заблокирована ради безопасности";

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
        // Installed some other way (the SDK's Desktop Runtime): nothing failed, winget just has nothing to upgrade.
        if (code == WingetClient.NoApplicationsFound && op == QueueOperation.Upgrade)
            return (QueueState.Skipped, "ПРОПУЩЕНО: установлено не через winget", false);
        if (WingetClient.IsSkipped(code) || WingetClient.IsBenign(code)) return (QueueState.Skipped, $"ПРОПУЩЕНО: {WingetClient.Describe(code)}", false);
        if (code == WingetClient.InstallerHashMismatch) return (QueueState.Failed, HashMismatchStatus, false);
        return (QueueState.Failed, $"ОШИБКА: {WingetClient.Describe(code)}", false);
    }

    /// <summary>
    /// What happened and what the user can do, for a finished queue row that did not end in "ГОТОВО".
    /// Every winget outcome gets a plain-language answer; unknown codes point to the log and the official site.
    /// </summary>
    public static string ResultDetail(int code, QueueOperation op, CatalogEntry e)
    {
        var site = $"Можно установить программу с официального сайта ({e.Website}).";
        var install = op == QueueOperation.Install;
        if (code == WingetClient.UpgradeVersionNotNewer)
            return "В каталоге winget нет версии новее установленной — обновлять нечего.";
        if (code == WingetClient.UpgradeVersionUnknown)
            return "winget не знает, какая версия установлена, и не обновляет вслепую. Если нужна новая версия — обновите программу " +
                   "её встроенным средством обновления или с официального сайта.";
        if (code == WingetClient.PackageIsPinned)
            return $"Пакет закреплён в winget — MONOLITH уважает закрепление и не обновляет его. Снять закрепление: «winget pin remove --id {e.Id}».";
        if (code == unchecked((int)0x8A15010D))
            return "Установлена другая версия программы — winget не ставит поверх неё.";
        if (code == unchecked((int)0x8A15010E))
            return "Установлена более новая версия — winget не понижает версию.";
        if (code == WingetClient.InstallerHashMismatch)
            return "Контрольная сумма скачанного установщика не совпала с каталогом winget. Чаще всего это значит, что производитель " +
                   "выложил новую версию по той же ссылке, а каталог winget ещё не обновлён; реже — файл повреждён или подменён по пути. " +
                   "Поэтому winget его не запустил, и обходить эту проверку MONOLITH не будет. " +
                   (e.VendorInstaller is { } v
                       ? $"Нажмите «СКАЧАТЬ У ПРОИЗВОДИТЕЛЯ»: MONOLITH скачает официальный установщик с {v.Host} и проверит его цифровую подпись " +
                         $"({SignerList(v.Signers)}). Или повторите через несколько дней, когда каталог winget обновится."
                       : "Скачайте программу с официального сайта (кнопка «ОТКРЫТЬ САЙТ») или повторите через несколько дней, когда каталог winget обновится.");
        if (code == WingetClient.InstallLocationRequired)
            return install
                ? "Установщик требует указать папку установки, а спросить её у вас в тихом режиме winget не может; со стандартной папкой установка тоже не прошла. " + site
                : "Для обновления winget требует папку, где стоит программа, а MONOLITH её не знает. Обновите программу её встроенным " +
                  "средством обновления (обычно при запуске) или установщиком с официального сайта.";
        if (code == WingetClient.NoApplicationsFound)
            return install
                ? $"Пакета {e.Id} сейчас нет в каталоге winget (его могли переименовать или убрать). {site}"
                : UnmanagedExplanation + " MONOLITH её не трогает.";
        if (code == WingetClient.NoApplicableInstaller)
            return "В каталоге winget нет установщика для этой системы (архитектура или версия Windows). " + site;
        if (code == WingetClient.HttpNotFound)
            return "Сервер производителя ответил «файл не найден» (404): ссылка в каталоге winget устарела. " + site;
        if (WingetClient.IsDownloadProblem(code))
            return "Не удалось скачать установщик: нет интернета или сервер производителя не ответил. Проверьте подключение и повторите; " +
                   "если ошибка повторяется, ссылка в каталоге winget, скорее всего, устарела. " + site;
        if (code == WingetClient.ElevationProhibited)
            return "Установщик запрещает запуск с правами администратора, а запустить его от вашего имени не удалось. " +
                   "Запустите MONOLITH без прав администратора и повторите. " + site;
        if (code == WingetClient.RebootRequiredToInstall)
            return "Windows ждёт перезагрузки после прошлой установки или обновления. Перезагрузите компьютер и повторите — MONOLITH сам не перезагружает.";
        if (code == unchecked((int)0x8A150019))
            return "Для этого пакета нужны права администратора. Перезапустите MONOLITH с правами администратора и повторите.";
        if (code is unchecked((int)0x8A150101) or unchecked((int)0x8A150103) or unchecked((int)0x8A150111))
            return "Программа сейчас запущена. Закройте её (и значок в трее рядом с часами) и повторите.";
        if (code == unchecked((int)0x8A150102))
            return "Сейчас идёт другая установка (Windows Update или другой установщик). Дождитесь её окончания и повторите.";
        if (code == unchecked((int)0x8A15010C))
            return "Установка отменена в окне установщика или в запросе UAC. Если это случайно — повторите и подтвердите запрос.";
        if (code is unchecked((int)0x8A15010F) or unchecked((int)0x8A15003A) or unchecked((int)0x8A15001B) or unchecked((int)0x8A15001C))
            return "Установку запрещает политика этого компьютера (настройки организации или администратора). Обратитесь к тому, кто управляет ПК.";
        if (code == unchecked((int)0x8A150083))
            return "Microsoft Store не выдал лицензию. Откройте Microsoft Store, войдите в свою учётную запись и повторите.";
        if (code == unchecked((int)0x8A150105))
            return "На системном диске не хватает места. Освободите место (раздел «Обслуживание → Очистка») и повторите.";
        if (code == unchecked((int)0x8A150106))
            return "Не хватает оперативной памяти. Закройте лишние программы и повторите.";
        if (code == unchecked((int)0x8A150113))
            return "Программа не поддерживает эту версию Windows. Проверьте системные требования на официальном сайте.";
        if (code is unchecked((int)0x8A150104) or unchecked((int)0x8A150110))
            return "Не удалось поставить то, что нужно пакету для работы (например, .NET или Visual C++). Установите «Набор геймера» на этой странице и повторите.";
        if (code == unchecked((int)0x8A150114))
            return "Установщик не умеет обновлять поверх установленной версии. Обновите программу её встроенным средством обновления " +
                   "или удалите её в «Параметры → Приложения» и установите заново.";
        if (code is unchecked((int)0x8A150041) or unchecked((int)0x8A150046) or unchecked((int)0x8A150045) or unchecked((int)0x8A15004B)
            or unchecked((int)0x8A150007) or WingetClient.InternalError or unchecked((int)0x8A150002))
            return "Сбой самого winget. Обновите «Установщик приложений» в Microsoft Store (кнопка вверху страницы, если winget устарел) и повторите.";
        if (code == unchecked((int)0x8A15005E))
            return "Сертификат сервера не совпал: соединение, похоже, перехватывает прокси или антивирус с проверкой HTTPS. " +
                   "winget отказался ради безопасности. Отключите перехват HTTPS или установите программу с сайта.";
        if (code is unchecked((int)0x8A150005) or unchecked((int)0x8A15006A))
            return "winget был прерван (закрыт или остановлен системой). Повторите установку.";
        if (code == -1)
            return "winget недоступен. Установите или обновите «Установщик приложений» из Microsoft Store и повторите.";
        if (code is unchecked((int)0x8A150006) or unchecked((int)0x8A150049) or unchecked((int)0x8A150108) or unchecked((int)0x8A150115)
            or unchecked((int)0x8A150112) or unchecked((int)0x8A150052) or unchecked((int)0x8A150060))
            return "Установщик программы завершился с ошибкой — его код есть в журнале ниже. Закройте программу, если она запущена, " +
                   "перезагрузите компьютер и повторите. " + site;
        return $"winget завершился с ошибкой ({WingetClient.Describe(code)}) — подробности в журнале ниже. Повторите позже. " + site;
    }

    /// <summary>The button a failed (or skipped) queue row offers, if any.</summary>
    public static QueueFallback FallbackFor(int code, QueueOperation op, CatalogEntry e)
    {
        if (code == WingetClient.InstallerHashMismatch)
            return e.VendorInstaller is not null ? QueueFallback.VendorDownload : QueueFallback.Website;
        if (code == WingetClient.NoApplicationsFound && op == QueueOperation.Upgrade) return QueueFallback.None;
        if (code == WingetClient.InstallLocationRequired || code == WingetClient.NoApplicationsFound || code == WingetClient.NoApplicableInstaller
            || WingetClient.IsDownloadProblem(code) || code == unchecked((int)0x8A150113))
            return QueueFallback.Website;
        return QueueFallback.None;
    }

    // ─────────────────────────── direct vendor download ───────────────────────────

    /// <summary>Lead of the confirmation before the vendor installer is downloaded.</summary>
    public static string VendorLead(CatalogEntry e, bool afterHashMismatch) =>
        (afterHashMismatch
            ? $"winget отказался ставить {e.Name}: установщик не совпал с каталогом winget (обычно производитель обновил файл, а каталог ещё нет). "
            : WebsiteOnlyReason(e) + " ") +
        "MONOLITH может скачать официальный установщик прямо с сервера производителя — без браузера и сторонних сайтов — " +
        "и проверить его цифровую подпись вместо контрольной суммы winget. Саму установку вы проводите в окне установщика.";

    /// <summary>Exactly what the direct download will do — host, file, folder, signer, how it is started.</summary>
    public static List<string> VendorPlan(VendorDownloadRequest r, bool restorePoint) =>
    [
        $"Источник: официальный сервер {r.Url.Host} (HTTPS), ссылка {r.Url.AbsoluteUri}",
        $"Файл: {r.FileName} → {r.Folder} (запись туда разрешена только администраторам)",
        $"Проверка: действительная цифровая подпись {SignerList(r.Signers)}; без неё файл удаляется и не запускается",
        "Установщик запустится от вашего имени, без прав MONOLITH; права администратора он попросит сам (запрос UAC)",
        restorePoint
            ? "Перед запуском будет создана точка восстановления (если Windows её не создаст, MONOLITH спросит, продолжать ли)"
            : "Точка восстановления создана не будет (защита системы или эта настройка MONOLITH выключены)",
        "Лицензию производителя показывает сам установщик — MONOLITH ничего не принимает за вас",
        $"Нужно свободного места на системном диске: не меньше {SystemProfile.FormatBytes(r.RequiredFreeSpace)}",
        "Удалить программу потом можно в «Параметры → Приложения»",
    ];

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
