using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Monolith.Core.Devices;
using Monolith.Modules.Services;
using Monolith.Tweaks;

namespace Monolith.Core;

/// <summary>Append only: the journal stores the names, and older builds cannot read names they do not know.</summary>
public enum ChangeKind
{
    Registry, Service, PowerScheme, ScheduledTask, Command,
    /// <summary>A per-user Windows input parameter (SystemParametersInfo + its HKCU value) — «Устройства и сеть».</summary>
    UserParameter,
    /// <summary>Resolution and refresh rate of one monitor (confirmed on screen).</summary>
    DisplayMode,
    /// <summary>HDR of one monitor (confirmed on screen).</summary>
    DisplayHdr,
    /// <summary>Monitor brightness or contrast over DDC/CI.</summary>
    MonitorSetting,
    /// <summary>Level or mute of a capture endpoint (microphone).</summary>
    AudioLevel,
    /// <summary>An NDIS advanced property of a network adapter (speed/duplex, EEE).</summary>
    NicProperty,
}

/// <summary>
/// One reversible change Lycoris made to the system, with everything needed to undo it.
/// Changes of the same target are LAYERS: every record holds the state right before its own change. Reverting the
/// newest layer restores that state; reverting an older layer while a newer one is active leaves the system alone and
/// hands its original over to the next newer layer (<see cref="ChangeJournal.RevertAsync"/>). So reverting one change
/// undoes exactly that change, and reverting all of them, newest first, ends at the state from before Lycoris.
/// </summary>
public sealed class ChangeRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid BatchId { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Who made the change: "tweak:game-mode", "services", "startup", "gameboost"...</summary>
    public string Source { get; init; } = "";
    /// <summary>Human readable, Russian: "Игровой режим Windows".</summary>
    public string Title { get; init; } = "";
    public ChangeKind Kind { get; init; }
    /// <summary>Unique key of the changed thing: "reg:HKCU\...\Name", "svc:DiagTrack", "power:active", "task:\Microsoft\...".</summary>
    public string Target { get; init; } = "";
    /// <summary>The state before this change (after a hand-over: the state before the reverted older layer).</summary>
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    /// <summary>
    /// SID of the Windows account whose profile was changed (HKCU values, per-user commands). Null for machine-wide
    /// changes, and for per-user records written by builds that did not record the account.
    /// </summary>
    public string? UserSid { get; init; }

    public RegistryValueSnapshot? Registry { get; set; }
    public ServiceStartSnapshot? Service { get; set; }
    public Guid? PowerSchemeOriginal { get; set; }
    /// <summary>A scheme Lycoris created (Ultimate Performance copy) and deletes on revert.</summary>
    public Guid? PowerSchemeCreated { get; set; }
    /// <summary>More created schemes this record deletes on revert (handed over from a reverted older layer).</summary>
    public List<Guid>? PowerSchemesCreatedMore { get; set; }
    /// <summary>
    /// Records from older builds only: other sources merged into this record (the record holds the original from before
    /// the first of them, so reverting it also undoes theirs). New changes of a journaled target get their own layer.
    /// </summary>
    public List<string> AlsoSources { get; set; } = [];

    public bool TouchedBy(string source) =>
        Source == source || AlsoSources.Contains(source);
    public bool? TaskWasEnabled { get; set; }
    public string? TaskPath { get; init; }
    public string? RevertFile { get; set; }
    public string? RevertArgs { get; set; }

    // Device changes (source "devices"): the original state of one setting of one device.
    public SpiSnapshot? Spi { get; set; }
    public DisplayModeSnapshot? DisplayMode { get; set; }
    public DisplayHdrSnapshot? DisplayHdr { get; set; }
    public MonitorVcpSnapshot? Monitor { get; set; }
    public AudioLevelSnapshot? Audio { get; set; }
    public NicPropertySnapshot? Nic { get; set; }

    /// <summary>A display or input change applied temporarily and not confirmed on screen yet (see DisplaySafeApply).</summary>
    [JsonIgnore] public bool IsPendingConfirm => DisplayMode?.PendingConfirm == true || DisplayHdr?.PendingConfirm == true || Spi?.PendingConfirm == true;

    public bool Reverted { get; set; }
    public DateTime? RevertedUtc { get; set; }
    public string? RevertError { get; set; }
    /// <summary>Reverted while a newer layer was active: the system was not touched, this record's original went to that layer.</summary>
    public Guid? HandedTo { get; set; }
    /// <summary>How the revert went when it was not a plain restore (target gone, fallback used…). Russian, shown on the row.</summary>
    public string? RevertNote { get; set; }
    /// <summary>When the target was read back after the revert. Null: not checked (or nothing to read back).</summary>
    public DateTime? RevertVerifiedUtc { get; set; }
    /// <summary>Set when the read-back differs from the original: «сейчас X, ожидалось Y».</summary>
    public string? RevertMismatch { get; set; }

    [JsonIgnore] public DateTime Timestamp => TimestampUtc.ToLocalTime();
    [JsonIgnore] public bool IsActive => !Reverted;
    /// <summary>The change went into one account's profile (HKCU, per-user DirectX/visual-effect settings).</summary>
    [JsonIgnore] public bool IsPerUser => UserScope.IsPerUser(Kind, Target, Registry);
    /// <summary>
    /// A per-user change from a build that did not record the account: never layered with an account's records, and
    /// bulk reverts leave it to a per-row revert.
    /// </summary>
    [JsonIgnore] public bool IsLegacyPerUser => IsPerUser && UserSid is null;
    /// <summary>Older builds merged later changes of the same target into one record (see <see cref="AlsoSources"/>).</summary>
    [JsonIgnore] public bool IsLegacyMerged => AlsoSources.Count > 0;
}

public sealed class ChangeBatch
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string Title { get; init; } = "";
    public string? RestorePoint { get; set; }
    [JsonIgnore] public DateTime Timestamp => TimestampUtc.ToLocalTime();
}

/// <summary>
/// Outcome of a revert. <see cref="Reverted"/> counts every record that is no longer active, including the ones handed
/// over to a newer layer (<see cref="HandedOver"/>). <see cref="JournalSaved"/> is false when the journal could not be
/// written afterwards: the system is reverted, but after a restart the records could show as active again.
/// </summary>
public sealed record RevertResult(int Reverted, int Failed, IReadOnlyList<string> Errors, bool JournalSaved = true)
{
    public bool Success => Failed == 0;
    public int HandedOver { get; init; }
    /// <summary>Records whose original was actually put back into the system (hand-overs change nothing).</summary>
    public int Restored => Reverted - HandedOver;
    /// <summary>
    /// Active records the revert left on purpose: another account's, or a per-user record from an older build that
    /// names no account (see <see cref="ChangeJournal.IsBulkRevertible"/>). Not failures, but not reverted either.
    /// </summary>
    public IReadOnlyList<ChangeRecord> Left { get; init; } = [];
    /// <summary>Records whose target was read back and equals the original.</summary>
    public int Confirmed { get; init; }
    /// <summary>«Title: сейчас X, ожидалось Y» for every record whose read-back differs from the original.</summary>
    public IReadOnlyList<string> Mismatches { get; init; } = [];
    /// <summary>Reverts that were not a plain restore (target gone, fallback used).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>One line for a toast, or null when nothing was read back.</summary>
    public string? VerificationText => (Confirmed, Mismatches.Count) switch
    {
        (0, 0) => null,
        (_, 0) => Loc.Tf("Откат подтверждён: {0}.", Confirmed),
        (0, var m) => Loc.Tf("Не совпадает с исходным: {0}.", m),
        var (c, m) => Loc.Tf("Откат подтверждён: {0}, не совпадает с исходным: {1}.", c, m),
    };
}

/// <summary>A reverted target read back: <see cref="Matches"/> null = cannot be checked (a command without a reader, a vanished service…).</summary>
public sealed record RevertCheck(bool? Matches, string? Live = null, string? Expected = null)
{
    public static readonly RevertCheck NotChecked = new(Matches: null);
    public string? MismatchText => Matches == false ? Loc.Tf("сейчас {0}, ожидалось {1}", Live ?? "—", Expected ?? "—") : null;
}

/// <summary>The Windows account per-user changes belong to. The journal is machine-wide, HKCU is not.</summary>
public static class UserScope
{
    private static readonly Lazy<string?> ProcessSid = new(() =>
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; }
        catch { return null; }
    });

    /// <summary>TESTS ONLY: replaces the account of this process.</summary>
    internal static Func<string?>? CurrentSidForTests { get; set; }

    /// <summary>SID of the account Lycoris runs as — whose HKCU it writes. Null when it cannot be read.</summary>
    public static string? CurrentSid => CurrentSidForTests is { } f ? f() : ProcessSid.Value;

    internal static readonly Regex SidPattern = new(@"^S-1-[0-9]{1,3}(-[0-9]{1,10}){1,14}$", RegexOptions.CultureInvariant);

    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Command targets (without "cmd:") that change the current account's settings: DirectX tokens, visual effects.</summary>
    public static bool IsPerUserCommand(string target) =>
        target.StartsWith("dx:", StringComparison.Ordinal) || target.StartsWith("spi:", StringComparison.Ordinal);

    internal static bool IsPerUser(ChangeKind kind, string target, RegistryValueSnapshot? reg) => kind switch
    {
        ChangeKind.Registry => reg is not null ? reg.Hive == RegHive.HKCU : target.StartsWith(@"reg:HKCU\", StringComparison.OrdinalIgnoreCase),
        ChangeKind.Command => target.StartsWith("cmd:", StringComparison.Ordinal) && IsPerUserCommand(target[4..]),
        ChangeKind.UserParameter => true,
        _ => false,
    };

    private static readonly ConcurrentDictionary<string, string> AccountNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// "DOMAIN\user" for a SID, or the SID itself when it cannot be resolved. Blocking on the first call per SID (an LSA
    /// lookup can wait seconds for an unreachable domain controller): call off the UI thread; the result is cached.
    /// </summary>
    public static string AccountName(string sid) => AccountNames.GetOrAdd(sid, static s =>
    {
        try { return new SecurityIdentifier(s).Translate(typeof(NTAccount)).Value; }
        catch { return s; }
    });

    /// <summary>The name <see cref="AccountName"/> already resolved, or the SID itself. Never blocks (UI thread).</summary>
    public static string CachedAccountName(string sid) => AccountNames.TryGetValue(sid, out var name) ? name : sid;

    public static string OtherAccountMessage(string sid) =>
        Loc.Tf("Изменение сделано для учётной записи {0}; войдите под ней, чтобы отменить", AccountName(sid));
}

/// <summary>What restoring one record did beyond the plain restore.</summary>
internal sealed record RevertOutcome(string? Note = null, bool Skipped = false, IReadOnlyList<Guid>? DeletedSchemes = null)
{
    public static readonly RevertOutcome Done = new();
}

/// <summary>The system side of an undo. Replaced in unit tests; the real one writes the registry, services, plans, tasks.</summary>
internal interface IJournalSystem
{
    /// <summary>Puts the state recorded in <paramref name="r"/> back. Only called for records that passed <see cref="JournalTrust"/>.</summary>
    Task<RevertOutcome> RestoreAsync(ChangeRecord r);
    Guid ActivePowerScheme();
    /// <summary>Null when the plans could not be listed.</summary>
    bool? PowerSchemeExists(Guid scheme);
    void DeletePowerScheme(Guid scheme);
}

