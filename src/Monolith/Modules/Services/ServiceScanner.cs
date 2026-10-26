using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Modules.Services;

/// <summary>Everything the ВСЕ СЛУЖБЫ tab knows about one service (read once per scan).</summary>
public sealed record ServiceInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public string PathName { get; init; } = "";
    public string StartName { get; init; } = "";
    /// <summary>The binary that does the work (ServiceDll for svchost-hosted services).</summary>
    public string Binary { get; init; } = "";
    public string Company { get; init; } = "";
    public bool IsThirdParty { get; init; }
    public int ServiceType { get; init; }
    public int ErrorControl { get; init; }
    public int LaunchProtected { get; init; }
    public bool HasTriggers { get; init; }
    public bool IsPerUser { get; init; }
    public ServiceStartMode? StartMode { get; init; }
    public bool Delayed { get; init; }
    public ServiceControllerStatus? Status { get; init; }
    /// <summary>Why MONOLITH won't touch it; null = changes allowed.</summary>
    public string? LockReason { get; init; }
}

/// <summary>Live start configuration and state of one service.</summary>
public sealed record ServiceLiveState(ServiceStartMode? StartMode, bool Delayed, bool HasTriggers, ServiceControllerStatus? Status, string? DisplayName)
{
    public bool Exists => StartMode is not null;
}

/// <summary>Facts about this PC that change what the module may offer.</summary>
public sealed record ServiceEnvironment(bool IsDomainJoined, bool IsMdmEnrolled, bool IsInsider, bool HasBluetoothAdapter)
{
    public bool IsManaged => IsDomainJoined || IsMdmEnrolled;
}

public sealed record DependentInfo(string Name, string DisplayName, bool IsRunning);

