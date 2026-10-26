using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Cleaner;

public sealed partial class CleanerViewModel : PageViewModel
{
    public override string Key => "cleaner";
    public override string Index => "04";
    public override string Title => "ОЧИСТКА";
    public override string Subtitle => "Анализ и удаление временных файлов, кэшей и мусора — только из безопасных мест.";
    public override string Glyph => Glyphs.Delete;
}