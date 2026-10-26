# MONOLITH: Drivers module design

The design uses three update sources: Windows Update (WUA COM), vendor tools from the vendors themselves, and a few NVIDIA endpoints that are hosted by NVIDIA but not documented. It does not use third-party driver databases. Every claim marked ✔ was checked read-only on the dev machine (build 26300, winget v1.11.510) on 2026-10-26.

---

## 0. Snapshot of the dev machine (read-only checks)

| Item | Result |
|---|---|
| Problem devices (`Win32_PnPEntity ConfigManagerErrorCode<>0`) | none |
| `Win32_PnPSignedDriver` rows / unsigned | 213 / 0 |
| dGPU | `PCI\VEN_10DE&DEV_2C05…` RTX 5070 Ti, `DriverVersion 32.0.16.1088` = **610.88**, oem14.inf |
| iGPU | `PCI\VEN_1002&DEV_13C0…` AMD Radeon Graphics, `32.0.11024.2`, `RadeonSoftwareVersion=24.10.24` (June 2024, came from WU) |
| NVIDIA latest (live lookup) | **621.01** Game Ready, WHQL, 2026-10-21 |
| AMD chipset pieces | present, **all delivered by Windows Update**. No "AMD Chipset Software" entry under Uninstall |
| WUA COM | `Microsoft.Update.Session` → CLSID `{4CB43D7F-7EEE-4906-8698-60DA1C38F2FE}`, `InprocServer32 ThreadingModel=Both`, TypeLib `{B596CC9F-56E5-419E-A622-E01BB457431E}` v2.0 |
| Driver policy | `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` absent; `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching\SearchOrderConfig = 1` (REG_DWORD, default) |

---

## 1. Windows Update Agent (WUA) API from C#

### 1.1 Mechanics
- **Search criteria:** `IsInstalled=0 and Type='Driver' and IsHidden=0`. The default criteria string is `IsInstalled=0 and IsHidden=0`. `OR` is allowed only at the top level, and `!=` works only with `Type`.
  - `BrowseOnly=0` gives *recommended* drivers. WU installs these by itself anyway.
  - `BrowseOnly=1` gives *optional* drivers (Settings → Windows Update → Advanced → Optional updates, URI `ms-settings:windowsupdate-optionalupdates`).
  - Microsoft's guidance is to install optional drivers only to fix a specific problem. **Leave them unchecked by default.**
- **Server:** `IUpdateSearcher.ServerSelection = 0` (ssDefault) follows WSUS/WUfB policy. `2` (ssWindowsUpdate) forces Microsoft's servers. Use 0, and offer 2 only as an advanced choice. Set `Online = true`.
- **Fields to show:**
  - From `IUpdate`: `Title`, `Description`, `Identity.UpdateID`/`RevisionNumber`, `BrowseOnly`, `MaxDownloadSize` (DECIMAL), `SupportUrl`, `EulaAccepted`, `InstallationBehavior.RebootBehavior` (0 never, 1 always, 2 can request), `LastDeploymentChangeTime`.
  - From `IWindowsDriverUpdate` (QI on driver updates; with `dynamic` the properties resolve directly): `DriverClass`, `DriverManufacturer`, `DriverProvider`, `DriverModel`, `DriverHardwareID`, `DriverVerDate`, `DeviceProblemNumber`, `DeviceStatus`. For drivers that map to several devices, `IWindowsDriverUpdate4.WindowsDriverUpdateEntries` holds the same fields per device.
  - **There is no version property.** The version is the tail of `Title` (for example `"NVIDIA - Display - 32.0.15.xxxx"`), parsed with the regex `(\d+\.){1,3}\d+$`.
- **Elevation:**
  - Search works as a standard user.
  - Download, Install, and setting `IsHidden` need administrator rights. Expect `0x80070005` otherwise.
  - Check with `WindowsPrincipal.IsInRole(Administrator)` before enabling the "Install" button.