/// <summary>
/// The undo journal: every system change is written here BEFORE it is made, with the original value.
/// Stored as JSON in %ProgramData%\MONOLITH\journal.json, saved atomically.
/// </summary>
public sealed class ChangeJournal
{
    private sealed class JournalFile
    {
        public int Version { get; set; } = 1;
        public List<ChangeBatch> Batches { get; set; } = [];
        public List<ChangeRecord> Records { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lazy<ChangeJournal> Default = new(() => new ChangeJournal(AppPaths.JournalFile));
    public static ChangeJournal Instance => Default.Value;

    private readonly object _lock = new();

    /// <summary>
    /// One revert or one journaled change at a time, journal-wide. A revert holds it for its whole run; a change holds
    /// it from reading the live state through <see cref="Add"/> and the system write (see <see cref="Exclusive{T}"/>).
    /// Otherwise a revert could hand an original to a layer that is being restored, or a new layer could record the
    /// state from the middle of a revert as its original.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<GateHold?> _gateHold = new();

    /// <summary>Marks the flow that holds the gate; it is no longer valid once released (a captured context cannot reuse it).</summary>
    private sealed class GateHold
    {
        public volatile bool Held = true;
    }

    /// <summary>Null: an in-memory journal (tests).</summary>
    private readonly string? _path;
    /// <summary>The real journal in %ProgramData%: only an elevated process may write it.</summary>
    private readonly bool _protected;
    private JournalFile _data = new();

    public event Action? Changed;

    /// <summary>The system side of an undo (tests replace it).</summary>
    internal IJournalSystem SystemAccess { get; set; } = WindowsJournalSystem.Instance;

    /// <summary>Set when the journal had to be quarantined because its folder was writable by non-admins.</summary>
    public string? QuarantinedFile { get; private set; }

    /// <summary>
    /// Set when the journal cannot be trusted or read (journal.json and its .bak are both damaged, or the data
    /// folder could not be locked down). No new changes are allowed until the user starts a new journal
    /// (<see cref="StartNewJournal"/>); the damaged files are never overwritten.
    /// </summary>
    public string? UnavailableReason { get; private set; }
    public bool IsUnavailable => UnavailableReason is not null;

    private static readonly string UnavailableMessage =
        Loc.T("Журнал изменений недоступен, поэтому Lycoris не вносит новые изменения: без журнала их нельзя было бы отменить.");

    public ChangeJournal(string path) : this(path, AppPaths.IsUnderMachineDir(path)) { }

    private ChangeJournal(string? path, bool isProtected)
    {
        _path = path;
        _protected = isProtected;
        if (path is not null) Load();
    }

    /// <summary>TESTS ONLY: a journal outside %ProgramData% (no folder ACL checks); a null path keeps it in memory.</summary>
    internal static ChangeJournal ForTests(string? path = null) => new(path, false);

    /// <summary>
    /// This process may write the journal: it is available and, for the real one in %ProgramData%, elevated. A process that
    /// cannot write it never reverts anything either (an undo it could not record would come back after a restart).
    /// </summary>
    public bool CanWrite => !IsUnavailable && (!_protected || Elevation.IsElevated || ChangeSession.BypassElevationForTests);

    private static readonly string CannotWriteRevert =
        Loc.T("журнал изменений нельзя записать (нужны права администратора) — откат не выполнен, система не изменена");

    public IReadOnlyList<ChangeRecord> Records { get { lock (_lock) return _data.Records.ToList(); } }
    public IReadOnlyList<ChangeBatch> Batches { get { lock (_lock) return _data.Batches.ToList(); } }
    public IReadOnlyList<ChangeRecord> ActiveRecords { get { lock (_lock) return _data.Records.Where(r => !r.Reverted).ToList(); } }

    /// <summary>Starts a batch. Throws when the journal cannot be written: no change may be made without it.</summary>
    public ChangeBatch BeginBatch(string title)
    {
        var batch = new ChangeBatch { Title = title };
        lock (_lock)
        {
            if (UnavailableReason is { } reason) throw new InvalidOperationException($"{UnavailableMessage} {reason}");
            _data.Batches.Add(batch);
            if (!SaveLocked())
            {
                _data.Batches.Remove(batch);
                throw new InvalidOperationException(Loc.T("Не удалось записать журнал изменений на диск — изменение не выполнено."));
            }
        }
        return batch;
    }

    public void UpdateBatch(ChangeBatch batch)
    {
        Save();
    }

    /// <summary>
    /// After the journal was found damaged or untrusted: sets the old files aside (never deletes them) and
    /// starts an empty journal so changes can be made again. Elevated only. Returns false when that failed.
    /// </summary>
    public bool StartNewJournal()
    {
        lock (_lock)
        {
            if (!IsUnavailable) return true;
            if (_protected && !Elevation.IsElevated && !ChangeSession.BypassElevationForTests) return false;
            if (_protected && Elevation.IsElevated)
            {
                AppPaths.SecureMachineDir();
                if (!AppPaths.MachineDirSecured) return false;
            }
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            try
            {
                if (_path is not null && File.Exists(_path)) File.Move(_path, _path + $".unavailable-{stamp}");
                if (_path is not null && File.Exists(_path + ".bak")) File.Move(_path + ".bak", _path + $".bak.unavailable-{stamp}");
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось отложить старый журнал изменений", ex);
                return false;
            }
            _data = new JournalFile();
            UnavailableReason = null;
            Log.Warn("Начат новый журнал изменений; старые файлы сохранены с отметкой {0}.", stamp);
        }
        Changed?.Invoke();
        return true;
    }

    // ───────────────────────────── Gate ─────────────────────────────

    /// <summary>
    /// Runs <paramref name="body"/> while no revert and no other journaled change runs. Blocking wait: call off the UI
    /// thread. Re-entrant: a body that already holds the gate (a tweak's apply calling the session) runs straight away.
    /// </summary>
    public T Exclusive<T>(Func<T> body)
    {
        if (_gateHold.Value is { Held: true }) return body();
        _gate.Wait();
        var previous = _gateHold.Value;
        var hold = new GateHold();
        _gateHold.Value = hold;
        try { return body(); }
        finally
        {
            hold.Held = false;
            _gateHold.Value = previous;
            _gate.Release();
        }
    }

    public void Exclusive(Action body) => Exclusive(() => { body(); return true; });

    /// <summary>As <see cref="Exclusive{T}"/>, waiting asynchronously; the gate stays held across the awaits of <paramref name="body"/>.</summary>
    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> body)
    {
        if (_gateHold.Value is { Held: true }) return await body().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        var hold = new GateHold();
        _gateHold.Value = hold; // set inside this async method: seen by body, never by the caller
        try { return await body().ConfigureAwait(false); }
        finally
        {
            hold.Held = false;
            _gate.Release();
        }
    }

    // ───────────────────────────── Accounts and layers ─────────────────────────────

