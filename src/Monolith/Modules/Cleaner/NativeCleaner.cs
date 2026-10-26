using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Monolith.Modules.Cleaner;

/// <summary>Result of deleting one file or folder through its handle.</summary>
public enum DeleteOutcome
{
    Deleted,
    /// <summary>Sharing / lock violation: a program has the file open. Skipped silently and counted.</summary>
    InUse,
    Denied,
    /// <summary>The file disappeared on its own between analysis and cleaning.</summary>
    Gone,
    /// <summary>The file changed after the analysis (size, time) — it is no longer the file the user approved.</summary>
    Changed,
    /// <summary>The safety checks refused it (now a link, moved outside the base, …).</summary>
    Refused,
    /// <summary>A folder that still has content.</summary>
    NotEmpty,
    Error,
}

public readonly record struct DeleteResult(DeleteOutcome Outcome, long FreedBytes = 0, int Win32Error = 0);

/// <summary>
/// Win32 helpers for the cleaner. Deletion works on a handle opened with FILE_FLAG_OPEN_REPARSE_POINT, so a file
/// or folder swapped for a link between analysis and cleaning is detected (attribute tag + final path check)
/// instead of followed.
/// </summary>
internal static class NativeCleaner
{
    public static readonly Guid FolderDownloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid FolderSavedGames = new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");
    public static readonly Guid FolderLocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    private const uint DELETE = 0x00010000;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    private const uint FILE_SHARE_ALL = 0x00000007;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    private const int FileBasicInfo = 0;
    private const int FileStandardInfo = 1;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfo = 9;
    private const int FileDispositionInfoEx = 21;

    private const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    private const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;
    private const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    private const uint FILE_ATTRIBUTE_READONLY = 0x1;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_INVALID_FUNCTION = 1;
    private const int ERROR_NOT_SUPPORTED = 50;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int ERROR_SHARING_VIOLATION = 32;
    private const int ERROR_LOCK_VIOLATION = 33;
    private const int ERROR_DIR_NOT_EMPTY = 145;

    // ───────────────────────────── Paths ─────────────────────────────

