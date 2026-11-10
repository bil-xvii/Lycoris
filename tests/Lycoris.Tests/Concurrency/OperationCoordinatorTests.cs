using Lycoris.Core;

namespace Lycoris.Tests.Concurrency;

public sealed class OperationCoordinatorTests
{
    private static OperationRequest R(OperationClass c, string name = "x") => new(c, name);
    private static readonly OperationClass[] All = Enum.GetValues<OperationClass>();

    [Fact]
    public void Matrix_is_symmetric_and_measure_background_never_conflict()
    {
        foreach (var a in All)
            foreach (var b in All)
            {
                Assert.Equal(OperationCoordinator.ClassesConflict(a, b), OperationCoordinator.ClassesConflict(b, a));
                if (a is OperationClass.Measure or OperationClass.Background) Assert.False(OperationCoordinator.ClassesConflict(a, b));
            }
    }

    [Theory]
    [InlineData(OperationClass.Install, OperationClass.Install, true)]
    [InlineData(OperationClass.Install, OperationClass.SystemRepair, true)]
    [InlineData(OperationClass.Install, OperationClass.Cleanup, true)]
    [InlineData(OperationClass.Install, OperationClass.JournalChange, true)]
    [InlineData(OperationClass.Install, OperationClass.Disruptive, true)]
    [InlineData(OperationClass.SystemRepair, OperationClass.Cleanup, true)]
    [InlineData(OperationClass.SystemRepair, OperationClass.SystemRepair, true)]
    [InlineData(OperationClass.JournalChange, OperationClass.JournalChange, true)]
    [InlineData(OperationClass.JournalChange, OperationClass.Disruptive, true)]
    [InlineData(OperationClass.JournalChange, OperationClass.Cleanup, false)]
    [InlineData(OperationClass.Disruptive, OperationClass.Cleanup, false)]
    [InlineData(OperationClass.Cleanup, OperationClass.Cleanup, true)]
    [InlineData(OperationClass.Measure, OperationClass.Install, false)]
    public void Matrix_cells(OperationClass a, OperationClass b, bool conflict) =>
        Assert.Equal(conflict, OperationCoordinator.ClassesConflict(a, b));

    [Fact]
    public void Quiet_measure_conflicts_with_heavy_classes_only()
    {
        var quiet = new OperationRequest(OperationClass.Measure, "Пинг") { Quiet = true };
        Assert.True(OperationCoordinator.Conflicts(quiet, R(OperationClass.Install)));
        Assert.True(OperationCoordinator.Conflicts(quiet, R(OperationClass.Disruptive)));
        Assert.False(OperationCoordinator.Conflicts(quiet, R(OperationClass.JournalChange)));
        Assert.False(OperationCoordinator.Conflicts(R(OperationClass.Measure), R(OperationClass.Install)));
    }

    [Fact]
    public async Task Refusable_class_is_refused_with_the_name_of_the_running_operation()
    {
        var c = new OperationCoordinator();
        using var install = await c.AcquireAsync(R(OperationClass.Install, "Установка Steam"));
        var ex = await Assert.ThrowsAsync<OperationRefusedException>(() => c.AcquireAsync(R(OperationClass.SystemRepair, "SFC")));
        Assert.Contains("установка Steam", ex.Message);
        Assert.Contains("дождитесь", ex.Message);
    }

