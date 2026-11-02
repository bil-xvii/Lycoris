
namespace Monolith.Core;

/// <summary>
/// What kind of work an operation is. The class decides what may run next to it (<see cref="OperationCoordinator.Conflicts"/>)
/// and what happens to a second operation that cannot start now (queue, refuse or wait quietly).
/// </summary>
public enum OperationClass
{
    /// <summary>winget / vendor / driver / Windows Update installers and driver package operations. Queued (FIFO).</summary>
    Install,
    /// <summary>DISM, SFC, chkdsk, defrag, network reset, store/icon cache reset. Refused at once when busy.</summary>
    SystemRepair,
    /// <summary>Apply / revert of tweaks, services, tasks, startup entries, SPI — everything that writes the undo journal. Queued (FIFO).</summary>
    JournalChange,
    /// <summary>NIC restart, display mode, Game Boost start/stop, restore point creation, heavy network tests. Refused at once when busy.</summary>
    Disruptive,
    /// <summary>Deleting files (cleaner). Queued (FIFO).</summary>
    Cleanup,
    /// <summary>Read-only measurements and analyses. Never block anything; capped in parallel; refused only when <see cref="OperationRequest.Quiet"/>.</summary>
    Measure,
    /// <summary>Warm-ups and background loads. Lowest priority: wait quietly for a free slot.</summary>
    Background,
}

public enum OperationState { Queued, Running, Cancelling }

/// <summary>What <see cref="OperationCoordinator.Check"/> says about starting an operation right now.</summary>
public enum OperationVerdict { Start, Queue, Refuse }

/// <summary>A request to run one operation. <see cref="Name"/> is shown to the user («Установка Steam»).</summary>
public sealed record OperationRequest(OperationClass Class, string Name)
{
    /// <summary>Page key (or service name) that started it — informational.</summary>
    public string? Owner { get; init; }

    /// <summary>
    /// A measurement that needs a quiet system (ping, latency, energy report): it does not start while an installer,
    /// a repair or a disruptive change runs, and those do not start while it runs.
    /// </summary>
    public bool Quiet { get; init; }

    /// <summary>Wait in the visible queue instead of being refused — for actions that must not be dropped (stopping a game session).</summary>
    public bool QueueIfBusy { get; init; }

    /// <summary>The work watches <see cref="OperationLease.Token"/> and can be cancelled safely from the task list.</summary>
    public bool Cancellable { get; init; }

    /// <summary>Longest wait in the queue; null = the default of the class.</summary>
    public TimeSpan? QueueTimeout { get; init; }
}

/// <summary>A running or queued operation as shown in the task list (immutable snapshot).</summary>
public sealed record OperationInfo(
    long Id, string Name, OperationClass Class, OperationState State, string? Owner, string? WaitingFor,
    string? Detail, bool CanCancel, DateTimeOffset Since)
{
    /// <summary>Measurements and warm-ups are not listed (they are short and never block the user).</summary>
    public bool IsListed => Class is not (OperationClass.Measure or OperationClass.Background);
    public string ClassText => OperationCoordinator.ClassLabel(Class);
}

/// <summary>The operation cannot start now and must not wait; <see cref="Exception.Message"/> is the plain Russian reason.</summary>
public sealed class OperationRefusedException(string message, string? blocker) : Exception(message)
{
    public string? Blocker { get; } = blocker;
}

/// <summary>The operation waited in the queue for longer than its limit.</summary>
public sealed class OperationTimeoutException(string message) : Exception(message);

/// <summary>
/// The permission to run one operation. Dispose releases it (also on exceptions). A nested lease (the same logical flow
/// asked again while it holds a lease) costs nothing and conflicts with nothing.
/// </summary>
public sealed class OperationLease : IDisposable
{
    private readonly OperationCoordinator? _owner;
    private OperationCoordinator.Op? _op;
    private readonly CancellationTokenSource? _cts;
    private int _disposed;

    internal OperationLease(OperationCoordinator? owner, OperationCoordinator.Op? op, CancellationTokenSource? cts, OperationLease? parent)
    {
        _owner = owner;
        _op = op;
        _cts = cts;
        Parent = parent;
    }

    internal OperationLease? Parent { get; }

    /// <summary>True when this flow already held a lease: nothing was acquired, nothing is released.</summary>
    public bool IsNested => _owner is null;

