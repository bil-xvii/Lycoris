using Monolith.Modules.Apps;
using Monolith.Modules.Cleaner;
using Monolith.Modules.Maintenance;
using Monolith.Modules.Services;
using Monolith.Modules.Tools;
using Monolith.Tweaks;

namespace Monolith.Shell;

/// <summary>Builds the command-palette index from the existing catalogs (in-memory C# data, read only).</summary>
public static class CommandPaletteIndex
{
    /// <summary>A searchable row that is not a tweak/tool/service: title, description, keywords, page and focus id.</summary>
    public sealed record Entry(PaletteKind Kind, string Title, string Description, string Keywords, string PageKey, string? FocusId, bool IsSection = false);

    public static IReadOnlyList<PaletteEntry> Build(
        IEnumerable<(string Key, string Title, string Subtitle)> pages,
        IEnumerable<Tweak> tweaks,
        IEnumerable<ToolSection> toolSections,
        IEnumerable<QuickLink> quickLinks,
        IEnumerable<CuratedService> services,
        IEnumerable<(string Key, string Title, string Subtitle, string Keywords)>? sections = null,
        IEnumerable<(string Title, string Description, string Keywords, string FocusId)>? deviceSettings = null,
        IEnumerable<RejectedTweak>? rejected = null,
        IEnumerable<Entry>? extra = null)
    {
        var list = new List<PaletteEntry>();

        foreach (var (key, title, subtitle) in pages)
            list.Add(new PaletteEntry { Kind = PaletteKind.Page, Title = title, Description = subtitle, Keywords = key, PageKey = key });

        // Former pages that now live inside another page: searchable, opened through the navigation aliases.
        foreach (var (key, title, subtitle, keywords) in sections ?? [])
            list.Add(new PaletteEntry
            {
                Kind = PaletteKind.Page, Title = title, Description = subtitle, Keywords = $"{key} {keywords}", PageKey = key, IsSection = true,
            });

        foreach (var t in tweaks)
            list.Add(new PaletteEntry
            {
                Kind = PaletteKind.Tweak, Title = t.Title, Description = t.Description,
                Keywords = $"{t.Id} {t.CategoryText}", Risk = t.Risk, PageKey = "optimize", FocusId = t.Id,
            });

        foreach (var section in toolSections)
            foreach (var card in section.Cards)
                list.Add(new PaletteEntry
                {
                    Kind = PaletteKind.Tool, Title = card.Title, Description = card.Description,
                    Keywords = $"{card.Command} {section.Title}", PageKey = MaintenanceViewModel.ToolsTab,
                });

        foreach (var link in quickLinks)
            list.Add(new PaletteEntry
            {
                Kind = PaletteKind.Tool, Title = link.Title, Description = Loc.Tf("Быстрый запуск в разделе «Обслуживание → Ремонт и диски»: {0}.", link.Command),
                Keywords = link.Command, PageKey = MaintenanceViewModel.ToolsTab,
            });

        foreach (var s in services)
            list.Add(new PaletteEntry
            {
                Kind = PaletteKind.Service, Title = s.Title, Description = s.Description,
                Keywords = $"{s.Name} {ServiceCatalog.GroupTitle(s.Group)}", Risk = s.Risk, PageKey = "services", FocusId = s.Name,
            });

        foreach (var (title, description, keywords, focus) in deviceSettings ?? [])
            list.Add(new PaletteEntry
            {
                Kind = PaletteKind.Tweak, Title = title, Description = description, Keywords = keywords, PageKey = "devices", FocusId = focus,
            });

        // Rejected tweaks («НЕ ДЕЛАЕМ»): searching «HPET», «таймер», «парковка ядер»… opens the explanation in Settings → О программе
        // instead of finding nothing. No risk tag: nothing can be applied.
        foreach (var r in rejected ?? [])
            list.Add(new PaletteEntry
            {
                Kind = PaletteKind.Myth, Title = r.Title, Description = r.WhyNot,
                Keywords = $"{r.Id} {r.Keywords} {string.Join(' ', r.RegistryValueNames)}",
                PageKey = "settings", FocusId = RejectedTweaks.FocusPrefix + r.Id,
            });

        // Programs, cleanup items, autostart entries, Settings rows, Drivers tabs, Restore sections (Obvious UI: the search
        // must know everything the user can click, not only tweaks).
        foreach (var e in extra ?? [])
            list.Add(new PaletteEntry
            {
                Kind = e.Kind, Title = e.Title, Description = e.Description, Keywords = e.Keywords, PageKey = e.PageKey, FocusId = e.FocusId,
                IsSection = e.IsSection,
            });

        return list;
    }

