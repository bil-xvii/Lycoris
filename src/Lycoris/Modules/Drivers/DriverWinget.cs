using Lycoris.Core;

namespace Lycoris.Modules.Drivers;

/// <summary>What a winget run started from the Drivers page ended with.</summary>
public enum DriverWingetOutcome
{
    Done,
    DoneReboot,
    Failed,
    /// <summary>The user pressed «СТОП»: the winget process tree was killed.</summary>
    Stopped,
    /// <summary>No answer within <see cref="DriverWinget.Timeout"/>: Lycoris stopped waiting, the process was left running.</summary>
    TimedOut,
}

/// <summary>
/// winget runs of the Drivers page (vendor tools, game runtimes of «ПОДБОР»): the limit, the outcome texts and the «СТОП».
/// The components are small (runtimes, vendor apps: minutes), so a run is waited for <see cref="Timeout"/>, not 30 minutes.
/// A timeout never kills (an installer cut half-way is worse); «СТОП» kills the winget tree only after the user confirms.
/// </summary>
public static class DriverWinget
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    public static int TimeoutMinutes => (int)Timeout.TotalMinutes;

    public static DriverWingetOutcome Classify(WingetResult r) =>
        r.Cancelled ? DriverWingetOutcome.Stopped
        : r.StillRunning ? DriverWingetOutcome.TimedOut
        : !r.Success ? DriverWingetOutcome.Failed
        : WingetClient.IsRebootRequired(r.ExitCode) ? DriverWingetOutcome.DoneReboot
        : DriverWingetOutcome.Done;

    public static string StoppedText => Loc.T("Остановлено — изменения, которые winget успел сделать, остались; проверьте список");

    public static string TimedOutText => Loc.Tf("winget не завершился за {0} мин — Lycoris перестал ждать, процесс не прерван. Установка может ещё идти: проверьте список позже", TimeoutMinutes);

    /// <summary>The text of a result that is not a success (stop, timeout, winget error).</summary>
    public static string ProblemText(WingetResult r) => Classify(r) switch
    {
        DriverWingetOutcome.Stopped => StoppedText,
        DriverWingetOutcome.TimedOut => TimedOutText,
        _ => r.Message,
    };
}

/// <summary>
/// One running winget call that the user may stop. <see cref="RunAsync"/> hands the work a token; <see cref="Stop"/> cancels
/// it (ProcessRunner then kills the winget process tree). A stop while the call still waits for the winget gate or for the
/// retry pause becomes a <see cref="WingetResult.Cancelled"/> result, never an exception.
/// </summary>
public sealed class WingetStopper
{
    private CancellationTokenSource? _cts;

    public bool IsRunning => Volatile.Read(ref _cts) is not null;

    public bool StopRequested => Volatile.Read(ref _cts) is { IsCancellationRequested: true };

    public async Task<WingetResult> RunAsync(Func<CancellationToken, Task<WingetResult>> run)
    {
        using var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null)
            throw new InvalidOperationException("A winget run of this page is already going.");
        try
        {
            var r = await run(cts.Token);
            return cts.IsCancellationRequested && !r.Cancelled && !r.Success ? r with { Cancelled = true } : r;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new WingetResult(-1, "", Cancelled: true);
        }
        finally
        {
            Interlocked.CompareExchange(ref _cts, null, cts);
        }
    }

    /// <summary>Stops the running call. False when nothing runs or a stop was already requested.</summary>
    public bool Stop()
    {
        var cts = Volatile.Read(ref _cts);
        if (cts is null || cts.IsCancellationRequested) return false;
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
