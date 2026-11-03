using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Lycoris.Core;

/// <summary>
/// Where Lycoris keeps its files.
/// Machine-wide state (undo journal, registry backups, driver backups/downloads) lives in %ProgramData%\Lycoris
/// and is locked down to Administrators/SYSTEM (see <see cref="SecureMachineDir"/>);
/// per-user state (settings, logs, cache) in %LocalAppData%\Lycoris.
/// </summary>
public static class AppPaths
{
    public const string AppName = "Lycoris";

    public static string LegacyMachineDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MONOLITH");
    public static string LegacyUserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MONOLITH");
    public static bool MachineMigrationFailed { get; private set; }
    public static string MachineReadDir => MachineMigrationFailed ? LegacyMachineDir : ReadDirectory(MachineDir, LegacyMachineDir);
    public static string UserReadDir => ReadDirectory(UserDir, LegacyUserDir);
    public static string SettingsReadFile => File.Exists(SettingsFile) ? SettingsFile : Path.Combine(UserReadDir, "settings.json");

    static AppPaths() => MigrateUserDir();

    private static void MigrateUserDir()
    {
        try { MigrateDirectory(LegacyUserDir, UserDir); }
        catch (Exception ex) { Log.Warn("User data migration failed; legacy data remains available: {0}", ex.Message); }
    }

    internal static string ReadDirectory(string current, string legacy) => Directory.Exists(current) ? current : legacy;

    internal static string MapLegacyMachinePath(string path)
        => MapLegacyMachinePath(path, LegacyMachineDir, MachineDir);

