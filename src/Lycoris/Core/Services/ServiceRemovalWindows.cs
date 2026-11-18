using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Lycoris.Core.Security;
using Lycoris.Modules.Services;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Lycoris.Core.Services;

internal sealed class ServiceRemovalWindows : IServiceRemovalWindows
{
    private const uint Connect = 1, CreateAccess = 2;
    private const uint QueryAccess = 0x2000D, DeleteAccess = 0x10000, ChangeConfigAccess = 2;
    private const int OwnProcess = 0x10, Disabled = 4, Stopped = 1, DescriptionLevel = 1;
    private const int InsufficientBuffer = 122, ServiceDoesNotExist = 1060, ServiceMarkedForDelete = 1072;
    private const string DefaultDacl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    public bool CanDelete() => ServiceRemovalAuthorization.CanDelete(Elevation.IsElevated, SafetyLock.IsLocked,
        AppSettings.Current.ExpertMode, AppSettings.Current.ServicesExpertMode, CanProveUnmanaged());

    public bool CanRestore() => ServiceRemovalAuthorization.CanRestore(Elevation.IsElevated, SafetyLock.IsLocked);

    internal static void DemandDeletion()
    {
        DemandRestore();
        if (!ServiceRemovalAuthorization.CanDelete(Elevation.IsElevated, SafetyLock.IsLocked,
                AppSettings.Current.ExpertMode, AppSettings.Current.ServicesExpertMode, CanProveUnmanaged()))
            throw new UnauthorizedAccessException(Loc.T("Изменение службы недоступно."));
    }

    internal static void DemandRestore()
    {
        SafetyLock.ThrowIfLocked(Loc.T("изменение службы"));
        if (!ServiceRemovalAuthorization.CanRestore(Elevation.IsElevated, SafetyLock.IsLocked))
            throw new UnauthorizedAccessException(Loc.T("Изменение службы недоступно."));
    }

    internal static bool CanProveUnmanaged()
    {
        try { return ReadUnmanaged(); }
        catch (Exception ex) when (ServiceRemoval.IsExpectedFailure(ex)) { return false; }
    }

    private static bool ReadUnmanaged()
    {
        var joinResult = NetGetJoinInformation(null, out var name, out var status);
        if (name != 0) _ = NetApiBufferFree(name);
        if (joinResult != 0 || status is not (1 or 2)) return false;
        var aadResult = NetGetAadJoinInformation(null, out var aad);
        if (aad != 0) NetFreeAadJoinInformation(aad);
        return aadResult == 0 && aad == 0 && IsDeviceRegisteredWithManagement(out var registered, 0, 0) == 0 && registered == 0;
    }

    public IServiceRemovalHandle Open(string name, bool deleteAccess)
    {
        CheckName(name);
        if (deleteAccess) DemandDeletion();
        using var manager = OpenManager(Connect);
        if (!Elevation.IsElevated)
        {
            using var existence = OpenService(manager, name, QueryAccess);
            throw new ServiceRemovalAuditNeedsElevation();
        }
        var access = QueryAccess | ServiceRemovalAudit.Access | (deleteAccess ? DeleteAccess : 0);
        var service = ServiceRemovalAudit.Open(() => OpenService(manager, name, access));
        return new ServiceHandle(service, name, deleteAccess);
    }

    public RemovalPathState InspectPath(ServiceRemovalSnapshot snapshot) =>
        ServiceRemovalPath.Probe(snapshot.ImageCommand, snapshot.Executable);

    public RemovalServiceState InspectService(string name)
    {
        CheckName(name);
        using var manager = OpenManager(Connect);
        for (var attempt = 0; ; attempt++)
        {
            using var service = OpenServiceW(manager, name, QueryAccess);
            if (!service.IsInvalid) return RemovalServiceState.Present;
            var error = Marshal.GetLastPInvokeError();
            if (error == ServiceDoesNotExist)
            {
                using var parent = OpenServicesKey();
                using var key = ServiceRemovalRegistry.Open(parent, name, optional: true);
                return key is null ? RemovalServiceState.Absent : RemovalServiceState.Unknown;
            }
            if (error != ServiceMarkedForDelete) return RemovalServiceState.Unknown;
            if (attempt == 3) return RemovalServiceState.PendingDeletion;
            Thread.Sleep(30);
        }
    }