- **Busy and reboot checks:**
  - Check `IUpdateInstaller.IsBusy`. If another install is running, the call fails immediately with WU_E_OPERATIONINPROGRESS.
  - Check `RebootRequiredBeforeInstallation`, and the global `Microsoft.Update.SystemInfo.RebootRequired`.
  - After install, read `IInstallationResult.RebootRequired`, plus `GetUpdateResult(i).ResultCode` (OperationResultCode: 2 Succeeded, 3 SucceededWithErrors, 4 Failed, 5 Aborted) and `.HResult`.
  - **Never reboot automatically.** Show a banner instead: «Требуется перезагрузка для завершения установки драйвера».
- **Threading:**
  - `ThreadingModel=Both` ✔, so create and use the objects on a dedicated **MTA** thread. Do not create them on the WPF STA UI thread: a synchronous `Search()` there freezes the UI, and objects created there get marshaled through a proxy.
  - Don't share one session across unrelated operations.
  - `End*` calls may be made from any thread. Never call `job.CleanUp()` inside a callback; call it after `End*`.
- **Timeouts:**
  - WUA has none, and Microsoft tells callers to build their own.
  - Search: run it synchronously on a worker thread with `WaitAsync(5 min)`. A sync call can't be cancelled, so on timeout let the background thread end by itself.
  - Download and install: use `BeginDownload`/`BeginInstall` with callbacks so that `job.RequestAbort()` can cancel them. Suggested limits are 30 min for download and 30 min for install.
- **EULA:** if `EulaAccepted == false`, show `EulaText` and require the user to click before calling `AcceptEula()`. Never accept it silently.
- **Hiding a bad driver:** `IUpdate.IsHidden = true` is the documented way to block one driver. Record it in the Undo Center; the revert is `IsHidden = false`.
- **HRESULTs to map to Russian messages:**
  - `0x80070422`: wuauserv disabled. «Служба Windows Update отключена». This means **the Services module must never disable wuauserv, BITS or TrustedInstaller.**
  - `0x8024402C`, `0x80072EE7`: no network or DNS.
  - `0x8024000B`: cancelled.
  - `0x80240016`: install not allowed, usually because another install is running or a reboot is pending.
