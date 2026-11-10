using Lycoris.Modules.Drivers;
using Xunit;

namespace Lycoris.Tests;

/// <summary>One automatic retry of driver downloads that failed for a transient reason (10.11.2026: 0x80240034 / 0x80070002, fine a minute later).</summary>
public class WuDownloadRetryTests
{
    [Theory]
    [InlineData(WuText.OrcFailed, unchecked((int)0x80240034), true)]   // WU_E_DOWNLOAD_FAILED — the owner's case
    [InlineData(WuText.OrcFailed, unchecked((int)0x80070002), true)]   // file not found in the download cache
    [InlineData(WuText.OrcFailed, unchecked((int)0x80072EE2), true)]   // internet timeout
    [InlineData(WuText.OrcFailed, unchecked((int)0x80246007), true)]   // not downloaded
    [InlineData(WuText.OrcFailed, 0, true)]                            // failed without a code
    [InlineData(WuText.OrcSucceeded, 0, false)]                        // downloaded fine
    [InlineData(WuText.OrcSucceededWithErrors, unchecked((int)0x80240034), false)]
    [InlineData(WuText.OrcAborted, unchecked((int)0x80240034), false)] // cancelled by the user
    [InlineData(WuText.OrcFailed, WuText.AccessDenied, false)]         // rights — a retry will not help
    [InlineData(WuText.OrcFailed, WuText.EulasDeclined, false)]        // licence not accepted
    [InlineData(WuText.OrcFailed, WuText.ServiceDisabled, false)]      // service off
    [InlineData(WuText.OrcFailed, unchecked((int)0x8024002E), false)]  // blocked by policy
    public void Only_transient_download_failures_are_retried(int resultCode, int hresult, bool retry) =>
        Assert.Equal(retry, WuText.IsRetryableDownload(resultCode, hresult));

    [Fact]
    public void The_download_failure_message_says_a_retry_already_happened_and_what_to_do_next()
    {
        var text = WuText.DescribeHResult(WuText.DownloadFailed);
        Assert.Contains("повторил", text);
        Assert.Contains("Сброс Центра обновления Windows", text);
        Assert.Equal(text, WuText.DescribeHResult(WuText.FileNotFound));
    }

    [Fact]
    public void The_retry_pause_is_short() =>
        Assert.InRange(WindowsUpdateDrivers.RetryDelay, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));
}
