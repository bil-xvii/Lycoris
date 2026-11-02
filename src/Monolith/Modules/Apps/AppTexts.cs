using System.Globalization;
using Monolith.Core;

namespace Monolith.Modules.Apps;

/// <summary>Plain-language texts of the page (tags, tooltips, confirmation lines) and small pure helpers.</summary>
public static class AppTexts
{
    public static string RemoteAccessWarning =>
        Loc.T("Программа даёт другому человеку полный доступ к вашему компьютеру. Мошенники часто просят установить её " +
        "под видом «службы поддержки» или «банка». Ставьте её, только если сами знаете зачем, и никогда — по просьбе незнакомцев.");

    /// <summary>Static tags of a card: category, store, then flags from the research (risky ones hatched).</summary>
    public static IReadOnlyList<AppTagInfo> BuildTags(CatalogEntry e)
    {
        var tags = new List<AppTagInfo> { new(AppCatalog.Info(e.Category).Tag, TagKind.Muted) };

        if (e.Source == PackageSource.MsStore)
            tags.Add(new("MS STORE", TagKind.Normal,
                Loc.T("Пакет из Microsoft Store (источник msstore). Ставится для текущего пользователя, дальше его обновляет сам Store.")));
        if (e.Has(AppFlags.RemoteAccess))
            tags.Add(new(Loc.T("УДАЛЁННЫЙ ДОСТУП"), TagKind.Hatch, RemoteAccessWarning));
        if (e.Has(AppFlags.KernelDriver))
            tags.Add(new(Loc.T("ДРАЙВЕР ЯДРА"), TagKind.Hatch,
                Loc.T("Устанавливает драйвер режима ядра: он работает с максимальными правами и может конфликтовать с античитами. " +
                "Удаляется вместе с программой через «Параметры → Приложения».")));
        if (e.Has(AppFlags.Anticheat))
            tags.Add(new(Loc.T("АНТИЧИТ"), TagKind.Hatch, e.Has(AppFlags.Region)
                ? Loc.T("Вместе с игрой ставится античит Riot Vanguard (драйвер ядра). Он работает постоянно и требует перезагрузки.")
                : Loc.T("Может вызвать срабатывание античита: оверлеи, внедрение в игры и низкоуровневый доступ к железу иногда блокируются онлайн-играми.")));
        if (e.Has(AppFlags.Expert))
            tags.Add(new(Loc.T("ЭКСПЕРТ"), TagKind.Hatch,
                Loc.T("Для опытных пользователей. Неправильное использование может привести к сбоям, перегреву или потере данных.")));
        if (e.Has(AppFlags.Reboot))
            tags.Add(new(Loc.T("ПЕРЕЗАГРУЗКА"), TagKind.Muted, Loc.T("После установки нужна перезагрузка. Lycoris никогда не перезагружает компьютер сам.")));
        if (e.Has(AppFlags.Eol))
            tags.Add(new(Loc.T("УСТАРЕВАЕТ"), TagKind.Muted,
                Loc.T("Microsoft прекращает выпуск исправлений безопасности для этой версии (.NET 6 — уже без поддержки, .NET 8 и 9 — до 10.11.2026). " +
                "Ставьте, только если её требует конкретная игра или программа.")));
        if (e.Has(AppFlags.Paid))
            tags.Add(new(Loc.T("ПЛАТНАЯ"), TagKind.Muted, Loc.T("Платная программа: после пробного периода нужна лицензия.")));
        if (e.Has(AppFlags.Trialware))
            tags.Add(new(Loc.T("ПРОБНЫЙ ПЕРИОД"), TagKind.Muted, Loc.T("Бесплатно на время пробного периода, дальше программа просит купить лицензию.")));
        if (e.Has(AppFlags.Preinstalled))
            tags.Add(new(Loc.T("ВСТРОЕНО В WINDOWS"), TagKind.Muted, e.SystemComponent
                ? Loc.T("Системный компонент Windows: обновляется через Microsoft Edge Update. Lycoris его не устанавливает и не удаляет.")
                : Loc.T("Обычно уже есть в Windows и обновляется сам через Microsoft Edge Update. Если его нет (бывает на Windows 10) — можно установить отсюда.")));
        if (e.Has(AppFlags.Win10Only))
            tags.Add(new(Loc.T("ДЛЯ WINDOWS 10"), TagKind.Muted, Loc.T("В Windows 11 22H2 и новее уже встроено — там этот пакет не нужен.")));
        if (e.Has(AppFlags.ExplicitUpgrade))
            tags.Add(new(Loc.T("ОБНОВЛЯЕТСЯ САМА"), TagKind.Muted, Loc.T("Программа обновляет себя сама, поэтому «Обновить всё» её пропускает.")));
        if (e.WebsiteOnly)
            tags.Add(new(Loc.T("НЕ ЧЕРЕЗ WINGET"), TagKind.Muted, WebsiteOnlyReason(e)));
        else if (e.Has(AppFlags.Portable))
            tags.Add(new(Loc.T("ПОРТАТИВНАЯ"), TagKind.Muted,
                Loc.T("Без классического установщика: winget распакует программу в профиль текущего пользователя (папка WinGet\\Packages) " +
                "и добавит команду запуска. Ярлыка в меню «Пуск» может не быть.")));
        if (e.Has(AppFlags.NvidiaOnly))
            tags.Add(new("NVIDIA", TagKind.Muted, Loc.T("Только для видеокарт NVIDIA.")));
        return tags;
    }

