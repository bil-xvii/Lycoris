using Lycoris.Core.Measurements;
using Lycoris.Modules.Dashboard;

namespace Lycoris.Tests.Diagnostics;

public sealed class HealthReportMeasurementTests
{
    private static Measurement Sample(long? freeMemoryBytes) => new()
    {
        RecordedAt = DateTimeOffset.UnixEpoch,
        Conditions = new MeasurementConditions
        {
            WindowsVersion = "Windows 11", WindowsBuild = "26100", LycorisVersion = "1.0.0",
            EnabledStartupItems = 5, FreeMemoryBytes = freeMemoryBytes,
            BootTimeSeconds = null, BootTimeSource = "test"
        }
    };

    [Theory]
    [InlineData("ru", null, "свободная память — недоступно, время загрузки — недоступно")]
    [InlineData("en", null, "free memory — unavailable, boot time — unavailable")]
    [InlineData("ru", 0L, "свободная память — 0 МБ, время загрузки — недоступно")]
    [InlineData("en", 0L, "free memory — 0 MB, boot time — unavailable")]
    [InlineData("ru", 1572864L, "свободная память — 1 МБ, время загрузки — недоступно")]
    [InlineData("en", 1572864L, "free memory — 1 MB, boot time — unavailable")]
    public void Memory_distinguishes_unavailable_from_zero_and_keeps_megabyte_conversion(string language, long? bytes, string expected)
    {
        using var _ = Loc.UseLanguage(language);
        var text = DashboardViewModel.FormatHealthReportMeasurement(Sample(bytes));
        Assert.Contains(expected, text);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void No_measurement_has_no_summary(string language)
    {
        using var _ = Loc.UseLanguage(language);
        Assert.Null(DashboardViewModel.FormatHealthReportMeasurement(null));
    }
}