    /// <summary>Stays valid until the lease is disposed — a context captured by a detached task cannot reuse it.</summary>
    public bool IsActive => Volatile.Read(ref _disposed) == 0 && (IsNested ? Parent?.IsActive ?? false : _op is not null);

    public OperationClass Class => _op?.Request.Class ?? Parent?.Class ?? OperationClass.Background;
    public long Id => _op?.Id ?? Parent?.Id ?? 0;

    /// <summary>Cancelled when the user presses ОТМЕНИТЬ in the task list (only for cancellable work) — observe it.</summary>
    public CancellationToken Token => _cts?.Token ?? Parent?.Token ?? CancellationToken.None;

    /// <summary>Registers how this operation is cancelled safely (stop after the current item, kill nothing). Null = not cancellable.</summary>
    public void SetCancelHandler(Action? onCancel)
    {
        var op = _op ?? Parent?._op;
        if (op is null || _owner is null && Parent is null) return;
        op.CancelHandler = onCancel;
        _owner?.RaiseChanged();
    }

    /// <summary>A short progress text for the task list («3 / 7»).</summary>
    public void Report(string? detail)
    {
        var op = _op;
        if (op is null || _owner is null) return;
        op.Detail = detail;
        _owner.RaiseChanged();
    }

    /// <summary>
    /// Makes this lease the current one of the calling flow (async-local), so operations started below it are nested.
    /// Call it in the method that runs the work; dispose to restore.
    /// </summary>
    public IDisposable Scope() => OperationCoordinator.EnterScope(this);

    /// <summary>
    /// Runs <paramref name="ask"/> (a modal question to the user) WITHOUT holding the lease, then takes it back first in line.
    /// Never hold a lease while a dialog waits: use it only before the operation has changed anything.
    /// </summary>
    public async Task<T> WhileUserDecidesAsync<T>(Func<Task<T>> ask, CancellationToken ct = default)
    {
        if (_owner is null || _op is not { } current) return await ask().ConfigureAwait(false);
        _owner.ReleaseForUser(current);
        try
        {
            return await ask().ConfigureAwait(false);
        }
        finally
        {
            _op = await _owner.ReacquireAsync(current, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var op = _op;
        _op = null;
        if (_owner is not null && op is not null) _owner.Release(op);
        _cts?.Dispose();
    }
}

/// <summary>
/// The one place that decides which long or system-changing operations may run together.
/// Every such operation takes a lease first (<see cref="RunAsync{T}"/> / <see cref="AcquireAsync"/>). Conflicting operations
/// never run together: the second one waits in a visible FIFO queue (Install, JournalChange, Cleanup) or is refused at once
/// with a plain Russian reason. Measurements and warm-ups never block anything and are capped in parallel.
/// <para>
/// Deadlock safety: admission is all-or-nothing under one lock (no hold-and-wait), a flow that already holds a lease may start
/// nested operations without waiting (async-local, like the journal gate), queue waits have timeouts and cancellation,
/// leases are released in <c>finally</c>, and no lease is held while a dialog waits for the user (<see cref="OperationLease.WhileUserDecidesAsync"/>).
/// </para>
/// </summary>
public sealed class OperationCoordinator
{
    /// <summary>The application's coordinator.</summary>
    public static OperationCoordinator Instance { get; } = new();

    /// <summary>At most this many measurements / warm-ups run at once (each spawns WMI / PowerShell / child processes).</summary>
    public const int DefaultLowPriorityCap = 3;

    private readonly object _lock = new();
    private readonly List<Op> _running = [];
    private readonly List<Op> _queued = [];
    private readonly TimeProvider _time;
    private readonly int _lowCap;
    private long _nextId;
    private volatile bool _closing;

    private static readonly AsyncLocal<OperationLease?> CurrentLease = new();

    public OperationCoordinator(TimeProvider? time = null, int lowPriorityCap = DefaultLowPriorityCap)
    {
        _time = time ?? TimeProvider.System;
        _lowCap = Math.Max(1, lowPriorityCap);
    }

    /// <summary>Raised (from any thread, outside the lock) whenever the list of operations or one of its entries changed.</summary>
    public event Action? Changed;

    internal void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* a faulty listener must never break scheduling */ }
    }

    /// <summary>While true no new user operation is admitted (the window is closing).</summary>
    public bool IsClosing => _closing;

    // ───────────────────────────── Matrix ─────────────────────────────

