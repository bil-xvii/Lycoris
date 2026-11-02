using Monolith.Core;

namespace Monolith.Tests.Concurrency;

public sealed class ProbeLimitTests
{
    [Fact]
    public async Task Many_short_child_processes_never_run_more_than_the_cap_at_once()
    {
        var cmd = ProcessRunner.Sys("cmd.exe");
        var results = await Task.WhenAll(Enumerable.Range(0, 14).Select(_ =>
            ProcessRunner.RunAsync(cmd, ["/c", "ping -n 2 127.0.0.1 >nul"],
                new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(30), LogCommand = false })))
            .WaitAsync(TimeSpan.FromMinutes(2));

        Assert.All(results, r => Assert.True(r.Success, r.Combined));
        Assert.InRange(ProcessRunner.PeakProbes, 1, ProcessRunner.MaxParallelProbes);
        Assert.Equal(0, ProcessRunner.ProbesRunning);
    }

    [Fact]
    public async Task A_cancelled_wait_for_a_probe_slot_returns_a_cancelled_result()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await ProcessRunner.RunAsync(ProcessRunner.Sys("cmd.exe"), ["/c", "exit 0"],
            new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(5), LogCommand = false }, cts.Token);
        Assert.True(r.Cancelled);
        Assert.Equal(0, ProcessRunner.ProbesRunning);
    }
}
