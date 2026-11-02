using System.IO;
using Lycoris.Core;

namespace Lycoris.Modules.Dashboard.Checks;

/// <summary>
/// Active code-integrity policies: the Microsoft vulnerable driver blocklist and Smart App Control.
/// Source: "CiTool.exe -lp -json" (Windows 11 22H2+, needs administrator rights). The argument string is fixed:
/// no other CiTool verb (update, remove, refresh, add/remove token) is ever passed. Without admin rights, without CiTool
/// or on any failure the blocklist reads as unknown; Smart App Control then falls back to its registry state.
/// </summary>
internal static class KernelProtectionProbe
{
    private const string CiPolicyKey = @"SYSTEM\CurrentControlSet\Control\CI\Policy";

    public static async Task<(bool? Blocklist, SmartAppControlState Sac)> ReadAsync(bool elevated)
    {
        var fromCi = elevated ? await ReadCiToolAsync().ConfigureAwait(false) : null;
        var sac = fromCi?.Sac ?? SmartAppControlState.Unknown;
        if (sac == SmartAppControlState.Unknown) sac = ReadSacRegistry();
        return (fromCi?.Blocklist, sac);
    }

    private static async Task<CiPolicyState?> ReadCiToolAsync()
    {
        var exe = ProcessRunner.Sys("CiTool.exe");
        if (!File.Exists(exe)) return null; // Windows 10 / Windows 11 21H2
        try
        {
            // -json: machine-readable and not localized; without it CiTool may wait for a key press.
            var r = await ProcessRunner.RunAsync(exe, ["-lp", "-json"],
                new ProcessRunOptions { Encoding = OutputEncoding.Utf8, Timeout = TimeSpan.FromSeconds(15), LogCommand = false }).ConfigureAwait(false);
            if (r.TimedOut || r.Cancelled) return null;
            return HealthRules.ParseCiPolicies(r.StdOut);
        }
        catch (Exception ex)
        {
            Log.Warn($"CiTool: {ex.Message}");
            return null;
        }
    }

    /// <summary>VerifiedAndReputablePolicyState: 0 off, 1 on, 2 evaluation. A missing value is unknown, not off.</summary>
    private static SmartAppControlState ReadSacRegistry()
    {
        try
        {
            return RegistryHelper.ReadInt(RegHive.HKLM, CiPolicyKey, "VerifiedAndReputablePolicyState") switch
            {
                0 => SmartAppControlState.Off,
                1 => SmartAppControlState.On,
                2 => SmartAppControlState.Evaluation,
                _ => SmartAppControlState.Unknown,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Smart App Control: {ex.Message}");
            return SmartAppControlState.Unknown;
        }
    }
}
