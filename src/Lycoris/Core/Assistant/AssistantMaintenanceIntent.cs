using System.Text.RegularExpressions;
using Lycoris.Modules.Maintenance;
using Lycoris.Shell;

namespace Lycoris.Core.Assistant;

/// <summary>Matches maintenance topics to fixed, read-only navigation destinations.</summary>
internal static class AssistantMaintenanceIntent
{
    private const RegexOptions Words = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Review = new(Loc.Keep(
        @"\b(?:провер\w*|посмотр\w*|просмотр\w*|покаж\w*|най\w*|какие|как|что|лишн\w*|ненужн\w*|неиспользуем\w*|необязательн\w*|удал\w*|очист\w*|почист\w*|отключ\w*|убра\w*|check|review|show|find|which|what|how|unneeded|unnecessary|unused|optional|remove|uninstall|clean(?:up)?|disable)\b"), Words);
    private static readonly Regex BareStartupCommand = new(Loc.Keep(
        @"\A *(?:(?:отключи(?:ть)?|выключи(?:ть)?) +(?:автозапуск|автозагрузку)|disable +(?:startup|autostart|start-up)) *\z"), Words);

    private static readonly (string Page, Regex Topic, string Guidance)[] Topics =
    [
        ("removal", new Regex(Loc.Keep(@"\b(?:программ\w*|приложени\w*|стор\w*|store|app(?:s|lications?)?|programs?|software|bloatware)\b"), Words),
            Loc.Key("В «Удалении и отключении» можно просмотреть установленные программы, в том числе приложения Microsoft Store, и решить, какие оставить. По запросу нельзя определить, какими из них вы пользуетесь.")),
        ("services", new Regex(Loc.Keep(@"\b(?:служб(?:ы|ам|ами|ах)?|services)\b"), Words),
            Loc.Key("В разделе «Автозапуск и службы» проверьте назначение служб и текущие настройки. Для изменения укажите одну службу и одно действие.")),
        (MainViewModel.StartupAlias, new Regex(Loc.Keep(@"\b(?:автозапуск\w*|автозагруз\w*|startup|autostart|start-up)\b"), Words),
            Loc.Key("В «Автозапуске» проверьте, что запускается вместе с Windows. Отключайте запись, только если знаете, зачем она нужна.")),
        (MaintenanceViewModel.CleanerTab, new Regex(Loc.Keep(@"\b(?:файл\w*|к[эе]ш\w*|мусор\w*|диск\w*|очистк\w*|files?|cache[sd]?|temporary|junk|disk|cleanup)\b"), Words),
            Loc.Key("В «Очистке» можно запустить анализ и проверить список временных файлов перед удалением.")),
    ];

    internal static IReadOnlyList<(string Page, string Guidance)> Match(string question) =>
        Review.IsMatch(question) || IsBareStartupRequest(question)
            ? Topics.Where(topic => topic.Topic.IsMatch(question)).Select(topic => (topic.Page, Loc.T(topic.Guidance))).ToArray()
            : [];

    internal static bool IsBareStartupRequest(string question) => BareStartupCommand.IsMatch(question);
}
