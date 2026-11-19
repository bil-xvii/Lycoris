using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Lycoris.Core;
using Lycoris.Modules.Removal;
using Lycoris.Modules.Startup;
using Lycoris.Shell;
using Lycoris.Tests.Concurrency;
using Lycoris.UI;

namespace Lycoris.Tests;

[CollectionDefinition(nameof(RemovalBatchViewModelTests), DisableParallelization = true)]
public sealed class RemovalBatchViewModelCollection { }

[Collection(nameof(RemovalBatchViewModelTests))]
public sealed class RemovalBatchViewModelTests
{
    [Fact]
    public async Task Windows_apps_preview_requires_individual_selection_and_confirms_exact_package()
    {
        using var language = Loc.UseLanguage("en");
        const string fullName = "ThirdParty.App_2.1.0.0_x64__publisher";
        const string bundle = "ThirdParty.App_2.1.0.0_neutral_~_publisher";
        var packaged = new FakeAction(RemovalBatchKind.UninstallWindowsApps)
        {
            Plan = new TestPlan([
                new RemovalBatchItem(fullName, "Third-party app", "Desktop account", "")
                {
                    NativeInstallerId = fullName,
                    RelatedPackageIds = [bundle],
                },
                new RemovalBatchItem("other", "Another app", "Desktop account", ""),
            ]),
            Result = new(0, 0, []) { Pending = 1 },
        };
        var confirmations = 0;
        var page = Page(confirm: (kind, details) =>
        {
            confirmations++;
            Assert.Equal(RemovalBatchKind.UninstallWindowsApps, kind);
            Assert.Contains(fullName, Assert.Single(details));
            Assert.Contains(bundle, details[0]);
            Assert.False(OperationCoordinator.InsideOperation);
            return Task.FromResult(true);
        }, windowsAppsBatch: packaged, desktopSid: () => "desktop-user");

        await page.PrepareWindowsAppsUninstallCommand.ExecuteAsync(null);

        Assert.True(page.IsUninstallPreview);
        Assert.Equal("Windows apps to uninstall", page.BatchPreviewTitle);
        Assert.Equal(0, page.BatchSelectedCount);
        Assert.False(page.SelectAllBatchCommand.CanExecute(null));
        Assert.False(page.ExecuteBatchCommand.CanExecute(null));
        page.BatchRows[0].IsSelected = true;
        await page.ExecuteBatchCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmations);
        Assert.Equal(fullName, Assert.Single(packaged.ExecutedKeys!));
        Assert.True(packaged.ExecutedWithLease);
        Assert.Contains("Windows accepted the request and the app is still registered.", page.BatchMessage);
    }

    [Fact]
    public async Task Windows_apps_abort_after_confirm_if_desktop_account_changes()
    {
        var packaged = new FakeAction(RemovalBatchKind.UninstallWindowsApps, "optional");
        string? desktop = "desktop-user";
        var page = Page(confirm: (_, _) =>
        {
            desktop = null;
            return Task.FromResult(true);
        }, windowsAppsBatch: packaged, desktopSid: () => desktop);

        await page.PrepareWindowsAppsUninstallCommand.ExecuteAsync(null);
        page.BatchRows[0].IsSelected = true;
        await page.ExecuteBatchCommand.ExecuteAsync(null);

        Assert.Equal(0, packaged.ExecuteCalls);
        Assert.False(page.HasBatchPreview);
    }

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
        await page.AutoUninstallCommand.ExecuteAsync(null);

        Assert.Empty(page.BatchRows);
        Assert.False(page.HasBatchPreview);
        Assert.False(page.IsBusy);
        Assert.False(page.ExecuteBatchCommand.CanExecute(null));
        Assert.True(page.PrepareUninstallCommand.CanExecute(null));
        Assert.Equal(cancelled ? "Candidate selection was cancelled." : "Could not find candidates. Try again.", page.BatchMessage);
        Assert.Null(uninstall.ExecutedPlan);
    }

    [Fact]
    public Task Wpf_preview_and_primary_button_use_real_bindings_and_exact_confirmation() => StaWorker.Run(() =>
    {
        using var language = Loc.UseLanguage("en");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previousHost = Ui.Host;
        var previousAnswer = Ui.AutoAnswer;
        var previousPreference = AppSettings.Current.ConfirmBeforeApply;
        var previousContext = SynchronizationContext.Current;
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        try
        {
            var host = new DialogHost();
            Ui.Host = host;
            Ui.AutoAnswer = null;
            AppSettings.Current.ConfirmBeforeApply = false;
            foreach (var theme in new[] { "Colors", "Brand", "Typography", "Converters", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Lycoris;component/Theme/{theme}.xaml"),
                });
            var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc", "obs");
            uninstall.Plan = new TestPlan([
                new("vlc", "vlc", "Machine", "Eligible") { Version = "3.0", NativeInstallerId = @"ARP\Machine\X64\vlc" },
                new("obs", "obs", "Machine", "Eligible") { Version = "32.0", NativeInstallerId = @"ARP\Machine\X64\obs" },
            ]);
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

            var primary = FindButton(view, "RemovalAutoUninstall");
            Assert.True(primary.IsEnabled);
            primary.Command.Execute(primary.CommandParameter);
            var dialog = Assert.IsType<DialogViewModel>(host.Dialog);
            Assert.True(dialog.IsDanger);
            Assert.Equal("Uninstall selected · 2", dialog.ConfirmText);
            Assert.Equal([@"vlc · 3.0 · Machine · ARP\Machine\X64\vlc", @"obs · 32.0 · Machine · ARP\Machine\X64\obs"], dialog.Details);
            Assert.Empty(page.Coordinator.Snapshot());
            Assert.Null(uninstall.ExecutedPlan);
            Assert.False(primary.IsEnabled);

            dialog.ConfirmCommand.Execute(null);
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(page.AutoUninstallCommand.ExecutionTask?.IsCompletedSuccessfully);
            Assert.Equal(1, uninstall.ExecuteCalls);
            Assert.Equal(["obs", "vlc"], uninstall.ExecutedKeys!.Order());
            Assert.False(page.HasBatchPreview);
            Assert.True(primary.IsEnabled);
            return true;
        }
        finally
        {
            Ui.Host?.Dialog?.CancelCommand.Execute(null);
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Ui.Host = previousHost;
            Ui.AutoAnswer = previousAnswer;
            AppSettings.Current.ConfirmBeforeApply = previousPreference;
            SynchronizationContext.SetSynchronizationContext(previousContext);
            app.Shutdown();
            dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }, "Removal preview binding test");

    [Fact]
    public async Task One_startup_click_checks_all_candidates_and_cancel_leaves_only_an_editable_preview()
    {
        var disable = new FakeAction(RemovalBatchKind.DisableStartup, "run:steam", "run:zoom");
        var shown = new List<string[]>();
        var page = Page(disable: disable, confirm: (kind, details) =>
        {
            Assert.Equal(RemovalBatchKind.DisableStartup, kind);
            Assert.False(OperationCoordinator.InsideOperation);
            shown.Add(details.ToArray());
            return Task.FromResult(shown.Count > 1);
        });
        page.SearchText = "hidden by search";

        await page.AutoDisableStartupCommand.ExecuteAsync(null);

        Assert.True(page.IsStartupTab);
        Assert.True(page.CanEditBatchPreview);
        Assert.Equal(["run:steam · Machine", "run:zoom · Machine"], Assert.Single(shown));
        Assert.All(page.BatchRows, row => Assert.True(row.IsSelected));
        Assert.Null(disable.ExecutedPlan);
        page.BatchRows[0].IsSelected = false;
        Assert.Null(disable.ExecutedPlan);

        await page.ExecuteBatchCommand.ExecuteAsync(null);

        Assert.Equal(["run:zoom · Machine"], shown[1]);
        Assert.Equal("run:zoom", Assert.Single(disable.ExecutedKeys!));
        Assert.Equal(1, disable.PrepareCalls);
        Assert.Equal(1, disable.ExecuteCalls);
    }

    [Fact]
    public async Task Primary_with_zero_candidates_shows_excluded_rows_and_never_opens_confirmation()
    {
        using var language = Loc.UseLanguage("en");
        var uninstall = new FakeAction(RemovalBatchKind.Uninstall)
        {
            Plan = new TestPlan([])
            {
                Summary = "Checked 1 program.",
                AssessedRows = [new("protected", "Runtime", "Microsoft", "3.0", "Machine", RemovalDecision.Keep, "Shared runtime.")],
            },
        };
        var dialogs = 0;
        var page = Page(uninstall, confirm: (_, _) =>
        {
            dialogs++;
            return Task.FromResult(true);
        });

        await page.AutoUninstallCommand.ExecuteAsync(null);

        Assert.Equal("Shared runtime.", Assert.Single(page.VisibleRows).Reason);
        Assert.Equal("Checked 1 program.\nNo programs qualify for batch uninstall.", page.BatchMessage);
        Assert.False(page.HasBatchPreview);
        Assert.Equal(0, dialogs);
        Assert.Null(uninstall.ExecutedPlan);
        Assert.False(page.IsBusy);
    }

    [Theory]
    [InlineData(AdminIntent.AutoUninstall, false)]
    [InlineData(AdminIntent.AutoDisableStartup, true)]
    public async Task Elevation_replays_only_the_named_action_with_a_fresh_plan(string command, bool startup)
    {
        var action = new FakeAction(startup ? RemovalBatchKind.DisableStartup : RemovalBatchKind.Uninstall, "old");
        AdminIntent? prompt = null;
        var before = Page(startup ? null : action, startup ? action : null, requireAdmin: (_, replay) =>
        {
            Assert.Equal(command, replay);
            prompt = new AdminIntent("removal", replay!);
            return false;
        });
        await (startup ? before.AutoDisableStartupCommand : before.AutoUninstallCommand).ExecuteAsync(null);
        Assert.Equal(0, action.PrepareCalls);
        Assert.Null(action.ExecutedPlan);
        var parsed = Assert.IsType<AdminIntent>(AdminIntent.Parse(prompt!.ToArgs()));
        Assert.Null(parsed.Parameter);
        Assert.Null(AdminIntent.Parse((parsed with { Parameter = "old" }).ToArgs()));
        Assert.Null(AdminIntent.Parse((parsed with { PageKey = "apps" }).ToArgs()));

        var fresh = new FakeAction(action.Kind, "fresh");
        IReadOnlyList<string>? shown = null;
        var after = Page(startup ? null : fresh, startup ? fresh : null, confirm: (_, rows) =>
        {
            shown = rows;
            return Task.FromResult(true);
        });
        Assert.True(parsed.TryExecuteOn(after));
        await ((startup ? after.AutoDisableStartupCommand : after.AutoUninstallCommand).ExecutionTask!);
        Assert.Equal(["fresh · Machine"], shown);
        Assert.Equal("fresh", Assert.Single(fresh.ExecutedKeys!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_observation_or_changed_account_rejects_the_approved_batch(bool accountChanged)
    {
        var clock = new ManualTimeProvider();
        var sid = "desktop-user";
        var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc");
        uninstall.Plan = new TestPlan(uninstall.Plan.Items, Observed: clock.GetUtcNow().UtcDateTime);
        var page = Page(uninstall, clock: clock, userSid: () => sid, confirm: (_, _) =>
        {
            if (accountChanged) sid = "another-user";
            else clock.Advance(TimeSpan.FromMinutes(5));
            return Task.FromResult(true);
        });

        await page.AutoUninstallCommand.ExecuteAsync(null);

        Assert.Null(uninstall.ExecutedPlan);
        Assert.False(page.HasBatchPreview);
        Assert.False(page.IsBusy);
        Assert.Empty(page.Coordinator.Snapshot());
    }

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

        var applying = page.AutoUninstallCommand.ExecuteAsync(null);

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
    public async Task Primary_keeps_busy_through_publishing_the_preview_and_waiting_for_confirmation()
    {
        var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc");
        var disable = new FakeAction(RemovalBatchKind.DisableStartup, "run:steam");
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new TaskCompletionSource<RemovalBatchPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        uninstall.Preparing = prepared.Task;
        var page = Page(uninstall, disable, confirm: (_, _) => answer.Task);
        var transition = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(RemovalViewModel.HasBatchPreview) && page.HasBatchPreview)
                transition.TrySetResult(page.AutoDisableStartupCommand.ExecuteAsync(null));
        };

        var applying = page.AutoUninstallCommand.ExecuteAsync(null);
        Assert.False(page.AutoDisableStartupCommand.CanExecute(null));
        Assert.False(page.RefreshCommand.CanExecute(null));
        prepared.SetResult(uninstall.Plan);
        await (await transition.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await page.PrepareDisableStartupCommand.ExecuteAsync(null);
        await page.ExecuteBatchCommand.ExecuteAsync(null);
        Assert.True(page.IsBusy);
        Assert.Equal(0, disable.PrepareCalls);
        Assert.Null(uninstall.ExecutedPlan);

        answer.SetResult(true);
        await applying;
        Assert.Equal(1, uninstall.PrepareCalls);
        Assert.Equal(1, uninstall.ExecuteCalls);
        Assert.True(page.AutoDisableStartupCommand.CanExecute(null));
    }

    [Fact]
    public async Task Changes_while_waiting_for_the_operation_lease_reject_the_confirmed_plan()
    {
        var uninstall = new FakeAction(RemovalBatchKind.Uninstall, "vlc");
        var page = Page(uninstall, confirm: (_, _) => Task.FromResult(true));
        using var blocker = await page.Coordinator.AcquireAsync(new OperationRequest(OperationClass.Install, "Other installer"));
        var applying = page.AutoUninstallCommand.ExecuteAsync(null);
        Assert.Contains(page.Coordinator.Snapshot(), operation => operation is
            { Class: OperationClass.Install, Owner: "removal", State: OperationState.Queued });

        Assert.False(page.AutoDisableStartupCommand.CanExecute(null));
        Assert.False(page.PrepareDisableStartupCommand.CanExecute(null));
        Assert.False(page.RefreshCommand.CanExecute(null));
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
            Assert.False(page.AutoUninstallCommand.CanExecute(null));
            await page.AutoUninstallCommand.ExecuteAsync(null);
            Assert.Equal(1, uninstall.PrepareCalls);
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
        Func<RemovalBatchKind, IReadOnlyList<string>, Task<bool>>? confirm = null,
        Func<string, string?, bool>? requireAdmin = null, TimeProvider? clock = null, Func<string?>? userSid = null,
        FakeAction? windowsAppsBatch = null, Func<string?>? desktopSid = null) =>
        new(uninstall ?? new FakeAction(RemovalBatchKind.Uninstall), disable ?? new FakeAction(RemovalBatchKind.DisableStartup),
            requireAdmin ?? ((_, _) => true), confirm, clock, userSid ?? (() => "desktop-user"), windowsAppsBatch, desktopSid)
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

    private sealed record TestPlan(IReadOnlyList<RemovalBatchItem> Candidates, string? Reason = null, DateTime? Observed = null)
        : RemovalBatchPlan(Guid.NewGuid(), "desktop-user", Observed ?? DateTime.UtcNow, Candidates, Reason);

    private sealed class DialogHost : IUiHost
    {
        public DialogViewModel? Dialog { get; set; }
        public ObservableCollection<ToastItem> Toasts { get; } = [];
        public void NavigateTo(string pageKey) => throw new InvalidOperationException(pageKey);
        public bool RestartElevated(AdminIntent? intent = null) => throw new InvalidOperationException();
        public bool RestartSameRights() => throw new InvalidOperationException();
    }

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