    /// <summary>Why the card sends the user to the vendor instead of winget.</summary>
    public static string WebsiteOnlyReason(CatalogEntry e) =>
        e.WebsiteReason ?? Loc.T("Через winget эта программа сейчас не устанавливается. Надёжнее скачать её с официального сайта.");

    /// <summary>Card tag / tooltip for a component that is installed but not as a winget package.</summary>
    public static string UnmanagedExplanation =>
        Loc.T("Найдено в системе, но установлено не через winget (например, вместе с .NET SDK, Visual Studio или игрой). " +
        "winget не может это обновить — обновляется тем, чем было установлено.");

    public static string ActionToolTip(AppItemViewModel item)
    {
        var e = item.Entry;
        return item.Action switch
        {
            AppAction.SystemComponent => Loc.T("Системный компонент Windows: Lycoris его не устанавливает, не обновляет и не удаляет."),
            AppAction.Website => WebsiteOnlyReason(e) + Loc.T(" Откроется официальная страница загрузки."),
            AppAction.VendorDownload => WebsiteOnlyReason(e) + " " + VendorDownloadToolTip(e),
            AppAction.Installed => e.Has(AppFlags.ForceExe)
                ? Loc.T("Классические библиотеки DirectX (June 2010) уже установлены — обновлять нечего.")
                : item.IsUnmanaged
                    ? UnmanagedExplanation
                    : Loc.T("Программа обновляется сама — отдельное обновление не нужно."),
            AppAction.Upgrade => Loc.T("Проверить и установить новую версию через winget. Если обновлений нет, ничего не изменится."),
            _ => InstallHint(e),
        };
    }

    /// <summary>What the direct download does — named host and signer, no promises beyond that.</summary>
    public static string VendorDownloadToolTip(CatalogEntry e) => e.VendorInstaller is { } v
        ? Loc.Tf("Lycoris скачает официальный установщик с сервера {0} (HTTPS), проверит цифровую подпись ({1}) " +
          "и запустит его от вашего имени — права администратора установщик попросит сам. Без прав администратора у Lycoris откроется сайт.", v.Host, SignerList(v.Signers))
        : Loc.Tf("Откроется официальная страница загрузки: {0}", e.Website);

    private static string SignerList(IEnumerable<string> signers) => string.Join(Loc.T(" или "), signers);

    private static string InstallHint(CatalogEntry e)
    {
        string hint;
        if (Elevation.IsElevated && e.RunsUnelevated)
            hint = Loc.T("Установка через winget без прав администратора — в профиль текущего пользователя.");
        else if (!Elevation.IsElevated && e.Scope is InstallScope.Machine)
            hint = Loc.T("Установка через winget. Установщик для всех пользователей сам покажет запрос UAC.");
        else if (e.Source == PackageSource.MsStore)
            hint = Loc.T("Установка через winget из Microsoft Store. ") + StoreVerification;
        else
            hint = Loc.T("Установка через winget. Установщик скачивается с сервера разработчика и проверяется по SHA256.");
        if (WingetClient.ExpandLocation(e.InstallLocation) is { } folder)
            hint += Loc.Tf(" Папка установки: {0} (без неё установщик не запускается).", folder);
        if (e.VendorInstaller is not null)
            hint += Loc.T(" Производитель обновляет файл по той же ссылке: если winget откажется из-за контрольной суммы, " +
                    "Lycoris предложит скачать установщик напрямую у производителя с проверкой цифровой подписи.");
        return hint;
    }

