# MONOLITH: Services module design, with a curated safe list (verified on Win11 Pro build 26300.9457, "26H2")

## 0. Key findings

1. **Most of the commonly listed "services to disable" are already Manual or trigger-start by default on Windows 11.** This was verified on this machine. Xbox, Bluetooth, smart card, biometrics, geolocation, WER, wisvc, PhoneSvc, RetailDemo and WMPNetworkSvc are all `Manual` and **stopped**. Changing them to Disabled saves about 0 MB and 0% CPU, and only removes features. The module should show these as "уже оптимально" and should not count them as optimizations.
2. **Only about 10 Microsoft services are Auto by default and are realistic candidates:** DiagTrack, MapsBroker, TrkWks, PcaSvc, StorSvc, CDPSvc, Spooler, WSearch, LanmanServer and edgeupdate. The real gain is small, a few tens of MB of RAM. FPS gains are placebo, so the UI should not promise FPS.
3. **WinUtil (Chris Titus) reduced its "Set Services to Manual" list to 5 entries:** CscService→Disabled, DiagTrack→Disabled, MapsBroker→Manual, StorSvc→Manual, SharedAccess→Disabled. Its hard-coded `OriginalType` for SharedAccess is "Automatic", but on 26300 it is **Manual (trigger)**. **Never hard-code the "original" value. Always snapshot the live config.**
4. **Use an allowlist, not a blocklist.** MONOLITH should modify only services in its curated database. Everything else (third-party, anti-cheat, vendor drivers) is read-only. The NEVER-TOUCH list in §2 is defense in depth for an expert mode.
5. **TabletInputService no longer exists on Win11.** It was renamed to `TextInputManagementService` in build 22471. It is Auto with triggers, and it handles keyboard/IME, emoji, clipboard history and the touch keyboard. **Never touch it.**
6. **Some services refuse configuration changes even for admin.** This happens through PPL (`LaunchProtected` ≠ 0) and through DACLs that don't give `SERVICE_CHANGE_CONFIG` to Builtin\Administrators. Detect this and lock those services in the UI. Never take ownership or rewrite DACLs.
7. **Scheduled tasks differ by build.** On 26300 the task `Microsoft Compatibility Appraiser` is replaced by **`Microsoft Compatibility Appraiser Exp`**, and `ProgramDataUpdater` and `KernelCeipTask` are absent. Disabling the Appraiser is **debated**: it feeds feature-update eligibility and safeguard data, and turning it off can stop feature-update offers.

Legend for the tables:
- `—` = keep Windows default
- `M` = Manual
- `D` = Disabled
- `D?` / `M?` = only after the user ticks the condition checkbox
- "Trig" = has a `TriggerInfo` subkey (services.msc shows "Вручную (запуск по триггеру)")
- "Delayed" = `DelayedAutostart=1`

Profiles:
- **Normal** = zero-risk only
- **Recommended** = balanced default
- **Gaming** = Recommended plus extra conditional items

---

## 1. Curated services table

### 1A. Auto by default: changing these has a real effect

