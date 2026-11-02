# Lycoris: device identification and vendor software for peripherals

Scope: how Lycoris can **identify** the user's gaming peripherals read-only (vendor, product name, one row per physical
device), which **vendor app** configures each one, how to detect that app and open it as the signed-in user, and what
Lycoris must **not** do. DPI, polling rate, RGB, button maps, EQ and firmware live in each device's own firmware and are
written only by the vendor's protocol. Windows has no documented API for them, so Lycoris's role is to identify the
device, explain the options and open the right vendor tool.

Every ✔ was checked **read-only** on the dev machine on 2026-10-27 (Win11 Pro 26H2, build 26300). Nothing was changed. No
HID output/feature report was sent. Devices were opened with `CreateFile(..., dwDesiredAccess = 0, ...)` only.

Related code that already exists:
- `Modules/Drivers/Hardware/HardwareCatalog.cs`: `PeripheralVendor`, `VendorSoftware`, 10 VIDs.
- `HardwareAdvisor.Peripherals()`: the «ПЕРИФЕРИЯ» group, one row per **vendor**.
- `HardwareRules.UsbId`, `IsGenericDeviceName`.
- `Modules/Apps/AppCatalog.cs`: G HUB, Synapse 4, SteelSeries GG, iCUE 5, Stream Deck.
- `Core/UnelevatedShell.StartAsUserAsync`.

This report extends those. It does not duplicate them.

---

## 0. Snapshot of the dev machine (read-only checks)

### 0.1 What Windows knows about each device ✔