    public static string WingetVerification => Loc.T("Установщики скачиваются с серверов разработчиков и проверяются по SHA256 из манифеста winget.");
    public static string StoreVerification => Loc.T("Пакеты Microsoft Store скачиваются из Store и проверяются его подписью.");

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
            ? Loc.Tf("Нажимая «{0}», вы принимаете лицензии издателей программ и условия Microsoft Store.", confirmText)
            : Loc.Tf("Нажимая «{0}», вы принимаете лицензии издателей программ.", confirmText);

    /// <summary>"Steam — Valve.Steam · winget · установка".</summary>
    public static string QueueLine(CatalogEntry e, string packageId, QueueOperation op, string? installedVersion = null)
    {
        var verb = op == QueueOperation.Install ? Loc.T("установка") : Loc.T("обновление");
        var line = $"{e.Name} — {packageId} · {e.SourceName} · {verb}";
        if (op == QueueOperation.Upgrade && !string.IsNullOrEmpty(installedVersion)) line += Loc.Tf(" (сейчас {0})", installedVersion);
        return line;
    }

    /// <summary>
    /// Warnings shown under the package list of the confirmation dialog. <paramref name="noted"/>: entries already listed
    /// with a ПОКА НЕ НУЖНО / НЕ НУЖНО reason — their expert, end-of-support and paid lines would only repeat it (the
    /// ВНИМАНИЕ lines about remote access, kernel drivers and anti-cheat always stay).
    /// </summary>
    public static IReadOnlyList<string> Warnings(IEnumerable<AppItemViewModel> items, IReadOnlySet<CatalogEntry>? noted = null)
    {
        var list = new List<string>();
        foreach (var item in items)
        {
            var e = item.Entry;
            var repeats = noted?.Contains(e) == true;
            if (e.Has(AppFlags.RemoteAccess))
                list.Add(Loc.Tf("ВНИМАНИЕ — {0}: программа удалённого доступа. Не устанавливайте её по просьбе незнакомцев, «службы поддержки» или «банка».", e.Name));
            if (e.Has(AppFlags.Anticheat) && e.Has(AppFlags.KernelDriver) && e.Has(AppFlags.Region))
                list.Add(Loc.Tf("ВНИМАНИЕ — {0}: вместе с игрой ставится античит Riot Vanguard (драйвер ядра).", e.Name));
            else if (e.Has(AppFlags.KernelDriver))
                list.Add(Loc.Tf("ВНИМАНИЕ — {0}: устанавливает драйвер ядра.", e.Name));
            else if (e.Has(AppFlags.Anticheat))
                list.Add(Loc.Tf("ВНИМАНИЕ — {0}: может вызвать срабатывание античита в онлайн-играх.", e.Name));
            if (e.Has(AppFlags.Expert) && !repeats)
                list.Add(Loc.Tf("{0}: инструмент для опытных пользователей.", e.Name));
            if (e.Has(AppFlags.Reboot))
                list.Add(Loc.Tf("{0}: после установки понадобится перезагрузка (Lycoris сам не перезагружает).", e.Name));
            if (e.Has(AppFlags.Eol) && !repeats)
                list.Add(Loc.Tf("{0}: версия без поддержки или с заканчивающейся поддержкой Microsoft.", e.Name));
            if ((e.Has(AppFlags.Trialware) || e.Has(AppFlags.Paid)) && !repeats)
                list.Add(Loc.Tf("{0}: платная программа или пробная версия.", e.Name));
            if (e.Has(AppFlags.Portable))
                list.Add(Loc.Tf("{0}: портативная программа — ярлыка в меню «Пуск» может не быть.", e.Name));
        }
        return list;
    }

    // ─────────────────────────── install advice ───────────────────────────

    public static string OptionalToolTip => Loc.T("По желанию: этому ПК программа не обязательна — ставьте, если она вам нужна.");