| Service (exact) | Display name | Что делает (RU) | Default on 26300 (verified) | Normal | Recommended | Gaming | Condition | Risk |
|---|---|---|---|---|---|---|---|---|
| `DiagTrack` | Connected User Experiences and Telemetry | Сбор и отправка диагностических данных (телеметрии) в Microsoft | Auto, no Trig, Running | — | M | M (D? in "Приватность+") | Not a Windows Insider (Insider requires optional diagnostic data). Not Intune/Autopatch/WUfB-reports managed. Apply the `AllowTelemetry` policy first (§4.3). | Low for M. D is **debated** (WinUtil uses D). |
| `MapsBroker` | Downloaded Maps Manager | Доступ приложений к скачанным офлайн-картам | Auto **Delayed**, no Trig, Stopped | M | M | M | none (the Maps app starts it on demand) | None |
| `TrkWks` | Distributed Link Tracking Client | Отслеживает перемещение NTFS-файлов для ярлыков и OLE-ссылок | Auto, no Trig | — | M | M | No domain network with moved files on shares | Very low |
| `PcaSvc` | Program Compatibility Assistant Service | Помощник совместимости: предлагает режимы совместимости для старых программ | Auto **Delayed** + Trig | — | — | M | none | Low. The benefit is debated. |
| `StorSvc` | Storage Service | Параметры хранилища, Контроль памяти, расположение новых приложений | Auto **Delayed** + Trig | — | — | M | none (WinUtil also sets M) | Low |
| `CDPSvc` | Connected Devices Platform Service | Связь с телефоном, «Обмен с устройствами поблизости», кросс-девайс буфер | Auto **Delayed** + Trig | — | — | M? | Not using Phone Link, Nearby Sharing or cross-device features | Low–Med |
| `Spooler` | Print Spooler | Очередь печати; нужна и для «Microsoft Print to PDF» | Auto, **no Trig** | — | — | D? | No printer, no Print-to-PDF, no OneNote/fax printing. ⚠ Without triggers, M is effectively off after boot. | Med for features. **Security gain:** this is MSRC's official PrintNightmare workaround. |
| `WSearch` | Windows Search | Индексация файлов и почты для быстрого поиска | Auto **Delayed** | — | — | — (D? Expert only) | Never searches file contents and no classic Outlook. Better: reduce the indexed locations instead. | Med (Start/Explorer/Outlook search). WMPNetworkSvc and workfolderssvc depend on it. |
| `LanmanServer` | Server | SMB-сервер: общий доступ к папкам/принтерам этого ПК | Auto + Trig (depends on SamSS, Srv2) | — | — | M? (Expert) | No shares hosted on this PC and no remote admin | Med |
| `iphlpsvc` | IP Helper | IPv6-туннели (Teredo, IP-HTTPS), portproxy | Auto (dependent: NcaSvc) | — | — | — | Teredo is used by Xbox networking (NAT type, party chat), DirectAccess and some VPNs | Med for Xbox users. **Keep.** |
| `edgeupdate` / `edgeupdatem` | Microsoft Edge Update Service | Обновление Edge **и WebView2 Runtime** | Auto Delayed + Trig / Manual + Trig | — | — | — | WebView2 is used by Widgets, Teams, Office and many launchers | **Never D.** A stale WebView2 is a security problem. |
| `gupdate`, `gupdatem` / `GoogleUpdaterService<ver>`, `GoogleUpdaterInternalService<ver>` | Google Update | Обновление Chrome | absent here | — | — | — | Match by prefix. Version-suffixed names change. | Never D (security) |
| `SysMain` | SysMain | Prefetch/Superfetch, оптимизация запуска приложений | Auto, Running | — | — | — | **Do not offer.** Disabling slows app launch, and the MMAgent cmdlets (memory compression, page combining) need SysMain. | Debated, so default = keep |

### 1B. Already Manual or Disabled by default: change is ~zero gain (offer `D?` only behind a condition)

