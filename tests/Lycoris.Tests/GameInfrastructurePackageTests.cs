using Lycoris.Modules.Removal;
using Lycoris.Modules.Startup;

namespace Lycoris.Tests;

public sealed class GameInfrastructurePackageTests
{
    [Theory]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbwe")]
    [InlineData("Microsoft.GamingServices_8wekyb3d8bbwe")]
    [InlineData("Microsoft.XboxApp_8wekyb3d8bbwe")]
    [InlineData("MICROSOFT.XBOXGAMINGOVERLAY_8WEKYB3D8BBWE")]
    [InlineData("Microsoft.XboxGameOverlay_8wekyb3d8bbwe")]
    [InlineData("Microsoft.XboxIdentityProvider_8wekyb3d8bbwe")]
    [InlineData("Microsoft.Xbox.TCUI_8wekyb3d8bbwe")]
    [InlineData("Microsoft.XboxSpeechToTextOverlay_8wekyb3d8bbwe")]
    public void Exact_game_infrastructure_identity_protects_removal_and_startup(string family)
    {
        Assert.True(PackagedRemovalInventory.IsGamingInfrastructureFamily(family));
        Assert.True(PackagedRemovalInventory.IsProtectedFamily(family, null));
        Assert.Equal(StartupRisk.Microsoft, StartupClassifier.ClassifyPackagedTask(family, "Startup", null, null));
    }

    [Theory]
    [InlineData("Microsoft.MicrosoftSolitaireCollection_8wekyb3d8bbwe")]
    [InlineData("Microsoft.GamingApp_fakepublisher")]
    [InlineData("Microsoft.GamingAppHelper_8wekyb3d8bbwe")]
    [InlineData(null)]
    public void Similar_labels_or_different_publishers_are_not_protected_identities(string? family) =>
        Assert.False(PackagedRemovalInventory.IsGamingInfrastructureFamily(family));
}
