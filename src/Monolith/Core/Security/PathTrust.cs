using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Monolith.Core.Security;

/// <summary>
/// «Can an ordinary program replace this file?» — the question behind every «an elevated process runs / loads / trusts that
/// path». A path counts as protected only when every owner and every Allow rule that grants a write-like right (to the
/// file, the folder it lies in, and the right to delete or re-permission any folder on the way up) belongs to SYSTEM,
/// Administrators or TrustedInstaller. Anything else — the user's own account, Users, Authenticated Users, Everyone — means
/// «user-writable»: a program running as that user could swap the file before an elevated process uses it (UAC does not
/// stop that: it binds to the path, not to the bytes).
/// </summary>
public static class PathTrust
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier CreatorOwnerSid = new(WellKnownSidType.CreatorOwnerSid, null);

    /// <summary>The accounts that may change protected files: SYSTEM, Administrators, TrustedInstaller.</summary>
    public static bool IsTrustedSid(SecurityIdentifier? sid) =>
        sid is not null && (sid.Equals(SystemSid) || sid.Equals(AdminsSid) || sid.Equals(TrustedInstallerSid));

    /// <summary>Rights that let a program replace or re-permission the FILE itself.</summary>
    internal const FileSystemRights FileWriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Rights on the FOLDER that holds the file that let a program plant or replace files next to it.</summary>
    internal const FileSystemRights FolderWriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>
    /// Rights on a folder further up: creating files there does not touch the path below, but deleting/renaming the child or
    /// re-permissioning does (renaming a folder away and putting another one in its place).
    /// </summary>
    internal const FileSystemRights AncestorRights =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>
    /// The first untrusted principal that holds one of <paramref name="rights"/> in <paramref name="acl"/> (or owns it), or null
    /// when only trusted accounts do. Pure over an ACL object, so tests build ACLs in memory.
    /// </summary>
    public static string? FindUntrustedWriter(FileSystemSecurity acl, FileSystemRights rights)
    {
        if (acl.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !IsTrustedSid(owner) && !owner.Equals(CreatorOwnerSid))
            return Describe(owner) + " (owner)";
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue; // applies to children only
            if (rule.IdentityReference is not SecurityIdentifier sid) continue;
            if (IsTrustedSid(sid) || sid.Equals(CreatorOwnerSid)) continue;
            if ((rule.FileSystemRights & rights) != 0) return Describe(sid);
        }
        return null;
    }

    private static string Describe(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch { return sid.Value; }
    }

    /// <summary>
    /// Why <paramref name="path"/> (a file) can be replaced by a non-administrator, or null when it cannot (or the path does
    /// not exist / cannot be read: unreadable is reported as <c>"unknown"</c> so callers fail closed).
    /// </summary>
    public static string? WhyUserWritable(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return "network path";
            var isFile = File.Exists(full);
            if (!isFile && !Directory.Exists(full)) return "unknown";

            string? who;
            if (isFile)
            {
                who = FindUntrustedWriter(new FileInfo(full).GetAccessControl(), FileWriteRights);
                if (who is not null) return who;
            }
            var folder = isFile ? Path.GetDirectoryName(full) : full;
            var first = true;
            while (!string.IsNullOrEmpty(folder))
            {
                var dir = new DirectoryInfo(folder);
                who = FindUntrustedWriter(dir.GetAccessControl(), first ? FolderWriteRights : AncestorRights);
                if (who is not null) return $"{folder}: {who}";
                first = false;
                folder = Path.GetDirectoryName(folder);
            }
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return "unknown";
        }
    }

    /// <summary>True when a non-administrator program could replace the file (or the answer cannot be had).</summary>
    public static bool IsUserWritable(string path) => WhyUserWritable(path) is not null;

    /// <summary>
    /// True when the path is on a network share, in a Temp folder, or on a removable drive — places an attacker (or a
    /// careless download) controls even without any account. Elevated work is never started from there.
    /// </summary>
    public static bool IsHostileLocation(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            foreach (var temp in new[] { Path.GetTempPath(), Environment.GetEnvironmentVariable("TEMP"), Environment.GetEnvironmentVariable("TMP"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") })
            {
                if (string.IsNullOrWhiteSpace(temp)) continue;
                var t = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temp)) + Path.DirectorySeparatorChar;
                if (full.StartsWith(t, StringComparison.OrdinalIgnoreCase)) return true;
            }
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root))
            {
                var type = new DriveInfo(root).DriveType;
                if (type is DriveType.Network or DriveType.Removable or DriveType.CDRom) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return true;
        }
    }
}
