namespace Lycoris.Tests.Concurrency;

/// <summary>A clock that only moves when the test says so; timers fire inside <see cref="Advance"/>.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (_lock) return _now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        lock (_lock) _timers.Add(t);
        t.Change(dueTime, period);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += by;
            due = _timers.Where(t => t.Due is { } d && d <= _now).ToList();
        }
        foreach (var t in due) t.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback cb, object? state) : ITimer
    {
        public DateTimeOffset? Due;
        private TimeSpan _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                _period = period;
            }
            return true;
        }

        public void Fire()
        {
            lock (owner._lock)
            {
                if (Due is null) return;
                Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : owner._now + _period;
            }
            cb(state);
        }

        public void Dispose() { lock (owner._lock) { Due = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