    /// <summary>"\\?\" form for Win32 calls (long paths, no further parsing).</summary>
    public static string ToExtended(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    /// <summary>The real path of a file or folder as the file system reports it, or null.</summary>
    public static string? GetFinalPath(string path, bool directory)
    {
        using var h = CreateFileW(ToExtended(path), FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) return null;
        return GetFinalPath(h);
    }

    private static string? GetFinalPath(SafeFileHandle h)
    {
        var buffer = new char[1024];
        var len = GetFinalPathNameByHandleW(h, buffer, (uint)buffer.Length, 0);
        if (len == 0) return null;
        if (len >= buffer.Length)
        {
            buffer = new char[len + 1];
            len = GetFinalPathNameByHandleW(h, buffer, (uint)buffer.Length, 0);
            if (len == 0 || len >= buffer.Length) return null;
        }
        var s = new string(buffer, 0, (int)len);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return null; // network: never
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        return s;
    }

    /// <summary>Expands 8.3 short names ("LYCORI~1") to the long form; null when the path does not exist.</summary>
    public static string? GetLongPath(string path)
    {
        var buffer = new char[1024];
        var len = GetLongPathNameW(ToExtended(path), buffer, (uint)buffer.Length);
        if (len == 0 || len >= buffer.Length) return null;
        var s = new string(buffer, 0, (int)len);
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    /// <summary>The 8.3 form of a path (may equal the input when short names are disabled), or null.</summary>
    public static string? GetShortPath(string path)
    {
        var buffer = new char[1024];
        var len = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        if (len == 0 || len >= buffer.Length) return null;
        return new string(buffer, 0, (int)len);
    }

    public static string? GetKnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────────── Deletion ─────────────────────────────

    /// <summary>
    /// Deletes one file that the analysis put into the manifest, after re-validating it through its own handle:
    /// still a regular file (no reparse tag), still inside the base (final path), same size and timestamps
    /// as analysed and still older than the cut-off. Hard-linked files count as 0 freed bytes.
    /// </summary>
    public static DeleteResult DeleteFile(FileCandidate c, string canonicalBase, long cutoffFileTime, PathGuard guard)
    {
        if (!guard.IsFileAllowed(c.Path, canonicalBase)) return new(DeleteOutcome.Refused);

        using var h = CreateFileW(ToExtended(c.Path), DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return FromError(Marshal.GetLastPInvokeError());

        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, out FILE_ATTRIBUTE_TAG_INFO tag, Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>()))
            return FromError(Marshal.GetLastPInvokeError());
        if ((tag.FileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) != 0 || tag.ReparseTag != 0)
            return new(DeleteOutcome.Refused);
        if (PathGuard.ShouldSkip((FileAttributes)tag.FileAttributes)) return new(DeleteOutcome.Refused);

        if (!GetFileInformationByHandleEx(h, FileStandardInfo, out FILE_STANDARD_INFO std, Marshal.SizeOf<FILE_STANDARD_INFO>()) ||
            !GetFileInformationByHandleEx(h, FileBasicInfo, out FILE_BASIC_INFO basic, Marshal.SizeOf<FILE_BASIC_INFO>()))
            return FromError(Marshal.GetLastPInvokeError());

        var final = GetFinalPath(h);
        if (final is null || !guard.IsFileAllowed(final, canonicalBase)) return new(DeleteOutcome.Refused);

        if (std.EndOfFile != c.Size || basic.LastWriteTime != c.LastWriteFileTime || basic.CreationTime != c.CreationFileTime)
            return new(DeleteOutcome.Changed);
        if (Math.Max(basic.LastWriteTime, basic.CreationTime) >= cutoffFileTime)
            return new(DeleteOutcome.Changed);

        var freed = std.NumberOfLinks > 1 ? 0 : std.AllocationSize;
        var error = MarkForDeletion(h, c.Path, (basic.FileAttributes & FILE_ATTRIBUTE_READONLY) != 0);
        return error == 0 ? new(DeleteOutcome.Deleted, freed) : FromError(error);
    }

    /// <summary>Removes a folder only if it is empty, is a real folder (not a link) and is still inside the base.</summary>
    public static DeleteResult DeleteEmptyDirectory(DirCandidate d, string canonicalBase, PathGuard guard)
    {
        if (!guard.IsFileAllowed(d.Path, canonicalBase)) return new(DeleteOutcome.Refused);

        using var h = CreateFileW(ToExtended(d.Path), DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return FromError(Marshal.GetLastPInvokeError());

        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, out FILE_ATTRIBUTE_TAG_INFO tag, Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>()))
            return FromError(Marshal.GetLastPInvokeError());
        if ((tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 || tag.ReparseTag != 0 || (tag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
            return new(DeleteOutcome.Refused);
        if (!GetFileInformationByHandleEx(h, FileBasicInfo, out FILE_BASIC_INFO basic, Marshal.SizeOf<FILE_BASIC_INFO>()))
            return FromError(Marshal.GetLastPInvokeError());
        if (basic.CreationTime != d.CreationFileTime) return new(DeleteOutcome.Changed);

        var final = GetFinalPath(h);
        if (final is null || !guard.IsFileAllowed(final, canonicalBase)) return new(DeleteOutcome.Refused);

        // A non-empty folder fails here with ERROR_DIR_NOT_EMPTY — nothing inside is ever removed by this call.
        var error = MarkForDeletion(h, d.Path, (basic.FileAttributes & FILE_ATTRIBUTE_READONLY) != 0);
        return error == 0 ? new(DeleteOutcome.Deleted) : FromError(error);
    }

    /// <summary>Sets the delete disposition on an open handle. Returns 0 or a Win32 error code.</summary>
    private static int MarkForDeletion(SafeFileHandle h, string path, bool readOnly)
    {
        var ex = new FILE_DISPOSITION_INFO_EX
        {
            Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
        };
        if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref ex, Marshal.SizeOf<FILE_DISPOSITION_INFO_EX>())) return 0;
        var error = Marshal.GetLastPInvokeError();
        if (error is not (ERROR_INVALID_PARAMETER or ERROR_NOT_SUPPORTED or ERROR_INVALID_FUNCTION)) return error;

        // File systems without POSIX delete semantics (FAT32/exFAT) or older builds.
        ex.Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
        if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref ex, Marshal.SizeOf<FILE_DISPOSITION_INFO_EX>())) return 0;