    public static string RecommendedTabToolTip =>
        Loc.T("Что стоит поставить на этот ПК: ещё не установленные программы с пометкой НУЖНО или РЕКОМЕНДУЕТСЯ. " +
        "Совет учитывает видеокарту, версию Windows, подключённую периферию и уже установленные программы.");

    /// <summary>The advice badge: НУЖНО = solid, РЕКОМЕНДУЕТСЯ = outline, the rest muted; tooltip = the reason.</summary>
    public static AppTagInfo AdviceTag(AppAdvice advice)
    {
        var kind = advice.Level switch
        {
            AdviceLevel.Required => TagKind.Solid,
            AdviceLevel.Recommended => TagKind.Normal,
            _ => TagKind.Muted,
        };
        return new AppTagInfo(advice.TagText, kind, advice.Reason.Length > 0 ? advice.Reason : OptionalToolTip);
    }

    /// <summary>
    /// Confirmation lines for packages Lycoris does not advise installing now ("НЕ НУЖНО — NVIDIA PhysX: …"), "НЕ НУЖНО"
    /// first. Only installs are listed: updates of installed programs carry no advice.
    /// </summary>
    public static IReadOnlyList<string> AdviceNotes(IEnumerable<(CatalogEntry Entry, AppAdvice? Advice)> installs) =>
        installs.Where(x => x.Advice is { IsDiscouraged: true })
            .OrderBy(x => x.Advice!.Level == AdviceLevel.NotNeeded ? 0 : 1)
            .Select(x => $"{x.Advice!.TagText} — {x.Entry.Name}: {x.Advice.Reason}")
            .ToList();

    /// <summary>The entries <see cref="AdviceNotes"/> lists (for <see cref="Warnings"/>, so a reason is not repeated).</summary>
    public static IReadOnlySet<CatalogEntry> NotedEntries(IEnumerable<(CatalogEntry Entry, AppAdvice? Advice)> installs) =>
        installs.Where(x => x.Advice is { IsDiscouraged: true }).Select(x => x.Entry).ToHashSet();

    /// <summary>The sentence before the package list when some packages are not advised; empty when all are fine.</summary>
    public static string AdviceLead(int discouraged) => discouraged switch
    {
        0 => "",
        1 => Loc.T("1 пакет из списка Lycoris сейчас ставить не советует — причина в начале списка. Установить его всё равно можно. "),
        _ => Loc.Tf("{0} из списка Lycoris сейчас ставить не советует — причины в начале списка. Установить их всё равно можно. ", Packages(discouraged)),
    };

    public static string KitCaption =>
        Loc.T("Библиотеки, которые нужны этому ПК: Visual C++, DirectX June 2010, .NET и другие с пометкой НУЖНО или РЕКОМЕНДУЕТСЯ. Установленное пропускается.");