    /// <summary>
    /// The compatibility matrix (symmetric). Install and SystemRepair conflict with everything that changes the system;
    /// JournalChange and Disruptive conflict with each other and with those two; Cleanup conflicts with Install, SystemRepair
    /// and itself; Measure and Background conflict with nothing (except a Quiet measurement, see <see cref="Conflicts(Op, Op)"/>).
    /// </summary>
    public static bool ClassesConflict(OperationClass a, OperationClass b)
    {
        if (a is OperationClass.Measure or OperationClass.Background || b is OperationClass.Measure or OperationClass.Background) return false;
        if (a > b) (a, b) = (b, a);
        return (a, b) switch
        {
            (OperationClass.Install, _) => true,
            (OperationClass.SystemRepair, _) => true,
            (OperationClass.JournalChange, OperationClass.JournalChange) => true,
            (OperationClass.JournalChange, OperationClass.Disruptive) => true,
            (OperationClass.Disruptive, OperationClass.Disruptive) => true,
            (OperationClass.Cleanup, OperationClass.Cleanup) => true,
            _ => false,
        };
    }

    private static bool IsHeavy(OperationClass c) => c is OperationClass.Install or OperationClass.SystemRepair or OperationClass.Disruptive;

    /// <summary>Do two requests exclude each other? The class matrix plus the rule for Quiet measurements.</summary>
    public static bool Conflicts(OperationRequest a, OperationRequest b)
    {
        if (ClassesConflict(a.Class, b.Class)) return true;
        return (a.Quiet && IsHeavy(b.Class)) || (b.Quiet && IsHeavy(a.Class));
    }

    internal static bool Conflicts(Op a, Op b) => Conflicts(a.Request, b.Request);

    private static bool IsLow(OperationClass c) => c is OperationClass.Measure or OperationClass.Background;

    public static string ClassLabel(OperationClass c) => c switch
    {
        OperationClass.Install => "УСТАНОВКА",
        OperationClass.SystemRepair => "РЕМОНТ",
        OperationClass.JournalChange => "ИЗМЕНЕНИЯ",
        OperationClass.Disruptive => "СИСТЕМА",
        OperationClass.Cleanup => "ОЧИСТКА",
        OperationClass.Measure => "ЗАМЕР",
        _ => "ФОН",
    };

    /// <summary>Default longest wait in the queue for a class.</summary>
    public static TimeSpan DefaultQueueTimeout(OperationClass c) => c switch
    {
        OperationClass.Install => TimeSpan.FromHours(4),
        OperationClass.Cleanup => TimeSpan.FromMinutes(60),
        OperationClass.JournalChange => TimeSpan.FromMinutes(30),
        OperationClass.Measure => TimeSpan.FromMinutes(2),
        OperationClass.Background => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(30),
    };

    private static bool Queues(OperationRequest r) =>
        r.QueueIfBusy || r.Class is OperationClass.Install or OperationClass.JournalChange or OperationClass.Cleanup or OperationClass.Background;

    /// <summary>«Установка Steam» → «установка Steam»; acronyms («DISM …») stay as they are.</summary>
    public static string Lower1(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.Length > 1 && (char.IsUpper(name[1]) || char.IsDigit(name[1]))) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string RefusalText(Op blocker, bool queuedBlocker) =>
        queuedBlocker
            ? $"Сначала дождитесь очереди: «{blocker.Request.Name}» ждёт своего времени."
            : blocker.Request.Quiet || blocker.Request.Class == OperationClass.Measure
                ? $"Сейчас идёт {Lower1(blocker.Request.Name)} — повторите, когда оно закончится."
                : $"Сейчас идёт {Lower1(blocker.Request.Name)} — дождитесь окончания.";

    // ───────────────────────────── Admission ─────────────────────────────

    internal sealed class Op
    {
        public required long Id { get; init; }
        public required OperationRequest Request { get; init; }
        public OperationState State;
        public DateTimeOffset Since;
        public Op? Blocker;
        public string? Detail;
        public Action? CancelHandler;
        public bool Resume;
        public TaskCompletionSource<bool>? Grant;
        public CancellationTokenSource? Cts;
        public bool CancelRequestedByUser;
        public long Order;
    }