    internal static string MapLegacyMachinePath(string path, string legacyRoot, string currentRoot)
    {
        var full = Path.GetFullPath(path);
        var oldRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot));
        if (!full.StartsWith(oldRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return path;
        var relative = Path.GetRelativePath(oldRoot, full);
        var moved = Path.Combine(currentRoot, relative);
        return File.Exists(moved) || Directory.Exists(moved) ? moved : path;
    }

    internal static void MigrateDirectory(string legacy, string current)
    {
        try
        {
            if (!Directory.Exists(legacy)) return;
            if ((File.GetAttributes(legacy) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Legacy data folder is a reparse point.");

            if (Directory.Exists(current))
            {
                var marker = Path.Combine(current, ".migrated-from-monolith");
                if (File.Exists(marker)) return;
                MergeMissingEntries(legacy, current);
                try { File.WriteAllText(marker, "User data merged from MONOLITH."); }
                catch (Exception ex) { Log.Warn("Could not write migration marker {0}: {1}", marker, ex.Message); }
                return;
            }

            try { Directory.Move(legacy, current); }
            catch (Exception moveError)
            {
                try { CopyDirectory(legacy, current); }
                catch (Exception copyError) { Log.Warn("Could not move user data ({0}); copy also failed: {1}", moveError.Message, copyError.Message); }
            }
        }
        catch (Exception ex) { Log.Warn("User data migration failed; legacy data remains available: {0}", ex.Message); }
    }

    private static void MergeMissingEntries(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                var target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target) && !Directory.Exists(target)) File.Copy(file, target, false);
            }
            catch (Exception ex) { Log.Warn("Could not merge file {0}: {1}", file, ex.Message); }
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                var target = Path.Combine(destination, Path.GetFileName(dir));
                if (File.Exists(target)) continue;
                if (!Directory.Exists(target)) Directory.CreateDirectory(target);
                MergeMissingEntries(dir, target);
            }
            catch (Exception ex) { Log.Warn("Could not merge directory {0}: {1}", dir, ex.Message); }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    public static string MachineDir { get; } = TestRoot is { } t ? Path.Combine(t, "machine") : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);

    public static string UserDir { get; } = TestRoot is { } t ? Path.Combine(t, "user") : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>
    /// Crash tests (tools/crash-test.ps1) point a DEBUG build at a throwaway folder instead of the owner's data.
    /// Never in Release: an elevated Lycoris must not read a journal from a folder another process chose.
    /// </summary>
    internal static string? TestRoot
    {
        get
        {
#if DEBUG
            var root = Environment.GetEnvironmentVariable("LYCORIS_TEST_ROOT");
            return string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
#else
            return null;
#endif
        }
    }

    public static string JournalFile => Path.Combine(MachineDir, "journal.json");
    public static string BackupsDir => Path.Combine(MachineDir, "Backups");
    public static string DriverBackupsDir => Path.Combine(MachineDir, "DriverBackups");
    public static string BackupsReadDir => Path.Combine(MachineReadDir, "Backups");
    public static string DriverBackupsReadDir => Path.Combine(MachineReadDir, "DriverBackups");
    public static string SettingsFile => Path.Combine(UserDir, "settings.json");
    public static string LogsDir => Path.Combine(UserDir, "Logs");
    public static string CacheDir => Path.Combine(UserDir, "Cache");

    public static string ExecutablePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "Lycoris.exe");

    /// <summary>True when the machine folder was writable by non-administrators before it was secured in this run.</summary>
    public static bool MachineDirWasInsecure { get; private set; }

    /// <summary>
    /// True once <see cref="SecureMachineDir"/> has fully locked the folder down in this (elevated) run.
    /// False when securing failed part-way: the folder (and the journal in it) must then not be trusted.
    /// </summary>
    public static bool MachineDirSecured => _secured;

    /// <summary>True for paths inside %ProgramData%\Lycoris (only an elevated process may write there).</summary>
    public static bool IsUnderMachineDir(string path) =>
        Path.GetFullPath(path).StartsWith(MachineDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFullPath(path).TrimEnd('\\'), MachineDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates a directory if possible. Returns false instead of throwing (e.g. no admin rights).</summary>
    public static bool TryEnsureDir(string dir)
    {
        try
        {
            // The machine folder is created only by an elevated process, with a locked-down ACL.
            if (IsUnderMachineDir(dir))
            {
                if (!Elevation.IsElevated) return Directory.Exists(dir);
                SecureMachineDir();
                if (!MachineDirSecured) return false; // never write into a folder we could not lock down
            }
            Directory.CreateDirectory(dir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static bool _secured;
    private static readonly object SecureLock = new();

    /// <summary>
    /// Elevated only. Makes %ProgramData%\Lycoris safe to trust: owner = Administrators, SYSTEM and
    /// Administrators full control, Users read-only, inheritance from ProgramData cut (ProgramData lets every
    /// user create files). Removes reparse points (junctions/symlinks) inside and resets child ACLs.
    /// If the folder was writable by non-admins, <see cref="MachineDirWasInsecure"/> is set — the caller must
    /// then treat the existing journal as untrusted. If securing fails, <see cref="MachineDirSecured"/> stays
    /// false (the next call retries) and the journal must not be used at all.
    /// </summary>
    public static void SecureMachineDir()
    {
        if (!Elevation.IsElevated) return;
        lock (SecureLock)
        {
            if (_secured) return;
            try
            {
                if (!Directory.Exists(MachineDir) && Directory.Exists(LegacyMachineDir))
                {
                    try
                    {
                        var legacy = new DirectoryInfo(LegacyMachineDir);
                        var owner = legacy.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                        if (legacy.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                            (owner is not null && !owner.Equals(AdminsSid) && !owner.Equals(SystemSid)) ||
                            Security.PathTrust.WhyUserWritable(LegacyMachineDir) is not null)
                            throw new UnauthorizedAccessException("Legacy machine folder is linked or not trusted.");
                        Directory.Move(LegacyMachineDir, MachineDir);
                    }
                    catch (Exception ex) { MachineMigrationFailed = true; Log.Warn("Legacy machine folder migration failed; reading its data if needed: {0}", ex.Message); }
                }
                var dir = new DirectoryInfo(MachineDir);
                if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // A link planted where our folder should be: remove the link itself (not its target).
                    MachineDirWasInsecure = true;
                    dir.Delete();
                    dir.Refresh();
                }

                var security = BuildSecurity();
                if (!dir.Exists)
                {
                    dir.Create(security);
                }
                else
                {
                    if (IsInsecure(dir.GetAccessControl())) MachineDirWasInsecure = true;
                    dir.SetAccessControl(security);
                }

                var childFailures = ResetChildren(dir, 0);
                if (childFailures > 0)
                {
                    // journal.json / .bak or a planted link could not be fixed: refuse to trust the folder this run.
                    Log.Error("Не удалось исправить права для {0} файлов в папке данных Lycoris — журнал изменений не будет использоваться.", childFailures);
                    return;
                }
                _secured = true;
                if (MachineDirWasInsecure)
                {
                    Log.Warn("Папка данных Lycoris была доступна для записи обычным пользователям — права исправлены.");
                    // Anything there could have been planted: never install drivers or run installers from it.
                    var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                    foreach (var sub in new[] { DriverBackupsDir, Path.Combine(MachineDir, "Downloads"), BackupsDir })
                    {
                        try
                        {
                            if (Directory.Exists(sub)) Directory.Move(sub, $"{sub}.untrusted-{stamp}");
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("Не удалось отложить {0}: {1}", sub, ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось защитить папку данных Lycoris", ex);
            }
        }
    }

    private static DirectorySecurity BuildSecurity()
    {
        var s = new DirectorySecurity();
        s.SetOwner(AdminsSid);
        s.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        s.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return s;
    }

    private const FileSystemRights WriteLike =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static bool IsTrusted(SecurityIdentifier? sid) =>
        sid is not null && (sid.Equals(SystemSid) || sid.Equals(AdminsSid) || sid.Equals(TrustedInstallerSid));

    private static bool IsInsecure(FileSystemSecurity acl)
    {
        if (!IsTrusted(acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)) return true;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue; // e.g. CREATOR OWNER template
            if (IsTrusted(rule.IdentityReference as SecurityIdentifier)) continue;
            if ((rule.FileSystemRights & WriteLike) != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Children inherit the locked-down parent ACL; links inside are removed, never followed.
    /// Returns how many critical entries could not be fixed (a link that stays, or a top-level file such as
    /// journal.json whose ACL could not be reset).
    /// </summary>
    private static int ResetChildren(DirectoryInfo dir, int depth)
    {
        if (depth > 8) return 0;
        var failures = 0;
        foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            var isLink = false;
            try
            {
                isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (isLink)
                {
                    MachineDirWasInsecure = true;
                    entry.Delete(); // removes the link only
                    continue;
                }
                if (entry is DirectoryInfo sub)
                {
                    var ds = new DirectorySecurity();
                    ds.SetOwner(AdminsSid);
                    ds.SetAccessRuleProtection(false, false);
                    if (IsInsecure(sub.GetAccessControl())) MachineDirWasInsecure = true;
                    sub.SetAccessControl(ds);
                    failures += ResetChildren(sub, depth + 1);
                }
                else if (entry is FileInfo file)
                {
                    if (IsInsecure(file.GetAccessControl())) MachineDirWasInsecure = true;
                    var fs = new FileSecurity();
                    fs.SetOwner(AdminsSid);
                    fs.SetAccessRuleProtection(false, false);
                    file.SetAccessControl(fs);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Права {0}: {1}", entry.FullName, ex.Message);
                if (isLink || (depth == 0 && entry is FileInfo)) failures++;
            }
        }
        return failures;
    }
}
