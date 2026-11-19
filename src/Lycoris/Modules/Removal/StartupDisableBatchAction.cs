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
        var assessments = new List<RemovalRow>();
        var facts = _observe.Capture(ct);
        foreach (var group in _scan().GroupBy(entry => entry.Key, Keys))
        {
            ct.ThrowIfCancellationRequested();
            if (group.Skip(1).Any())
            {
                var rows = group.Select(entry => RemovalPolicy.Startup(entry, unique: false)).ToArray();
                assessments.Add(rows.FirstOrDefault(row => row.Decision == RemovalDecision.Keep) ?? rows[0]);
                continue;
            }

            var assessment = _observe.Read(group.First(), facts, ct);
            assessments.Add(assessment.Row);
            if (assessment is { Row.Decision: RemovalDecision.DisableStartup, Proof: { } observation }) confirmed.Add(observation);
        }
        problem = AccountProblem(sid);
        if (problem is not null) return new Plan(this, sid, time, stamp, [], [], problem);
        if (_time.GetElapsedTime(stamp) >= MaximumAge) return new Plan(this, sid, time, stamp, [], [], Expired);

        var preview = confirmed.Select(observation =>
        {
            var entry = observation.Startup;
            var row = RemovalPolicy.Startup(entry, unique: true, confirmed: true);
            var scope = StartupLocations.IsMachine(entry.Source) ? Loc.T("ДЛЯ ВСЕХ") : Loc.T("ДЛЯ МЕНЯ");
            return new RemovalBatchItem(row.Key, row.Name, $"{scope} · {StartupLocations.DescribeMark(entry)}",
                row.Reason);
        }).ToArray();
        return new Plan(this, sid, time, stamp, confirmed.AsReadOnly(), Array.AsReadOnly(preview), null)
        {
            AssessedRows = assessments.AsReadOnly(),
            Summary = Loc.Tf("Можно отключить {0} из {1} проверенных записей автозапуска.", preview.Length, assessments.Count),
        };
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
        try
        {
            return await StaWorker.Run(() =>
            {
                // Starting the session can wait for a restore point. Use the original approved identities again.
                var afterBegin = Revalidate(approved, beforeBegin.Entries, ct);
                if (afterBegin.Error is { } afterError) return Failure(requested.Length, afterError);
                return Apply(session, approved, afterBegin.Entries, requested.Length, ct);
            }, "Lycoris startup batch write").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new RemovalBatchResult(0, requested.Length, []);
        }
        finally { session.Complete(); }
    }

    private (StartupDisableObservation.Entry[] Entries, string? Error) Revalidate(Plan plan,
        StartupDisableObservation.Entry[] candidates, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ExecutionProblem(plan) is { } problem) return ([], problem);
        var facts = _observe.Capture(ct);
        var current = candidates.Where(entry =>
        {
            ct.ThrowIfCancellationRequested();
            return _observe.Read(entry.Startup, facts, ct).Proof == entry;
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
            RemovalSoftwareSnapshot facts;
            try { facts = _observe.Capture(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            session.Exclusive(() =>
            {
                var entry = observation.Startup;
                var name = RemovalPolicy.Startup(entry, unique: true).Name;
                var write = WriteFor(observation);
                var rejected = false;
                var wrote = session.SetRegistry(write.Hive, write.Key, write.Name, write.Kind, write.Value, JournalSource, name, snapshot =>
                {
                    try
                    {
                        rejected = ct.IsCancellationRequested
                            || (blocked = ExecutionProblem(plan)) is not null
                            || !write.MatchesOriginal(snapshot)
                            || _observe.Read(entry, facts, ct).Proof != observation
                            || ct.IsCancellationRequested
                            || (blocked = ExecutionProblem(plan)) is not null;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { rejected = true; }
                    return !rejected;
                });
                if (rejected) return;

                var live = AccountProblem(plan.PreparedSid) is null ? write.ReadBack() : ReadBack.Uncertain;
                if (wrote && live == ReadBack.Changed)
                {
                    applied++;
                    return;
                }

                if (wrote || live != ReadBack.Unchanged)
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
        errors.AddRange(session.Errors);
        return new RemovalBatchResult(applied, requested - applied - (unknown ? 1 : 0), errors.AsReadOnly(), unknown);
    }

    private enum ReadBack { Unchanged, Changed, Uncertain }

    private sealed record MarkWrite(RegHive Hive, string Key, string Name, RegistryValueKind Kind, object Value,
        Func<RegistryValueSnapshot, bool> MatchesOriginal, Func<ReadBack> ReadBack);

    private MarkWrite WriteFor(StartupDisableObservation.Entry observation)
    {
        var entry = observation.Startup;
        if (observation is StartupDisableObservation.PackagedEntry packaged)
        {
            var disabled = (int)PackagedTaskState.DisabledByUser;
            return new MarkWrite(RegHive.HKCU, PackagedStartup.KeyPath(entry.PackageFamilyName!, entry.Name),
                PackagedStartup.StateValue, RegistryValueKind.DWord, disabled,
                snapshot => PackagedStartup.OriginalState(snapshot) == packaged.State, () =>
                {
                    try
                    {
                        var state = StartupDisableObservation.ReadPackageState(entry);
                        return state == disabled ? ReadBack.Changed : state == packaged.State ? ReadBack.Unchanged : ReadBack.Uncertain;
                    }
                    catch (Exception ex) when (RemovalSoftwareEvidence.ReadFailed(ex)) { return ReadBack.Uncertain; }
                });
        }

        var classic = (StartupDisableObservation.ClassicEntry)observation;
        var bytes = StartupApproved.Build(false, _time.GetUtcNow().UtcDateTime);
        var desired = new StartupDisableObservation.Approval(StartupDisableObservation.ApprovalState.Disabled, Convert.ToBase64String(bytes));
        return new MarkWrite(StartupLocations.ApprovedHive(entry.Source), StartupLocations.ApprovedKey(entry.Source),
            entry.Name, RegistryValueKind.Binary, bytes, classic.Mark.Matches, () =>
            {
                var live = StartupDisableObservation.ReadApproval(entry);
                return live == desired ? ReadBack.Changed : live == classic.Mark ? ReadBack.Unchanged : ReadBack.Uncertain;
            });
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
