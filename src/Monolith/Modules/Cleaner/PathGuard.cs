using System.IO;

namespace Monolith.Modules.Cleaner;

/// <summary>Why a path was refused by <see cref="PathGuard"/>.</summary>
public enum GuardVerdict
{
    Ok,
    Empty,
    NotAbsolute,
    Network,
    DriveRoot,
    /// <summary>The path is (or contains) a protected folder: Windows, System32, Program Files, the profile root, Documents…</summary>
    ProtectedFolder,
    /// <summary>The path lies inside an area Lycoris never touches (WinSxS, Installer, Prefetch, Documents, Content.Outlook…).</summary>
    NoTouchArea,
    Missing,
    NotDirectory,
    /// <summary>The folder itself, or one of its parents, is a junction / symbolic link / mount point.</summary>
    ReparsePoint,
    Inaccessible,
}

/// <summary>
/// The cleaner's safety net. Every folder the cleaner walks and every file it deletes goes through this guard:
/// no drive roots, no system or user-content folders, nothing outside the item's own base folder,
/// never through junctions or symbolic links.
/// </summary>
public sealed class PathGuard
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS — cloud placeholders (OneDrive and others).</summary>
    public const FileAttributes PlaceholderAttributes = (FileAttributes)0x00440000;

    /// <summary>Path segments that are never touched wherever they appear (compared with surrounding backslashes).</summary>
    private static readonly string[] NoTouchSegments =
    [
        @"\System Volume Information\",
        @"\$Recycle.Bin\",
        @"\Content.Outlook\",
        @"\OfficeFileCache\",
        @"\$PatchCache$\",
        @"\MeasuredBoot\",
        @"\WinREAgent\",
        @"\librarycache\",
        @"\Local Storage\",
        @"\Session Storage\",
        @"\IndexedDB\",
        @"\LocalState\",
        @"\WinSxS\",
        @"\DriverStore\",
    ];

    private static readonly Lazy<PathGuard> DefaultGuard = new(CreateDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string[] _protected;
    private readonly string[] _noTouch;

    /// <param name="protectedFolders">A base folder may never be one of these, nor contain one of them.</param>
    /// <param name="noTouchAreas">Nothing inside these folders is ever enumerated or deleted.</param>
    public PathGuard(IEnumerable<string?> protectedFolders, IEnumerable<string?> noTouchAreas)
    {
        _protected = Clean(protectedFolders);
        _noTouch = Clean(noTouchAreas);

        static string[] Clean(IEnumerable<string?> paths) => paths
            .Select(Normalize)
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Guard configured for this PC (known folders resolved through the shell).</summary>
    public static PathGuard Default => DefaultGuard.Value;

    public IReadOnlyList<string> ProtectedFolders => _protected;
    public IReadOnlyList<string> NoTouchAreas => _noTouch;

    // ───────────────────────────── Pure string checks ─────────────────────────────

    /// <summary>
    /// Full, normalized path without a trailing separator (except for a drive root): resolves "..", "/" and the
    /// "\\?\" / "\??\" prefixes. Returns null for relative or malformed paths.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"').Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.Length == 0 || !Path.IsPathFullyQualified(p)) return null;
        try
        {
            p = Path.GetFullPath(p);
        }
        catch
        {
            return null;
        }
        if (p.Length > 3) p = p.TrimEnd('\\');
        return p;
    }

    public static bool IsNetworkPath(string normalized) => normalized.StartsWith(@"\\", StringComparison.Ordinal);

    public static bool IsDriveRoot(string normalized) =>
        normalized.Length <= 3 && normalized.Length >= 2 && normalized[1] == ':';

    /// <summary>True when <paramref name="path"/> is inside <paramref name="root"/> (and not equal to it).</summary>
    public static bool IsStrictlyUnder(string path, string root)
    {
        if (path.Length <= root.Length) return false;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        return root.EndsWith('\\') || path[root.Length] == '\\';
    }

    public static bool IsSameOrUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || IsStrictlyUnder(path, root);

    /// <summary>Junctions, symbolic links, mount points and cloud placeholders are never followed or deleted.</summary>
    public static bool ShouldSkip(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0 ||
        (attributes & PlaceholderAttributes) != 0 ||
        (attributes & FileAttributes.Offline) != 0;

    /// <summary>True when the normalized path is inside a no-touch area or contains a no-touch segment.</summary>
    public bool IsInNoTouchArea(string normalized)
    {
        foreach (var area in _noTouch)
            if (IsSameOrUnder(normalized, area))
                return true;
        var probe = normalized.EndsWith('\\') ? normalized : normalized + "\\";
        foreach (var segment in NoTouchSegments)
            if (probe.Contains(segment, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>String-only validation of a folder the cleaner is about to walk.</summary>
    public GuardVerdict CheckBase(string? path) => CheckBaseCore(path, allowProtectedFolder: false);

    /// <summary>
    /// Validation for a single-file target (e.g. %SystemRoot%\MEMORY.DMP): the parent folder may be a protected
    /// folder, because only that one named file is ever looked at — never the folder's other content.
    /// </summary>
    public GuardVerdict CheckSingleFileBase(string? path) => CheckBaseCore(path, allowProtectedFolder: true);

    private GuardVerdict CheckBaseCore(string? path, bool allowProtectedFolder)
    {
        if (string.IsNullOrWhiteSpace(path)) return GuardVerdict.Empty;
        var n = Normalize(path);
        if (n is null) return GuardVerdict.NotAbsolute;
        if (IsNetworkPath(n)) return GuardVerdict.Network;
        if (IsDriveRoot(n)) return GuardVerdict.DriveRoot;
        if (IsInNoTouchArea(n)) return GuardVerdict.NoTouchArea;
        // A single-file target never lists or walks its folder, so the folder itself may be a protected one.
        if (allowProtectedFolder) return GuardVerdict.Ok;
        foreach (var p in _protected)
        {
            // The base may not be a protected folder and may not contain one (C:\Users contains the profile).
            if (IsSameOrUnder(p, n)) return GuardVerdict.ProtectedFolder;
        }
        return GuardVerdict.Ok;
    }

    /// <summary>A file may be deleted only when it lies strictly inside its (canonical) base and outside no-touch areas.</summary>
    public bool IsFileAllowed(string? filePath, string canonicalBase)
    {
        var n = Normalize(filePath);
        if (n is null || IsNetworkPath(n)) return false;
        if (!IsStrictlyUnder(n, canonicalBase)) return false;
        return !IsInNoTouchArea(n);
    }

    // ───────────────────────────── On-disk checks ─────────────────────────────

    /// <summary>
    /// Checks the folder on disk: it exists, is a directory, neither it nor any parent is a reparse point,
    /// and the path the file system reports for it (GetFinalPathNameByHandle) is the same path —
    /// so nothing on the way is redirected. <paramref name="canonical"/> is that final path.
    /// </summary>
    public static GuardVerdict CheckOnDisk(string normalized, out string canonical)
    {
        canonical = normalized;
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(normalized);
        }
        catch (FileNotFoundException) { return GuardVerdict.Missing; }
        catch (DirectoryNotFoundException) { return GuardVerdict.Missing; }
        catch (UnauthorizedAccessException) { return GuardVerdict.Inaccessible; }
        catch (IOException) { return GuardVerdict.Inaccessible; }

        if ((attributes & FileAttributes.Directory) == 0) return GuardVerdict.NotDirectory;
        if ((attributes & FileAttributes.ReparsePoint) != 0) return GuardVerdict.ReparsePoint;

        // Every parent up to (not including) the drive root must be a plain folder too.
        var parent = Path.GetDirectoryName(normalized);
        while (!string.IsNullOrEmpty(parent) && !IsDriveRoot(parent))
        {
            try
            {
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return GuardVerdict.ReparsePoint;
            }
            catch (UnauthorizedAccessException) { return GuardVerdict.Inaccessible; }
            catch (IOException) { return GuardVerdict.Inaccessible; }
            parent = Path.GetDirectoryName(parent);
        }

        var finalPath = NativeCleaner.GetFinalPath(normalized, directory: true);
        if (finalPath is null) return GuardVerdict.Inaccessible;
        var expected = NativeCleaner.GetLongPath(normalized) ?? normalized;
        if (!string.Equals(Normalize(finalPath), Normalize(expected), StringComparison.OrdinalIgnoreCase))
            return GuardVerdict.ReparsePoint;

        canonical = Normalize(finalPath) ?? normalized;
        return GuardVerdict.Ok;
    }

    public static string Describe(GuardVerdict verdict) => verdict switch
    {
        GuardVerdict.Ok => Loc.T("ок"),
        GuardVerdict.Empty => Loc.T("путь не задан"),
        GuardVerdict.NotAbsolute => Loc.T("путь не абсолютный"),
        GuardVerdict.Network => Loc.T("сетевой путь"),
        GuardVerdict.DriveRoot => Loc.T("корень диска"),
        GuardVerdict.ProtectedFolder => Loc.T("защищённая папка"),
        GuardVerdict.NoTouchArea => Loc.T("защищённая область"),
        GuardVerdict.Missing => Loc.T("папка не найдена"),
        GuardVerdict.NotDirectory => Loc.T("это не папка"),
        GuardVerdict.ReparsePoint => Loc.T("путь проходит через ссылку (junction / symlink)"),
        GuardVerdict.Inaccessible => Loc.T("нет доступа"),
        _ => verdict.ToString(),
    };

    // ───────────────────────────── Default configuration ─────────────────────────────

    private static PathGuard CreateDefault()
    {
        static string F(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify); }
            catch { return ""; }
        }

        var windows = F(Environment.SpecialFolder.Windows);
        var profile = F(Environment.SpecialFolder.UserProfile);
        var systemDrive = string.IsNullOrEmpty(windows) ? "C:\\" : Path.GetPathRoot(windows) ?? "C:\\";
        var downloads = NativeCleaner.GetKnownFolder(NativeCleaner.FolderDownloads);
        var savedGames = NativeCleaner.GetKnownFolder(NativeCleaner.FolderSavedGames);
        var localLow = NativeCleaner.GetKnownFolder(NativeCleaner.FolderLocalAppDataLow);

        var userContent = new[]
        {
            F(Environment.SpecialFolder.MyDocuments),
            F(Environment.SpecialFolder.Desktop),
            F(Environment.SpecialFolder.DesktopDirectory),
            F(Environment.SpecialFolder.MyPictures),
            F(Environment.SpecialFolder.MyMusic),
            F(Environment.SpecialFolder.MyVideos),
            F(Environment.SpecialFolder.Favorites),
            F(Environment.SpecialFolder.CommonDocuments),
            F(Environment.SpecialFolder.CommonDesktopDirectory),
            F(Environment.SpecialFolder.CommonPictures),
            F(Environment.SpecialFolder.CommonMusic),
            F(Environment.SpecialFolder.CommonVideos),
            downloads,
            savedGames,
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveConsumer"),
            Environment.GetEnvironmentVariable("OneDriveCommercial"),
        };

        var protectedFolders = new List<string?>
        {
            windows,
            F(Environment.SpecialFolder.System),
            F(Environment.SpecialFolder.SystemX86),
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"),
            F(Environment.SpecialFolder.CommonProgramFiles),
            F(Environment.SpecialFolder.CommonProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
            profile,
            string.IsNullOrEmpty(profile) ? null : Path.GetDirectoryName(profile),
            Environment.GetEnvironmentVariable("PUBLIC"),
            F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.LocalApplicationData),
            localLow,
            F(Environment.SpecialFolder.StartMenu),
            F(Environment.SpecialFolder.Templates),
            F(Environment.SpecialFolder.Fonts),
        };
        protectedFolders.AddRange(userContent);

        var noTouch = new List<string?>
        {
            Path.Combine(windows, "WinSxS"),
            F(Environment.SpecialFolder.System),
            F(Environment.SpecialFolder.SystemX86),
            Path.Combine(windows, "Installer"),
            Path.Combine(windows, "Prefetch"),
            Path.Combine(windows, "servicing"),
            Path.Combine(windows, "Boot"),
            Path.Combine(windows, "Fonts"),
            Path.Combine(windows, "Logs", "MeasuredBoot"),
            Path.Combine(windows, "Logs", "WinREAgent"),
            Path.Combine(F(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
            Path.Combine(systemDrive, "Windows.old"),
            Path.Combine(systemDrive, "$Windows.~BT"),
            Path.Combine(systemDrive, "$Windows.~WS"),
        };
        noTouch.AddRange(userContent);

        return new PathGuard(protectedFolders.Where(p => !string.IsNullOrWhiteSpace(p)),
            noTouch.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