| Service(s) | Display name | Что делает (RU) | Default on 26300 | N / R / G | `D?` condition | Risk if D |
|---|---|---|---|---|---|---|
| `dmwappushservice` | Device Management WAP Push message Routing | Маршрутизация push-сообщений MDM | Manual + Trig (flag Delayed=1, ignored) | — / — / — | Not MDM/Intune-enrolled, no provisioning packages, no UWF | Low–Med |
| `RetailDemo` | Retail Demo Service | Демо-режим для витрин магазинов | Manual | — / D / D | none | None |
| `WMPNetworkSvc` | Windows Media Player Network Sharing | DLNA-раздача медиатеки WMP Legacy | Manual (depends on WSearch) | — / D / D | No DLNA sharing from WMP Legacy | None |
| `RemoteRegistry` | Remote Registry | Удалённое изменение реестра | **Disabled** + Trig | keep D | Never enable it | n/a |
| `Fax` | Fax | Факс | **absent** (optional capability `Print.Fax.Scan`) | D? if present | No fax | None |
| `PrintNotify` | Printer Extensions and Notifications | Расширения/уведомления драйверов принтеров | Manual | — | Only together with Spooler D | Low |
| `XblAuthManager`, `XblGameSave` (Trig), `XboxNetApiSvc`, `XboxGipSvc` (Trig) | Xbox Live Auth Manager / Game Save / Networking / Accessory Mgmt | Вход Xbox, облачные сохранения, сеть/party, прошивка и настройка Xbox-контроллеров | Manual, Stopped | **— in all profiles** | Only "Не использую Xbox app / Game Pass / Store-игры / контроллер Xbox" | **High** for Game Pass users (sign-in, saves, controller) |
| `WerSvc` | Windows Error Reporting Service | Отчёты о сбоях, LocalDumps | Manual + Trig | — / — / — | Prefer the policy `Windows Error Reporting\Disabled=1` (§4.3) | Low–Med. Server guidance says "Do not disable". **Debated.** |
| `wisvc` | Windows Insider Service | Программа предварительной оценки | Manual + Trig | — / D? / D? | Not enrolled (no `HKLM\SOFTWARE\Microsoft\WindowsSelfHost\Applicability\BranchName`) | None |
| `lfsvc` | Geolocation Service | Геолокация и геозоны | Manual + Trig, Running | — | Location off **and** no auto time zone (`tzautoupdate`), Weather or Find My Device. Better: use the Settings toggle. | Low–Med |
| `PhoneSvc` | Phone Service | Состояние телефонии (звонки Phone Link, VoIP) | Manual + Trig | — | No Phone Link calls | Low |
| `SCardSvr`, `ScDeviceEnum`, `SCPolicySvc`, `CertPropSvc` | Smart Card … | Смарт-карты и токены | Manual + Trig | **— always** | No smart cards. ⚠ RU users: **Рутокен/JaCarta (ЭЦП, Госуслуги, ФНС)** and YubiKey PIV need these. | **High** |
| `WbioSrvc` | Windows Biometric Service | Отпечаток/лицо (Windows Hello) | Manual + Trig | **— always** | Do not offer | **High** (breaks Windows Hello) |
| `bthserv`, `BTAGService`, `BthAvctpSvc` | Bluetooth Support / Audio Gateway / AVCTP | Bluetooth, гарнитуры, AVRCP-кнопки | Manual + Trig, Running | — | No BT adapter at all (and then the gain is 0) | **High** (headsets, controllers) |
| `diagnosticshub.standardcollector.service` | Diagnostics Hub Standard Collector | Сборщик профилировщика VS | **absent** on 26300 (installed with VS / older builds) | — / D? | No Visual Studio diagnostics | None |
| `WpcMonSvc` | Parental Controls | Семейная безопасность | Manual | — | No Microsoft Family | Low |
| `SEMgrSvc` | Payments and NFC/SE Manager | NFC-платежи | Manual + Trig | — | No NFC | None |
| `icssvc` | Windows Mobile Hotspot Service | Мобильный хот-спот | Manual + Trig | — | Never uses hotspot | Low |
| `SharedAccess` | Internet Connection Sharing | ICS; DHCP/DNS для Hyper-V **Default Switch**/WSL2 NAT | Manual + Trig (WinUtil assumes Auto) | — | **Debated.** The Default Switch relies on ICS. | Med (Hyper-V/WSL2) |
| `CscService` | Offline Files | Автономные файлы сетевых папок | Manual + Trig | — / — / D? | No Offline Files (WinUtil uses D) | Low |
| `TermService`, `SessionEnv`, `UmRdpService` | Remote Desktop Services | Входящий RDP | Manual | — | Control RDP via Settings (`fDenyTSConnections`) instead | — |
| `vmic*` (8 services) | Hyper-V guest integration | Работают только внутри ВМ | Manual + Trig | — | none, since gain = 0 | — |

> **Disabled vs Manual:** Manual + Trig services already cost 0 when idle. `D` only blocks on-demand start, which turns "works when needed" into "mysteriously broken". Default every `D?` to unchecked.

---

## 2. Protected lists

### 2A. HARD-BLOCK: refuse in every mode

