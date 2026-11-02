using System.ServiceProcess;
using Lycoris.Core;
using Xunit;

namespace Lycoris.Tests.Core;

/// <summary>Texts of the system layer (Core/**) in both languages; values stored in the journal stay Russian.</summary>
public class CoreLocalizationTests
{
    [Fact]
    public void Service_start_types_are_translated_in_english_and_stay_russian_by_default()
    {
        Assert.Equal("Вручную", ServiceControl.DescribeStart(ServiceStartMode.Manual, false));
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("Manual", ServiceControl.DescribeStart(ServiceStartMode.Manual, false));
        Assert.Equal("Automatic (delayed start)", ServiceControl.DescribeStart(ServiceStartMode.Automatic, true));
    }

    [Fact]
    public void Self_test_lock_message_names_the_action_in_the_interface_language()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("“changing a service” is blocked: self-test mode.",
            Loc.Tf("«{0}» заблокировано: режим самопроверки.", Loc.Sink("изменение службы")));
    }

    [Fact]
    public void Revert_mismatch_text_is_localized()
    {
        var check = new RevertCheck(false, "1", "2");
        Assert.Equal("сейчас 1, ожидалось 2", check.MismatchText);
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("now 1, expected 2", check.MismatchText);
    }

    [Fact]
    public void Stored_journal_values_stay_russian_and_are_translated_when_shown()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("отсутствовало", Loc.Keep("отсутствовало"));
        Assert.Equal("missing", Loc.Sink("отсутствовало"));
        Assert.Equal("Enabled", Loc.Sink("Включена"));
    }
}
