using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Apps;

public sealed partial class AppsViewModel : PageViewModel
{
    public override string Key => "apps";
    public override string Index => "08";
    public override string Title => "ПРИЛОЖЕНИЯ";
    public override string Subtitle => "Лаунчеры, игровые библиотеки и программы — установка и обновление в один клик через winget.";
    public override string Glyph => Glyphs.Download;
}