    /// <summary>Under the lock: the first running operation that excludes <paramref name="op"/>, or a queued earlier one, or the low-priority cap.</summary>
    private Op? FindBlocker(Op op, out bool queuedBlocker, out bool capBlocked)
    {
        queuedBlocker = false;
        capBlocked = false;
        foreach (var r in _running)
            if (!ReferenceEquals(r, op) && Conflicts(r, op)) return r;
        foreach (var q in _queued)
        {
            if (ReferenceEquals(q, op) || q.Order >= op.Order) continue;
            if (Conflicts(q, op)) { queuedBlocker = true; return q; }
        }
        if (IsLow(op.Request.Class) && _running.Count(r => IsLow(r.Request.Class)) >= _lowCap)
            capBlocked = true;
        return null;
    }

    /// <summary>
    /// What would happen to a request right now — without taking anything. Pages use it to show the reason before they
    /// ask the user to confirm something that cannot run at the moment.
    /// </summary>
    public (OperationVerdict Verdict, string? Reason) Check(OperationRequest request)
    {
        if (CurrentLease.Value is { IsActive: true } parent && IsNestedAllowed(parent, request)) return (OperationVerdict.Start, null);
        var probe = new Op { Id = -1, Request = request, Order = long.MaxValue };
        lock (_lock)
        {
            if (_closing && !IsLow(request.Class)) return (OperationVerdict.Refuse, "MONOLITH закрывается — новые задачи не запускаются.");
            var blocker = FindBlocker(probe, out var queuedBlocker, out var cap);
            if (blocker is null && !cap) return (OperationVerdict.Start, null);
            if (Queues(request) || cap) return (OperationVerdict.Queue, blocker is null ? null : $"ждёт: {blocker.Request.Name}");
            return (OperationVerdict.Refuse, RefusalText(blocker!, queuedBlocker));
        }
    }

    private static bool IsNestedAllowed(OperationLease parent, OperationRequest child) =>
        !IsLow(parent.Class) || IsLow(child.Class);

    /// <summary>
    /// Takes a lease: returns at once when nothing conflicts; otherwise waits in the FIFO queue (Install, JournalChange,
    /// Cleanup, Background, <see cref="OperationRequest.QueueIfBusy"/>) or throws <see cref="OperationRefusedException"/>.
    /// Throws <see cref="OperationTimeoutException"/> after the queue limit and <see cref="OperationCanceledException"/> when cancelled.
    /// The lease must be disposed.
    /// </summary>
    public async Task<OperationLease> AcquireAsync(OperationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CurrentLease.Value is { IsActive: true } parent && IsNestedAllowed(parent, request))
            return new OperationLease(null, null, null, parent);

        ct.ThrowIfCancellationRequested();
        var op = new Op { Id = Interlocked.Increment(ref _nextId), Request = request, Since = _time.GetUtcNow() };
        op.Cts = new CancellationTokenSource();
        Task<bool>? wait = null;
        string? refusal = null;
        string? blockerName = null;
        lock (_lock)
        {
            op.Order = op.Id;
            if (_closing && !IsLow(request.Class))
                refusal = "MONOLITH закрывается — новые задачи не запускаются.";
            else
            {
                var blocker = FindBlocker(op, out var queuedBlocker, out var cap);
                if (blocker is null && !cap)
                {
                    op.State = OperationState.Running;
                    _running.Add(op);
                }
                else if (Queues(request) || cap)
                {
                    op.State = OperationState.Queued;
                    op.Blocker = blocker;
                    blockerName = blocker?.Request.Name;
                    op.Grant = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _queued.Add(op);
                    wait = op.Grant.Task;
                }
                else refusal = RefusalText(blocker!, queuedBlocker);
            }
        }