| Group | Services | Reason |
|---|---|---|
| RPC/COM core | `RpcSs`, `DcomLaunch`, `RpcEptMapper`, `EventSystem`, `SENS` | Everything depends on them. Disabling = unbootable or broken shell. Admin has **no `SERVICE_CHANGE_CONFIG`** on RpcSs/DcomLaunch (verified in SDDL). |
| Session/logon/identity | `LSM`, `ProfSvc`, `UserManager`, `SamSs`, `KeyIso`, `VaultSvc`, `Appinfo`, `gpsvc`, `TokenBroker`, `seclogon`, `wlidsvc`, `CredentialEnrollmentManagerUserSvc` | Logon, profiles, UAC elevation, MSA/Store tokens. LSM's DACL even denies non-elevated reads, so WMI shows StartMode "Unknown". |
| Platform/management | `Winmgmt`, `EventLog`, `Schedule`, `PlugPlay`, `Power`, `DeviceInstall`, `DsmSvc`, `BrokerInfrastructure`, `SystemEventsBroker`, `TimeBrokerSvc`, `CoreMessagingRegistrar`, `StateRepository` | WMI, logs, tasks, device install, power, UWP background |
| Shell/UWP/Store | `AppXSvc`, `ClipSVC`, `LicenseManager`, `InstallService`, `AppReadiness`, `camsvc`, `DispBrokerDesktopSvc`, `Themes`, `FontCache`, `TextInputManagementService` (Win10: `TabletInputService`), `ShellHWDetection`, `WpnService`, `WpnUserService`, `cbdhsvc` | Store/app licensing, display policy, typing/IME/emoji/Win+V, notifications. AppXSvc and ClipSVC are PPL. |
| Network | `Dhcp`, `Dnscache`, `nsi`, `NlaSvc`, `netprofm`, `Wcmsvc`, `WlanSvc`, `LanmanWorkstation`, `WinHttpAutoProxySvc`, `Netman`, `NcbService`, `RasMan`, `IKEEXT`, `BFE`, `mpssvc`, `DusmSvc` | Connectivity, VPN, firewall. BFE/mpssvc: admin has no `DC` right. |
| Security | `WinDefend`, `WdNisSvc`, `MDCoreSvc`, `Sense`, `SecurityHealthService`, `wscsvc`, `webthreatdefsvc`, `webthreatdefusersvc`, `AppIDSvc`, `CryptSvc`, `EFS`, `BDESVC`, `SgrmBroker` | Weakens security. Most are PPL and Tamper Protection guards them. |
| Servicing/recovery | `wuauserv`, `UsoSvc`, `WaaSMedicSvc`, `BITS`, `DoSvc`, `TrustedInstaller`, `msiserver`, `sppsvc`, `VSS`, `swprv`, `InventorySvc` | Updates, activation, **System Restore (VSS/swprv) that MONOLITH itself relies on** |
| Audio/video/input | `Audiosrv`, `AudioEndpointBuilder`, `FrameServer`, `FrameServerMonitor`, `hidserv`, `GameInputSvc` | Sound, webcams (Discord/OBS), HID/controllers |
| Virtualization/VBS | `HvHost`, `vmcompute`, `hns` (if present) | VBS/HVCI, WSL2, Sandbox |
| Diagnostics needed by OS | `DPS`, `WdiServiceHost`, `WdiSystemHost`, `whesvc`, `WSAIFabricSvc` | Troubleshooters and resource-exhaustion detection. WSAIFabricSvc (Windows AI components host, Auto, new) and whesvc have unknown dependents, so they are **debated** → block. |
| Third-party (rule) | Anti-cheat: `vgc`, `vgk`, `EasyAntiCheat*`, `BEService`, `FACEIT*`, `PnkBstr*`. Launchers: `Steam Client Service`, `GamingServices`, `GamingServicesNet`. Vendor services such as `NVDisplay.ContainerLocalSystem`, `AMD External Events Utility`, `amd3dvcacheSvc`, `AMD Crash Defender Service`. | Games won't launch, FreeSync/hotkeys/driver features break |

### 2B. Rule-based blocks (evaluate at runtime, any one blocks)

