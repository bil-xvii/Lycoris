using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Lycoris.Core;

namespace Lycoris.Tests.Core;

[Collection("Registry")]
public sealed class JournalWriterLeaseTests
{
    [Fact]
    public void The_lock_is_global_and_test_roots_get_separate_names()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "LycorisWriter");
        var normal = JournalWriterLease.NameFor(null);
        var isolated = JournalWriterLease.NameFor(testRoot);

        Assert.StartsWith(@"Global\", normal);
        Assert.StartsWith(normal + ".", isolated);
        Assert.Equal(isolated, JournalWriterLease.NameFor(testRoot.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.NotEqual(isolated, JournalWriterLease.NameFor(testRoot + "-other"));
    }

    [Fact]
    public void The_existing_mutex_must_have_a_trusted_owner_and_protected_nonpublic_access()
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var expected = JournalWriterLease.CreateSecurity();
        Assert.True(JournalWriterLease.IsTrusted(expected));

        var untrustedOwner = JournalWriterLease.CreateSecurity();
        untrustedOwner.SetOwner(users);
        Assert.False(JournalWriterLease.IsTrusted(untrustedOwner));

        var publicWait = JournalWriterLease.CreateSecurity();
        publicWait.AddAccessRule(new MutexAccessRule(users, MutexRights.Synchronize, AccessControlType.Allow));
        Assert.False(JournalWriterLease.IsTrusted(publicWait));

        var unprotected = JournalWriterLease.CreateSecurity();
        unprotected.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        Assert.False(JournalWriterLease.IsTrusted(unprotected));
    }

    [Fact]
    public void Contention_refuses_immediately_and_an_abandoned_mutex_can_be_acquired()
    {
        var name = $@"Local\Lycoris.WriterTest.{Guid.NewGuid():N}";
        using var holder = new Mutex(false, name);
        using var contender = new Mutex(false, name);
        Assert.True(JournalWriterLease.TryEnter(holder));
        try
        {
            Assert.False(TryOnAnotherThread(contender));
        }
        finally { holder.ReleaseMutex(); }

        Assert.True(TryOnAnotherThread(contender, abandon: true));
        Assert.True(JournalWriterLease.TryEnter(holder));
        holder.ReleaseMutex();
    }

    [Fact]
    public async Task Ownership_survives_dispatcher_exit_while_a_journal_write_is_still_pending()
    {
        var name = $@"Local\Lycoris.WriterTest.{Guid.NewGuid():N}";
        var timeout = TimeSpan.FromSeconds(5);
        using var contender = new Mutex(false, name);
        using var closeDispatcher = new ManualResetEventSlim();
        var acquired = new TaskCompletionSource<JournalWriterLease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new Thread(() =>
        {
            acquired.SetResult(JournalWriterLease.TryAcquireForTests(() => new Mutex(false, name), timeout, out _));
            closeDispatcher.Wait(timeout);
        }) { IsBackground = true };
        dispatcher.Start();

        using var fixture = Assert.IsType<JournalWriterLease>(await acquired.Task.WaitAsync(timeout));
        using var dir = new TempJournalDirectory();
        var writer = ChangeJournal.ForTests(dir.Journal);
        var beforeWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permitWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.Run(async () =>
        {
            beforeWrite.SetResult();
            if (await permitWrite.Task) writer.BeginBatch("late writer batch");
        });

        try
        {
            await beforeWrite.Task.WaitAsync(timeout);
            closeDispatcher.Set();
            Assert.True(dispatcher.Join(timeout));
            Assert.Same(fixture, JournalWriterLease.Current);
            Assert.False(TryOnAnotherThread(contender));

            permitWrite.SetResult(true);
            await pending.WaitAsync(timeout);
            Assert.Equal("late writer batch", Assert.Single(ChangeJournal.ForTests(dir.Journal).Batches).Title);
            Assert.False(TryOnAnotherThread(contender));
        }
        finally
        {
            closeDispatcher.Set();
            permitWrite.TrySetResult(false);
            await pending.WaitAsync(timeout);
        }

        fixture.Dispose();
        Assert.True(TryOnAnotherThread(contender));
    }

    [Fact]
    public async Task A_timed_out_start_cannot_publish_a_late_owner()
    {
        var name = $@"Local\Lycoris.WriterTest.{Guid.NewGuid():N}";
        var timeout = TimeSpan.FromSeconds(5);
        using var contender = new Mutex(false, name);
        using var openMutex = new ManualResetEventSlim();
        var owner = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var late = JournalWriterLease.TryAcquireForTests(() =>
            {
                owner.SetResult(Thread.CurrentThread);
                openMutex.Wait(timeout);
                return new Mutex(false, name);
            }, TimeSpan.Zero, out var busy);
            Assert.Null(late);
            Assert.False(busy);

            var thread = await owner.Task.WaitAsync(timeout);
            openMutex.Set();
            Assert.True(thread.Join(timeout));
            Assert.Null(JournalWriterLease.Current);
            Assert.True(TryOnAnotherThread(contender));
        }
        finally
        {
            openMutex.Set();
            JournalWriterLease.Current?.Dispose();
        }
    }

    private static bool TryOnAnotherThread(Mutex mutex, bool abandon = false)
    {
        var acquired = false;
        var thread = new Thread(() =>
        {
            acquired = JournalWriterLease.TryEnter(mutex);
            if (acquired && !abandon) mutex.ReleaseMutex();
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        return acquired;
    }

    [Fact]
    public void A_stale_protected_reader_cannot_save_or_reuse_a_merged_record()
    {
        using var dir = new TempJournalDirectory();
        var writer = ChangeJournal.ForTests(dir.Journal);
        var batch = writer.BeginBatch("original batch");
        var existing = new ChangeRecord { BatchId = batch.Id, Source = "startup", Target = "temporary test target" };
        writer.Add(existing);
        Assert.Null(JournalWriterLease.Current);
        var observer = ChangeJournal.ProtectedReaderForTests(dir.Journal);
        writer.BeginBatch("more recent batch");
        var current = File.ReadAllBytes(dir.Journal);
        var backup = File.ReadAllBytes(dir.Journal + ".bak");

        var previousBypass = ChangeSession.BypassElevationForTests;
        try
        {
            ChangeSession.BypassElevationForTests = true;
            Assert.Single(observer.Batches);
            Assert.False(observer.CanWrite);
            Assert.Throws<InvalidOperationException>(() => observer.Add(existing));
            Assert.False(observer.Save());
            Assert.Equal(current, File.ReadAllBytes(dir.Journal));
            Assert.Equal(backup, File.ReadAllBytes(dir.Journal + ".bak"));
        }
        finally { ChangeSession.BypassElevationForTests = previousBypass; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_protected_reader_never_repairs_or_moves_damaged_files(bool validBackup)
    {
        using var dir = new TempJournalDirectory();
        File.WriteAllText(dir.Journal, "broken main journal");
        var backup = dir.Journal + ".bak";
        if (validBackup) ChangeJournal.ForTests(backup).BeginBatch("backup batch");
        else File.WriteAllText(backup, "broken backup");
        var originalBackup = File.ReadAllBytes(backup);

        var observer = ChangeJournal.ProtectedReaderForTests(dir.Journal);

        Assert.Equal(!validBackup, observer.IsUnavailable);
        if (validBackup) Assert.Equal("backup batch", Assert.Single(observer.Batches).Title);
        else Assert.Empty(observer.Batches);
        Assert.False(observer.StartNewJournal());
        Assert.Equal("broken main journal", File.ReadAllText(dir.Journal));
        Assert.Equal(originalBackup, File.ReadAllBytes(backup));
        Assert.Equal(new[] { dir.Journal, backup }, Directory.GetFiles(dir.Path).Order(StringComparer.Ordinal));
    }

    private sealed class TempJournalDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("LycorisWriterTest-").FullName;
        public string Journal => System.IO.Path.Combine(Path, "journal.json");
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