        if (refusal is not null)
        {
            op.Cts.Dispose();
            throw new OperationRefusedException(refusal, null);
        }
        RaiseChanged();
        if (wait is not null)
        {
            try
            {
                await WaitForGrantAsync(op, ct).ConfigureAwait(false);
            }
            catch
            {
                op.Cts.Dispose();
                throw;
            }
        }
        return new OperationLease(this, op, op.Cts, null);
    }

    private async Task WaitForGrantAsync(Op op, CancellationToken ct)
    {
        var limit = op.Request.QueueTimeout ?? DefaultQueueTimeout(op.Request.Class);
        using var timeout = new CancellationTokenSource(limit, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, op.Cts!.Token);
        using var reg = linked.Token.Register(static state =>
        {
            var (coordinator, o) = ((OperationCoordinator, Op))state!;
            coordinator.AbandonQueued(o);
        }, (this, op));
        try
        {
            await op.Grant!.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested && !op.CancelRequestedByUser)
            {
                string? blocker;
                lock (_lock) blocker = _running.FirstOrDefault(r => Conflicts(r, op))?.Request.Name;
                throw new OperationTimeoutException(
                    $"«{op.Request.Name}» не дождалась очереди за {FormatSpan(limit)}" +
                    (blocker is null ? "." : $": всё ещё идёт {Lower1(blocker)}.") + " Задача не запущена — повторите позже.");
            }
            throw;
        }
    }

    private static string FormatSpan(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} мин" : $"{(int)t.TotalSeconds} с";

    /// <summary>A queued operation gave up (cancelled or timed out): leaves the queue, the others move on.</summary>
    private void AbandonQueued(Op op)
    {
        var removed = false;
        lock (_lock) removed = _queued.Remove(op);
        if (removed) op.Grant?.TrySetCanceled();
        RaiseChanged();
        Pump();
    }

    /// <summary>Starts every queued operation that may start now (FIFO among conflicting ones).</summary>
    private void Pump()
    {
        List<Op>? started = null;
        lock (_lock)
        {
            foreach (var q in _queued.OrderBy(o => o.Order).ToList())
            {
                var blocker = FindBlocker(q, out _, out var cap);
                if (blocker is not null || cap)
                {
                    q.Blocker = blocker;
                    continue;
                }
                _queued.Remove(q);
                q.State = OperationState.Running;
                q.Blocker = null;
                _running.Add(q);
                (started ??= []).Add(q);
            }
        }
        if (started is null) return;
        foreach (var s in started) s.Grant?.TrySetResult(true);
        RaiseChanged();
    }

    internal void Release(Op op)
    {
        lock (_lock) _running.Remove(op);
        RaiseChanged();
        Pump();
    }

    internal void ReleaseForUser(Op op) => Release(op);

    /// <summary>After a dialog: back into the queue ahead of everything that waits (it was running before).</summary>
    internal async Task<Op> ReacquireAsync(Op previous, CancellationToken ct)
    {
        var op = new Op
        {
            Id = previous.Id, Request = previous.Request, Since = previous.Since, Resume = true,
            Cts = previous.Cts, CancelHandler = previous.CancelHandler, Detail = previous.Detail,
        };
        lock (_lock)
        {
            op.Order = long.MinValue + op.Id; // ahead of every waiting operation
            var blocker = FindBlocker(op, out _, out var cap);
            if (blocker is null && !cap)
            {
                op.State = OperationState.Running;
                _running.Add(op);
            }
            else
            {
                op.State = OperationState.Queued;
                op.Blocker = blocker;
                op.Grant = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _queued.Add(op);
            }
        }
        RaiseChanged();
        if (op.Grant is not null) await WaitForGrantAsync(op, ct).ConfigureAwait(false);
        return op;
    }

    // ───────────────────────────── Convenience ─────────────────────────────

    /// <summary>
    /// Acquires a lease, runs <paramref name="body"/> with it as the current lease of the flow (nested operations pass),
    /// and releases it in <c>finally</c>, whatever <paramref name="body"/> does.
    /// </summary>
    public async Task<T> RunAsync<T>(OperationRequest request, Func<OperationLease, Task<T>> body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var lease = await AcquireAsync(request, ct).ConfigureAwait(false);
        using var scope = lease.Scope();
        return await body(lease).ConfigureAwait(false);
    }

    public Task RunAsync(OperationRequest request, Func<OperationLease, Task> body, CancellationToken ct = default) =>
        RunAsync<bool>(request, async l => { await body(l).ConfigureAwait(false); return true; }, ct);

    /// <summary>Makes <paramref name="lease"/> the current lease of the calling flow until the returned scope is disposed.</summary>
    internal static IDisposable EnterScope(OperationLease lease)
    {
        var previous = CurrentLease.Value;
        CurrentLease.Value = lease;
        return new ScopeToken(previous);
    }

    private sealed class ScopeToken(OperationLease? previous) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) CurrentLease.Value = previous;
        }
    }

    /// <summary>True when the calling flow holds an active lease (nested operations pass).</summary>
    public static bool InsideOperation => CurrentLease.Value is { IsActive: true };

    // ───────────────────────────── Task list ─────────────────────────────

    /// <summary>Running and queued operations, running first, queued in FIFO order.</summary>
    public IReadOnlyList<OperationInfo> Snapshot()
    {
        lock (_lock)
        {
            var list = new List<OperationInfo>(_running.Count + _queued.Count);
            list.AddRange(_running.OrderBy(o => o.Since).Select(ToInfo));
            list.AddRange(_queued.OrderBy(o => o.Order).Select(ToInfo));
            return list;
        }
    }

    private static OperationInfo ToInfo(Op o) => new(
        o.Id, o.Request.Name, o.Request.Class, o.State, o.Request.Owner,
        o.State == OperationState.Queued && o.Blocker is { } b ? $"ждёт: {b.Request.Name}" : null,
        o.Detail, CanCancelOp(o), o.Since);

    private static bool CanCancelOp(Op o) =>
        o.State == OperationState.Queued || o.Request.Cancellable || o.CancelHandler is not null;

    /// <summary>Operations the user should know about (listed ones: running + queued).</summary>
    public int ListedCount
    {
        get
        {
            lock (_lock) return _running.Count(o => IsListedClass(o.Request.Class)) + _queued.Count(o => IsListedClass(o.Request.Class));
        }
    }

    private static bool IsListedClass(OperationClass c) => !IsLow(c);

    /// <summary>
    /// ОТМЕНИТЬ: a queued operation leaves the queue; a running one is cancelled only where that is safe (its token or its
    /// cancel handler). Returns false when nothing could be cancelled.
    /// </summary>
    public bool Cancel(long id)
    {
        Op? op;
        lock (_lock) op = _queued.FirstOrDefault(o => o.Id == id) ?? _running.FirstOrDefault(o => o.Id == id);
        if (op is null) return false;
        if (op.State == OperationState.Queued)
        {
            op.CancelRequestedByUser = true;
            AbandonQueued(op);
            return true;
        }
        if (!CanCancelOp(op)) return false;
        op.CancelRequestedByUser = true;
        op.State = OperationState.Cancelling;
        try
        {
            if (op.CancelHandler is { } handler) handler();
            else op.Cts?.Cancel();
        }
        catch (ObjectDisposedException) { /* finished meanwhile */ }
        catch (Exception ex)
        {
            Log.Warn($"Отмена «{op.Request.Name}»: {ex.Message}");
        }
        RaiseChanged();
        return true;
    }

    /// <summary>Cancels everything that can be cancelled safely (exit dialog); returns how many were asked to stop.</summary>
    public int CancelAllSafe()
    {
        List<long> ids;
        lock (_lock)
            ids = _queued.Concat(_running).Where(o => IsListedClass(o.Request.Class) && CanCancelOp(o)).Select(o => o.Id).ToList();
        return ids.Count(Cancel);
    }

    /// <summary>The window is closing: no new user operation starts (queued ones are cancelled by <see cref="CancelAllSafe"/>).</summary>
    public void BeginClosing()
    {
        _closing = true;
        RaiseChanged();
    }

    /// <summary>Takes the closing flag back (the user chose to wait).</summary>
    public void CancelClosing()
    {
        _closing = false;
        RaiseChanged();
    }

    /// <summary>Tests: how many operations are held (running + queued).</summary>
    internal int TotalCount
    {
        get
        {
            lock (_lock) return _running.Count + _queued.Count;
        }
    }
}

/// <summary>Pure logic of the exit dialog: what is still going on and what can be cancelled safely.</summary>
public sealed record ExitPlan(IReadOnlyList<OperationInfo> Blocking, int SafeToCancel)
{
    public bool NeedsDialog => Blocking.Count > 0;
    public int NotCancellable => Blocking.Count - SafeToCancel;

    public static ExitPlan From(IEnumerable<OperationInfo> operations)
    {
        var blocking = operations.Where(o => o.IsListed).ToList();
        return new ExitPlan(blocking, blocking.Count(o => o.CanCancel));
    }

    public string Message =>
        $"Сейчас выполняется: {Blocking.Count(o => o.State != OperationState.Queued)}, в очереди: {Blocking.Count(o => o.State == OperationState.Queued)}. " +
        (NotCancellable == 0
            ? "Все задачи можно отменить без вреда."
            : "Установщики и инструменты ремонта MONOLITH не обрывает: при выходе они продолжат работу сами, но их результат в MONOLITH уже не появится.");

    public IEnumerable<string> Lines =>
        Blocking.Select(o => $"{o.ClassText} · {o.Name} — {(o.State == OperationState.Queued ? "в очереди" : "идёт")}" +
                             (o.CanCancel ? "" : " (прервать нельзя)"));
}
