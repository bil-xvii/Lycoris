using System.IO;
using System.Security.Principal;
using Lycoris.Modules.Cleaner;
using Lycoris.Modules.Games;
using Microsoft.Win32;

namespace Lycoris.Core.Diagnostics;

/// <summary>
/// Where the archive may be written. The Desktop path comes from HKCU «User Shell Folders», which any program of the same user can
/// repoint (to a system folder, to a share, through a junction), so it is accepted only when it is a plain existing folder of the
/// fixed local disk INSIDE the profile of the person at the desktop. The file is created exclusively (never overwrites, never follows
/// a pre-planted file) and the path the file system reports for the open handle is compared with the folder.
/// </summary>
public static partial class ProblemReport
{
    /// <summary>The checked Desktop folder (as the file system reports it) and the profile it lies in.</summary>
    public sealed record Destination(string Folder, string Profile);

    /// <summary>Why a folder is not accepted, or null and the canonical folder when it is.</summary>
    internal static string? ValidateDesktop(string? desktop, string? profile, out string? canonical)
    {
        canonical = null;
        var d = PathGuard.Normalize(desktop);
        var p = PathGuard.Normalize(profile);
        if (d is null || p is null) return "the path is not absolute";
        if (PathGuard.IsNetworkPath(d) || PathGuard.IsNetworkPath(p)) return "a network path";
        if (!PathGuard.IsStrictlyUnder(d, p)) return "the folder is outside the profile";
        try
        {
            if (new DriveInfo(Path.GetPathRoot(d)!).DriveType != DriveType.Fixed) return "not a fixed local disk";
            // NO CreateDirectory: a missing folder is a refusal.
            if (!Directory.Exists(d) || !Directory.Exists(p)) return "the folder does not exist";
            // The profile and every folder down to the Desktop must not be a junction or a symbolic link. (Other reparse points,
            // such as the OneDrive placeholders, are no redirection: the final-path comparison below covers them.)
            var current = p;
            if (IsRedirect(current)) return "the profile is a link";
            foreach (var part in d[p.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (IsRedirect(current)) return "the folder or a parent is a link";
            }
            var finalDesktop = NativeCleaner.GetFinalPath(d, directory: true);
            var finalProfile = NativeCleaner.GetFinalPath(p, directory: true);
            if (finalDesktop is null || finalProfile is null) return "the folder cannot be checked";
            var fd = PathGuard.Normalize(finalDesktop);
            var fp = PathGuard.Normalize(finalProfile);
            if (fd is null || fp is null || PathGuard.IsNetworkPath(fd) || !PathGuard.IsStrictlyUnder(fd, fp)) return "the folder is redirected";
            if (!string.Equals(fd, PathGuard.Normalize(NativeCleaner.GetLongPath(d) ?? d), StringComparison.OrdinalIgnoreCase)) return "the folder is redirected";
            canonical = fd;
            return null;
        }
        catch (Exception e) { return e.GetType().Name; }
    }

    private static bool IsRedirect(string path)
    {
        var info = new DirectoryInfo(path);
        return info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;
    }

    /// <summary>
    /// Creates a NEW file «name-xxxxxx.zip» in the folder (FileMode.CreateNew, no sharing), checks that the open handle really lives in
    /// <paramref name="expectedFolder"/> (when given) and writes the data. A file that landed elsewhere is deleted and refused.
    /// Returns the path written.
    /// </summary>
    internal static string WriteUnique(string folder, byte[] data, string? expectedFolder = null, string prefix = "Lycoris-report", string extension = "zip")
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var path = Path.Combine(folder, $"{prefix}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}-{suffix}.{extension}");
        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            if (expectedFolder is not null)
            {
                var final = PathGuard.Normalize(NativeCleaner.GetFinalPath(fs.SafeFileHandle));
                var where = final is null ? null : PathGuard.Normalize(Path.GetDirectoryName(final));
                if (where is null || !string.Equals(where, PathGuard.Normalize(expectedFolder), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The report file did not land in the desktop folder.");
            }
            fs.Write(data);
            fs.Flush(true);
        }
        catch
        {
            fs.Dispose();
            try { File.Delete(path); } catch { }
            throw;
        }
        fs.Dispose();
        return path;
    }

    /// <summary>
    /// The Desktop of the person at the keyboard: with rights from another admin account that is THEIR profile, not the admin's.
    /// Throws <see cref="InvalidOperationException"/> (with a localized text) when no safe folder exists.
    /// </summary>
    public static Destination ResolveDestination()
    {
        var candidates = new List<(string Profile, string Desktop)>();
        if (Elevation.IsElevated && Elevation.InteractiveUserState != true)
        {
            // Another admin account (or unknown): use the shell owner's profile only when it is known.
            if (Elevation.InteractiveUserState != false) throw new InvalidOperationException(ProblemReportText.DesktopUnknownUser);
            string? own = null;
            try { using var me = WindowsIdentity.GetCurrent(); own = me.User?.Value; } catch { }
            var other = InteractiveUser.AllSids.FirstOrDefault(s => !string.Equals(s, own, StringComparison.OrdinalIgnoreCase));
            var profile = other is null ? null : ProfilePathOfSid(other);
            if (profile is null) throw new InvalidOperationException(ProblemReportText.DesktopUnknownUser);
            candidates.Add((profile, Path.Combine(profile, "Desktop")));
            var shell = ShellFolderDesktop(other!, profile);
            if (shell is not null) candidates.Insert(0, (profile, shell));
        }
        else
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add((profile, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
            candidates.Add((profile, Path.Combine(profile, "Desktop")));
        }
        foreach (var (profile, desktop) in candidates)
            if (ValidateDesktop(desktop, profile, out var canonical) is null && canonical is not null) return new Destination(canonical, profile);
        throw new InvalidOperationException(ProblemReportText.DesktopRefused);
    }

    private static string? ShellFolderDesktop(string sid, string profile)
    {
        try
        {
            using var key = Registry.Users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", false);
            var raw = key?.GetValue("Desktop", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (raw is null) return null;
            raw = raw.Replace("%USERPROFILE%", profile, StringComparison.OrdinalIgnoreCase);
            return raw.Contains('%') ? null : raw;
        }
        catch { return null; }
    }

    /// <summary>Writes the archive to the checked Desktop; returns the folder.</summary>
    /// <summary>Writes the PC health report (HTML) to the same checked Desktop; returns the full file path.</summary>
    public static string SaveHealthReportToDesktop(byte[] html)
    {
        var destination = ResolveDestination();
        return WriteUnique(destination.Folder, html, destination.Folder, "Lycoris-PC-report", "html");
    }

    public static string SaveToDesktop(byte[] archive)
    {
        var destination = ResolveDestination();
        WriteUnique(destination.Folder, archive, destination.Folder);
        return destination.Folder;
    }
}