    public void Restore(ServiceRemovalSnapshot snapshot)
    {
        ScmHandle service;
        int createError;
        try
        {
            CheckName(snapshot.Name);
            DemandRestore();
            if (InspectService(snapshot.Name) != RemovalServiceState.Absent || InspectPath(snapshot) != RemovalPathState.Missing)
                throw new InvalidOperationException(Loc.T("Не удалось подтвердить, что имя службы свободно."));

            using var parent = OpenServicesKey(audit: true);
            if (!IsInheritedKeySecurity(new RawSecurityDescriptor(snapshot.RegistrySddl), parent)
                || !IsDefaultServiceSecurity(new RawSecurityDescriptor(snapshot.ServiceSddl))
                || !SafeMetadata(snapshot.DisplayName, 256) || !SafeMetadata(snapshot.Description, 1024, allowEmpty: true)
                || snapshot.ErrorControl is not (0 or 1))
                throw new InvalidOperationException(Loc.T("Конфигурацию службы нельзя восстановить без изменений."));
            DemandRestore();
            using var manager = OpenManager(Connect | CreateAccess);
            service = CreateServiceW(manager, snapshot.Name, snapshot.DisplayName, QueryAccess | ChangeConfigAccess | DeleteAccess,
                OwnProcess, Disabled, snapshot.ErrorControl, snapshot.ImageCommand, null, 0, null, "LocalSystem", null);
            createError = Marshal.GetLastPInvokeError();
        }
        catch (Exception ex) when (ServiceRemoval.IsExpectedFailure(ex))
        {
            throw new ServiceRestoreDidNotWrite(ex);
        }

        using var created = service;
        if (created.IsInvalid)
        {
            var failure = new Win32Exception(createError);
            if (createError is 5 or 6 or 87 or 1072 or 1073 or 1078) throw new ServiceRestoreDidNotWrite(failure);
            throw failure;
        }

        try
        {
            if (snapshot.Description.Length > 0)
            {
                DemandRestore();
                var description = new ServiceDescription { Text = snapshot.Description };
                if (!ChangeServiceConfig2W(service, DescriptionLevel, in description)) throw Error();
            }

            using var readback = Open(snapshot.Name, deleteAccess: false);
            if (readback.Inspect() != snapshot || InspectPath(snapshot) != RemovalPathState.Missing)
                throw new InvalidOperationException(Loc.T("Восстановленная служба отличается от резервной копии."));
        }
        catch (Exception ex) when (ServiceRemoval.IsExpectedFailure(ex))
        {
            // CreateService returns the handle for the entry created by this call, never an existing service.
            // Keep a partial restore disabled if policy changed or removal of the new entry fails.
            if (!CanRestore() || !DeleteService(service))
                throw new InvalidOperationException(Loc.T("Служба восстановлена не полностью и оставлена отключённой."), ex);
            throw;
        }
    }

    private sealed class ServiceHandle(ScmHandle handle, string name, bool deleteAccess) : IServiceRemovalHandle
    {
        public ServiceRemovalSnapshot? Inspect() => ServiceRemovalWindows.Inspect(handle, name);

        public int Delete()
        {
            if (!deleteAccess) throw new UnauthorizedAccessException();
            DemandDeletion();
            return DeleteService(handle) ? 0 : Marshal.GetLastPInvokeError();
        }

        public void Dispose() => handle.Dispose();
    }

