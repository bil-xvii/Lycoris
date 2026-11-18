using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Lycoris.Core.Security;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Lycoris.Core.Services;

// The journal is untrusted. The key and authenticated payloads live in HKLM, outside the journal and
// its ACL-repair / migration machinery. Creation sets the ACL atomically and existing ACLs are never repaired.
internal sealed class ServiceRemovalRegistryStore : IServiceRemovalStore
{
    private const string StoreName = ServiceRemovalRegistryProtection.StoreName;
    private const string KeyName = "AuthenticationKey";

    public void Save(ServiceRemovalBinding binding, ServiceRemovalSnapshot snapshot)
    {
        ServiceRemovalWindows.DemandDeletion();
        using var machine = LocalMachine();
        using var software = ServiceRemovalRegistry.Open(machine, "SOFTWARE")!;
        var machineId = MachineId(software);
        using var store = OpenStore(software, writable: true, create: true)!;
        var valueName = RecordName(binding);
        if (ServiceRemovalRegistry.ReadBinary(store, valueName, ServiceRemovalEnvelope.MaxBytes) is not null)
            throw new InvalidDataException("Duplicate service recovery token.");
        var key = ReadKey(store);
        try
        {
            ServiceRemovalRegistry.WriteBinary(store, valueName, ServiceRemovalEnvelope.Seal(key, machineId, binding, snapshot));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public ServiceRemovalSnapshot? Read(ServiceRemovalBinding binding)
    {
        using var machine = LocalMachine();
        using var software = ServiceRemovalRegistry.Open(machine, "SOFTWARE")!;
        using var store = OpenStore(software, writable: false, create: false);
        if (store is null) return null;
        using var consumed = ServiceRemovalRegistry.Open(store, ConsumedName(binding), optional: true, exclusiveAcl: true);
        return consumed is null ? Read(software, store, binding) : null;
    }

    public bool Consume(ServiceRemovalBinding binding)
    {
        ServiceRemovalWindows.DemandRestore();
        using var machine = LocalMachine();
        using var software = ServiceRemovalRegistry.Open(machine, "SOFTWARE")!;
        using var store = OpenStore(software, writable: true, create: false);
        if (store is null || Read(software, store, binding) is null) return false;
        using var existing = ServiceRemovalRegistry.Open(store, ConsumedName(binding), optional: true, exclusiveAcl: true);
        if (existing is not null) return false;
        using var consumed = ServiceRemovalRegistry.CreateExclusive(store, ConsumedName(binding), out var created);
        if (!created) return false;
        ServiceRemovalRegistry.Flush(consumed);
        return true;
    }

    public void Release(ServiceRemovalBinding binding)
    {
        ServiceRemovalWindows.DemandRestore();
        using var machine = LocalMachine();
        using var software = ServiceRemovalRegistry.Open(machine, "SOFTWARE")!;
        using var store = OpenStore(software, writable: true, create: false);
        if (store is null || Read(software, store, binding) is null)
            throw new InvalidDataException("Unverified service recovery reservation.");
        var markerName = ConsumedName(binding);
        using (var marker = ServiceRemovalRegistry.Open(store, markerName, exclusiveAcl: true))
        {
            if (marker is null || marker.ValueCount != 0 || marker.SubKeyCount != 0)
                throw new InvalidDataException("Unexpected service recovery reservation.");
        }
        ServiceRemovalRegistry.DropEmptyMarker(store, markerName);
    }

    private static RegistryKey LocalMachine()
    {
        var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        try
        {
            ServiceRemovalRegistry.RequireAcl(machine, exclusive: false);
            return machine;
        }
        catch
        {
            machine.Dispose();
            throw;
        }
    }

    private static RegistryKey? OpenStore(RegistryKey software, bool writable, bool create)
    {
        var existing = ServiceRemovalRegistry.Open(software, StoreName, optional: true, writable: writable, exclusiveAcl: true);
        if (existing is not null || !create) return existing;

        var store = ServiceRemovalRegistry.CreateExclusive(software, StoreName, out var created);
        if (!created) return store; // Only the creator writes the key. A missing/invalid existing key is rejected.
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            ServiceRemovalRegistry.WriteBinary(store, KeyName, secret);
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static string RecordName(ServiceRemovalBinding binding) => "Backup-" + binding.Token.ToString("N");

    private static string ConsumedName(ServiceRemovalBinding binding) => "Consumed-" + binding.Token.ToString("N");

    private static byte[] ReadKey(RegistryKey store) =>
        ServiceRemovalRegistry.ReadBinary(store, KeyName, 32) is { Length: 32 } key
            ? key : throw new InvalidDataException("Invalid service recovery key.");

    private static Guid MachineId(RegistryKey software)
    {
        using var microsoft = ServiceRemovalRegistry.Open(software, "Microsoft")!;
        using var cryptography = ServiceRemovalRegistry.Open(microsoft, "Cryptography")!;
        if (cryptography.GetValue("MachineGuid", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value
            && Guid.TryParse(value, out var id) && id != Guid.Empty) return id;
        throw new InvalidDataException("Could not verify the machine for service recovery.");
    }

    private static ServiceRemovalSnapshot? Read(RegistryKey software, RegistryKey store, ServiceRemovalBinding binding)
    {
        var key = ReadKey(store);
        try
        {
            var sealedPayload = ServiceRemovalRegistry.ReadBinary(store, RecordName(binding), ServiceRemovalEnvelope.MaxBytes);
            return sealedPayload is null ? null : ServiceRemovalEnvelope.Open(key, MachineId(software), binding, sealedPayload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

internal static class ServiceRemovalEnvelope
{
    internal const int MaxBytes = 16384;
    private const int MacBytes = 32;
    private const int Version = 1;
    private sealed record Payload(int Version, Guid Machine, ServiceRemovalBinding Binding, ServiceRemovalSnapshot Snapshot);

    internal static byte[] Seal(byte[] key, Guid machine, ServiceRemovalBinding binding, ServiceRemovalSnapshot snapshot)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(Version, machine, binding, snapshot));
        if (payload.Length > MaxBytes - MacBytes) throw new InvalidDataException("Service recovery payload exceeds its size limit.");
        byte[] result = new byte[MacBytes + payload.Length];
        HMACSHA256.HashData(key, payload, result.AsSpan(0, MacBytes));
        payload.CopyTo(result, MacBytes);
        return result;
    }

    internal static ServiceRemovalSnapshot? Open(byte[] key, Guid machine, ServiceRemovalBinding binding, byte[] envelope)
    {
        if (envelope.Length is <= MacBytes or > MaxBytes) return null;
        var payload = envelope.AsSpan(MacBytes);
        Span<byte> mac = stackalloc byte[MacBytes];
        HMACSHA256.HashData(key, payload, mac);
        if (!CryptographicOperations.FixedTimeEquals(mac, envelope.AsSpan(0, MacBytes))) return null;
        try
        {
            var data = JsonSerializer.Deserialize<Payload>(payload);
            return data is { Version: Version } && data.Machine == machine && data.Binding == binding ? data.Snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class ServiceRemovalRegistry
{
    private const int RegistryView64 = 0x0100, OpenLink = 8, Read = 0x20019, Write = 0x20006;
    private const int ValueBinary = 3, FileNotFound = 2;
    private const int KeyWriteLike = 0x000D0026 | 0x50000000;
    private const string ProtectedSddl = "O:BAD:P(A;;KA;;;SY)(A;;KA;;;BA)";
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);

    internal static RegistryKey? Open(RegistryKey parent, string child, bool optional = false, bool writable = false,
        bool exclusiveAcl = false, bool audit = false)
    {
        var access = RegistryView64 | Read | (writable ? Write : 0) | (audit ? ServiceRemovalAudit.Access : 0);
        (int Status, SafeRegistryHandle Handle) OpenRelative()
        {
            var code = RegOpenKeyExW(parent.Handle, child, OpenLink, access, out var key);
            return (code, key);
        }

        var (status, handle) = audit ? ServiceRemovalAudit.Open(OpenRelative) : OpenRelative();
        if (optional && status == FileNotFound)
        {
            handle?.Dispose();
            return null;
        }
        if (status != 0)
        {
            handle?.Dispose();
            throw new Win32Exception(status);
        }
        return Adopt(handle, exclusiveAcl, audit);
    }

    internal static RegistryKey CreateExclusive(RegistryKey parent, string child, out bool created)
    {
        ServiceRemovalWindows.DemandRestore();
        var descriptor = new RawSecurityDescriptor(ProtectedSddl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        unsafe
        {
            fixed (byte* p = bytes)
            {
                var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = (nint)p };
                var status = RegCreateKeyExW(parent.Handle, child, 0, null, 0, RegistryView64 | Read | Write,
                    in security, out var handle, out var disposition);
                if (status != 0)
                {
                    handle?.Dispose();
                    throw new Win32Exception(status);
                }
                created = disposition == 1;
                if (created) return Adopt(handle, exclusive: true);

                // RegCreateKeyEx can follow a link when opening an already existing key. Never use that handle.
                handle.Dispose();
                return Open(parent, child, writable: true, exclusiveAcl: true)!;
            }
        }
    }

    private static RegistryKey Adopt(SafeRegistryHandle handle, bool exclusive, bool audit = false)
    {
        var key = RegistryKey.FromHandle(handle, RegistryView.Registry64);
        try
        {
            uint size = 0;
            var link = RegQueryValueExW(key.Handle, "SymbolicLinkValue", 0, out _, null, ref size);
            if (link != FileNotFound) throw new InvalidDataException("Registry link or unverified key.");
            RequireAcl(key, exclusive, audit);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    internal static RawSecurityDescriptor RequireAcl(RegistryKey key, bool exclusive, bool audit = false)
    {
        const int InsufficientBuffer = 122;
        int size = 0;
        var components = audit ? 15 : 7;
        if (RegGetKeySecurity(key.Handle, components, null, ref size) != InsufficientBuffer || size is <= 0 or > 16384)
            throw new InvalidDataException("Unverified registry security descriptor.");
        var bytes = new byte[size];
        var status = RegGetKeySecurity(key.Handle, components, bytes, ref size);
        if (status != 0) throw new Win32Exception(status);
        var descriptor = new RawSecurityDescriptor(bytes, 0);
        if (!TrustedAcl(descriptor, exclusive)) throw new UnauthorizedAccessException("Untrusted service registry key.");
        return descriptor;
    }

    internal static bool TrustedAcl(RawSecurityDescriptor descriptor, bool exclusive)
    {
        if (descriptor.DiscretionaryAcl is not { } acl || descriptor.Owner is not { } owner) return false;
        if (exclusive)
        {
            if ((!owner.Equals(Admins) && !owner.Equals(System)) || acl.Count != 2
                || !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)) return false;
            return acl.Cast<GenericAce>().Any(a => FullFor(a, Admins)) && acl.Cast<GenericAce>().Any(a => FullFor(a, System));
        }
        if (!PathTrust.IsTrustedSid(owner)) return false;
        foreach (GenericAce ace in acl)
        {
            if (ace is not CommonAce common || common.IsCallback
                || common.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied)) return false;
            if (common.AceQualifier == AceQualifier.AccessDenied || common.AceFlags.HasFlag(AceFlags.InheritOnly)) continue;
            // CREATOR OWNER is an inheritance template, applicable child owners are checked at each open.
            if (!PathTrust.IsTrustedSid(common.SecurityIdentifier)
                && !common.SecurityIdentifier.IsWellKnown(WellKnownSidType.CreatorOwnerSid)
                && (common.AccessMask & KeyWriteLike) != 0) return false;
        }
        return true;
    }

    private static bool FullFor(GenericAce ace, SecurityIdentifier sid) =>
        ace is CommonAce { AceQualifier: AceQualifier.AccessAllowed, IsCallback: false, AceFlags: AceFlags.None,
            AccessMask: 0x000F003F } allow && allow.SecurityIdentifier.Equals(sid);

    internal static byte[]? ReadBinary(RegistryKey key, string name, int maxBytes)
    {
        uint size = 0;
        var result = RegQueryValueExW(key.Handle, name, 0, out var kind, null, ref size);
        if (result == FileNotFound) return null;
        if (result != 0) throw new Win32Exception(result);
        if (kind != ValueBinary || size > maxBytes) throw new InvalidDataException("Invalid service recovery value.");
        var bytes = new byte[size];
        result = RegQueryValueExW(key.Handle, name, 0, out kind, bytes, ref size);
        if (result != 0) throw new Win32Exception(result);
        if (kind != ValueBinary || size != bytes.Length) throw new InvalidDataException("Service recovery value changed while reading.");
        return bytes;
    }

    internal static void WriteBinary(RegistryKey key, string name, byte[] bytes)
    {
        ServiceRemovalWindows.DemandRestore();
        var result = RegSetValueExW(key.Handle, name, 0, ValueBinary, bytes, bytes.Length);
        if (result != 0) throw new Win32Exception(result);
        Flush(key);
    }

    internal static void DropEmptyMarker(RegistryKey parent, string name)
    {
        ServiceRemovalWindows.DemandRestore();
        var status = RegDeleteKeyExW(parent.Handle, name, RegistryView64, 0);
        if (status != 0) throw new Win32Exception(status);
        Flush(parent);
    }

    internal static void Flush(RegistryKey key)
    {
        var status = RegFlushKey(key.Handle);
        if (status != 0) throw new Win32Exception(status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint Descriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(SafeRegistryHandle parent, string key, int options, int access, out SafeRegistryHandle result);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyExW(SafeRegistryHandle parent, string key, int reserved, string? keyClass, int options,
        int access, in SecurityAttributes security, out SafeRegistryHandle result, out int disposition);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string name, nint reserved, out int type, [Out] byte[]? data, ref uint size);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegGetKeySecurity(SafeRegistryHandle key, int information, [Out] byte[]? security, ref int size);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(SafeRegistryHandle key, string name, int reserved, int type, byte[] data, int size);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int RegDeleteKeyExW(SafeRegistryHandle parent, string name, int access, int reserved);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegFlushKey(SafeRegistryHandle key);
}

