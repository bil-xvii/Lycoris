using Lycoris.Core;
using Lycoris.Core.Assistant;
using Lycoris.Core.Diagnostics;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Tweaks;

namespace Lycoris.Modules.Dashboard;

public sealed partial class DashboardViewModel
{
    private Task<AssistantDiagnosticSnapshot?>? _refreshTask;
    private bool _assistantFixRunning;

    internal Func<Task<SystemSnapshot>> InspectAsync { get; set; } = SystemInspector.CollectAsync;

    internal Task<AssistantDiagnosticSnapshot?> RefreshForAssistantAsync() => RefreshAsync(showBusy: true);

    // All callers run on the UI thread. Publish the task before IsChecking notifies observers so they can share it too.
    private Task<AssistantDiagnosticSnapshot?> RefreshAsync(bool showBusy)
    {
        if (_refreshTask is { IsCompleted: false } running) return running;
        var completion = new TaskCompletionSource<AssistantDiagnosticSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshTask = completion.Task;
        _ = CompleteRefreshAsync(showBusy, completion);
        return completion.Task;
    }

    private async Task CompleteRefreshAsync(bool showBusy, TaskCompletionSource<AssistantDiagnosticSnapshot?> completion)
    {
        AssistantDiagnosticSnapshot? fresh = null;
        IsChecking = true;
        try
        {
            if (showBusy)
                await RunBusyAsync(OperationClass.Measure, Loc.Key("Проверка системы…"), async () => fresh = await CheckCoreAsync());
            else if (await AcquireLeaseAsync(OperationClass.Measure, Loc.T("Проверка системы…")) is { } lease)
            {
                using (lease)
                using (lease.Scope())
                    fresh = await CheckCoreAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Проверка системы", ex);
        }
        finally
        {
            IsChecking = false;
            completion.SetResult(fresh);
        }
    }

    // The next check must begin after the confirmation or the write, even if a read from before it is still running.
    private async Task<AssistantDiagnosticSnapshot?> RefreshAfterBoundaryAsync()
    {
        if (_refreshTask is { IsCompleted: false } running) await running;
        return await RefreshAsync(showBusy: false);
    }

    internal async Task<AssistantFixResult> FixFromAssistantAsync(string id, Func<string, bool> requestAdmin)
    {
        if (ChangeSession.Locked || _assistantFixRunning || !AssistantDiagnostics.IsKnownFix(id))
            return new(AssistantFixState.NoLongerEligible, null);

        _assistantFixRunning = true;
        try
        {
            return await AssistantDiagnostics.FixAsync(id, RefreshForAssistantAsync, RefreshAfterBoundaryAsync,
                (tweak, checkCurrent) => ApplyTweakAsync(tweak.Id, id, checkCurrent, requestAdmin));
        }
        finally
        {
            _assistantFixRunning = false;
        }
    }

    internal static bool IsAssistantFixApplicable(HealthCheckResult check, SystemSnapshot snapshot, Func<Tweak, bool?> catalogApplies) =>
        AssistantDiagnostics.FixCandidate(check) is { } tweak
        && (tweak.Id != HealthRules.TweakTrim || (snapshot.SystemDisk?.IsSsd == true && snapshot.TrimPolicyAbsent == true))
        && (tweak.Id != HealthRules.TweakHags || snapshot.PrimaryGpuSupportsHags == true)
        && catalogApplies(tweak) == true;

    internal async Task<HealthReportData?> RefreshAssistantContextAsync()
    {
        var fresh = await RefreshForAssistantAsync();
        return fresh is null ? null : BuildHealthReportData(fresh);
    }
}