    private static ServiceRemovalSnapshot? Inspect(ScmHandle handle, string name)
    {
        if (!QueryServiceStatus(handle, out var status)) throw Error();
        if (status.ServiceType != OwnProcess || status.CurrentState != Stopped) return null;

        // A successful SERVICE_STATE_ALL enumeration is required. ERROR_MORE_DATA or any other failure blocks.
        using var dependentBuffer = new NativeBuffer(256);
        if (!EnumDependentServicesW(handle, 3, dependentBuffer.Pointer, dependentBuffer.Length, out _, out var dependents)
            || dependents != 0) return null;

        using var configBuffer = Query((nint buffer, int size, out int needed) => QueryServiceConfigW(handle, buffer, size, out needed));
        var config = configBuffer.Read<QueryConfig>();
        if (config.Type != OwnProcess || config.Start != Disabled || config.ErrorControl is not (0 or 1)
            || config.Tag != 0 || configBuffer.Text(config.Group).Length > 0 || configBuffer.Text(config.Dependencies).Length > 0
            || configBuffer.Text(config.Account) != "LocalSystem") return null;

        var command = configBuffer.Text(config.ImagePath);
        var executable = ServiceRemovalPath.RecipeExecutable(command);
        var display = configBuffer.Text(config.DisplayName);
        if (executable is null || !SafeMetadata(display, 256) || !DefaultOptionalConfig(handle)) return null;

        using var description = QueryConfig2(handle, DescriptionLevel);
        var desc = description.Text(description.Read<nint>());
        if (!SafeMetadata(desc, 1024, allowEmpty: true)) return null;

        var sddl = ReadDefaultServiceSecurity(handle);
        if (sddl is null) return null;
        using var parent = OpenServicesKey(audit: true);
        using var key = ServiceRemovalRegistry.Open(parent, name, audit: true);
        if (key is null || parent.SubKeyCount > 8192 || !parent.GetSubKeyNames().Contains(name, StringComparer.Ordinal)
            || key.SubKeyCount != 0 || !RegistryValuesMatch(key, command, display, desc, config.ErrorControl)) return null;
        var descriptor = ServiceRemovalRegistry.RequireAcl(key, exclusive: false, audit: true);
        if (!IsInheritedKeySecurity(descriptor, parent)) return null;
        return new(name, display, command, executable, desc, config.ErrorControl, sddl, descriptor.GetSddlForm(AccessControlSections.All));
    }

    private static bool DefaultOptionalConfig(ScmHandle handle)
    {
        foreach (var (level, expected) in new[] { (3, 0), (4, 0), (5, 0), (7, 10000), (12, 0) })
        {
            using var buffer = QueryConfig2(handle, level);
            if (buffer.Read<int>() != expected) return false;
        }

        using var failureBuffer = QueryConfig2(handle, 2);
        var failure = failureBuffer.Read<ServiceFailureActions>();
        if (failure.Count != 0 || failure.ResetPeriod != 0 || failureBuffer.Text(failure.RebootMessage).Length != 0
            || failureBuffer.Text(failure.Command).Length != 0) return false;
        using var privileges = QueryConfig2(handle, 6);
        if (privileges.Text(privileges.Read<nint>()).Length != 0) return false;
        using var triggers = QueryConfig2(handle, 8);
        if (triggers.Read<int>() != 0) return false;
        using var preferred = QueryConfig2(handle, 9);
        return preferred.Read<ushort>() == ushort.MaxValue;
    }

    private static bool RegistryValuesMatch(RegistryKey key, string command, string display, string desc, int error)
    {
        if (key.ValueCount != (desc.Length == 0 ? 6 : 7)) return false;
        return IsValue(key, "Type", OwnProcess, RegistryValueKind.DWord)
            && IsValue(key, "Start", Disabled, RegistryValueKind.DWord)
            && IsValue(key, "ErrorControl", error, RegistryValueKind.DWord)
            && IsValue(key, "ImagePath", command, RegistryValueKind.ExpandString)
            && IsValue(key, "ObjectName", "LocalSystem", RegistryValueKind.String)
            && IsValue(key, "DisplayName", display, RegistryValueKind.String)
            && (desc.Length == 0 || IsValue(key, "Description", desc, RegistryValueKind.String));
    }

