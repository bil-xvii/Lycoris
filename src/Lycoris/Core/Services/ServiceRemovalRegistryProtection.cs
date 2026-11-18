using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Lycoris.Core.Services;

/// <summary>Keeps authenticated service backups outside the authority of generic registry writes and undo.</summary>
internal static class ServiceRemovalRegistryProtection
{
    internal const string StoreName = "Lycoris.ServiceRemoval.v1";
    private const string MachinePrefix = @"\REGISTRY\MACHINE\";
    private const int KeyNameInformation = 3;
    private const int NativeView = 0x0100, ReadKey = 0x20019, WriteKey = 0x20006;
    private const int CaseInsensitiveNoReparse = 0x1040;

    internal static bool IsReserved(RegHive hive, string? path) =>
        hive == RegHive.HKLM && IsReservedMachinePath(path);

    internal static bool IsReservedNativePath(string path) =>
        path.StartsWith(MachinePrefix, StringComparison.OrdinalIgnoreCase) && IsReservedMachinePath(path[MachinePrefix.Length..]);

    private static bool IsReservedMachinePath(string? path)
    {
        if (path is null) return false;
        // Win32 names are NUL-terminated. Also reserve the direct physical name of the 32-bit view.
        var end = path.IndexOf('\0');
        if (end >= 0) path = path[..end];
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase)) return false;
        var index = parts[1].Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        return parts.Length > index && parts[index].Equals(StoreName, StringComparison.OrdinalIgnoreCase);
    }

    internal static void RequirePublicName(RegHive hive, string path)
    {
        if (IsReserved(hive, path) || path.Contains('\0'))
            throw new UnauthorizedAccessException("Protected service recovery registry area.");
    }

    internal static void RequirePublicHandle(RegistryKey key) => RequirePublicPath(NameOf(key.Handle));

    /// <summary>Checks the exact key handle used for the mutation, including when a path uses registry aliases.</summary>
    internal static RegistryKey? OpenForMutation(RegistryKey root, RegHive hive, string path, bool create, bool delete = false)
    {
        RequirePublicName(hive, path);
        if (!create)
        {
            var key = delete
                ? root.OpenSubKey(path, RegistryRights.ReadKey | RegistryRights.Delete)
                : root.OpenSubKey(path, writable: true);
            return Verify(key);
        }

        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new UnauthorizedAccessException("Cannot write a registry hive root.");
        RegistryKey? parent = null;
        try
        {
            for (var i = 0; i < parts.Length; i++)
            {
                var current = parent ?? root;
                var child = (i == parts.Length - 1 ? current.OpenSubKey(parts[i], writable: true)
                        : current.OpenSubKey(parts[i], writable: false))
                    ?? CreateChild(parent, hive, parts[i]);
                var verified = Verify(child);
                parent?.Dispose();
                parent = verified;
            }
            var result = parent;
            parent = null;
            return result;
        }
        finally
        {
            parent?.Dispose();
        }
    }

    internal static void DeleteEmpty(RegistryKey key)
    {
        RequirePublicHandle(key);
        if (key.SubKeyCount != 0 || key.ValueCount != 0) return;
        var status = NtDeleteKey(key.Handle);
        if (status != 0) throw Error(status);
    }

    private static RegistryKey? Verify(RegistryKey? key)
    {
        if (key is null) return null;
        try
        {
            RequirePublicHandle(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static void RequirePublicPath(string path)
    {
        if (IsReservedNativePath(path))
            throw new UnauthorizedAccessException("Protected service recovery registry area.");
    }

    private static RegistryKey CreateChild(RegistryKey? parent, RegHive hive, string name)
    {
        if (parent is not null)
        {
            var handle = parent.Handle;
            RequirePublicPath(NameOf(handle) + "\\" + name);
            return CreateNoReparse(handle, name);
        }

        // Predefined HKEY constants are Win32 aliases, not handles for NtCreateKey.
        switch (hive)
        {
            case RegHive.HKLM:
                return CreateNoReparse(null, MachinePrefix + name);
            case RegHive.HKU:
                return CreateNoReparse(null, @"\REGISTRY\USER\" + name);
            case RegHive.HKCU:
                var status = RegOpenCurrentUser(ReadKey | NativeView, out var currentUser);
                using (currentUser)
                {
                    if (status != 0) throw new Win32Exception(status);
                    RequirePublicPath(NameOf(currentUser) + "\\" + name);
                    return CreateNoReparse(currentUser, name);
                }
            default:
                throw new UnauthorizedAccessException("Cannot create a key through the merged classes hive.");
        }
    }

    private static unsafe RegistryKey CreateNoReparse(SafeRegistryHandle? parent, string name)
    {
        fixed (char* text = name)
        {
            var byteLength = checked((ushort)(name.Length * sizeof(char)));
            var objectName = new UnicodeString { Length = byteLength, MaximumLength = byteLength, Buffer = (nint)text };
            var reference = false;
            try
            {
                parent?.DangerousAddRef(ref reference);
                var attributes = new ObjectAttributes
                {
                    Length = sizeof(ObjectAttributes), Root = parent?.DangerousGetHandle() ?? 0,
                    Name = (nint)(&objectName), Flags = CaseInsensitiveNoReparse,
                };
                // A new link inserted after the lookup is refused by the native create itself.
                var status = NtCreateKey(out var key, ReadKey | WriteKey | NativeView, in attributes, 0, 0, 0, out _);
                if (status == 0) return RegistryKey.FromHandle(key, RegistryView.Registry64);
                key?.Dispose();
                throw Error(status);
            }
            finally
            {
                if (reference) parent!.DangerousRelease();
            }
        }
    }

    private static unsafe string NameOf(SafeRegistryHandle handle)
    {
        Span<byte> buffer = stackalloc byte[8192];
        int status;
        int used;
        fixed (byte* p = buffer)
            status = NtQueryKey(handle, KeyNameInformation, (nint)p, buffer.Length, out used);
        if (status != 0) throw Error(status);
        var length = MemoryMarshal.Read<int>(buffer);
        if (used is < 4 or > 8192 || length <= 0 || (length & 1) != 0 || length > used - 4)
            throw new InvalidDataException("Registry key identity could not be verified.");
        return new string(MemoryMarshal.Cast<byte, char>(buffer.Slice(4, length)));
    }

    private static Win32Exception Error(int ntStatus) => new(RtlNtStatusToDosError(ntStatus));

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public nint Root, Name;
        public uint Flags;
        public nint Security, QualityOfService;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryKey(SafeRegistryHandle key, int infoClass, nint buffer, int length, out int used);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateKey(out SafeRegistryHandle key, int access, in ObjectAttributes attributes,
        int titleIndex, nint keyClass, int options, out int disposition);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtDeleteKey(SafeRegistryHandle key);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegOpenCurrentUser(int access, out SafeRegistryHandle key);
}

