using System.ComponentModel;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Lycoris.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Lycoris.Modules.Services;

public static partial class ServiceScanner
{
    private const uint ScManagerEnumerateService = 4;
    private const uint ScmWin32AndDriver = 0x3b;
    private const uint ServiceStateAll = 3;
    private const uint ServiceQueryConfig = 1;
    private const int ErrorMoreData = 234;
    private const int ServiceEnumBufferSize = 256 * 1024;

    /// <summary>Keeps these SCM entries alive across final validation and execution, even when marked for deletion.</summary>
    internal static IDisposable? HoldActionServices(IEnumerable<string> names)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return null;
        var services = new HeldActionServices();
        var handedOff = false;
        try
        {
            foreach (var name in names)
            {
                var service = OpenService(scm, name, ServiceQueryConfig);
                if (service.IsInvalid)
                {
                    service.Dispose();
                    return null;
                }
                services.Handles.Add(service);
            }
            handedOff = true;
            return services;
        }
        finally
        {
            if (!handedOff) services.Dispose();
            CloseServiceHandle(scm);
        }
    }

    private sealed class HeldActionServices : IDisposable
    {
        public List<ServiceHandle> Handles { get; } = [];

        public void Dispose()
        {
            foreach (var handle in Handles) handle.Dispose();
            Handles.Clear();
        }
    }

    internal static IReadOnlyList<ServiceActionIdentity> ScanActionIdentities()
    {
        var scm = OpenSCManager(null, null, ScManagerEnumerateService);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var buffer = Marshal.AllocHGlobal(ServiceEnumBufferSize);
            try
            {
                var identities = new List<ServiceActionIdentity>();
                var resume = 0;
                var stride = Marshal.SizeOf<ScmEnumerationEntry>();
                // A failed or partial enumeration must never yield a resolvable target.
                for (var page = 0; page < 32; page++)
                {
                    var previousResume = resume;
                    var complete = EnumServicesStatusEx(scm, 0, ScmWin32AndDriver, ServiceStateAll, buffer,
                        ServiceEnumBufferSize, out _, out var count, ref resume, null);
                    var error = Marshal.GetLastPInvokeError();
                    if (!complete && (error != ErrorMoreData || resume == previousResume || count == 0))
                        throw new Win32Exception(error);
                    if (count < 0 || count > ServiceEnumBufferSize / stride)
                        throw new InvalidDataException();
                    for (var index = 0; index < count; index++)
                    {
                        var entry = Marshal.PtrToStructure<ScmEnumerationEntry>(buffer + index * stride);
                        var name = Marshal.PtrToStringUni(entry.Name);
                        var display = Marshal.PtrToStringUni(entry.DisplayName);
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(display))
                            throw new InvalidDataException();
                        identities.Add(new(name, display));
                    }
                    if (complete && identities.Count > 0) return identities;
                    if (complete) break;
                }
                throw new InvalidDataException();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    internal static ServiceActionFacts ReadActionFacts(ServiceActionIdentity identity, ServiceAction action, bool elevated,
        bool? managed = null)
    {
        var isManaged = managed ?? ReadActionManaged();
        var catalogGatePassed = (action is ServiceAction.Enable or ServiceAction.Start) || ReadActionCatalogGate(identity.Name);
        var facts = ReadActionConfiguration(identity);
        var name = facts.Identity.Name;
        var dependencies = action switch
        {
            ServiceAction.Start when facts.Status != ServiceControllerStatus.Running && facts.Start != ServiceStartMode.Disabled =>
                ReadActionPrerequisites(name),
            ServiceAction.Stop when facts.Status != ServiceControllerStatus.Stopped => ReadActionDependents(name, activeOnly: true),
            ServiceAction.Disable when facts.Start != ServiceStartMode.Disabled => ReadActionDependents(name, activeOnly: false),
            _ => ((int?)null, (string?)null),
        };
        var canChange = elevated && CanAccessAction(name, action);
        var activeRecordId = elevated ? ChangeJournal.Instance.FindActive("svc:" + name)?.Id : null;
        if (ReadActionConfiguration(identity) != facts) throw new InvalidDataException();
        return facts with
        {
            Managed = isManaged,
            CatalogGatePassed = catalogGatePassed,
            DependentCount = dependencies.Item1,
            DependencyState = dependencies.Item2,
            CanChange = canChange,
            ActiveRecordId = activeRecordId,
        };
    }

    internal static ServiceActionFacts ReadActionConfiguration(ServiceActionIdentity identity)
    {
        using var hklm = RegistryHelper.OpenBase(RegHive.HKLM);
        using var key = hklm.OpenSubKey($@"{ServicesKey}\{identity.Name}", false) ?? throw new InvalidDataException();
        var initialVersion = ReadActionKeyVersion(key);
        var type = ReadActionDword(key, "Type");
        var start = ReadActionDword(key, "Start");
        var errorControl = ReadActionDword(key, "ErrorControl");
        var launchProtected = ReadActionDword(key, "LaunchProtected", optional: true);
        var rawDelayed = ReadActionDword(key, "DelayedAutostart", optional: true);
        var path = ReadActionString(key, "ImagePath") ?? throw new InvalidDataException();
        var account = ReadActionString(key, "ObjectName") ?? throw new InvalidDataException();
        using var parameters = OpenActionSubKey(key, "Parameters");
        var parametersVersion = parameters is null ? null : (long?)ReadActionKeyVersion(parameters);
        var serviceDll = parameters is null ? null : ReadActionString(parameters, "ServiceDll");
        using var triggers = OpenActionSubKey(key, "TriggerInfo");
        var triggerVersion = triggers is null ? null : (long?)ReadActionKeyVersion(triggers);

        using var controller = new ServiceController { ServiceName = identity.Name };
        var status = controller.Status;
        if (start != (int)controller.StartType || type != (int)controller.ServiceType ||
            !string.Equals(identity.DisplayName, controller.DisplayName, StringComparison.Ordinal) ||
            start is < 0 or > 4 || errorControl is < 0 or > 3 || launchProtected is < 0 or > 3 || rawDelayed is < 0 or > 1 ||
            (parameters is not null && ReadActionKeyVersion(parameters) != parametersVersion) ||
            (triggers is not null && ReadActionKeyVersion(triggers) != triggerVersion) ||
            ReadActionKeyVersion(key) != initialVersion)
            throw new InvalidDataException();

        return new(identity, (ServiceStartMode)start!.Value, status, type!.Value, errorControl!.Value, launchProtected ?? 0,
            path, account, serviceDll, rawDelayed is null ? null : rawDelayed == 1, initialVersion, parametersVersion, triggerVersion,
            Managed: null, CatalogGatePassed: null, DependentCount: null, CanChange: null, ActiveRecordId: null);
    }

    private static int? ReadActionDword(RegistryKey key, string value, bool optional = false)
    {
        var raw = ReadActionValue(key, value);
        if (raw is int number) return number;
        if (optional && raw is null) return null;
        throw new InvalidDataException();
    }

    private static string? ReadActionString(RegistryKey key, string value)
    {
        var raw = ReadActionValue(key, value);
        if (raw is null) return null;
        return raw is string text && !string.IsNullOrWhiteSpace(text) && !text.Contains('\0')
            ? text : throw new InvalidDataException();
    }

    private static object? ReadActionValue(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null && key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException();
        return value;
    }

    private static RegistryKey? OpenActionSubKey(RegistryKey key, string name)
    {
        var subKey = key.OpenSubKey(name, false);
        if (subKey is null && key.GetSubKeyNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException();
        return subKey;
    }

    private static long ReadActionKeyVersion(RegistryKey key)
    {
        var error = RegQueryInfoKey(key.Handle, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, out var lastWrite);
        return error == 0 ? lastWrite : throw new Win32Exception(error);
    }

    internal static bool ReadActionManaged()
    {
        using var searcher = new ManagementObjectSearcher("SELECT PartOfDomain FROM Win32_ComputerSystem")
        {
            Options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5) },
        };
        using var results = searcher.Get();
        bool? domain = null;
        foreach (ManagementObject computer in results)
        {
            using (computer)
            {
                if (domain is not null || computer["PartOfDomain"] is not bool joined) throw new InvalidDataException();
                domain = joined;
            }
        }
        if (domain is null) throw new InvalidDataException();
        if (domain.Value) return true;
        // Ask Windows directly and require ERROR_SUCCESS, even when registry enrollment entries are absent.
        var error = IsDeviceRegisteredWithManagement(out var mdmEnrolled, 0, 0);
        if (error != 0) throw new Win32Exception(error);
        return mdmEnrolled;
    }

    private static bool ReadActionCatalogGate(string name)
    {
        switch (ServiceCatalog.Find(name)?.Gate)
        {
            case ServiceGate.NotInsider:
                using (var hklm = RegistryHelper.OpenBase(RegHive.HKLM))
                using (var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\Applicability", false))
                    return key is null || ReadActionString(key, "BranchName") is null;
            case ServiceGate.NoBluetoothAdapter:
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'")
                {
                    Options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5) },
                })
                using (var results = searcher.Get())
                    return results.Count == 0;
            default:
                return true;
        }
    }

    private static (int?, string?) ReadActionDependents(string name, bool activeOnly)
    {
        try
        {
            using var controller = new ServiceController { ServiceName = name };
            // The .NET implementation calls EnumDependentServices(SERVICE_STATE_ALL), and
            // throws on Win32 failures; GetDependents is deliberately best-effort for display.
            var dependents = controller.DependentServices;
            return ReadActionDependencyState(dependents, status => !activeOnly || status != ServiceControllerStatus.Stopped);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return (null, null);
        }
    }

    private static (int?, string?) ReadActionPrerequisites(string name)
    {
        using var hklm = RegistryHelper.OpenBase(RegHive.HKLM);
        using var key = hklm.OpenSubKey($@"{ServicesKey}\{name}", false) ?? throw new InvalidDataException();
        var groups = ReadActionValue(key, "DependOnGroup");
        if (groups is string[] { Length: > 0 }) return (1, null);
        if (groups is not null and not string[]) return (null, null);
        var configured = ReadActionValue(key, "DependOnService");
        if (configured is not null and not string[]) return (null, null);

        using var controller = new ServiceController { ServiceName = name };
        var dependencies = controller.ServicesDependedOn;
        try
        {
            var expected = configured as string[] ?? [];
            if (expected.Any(dependency => dependency.StartsWith('+')) ||
                !expected.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(dependencies.Select(d => d.ServiceName)))
                return (null, null);
            var snapshot = expected.Select(dependency => new ServiceController { ServiceName = dependency }).ToArray();
            return ReadActionDependencyState(snapshot, status => status != ServiceControllerStatus.Running);
        }
        finally
        {
            foreach (var dependency in dependencies) dependency.Dispose();
        }
    }

    private static (int?, string?) ReadActionDependencyState(ServiceController[] controllers,
        Func<ServiceControllerStatus, bool> blocksAction)
    {
        try
        {
            var facts = controllers.Select(controller => (controller.ServiceName, controller.Status))
                .OrderBy(fact => fact.ServiceName, StringComparer.Ordinal).ToArray();
            if (facts.Any(fact => !Enum.IsDefined(fact.Status))) return (null, null);
            return (facts.Count(fact => blocksAction(fact.Status)),
                string.Join("\0", facts.Select(fact => $"{fact.ServiceName}\0{(int)fact.Status}")));
        }
        finally
        {
            foreach (var controller in controllers) controller.Dispose();
        }
    }

    private static bool CanAccessAction(string name, ServiceAction action)
    {
        const uint serviceStart = 0x0010;
        const uint serviceStop = 0x0020;
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return false;
        try
        {
            if (action is not (ServiceAction.Start or ServiceAction.Stop)) return CanChangeConfig(scm, name);
            using var service = OpenService(scm, name, action == ServiceAction.Start ? serviceStart : serviceStop);
            if (service.IsInvalid) return false;
            if (action == ServiceAction.Start) return true;
            using var controller = new ServiceController { ServiceName = name };
            return controller.CanStop;
        }
        finally { CloseServiceHandle(scm); }
    }

    private sealed class ServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScmEnumerationEntry
    {
        internal IntPtr Name;
        internal IntPtr DisplayName;
        internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
        IntPtr buffer, int bufferSize, out int needed, out int returned, ref int resume, string? groupName);

    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW", ExactSpelling = true)]
    private static extern int RegQueryInfoKey(SafeRegistryHandle key, IntPtr keyClass, IntPtr classLength,
        IntPtr reserved, IntPtr subKeys, IntPtr maxSubKeyLength, IntPtr maxClassLength, IntPtr values,
        IntPtr maxValueNameLength, IntPtr maxValueLength, IntPtr securityDescriptor, out long lastWrite);

    [DllImport("mdmregistration.dll", ExactSpelling = true)]
    private static extern int IsDeviceRegisteredWithManagement([MarshalAs(UnmanagedType.Bool)] out bool registered,
        uint upnLength, IntPtr upn);
}
