using Lycoris.Shell;
using Lycoris.UI;

namespace Lycoris.Modules.Assistant;

/// <summary>
/// QA only («--demo-assistant», honoured only together with --snapshot): a sample conversation so the filled «Помощник» page can be
/// looked at on a screenshot (CI machines have no Ollama). Nothing is sent anywhere; the buttons point to real places of the palette index.
/// </summary>
public static class AssistantDemo
{
    public const string Switch = "--demo-assistant";

    public static bool Resolve(bool snapshot, IReadOnlyList<string> args) =>
        snapshot && args.Any(a => a.Equals(Switch, StringComparison.OrdinalIgnoreCase));

    internal static void Fill(AssistantViewModel vm)
    {
        vm.ShowDemoState(["qwen3:8b", "llama3.2:3b"], "qwen3:8b");
        vm.AddMessage(new AssistantMessage { IsUser = true, Text = Loc.T("Почему игры иногда подтормаживают?") });
        var answer = new AssistantMessage
        {
            Text = Loc.T("Судя по проверкам, при входе в Windows запускается 14 программ — часть из них работает в фоне и во время игры. Посмотрите список в «Автозапуске» и отключите то, чем не пользуетесь. Ещё выключена оптимизация оконных игр: включите её в «Оптимизации» — это безопасно и откатывается в один клик."),
        };
        var index = Ui.Host is MainViewModel shell ? CommandPaletteIndex.Build(shell.Pages) : [];
        foreach (var query in new[] { "автозапуск", "оконных игр" })
            if (CommandPaletteSearch.Search(index, query, 1) is [var hit]) answer.Actions.Add(hit);
        vm.AddMessage(answer);
        vm.AddMessage(new AssistantMessage { IsUser = true, Text = Loc.T("А отключать Защитник Windows стоит?") });
        vm.AddMessage(new AssistantMessage
        {
            Text = Loc.T("Нет. Защитник почти не влияет на FPS в играх, а без него ПК остаётся без защиты от вирусов. Lycoris намеренно не отключает его."),
        });
    }
}
