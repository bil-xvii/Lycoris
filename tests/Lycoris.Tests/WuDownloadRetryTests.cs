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
    [InlineData(WuText.OrcFailed, unchecked((int)0x80D02002), true)]   // Delivery Optimization stalled
    [InlineData(WuText.OrcFailed, unchecked((int)0x8024600B), true)]   // content changed — download must restart
    [InlineData(WuText.OrcFailed, unchecked((int)0x80246002), true)]   // bad hash — fetch again
    [InlineData(WuText.OrcFailed, unchecked((int)0x80244021), true)]   // HTTP 502
    [InlineData(WuText.OrcFailed, unchecked((int)0x8024200D), false)]  // install-side code, not a download error
    [InlineData(WuText.OrcFailed, unchecked((int)0x80070070), false)]  // disk full — a retry will not help
    [InlineData(WuText.OrcInProgress, 0, false)]                       // still running
    [InlineData(WuText.OrcNotStarted, 0, false)]
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
    public void A_network_failure_waits_longer_than_a_dropped_file()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), WuText.RetryDelayFor([WuText.DownloadFailed]));
        Assert.Equal(TimeSpan.FromSeconds(30), WuText.RetryDelayFor([WuText.DownloadFailed, unchecked((int)0x8024001F)]));
    }
}
