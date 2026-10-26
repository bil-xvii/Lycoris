using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Monolith.Core;

/// <summary>Start configuration of a service, captured before a change so it can be restored.</summary>
public sealed record ServiceStartSnapshot(string Name, ServiceStartMode StartMode, bool DelayedAutoStart)
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
            return new ServiceStartSnapshot(name, (ServiceStartMode)start, delayed && start == 2);
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
        ServiceStartMode.Automatic when delayed => "Автоматически (отложенный запуск)",
        ServiceStartMode.Automatic => "Автоматически",
        ServiceStartMode.Manual => "Вручную",
        ServiceStartMode.Disabled => "Отключена",
        ServiceStartMode.Boot => "Загрузка",
        ServiceStartMode.System => "Система",
        _ => mode.ToString(),
    };

    /// <summary>Changes the start type through the Service Control Manager (the same API services.msc uses).</summary>
    public static void SetStartMode(string name, ServiceStartMode mode, bool delayedAutoStart = false, bool allowProtected = false)
    {
        if (!allowProtected && IsProtected(name))
            throw new InvalidOperationException($"Служба «{name}» критична для Windows — MONOLITH её не изменяет.");
        SafetyLock.ThrowIfLocked("изменение службы");
        Elevation.Demand("изменение служб");

        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var svc = OpenService(scm, name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Не удалось открыть службу {name}");
            try
            {
                if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, (uint)mode, SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Не удалось изменить тип запуска {name}");

                if (mode == ServiceStartMode.Automatic)
                {
                    var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayedAutoStart };
                    ChangeServiceConfig2(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info);
                }
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

    public static void Restore(ServiceStartSnapshot s) => SetStartMode(s.Name, s.StartMode, s.DelayedAutoStart, allowProtected: true);

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
            Log.Warn($"Не удалось остановить службу {name}: {ex.Message}");
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
            Log.Warn($"Не удалось запустить службу {name}: {ex.Message}");
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
