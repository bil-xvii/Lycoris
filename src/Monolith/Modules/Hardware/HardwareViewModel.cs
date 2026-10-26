using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Hardware;

public sealed partial class HardwareViewModel : PageViewModel
{
    public override string Key => "hardware";
    public override string Index => "02";
    public override string Title => "ЖЕЛЕЗО";
    public override string Subtitle => "MONOLITH изучает комплектующие вашего ПК и подбирает под них драйверы, утилиты и библиотеки — только из официальных источников.";
    public override string Glyph => Glyphs.Component;
}
