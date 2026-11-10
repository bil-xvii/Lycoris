using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using Lycoris.Core;
using Lycoris.Modules.Drivers;

namespace Lycoris.Tests;

/// <summary>Drivers page: «СТОП» for a running winget and the shorter, honest timeout (docs/audit-2026-11.md, «Not fixed»).</summary>
public sealed class DriversWingetStopTests
{
    [Fact]
    public void Results_are_classified_as_done_failed_stopped_or_timed_out()
    {
        Assert.Equal(DriverWingetOutcome.Done, DriverWinget.Classify(new WingetResult(0, "")));
        Assert.Equal(DriverWingetOutcome.Done, DriverWinget.Classify(new WingetResult(WingetClient.AlreadyInstalled, "")));
        Assert.Equal(DriverWingetOutcome.DoneReboot, DriverWinget.Classify(new WingetResult(WingetClient.RebootRequiredToFinish, "")));
        Assert.Equal(DriverWingetOutcome.Failed, DriverWinget.Classify(new WingetResult(WingetClient.DownloadFailed, "")));
        Assert.Equal(DriverWingetOutcome.Stopped, DriverWinget.Classify(new WingetResult(-1, "", Cancelled: true)));
        Assert.Equal(DriverWingetOutcome.TimedOut, DriverWinget.Classify(new WingetResult(-1, "") { StillRunning = true }));
    }

    [Fact]
    public void The_stop_and_timeout_texts_are_honest_and_translated()
    {
        Assert.Equal("Остановлено — изменения, которые winget успел сделать, остались; проверьте список", DriverWinget.StoppedText);
        Assert.Contains(DriverWinget.TimeoutMinutes.ToString(), DriverWinget.TimedOutText);
        Assert.Contains("не прерван", DriverWinget.TimedOutText);
        Assert.Equal(DriverWinget.StoppedText, DriverWinget.ProblemText(new WingetResult(-1, "", Cancelled: true)));
        Assert.Equal(DriverWinget.TimedOutText, DriverWinget.ProblemText(new WingetResult(-1, "") { StillRunning = true }));

        using var _ = Loc.UseLanguage("en");
        Assert.StartsWith("Stopped", DriverWinget.StoppedText);
        Assert.Contains(DriverWinget.TimeoutMinutes + " min", DriverWinget.TimedOutText);
        Assert.Equal("STOP WINGET?", Loc.T("ОСТАНОВИТЬ WINGET?"));
        Assert.Equal("STOP", Loc.T("СТОП"));
    }

    [Fact]
    public void Drivers_wait_less_than_the_general_winget_limit_but_minutes_not_seconds()
    {
        Assert.True(DriverWinget.Timeout < WingetClient.DefaultTimeout);
        Assert.True(DriverWinget.Timeout >= TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Stop_cancels_the_running_call_and_gives_a_stopped_result()
    {
        var stopper = new WingetStopper();
        Assert.False(stopper.Stop()); // nothing runs

        var started = new TaskCompletionSource();
        var run = stopper.RunAsync(async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct); // a winget that never ends; ProcessRunner returns Cancelled, a delay throws
            return new WingetResult(0, "");
        });
        await started.Task;
        Assert.True(stopper.IsRunning);
        Assert.True(stopper.Stop());
        Assert.False(stopper.Stop()); // a second press does nothing

        var r = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(r.Cancelled);
        Assert.Equal(DriverWingetOutcome.Stopped, DriverWinget.Classify(r));
        Assert.False(stopper.IsRunning);
    }

    [Fact]
    public async Task A_killed_winget_that_reports_a_failure_counts_as_stopped()
    {
        var stopper = new WingetStopper();
        var r = await stopper.RunAsync(ct =>
        {
            stopper.Stop();
            return Task.FromResult(new WingetResult(1, "killed")); // exit code of the killed process
        });
        Assert.Equal(DriverWingetOutcome.Stopped, DriverWinget.Classify(r));
    }

    [Fact]
    public async Task A_normal_result_passes_unchanged_and_the_stopper_can_run_again()
    {
        var stopper = new WingetStopper();
        var first = await stopper.RunAsync(_ => Task.FromResult(new WingetResult(WingetClient.RebootRequiredToFinish, "ok")));
        Assert.Equal(DriverWingetOutcome.DoneReboot, DriverWinget.Classify(first));
        Assert.False(stopper.IsRunning);
        var second = await stopper.RunAsync(_ => Task.FromResult(new WingetResult(0, "")));
        Assert.True(second.Success);
    }

    [Fact]
    public async Task Only_one_winget_call_of_the_page_runs_at_a_time()
    {
        var stopper = new WingetStopper();
        var release = new TaskCompletionSource<WingetResult>();
        var first = stopper.RunAsync(_ => release.Task);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stopper.RunAsync(_ => Task.FromResult(new WingetResult(0, ""))));
        release.SetResult(new WingetResult(0, ""));
        Assert.True((await first).Success);
    }

    [Fact]
    public async Task Cancelling_a_process_kills_its_whole_tree_at_once()
    {
        // cmd.exe starts ping.exe as a child: the result must come back long before ping's 30 seconds.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var sw = Stopwatch.StartNew();
        var r = await ProcessRunner.RunAsync(ProcessRunner.Sys("cmd.exe"), "/c ping -n 30 127.0.0.1 >nul",
            new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromMinutes(2), KillOnTimeout = false }, cts.Token);
        Assert.True(r.Cancelled);
        Assert.False(r.StillRunning);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), sw.Elapsed.ToString());
    }

    [Fact]
    public void Drivers_page_has_the_stop_command_and_the_strip_members()
    {
        var stop = typeof(DriversViewModel).GetProperty("StopWingetCommand", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(stop);
        Assert.True(typeof(ICommand).IsAssignableFrom(stop!.PropertyType));
        Assert.NotNull(typeof(DriversViewModel).GetProperty("IsWingetRunning", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(DriversViewModel).GetProperty("ShowInstallStrip", BindingFlags.Public | BindingFlags.Instance));
    }
}
