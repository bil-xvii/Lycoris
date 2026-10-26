using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Dashboard;

public sealed partial class DashboardViewModel : PageViewModel
{
    public override string Key => "dashboard";
    public override string Index => "01";
    public override string Title => "ПАНЕЛЬ";
    public override string Subtitle => "Состояние системы в реальном времени, оценка и рекомендации.";
    public override string Glyph => Glyphs.Home;
}