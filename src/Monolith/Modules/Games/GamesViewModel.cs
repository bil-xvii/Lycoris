using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Games;

public sealed partial class GamesViewModel : PageViewModel
{
    public override string Key => "games";
    public override string Index => "03";
    public override string Title => "ИГРЫ";
    public override string Subtitle => "Библиотека игр и игровой режим: максимум ресурсов игре, пока она запущена.";
    public override string Glyph => Glyphs.Game;
}