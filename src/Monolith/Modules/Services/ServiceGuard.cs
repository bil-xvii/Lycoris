using Monolith.Core;

namespace Monolith.Modules.Services;

/// <summary>
/// Defense in depth for the ВСЕ СЛУЖБЫ tab (docs/research/services.md §2): services MONOLITH never modifies,
/// on top of <see cref="ServiceControl.Protected"/>. Locked services are shown with a lock and no actions.
/// </summary>
public static class ServiceGuard
{
    private const string Core = "Ядро Windows (RPC/COM): от неё зависит вся система.";
    private const string Session = "Вход в систему, профили, UAC и учётные записи.";
    private const string Platform = "Платформа Windows: WMI, журналы, планировщик, устройства, питание.";
    private const string Shell = "Оболочка, Microsoft Store, ввод текста, буфер обмена и уведомления.";
    private const string Network = "Сеть, VPN или брандмауэр: без неё пропадёт подключение или защита.";
    private const string Security = "Безопасность Windows: отключение ослабляет защиту.";
    private const string Servicing = "Обновления, активация или восстановление системы (на них опирается и MONOLITH).";
    private const string Media = "Звук, камеры или устройства ввода.";
    private const string Virtualization = "Виртуализация и VBS: WSL2, песочница, защита памяти.";
    private const string Diagnostics = "Диагностика, нужная самой Windows (средства устранения неполадок).";
    private const string ThirdParty = "Античит, игровой лаунчер или служба драйвера: игры и функции драйвера перестанут работать.";
    private const string Updater = "Обновление браузера и WebView2: без него остаются незакрытые уязвимости.";
    private const string Debated = "Спорная настройка: отключение чаще вредит, чем помогает.";
    private const string Identity = "Смарт-карты, токены ЭЦП (Рутокен, JaCarta) и Windows Hello.";
    private const string NetAuth = "Вход в корпоративные сети (802.1X, WPA2/3-Enterprise, eduroam) и DirectAccess: без неё пропадёт подключение.";
    private const string Radio = "Управление радиомодулями: без неё не работают режим «в самолёте» и переключатели Wi-Fi/Bluetooth.";
    private const string Time = "Синхронизация времени и часового пояса: неверное время ломает вход, сертификаты HTTPS и обновления.";

    /// <summary>Research §2A HARD-BLOCK list plus "never / do not offer" services from §1.</summary>
    private static readonly Dictionary<string, string> HardBlock = Build(
        (Core, ["RpcSs", "DcomLaunch", "RpcEptMapper", "EventSystem", "SENS"]),
        (Session, ["LSM", "ProfSvc", "UserManager", "SamSs", "KeyIso", "VaultSvc", "Appinfo", "gpsvc", "TokenBroker",
                   "seclogon", "wlidsvc", "CredentialEnrollmentManagerUserSvc"]),
        (Platform, ["Winmgmt", "EventLog", "Schedule", "PlugPlay", "Power", "DeviceInstall", "DsmSvc", "BrokerInfrastructure",
                    "SystemEventsBroker", "TimeBrokerSvc", "CoreMessagingRegistrar", "StateRepository"]),
        (Shell, ["AppXSvc", "ClipSVC", "LicenseManager", "InstallService", "AppReadiness", "camsvc", "DispBrokerDesktopSvc",
                 "Themes", "FontCache", "TextInputManagementService", "TabletInputService", "ShellHWDetection", "WpnService",
                 "WpnUserService", "cbdhsvc"]),
        (Network, ["Dhcp", "Dnscache", "nsi", "NlaSvc", "netprofm", "Wcmsvc", "WlanSvc", "LanmanWorkstation",
                   "WinHttpAutoProxySvc", "Netman", "NcbService", "RasMan", "IKEEXT", "BFE", "mpssvc", "DusmSvc", "iphlpsvc"]),
        (Security, ["WinDefend", "WdNisSvc", "MDCoreSvc", "Sense", "SecurityHealthService", "wscsvc", "webthreatdefsvc",
                    "webthreatdefusersvc", "AppIDSvc", "CryptSvc", "EFS", "BDESVC", "SgrmBroker"]),
        (Servicing, ["wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS", "DoSvc", "TrustedInstaller", "msiserver", "sppsvc", "VSS",
                     "swprv", "InventorySvc"]),
        (Media, ["Audiosrv", "AudioEndpointBuilder", "FrameServer", "FrameServerMonitor", "hidserv", "GameInputSvc"]),
        (Virtualization, ["HvHost", "vmcompute", "hns"]),
        (Diagnostics, ["DPS", "WdiServiceHost", "WdiSystemHost", "whesvc", "WSAIFabricSvc"]),
        (ThirdParty, ["vgc", "vgk", "BEService", "Steam Client Service", "GamingServices", "GamingServicesNet",
                      "NVDisplay.ContainerLocalSystem", "AMD External Events Utility", "amd3dvcacheSvc", "AMD Crash Defender Service"]),
        (Updater, ["edgeupdate", "edgeupdatem", "gupdate", "gupdatem"]),
        (Debated, ["SysMain", "SharedAccess"]),
        (Identity, ["SCardSvr", "ScDeviceEnum", "SCPolicySvc", "CertPropSvc", "WbioSrvc"]),
        (NetAuth, ["Eaphost", "dot3svc", "NcaSvc"]),
        (Radio, ["RmSvc"]),
        (Time, ["W32Time", "tzautoupdate"]));

