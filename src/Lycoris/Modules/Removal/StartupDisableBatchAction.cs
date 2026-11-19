using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.Modules.Startup;

namespace Lycoris.Modules.Removal;

/// <summary>Applies only the approved StartupApproved marks, each with its own exact journal snapshot.</summary>
internal sealed class StartupDisableBatchAction : IRemovalBatchAction
{
    internal const string JournalSource = "removal-startup";

    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);
    private static readonly StringComparer Keys = StringComparer.OrdinalIgnoreCase;
    private readonly Func<IReadOnlyList<StartupEntry>> _scan;
    private readonly StartupDisableObservation _observe;
    private readonly Func<string, IProgress<string>?, Task<ChangeSession>> _begin;
    private readonly TimeProvider _time;

    public StartupDisableBatchAction()
        : this(StartupScanner.Scan, new(StartupSignatures.ReadUncached, StartupLocations.FolderPath),
            (title, progress) => ChangeSession.BeginAsync(title, progress: progress), TimeProvider.System)
    {
    }

    internal StartupDisableBatchAction(Func<IReadOnlyList<StartupEntry>> scan, StartupDisableObservation observe,
        Func<string, IProgress<string>?, Task<ChangeSession>> begin, TimeProvider? time = null)
    {
        _scan = scan;
        _observe = observe;
        _begin = begin;
        _time = time ?? TimeProvider.System;
    }

    private sealed record Plan(StartupDisableBatchAction Owner, string? PreparedSid, DateTime PreparedUtc, long Timestamp,
        IReadOnlyList<StartupDisableObservation.Entry> Approved, IReadOnlyList<RemovalBatchItem> Preview, string? Reason)
        : RemovalBatchPlan(Guid.NewGuid(), PreparedSid, PreparedUtc, Preview, Reason);

    public RemovalBatchKind Kind => RemovalBatchKind.DisableStartup;

    private static string Title => Loc.T("Пакетное отключение автозапуска");
    private static string Expired => Loc.T("Предпросмотр автозапуска устарел. Откройте его снова.");
    private static string AccountChanged => Loc.T("Учётная запись изменилась. Откройте предпросмотр автозапуска снова.");
    private static string Locked => Loc.T("Изменения системы заблокированы (режим самопроверки).");

    public Task<RemovalBatchPlan> PrepareAsync(CancellationToken ct) =>
        StaWorker.Run<RemovalBatchPlan>(() => Prepare(ct), "Lycoris startup batch preview");

    private Plan Prepare(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var time = _time.GetUtcNow().UtcDateTime;
        var stamp = _time.GetTimestamp();
        var sid = UserScope.CurrentSid;
        var problem = SafetyLock.IsLocked ? Locked : AccountProblem(sid);
        if (problem is not null) return new Plan(this, sid, time, stamp, [], [], problem);

        var confirmed = new List<StartupDisableObservation.Entry>();
        foreach (var group in _scan().GroupBy(entry => entry.Key, Keys))
        {
            ct.ThrowIfCancellationRequested();
            if (group.Skip(1).Any()) continue;
            if (_observe.Read(group.First()) is { } observation) confirmed.Add(observation);
        }

        problem = AccountProblem(sid);
        if (problem is not null) return new Plan(this, sid, time, stamp, [], [], problem);
        if (_time.GetElapsedTime(stamp) >= MaximumAge) return new Plan(this, sid, time, stamp, [], [], Expired);

        var preview = confirmed.Select(observation =>
        {
            var entry = observation.Startup;
            var row = RemovalPolicy.Startup(entry, unique: true);
            var scope = StartupLocations.IsMachine(entry.Source) ? Loc.T("ДЛЯ ВСЕХ") : Loc.T("ДЛЯ МЕНЯ");
            return new RemovalBatchItem(row.Key, row.Name, $"{scope} · {StartupLocations.DescribeMark(entry)}",
                $"{entry.Command}\n{row.Reason}");
        }).ToArray();
        return new Plan(this, sid, time, stamp, confirmed.AsReadOnly(), Array.AsReadOnly(preview), null);
    }

    public async Task<RemovalBatchResult> ExecuteAsync(RemovalBatchPlan plan, IReadOnlySet<string> selectedKeys,
        IProgress<string>? progress, CancellationToken ct)
    {
        var selected = selectedKeys.ToHashSet(Keys);
        if (plan is not Plan approved || !ReferenceEquals(approved.Owner, this) || approved.Reason is not null)
            return Failure(selectedKeys.Count, Expired);

        var requested = approved.Approved.Where(entry => selected.Contains(entry.Startup.Key)).ToArray();
        if (selected.Count != selectedKeys.Count || requested.Length != selected.Count)
            return Failure(selectedKeys.Count, Loc.T("Выбранные записи отсутствуют в подтверждённом списке автозапуска."));
        if (requested.Length == 0) return new RemovalBatchResult(0, 0, []);

        // The user can open another app or receive policy changes while the confirmation/lease is pending.
        var beforeBegin = await StaWorker.Run(() => Revalidate(approved, requested, ct), "Lycoris startup batch check")
            .ConfigureAwait(false);
        if (beforeBegin.Error is { } beforeError) return Failure(requested.Length, beforeError);
        if (beforeBegin.Entries.Length == 0) return new RemovalBatchResult(0, requested.Length, []);

        ct.ThrowIfCancellationRequested();
        ChangeSession session;
        try { session = await _begin(Title, progress).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.IO.IOException)
        {
            return Failure(requested.Length, ex.Message);
        }
        return await StaWorker.Run(() =>
        {
            // Starting the session can wait for a restore point. Use the original approved identities again.
            var afterBegin = Revalidate(approved, beforeBegin.Entries, ct);
            if (afterBegin.Error is { } afterError) return Failure(requested.Length, afterError);
            return Apply(session, approved, afterBegin.Entries, requested.Length, ct);
        }, "Lycoris startup batch write").ConfigureAwait(false);
    }

    private (StartupDisableObservation.Entry[] Entries, string? Error) Revalidate(Plan plan,
        StartupDisableObservation.Entry[] candidates, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ExecutionProblem(plan) is { } problem) return ([], problem);
        var current = candidates.Where(entry =>
        {
            ct.ThrowIfCancellationRequested();
            return _observe.Read(entry.Startup) == entry;
        }).ToArray();
        return (current, ExecutionProblem(plan));
    }

    private RemovalBatchResult Apply(ChangeSession session, Plan plan, StartupDisableObservation.Entry[] candidates,
        int requested, CancellationToken ct)
    {
        var applied = 0;
        var unknown = false;
        var errors = new List<string>();
        foreach (var observation in candidates)
        {
            if (ct.IsCancellationRequested) break;
            if (ExecutionProblem(plan) is { } problem)
            {
                errors.Add(problem);
                break;
            }

            string? blocked = null;
            session.Exclusive(() =>
            {
                var entry = observation.Startup;
                var name = RemovalPolicy.Startup(entry, unique: true).Name;
                var bytes = StartupApproved.Build(false, _time.GetUtcNow().UtcDateTime);
                var desired = new StartupDisableObservation.Approval(StartupDisableObservation.ApprovalState.Disabled,
                    Convert.ToBase64String(bytes));
                var rejected = false;
                var wrote = session.SetRegistry(StartupLocations.ApprovedHive(entry.Source), StartupLocations.ApprovedKey(entry.Source),
                    entry.Name, RegistryValueKind.Binary, bytes, JournalSource, name, snapshot =>
                    {
                        rejected = ct.IsCancellationRequested
                            || (blocked = ExecutionProblem(plan)) is not null
                            || !observation.Mark.Matches(snapshot)
                            || _observe.Read(entry) != observation
                            || (blocked = ExecutionProblem(plan)) is not null;
                        return !rejected;
                    });
                if (rejected) return;

                var live = AccountProblem(plan.PreparedSid) is null
                    ? StartupDisableObservation.ReadApproval(entry)
                    : new StartupDisableObservation.Approval(StartupDisableObservation.ApprovalState.Unknown);
                if (wrote && live == desired)
                {
                    applied++;
                    return;
                }

                if (wrote || live != observation.Mark)
                {
                    unknown = true;
                    errors.Add(Loc.Tf("Не удалось подтвердить состояние автозапуска после изменения: {0}", name));
                }
            });
            if (blocked is not null)
            {
                errors.Add(blocked);
                break;
            }
            if (unknown) break;
        }
        session.Complete();
        errors.AddRange(session.Errors);
        return new RemovalBatchResult(applied, requested - applied - (unknown ? 1 : 0), errors.AsReadOnly(), unknown);
    }

    private string? ExecutionProblem(Plan plan)
    {
        if (SafetyLock.IsLocked) return Locked;
        if (!ChangeSession.BypassElevationForTests && !Elevation.IsElevated)
            return Loc.Tf("Для действия «{0}» нужны права администратора. Перезапустите Lycoris от имени администратора.", Loc.Sink(Title));
        if (AccountProblem(plan.PreparedSid) is { } account) return account;
        var age = _time.GetElapsedTime(plan.Timestamp);
        return age < TimeSpan.Zero || age >= MaximumAge ? Expired : null;
    }

    private static string? AccountProblem(string? sid)
    {
        if (Elevation.InteractiveUserProblem is { } problem) return problem;
        if (string.IsNullOrWhiteSpace(sid) || !UserScope.SidPattern.IsMatch(sid)) return Elevation.UnknownAccountText;
        return UserScope.Same(sid, UserScope.CurrentSid) ? null : AccountChanged;
    }

    private static RemovalBatchResult Failure(int skipped, string error) => new(0, skipped, [error]);
}
