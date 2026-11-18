using System.Reflection;
using Lycoris.Core;
using Lycoris.Core.Assistant;
using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Tweaks;

namespace Lycoris.Tests;

[Collection("Registry")]
public class AssistantRegistryVerificationTests
{
    private const string GameMode = @"Software\Microsoft\GameBar";
    private const string GameDvr = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const BindingFlags ProbeFlags = BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData("ReadGameMode", "game-mode")]
    [InlineData("ReadBackgroundRecording", "gamedvr")]
    public void A_registry_read_failure_after_a_write_does_not_confirm_the_fix(string probe, string id)
    {
        // OpenBase rejects this hive before touching the real registry.
        var setting = ReadWithRedirect(probe, static (hive, path) =>
            hive == RegHive.HKCU && path is GameMode or GameDvr ? ((RegHive)int.MaxValue, path) : (hive, path));
        var check = id == "game-mode"
            ? HealthRules.CheckGameMode(new SystemSnapshot { GameModeOn = setting })
            : HealthRules.CheckGameDvr(new SystemSnapshot { BackgroundRecordingOn = setting });
        var postcheck = new AssistantDiagnosticSnapshot(new DateTime(2026, 11, 18, 17, 12, 0),
            check is null ? [] : [check], new HashSet<string>());
        var applied = new TweakRunResult(1, 0, [], null, RestartRequirement.None);

        Assert.Equal(AssistantFixState.Unverified, AssistantDiagnostics.VerifyFix(id, applied, postcheck));
        Assert.Null(check);
    }

    [Theory]
    [InlineData("ReadGameMode", true)]
    [InlineData("ReadBackgroundRecording", false)]
    public void A_confirmed_absent_value_uses_the_windows_default(string probe, bool expected)
    {
        var missing = $@"Software\Lycoris-Tests\Absent-{Guid.NewGuid():N}";
        var setting = ReadWithRedirect(probe, (hive, _) => (hive, missing));

        Assert.Equal(expected, setting);
    }

    [Theory]
    [InlineData(true, null, false)]
    [InlineData(false, true, true)]
    public void Trim_fix_requires_confirmed_policy_absence_even_if_the_catalog_allows_it(
        bool readFails, bool? expectedPolicyAbsent, bool expectedEligible)
    {
        var missing = $@"Software\Lycoris-Tests\Absent-{Guid.NewGuid():N}";
        var policyAbsent = ReadWithRedirect("ReadTrimPolicyAbsent", (hive, path) =>
            hive == RegHive.HKLM && path == TweakCatalog.FileSystemPolicy
                ? readFails ? ((RegHive)int.MaxValue, path) : (RegHive.HKCU, missing)
                : (hive, path));
        var facts = new SystemSnapshot
        {
            TrimDisabled = true,
            SystemDisk = new SystemDisk("Test SSD", "SSD", "NVMe", 30, 100),
            TrimPolicyAbsent = policyAbsent,
        };

        Assert.Equal(expectedPolicyAbsent, policyAbsent);
        Assert.Equal(expectedEligible, DashboardViewModel.IsAssistantFixApplicable(HealthRules.CheckTrim(facts)!, facts, _ => true));
    }

    private static bool? ReadWithRedirect(string probeName, Func<RegHive, string, (RegHive, string)> redirect)
    {
        var probe = typeof(SystemInspector).GetMethod(probeName, ProbeFlags)!.CreateDelegate<Func<bool?>>();
        var safe = typeof(SystemInspector).GetMethod("Safe", ProbeFlags)!.MakeGenericMethod(typeof(bool?))
            .CreateDelegate<Func<Func<bool?>, bool?, bool?>>();
        var previous = RegistryHelper.Redirect;
        try
        {
            RegistryHelper.Redirect = redirect;
            return safe(probe, null);
        }
        finally
        {
            RegistryHelper.Redirect = previous;
        }
    }
}
