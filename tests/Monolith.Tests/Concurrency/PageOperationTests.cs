using Monolith.Core;
using Monolith.Shell;

namespace Monolith.Tests.Concurrency;

/// <summary>What a page does when a second long operation is started while another one runs: queue (visible) or refuse (with the reason).</summary>
public sealed class PageOperationTests
{
    private sealed class FakePage : PageViewModel
    {
        public FakePage(OperationCoordinator coordinator, string key = "fake")
        {
            Coordinator = coordinator;
            _key = key;
        }

        private readonly string _key;
        public override string Key => _key;
        public override string Index => "99";
        public override string Title => "ТЕСТ";
        public override string Subtitle => "";
        public override string Glyph => "";

        public Task Run(OperationClass kind, string text, Func<Task> work, bool quiet = false) => RunBusyAsync(kind, text, work, quiet);
        public bool Can(OperationClass kind, string name) => CanStartNow(kind, name);
        public Task<OperationLease?> Lease(OperationClass kind, string name, Action? cancel = null) => AcquireLeaseAsync(kind, name, cancel);
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

    [Fact]
    public async Task Second_journal_change_waits_in_the_visible_queue_and_then_runs()
    {
        var c = new OperationCoordinator();
        var a = new FakePage(c, "optimize");
        var b = new FakePage(c, "services");
        var release = new TaskCompletionSource();
        var order = new List<string>();

        var first = a.Run(OperationClass.JournalChange, "Применение изменений…", async () => { order.Add("A start"); await release.Task; order.Add("A end"); });
        await WaitUntil(() => order.Contains("A start"));
        var second = b.Run(OperationClass.JournalChange, "Изменение служб…", () => { order.Add("B"); return Task.CompletedTask; });
        await WaitUntil(() => c.Snapshot().Count == 2);

        Assert.True(b.IsBusy);
        Assert.StartsWith("В очереди", b.BusyText);
        Assert.Contains("В очереди", b.OperationNote);
        Assert.DoesNotContain("B", order);
        Assert.Equal(OperationState.Queued, c.Snapshot()[1].State);

        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["A start", "A end", "B"], order);
        Assert.False(b.IsBusy);
        Assert.Null(b.OperationNote);
        Assert.Equal(0, c.TotalCount);
    }

    [Fact]
    public async Task Repair_during_an_install_is_refused_with_the_name_of_the_install()
    {
        var c = new OperationCoordinator();
        var installs = new FakePage(c, "apps");
        var tools = new FakePage(c, "tools");
        var release = new TaskCompletionSource();
        var ran = false;

        var install = installs.Run(OperationClass.Install, "Установка Steam…", () => release.Task);
        await WaitUntil(() => c.Snapshot().Count == 1);
        Assert.False(tools.Can(OperationClass.SystemRepair, "Проверка системных файлов"));
        Assert.Contains("установка Steam", tools.OperationNote);
        Assert.Contains("дождитесь", tools.OperationNote);

        await tools.Run(OperationClass.SystemRepair, "Проверка системных файлов…", () => { ran = true; return Task.CompletedTask; });
        Assert.False(ran);
        Assert.False(tools.IsBusy);
        Assert.Contains("установка Steam", tools.OperationNote);

        release.SetResult();
        await install;
    }

    [Fact]
    public async Task A_queued_start_taken_out_of_the_queue_never_runs()
    {
        var c = new OperationCoordinator();
        var a = new FakePage(c, "apps");
        var b = new FakePage(c, "drivers");
        var release = new TaskCompletionSource();
        var ran = false;

        var first = a.Run(OperationClass.Install, "Установка A…", () => release.Task);
        await WaitUntil(() => c.Snapshot().Count == 1);
        var second = b.Run(OperationClass.Install, "Установка драйверов…", () => { ran = true; return Task.CompletedTask; });
        await WaitUntil(() => c.Snapshot().Count == 2);
        Assert.True(c.Cancel(c.Snapshot()[1].Id));
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(ran);
        Assert.False(b.IsBusy);
        Assert.Null(b.OperationNote);

        release.SetResult();
        await first;
    }

    [Fact]
    public async Task A_crashing_operation_fails_only_itself_and_frees_the_lease()
    {
        var c = new OperationCoordinator();
        var a = new FakePage(c, "optimize");
        var b = new FakePage(c, "services");

        await a.Run(OperationClass.JournalChange, "Падает…", () => throw new InvalidOperationException("boom"));
        Assert.False(a.IsBusy);
        Assert.Equal(0, c.TotalCount);

        var ran = false;
        await b.Run(OperationClass.JournalChange, "Следующая…", () => { ran = true; return Task.CompletedTask; });
        Assert.True(ran);
    }

    [Fact]
    public async Task Measure_never_waits_behind_a_journal_change()
    {
        var c = new OperationCoordinator();
        var a = new FakePage(c, "optimize");
        var b = new FakePage(c, "dashboard");
        var release = new TaskCompletionSource();
        var first = a.Run(OperationClass.JournalChange, "Применение…", () => release.Task);
        await WaitUntil(() => c.Snapshot().Count == 1);
        var measured = false;
        await b.Run(OperationClass.Measure, "Проверка системы…", () => { measured = true; return Task.CompletedTask; });
        Assert.True(measured);
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task Nested_operation_inside_a_page_operation_does_not_queue_behind_it()
    {
        var c = new OperationCoordinator();
        var page = new FakePage(c, "apps");
        var nested = false;
        await page.Run(OperationClass.Install, "Установка…", async () =>
        {
            // e.g. RestorePointService.CreateAsync inside an install
            await c.RunAsync(new OperationRequest(OperationClass.Disruptive, "Создание точки восстановления") { QueueIfBusy = true },
                _ => { nested = true; return Task.CompletedTask; }).WaitAsync(TimeSpan.FromSeconds(5));
        });
        Assert.True(nested);
    }

    [Fact]
    public async Task Manual_lease_for_pages_with_their_own_progress_ui_queues_and_reports()
    {
        var c = new OperationCoordinator();
        var a = new FakePage(c, "apps");
        var b = new FakePage(c, "drivers");
        using var first = (await a.Lease(OperationClass.Install, "Загрузка A"))!;
        var secondTask = b.Lease(OperationClass.Install, "Загрузка драйвера B");
        await WaitUntil(() => c.Snapshot().Count == 2);
        Assert.Contains("В очереди", b.OperationNote);
        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(second);
        Assert.Null(b.OperationNote);
    }

    [Fact]
    public void Operation_item_shows_state_and_why_it_cannot_be_cancelled()
    {
        var running = new OperationInfo(1, "Проверка системных файлов", OperationClass.SystemRepair, OperationState.Running, "tools", null, null, false, DateTimeOffset.UtcNow);
        var item = new OperationItemViewModel(running);
        Assert.Equal("ИДЁТ", item.StateText);
        Assert.False(item.CanCancel);
        Assert.Contains("прервать нельзя", item.NoCancelText);

        item.Apply(running with { Id = 2, State = OperationState.Queued, CanCancel = true, WaitingFor = "ждёт: Установка Steam" });
        Assert.Equal("В ОЧЕРЕДИ", item.StateText);
        Assert.True(item.CanCancel);
        Assert.Equal("ждёт: Установка Steam", item.Note);
        Assert.Null(item.NoCancelText);
    }
}