    /// <summary>
    /// Device settings of «Устройства и сеть» (they exist only on that page): the eleven input parameters by their journal
    /// target, the monitor, sound and network rows by device kind. The page resolves the id to the row (or the device).
    /// </summary>
    public static IReadOnlyList<(string Title, string Description, string Keywords, string FocusId)> DeviceEntries { get; } =
    [
        .. Monolith.Core.Devices.SpiCatalog.All.Select(e => (e.Title,
            Loc.Tf("{0} — раздел «Устройства и сеть», вкладка «Вручную».", e.Id.StartsWith("keyboard", StringComparison.Ordinal) ? Loc.T("Клавиатура") : Loc.T("Мышь")),
            $"{e.Id} " + Loc.Keep("мышь клавиатура устройства"), Monolith.Core.Devices.DeviceTargets.Spi(e.Id))),
        (Loc.Key("Разрешение и частота обновления"), Loc.Key("Режим монитора с подтверждением на экране — раздел «Устройства и сеть»."), Loc.Keep("монитор экран герц гц частота разрешение refresh"), "kind:monitor"),
        (Loc.Key("HDR монитора"), Loc.Key("Включение HDR с подтверждением на экране — раздел «Устройства и сеть»."), Loc.Keep("hdr монитор экран"), "display-hdr"),
        (Loc.Key("Яркость и контраст монитора"), Loc.Key("Яркость по DDC/CI — раздел «Устройства и сеть»."), Loc.Keep("яркость контраст монитор ddc"), "display-brightness"),
        (Loc.Key("Уровень микрофона"), Loc.Key("Уровень записи и проверка уровня — раздел «Устройства и сеть»."), Loc.Keep("микрофон звук запись уровень"), "mic-level"),
        (Loc.Key("Наушники и звук"), Loc.Key("Устройство вывода, формат и улучшения звука — раздел «Устройства и сеть»."), Loc.Keep("звук наушники колонки вывод"), "kind:audiooutput"),
        (Loc.Key("Сеть и пинг"), Loc.Key("Сетевой адаптер, замер пинга и автонастройка сети — раздел «Устройства и сеть»."), Loc.Keep("сеть пинг интернет ethernet wi-fi адаптер"), "kind:network"),
        (Loc.Key("Частота опроса и DPI мыши"), Loc.Key("Замер частоты опроса и проверка DPI — раздел «Устройства и сеть»."), Loc.Keep("мышь dpi опрос герц polling"),
            Monolith.Modules.Devices.Input.InputProvider.PollingRow),
    ];

    /// <summary>Sections that used to be pages; their keys are navigation aliases (see MainViewModel.NavigateTo).</summary>
    public static IReadOnlyList<(string Key, string Title, string Subtitle, string Keywords)> Sections { get; } =
    [
        (MaintenanceViewModel.CleanerTab, Loc.Key("ОЧИСТКА"), Loc.Key("Временные файлы, кэши и мусор — раздел «Обслуживание»."), Loc.Keep("обслуживание мусор")),
        (MaintenanceViewModel.ToolsTab, Loc.Key("РЕМОНТ И ДИСКИ"), Loc.Key("Проверка и восстановление Windows, диски и сеть — раздел «Обслуживание»."), Loc.Keep("инструменты обслуживание")),
        (MainViewModel.StartupAlias, Loc.Key("АВТОЗАПУСК"), Loc.Key("Программы, которые запускаются вместе с Windows, — раздел «Автозапуск и службы»."), Loc.Keep("службы задачи")),
    ];

    // ───────────── Focus-id prefixes the pages resolve (documented in CLAUDE.md «Deep links») ─────────────

    /// <summary>Apps page: «app:&lt;winget id&gt;» — the card of that program.</summary>
    public const string AppFocusPrefix = "app:";
    /// <summary>Cleaner tab: «clean:&lt;item id&gt;» — the row of that cleanup item.</summary>
    public const string CleanFocusPrefix = "clean:";
    /// <summary>Services page: «startup:&lt;entry key&gt;» — the АВТОЗАПУСК tab filtered to that entry.</summary>
    public const string StartupFocusPrefix = "startup:";
    /// <summary>Settings page: «setting:&lt;row&gt;» — scrolls to that row.</summary>
    public const string SettingFocusPrefix = "setting:";
    /// <summary>Restore page: «section:&lt;name&gt;» — a part of the page (last batch, restore points, drift).</summary>
    public const string RestoreSectionPrefix = "section:";

    /// <summary>The Apps catalog (verified winget / Store packages): «Steam», «Discord», «7-Zip»…</summary>
    public static IReadOnlyList<Entry> AppEntries { get; } = AppCatalog.All
        .Select(a => new Entry(PaletteKind.App, a.Name, Loc.Tf("{0} — раздел «Приложения», {1}.", a.Description, AppCatalog.Info(a.Category).Title.ToLowerInvariant()),
            $"{a.Id} {AppCatalog.Info(a.Category).Title} " + Loc.Keep("программа установить") + $" {a.Website}", "apps", AppFocusPrefix + a.Id))
        .ToList();