    private static readonly (string Prefix, string Reason)[] HardBlockPrefixes =
    [
        ("EasyAntiCheat", ThirdParty),
        ("FACEIT", ThirdParty),
        ("PnkBstr", ThirdParty),
        ("BattlEye", ThirdParty),
        ("GoogleUpdaterService", Updater),
        ("GoogleUpdaterInternalService", Updater),
    ];

    /// <summary>Services that must stay disabled: MONOLITH never enables or starts them.</summary>
    private static readonly HashSet<string> NeverEnable = new(StringComparer.OrdinalIgnoreCase) { "RemoteRegistry" };

    private static Dictionary<string, string> Build(params (string Reason, string[] Names)[] groups)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (reason, names) in groups)
            foreach (var n in names)
                d.TryAdd(n, reason);
        return d;
    }

    /// <summary>True when the service is on the hard-block list (by name or prefix).</summary>
    public static bool IsHardBlocked(string name) => HardBlockReason(name) is not null;

    public static string? HardBlockReason(string name)
    {
        if (HardBlock.TryGetValue(name, out var reason)) return reason;
        var baseName = ServiceControl.BaseName(name);
        if (!baseName.Equals(name, StringComparison.OrdinalIgnoreCase) && HardBlock.TryGetValue(baseName, out reason)) return reason;
        foreach (var (prefix, r) in HardBlockPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return r;
        return null;
    }

    public static bool MustStayDisabled(string name) => NeverEnable.Contains(name);

    /// <summary>True for per-user service templates/instances (<c>SERVICE_USER_SERVICE</c>) or a "_LUID" suffix.</summary>
    public static bool IsPerUser(string name, int serviceType) =>
        (serviceType & 0x40) != 0 || !ServiceControl.BaseName(name).Equals(name, StringComparison.Ordinal);

    /// <summary>
    /// Returns why MONOLITH refuses to touch this service, or null when changes are allowed.
    /// Order matters: the most specific, most useful explanation wins.
    /// </summary>
    public static string? GetLockReason(string name, int serviceType, int start, int errorControl, int launchProtected, string imagePath)
    {
        if (ServiceControl.IsProtected(name))
            return "Критическая служба Windows: её изменение может оставить систему без сети, обновлений, звука или загрузки.";
        if (IsPerUser(name, serviceType))
        {
            var template = ServiceControl.BaseName(name);
            return $"Пользовательская служба (шаблон «{template}»). Windows создаёт её копию при каждом входе; изменение шаблона " +
                   "действует на всех пользователей, а отключение ломает буфер обмена Win+V, уведомления и «Связь с телефоном». " +
                   "MONOLITH такие службы не меняет.";
        }
        if (HardBlockReason(name) is { } reason)
            return reason;
        if ((serviceType & 0x0B) != 0 || start is 0 or 1)
            return "Драйвер или служба ранней загрузки: ошибка в настройке может помешать Windows загрузиться.";
        if (launchProtected != 0)
            return "Защищённая служба (PPL): Windows запрещает менять её настройки даже администратору.";
        if (errorControl == 3)
            return "Служба помечена как критичная для загрузки (ErrorControl = 3).";
        if (IsDriverStorePath(imagePath))
            return "Служба из пакета драйвера производителя: без неё перестанут работать функции драйвера.";
        return null;
    }

    /// <summary>
    /// Research §0.4/§2B "not in the curated allowlist": outside expert mode (Settings → ЭКСПЕРТНЫЙ РЕЖИМ СЛУЖБ)
    /// every non-curated service — other Microsoft services and all third-party ones — is read-only.
    /// Returns null when the service is curated or expert mode is on.
    /// </summary>
    public static string? NotCuratedLockReason(string name, bool isThirdParty, bool expertMode)
    {
        if (expertMode || ServiceCatalog.IsCurated(name)) return null;
        var what = isThirdParty
            ? "Служба стороннего производителя: MONOLITH не знает, какие функции её программы от неё зависят."
            : "Этой службы нет в проверенном списке MONOLITH: последствия её изменения не изучены.";
        return what + " Здесь она только для просмотра. Менять такие службы на свой риск можно после включения " +
               "«Экспертного режима служб» в разделе «Настройки».";
    }

    public static bool IsDriverStorePath(string? imagePath) =>
        !string.IsNullOrEmpty(imagePath) && imagePath.Contains(@"\DriverStore\FileRepository\", StringComparison.OrdinalIgnoreCase);
}