    private static bool SameTarget(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Records compared by identity (two records never stand for the same change).</summary>
    public static readonly IEqualityComparer<ChangeRecord> ByReference = ReferenceEqualityComparer.Instance;

    /// <summary>Scope of per-user records from older builds: they name no account, so they never share a scope with an account's records.</summary>
    internal const string LegacyScope = "LEGACY";

    /// <summary>
    /// Whose state a record changed: "" for machine-wide changes, the account SID for per-user ones. Per-user records
    /// from older builds (no SID) may have gone into any account's HKCU: they get a scope of their own, so a new change
    /// of an account is never layered under or over them (and never inherits their created keys).
    /// </summary>
    internal static string ScopeOf(ChangeRecord r) =>
        !r.IsPerUser ? "" : r.UserSid is { } sid ? sid.ToUpperInvariant() : LegacyScope;

    /// <summary>
    /// For a per-user record from an older build: the newest active record of the current account on the same target
    /// — that change sets the value in this profile now. Null when there is none, or <paramref name="r"/> is not such a record.
    /// Restoring the old record would overwrite that change, so it waits until the change is reverted.
    /// </summary>
    internal static ChangeRecord? ShadowOf(IReadOnlyList<ChangeRecord> records, ChangeRecord r, string? currentSid)
    {
        if (!r.IsLegacyPerUser || r.Reverted || r.Target.Length == 0 || currentSid is null) return null;
        for (var i = records.Count - 1; i >= 0; i--)
        {
            var x = records[i];
            if (!x.Reverted && x.UserSid is { } sid && UserScope.Same(sid, currentSid) && SameTarget(x.Target, r.Target)) return x;
        }
        return null;
    }

    /// <summary>See <see cref="ShadowOf"/>.</summary>
    public ChangeRecord? ShadowedBy(ChangeRecord r)
    {
        var sid = UserScope.CurrentSid;
        lock (_lock) return ShadowOf(_data.Records, r, sid);
    }

    /// <summary>True for a per-user change recorded for another Windows account: it is never reverted from this one.</summary>
    public static bool IsOtherUser(ChangeRecord r, string? currentSid) =>
        r.UserSid is { } sid && !UserScope.Same(sid, currentSid);

    /// <summary>
    /// Machine-wide changes and the current account's own. Older per-user records without a SID are not another
    /// account's (they can be reverted here, from their row or their tweak), but they are not layered with this account's.
    /// </summary>
    public static bool IsMine(ChangeRecord r) => !IsOtherUser(r, UserScope.CurrentSid);

    /// <summary>
    /// What «ОТКАТИТЬ ВСЁ» and a batch revert may undo on their own: active, this account's or machine-wide, and not a
    /// per-user record from an older build (those name no account, so they are reverted only from their own row).
    /// </summary>
    public static bool IsBulkRevertible(ChangeRecord r) => !r.Reverted && IsMine(r) && !r.IsLegacyPerUser;

    /// <summary>Active records a bulk revert leaves on purpose (another account's, or an older per-user record without one).</summary>
    public static bool IsLeftByBulk(ChangeRecord r) => !r.Reverted && !IsBulkRevertible(r);

    /// <summary>The next newer active layer of <paramref name="r"/>'s target in the same scope, or null when <paramref name="r"/> sets the live value.</summary>
    internal static ChangeRecord? NewerLayer(IReadOnlyList<ChangeRecord> records, ChangeRecord r)
    {
        var i = -1;
        for (var k = 0; k < records.Count; k++)
            if (ReferenceEquals(records[k], r)) { i = k; break; }
        if (i < 0 || r.Target.Length == 0) return null;
        var scope = ScopeOf(r);
        for (var j = i + 1; j < records.Count; j++)
        {
            var n = records[j];
            if (!n.Reverted && SameTarget(n.Target, r.Target) && ScopeOf(n) == scope) return n;
        }
        return null;
    }

    /// <summary>
    /// The active records that set the live value of their target (the newest layer per target and scope), in input
    /// order. Drift checks and live reads compare only these with the system: an older layer's value is already replaced.
    /// An older build's per-user record under the current account's own change of the same value is not one either.
    /// </summary>
    public static IReadOnlyList<ChangeRecord> TopLayers(IEnumerable<ChangeRecord> records, string? currentSid = null)
    {
        var active = records.Where(r => !r.Reverted).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tops = new List<ChangeRecord>();
        for (var i = active.Count - 1; i >= 0; i--)
        {
            var r = active[i];
            if ((r.Target.Length == 0 || seen.Add(ScopeOf(r) + "|" + r.Target)) && ShadowOf(active, r, currentSid) is null) tops.Add(r);
        }
        tops.Reverse();
        return tops;
    }

    /// <summary>The newest active change of a target for the current account (machine-wide targets: any).</summary>
    public ChangeRecord? FindActive(string target)
    {
        var sid = UserScope.CurrentSid;
        lock (_lock)
            return _data.Records.LastOrDefault(r => !r.Reverted && SameTarget(r.Target, target) && !IsOtherUser(r, sid));
    }

    /// <summary>
    /// Every active change of a target for the current account, oldest first (the last one sets the live value). Older
    /// per-user records without an account come first; they are not layers of the account's own records (<see cref="ScopeOf"/>).
    /// </summary>
    public IReadOnlyList<ChangeRecord> ActiveLayers(string target)
    {
        var sid = UserScope.CurrentSid;
        lock (_lock)
            return _data.Records.Where(r => !r.Reverted && SameTarget(r.Target, target) && !IsOtherUser(r, sid)).ToList();
    }

    /// <summary>The newer active change that now sets <paramref name="r"/>'s target, or null when <paramref name="r"/> does.</summary>
    public ChangeRecord? NewerLayer(ChangeRecord r)
    {
        lock (_lock) return r.Reverted ? null : NewerLayer(_data.Records, r);
    }

    public bool HasActiveSource(string source)
    {
        var sid = UserScope.CurrentSid;
        lock (_lock) return _data.Records.Any(r => !r.Reverted && r.TouchedBy(source) && !IsOtherUser(r, sid));
    }

    /// <summary>The records «ОТКАТИТЬ ВСЁ» reverts (see <see cref="IsBulkRevertible"/>).</summary>
    public IReadOnlyList<ChangeRecord> RevertAllCandidates()
    {
        lock (_lock) return _data.Records.Where(IsBulkRevertible).ToList();
    }

    /// <summary>The records a batch revert reverts (see <see cref="IsBulkRevertible"/>).</summary>
    public IReadOnlyList<ChangeRecord> BatchCandidates(Guid batchId)
    {
        lock (_lock) return _data.Records.Where(r => r.BatchId == batchId && IsBulkRevertible(r)).ToList();
    }

    /// <summary>
    /// For a write of a value into <paramref name="path"/>: the shallowest key Lycoris created that covers it, from an
    /// active record of the same hive and account. A value written into such a key inherits it, so whichever record is
    /// reverted last removes the created keys, in any order. Records from older builds count with their own key only,
    /// and a write of an account never inherits a key from an older per-user record without an account.
    /// </summary>
    public string? InheritedKeyRoot(RegHive hive, string path, string? userSid)
    {
        var scope = hive == RegHive.HKCU ? userSid?.ToUpperInvariant() ?? LegacyScope : "";
        string? best = null;
        lock (_lock)
        {
            foreach (var r in _data.Records)
            {
                if (r.Reverted || ScopeOf(r) != scope) continue;
                string? root;
                if (r.Kind == ChangeKind.Registry && r.Registry is { } s && s.Hive == hive)
                    root = s.CreatedKeyRoot ?? (!s.KeyExisted ? s.Path : null);
                else if (hive == RegHive.HKCU && r.Kind == ChangeKind.Command)
                    root = TweakCatalog.DxCreatedKeyRoot(r); // a DirectX token script that created UserGpuPreferences (or DirectX)
                else continue;
                if (root is not null && RegistryHelper.IsSameOrAncestor(root, path)) best = RegistryHelper.ShallowerRoot(best, root);
            }
        }
        return best;
    }

    /// <summary>
    /// Adds a record BEFORE the change is made. A target that already has an active change gets a new LAYER whose
    /// original is the live state now (the value of the previous layer), so each change can be undone on its own.
    /// Only a repeated write of the same target by the same source in the same batch is merged: the existing record is
    /// returned (IsNew = false) — call <see cref="MergeInto"/> after the change succeeded.
    /// Throws (and keeps nothing) when a new record could not be written to disk: the caller must then not make the
    /// change — no change without an on-disk undo record.
    /// </summary>
    public (ChangeRecord Record, bool IsNew) Add(ChangeRecord record)
    {
        ChangeRecord result;
        bool isNew;
        lock (_lock)
        {
            var scope = ScopeOf(record);
            var top = record.Target.Length == 0 ? null
                : _data.Records.LastOrDefault(r => !r.Reverted && SameTarget(r.Target, record.Target) && ScopeOf(r) == scope);
            if (top is not null && CanMerge(top, record))
            {
                result = top;
                isNew = false;
            }
            else
            {
                if (UnavailableReason is { } reason) throw new InvalidOperationException($"{UnavailableMessage} {reason}");
                _data.Records.Add(record);
                if (!SaveLocked())
                {
                    _data.Records.Remove(record);
                    throw new InvalidOperationException(Loc.T("Не удалось записать журнал изменений на диск — изменение не выполнено."));
                }
                result = record;
                isNew = true;
            }
        }
        if (isNew) Changed?.Invoke();
        return (result, isNew);
    }

    /// <summary>The same change written twice in one action (same batch, source, account): one record keeps the first original.</summary>
    internal static bool CanMerge(ChangeRecord top, ChangeRecord incoming) =>
        top.BatchId == incoming.BatchId && top.Source == incoming.Source && top.Kind == incoming.Kind
        && UserScope.Same(top.UserSid, incoming.UserSid) && !top.IsLegacyMerged
        && (incoming.PowerSchemeCreated is null || top.PowerSchemeCreated is null || top.PowerSchemeCreated == incoming.PowerSchemeCreated);

    /// <summary>After a successful repeated write of a merged target (see <see cref="Add"/>): remember the new value.</summary>
    public void MergeInto(ChangeRecord existing, ChangeRecord incoming)
    {
        lock (_lock)
        {
            existing.NewValue = incoming.NewValue;
            if (incoming.Source != existing.Source && !existing.AlsoSources.Contains(incoming.Source))
                existing.AlsoSources.Add(incoming.Source);
            existing.PowerSchemeCreated ??= incoming.PowerSchemeCreated;
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Removes a record that was added but whose change then failed.</summary>
    public void Remove(ChangeRecord record)
    {
        lock (_lock)
        {
            _data.Records.Remove(record);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    // ───────────────────────────── Display changes awaiting confirmation ─────────────────────────────

    /// <summary>A display or input change was confirmed on screen (or found saved after a crash): no longer pending; <paramref name="newValue"/> is the live value.</summary>
    public bool ConfirmPending(ChangeRecord r, string newValue)
    {
        bool saved;
        lock (_lock)
        {
            if (r.DisplayMode is { } mode) mode.PendingConfirm = false;
            if (r.DisplayHdr is { } hdr) hdr.PendingConfirm = false;
            if (r.Spi is { } spi) spi.PendingConfirm = false;
            r.NewValue = newValue;
            saved = SaveLocked();
        }
        Changed?.Invoke();
        return saved;
    }

    /// <summary>Active display / input records applied temporarily and never confirmed (Lycoris or the PC died during the countdown).</summary>
    public IReadOnlyList<ChangeRecord> PendingDisplayRecords()
    {
        lock (_lock) return _data.Records.Where(r => !r.Reverted && r.IsPendingConfirm).ToList();
    }

    /// <summary>A pending record whose change is not in effect any more: closed as reverted, with <paramref name="note"/>.</summary>
    public bool ClosePending(ChangeRecord r, string note)
    {
        bool saved;
        lock (_lock)
        {
            if (r.DisplayMode is { } mode) mode.PendingConfirm = false;
            if (r.DisplayHdr is { } hdr) hdr.PendingConfirm = false;
            if (r.Spi is { } spi) spi.PendingConfirm = false;
            r.Reverted = true;
            r.RevertedUtc = DateTime.UtcNow;
            r.RevertError = null;
            r.RevertNote = note;
            saved = SaveLocked();
        }
        Log.Info($"«{r.Title}»: {note}");
        Changed?.Invoke();
        return saved;
    }

    // ───────────────────────────── Revert ─────────────────────────────

    /// <summary>
    /// Reverts the given records, newest first. A record whose target has a newer active layer (same account) is not
    /// restored in the system: the newer layer takes over its original (and the plan copy it created, while that plan
    /// is still needed) and the record is marked reverted — the newer change keeps working, and its own undo later goes
    /// back to the state from before both. Every other record is restored and, when <paramref name="verify"/> is given,
    /// read back right away; the result is stored on the record (<see cref="ChangeRecord.RevertMismatch"/>).
    /// The journal is saved after every record. Another account's records are refused, and so is an older per-user
    /// record while the current account's own later change of the same value is active (<see cref="ShadowOf"/>).
    /// Runs under the journal gate: no other revert and no journaled change runs meanwhile.
    /// </summary>
    public async Task<RevertResult> RevertAsync(IEnumerable<ChangeRecord> records, Func<ChangeRecord, RevertCheck>? verify = null)
    {
        var result = await ExclusiveAsync(() => RevertCoreAsync(records, verify)).ConfigureAwait(false);
        Changed?.Invoke();
        return result;
    }

    /// <summary>Picks the records under the gate (so the list sees every earlier revert), reverts them and reports what was left on purpose.</summary>
    private async Task<RevertResult> RevertSelectedAsync(Func<(List<ChangeRecord> Revert, List<ChangeRecord> Left)> select,
        Func<ChangeRecord, RevertCheck>? verify)
    {
        var result = await ExclusiveAsync(async () =>
        {
            List<ChangeRecord> revert, left;
            lock (_lock) (revert, left) = select();
            var r = await RevertCoreAsync(revert, verify).ConfigureAwait(false);
            return r with { Left = left };
        }).ConfigureAwait(false);
        Changed?.Invoke();
        return result;
    }

    /// <summary>What a revert reads to put a record back: compared after the restore as a backstop to the gate.</summary>
    private readonly record struct RestoreInputs(string OldValue, RegistryValueSnapshot? Registry, ServiceStartSnapshot? Service,
        Guid? PlanOriginal, Guid? PlanCreated, string PlansMore, bool? TaskWasEnabled, string? RevertFile, string? RevertArgs,
        SpiSnapshot? Spi, DisplayModeSnapshot? DisplayMode, DisplayHdrSnapshot? DisplayHdr, MonitorVcpSnapshot? Monitor,
        AudioLevelSnapshot? Audio, NicPropertySnapshot? Nic)
    {
        public static RestoreInputs Of(ChangeRecord r) => new(r.OldValue, r.Registry, r.Service, r.PowerSchemeOriginal,
            r.PowerSchemeCreated, string.Join(",", r.PowerSchemesCreatedMore ?? []), r.TaskWasEnabled, r.RevertFile, r.RevertArgs,
            r.Spi, r.DisplayMode, r.DisplayHdr, r.Monitor, r.Audio, r.Nic);
    }

    private async Task<RevertResult> RevertCoreAsync(IEnumerable<ChangeRecord> records, Func<ChangeRecord, RevertCheck>? verify)
    {
        List<ChangeRecord> list;
        lock (_lock)
        {
            var order = new Dictionary<ChangeRecord, int>(ByReference);
            for (var i = 0; i < _data.Records.Count; i++) order[_data.Records[i]] = i;
            // Newest first (journal order), so each record is the top layer of its target when it is undone.
            list = records.Distinct(ByReference).Where(r => !r.Reverted)
                .OrderByDescending(r => order.TryGetValue(r, out var i) ? i : -1)
                .ThenByDescending(r => r.TimestampUtc).ToList();
        }

        var sid = UserScope.CurrentSid;
        int ok = 0, failed = 0, handed = 0, confirmed = 0;
        var errors = new List<string>();
        var mismatches = new List<string>();
        var notes = new List<string>();
        var saved = true;
        foreach (var r in list)
        {
            if (r.Reverted) continue; // closed meanwhile (a setting of a power plan deleted by an earlier record)
            try
            {
                // Checked before any system write: an undo that cannot be recorded must not happen at all.
                if (!CanWrite) throw new InvalidOperationException(CannotWriteRevert);
                if (IsOtherUser(r, sid)) throw new InvalidOperationException(UserScope.OtherAccountMessage(r.UserSid!));
                // The journal file is input, not authority: refuse anything Lycoris's own code could not have recorded.
                if (JournalTrust.Check(r) is { } refusal)
                    throw new InvalidOperationException(Loc.Tf("Отказано: {0}. Такую запись Lycoris создать не мог.", refusal));

                ChangeRecord? newer, shadow;
                lock (_lock)
                {
                    newer = NewerLayer(_data.Records, r);
                    shadow = newer is null ? ShadowOf(_data.Records, r, sid) : null;
                }
                if (newer is not null)
                {
                    saved &= HandOver(r, newer);
                    handed++;
                    Log.Success("Откат «{0}»: значение сейчас задаёт более позднее изменение «{1}» — система не изменена, исходное значение передано ему.", r.Title, newer.Title);
                    continue;
                }
                if (shadow is not null)
                    throw new InvalidOperationException(
                        Loc.Tf("сейчас это значение в профиле текущей учётной записи задаёт более позднее изменение «{0}» — сначала откатите его", shadow.Title));

                var outcome = await RestoreCheckedAsync(r).ConfigureAwait(false);
                lock (_lock)
                {
                    r.Reverted = true;
                    r.RevertedUtc = DateTime.UtcNow;
                    r.RevertError = null;
                    r.HandedTo = null;
                    r.RevertNote = outcome.Note;
                    if (r.Spi is { } spi) spi.PendingConfirm = false;
                    r.RevertVerifiedUtc = null;
                    r.RevertMismatch = null;
                    saved &= SaveLocked(); // persist each completed undo at once
                }
                ok++;
                if (outcome.Note is { } note) notes.Add($"{r.Title}: {note}");
                Log.Success("Откат: {0} → {1}", r.Title, r.OldValue);
                if (outcome.DeletedSchemes is { Count: > 0 } deleted) saved &= CloseSettingsOfDeletedPlans(deleted);

                if (verify is not null && !outcome.Skipped)
                {
                    var check = SafeVerify(verify, r);
                    lock (_lock)
                    {
                        r.RevertVerifiedUtc = check.Matches is null ? null : DateTime.UtcNow;
                        r.RevertMismatch = check.MismatchText;
                    }
                    if (check.Matches == true) confirmed++;
                    else if (check.Matches == false)
                    {
                        mismatches.Add($"{r.Title}: {check.MismatchText}");
                        Log.Warn("Откат «{0}» не совпал с исходным значением: {1}", r.Title, check.MismatchText);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_lock) r.RevertError = ex.Message;
                failed++;
                errors.Add($"{r.Title}: {ex.Message}");
                Log.Error("Не удалось откатить «{0}»", ex, r.Title);
            }
        }
        saved &= Save();
        if (!saved && list.Count > 0)
            Log.Error("Журнал изменений не удалось сохранить после отката — после перезапуска откаченные записи могут снова показаться активными.");
        return new RevertResult(ok + handed, failed, errors, saved)
        {
            HandedOver = handed, Confirmed = confirmed, Mismatches = mismatches, Notes = notes,
        };
    }

    /// <summary>
    /// Restores <paramref name="r"/> in the system. Backstop to the gate: the restore inputs are read before the await
    /// and compared after it; when they changed meanwhile, the restore runs again with the new ones (and gives up with
    /// an error after three tries, leaving the record active) instead of marking a stale restore as done.
    /// </summary>
    private async Task<RevertOutcome> RestoreCheckedAsync(ChangeRecord r)
    {
        for (var attempt = 1; ; attempt++)
        {
            RestoreInputs before;
            lock (_lock) before = RestoreInputs.Of(r);
            var outcome = await SystemAccess.RestoreAsync(r).ConfigureAwait(false);
            bool same;
            lock (_lock) same = RestoreInputs.Of(r) == before;
            if (same) return outcome;
            if (attempt >= 3)
                throw new InvalidOperationException(Loc.T("исходное значение менялось во время отката — запись оставлена активной, повторите откат"));
            Log.Warn("Откат «{0}»: исходное значение изменилось во время отката — откат повторён.", r.Title);
        }
    }

    private static RevertCheck SafeVerify(Func<ChangeRecord, RevertCheck> verify, ChangeRecord r)
    {
        try { return verify(r); }
        catch (Exception ex)
        {
            Log.Warn("Не удалось проверить откат «{0}»: {1}", r.Title, ex.Message);
            return RevertCheck.NotChecked;
        }
    }

    /// <summary>Every plan a record deletes on revert.</summary>
    internal static List<Guid> CreatedSchemes(ChangeRecord r)
    {
        var list = new List<Guid>();
        if (r.PowerSchemeCreated is { } c) list.Add(c);
        if (r.PowerSchemesCreatedMore is { } more) list.AddRange(more);
        return list.Where(g => g != Guid.Empty).Distinct().ToList();
    }

    /// <summary>
    /// Reverting <paramref name="r"/> while <paramref name="newer"/> sets the same target: nothing changes in the system.
    /// The newer layer takes over r's original (registry snapshot, service start, plan, task state, or the absolute undo
    /// command of the catalog — so it stays a valid record), and r is marked reverted. A plan copy r created is kept
    /// (and handed over) while it is still needed: it is the active plan, or another active layer goes back to it on its
    /// undo; otherwise it is deleted now. Returns whether the journal was saved.
    /// </summary>
    private bool HandOver(ChangeRecord r, ChangeRecord newer)
    {
        if (r.Kind != newer.Kind) throw new InvalidOperationException(Loc.T("Отказано: у изменений одной цели разный тип."));
        var created = r.Kind == ChangeKind.PowerScheme ? CreatedSchemes(r) : [];
        var active = created.Count > 0 ? SystemAccess.ActivePowerScheme() : Guid.Empty;
        var keep = created.Count == 0 ? [] : StillNeededPlans(r, newer, created, active);
        var drop = created.Except(keep).ToList();
        var newerMayOwnPlans = JournalTrust.IsTweakish(newer);
        bool saved;
        lock (_lock)
        {
            var backup = (newer.OldValue, newer.Registry, newer.Service, newer.PowerSchemeOriginal, newer.PowerSchemeCreated,
                More: newer.PowerSchemesCreatedMore?.ToList(), newer.TaskWasEnabled, newer.RevertFile, newer.RevertArgs);
            var deviceBackup = (newer.Spi, newer.DisplayMode, newer.DisplayHdr, newer.Monitor, newer.Audio, newer.Nic);
            newer.OldValue = r.OldValue;
            switch (r.Kind)
            {
                case ChangeKind.Registry: newer.Registry = r.Registry; break;
                case ChangeKind.Service: newer.Service = r.Service; break;
                case ChangeKind.PowerScheme: newer.PowerSchemeOriginal = r.PowerSchemeOriginal; break;
                case ChangeKind.ScheduledTask: newer.TaskWasEnabled = r.TaskWasEnabled; break;
                case ChangeKind.Command:
                    newer.RevertFile = r.RevertFile;
                    newer.RevertArgs = r.RevertArgs;
                    break;
                // Device records: only the ORIGINAL part moves; the newer layer keeps what it set (its target).
                case ChangeKind.UserParameter: newer.Spi = r.Spi; break;
                case ChangeKind.DisplayMode when r.DisplayMode is { } older && newer.DisplayMode is { } mine:
                    newer.DisplayMode = new DisplayModeSnapshot
                    {
                        MonitorPath = mine.MonitorPath, MonitorName = mine.MonitorName, Original = older.Original, Target = mine.Target,
                        PendingConfirm = mine.PendingConfirm,
                    };
                    break;
                case ChangeKind.DisplayHdr when r.DisplayHdr is { } older && newer.DisplayHdr is { } mine:
                    newer.DisplayHdr = new DisplayHdrSnapshot
                    {
                        MonitorPath = mine.MonitorPath, MonitorName = mine.MonitorName, OriginalEnabled = older.OriginalEnabled,
                        TargetEnabled = mine.TargetEnabled, PendingConfirm = mine.PendingConfirm,
                        ModeBefore = older.ModeBefore, AcmBefore = older.AcmBefore,
                    };
                    break;
                case ChangeKind.MonitorSetting: newer.Monitor = r.Monitor; break;
                case ChangeKind.AudioLevel: newer.Audio = r.Audio; break;
                case ChangeKind.NicProperty: newer.Nic = r.Nic; break;
            }
            if (newerMayOwnPlans)
            {
                foreach (var c in keep)
                {
                    if (newer.PowerSchemeCreated is null) newer.PowerSchemeCreated = c;
                    else if (newer.PowerSchemeCreated != c && newer.PowerSchemesCreatedMore?.Contains(c) != true)
                        (newer.PowerSchemesCreatedMore ??= []).Add(c);
                }
            }
            if (JournalTrust.Check(newer) is { } refusal)
            {
                newer.OldValue = backup.OldValue;
                newer.Registry = backup.Registry;
                newer.Service = backup.Service;
                newer.PowerSchemeOriginal = backup.PowerSchemeOriginal;
                newer.PowerSchemeCreated = backup.PowerSchemeCreated;
                newer.PowerSchemesCreatedMore = backup.More;
                newer.TaskWasEnabled = backup.TaskWasEnabled;
                newer.RevertFile = backup.RevertFile;
                newer.RevertArgs = backup.RevertArgs;
                (newer.Spi, newer.DisplayMode, newer.DisplayHdr, newer.Monitor, newer.Audio, newer.Nic) = deviceBackup;
                throw new InvalidOperationException(Loc.Tf("Отказано: {0}. Исходное значение нельзя передать более позднему изменению.", refusal));
            }
            r.Reverted = true;
            r.RevertedUtc = DateTime.UtcNow;
            r.RevertError = null;
            r.HandedTo = newer.Id;
            if (r.Spi is { } spiOfOlder) spiOfOlder.PendingConfirm = false;
            r.RevertVerifiedUtc = null;
            r.RevertMismatch = null;
            r.RevertNote = !newerMayOwnPlans && keep.Count > 0
                ? Loc.T("Созданная Lycoris схема питания ещё нужна (она включена или к ней вернёт откат более позднего изменения), поэтому не удалена: удалите её в «Электропитании», когда она станет не нужна.")
                : null;
            saved = SaveLocked();
        }
        if (!newerMayOwnPlans && keep.Count > 0) Log.Warn($"«{r.Title}»: {r.RevertNote}");

        var gone = new List<Guid>();
        foreach (var c in drop)
        {
            try
            {
                if (SystemAccess.PowerSchemeExists(c) == true) SystemAccess.DeletePowerScheme(c);
                if (SystemAccess.PowerSchemeExists(c) == false) gone.Add(c);
            }
            catch (Exception ex) { Log.Warn("Не удалось удалить схему питания {0}: {1}", c, ex.Message); }
        }
        if (gone.Count > 0) saved &= CloseSettingsOfDeletedPlans(gone);
        return saved;
    }

    /// <summary>
    /// The plan copies of <paramref name="r"/> that must survive its hand-over to <paramref name="newer"/>: the active
    /// plan, and every plan another active layer goes back to on its undo. The newer layer counts with the original it
    /// takes over from r (its own original — often r's copy — is replaced by it).
    /// </summary>
    private List<Guid> StillNeededPlans(ChangeRecord r, ChangeRecord newer, IReadOnlyList<Guid> created, Guid active)
    {
        HashSet<Guid> referenced;
        lock (_lock)
        {
            referenced = _data.Records
                .Where(x => !x.Reverted && !ReferenceEquals(x, r) && !ReferenceEquals(x, newer)
                            && x.Kind == ChangeKind.PowerScheme && SameTarget(x.Target, r.Target))
                .Select(x => x.PowerSchemeOriginal ?? Guid.Empty)
                .ToHashSet();
        }
        if (r.PowerSchemeOriginal is { } original) referenced.Add(original);
        return created.Where(c => c == active || referenced.Contains(c)).ToList();
    }

    /// <summary>
    /// A deleted plan copy takes its settings with it: active power-setting records of that plan have nothing left to
    /// undo, so they are marked reverted (with a note) instead of failing forever.
    /// </summary>
    private bool CloseSettingsOfDeletedPlans(IReadOnlyCollection<Guid> schemes)
    {
        var closed = new List<ChangeRecord>();
        lock (_lock)
        {
            foreach (var g in schemes)
            {
                var prefix = $"cmd:powercfg:{g:D}:";
                foreach (var r in _data.Records.Where(x => !x.Reverted && x.Kind == ChangeKind.Command
                                                           && x.Target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    r.Reverted = true;
                    r.RevertedUtc = DateTime.UtcNow;
                    r.RevertError = null;
                    r.RevertNote = Loc.T("Схема питания, в которой Lycoris менял этот параметр, удалена вместе с ним — откатывать нечего.");
                    closed.Add(r);
                }
            }
            if (closed.Count == 0) return true;
            foreach (var r in closed) Log.Info("«{0}»: схема питания удалена вместе с параметром — запись закрыта.", r.Title);
            return SaveLocked();
        }
    }

    /// <summary>
    /// The records a revert of <paramref name="sources"/> (a tweak, a page) undoes, and the ones it leaves. Undone: active
    /// and not another account's. Left: an older per-user record under the current account's own later change of the same
    /// value (<see cref="ShadowOf"/>) — the old record may be another account's, so its original is not written over this
    /// profile's change. It can still be reverted from its own row once that change is reverted.
    /// </summary>
    public (IReadOnlyList<ChangeRecord> Revert, IReadOnlyList<ChangeRecord> Left) SourceCandidates(IReadOnlyCollection<string> sources)
    {
        var sid = UserScope.CurrentSid;
        lock (_lock) return SourceCandidatesLocked(sources, sid);
    }

    private (List<ChangeRecord> Revert, List<ChangeRecord> Left) SourceCandidatesLocked(IReadOnlyCollection<string> sources, string? sid)
    {
        var revert = new List<ChangeRecord>();
        var left = new List<ChangeRecord>();
        foreach (var r in _data.Records)
        {
            if (r.Reverted || IsOtherUser(r, sid) || !sources.Any(r.TouchedBy)) continue;
            (ShadowOf(_data.Records, r, sid) is null ? revert : left).Add(r);
        }
        return (revert, left);
    }

    public Task<RevertResult> RevertSourceAsync(string source, Func<ChangeRecord, RevertCheck>? verify = null) =>
        RevertSourcesAsync([source], verify);

    /// <summary>Reverts what <paramref name="sources"/> changed for this account (see <see cref="SourceCandidates"/>).</summary>
    public Task<RevertResult> RevertSourcesAsync(IReadOnlyCollection<string> sources, Func<ChangeRecord, RevertCheck>? verify = null)
    {
        var sid = UserScope.CurrentSid;
        return RevertSelectedAsync(() => SourceCandidatesLocked(sources, sid), verify);
    }

    /// <summary>Reverts a batch (see <see cref="IsBulkRevertible"/>); <see cref="RevertResult.Left"/> names the records it left.</summary>
    public Task<RevertResult> RevertBatchAsync(Guid batchId, Func<ChangeRecord, RevertCheck>? verify = null) =>
        RevertSelectedAsync(() =>
        {
            var batch = _data.Records.Where(r => r.BatchId == batchId).ToList();
            return (batch.Where(IsBulkRevertible).ToList(), batch.Where(IsLeftByBulk).ToList());
        }, verify);

    /// <summary>«ОТКАТИТЬ ВСЁ» (see <see cref="IsBulkRevertible"/>); <see cref="RevertResult.Left"/> names the records it left.</summary>
    public Task<RevertResult> RevertAllAsync(Func<ChangeRecord, RevertCheck>? verify = null) =>
        RevertSelectedAsync(() => (_data.Records.Where(IsBulkRevertible).ToList(), _data.Records.Where(IsLeftByBulk).ToList()), verify);

    /// <summary>
    /// Registry locations Lycoris never writes during an undo: autostart and process-hijack points.
    /// No legitimate Lycoris record targets them; refusing them makes a forged journal useless.
    /// </summary>
    internal static bool IsForbiddenRestoreTarget(RegistryValueSnapshot s)
    {
        var p = s.Path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        var n = s.Name.ToLowerInvariant();
        if (p.Contains("image file execution options") || p.Contains("silentprocessexit")) return true;
        if (p.Contains(@"\winlogon") || p.Contains(@"\appinit_dlls") || n == "appinit_dlls") return true;
        if (p.EndsWith(@"currentversion\run") || p.EndsWith(@"currentversion\runonce") || p.EndsWith(@"currentversion\runonceex")
            || p.EndsWith(@"currentversion\runservices") || p.EndsWith(@"currentversion\runservicesonce")
            || p.EndsWith(@"policies\explorer\run")) return true;
        if (p.Contains(@"\services\") && n is "imagepath" or "servicedll" or "failurecommand") return true;
        if (p.Contains(@"\shell\open\command") || p.Contains(@"\shellex\")) return true;
        if (p.Contains(@"windows defender") || p.Contains(@"\lsa")) return true;
        return false;
    }

    // ───────────────────────────── Storage ─────────────────────────────

    private void Load()
    {
        var path = _path!;
        if (_protected && Elevation.IsElevated)
        {
            AppPaths.SecureMachineDir();
            if (!AppPaths.MachineDirSecured)
            {
                // Could not lock the folder down: anyone might still be able to edit the journal. Never replay it.
                UnavailableReason = Loc.T("Не удалось защитить папку данных Lycoris от записи обычными пользователями.");
                Log.Error("Журнал изменений не загружен: папка данных не защищена.");
                return;
            }
            if (AppPaths.MachineDirWasInsecure && (File.Exists(path) || File.Exists(path + ".bak")))
            {
                // Someone without admin rights could have edited it: never replay it. Keep it for inspection.
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var quarantined = path + $".untrusted-{stamp}";
                try
                {
                    if (File.Exists(path)) File.Move(path, quarantined);
                    if (File.Exists(path + ".bak")) File.Move(path + ".bak", quarantined + ".bak");
                }
                catch (Exception ex)
                {
                    UnavailableReason = Loc.T("Журнал хранился в незащищённой папке, и его не удалось отложить.");
                    Log.Error("Не удалось отложить недоверенный журнал изменений", ex);
                    return;
                }
                QuarantinedFile = quarantined;
                Log.Warn("Журнал изменений хранился в незащищённой папке и отложен как недоверенный: {0}", QuarantinedFile);
                return;
            }
        }

        if (TryRead(path, out var data, out var error))
        {
            _data = data;
            return;
        }
        var backupPath = path + ".bak";
        if (error is null)
        {
            // No journal.json. A surviving .bak still holds undo records (e.g. a crash during a save).
            if (TryRead(backupPath, out var orphan, out _))
            {
                _data = orphan;
                Log.Warn("Журнал изменений не найден — загружена резервная копия.");
            }
            return;
        }

        // Damaged: keep a copy, then fall back to the last good version.
        try { File.Copy(path, path + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
        if (TryRead(backupPath, out var backup, out _))
        {
            _data = backup;
            Log.Warn("Журнал изменений был повреждён — восстановлен из резервной копии.");
            // Move the damaged file away so the next save does not rotate it into .bak over the good copy.
            if (CanWrite)
            {
                try { File.Delete(path); } catch { }
            }
            return;
        }

        // Both copies are unreadable: keep every file untouched and refuse new changes (their undo would be lost).
        _data = new JournalFile();
        UnavailableReason = Loc.T("Файл журнала и его резервная копия повреждены.");
        Log.Error("Журнал изменений и его резервная копия повреждены — новые изменения заблокированы", error);
    }

    private static bool TryRead(string path, out JournalFile data, out Exception? error)
    {
        data = new JournalFile();
        error = null;
        try
        {
            if (!File.Exists(path)) return false;
            data = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(path), JsonOptions) ?? new JournalFile();
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>Writes the journal to disk. Returns false when nothing was written (the error is logged).</summary>
    public bool Save()
    {
        lock (_lock) return SaveLocked();
    }

    private bool SaveLocked()
    {
        // A non-elevated Lycoris never creates or touches the machine journal; an unavailable one is never overwritten.
        if (!CanWrite) return false;
        if (_path is null) return true; // in-memory (tests)
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            if (!(_protected ? AppPaths.TryEnsureDir(dir) : TryCreateDir(dir)))
            {
                Log.Error("Не удалось сохранить журнал изменений: папка {0} недоступна.", dir);
                return false;
            }
            var tmp = _path + ".tmp";
            try { File.Delete(tmp); } catch { }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_data, JsonOptions);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(_path))
                File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmp, _path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить журнал изменений", ex);
            return false;
        }
    }

    /// <summary>A journal outside %ProgramData%\MONOLITH (tests): its folder is created like any other.</summary>
    private static bool TryCreateDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>The real system side of an undo: registry, services, power plans, scheduled tasks, catalog undo commands.</summary>
internal sealed class WindowsJournalSystem : IJournalSystem
{
    public static readonly WindowsJournalSystem Instance = new();

    /// <summary>The only programs a journaled undo command may run (full System32 paths).</summary>
    private static readonly string[] AllowedRevertPrograms =
    [
        ProcessRunner.Sys("cmd.exe"),
        Path.Combine(ProcessRunner.System32, "WindowsPowerShell", "v1.0", "powershell.exe"),
        ProcessRunner.Sys("powercfg.exe"),
        ProcessRunner.Sys("netsh.exe"),
        ProcessRunner.Sys("fsutil.exe"),
        ProcessRunner.Sys("pnputil.exe"),
    ];

    public Guid ActivePowerScheme() => PowerSchemes.GetActive();
    public bool? PowerSchemeExists(Guid scheme) => PowerSchemes.TryExists(scheme);
    public void DeletePowerScheme(Guid scheme) => PowerSchemes.Delete(scheme);

    public async Task<RevertOutcome> RestoreAsync(ChangeRecord r)
    {
        switch (r.Kind)
        {
            case ChangeKind.Registry when r.Registry is not null:
                if (ChangeJournal.IsForbiddenRestoreTarget(r.Registry))
                    throw new InvalidOperationException(Loc.Tf("Отказано: {0} — защищённое место реестра. Такую запись Lycoris создать не мог.", r.Registry.FullName));
                if (r.Source == "startup" && JournalTrust.IsPackagedStartupMark(r.Registry))
                {
                    // Never recreate the key of an uninstalled app, and never override a state set by policy.
                    if (!RegistryHelper.KeyExists(r.Registry.Hive, r.Registry.Path))
                        return new RevertOutcome(Loc.T("Приложения больше нет — откатывать нечего."), Skipped: true);
                    if (RegistryHelper.Read(r.Registry.Hive, r.Registry.Path, "State") is int live && live is 3 or 4)
                        return new RevertOutcome(Loc.T("Состояние задано политикой организации — Lycoris его не меняет."), Skipped: true);
                }
                RegistryHelper.Restore(r.Registry);
                return RevertOutcome.Done;

            case ChangeKind.Service when r.Service is not null:
                if (ServiceControl.Query(r.Service.Name) is null)
                {
                    Log.Info("Откат пропущен: службы {0} больше нет в системе.", r.Service.Name);
                    return new RevertOutcome(Loc.T("Службы больше нет в системе — откатывать нечего."), Skipped: true);
                }
                // Never allowProtected: no legitimate record can target a protected service (JournalTrust refuses it too).
                ServiceControl.Restore(r.Service);
                return RevertOutcome.Done;

            case ChangeKind.PowerScheme:
            {
                string? note = null;
                if (r.PowerSchemeOriginal is { } original && original != Guid.Empty && PowerSchemes.Exists(original))
                {
                    PowerSchemes.SetActive(original);
                }
                else
                {
                    PowerSchemes.SetActive(PowerSchemes.Balanced);
                    note = r.PowerSchemeOriginal is { } o && o != Guid.Empty
                        ? Loc.T("Исходной схемы питания больше нет — включена «Сбалансированная».")
                        : Loc.T("Исходная схема питания не была записана — включена «Сбалансированная».");
                    Log.Warn($"«{r.Title}»: {note}");
                }
                var gone = new List<Guid>();
                foreach (var created in ChangeJournal.CreatedSchemes(r))
                {
                    if (created == r.PowerSchemeOriginal) continue;
                    if (PowerSchemes.TryExists(created) == true) PowerSchemes.Delete(created);
                    if (PowerSchemes.TryExists(created) == false) gone.Add(created);
                    else Log.Warn("«{0}»: созданную Lycoris схему питания {1} удалить не удалось.", r.Title, created);
                }
                return new RevertOutcome(note, DeletedSchemes: gone);
            }

            case ChangeKind.ScheduledTask when r.TaskPath is not null && r.TaskWasEnabled is { } wasEnabled:
                if (!ScheduledTasks.Query(r.TaskPath).Exists)
                {
                    Log.Info("Откат пропущен: задачи {0} больше нет в планировщике.", r.TaskPath);
                    return new RevertOutcome(Loc.T("Задачи больше нет в планировщике — откатывать нечего."), Skipped: true);
                }
                ScheduledTasks.SetEnabled(r.TaskPath, wasEnabled);
                return RevertOutcome.Done;

            case ChangeKind.Command when !string.IsNullOrWhiteSpace(r.RevertFile):
                SafetyLock.ThrowIfLocked(Loc.T("откат команды"));
                // A power setting of a plan that no longer exists went away with the plan: nothing is left to undo.
                if (JournalTrust.PowercfgTarget(r.Target) is { } pc && PowerSchemes.TryExists(pc.Scheme) == false)
                {
                    Log.Info("Откат пропущен: схемы питания {0} больше нет — изменённое значение удалено вместе с ней.", pc.Scheme);
                    return new RevertOutcome(Loc.T("Схемы питания больше нет — параметр удалён вместе с ней, откатывать нечего."), Skipped: true);
                }
                // DNS records from older builds name the adapter by its (non-persistent) index: run the current
                // GUID-based script with the same servers instead.
                var (revertFile, revertArgs) = JournalTrust.UpgradeLegacyDns(r) ?? (r.RevertFile!, r.RevertArgs ?? "");
                if (!AllowedRevertPrograms.Contains(revertFile, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Loc.Tf("Отказано: команда отката «{0}» не из списка системных утилит.", revertFile));
                var encoded = revertArgs.Contains("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
                if (encoded) Log.Command(Loc.Tf("powershell: откат «{0}»", r.Title));
                var result = await ProcessRunner.RunAsync(revertFile, revertArgs,
                    new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(2), LogCommand = !encoded }).ConfigureAwait(false);
                var rebootPending = result is { ExitCode: ProcessExit.RebootRequired, TimedOut: false, Cancelled: false }
                                    && string.Equals(r.RevertFile, ProcessRunner.Sys("pnputil.exe"), StringComparison.OrdinalIgnoreCase);
                if (rebootPending)
                {
                    Log.Warn("Откат «{0}» вступит в силу после перезагрузки.", r.Title);
                    return new RevertOutcome(Loc.T("Вступит в силу после перезагрузки."), Skipped: true);
                }
                if (!result.Success) throw new InvalidOperationException(Loc.Tf("Команда отката завершилась с кодом {0}", result.ExitCode));
                return RevertOutcome.Done;

            // Device records: the writers re-validate against the live device (monitor present, mode offered, range…).
            case ChangeKind.UserParameter:
                SafetyLock.ThrowIfLocked(Loc.T("откат параметра Windows"));
                return await Task.Run(() => DeviceRestore.RestoreUserParameter(r)).ConfigureAwait(false);
            case ChangeKind.DisplayMode:
                SafetyLock.ThrowIfLocked(Loc.T("откат параметров экрана"));
                return await DisplaySafeApply.RestoreModeAsync(r).ConfigureAwait(false);
            case ChangeKind.DisplayHdr:
                SafetyLock.ThrowIfLocked(Loc.T("откат HDR"));
                return await DisplaySafeApply.RestoreHdrAsync(r).ConfigureAwait(false);
            case ChangeKind.MonitorSetting:
                SafetyLock.ThrowIfLocked(Loc.T("откат настройки монитора"));
                return await Task.Run(() => DeviceRestore.RestoreMonitor(r)).ConfigureAwait(false);
            case ChangeKind.AudioLevel:
                SafetyLock.ThrowIfLocked(Loc.T("откат уровня звука"));
                return await Task.Run(() => DeviceRestore.RestoreAudio(r)).ConfigureAwait(false);
            case ChangeKind.NicProperty:
                SafetyLock.ThrowIfLocked(Loc.T("откат свойства сетевого адаптера"));
                return await Task.Run(() => DeviceRestore.RestoreNic(r)).ConfigureAwait(false);

            default:
                throw new InvalidOperationException(Loc.T("Запись журнала не содержит данных для отката."));
        }
    }
}

/// <summary>Well-known exit codes of Windows tools.</summary>
public static class ProcessExit
{
    /// <summary>ERROR_SUCCESS_REBOOT_REQUIRED: done, takes effect after a restart (pnputil, msiexec, DISM).</summary>
    public const int RebootRequired = 3010;
}

/// <summary>
/// Validates journal records before they are replayed with admin rights. The journal is untrusted input
/// (CLAUDE.md §7): a record is accepted only when it has exactly the shape Lycoris's own code writes —
/// a known source, the kind and target that source writes, no protected or hard-blocked service, and for
/// commands an undo command line that is rebuilt from the same code and compared character for character.
/// </summary>
internal static class JournalTrust
{
    private static readonly HashSet<string> ModuleSources = new(StringComparer.Ordinal)
    {
        "services", "services-tasks", "startup", "startup-tasks", "gameboost", "drivers", "games", DeviceTargets.Source,
    };

    /// <summary>Kinds only the «Устройства и сеть» page writes (and the only kinds it writes).</summary>
    internal static bool IsDeviceKind(ChangeKind kind) => kind is ChangeKind.UserParameter or ChangeKind.DisplayMode or ChangeKind.DisplayHdr
        or ChangeKind.MonitorSetting or ChangeKind.AudioLevel or ChangeKind.NicProperty;

    private static readonly Regex TweakSource = new(@"^tweak:[a-z0-9][a-z0-9-]{0,80}$", RegexOptions.CultureInvariant);

    internal static bool IsKnownSource(string source) => ModuleSources.Contains(source) || TweakSource.IsMatch(source);

    /// <summary>Records of a tweak (or older merged records a tweak touched): only these may own plan copies to delete.</summary>
    internal static bool IsTweakish(ChangeRecord r) =>
        r.Source.StartsWith("tweak:", StringComparison.Ordinal) || r.AlsoSources.Any(s => s.StartsWith("tweak:", StringComparison.Ordinal));

    /// <summary>Null when the record may be reverted; otherwise a short Russian reason.</summary>
    internal static string? Check(ChangeRecord r)
    {
        if (!IsKnownSource(r.Source)) return Loc.Tf("неизвестный источник записи «{0}»", r.Source);
        foreach (var s in r.AlsoSources)
            if (!IsKnownSource(s)) return Loc.Tf("неизвестный источник записи «{0}»", s);
        var isTweak = r.Source.StartsWith("tweak:", StringComparison.Ordinal);

        // Each module writes exactly one kind of record; tweaks may write any kind.
        ChangeKind? expected = r.Source switch
        {
            "services" => ChangeKind.Service,
            "services-tasks" or "startup-tasks" => ChangeKind.ScheduledTask,
            "startup" or "games" => ChangeKind.Registry,
            // The game session switches the plan or, where no plan switch is possible, the Windows 11 AC power mode.
            "gameboost" => r.Kind == ChangeKind.Command ? ChangeKind.Command : ChangeKind.PowerScheme,
            "drivers" => ChangeKind.Command,
            _ => null,
        };
        if (expected is { } kind && r.Kind != kind) return Loc.Tf("источник «{0}» не создаёт записи типа {1}", r.Source, r.Kind);
        var deviceKind = IsDeviceKind(r.Kind);
        if (r.Source == DeviceTargets.Source && !deviceKind) return Loc.Tf("источник «{0}» не создаёт записи типа {1}", r.Source, r.Kind);
        if (deviceKind && (r.Source != DeviceTargets.Source || r.AlsoSources.Count > 0))
            return Loc.Tf("записи типа {0} создаёт только раздел «Устройства и сеть»", r.Kind);

        // An account is recorded only for changes in one account's profile.
        if (r.UserSid is { } sid)
        {
            if (!UserScope.SidPattern.IsMatch(sid)) return Loc.T("некорректная учётная запись в записи");
            if (!r.IsPerUser) return Loc.T("учётная запись указана для изменения всей системы");
        }

        switch (r.Kind)
        {
            case ChangeKind.Registry when r.Registry is { } reg:
                if (!string.Equals(r.Target, $@"reg:{reg.Hive}\{reg.Path}\{reg.Name}", StringComparison.OrdinalIgnoreCase))
                    return Loc.T("цель записи не совпадает с параметром реестра");
                if (reg.Hive == RegHive.HKCR) return Loc.Tf("раздел {0} Lycoris не меняет", reg.Hive);
                if (ChangeJournal.IsForbiddenRestoreTarget(reg)) return Loc.Tf("{0} — защищённое место реестра", reg.FullName);
                if (reg.CreatedKeyRoot is { } root)
                {
                    if (!RegistryHelper.IsValidCreatedRoot(reg.Path, root)) return Loc.T("некорректный раздел, созданный Lycoris");
                    if (ChangeJournal.IsForbiddenRestoreTarget(new RegistryValueSnapshot { Hive = reg.Hive, Path = root, Name = "" }))
                        return Loc.Tf("{0}\\{1} — защищённое место реестра", reg.Hive, root);
                }
                if (r.Source == "startup" && !IsStartupMark(reg)) return Loc.Tf("{0} — не отметка автозагрузки", reg.FullName);
                if (r.Source == "games" && !IsGpuPreference(reg)) return Loc.Tf("{0} — не выбор видеокарты для игры", reg.FullName);
                return null;

            case ChangeKind.Service when r.Service is { } svc:
                if (!string.Equals(r.Target, "svc:" + svc.Name, StringComparison.OrdinalIgnoreCase)) return Loc.T("цель записи не совпадает со службой");
                if (ServiceControl.IsProtected(svc.Name)) return Loc.Tf("служба {0} защищена от изменений", svc.Name);
                // Older builds could change these services (hard-blocked since): their records may still put back a
                // start type that lets the service run. Every other hard-blocked service is never touched.
                if (ServiceGuard.IsHardBlocked(svc.Name)
                    && !(LegacyRevertibleServices.Contains(svc.Name) && svc.StartMode != System.ServiceProcess.ServiceStartMode.Disabled))
                    return Loc.Tf("службу {0} Lycoris никогда не меняет", svc.Name);
                return null;

            case ChangeKind.ScheduledTask when r.TaskPath is { } task:
                if (!string.Equals(r.Target, "task:" + task, StringComparison.OrdinalIgnoreCase)) return Loc.T("цель записи не совпадает с задачей");
                if (!task.StartsWith('\\') || task.Any(char.IsControl)) return Loc.T("некорректный путь задачи");
                return null;

            case ChangeKind.PowerScheme:
                if (r.Target != "power:active") return Loc.T("некорректная цель записи плана питания");
                var created = new List<Guid>();
                if (r.PowerSchemeCreated is { } c) created.Add(c);
                if (r.PowerSchemesCreatedMore is { } more) created.AddRange(more);
                if (created.Count > 0)
                {
                    if (!IsTweakish(r)) return Loc.T("план питания на удаление указан не твиком");
                    if (created.Any(g => g == Guid.Empty || PowerSchemes.IsBuiltIn(g))) return Loc.T("встроенный план питания указан на удаление");
                }
                return null;

            case ChangeKind.Command:
                return CheckCommand(r, isTweak);

            case ChangeKind.UserParameter: return CheckSpi(r);
            case ChangeKind.DisplayMode: return CheckDisplayMode(r);
            case ChangeKind.DisplayHdr: return CheckDisplayHdr(r);
            case ChangeKind.MonitorSetting: return CheckMonitor(r);
            case ChangeKind.AudioLevel: return CheckAudio(r);
            case ChangeKind.NicProperty: return CheckNic(r);

            default:
                return null; // no data to revert: the system side reports that itself
        }
    }

    // ───────────────────────────── Device records ─────────────────────────────

    private static bool SameText(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Names are only shown, never used to find a device; still: short and printable.</summary>
    private static bool PlainName(string? name) => name is { Length: <= 200 } && !name.Any(char.IsControl);

    private static string? CheckSpi(ChangeRecord r)
    {
        if (r.Spi is not { } s) return Loc.T("в записи параметра нет данных");
        if (SpiCatalog.Find(s.Id) is not { } entry) return Loc.Tf("неизвестный параметр «{0}»", s.Id);
        if (!string.Equals(r.Target, DeviceTargets.Spi(s.Id), StringComparison.Ordinal)) return Loc.T("цель записи не совпадает с параметром");
        if (!entry.IsValidOriginal(s.Value)) return Loc.T("исходное значение параметра вне допустимого диапазона");
        if (r.UserSid is null) return Loc.T("для параметра пользователя не указана учётная запись");
        if ((s.Persisted is null) != (entry.Persisted is null)) return Loc.T("сохранённое значение параметра не совпадает с каталогом");
        if (s.Persisted is { } p && entry.Persisted is { } catalog)
        {
            if (p.Hive != RegHive.HKCU || !SameText(p.Path.TrimEnd('\\'), catalog.Path) || !SameText(p.Name, catalog.Name))
                return Loc.T("место сохранённого значения не совпадает с каталогом");
            if (p.CreatedKeyRoot is not null) return Loc.T("для параметра указан созданный раздел реестра");
            // Windows writes REG_SZ; a third-party tool may have left a DWORD: both are put back exactly as they were.
            if (p.ValueExisted && (!DeviceRestore.IsPersistedKind(p.Kind) || (p.Data?.Length ?? 0) > 64))
                return Loc.T("тип или размер сохранённого значения не тот");
            // Windows keeps the same number there as the live value (decimal text or a DWORD): anything else is not a value
            // Lycoris ever saw it write, so it is not put back.
            if (p.ValueExisted && (!int.TryParse(p.Data, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var stored)
                                   || !entry.IsValidOriginal(stored)))
                return Loc.T("сохранённое значение параметра вне допустимого диапазона");
        }
        return null;
    }

    private static string? CheckDisplayMode(ChangeRecord r)
    {
        if (r.DisplayMode is not { Original: not null, Target: not null } d) return Loc.T("в записи режима экрана нет данных");
        if (!DeviceTargets.IsMonitorPath(d.MonitorPath)) return Loc.T("некорректный путь монитора");
        if (!SameText(r.Target, DeviceTargets.DisplayMode(d.MonitorPath))) return Loc.T("цель записи не совпадает с монитором");
        if (!d.Original.IsSane || !d.Target.IsSane) return Loc.T("недопустимый режим экрана");
        if (!PlainName(d.MonitorName)) return Loc.T("некорректное имя монитора");
        return r.UserSid is null ? null : Loc.T("учётная запись указана для изменения всей системы");
    }

    private static string? CheckDisplayHdr(ChangeRecord r)
    {
        if (r.DisplayHdr is not { } h) return Loc.T("в записи HDR нет данных");
        if (!DeviceTargets.IsMonitorPath(h.MonitorPath)) return Loc.T("некорректный путь монитора");
        if (!SameText(r.Target, DeviceTargets.DisplayHdr(h.MonitorPath))) return Loc.T("цель записи не совпадает с монитором");
        if (h.ModeBefore is { IsSane: false }) return Loc.T("недопустимый режим экрана");
        if (!PlainName(h.MonitorName)) return Loc.T("некорректное имя монитора");
        return r.UserSid is null ? null : Loc.T("учётная запись указана для изменения всей системы");
    }

    private static string? CheckMonitor(ChangeRecord r)
    {
        if (r.Monitor is not { } m) return Loc.T("в записи монитора нет данных");
        if (m.Code is not (MonitorVcp.Brightness or MonitorVcp.Contrast)) return Loc.T("Lycoris меняет по DDC/CI только яркость и контраст");
        // VCP values are 16-bit: the whole range a real monitor can report is accepted.
        if (m.Max is < 1 or > 65535 || m.Value > m.Max || m.Min > m.Value) return Loc.T("значение вне диапазона монитора");
        if (!DeviceTargets.IsMonitorPath(m.MonitorPath)) return Loc.T("некорректный путь монитора");
        if (!SameText(r.Target, DeviceTargets.Monitor(m.MonitorPath, m.Code))) return Loc.T("цель записи не совпадает с монитором");
        if (!PlainName(m.MonitorName)) return Loc.T("некорректное имя монитора");
        return r.UserSid is null ? null : Loc.T("учётная запись указана для изменения всей системы");
    }

    private static string? CheckAudio(ChangeRecord r)
    {
        if (r.Audio is not { } a) return Loc.T("в записи звука нет данных");
        if (!DeviceTargets.IsCaptureEndpointId(a.EndpointId)) return Loc.T("Lycoris меняет уровень только у устройств записи");
        if (a.Component is not (AudioComponent.Level or AudioComponent.Mute)) return Loc.T("неизвестная часть настройки звука");
        var scalar = a.Scalar;
        if (!float.IsFinite(scalar) || scalar < 0f || scalar > 1f) return Loc.T("уровень вне диапазона 0–100 %");
        if (!SameText(r.Target, DeviceTargets.Audio(a.EndpointId, a.Component))) return Loc.T("цель записи не совпадает с устройством");
        if (!PlainName(a.EndpointName)) return Loc.T("некорректное имя устройства");
        return r.UserSid is null ? null : Loc.T("учётная запись указана для изменения всей системы");
    }

    private static string? CheckNic(ChangeRecord r)
    {
        if (r.Nic is not { } n) return Loc.T("в записи сетевого адаптера нет данных");
        if (n.InterfaceGuid == Guid.Empty) return Loc.T("не указан сетевой адаптер");
        if (!DeviceTargets.IsNicKeyword(n.Keyword)) return Loc.Tf("свойство «{0}» Lycoris не меняет", n.Keyword);
        if ((n.Value is not null && !DeviceTargets.IsNicValue(n.Value)) || (n.DefaultValue is not null && !DeviceTargets.IsNicValue(n.DefaultValue)))
            return Loc.T("значение свойства не число");
        if (n.RestoreValue is null) return Loc.T("в записи нет прежнего значения");
        if (!SameText(r.Target, DeviceTargets.NicProperty(n.InterfaceGuid, n.Keyword))) return Loc.T("цель записи не совпадает с адаптером");
        if (!PlainName(n.AdapterName)) return Loc.T("некорректное имя адаптера");
        return r.UserSid is null ? null : Loc.T("учётная запись указана для изменения всей системы");
    }

    private const string StartupApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>Packaged-app startup tasks: HKCU …\AppModel\SystemAppData\&lt;PackageFamilyName&gt;\&lt;TaskId&gt;, value State (DWORD).</summary>
    private static readonly Regex PackagedStartupTask = new(
        @"^Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\SystemAppData\\" +
        @"[A-Za-z0-9.\-]{3,50}_[a-z0-9]{13}\\[A-Za-z0-9._\-]{1,128}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private const string GpuPreferencesPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private static readonly Regex ExePath = new(@"^[A-Za-z]:\\[^\x00-\x1F""<>|*?]+\.exe$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The marks the startup page writes: StartupApproved (Run, Run32, StartupFolder) or a packaged app's startup task state.</summary>
    private static bool IsStartupMark(RegistryValueSnapshot reg)
    {
        var p = reg.Path.TrimEnd('\\');
        if (new[] { "Run", "Run32", "StartupFolder" }.Any(sub => string.Equals(p, StartupApprovedRoot + sub, StringComparison.OrdinalIgnoreCase)))
            return true;
        return IsPackagedStartupMark(reg);
    }

    /// <summary>
    /// A packaged app's startup task state as the startup page writes it: the value always existed (Lycoris never
    /// creates it) and the original is one of the user states 0–2 (3/4 are set by policy and never touched).
    /// </summary>
    internal static bool IsPackagedStartupMark(RegistryValueSnapshot reg) =>
        reg.Hive == RegHive.HKCU && PackagedStartupTask.IsMatch(reg.Path.TrimEnd('\\')) && reg.Name == "State"
        && reg.ValueExisted && reg.Kind == Microsoft.Win32.RegistryValueKind.DWord
        && uint.TryParse(reg.Data, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) && v <= 2;

    /// <summary>The games page's per-game GPU choice: HKCU …\DirectX\UserGpuPreferences, a value named by the game's exe path (REG_SZ).</summary>
    private static bool IsGpuPreference(RegistryValueSnapshot reg) =>
        reg.Hive == RegHive.HKCU
        && string.Equals(reg.Path.TrimEnd('\\'), GpuPreferencesPath, StringComparison.OrdinalIgnoreCase)
        && ExePath.IsMatch(reg.Name)
        && (!reg.ValueExisted || reg.Kind == Microsoft.Win32.RegistryValueKind.String);

    // ───────────────────────────── Undo commands ─────────────────────────────

    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
    private const string PsPrefix = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";
    private const string CmdHead = "/d /s /c \"";
    private static readonly string Unknown = Loc.T("команда отката не совпадает ни с одной командой Lycoris");

    /// <summary>Services that older builds could change in ВСЕ СЛУЖБЫ and that are hard-blocked now.</summary>
    private static readonly HashSet<string> LegacyRevertibleServices =
        new(["Eaphost", "dot3svc", "NcaSvc", "RmSvc", "W32Time", "tzautoupdate"], StringComparer.OrdinalIgnoreCase);

    private static string Quoted(string exe) => "\"" + ProcessRunner.Sys(exe) + "\"";
    private static bool SameFile(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? CheckCommand(ChangeRecord r, bool isTweak)
    {
        var file = r.RevertFile ?? "";
        var args = r.RevertArgs ?? "";
        if (!r.Target.StartsWith("cmd:", StringComparison.Ordinal)) return Loc.T("некорректная цель команды");
        var target = r.Target[4..];

        // Drivers → enable a device; its undo is pnputil /disable-device "<the same instance ID>".
        if (target.StartsWith("device-enable:", StringComparison.Ordinal))
        {
            var id = target["device-enable:".Length..];
            if (r.Source != "drivers" || id.Length == 0 || id.Contains('"') || id.Any(char.IsControl)) return Unknown;
            return SameFile(file, ProcessRunner.Sys("pnputil.exe")) && args == $"/disable-device \"{id}\"" ? null : Unknown;
        }
        // Windows 11 power mode overlay: PowerShell that sets one of the three documented modes. The AC mode is written by the
        // power-mode tweaks and by the game session (source "gameboost", for the duration of the game); the DC mode by tweaks only.
        if (target is "powermode:ac" or "powermode:dc")
        {
            var dc = target == "powermode:dc";
            if (!isTweak && (dc || r.Source != "gameboost")) return Unknown;
            if (!SameFile(file, TweakCatalog.PowerShellExe) || PowerModeUndo(r) is not { } pm || pm.Dc != dc) return Unknown;
            return TweakCatalog.PowerShellArgs(TweakCatalog.PowerModeScript(pm.Mode, dc)) == args ? null : Unknown;
        }
        if (!isTweak) return Unknown;

        // PowerShell undo scripts: decode, read the original value back out, rebuild with the catalog code, compare.
        if (target.StartsWith("dx:", StringComparison.Ordinal) || target == "spi:visual-effects")
        {
            if (!SameFile(file, TweakCatalog.PowerShellExe) || PsScript(args) is not { } script) return Unknown;
            var rebuilt = target == "spi:visual-effects" ? RebuildSpi(script) : RebuildDx(target[3..], script);
            return rebuilt == args ? null : Unknown;
        }

        // TCP receive-window auto-tuning: netsh with one of the five documented levels, nothing else.
        if (target == "tcp-autotuning")
            return SameFile(file, ProcessRunner.Sys("netsh.exe")) && TweakCatalog.TcpAutotuningLevels.Any(l => args == TweakCatalog.TcpAutotuningArgs(l))
                ? null : Unknown;

        var dns = Regex.Match(target, $@"^dns([46]):(\{{?{GuidPattern}\}}?)$");
        if (dns.Success)
        {
            if (UpgradeLegacyDns(r) is not null) return null;
            if (!SameFile(file, TweakCatalog.PowerShellExe) || PsScript(args) is not { } dnsScript) return Unknown;
            var rebuilt = RebuildDns(dns.Groups[1].Value == "6" ? "ipv6" : "ipv4", dns.Groups[2].Value, dnsScript);
            return rebuilt == args ? null : Unknown;
        }

        // cmd.exe undo lines: only the exact powercfg / hibernate lines the catalog writes.
        if (!SameFile(file, ProcessRunner.Sys("cmd.exe"))) return Unknown;
        if (!args.StartsWith(CmdHead, StringComparison.Ordinal) || !args.EndsWith('"') || args.Length <= CmdHead.Length) return Unknown;
        var inner = args[CmdHead.Length..^1];
        var powercfg = Quoted("powercfg.exe");

        if (target == "hibernate")
            return inner == $"{powercfg} /hibernate on" || inner == $"{powercfg} /hibernate on && {powercfg} /hibernate /type reduced"
                ? null : Unknown;

        // A plan setting's AC or DC value: the undo line must write the same side the target names, for the same scheme and setting.
        var pc = PowercfgTargetPattern.Match("cmd:" + target);
        if (pc.Success)
        {
            var m = PowercfgUndoLine(inner, dc: pc.Groups[3].Value == "dc");
            return m is { Success: true }
                   && string.Equals(m.Groups[1].Value, pc.Groups[1].Value, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(m.Groups[3].Value, pc.Groups[2].Value, StringComparison.OrdinalIgnoreCase)
                ? null : Unknown;
        }

        return Unknown;
    }

    /// <summary>The exact undo line of a plan setting: powercfg /setacvalueindex (AC) or /setdcvalueindex (DC), then /setactive.</summary>
    private static Match PowercfgUndoLine(string inner, bool dc)
    {
        var exe = Regex.Escape(Quoted("powercfg.exe"));
        var verb = dc ? "/setdcvalueindex" : "/setacvalueindex";
        return Regex.Match(inner,
            $"^{exe} {verb} ({GuidPattern}) ({GuidPattern}) ({GuidPattern}) ([0-9]{{1,10}}) && {exe} /setactive SCHEME_CURRENT$");
    }

    /// <summary>"cmd:powercfg:{scheme}:{setting}:ac|dc": groups 1 scheme, 2 setting, 3 side.</summary>
    private static readonly Regex PowercfgTargetPattern =
        new($"^cmd:powercfg:({GuidPattern}):({GuidPattern}):(ac|dc)$", RegexOptions.CultureInvariant);

    /// <summary>"cmd:powercfg:{scheme}:{setting}:ac|dc" → the scheme and setting, or null for any other target.</summary>
    internal static (Guid Scheme, Guid Setting)? PowercfgTarget(string target)
    {
        var m = PowercfgTargetPattern.Match(target);
        return m.Success && Guid.TryParse(m.Groups[1].Value, out var scheme) && Guid.TryParse(m.Groups[2].Value, out var setting)
            ? (scheme, setting) : null;
    }

    /// <summary>Which side a plan-setting record changed: true = DC (on battery), false = AC, null for any other target.</summary>
    internal static bool? PowercfgIsDc(string target)
    {
        var m = PowercfgTargetPattern.Match(target);
        return m.Success ? m.Groups[3].Value == "dc" : null;
    }

    /// <summary>The power setting and the value (of the side the target names) a powercfg undo record writes back (null for other records).</summary>
    internal static (Guid Scheme, Guid Subgroup, Guid Setting, uint Value)? PowercfgUndo(ChangeRecord r)
    {
        if (r.Kind != ChangeKind.Command || PowercfgIsDc(r.Target) is not { } dc) return null;
        var args = r.RevertArgs ?? "";
        if (!args.StartsWith(CmdHead, StringComparison.Ordinal) || !args.EndsWith('"') || args.Length <= CmdHead.Length) return null;
        var m = PowercfgUndoLine(args[CmdHead.Length..^1], dc);
        if (!m.Success) return null;
        return Guid.TryParse(m.Groups[1].Value, out var scheme) && Guid.TryParse(m.Groups[2].Value, out var sub)
               && Guid.TryParse(m.Groups[3].Value, out var setting)
               && uint.TryParse(m.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? (scheme, sub, setting, value) : null;
    }

    /// <summary>
    /// The side (AC / DC) and the documented mode GUID a power-mode undo record puts back (parsed from the «$g=[Guid]'…';» of its
    /// script), or null for any other record or an undocumented GUID. Pure: no Windows calls.
    /// </summary>
    internal static (bool Dc, Guid Mode)? PowerModeUndo(ChangeRecord r)
    {
        if (r.Kind != ChangeKind.Command || r.Target is not ("cmd:powermode:ac" or "cmd:powermode:dc")) return null;
        if (PsScript(r.RevertArgs ?? "") is not { } script) return null;
        var g = Regex.Match(script, $@"\$g=\[Guid\]'({GuidPattern})';");
        if (!g.Success || !Guid.TryParse(g.Groups[1].Value, out var mode) || !TweakCatalog.IsDocumentedPowerMode(mode)) return null;
        return (r.Target == "cmd:powermode:dc", mode);
    }

    /// <summary>The adapter and the DNS servers a DNS undo record puts back (empty = automatic), or null for other records.</summary>
    internal static (bool Ipv6, string Adapter, IReadOnlyList<string> Servers)? DnsUndo(ChangeRecord r)
    {
        if (r.Kind != ChangeKind.Command || !r.Target.StartsWith("cmd:", StringComparison.Ordinal)) return null;
        var dns = Regex.Match(r.Target[4..], $@"^dns([46]):(\{{?{GuidPattern}\}}?)$");
        if (!dns.Success) return null;
        var args = UpgradeLegacyDns(r)?.Args ?? r.RevertArgs ?? "";
        if (PsScript(args) is not { } script) return null;
        var servers = Regex.Matches(script, "'address=([0-9A-Fa-f:.%]+)'").Select(m => m.Groups[1].Value).ToList();
        return (dns.Groups[1].Value == "6", dns.Groups[2].Value, servers);
    }

    /// <summary>The DirectX token and the value a dx undo record puts back (null value = token removed), or null for other records.</summary>
    internal static (string Token, string? Value)? DxUndo(ChangeRecord r)
    {
        if (r.Kind != ChangeKind.Command || !r.Target.StartsWith("cmd:dx:", StringComparison.Ordinal)) return null;
        if (PsScript(r.RevertArgs ?? "") is not { } script) return null;
        var m = Regex.Match(script, @"\$v=(\$null|'(?:[^']|'')*');");
        if (!m.Success) return null;
        var raw = m.Groups[1].Value;
        return (r.Target["cmd:dx:".Length..], raw == "$null" ? null : raw[1..^1].Replace("''", "'"));
    }

    private static string? PsScript(string args)
    {
        if (!args.StartsWith(PsPrefix, StringComparison.Ordinal)) return null;
        try { return Encoding.Unicode.GetString(Convert.FromBase64String(args[PsPrefix.Length..])); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// A DNS undo record written by an older build (cmd.exe + netsh by interface index), rebuilt character for
    /// character with that build's code to prove it is genuine. Returns the equivalent current command (PowerShell,
    /// adapter found by GUID at run time), or null when the record is not such a legacy record.
    /// </summary>
    internal static (string File, string Args)? UpgradeLegacyDns(ChangeRecord r)
    {
        if (r.Kind != ChangeKind.Command || !r.Target.StartsWith("cmd:", StringComparison.Ordinal)) return null;
        var dns = Regex.Match(r.Target[4..], $@"^dns([46]):(\{{?{GuidPattern}\}}?)$");
        if (!dns.Success || !SameFile(r.RevertFile, ProcessRunner.Sys("cmd.exe"))) return null;
        var args = r.RevertArgs ?? "";
        if (!args.StartsWith(CmdHead, StringComparison.Ordinal) || !args.EndsWith('"') || args.Length <= CmdHead.Length) return null;
        var inner = args[CmdHead.Length..^1];
        var family = dns.Groups[1].Value == "6" ? "ipv6" : "ipv4";
        var flush = " && " + Quoted("ipconfig.exe") + " /flushdns";
        if (!inner.EndsWith(flush, StringComparison.Ordinal)) return null;
        var body = inner[..^flush.Length];
        var first = Regex.Match(body, $@"interface {family} set dnsservers name=([0-9]{{1,9}}) source=(?:dhcp|static address=([0-9A-Fa-f:.%]+) register=primary)");
        if (!first.Success) return null;
        var index = int.Parse(first.Groups[1].Value, CultureInfo.InvariantCulture);
        var servers = new List<string>();
        if (first.Groups[2].Success) servers.Add(first.Groups[2].Value);
        foreach (Match m in Regex.Matches(body, "add dnsservers name=[0-9]{1,9} address=([0-9A-Fa-f:.%]+) index="))
            servers.Add(m.Groups[1].Value);
        try
        {
            if (LegacyDnsCommand(family, index, servers) != body) return null;
            return (TweakCatalog.PowerShellExe, TweakCatalog.PowerShellArgs(TweakCatalog.DnsScript(family, dns.Groups[2].Value, servers)));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return null; }
    }

    /// <summary>The netsh line older builds wrote (TweakCatalog.DnsCommand before the GUID-based script).</summary>
    private static string LegacyDnsCommand(string family, int index, IReadOnlyList<string> servers)
    {
        var netsh = Quoted("netsh.exe");
        if (servers.Count == 0) return $"{netsh} interface {family} set dnsservers name={index} source=dhcp";
        var addresses = servers.Select(s => System.Net.IPAddress.TryParse(s, out var ip)
            ? ip.ToString()
            : throw new InvalidOperationException(Loc.Tf("некорректный адрес DNS-сервера: {0}", s))).ToList();
        return string.Join(" && ", addresses.Select((s, i) => i == 0
            ? $"{netsh} interface {family} set dnsservers name={index} source=static address={s} register=primary validate=no"
            : $"{netsh} interface {family} add dnsservers name={index} address={s} index={i + 1} validate=no"));
    }

    /// <summary>Reads the server list back out of a DNS undo script and rebuilds it with the catalog code.</summary>
    private static string? RebuildDns(string family, string guid, string script)
    {
        var servers = new List<string>();
        foreach (Match m in Regex.Matches(script, "'address=([0-9A-Fa-f:.%]+)'"))
            servers.Add(m.Groups[1].Value);
        var keep = script.Contains("Get-DnsClient -InterfaceIndex $i", StringComparison.Ordinal);
        try { return TweakCatalog.PowerShellArgs(TweakCatalog.DnsScript(family, guid, servers, keepRegistration: keep)); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return null; }
    }

    private static string? RebuildDx(string token, string script)
    {
        if (!Regex.IsMatch(token, "^[A-Za-z0-9_]{1,64}$")) return null;
        var m = Regex.Match(script, @"\$v=(\$null|'(?:[^']|'')*');");
        if (!m.Success) return null;
        var raw = m.Groups[1].Value;
        var value = raw == "$null" ? null : raw[1..^1].Replace("''", "'");
        var own = Regex.Match(script, @"\$mk=\$true;\$mp=\$(true|false);");
        return TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript(token, value,
            createdKey: own.Success, createdParent: own.Success && own.Groups[1].Value == "true"));
    }

    private static string? RebuildSpi(string script)
    {
        var states = new List<(uint SetAction, bool On)>();
        foreach (var f in TweakCatalog.VisualEffectFlags)
        {
            var m = Regex.Match(script, $@"\[Monolith\.Spi\]::Set\(0x{f.Set:X},0,\[IntPtr\]([01]),3\)");
            if (!m.Success) return null;
            states.Add((f.Set, m.Groups[1].Value == "1"));
        }
        var anim = Regex.Match(script, @"SetAnimation\(0x49,8,\[int\[\]\]@\(8,([0-9]{1,6})\),3\)");
        if (!anim.Success) return null;
        var minAnimate = int.Parse(anim.Groups[1].Value, CultureInfo.InvariantCulture);
        return TweakCatalog.PowerShellArgs(TweakCatalog.SpiScript(states, minAnimate));
    }
}
