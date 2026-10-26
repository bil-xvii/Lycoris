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

    /// <summary>Text dump of every value the tweak touches (kind + data, or "absent").</summary>
    private static string Capture(RegistryTweak tweak) =>
        string.Join("\n", tweak.Values.Select(v =>
        {
            var s = RegistryHelper.Snapshot(v.Hive, v.Path, v.Name);
            return $"{v.Hive}\\{v.Path}\\{v.Name} = {(s.ValueExisted ? $"{s.Kind}:{s.Data}" : "absent")}";
        }));
}