    /// <summary>Tooltip of the gamer kit row: what is in it on this PC and which runtimes are deliberately left out.</summary>
    public static string KitToolTip(IEnumerable<CatalogEntry> kit, IEnumerable<CatalogEntry> leftOut)
    {
        var names = string.Join(", ", kit.Select(e => e.Name));
        var left = string.Join(", ", leftOut.Select(e => e.Name));
        return Loc.T("Библиотеки, без которых игры не запускаются или падают с ошибками вроде «MSVCP140.dll не найден» и «d3dx9_43.dll отсутствует». ") +
               (names.Length > 0
                   ? Loc.Tf("В набор входит то, что этому ПК НУЖНО или РЕКОМЕНДУЕТСЯ: {0}. ", names)
                   : Loc.T("Состав набора появится после проверки установленных программ. ")) +
               (left.Length > 0 ? Loc.Tf("Не входят — этому ПК они не обязательны: {0}. Их ставят по одной, если игра или программа попросит.", left) : "");
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
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLowerInvariant().Replace('ё', 'е');

    /// <summary>The queue status after winget refused an installer whose hash no longer matches its manifest.</summary>
    public static string HashMismatchStatus =>
        Loc.T("ОШИБКА: производитель обновил установщик, а каталог winget ещё нет — установка заблокирована ради безопасности");

    /// <summary>How a finished winget run is shown in the queue.</summary>
    public static (QueueState State, string Text, bool Reboot) DescribeResult(int code, QueueOperation op)
    {
        if (code == 0) return (QueueState.Done, Loc.T("ГОТОВО"), false);
        if (code == WingetClient.UpdateNotApplicable)
            return (QueueState.Done, op == QueueOperation.Upgrade ? Loc.T("ГОТОВО: уже последняя версия") : Loc.T("ГОТОВО: уже установлено, обновлений нет"), false);
        if (code == WingetClient.AlreadyInstalled) return (QueueState.Done, Loc.T("ГОТОВО: уже установлено"), false);
        if (code == WingetClient.RebootRequiredToFinish) return (QueueState.Done, Loc.T("ГОТОВО: нужна перезагрузка"), true);
        if (code == WingetClient.RebootInitiated) return (QueueState.Done, Loc.T("ГОТОВО: установщик перезагружает ПК"), true);
        if (code == WingetClient.RebootRequiredToInstall) return (QueueState.Failed, Loc.T("ОШИБКА: нужна перезагрузка перед установкой"), true);
        // Installed some other way (the SDK's Desktop Runtime): nothing failed, winget just has nothing to upgrade.
        if (code == WingetClient.NoApplicationsFound && op == QueueOperation.Upgrade)
            return (QueueState.Skipped, Loc.T("ПРОПУЩЕНО: установлено не через winget"), false);
        if (WingetClient.IsSkipped(code) || WingetClient.IsBenign(code)) return (QueueState.Skipped, Loc.Tf("ПРОПУЩЕНО: {0}", WingetClient.Describe(code)), false);
        if (code == WingetClient.InstallerHashMismatch) return (QueueState.Failed, HashMismatchStatus, false);
        return (QueueState.Failed, Loc.Tf("ОШИБКА: {0}", WingetClient.Describe(code)), false);
    }

    /// <summary>
    /// What happened and what the user can do, for a finished queue row that did not end in "ГОТОВО".
    /// Every winget outcome gets a plain-language answer; unknown codes point to the log and the official site.
    /// </summary>
    /// <param name="elevated">Defaults to the current process: the vendor download needs administrator rights.</param>
    public static string ResultDetail(int code, QueueOperation op, CatalogEntry e, bool? elevated = null)
    {
        var site = Loc.Tf("Можно установить программу с официального сайта ({0}).", e.Website);
        var install = op == QueueOperation.Install;
        if (code == WingetClient.UpgradeVersionNotNewer)
            return Loc.T("В каталоге winget нет версии новее установленной — обновлять нечего.");
        if (code == WingetClient.UpgradeVersionUnknown)
            return Loc.T("winget не знает, какая версия установлена, и не обновляет вслепую. Если нужна новая версия — обновите программу " +
                   "её встроенным средством обновления или с официального сайта.");
        if (code == WingetClient.PackageIsPinned)
            return Loc.Tf("Пакет закреплён в winget — Lycoris уважает закрепление и не обновляет его. Снять закрепление: «winget pin remove --id {0}».", e.Id);
        if (code == unchecked((int)0x8A15010D))
            return Loc.T("Установлена другая версия программы — winget не ставит поверх неё.");
        if (code == unchecked((int)0x8A15010E))
            return Loc.T("Установлена более новая версия — winget не понижает версию.");
        if (code == WingetClient.InstallerHashMismatch)
            return Loc.T("Контрольная сумма скачанного установщика не совпала с каталогом winget. Чаще всего это значит, что производитель " +
                   "выложил новую версию по той же ссылке, а каталог winget ещё не обновлён; реже — файл повреждён или подменён по пути. " +
                   "Поэтому winget его не запустил, и обходить эту проверку Lycoris не будет. ") +
                   (e.VendorInstaller is { } v && (elevated ?? Elevation.IsElevated)
                       ? Loc.Tf("Нажмите «СКАЧАТЬ У ПРОИЗВОДИТЕЛЯ»: Lycoris скачает официальный установщик с {0} и проверит его цифровую подпись " +
                         "({1}). Или повторите через несколько дней, когда каталог winget обновится.", v.Host, SignerList(v.Signers))
                       : Loc.T("Скачайте программу с официального сайта (кнопка «ОТКРЫТЬ САЙТ») или повторите через несколько дней, когда каталог winget обновится."));
        if (code == WingetClient.InstallLocationRequired)
            return install
                ? Loc.T("Установщик требует указать папку установки, а спросить её у вас в тихом режиме winget не может; со стандартной папкой установка тоже не прошла. ") + site
                : Loc.T("Для обновления winget требует папку, где стоит программа, а Lycoris её не знает. Обновите программу её встроенным " +
                  "средством обновления (обычно при запуске) или установщиком с официального сайта.");
        if (code == WingetClient.NoApplicationsFound)
            return install
                ? Loc.Tf("Пакета {0} сейчас нет в каталоге winget (его могли переименовать или убрать). {1}", e.Id, site)
                : UnmanagedExplanation + Loc.T(" Lycoris её не трогает.");
        if (code == WingetClient.NoApplicableInstaller)
            return Loc.T("В каталоге winget нет установщика для этой системы (архитектура или версия Windows). ") + site;
        if (code == WingetClient.HttpNotFound)
            return Loc.T("Сервер производителя ответил «файл не найден» (404): ссылка в каталоге winget устарела. ") + site;
        if (WingetClient.IsDownloadProblem(code))
            return Loc.T("Не удалось скачать установщик: нет интернета или сервер производителя не ответил. Проверьте подключение и повторите; " +
                   "если ошибка повторяется, ссылка в каталоге winget, скорее всего, устарела. ") + site;
        if (code == WingetClient.ElevationProhibited)
            return Loc.T("Установщик запрещает запуск с правами администратора, а запустить его от вашего имени не удалось. " +
                   "Запустите Lycoris без прав администратора и повторите. ") + site;
        if (code == WingetClient.RebootRequiredToInstall)
            return Loc.T("Windows ждёт перезагрузки после прошлой установки или обновления. Перезагрузите компьютер и повторите — Lycoris сам не перезагружает.");
        if (code == unchecked((int)0x8A150019))
            return Loc.T("Для этого пакета нужны права администратора. Перезапустите Lycoris с правами администратора и повторите.");
        if (code is unchecked((int)0x8A150101) or unchecked((int)0x8A150103) or unchecked((int)0x8A150111))
            return Loc.T("Программа сейчас запущена. Закройте её (и значок в трее рядом с часами) и повторите.");
        if (code == unchecked((int)0x8A150102))
            return Loc.T("Сейчас идёт другая установка (Windows Update или другой установщик). Дождитесь её окончания и повторите.");
        if (code == unchecked((int)0x8A15010C))
            return Loc.T("Установка отменена в окне установщика или в запросе UAC. Если это случайно — повторите и подтвердите запрос.");
        if (code is unchecked((int)0x8A15010F) or unchecked((int)0x8A15003A) or unchecked((int)0x8A15001B) or unchecked((int)0x8A15001C))
            return Loc.T("Установку запрещает политика этого компьютера (настройки организации или администратора). Обратитесь к тому, кто управляет ПК.");
        if (code == unchecked((int)0x8A150083))
            return Loc.T("Microsoft Store не выдал лицензию. Откройте Microsoft Store, войдите в свою учётную запись и повторите.");
        if (code == unchecked((int)0x8A150105))
            return Loc.T("На системном диске не хватает места. Освободите место (раздел «Обслуживание → Очистка») и повторите.");
        if (code == unchecked((int)0x8A150106))
            return Loc.T("Не хватает оперативной памяти. Закройте лишние программы и повторите.");
        if (code == unchecked((int)0x8A150113))
            return Loc.T("Программа не поддерживает эту версию Windows. Проверьте системные требования на официальном сайте.");
        if (code is unchecked((int)0x8A150104) or unchecked((int)0x8A150110))
            return Loc.T("Не удалось поставить то, что нужно пакету для работы (например, .NET или Visual C++). Установите «Набор геймера» на этой странице и повторите.");
        if (code == unchecked((int)0x8A150114))
            return Loc.T("Установщик не умеет обновлять поверх установленной версии. Обновите программу её встроенным средством обновления " +
                   "или удалите её в «Параметры → Приложения» и установите заново.");
        if (code is unchecked((int)0x8A150041) or unchecked((int)0x8A150046) or unchecked((int)0x8A150045) or unchecked((int)0x8A15004B)
            or unchecked((int)0x8A150007) or WingetClient.InternalError or unchecked((int)0x8A150002))
            return Loc.T("Сбой самого winget. Обновите «Установщик приложений» в Microsoft Store (кнопка вверху страницы, если winget устарел) и повторите.");
        if (code == unchecked((int)0x8A15005E))
            return Loc.T("Сертификат сервера не совпал: соединение, похоже, перехватывает прокси или антивирус с проверкой HTTPS. " +
                   "winget отказался ради безопасности. Отключите перехват HTTPS или установите программу с сайта.");
        if (code is unchecked((int)0x8A150005) or unchecked((int)0x8A15006A))
            return Loc.T("winget был прерван (закрыт или остановлен системой). Повторите установку.");
        if (code == -1)
            return Loc.T("winget недоступен. Установите или обновите «Установщик приложений» из Microsoft Store и повторите.");
        if (code is unchecked((int)0x8A150006) or unchecked((int)0x8A150049) or unchecked((int)0x8A150108) or unchecked((int)0x8A150115)
            or unchecked((int)0x8A150112) or unchecked((int)0x8A150052) or unchecked((int)0x8A150060))
            return Loc.T("Установщик программы завершился с ошибкой — его код есть в журнале ниже. Закройте программу, если она запущена, " +
                   "перезагрузите компьютер и повторите. ") + site;
        return Loc.Tf("winget завершился с ошибкой ({0}) — подробности в журнале ниже. Повторите позже. ", WingetClient.Describe(code)) + site;
    }

    /// <summary>The button a failed (or skipped) queue row offers, if any.</summary>
    /// <param name="elevated">Defaults to the current process: without admin rights the vendor download is not offered (site instead).</param>
    public static QueueFallback FallbackFor(int code, QueueOperation op, CatalogEntry e, bool? elevated = null)
    {
        if (code == WingetClient.InstallerHashMismatch)
            return e.VendorInstaller is not null && (elevated ?? Elevation.IsElevated) ? QueueFallback.VendorDownload : QueueFallback.Website;
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
            ? Loc.Tf("winget отказался ставить {0}: установщик не совпал с каталогом winget (обычно производитель обновил файл, а каталог ещё нет). ", e.Name)
            : WebsiteOnlyReason(e) + " ") +
        Loc.T("Lycoris может скачать официальный установщик прямо с сервера производителя — без браузера и сторонних сайтов — " +
        "и проверить его цифровую подпись вместо контрольной суммы winget. Саму установку вы проводите в окне установщика.");

    /// <summary>Exactly what the direct download will do — host, file, folder, signer, how it is started.</summary>
    public static List<string> VendorPlan(VendorDownloadRequest r, bool restorePoint) =>
    [
        Loc.Tf("Источник: официальный сервер {0} (HTTPS), ссылка {1}", r.Url.Host, r.Url.AbsoluteUri),
        Loc.Tf("Файл: {0} → {1} (запись туда разрешена только администраторам)", r.FileName, r.Folder),
        Loc.Tf("Проверка: действительная цифровая подпись {0}; без неё файл удаляется и не запускается", SignerList(r.Signers)),
        Loc.T("Установщик запустится от вашего имени, без прав Lycoris; права администратора он попросит сам (запрос UAC)"),
        restorePoint
            ? Loc.T("Перед запуском будет создана точка восстановления (если Windows её не создаст, Lycoris спросит, продолжать ли)")
            : Loc.T("Точка восстановления создана не будет (защита системы или эта настройка Lycoris выключены)"),
        Loc.T("Лицензию производителя показывает сам установщик — Lycoris ничего не принимает за вас"),
        Loc.Tf("Нужно свободного места на системном диске: не меньше {0}", SystemProfile.FormatBytes(r.RequiredFreeSpace)),
        Loc.T("Удалить программу потом можно в «Параметры → Приложения»"),
    ];

    /// <summary>"1 программа / 3 программы / 5 программ".</summary>
    public static string Programs(int n) => Loc.N(n, "{0} программа", "{0} программы", "{0} программ");

    /// <summary>"1 пакет / 3 пакета / 5 пакетов".</summary>
    public static string Packages(int n) => Loc.N(n, "{0} пакет", "{0} пакета", "{0} пакетов");
}
