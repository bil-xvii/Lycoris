using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Home;
using Xunit;

namespace Lycoris.Tests;

public sealed class HomeTests
{
    [Fact]
    public void Home_page_uses_existing_dashboard_health_and_has_stable_navigation_key()
    {
        var dashboard = new DashboardViewModel();
        var home = new HomeViewModel(dashboard);

        Assert.Equal("home", home.Key);
        Assert.Equal("Доктор собирает сведения при первом открытии Панели.", home.DoctorText);
    }
}
