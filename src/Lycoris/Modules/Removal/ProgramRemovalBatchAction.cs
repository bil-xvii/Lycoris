using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lycoris.Core;
using Lycoris.Modules.Apps;
using Lycoris.Modules.Games.Sources;

namespace Lycoris.Modules.Removal;

internal delegate Task<UnelevatedResult> ProgramRemovalCommandRunner(string sid, IReadOnlyList<string> args,
    Func<bool>? validateBeforeLaunch, CancellationToken ct);

internal sealed record NativeArpIdentity(string Id, string ProductCode, string Version, ProgramRemovalScope Scope)
{
    internal static NativeArpIdentity? TryCreate(UninstallRegistration registration, Architecture osArchitecture)
    {
        if (osArchitecture != Architecture.X64 || !UnelevatedWinget.IsSafeProductCode(registration.KeyName) ||
            registration.Version is not { } version || !UnelevatedWinget.IsSafeRemovalVersion(version)) return null;

        // WinGet ARPHelper builds this exact local ID from the registry view and original subkey name.
        var prefix = registration.Scope switch
        {
            UninstallScope.Machine64 => @"ARP\Machine\X64\",
            UninstallScope.Machine32 => @"ARP\Machine\X86\",
            UninstallScope.DesktopUser => @"ARP\User\X64\",
            _ => null,
        };
        return prefix is null ? null : new(prefix + registration.KeyName, registration.KeyName, version,
            registration.Scope == UninstallScope.DesktopUser ? ProgramRemovalScope.User : ProgramRemovalScope.Machine);
    }
}

internal sealed class ProgramRemovalBatchAction : IRemovalBatchAction
{
    private static readonly SemaphoreSlim ExecutionGate = new(1, 1);
    private static readonly TimeSpan MaximumPlanAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(45);
    // First stable WinGet release with the local ARP scope/view ID and installed version selector used here.
    private static readonly Version MinimumRemovalClientVersion = new(1, 8, 1791);
    private readonly ConditionalWeakTable<Plan, object> _issued = new();
    private readonly Func<UninstallInventory> _inventory;
    private readonly Func<string?> _desktopSid;
    private readonly Func<bool> _hasElevation;
    private readonly IProgramRemovalInspector _inspector;
    private readonly Func<string?, CancellationToken, Task<PackagedRemovalSnapshot>> _readPackaged;
    private readonly ProgramRemovalCommandRunner _run;
    private readonly TimeProvider _clock;
    private readonly ChangeJournal? _journal;

    private sealed record Candidate(UninstallRegistration Registration, NativeArpIdentity Identity, ProgramRemovalProof Proof)
    {
        public string Key => Identity.Id;
    }

    private sealed record Plan(Guid Id, string? UserSid, DateTime ObservedUtc, IReadOnlyList<RemovalBatchItem> Items,
        string? UnavailableReason, IReadOnlyList<Candidate> Candidates) : RemovalBatchPlan(Id, UserSid, ObservedUtc, Items, UnavailableReason);

    public ProgramRemovalBatchAction() : this(UninstallEntries.ReadInventory, DesktopSid, () => Elevation.IsElevated, new ProgramRemovalInspector(),
        UnelevatedWinget.RunForRemovalAsync, TimeProvider.System) { }

    internal ProgramRemovalBatchAction(Func<UninstallInventory> inventory, Func<string?> desktopSid, Func<bool> hasElevation,
        IProgramRemovalInspector inspector, ProgramRemovalCommandRunner run, TimeProvider clock, ChangeJournal? journal = null,
        Func<string?, CancellationToken, Task<PackagedRemovalSnapshot>>? readPackaged = null)
    {
        _inventory = inventory;
        _desktopSid = desktopSid;
        _hasElevation = hasElevation;
        _inspector = inspector;
        _readPackaged = readPackaged ?? PackagedRemovalInventory.ReadAsync;
        _run = run;
        _clock = clock;
        _journal = journal;
    }

