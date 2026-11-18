using Lycoris.Core;

namespace Lycoris.Tests.Core;

public sealed class WingetDetectionReliabilityTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(WingetClient.NoApplicationsFound, false)]
    [InlineData(WingetClient.NoNetwork, null)]
    [InlineData(WingetClient.InternalError, null)]
    [InlineData(-1, null)]
    [InlineData(WingetClient.AlreadyInstalled, null)]
    public void Only_exact_list_results_establish_presence_or_absence(int exitCode, bool? expected) =>
        Assert.Equal(expected, WingetClient.InstalledState(new(exitCode, "")));

    [Fact]
    public void Cancelled_or_timed_out_checks_are_unknown_even_if_the_process_exit_code_is_zero()
    {
        Assert.Null(WingetClient.InstalledState(new(0, "", Cancelled: true)));
        Assert.Null(WingetClient.InstalledState(new(0, "") { TimedOut = true }));
        Assert.Null(WingetClient.InstalledState(new(0, "") { StillRunning = true }));
    }

    [Theory]
    [InlineData("{\"Sources\":[]}")]
    [InlineData("{\"Sources\":[{\"Packages\":[]}]}")]
    public void Structurally_valid_empty_export_is_successful(string json)
    {
        var result = WingetClient.ExportState(new(0, ""), json);
        Assert.True(result.Success);
        Assert.Empty(result.Packages);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"Sources\":[{}]}")]
    [InlineData("{\"Sources\":[{\"Packages\":[{\"Version\":\"1\"}]}]}")]
    [InlineData("{\"Sources\":[{\"Packages\":[{\"PackageIdentifier\":\"Valve.Steam\",\"Version\":5}]}]}")]
    [InlineData("{\"Sources\":[{\"Packages\":[{\"PackageIdentifier\":\"Valve.Steam\"},null]}]}")]
    public void Malformed_or_partly_invalid_export_never_proves_absence(string json)
    {
        var result = WingetClient.ExportState(new(0, ""), json);
        Assert.False(result.Success);
        Assert.Empty(result.Packages);
    }

    [Fact]
    public void Valid_json_cannot_hide_a_failed_cancelled_or_timed_out_export()
    {
        const string json = "{\"Sources\":[{\"Packages\":[{\"PackageIdentifier\":\"Valve.Steam\"}]}]}";
        Assert.False(WingetClient.ExportState(new(WingetClient.NoNetwork, ""), json).Success);
        Assert.False(WingetClient.ExportState(new(0, "", Cancelled: true), json).Success);
        Assert.False(WingetClient.ExportState(new(0, "") { TimedOut = true }, json).Success);
        Assert.False(WingetClient.ExportState(new(0, "") { StillRunning = true }, json).Success);
        Assert.False(WingetClient.ExportState(new(0, ""), null).Success);
    }
}
