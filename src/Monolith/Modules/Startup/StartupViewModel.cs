using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Startup;

public sealed partial class StartupViewModel : PageViewModel
{
    public override string Key => "startup";
    public override string Index => "07";
    public override string Title => "АВТОЗАПУСК";
    public override string Subtitle => "Программы и задачи, которые запускаются вместе с Windows.";
    public override string Glyph => Glyphs.Power;
}