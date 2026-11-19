using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Lycoris.Core;
using Lycoris.Modules.Removal;
using Lycoris.Modules.Startup;
using Lycoris.UI;

namespace Lycoris.Tests;

[CollectionDefinition(nameof(RemovalBatchViewModelTests), DisableParallelization = true)]
public sealed class RemovalBatchViewModelCollection { }

[Collection(nameof(RemovalBatchViewModelTests))]
public sealed class RemovalBatchViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepare_selects_every_eligible_item_on_the_relevant_tab_despite_the_browser_filter(bool startup)
    {
        var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc", "obs");
        var disable = new FakeAction(RemovalBatchKind.DisableStartup, "run:steam", "run:zoom");
        var page = Page(uninstall, disable);
        page.TabIndex = startup ? 0 : 1;
        page.SearchText = "a search with no matches";
        page.SelectedFilter = page.Filters.Single(filter => filter.Decision == RemovalDecision.Keep);

        await (startup ? page.PrepareDisableStartupCommand : page.PrepareUninstallCommand).ExecuteAsync(null);

        Assert.True(page.HasBatchPreview);
        Assert.Equal(startup, page.IsStartupTab);
        Assert.Equal(!startup, page.IsUninstallPreview);
        Assert.Empty(page.VisibleRows);
        Assert.Equal(startup ? new[] { "run:steam", "run:zoom" } : ["vlc", "obs"],
            page.BatchRows.Select(row => row.Item.Key));
        Assert.All(page.BatchRows, row => Assert.True(row.IsSelected));
        Assert.Equal(2, page.BatchSelectedCount);
        Assert.Equal(startup ? 0 : 1, uninstall.PrepareCalls);
        Assert.Equal(startup ? 1 : 0, disable.PrepareCalls);
        Assert.Null(uninstall.ExecutedPlan);
        Assert.Null(disable.ExecutedPlan);
    }

    [Fact]
    public async Task Unavailable_or_empty_plan_clears_previous_preview_and_never_offers_execution()
    {
        using var language = Loc.UseLanguage("en");
        var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc");
        var page = Page(uninstall);
        await page.PrepareUninstallCommand.ExecuteAsync(null);
        Assert.True(page.HasBatchPreview);

        uninstall.Plan = new TestPlan(uninstall.Plan.Items, "Can't verify all inventory sources.");
        await page.PrepareUninstallCommand.ExecuteAsync(null);
        Assert.Empty(page.BatchRows);
        Assert.False(page.HasBatchPreview);
        Assert.False(page.ExecuteBatchCommand.CanExecute(null));
        Assert.Equal("Can't verify all inventory sources.", page.BatchMessage);

        await page.PrepareDisableStartupCommand.ExecuteAsync(null);
        Assert.Equal("No startup entries qualify for batch disabling.", page.BatchMessage);
        Assert.False(page.ExecuteBatchCommand.CanExecute(null));
        Assert.Null(uninstall.ExecutedPlan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_preparation_reports_why_and_leaves_no_old_preview(bool cancelled)
    {
        using var language = Loc.UseLanguage("en");
        var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc");
        var page = Page(uninstall);
        await page.PrepareUninstallCommand.ExecuteAsync(null);
        Assert.True(page.HasBatchPreview);

        uninstall.Preparing = cancelled
            ? Task.FromCanceled<RemovalBatchPlan>(new CancellationToken(canceled: true))
            : Task.FromException<RemovalBatchPlan>(new InvalidOperationException("read failed"));
        await page.PrepareUninstallCommand.ExecuteAsync(null);

        Assert.Empty(page.BatchRows);
        Assert.False(page.HasBatchPreview);
        Assert.False(page.IsBusy);
        Assert.False(page.ExecuteBatchCommand.CanExecute(null));
        Assert.True(page.PrepareUninstallCommand.CanExecute(null));
        Assert.Equal(cancelled ? "Candidate selection was cancelled." : "Could not find candidates. Try again.", page.BatchMessage);
        Assert.Null(uninstall.ExecutedPlan);
    }

    [Fact]
    public Task Wpf_preview_shows_prechecked_candidates_and_updates_execute_count() => StaWorker.Run(() =>
    {
        using var language = Loc.UseLanguage("en");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            foreach (var theme in new[] { "Colors", "Brand", "Typography", "Converters", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Lycoris;component/Theme/{theme}.xaml"),
                });
            var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc", "obs");
            var page = Page(uninstall);
            var view = new RemovalView { DataContext = page };
            var size = new Size(700, 570);
            view.Measure(size);
            view.Arrange(new Rect(size));
            view.UpdateLayout();

            var prepare = FindButton(view, "RemovalPrepareUninstall");
            Assert.True(prepare.IsEnabled);
            prepare.Command.Execute(prepare.CommandParameter);
            Assert.True(page.PrepareUninstallCommand.ExecutionTask?.IsCompletedSuccessfully);
            view.UpdateLayout();

            var rows = Descendants(view).OfType<CheckBox>().Where(box => box.DataContext is RemovalBatchPreviewRow).ToArray();
            Assert.Equal(["vlc", "obs"], rows.Select(AutomationProperties.GetName));
            Assert.All(rows, box => Assert.True(box.IsChecked));
            var execute = FindButton(view, "RemovalExecuteBatch");
            Assert.Equal("Uninstall selected · 2", execute.Content);
            Assert.Same(page.ExecuteBatchCommand, execute.Command);
            Assert.True(execute.IsEnabled);
            rows[1].SetCurrentValue(CheckBox.IsCheckedProperty, false);

            Assert.False(page.BatchRows[1].IsSelected);
            Assert.Equal("Uninstall selected · 1", execute.Content);
            Assert.Null(uninstall.ExecutedPlan);
            return true;
        }
        finally
        {
            app.Shutdown();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }, "Removal preview binding test");

    [Fact]
    public async Task Editing_and_confirming_submits_only_the_rows_shown_in_confirmation()
    {
        var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc", "obs", "7zip");
        IReadOnlyList<string>? shown = null;
        var page = Page(uninstall, confirm: (kind, details) =>
        {
            Assert.Equal(RemovalBatchKind.Uninstall, kind);
            Assert.False(OperationCoordinator.InsideOperation);
            shown = details;
            return Task.FromResult(true);
        });

        await page.PrepareUninstallCommand.ExecuteAsync(null);
        page.ClearBatchSelectionCommand.Execute(null);
        Assert.Equal(0, page.BatchSelectedCount);
        Assert.False(page.ExecuteBatchCommand.CanExecute(null));
        page.SelectAllBatchCommand.Execute(null);
        Assert.Equal(3, page.BatchSelectedCount);
        page.BatchRows[1].IsSelected = false;

        await page.ExecuteBatchCommand.ExecuteAsync(null);

        Assert.Equal(["vlc · Machine", "7zip · Machine"], shown);
        Assert.Same(uninstall.Plan, uninstall.ExecutedPlan);
        Assert.Equal(["7zip", "vlc"], uninstall.ExecutedKeys!.Order());
        Assert.True(uninstall.ExecutedWithLease);
        Assert.False(page.HasBatchPreview);
    }

    [Fact]
    public async Task Cancelling_the_standard_dialog_or_dismissing_the_preview_never_executes()
    {
        using var language = Loc.UseLanguage("en");
        var previousAnswer = Ui.AutoAnswer;
        var answeredCount = Ui.AutoAnswered.Count;
        try
        {
            Ui.AutoAnswer = false;
            var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc");
            var page = Page(uninstall);
            await page.PrepareUninstallCommand.ExecuteAsync(null);

            await page.ExecuteBatchCommand.ExecuteAsync(null);

            Assert.Equal("Batch uninstall", Assert.Single(Ui.AutoAnswered.Skip(answeredCount)));
            Assert.True(page.HasBatchPreview);
            Assert.Null(uninstall.ExecutedPlan);
            page.CancelBatchPreviewCommand.Execute(null);
            Assert.Empty(page.BatchRows);
            Assert.False(page.ExecuteBatchCommand.CanExecute(null));
            Assert.Null(uninstall.ExecutedPlan);
        }
        finally
        {
            Ui.AutoAnswer = previousAnswer;
            Ui.AutoAnswered.RemoveRange(answeredCount, Ui.AutoAnswered.Count - answeredCount);
        }
    }

    [Theory]
    [InlineData("tab")]
    [InlineData("refresh")]
    [InlineData("mode")]
    [InlineData("selection")]
    [InlineData("leave")]
    public async Task Changes_during_confirmation_invalidate_the_whole_batch(string change)
    {
        using var language = Loc.UseLanguage("en");
        var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc", "obs");
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = Page(uninstall, confirm: (_, _) => answer.Task);
        await page.PrepareUninstallCommand.ExecuteAsync(null);

        var applying = page.ExecuteBatchCommand.ExecuteAsync(null);

        Assert.True(page.IsBusy);
        Assert.Empty(page.Coordinator.Snapshot());
        switch (change)
        {
            case "tab":
                page.TabIndex = 1;
                page.TabIndex = 0;
                break;
            case "refresh":
                await page.RefreshCommand.ExecuteAsync(null);
                break;
            case "mode":
                page.RefreshExpertMode();
                break;
            case "selection":
                page.BatchRows[0].IsSelected = false;
                page.BatchRows[0].IsSelected = true;
                break;
            case "leave":
                page.Leave();
                break;
        }
        answer.SetResult(true);
        await applying;

        Assert.Null(uninstall.ExecutedPlan);
        Assert.False(page.HasBatchPreview);
        Assert.False(page.IsBusy);
        Assert.Equal("The list or conditions changed. Find candidates again.", page.BatchMessage);
    }

    [Fact]
    public async Task Shared_guard_blocks_other_preparations_refresh_and_a_second_execute()
    {
        var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc");
        var disable = new FakeAction(RemovalBatchKind.DisableStartup, "run:steam");
        var prepared = new TaskCompletionSource<RemovalBatchPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        uninstall.Preparing = prepared.Task;
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmations = 0;
        var page = Page(uninstall, disable, confirm: (_, _) =>
        {
            confirmations++;
            return answer.Task;
        });
        var preparing = page.PrepareUninstallCommand.ExecuteAsync(null);
        Assert.False(page.PrepareDisableStartupCommand.CanExecute(null));
        Assert.False(page.RefreshCommand.CanExecute(null));
        await page.PrepareDisableStartupCommand.ExecuteAsync(null);
        Assert.Equal(0, disable.PrepareCalls);

        prepared.SetResult(uninstall.Plan);
        await preparing;
        var applying = page.ExecuteBatchCommand.ExecuteAsync(null);
        await page.ExecuteBatchCommand.ExecuteAsync(null);
        Assert.Equal(1, confirmations);
        answer.SetResult(true);
        await applying;
        Assert.Equal(1, uninstall.ExecuteCalls);
    }

    [Fact]
    public async Task Changes_while_waiting_for_the_operation_lease_reject_the_confirmed_plan()
    {
        var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc");
        var page = Page(uninstall, confirm: (_, _) => Task.FromResult(true));
        await page.PrepareUninstallCommand.ExecuteAsync(null);
        using var blocker = await page.Coordinator.AcquireAsync(new OperationRequest(OperationClass.Install, "Other installer"));
        var applying = page.ExecuteBatchCommand.ExecuteAsync(null);
        Assert.Contains(page.Coordinator.Snapshot(), operation => operation is
            { Class: OperationClass.Install, Owner: "removal", State: OperationState.Queued });

        page.TabIndex = 1;
        blocker.Dispose();
        await applying.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(uninstall.ExecutedPlan);
        Assert.Empty(page.Coordinator.Snapshot());
    }

    [Fact]
    public async Task Self_test_mode_allows_review_but_cannot_execute()
    {
        var previousLock = ChangeSession.Locked;
        try
        {
            ChangeSession.Locked = true;
            var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc");
            var page = Page(uninstall, confirm: (_, _) => throw new InvalidOperationException("No dialog in self-test"));

            await page.PrepareUninstallCommand.ExecuteAsync(null);
            Assert.True(Assert.Single(page.BatchRows).IsSelected);
            Assert.False(page.ExecuteBatchCommand.CanExecute(null));
            await page.ExecuteBatchCommand.ExecuteAsync(null);
            Assert.Null(uninstall.ExecutedPlan);
        }
        finally { ChangeSession.Locked = previousLock; }
    }

    [Fact]
    public async Task Partial_startup_result_shows_verified_counts_and_a_stop_on_unknown_outcome()
    {
        using var language = Loc.UseLanguage("en");
        var disable = new FakeAction(RemovalBatchKind.DisableStartup, "a", "b", "c", "d")
        {
            Result = new RemovalBatchResult(1, 2, ["Could not verify c."], UnknownOutcome: true),
        };
        var page = Page(disable: disable, confirm: (_, _) => Task.FromResult(true));
        disable.OnExecute = () => Assert.Contains(page.Coordinator.Snapshot(), operation => operation is
            { Class: OperationClass.JournalChange, Owner: "removal", State: OperationState.Running });
        await page.PrepareDisableStartupCommand.ExecuteAsync(null);

        await page.ExecuteBatchCommand.ExecuteAsync(null);

        Assert.Equal("Completed: 1 · Skipped: 2 · Error messages: 1\n" +
                     "The outcome could not be confirmed. Remaining actions were stopped.\n" +
                     "Could not verify c.", page.BatchMessage);
        Assert.Equal(1, disable.ExecuteCalls);
        Assert.False(page.HasBatchPreview);
        Assert.False(page.IsBusy);
    }

    private static RemovalViewModel Page(FakeAction? uninstall = null, FakeAction? disable = null,
        Func<RemovalBatchKind, IReadOnlyList<string>, Task<bool>>? confirm = null) =>
        new(uninstall ?? new FakeAction(RemovalBatchKind.Uninstall), disable ?? new FakeAction(RemovalBatchKind.DisableStartup), _ => true, confirm)
        {
            Coordinator = new OperationCoordinator(),
        };

    private static Button FindButton(DependencyObject root, string id) => Assert.Single(Descendants(root).OfType<Button>(),
        button => AutomationProperties.GetAutomationId(button) == id && button.Visibility == Visibility.Visible);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed record TestPlan(IReadOnlyList<RemovalBatchItem> Candidates, string? Reason = null)
        : RemovalBatchPlan(Guid.NewGuid(), "desktop-user", DateTime.UtcNow, Candidates, Reason);

    private sealed class FakeAction(RemovalBatchKind kind, params string[] keys) : IRemovalBatchAction
    {
        public RemovalBatchKind Kind { get; } = kind;
        public RemovalBatchPlan Plan { get; set; } = new TestPlan(keys.Select(key => new RemovalBatchItem(key, key, "Machine", "Eligible")).ToArray());
        public RemovalBatchResult Result { get; init; } = new(keys.Length, 0, []);
        public Task<RemovalBatchPlan>? Preparing { get; set; }
        public Action? OnExecute { get; set; }
        public int PrepareCalls { get; private set; }
        public int ExecuteCalls { get; private set; }
        public bool ExecutedWithLease { get; private set; }
        public RemovalBatchPlan? ExecutedPlan { get; private set; }
        public IReadOnlySet<string>? ExecutedKeys { get; private set; }

        public Task<RemovalBatchPlan> PrepareAsync(CancellationToken ct)
        {
            PrepareCalls++;
            return Preparing ?? Task.FromResult(Plan);
        }

        public Task<RemovalBatchResult> ExecuteAsync(RemovalBatchPlan plan, IReadOnlySet<string> selectedKeys,
            IProgress<string>? progress, CancellationToken ct)
        {
            ExecuteCalls++;
            ExecutedPlan = plan;
            ExecutedKeys = selectedKeys;
            ExecutedWithLease = OperationCoordinator.InsideOperation;
            OnExecute?.Invoke();
            return Task.FromResult(Result);
        }
    }
}
