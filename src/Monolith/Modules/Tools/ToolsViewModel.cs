using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Tools;

public sealed partial class ToolsViewModel : PageViewModel
{
    public override string Key => "tools";
    public override string Index => "09";
    public override string Title => "ИНСТРУМЕНТЫ";
    public override string Subtitle => "Проверка и восстановление Windows, диски, сеть и системные утилиты.";
    public override string Glyph => Glyphs.Repair;
}