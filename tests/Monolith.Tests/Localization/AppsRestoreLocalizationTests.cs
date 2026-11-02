using Monolith.Core;
using Monolith.Modules.Apps;
using Monolith.Modules.Restore;

namespace Monolith.Tests.Localization;

/// <summary>English texts of the Apps and Restore areas (Russian stays the default and is covered by the existing tests).</summary>
public sealed class AppsRestoreLocalizationTests
{
    [Fact]
    public void Winget_codes_are_described_in_English()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("the installer hash did not match the winget catalog — the install was blocked for safety",
            WingetClient.Describe(WingetClient.InstallerHashMismatch));
        Assert.Equal("code -1978335000 (0x8A1500E8)", WingetClient.Describe(unchecked((int)0x8A1500E8)));
    }

    [Fact]
    public void Hash_mismatch_status_and_counts_are_English()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.StartsWith("ERROR: the vendor updated the installer", AppTexts.HashMismatchStatus);
        Assert.Equal("1 program", AppTexts.Programs(1));
        Assert.Equal("5 programs", AppTexts.Programs(5));
        Assert.Equal("3 packages", AppTexts.Packages(3));
    }

    [Fact]
    public void Catalog_tabs_and_advice_tags_are_English()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("LAUNCHERS", Loc.T("ЛАУНЧЕРЫ"));
        Assert.Equal("NEEDED", AppAdvisor.TagText(AdviceLevel.Required));
        Assert.Equal("NOT NEEDED", AppAdvisor.TagText(AdviceLevel.NotNeeded));
    }

    [Fact]
    public void Restore_labels_are_English()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("REGISTRY", RestoreFormat.KindLabel(Monolith.Core.ChangeKind.Registry));
        Assert.Equal("Power plan High", RestoreFormat.DescribeTarget("power:High"));
        Assert.Equal("enabled", Loc.Sink("enabled"));
        Assert.Equal("Enabled", Loc.Sink("Включена"));
    }
}
