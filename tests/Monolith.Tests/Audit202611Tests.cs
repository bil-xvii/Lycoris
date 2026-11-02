using System.ServiceProcess;
using Monolith.Core;
using Monolith.Modules.Restore;

namespace Monolith.Tests;

/// <summary>Regression tests for the defects of the correctness audit (docs/audit-2026-11.md).</summary>
public sealed class Audit202611Tests
{
    [Fact]
    public void Journal_service_texts_are_Russian_whatever_the_interface_language()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("Вручную", ServiceControl.DescribeStartNeutral(ServiceStartMode.Manual, false));
        Assert.Equal("Автоматически (отложенный запуск)", ServiceControl.DescribeStartNeutral(ServiceStartMode.Automatic, true));
        var snapshot = new ServiceStartSnapshot("X", ServiceStartMode.Disabled, false);
        Assert.Equal("Отключена", snapshot.DescribeNeutral());
    }

    [Theory]
    [InlineData("Manual", "Вручную")]
    [InlineData("Вручную", "Вручную")]
    [InlineData(" disabled ", "Отключена")]
    [InlineData("Automatic (delayed start)", "Автоматически (отложенный запуск)")]
    [InlineData("что-то своё", "что-то своё")]
    public void Start_type_text_of_either_language_normalizes(string text, string expected)
    {
        Assert.Equal(expected, ServiceControl.NormalizeStart(text));
        using var _ = Loc.UseLanguage("en");
        Assert.Equal(expected, ServiceControl.NormalizeStart(text));
    }

    [Fact]
    public void A_record_written_in_English_is_not_drift_and_can_be_reapplied()
    {
        // An English-interface session wrote "Manual"; the live read is the neutral Russian text.
        Assert.False(LiveState.IsDrift(ChangeKind.Service, "Manual", "Вручную"));
        Assert.True(LiveState.IsDrift(ChangeKind.Service, "Manual", "Отключена"));
        Assert.True(DriftDetector.TryParseServiceStart("Manual", out var mode, out var delayed));
        Assert.Equal(ServiceStartMode.Manual, mode);
        Assert.False(delayed);
    }
}
