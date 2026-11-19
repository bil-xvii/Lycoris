using System.Windows.Threading;
using Lycoris.Core;
using Lycoris.Modules.Restore;
using Lycoris.Modules.Startup;
using Lycoris.Shell;
using Lycoris.UI;

namespace Lycoris.Tests;

public class RestoreFocusNavigationTests
{
    private const string DriftLink = CommandPaletteIndex.RestoreSectionPrefix + "drift";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public Task First_visit_waits_for_the_drift_read_and_opens_only_matching_records() => Sta(async () =>
    {
        var fixture = new JournalFixture();
        var read = PendingRead();
        var vm = fixture.Create(() => read.Task);
        var focused = NextSection(vm);

        Ui.RequestFocus(vm.Key, DriftLink);
        await vm.ActivateAsync();

        AssertChecking(vm);
        read.SetResult([Drift(fixture.First)]);

        Assert.Equal("drift", await focused.WaitAsync(Timeout));
        AssertOnlyDrift(vm, fixture.First);
    });

    [Fact]
    public Task Returning_to_restore_uses_a_fresh_result_instead_of_showing_cached_matches() => Sta(async () =>
    {
        var fixture = new JournalFixture();
        var read = PendingRead();
        var next = Task.FromResult<IReadOnlyList<DriftItem>>([Drift(fixture.First)]);
        var vm = fixture.Create(() => next);
        await vm.ActivateAsync();
        Assert.Equal(1, vm.DriftCount);
        vm.Leave();
        next = read.Task;
        var focused = NextSection(vm);

        Ui.RequestFocus(vm.Key, DriftLink);
        await vm.ActivateAsync();

        AssertChecking(vm);
        read.SetResult([Drift(fixture.Second)]);
        Assert.Equal("drift", await focused.WaitAsync(Timeout));
        AssertOnlyDrift(vm, fixture.Second);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Empty_or_failed_reads_do_not_show_the_unfiltered_journal(bool failed) => Sta(async () =>
    {
        var fixture = new JournalFixture();
        var read = PendingRead();
        var vm = fixture.Create(() => read.Task);
        var opening = vm.ApplyFocusAsync(DriftLink);
        AssertChecking(vm);

        if (failed) read.SetException(new InvalidOperationException("fixture read failed"));
        else read.SetResult([]);
        await opening.WaitAsync(Timeout);

        Assert.Equal("drift", vm.FocusedSection);
        Assert.Equal(JournalFilter.Drifted, vm.Filter);
        Assert.True(vm.IsFilterEmpty);
        Assert.Empty(vm.Batches);
        Assert.False(vm.HasDrift);
        Assert.Equal(failed
            ? Loc.T("Не удалось проверить изменения вне Lycoris. Нажмите «ОБНОВИТЬ», чтобы повторить проверку.")
            : Loc.T("Все настройки Lycoris на месте — вне Lycoris ничего не изменено."), vm.FilterEmptyText);
    });

    [Fact]
    public Task Manual_filter_during_the_check_keeps_its_selection_and_cancels_the_scroll() => Sta(async () =>
    {
        var fixture = new JournalFixture();
        var read = PendingRead();
        var vm = fixture.Create(() => read.Task);
        var opening = vm.ApplyFocusAsync(DriftLink);

        vm.Filter = JournalFilter.Reverted;
        read.SetResult([Drift(fixture.First)]);
        await opening.WaitAsync(Timeout);

        Assert.Equal(JournalFilter.Reverted, vm.Filter);
        Assert.Equal(fixture.Reverted.Id, Assert.Single(vm.Batches.SelectMany(batch => batch.Records)).Record.Id);
        Assert.True(vm.HasDrift);
        Assert.Null(vm.FocusedSection);
    });

    [Fact]
    public Task A_new_record_link_keeps_its_target_when_the_older_drift_check_finishes() => Sta(async () =>
    {
        var fixture = new JournalFixture();
        var read = PendingRead();
        var vm = fixture.Create(() => read.Task);
        var opening = vm.ApplyFocusAsync(DriftLink);

        await vm.ApplyFocusAsync(CommandPaletteIndex.RestoreSectionPrefix + $"record:{fixture.Second.Id:D}");
        read.SetResult([Drift(fixture.First)]);
        await opening.WaitAsync(Timeout);

        Assert.Equal(JournalFilter.All, vm.Filter);
        Assert.Equal("journal", vm.FocusedSection);
        Assert.Equal(fixture.Second.Id.ToString(), vm.FocusedRecordId);
        Assert.True(Assert.Single(vm.Batches, batch => batch.Records.Any(row => row.Record.Id == fixture.Second.Id)).IsExpanded);
    });

    [Theory]
    [InlineData("last-batch", JournalFilter.Active)]
    [InlineData("journal", JournalFilter.Drifted)]
    [InlineData("restore-points", JournalFilter.Drifted)]
    public Task Another_section_keeps_its_scroll_target_when_drift_finishes(string section, JournalFilter filter) => Sta(async () =>
    {
        var fixture = new JournalFixture();
        var read = PendingRead();
        var vm = fixture.Create(() => read.Task);
        var opening = vm.ApplyFocusAsync(DriftLink);

        await vm.ApplyFocusAsync(CommandPaletteIndex.RestoreSectionPrefix + section);
        read.SetResult([Drift(fixture.First)]);
        await opening.WaitAsync(Timeout);

        Assert.Equal(section, vm.FocusedSection);
        Assert.Equal(filter, vm.Filter);
        if (section == "last-batch") Assert.True(vm.Batches[0].IsExpanded);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_new_drift_request_waits_for_its_own_read_even_if_the_earlier_read_fails(bool failed) => Sta(async () =>
    {
        var fixture = new JournalFixture();
        var first = PendingRead();
        var latest = PendingRead();
        var latestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = first.Task;
        var vm = fixture.Create(() =>
        {
            if (next == latest.Task) latestStarted.TrySetResult();
            return next;
        });
        var opening = vm.ApplyFocusAsync(DriftLink);
        next = latest.Task;
        _ = vm.ApplyFocusAsync(DriftLink);

        if (failed) first.SetException(new InvalidOperationException("earlier read failed"));
        else first.SetResult([Drift(fixture.First)]);
        await latestStarted.Task.WaitAsync(Timeout);

        AssertChecking(vm);
        latest.SetResult([Drift(fixture.Second)]);
        await opening.WaitAsync(Timeout);
        Assert.Equal("drift", vm.FocusedSection);
        AssertOnlyDrift(vm, fixture.Second);
    });

    [Fact]
    public Task A_historical_startup_disable_shows_the_explanation_and_the_owner_link() => Sta(async () =>
    {
        var journal = ChangeJournal.ForTests();
        var batch = journal.BeginBatch("Startup fixture");
        var record = journal.Add(new ChangeRecord
        {
            BatchId = batch.Id, Title = "Old startup entry", Kind = ChangeKind.Registry,
            Source = DriftDetector.StartupSource, NewValue = "Binary: 03",
        }).Record;
        var drift = new DriftItem(record, "disabled", "enabled", DriftFix.StartupEntry, null, "startup");
        var vm = new RestoreViewModel(journal, _ => Task.FromResult<IReadOnlyList<DriftItem>>([drift]), () => Task.CompletedTask);

        await vm.ApplyFocusAsync(DriftLink);

        var row = Assert.Single(Assert.Single(vm.Batches).Records);
        Assert.False(vm.HasReapplicableDrift);
        Assert.False(row.CanReapply);
        Assert.True(row.CanShowOwner);
        Assert.Contains(Loc.T("В журнале нет привязки к текущей программе или задаче. Откройте раздел «Автозапуск» и проверьте запись перед отключением."), row.Note);
    });

    private static TaskCompletionSource<IReadOnlyList<DriftItem>> PendingRead() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<string> NextSection(RestoreViewModel vm)
    {
        var section = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RestoreViewModel.FocusedSection) && vm.FocusedSection is { } name)
                section.TrySetResult(name);
        };
        return section.Task;
    }

    private static void AssertChecking(RestoreViewModel vm)
    {
        Assert.Equal(JournalFilter.Drifted, vm.Filter);
        Assert.Empty(vm.Batches);
        Assert.False(vm.HasDrift);
        Assert.Null(vm.FocusedSection);
        Assert.Equal(Loc.T("Проверка…"), vm.FilterEmptyText);
    }

    private static void AssertOnlyDrift(RestoreViewModel vm, ChangeRecord expected)
    {
        Assert.Equal(JournalFilter.Drifted, vm.Filter);
        Assert.True(vm.HasDrift);
        Assert.Equal(1, vm.DriftCount);
        var batch = Assert.Single(vm.Batches);
        Assert.True(batch.IsExpanded);
        var row = Assert.Single(batch.Records);
        Assert.Equal(expected.Id, row.Record.Id);
        Assert.True(row.IsDrifted);
    }

    private static DriftItem Drift(ChangeRecord r) => new(r, "applied", "changed", DriftFix.None, null, null);

    private sealed class JournalFixture
    {
        private readonly ChangeJournal _journal = ChangeJournal.ForTests();
        public ChangeRecord First { get; }
        public ChangeRecord Second { get; }
        public ChangeRecord Reverted { get; }

        public JournalFixture()
        {
            First = Add("first");
            Second = Add("second");
            Reverted = Add("reverted", reverted: true);
        }

        public RestoreViewModel Create(Func<Task<IReadOnlyList<DriftItem>>> read) =>
            new(_journal, _ => read(), () => Task.CompletedTask);

        private ChangeRecord Add(string title, bool reverted = false)
        {
            var batch = _journal.BeginBatch(title);
            return _journal.Add(new ChangeRecord { BatchId = batch.Id, Target = "fixture:" + title, Title = title, Reverted = reverted }).Record;
        }
    }

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
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
            return true;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }, "Restore focus test");
}
