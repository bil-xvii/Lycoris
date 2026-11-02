using Microsoft.Win32;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Devices;
using Monolith.Modules.Devices.Audio;
using Monolith.Tweaks;

namespace Monolith.Tests.Devices;

/// <summary>
/// Call ducking is the Optimize tweak «audio-ducking» (research devices_audio.md §2: Windows settings as tweaks live on
/// Optimize; the device page links to it and applies it from its plan through TweakEngine). Runs against a redirected
/// registry and a test journal — the real UserDuckingPreference is never touched.
/// </summary>
[Collection("Registry")]
public sealed class DuckingJournalTests : IDisposable
{
    private readonly string _root = $@"Software\MONOLITH-Tests\Ducking-{Guid.NewGuid():N}";

    public DuckingJournalTests()
    {
        RegistryHelper.Redirect = (hive, path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");
        ChangeSession.BypassElevationForTests = true;
    }

    public void Dispose()
    {
        RegistryHelper.Redirect = null;
        ChangeSession.BypassElevationForTests = false;
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, false); }
        catch { /* best effort */ }
    }

    private static RegistryTweak Tweak => (RegistryTweak)TweakCatalog.Find(AudioProvider.DuckingTweakId)!;

    private static object? Live() => RegistryHelper.Read(RegHive.HKCU, DeviceTargets.DuckingPath, DeviceTargets.DuckingName);

    private static async Task<ChangeJournal> ApplyThroughThePlanPath(ChangeJournal journal)
    {
        // What DeviceApply.ApplyPlanAsync does for an OptimizeTweak row: TweakEngine in the page's own session.
        var session = await ChangeSession.BeginAsync("Устройства и сеть: автонастройка", createRestorePoint: false, journal: journal);
        var (skipped, restart, _, _) = TweakEngine.ApplyInSession(session, [Tweak], new SystemProfile());
        session.Complete();
        Assert.Equal(0, skipped);
        Assert.Equal(RestartRequirement.SignOut, restart);
        Assert.Empty(session.Errors);
        return journal;
    }

    [Fact]
    public void The_tweak_is_the_researched_value_optional_and_in_the_gaming_profile()
    {
        var t = Tweak;
        var value = Assert.Single(t.Values);
        Assert.Equal((RegHive.HKCU, DeviceTargets.DuckingPath, DeviceTargets.DuckingName, RegistryValueKind.DWord, (object?)3),
            (value.Hive, value.Path, value.Name, value.Kind, value.Value));
        Assert.Equal(TweakRisk.Optional, t.Risk);
        Assert.Equal(ProfileSet.Gaming | ProfileSet.Streaming, t.DefaultIn); // wave 3: the streaming scenario also keeps game sound audible
        Assert.Equal(RestartRequirement.SignOut, t.Restart);
        Assert.Contains(TweakCatalog.DefaultsFor(ProfileKind.Gaming), x => x.Id == t.Id);
        Assert.DoesNotContain(TweakCatalog.DefaultsFor(ProfileKind.Normal), x => x.Id == t.Id);
    }

    [Fact]
    public async Task Applying_from_the_plan_records_the_tweaks_own_source_and_passes_the_journal_check()
    {
        var journal = await ApplyThroughThePlanPath(ChangeJournal.ForTests());

        var r = Assert.Single(journal.ActiveRecords);
        Assert.Equal("tweak:" + AudioProvider.DuckingTweakId, r.Source); // never "devices": the page writes no registry values
        Assert.Equal(ChangeKind.Registry, r.Kind);
        Assert.Null(JournalTrust.Check(r));
        Assert.Equal(3, Live());
        Assert.Equal(TweakState.Applied, Tweak.Detect());
    }

    [Fact]
    public async Task Undo_deletes_a_value_that_was_absent()
    {
        Assert.Null(Live());
        var journal = await ApplyThroughThePlanPath(ChangeJournal.ForTests());

        var result = await journal.RevertAllAsync();

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Null(Live());
        Assert.Equal(TweakState.NotApplied, Tweak.Detect());
    }

    [Theory]
    [InlineData(RegistryValueKind.DWord, 1)]
    [InlineData(RegistryValueKind.DWord, 0)]
    [InlineData(RegistryValueKind.String, "2")] // another type than the tweak writes comes back with its own type
    public async Task Undo_restores_an_existing_value_exactly(RegistryValueKind kind, object original)
    {
        RegistryHelper.Write(RegHive.HKCU, DeviceTargets.DuckingPath, DeviceTargets.DuckingName, kind, original);
        var before = RegistryHelper.Snapshot(RegHive.HKCU, DeviceTargets.DuckingPath, DeviceTargets.DuckingName);
        var journal = await ApplyThroughThePlanPath(ChangeJournal.ForTests());

        var result = await journal.RevertAllAsync();

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var after = RegistryHelper.Snapshot(RegHive.HKCU, DeviceTargets.DuckingPath, DeviceTargets.DuckingName);
        Assert.Equal((before.ValueExisted, before.Kind, before.Data), (after.ValueExisted, after.Kind, after.Data));
    }

    [Fact]
    public void The_page_row_reads_the_tweak_and_leaves_the_plan_once_applied()
    {
        var card = new DeviceCard
        {
            Key = "audio:x", Kind = DeviceKind.AudioOutput, Title = "Наушники", Settings = [AudioProvider.DuckingRow(1)],
        };
        DeviceScan.ReadTweakRows([card], new SystemProfile());
        var row = card.Settings[0];
        Assert.True(row.IsReadOnly);
        Assert.Equal("тише на 80 %", row.CurrentText);
        var item = Assert.Single(DevicePlan.Build([card], interactiveUser: true));
        Assert.Equal(PlanItemKind.OptimizeTweak, item.Kind);
        Assert.Equal(("тише на 80 %", "ничего не делать"), (item.From, item.To));
        Assert.False(item.IsSelected); // SAFE-OPTIONAL: offered unchecked
        Assert.Equal(new DeviceLink(DeviceLinkKind.FocusTweak, "В ОПТИМИЗАЦИИ", AudioProvider.DuckingTweakId), item.Link);

        RegistryHelper.Write(RegHive.HKCU, DeviceTargets.DuckingPath, DeviceTargets.DuckingName, RegistryValueKind.DWord, 3);
        DeviceScan.ReadTweakRows([card], new SystemProfile());

        Assert.Equal("ничего не делать", row.CurrentText);
        Assert.Empty(DevicePlan.Build([card], interactiveUser: true));
    }
}
