using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace Lycoris.Core.Diagnostics;

/// <summary>Who and what the anonymizer must hide: every name this PC and this account are known by, not only two environment variables.</summary>
public static partial class ProblemReport
{
    private static readonly Lazy<IReadOnlyList<(string Needle, string Replacement)>> CurrentIdentityList = new(BuildCurrentIdentities);

    /// <summary>User name, domain, NetBIOS and DNS computer names, profile folder name, and the same for the person at the desktop when Lycoris runs as another admin.</summary>
    internal static IReadOnlyList<(string Needle, string Replacement)> CurrentIdentities => CurrentIdentityList.Value;

    internal static List<(string Needle, string Replacement)> IdentitiesFrom(IEnumerable<string?> computers, IEnumerable<string?> users, IEnumerable<string?> domains)
    {
        var list = new List<(string, string)>();
        foreach (var c in computers) if (!string.IsNullOrWhiteSpace(c)) list.Add((c.Trim(), "<pc>"));
        foreach (var u in users) if (!string.IsNullOrWhiteSpace(u)) list.Add((u.Trim(), "<user>"));
        foreach (var d in domains) if (!string.IsNullOrWhiteSpace(d)) list.Add((d.Trim(), "<domain>"));
        return list;
    }

    private static IReadOnlyList<(string Needle, string Replacement)> BuildCurrentIdentities()
    {
        var computers = new List<string?>();
        var users = new List<string?>();
        var domains = new List<string?>();
        Try(() => computers.Add(Environment.MachineName));
        Try(() => computers.Add(Dns.GetHostName()));
        Try(() =>
        {
            var ip = IPGlobalProperties.GetIPGlobalProperties();
            if (!string.IsNullOrEmpty(ip.DomainName))
            {
                domains.Add(ip.DomainName);
                computers.Add(ip.HostName + "." + ip.DomainName);   // the full DNS name, not only the 15 characters of NetBIOS
            }
        });
        Try(() => users.Add(Environment.UserName));
        Try(() => domains.Add(Environment.UserDomainName));
        Try(() => users.Add(LeafName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
        Try(() =>
        {
            // The person at the desktop can be another account than the one Lycoris runs as (over-the-shoulder UAC).
            foreach (var sid in Modules.Games.InteractiveUser.AllSids)
            {
                var profile = ProfilePathOfSid(sid);
                if (profile is not null) users.Add(LeafName(profile));
                try
                {
                    var account = new System.Security.Principal.SecurityIdentifier(sid).Translate(typeof(System.Security.Principal.NTAccount)).Value;
                    var slash = account.IndexOf('\\');
                    if (slash > 0) { domains.Add(account[..slash]); users.Add(account[(slash + 1)..]); } else users.Add(account);
                }
                catch { }
            }
        });
        return IdentitiesFrom(computers, users, domains);
    }

    private static void Try(Action action) { try { action(); } catch { } }

    private static string? LeafName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var leaf = Path.GetFileName(path.TrimEnd('\\', '/'));
        return leaf.Length == 0 ? null : leaf;
    }

    /// <summary>The profile folder of a SID from the machine-wide profile list (read only), or null.</summary>
    internal static string? ProfilePathOfSid(string sid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid, false);
            return key?.GetValue("ProfileImagePath") as string;
        }
        catch { return null; }
    }
}
