using System.Windows.Threading;
using Lycoris.Modules.Removal;
using Lycoris.Modules.Services;
using Lycoris.Modules.Startup;
using Lycoris.Shell;

namespace Lycoris.Tests;

public sealed class StartupFocusNavigationTests
{
    private static readonly StartupEntry UserEntry = Entry(StartupSource.RunUser);
    private static readonly StartupEntry MachineEntry = Entry(StartupSource.RunMachine);
    private static readonly DateTime FirstCheck = new(2026, 11, 19, 10, 0, 0);

    [Fact]
    public Task First_focus_waits_for_the_scan_then_shows_only_the_requested_duplicate_name() => Sta(async () =>
    {
        var scan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = new StartupViewModel();
        var destination = new ServicesViewModel(startup, () => scan.Task);

        var opening = destination.ApplyFocusAsync(CommandPaletteIndex.StartupFocusPrefix + MachineEntry.Key.ToLowerInvariant());
        Assert.False(opening.IsCompleted);
        Assert.Equal(ServicesViewModel.TabStartup, destination.SelectedTab);
        Assert.Empty(startup.ItemsView);
        SetSnapshot(startup, FirstCheck, UserEntry, MachineEntry);
        scan.SetResult();
        await opening;

        Assert.Equal(MachineEntry.Key, Assert.Single(startup.ItemsView.Cast<StartupItem>()).Key);
        Assert.Equal("Launcher", startup.Search);
        startup.Search = "Launch";
        Assert.Equal(new[] { UserEntry.Key, MachineEntry.Key }, VisibleKeys(startup));
    });

    [Fact]
    public Task Already_loaded_focus_bypasses_the_filter_then_normal_tab_navigation_clears_the_exact_scope() => Sta(async () =>
    {
        var startup = new StartupViewModel();
        int activations = 0;
        var destination = new ServicesViewModel(startup, () =>
        {
            activations++;
            SetSnapshot(startup, FirstCheck, UserEntry, MachineEntry);
            return Task.CompletedTask;
        });
        await destination.ApplyFocusAsync(CommandPaletteIndex.StartupFocusPrefix + UserEntry.Key);
        startup.Filter = "enabled";
        startup.Search = "Different";
        Assert.Empty(startup.ItemsView);

        await destination.ApplyFocusAsync(CommandPaletteIndex.StartupFocusPrefix + MachineEntry.Key);

        Assert.Equal("all", startup.Filter);
        Assert.Equal(MachineEntry.Key, Assert.Single(startup.ItemsView.Cast<StartupItem>()).Key);
        Assert.Equal(1, activations);
        await destination.ApplyFocusAsync("tab:startup");
        Assert.Equal(new[] { UserEntry.Key, MachineEntry.Key }, VisibleKeys(startup));
        Assert.Empty(startup.Search);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Reopening_uses_the_pending_scan_and_clears_the_old_focus_if_missing_or_failed(bool successful) => Sta(async () =>
    {
        var scan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = new StartupViewModel();
        var initial = true;
        var destination = new ServicesViewModel(startup, () =>
        {
            if (!initial) return scan.Task;
            initial = false;
            SetSnapshot(startup, FirstCheck, UserEntry, MachineEntry);
            return Task.CompletedTask;
        });
        await destination.ApplyFocusAsync(CommandPaletteIndex.StartupFocusPrefix + MachineEntry.Key);
        Assert.Equal(MachineEntry.Key, Assert.Single(startup.ItemsView.Cast<StartupItem>()).Key);
        destination.Deactivate();

        var opening = destination.ApplyFocusAsync(CommandPaletteIndex.StartupFocusPrefix + MachineEntry.Key);
        Assert.False(opening.IsCompleted);
        if (successful) SetSnapshot(startup, FirstCheck.AddSeconds(1), UserEntry);
        scan.SetResult();
        await opening;

        Assert.Empty(startup.Search);
        Assert.Equal(successful ? new[] { UserEntry.Key } : [UserEntry.Key, MachineEntry.Key], VisibleKeys(startup));
    });

    [Fact]
    public Task Explicit_search_during_the_scan_wins_over_the_pending_link() => Sta(async () =>
    {
        var scan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = new StartupViewModel();
        var destination = new ServicesViewModel(startup, () => scan.Task);
        var opening = destination.ApplyFocusAsync(CommandPaletteIndex.StartupFocusPrefix + MachineEntry.Key);

        startup.Search = "Launch";
        SetSnapshot(startup, FirstCheck, UserEntry, MachineEntry);
        scan.SetResult();
        await opening;

        Assert.Equal("Launch", startup.Search);
        Assert.Equal(new[] { UserEntry.Key, MachineEntry.Key }, VisibleKeys(startup));
    });

    [Fact]
    public void Removal_can_link_an_already_disabled_startup_entry_but_not_an_ambiguous_one()
    {
        var panel = new RemovalViewModel { TabIndex = 1 };
        panel.SelectedRow = RemovalPolicy.Startup(MachineEntry, unique: true);
        Assert.Equal(RemovalDecision.AlreadyDisabled, panel.SelectedRow.Decision);
        Assert.True(panel.ShowStartupAction);

        panel.SelectedRow = RemovalPolicy.Startup(MachineEntry, unique: false);
        Assert.False(panel.ShowStartupAction);
    }

    private static void SetSnapshot(StartupViewModel startup, DateTime readAt, params StartupEntry[] entries)
    {
        startup.Items.Clear();
        foreach (var entry in entries) startup.Items.Add(new StartupItem(entry));
        startup.CheckedAt = readAt;
    }

    private static string[] VisibleKeys(StartupViewModel startup) => startup.ItemsView.Cast<StartupItem>().Select(i => i.Key).ToArray();

    private static StartupEntry Entry(StartupSource source) => new()
    {
        Source = source, Name = "Launch", DisplayName = "Launcher", Command = "fixture.exe",
        TargetPath = @"C:\Fixture\fixture.exe", TargetExists = true, Enabled = source == StartupSource.RunUser,
    };

    private static Task Sta(Func<Task> body) => StaWorker.Run(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var task = body();
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
                    TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
            return true;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }, "Startup focus test");
}