| Device (owner's description) | USB VID:PID | `DEVPKEY_Device_BusReportedDeviceDesc` (= USB iProduct) | `HidD_GetManufacturerString` / `HidD_GetProductString` | PnP FriendlyName (Win32_PnPEntity.Name) | ContainerId groups it? |
|---|---|---|---|---|---|
| Logitech mouse via LIGHTSPEED | `046D:C54D` | **"USB Receiver"** (composite parent + MI_00/01/02); "LIGHTSPEED Receiver" on 7 `&LAMPARRAY` children | "Logitech" / "USB Receiver" (all 7 TLCs) | "LIGHTSPEED Receiver" (parent; comes from G HUB's INF), children generic | yes `{B7F3C0B6-…}`, but each LampArray child has its **own** container |
| ↳ mouse behind the receiver | (not on USB) | — the receiver does not report it | — | — | — |
| ↳ same mouse, seen earlier by cable | `046D:C0A8` (non-present) | **"PRO X2 SUPERSTRIKE"**, LastArrival 27.10.2026 20:40:54 | — | — | `{A4B6AFDB-…}` |
| ↳ its firmware-update mode | `046D:AB39` (non-present) | "Bootloader for X2 SUPERSTRIKE" | — | — | `{A18349D2-…}` |
| Logitech keyboard | `046D:C339` | **"PRO Gaming Keyboard"** (parent and MI_00/MI_01) | "Logitech" / "PRO Gaming Keyboard", serial 088D… | "PRO" (from G HUB's INF and LampArray child) | yes `{8E8EB054-…}` (LampArray child separate) |
| G HUB virtual mouse / keyboard | `046D:C231` / `046D:C232` | "Logitech_LGVirHid02" / "…01" on `LGHUBDEVICE\…`; the HID child has none | HidD string calls fail with error 1784 | "Logitech G HUB Virtual Mouse/Keyboard", "HID-совместимая мышь" | own containers, parent `LGHUBDevice\…` |
| Sony INZONE E9 | `054C:0F80` | **"INZONE E9"** (parent, MI_00, MI_02) | "Sony" / "INZONE E9" (2 consumer-control TLCs) | "Составное USB устройство" (parent), "INZONE E9" (audio MI_00) | yes `{74FD64AE-…}`, incl. the endpoint «Наушники (INZONE E9)» |
| Razer Seiren V3 Mini | `1532:056A` | **"Razer Seiren V3 Mini"** | "Razer Inc." / "Razer Seiren V3 Mini" | "Razer Seiren V3 Mini" (audio + HID), parent "Составное USB устройство" | yes `{74FD64AF-…}`, incl. «Микрофон (Razer Seiren V3 Mini)» |
| MSI USB device | `0DB0:0076` | **"MYSTIC LIGHT"** | "MSI" / "MYSTIC LIGHT", UsagePage 0xFF00 (vendor) | "USB-устройство ввода" | **no**: `{00000000-0000-0000-FFFF-FFFFFFFFFFFF}` = local-machine container. `InLocalMachineContainer=True`, `RemovalPolicy=1` (internal motherboard USB header) |
| Monitors | (display) | — | — | "Generic Monitor (X27U W3)", "(Odyssey G5)" | own containers. HDMI/DP audio endpoints share them |

So a correct product name was available read-only for **every** physical USB device here. The one gap is the model of
the mouse behind the LIGHTSPEED receiver: Windows only sees the receiver. G HUB learns the model through Logitech's
proprietary HID++ protocol (§2.5).

### 0.2 Vendor software found ✔

| App | Detected by | Version | Running now | Autostart | Launch target (signature) |
|---|---|---|---|---|---|
| Logitech G HUB | HKLM Uninstall `{521c89be-…}` DisplayName "Logitech G HUB", Publisher "Logitech", DisplayIcon `C:\Program Files\LGHUB\lghub.exe` | 2026.7.992920 | `lghub_agent`, `lghub_system_tray`; services `LGHUBUpdaterService`, `logi_lamparray_service` (Auto, Running) | HKCU Run `LGHUB = "…\system_tray\lghub_system_tray.exe" --minimized` | Start-menu `Logi\Logitech G HUB.lnk` → `C:\Program Files\LGHUB\system_tray\lghub_system_tray.exe`. URI `lghubapp:` → `lghub.exe -- %1`. Signer **"Logitech Inc"** (Valid) |
| Razer Synapse 4 | HKLM **WOW6432Node** Uninstall `Razer Synapse`, Publisher "Razer Inc." | 4.0.503 | no (service `Razer Elevation Service` Manual/Stopped) | — | `Razer\Razer Synapse.lnk` → `C:\Program Files\Razer\RazerAppEngine\RazerAppEngine.exe --url-params=apps=synapse`. URI `RazerAppEngine.synapse:`. Signer **"Razer USA Ltd."** (Valid) |
| Sony INZONE Hub | HKLM Uninstall `{7FB673E2-…}` DisplayName "INZONE Hub", Publisher "Sony Corporation", InstallLocation `C:\Program Files\Sony\INZONE Hub\` | 1.0.20.0 | `INZONEHub.exe` | All-users Startup folder `INZONE Hub.lnk … /startup` | `INZONE Hub.lnk` → `C:\Program Files\Sony\INZONE Hub\INZONEHub.exe`. Signer **"Sony Corporation"** (Valid) |
| OpenRGB | HKLM Uninstall `{F489BC39-…}` (MSI package, no InstallLocation) | 1.1.0 | no | — | per-user shortcut → `C:\Program Files\OpenRGB\OpenRGB.exe`, **NotSigned**. No PawnIO/WinRing0 driver installed |
| MSI Center (Mystic Light) | not installed | — | — | — | — |

Drivers that came with this software ✔:
- `logi_lamparray` (G HUB's Virtual HID Framework driver that adds the `&LAMPARRAY` children).
- `logi_joy_vir_hid`, `logi_joy_bus_enum` and `logi_joy_xlcore` (the G HUB virtual devices).
- Sony's audio INF `oem30.inf` 1.0.34.72 on the E9 audio interface. The stack is still Microsoft `usbaudio`.

### 0.3 What the current «ПЕРИФЕРИЯ» group would show here (code reading, not run)

- **Logitech row.** Title "PRO · LIGHTSPEED Receiver". The id list is "046D:C231, 046D:C232, 046D:C339, 046D:C54D".
  - `UsbIdRegex` (`^(?:USB|HID)\\VID_…`) also matches `HID\VID_046D&PID_C231` and `…C232`, the HID children of the G HUB
    **virtual** devices. Classes Mouse/Keyboard are in `PeripheralClasses`, so the two virtual devices are counted as
    hardware.
  - Without G HUB's INF, the names would all be generic and the title would fall back to "N устройств Logitech". The
    bus-reported names "PRO Gaming Keyboard" / "USB Receiver" are not used today.
- **Sony row.** "INZONE E9", INZONE Hub offered. Correct (`SoftwareNameFilter = "INZONE"`).
- **Razer row.** "Razer Seiren V3 Mini", Synapse 4. Correct.
- **MSI `0DB0:0076`** goes to the footer as an "unknown vendor". 0DB0 is not in `Peripherals`, so there is no hint about
  MSI Center / Mystic Light.
- **No row can open an installed vendor app.** Actions are install-only.

---

## 1. Summary

| id | Russian UI title | Class | Admin | Mechanism (short) |
|---|---|---|---|---|
| `device-inventory` | Список подключённых устройств | INFO-ONLY | no | CfgMgr32 `CM_Get_Device_ID_List(USB/HID/BTHENUM/BTHLE, PRESENT)` + `CM_Get_DevNode_PropertyW`. 47 devnodes in 7 ms ✔ |
| `device-product-name` | Название модели устройства | INFO-ONLY | no | `DEVPKEY_Device_BusReportedDeviceDesc` → `HidD_GetProductString` (access 0) → container name → PID table |
| `device-grouping` | Одна строка на устройство | INFO-ONLY | no | `DEVPKEY_Device_ContainerId`, with a parent walk for the local-machine container and LampArray/virtual children |
| `device-virtual-filter` | Скрывать виртуальные устройства | INFO-ONLY | no | Drop devnodes whose parent chain is not `USB\`/`BTHENUM\`/`BTHLE\`, plus `&LAMPARRAY` children |
| `device-kind` | Тип устройства | INFO-ONLY | no | HID TLC UsagePage/Usage (`HidP_GetCaps`), PnP class, audio endpoints in the same container |
| `receiver-paired-hint` | Мышь за приёмником | INFO-ONLY | no | PID table for receivers + **non-present** devnodes of the same VID with a bus-reported name ("ранее по кабелю") |
| `device-serial-privacy` | Серийные номера не показываются | SAFE-DEFAULT | no | Never display/log/export `iSerialNumber` / instance-id tails |
| `vid-vendor-map` | Производитель по VID | INFO-ONLY | no | VID table §3. For brands on generic chip VIDs, match the iManufacturer/iProduct strings instead |
| `vendor-app-detect` | Установлена ли программа производителя | INFO-ONLY | no | Uninstall keys (HKLM, HKLM\WOW6432Node, HKCU) + Start-menu .lnk + AppX packages |
| `vendor-app-launch` | Открыть программу производителя | SAFE-OPTIONAL | no | `UnelevatedShell.StartAsUserAsync(target)`. Target from a fixed table + signature check |
| `vendor-app-install` | Установить программу производителя | SAFE-OPTIONAL | yes (machine scope) | Existing Apps/winget path. New ids in §4 |
| `onboard-memory-advice` | Сохранить профиль в память мыши | INFO-ONLY | no | Text + "open vendor app". Lycoris never writes the profile |
| `vendor-autostart-advice` | Что будет без автозапуска программы | INFO-ONLY | no | Startup page note for G HUB / Synapse / INZONE Hub / iCUE / GG entries |
| `dynamic-lighting-detect` | Подсветка через Windows (Dynamic Lighting) | INFO-ONLY | no | LampArray devnodes / `Windows.Devices.Lights.LampArray.GetDeviceSelector()`. Opens `ms-settings:personalization-lighting` |
| `rgb-unifier-apps` | OpenRGB / SignalRGB | ADVANCED | yes | Apps catalog only, flagged expert + conflicts. Never auto-suggested |
| `vendor-protocol-writes` | Менять DPI/частоту/подсветку напрямую | **DO-NOT-INCLUDE** | — | HID++/Razer/SteelSeries feature reports: undocumented, conflicts with vendor apps, can corrupt onboard profiles |
| `vendor-protocol-reads` | Читать имя мыши через HID++ | **DO-NOT-INCLUDE** | — | Requires sending output reports (a write) into a device G HUB owns |
| `firmware-updates` | Обновлять прошивки устройств | **DO-NOT-INCLUDE** | — | Bricking risk. Vendor tools only (link out) |
| `usb-polling-overclock` | «Разгон» частоты опроса USB (hidusbf и т.п.) | **DO-NOT-INCLUDE** | — | Patched/unsigned driver, test-signing, anti-cheat |
| `lowlevel-rgb-drivers` | WinRing0 / inpout / PawnIO | **DO-NOT-INCLUDE** | — | Kernel drivers with arbitrary port/SMBus access. WinRing0 is flagged by Defender |
| `vendor-debloat` | Удалять службы/драйверы G HUB, Synapse… | **DO-NOT-INCLUDE** | — | Breaks the devices' features. Uninstall goes through the vendor uninstaller only |
| `ghost-device-removal` | Удалять «призрачные» устройства | **DO-NOT-INCLUDE** | — | Removes the history used for names and restore. `pnputil /remove-device` is irreversible |

---

## 2. Identification pipeline

### 2.1 `device-inventory`: enumerate devnodes (INFO-ONLY)

**Mechanism (read).**
- `CM_Get_Device_ID_List_SizeW` / `CM_Get_Device_ID_ListW(pszFilter: "USB" | "HID" | "BTHENUM" | "BTHLE" | "BTHLEDevice", ulFlags: CM_GETIDLIST_FILTER_ENUMERATOR (0x1) | CM_GETIDLIST_FILTER_PRESENT (0x100))`.
- Then `CM_Locate_DevNodeW(out devInst, id, CM_LOCATE_DEVNODE_NORMAL)`.
- Then `CM_Get_DevNode_PropertyW(devInst, ref DEVPROPKEY, out type, buf, ref size, 0)`.
- Measured ✔: 47 USB+HID devnodes with 4 properties each in **7 ms** from PowerShell `Add-Type`.
- The current scanner's WMI path (`Win32_PnPEntity`) works too. `Win32_PnPEntity.GetDeviceProperties(["DEVPKEY_Device_BusReportedDeviceDesc", …])` returned the same strings ✔, but each call is one WMI round-trip per device. Use CfgMgr32 for the device list and keep WMI as the fallback.
- To find non-present devnodes (history), omit `CM_GETIDLIST_FILTER_PRESENT` and read `DEVPKEY_Device_IsPresent`.

**Property keys used** (fmtid, pid, all from devpkey.h):

| Key | fmtid | pid | type |
|---|---|---|---|
| `DEVPKEY_Device_BusReportedDeviceDesc` | `540b947e-8b40-45bc-a8a2-6a0b894cbda2` | 4 | STRING |
| `DEVPKEY_Device_ContainerId` | `8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c` | 2 | GUID |
| `DEVPKEY_Device_Parent` | `4340a6c5-93fa-4706-972c-7b648008a5a7` | 8 | STRING |
| `DEVPKEY_Device_FriendlyName` | `a45c254e-df1c-4efd-8020-67d146a850e0` | 14 | STRING |
| `DEVPKEY_Device_DeviceDesc` | same | 2 | STRING |
| `DEVPKEY_Device_Class` | same | 9 | STRING |
| `DEVPKEY_Device_Service` | same | 6 | STRING |
| `DEVPKEY_Device_Manufacturer` | same | 13 | STRING |
| `DEVPKEY_Device_InLocalMachineContainer` | `8c7ed206-…` | 4 | BOOLEAN |
| `DEVPKEY_Device_IsPresent` | `540b947e-…` | 5 | BOOLEAN |
| `DEVPKEY_Device_LastArrivalDate` | `83da6326-97a6-4088-9453-a1923f573b29` | 102 | FILETIME |
| `DEVPKEY_Device_ProblemCode` | `4340a6c5-…` | 3 | UINT32 |

**Admin:** no. **Risk:** none. The calls read the PnP database only and send no I/O to the device. **Revert:** nothing to
revert.

Evidence:
- DEVPKEY_Device_BusReportedDeviceDesc is set by PnP from the bus driver's `IRP_MN_QUERY_DEVICE_TEXT` and is readable with `CM_Get_DevNode_Property` / `SetupDiGetDeviceProperty`, Windows 7+ (https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-busreporteddevicedesc).
- CM_Get_Device_ID_List (https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_device_id_listw).
- CM_Get_DevNode_Property (https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_propertyw).
- Win32_PnPEntity.GetDeviceProperties (https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/getdeviceproperties-win32-pnpentity).

### 2.2 `device-product-name`: the model name (INFO-ONLY)

**Priority, best first:**
1. `DEVPKEY_Device_BusReportedDeviceDesc` of the **USB device node** (the composite parent `USB\VID_x&PID_y\<instance>`, not the `&MI_xx` / `HID\…` children). This is the device's own USB iProduct string, cached by PnP. It does no device I/O, so it cannot wake a sleeping Bluetooth device or block. ✔ "PRO Gaming Keyboard", "INZONE E9", "Razer Seiren V3 Mini", "MYSTIC LIGHT", "USB Receiver".
2. `HidD_GetProductString` / `HidD_GetManufacturerString` on any HID interface of the group. Open it with `CreateFileW(path, 0 /*no access*/, FILE_SHARE_READ|FILE_SHARE_WRITE, null, OPEN_EXISTING, 0, 0)`.
   - Zero access is allowed even on keyboard/mouse collections, which the system opens exclusively ✔.
   - The interface paths come from `CM_Get_Device_Interface_ListW(GUID_DEVINTERFACE_HID = HidD_GetHidGuid(), deviceId, CM_GET_DEVICE_INTERFACE_LIST_PRESENT)`.
   - Use it as a fallback for Bluetooth HID, where the bus-reported description may be empty. Call it on a worker thread with a timeout, because on BT the request goes to the device.
   - The buffer is UTF-16, ≤ 4093 bytes (126 chars for USB).
   - Error 1784 means the device has no string (G HUB virtual devices ✔). Treat it as "no name".
3. The container name. Windows.Devices.Enumeration `DeviceInformation.FindAllAsync("", props, DeviceInformationKind.DeviceContainer)` returned "INZONE E9", "PRO", "Razer Seiren V3 Mini" and "PRO X2 SUPERSTRIKE" ✔.
   - Available through the project's TFM `net10.0-windows10.0.19041.0` projections, so no NuGet is needed.
   - It is slower and asynchronous. Use it only if 1 and 2 are empty.
4. The PID table (§3) for known receivers and docks ("USB Receiver" → «Приёмник LIGHTSPEED»).
5. The vendor name plus the kind: «Мышь Logitech», «USB-аудио Razer».

**Generic iProduct strings** that must fall through to step 4 or 5:
- "USB Receiver", "Wireless Receiver", "2.4G Receiver", "USB Audio Device", "USB Composite Device", "Gaming Mouse",
  "Gaming Keyboard", "Wireless Controller", "USB Device", "HID Device".
- Plus the existing `HardwareRules.GenericNameMarkers`.

**Do not use `FriendlyName`/`DeviceDesc`** for the model:
- On Russian Windows they are localized class names («Составное USB устройство», «HID-совместимая мышь»).
- On this PC G HUB's INF renamed the Logitech parents to "PRO" / "LIGHTSPEED Receiver". Without G HUB installed those
  names would not exist.
- The bus-reported string is not localized ("Localized? No") and does not depend on vendor software.

**Admin:** no. **Risk:** none (read). **Revert:** none.

Evidence:
- HidD_GetProductString (https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getproductstring).
- Opening HID collections: SetupDi/CM to find, CreateFile to open, HidD_* for information (https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/opening-hid-collections).
- "The system opens all keyboard and mouse collections for its exclusive use" (https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/keyboard-and-mouse-hid-client-drivers). Zero-access opens still answered HidD_Get*String on both the keyboard (UP 0x01/U 0x06) and mouse (UP 0x01/U 0x02) TLCs here ✔.

### 2.3 `device-grouping`: collapse many HID collections into one row (INFO-ONLY)

**Why.** One physical device yields many devnodes:
- The keyboard `046D:C339`: USB parent + `MI_00` + `MI_01` + HID `MI_00` + `MI_01&COL01..COL04` + a `&LAMPARRAY` child = **9 devnodes**.
- The receiver `C54D`: 1 + 3 + 6 HID + 7 LampArray children = **17**.

Microsoft's hardware-ID formats:
- `&MI_zz` is the USB interface number of a composite device.
- `&COLbb` is the top-level collection number.

**Rule.**
1. Group by `DEVPKEY_Device_ContainerId`. All functions of one removable physical device share it ✔, including the
   audio endpoint `SWD\MMDEVAPI\…` («Наушники (INZONE E9)» has the E9's container ✔).
2. **Exceptions found on this PC:**
   - **Local-machine container `{00000000-0000-0000-FFFF-FFFFFFFFFFFF}`** (`DEVPKEY_Device_InLocalMachineContainer = TRUE`). Internal devices land here, e.g. the MSI `MYSTIC LIGHT` controller on a motherboard USB header, and internal hubs. Never merge by this GUID. Group by the USB device node instead: walk `DEVPKEY_Device_Parent` up from `HID\…`/`USB\…&MI_xx` until the instance id matches `^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\` without `&MI_`/`&LAMPARRAY`.
   - **NULL_GUID / no container.** Same treatment as the local-machine container (MS: "the device must not be part of any container").
   - **`&LAMPARRAY` children** (G HUB's VHF driver `logi_lamparray`, `LocationInfo = "LampArray Support Virtual Device"`) get their **own** containers ✔. That is why Windows' container list shows 7 extra "LIGHTSPEED Receiver" entries. Attach them to their parent USB node, do not show them as devices, and use them only as the "supports Dynamic Lighting" flag (§7).
   - **Vendor virtual devices** (`LGHUBDEVICE\…` and their `HID\VID_046D&PID_C231/C232` children) have their own containers. Drop them (§2.4).
3. Container order alone does not give a name. Pick the name per §2.2 from the group's USB device node.
4. **Fallback when no USB node exists** (Bluetooth): group by container. BT HID ids carry the vendor, e.g. `…_VID&0002046D_PID&B023` (classic) or `…_VID&02046d_PID&b023` (LE). The existing `BluetoothIdRegex` already parses both.

**Admin:** no. **Risk:** none. **Revert:** none.

Evidence:
- Container IDs group the devnodes of one physical device. NULL_GUID means "not part of any container" (https://learn.microsoft.com/en-us/windows-hardware/drivers/install/overview-of-container-ids).
- How they are generated: bus-supplied, from the removable capability, or from an override (https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-container-ids-are-generated).
- HID hardware-ID formats `HID\Vid_v&Pid_d&MI_z&Colb` and the special `HID_DEVICE_SYSTEM_*` ids (https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/hidclass-hardware-ids-for-top-level-collections).

### 2.4 `device-virtual-filter` (INFO-ONLY)

Drop a devnode when either condition holds.

**(a) Its parent chain does not reach a real bus enumerator** (`USB\`, `BTHENUM\`, `BTHLE\`, `BTHLEDevice\`, `PCI\`, `HDAUDIO\`).

Verified virtual roots ✔:

| Root | Parent example / source |
|---|---|
| `LGHUBDEVICE\VID_046D&PID_C231/C232` | parent `LGHUBDevice\…`, service `logi_joy_vir_hid` |
| `ROOT\…` | — |
| `SWD\…` | except MMDEVAPI endpoints, which are only used to attach audio to a group |
| `HID\…` | when its parent is one of the above |

**(b) Its instance id contains `&LAMPARRAY\`, or `DEVPKEY_Device_Stack` contains `\Driver\logi_lamparray`.**

Also skip:
- `USB\ROOT_HUB*`, hubs (class `USB` with service `USBHUB3`/`usbhub`), host controllers.
- Mass storage (`USBSTOR`, `UASPStor`).
- Bluetooth radios (already handled in Network).

This fixes the current `HardwareRules.UsbId` false positive on `HID\VID_046D&PID_C231/C232` (§0.3).

### 2.5 `device-kind` and `receiver-paired-hint` (INFO-ONLY)

**Kind of device.** Read `HidD_GetPreparsedData` + `HidP_GetCaps` per HID interface (zero-access handle) ✔:

| TLC UsagePage / Usage | Kind | Seen on this PC |
|---|---|---|
| `0x01/0x02` | mouse | C54D MI_00 |
| `0x01/0x06` | keyboard | C339, C54D MI_01 COL01 (a receiver also exposes a keyboard TLC for macros) |
| `0x01/0x04`, `0x01/0x05` | joystick / gamepad | — |
| `0x01/0x80` | system control | C54D COL03 |
| `0x0C/0x01` | consumer control (media keys, headset buttons) | E9, Seiren, keyboards |
| `0x59/*` | LampArray (Dynamic Lighting) | — (G HUB's LampArray is a separate VHF node) |
| `0xFF00–0xFFFF` | vendor-defined (the vendor app's config channel) | C54D MI_02, C339 COL03/04 (`0xFF43`), MYSTIC LIGHT |

- **Audio:** a `MEDIA` class node with service `usbaudio`/`usbaudio2` (or a vendor INF on top) plus render/capture endpoints in the same container → «наушники» / «микрофон».
- **Xbox controllers:** PnP class `XnaComposite` / `XboxComposite`. **Not verified here** (no controller attached). Re-check on a PC with one before relying on it.
- **Decision order:**
  1. PID table (receivers, docks).
  2. Audio endpoints in the container. Render plus capture means "headset"; capture only means "microphone".
  3. HID usages, in priority order: gamepad > mouse > keyboard. A receiver exposes mouse + keyboard, which is why rule 1 comes first.
  4. Consumer-only → "устройство управления".
  5. Vendor-only TLC → "контроллер подсветки/устройство производителя".

**Receivers.** A wireless receiver reports itself ("USB Receiver"), not the paired device. Windows has no documented way
to name the paired mouse. Logitech's HID++ can, but that needs output reports (§8).

What Lycoris can do honestly:
- Show «Приёмник LIGHTSPEED (Logitech)» from the PID table.
- **History hint:** list non-present devnodes with the same VID, a mouse/keyboard kind and a bus-reported name, whose
  `DEVPKEY_Device_LastArrivalDate` falls within the last 180 days. Here ✔: `046D:C0A8` "PRO X2 SUPERSTRIKE", last
  arrival 27.10.2026.
- Show it as «Раньше по кабелю подключалась: PRO X2 SUPERSTRIKE». Never claim it is the device behind the receiver.
- Ignore `Bootloader for …` entries (firmware-update mode, `046D:AB39` ✔). They only confirm the model.

Evidence: Logitech receiver PIDs, and Logitech's statement that receivers are 0xC526–0xC5xx and HID++ 2.0 wired devices
are 0xC07D–0xC094 / 0xC32B–0xC344:
- https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/base_usb.py
- Linux hid-ids.h `USB_DEVICE_ID_LOGITECH_*RECEIVER*` (https://github.com/torvalds/linux/blob/master/drivers/hid/hid-ids.h)

### 2.6 `device-serial-privacy` (SAFE-DEFAULT)

USB serials (iSerialNumber: `088D34643436` keyboard, `375C32603335` receiver, `7E4724092002` MSI ✔) are unique hardware
identifiers. They also appear as the tail of USB instance ids.

Rules:
- Never show them in the UI.
- Never write them to the log or the in-app console.
- Never put them into exported profiles or self-test reports.
- Use them only in memory, as grouping keys.

Revert: n/a.

---

## 3. VID → vendor / product family table

Rules:
- Match VID first.
- For brands that ship on a **chip vendor's VID** (marked *generic*), the VID proves nothing. Match the iManufacturer /
  iProduct strings from §2.2 instead, e.g. "Glorious", "Pulsar", "LAMZU", "VAXEE", "Endgame Gear".
- PID families are hints for naming receivers and docks only. Everything else is named by the device's own string.

| VID | Vendor (UI name) | Important PIDs / families | Source |
|---|---|---|---|
| `046D` | Logitech | **Receivers**: C52B/C532 Unifying, C52F/C534/C526/C531/C537… Nano, C548 **Bolt**, **LIGHTSPEED C539, C53A, C53D, C53F, C541, C545, C547, C54D** (C54D ✔). **G HUB virtual** C231 mouse / C232 keyboard (`LGHUBDEVICE\`, ✔). Wired gaming mice C07D–C09x+ (C08B G502 SE HERO, C088 G Pro Wireless wired, C08D G502 LIGHTSPEED, C094 PRO X Wireless, C095 G502 X PLUS), **C0A8 PRO X2 SUPERSTRIKE** ✔, AB39 its bootloader ✔. Keyboards C33x–C34x (C336 G213, C339 **PRO Gaming Keyboard** ✔, C33A G413, C33C G512, C33E G915, C33F G815, C343 G915 TKL). Headsets 0Axx (0A66 G533, 0A87 G935, 0AAA PRO X, 0AB5 G733, 0ABA PRO X Wireless, 0AC4 G535). Bluetooth B0xx/B3xx | Solaar base_usb.py + descriptors.py, usb.ids, libratbag, this PC |
| `1532` | Razer | Mice mostly 00xx, keyboards 02xx, audio/mics 05xx (**056A Seiren V3 Mini** ✔). Observed pattern, not a rule | usb.ids, this PC |
| `1038` | SteelSeries | 1720 Rival 310, 1260 Arctis 7 adapter, many others | usb.ids, libratbag |
| `1B1C` | Corsair | 0C0B Lighting Node Pro, 1Bxx keyboards/mice | usb.ids |
| `0951` | HyperX (Kingston era) | 16C4 Cloud Flight, 16D2 Alloy FPS Pro, 16E4 Pulsefire Raid | usb.ids |
| `03F0` | HP / HyperX (2021+) | same VID as HP keyboards/mice. Require "HyperX" in iProduct before suggesting NGENUITY | usb.ids, HardwareCatalog |
| `054C` | Sony | **INZONE**: 0F80 E9 ✔ (others: match "INZONE" in the name). DualShock 4 05C4/09CC, DS4 adaptor 0BA0, **DualSense 0CE6**, **DualSense Edge 0DF2** | usb.ids, Linux hid-ids.h, this PC |
| `0DB0` | MSI | **0076 "MYSTIC LIGHT"** motherboard RGB controller ✔ (internal header, local-machine container). Other 0DB0 ids are Wi-Fi/BT modules | Linux hid-ids.h `USB_VENDOR_ID_MSI_2`, usb.ids, this PC |
| `1462` | MSI | also used by MSI. Laptop LED panels use `1770` | usb.ids, hid-ids.h |
| `0B05` | ASUS (ROG) | 1ACE ROG Omni Receiver, 17C2 ROG Spitfire, many Wi-Fi ids (filter by class) | libratbag, usb.ids |
| `31E3` | Wooting | all current keyboards (legacy Wooting One: `03EB:FF01`) | Wooting udev help article |
| `3434` | Keychron | QMK/VIA keyboards | QMK `keyboards/keychron/*/info.json` `"vid": "0x3434"` |
| `1AF3`, `3057` | ZOWIE (BenQ), via "Kingsis" | 1AF3:0001 / 3057:0002 "ZOWIE Gaming mouse" | usb.ids |
| `3554` | Pulsar (Linux names it PULSAR) | F507 wired, F508 1 kHz, F509 4 kHz dongle. Other brands reuse this VID, so check the string | Linux hid-ids.h |
| `373B` | ATK (Linux `ATK_ALT`) | also used by other "8K dongle" brands, so check the string | Linux hid-ids.h |
| `258A` | *generic* SinoWealth | Glorious Model O (0036) / Model D (0033) wired V1, many no-name mice | libratbag |
| `093A`, `1915`, `04D9`, `0C45`, `1A86` | *generic* Pixart, Nordic, Holtek, Microdia, QinHeng | used by countless brands (Lamzu, VAXEE, Endgame Gear, budget mice) | usb.ids |
| `045E` | Microsoft / Xbox | 028E Xbox 360, 02EA One S (USB), 02FD One S (BT), 0B12 Series X\|S (USB), 0B13/0B20/0B22 BLE, 0B05 Elite 2 (BT) | usb.ids, Linux hid-ids.h |
| `28DE` | Valve (Steam Controller / Deck) | 1102, 1142 | usb.ids |
| `057E` | Nintendo | 2009 Switch Pro Controller | usb.ids |
| `2DC8` | 8BitDo | 6000/6001 SF30/SN30 Pro… | usb.ids |
| `0E6F` / `24C6` / `0F0D` / `0738` / `146B` / `044F` | PDP / PowerA (Thrustmaster-registered) / Hori / Mad Catz / BigBen / Thrustmaster | Xbox-licensed pads/sticks | usb.ids |
| `0FD9` | Elgato | Stream Deck 0060/0063/006C/006D | usb.ids |
| `1E7D` | ROCCAT (Turtle Beach) | Kone series | usb.ids, libratbag |
| `10F5` / `9886` | Turtle Beach / Astro | headsets | usb.ids |
| `1E71` / `2516` / `1EA7` / `357D` | NZXT / Cooler Master / Sharkoon / Sharkoon | — | usb.ids |
| `041E` / `1377` / `14ED` / `19F7` / `0909` / `1235` | Creative / Sennheiser / Shure / RØDE / Audio-Technica / Focusrite | USB audio. Usually no app needed | usb.ids |
| `046A` | CHERRY | keyboards | usb.ids |
| `05E3` | *generic* Genesys hub | not a peripheral (the Seiren hangs off one here ✔) | usb.ids |

Sources: Linux USB ID repository mirror https://github.com/usbids/usbids (usb.ids); Linux `drivers/hid/hid-ids.h`
(https://github.com/torvalds/linux/blob/master/drivers/hid/hid-ids.h); libratbag device files
(https://github.com/libratbag/libratbag/tree/master/data/devices); Wooting udev rules
(https://help.wooting.io/article/147-configuring-device-access-for-wootility-under-linux-udev-rules); QMK Keychron
(https://github.com/qmk/qmk_firmware/tree/master/keyboards/keychron).

**Vendors with no verified own VID:** Lamzu, VAXEE, Endgame Gear, newer Glorious (libratbag lists `3794:A000` for the
Model O Eternal; the owner of that VID is unverified). Identify them by the iManufacturer / iProduct string only.

---

## 4. Vendor software matrix

"Apps" means `Modules/Apps/AppCatalog.cs`. "HW" means `Modules/Drivers/Hardware/HardwareCatalog.cs`.

| Vendor | App | Source (verified) | In Apps / HW | Install detection (read) | Launch target → signer | Onboard memory / without the app |
|---|---|---|---|---|---|---|
| Logitech G | **G HUB** | winget `Logitech.GHUB` (+ VendorInstaller download01.logi.com) | ✔ / ✔ | Uninstall DisplayName "Logitech G HUB", Publisher "Logitech" ✔ | `C:\Program Files\LGHUB\system_tray\lghub_system_tray.exe` (Start-menu `Logi\Logitech G HUB.lnk`) ✔ → "Logitech Inc" ✔ | Mice: G HUB «On-Board Memory mode» writes DPI/report rate/buttons to the mouse and they work without G HUB. In software mode, settings apply only while G HUB runs |
| Logitech G | **Onboard Memory Manager** | winget `Logitech.OnboardMemoryManager` 2.6.1749, **portable**, download01.logi.com, publisher Logitech ✔ | — / — | portable (no Uninstall entry) | — | For pros who want onboard settings without G HUB. **Add to HW as an optional alternative for Logitech mice** |
| Logitech (office) | Logi Options+ | winget `Logitech.OptionsPlus` (publisher Logitech) ✔ | — / ✔ | DisplayName "Logi Options+" | — | MX/ERGO devices. G HUB does not configure them |
| Razer | **Synapse 4** | winget `RazerInc.RazerInstaller.Synapse4` | ✔ / ✔ | **WOW6432Node** Uninstall `Razer Synapse`, Publisher "Razer Inc.", DisplayVersion `4.x` ✔ (`3.x` = Synapse 3, `RazerInc.RazerInstaller.Synapse3`, legacy devices) | `C:\Program Files\Razer\RazerAppEngine\RazerAppEngine.exe` + fixed args `--url-params=apps=synapse` ✔ → "Razer USA Ltd." ✔ | Mice/keyboards have onboard profiles. Seiren V3 Mini works without Synapse. Synapse adds sample-rate/mixer settings (razer.com) |
| SteelSeries | **GG** (ex-Engine; Sonar) | winget `SteelSeries.GG` | ✔ / ✔ | DisplayName "SteelSeries GG"/"SteelSeries Engine" | not installed here: resolve from its Start-menu .lnk, signer "SteelSeries ApS" (to verify on a PC with GG) | most devices store settings on the device |
| Corsair | **iCUE 5** | winget `Corsair.iCUE.5` | ✔ / ✔ | DisplayName contains "iCUE" | not installed here: resolve from .lnk, signer "Corsair Memory, Inc." (to verify) | hardware lighting/profiles on device; software lighting needs iCUE |
| HyperX | **NGENUITY** | Microsoft Store **9P1TBXR6QDCX** (official listing) | — / ✔ (URL only) | AppX package name contains "NGENUITY" (`PackageManager.FindPackagesForUser("")`, read-only) | `shell:AppsFolder\<AUMID>` via `UnelevatedShell` | **Add StoreId 9P1TBXR6QDCX to `HardwareCatalog.Ngenuity`** |
| Sony INZONE | **INZONE Hub** | sony.com support page only (no winget; `penguinwokrs.OpenInzone` is third-party and must not be offered) | — / ✔ | Uninstall DisplayName "INZONE Hub", Publisher "Sony Corporation" ✔ | `C:\Program Files\Sony\INZONE Hub\INZONEHub.exe` ✔ → "Sony Corporation" ✔ | E9: EQ, spatial sound, dynamic range and profiles via the USB-C audio box with INZONE Hub (Sony help guide). Firmware updates through Hub |
| Sony PlayStation | **PlayStation Accessories** | winget `PlayStation.PlayStationAccessories` 2.2.1.2, publisher Sony Interactive Entertainment, installer fwupdater.dl.playstation.net ✔ | — / — | DisplayName "PlayStation Accessories" | from .lnk (to verify) | DualSense / Edge firmware, battery, settings. **Add for VID 054C PID 0CE6/0DF2** (today HW says "no software needed") |
| Microsoft / Xbox | **Xbox Accessories** | Microsoft Store **9NBLGGH30XJ3** | — / — | AppX "Microsoft.XboxDevices" (name to verify) | `shell:AppsFolder` | controller firmware + remap. **Add for 045E Xbox PIDs** |
| MSI | **MSI Center** (Mystic Light module) | winget `MSI.MSICenter` 2.0.72, publisher Micro-Star International, installer `download-2.msi.com/uti_exe/desktop/MSI-Center.zip` (**unversioned** zip link, like G HUB's) | — / — | Uninstall "MSI Center SDK" + AppX "MSI Center" (to verify) | AppX via `shell:AppsFolder` | lighting state is kept by the controller. **Add VID 0DB0 (PID 0076 "MYSTIC LIGHT") → MSI Center, link-first** (installs services/SDK; unversioned link → likely hash mismatches) |
| ASUS | **Armoury Crate** | winget `Asus.ArmouryCrate` exists (ArmouryCrateInstallTool.zip, dlcdnets.asus.com) | — / ✔ (link only, by design) | DisplayName "Armoury Crate" / AppX "ArmouryCrate" | AppX | keep link-only (installer adds several services) |
| Wooting | **Wootility** | wooting.io/wootility (no winget) | — / ✔ | DisplayName "Wootility" | per-user Electron app (to verify) | profiles stored on the keyboard |
| Glorious | **CORE 2.x** | gloriousgaming.com/pages/software (no winget). Not for GMMK 1 / Model O/D wired V1 | — / — | DisplayName "Glorious CORE" (to verify) | — | needed for >1 kHz polling on 4K/8K PRO mice |
| Keychron | **Keychron Launcher** (web, WebHID) / VIA | launcher.keychron.com | — / — | none (web) | `UnelevatedShell.OpenAsync(url)` | settings stored on the keyboard (QMK) |
| Pulsar | Pulsar Fusion (desktop) / web driver beta bbb.pulsar.gg | pulsar.gg download page, support.pulsar.gg | — / — | DisplayName "Pulsar" (to verify) | web: `OpenAsync` | onboard |
| VAXEE | none (on-device buttons). 4K models: WebHID web driver | reviews (TechPowerUp, RTINGS) | — / — | — | — | everything on the mouse |
| ZOWIE | **driverless** by design | zowie.benq.com (EC-CW, U2: "driverless; plug and play") | — / — | — | — | everything on the mouse (bottom switches) |
| Endgame Gear, Lamzu | per-model configurator (desktop or web) | not verified | — / — | — | — | onboard |
| Third-party RGB | OpenRGB (`OpenRGB.OpenRGB`), SignalRGB (`WhirlwindFX.SignalRgb`) | winget ✔ | — / — | Uninstall "OpenRGB" ✔ (installed here, **unsigned exe**) | do **not** offer a launch button for unsigned exes | see §6 `rgb-unifier-apps` |

Evidence:
- winget manifests read with `winget show --id … -e --source winget` on 2026-10-27 ✔.
- G HUB On-Board Memory mode: https://support.logi.com/hc/en-gb/articles/6505257646743-Enable-On-Board-Memory-mode-on-your-gaming-mouse-with-Logitech-G-HUB. Settings are carried to another PC "without needing to install G HUB".
- Logitech OMM page: https://support.logi.com/hc/en-us/articles/360059641133-Onboard-Memory-Manager
- Razer Seiren V3 Mini + Synapse: https://www.razer.com/streaming-microphones/razer-seiren-v3-mini
- Sony INZONE E9 help guide (USB-C audio box + INZONE Hub: EQ, spatial sound): https://helpguide.sony.net/mdr/2989/v1/en/print.pdf
- INZONE Hub support: https://www.sony.ca/en/electronics/support/others-software/inzone-hub/articles/00279893
- PlayStation Accessories: https://controller.dl.playstation.net/controller/lang/en/2100004.html
- NGENUITY Store listing: https://apps.microsoft.com/detail/9P1TBXR6QDCX
- Xbox Accessories: https://apps.microsoft.com/detail/9NBLGGH30XJ3
- Glorious CORE: https://www.gloriousgaming.com/pages/software
- Pulsar web driver: https://support.pulsar.gg/support/solutions/articles/67000746547
- ZOWIE U2 FAQ (driverless): https://zowie.benq.com/en-ca/support/products/mouse/u2/faq.html
- VAXEE XE V2 web driver (WebHID): https://www.techpowerup.com/review/vaxee-xe-v2-wireless/8.html

---

## 5. Detecting and opening the vendor app

### 5.1 `vendor-app-detect` (INFO-ONLY)

**Read:**
1. Uninstall keys `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`, `HKLM\SOFTWARE\WOW6432Node\…\Uninstall`
   (Razer lives here ✔), and `HKCU\…\Uninstall` (per-user Electron apps).
   - Match **DisplayName and Publisher** together, e.g. "Logitech G HUB" + "Logitech", "INZONE Hub" + "Sony Corporation".
   - The existing `VendorSoftware.InstalledNames` matches DisplayName only. Adding the publisher avoids false hits like
     "OpenInzone".
2. AppX/MSIX: `Windows.Management.Deployment.PackageManager.FindPackagesForUser("")` (read-only, current user) for
   Store-delivered apps (NGENUITY, Xbox Accessories, MSI Center, Armoury Crate).
3. Running state (optional status tag «ЗАПУЩЕНА»): `Process.GetProcessesByName` for `lghub_agent`, `RazerAppEngine`,
   `INZONEHub`… Read-only.

**Admin:** no. **Risk:** none. **Revert:** none.

### 5.2 `vendor-app-launch` (SAFE-OPTIONAL)

The button is «ОТКРЫТЬ <APP>», shown on the device row when the app is detected.

**Mechanism:**
- `UnelevatedShell.StartAsUserAsync(exePath, fixedArgs, Path.GetDirectoryName(exePath))`. The vendor app must never
  inherit Lycoris's admin token (CLAUDE.md rule 8).
- Store apps: `UnelevatedShell.StartAsUserAsync("shell:AppsFolder\\" + aumid)`.
- Web configurators: `UnelevatedShell.OpenAsync(url)`.

**Target selection (safe):**
- The exe path comes from a **fixed per-app table** in code (relative path under `%ProgramFiles%` /
  `%ProgramFiles(x86)%` / `%LOCALAPPDATA%\Programs`), validated against the Uninstall entry's DisplayIcon/InstallLocation
  or the Start-menu .lnk target.
- Arguments only from the table (`--url-params=apps=synapse` for Synapse 4). Never take arguments from registry/.lnk data.
- Before starting, require a **valid Authenticode signature** from the app's expected signer:
  "Logitech Inc", "Razer USA Ltd.", "Sony Corporation" ✔; others to verify.
  - Reuse the signer check of `Core/VendorDownloads`, or call WinVerifyTrust directly.
  - HKCU Uninstall keys and HKCU\Software\Classes are user-writable. The signature check stops a planted entry from
    being started by a click in Lycoris.
  - Unsigned exes (OpenRGB here ✔) get no launch button.
- **Do not** use the vendor URI handlers (`lghubapp:`, `RazerAppEngine.synapse:`) as the primary target. They resolve
  through HKCR, which includes user-writable HKCU\Software\Classes. They are acceptable only after the same
  resolve-and-verify step.

**Admin:** no. **Risk:** low. It starts a signed vendor program as the user, and that program may itself ask for UAC.
**Revert:** nothing changes on the system. The user closes the app.

### 5.3 `vendor-app-install` (SAFE-OPTIONAL)

**Recommended catalog changes** (all through the existing Apps/winget or website flow):
- `HardwareCatalog`:
  - add `PlayStation.PlayStationAccessories` for DualSense / Edge.
  - add Xbox Accessories (Store 9NBLGGH30XJ3) for Xbox controllers.
  - add StoreId `9P1TBXR6QDCX` to NGENUITY.
  - add MSI Center (`MSI.MSICenter`) for VID 0DB0 "MYSTIC LIGHT", **link-first**.
  - add `Logitech.OnboardMemoryManager` as a second, optional Logitech tool.
- **Do not** add third-party look-alikes (`penguinwokrs.OpenInzone`, `seerge.g-helper`, "Chroma Control") as vendor
  software.

**Admin:** yes for machine-scope winget installs (existing rule). **Risk:**
- Vendor suites install services and drivers. `logi_lamparray`, `logi_joy_*` and Razer Elevation Service are verified
  here ✔.
- Each install shows the existing confirmation dialog.

**Revert:** uninstall via the vendor's own uninstaller (Apps page), with a confirmation. Lycoris never removes vendor
drivers/services by itself.

---

## 6. Onboard memory, autostart and RGB unifiers

**`onboard-memory-advice` (INFO-ONLY).** Gaming mice and keyboards keep DPI, polling rate and usually button maps in
their own memory once the vendor app saved them there:
- Logitech: "On-Board Memory mode" in G HUB, or OMM.
- Razer: onboard profiles.
- Wooting / Keychron / VAXEE / ZOWIE / Pulsar: always on device.

What depends on the vendor app running:
- "Software mode" profiles.
- Per-game profiles.
- Software lighting effects.
- Macros on some devices.
- Headset EQ / virtual surround in INZONE Hub (applies over the INZONE USB audio path).

Recommended text on a Logitech mouse row: «Если хотите, чтобы настройки работали без G HUB, включите в G HUB режим
встроенной памяти и сохраните профиль в мышь». Lycoris only explains and opens the app.

**`vendor-autostart-advice` (INFO-ONLY).** The Startup page lets the user turn off:
- `LGHUB` (HKCU Run ✔).
- `INZONE Hub.lnk /startup` (Startup folder ✔).
- iCUE / GG / Synapse entries.

Before turning one off, show one line saying what stops working:
- «Подсветка и программные профили не будут применяться до запуска G HUB. Настройки во встроенной памяти мыши сохранятся.»
- For INZONE: «Эквалайзер и пространственный звук INZONE Hub не будут работать, пока программа закрыта.»

This is a description, not a new switch. The undo is the existing Startup undo.

**`rgb-unifier-apps` (ADVANCED).**
- What they are: OpenRGB (`OpenRGB.OpenRGB`, unsigned exe ✔) and SignalRGB (`WhirlwindFX.SignalRgb`) drive many brands'
  RGB through reverse-engineered protocols.
- Low-level hardware: OpenRGB 1.0 replaced WinRing0 with PawnIO for SMBus/Super-IO access. Users need PawnIO (or admin
  rights) for RAM/motherboard RGB.
- Conflicts: they fight with the vendor apps for the same vendor HID channel, e.g. G HUB / MSI Center vs OpenRGB on
  this PC.

What Lycoris may do:
- List them in the Apps catalog with `Expert | KernelDriver`.
- Show an info line when both a unifier and a vendor app are installed.
- Never auto-suggest them.

Evidence:
- OpenRGB merge request "Switch from WinRing0 to PawnIO": https://gitlab.com/CalcProgrammer1/OpenRGB/-/merge_requests/2885
- OpenRGB 1.0 PawnIO note: https://www.it-connect.tech/openrgb-1-0-the-open-source-tool-that-controls-rgb-devices-on-windows-linux-and-macos/

---

## 7. `dynamic-lighting-detect`: Windows Dynamic Lighting (INFO-ONLY)

What Windows supports:
- Windows 11 controls **HID LampArray** devices natively (Settings → Персонализация → Динамическое освещение, URI
  `ms-settings:personalization-lighting`).
- The devices are either native-firmware LampArray (UsagePage 0x59) or a vendor **VHF driver**.

On this PC:
- The Logitech keyboard and receiver are exposed through G HUB's VHF driver `logi_lamparray` (`&LAMPARRAY` children ✔).
- Without G HUB those children disappear.

What Lycoris does:
- Show the tag «DYNAMIC LIGHTING» on a device row when the group has a LampArray child or a 0x59 TLC.
  - Alternative read: `Windows.Devices.Lights.LampArray.GetDeviceSelector()` with
    `DeviceInformation.FindAllAsync(selector)`. Read-only, no NuGet.
- Offer «Открыть параметры освещения» (`UnelevatedShell.StartAsUserAsync("ms-settings:personalization-lighting")`).
- Changing Dynamic Lighting settings themselves is outside this report and belongs to the lighting/peripheral settings
  research.

**Admin:** no. **Risk:** none. **Revert:** none.

Evidence:
- Dynamic Lighting: HID LampArray devices, "native FW implementation, or through a VHF driver"; Logitech: "All Logitech G LIGHTSYNC RGB devices are supported" (https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices).
- User guide: https://support.microsoft.com/windows/control-your-dynamic-lighting-devices-in-windows-8e8f22e3-e820-476c-8f9d-9ffc7b6ffcd2

---

## 8. DO-NOT-INCLUDE

| id | "Tweak" seen online / idea | Why not |
|---|---|---|
| `vendor-protocol-writes` | Set DPI / polling / RGB / button maps by sending HID feature or output reports (Logitech HID++, Razer, SteelSeries, MSI Mystic Light protocols, as OpenRGB/libratbag/Solaar do) | Undocumented, reverse-engineered, and changes per firmware. A wrong report can corrupt onboard profiles or leave the device in a bad state. It races with the running vendor app (G HUB/Synapse own those vendor TLCs: UsagePage 0xFF00/0xFF43 ✔). There is no documented "read original value" for an exact undo. Some anti-cheats flag unknown processes that talk to input devices. Lycoris's rule: documented, reversible changes only |
| `vendor-protocol-reads` | Ask the LIGHTSPEED receiver which mouse is paired (HID++ "get device name") | Even a "read" requires **writing** an output report into a device another app is using. Use the read-only history hint (§2.5) instead |
| `firmware-updates` | Flash device/receiver firmware from Lycoris | Bricking risk, vendor-specific bootloaders (`046D:AB39` ✔), no undo. Link to the vendor app instead |
| `usb-polling-overclock` | "1000→8000 Hz USB polling hack" (hidusbf / patched usbport) | Replaces or patches signed system drivers. Needs test-signing or Secure Boot off. Kernel anti-cheats refuse to run. Modern 4K/8K mice set their rate in firmware through the vendor app |
| `lowlevel-rgb-drivers` | Install WinRing0 / inpout32 / PawnIO to "control RGB without bloat" | Generic kernel drivers with physical-memory/port access. Defender flags WinRing0 as "HackTool:Win32/Winring0". This contradicts CLAUDE.md (no risky driver installs) |
| `vendor-debloat` | Delete or disable G HUB / Synapse / iCUE services, LGHUB virtual devices, `logi_lamparray` drivers | Breaks macros, Dynamic Lighting, firmware updates and the vendor uninstaller. The supported route is the vendor's uninstaller or autostart-off with an explanation (§6) |
| `ghost-device-removal` | "Clean up hidden/ghost devices" (`pnputil /remove-device`, DevManView) | Irreversible. Removes driver bindings Windows reuses on replug, and the history Lycoris uses for names. No user benefit |
| — | Rename devices by writing `FriendlyName` in `Enum\…` | Writing under `HKLM\SYSTEM\CurrentControlSet\Enum` is protected and unsupported. Show Lycoris's own label instead |

---

## 9. C# sketch (read-only; .NET 10, no NuGet)

```csharp
// CfgMgr32: present devnodes + properties. Pure reads of the PnP database (no device I/O).
static class Cm
{
    [StructLayout(LayoutKind.Sequential)] public struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    const int CR_SUCCESS = 0, CR_BUFFER_SMALL = 0x1A;
    const uint FILTER_ENUMERATOR = 0x1, FILTER_PRESENT = 0x100;
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_SizeW(out int len, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_ListW(string? filter, char[] buf, int len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint dn, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_PropertyW(uint dn, in DEVPROPKEY key, out uint type, byte[]? buf, ref int size, uint flags);

    public static readonly DEVPROPKEY BusReportedDesc = new() { fmtid = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4 };
    public static readonly DEVPROPKEY ContainerId     = new() { fmtid = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
    public static readonly DEVPROPKEY Parent          = new() { fmtid = new("4340a6c5-93fa-4706-972c-7b648008a5a7"), pid = 8 };
    public static readonly Guid LocalMachineContainer = new("00000000-0000-0000-ffff-ffffffffffff");

    public static IEnumerable<string> PresentIds(string enumerator)
    {
        // The list can grow between the two calls (hot-plug): retry on CR_BUFFER_SMALL.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var len, enumerator, FILTER_ENUMERATOR | FILTER_PRESENT) != CR_SUCCESS) yield break;
            var buf = new char[len];
            var cr = CM_Get_Device_ID_ListW(enumerator, buf, len, FILTER_ENUMERATOR | FILTER_PRESENT);
            if (cr == CR_BUFFER_SMALL) continue;
            if (cr != CR_SUCCESS) yield break;
            foreach (var id in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)) yield return id;
            yield break;
        }
    }

    public static byte[]? Raw(string instanceId, DEVPROPKEY key, out uint type)
    {
        type = 0;
        if (CM_Locate_DevNodeW(out var dn, instanceId, 0) != CR_SUCCESS) return null;
        var size = 0;
        CM_Get_DevNode_PropertyW(dn, key, out type, null, ref size, 0);
        if (size == 0) return null;
        var b = new byte[size];
        return CM_Get_DevNode_PropertyW(dn, key, out type, b, ref size, 0) == CR_SUCCESS ? b : null;
    }

    public static string? Str(string id, DEVPROPKEY k) =>
        Raw(id, k, out _) is { } b ? Encoding.Unicode.GetString(b).TrimEnd('\0') : null;
    public static Guid? Guid_(string id, DEVPROPKEY k) =>
        Raw(id, k, out _) is { Length: 16 } b ? new Guid(b) : null;
}

// HID strings with a ZERO-ACCESS handle (allowed on system-owned keyboard/mouse collections). Fallback only.
static string? HidProduct(string interfacePath)
{
    using var h = CreateFileW(interfacePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
    if (h.IsInvalid) return null;
    var buf = new byte[256];                             // 126 UTF-16 chars + NUL is the USB maximum
    return HidD_GetProductString(h, buf, buf.Length)     // error 1784 = no string (virtual devices)
        ? Encoding.Unicode.GetString(buf).Split('\0')[0] : null;
}
// Never call ReadFile/WriteFile/HidD_SetFeature/HidD_SetOutputReport: Lycoris only reads descriptors.
```

Grouping key for a devnode:
- Take `ContainerId`.
- If it is the local-machine container, null or absent, use the instance id of the nearest `USB\VID_x&PID_y\…`
  ancestor (walk `Parent`).
- `&LAMPARRAY` and `LGHUBDEVICE`/`ROOT`/`SWD` subtrees are flags or drops, not rows (§2.3–2.4).

---

## Sources

- Microsoft:
  - DEVPKEY_Device_BusReportedDeviceDesc — https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-busreporteddevicedesc
  - Overview of Container IDs — https://learn.microsoft.com/en-us/windows-hardware/drivers/install/overview-of-container-ids
  - How Container IDs are generated — https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-container-ids-are-generated
  - HIDClass hardware IDs for TLCs — https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/hidclass-hardware-ids-for-top-level-collections
  - Opening HID collections — https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/opening-hid-collections
  - Keyboard and mouse HID client drivers — https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/keyboard-and-mouse-hid-client-drivers
  - HidD_GetProductString — https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidsdi/nf-hidsdi-hidd_getproductstring
  - CM_Get_DevNode_PropertyW — https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/nf-cfgmgr32-cm_get_devnode_propertyw
  - Win32_PnPEntity.GetDeviceProperties — https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/getdeviceproperties-win32-pnpentity
  - Dynamic Lighting devices — https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices
- ID databases:
  - usb.ids — https://github.com/usbids/usbids
  - Linux hid-ids.h — https://github.com/torvalds/linux/blob/master/drivers/hid/hid-ids.h
  - Solaar receivers — https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/base_usb.py
  - Solaar device descriptors — https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/descriptors.py
  - libratbag — https://github.com/libratbag/libratbag/tree/master/data/devices
  - QMK Keychron — https://github.com/qmk/qmk_firmware/tree/master/keyboards/keychron
  - Wooting udev — https://help.wooting.io/article/147-configuring-device-access-for-wootility-under-linux-udev-rules
- Vendors:
  - Logitech On-Board Memory — https://support.logi.com/hc/en-gb/articles/6505257646743
  - Logitech OMM — https://support.logi.com/hc/en-us/articles/360059641133
  - Razer Seiren V3 Mini — https://www.razer.com/streaming-microphones/razer-seiren-v3-mini
  - Sony INZONE E9 help guide — https://helpguide.sony.net/mdr/2989/v1/en/print.pdf
  - PlayStation Accessories — https://controller.dl.playstation.net/controller/lang/en/2100004.html
  - HyperX NGENUITY Store — https://apps.microsoft.com/detail/9P1TBXR6QDCX
  - Glorious CORE — https://www.gloriousgaming.com/pages/software
  - Pulsar web driver — https://support.pulsar.gg/support/solutions/articles/67000746547
  - ZOWIE U2 FAQ — https://zowie.benq.com/en-ca/support/products/mouse/u2/faq.html
- OpenRGB / WinRing0:
  - https://gitlab.com/CalcProgrammer1/OpenRGB/-/merge_requests/2885
- winget manifests:
  - `winget show --id <id> -e --source winget`, read 2026-10-27, for Logitech.GHUB, Logitech.OnboardMemoryManager,
    Logitech.OptionsPlus, RazerInc.RazerInstaller.Synapse4/3, SteelSeries.GG, Corsair.iCUE.5, MSI.MSICenter,
    Asus.ArmouryCrate, PlayStation.PlayStationAccessories, OpenRGB.OpenRGB and WhirlwindFX.SignalRgb.
