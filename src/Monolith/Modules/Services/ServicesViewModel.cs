using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Services;

public sealed partial class ServicesViewModel : PageViewModel
{
    public override string Key => "services";
    public override string Index => "06";
    public override string Title => "СЛУЖБЫ";
    public override string Subtitle => "Службы Windows: рекомендации и полный список. Критичные службы защищены от изменений.";
    public override string Glyph => Glyphs.Services;
}