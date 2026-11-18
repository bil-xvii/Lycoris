using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lycoris.Core.Background;

internal static class BackgroundModePolicy
{
    public const string Argument = "--background";
    public const string TaskArguments = Argument + " --no-elevate";
    public const string NormalInstanceName = @"Local\Lycoris.SingleInstance";

    internal static string InstanceNameFor(string? testRoot)
    {
#if DEBUG
        if (!string.IsNullOrWhiteSpace(testRoot))
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(testRoot)).ToLowerInvariant();
            return NormalInstanceName + ".Test." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
        }
#endif
        return NormalInstanceName;
    }

    // Relaunches with --elevated (including same-rights restarts) carry the parent arguments, but open visibly.
    public static bool IsLogonLaunch(IEnumerable<string> args, bool qaMode, bool elevated) =>
        !qaMode && !elevated && args.Contains(Argument, StringComparer.OrdinalIgnoreCase)
        && !args.Contains("--elevated", StringComparer.OrdinalIgnoreCase);

    public static bool CanStartHidden(bool logonLaunch, bool trayReady, bool dialogOpen) =>
        logonLaunch && trayReady && !dialogOpen;

    public static bool CanHideOnClose(bool enabled, bool trayReady, bool dialogOpen, bool exiting) =>
        enabled && trayReady && !dialogOpen && !exiting;

    public static bool ShouldReveal(bool alreadyHidden, bool trayReady) => alreadyHidden && !trayReady;
}
