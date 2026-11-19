using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Lycoris.Core;
using Lycoris.Modules.Apps;
using Lycoris.Modules.Games.Sources;

namespace Lycoris.Modules.Removal;

internal delegate Task<UnelevatedResult> ProgramRemovalCommandRunner(string sid, IReadOnlyList<string> args,
    Func<bool>? validateBeforeLaunch, CancellationToken ct);

internal sealed class ProgramRemovalBatchAction : IRemovalBatchAction
{
    private static readonly SemaphoreSlim ExecutionGate = new(1, 1);
    private static readonly TimeSpan MaximumPlanAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(45);
    private readonly ConditionalWeakTable<Plan, object> _issued = new();
    private readonly Func<UninstallInventory> _inventory;
    private readonly Func<string?> _desktopSid;
    private readonly Func<bool> _hasElevation;
    private readonly ProgramRemovalCommandRunner _run;
    private readonly TimeProvider _clock;
    private readonly ChangeJournal? _journal;

    private sealed record Candidate(UninstallRegistration Registration, ProgramRemovalPackage Package, ProgramRemovalScope Scope)
    {
        public string Key => Package.Id;
    }

    private sealed record Plan(Guid Id, string? UserSid, DateTime ObservedUtc, IReadOnlyList<RemovalBatchItem> Items,
        string? UnavailableReason, IReadOnlyList<Candidate> Candidates) : RemovalBatchPlan(Id, UserSid, ObservedUtc, Items, UnavailableReason);

    public ProgramRemovalBatchAction() : this(UninstallEntries.ReadInventory, DesktopSid, () => Elevation.IsElevated,
        UnelevatedWinget.RunForRemovalAsync, TimeProvider.System) { }

    internal ProgramRemovalBatchAction(Func<UninstallInventory> inventory, Func<string?> desktopSid, Func<bool> hasElevation,
        ProgramRemovalCommandRunner run, TimeProvider clock, ChangeJournal? journal = null)
    {
        _inventory = inventory;
        _desktopSid = desktopSid;
        _hasElevation = hasElevation;
        _run = run;
        _clock = clock;
        _journal = journal;
    }

    public RemovalBatchKind Kind => RemovalBatchKind.Uninstall;

    public Task<RemovalBatchPlan> PrepareAsync(CancellationToken ct) => Task.Run(() => PrepareCoreAsync(ct), ct);

    private async Task<RemovalBatchPlan> PrepareCoreAsync(CancellationToken ct)
    {
        var observed = _clock.GetUtcNow().UtcDateTime;
        var sid = _desktopSid();
        Plan Empty(string? reason = null) => new(Guid.NewGuid(), sid, observed, [], reason, []);
        if (string.IsNullOrWhiteSpace(sid)) return Empty(DesktopUnknown());

        var inventory = _inventory();
        if (inventory.State != UninstallReadState.Complete) return Empty(InventoryUnknown());
        var registrations = new List<(UninstallRegistration Entry, string Id)>();
        foreach (var entry in inventory.Entries)
            if (EligibleId(entry, inventory) is { } id) registrations.Add((entry, id));
        if (registrations.Count == 0) return Empty();

        var export = await ReadExportAsync(sid, ct).ConfigureAwait(false);
        if (export is null) return Empty(SourceUnknown());
        var candidates = new List<Candidate>();
        foreach (var (entry, id) in registrations)
        {
            ct.ThrowIfCancellationRequested();
            var scope = ScopeOf(entry);
            if (export.Match(id, entry.Version!, scope) is not { } proof) continue;
            var candidate = new Candidate(entry, proof, scope);
            if (await ScopeIsUniqueAsync(sid, candidate, ct).ConfigureAwait(false)) candidates.Add(candidate);
        }

        ct.ThrowIfCancellationRequested();
        if (!UserScope.Same(sid, _desktopSid())) return Empty(DesktopUnknown());
        var fresh = _inventory();
        if (fresh.State != UninstallReadState.Complete) return Empty(InventoryUnknown());
        candidates.RemoveAll(candidate => !IsStillUnique(candidate, fresh));
        var rows = candidates.Select(candidate =>
        {
            var row = RemovalPolicy.Program(candidate.Registration, current: true);
            return new RemovalBatchItem(candidate.Key, row.Name, row.Source, row.Reason);
        }).ToArray();
        var plan = new Plan(Guid.NewGuid(), sid, observed, Array.AsReadOnly(rows), null, candidates.AsReadOnly());
        _issued.Add(plan, new object());
        return plan;
    }

