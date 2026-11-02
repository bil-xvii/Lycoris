using System.IO;

namespace Lycoris.Core.Security;

/// <summary>
/// The environment is user-controlled (HKCU\Environment, the parent process). An elevated child started with it would
/// inherit hooks the user — or a program running as the user — put there: a .NET startup hook or profiler DLL, a
/// PowerShell module path that starts with the user's Documents folder (PowerShell auto-loads «Get-NetAdapter»,
/// «Get-AuthenticodeSignature»… from the FIRST folder that has such a module), Python/Node start-up scripts.
/// Child processes get a cleaned copy; PowerShell gets a module path that names only system folders.
/// </summary>
public static class EnvironmentGuard
{
    /// <summary>Variables that load or run code in a child (or change how it searches for code). Removed for every child process.</summary>
    public static readonly IReadOnlySet<string> InjectingVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_DbgEnableMiniDump", "COMPlus_DbgEnableMiniDump",
        "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH", "COR_PROFILER_PATH_32", "COR_PROFILER_PATH_64",
        "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "CORECLR_PROFILER_PATH_32", "CORECLR_PROFILER_PATH_64",
        "__COMPAT_LAYER", "__PSLockdownPolicy",
        "PYTHONSTARTUP", "PYTHONPATH", "PYTHONHOME", "PYTHONUSERBASE", "PYTHONINSPECT",
        "NODE_OPTIONS", "NODE_PATH", "NODE_EXTRA_CA_CERTS",
        "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS",
        "PERL5LIB", "PERL5OPT", "RUBYOPT", "RUBYLIB",
    };

    /// <summary>
    /// Removes <see cref="InjectingVariables"/> from <paramref name="env"/> and returns the names that were present.
    /// Works on a copy of the environment (ProcessStartInfo.Environment), never on this process.
    /// </summary>
    public static IReadOnlyList<string> Clean(IDictionary<string, string?> env)
    {
        var removed = new List<string>();
        foreach (var key in env.Keys.ToList())
        {
            if (!InjectingVariables.Contains(key)) continue;
            env.Remove(key);
            removed.Add(key);
        }
        return removed;
    }

    /// <summary>Names of injecting variables set in THIS process (reported at startup).</summary>
    public static IReadOnlyList<string> PresentInThisProcess()
    {
        var found = new List<string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && InjectingVariables.Contains(k)) found.Add(k);
        return found;
    }

    /// <summary>
    /// The module folders of Windows PowerShell 5.1 itself: System32 and Program Files only (never a user folder). The ORDER matters:
    /// when the value equals the machine-wide default (Program Files first, then System32) Windows PowerShell treats it as «not set by
    /// the user» and puts the user's Documents\WindowsPowerShell\Modules back in front — so the folders are listed the other way round.
    /// </summary>
    public static string TrustedPowerShellModulePath() => string.Join(';',
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules"));

    /// <summary>Environment overrides for a powershell.exe child: trusted module path on top of the cleaned environment.</summary>
    public static void ApplyForPowerShell(IDictionary<string, string?> env)
    {
        Clean(env);
        env["PSModulePath"] = TrustedPowerShellModulePath();
    }
}