    private static bool IsValue(RegistryKey key, string name, object expected, RegistryValueKind kind) =>
        key.GetValueNames().Contains(name, StringComparer.Ordinal)
        && key.GetValueKind(name) == kind
        && Equals(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), expected);

    private static bool IsInheritedKeySecurity(RawSecurityDescriptor descriptor, RegistryKey parent)
    {
        var ancestor = ServiceRemovalRegistry.RequireAcl(parent, exclusive: false, audit: true);
        // Standard SCM-created keys inherit the Services ACL and have the Administrators owner and SYSTEM group.
        return descriptor.Owner?.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid) == true
            && descriptor.Group?.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) == true
            && descriptor.SystemAcl is null && ancestor.SystemAcl is null
            && !descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)
            && descriptor.DiscretionaryAcl is { } acl
            && acl.Cast<GenericAce>().All(a => a.AceFlags.HasFlag(AceFlags.Inherited))
            && descriptor.GetSddlForm(AccessControlSections.Access) == ancestor.GetSddlForm(AccessControlSections.Access);
    }

    private static string? ReadDefaultServiceSecurity(ScmHandle handle)
    {
        using var security = Query((nint buffer, int size, out int needed) =>
            QueryServiceObjectSecurity(handle, 15, buffer, size, out needed));
        var descriptor = new RawSecurityDescriptor(security.Bytes(), 0);
        return IsDefaultServiceSecurity(descriptor) ? descriptor.GetSddlForm(AccessControlSections.All) : null;
    }

    internal static bool IsDefaultServiceSecurity(RawSecurityDescriptor descriptor) =>
        descriptor.Owner?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true
        && descriptor.Group?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true
        && descriptor.GetSddlForm(AccessControlSections.Access) == DefaultDacl
        && descriptor.SystemAcl is null;

    private static RegistryKey OpenServicesKey(bool audit = false)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        ServiceRemovalRegistry.RequireAcl(root, exclusive: false);
        using var system = ServiceRemovalRegistry.Open(root, "SYSTEM")!;
        using var select = ServiceRemovalRegistry.Open(system, "Select")!;
        if (select.GetValue("Current") is not int current || current is <= 0 or > 999)
            throw new InvalidDataException("Cannot identify the active service registry key.");
        // CurrentControlSet is itself a Windows registry link. Open the concrete control set and reject every other link.
        using var control = ServiceRemovalRegistry.Open(system, FormattableString.Invariant($"ControlSet{current:000}"))!;
        return ServiceRemovalRegistry.Open(control, "Services", audit: audit)!;
    }

    private static bool SafeMetadata(string text, int max, bool allowEmpty = false) =>
        text.Length <= max && (allowEmpty || text.Length != 0) && !text.StartsWith('@')
        && !text.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);

    private static void CheckName(string name)
    {
        if (!ServiceRemoval.IsSupportedName(name) || ServiceControl.IsProtected(name) || ServiceGuard.IsHardBlocked(name)
            || ServiceGuard.IsPerUser(name, OwnProcess) || ServiceGuard.OptimizeTweakFor(name) is not null
            || ServiceGuard.MustStayDisabled(name)) throw new UnauthorizedAccessException();
    }

    private static ScmHandle OpenService(ScmHandle manager, string name, uint access)
    {
        var service = OpenServiceW(manager, name, access);
        if (!service.IsInvalid) return service;
        var error = Marshal.GetLastPInvokeError();
        service.Dispose();
        throw new Win32Exception(error);
    }

    private static ScmHandle OpenManager(uint access)
    {
        var manager = OpenSCManagerW(null, null, access);
        if (!manager.IsInvalid) return manager;
        var error = Marshal.GetLastPInvokeError();
        manager.Dispose();
        throw new Win32Exception(error);
    }

    private static NativeBuffer QueryConfig2(ScmHandle service, int level)
    {
        // MS-SCMR permits up to 8 KiB and rejects NULL output buffers. A successful call is required.
        var buffer = new NativeBuffer(8192);
        if (QueryServiceConfig2W(service, level, buffer.Pointer, buffer.Length, out _)) return buffer;
        var error = Marshal.GetLastPInvokeError();
        buffer.Dispose();
        throw new Win32Exception(error);
    }

    private delegate bool QueryBuffer(nint buffer, int size, out int needed);

    private static NativeBuffer Query(QueryBuffer query)
    {
        if (query(0, 0, out var needed) || Marshal.GetLastPInvokeError() != InsufficientBuffer || needed is <= 0 or > 8192)
            throw new InvalidDataException("Unverified SCM buffer size.");
        var buffer = new NativeBuffer(needed);
        if (query(buffer.Pointer, needed, out _)) return buffer;
        var error = Marshal.GetLastPInvokeError();
        buffer.Dispose();
        throw new Win32Exception(error);
    }

    private sealed class NativeBuffer : IDisposable
    {
        private readonly int _length;
        public nint Pointer { get; }
        public int Length => _length;

        public unsafe NativeBuffer(int length)
        {
            _length = length;
            Pointer = Marshal.AllocHGlobal(length);
            new Span<byte>((void*)Pointer, length).Clear();
        }

        public T Read<T>() where T : struct =>
            _length >= Marshal.SizeOf<T>() ? Marshal.PtrToStructure<T>(Pointer)
                : throw new InvalidDataException("Truncated SCM buffer.");

        public unsafe string Text(nint address)
        {
            if (address == 0) return "";
            var offset = address - Pointer;
            if (offset < 0 || offset > _length - 2 || (offset & 1) != 0)
                throw new InvalidDataException("Invalid SCM string pointer.");
            var chars = new ReadOnlySpan<char>((void*)address, (_length - (int)offset) / sizeof(char));
            var end = chars.IndexOf('\0');
            return end >= 0 ? new string(chars[..end]) : throw new InvalidDataException("Unterminated SCM string.");
        }

        public byte[] Bytes()
        {
            var result = new byte[_length];
            Marshal.Copy(Pointer, result, 0, _length);
            return result;
        }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    private static Win32Exception Error() => new(Marshal.GetLastPInvokeError());

    private sealed class ScmHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ScmHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryConfig
    {
        public int Type, Start, ErrorControl;
        public nint ImagePath, Group;
        public int Tag;
        public nint Dependencies, Account, DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType, CurrentState, AcceptedControls, Win32Exit, ServiceExit, Checkpoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public int ResetPeriod;
        public nint RebootMessage, Command;
        public int Count;
        public nint Actions;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceDescription
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Text;
    }

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ScmHandle OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ScmHandle OpenServiceW(ScmHandle manager, string service, uint access);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(ScmHandle service, out ServiceStatus status);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(ScmHandle service, nint buffer, int size, out int needed);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2W(ScmHandle service, int level, nint buffer, int size, out int needed);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceObjectSecurity(ScmHandle service, int information, nint buffer, int size, out int needed);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDependentServicesW(ScmHandle service, int state, nint buffer, int size, out int needed, out int returned);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(ScmHandle service);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ScmHandle CreateServiceW(ScmHandle manager, string name, string displayName, uint rights, int serviceType,
        int startType, int errorControl, string executable, string? group, nint tag, string? dependencies, string account, string? password);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(ScmHandle service, int level, in ServiceDescription description);

    [DllImport("netapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out nint name, out int status);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern int NetApiBufferFree(nint buffer);

    [DllImport("netapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int NetGetAadJoinInformation(string? tenant, out nint joinInfo);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern void NetFreeAadJoinInformation(nint joinInfo);

    [DllImport("mdmregistration.dll", ExactSpelling = true)]
    private static extern int IsDeviceRegisteredWithManagement(out int registered, uint upnLength, nint upn);
}

