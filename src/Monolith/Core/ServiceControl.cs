using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Monolith.Core;

/// <summary>
/// Start configuration of a service, captured before a change so it can be restored.
/// <see cref="DelayedAutoStart"/> is the effective flag (only meaningful for Automatic); <see cref="RawDelayed"/> is the
/// stored DelayedAutostart flag whatever the start type (Windows keeps it for Manual/Disabled services too, and uses it
/// again when something switches the service back to Automatic). Null in records from older builds.
/// </summary>
public sealed record ServiceStartSnapshot(string Name, ServiceStartMode StartMode, bool DelayedAutoStart, bool? RawDelayed = null)
{
    public string Describe() => ServiceControl.DescribeStart(StartMode, DelayedAutoStart);
}

/// <summary>Reads and changes Windows service start types. Refuses to touch critical services.</summary>
public static class ServiceControl
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>
    /// Services MONOLITH will never modify: core OS, security, networking basics, updates, audio, licensing.
    /// Changing any of them can leave Windows unbootable, offline, unpatched or unprotected.
    /// </summary>
    public static readonly IReadOnlySet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // RPC / COM / core
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "Power", "PlugPlay", "ProfSvc", "UserManager",
        "CoreMessagingRegistrar", "StateRepository", "SystemEventsBroker", "BrokerInfrastructure", "TimeBrokerSvc",
        "Schedule", "Winmgmt", "EventLog", "EventSystem", "gpsvc", "SENS", "ShellHWDetection", "Themes",
        "FontCache", "Appinfo", "AppXSvc", "ClipSVC", "LicenseManager", "TokenBroker", "camsvc", "msiserver",
        "TrustedInstaller", "KeyIso", "VaultSvc", "Netlogon", "CryptSvc", "DsmSvc", "DeviceInstall", "DevicesFlowUserSvc",
        "TextInputManagementService", "WinHttpAutoProxySvc", "Wcmsvc", "WpnService", "WpnUserService", "UdkUserSvc",
        "cbdhsvc", "UnistoreSvc", "UserDataSvc", "OneSyncSvc", "AppReadiness", "InstallService", "VSS", "swprv",
        "sppsvc", "wlidsvc", "seclogon", "CertPropSvc", "StorSvc", "TieringEngineService", "defragsvc",
        // Security
        "WinDefend", "WdNisSvc", "Sense", "SecurityHealthService", "wscsvc", "mpssvc", "BFE", "SgrmBroker",
        "WdBoot", "WdFilter", "WdNisDrv", "EFS", "BDESVC", "Wecsvc",
        // Networking basics
        "Dhcp", "Dnscache", "nsi", "NlaSvc", "netprofm", "LanmanWorkstation", "WlanSvc", "Netman", "iphlpsvc",
        "NcbService", "WwanSvc", "Tcpip", "AFD", "IKEEXT", "PolicyAgent",
        // Updates
        "wuauserv", "BITS", "UsoSvc", "WaaSMedicSvc", "DoSvc",
        // Audio / input / display
        "AudioSrv", "AudioEndpointBuilder", "hidserv", "DispBrokerDesktopSvc", "GraphicsPerfSvc",
        // Storage / boot drivers
        "disk", "volmgr", "volsnap", "partmgr", "mountmgr", "storahci", "stornvme", "iaStorV", "Ntfs", "fvevol",
    };

    public static bool IsProtected(string serviceName)
    {
        var baseName = BaseName(serviceName);
        return Protected.Contains(serviceName) || Protected.Contains(baseName);
    }

    /// <summary>Per-user service instances look like "CDPUserSvc_4f1a2"; the template is "CDPUserSvc".</summary>
    public static string BaseName(string serviceName)
    {
        var i = serviceName.LastIndexOf('_');
        if (i > 0 && serviceName.Length - i - 1 is >= 4 and <= 8 && serviceName[(i + 1)..].All(Uri.IsHexDigit))
            return serviceName[..i];
        return serviceName;
    }

    public static bool Exists(string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{name}", false);
        return k?.GetValue("Start") is not null;
    }

    public static ServiceStartSnapshot? Query(string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{ServicesKey}\{name}", false);
            if (k?.GetValue("Start") is not int start) return null;
            var delayed = k.GetValue("DelayedAutostart") is int d && d == 1;
            // An absent flag and 0 mean the same to the Service Control Manager.
            return new ServiceStartSnapshot(name, (ServiceStartMode)start, delayed && start == 2, delayed);
        }
        catch
        {
            return null;
        }
    }

    public static ServiceControllerStatus? GetStatus(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return sc.Status;
        }
        catch
        {
            return null;
        }
    }

    public static string DescribeStart(ServiceStartMode mode, bool delayed) => mode switch
    {
        ServiceStartMode.Automatic when delayed => Loc.T("Автоматически (отложенный запуск)"),
        ServiceStartMode.Automatic => Loc.T("Автоматически"),
        ServiceStartMode.Manual => Loc.T("Вручную"),
        ServiceStartMode.Disabled => Loc.T("Отключена"),
        ServiceStartMode.Boot => Loc.T("Загрузка"),
        ServiceStartMode.System => Loc.T("Система"),
        _ => mode.ToString(),
    };

    /// <summary>Changes the start type through the Service Control Manager (the same API services.msc uses).</summary>
    public static void SetStartMode(string name, ServiceStartMode mode, bool delayedAutoStart = false, bool allowProtected = false)
    {
        if (!allowProtected && IsProtected(name))
            throw new InvalidOperationException(Loc.Tf("Служба «{0}» критична для Windows — MONOLITH её не изменяет.", name));
        SafetyLock.ThrowIfLocked(Loc.T("изменение службы"));
        Elevation.Demand(Loc.T("изменение служб"));

        var before = Query(name);
        WithService(name, svc =>
        {
            SetStartType(svc, name, mode);
            if (mode != ServiceStartMode.Automatic) return;
            if (SetDelayedFlag(svc, delayedAutoStart)) return;
            // The start type is already changed: put it back, so a failed change leaves nothing behind.
            var error = Marshal.GetLastWin32Error();
            if (before is not null && before.StartMode != mode)
            {
                try { SetStartType(svc, name, before.StartMode); }
                catch (Exception ex) { Log.Warn("Служба {0}: не удалось вернуть тип запуска после ошибки: {1}", name, ex.Message); }
            }
            throw new Win32Exception(error, Loc.Tf("Не удалось изменить отложенный запуск {0}", name));
        });
    }

    /// <summary>
    /// Puts a start configuration back exactly: the start type and the stored DelayedAutostart flag (for every start
    /// type, not only Automatic). Protected services are refused here too: MONOLITH never journals them.
    /// </summary>
    public static void Restore(ServiceStartSnapshot s)
    {
        if (s.RawDelayed is not { } raw)
        {
            SetStartMode(s.Name, s.StartMode, s.DelayedAutoStart); // record from an older build: the flag was not captured
            return;
        }
        if (IsProtected(s.Name))
            throw new InvalidOperationException(Loc.Tf("Служба «{0}» критична для Windows — MONOLITH её не изменяет.", s.Name));
        SafetyLock.ThrowIfLocked(Loc.T("изменение службы"));
        Elevation.Demand(Loc.T("изменение служб"));

        var current = Query(s.Name);
        WithService(s.Name, svc =>
        {
            if (s.StartMode == ServiceStartMode.Automatic)
            {
                SetStartType(svc, s.Name, s.StartMode);
                if (!SetDelayedFlag(svc, raw))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), Loc.Tf("Не удалось вернуть отложенный запуск {0}", s.Name));
                return;
            }
            // Not Automatic: set the flag first, while the service still has the start type MONOLITH gave it.
            if (current?.RawDelayed != raw && !SetDelayedFlag(svc, raw))
                Log.Warn("Служба {0}: не удалось вернуть флаг отложенного запуска ({1}). Windows учитывает его только для автоматического запуска.", s.Name, raw ? "1" : "0");
            SetStartType(svc, s.Name, s.StartMode);
        });
    }

    private static void WithService(string name, Action<IntPtr> action)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var svc = OpenService(scm, name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), Loc.Tf("Не удалось открыть службу {0}", name));
            try
            {
                action(svc);
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static void SetStartType(IntPtr svc, string name, ServiceStartMode mode)
    {
        if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, (uint)mode, SERVICE_NO_CHANGE,
                null, null, IntPtr.Zero, null, null, null, null))
            throw new Win32Exception(Marshal.GetLastWin32Error(), Loc.Tf("Не удалось изменить тип запуска {0}", name));
    }

    private static bool SetDelayedFlag(IntPtr svc, bool delayed)
    {
        var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayed };
        return ChangeServiceConfig2(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info);
    }

    public static async Task<bool> StopAsync(string name, TimeSpan timeout)
    {
        if (IsProtected(name) || SafetyLock.IsLocked) return false;
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) return true;
            if (!sc.CanStop) return false;
            sc.Stop();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось остановить службу {0}: {1}", name, ex.Message);
            return false;
        }
    }

    public static async Task<bool> StartAsync(string name, TimeSpan timeout)
    {
        if (SafetyLock.IsLocked) return false;
        try
        {
            using var sc = new ServiceController(name);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return true;
            sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, timeout)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось запустить службу {0}: {1}", name, ex.Message);
            return false;
        }
    }

    #region P/Invoke
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DELAYED_AUTO_START_INFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fDelayedAutostart;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref SERVICE_DELAYED_AUTO_START_INFO info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
    #endregion
}
