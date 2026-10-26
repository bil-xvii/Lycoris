using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Optimize;

public sealed partial class OptimizeViewModel : PageViewModel
{
    public override string Key => "optimize";
    public override string Index => "02";
    public override string Title => "ОПТИМИЗАЦИЯ";
    public override string Subtitle => "Профили настройки Windows: обычный, игровой, рекомендуемый. Любое изменение можно откатить.";
    public override string Glyph => Glyphs.Bolt;
}