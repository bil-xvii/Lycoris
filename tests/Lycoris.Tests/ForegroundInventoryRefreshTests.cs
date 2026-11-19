using System.Windows.Threading;
using Lycoris.Core;
using Lycoris.Modules.Services;
using Lycoris.Modules.Startup;
using Lycoris.Shell;

namespace Lycoris.Tests;

[Collection("Registry")]
public sealed class ForegroundInventoryRefreshTests
{
    [Fact]
    public async Task Foreground_refresh_waits_for_loaded_and_stale_inventory()
    {
        var clock = new InventoryClock();
        var page = new InventoryPage(clock) { ReadAt = clock.GetUtcNow().UtcDateTime };
        Assert.False(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
        await page.EnsureLoadedAsync();
        clock.Advance(TimeSpan.FromSeconds(179));
        Assert.False(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
        Assert.Equal(0, page.Reads);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
        Assert.Equal(1, page.Reads);
    }

    [Fact]
    public async Task An_incomplete_read_is_debounced_and_can_retry_at_the_interval()
    {
        var clock = new InventoryClock();
        var page = new InventoryPage(clock);
        await page.EnsureLoadedAsync();

        Assert.True(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
        Assert.False(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
        clock.Advance(TimeSpan.FromSeconds(179));
        Assert.False(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
        Assert.Equal(1, page.Reads);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
        Assert.Equal(2, page.Reads);
    }

    [Fact]
    public async Task Periodic_ticks_respect_the_interval_and_do_no_work_after_the_window_closes()
    {
        var clock = new InventoryClock();
        var page = new InventoryPage(clock);
        await page.EnsureLoadedAsync();
        var isOpen = true;
        Task<bool> Tick() => page.RefreshStaleInventoryAsync(() => isOpen, NoRunningGame);

        Assert.True(await Tick());
        for (var tick = 1; tick <= 6; tick++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(tick == 6, await Tick());
        }
        Assert.Equal(2, page.Reads);
        isOpen = false;
        clock.Advance(TimeSpan.FromHours(1));
        Assert.False(await Tick());
        Assert.Equal(2, page.Reads);
    }

    [Fact]
    public async Task A_delayed_foreground_callback_rechecks_the_page_and_disregards_a_duplicate_activation()
    {
        var page = new InventoryPage(new());
        await page.EnsureLoadedAsync();
        var gameRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visible = true;

        var pending = page.RefreshStaleInventoryAsync(() => visible, () => gameRead.Task);
        Assert.False(pending.IsCompleted);
        Assert.False(await page.RefreshStaleInventoryAsync(() => visible, NoRunningGame));
        visible = false;
        gameRead.SetResult(false);
        Assert.False(await pending);
        Assert.Equal(0, page.Reads);

        visible = true;
        Assert.True(await page.RefreshStaleInventoryAsync(() => visible, NoRunningGame));
        Assert.Equal(1, page.Reads);
    }

    [Fact]
    public async Task A_game_or_a_new_system_operation_during_preflight_defers_without_marking_inventory_fresh()
    {
        var page = new InventoryPage(new());
        await page.EnsureLoadedAsync();

        Assert.False(await page.RefreshStaleInventoryAsync(() => true, () => Task.FromResult(true)));
        var gameRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = page.RefreshStaleInventoryAsync(() => true, () => gameRead.Task);
        using (await page.Coordinator.AcquireAsync(new(OperationClass.Install, "Test install")))
        {
            gameRead.SetResult(false);
            Assert.False(await pending);
            Assert.False(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
            Assert.Equal(0, page.Reads);
        }

        Assert.True(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
        Assert.Equal(1, page.Reads);
    }

    [Fact]
    public Task A_stale_services_page_rebuilds_its_catalog_and_discovers_newly_registered_services() => Sta(async () =>
    {
        var clock = new InventoryClock();
        var startup = new StartupViewModel();
        var scans = 0;
        var page = new ServicesViewModel(startup, () =>
        {
            startup.CheckedAt = clock.GetUtcNow().UtcDateTime;
            return Task.CompletedTask;
        })
        {
            Coordinator = new(),
            ForegroundClock = clock,
            ScanInventoryAsync = _ => Task.FromResult(new ServicesViewModel.ServiceInventory(
                new ServiceEnvironment(false, false, false, false),
                new Dictionary<string, ServiceLiveState>(), [],
                ++scans == 1
                    ? [new ServiceInfo { Name = "ExistingTestService", DisplayName = "Existing test service" }]
                    : [new ServiceInfo { Name = "NewTestService", DisplayName = "New test service" }])),
        };
        page.SelectedTab = ServicesViewModel.TabRecommendations;
        await page.EnsureLoadedAsync();
        Assert.Equal("ExistingTestService", Assert.Single(page.AllServices).Name);

        clock.Advance(TimeSpan.FromSeconds(179));
        Assert.False(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
        Assert.Equal(1, scans);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
        Assert.Equal("NewTestService", Assert.Single(page.AllServices).Name);
        Assert.Equal(2, scans);
    });

    [Fact]
    public Task First_startup_view_can_load_immediately_after_a_service_only_refresh() => Sta(async () =>
    {
        var clock = new InventoryClock();
        var startup = new StartupViewModel();
        var startupScans = 0;
        var serviceScans = 0;
        var page = new ServicesViewModel(startup, () =>
        {
            if (++startupScans == 1) return Task.CompletedTask; // First load did not publish an inventory.
            startup.Items.Add(new StartupItem(new StartupEntry
            {
                Name = "TestStartup", DisplayName = "Test startup entry", Source = StartupSource.RunUser,
                Command = "fixture.exe", Enabled = true,
            }));
            startup.CheckedAt = clock.GetUtcNow().UtcDateTime;
            return Task.CompletedTask;
        })
        {
            Coordinator = new(),
            ForegroundClock = clock,
            ScanInventoryAsync = _ =>
            {
                serviceScans++;
                return Task.FromResult(new ServicesViewModel.ServiceInventory(new(false, false, false, false),
                    new Dictionary<string, ServiceLiveState>(), [], []));
            },
        };
        page.SelectedTab = ServicesViewModel.TabRecommendations;
        await page.EnsureLoadedAsync();
        Assert.Null(startup.CheckedAt);

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(await page.RefreshStaleInventoryAsync(() => true, NoRunningGame));
        Assert.Equal(2, serviceScans);
        Assert.Equal(1, startupScans);

        page.SelectedTab = ServicesViewModel.TabStartup;
        Assert.Equal(2, startupScans);
        Assert.NotNull(startup.CheckedAt);
        Assert.Equal("TestStartup", Assert.Single(startup.Items).Entry.Name);
    });

    private static Task<bool> NoRunningGame() => Task.FromResult(false);

    private sealed class InventoryPage : PageViewModel
    {
        public InventoryPage(InventoryClock clock)
        {
            ForegroundClock = clock;
            Coordinator = new();
        }

        public override string Key => "test";
        public override string Index => "";
        public override string Title => "Inventory test";
        public override string Subtitle => "";
        public override string Glyph => "";
        public int Reads { get; private set; }
        public DateTime? ReadAt { get; init; }

        protected override bool ForegroundInventoryIsStale => InventoryIsStale(ReadAt);
        protected override Task RefreshForegroundInventoryAsync(Func<bool> stillForeground)
        {
            Reads++;
            return Task.CompletedTask;
        }
    }

    private sealed class InventoryClock : TimeProvider
    {
        private static readonly DateTimeOffset Start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        private TimeSpan _elapsed;
        public override DateTimeOffset GetUtcNow() => Start + _elapsed;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan amount) => _elapsed += amount;
    }

    private static Task Sta(Func<Task> body) => StaWorker.Run(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = body();
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
            return true;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }, "Foreground inventory test");
}