    /// <summary>The cleanup catalog: «Корзина», «Кэш DirectX», «Временные файлы Windows»…</summary>
    public static IReadOnlyList<Entry> CleanEntries { get; } = CleanCatalog.All
        .Select(c => new Entry(PaletteKind.Clean, c.Name, Loc.Tf("{0} — раздел «Обслуживание → Очистка», {1}.", c.Description, CleanCatalog.CategoryTitle(c.Category).ToLowerInvariant()),
            $"{c.Id} {CleanCatalog.CategoryTitle(c.Category)} " + Loc.Keep("очистка мусор кэш удалить"), MaintenanceViewModel.CleanerTab, CleanFocusPrefix + c.Id))
        .ToList();

    /// <summary>Rows of the Settings page (static: the page has a fixed set of options).</summary>
    public static IReadOnlyList<Entry> SettingsEntries { get; } =
    [
        new(PaletteKind.Setting, Loc.Key("Язык / Language"), Loc.Key("Язык интерфейса: авто, русский или English — раздел «Настройки»."),
            Loc.Keep("язык language english русский английский интерфейс локализация"), "settings", SettingFocusPrefix + "language"),
        new(PaletteKind.Setting, Loc.Key("Запускать сразу с правами администратора"), Loc.Key("Запрос UAC при каждом запуске вместо перезапуска при первом изменении — раздел «Настройки»."),
            Loc.Keep("права администратора uac запуск elevate"), "settings", SettingFocusPrefix + "elevate"),
        new(PaletteKind.Setting, Loc.Key("Подтверждение перед применением"), Loc.Key("Показывать точный список изменений перед применением — раздел «Настройки»."),
            Loc.Keep("подтверждение применить список изменений confirm"), "settings", SettingFocusPrefix + "confirm"),
        new(PaletteKind.Setting, Loc.Key("Интервал обновления мониторинга"), Loc.Key("Как часто Панель опрашивает процессор, видеокарту, память и диски — раздел «Настройки»."),
            Loc.Keep("мониторинг интервал панель опрос"), "settings", SettingFocusPrefix + "monitor-interval"),
        new(PaletteKind.Setting, Loc.Key("Журнал действий (консоль)"), Loc.Key("Показывать журнал действий Lycoris внизу окна — раздел «Настройки»."),
            Loc.Keep("консоль журнал лог"), "settings", SettingFocusPrefix + "console"),
        new(PaletteKind.Setting, Loc.Key("Папки данных Lycoris"), Loc.Key("Где лежат настройки, журнал изменений, резервные копии и логи — раздел «Настройки»."),
            Loc.Keep("данные папки журнал резервные копии логи файлы"), "settings", SettingFocusPrefix + "data"),
        new(PaletteKind.Setting, Loc.Key("Сброс настроек Lycoris"), Loc.Key("Вернуть параметры Lycoris к значениям по умолчанию — раздел «Настройки»."),
            Loc.Keep("сброс по умолчанию настройки"), "settings", SettingFocusPrefix + "reset"),
        new(PaletteKind.Setting, Loc.Key("Как Lycoris защищает систему"), Loc.Key("Журнал отката, точки восстановления, что никогда не делается — раздел «Настройки → О программе»."),
            Loc.Keep("безопасность защита откат о программе"), "settings", SettingFocusPrefix + "about"),
        new(PaletteKind.Setting, Loc.Key("Компоненты и лицензии"), Loc.Key("Версия, шрифты, библиотеки — раздел «Настройки → О программе»."),
            Loc.Keep("лицензии версия компоненты о программе"), "settings", SettingFocusPrefix + "licenses"),
    ];

