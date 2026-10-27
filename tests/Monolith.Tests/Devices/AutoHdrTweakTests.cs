using Monolith.Core;
using Monolith.Tweaks;

namespace Monolith.Tests.Devices;

/// <summary>The Optimize tweak «auto-hdr» (research display-auto-hdr): SAFE-OPTIONAL, gated by build and by HDR being on.</summary>
[Collection("Registry")] // replaces the process-wide HDR probe of the catalog
public sealed class AutoHdrTweakTests : IDisposable
{
    private readonly Func<bool?> _probe = TweakCatalog.HdrEnabledProbe;

    public AutoHdrTweakTests() => TweakCatalog.HdrEnabledProbe = () => true;

    public void Dispose() => TweakCatalog.HdrEnabledProbe = _probe;

    private static SystemProfile Profile(int build, int ubr = 0)
    {
        var sp = new SystemProfile();
        void Set(string name, object value) => typeof(SystemProfile).GetProperty(name)!.SetValue(sp, value);
        Set(nameof(SystemProfile.OsBuild), build);
        Set(nameof(SystemProfile.OsUbr), ubr);
        return sp;
    }

    private static Tweak AutoHdr => TweakCatalog.Find("auto-hdr")!;

    [Fact]
    public void Exists_as_an_optional_gaming_item_off_by_default()
    {
        var t = AutoHdr;
        Assert.NotNull(t);
        Assert.Equal(TweakRisk.Optional, t.Risk);
        Assert.Equal(ProfileSet.None, t.DefaultIn);
        Assert.Equal(TweakCategory.Gaming, t.Category);
        Assert.False(string.IsNullOrWhiteSpace(t.NotApplicableReason));
        Assert.Contains("HDR", t.NotApplicableReason);
        Assert.Contains("AutoHDREnable", t.Details);
        Assert.Equal(1, TweakCatalog.All.Count(x => x.Id == "auto-hdr"));
        // Listed after the VRR item it belongs with.
        var ids = TweakCatalog.All.Select(x => x.Id).ToList();
        Assert.Equal(ids.IndexOf("vrr-optimization") + 1, ids.IndexOf("auto-hdr"));
    }

    [Theory]
    [InlineData(26100, 3193, false)] // 24H2 before KB5051987 (Auto HDR bug, safeguard 55382406)
    [InlineData(26100, 3194, true)]
    [InlineData(26300, 0, true)]
    [InlineData(22631, 4317, true)]
    [InlineData(19045, 5000, false)] // Windows 10
    public void Build_gate(int build, int ubr, bool applies)
    {
        Assert.Equal(applies, TweakCatalog.AutoHdrBuildSupported(Profile(build, ubr)));
        Assert.Equal(applies, AutoHdr.IsApplicable(Profile(build, ubr)));
    }

    [Fact]
    public void Hdr_off_everywhere_greys_it_out_and_unknown_keeps_it()
    {
        TweakCatalog.HdrEnabledProbe = () => false;
        Assert.False(AutoHdr.IsApplicable(Profile(26300)));
        TweakCatalog.HdrEnabledProbe = () => null; // cannot tell: still offered
        Assert.True(AutoHdr.IsApplicable(Profile(26300)));
        TweakCatalog.HdrEnabledProbe = () => true;
        Assert.True(AutoHdr.IsApplicable(Profile(26300)));
    }

    [Fact]
    public void Its_undo_record_is_accepted_by_the_journal_check_and_tampering_is_not()
    {
        ChangeRecord Record(string args) => new()
        {
            Source = "tweak:auto-hdr", Title = "Auto HDR для игр", Kind = ChangeKind.Command, Target = "cmd:dx:AutoHDREnable",
            RevertFile = TweakCatalog.PowerShellExe, RevertArgs = args,
        };

        foreach (var value in new[] { null, "0", "1" })
            Assert.Null(JournalTrust.Check(Record(TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript("AutoHDREnable", value)))));
        foreach (var (key, parent) in new[] { (true, false), (true, true) })
            Assert.Null(JournalTrust.Check(Record(TweakCatalog.PowerShellArgs(
                TweakCatalog.DxTokenScript("AutoHDREnable", null, createdKey: key, createdParent: parent)))));
        Assert.NotNull(JournalTrust.Check(Record(TweakCatalog.PowerShellArgs(TweakCatalog.DxTokenScript("AutoHDREnable", "0") + ";Start-Process calc"))));
    }
}
