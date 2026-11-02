using System.ServiceProcess;
using Monolith.Modules.Services;
using Monolith.Modules.Startup;

namespace Monolith.Tests;

/// <summary>English for the Services and Startup areas (the Russian texts are asserted by ServicesTests / StartupTests).</summary>
public sealed class ServicesStartupEnglishTests
{
    [Fact]
    public void Service_start_types_and_states_are_english()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("Automatic (delayed, trigger)", ServiceText.Start(ServiceStartMode.Automatic, true, true));
        Assert.Equal("Manual", ServiceText.Start(ServiceStartMode.Manual, false));
        Assert.Equal("Running", ServiceText.Status(ServiceControllerStatus.Running));
    }

    [Fact]
    public void Lock_reason_of_a_per_user_service_is_english_with_the_template_name()
    {
        using var _ = Loc.UseLanguage("en");
        var text = ServiceGuard.GetLockReason("CDPUserSvc_1a2b3", 0xF0, 3, 1, 0, "")!;
        Assert.Contains("per-user service", text);
        Assert.Contains("CDPUserSvc", text);
    }

    [Fact]
    public void Boot_seconds_use_a_dot_in_english()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("41.2 s", BootPerformance.Seconds(41_200));
    }

    [Theory]
    [InlineData(1, "1 boot")]
    [InlineData(3, "3 boots")]
    public void Boot_count_plural_is_english(int n, string expected)
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal(expected, Loc.N(n, "{0} загрузку", "{0} загрузки", "{0} загрузок"));
    }

    [Fact]
    public void Journal_titles_stay_russian_in_english_mode()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("Автозапуск: включение «Foo»", StartupChanges.BatchTitle(["Foo"], enable: true));
    }
}
