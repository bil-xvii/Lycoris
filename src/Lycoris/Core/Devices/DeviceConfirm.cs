namespace Lycoris.Core.Devices;

/// <summary>
/// A «keep or go back» prompt: <see cref="KeepText"/> keeps the change, everything else — <see cref="RevertText"/>,
/// Esc, closing, the timeout — goes back. <see cref="AvoidGdiDevice"/> (\\.\DISPLAYn) is the monitor being changed:
/// the prompt appears on another one when there is another one. <see cref="Window"/> overrides
/// <see cref="Seconds"/> (tests use fractions of a second).
/// </summary>
public sealed record CountdownRequest(string Title, string Message, int Seconds, string KeepText, string RevertText, string? AvoidGdiDevice)
{
    public TimeSpan? Window { get; init; }

    public TimeSpan Duration => Window ?? TimeSpan.FromSeconds(Math.Max(1, Seconds));

    /// <summary>The one keep verb of every countdown (mode, HDR, buttons, pointer speed, brightness).</summary>
    public static string Keep => Loc.T("ОСТАВИТЬ");
    public static string Revert => Loc.T("ВЕРНУТЬ");
}

/// <summary>
/// The UI hooks device changes need (set by the shell: MainViewModel). Core never shows UI itself, and works without
/// them: a missing countdown means «go back», a missing question means «no».
/// </summary>
public static class DeviceConfirm
{
    /// <summary>Shows the countdown prompt; the token closes it. True = keep.</summary>
    public static Func<CountdownRequest, CancellationToken, Task<bool>>? Countdown { get; set; }

    /// <summary>Asks a yes/no question (title, message, details). True = yes.</summary>
    public static Func<string, string, IReadOnlyList<string>, Task<bool>>? Ask { get; set; }

    /// <summary>
    /// Runs the countdown. The deadline is a thread-pool timer (it fires even when the UI hangs): on timeout or
    /// <paramref name="ct"/> the prompt is closed and the answer is «go back». True only when the prompt answered
    /// «keep» before the deadline.
    /// </summary>
    public static async Task<bool> CountdownAsync(CountdownRequest r, CancellationToken ct)
    {
        var hook = Countdown;
        if (hook is null || ct.IsCancellationRequested) return false;
        using var promptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<bool> prompt;
        try
        {
            prompt = hook(r, promptCts.Token);
        }
        catch (Exception ex)
        {
            Log.Warn("Окно подтверждения не открылось: {0}", ex.Message);
            return false;
        }

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new Timer(_ => cancelled.TrySetResult(), null, r.Duration, Timeout.InfiniteTimeSpan);
        using var registration = ct.Register(() => cancelled.TrySetResult());
        var first = await Task.WhenAny(prompt, cancelled.Task).ConfigureAwait(false);
        if (first == prompt && !cancelled.Task.IsCompleted)
        {
            try { return prompt.IsCompletedSuccessfully && prompt.Result; }
            catch { return false; }
        }

        // Timed out or cancelled: close the prompt; whatever it answers later no longer counts.
        try { promptCts.Cancel(); } catch { }
        _ = prompt.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        return false;
    }

    /// <summary>Asks a question; false when no UI is attached.</summary>
    public static async Task<bool> AskAsync(string title, string message, IReadOnlyList<string> details)
    {
        var hook = Ask;
        if (hook is null) return false;
        try { return await hook(title, message, details).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Log.Warn("Вопрос «{0}» не показан: {1}", title, ex.Message);
            return false;
        }
    }
}
