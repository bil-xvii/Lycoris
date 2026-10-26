using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Restore;

public sealed partial class RestoreViewModel : PageViewModel
{
    public override string Key => "restore";
    public override string Index => "10";
    public override string Title => "ВОССТАНОВЛЕНИЕ";
    public override string Subtitle => "Журнал всех изменений MONOLITH, откат и точки восстановления Windows.";
    public override string Glyph => Glyphs.History;
}