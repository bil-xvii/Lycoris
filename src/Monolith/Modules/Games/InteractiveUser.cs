using System.Diagnostics;
using System.Security.Principal;
using Monolith.Core;

namespace Monolith.Modules.Games;

/// <summary>
/// The person logged in at this console. When Lycoris is elevated through UAC with a different admin
/// account, HKCU belongs to that admin — the user's own settings (Game Mode, Steam path…) then live in
/// HKU\&lt;SID of explorer.exe's owner&gt; (docs/research/gaming_tweaks.md §0). Read-only.
/// </summary>
internal static class InteractiveUser
{
    private static readonly Lazy<(string? Own, string? Shell)> Sids = new(Detect);

    /// <summary>SIDs that count as "the user": our own token and the shell owner in this session.</summary>
    public static IReadOnlyCollection<string> AllSids
    {
        get
        {
            var (own, shell) = Sids.Value;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (own is not null) set.Add(own);
            if (shell is not null) set.Add(shell);
            return set;
        }
    }

    /// <summary>Hive + path prefix of the interactive user's registry: HKCU, or HKU\&lt;SID&gt;\ when we run as another account.</summary>
    public static (RegHive Hive, string Prefix) Hive
    {
        get
        {
            var (own, shell) = Sids.Value;
            if (shell is not null && !string.Equals(own, shell, StringComparison.OrdinalIgnoreCase) &&
                RegistryHelper.KeyExists(RegHive.HKU, shell))
                return (RegHive.HKU, shell + "\\");
            return (RegHive.HKCU, "");
        }
    }

    public static int? ReadInt(string path, string name)
    {
        var (hive, prefix) = Hive;
        return RegistryHelper.ReadInt(hive, prefix + path, name);
    }

    public static string? ReadString(string path, string name)
    {
        var (hive, prefix) = Hive;
        return RegistryHelper.ReadString(hive, prefix + path, name);
    }

    private static (string? Own, string? Shell) Detect()
    {
        string? own = null, shell = null;
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            own = me.User?.Value;
        }
        catch { }
        try
        {
            int session;
            using (var current = Process.GetCurrentProcess()) session = current.SessionId;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                using (p)
                {
                    try
                    {
                        if (shell is null && p.SessionId == session) shell = GamesNative.GetOwnerSid(p.Id);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return (own, shell);
    }
}