/// <summary>Read-only access to services: WMI, registry and the SCM. Never changes anything.</summary>
public static class ServiceScanner
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    // ───────────────────────────── Environment ─────────────────────────────

    public static ServiceEnvironment ReadEnvironment()
    {
        return new ServiceEnvironment(ReadDomainJoined(), ReadMdmEnrolled(), ReadInsider(), ReadBluetoothAdapter());
    }

    private static bool ReadDomainJoined()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT PartOfDomain FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in searcher.Get())
                using (mo)
                    return mo["PartOfDomain"] is true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Службы: не удалось определить членство в домене — {ex.Message}");
        }
        return false;
    }

    private static bool ReadMdmEnrolled()
    {
        try
        {
            using var hklm = RegistryHelper.OpenBase(RegHive.HKLM);
            using var root = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments", false);
            if (root is null) return false;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub, false);
                if (k is null) continue;
                if (ManagedDetection.IsMdmEnrollment(AsInt(k.GetValue("EnrollmentState")), AsInt(k.GetValue("EnrollmentType")),
                        k.GetValue("ProviderID") as string))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Службы: не удалось проверить регистрацию MDM — {ex.Message}");
        }
        return false;
    }

    private static bool ReadInsider() =>
        !string.IsNullOrWhiteSpace(RegistryHelper.ReadString(RegHive.HKLM, @"SOFTWARE\Microsoft\WindowsSelfHost\Applicability", "BranchName"));

    /// <summary>Any present device of the Bluetooth class. On failure assume "yes" — the safe answer.</summary>
    private static bool ReadBluetoothAdapter()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'");
            using var results = searcher.Get();
            return results.Count > 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Службы: не удалось проверить наличие Bluetooth — {ex.Message}");
            return true;
        }
    }

    // ───────────────────────────── One service ─────────────────────────────

    /// <summary>Current start type (registry, like services.msc), trigger flag and state of one service.</summary>
    public static ServiceLiveState ReadLiveState(string name)
    {
        var snapshot = ServiceControl.Query(name);
        if (snapshot is null) return new ServiceLiveState(null, false, false, null, null);
        var triggers = RegistryHelper.KeyExists(RegHive.HKLM, $@"{ServicesKey}\{name}\TriggerInfo");
        ServiceControllerStatus? status = null;
        string? display = null;
        try
        {
            using var sc = new ServiceController(name);
            status = sc.Status;
            display = sc.DisplayName;
        }
        catch
        {
            // Some services deny status queries without admin rights.
        }
        return new ServiceLiveState(snapshot.StartMode, snapshot.DelayedAutoStart, triggers, status, display);
    }

    /// <summary>Services that depend on this one (read-only SCM query).</summary>
    public static IReadOnlyList<DependentInfo> GetDependents(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.DependentServices
                .Select(d =>
                {
                    using (d)
                    {
                        ServiceControllerStatus? st = null;
                        try { st = d.Status; } catch { }
                        return new DependentInfo(d.ServiceName, d.DisplayName, ServiceText.IsRunning(st));
                    }
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    // ───────────────────────────── Full scan ─────────────────────────────

    /// <summary>
    /// Every Win32 service (Win32_Service) enriched with registry facts, publisher and lock reason.
    /// When <paramref name="probeChangeAccess"/> is set (admin only) services the SCM refuses to reconfigure are locked too.
    /// </summary>
    public static List<ServiceInfo> ScanAll(bool probeChangeAccess)
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var companies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ServiceInfo>();

        using var hklm = RegistryHelper.OpenBase(RegHive.HKLM);
        using var servicesKey = hklm.OpenSubKey(ServicesKey, false);
        var scm = probeChangeAccess ? OpenSCManager(null, null, SC_MANAGER_CONNECT) : IntPtr.Zero;
        try
        {
            foreach (var mo in QueryWin32Services())
            {
                using (mo)
                {
                    var name = mo["Name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    try
                    {
                        list.Add(BuildInfo(mo, name, servicesKey, scm, windowsDir, companies));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Службы: пропущена {name} — {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
        return list;
    }

    private static List<ManagementObject> QueryWin32Services()
    {
        const string full = "SELECT Name, DisplayName, State, StartMode, DelayedAutoStart, Description, PathName, StartName FROM Win32_Service";
        const string basic = "SELECT Name, DisplayName, State, StartMode, Description, PathName, StartName FROM Win32_Service";
        try
        {
            return Run(full);
        }
        catch (ManagementException)
        {
            // Older Windows 10 builds have no DelayedAutoStart property; the registry has it anyway.
            return Run(basic);
        }

        static List<ManagementObject> Run(string wql)
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery(wql));
            return searcher.Get().Cast<ManagementObject>().ToList();
        }
    }

    private static ServiceInfo BuildInfo(ManagementObject mo, string name, RegistryKey? servicesKey, IntPtr scm,
        string windowsDir, Dictionary<string, string> companies)
    {
        var pathName = mo["PathName"]?.ToString() ?? "";
        int type = 0, errorControl = 1, launchProtected = 0;
        int? start = null;
        var delayed = false;
        var triggers = false;
        string imagePath = pathName;
        string? serviceDll = null;

        var registryReadable = true;
        try
        {
            using var k = servicesKey?.OpenSubKey(name, false);
            if (k is not null)
            {
                type = AsInt(k.GetValue("Type")) ?? 0;
                start = AsInt(k.GetValue("Start"));
                delayed = AsInt(k.GetValue("DelayedAutostart")) == 1;
                errorControl = AsInt(k.GetValue("ErrorControl")) ?? 1;
                launchProtected = AsInt(k.GetValue("LaunchProtected")) ?? 0;
                if (k.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string ip && ip.Length > 0)
                    imagePath = ip;
                using (var trig = k.OpenSubKey("TriggerInfo", false)) triggers = trig is not null;
                using (var par = k.OpenSubKey("Parameters", false))
                    serviceDll = par?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A few service keys (lmhosts, nsi…) deny reads without admin rights. WMI still gives the basics.
            registryReadable = false;
        }

        // Registry (as services.msc) first; WMI's string as a fallback (e.g. for keys we may not read).
        var startMode = start is >= 0 and <= 4 ? (ServiceStartMode)start.Value : ServiceText.ParseWmiStartMode(mo["StartMode"]?.ToString());
        if (start is null && SafeBool(mo, "DelayedAutoStart")) delayed = true;
        if (startMode != ServiceStartMode.Automatic) delayed = false;

        var exe = ServicePathParser.ExtractExecutable(pathName.Length > 0 ? pathName : imagePath, windowsDir);
        var binary = exe;
        if (ServicePathParser.IsSvchost(exe) && !string.IsNullOrWhiteSpace(serviceDll))
            binary = ServicePathParser.ExtractExecutable(serviceDll, windowsDir);
        var company = CompanyOf(binary, companies);

        var lockReason = ServiceGuard.GetLockReason(name, type, start ?? 3, errorControl, launchProtected, imagePath);
        if (lockReason is null && !registryReadable)
            lockReason = "Windows не даёт прочитать настройки этой службы, поэтому MONOLITH не может проверить её безопасность и не меняет её.";
        if (lockReason is null && scm != IntPtr.Zero && !CanChangeConfig(scm, name))
            lockReason = "Windows запрещает менять настройки этой службы даже администратору (права доступа службы).";

        return new ServiceInfo
        {
            Name = name,
            DisplayName = NonEmpty(mo["DisplayName"]?.ToString(), name),
            Description = mo["Description"]?.ToString()?.Trim() ?? "",
            PathName = pathName,
            StartName = mo["StartName"]?.ToString() ?? "",
            Binary = binary,
            Company = company,
            IsThirdParty = ServiceClassifier.IsThirdParty(company, binary, windowsDir),
            ServiceType = type,
            ErrorControl = errorControl,
            LaunchProtected = launchProtected,
            HasTriggers = triggers,
            IsPerUser = ServiceGuard.IsPerUser(name, type),
            StartMode = startMode,
            Delayed = delayed,
            Status = ServiceText.ParseWmiState(mo["State"]?.ToString()),
            LockReason = lockReason,
        };
    }

    /// <summary>Fresh start type and state for many services at once (cheap: one SCM enumeration + registry).</summary>
    public static Dictionary<string, ServiceLiveState> ReadLiveStates(IEnumerable<string> names)
    {
        var statuses = new Dictionary<string, (ServiceControllerStatus Status, string Display)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var sc in ServiceController.GetServices())
            {
                using (sc)
                {
                    try { statuses[sc.ServiceName] = (sc.Status, sc.DisplayName); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Службы: не удалось получить состояние служб — {ex.Message}");
        }

        var result = new Dictionary<string, ServiceLiveState>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var snapshot = ServiceControl.Query(name);
            var triggers = snapshot is not null && RegistryHelper.KeyExists(RegHive.HKLM, $@"{ServicesKey}\{name}\TriggerInfo");
            statuses.TryGetValue(name, out var st);
            result[name] = new ServiceLiveState(snapshot?.StartMode, snapshot?.DelayedAutoStart ?? false, triggers,
                statuses.ContainsKey(name) ? st.Status : null, statuses.ContainsKey(name) ? st.Display : null);
        }
        return result;
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private static string CompanyOf(string file, Dictionary<string, string> cache)
    {
        if (string.IsNullOrWhiteSpace(file)) return "";
        if (cache.TryGetValue(file, out var c)) return c;
        try
        {
            c = File.Exists(file) ? FileVersionInfo.GetVersionInfo(file).CompanyName?.Trim() ?? "" : "";
        }
        catch
        {
            c = "";
        }
        cache[file] = c;
        return c;
    }

    private static bool CanChangeConfig(IntPtr scm, string name)
    {
        // Opening a handle with SERVICE_CHANGE_CONFIG access changes nothing; it only asks the SCM whether we could.
        var h = OpenService(scm, name, SERVICE_CHANGE_CONFIG);
        if (h != IntPtr.Zero)
        {
            CloseServiceHandle(h);
            return true;
        }
        return Marshal.GetLastWin32Error() != ERROR_ACCESS_DENIED;
    }

    private static int? AsInt(object? v) => v switch
    {
        int i => i,
        long l => unchecked((int)l),
        string s when int.TryParse(s, out var x) => x,
        _ => null,
    };

    private static bool SafeBool(ManagementBaseObject mo, string property)
    {
        try { return mo[property] is true; }
        catch { return false; }
    }

    private static string NonEmpty(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const int ERROR_ACCESS_DENIED = 5;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr scm, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
