using Lycoris.Core;

namespace Lycoris.Modules.Services;

/// <summary>
/// Defense in depth for the ВСЕ СЛУЖБЫ tab (docs/research/services.md §2): services Lycoris never modifies,
/// on top of <see cref="ServiceControl.Protected"/>. Locked services are shown with a lock and no actions.
/// </summary>
public static class ServiceGuard
{
    public static bool ExpertModeEnabled(bool expertMode, bool servicesExpertMode) => expertMode && servicesExpertMode;
    public static bool CanReapplyService(bool isCurated, bool expertMode, bool servicesExpertMode) =>
        isCurated || ExpertModeEnabled(expertMode, servicesExpertMode);

    private static readonly string Core = Loc.T("Ядро Windows (RPC/COM): от неё зависит вся система.");
    private static readonly string Session = Loc.T("Вход в систему, профили, UAC и учётные записи.");
    private static readonly string Platform = Loc.T("Платформа Windows: WMI, журналы, планировщик, устройства, питание.");
    private static readonly string Shell = Loc.T("Оболочка, Microsoft Store, ввод текста, буфер обмена и уведомления.");
    private static readonly string Network = Loc.T("Сеть, VPN или брандмауэр: без неё пропадёт подключение или защита.");
    private static readonly string Security = Loc.T("Безопасность Windows: отключение ослабляет защиту.");
    private static readonly string Servicing = Loc.T("Обновления, активация или восстановление системы (на них опирается и Lycoris).");
    private static readonly string Media = Loc.T("Звук, камеры или устройства ввода.");
    private static readonly string Virtualization = Loc.T("Виртуализация и VBS: WSL2, песочница, защита памяти.");
    private static readonly string Diagnostics = Loc.T("Диагностика, нужная самой Windows (средства устранения неполадок).");
    private static readonly string ThirdParty = Loc.T("Античит, игровой лаунчер или служба драйвера: игры и функции драйвера перестанут работать.");
    private static readonly string Updater = Loc.T("Обновление браузера и WebView2: без него остаются незакрытые уязвимости.");
    private static readonly string Debated = Loc.T("Спорная настройка: отключение чаще вредит, чем помогает.");
    private static readonly string Identity = Loc.T("Смарт-карты, токены ЭЦП (Рутокен, JaCarta) и Windows Hello.");
    private static readonly string NetAuth = Loc.T("Вход в корпоративные сети (802.1X, WPA2/3-Enterprise, eduroam) и DirectAccess: без неё пропадёт подключение.");
    private static readonly string Radio = Loc.T("Управление радиомодулями: без неё не работают режим «в самолёте» и переключатели Wi-Fi/Bluetooth.");
    private static readonly string Time = Loc.T("Синхронизация времени и часового пояса: неверное время ломает вход, сертификаты HTTPS и обновления.");

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

    /// <summary>Services that must stay disabled: Lycoris never enables or starts them.</summary>
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
    /// Returns why Lycoris refuses to touch this service, or null when changes are allowed.
    /// Order matters: the most specific, most useful explanation wins.
    /// </summary>
    public static string? GetLockReason(string name, int serviceType, int start, int errorControl, int launchProtected, string imagePath)
    {
        if (ServiceControl.IsProtected(name))
            return Loc.T("Критическая служба Windows: её изменение может оставить систему без сети, обновлений, звука или загрузки.");
        if (IsPerUser(name, serviceType))
        {
            var template = ServiceControl.BaseName(name);
            return Loc.Tf("Пользовательская служба (шаблон «{0}»). Windows создаёт её копию при каждом входе; изменение шаблона " +
                   "действует на всех пользователей, а отключение ломает буфер обмена Win+V, уведомления и «Связь с телефоном». " +
                   "Lycoris такие службы не меняет.", template);
        }
        if (HardBlockReason(name) is { } reason)
            return reason;
        if ((serviceType & 0x0B) != 0 || start is 0 or 1)
            return Loc.T("Драйвер или служба ранней загрузки: ошибка в настройке может помешать Windows загрузиться.");
        if (launchProtected != 0)
            return Loc.T("Защищённая служба (PPL): Windows запрещает менять её настройки даже администратору.");
        if (errorControl == 3)
            return Loc.T("Служба помечена как критичная для загрузки (ErrorControl = 3).");
        if (IsDriverStorePath(imagePath))
            return Loc.T("Служба из пакета драйвера производителя: без неё перестанут работать функции драйвера.");
        return null;
    }

    /// <summary>
    /// Services whose start type has exactly one place in Lycoris: an Optimize tweak (service name → tweak id).
    /// They are read-only here even in expert mode, so the same value is never changed from two pages.
    /// </summary>
    private static readonly Dictionary<string, string> OptimizeOwned = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DiagTrack"] = "diagtrack-manual",
    };

    /// <summary>The Optimize tweak id that owns this service, or null.</summary>
    public static string? OptimizeTweakFor(string name) => OptimizeOwned.GetValueOrDefault(name);

    /// <summary>Lock reason for a service owned by an Optimize tweak (applies in expert mode too), or null.</summary>
    public static string? OptimizeOwnedLockReason(string name) => OptimizeTweakFor(name) is null
        ? null
        : Loc.T("Эта служба настраивается только в разделе «Оптимизация» — там объяснён риск и там же откат.");

    /// <summary>
    /// Research §0.4/§2B "not in the curated allowlist": outside expert mode (ЭКСПЕРТНЫЙ РЕЖИМ on the ВСЕ СЛУЖБЫ tab)
    /// every non-curated service — other Microsoft services and all third-party ones — is read-only.
    /// Returns null when the service is curated or expert mode is on.
    /// </summary>
    public static string? NotCuratedLockReason(string name, bool isThirdParty, bool expertMode)
    {
        if (expertMode || ServiceCatalog.IsCurated(name)) return null;
        var what = isThirdParty
            ? Loc.T("Служба стороннего производителя: Lycoris не знает, какие функции её программы от неё зависят.")
            : Loc.T("Этой службы нет в проверенном списке Lycoris: последствия её изменения не изучены.");
        return what + Loc.T(" Здесь она только для просмотра. Менять такие службы на свой риск можно после включения " +
               "«Экспертного режима служб» над списком.");
    }

    public static bool IsDriverStorePath(string? imagePath) =>
        !string.IsNullOrEmpty(imagePath) && imagePath.Contains(@"\DriverStore\FileRepository\", StringComparison.OrdinalIgnoreCase);
}
