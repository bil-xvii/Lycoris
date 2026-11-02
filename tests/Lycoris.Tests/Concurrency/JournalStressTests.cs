using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Lycoris.Core;

namespace Lycoris.Tests.Concurrency;

/// <summary>
/// Many sessions at once on distinct and on the same targets, with reverts in between — against a redirected registry
/// (HKCU\Software\Lycoris-Tests) and a temp journal. Nothing is lost, nothing throws, the journal file stays valid JSON, and
/// reverting everything puts every value back.
/// </summary>
[Collection("Registry")]
public sealed class JournalStressTests : IDisposable
{
    private readonly string _root = $@"Software\Lycoris-Tests\Stress-{Guid.NewGuid():N}";
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"lycoris-stress-{Guid.NewGuid():N}.json");

    public JournalStressTests()
    {
        RegistryHelper.Redirect = (hive, path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");
        ChangeSession.BypassElevationForTests = true;
    }

    public void Dispose()
    {
        RegistryHelper.Redirect = null;
        ChangeSession.BypassElevationForTests = false;
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, false); } catch { }
        foreach (var f in new[] { _journalPath, _journalPath + ".bak", _journalPath + ".tmp" })
            try { File.Delete(f); } catch { }
    }

    private const string Distinct = @"Software\Lycoris-Stress\Distinct";
    private const string Shared = @"Software\Lycoris-Stress\Shared";

    private static int? Read(string path, string name) =>
        RegistryHelper.Read(RegHive.HKCU, path, name) is int i ? i : null;

    private void AssertJournalFileIsValid(ChangeJournal inMemory)
    {
        Assert.True(File.Exists(_journalPath));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(_journalPath)); // throws on a torn / half-written file
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        var reloaded = new ChangeJournal(_journalPath);
        Assert.Null(reloaded.UnavailableReason);
        Assert.Equal(inMemory.ActiveRecords.Count, reloaded.ActiveRecords.Count); // no record lost between memory and disk
    }

    [Fact]
    public async Task Parallel_sessions_on_distinct_and_shared_targets_lose_nothing()
    {
        const int Workers = 16, Rounds = 5;
        var journal = ChangeJournal.ForTests(_journalPath);
        var errors = new ConcurrentBag<string>();

        await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
        {
            for (var r = 0; r < Rounds; r++)
            {
                try
                {
                    var session = await ChangeSession.BeginAsync($"w{w} r{r}", createRestorePoint: false, journal: journal);
                    session.SetRegistry(RegHive.HKCU, Distinct, $"w{w}", RegistryValueKind.DWord, r + 1, "tweak:stress", "distinct");
                    session.SetRegistry(RegHive.HKCU, Shared, $"s{w % 4}", RegistryValueKind.DWord, w * 100 + r, "tweak:stress", "shared");
                    session.Complete();
                    foreach (var e in session.Errors) errors.Add(e);
                    if (r % 3 == 2)
                    {
                        var mine = journal.ActiveRecords.Where(x => x.BatchId == session.Batch.Id).ToList();
                        var res = await journal.RevertAsync(mine);
                        foreach (var e in res.Errors) errors.Add(e);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex.ToString());
                }
            }
        }))).WaitAsync(TimeSpan.FromMinutes(3));

        Assert.Empty(errors);
        AssertJournalFileIsValid(journal);
        Assert.NotEmpty(journal.ActiveRecords);

        var all = await new ChangeJournal(_journalPath).RevertAllAsync(); // reload from disk, like after a restart
        Assert.True(all.Success, string.Join("; ", all.Errors));
        for (var w = 0; w < Workers; w++) Assert.Null(Read(Distinct, $"w{w}"));
        for (var s = 0; s < 4; s++) Assert.Null(Read(Shared, $"s{s}"));
    }

    [Fact]
    public async Task Same_target_hammered_with_interleaved_reverts_ends_at_the_original()
    {
        RegistryHelper.Write(RegHive.HKCU, Shared, "hot", RegistryValueKind.DWord, 7); // an original that must come back
        var journal = ChangeJournal.ForTests(_journalPath);
        var errors = new ConcurrentBag<string>();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(w => Task.Run(async () =>
        {
            for (var r = 0; r < 4; r++)
            {
                try
                {
                    var session = await ChangeSession.BeginAsync($"hot {w}/{r}", createRestorePoint: false, journal: journal);
                    session.SetRegistry(RegHive.HKCU, Shared, "hot", RegistryValueKind.DWord, 1000 + w * 10 + r, $"tweak:src{w % 3}", "hot");
                    session.Complete();
                    foreach (var e in session.Errors) errors.Add(e);
                    if ((w + r) % 4 == 0)
                    {
                        var res = await journal.RevertAsync(journal.ActiveRecords.Where(x => x.BatchId == session.Batch.Id).ToList());
                        foreach (var e in res.Errors) errors.Add(e);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(ex.ToString());
                }
            }
        }))).WaitAsync(TimeSpan.FromMinutes(3));

        Assert.Empty(errors);
        AssertJournalFileIsValid(journal);
        var all = await journal.RevertAllAsync();
        Assert.True(all.Success, string.Join("; ", all.Errors));
        Assert.Equal(7, Read(Shared, "hot"));
        Assert.Empty(journal.ActiveRecords);
    }

    [Fact]
    public async Task Revert_all_running_beside_new_sessions_never_corrupts_the_journal()
    {
        var journal = ChangeJournal.ForTests(_journalPath);
        var errors = new ConcurrentBag<string>();
        var stop = false;

        var reverter = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    var res = await journal.RevertAllAsync();
                    foreach (var e in res.Errors) errors.Add(e);
                }
                catch (Exception ex) { errors.Add(ex.ToString()); }
                await Task.Delay(5);
            }
        });
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (var r = 0; r < 5; r++)
            {
                try
                {
                    var session = await ChangeSession.BeginAsync($"rw {w}/{r}", createRestorePoint: false, journal: journal);
                    session.SetRegistry(RegHive.HKCU, Distinct, $"rw{w}", RegistryValueKind.DWord, r + 1, "tweak:stress", "rw");
                    session.Complete();
                    foreach (var e in session.Errors) errors.Add(e);
                }
                catch (Exception ex) { errors.Add(ex.ToString()); }
            }
        })).ToArray();
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromMinutes(3));
        Volatile.Write(ref stop, true);
        await reverter.WaitAsync(TimeSpan.FromMinutes(1));

        Assert.Empty(errors);
        var last = await journal.RevertAllAsync();
        Assert.True(last.Success, string.Join("; ", last.Errors));
        AssertJournalFileIsValid(journal);
        for (var w = 0; w < 8; w++) Assert.Null(Read(Distinct, $"rw{w}"));
    }
}
