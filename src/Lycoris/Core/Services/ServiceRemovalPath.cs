using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Lycoris.Core.Security;
using Microsoft.Win32.SafeHandles;

namespace Lycoris.Core.Services;

internal static class ServiceRemovalPath
{
    private const int FileNotFound = 2, PathNotFound = 3;
    private const uint InvalidAttributes = uint.MaxValue;
    private const string RelativeExecutable = @"Everything\Everything.exe";

    internal static string? RecipeExecutable(string imageCommand) => RecipeExecutable(imageCommand,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

    // voidtools documents both standard installation folders and the literal -svc option.
    // Nothing else from the command line is interpreted, expanded, or passed to CreateService.
    internal static string? RecipeExecutable(string imageCommand, params string[] knownFolders)
    {
        if (imageCommand is not { Length: > 0 and <= 1024 }) return null;
        foreach (var folder in knownFolders)
        {
            if (string.IsNullOrEmpty(folder) || folder.Length < 4 || !char.IsAsciiLetter(folder[0])
                || folder[1] != ':' || folder[2] != '\\'
                || !string.Equals(folder, Path.GetFullPath(folder), StringComparison.Ordinal)
                || folder.IndexOfAny(['%', '/', '"']) >= 0)
                continue;
            var path = Path.Combine(folder, RelativeExecutable);
            var quoted = "\"" + path + "\"";
            if (imageCommand == quoted || imageCommand == quoted + " -svc"
                || (!path.Any(char.IsWhiteSpace) && (imageCommand == path || imageCommand == path + " -svc")))
                return path;
        }
        return null;
    }

    internal static RemovalPathState Probe(string command, string executable)
    {
        if (RecipeExecutable(command) != executable) return RemovalPathState.Unknown;

        var drive = Path.GetPathRoot(executable);
        var productDir = Path.GetDirectoryName(executable);
        var programFiles = productDir is null ? null : Path.GetDirectoryName(productDir);
        if (drive is null || productDir is null || programFiles is null || !IsFixedDevice(drive))
            return RemovalPathState.Unknown;

        var ancestors = new Stack<string>();
        for (var parent = productDir; !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            ancestors.Push(parent);

        string? lastPresent = null;
        RawSecurityDescriptor? lastAcl = null;
        foreach (var dir in ancestors)
        {
            const uint ReadControlAndAttributes = 0x20080, ShareReadWrite = 3, OpenExisting = 3, OpenDirectoryNoFollow = 0x02200000;
            using var handle = CreateFileW(dir, ReadControlAndAttributes, ShareReadWrite, 0, OpenExisting, OpenDirectoryNoFollow, 0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                // Allow a missing product folder after the verified Program Files directory only.
                if (dir != productDir || lastPresent != programFiles || !IsMissing(error))
                    return RemovalPathState.Unknown;
                break;
            }

            const int FileAttributeTagInfo = 9;
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var info, 8)
                || !info.Attributes.HasFlag(FileAttributes.Directory) || IsUnsafe(info.Attributes))
                return RemovalPathState.Unknown;
            // Inspect the very directory whose reparse flags were checked. A second lookup by path can follow a
            // junction installed on an untrusted directory between the attribute read and the ACL read.
            var acl = DirectoryAcl(handle);
            if (acl is null || !TrustDirectory(acl, PathTrust.AncestorRights))
                return RemovalPathState.Unknown;
            lastAcl = acl;
            lastPresent = dir;
        }

        if (lastAcl is null || !TrustDirectory(lastAcl, PathTrust.FolderWriteRights))
            return RemovalPathState.Unknown;
        var (fileAttributes, fileError) = Attributes(executable);
        if (fileError == 0) return IsUnsafe(fileAttributes) ? RemovalPathState.Unknown : RemovalPathState.Present;
        return IsMissing(fileError) ? RemovalPathState.Missing : RemovalPathState.Unknown;
    }

    internal static bool IsMissing(int error) => error is FileNotFound or PathNotFound;

    private static bool IsUnsafe(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Device)) != 0;

    private static bool TrustDirectory(RawSecurityDescriptor descriptor, FileSystemRights writeLike)
    {
        // Include attribute writes (e.g. changing a directory into a reparse point) and generics from raw ACEs.
        const int GenericWriteOrAll = 0x50000000;
        var dangerous = (int)(writeLike | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes) | GenericWriteOrAll;
        if (!PathTrust.IsTrustedSid(descriptor.Owner) || descriptor.DiscretionaryAcl is not { } dacl) return false;
        foreach (GenericAce entry in dacl)
        {
            if (entry is not CommonAce ace || ace.IsCallback
                || ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied)) return false;
            if (ace.AceQualifier == AceQualifier.AccessDenied || ace.AceFlags.HasFlag(AceFlags.InheritOnly)) continue;
            if (!PathTrust.IsTrustedSid(ace.SecurityIdentifier) && (ace.AccessMask & dangerous) != 0) return false;
        }
        return true;
    }

    private static RawSecurityDescriptor? DirectoryAcl(SafeFileHandle handle)
    {
        const int FileObject = 1, OwnerAndDacl = 5;
        if (GetSecurityInfo(handle, FileObject, OwnerAndDacl, 0, 0, 0, 0, out var descriptor) != 0) return null;
        if (descriptor == 0) return null;
        try
        {
            var bytes = GetSecurityDescriptorLength(descriptor);
            if (bytes is < 20 or > 16384) return null;
            var copy = new byte[bytes];
            Marshal.Copy(descriptor, copy, 0, bytes);
            return new RawSecurityDescriptor(copy, 0);
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    private static (FileAttributes Attributes, int Error) Attributes(string path)
    {
        var raw = GetFileAttributesW(path);
        return raw == InvalidAttributes ? (0, Marshal.GetLastPInvokeError()) : ((FileAttributes)raw, 0);
    }

    private static bool IsFixedDevice(string root)
    {
        if (root.Length != 3 || GetDriveTypeW(root) != 3) return false;
        // A DOS drive remapped by SUBST may appear fixed. Require a direct local volume mapping.
        var target = new char[512];
        var size = QueryDosDeviceW(root[..2], target, (uint)target.Length);
        if (size <= 1) return false;
        var terminator = Array.IndexOf(target, '\0');
        const string prefix = @"\Device\HarddiskVolume";
        return terminator > prefix.Length && target.AsSpan(0, prefix.Length).SequenceEqual(prefix)
            && target.AsSpan(prefix.Length, terminator - prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public FileAttributes Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing, nint security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTagInfo info, uint size);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int GetSecurityInfo(SafeFileHandle handle, int objectType, int information,
        nint owner, nint group, nint dacl, nint sacl, out nint descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int GetSecurityDescriptorLength(nint descriptor);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string name, [Out] char[] target, uint length);
}

