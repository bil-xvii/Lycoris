using System.IO;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Devices;
using Monolith.Modules.Devices.Network;
using Monolith.Modules.Restore;
using Monolith.Tests.Core;
using Monolith.Tweaks;
using Monolith.UI;

namespace Monolith.Tests.Devices;

/// <summary>
/// Changes the «Сеть» group can make — adapter properties through ChangeSession with a fake writer and an isolated
/// journal, the PowerShell whitelist, and the measured-only «ipv6-prefer-ipv4» tweak against a redirected registry.
/// Nothing here touches a real adapter, the real registry or the real journal.
/// </summary>
[Collection("Registry")]
public sealed class NetworkApplyTests : IDisposable
{
    private readonly string _root = $@"Software\MONOLITH-Tests\Redirect-{Guid.NewGuid():N}";
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"monolith-nettest-{Guid.NewGuid():N}.json");
    private readonly FakeNicWriter _nic = new();
    private readonly IDisposable _writers;
    private readonly ChangeJournal _journal = ChangeJournal.ForTests();
    private string? _game;
    private readonly List<Guid> _linkReads = [];

    public NetworkApplyTests()
    {
        RegistryHelper.Redirect = (hive, path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");
        ChangeSession.BypassElevationForTests = true;
        _writers = DeviceWriters.OverrideForTests(nic: _nic);
        NetGames.RunningGameForTests = () => _game;
        Ui.AutoAnswer = true;
        NetworkCards.LinkPoll = TimeSpan.FromMilliseconds(1);
        NetworkCards.LinkReader = g =>
        {
            _linkReads.Add(g);
            return (true, LinkFullDuplex, 1_000_000_000);
        };
    }

    private bool? LinkFullDuplex { get; set; } = true;

    public void Dispose()
    {
        _writers.Dispose();
        NetGames.RunningGameForTests = null;
        Ui.AutoAnswer = null;
        NetworkCards.LinkPoll = TimeSpan.FromSeconds(1);
        NetworkCards.LinkReader = NetSystem.ReadLink;
        NetworkEvidence.Ipv6Worse = false;
        RegistryHelper.Redirect = null;
        ChangeSession.BypassElevationForTests = false;
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, false); } catch { }
        foreach (var f in new[] { _journalPath, _journalPath + ".bak", _journalPath + ".tmp" })
            try { File.Delete(f); } catch { }
    }

    private NetAdapterInfo Adapter(string sd) => new()
    {
        InterfaceGuid = _nic.Adapter, Name = "Ethernet", Description = "Realtek(R) PCI(e) Ethernet Controller", Medium = NetMedium.Ethernet,
        Connected = true, SpeedBps = 100_000_000, FullDuplex = true, LinkDrops = 0,
        Properties = [new NicPropertyInfo("*SpeedDuplex", sd, ["0", "1", "2", "3", "4", "6"], "0")],
    };

    private DeviceSetting SpeedRow(string sd, DeviceResultStore? results = null)
    {
        _nic.Props["*SpeedDuplex"] = new NicPropertyInfo("*SpeedDuplex", sd, ["0", "1", "2", "3", "4", "6"], "0");
        var snap = new NetSnapshot { Adapters = [Adapter(sd)], BestInterface = _nic.Adapter, Gateway = "192.168.1.1" };
        var card = NetworkCards.Build(snap, results ?? new DeviceResultStore()).Single(c => c.Key == "net:" + _nic.Adapter.ToString("D"));
        return card.Settings.Single(s => s.Id == "nic:*SpeedDuplex");
    }

    private Task<ChangeSession> Begin() => ChangeSession.BeginAsync("test", createRestorePoint: false, journal: _journal);

    // ───────────────────────────── Adapter properties ─────────────────────────────

    [Fact]
    public async Task Speed_duplex_back_to_auto_is_journaled_and_reverts_exactly()
    {
        var row = SpeedRow("4");
        var s = await Begin();
        var outcome = await row.Apply!(s, DeviceValue.Of(0), CancellationToken.None);
        s.Complete();

        Assert.Equal(ApplyOutcome.Applied, outcome);
        Assert.Equal("0", _nic.Props["*SpeedDuplex"].Value);
        var record = Assert.Single(_journal.ActiveRecords);
        Assert.Equal(DeviceTargets.Source, record.Source);
        Assert.Equal(ChangeKind.NicProperty, record.Kind);
        Assert.Equal(row.JournalTarget, record.Target);
        Assert.Equal("4", record.Nic!.Value);
        Assert.Single(_linkReads); // the link was checked after the restart
        Assert.Equal(DeviceValue.Of(0), row.ReadCurrent!());

        var revert = await _journal.RevertAsync([record], LiveState.VerifyRevert);
        Assert.True(revert.Success, string.Join("; ", revert.Errors));
        Assert.Equal("4", _nic.Props["*SpeedDuplex"].Value);
    }

    [Fact]
    public async Task Speed_duplex_only_repairs_to_auto_and_never_forces_a_speed()
    {
        // Research: Lycoris returns a forced speed/duplex to auto-negotiation; it never sets a fixed speed.
        var forced = SpeedRow("4");
        Assert.Equal(SettingControl.Choice, forced.Control);
        Assert.Equal([0L, 4L], forced.Options.Select(o => o.Value.Number).ToList());
        var s = await Begin();
        Assert.Equal(ApplyOutcome.Blocked, await forced.Apply!(s, DeviceValue.Of(6), CancellationToken.None));
        s.Complete();
        Assert.Equal(0, _nic.SetCalls);
        Assert.Empty(_journal.ActiveRecords);

        var auto = SpeedRow("0");
        Assert.Equal(SettingControl.Info, auto.Control);
        Assert.True(auto.IsReadOnly);
        Assert.Null(auto.Recommended);
        Assert.Equal(DeviceValue.Of(0), auto.Current);
    }

    [Fact]
    public async Task Half_duplex_after_auto_is_flagged_for_the_page()
    {
        LinkFullDuplex = false;
        var results = new DeviceResultStore();
        var row = SpeedRow("6", results);
        var s = await Begin();
        Assert.Equal(ApplyOutcome.Applied, await row.Apply!(s, DeviceValue.Of(0), CancellationToken.None));
        s.Complete();
        Assert.True(results.Get<bool>(NetKeys.DuplexRegression(_nic.Adapter)));
    }

    [Fact]
    public async Task Nothing_restarts_the_adapter_during_a_game()
    {
        _game = "Counter-Strike 2";
        var row = SpeedRow("4");
        var s = await Begin();
        Assert.Equal(ApplyOutcome.Blocked, await row.Apply!(s, DeviceValue.Of(0), CancellationToken.None));
        s.Complete();
        Assert.Equal(0, _nic.SetCalls);
        Assert.Empty(_journal.ActiveRecords);
        Assert.Contains(s.Errors, e => e.Contains("Counter-Strike 2"));
    }

    [Fact]
    public async Task Declining_the_restart_changes_nothing()
    {
        Ui.AutoAnswer = false;
        var row = SpeedRow("4");
        var s = await Begin();
        Assert.Equal(ApplyOutcome.Declined, await row.Apply!(s, DeviceValue.Of(0), CancellationToken.None));
        s.Complete();
        Assert.Equal(0, _nic.SetCalls);
        Assert.Empty(_journal.ActiveRecords);
    }

    [Fact]
    public async Task The_same_value_is_no_change()
    {
        var row = SpeedRow("4");
        var s = await Begin();
        Assert.Equal(ApplyOutcome.NoChange, await row.Apply!(s, DeviceValue.Of(4), CancellationToken.None));
        s.Complete();
        Assert.Equal(0, _nic.SetCalls);
    }

    [Fact]
    public void Powershell_is_built_only_from_whitelisted_keywords_and_numbers()
    {
        var g = new Guid("21ca98f6-bc9b-4db8-bc1f-2595dcb69e43");
        var script = NicWriter.Script(g, "*speedduplex", "0");
        Assert.Contains("{21CA98F6-BC9B-4DB8-BC1F-2595DCB69E43}", script);
        Assert.Contains("-RegistryKeyword '*SpeedDuplex' -RegistryValue '0'", script);
        Assert.Contains("Set-NetAdapterAdvancedProperty", script);

        // DO-NOT-INCLUDE keywords (interrupt moderation, offloads, buffers, jumbo frames) and anything non-numeric are refused.
        foreach (var kw in new[] { "*InterruptModeration", "*ReceiveBuffers", "*LsoV2IPv4", "*JumboPacket", "*FlowControl", "StaPreferredBand", "roamPolicy" })
            Assert.Throws<ArgumentException>(() => NicWriter.Script(g, kw, "0"));
        Assert.Throws<ArgumentException>(() => NicWriter.Script(g, "*EEE", "0'; Remove-Item C:\\ -Recurse; '"));
        Assert.Throws<ArgumentException>(() => NicWriter.Script(Guid.Empty, "*EEE", "0"));
    }

    // ───────────────────────────── ipv6-prefer-ipv4 ─────────────────────────────

    private const string Tcpip6 = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";
    private static readonly Tweak V4 = TweakCatalog.Find("ipv6-prefer-ipv4")!;

    private async Task ApplyTweakAsync()
    {
        var journal = new ChangeJournal(_journalPath);
        var session = await ChangeSession.BeginAsync("test ipv6", createRestorePoint: false, journal: journal);
        try { V4.Apply(new TweakContext { Session = session, System = SystemProfile.Current, Tweak = V4 }); }
        finally { session.Complete(); }
    }

    [Fact]
    public void Ipv4_preference_is_advanced_never_profiled_and_gated_by_the_measurement()
    {
        Assert.Equal(TweakRisk.Advanced, V4.Risk);
        Assert.Equal(TweakCategory.Network, V4.Category);
        Assert.Equal(ProfileSet.None, V4.DefaultIn);
        Assert.False(string.IsNullOrWhiteSpace(V4.Warning));
        Assert.Equal(RestartRequirement.Reboot, V4.Restart);

        NetworkEvidence.Ipv6Worse = false;
        Assert.False(V4.IsApplicable(SystemProfile.Current));
        NetworkEvidence.Ipv6Worse = true;
        Assert.True(V4.IsApplicable(SystemProfile.Current));
        Assert.Equal(TweakState.NotApplied, V4.Detect());
    }

    [Fact]
    public async Task Ipv4_preference_writes_only_0x20_and_reverts_to_absent()
    {
        NetworkEvidence.Ipv6Worse = true;
        await ApplyTweakAsync();
        Assert.Equal(0x20, RegistryHelper.ReadInt(RegHive.HKLM, Tcpip6, "DisabledComponents"));
        Assert.Equal(TweakState.Applied, V4.Detect());
        Assert.True(V4.IsApplicable(SystemProfile.Current)); // stays visible (and revertible) once applied

        var result = await new ChangeJournal(_journalPath).RevertAllAsync();
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Null(RegistryHelper.ReadInt(RegHive.HKLM, Tcpip6, "DisabledComponents"));
    }

    [Fact]
    public async Task Ipv4_preference_never_overwrites_other_disabled_components_bits()
    {
        NetworkEvidence.Ipv6Worse = true;
        RegistryHelper.Write(RegHive.HKLM, Tcpip6, "DisabledComponents", RegistryValueKind.DWord, 0x01);
        Assert.False(V4.IsApplicable(SystemProfile.Current));
        Assert.Equal(TweakState.NotApplicable, V4.Detect());
        try { await ApplyTweakAsync(); }
        catch (InvalidOperationException) { }
        Assert.Equal(0x01, RegistryHelper.ReadInt(RegHive.HKLM, Tcpip6, "DisabledComponents"));
    }
}
