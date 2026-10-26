using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Drivers;

public sealed partial class DriversViewModel : PageViewModel
{
    public override string Key => "drivers";
    public override string Index => "05";
    public override string Title => "ДРАЙВЕРЫ";
    public override string Subtitle => "Устройства, обновления драйверов через Windows Update и официальные утилиты производителей.";
    public override string Glyph => Glyphs.Devices;
}