| Rule | Detection |
|---|---|
| Not in the curated allowlist | Name lookup |
| Driver | `Type & 0x0B` (`SERVICE_KERNEL_DRIVER=1`, `SERVICE_FILE_SYSTEM_DRIVER=2`), or `Start` ∈ {0,1} |
| Protected service (PPL) | `QueryServiceConfig2W(…, SERVICE_CONFIG_LAUNCH_PROTECTED=12)` ≠ 0, or registry `LaunchProtected`. Verified: WinDefend/WdNisSvc/MDCoreSvc=3 (Antimalware-Light); SecurityHealthService/wscsvc/AppXSvc/ClipSVC/WaaSMedicSvc/DoSvc/AppIDSvc/Sense=2 (Windows-Light); sppsvc=1. For PPL, MS docs say non-protected processes can't alter config (only `sc config start= auto` is allowed). |
| Admin lacks change right | `OpenServiceW(…, SERVICE_CHANGE_CONFIG)` → error 5. Verified: the BA ACE lacks `DC` on WinDefend, WdNisSvc, MDCoreSvc, SecurityHealthService, wscsvc, AppXSvc, ClipSVC, sppsvc, RpcSs, DcomLaunch, BFE, mpssvc. |
| Critical | `ErrorControl == 3` (SERVICE_ERROR_CRITICAL) |
| Vendor/driver package | `ImagePath` under `\System32\DriverStore\FileRepository\` |
| Running dependents | `EnumDependentServicesW(SERVICE_ACTIVE)` non-empty → warn/block |
| Policy-managed | Domain-joined / MDM-enrolled (`HKLM\SOFTWARE\Microsoft\Enrollments\*\EnrollmentState=1`, `dsregcmd /status`) → module read-only (GPO security templates will revert changes) |

---

## 3. Technical implementation (C# / .NET 10)

### 3.1 Reading config

Use the **SCM API**, ideally generated with `Microsoft.Windows.CsWin32`.
- `ServiceController` (NuGet `System.ServiceProcess.ServiceController`) can enumerate and read `StartType`, but it can't set it, can't see the delayed flag, and can't read triggers.
- WMI `Win32_Service.ChangeStartMode` can't set delayed-auto.

| Call | Purpose | Constants |
|---|---|---|
| `OpenSCManagerW(null, null, SC_MANAGER_CONNECT\|SC_MANAGER_ENUMERATE_SERVICE)` | SCM handle | 0x0001 \| 0x0004 |
| `OpenServiceW(scm, name, SERVICE_QUERY_CONFIG\|SERVICE_CHANGE_CONFIG\|SERVICE_QUERY_STATUS\|READ_CONTROL)` | Service handle. Open read-only when only scanning. | 0x1 \| 0x2 \| 0x4 \| 0x20000 |
| `QueryServiceConfigW` (two-call pattern, `ERROR_INSUFFICIENT_BUFFER`=122) | `QUERY_SERVICE_CONFIGW {dwServiceType, dwStartType, dwErrorControl, lpBinaryPathName, lpLoadOrderGroup, dwTagId, lpDependencies, lpServiceStartName, lpDisplayName}` | Start: 0 Boot, 1 System, 2 Auto, 3 Demand, 4 Disabled |
| `QueryServiceConfig2W(h, 3)` | `SERVICE_DELAYED_AUTO_START_INFO { BOOL fDelayedAutostart }` | `SERVICE_CONFIG_DELAYED_AUTO_START_INFO=3` |
| `QueryServiceConfig2W(h, 8)` | `SERVICE_TRIGGER_INFO { DWORD cTriggers; SERVICE_TRIGGER* pTriggers; BYTE* pReserved }` | `SERVICE_CONFIG_TRIGGER_INFO=8` (Unicode only) |
| `QueryServiceConfig2W(h, 12)` | `SERVICE_LAUNCH_PROTECTED_INFO { DWORD dwLaunchProtected }` | 0 None, 1 Windows, 2 Windows-Light, 3 Antimalware-Light |
| `QueryServiceObjectSecurity(h, DACL_SECURITY_INFORMATION)` | DACL snapshot (diagnostics only) | — |

`SERVICE_TRIGGER` (x64 layout):

| Field | Offset |
|---|---|
| `dwTriggerType` | @0 |
| `dwAction` | @4 (1 = start, 2 = stop) |
| `GUID* pTriggerSubtype` | @8 |
| `cDataItems` | @16 |
| `pDataItems` | @24 (`SERVICE_TRIGGER_SPECIFIC_DATA_ITEM {dwDataType, cbData, pData}`) |

Trigger types:

| Value | Type |
|---|---|
| 1 | DEVICE_INTERFACE_ARRIVAL |
| 2 | IP_ADDRESS_AVAILABILITY |
| 3 | DOMAIN_JOIN |
| 4 | FIREWALL_PORT_EVENT |
| 5 | GROUP_POLICY |
| 6 | NETWORK_ENDPOINT (subtype `RPC_INTERFACE_EVENT_GUID` BC90D167-9470-4139-A9BA-BE0BBBF5B74D or `NAMED_PIPE_EVENT_GUID`) |
| 20 | CUSTOM (ETW) |

Fast check: `HKLM\SYSTEM\CurrentControlSet\Services\<svc>\TriggerInfo` exists. Debug: `sc.exe qtriggerinfo <svc>`. Verified example: XblGameSave = NETWORK_ENDPOINT/RPC; bthserv = DEVICE_INTERFACE_ARRIVAL + 2× RPC.

**Never modify triggers.** Store them only for display, as "Вручную (триггер)".

Registry layout (read and fallback only), `HKLM\SYSTEM\CurrentControlSet\Services\<svc>`:

| Value | Type | Meaning |
|---|---|---|
| `Start` | REG_DWORD | 0–4 |
| `DelayedAutostart` | REG_DWORD | 0/1 |
| `Type` | REG_DWORD | service type |
| `ErrorControl` | REG_DWORD | error control level |
| `LaunchProtected` | REG_DWORD | PPL level |
| `UserServiceFlags` | REG_DWORD | per-user template flags |
| `ObjectName` | REG_SZ | service account |
| `TriggerInfo\N\` | subkey | trigger definitions |

### 3.2 Changing start type safely

1. **Pre-checks:** elevated, allowlisted, no §2B rule hit, not domain/MDM.
2. **Write-ahead journal:** persist the snapshot JSON (3.5) and flush it **before** any change.
3. **Restore point:** once per batch (`SRSetRestorePointW`, or WMI `root\default:SystemRestore.CreateRestorePoint(desc, 12 /*MODIFY_SETTINGS*/, 100)`). Note the default 1440-minute throttle (`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\SystemRestorePointCreationFrequency`). If no restore point was created, the batch should require an explicit "continue without restore point" confirmation.
4. **Apply:** `ChangeServiceConfigW(h, SERVICE_NO_CHANGE /*0xFFFFFFFF*/, newStart, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null)`.
   - Auto→Auto-Delayed: set start=2, then `ChangeServiceConfig2W(h, 3, &{fDelayedAutostart=TRUE})`.
   - Away from Auto: leave the delayed flag as it is (it's ignored), but restore it explicitly on revert.
5. **Don't stop by default.** M takes effect at next boot. "Остановить сейчас" is allowed only when there are no active dependents (`ControlService(SERVICE_CONTROL_STOP)`).
6. **Verify:** re-query and mark the journal entry `applied`.
7. **Fallback:** `sc.exe config <svc> start= demand|auto|delayed-auto|disabled` (the space after `=` is required). Check only the exit code (0 OK, 5 access denied, 1060 does not exist). Output is localized (RU), so never parse it.
8. **Don't write `Start` to the registry directly.** The SCM caches config until reboot, there is no validation, and ACL/PPL bypass attempts are security-weakening.

### 3.3 Per-user services (`<template>_<LUID>`)

Verified on this machine with suffix `_9fa35`. Templates: AarSvc, BcastDVRUserService, BluetoothUserService, CaptureService, cbdhsvc (Auto), CDPUserSvc (Auto), CloudBackupRestoreSvc, ConsentUxUserSvc, CredentialEnrollmentManagerUserSvc, DeviceAssociationBrokerSvc, DevicePickerUserSvc, DevicesFlowUserSvc, MessagingService, NPSMSvc, OneSyncSvc, P9RdrService, PenService, PimIndexMaintenanceSvc, PrintWorkflowUserSvc, UdkUserSvc, UnistoreSvc, UserDataSvc, webthreatdefusersvc (Auto), WpnUserService (Auto).

| Aspect | Rule |
|---|---|
| Detection | `Type & 0x40` (SERVICE_USER_SERVICE). Template = 0x50/0x60. Instance adds 0x80 → 0xD0/0xE0. Fallback regex `^(.+)_[0-9a-f]{4,8}$`. |
| Suffix | The logon-session LUID. It changes per logon/boot, so **never persist instance names**. Store the template name. |
| Editing instance | Lost at sign-out. Don't do it. |
| Editing template | `OpenServiceW("CDPUserSvc")` works (MS docs use `Set-Service -Name PimIndexMaintenanceSvc`). Takes effect at the next sign-in for **all users**. |
| `UserServiceFlags=0` | Prevents instance creation. **MONOLITH must never use it.** |
| Policy | Per-user services are **read-only** in MONOLITH (grouped under the template). Disabling cbdhsvc / WpnUserService / CDPUserSvc / UnistoreSvc breaks Win+V, notifications, Phone Link and Mail/People. |

### 3.4 Admin "Access denied" cases

| Cause | Examples (verified) | Handling |
|---|---|---|
| PPL (`LaunchProtected` ≠ 0) | WinDefend, WdNisSvc, MDCoreSvc, SecurityHealthService, wscsvc, AppXSvc, ClipSVC, WaaSMedicSvc, DoSvc, AppIDSvc, Sense, sppsvc | Lock in the UI with a 🔒 "Защищено Windows" badge |
| DACL lacks `DC` for BA | RpcSs, DcomLaunch, BFE, mpssvc + the security services above | Lock |
| Tamper Protection / registry ACL | Defender keys | Never attempt |
| TrustedInstaller-owned keys | Many `Services\*` keys | Don't take ownership |

### 3.5 Backup/revert record

Store at `%ProgramData%\MONOLITH\backups\services\<batchId>.json`, with the ACL set to SYSTEM + Administrators write only. This stops non-admin malware from feeding an elevated revert.

```json
{"batchId":"…","utc":"…","os":{"build":26300,"ubr":9457,"displayVersion":"26H2"},
 "restorePoint":{"created":true,"seq":42},
 "items":[{"name":"DiagTrack","serviceType":16,"errorControl":1,
   "orig":{"start":2,"delayed":false},"applied":{"start":3,"delayed":false},
   "hasTriggers":false,"launchProtected":0,"imagePathSha256":"…","state":"applied"}]}
