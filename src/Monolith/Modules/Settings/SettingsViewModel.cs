using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Settings;

public sealed partial class SettingsViewModel : PageViewModel
{
    public override string Key => "settings";
    public override string Index => "11";
    public override string Title => "НАСТРОЙКИ";
    public override string Subtitle => "Поведение программы, безопасность и сведения о MONOLITH.";
    public override string Glyph => Glyphs.Settings;
}