    [Fact]
    public async Task Queueable_class_waits_in_fifo_order()
    {
        var c = new OperationCoordinator();
        var first = await c.AcquireAsync(R(OperationClass.Install, "A"));
        var order = new List<string>();
        var tasks = new List<Task>();
        foreach (var n in new[] { "B", "C", "D" })
        {
            var name = n;
            tasks.Add(Task.Run(async () =>
            {
                using var l = await c.AcquireAsync(R(OperationClass.Install, name));
                lock (order) order.Add(name);
            }));
            await WaitUntil(() => c.Snapshot().Any(i => i.Name == name && i.State == OperationState.Queued));
        }
        Assert.Equal(["A", "B", "C", "D"], c.Snapshot().Select(i => i.Name));
        Assert.Contains("ждёт", c.Snapshot().First(i => i.Name == "B").WaitingFor);
        first.Dispose();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["B", "C", "D"], order);
        Assert.Equal(0, c.TotalCount);
    }

    [Fact]
    public async Task Cancelling_a_queued_item_removes_it_and_lets_the_others_pass()
    {
        var c = new OperationCoordinator();
        var first = await c.AcquireAsync(R(OperationClass.Install, "A"));
        var b = Task.Run(() => c.AcquireAsync(R(OperationClass.Install, "B")));
        await WaitUntil(() => c.Snapshot().Any(i => i.Name == "B"));
        var cc = Task.Run(() => c.AcquireAsync(R(OperationClass.Install, "C")));
        await WaitUntil(() => c.Snapshot().Any(i => i.Name == "C"));
        var infoB = c.Snapshot().First(i => i.Name == "B");
        Assert.True(infoB.CanCancel);
        Assert.True(c.Cancel(infoB.Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        first.Dispose();
        using var lc = await cc.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["C"], c.Snapshot().Select(i => i.Name));
    }

    [Fact]
    public async Task Running_operation_without_a_safe_cancel_cannot_be_cancelled()
    {
        var c = new OperationCoordinator();
        using var l = await c.AcquireAsync(R(OperationClass.SystemRepair, "DISM"));
        var info = c.Snapshot().Single();
        Assert.False(info.CanCancel);
        Assert.False(c.Cancel(info.Id));
        var cancelled = 0;
        l.SetCancelHandler(() => cancelled++);
        Assert.True(c.Snapshot().Single().CanCancel);
        Assert.True(c.Cancel(info.Id));
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public async Task Cancellable_request_cancels_the_lease_token()
    {
        var c = new OperationCoordinator();
        using var l = await c.AcquireAsync(new OperationRequest(OperationClass.Cleanup, "Очистка") { Cancellable = true });
        Assert.False(l.Token.IsCancellationRequested);
        c.Cancel(l.Id);
        Assert.True(l.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Lease_is_released_when_the_body_throws()
    {
        var c = new OperationCoordinator();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.RunAsync(R(OperationClass.Install, "A"), (Func<OperationLease, Task>)(_ => throw new InvalidOperationException("boom"))));
        Assert.Equal(0, c.TotalCount);
        using var again = await c.AcquireAsync(R(OperationClass.Install, "B"));
        Assert.Single(c.Snapshot());
    }

    [Fact]
    public async Task Nested_operation_of_the_same_flow_does_not_wait_or_refuse()
    {
        var c = new OperationCoordinator();
        var ran = false;
        await c.RunAsync(R(OperationClass.Install, "Outer"), async outer =>
        {
            await c.RunAsync(R(OperationClass.Install, "Inner"), async inner =>
            {
                Assert.True(inner.IsNested);
                await Task.Yield();
                await c.RunAsync(R(OperationClass.SystemRepair, "Deeper"), _ => { ran = true; return Task.CompletedTask; });
            });
            Assert.Single(c.Snapshot());
        });
        Assert.True(ran);
        Assert.Equal(0, c.TotalCount);
    }

    [Fact]
    public async Task A_captured_context_cannot_reuse_a_released_lease()
    {
        var c = new OperationCoordinator();
        var gate = new TaskCompletionSource();
        var seen = new TaskCompletionSource<bool>();
        await c.RunAsync(R(OperationClass.Install, "A"), lease =>
        {
            var t = Task.Run(async () => { await gate.Task; seen.SetResult(OperationCoordinator.InsideOperation); });
            return Task.CompletedTask;
        });
        gate.SetResult();
        Assert.False(await seen.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Queue_wait_times_out_with_a_clear_message()
    {
        var clock = new ManualTimeProvider();
        var c = new OperationCoordinator(clock);
        using var first = await c.AcquireAsync(R(OperationClass.Install, "Установка Steam"));
        var waiting = Task.Run(() => c.AcquireAsync(new OperationRequest(OperationClass.JournalChange, "Применение") { QueueTimeout = TimeSpan.FromMinutes(5) }));
        await WaitUntil(() => c.Snapshot().Any(i => i.Name == "Применение"));
        // The waiter shows in the queue a moment before it arms its timeout timer: a single Advance in that gap moved the
        // clock before the timer existed, the timer never fired and the test hung (CI windows-2025, 10.11). Advance until it ends.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!waiting.IsCompleted && DateTime.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromMinutes(6));
            await Task.WhenAny(waiting, Task.Delay(20));
        }
        var ex = await Assert.ThrowsAsync<OperationTimeoutException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("не дождалась очереди", ex.Message);
        Assert.Contains("установка Steam", ex.Message);
        Assert.Single(c.Snapshot());
    }

    [Fact]
    public async Task Caller_cancellation_of_a_queued_wait_leaves_the_queue()
    {
        var c = new OperationCoordinator();
        using var first = await c.AcquireAsync(R(OperationClass.Install, "A"));
        using var cts = new CancellationTokenSource();
        var waiting = Task.Run(() => c.AcquireAsync(R(OperationClass.Install, "B"), cts.Token));
        await WaitUntil(() => c.Snapshot().Count == 2);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Single(c.Snapshot());
    }

    [Fact]
    public async Task Low_priority_work_is_capped_and_never_blocks_user_actions()
    {
        var c = new OperationCoordinator(lowPriorityCap: 2);
        var m1 = await c.AcquireAsync(R(OperationClass.Measure));
        var m2 = await c.AcquireAsync(R(OperationClass.Background));
        var third = Task.Run(() => c.AcquireAsync(R(OperationClass.Measure, "m3")));
        await WaitUntil(() => c.TotalCount == 3);
        Assert.False(third.IsCompleted);
        using var user = await c.AcquireAsync(R(OperationClass.Install, "Установка")); // not starved
        Assert.Equal(1, c.ListedCount);
        m1.Dispose();
        using var l3 = await third.WaitAsync(TimeSpan.FromSeconds(10));
        m2.Dispose();
    }

    [Fact]
    public async Task Quiet_measure_is_refused_during_an_install_and_the_other_way_round()
    {
        var c = new OperationCoordinator();
        var quiet = new OperationRequest(OperationClass.Measure, "Замер задержек") { Quiet = true };
        using (await c.AcquireAsync(R(OperationClass.Install, "Установка")))
            await Assert.ThrowsAsync<OperationRefusedException>(() => c.AcquireAsync(quiet));
        using (await c.AcquireAsync(quiet))
            await Assert.ThrowsAsync<OperationRefusedException>(() => c.AcquireAsync(R(OperationClass.Disruptive, "Смена сети")));
    }

    [Fact]
    public async Task QueueIfBusy_waits_instead_of_refusing()
    {
        var c = new OperationCoordinator();
        var first = await c.AcquireAsync(R(OperationClass.Disruptive, "Старт игрового сеанса"));
        var stop = Task.Run(() => c.AcquireAsync(new OperationRequest(OperationClass.Disruptive, "Остановка игрового сеанса") { QueueIfBusy = true }));
        await WaitUntil(() => c.Snapshot().Count == 2);
        first.Dispose();
        using var l = await stop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhileUserDecides_lets_others_in_and_takes_the_lease_back()
    {
        var c = new OperationCoordinator();
        using var lease = await c.AcquireAsync(R(OperationClass.Install, "A"));
        var order = new List<string>();
        var asked = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var user = Task.Run(() => lease.WhileUserDecidesAsync(async () =>
        {
            asked.SetResult();
            await release.Task;
            lock (order) order.Add("answered");
            return 1;
        }));
        await asked.Task;
        using (var other = await c.AcquireAsync(R(OperationClass.Install, "B"))) lock (order) order.Add("B ran");
        release.SetResult();
        await user;
        Assert.Equal(["B ran", "answered"], order);
        Assert.Equal("A", c.Snapshot().Single().Name);
    }

    [Fact]
    public async Task Closing_refuses_new_user_operations()
    {
        var c = new OperationCoordinator();
        c.BeginClosing();
        await Assert.ThrowsAsync<OperationRefusedException>(() => c.AcquireAsync(R(OperationClass.JournalChange)));
        using var m = await c.AcquireAsync(R(OperationClass.Measure));
        c.CancelClosing();
        using var j = await c.AcquireAsync(R(OperationClass.JournalChange));
    }

    [Fact]
    public async Task Check_does_not_take_anything()
    {
        var c = new OperationCoordinator();
        using var l = await c.AcquireAsync(R(OperationClass.Install, "Установка Steam"));
        var (verdict, reason) = c.Check(R(OperationClass.SystemRepair));
        Assert.Equal(OperationVerdict.Refuse, verdict);
        Assert.Contains("установка Steam", reason);
        Assert.Equal(OperationVerdict.Queue, c.Check(R(OperationClass.Install)).Verdict);
        Assert.Equal(OperationVerdict.Start, c.Check(R(OperationClass.Measure)).Verdict);
        Assert.Single(c.Snapshot());
    }

    [Fact]
    public void Lower1_keeps_acronyms()
    {
        Assert.Equal("установка Steam", OperationCoordinator.Lower1("Установка Steam"));
        Assert.Equal("DISM: восстановление", OperationCoordinator.Lower1("DISM: восстановление"));
    }

    [Fact]
    public async Task Exit_plan_lists_blocking_operations_and_what_is_safe_to_cancel()
    {
        var c = new OperationCoordinator();
        using var repair = await c.AcquireAsync(R(OperationClass.SystemRepair, "SFC"));
        var q = Task.Run(() => c.AcquireAsync(R(OperationClass.Install, "Установка")));
        await WaitUntil(() => c.Snapshot().Count == 2);
        using var measure = await c.AcquireAsync(R(OperationClass.Measure));
        var plan = ExitPlan.From(c.Snapshot());
        Assert.True(plan.NeedsDialog);
        Assert.Equal(2, plan.Blocking.Count);
        Assert.Equal(1, plan.SafeToCancel);
        Assert.Equal(1, plan.NotCancellable);
        Assert.Contains("не обрывает", plan.Message);
        Assert.Equal(1, c.CancelAllSafe());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => q);
        Assert.False(ExitPlan.From([]).NeedsDialog);
    }

    [Fact]
    public async Task Randomised_storm_of_200_operations_finishes_without_deadlock_and_without_overlap()
    {
        var c = new OperationCoordinator();
        var rnd = new Random(12345);
        var running = new List<OperationRequest>();
        var violations = 0;
        var done = 0;
        var refused = 0;
        var tasks = Enumerable.Range(0, 200).Select(i =>
        {
            var cls = All[rnd.Next(All.Length)];
            var delay = rnd.Next(0, 3);
            var quiet = cls == OperationClass.Measure && rnd.Next(4) == 0;
            return Task.Run(async () =>
            {
                var req = new OperationRequest(cls, $"op{i}") { Quiet = quiet, QueueTimeout = TimeSpan.FromHours(1) };
                try
                {
                    await c.RunAsync(req, async _ =>
                    {
                        lock (running)
                        {
                            if (running.Any(r => OperationCoordinator.Conflicts(r, req))) Interlocked.Increment(ref violations);
                            running.Add(req);
                        }
                        await Task.Delay(delay);
                        if (i % 7 == 0) await c.RunAsync(R(OperationClass.Install, "nested"), _ => Task.CompletedTask);
                        lock (running) running.Remove(req);
                    });
                    Interlocked.Increment(ref done);
                }
                catch (OperationRefusedException) { Interlocked.Increment(ref refused); }
            });
        }).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(0, violations);
        Assert.Equal(200, done + refused);
        Assert.True(done > 0);
        Assert.Equal(0, c.TotalCount);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("condition not reached");
            await Task.Delay(5);
        }
    }
}