```

**Revert:**

| Situation | Action |
|---|---|
| Service missing | Skip and log |
| current == `applied` | Restore `orig` (start first, then delayed) |
| current ≠ `applied` (drift: Windows Update, GPO or user) | **Ask**. Don't overwrite silently. |
| Name not in allowlist | Reject the record (tamper check) |

**Post-feature-update:** when `CurrentBuild`/`UBR` changes, re-scan and show the drift. Never re-apply silently.

---

## 4. Scheduled tasks

### 4.1 Task table (state verified on 26300 where noted)

| Task path | Action / Что делает | State on 26300 | N / R / G | Notes |
|---|---|---|---|---|
| `\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser` | `compattelrunner.exe` appraiser: оценка совместимости + телеметрия | **absent** (present on Win10 / ≤23H2) | — / — / — (opt-in "Приватность+") | **Debated.** Feeds upgrade eligibility and safeguards. Disabling can stop feature-update offers. |
| `…\Application Experience\Microsoft Compatibility Appraiser Exp` | `compattelrunner.exe -m:appraiser.dll -f:DoScheduledTelemetryRun express` | Ready | same as above | same as above |
| `…\Application Experience\ProgramDataUpdater` | CEIP-инвентаризация программ | absent (Win10/early 11) | D if present | Low |
| `…\Application Experience\MareBackup` | `compattelrunner -m:aemarebackup.dll` (резерв данных appraiser) | Ready | — / D / D | Low |
| `…\Application Experience\StartupAppTask` | «Влияние на запуск» в Диспетчере задач | Ready | keep | Breaks startup-impact info |
| `…\Application Experience\PcaPatchDbTask`, `SdbinstMergeDbTask` | Базы совместимости | Ready | **never** | Compat shims |
| `\Microsoft\Windows\Customer Experience Improvement Program\Consolidator` | `wsqmcons.exe` — CEIP | Ready | D / D / D | Safe |
| `…\Customer Experience Improvement Program\UsbCeip` | USB CEIP | Ready | D / D / D | Safe |
| `…\Customer Experience Improvement Program\KernelCeipTask` | Kernel CEIP | absent (Win10) | D if present | Safe |
| `\Microsoft\Windows\Autochk\Proxy` | `rundll32 acproxy.dll,PerformAutochkOperations` (SQM-данные autochk) | Ready | — / D / D | Low |
| `\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector` | Сбор данных о дисках для MS | Ready (Resolver already Disabled) | — / D / D | Low |
| `\Microsoft\Windows\Feedback\Siuf\DmClient`, `DmClientOnScenarioDownload` | Опросы «Отзывы» | Ready | D / D / D | Safe |
| `\Microsoft\Windows\Windows Error Reporting\QueueReporting` | Отправка очереди WER | Ready | — / — / D? | Low. Prefer the WER policy. |
| `\Microsoft\Windows\Maps\MapsToastTask`, `MapsUpdateTask` | Карты | Ready / Disabled | — / D / D | Safe |
| `\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem` | Анализ энергоэффективности | Ready | — / — / D | Low |
| `\Microsoft\Windows\Sustainability\SustainabilityTelemetry` | Телеметрия «устойчивости» (new) | Ready | — / D / D | Low |
| `\Microsoft\Windows\Device Information\Device`, `Device User` | Сбор сведений об устройстве | Ready | keep | Debated, unknown consumers |
| `\Microsoft\Windows\Flighting\FeatureConfig\*`, `\Flighting\OneSettings\RefreshCache` | Controlled feature rollout | Ready | **never** | Breaks feature enablement |
| `\Microsoft\Windows\Defrag\ScheduledDefrag` | TRIM / оптимизация | Ready | **never** | SSD retrim |
| `\MicrosoftEdgeUpdateTaskMachineCore{GUID}`, `…UA{GUID}` | Edge + WebView2 updates | root folder; not visible unelevated | **never** | Security |
| `\Microsoft\Office\OfficeTelemetryAgentLogOn2016`, `…FallBack2016` | Office telemetry | if present | D | Low |
| `\NvTmRep_CrashReport1..4_{…}` | Old NVIDIA telemetry | if present (match by prefix) | D | Low |

### 4.2 Mechanism

**Read and back up:**
- Task Scheduler COM (`ITaskService.Connect()` → `GetFolder(path).GetTask(name)` → `IRegisteredTask.Enabled`, plus `.Xml` for the backup), or the NuGet `TaskScheduler` (dahall) wrapper.
- CLI fallback: `schtasks /Query /TN "<path>" /XML`. Status text is localized, so don't parse it.

**Change:**
```
schtasks /Change /TN "\Microsoft\Windows\Customer Experience Improvement Program\Consolidator" /Disable
schtasks /Change /TN "<same>" /Enable    :: revert only if originally Enabled
```
- Exit code: 0 = OK, 1 = fail (e.g. UpdateOrchestrator/WaaSMedic tasks deny admin → treat as protected).
- Needs elevation.
- Existence varies by build, so query before offering.
- Feature updates may re-enable or re-create tasks, so run a drift check.

### 4.3 Prefer official policies over killing services/tasks

Revert = restore the previous value, or delete it if it was absent.

| Policy (HKLM, REG_DWORD) | Value | Effect |
|---|---|---|
| `SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry` | 1 | Required diagnostic data. 0 works only on Enterprise/Education/Server; Pro treats it as 1. This machine already has `…\CurrentVersion\Policies\DataCollection\AllowTelemetry=1`. |
| `SOFTWARE\Policies\Microsoft\SQMClient\Windows\CEIPEnable` | 0 | CEIP off (Consolidator/UsbCeip become no-ops) |
| `SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting\Disabled` | 1 | WER off without disabling WerSvc |
| `SOFTWARE\Policies\Microsoft\Windows\AppCompat\AITEnable` | 0 | "Turn off Application Telemetry" |

---

## Sources

- https://learn.microsoft.com/en-us/windows/application-management/per-user-services-in-windows
- https://learn.microsoft.com/en-us/windows-server/security/windows-services/security-guidelines-for-disabling-system-services-in-windows-server
- https://learn.microsoft.com/en-us/windows/win32/services/protecting-anti-malware-services-
- https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfig2w
- https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_trigger
- https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfigw
- https://msrc.microsoft.com/update-guide/vulnerability/CVE-2021-34527 (Spooler disable workaround)
- https://blogs.windows.com/windows-insider/2021/10/04/announcing-windows-11-insider-preview-build-22471 (TabletInputService → TextInputManagementService)
- https://pc.watch.impress.co.jp/docs/news/1356096.html
- https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/config/tweaks.json (WPFTweaksServices, 5 entries)
- https://techcommunity.microsoft.com/discussions/windows-servicing/making-sure-safeguards-are-respected/2184529
- https://support.microsoft.com/en-ca/help/4525208 (Appraiser / CompatTelRunner data flow)
- https://discourse.ubuntu.com/t/13694 (Hyper-V Default Switch relies on ICS)
- https://learn.microsoft.com/en-us/answers/questions/172301/hyper-v-vm-network-connection-problem-with-default
- Local read-only verification on this machine: `Get-CimInstance Win32_Service`, `Get-Service`, `sc.exe sdshow/qprotection/qtriggerinfo/qc`, `Get-ScheduledTask`, `reg query` (shell was **not elevated**, so root-folder tasks such as the Edge updater were not visible)