    /// <summary>Tabs of the Drivers page («tab:&lt;DriversTab&gt;», resolved by the page).</summary>
    public static IReadOnlyList<Entry> DriverTabEntries { get; } =
    [
        new(PaletteKind.Page, Loc.Key("ПОДБОР ДРАЙВЕРОВ ДЛЯ ВАШЕГО ПК"), Loc.Key("Видеокарта, чипсет, сеть, звук: что установлено и что стоит обновить — раздел «Драйверы»."),
            Loc.Keep("драйверы видеокарта nvidia amd intel чипсет gpu подбор"), "drivers", "tab:Hardware", IsSection: true),
        new(PaletteKind.Page, Loc.Key("ОБНОВЛЕНИЯ ДРАЙВЕРОВ ИЗ WINDOWS UPDATE"), Loc.Key("Необязательные обновления драйверов Центра обновления — раздел «Драйверы»."),
            Loc.Keep("драйверы обновления windows update"), "drivers", "tab:Updates", IsSection: true),
        new(PaletteKind.Page, Loc.Key("ПРОБЛЕМНЫЕ УСТРОЙСТВА"), Loc.Key("Устройства с ошибками и без драйвера — раздел «Драйверы»."),
            Loc.Keep("драйверы проблемы устройства ошибка диспетчер устройств"), "drivers", "tab:Problems", IsSection: true),
        new(PaletteKind.Page, Loc.Key("УСТАНОВЛЕННЫЕ ДРАЙВЕРЫ"), Loc.Key("Список установленных драйверов и их версии — раздел «Драйверы»."),
            Loc.Keep("драйверы установленные версии список"), "drivers", "tab:Installed", IsSection: true),
        new(PaletteKind.Page, Loc.Key("РЕЗЕРВНАЯ КОПИЯ ДРАЙВЕРОВ"), Loc.Key("Сохранить и восстановить драйверы — раздел «Драйверы»."),
            Loc.Keep("драйверы резервная копия бэкап восстановить"), "drivers", "tab:Backup", IsSection: true),
    ];

    /// <summary>Parts of the Restore page.</summary>
    public static IReadOnlyList<Entry> RestoreEntries { get; } =
    [
        new(PaletteKind.Page, Loc.Key("ОТКАТИТЬ ПОСЛЕДНИЙ ПАКЕТ ИЗМЕНЕНИЙ"), Loc.Key("Вернуть последние изменения Lycoris — раздел «Восстановление»."),
            Loc.Keep("откат отменить вернуть последний пакет журнал"), "restore", RestoreSectionPrefix + "last-batch", IsSection: true),
        new(PaletteKind.Page, Loc.Key("ЖУРНАЛ ИЗМЕНЕНИЙ LYCORIS"), Loc.Key("Каждое изменение с исходным значением и кнопкой отката — раздел «Восстановление»."),
            Loc.Keep("журнал изменений история откат"), "restore", RestoreSectionPrefix + "journal", IsSection: true),
        new(PaletteKind.Page, Loc.Key("ТОЧКИ ВОССТАНОВЛЕНИЯ WINDOWS"), Loc.Key("Создать точку, включить защиту системы, открыть восстановление Windows — раздел «Восстановление»."),
            Loc.Keep("точка восстановления защита системы windows создать"), "restore", RestoreSectionPrefix + "restore-points", IsSection: true),
        new(PaletteKind.Page, Loc.Key("ИЗМЕНЕНО ВНЕ LYCORIS"), Loc.Key("Настройки Lycoris, изменённые Windows или другой программой — раздел «Восстановление»."),
            Loc.Keep("изменено вне monolith drift расхождение"), "restore", RestoreSectionPrefix + "drift", IsSection: true),
    ];

    /// <summary>Autostart entries found by the last scan (live data; empty until the АВТОЗАПУСК tab has loaded).</summary>
    public static IEnumerable<Entry> StartupEntries(IEnumerable<(string Key, string Name, string? Publisher, string? Command)> items) =>
        items.Select(i => new Entry(PaletteKind.Startup, i.Name,
            Loc.Tf("{0} в автозапуске — раздел «Автозапуск и службы», вкладка «Автозапуск».", string.IsNullOrWhiteSpace(i.Publisher) ? Loc.T("Программа") : i.Publisher),
            $"{i.Publisher} {i.Command} " + Loc.Keep("автозапуск startup"), "services", StartupFocusPrefix + i.Key));

    /// <summary>Everything static (no live scans): catalogs, Settings rows, Drivers tabs, Restore sections.</summary>
    public static IEnumerable<Entry> StaticExtras => AppEntries.Concat(CleanEntries).Concat(SettingsEntries).Concat(DriverTabEntries).Concat(RestoreEntries);

    /// <summary>The index for the running app: pages from the shell, tools from the live Tools tab, autostart from the live scan.</summary>
    public static IReadOnlyList<PaletteEntry> Build(IEnumerable<PageViewModel> pages)
    {
        var pageList = pages.ToList();
        var tools = pageList.OfType<MaintenanceViewModel>().FirstOrDefault()?.Tools ?? pageList.OfType<ToolsViewModel>().FirstOrDefault();
        var startup = pageList.OfType<ServicesViewModel>().FirstOrDefault()?.Startup.Items
            .Select(i => (i.Key, i.DisplayName, i.Entry.Publisher, (string?)i.Entry.Command)).ToList() ?? [];
        return Build(
            pageList.Select(p => (p.Key, p.Title, p.Subtitle)),
            TweakCatalog.All,
            tools?.Sections ?? [],
            tools?.QuickLinks ?? [],
            ServiceCatalog.Services,
            Sections,
            DeviceEntries,
            RejectedTweaks.All,
            StaticExtras.Concat(StartupEntries(startup)));
    }
}
