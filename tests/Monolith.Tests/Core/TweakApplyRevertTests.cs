using System.IO;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Tweaks;

namespace Monolith.Tests.Core;

[CollectionDefinition("Registry", DisableParallelization = true)]
public sealed class RegistryCollection;

/// <summary>
/// Applies EVERY registry tweak of the catalog and reverts it — against a redirected registry
/// (HKCU\Software\MONOLITH-Tests\Redirect-*), never the real settings — and checks that every value
/// comes back exactly as it was, both when it existed before and when it did not.
/// </summary>
[Collection("Registry")]
public sealed class TweakApplyRevertTests : IDisposable
{
    private readonly string _root = $@"Software\MONOLITH-Tests\Redirect-{Guid.NewGuid():N}";
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"monolith-tweaktest-{Guid.NewGuid():N}.json");

    public TweakApplyRevertTests()
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

    public static IEnumerable<object[]> RegistryTweakIds() =>
        TweakCatalog.All.OfType<RegistryTweak>().Select(t => new object[] { t.Id });

    [Theory]
    [MemberData(nameof(RegistryTweakIds))]
    public async Task Tweak_reverts_to_absent_values(string id)
    {
        var tweak = (RegistryTweak)TweakCatalog.Find(id)!;
        var before = Capture(tweak);

        await ApplyAndAssertApplied(tweak);
        await RevertAll();

        Assert.Equal(before, Capture(tweak));
    }

    [Theory]
    [MemberData(nameof(RegistryTweakIds))]
    public async Task Tweak_reverts_to_existing_original_values(string id)
    {
        var tweak = (RegistryTweak)TweakCatalog.Find(id)!;
        foreach (var v in tweak.Values)
            RegistryHelper.Write(v.Hive, v.Path, v.Name, v.Kind, Sentinel(v.Kind));
        var before = Capture(tweak);

        await ApplyAndAssertApplied(tweak);
        await RevertAll();

        Assert.Equal(before, Capture(tweak));
    }

    /// <summary>An original of another kind (REG_SZ text where the tweak writes a DWORD, and the reverse) comes back with its own kind.</summary>
    [Theory]
    [MemberData(nameof(RegistryTweakIds))]
    public async Task Tweak_reverts_to_existing_original_values_of_another_kind(string id)
    {
        var tweak = (RegistryTweak)TweakCatalog.Find(id)!;
        foreach (var v in tweak.Values)
        {
            var other = v.Kind == RegistryValueKind.String ? RegistryValueKind.DWord : RegistryValueKind.String;
            RegistryHelper.Write(v.Hive, v.Path, v.Name, other, Sentinel(other));
        }
        var before = Capture(tweak);

        await ApplyAndAssertApplied(tweak);
        await RevertAll();

        Assert.Equal(before, Capture(tweak));
    }

    private async Task ApplyAndAssertApplied(RegistryTweak tweak)
    {
        var journal = new ChangeJournal(_journalPath);
        var session = await ChangeSession.BeginAsync("test " + tweak.Id, createRestorePoint: false, journal: journal);
        tweak.Apply(new TweakContext { Session = session, System = SystemProfile.Current, Tweak = tweak });
        session.Complete();
        Assert.Empty(session.Errors);
        Assert.Equal(TweakState.Applied, tweak.Detect());
    }

    private async Task RevertAll()
    {
        var journal = new ChangeJournal(_journalPath); // reload from disk, like after a restart
        var result = await journal.RevertAllAsync();
        Assert.True(result.Success, string.Join("; ", result.Errors));
    }

    private static object Sentinel(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => 0x5A5A,
        RegistryValueKind.QWord => 0x5A5A5A5AL,
        RegistryValueKind.Binary => new byte[] { 9, 8, 7, 6, 5 },
        RegistryValueKind.MultiString => new[] { "orig-1", "orig-2" },
        _ => "orig-value",
    };

    /// <summary>Text dump of every value the tweak touches (kind + data, or "absent") and whether each key exists.</summary>
    private static string Capture(RegistryTweak tweak) =>
        string.Join("\n", tweak.Values.Select(v =>
        {
            var s = RegistryHelper.Snapshot(v.Hive, v.Path, v.Name);
            return $"{v.Hive}\\{v.Path}\\{v.Name} = {(s.ValueExisted ? $"{s.Kind}:{s.Data}" : "absent")}";
        }).Concat(tweak.Values.Select(v => (v.Hive, v.Path)).Distinct().Select(k =>
            $"key {k.Hive}\\{k.Path}: {(RegistryHelper.KeyExists(k.Hive, k.Path) ? "exists" : "absent")}")));

    // ───────────────────────────── Created keys and layers (real redirected registry) ─────────────────────────────

    /// <summary>Keys that exist on every Windows: present before MONOLITH, so they must survive every revert.</summary>
    private static void SeedBaseline(params (RegHive Hive, string Path)[] keys)
    {
        foreach (var (hive, path) in keys) RegistryHelper.Write(hive, path, "monolith-test-baseline", RegistryValueKind.String, "x");
    }

    private async Task<ChangeJournal> ApplyAsync(ChangeJournal journal, params string[] ids)
    {
        foreach (var id in ids)
        {
            var tweak = TweakCatalog.Find(id)!;
            var session = await ChangeSession.BeginAsync("test " + id, createRestorePoint: false, journal: journal);
            tweak.Apply(new TweakContext { Session = session, System = SystemProfile.Current, Tweak = tweak });
            session.Complete();
            Assert.Empty(session.Errors);
        }
        return journal;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_tweaks_sharing_a_created_key_remove_it_in_either_revert_order(bool creatorFirst)
    {
        const string windows = @"Software\Policies\Microsoft\Windows";
        SeedBaseline((RegHive.HKCU, @"Software\Policies\Microsoft"));
        var journal = await ApplyAsync(new ChangeJournal(_journalPath), "recall-snapshots-off", "click-to-do-off");
        Assert.True(RegistryHelper.KeyExists(RegHive.HKCU, windows + @"\WindowsAI"));

        var order = creatorFirst ? new[] { "recall-snapshots-off", "click-to-do-off" } : ["click-to-do-off", "recall-snapshots-off"];
        foreach (var id in order)
        {
            var r = await journal.RevertSourceAsync("tweak:" + id);
            Assert.True(r.Success, string.Join("; ", r.Errors));
        }

        Assert.False(RegistryHelper.KeyExists(RegHive.HKCU, windows + @"\WindowsAI"));
        Assert.False(RegistryHelper.KeyExists(RegHive.HKCU, windows));                      // MONOLITH created it too
        Assert.True(RegistryHelper.KeyExists(RegHive.HKCU, @"Software\Policies\Microsoft")); // existed before
    }

    [Fact]
    public async Task Per_record_revert_oldest_first_removes_the_created_policy_key()
    {
        SeedBaseline((RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows"));
        var journal = await ApplyAsync(new ChangeJournal(_journalPath), "wu-defer-feature-updates", "wu-exclude-drivers");
        foreach (var record in journal.ActiveRecords) // insertion order: the record that created the key goes first
        {
            var r = await journal.RevertAsync([record]);
            Assert.True(r.Success, string.Join("; ", r.Errors));
        }
        Assert.False(RegistryHelper.KeyExists(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate"));
        Assert.True(RegistryHelper.KeyExists(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows"));
    }

    [Fact]
    public async Task A_created_chain_shorter_than_four_segments_is_removed_whole()
    {
        SeedBaseline((RegHive.HKCU, @"Software\Microsoft"));
        var journal = await ApplyAsync(new ChangeJournal(_journalPath), "feedback-requests-off");
        Assert.True((await journal.RevertAllAsync()).Success);
        Assert.False(RegistryHelper.KeyExists(RegHive.HKCU, @"Software\Microsoft\Siuf"));
        Assert.True(RegistryHelper.KeyExists(RegHive.HKCU, @"Software\Microsoft"));
    }

    [Fact]
    public async Task Two_changes_of_one_value_in_different_batches_revert_exactly()
    {
        const string path = @"Software\MonolithLayerTest";
        RegistryHelper.Write(RegHive.HKCU, path, "Mode", RegistryValueKind.DWord, 7);
        var journal = new ChangeJournal(_journalPath);
        foreach (var (source, value) in new[] { ("tweak:a", 1), ("tweak:b", 2) })
        {
            var session = await ChangeSession.BeginAsync(source, createRestorePoint: false, journal: journal);
            Assert.True(session.SetRegistry(RegHive.HKCU, path, "Mode", RegistryValueKind.DWord, value, source, source));
            session.Complete();
        }
        Assert.Equal(2, journal.ActiveRecords.Count);

        await journal.RevertSourceAsync("tweak:a"); // the older one: B keeps its value
        Assert.Equal(2, RegistryHelper.ReadInt(RegHive.HKCU, path, "Mode"));
        await journal.RevertSourceAsync("tweak:b");
        Assert.Equal(7, RegistryHelper.ReadInt(RegHive.HKCU, path, "Mode"));
    }
}