    public RemovalBatchKind Kind => RemovalBatchKind.Uninstall;

    public Task<RemovalBatchPlan> PrepareAsync(CancellationToken ct) => Task.Run(() => PrepareCoreAsync(ct), ct);

    private async Task<RemovalBatchPlan> PrepareCoreAsync(CancellationToken ct)
    {
        var sid = _desktopSid();
        var inventory = _inventory();
        var desktopPackages = _readPackaged(sid, ct);
        var inspection = _inspector.InspectAsync(inventory, ct);
        await Task.WhenAll(inspection, desktopPackages).ConfigureAwait(false);
        var assessments = await inspection.ConfigureAwait(false);
        var packaged = await desktopPackages.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var registrations = IndexUniqueRegistrations(inventory);
        var evaluated = assessments.Select(a => (Assessment: a, Candidate: FromAssessment(a, registrations))).ToArray();
        var unavailable = inventory.State != UninstallReadState.Complete ? InventoryUnknown()
            : string.IsNullOrWhiteSpace(sid) || !UserScope.Same(sid, _desktopSid()) ? DesktopUnknown() : null;
        if (unavailable is null && evaluated.Any(entry => entry.Candidate is not null))
        {
            unavailable = await ClientProblemAsync(sid!, ct).ConfigureAwait(false);
            if (unavailable is null && !await SourceIsOfficialAsync(sid!, ct).ConfigureAwait(false)) unavailable = SourceUnknown();
            if (unavailable is null)
            {
                if (!UserScope.Same(sid, _desktopSid())) unavailable = DesktopUnknown();
                else
                {
                    var fresh = _inventory();
                    if (fresh.State != UninstallReadState.Complete) unavailable = InventoryUnknown();
                    else registrations = IndexUniqueRegistrations(fresh);
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        var sameDesktop = !string.IsNullOrWhiteSpace(sid) && UserScope.Same(sid, _desktopSid());
        if (!sameDesktop) unavailable ??= DesktopUnknown();

        var candidates = new List<Candidate>();
        var items = new List<RemovalBatchItem>();
        var rows = new List<RemovalRow>(evaluated.Length);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (assessment, candidate) in evaluated)
        {
            var row = assessment.Row;
            if (row.Decision == RemovalDecision.ConsiderRemoval)
            {
                if (unavailable is null && candidate is not null && IsIndexed(candidate.Registration, registrations)
                    && keys.Add(candidate.Key))
                {
                    candidates.Add(candidate);
                    items.Add(new RemovalBatchItem(candidate.Key, row.Name, row.Source, row.Reason)
                    {
                        Version = candidate.Identity.Version,
                        NativeInstallerId = candidate.Identity.Id,
                    });
                }
                else row = row with
                {
                    Decision = RemovalDecision.Review,
                    Reason = unavailable ?? (assessment.Proof is null ? ProofUnknown() : IdentityUnknown()),
                };
            }
            rows.Add(row);
        }

        if (sameDesktop) rows.AddRange(packaged.Rows);
        var keep = rows.Count(row => row.Decision == RemovalDecision.Keep);
        var summary = Loc.Tf("Проверено программ: {0}. Для удаления: {1}. Нужна проверка: {2}. Оставить: {3}.",
            rows.Count, items.Count, rows.Count - items.Count - keep, keep);
        if (!sameDesktop || !packaged.Complete)
            summary += " " + Loc.T("Не удалось проверить пакеты Microsoft Store для пользователя рабочего стола.");
        var plan = new Plan(Guid.NewGuid(), sid, _clock.GetUtcNow().UtcDateTime, items.AsReadOnly(), unavailable, candidates.AsReadOnly())
        {
            AssessedRows = rows.AsReadOnly(),
            Summary = summary,
        };
        if (candidates.Count != 0) _issued.Add(plan, new object());
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
            var age = _clock.GetUtcNow().UtcDateTime - approved.ObservedUtc;
            if (age < TimeSpan.Zero || age > MaximumPlanAge || !CanExecute(approved))
            {
                errors.Add(Expired());
                return Result();
            }
            if (await ClientProblemAsync(approved.UserSid!, ct).ConfigureAwait(false) is { } clientProblem)
            {
                if (!ct.IsCancellationRequested) errors.Add(clientProblem);
                return Result();
            }
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

                bool StillRegistered() => !ct.IsCancellationRequested && CanExecute(approved) &&
                    IsStillUnique(candidate.Registration, _inventory()) && !ct.IsCancellationRequested && CanExecute(approved);

                if (!StillRegistered())
                {
                    errors.Add(Changed(name));
                    continue;
                }
                if (!await SourceIsOfficialAsync(approved.UserSid!, ct).ConfigureAwait(false))
                {
                    if (!ct.IsCancellationRequested) errors.Add(SourceUnknown());
                    break;
                }
                if (!CanExecute(approved) || await ProbeLocalAsync(approved.UserSid!, candidate.Identity, ct).ConfigureAwait(false) != true)
                {
                    if (!ct.IsCancellationRequested) errors.Add(Loc.Tf("{0}: не удалось подтвердить локальную запись в WinGet.", name));
                    continue;
                }

                bool StillApproved() => !ct.IsCancellationRequested && CanExecute(approved) &&
                    _inspector.Revalidate(candidate.Registration, candidate.Proof, ct) && StillRegistered();

                var attempt = new ProgramRemovalAudit(approved.Id, Guid.NewGuid(), candidate.Key, candidate.Identity.Scope,
                    _clock.GetUtcNow().UtcDateTime);
                var outcome = await ChangeSession.RunProgramRemovalAsync(attempt, StillRegistered,
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
        var target = candidate.Identity;
        var args = PackageArgs("uninstall", target).Concat(
            new[] { "--version", target.Version, "--product-code", target.ProductCode, "--interactive" }).ToArray();
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
        var listed = await ProbeLocalAsync(sid, target, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || !UserScope.Same(sid, _desktopSid())) return new(ProgramRemovalAuditOutcome.Attempted);
        var fresh = _inventory();
        if (run.ExitCode == 0 && listed == false && IsAbsent(candidate.Registration, fresh))
            return new(ProgramRemovalAuditOutcome.Removed, 0, FreshVerified: true);
        if (run.ExitCode != 0 && listed == true && IsStillUnique(candidate.Registration, fresh))
            return new(ProgramRemovalAuditOutcome.Failed, run.ExitCode, FreshVerified: true);
        return new(ProgramRemovalAuditOutcome.Attempted);
    }

    private bool CanExecute(Plan plan) => !SafetyLock.IsLocked && _hasElevation() && UserScope.Same(plan.UserSid, _desktopSid());

    private async Task<string?> ClientProblemAsync(string sid, CancellationToken ct)
    {
        var run = await ProbeAsync(sid, ["--version"], ct).ConfigureAwait(false);
        if (!WasObserved(run, 0) || ParseClientVersion(run.Output) is not { } version)
            return Loc.T("Не удалось подтвердить версию WinGet для пакетного удаления.");
        return version < MinimumRemovalClientVersion
            ? Loc.Tf("Для пакетного удаления требуется WinGet версии {0} или новее.", MinimumRemovalClientVersion)
            : null;
    }

    internal static Version? ParseClientVersion(string output)
    {
        var text = output.Trim();
        if (text.Length is < 5 or > 43) return null;
        if (text[0] is 'v' or 'V') text = text[1..];
        return text.All(c => char.IsAsciiDigit(c) || c == '.') && Version.TryParse(text, out var version) && version.Build >= 0
            ? version : null;
    }

    private async Task<bool> SourceIsOfficialAsync(string sid, CancellationToken ct)
    {
        var run = await ProbeAsync(sid, ["source", "export", "--name", "winget", "--disable-interactivity"], ct).ConfigureAwait(false);
        return WasObserved(run, 0) && ProgramRemovalExport.IsOfficialSource(run.Output);
    }

    private async Task<bool?> ProbeLocalAsync(string sid, NativeArpIdentity target, CancellationToken ct)
    {
        // WinGet EnsureMatchesFromSearchResult uses this HRESULT for zero installed matches, regardless of locale.
        var run = await ProbeAsync(sid, PackageArgs("list", target), ct).ConfigureAwait(false);
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

    private static string[] PackageArgs(string command, NativeArpIdentity target) =>
        [command, "--id", target.Id, "--exact", "--source", "winget", "--scope",
            target.Scope == ProgramRemovalScope.Machine ? "machine" : "user", "--disable-interactivity"];

    private static string? DesktopSid() => Elevation.InteractiveUserState == true && UserScope.CurrentSid is { } sid
        && UnelevatedWinget.IsUnelevatedDesktopUser(sid) ? sid : null;

    private static Candidate? FromAssessment(ProgramRemovalAssessment assessment,
        IReadOnlyDictionary<string, UninstallRegistration?> unique)
    {
        var entry = assessment.Registration;
        if (assessment.Row.Decision != RemovalDecision.ConsiderRemoval || assessment.Proof is not { } proof ||
            entry.NoRemove != false || entry.SystemComponent != false || entry.ParentOrUpdate ||
            !entry.HasUninstallRegistration || string.IsNullOrWhiteSpace(entry.UninstallFingerprint) ||
            !IsIndexed(entry, unique) || NativeArpIdentity.TryCreate(entry, RuntimeInformation.OSArchitecture) is not { } target) return null;
        return new(entry, target, proof);
    }

    private static Dictionary<string, UninstallRegistration?> IndexUniqueRegistrations(UninstallInventory inventory)
    {
        // MSI ultimately receives the product code; identical subkeys across views or accounts are ambiguous.
        var index = new Dictionary<string, UninstallRegistration?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inventory.Entries)
            if (!index.TryAdd(entry.KeyName, entry)) index[entry.KeyName] = null;
        return index;
    }

    private static bool IsIndexed(UninstallRegistration entry, IReadOnlyDictionary<string, UninstallRegistration?> unique) =>
        unique.TryGetValue(entry.KeyName, out var observed) && observed == entry;

    private static bool IsStillUnique(UninstallRegistration entry, UninstallInventory inventory) =>
        inventory.State == UninstallReadState.Complete &&
        inventory.Entries.Where(other => SameProduct(entry, other)).Take(2).ToArray() is [var observed] && observed == entry;

    private static bool IsAbsent(UninstallRegistration entry, UninstallInventory inventory) =>
        inventory.State == UninstallReadState.Complete && !inventory.Entries.Any(other => SameProduct(entry, other));

    private static bool SameProduct(UninstallRegistration a, UninstallRegistration b) =>
        string.Equals(a.KeyName, b.KeyName, StringComparison.OrdinalIgnoreCase);

    private static string DesktopUnknown() => Loc.T("Не удалось подтвердить пользователя рабочего стола для WinGet.");
    private static string InventoryUnknown() => Loc.T("Не удалось подтвердить полный список установленных программ.");
    private static string SourceUnknown() => Loc.T("Не удалось подтвердить официальный источник и доступность WinGet.");
    private static string IdentityUnknown() => Loc.T("Не подтверждён точный локальный идентификатор установки.");
    private static string ProofUnknown() => Loc.T("Не удалось подтвердить данные программы для пакетного удаления.");
    private static string Expired() => Loc.T("Выбор для удаления устарел. Подготовьте список заново.");
    private static string Changed(string name) => Loc.Tf("{0}: регистрация установки изменилась или больше не подтверждается.", name);
}