        var classic = new FILE_DISPOSITION_INFO { DeleteFile = 1 };
        if (SetFileInformationByHandle(h, FileDispositionInfo, ref classic, Marshal.SizeOf<FILE_DISPOSITION_INFO>())) return 0;
        error = Marshal.GetLastPInvokeError();
        if (error != ERROR_ACCESS_DENIED || !readOnly) return error;

        // Classic disposition refuses read-only files: clear the attribute through a second handle (same safety flags).
        using (var attrHandle = CreateFileW(ToExtended(path), FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero,
                   OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
        {
            if (attrHandle.IsInvalid) return Marshal.GetLastPInvokeError();
            if (!GetFileInformationByHandleEx(attrHandle, FileBasicInfo, out FILE_BASIC_INFO b, Marshal.SizeOf<FILE_BASIC_INFO>()))
                return Marshal.GetLastPInvokeError();
            b.FileAttributes &= ~FILE_ATTRIBUTE_READONLY;
            if (b.FileAttributes == 0) b.FileAttributes = FILE_ATTRIBUTE_NORMAL;
            b.CreationTime = b.LastAccessTime = b.LastWriteTime = b.ChangeTime = 0; // 0 = leave unchanged
            if (!SetFileInformationByHandle(attrHandle, FileBasicInfo, ref b, Marshal.SizeOf<FILE_BASIC_INFO>()))
                return Marshal.GetLastPInvokeError();
        }
        return SetFileInformationByHandle(h, FileDispositionInfo, ref classic, Marshal.SizeOf<FILE_DISPOSITION_INFO>())
            ? 0
            : Marshal.GetLastPInvokeError();
    }

    private static DeleteResult FromError(int error) => error switch
    {
        ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION => new(DeleteOutcome.InUse, 0, error),
        ERROR_ACCESS_DENIED => new(DeleteOutcome.Denied, 0, error),
        ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => new(DeleteOutcome.Gone, 0, error),
        ERROR_DIR_NOT_EMPTY => new(DeleteOutcome.NotEmpty, 0, error),
        _ => new(DeleteOutcome.Error, 0, error),
    };

    // ───────────────────────────── Low-priority worker thread ─────────────────────────────

    /// <summary>Puts the calling thread into background mode (low I/O and CPU priority). Returns false if it already was.</summary>
    public static bool EnterBackgroundMode() => SetThreadPriority(GetCurrentThread(), 0x00010000);

    public static void LeaveBackgroundMode() => SetThreadPriority(GetCurrentThread(), 0x00020000);

    // ───────────────────────────── Recycle Bin ─────────────────────────────

    /// <summary>Size and item count of the current user's Recycle Bin on all drives, or null when the shell refuses.</summary>
    public static (long Bytes, long Items)? QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        var hr = SHQueryRecycleBinW(null, ref info);
        return hr == 0 ? (info.i64Size, info.i64NumItems) : null;
    }

    /// <summary>Empties the Recycle Bin on all drives without the shell's own dialogs. Returns the HRESULT.</summary>
    public static int EmptyRecycleBin() => SHEmptyRecycleBinW(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);

    // ───────────────────────────── Processes / sessions ─────────────────────────────

    /// <summary>True when a named kernel mutex exists (also when it exists but we may not open it).</summary>
    public static bool MutexExists(string name)
    {
        var h = OpenMutexW(0x00100000 /* SYNCHRONIZE */, false, name);
        if (h != IntPtr.Zero)
        {
            CloseHandle(h);
            return true;
        }
        return Marshal.GetLastPInvokeError() == ERROR_ACCESS_DENIED;
    }

    public static string? GetProcessImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>"DOMAIN\user" signed in to this desktop session, or null when unknown.</summary>
    public static (string? Domain, string? User) GetSessionUser()
    {
        return (Query(7 /* WTSDomainName */), Query(5 /* WTSUserName */));

        static string? Query(int infoClass)
        {
            try
            {
                if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, infoClass, out var buffer, out _)) return null;
                try { return Marshal.PtrToStringUni(buffer); }
                finally { WTSFreeMemory(buffer); }
            }
            catch
            {
                return null;
            }
        }
    }

    #region P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO_EX
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FILE_ATTRIBUTE_TAG_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FILE_STANDARD_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FILE_BASIC_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref FILE_DISPOSITION_INFO_EX info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref FILE_DISPOSITION_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref FILE_BASIC_INFO info, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] buffer, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] buffer, uint length);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenMutexW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] buffer, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
    #endregion
}
