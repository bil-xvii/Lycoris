using System.Runtime.CompilerServices;
using Lycoris.Core;
using Windows.Foundation.Metadata;
using Windows.Management.Deployment;

namespace Lycoris.Modules.Removal;

internal sealed class PackagedProgramRemovalBatchAction(
    Func<string?> desktopSid,
    Func<string, CancellationToken, Task<PackagedUninstallSnapshot?>> read,
    Func<bool> supportsDeferredRemoval,
    Func<string, CancellationToken, Task<int>> remove,
    TimeProvider clock,
    ChangeJournal? journal = null) : IRemovalBatchAction
{
    private static readonly SemaphoreSlim ExecutionGate = new(1, 1);
    private static readonly StringComparer Identity = StringComparer.OrdinalIgnoreCase;
    private static readonly TimeSpan MaximumPlanAge = TimeSpan.FromMinutes(5);
    // The project targets the 19041 SDK, whose projection does not expose this newer enum member.
    private const RemovalOptions DeferWhenInUse = (RemovalOptions)8192;
    private readonly ConditionalWeakTable<Plan, object> _issued = new();
    private ChangeJournal Journal => journal ?? ChangeJournal.Instance;

    private sealed record Plan(Guid Id, string? UserSid, DateTime ObservedUtc, IReadOnlyList<RemovalBatchItem> Items,
        string? UnavailableReason, IReadOnlyDictionary<string, Target> Candidates)
        : RemovalBatchPlan(Id, UserSid, ObservedUtc, Items, UnavailableReason);

    private sealed record Target(InstalledPackageRegistration Main, IReadOnlyList<InstalledPackageRegistration> Associated);

    public PackagedProgramRemovalBatchAction() : this(
        DesktopSid, PackagedRemovalInventory.ReadForUninstallAsync,
        () => ApiInformation.IsEnumNamedValuePresent("Windows.Management.Deployment.RemovalOptions", "DeferRemovalWhenPackagesAreInUse"),
        RemoveNativeAsync, TimeProvider.System) { }

    public RemovalBatchKind Kind => RemovalBatchKind.UninstallWindowsApps;

    internal static string? DesktopSid() => Elevation.InteractiveUserState == true ? UserScope.CurrentSid : null;

    public Task<RemovalBatchPlan> PrepareAsync(CancellationToken ct) => Task.Run(async () =>
    {
        var observed = clock.GetUtcNow().UtcDateTime;
        var sid = desktopSid();
        var candidates = new Dictionary<string, Target>(Identity);
        var items = new List<RemovalBatchItem>();
        string? unavailable = null;
        if (string.IsNullOrWhiteSpace(sid))
            unavailable = Loc.T("Не удалось подтвердить пользователя рабочего стола для удаления приложений Windows.");
        else if (!supportsDeferredRemoval())
            unavailable = Loc.T("Эта версия Windows не подтвердила поддержку отложенного удаления открытых приложений.");
        else if (await ReadCurrentAsync(sid, ct).ConfigureAwait(false) is not { } snapshot)
            unavailable = Loc.T("Не удалось подтвердить полный список приложений Windows и их зависимостей.");
        else
        {
            var graph = new RegistrationGraph(snapshot);
            foreach (var package in snapshot.Native.Registrations.OrderBy(package => package.Name, StringComparer.CurrentCulture))
            {
                ct.ThrowIfCancellationRequested();
                if (graph.FindTarget(package) is not { } target || HasUnfinishedAttempt(package)) continue;
                candidates.Add(package.FullName, target);
                var scope = Loc.T("Пакет Windows · пользователь рабочего стола");
                var note = package.Dependencies.Count == 0 ? "" :
                    Loc.T("Общие зависимости сохранятся, если они по-прежнему нужны другим приложениям.");
                items.Add(new(package.FullName, package.Name, scope, note)
                {
                    Version = package.Version,
                    NativeInstallerId = package.FullName,
                    RelatedPackageIds = Array.AsReadOnly(target.Associated.Select(entry => entry.FullName).ToArray()),
                });
            }
        }

        var plan = new Plan(Guid.NewGuid(), sid, observed, items.AsReadOnly(), unavailable, candidates)
        {
            Summary = unavailable is null
                ? Loc.T("Здесь показаны только приложения с подтверждённой возможностью удаления. Другими приложениями можно управлять в параметрах Windows.")
                : null,
        };
        _issued.Add(plan, new object());
        return (RemovalBatchPlan)plan;
    }, ct);

    public Task<RemovalBatchResult> ExecuteAsync(RemovalBatchPlan plan, IReadOnlySet<string> selectedKeys,
        IProgress<string>? progress, CancellationToken ct) =>
        Task.Run(() => ExecuteCoreAsync(plan, selectedKeys.ToHashSet(Identity), progress, ct));

    private async Task<RemovalBatchResult> ExecuteCoreAsync(RemovalBatchPlan plan, HashSet<string> selected,
        IProgress<string>? progress, CancellationToken ct)
    {
        var removed = 0;
        var errors = new List<string>();
        RemovalBatchResult Result(bool unknown = false, int pending = 0) =>
            new(removed, selected.Count - removed - (unknown ? 1 : pending), errors.AsReadOnly(), unknown) { Pending = pending };

        if (selected.Count == 0) return Result();
        if (plan is not Plan approved || !_issued.Remove(approved) || approved.UserSid is not { } sid ||
            !selected.IsSubsetOf(approved.Candidates.Keys))
        {
            errors.Add(Expired());
            return Result();
        }

        bool IsCurrent()
        {
            var age = clock.GetUtcNow().UtcDateTime - approved.ObservedUtc;
            return !ct.IsCancellationRequested && !SafetyLock.IsLocked && UserScope.Same(sid, desktopSid()) &&
                age >= TimeSpan.Zero && age <= MaximumPlanAge;
        }

        var locked = false;
        try
        {
            await ExecutionGate.WaitAsync(ct).ConfigureAwait(false);
            locked = true;
            foreach (var item in approved.Items.Where(item => selected.Contains(item.Key)))
            {
                if (!IsCurrent() || !supportsDeferredRemoval())
                {
                    if (!ct.IsCancellationRequested) errors.Add(Expired());
                    break;
                }

                var candidate = approved.Candidates[item.Key];
                var target = candidate.Main;
                progress?.Report(Loc.Tf("Проверка и удаление: {0}", target.Name));
                async Task<bool> StillEligibleAsync()
                {
                    var snapshot = await ReadCurrentAsync(sid, ct).ConfigureAwait(false);
                    return IsCurrent() && snapshot is not null && new RegistrationGraph(snapshot).Matches(candidate, selected);
                }

                if (HasUnfinishedAttempt(target) || !await StillEligibleAsync().ConfigureAwait(false))
                {
                    if (!ct.IsCancellationRequested) errors.Add(Changed(target.Name));
                    continue;
                }

                var attempt = new ProgramRemovalAudit(approved.Id, Guid.NewGuid(), AuditIdentity(target), ProgramRemovalScope.User,
                    clock.GetUtcNow().UtcDateTime);
                var pending = false;
                bool CanDispatch() => IsCurrent() && supportsDeferredRemoval() && !HasUnfinishedAttempt(target, attempt.OperationId);
                var outcome = await ChangeSession.RunProgramRemovalAsync(attempt, CanDispatch, async () =>
                {
                    bool eligible;
                    try { eligible = await StillEligibleAsync().ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { eligible = false; }
                    if (!eligible || !CanDispatch()) return new(ProgramRemovalAuditOutcome.NotStarted);
                    var exit = await remove(target.FullName, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) return new(ProgramRemovalAuditOutcome.Attempted);
                    var verified = await ReadCurrentAsync(sid, ct).ConfigureAwait(false);
                    if (verified is null) return new(ProgramRemovalAuditOutcome.Attempted);
                    var state = new RegistrationGraph(verified).FamilyState(candidate);
                    if (exit == 0 && state == RegistrationState.Absent)
                        return new(ProgramRemovalAuditOutcome.Removed, 0, FreshVerified: true);
                    if (exit != 0 && state == RegistrationState.Exact)
                        return new(ProgramRemovalAuditOutcome.Failed, exit, FreshVerified: true);
                    pending = exit == 0 && state == RegistrationState.Exact;
                    return new(ProgramRemovalAuditOutcome.Attempted);
                }, Journal).ConfigureAwait(false);

                switch (outcome)
                {
                    case ProgramRemovalAuditOutcome.Removed:
                        removed++;
                        break;
                    case ProgramRemovalAuditOutcome.NotStarted:
                        if (!ct.IsCancellationRequested) errors.Add(Changed(target.Name));
                        break;
                    case ProgramRemovalAuditOutcome.Failed:
                        errors.Add(Loc.Tf("{0}: Windows сообщила об ошибке удаления.", target.Name));
                        break;
                    case ProgramRemovalAuditOutcome.JournalUnavailable:
                        errors.Add(Loc.T("Не удалось записать попытку удаления в журнал. Удаление остановлено."));
                        return Result();
                    default:
                        return pending ? Result(pending: 1) : Result(unknown: true);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (locked) ExecutionGate.Release();
        }
        return Result();
    }

    private async Task<PackagedUninstallSnapshot?> ReadCurrentAsync(string sid, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!UserScope.Same(sid, desktopSid())) return null;
        var snapshot = await read(sid, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (snapshot?.Native.Complete != true || !UserScope.Same(sid, snapshot.UserSid) ||
            !UserScope.Same(sid, desktopSid()) || !SourcesAgree(snapshot)) return null;
        return snapshot;
    }

    private static bool SourcesAgree(PackagedUninstallSnapshot snapshot)
    {
        if (snapshot.Appx.Count != snapshot.Native.Registrations.Count) return false;
        var metadataByName = snapshot.Appx.ToLookup(entry => entry.PackageFullName, Identity);
        return snapshot.Native.Registrations.Select(entry => entry.FullName).Distinct(Identity).Count() == snapshot.Appx.Count &&
            snapshot.Native.Registrations.All(entry => metadataByName[entry.FullName].Take(2).ToArray() is [var metadata] &&
                Identity.Equals(entry.Family, metadata.PackageFamilyName) &&
                new HashSet<string>(entry.Dependencies, Identity).SetEquals(metadata.Dependencies));
    }

    private static string AuditPrefix(InstalledPackageRegistration package) => $"msix:{package.Family}:";
    private static string AuditIdentity(InstalledPackageRegistration package) => AuditPrefix(package) + package.FullName;

    // The current journal format has no per-user key. Stop retries for this family conservatively across accounts.
    private bool HasUnfinishedAttempt(InstalledPackageRegistration package, Guid? current = null) =>
        Journal.ProgramRemovals.Any(entry => entry.Outcome == ProgramRemovalAuditOutcome.Attempted &&
            entry.OperationId != current && entry.PackageId.StartsWith(AuditPrefix(package), StringComparison.OrdinalIgnoreCase));

    private static async Task<int> RemoveNativeAsync(string fullName, CancellationToken ct)
    {
        // Wait cancellation never cancels the non-cancelable Windows deployment operation.
        var operation = new PackageManager().RemovePackageAsync(fullName, DeferWhenInUse);
        var result = await operation.AsTask().WaitAsync(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        return result.ExtendedErrorCode?.HResult ?? 0;
    }

    private enum RegistrationState { Absent, Exact, Uncertain }

    private sealed class RegistrationGraph(PackagedUninstallSnapshot snapshot)
    {
        private readonly ILookup<string, InstalledPackageRegistration> _nativeFamily = snapshot.Native.Registrations.ToLookup(row => row.Family, Identity);
        private readonly ILookup<string, InstalledPackageRegistration> _nativeFull = snapshot.Native.Registrations.ToLookup(row => row.FullName, Identity);
        private readonly IReadOnlyDictionary<string, AppxRemovalRegistration> _appx = snapshot.Appx.ToDictionary(row => row.PackageFullName, Identity);
        private readonly ILookup<string, InstalledPackageRegistration> _consumers = snapshot.Native.Registrations
            .SelectMany(row => row.Dependencies.Distinct(Identity).Select(dependency => (dependency, row)))
            .ToLookup(edge => edge.dependency, edge => edge.row, Identity);

        internal RegistrationState FamilyState(Target target)
        {
            var present = _nativeFamily[target.Main.Family].ToArray();
            if (present.Length == target.Associated.Count + 1 &&
                present.Any(package => SameConfirmedRegistration(target.Main, package)) &&
                target.Associated.All(expected => present.Any(package => SameConfirmedRegistration(expected, package))))
                return RegistrationState.Exact;

            // Resource and bundle bookkeeping can outlive the principal registration, but a new member
            // of the family (including a replacement main) makes the outcome uncertain.
            if (!_nativeFull.Contains(target.Main.FullName) &&
                present.All(package => target.Associated.Any(expected => SameConfirmedRegistration(expected, package))) &&
                target.Main.Dependencies.All(dependency => _nativeFull.Contains(dependency) ||
                    target.Associated.Any(package => Identity.Equals(package.FullName, dependency))))
                return RegistrationState.Absent;
            return RegistrationState.Uncertain;
        }

        internal bool Matches(Target approved, IReadOnlySet<string> selected) =>
            FamilyState(approved) == RegistrationState.Exact && FindTarget(approved.Main, selected) is not null;

        internal Target? FindTarget(InstalledPackageRegistration target, IReadOnlySet<string>? selected = null)
        {
            if (target.Kind != PackagedRegistrationKind.Main || !_nativeFull.Contains(target.FullName)) return null;
            var main = _nativeFull[target.FullName].Single();
            if (main.Kind != PackagedRegistrationKind.Main || !main.HasAppListEntry || !IsRemovable(main) ||
                _consumers.Contains(main.FullName)) return null;

            var related = _nativeFamily[main.Family].Where(entry => !Identity.Equals(entry.FullName, main.FullName)).ToArray();
            if (related.Count(entry => entry.Kind == PackagedRegistrationKind.Bundle) > 1 ||
                related.Any(entry => !IsBookkeeping(main, entry))) return null;
            var relatedIds = related.Select(entry => entry.FullName).ToHashSet(Identity);

            // Windows can remove unused dependencies. Any dependency outside the confirmed family must
            // still have a registered consumer after all selected apps have been removed.
            if (main.Dependencies.Any(dependency => !relatedIds.Contains(dependency) &&
                (!_nativeFull.Contains(dependency) || _appx[dependency].NonRemovable is null ||
                    !_consumers[dependency].Any(consumer => !Identity.Equals(consumer.Family, main.Family) &&
                        selected?.Contains(consumer.FullName) != true && _appx[consumer.FullName].NonRemovable.HasValue))))
                return null;

            return new(main, Array.AsReadOnly(related.OrderBy(entry => entry.FullName, Identity).ToArray()));
        }

        // Windows App SDK removes the exact Main full name; Windows handles the associated bundle/resources.
        // Match same-version assets only, and require every resource to be an actual dependency of the main.
        private bool IsBookkeeping(InstalledPackageRegistration main, InstalledPackageRegistration entry) =>
            entry.Kind is PackagedRegistrationKind.Bundle or PackagedRegistrationKind.Resource &&
            entry.Version == main.Version && IsRemovable(entry) && entry.Dependencies.Count == 0 &&
            _consumers[entry.FullName].All(consumer => Identity.Equals(consumer.FullName, main.FullName)) &&
            (entry.Kind != PackagedRegistrationKind.Resource || main.Dependencies.Contains(entry.FullName, Identity));

        private bool IsRemovable(InstalledPackageRegistration package) =>
            !package.Protected && package.StoreSigned && _appx[package.FullName].NonRemovable == false;

        private static bool SameConfirmedRegistration(InstalledPackageRegistration expected, InstalledPackageRegistration current) =>
            Identity.Equals(expected.FullName, current.FullName) && expected.Kind == current.Kind &&
            expected.Version == current.Version && expected.Protected == current.Protected && expected.StoreSigned == current.StoreSigned &&
            expected.HasAppListEntry == current.HasAppListEntry &&
            new HashSet<string>(expected.Dependencies, Identity).SetEquals(current.Dependencies);
    }

    private static string Expired() => Loc.T("Выбор для удаления устарел. Подготовьте список заново.");
    private static string Changed(string name) => Loc.Tf("{0}: регистрация установки изменилась или больше не подтверждается.", name);
}