- **Policies to read and display (read-only):**
  - `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\ExcludeWUDriversInQualityUpdate` (DWORD, 1 = WU won't deliver drivers).
  - The same value under `HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Update`.
  - `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching\SearchOrderConfig` (DWORD, default 1).
  - Classification: **turning WU drivers off is debated.** It stops WU from overwriting GPU drivers, but it also blocks driver security fixes. Expose it only as an advanced, reversible option that is off by default.

### 1.2 C# sketch (.NET 10, WPF; `dynamic` COM, no trimming or AOT)

All callback IIDs below were checked in `HKCR\Interface` ✔.

```csharp
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

[ComImport, Guid("E01402D5-F8DA-43BA-A012-38894BD048F1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IInstallationProgressChangedCallback { void Invoke([MarshalAs(UnmanagedType.IUnknown)] object job, [MarshalAs(UnmanagedType.IUnknown)] object args); }
[ComImport, Guid("45F4F6F3-D602-4F98-9A8A-3EFA152AD2D3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IInstallationCompletedCallback { void Invoke([MarshalAs(UnmanagedType.IUnknown)] object job, [MarshalAs(UnmanagedType.IUnknown)] object args); }
// Also available: ISearchCompletedCallback 88AEE058-D4B0-4725-A2F1-814A67AE964C,
// IDownloadCompletedCallback 77254866-9F5B-4C8E-B9E2-C77A8530D64B, IDownloadProgressChangedCallback 8C3F1CDD-6173-4591-AEBD-A56A53CA77C1

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
sealed class InstallCb : IInstallationProgressChangedCallback, IInstallationCompletedCallback {
    public readonly ManualResetEventSlim Done = new(); public IProgress<int>? P;
    void IInstallationProgressChangedCallback.Invoke(object j, object a) => P?.Report((int)((dynamic)a).Progress.PercentComplete);
    void IInstallationCompletedCallback.Invoke(object j, object a) => Done.Set();
}

public sealed record WuDriver(string Id, string Title, string? Version, string? Class, string? Manufacturer,
    string? Provider, string? Model, string? HwId, DateTime? Date, int Problem, bool Optional, decimal SizeMB);

public static class Wua {
    const string Criteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";
    static readonly Regex Ver = new(@"(\d+\.){1,3}\d+\s*$");
    static T? Try<T>(Func<T> f) { try { return f(); } catch { return default; } }
    static dynamic New(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, true)!)!;

    static Task<T> Mta<T>(Func<T> body, TimeSpan timeout, CancellationToken ct) {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() => { try { tcs.TrySetResult(body()); } catch (Exception e) { tcs.TrySetException(e); } })
                { IsBackground = true, Name = "WUA" };
        t.SetApartmentState(ApartmentState.MTA); t.Start();
        return tcs.Task.WaitAsync(timeout, ct);            // TimeoutException → «Windows Update не отвечает»
    }

    static dynamic Search(dynamic session) {
        dynamic s = session.CreateUpdateSearcher(); s.Online = true; s.ServerSelection = 0; // ssDefault
        dynamic r = s.Search(Criteria);
        if ((int)r.ResultCode is not (2 or 3)) throw new InvalidOperationException($"WUA search rc={(int)r.ResultCode}");
        return r.Updates;
    }

    public static Task<List<WuDriver>> FindAsync(CancellationToken ct) => Mta(() => {
        dynamic session = New("Microsoft.Update.Session"); session.ClientApplicationID = "MONOLITH";
        dynamic ups = Search(session); var list = new List<WuDriver>();
        for (int i = 0; i < (int)ups.Count; i++) {
            dynamic u = ups[i]; string title = u.Title; var v = Ver.Match(title).Value.Trim();
            list.Add(new((string)u.Identity.UpdateID, title, v.Length > 0 ? v : null,
                Try(() => (string)u.DriverClass), Try(() => (string)u.DriverManufacturer),
                Try(() => (string)u.DriverProvider), Try(() => (string)u.DriverModel),
                Try(() => (string)u.DriverHardwareID), Try(() => (DateTime?)(DateTime)u.DriverVerDate),
                Try(() => (int)u.DeviceProblemNumber), (bool)u.BrowseOnly, (decimal)u.MaxDownloadSize / 1048576m));
        }
        return list;
    }, TimeSpan.FromMinutes(5), ct);

    // Caller has already: confirmed with the user, created a restore point, exported the current oemNN.inf.
    public static Task<(bool reboot, int[] codes)> InstallAsync(ISet<string> ids, IProgress<int> p, CancellationToken ct) => Mta(() => {
        dynamic session = New("Microsoft.Update.Session"); session.ClientApplicationID = "MONOLITH";
        dynamic ups = Search(session); dynamic coll = New("Microsoft.Update.UpdateColl");
        for (int i = 0; i < (int)ups.Count; i++) { dynamic u = ups[i];
            if (ids.Contains((string)u.Identity.UpdateID) && (bool)u.EulaAccepted) coll.Add(u); }
        if ((int)coll.Count == 0) return (false, Array.Empty<int>());
        dynamic dl = session.CreateUpdateDownloader(); dl.Updates = coll; dl.Download(); // or BeginDownload for progress/abort
        dynamic inst = session.CreateUpdateInstaller();
        if ((bool)inst.IsBusy) throw new InvalidOperationException("Windows Update уже выполняет установку");
        if ((bool)inst.RebootRequiredBeforeInstallation) throw new InvalidOperationException("Сначала перезагрузите ПК");
        inst.Updates = coll; inst.AllowSourcePrompts = false;
        var cb = new InstallCb { P = p };
        dynamic job = inst.BeginInstall(cb, cb, null);
        using (ct.Register(() => { try { job.RequestAbort(); } catch { } }))
            if (!cb.Done.Wait(TimeSpan.FromMinutes(30))) job.RequestAbort();
        cb.Done.Wait(TimeSpan.FromMinutes(2));
        dynamic r = inst.EndInstall(job); job.CleanUp();
        var codes = Enumerable.Range(0, (int)coll.Count).Select(i => (int)r.GetUpdateResult(i).ResultCode).ToArray();
        return ((bool)r.RebootRequired, codes);
    }, TimeSpan.FromMinutes(70), ct);
}
```

Notes on the sketch:
- `dynamic` needs built-in COM interop and fails under `PublishTrimmed` or NativeAOT, which WPF doesn't use anyway.
- The alternative is a checked-in tlbimp'd `Interop.WUApiLib.dll`. `<COMReference>` builds only with VS MSBuild, not with `dotnet build` (error MSB4803).
- Test the callback CCW path on Windows 10 22H2 as well.

---

## 2. Listing installed drivers and problem devices

Use `System.Management` (NuGet) on `Task.Run` (thread pool, MTA). A full `Win32_PnPSignedDriver` query takes several seconds, so cache the result and refresh it manually.

```sql
SELECT DeviceName, DeviceID, DeviceClass, DriverVersion, DriverDate, DriverProviderName, Manufacturer,
       InfName, IsSigned, Signer, HardWareID FROM Win32_PnPSignedDriver
SELECT Name, DeviceID, PNPClass, HardwareID, ConfigManagerErrorCode, Present, Status
       FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0
```

- Convert `DriverDate` with `ManagementDateTimeConverter.ToDateTime`.
- **Never call a driver "outdated" because of its date.** Inbox drivers are deliberately dated 2006/2009 so that any OEM driver outranks them. On this machine `cpu.inf` shows 21.04.2009 with version 10.0.26100.9278. Windows ranks drivers by signature, then hardware-ID match, then date, then version.
- Show a «Встроенный драйвер Microsoft» badge when `InfName` is not `oem*.inf`.
- Group the duplicates (16× "AMD Processor").
- Alternative sources: `pnputil /enum-devices /problem /deviceids` and `pnputil /enum-drivers /format xml`. ✔ on 26300 (`/format txt|xml|csv`); on Windows 10, parse the text output.
- **Missing drivers** have code 28. For each one:
  1. Show `HardwareID[0]` and the vendor from a local VEN/VID table (10DE NVIDIA, 1002/1022 AMD, 8086/8087 Intel, 10EC/0BDA Realtek, 14E4 Broadcom, 168C/17CB Qualcomm, 1B21 ASMedia, 1462 MSI subsys…).
  2. Run the WU search; WU matches problem devices, as shown by `DeviceProblemNumber`.
  3. Link to the motherboard or laptop vendor's support page, found from `Win32_BaseBoard.Manufacturer/Product` and `Win32_ComputerSystem.Model`.
- Don't list `Present=FALSE` devices or code 45 as errors.

| Code | UI text (RU) | Suggested action |
|---|---|---|
| 1 | Устройство настроено неправильно | Обновить драйвер |
| 3 | Драйвер повреждён или не хватает памяти | Переустановить драйвер |
| 10 | Устройство не может запуститься | Обновить или откатить драйвер |
| 12 | Недостаточно свободных ресурсов | Проверить настройки BIOS/UEFI |
| 14 | Требуется перезагрузка | Перезагрузить |
| 18 | Нужно переустановить драйверы | Переустановить |
| 19 | Повреждены сведения о конфигурации в реестре | Удалить устройство и сканировать заново; **реестр автоматически не чистить** |
| 21/38/47 | Устройство удаляется / прежний драйвер ещё в памяти / подготовлено к извлечению | Перезагрузить |
| 22 | Устройство отключено | Включить (`pnputil /enable-device`), с подтверждением |
| 24 | Устройство отсутствует или установлены не все драйверы | Переподключить, обновить |
| **28** | **Драйвер не установлен** | Windows Update → сайт производителя |
| 29 | Отключено в прошивке (BIOS/UEFI) | Включить в BIOS |
| 31/39 | Windows не может загрузить драйвер / драйвер повреждён или отсутствует | Переустановить |
| 32 | Служба драйвера отключена (Start=4) | Часто это след «твикера»; предложить вернуть исходный Start |
| 33/34/35 | Ресурсы не определены / нужна ручная настройка / в прошивке нет данных | Обновить BIOS у производителя платы |
| 37/40/41/42 | Ошибка инициализации / нет ключа службы / устройство не найдено / дубликат | Переустановить |
| 43 | Windows остановила устройство: оно сообщило о проблеме | Драйвер, питание или железо; частый код для GPU и USB |
| 44 | Устройство выключено приложением или службой | Проверить ПО |
| 48 | Драйвер заблокирован из-за проблем совместимости | **Не обходить**; ждать новую версию |
| 52 | Не удаётся проверить цифровую подпись | **Не отключать проверку подписи**; ставить только подписанный драйвер |
| 54/56 | Устройство перезапускается / Windows ещё настраивает класс | Подождать, перезагрузить |

---

## 3. GPU drivers

**Detecting the vendor:** read `Win32_VideoController.PNPDeviceID`. `VEN_10DE` is NVIDIA, `VEN_1002` is AMD, `VEN_8086` is Intel. If `InfPath=display.inf` ("Microsoft Basic Display Adapter"), the real driver is missing. The installed details live in `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\00NN` (`DriverDesc`, `DriverVersion`, `ProviderName`, `MatchingDeviceId`, `InfPath`).

### NVIDIA
- **Installed version:**
  - `DriverVersion "32.0.16.1088"`: drop the dots and take the last 5 digits, `61088` → **610.88**.
  - Cross-check with the Uninstall key `{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}_Display.Driver`, `DisplayVersion=610.88` ✔.
- **Latest version.** This is undocumented but hosted by NVIDIA, and the live call worked on 2026-10-26. Classify it as *unofficial: use only for an "update available" hint*.
  1. Get psid/pfid: `https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3` returns XML `LookupValue{ParentID=psid, Name, Value=pfid}`. RTX 5070 Ti is psid 131, pfid 1068; the Laptop variant is psid 133, pfid 1075. Cache the XML for 7 days.
  2. Query: `https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid=131&pfid=1068&osID=57&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1`
  3. Read `IDS[0].downloadInfo.{Version, ReleaseDateTime, DownloadURL, DetailsURL, IsWHQL, IsBeta}`. `Name` and the notes are URL-encoded. The live call returned 621.01, `DetailsURL …/drivers/details/282412/`.
  4. On any error or schema change, fall back silently to «Проверить в NVIDIA App / на nvidia.com».
- **Installing:**
  - Recommended: the **NVIDIA App** via the Microsoft Store source of winget, **`XP8CLZL93F5Z4P`** ✔ (publisher "NVIDIA Corp."). Command: `winget install --id XP8CLZL93F5Z4P --source msstore`. The msstore agreements must be accepted by the user in the UI.
  - Or open `DetailsURL` in the browser.
  - If you ever add a direct download: allow only `*.download.nvidia.com`, verify the Authenticode signer "NVIDIA Corporation" (WinVerifyTrust), and launch the installer interactively, not with silent flags.

### AMD
- **Installed version:** the Class key holds `RadeonSoftwareVersion` (REG_SZ, here `24.10.24`), `RadeonSoftwareEdition` and `ReleaseVersion` ✔.
- **Latest version:** there is **no public API**. AMD's site listed 26.9.1 (optional, 2026-09-03) and 26.8.1 (WHQL Recommended). drivers.amd.com refuses downloads without a Referer, so **don't scrape it**.
- **Recommendation:** open `https://www.amd.com/en/support/download/drivers.html`, which hosts the official auto-detect installer.
- **winget:**
  - There is **no Adrenalin package**: `AMD.AMDSoftware` and `AMD.RadeonSoftware` were not found.
  - `AMD.AMDSoftwareCloudEdition` exists but is for **Azure NGads V620 VMs only**. Block-list it.
  - msstore `9NZ1BJQN6BHL` "AMD Radeon Software" is only the control-panel UI and contains no driver.
- **iGPU on this machine:** the iGPU has an old WU driver (24.10.24) while the display runs on NVIDIA. Show this as informational only, with no nag.

### Intel
- `Intel.IntelDriverAndSupportAssistant` ✔ (v26.2.0.7, moniker `intel-dsa`). Alternative page: `https://www.intel.com/content/www/us/en/support/detect.html`. No public version API.

### Verified IDs

| Purpose | ID | Status |
|---|---|---|
| NVIDIA App | `Nvidia.NVIDIAApp`, `Nvidia.GeForceExperience`, `Nvidia.App`, `Nvidia.NvidiaApp` | ✘ not in winget |
| NVIDIA App | **msstore `XP8CLZL93F5Z4P`** | ✔ |
| NVIDIA Control Panel | msstore `9NF8H0H7WMLT` | ✔. Only if it is missing; it normally comes with the DCH driver |
| AMD Adrenalin | — | ✘ none. Use the amd.com auto-detect installer |
| AMD chipset | — | ✘ none (`winget search chipset/Ryzen` returns nothing official) |
| Intel DSA | `Intel.IntelDriverAndSupportAssistant` | ✔ 26.2.0.7 |
| Realtek audio | (winget: none) msstore `9P2B8MCSVPLN` "Realtek Audio Control" | ✔ UI only; the driver comes from WU or the board vendor |
| DDU | `Wagnardsoft.DisplayDriverUninstaller` | ✔ 18.1.5.3, moniker `ddu` |
| NVCleanstall (advanced, optional) | `TechPowerUp.NVCleanstall` | ✔ 1.19.0 |
| Block-list (never offer) | `GlennDelahoy.SnappyDriverInstallerOrigin`, `samlab-ws.SnappyDriverInstaller`, `AMD.AMDSoftwareCloudEdition` | exist; **do not use** |

---

## 4. AMD chipset and Ryzen X3D

**Detection.** Use PnP hardware IDs, not service names. All of the following were checked on this machine ✔:

| Component | HWID | INF (here) | Service |
|---|---|---|---|
| 3D V-Cache Performance Optimizer | `ACPI\VEN_AMDI&DEV_0101` | amd3dvcache.inf (oem4) 1.0.0.9, 2024-04-15 | kernel `amd3dvcache` (Manual, Running) and `amd3dvcacheSvc` «AMD 3D V-Cache Performance Optimizer Service» (Auto) |
| PPM Provisioning File | `ACPI\VEN_AMDI&DEV_0052` | amdppmpf.inf (oem5) 8.0.0.20, attribute *Declarative* | none |
| App Compatibility Database | `ACPI\VEN_AMDI&DEV_0204` | amdappcompat.inf 1.0.0.3 | `AmdAppCompat`, `AmdAppCompatSvc` |
| SMBus / PSP / GPIO / I2C | `PCI\VEN_1022&DEV_790B`, `…DEV_1649`, `ACPI\AMDIF031`, `ACPI\AMDI0010` | smbusamd, amdpsp, amdgpio3, amdi2c | `amdpsp`, `amdgpio3`, `amdi2c` |

- **`AmdPPM` is the inbox Windows "AMD Processor" driver** (cpu.inf). It is always present, so **do not use it to detect the chipset driver.**
- If the AMD package was installed manually, the Uninstall key shows a "AMD Chipset Software" entry. Here everything arrived through WU instead: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching\DriverUpdates\{…}` lists `acpi\amdi0101`, `acpi\amdi0052`, and so on. That key is undocumented; read it only as a hint.
- **Rule:** if the CPU is Ryzen (`Win32_Processor.Name`) and the `AMDI0101`/`AMDI0052` device has code 28 or no oem INF, show «Установите драйвер чипсета AMD» with a link to `https://www.amd.com/en/support/download/drivers.html`, under Chipsets → your socket (AM5 here).
- **Debated: what the optimizer does on X3D.**
  - On **dual-CCD** X3D chips (7900X3D/7950X3D/9900X3D/9950X3D) it steers games onto the V-Cache CCD by parking the other CCD. That requires the chipset driver plus Game Mode and Xbox Game Bar. **The Gaming profile must never disable Game Mode or Game Bar on those CPUs.**
  - On the **single-CCD 9800X3D**, every core has the cache, so the optimizer has nothing to steer. This is an inference; AMD has not said so explicitly. It is harmless, so **don't disable its services.**
  - Recommendation: keep the chipset package current from amd.com or WU, and apply no tweaks.

---

## 5. Driver backup and restore

| | `pnputil /export-driver * <dir>` | `dism /online /export-driver /destination:<dir>` (`Export-WindowsDriver -Online -Destination`) |
|---|---|---|
| Scope | third-party (`oem*.inf`) DriverStore packages; inbox drivers are not exported (not needed) | same |
| Available since | Windows 10 1607 | Windows 8.1 |
| Pros | lightweight, also exports **one** package (`pnputil /export-driver oem14.inf <dir>`) | the PowerShell cmdlet returns objects (OriginalFileName, ClassName, ProviderName, Version, Date), handy for a manifest; logs to `%windir%\Logs\DISM\dism.log` |

- **Recommendation:** use pnputil, and write a JSON manifest from `pnputil /enum-drivers /format xml` (fallback: parse the text). Create the target folder first. Warn the user about size: an NVIDIA package alone is about 1–2 GB.
- One article claims DISM is safer because pnputil doesn't copy `.pnf` files. That claim is **debated or weak**: `.pnf` files are a precompiled cache that Windows regenerates.
- **Restore:** per package, `pnputil /add-driver "<dir>\<package>\<name>.inf" /install`, only for the INFs listed in `monolith-backup.json`. MONOLITH refuses a backup that has unlisted INFs, missing files or links (reparse points) anywhere in its tree. Exit code 0 means success; 3010 means a reboot is required. `/install` installs only if the package outranks the current driver, so it **does not downgrade**.
- **Rollback after a bad update:**
  - Use the documented `DiRollbackDriver` (newdev.dll, `ROLLBACK_FLAG_NO_UI`, out `NeedReboot`), or Device Manager → Roll Back.
  - Before every install, MONOLITH exports the device's current `oemNN.inf` and records {instanceId, old INF and version, new version, restore point} in the Undo Center.
  - The previous package must still be in the DriverStore. So **the Junk Cleaner must never purge `DriverStore\FileRepository`** (Disk Cleanup's "Device driver packages", `pnputil /delete-driver`).
- **Restore points:** create one before driver batches. Windows throttles them to one per 24 h by default (`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\SystemRestorePointCreationFrequency`, default 1440 min). If creation is skipped, tell the user rather than failing silently.

---

## 6. What not to do

- **No third-party driver databases or packs:** Driver Booster, DriverPack, Snappy/SDIO, Driver Easy and the like. They risk wrong HWID matches, modified INFs, cross-signed or old packages, and bundled adware.
- **No driver or registry "cleaners"** and no DriverStore purge. Never use `pnputil /delete-driver … /force`.
- **No forced or downgrading installs** (`INSTALLFLAG_FORCE`).
- **Never touch signing or security:** no `bcdedit /set testsigning on` or `nointegritychecks`, and no bypassing code 48 or code 52.
- **No silent bulk installs** of optional WU drivers or beta drivers, and no installs at boot.
- **No BIOS or firmware flashing from the app.** NVIDIA UEFI/DisplayPort firmware updaters exist in winget; only link to the vendor.
- **Don't disable wuauserv, BITS or TrustedInstaller**, and don't disable WU driver delivery by default.
- **DDU** (`Wagnardsoft.DisplayDriverUninstaller`) is the only driver remover to recommend, and only as a manual action, never automated. The warning dialog should say:
  - create a restore point first;
  - boot into Safe Mode;
  - disconnect from the network;
  - have the new driver downloaded in advance.
  - Also note that DDU's option to stop WU from fetching drivers changes the device-installation setting.
- **NVCleanstall** is shown only in an "Для опытных" (advanced) section.

---

## 7. UX for the Drivers page («Драйверы»)

1. **Status card:** «213 драйверов · проблем: 0 · обновлений от Windows Update: N · Видеокарта: NVIDIA 610.88 → доступна 621.01 (WHQL, 21.10.2026)», plus the last scan time and a «Проверить» button. Never show alarmist "52 outdated drivers" counts or scores.
2. **Tabs:** «Обзор» · «Обновления Windows Update» · «Видеокарта» · «Чипсет» · «Все драйверы» · «Резервные копии».
3. **Problem devices:** each row shows the name, the code with its Russian explanation from the table, the HWID with a copy button, the vendor, and actions («Найти в Windows Update», «Сайт производителя платы», «Включить»).
4. **WU updates list:**
   - Badges «Рекомендуемое» (BrowseOnly=0, pre-checked) and «Необязательное» (BrowseOnly=1, unchecked, tooltip «ставьте только при проблеме с устройством»).
   - Columns: name, class, manufacturer, version, date, size, reboot needed.
   - Install flow:
     1. Confirmation dialog.
     2. Restore point.
     3. Export of the current oem INF.
     4. Download and install with progress and «Отмена».
     5. Per-item result.
     6. Reboot banner.
   - Each row also gets «Скрыть это обновление», which is reversible through the Undo Center.
5. **GPU card per adapter:** vendor logo, installed and latest versions, «Открыть страницу драйвера», «Установить NVIDIA App / Intel DSA» (winget), «AMD: открыть официальный установщик». For a non-primary iGPU, show info only.
6. **Chipset card:** AMD components ✓ or ✗ with versions; on dual-CCD X3D, add «Game Mode и Xbox Game Bar должны быть включены».
7. **Backups:** «Создать резервную копию драйверов» (folder picker, size estimate), a list of backups with their manifests, «Восстановить», and «Откатить драйвер устройства» (DiRollbackDriver).
8. **Robustness:**
   - Every operation is cancellable and has a timeout.
   - Offline mode shows the local inventory only.
   - Without admin rights, the install buttons are disabled with an explanation.
   - Policy-managed machines (WSUS/WUfB, ExcludeWUDriversInQualityUpdate) get an info bar.
   - Every action is written to the Undo Center log.

---

## Sources
- https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-search
- https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nn-wuapi-iwindowsdriverupdate
- https://learn.microsoft.com/en-us/windows/win32/wua_sdk/guidelines-for-asynchronous-wua-operations
- https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdateinstaller-get_isbusy
- https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdateinstaller-begininstall
- https://learn.microsoft.com/en-us/windows/win32/api/newdev/nf-newdev-dirollbackdriver
- https://learn.microsoft.com/en-us/windows-hardware/drivers/install/device-manager-error-messages
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-driver-servicing-command-line-options-s14
- https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3 (live-tested)
- https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php (live-tested, undocumented)
- https://github.com/ZenitH-AT/nvidia-update
- https://www.amd.com/en/support/download/drivers.html
- https://www.amd.com/en/resources/support-articles/release-notes/RN-RYZEN-CHIPSET-7-06-02-123.html
- https://www.intel.com/content/www/us/en/support/detect.html
- https://www.wagnardsoft.com/
- https://hothardware.com/news/amds-chipset-driver-for-v-cache-cpus
- https://www.phoronix.com/review/amd-3d-vcache-optimizer-9950x3d
- https://www.neowin.net/news/amd-finally-fixes-windows-10-vbs-issue-for-ryzen-x3d-but-it-does-not-fix-performance-loss/
- https://www.ninjaone.com/blog/backup-and-restore-device-drivers
- https://www.elevenforum.com/t/check-for-and-install-optional-updates-in-windows-11.10588/