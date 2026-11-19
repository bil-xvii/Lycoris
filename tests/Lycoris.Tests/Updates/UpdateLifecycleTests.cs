using System.Net;
using System.Net.Http;
using System.Threading.Channels;
using Lycoris.Core;
using Lycoris.Core.Updates;
using Lycoris.Shell;
using Lycoris.Tests.Concurrency;

namespace Lycoris.Tests.Updates;

public sealed class UpdateLifecycleTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_running_app_checks_again_when_due_and_respects_the_automatic_toggle()
    {
        var time = new SchedulingTimeProvider();
        var store = new FakeStore { AutoCheck = false };
        var handler = new FakeHandler(_ => FakeHandler.Json(Kit.ReleaseJson()));
        using var http = new HttpClient(handler);
        var controller = new UpdateController(MakeService(http, store, time));
        var coordinator = new OperationCoordinator();
        using var stop = new CancellationTokenSource();
        var checks = controller.RunBackgroundChecksAsync(time, coordinator, stop.Token);

        try
        {
            Assert.Equal(TimeSpan.FromSeconds(15), await time.NextDelayAsync());
            time.Advance(TimeSpan.FromSeconds(15));
            Assert.Equal(TimeSpan.FromMinutes(15), await time.NextDelayAsync());
            Assert.Empty(handler.Requests);

            store.AutoCheck = true;
            time.Advance(TimeSpan.FromMinutes(15));
            Assert.Equal(TimeSpan.FromMinutes(15), await time.NextDelayAsync());
            Assert.Single(handler.Requests);
            Assert.Equal("1.0.1", controller.Latest?.VersionText);

            time.Advance(TimeSpan.FromHours(11) + TimeSpan.FromMinutes(59));
            Assert.Equal(TimeSpan.FromMinutes(15), await time.NextDelayAsync());
            Assert.Single(handler.Requests);

            time.Advance(TimeSpan.FromMinutes(15));
            Assert.Equal(TimeSpan.FromMinutes(15), await time.NextDelayAsync());
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(time.GetUtcNow().UtcDateTime, store.LastCheckUtc);
        }
        finally
        {
            stop.Cancel();
            await checks.WaitAsync(WaitLimit);
        }

        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(coordinator.Snapshot());
    }

    [Fact]
    public async Task Stopping_the_scheduler_cancels_an_http_check_already_in_progress()
    {
        var time = new SchedulingTimeProvider();
        var store = new FakeStore();
        var handler = new BlockingFirstRequest();
        using var http = new HttpClient(handler);
        var controller = new UpdateController(MakeService(http, store, time));
        var coordinator = new OperationCoordinator();
        using var stop = new CancellationTokenSource();
        var checks = controller.RunBackgroundChecksAsync(time, coordinator, stop.Token);

        try
        {
            Assert.Equal(TimeSpan.FromSeconds(15), await time.NextDelayAsync());
            time.Advance(TimeSpan.FromSeconds(15));
            await handler.Started.Task.WaitAsync(WaitLimit);
            Assert.Single(coordinator.Snapshot());
        }
        finally
        {
            stop.Cancel();
            await checks.WaitAsync(WaitLimit);
        }

        Assert.Null(store.LastCheckUtc);
        Assert.Empty(coordinator.Snapshot());
    }

    [Theory]
    [InlineData(false, UpdateCheckStatus.Skipped, 1)]
    [InlineData(true, UpdateCheckStatus.UpToDate, 2)]
    public async Task Checks_wait_for_the_active_request_and_only_manual_checks_bypass_due_time(
        bool manual, UpdateCheckStatus secondStatus, int requests)
    {
        var store = new FakeStore();
        var handler = new BlockingFirstRequest();
        using var http = new HttpClient(handler);
        var service = MakeService(http, store, new ManualTimeProvider());
        var first = service.CheckAsync(manual: false);
        Task<UpdateCheckResult> second;
        try
        {
            await handler.Started.Task.WaitAsync(WaitLimit);
            second = service.CheckAsync(manual);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, handler.RequestCount);
        }
        finally { handler.Release.TrySetResult(); }

        Assert.Equal(UpdateCheckStatus.UpToDate, (await first.WaitAsync(WaitLimit)).Status);
        Assert.Equal(secondStatus, (await second.WaitAsync(WaitLimit)).Status);
        Assert.Equal(requests, handler.RequestCount);
    }

    [Fact]
    public async Task Cancelling_a_check_waiting_for_the_gate_keeps_the_active_request_in_control()
    {
        var handler = new BlockingFirstRequest();
        using var http = new HttpClient(handler);
        var service = MakeService(http, new FakeStore(), new ManualTimeProvider());
        using var stop = new CancellationTokenSource();
        var first = service.CheckAsync(manual: false);
        Task<UpdateCheckResult> next;
        try
        {
            await handler.Started.Task.WaitAsync(WaitLimit);
            var cancelled = service.CheckAsync(manual: true, stop.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(WaitLimit));

            next = service.CheckAsync(manual: true);
            Assert.False(next.IsCompleted);
            Assert.Equal(1, handler.RequestCount);
        }
        finally { handler.Release.TrySetResult(); }

        Assert.Equal(UpdateCheckStatus.UpToDate, (await first.WaitAsync(WaitLimit)).Status);
        Assert.Equal(UpdateCheckStatus.UpToDate, (await next.WaitAsync(WaitLimit)).Status);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handover_refuses_a_listed_operation_or_a_game_session_already_active(bool gameSession)
    {
        using var language = Loc.UseLanguage("ru");
        var coordinator = new OperationCoordinator();
        using var active = gameSession ? null : await coordinator.AcquireAsync(new OperationRequest(OperationClass.Install, "Steam"));

        var result = await UpdateController.TryHandoverAsync(coordinator, () => gameSession,
            () => throw new InvalidOperationException("The updater must not start"));

        Assert.Contains(gameSession ? "Идёт игровой сеанс" : "Steam", result.Blocker);
        Assert.Null(result.Launch);
        Assert.False(coordinator.IsClosing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handover_fences_the_launch_await_and_reopens_only_if_the_updater_does_not_start(bool started)
    {
        var coordinator = new OperationCoordinator();
        var launch = new TaskCompletionSource<UpdateLauncher.LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handover = UpdateController.TryHandoverAsync(coordinator, () => false, () => launch.Task);
        var install = new OperationRequest(OperationClass.Install, "Steam");
        var game = new OperationRequest(OperationClass.Disruptive, "Game Boost");
        var expected = new UpdateLauncher.LaunchResult(started, started ? null : "Launch failed");

        try
        {
            Assert.False(handover.IsCompleted);
            await Assert.ThrowsAsync<OperationRefusedException>(() => coordinator.AcquireAsync(install));
            await Assert.ThrowsAsync<OperationRefusedException>(() => coordinator.AcquireAsync(game));
        }
        finally { launch.TrySetResult(expected); }

        var result = await handover.WaitAsync(WaitLimit);
        Assert.Null(result.Blocker);
        Assert.Equal(expected, result.Launch);
        Assert.Equal(started, coordinator.IsClosing);
        if (started) await Assert.ThrowsAsync<OperationRefusedException>(() => coordinator.AcquireAsync(install));
        else using (await coordinator.AcquireAsync(install)) Assert.Equal(1, coordinator.ListedCount);
    }

    [Fact]
    public async Task Cancelling_handover_reopens_the_coordinator()
    {
        var coordinator = new OperationCoordinator();
        var launch = new TaskCompletionSource<UpdateLauncher.LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handover = UpdateController.TryHandoverAsync(coordinator, () => false, () => launch.Task);

        launch.SetCanceled();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handover.WaitAsync(WaitLimit));
        Assert.False(coordinator.IsClosing);
        using var install = await coordinator.AcquireAsync(new OperationRequest(OperationClass.Install, "Steam"));
        Assert.Equal(1, coordinator.ListedCount);
    }

    [Fact]
    public async Task Handover_does_not_clear_an_existing_close()
    {
        var coordinator = new OperationCoordinator();
        coordinator.BeginClosing();

        var result = await UpdateController.TryHandoverAsync(coordinator, () => false,
            () => throw new InvalidOperationException("The updater must not start"));

        Assert.Null(result.Launch);
        Assert.True(coordinator.IsClosing);
    }

    [Fact]
    public async Task A_window_close_and_its_cancellation_cannot_reopen_an_active_handover()
    {
        var coordinator = new OperationCoordinator();
        var launch = new TaskCompletionSource<UpdateLauncher.LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handover = UpdateController.TryHandoverAsync(coordinator, () => false, () => launch.Task);
        var install = new OperationRequest(OperationClass.Install, "Steam");

        try
        {
            coordinator.CancelClosing();
            await Assert.ThrowsAsync<OperationRefusedException>(() => coordinator.AcquireAsync(install));

            coordinator.BeginClosing();
            coordinator.CancelClosing();
            await Assert.ThrowsAsync<OperationRefusedException>(() => coordinator.AcquireAsync(install));
        }
        finally { launch.TrySetResult(new UpdateLauncher.LaunchResult(false, "Launch failed")); }

        await handover.WaitAsync(WaitLimit);
        Assert.False(coordinator.IsClosing);
    }

    [Fact]
    public async Task A_failed_handover_leaves_a_window_close_requested_during_staging_in_effect()
    {
        var coordinator = new OperationCoordinator();
        var launch = new TaskCompletionSource<UpdateLauncher.LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handover = UpdateController.TryHandoverAsync(coordinator, () => false, () => launch.Task);
        coordinator.BeginClosing();

        launch.SetResult(new UpdateLauncher.LaunchResult(false, "Launch failed"));
        await handover.WaitAsync(WaitLimit);

        var install = new OperationRequest(OperationClass.Install, "Steam");
        await Assert.ThrowsAsync<OperationRefusedException>(() => coordinator.AcquireAsync(install));
        coordinator.CancelClosing();
        using var lease = await coordinator.AcquireAsync(install);
        Assert.Equal(1, coordinator.ListedCount);
    }

    private static UpdateService MakeService(HttpClient http, FakeStore store, TimeProvider time) =>
        new(http, store, () => new SemVer(1, 0, 0, 0, ""), () => time.GetUtcNow().UtcDateTime);

    private sealed class SchedulingTimeProvider : TimeProvider
    {
        private readonly ManualTimeProvider _time = new();
        private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();

        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();
        public void Advance(TimeSpan duration) => _time.Advance(duration);
        public Task<TimeSpan> NextDelayAsync() => _timers.Reader.ReadAsync().AsTask().WaitAsync(WaitLimit);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _time.CreateTimer(callback, state, dueTime, period);
            _timers.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    private sealed class BlockingFirstRequest : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return FakeHandler.Status(HttpStatusCode.NotFound);
        }
    }
}
