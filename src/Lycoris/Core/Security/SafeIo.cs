using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Lycoris.Core.Security;

/// <summary>
/// Reading files that somebody else could have written (settings, caches, markers, manifests, imported files). Every
/// reader gets a hard size limit, a refusal for links, and a handle that nobody can write through while it reads — so a
/// huge or swapped file can neither exhaust memory nor change between «look» and «read».
/// </summary>
public static class SafeIo
{
    private const uint GenericRead = 0x80000000, ShareRead = 1, OpenExisting = 3, OpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInfo = 9;
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTagInfo { public uint Attributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTagInfo info, uint size);

    /// <summary>Opens the file itself (without following a final reparse point) and reads its bounded tail.</summary>
    public static byte[] ReadTailNoFollow(string path, int maxBytes)
    {
        using var handle = CreateFileW(path, GenericRead, ShareRead, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Cannot open file without following links.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var info, (uint)Marshal.SizeOf<AttributeTagInfo>()))
            throw new IOException("Cannot inspect opened file handle.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) != 0)
            throw new InvalidDataException("the file is a link or directory");
        using var fs = new FileStream(handle, FileAccess.Read, 4096, false);
        var take = (int)Math.Min(fs.Length, maxBytes);
        fs.Position = fs.Length - take;
        var bytes = new byte[take]; var read = 0;
        while (read < take) { var n = fs.Read(bytes, read, take - read); if (n == 0) break; read += n; }
        return read == bytes.Length ? bytes : bytes[..read];
    }
    /// <summary>Settings, caches and markers are small; nothing Lycoris keeps in the user folder needs more.</summary>
    public const int MaxSmallFileBytes = 4 * 1024 * 1024;

    /// <summary>The undo journal may grow with years of use; it is still bounded.</summary>
    public const int MaxJournalBytes = 64 * 1024 * 1024;

    /// <summary>Reads at most <paramref name="maxBytes"/>; throws <see cref="InvalidDataException"/> for a larger file or a link.</summary>
    public static byte[] ReadBytes(string path, int maxBytes = MaxSmallFileBytes)
    {
        var info = new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("the file is a link");
        // FileShare.Read: while we read, no other process can write or delete the file.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (fs.Length > maxBytes) throw new InvalidDataException($"the file is larger than {maxBytes} bytes");
        var buffer = new byte[(int)fs.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        // The file grew between Length and Read (it cannot with FileShare.Read, but never trust): one more byte means too big.
        if (fs.ReadByte() >= 0) throw new InvalidDataException($"the file is larger than {maxBytes} bytes");
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>Like <see cref="ReadBytes"/> as UTF-8 text without the byte-order mark.</summary>
    public static string ReadText(string path, int maxBytes = MaxSmallFileBytes)
    {
        var bytes = ReadBytes(path, maxBytes);
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>True for text without control characters other than tab (names, ids and paths that come from a file).</summary>
    public static bool IsPlainText(string? s, int maxLength)
    {
        if (s is null || s.Length > maxLength) return false;
        foreach (var c in s)
            if (char.IsControl(c) && c != '\t') return false;
        return true;
    }

    /// <summary>
    /// A folder for temporary files that only this program (and administrators) can touch: the locked-down machine folder
    /// when elevated, the user's Lycoris folder otherwise — never the shared %TEMP%. Null when it cannot be had.
    /// </summary>
    public static string? PrivateTempDir()
    {
        var dir = Path.Combine(Elevation.IsElevated ? AppPaths.MachineDir : AppPaths.UserDir, "Tmp");
        return AppPaths.TryEnsureDir(dir) ? dir : null;
    }

    /// <summary>A not yet existing file name inside <see cref="PrivateTempDir"/> (random, unguessable). Null when no private folder.</summary>
    public static string? NewPrivateTempFile(string extension)
    {
        if (PrivateTempDir() is not { } dir) return null;
        var ext = extension.Length > 0 && extension.All(c => char.IsAsciiLetterOrDigit(c) || c == '.') ? extension : "";
        return Path.Combine(dir, "mn_" + Guid.NewGuid().ToString("N") + ext);
    }
}
