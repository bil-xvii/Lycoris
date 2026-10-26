using System.IO;
using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Tests.Core;

/// <summary>
/// The undo journal is what keeps MONOLITH from ever leaving the system in a changed state it can't restore.
/// These tests only touch HKCU\Software\MONOLITH-Tests and a temp folder.
/// </summary>
public sealed class RegistryAndJournalTests : IDisposable
{
    private const string Root = @"Software\MONOLITH-Tests";
    private readonly string _key = $@"{Root}\{Guid.NewGuid():N}";
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"monolith-journal-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_key, false); } catch { }
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(Root, false);
        }
        catch { }
        foreach (var f in new[] { _journalPath, _journalPath + ".bak", _journalPath + ".tmp" })
            try { File.Delete(f); } catch { }
    }

    [Fact]
    public void Snapshot_of_missing_value_restores_by_deleting()
    {
        RegistryHelper.Write(RegHive.HKCU, _key, "Existing", RegistryValueKind.DWord, 5);
        var snap = RegistryHelper.Snapshot(RegHive.HKCU, _key, "New");
        Assert.True(snap.KeyExisted);
        Assert.False(snap.ValueExisted);

        RegistryHelper.Write(RegHive.HKCU, _key, "New", RegistryValueKind.DWord, 1);
        RegistryHelper.Restore(snap);

        Assert.Null(RegistryHelper.Read(RegHive.HKCU, _key, "New"));
        Assert.Equal(5, RegistryHelper.ReadInt(RegHive.HKCU, _key, "Existing"));
    }

    [Fact]
    public void Snapshot_roundtrips_every_value_kind()
    {
        RegistryHelper.Write(RegHive.HKCU, _key, "Dw", RegistryValueKind.DWord, unchecked((int)0xFFFFFFFF));
        RegistryHelper.Write(RegHive.HKCU, _key, "Qw", RegistryValueKind.QWord, 1234567890123L);
        RegistryHelper.Write(RegHive.HKCU, _key, "Sz", RegistryValueKind.String, "текст");
        RegistryHelper.Write(RegHive.HKCU, _key, "Ex", RegistryValueKind.ExpandString, "%SystemRoot%\\x");
        RegistryHelper.Write(RegHive.HKCU, _key, "Ms", RegistryValueKind.MultiString, new[] { "a", "б" });
        RegistryHelper.Write(RegHive.HKCU, _key, "Bin", RegistryValueKind.Binary, new byte[] { 2, 0, 0, 0, 0xFF });

        var names = new[] { "Dw", "Qw", "Sz", "Ex", "Ms", "Bin" };
        var snaps = names.Select(n => RegistryHelper.Snapshot(RegHive.HKCU, _key, n)).ToList();

        foreach (var n in names) RegistryHelper.Write(RegHive.HKCU, _key, n, RegistryValueKind.DWord, 0);
        foreach (var s in snaps) RegistryHelper.Restore(s);

        Assert.True(RegistryHelper.ValueEquals(RegHive.HKCU, _key, "Dw", RegistryValueKind.DWord, unchecked((int)0xFFFFFFFF)));
        Assert.True(RegistryHelper.ValueEquals(RegHive.HKCU, _key, "Qw", RegistryValueKind.QWord, 1234567890123L));
        Assert.Equal("текст", RegistryHelper.ReadString(RegHive.HKCU, _key, "Sz"));
        Assert.Equal("%SystemRoot%\\x", RegistryHelper.ReadString(RegHive.HKCU, _key, "Ex"));
        Assert.Equal(new[] { "a", "б" }, (string[])RegistryHelper.Read(RegHive.HKCU, _key, "Ms")!);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0xFF }, (byte[])RegistryHelper.Read(RegHive.HKCU, _key, "Bin")!);

        using var k = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal(RegistryValueKind.ExpandString, k.GetValueKind("Ex"));
        Assert.Equal(RegistryValueKind.QWord, k.GetValueKind("Qw"));
    }

    [Fact]
    public void Restoring_value_in_key_that_did_not_exist_removes_the_created_key()
    {
        var deepKey = $@"{_key}\Created\Deeper";
        var snap = RegistryHelper.Snapshot(RegHive.HKCU, deepKey, "Flag");
        Assert.False(snap.KeyExisted);

        RegistryHelper.Write(RegHive.HKCU, deepKey, "Flag", RegistryValueKind.DWord, 1);
        Assert.True(RegistryHelper.KeyExists(RegHive.HKCU, deepKey));

        RegistryHelper.Restore(snap);
        Assert.False(RegistryHelper.KeyExists(RegHive.HKCU, deepKey));
    }

    [Fact]
    public async Task Journal_reverts_to_the_first_original_value_after_repeated_changes()
    {
        RegistryHelper.Write(RegHive.HKCU, _key, "Mode", RegistryValueKind.DWord, 7);
        var journal = new ChangeJournal(_journalPath);
        var batch = journal.BeginBatch("test");

        // First change: 7 -> 1
        var first = RegistryHelper.Snapshot(RegHive.HKCU, _key, "Mode");
        var (rec1, isNew1) = journal.Add(new ChangeRecord
        {
            BatchId = batch.Id, Source = "test", Title = "Mode", Kind = ChangeKind.Registry,
            Target = $"reg:HKCU\\{_key}\\Mode", OldValue = first.Describe(), NewValue = "1", Registry = first,
        });
        RegistryHelper.Write(RegHive.HKCU, _key, "Mode", RegistryValueKind.DWord, 1);

        // Second change of the same target: 1 -> 2 (must NOT replace the original 7)
        var second = RegistryHelper.Snapshot(RegHive.HKCU, _key, "Mode");
        var (rec2, isNew2) = journal.Add(new ChangeRecord
        {
            BatchId = batch.Id, Source = "test", Title = "Mode", Kind = ChangeKind.Registry,
            Target = $"reg:HKCU\\{_key}\\Mode", OldValue = second.Describe(), NewValue = "2", Registry = second,
        });
        RegistryHelper.Write(RegHive.HKCU, _key, "Mode", RegistryValueKind.DWord, 2);

        Assert.True(isNew1);
        Assert.False(isNew2);
        Assert.Same(rec1, rec2);
        Assert.Single(journal.ActiveRecords);

        var result = await journal.RevertSourceAsync("test");
        Assert.True(result.Success);
        Assert.Equal(1, result.Reverted);
        Assert.Equal(7, RegistryHelper.ReadInt(RegHive.HKCU, _key, "Mode"));
        Assert.Empty(journal.ActiveRecords);
    }

    [Fact]
    public async Task Journal_persists_and_reloads_from_disk()
    {
        RegistryHelper.Write(RegHive.HKCU, _key, "Persist", RegistryValueKind.String, "orig");
        var journal = new ChangeJournal(_journalPath);
        var batch = journal.BeginBatch("persist");
        var snap = RegistryHelper.Snapshot(RegHive.HKCU, _key, "Persist");
        journal.Add(new ChangeRecord
        {
            BatchId = batch.Id, Source = "persist", Title = "Persist", Kind = ChangeKind.Registry,
            Target = $"reg:HKCU\\{_key}\\Persist", OldValue = snap.Describe(), NewValue = "changed", Registry = snap,
        });
        RegistryHelper.Write(RegHive.HKCU, _key, "Persist", RegistryValueKind.String, "changed");

        // A new instance (e.g. after an app restart or crash) must still be able to undo.
        var reloaded = new ChangeJournal(_journalPath);
        Assert.Single(reloaded.ActiveRecords);
        Assert.Single(reloaded.Batches);

        var result = await reloaded.RevertBatchAsync(batch.Id);
        Assert.True(result.Success);
        Assert.Equal("orig", RegistryHelper.ReadString(RegHive.HKCU, _key, "Persist"));
    }

    [Fact]
    public void Damaged_journal_is_kept_as_a_copy_and_a_new_one_starts()
    {
        File.WriteAllText(_journalPath, "{ this is not json");
        var journal = new ChangeJournal(_journalPath);
        Assert.Empty(journal.Records);
        var copies = Directory.GetFiles(Path.GetDirectoryName(_journalPath)!, Path.GetFileName(_journalPath) + ".broken-*");
        Assert.NotEmpty(copies);
        foreach (var c in copies) File.Delete(c);
    }

    [Fact]
    public void Removed_record_is_not_reverted()
    {
        var journal = new ChangeJournal(_journalPath);
        var batch = journal.BeginBatch("x");
        var (rec, _) = journal.Add(new ChangeRecord
        {
            BatchId = batch.Id, Source = "x", Title = "x", Kind = ChangeKind.Registry, Target = "reg:x",
            Registry = new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = _key, Name = "x" },
        });
        journal.Remove(rec);
        Assert.Empty(journal.ActiveRecords);
    }
}