    public Task<RemovalBatchResult> ExecuteAsync(RemovalBatchPlan plan, IReadOnlySet<string> selectedKeys,
        IProgress<string>? progress, CancellationToken ct)
    {
        var selected = selectedKeys.ToFrozenSet(StringComparer.Ordinal);
        return Task.Run(() => ExecuteCoreAsync(plan, selected, progress, ct));
    }

    private async Task<RemovalBatchResult> ExecuteCoreAsync(RemovalBatchPlan plan, IReadOnlySet<string> selected,
        IProgress<string>? progress, CancellationToken ct)
    {
        var applied = 0;
        var errors = new List<string>();
        RemovalBatchResult Result(bool unknown = false) =>
            new(applied, selected.Count - applied - (unknown ? 1 : 0), errors.AsReadOnly(), unknown);
        if (selected.Count == 0) return Result();
        if (plan is not Plan approved || !_issued.Remove(approved) || !selected.IsSubsetOf(approved.Candidates.Select(item => item.Key)))
        {
            errors.Add(Expired());
            return Result();
        }

        var hasGate = false;
        try
        {
            await ExecutionGate.WaitAsync(ct).ConfigureAwait(false);
            hasGate = true;
            foreach (var candidate in approved.Candidates.Where(item => selected.Contains(item.Key)))
            {
                ct.ThrowIfCancellationRequested();
                if (!CanExecute(approved))
                {
                    errors.Add(Expired());
                    break;
                }

                var name = candidate.Registration.DisplayName;
                progress?.Report(Loc.Tf("Проверка и удаление: {0}", name));
                var export = await ReadExportAsync(approved.UserSid!, ct).ConfigureAwait(false);
                if (!CanExecute(approved) || export?.Match(candidate.Key, candidate.Package.Version, candidate.Scope) != candidate.Package
                    || !await ScopeIsUniqueAsync(approved.UserSid!, candidate, ct).ConfigureAwait(false))
                {
                    errors.Add(Changed(name));
                    continue;
                }

                bool StillApproved() => !ct.IsCancellationRequested && CanExecute(approved) && IsStillUnique(candidate, _inventory())
                    && !ct.IsCancellationRequested && CanExecute(approved);

                var attempt = new ProgramRemovalAudit(approved.Id, Guid.NewGuid(), candidate.Key, candidate.Scope,
                    _clock.GetUtcNow().UtcDateTime);
                var outcome = await ChangeSession.RunProgramRemovalAsync(attempt, StillApproved,
                    () => DispatchAndVerifyAsync(approved.UserSid!, candidate, StillApproved, ct), _journal).ConfigureAwait(false);
                switch (outcome)
                {
                    case ProgramRemovalAuditOutcome.Removed:
                        applied++;
                        break;
                    case ProgramRemovalAuditOutcome.NotStarted:
                        if (!ct.IsCancellationRequested) errors.Add(Changed(name));
                        break;
                    case ProgramRemovalAuditOutcome.Failed:
                        errors.Add(Loc.Tf("{0}: WinGet сообщил об ошибке удаления.", name));
                        break;
                    case ProgramRemovalAuditOutcome.JournalUnavailable:
                        errors.Add(Loc.T("Не удалось записать попытку удаления в журнал. Удаление остановлено."));
                        return Result();
                    default:
                        errors.Add(Loc.Tf("{0}: результат удаления не подтверждён.", name));
                        return Result(unknown: true);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (hasGate) ExecutionGate.Release();
        }
        return Result();
    }

    private async Task<ProgramRemovalAuditResult> DispatchAndVerifyAsync(string sid, Candidate candidate,
        Func<bool> stillApproved, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return new(ProgramRemovalAuditOutcome.NotStarted);
        var args = PackageArgs("uninstall", candidate.Key, candidate.Scope).Concat(
            new[] { "--version", candidate.Package.Version, "--product-code", candidate.Registration.KeyName, "--silent" }).ToArray();
        UnelevatedResult run;
        try { run = await _run(sid, args, stillApproved, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(ProgramRemovalAuditOutcome.Attempted);
        }
        if (run.Outcome == UnelevatedOutcome.LaunchFailed && !run.StillRunning) return new(ProgramRemovalAuditOutcome.NotStarted);
        if (run.Outcome != UnelevatedOutcome.Completed || run.StillRunning || ct.IsCancellationRequested)
            return new(ProgramRemovalAuditOutcome.Attempted);

        if (!await SourceIsOfficialAsync(sid, ct).ConfigureAwait(false)) return new(ProgramRemovalAuditOutcome.Attempted);
        var listed = await ProbeScopeAsync(sid, candidate.Key, candidate.Scope, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || !UserScope.Same(sid, _desktopSid())) return new(ProgramRemovalAuditOutcome.Attempted);
        var fresh = _inventory();
        if (run.ExitCode == 0 && listed == false && IsAbsent(candidate, fresh))
            return new(ProgramRemovalAuditOutcome.Removed, 0, FreshVerified: true);
        if (run.ExitCode != 0 && listed == true && IsStillUnique(candidate, fresh))
            return new(ProgramRemovalAuditOutcome.Failed, run.ExitCode, FreshVerified: true);
        return new(ProgramRemovalAuditOutcome.Attempted);
    }

    private bool CanExecute(Plan plan)
    {
        var age = _clock.GetUtcNow().UtcDateTime - plan.ObservedUtc;
        return age >= TimeSpan.Zero && age <= MaximumPlanAge && !SafetyLock.IsLocked && _hasElevation() &&
            UserScope.Same(plan.UserSid, _desktopSid());
    }

    private async Task<ProgramRemovalExport?> ReadExportAsync(string sid, CancellationToken ct)
    {
        if (!await SourceIsOfficialAsync(sid, ct).ConfigureAwait(false)) return null;
        var run = await ProbeAsync(sid, ["export", "--source", "winget", "--include-versions", "--disable-interactivity"], ct)
            .ConfigureAwait(false);
        return WasObserved(run, 0) ? ProgramRemovalExport.Parse(run.Output) : null;
    }

    private async Task<bool> SourceIsOfficialAsync(string sid, CancellationToken ct)
    {
        var run = await ProbeAsync(sid, ["source", "export", "--name", "winget", "--disable-interactivity"], ct).ConfigureAwait(false);
        return WasObserved(run, 0) && ProgramRemovalExport.IsOfficialSource(run.Output);
    }

    private async Task<bool> ScopeIsUniqueAsync(string sid, Candidate candidate, CancellationToken ct) =>
        await ProbeScopeAsync(sid, candidate.Key, candidate.Scope, ct).ConfigureAwait(false) == true
        && await ProbeScopeAsync(sid, candidate.Key, candidate.Scope == ProgramRemovalScope.Machine
            ? ProgramRemovalScope.User : ProgramRemovalScope.Machine, ct).ConfigureAwait(false) == false;

    private async Task<bool?> ProbeScopeAsync(string sid, string id, ProgramRemovalScope scope, CancellationToken ct)
    {
        // WinGet EnsureMatchesFromSearchResult uses this HRESULT for zero installed matches, regardless of locale.
        var run = await ProbeAsync(sid, PackageArgs("list", id, scope), ct).ConfigureAwait(false);
        return WasObserved(run, 0) ? true : WasObserved(run, WingetClient.NoApplicationsFound) ? false : null;
    }

    private async Task<UnelevatedResult> ProbeAsync(string sid, IReadOnlyList<string> args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try { return await _run(sid, args, null, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(UnelevatedOutcome.Untracked, -1, "");
        }
    }

    private static bool WasObserved(UnelevatedResult result, int code) =>
        result.Outcome == UnelevatedOutcome.Completed && !result.StillRunning && result.ExitCode == code;

    private static string[] PackageArgs(string command, string id, ProgramRemovalScope scope) =>
        [command, "--id", id, "--exact", "--source", "winget", "--scope",
            scope == ProgramRemovalScope.Machine ? "machine" : "user", "--disable-interactivity"];

    private static string? DesktopSid() => Elevation.InteractiveUserState == true && UserScope.CurrentSid is { } sid
        && UnelevatedWinget.IsUnelevatedDesktopUser(sid) ? sid : null;

    private static ProgramRemovalScope ScopeOf(UninstallRegistration entry) =>
        entry.Scope == UninstallScope.DesktopUser ? ProgramRemovalScope.User : ProgramRemovalScope.Machine;

    private static string? EligibleId(UninstallRegistration entry, UninstallInventory inventory)
    {
        var id = Family(entry.DisplayName);
        if (id is null || entry.NoRemove != false || entry.SystemComponent != false || entry.ParentOrUpdate || entry.UninstallFingerprint is null ||
            !UnelevatedWinget.IsSafeProductCode(entry.KeyName) ||
            entry.Version is not { } version || !UnelevatedWinget.IsSafeToken(version) || !char.IsAsciiDigit(version[0]) ||
            RemovalPolicy.Program(entry, current: true).Decision != RemovalDecision.ConsiderRemoval) return null;
        return inventory.Entries.Count(other => SameTargetFamily(entry, id, other)) == 1 ? id : null;
    }

    private static bool IsStillUnique(Candidate candidate, UninstallInventory inventory) =>
        inventory.State == UninstallReadState.Complete && inventory.Entries.Contains(candidate.Registration) &&
        EligibleId(candidate.Registration, inventory) == candidate.Key;

    private static bool IsAbsent(Candidate candidate, UninstallInventory inventory) =>
        inventory.State == UninstallReadState.Complete &&
        !inventory.Entries.Any(other => SameTargetFamily(candidate.Registration, candidate.Key, other));

    private static bool SameTargetFamily(UninstallRegistration original, string id, UninstallRegistration other) =>
        Family(other.DisplayName) == id || ScopeOf(original) == ScopeOf(other) &&
        string.Equals(original.KeyName, other.KeyName, StringComparison.OrdinalIgnoreCase);

    private static string? Family(string displayName)
    {
        if (HasFamilyName(displayName, "VLC media player")) return "VideoLAN.VLC";
        if (HasFamilyName(displayName, "OBS Studio")) return "OBSProject.OBSStudio";
        return HasFamilyName(displayName, "7-Zip") ? "7zip.7zip" : null;
    }

    private static bool HasFamilyName(string displayName, string name) =>
        string.Equals(displayName, name, StringComparison.OrdinalIgnoreCase) ||
        displayName.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase);

    private static string DesktopUnknown() => Loc.T("Не удалось подтвердить пользователя рабочего стола для WinGet.");
    private static string InventoryUnknown() => Loc.T("Не удалось подтвердить полный список установленных программ.");
    private static string SourceUnknown() => Loc.T("Не удалось подтвердить официальный источник winget и список установленных пакетов.");
    private static string Expired() => Loc.T("Выбор для удаления устарел. Подготовьте список заново.");
    private static string Changed(string name) => Loc.Tf("{0}: регистрация установки изменилась или больше не подтверждается.", name);
}
