# MONOLITH: displays and monitors — what to read, recommend and change (Win11 24H2–26H2, Win10 22H2)

Scope: per-monitor resolution and refresh rate, HDR / Auto HDR / HDR calibration, scaling, VRR, colour depth, link
bandwidth, mixed refresh rates and monitor brightness over DDC/CI. Every mechanism below is a documented Windows API or a
documented Settings page. Monitor firmware settings (overdrive, OLED care, picture presets, DSC on/off, the
monitor's own DP-version switch) and GPU driver settings (G-SYNC/FreeSync, colour depth, integer scaling) **cannot be
written through Windows**. For those MONOLITH measures, recommends and points to the OSD or the vendor app.

The classifications are binding (CLAUDE.md): SAFE-DEFAULT / SAFE-OPTIONAL / ADVANCED / DO-NOT-INCLUDE, plus
**INFO-ONLY** (MONOLITH reads and explains but writes nothing). Every ✔ was checked **read-only** on the dev machine on
2026-10-27. Nothing on that PC was changed: the probes called only `EnumDisplayDevices`, `EnumDisplaySettingsEx`,
`QueryDisplayConfig`, `DisplayConfigGetDeviceInfo`, `GetDpiForMonitor`, the dxva2 `Get*` DDC/CI calls, WMI `root\wmi`
monitor classes and registry reads.

---

## 0. Dev machine snapshot (read-only, 2026-10-27)

| Item | X27U W3 (primary) | Odyssey G5 |
|---|---|---|
| EDID id / made | `ACR1132`, 2025 wk 34. DDC model string `ACER X27UW3` | `SAM74A7`, 2025 wk 23. DDC model string `FALCON` |
| Panel (vendor listings) | Acer Predator X27U W3: 26.5" QD-OLED 2560×1440 240 Hz, DP 1.4 + 2×HDMI | Samsung Odyssey G5 27" 1440p 180 Hz |
| GDI device / GPU | `\\.\DISPLAY1` on RTX 5070 Ti (LUID …EEBE), flags 0x5 = attached + primary | `\\.\DISPLAY2` on the same RTX 5070 Ti |
| Output technology | 10 = `DISPLAYPORT_EXTERNAL` ✔ (DDC VCP 0x60 = 0x0F = DP-1) | 10 = DisplayPort ✔ |
| Current mode (GDI) | 2560×1440 @ **240** Hz, 32 bpp, orientation 0, pos (0,0) | 1440×2560 @ **180** Hz, orientation 3 (270°, portrait), pos (−1440,−464) |
| Exact rate (CCD) | **239.970 Hz** (239970/1000), pixel clock 1056.75 MHz, total 2720×1619 | **179.952 Hz**, pixel clock 706.25 MHz, total 2650×1481 |
| Registry mode (`ENUM_REGISTRY_SETTINGS`) | 2560×1440 @ 240 | 1440×2560 @ 180 |
| Max offered at native resolution | 240 (rates at 2560×1440: 59, 60, 120, 144, 180, 240) ✔ | 180 (rates: 59, 60, 120, 165, 180) ✔ |
| `GET_TARGET_PREFERRED_MODE` | 2560×1440 @ **59.951 Hz** ← the EDID preferred timing is 60 Hz | 2560×1440 @ 179.952 Hz |
| EDID | 1.4, DP, 10 bpc. Range limits **V 48–240 Hz**, max pixel clock 1200 MHz. 1st DTD = 2560×1440@59.95. **The 240 Hz timing exists only in the DisplayID extension** (also 180/144 Hz). CTA: AMD FreeSync VSDB 48–240 Hz, BT.2020 colorimetry, HDR static metadata EOTF SDR+gamma+**PQ**, max luminance code ≈ 456 nits | 1.4, DP, 10 bpc. Range limits **V 50–180 Hz**, max 710 MHz. 1st DTD 2560×1440@164.98. 180 Hz in DisplayID. HDR static metadata PQ, max ≈ 400 nits |
| Advanced colour (`GET_ADVANCED_COLOR_INFO`, type 9) | supported=1, enabled=0, encoding RGB, **bpc 8** | supported=1, enabled=0, RGB, **bpc 8** |
| `GET_ADVANCED_COLOR_INFO_2` (type 15, 24H2+) | value 0x51: AC supported, **HDR supported, HDR user-enabled 0**, WCG supported, WCG user-enabled 0, active mode 0 (SDR) ✔ | same, 0x51 |
| SDR white level (type 11) | 1000 → 80 nits (HDR is off, so nominal) | 1000 → 80 nits |
| Scaling | path scaling 1 = IDENTITY. Effective DPI **96 (100 %)**, raw DPI 110 | IDENTITY. 96 DPI (100 %), raw 108 |
| DPI registry | `HKCU\Control Panel\Desktop\PerMonitorSettings` **absent**. `HKLM\…\GraphicsDrivers\ScaleFactors\<id>\DpiValue` = 0 for both, so both run at the **Windows-recommended** scale | |
| DDC/CI | Works. `GetMonitorCapabilities` fails (err 203), but `GetMonitorBrightness` = 100 (0..100) and contrast 50 succeed. MCCS 2.2. Vendor codes E0–EE (picture modes "Game Action/Racing/Sports", HDR mode) | `GetMonitorCapabilities` OK (caps 0x1C1E). Brightness **30 (0..50)**, contrast 50 (0..50). MCCS 2.0 |
| DirectX prefs | `HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings` = `SwapEffectUpgradeEnable=1;` (no `AutoHDREnable`, no `VRROptimizeEnable`). Per-game `GpuPreference=2;` for 4 games. `HKCU\…\DirectX\GraphicsSettings\AutoHDROptOutApplicable` = 0 | |
| MonitorDataStore | `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore\<id>` → `AutoColorManagementSupported` = 1 for both | |
| CCD database | `HKLM\…\GraphicsDrivers\Configuration` and `\Connectivity` are **not readable** even by admins (SYSTEM-only ACL) ✔, so MONOLITH must never try to read or write them directly | |
| Windows HDR Calibration app | **not installed** (`Get-AppxPackage *HDRCalibration*` empty) | |
| Other GDI devices | DISPLAY3–4 (NVIDIA) and DISPLAY5–9 (AMD iGPU) exist but are not attached: the iGPU has no monitor | |

**Conclusion for this PC:** both monitors already run at their maximum refresh at native resolution, both are on the
RTX 5070 Ti, both connect over DisplayPort, and scaling is at the recommended 100 %. The brief's "239 Hz" is the
monitor's own 239.970 Hz timing from DisplayID. That *is* the 240 Hz mode, and no exact 240.000 Hz timing exists in the
EDID. It must never be flagged as "below maximum" (`HealthRules.IsBelowMaxRefresh` already allows 1 Hz of slack).
The real risk on this PC is the X27U's EDID **preferred timing of 59.95 Hz**. After a driver reinstall, a cable or
port change or a new monitor, Windows/the driver can come back at 60 Hz, which is exactly the #1 problem below.

---

## 1. Summary

| ID | RU title | What MONOLITH does / recommends | Mechanism | Admin | Class |
|---|---|---|---|---|---|
| `display-refresh-rate` | Частота обновления монитора | Detect "below max at native res"; one click → max rate, with **15 s auto-revert** | `EnumDisplaySettingsEx` + `ChangeDisplaySettingsEx` (temp → `CDS_UPDATEREGISTRY`), verify with `QueryDisplayConfig` | No* | **SAFE-DEFAULT** (interactive only) |
| `display-resolution` | Разрешение экрана | Recommend native; manual choice from the driver's mode list, same auto-revert | Same as above; native = `GET_TARGET_PREFERRED_MODE` size | No* | **SAFE-DEFAULT** (native) / manual SAFE-OPTIONAL |
| `display-mode-safe-apply` | Защитный откат параметров экрана | Mandatory engine for every display write: journal first, temporary apply, 15 s confirm, watchdog revert | §2 | No* | **INFO-ONLY** (design requirement, not a toggle) |
| `display-hdr` | HDR (по монитору) | Show state per monitor; manual toggle; recommend by panel class | `DisplayConfigGetDeviceInfo` type 15/9 + `DisplayConfigSetDeviceInfo` `SET_HDR_STATE` (16, 24H2+) / `SET_ADVANCED_COLOR_STATE` (10) | No* | **SAFE-OPTIONAL** |
| `display-auto-hdr` | Auto HDR для игр | Offer only when HDR is on somewhere | `DirectXUserGlobalSettings` token `AutoHDREnable` (existing `DxTokenTweak`) | No (HKCU) | **SAFE-OPTIONAL** |
| `display-hdr-calibration` | Калибровка HDR | Recommend the Microsoft app after HDR is turned on | Store `9N7F2SM5D1LR` (Apps page) | — | **INFO-ONLY** |
| `display-sdr-white-level` | Яркость SDR-контента в HDR | Read and show; no documented setter | `GET_SDR_WHITE_LEVEL` (11) | — | **INFO-ONLY** |
| `display-acm` | Автоуправление цветом (ACM/WCG) | Show state; link to Settings | `GET_ADVANCED_COLOR_INFO_2` bits; `SET_WCG_STATE` exists but its mapping isn't verified | — | **INFO-ONLY** |
| `display-scaling` | Масштаб (DPI) | Show effective vs recommended; link to Settings | `GetDpiForMonitor`; setter is undocumented | — | **INFO-ONLY** |
| `display-vrr` | G-SYNC / FreeSync | Detect VRR capability from EDID; explain driver settings + FPS cap | EDID range limits / FreeSync VSDB; Windows-side VRR stays the existing `vrr-optimization` tweak | — | **INFO-ONLY** |
| `display-color-depth` | Глубина цвета (бит на канал) | Show link bpc; explain DP/HDMI limits | `GET_ADVANCED_COLOR_INFO.bitsPerColorChannel` | — | **INFO-ONLY** |
| `display-link-bandwidth` | Кабель и порт (DP/HDMI) | Explain why max Hz is missing (cable/port/iGPU) | Output technology + EDID max vs offered max + pixel-clock math | — | **INFO-ONLY** |
| `display-mixed-refresh` | Мониторы с разной частотой | Explain Win11 behaviour; optional manual lower rate on the 2nd monitor | Uses `display-refresh-rate` | — | **INFO-ONLY** |
| `display-primary` | Основной монитор | Warn if the primary isn't the fastest monitor; link to Settings | `DISPLAY_DEVICE_PRIMARY_DEVICE` | — | **INFO-ONLY** |
| `display-ddc-brightness` | Яркость и контраст монитора (DDC/CI) | Manual sliders for external monitors, read-back verified | dxva2 `Get/SetMonitorBrightness`, `Get/SetMonitorContrast` | No | **SAFE-OPTIONAL** (manual, no default) |
| `display-ddc-vendor` | Режимы, вход, питание через DDC/CI | — | VCP 0x04/0x60/0xD6/0x14/E0–FF | — | **DO-NOT-INCLUDE** |
| `display-drr` | Динамическая частота (DRR) | Laptops only; leave to Windows | Settings | — | **INFO-ONLY** |
| `display-overclock-edid` | Разгон монитора / CRU / EDID override | — | `EDID_OVERRIDE`, custom resolutions | — | **DO-NOT-INCLUDE** |
| `display-tdr` | TdrDelay / TdrLevel | — | `GraphicsDrivers\Tdr*` | — | **DO-NOT-INCLUDE** |
| `display-dpi-registry` | Масштаб через реестр / недокументированный API | — | `PerMonitorSettings\DpiValue`, `DisplayConfigSetDeviceInfo` with negative types | — | **DO-NOT-INCLUDE** |
| `display-dwm-disable` | Отключение DWM / композиции | — | killing dwm.exe, IFEO hacks | — | **DO-NOT-INCLUDE** |
| `display-force-max-bpc` | «Максимальная глубина цвета всегда» | — | driver panel | — | **DO-NOT-INCLUDE** (as a "boost") |

\* Windows itself needs no admin rights for these calls; the Settings app makes them as a standard user. MONOLITH
still requires elevation because the undo journal lives in the admin-only `%ProgramData%\MONOLITH` and the app is
read-only without admin (CLAUDE.md rules 1, 6, 7).

`mpo-disable` (ADVANCED), `vrr-optimization` (SAFE-OPTIONAL), `windowed-games-optimization` (SAFE-DEFAULT) and
`hags` already exist in `TweakCatalog.Gaming.cs`. They stay in Optimize, and the display page links to them rather than
duplicating them (minimal-UI rule).

---

## 2. The safety core: applying a display mode with auto-revert

### 2.1 What Windows does

- The Windows display UI applies "resolution, layout, orientation, scaling, primary, bit depth, and refresh rate"
  through **`SetDisplayConfig`** (Microsoft, *SetDisplayConfig Summary and Scenarios*).
- `SetDisplayConfig` has two documented modes:
  - a **temporary** configuration that is not saved (`SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG`);
  - a **saved** one (`… | SDC_SAVE_TO_DATABASE`).

  Its documented scenario "**Return from a temporary mode to the last saved display configuration**" is
  `SDC_APPLY | SDC_USE_DATABASE_CURRENT`.
- After a resolution or refresh change, Settings asks "Keep these display settings?" (RU: «Сохранить эти параметры
  экрана?») and **reverts automatically after ~15 s** unless the user clicks Keep. This is observed behaviour, described
  in university IT guides and Windows tutorials. Microsoft doesn't document how it is implemented, so MONOLITH must build
  its own equivalent and must not assume an OS safety net exists. The OS gives none: `ChangeDisplaySettingsEx` and
  `SetDisplayConfig` apply immediately and never ask.
- The persistence database (`GraphicsDrivers\Configuration`) is SYSTEM-only ✔. Only the CCD/GDI APIs may change it.
- The CCD API "doesn't provide any functionality to enumerate the possible source modes … APIs that existed before
  Windows 7 already provide this functionality" (Microsoft). So the mode list comes from `EnumDisplaySettingsEx`.

### 2.2 MONOLITH algorithm (`ChangeSession.SetDisplayModeAsync`, new record kind)

1. **Preconditions** (refuse with a plain-language reason if any one fails):
   - The process is in the interactive console session: `GetSystemMetrics(SM_REMOTESESSION) == 0`. `SetDisplayConfig`
     returns `ERROR_ACCESS_DENIED` from remote sessions.
   - No fullscreen game is running: `SHQueryUserNotificationState` ≠ `QUNS_RUNNING_D3D_FULL_SCREEN`/`QUNS_BUSY`, and
     no Game Boost session is active.
   - The target mode comes **only** from `EnumDisplaySettingsEx(dev, i, …, 0)` for that device. That list is already
     filtered by the driver for the EDID and the link. Use 32 bpp, progressive only (`DM_INTERLACED` clear).
   - `ChangeDisplaySettingsEx(dev, &dm, NULL, CDS_TEST, NULL) == DISP_CHANGE_SUCCESSFUL`.
2. **Snapshot:**
   - `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` gives full path and mode arrays: exact rational refresh, orientation,
     position, scaling.
   - Also take the GDI `ENUM_CURRENT_SETTINGS` and `ENUM_REGISTRY_SETTINGS` DEVMODEs.
   - Key the record by the target's `monitorDevicePath` (from `DISPLAYCONFIG_TARGET_DEVICE_NAME`, for example
     `\\?\DISPLAY#ACR1132#5&30ca09c6&0&UID4355#{e6f07b5f-…}`) plus the EDID manufacturer and product ids.
   - **Never** key it by `\\.\DISPLAYn`, because those numbers are reassigned.
3. **Journal first:** write a record in state `PendingConfirm` with original = {w, h, refresh num/den, GDI Hz,
   orientation, position} and target = {…}, *before* any change (prime directive 1). Display state is not covered by
   restore points, so the journal is the only undo.
4. **Temporary apply:**
   - Set `dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL`. Don't set position or
     orientation, so the layout and the portrait G5 are kept.
   - Call `ChangeDisplaySettingsEx(dev, &dm, NULL, 0, NULL)`. Flag 0 means "changed dynamically" without
     `CDS_UPDATEREGISTRY`, so nothing is persisted.
   - Wait for `WM_DISPLAYCHANGE` (or poll `QueryDisplayConfig` for up to 5 s), and start the countdown only after that.
5. **Confirmation dialog:**
   - Topmost, on the primary monitor or on a monitor that was *not* changed.
   - Title «Сохранить эти параметры экрана?»
   - Countdown «Возврат к прежним параметрам через 15 с».
   - Buttons **СОХРАНИТЬ** / **ВЕРНУТЬ**. Default and focused button = ВЕРНУТЬ. Esc, closing the window or Alt+F4 = revert.
   - The 15 s timer is a `System.Threading.Timer`, **not** a dispatcher timer, so a hung UI thread still reverts.
6. **Out-of-process guard** (crash-proof):
   - Before step 4, start `MONOLITH.exe --display-guard <id> <deadline>` in the same session.
   - The guard waits on a named event `Local\MONOLITH-DisplayKeep-<id>`.
   - If the event is not signalled by the deadline (15 s + 5 s slack), or the main process dies, the guard performs the
     revert in step 8.
   - Without this guard, a crash during the window would leave a black "out of range" screen until reboot or re-plug.
     A reboot does restore the saved mode, because nothing was persisted.
7. **Keep:**
   - Signal the event, then call `ChangeDisplaySettingsEx(dev, &dm, NULL, CDS_UPDATEREGISTRY, NULL)` to persist; the
     mode is already active, so there is no extra flash.
   - Re-read `QueryDisplayConfig`. Confirm the target path now has the target size and refresh. Show the **exact** rate
     ("239,97 Гц"), not the GDI integer.
   - Mark the record `Applied`.
8. **Revert** (timeout, ВЕРНУТЬ, failure in step 4, or the guard):
   1. `ChangeDisplaySettingsEx(NULL, NULL, NULL, 0, NULL)`. Microsoft documents it as "the easiest way to return to
      the default mode after a dynamic mode change".
   2. Verify with `QueryDisplayConfig` against the snapshot.
   3. If it still differs: `SetDisplayConfig(0, NULL, 0, NULL, SDC_APPLY | SDC_USE_DATABASE_CURRENT)`.
   4. Last resort: `SetDisplayConfig(snapshotPaths, snapshotModes, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES)`.
   5. Delete the pending record and log `Log.Warn`.
9. **Startup recovery:** if MONOLITH starts and finds a `PendingConfirm` record, perform step 8 and drop the record. It
   was never confirmed.
10. **Later undo** (Restore page or per-row ОТКАТИТЬ):
    - Find the active path whose `monitorDevicePath` matches. If the monitor isn't connected, keep the record and say
      «монитор не подключён».
    - If the current mode ≠ the recorded *target* mode, the user changed it since. Ask before overwriting
      (DriftDetector-style).
    - Check that the original mode is still offered and passes `CDS_TEST`. A different cable can remove a mode.
    - Run the **same** temp-apply + 15 s confirm flow. Undo is a display change too.
11. **Batching:** one monitor per confirmation. Never part of an unattended "apply all" profile run: the Recommended or
    Gaming profile may *list* the fix, but it runs only interactively. For two monitors, documented batching
    (`CDS_UPDATEREGISTRY|CDS_NORESET` per device, then `ChangeDisplaySettingsEx(NULL,…)`) is allowed, but still with one
    confirmation covering both.

**Pre-ship verification** (on a test machine, or on the owner's PC only with his explicit consent; never in
`--selftest` or unit tests):
- (a) After a flag-0 change, `ENUM_REGISTRY_SETTINGS` and a reboot still give the old mode.
- (b) `ChangeDisplaySettingsEx(NULL,…)` restores it.
- (c) `SDC_USE_DATABASE_CURRENT` restores it.
- (d) Killing MONOLITH during the countdown lets the guard revert.
- (e) A mode the cable can't carry (simulate with an HDMI 2.0 cable at 1440p240) reverts by itself.

Unit tests can cover the decision logic only: mode selection, the slack rule and record validation.

### 2.3 C# sketch (P/Invoke only, no new NuGet)

```csharp
// user32: EnumDisplaySettingsExW, ChangeDisplaySettingsExW (DEVMODEW 220 bytes, already in DisplayProbe),
// GetDisplayConfigBufferSizes, QueryDisplayConfig, SetDisplayConfig, DisplayConfigGetDeviceInfo, DisplayConfigSetDeviceInfo
const uint CDS_UPDATEREGISTRY = 0x01, CDS_TEST = 0x02, CDS_NORESET = 0x10000000;
const int  DISP_CHANGE_SUCCESSFUL = 0, DISP_CHANGE_RESTART = 1;
const uint SDC_APPLY = 0x80, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_SAVE_TO_DATABASE = 0x200,
           SDC_ALLOW_CHANGES = 0x400, SDC_VALIDATE = 0x40,
           SDC_USE_DATABASE_CURRENT = 0x1 | 0x2 | 0x4 | 0x8;
const uint DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
// Temporary:   ChangeDisplaySettingsExW(gdiName, ref dm, IntPtr.Zero, 0, IntPtr.Zero)
// Keep:        ChangeDisplaySettingsExW(gdiName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero)
// Revert:      ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero)  // lpDevMode NULL overload
//              then SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_USE_DATABASE_CURRENT) if still different
```

`DisplayProbe` already has the structs. The writer must live in `Core/` (for example `Core/DisplayConfig.cs`) and be
reachable only through `ChangeSession`. Modules stay read-only.

---

## 3. Settings

### `display-refresh-rate`: Частота обновления монитора → **SAFE-DEFAULT (interactive, with auto-revert)**

- **Why it matters:** a high-refresh monitor left at 60 Hz is the single most common, fully free gaming loss. Windows
  doesn't warn about it: "Windows 10 will happily let that high refresh rate monitor sit at 60Hz for its entire life"
  (PCWorld).
  - Causes: the EDID *preferred* timing is often 60 Hz (verified here: X27U preferred = 59.95 Hz, and 240 Hz exists only
    in the DisplayID block); a driver reinstall; a new cable or port.
  - Microsoft: available rates depend on the display, and "many popular game titles automatically use VRR". VRR doesn't
    lift the 60 Hz ceiling if the desktop mode is 60 Hz.
- **Recommended (gaming and everyday, desktops):** the highest progressive rate offered at the native resolution.
  - Everyday benefit: smoother cursor and scrolling. Cost: slightly higher GPU idle power with several high-refresh
    monitors (see `display-mixed-refresh`).
  - Laptops with an internal panel: if Windows offers **Dynamic refresh rate**, leave it on (`display-drr`).
  - On battery, don't push max refresh. Recommend it only on AC.
- **Manual range:** any rate `EnumDisplaySettingsEx` offers for the current resolution. In the UI, show the exact CCD rate
  after applying.
- **Detection (read):**
  - `DisplayProbe.Read()` already gives current and max at the current resolution.
  - Use the max at the **native** resolution (`DISPLAYCONFIG_TARGET_PREFERRED_MODE` width/height) for the
    recommendation.
  - Flag only when `max > current + 1` (`HealthRules.IsBelowMaxRefresh`). 239.97 vs 240 is the same timing.
- **Write:** §2. **Revert:** §2 step 8 (immediate) / step 10 (later, exact original from the journal).
- **Admin:** no for Windows (yes for MONOLITH's journal).
- **Risk:**
  - Low. A marginal cable at a near-limit rate can flicker or black out. Here the X27U at 240 Hz uses 25.36 of the
    25.92 Gbit/s that DP 1.4 provides; auto-revert covers that.
  - A mode change may blank all monitors on that GPU for a moment.
- **UI:** the existing Dashboard check `refresh-rate` («МОНИТОР НЕ НА МАКСИМАЛЬНОЙ ЧАСТОТЕ») currently only opens
  `ms-settings:display-advanced`. Note that on Windows 11 this URI opens System > Display, and Advanced display has no
  URI of its own (Microsoft). Its button should deep-link into the new display card, which runs §2.

### `display-resolution`: Разрешение экрана → **SAFE-DEFAULT (recommend native) / SAFE-OPTIONAL (manual)**

- **Recommended:** native, i.e. the size of `DISPLAYCONFIG_TARGET_PREFERRED_MODE`, cross-checked with the active size of
  the EDID's first DTD. Microsoft: "Monitors can support resolutions that are lower than their native resolutions, but
  text won't look as sharp", and "It's usually best to stick with the one that's marked (Recommended)".
- **Manual range:** any mode in `EnumDisplaySettingsEx` (verified here: 24 sizes per monitor, from 640×480 up to native).
  - Non-native modes are scaled by the GPU or the monitor and look blurry, except integer ratios with integer scaling,
    which is a driver option: INFO-ONLY.
  - A lower desktop resolution doesn't raise game FPS in fullscreen-exclusive titles (they set their own mode). In
    borderless games, lower the in-game render resolution or use DLSS/FSR instead.
- **Mechanism / revert / admin:** as `display-refresh-rate`, with `DM_PELSWIDTH|DM_PELSHEIGHT` (+ frequency, picking the
  max rate at the new size unless the user picks one).
  - Path scaling (`DISPLAYCONFIG_PATH_TARGET_INFO.scaling`: IDENTITY / CENTERED / STRETCHED / ASPECTRATIOCENTEREDMAX) is
    preserved from the snapshot.
  - `SetDisplayConfig` sets IDENTITY by itself when source and target sizes match.
- **Risk:** low, with auto-revert. Window positions and icons can move after a resolution change; that's normal Windows
  behaviour.

### `display-hdr`: HDR (по монитору) → **SAFE-OPTIONAL (manual, per monitor, no default)**

- **Read:**
  - Windows 11 24H2+ (build ≥ 26100): `DisplayConfigGetDeviceInfo` with `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2`
    (= 15) → `highDynamicRangeSupported`, `highDynamicRangeUserEnabled`, `wideColorSupported`, `wideColorUserEnabled`,
    `advancedColorLimitedByPolicy`, `bitsPerColorChannel`, `activeColorMode` (0 SDR, 1 WCG, 2 HDR). Verified ✔ on
    26300: value 0x51 = AC supported + HDR supported + WCG supported, all user-off.
  - Older builds: `GET_ADVANCED_COLOR_INFO` (= 9) → `advancedColorSupported`, `advancedColorEnabled`,
    `wideColorEnforced`, `advancedColorForceDisabled`.
- **Write:**
  - Build ≥ 26100: `DisplayConfigSetDeviceInfo` with `DISPLAYCONFIG_SET_HDR_STATE` (`header.type` = 16 =
    `DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE`, bit 0 `enableHdr`).
  - Older: `DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE` (type 10, bit 0 `enableAdvancedColor`).
  - Header: `adapterId` + `id` of the **target** from `QueryDisplayConfig`.
  - On 24H2+, "advanced colour" also covers WCG/ACM on SDR displays, so type 10 is no longer an exact HDR switch there.
    This is why the open-source libdisplaydevice (Sunshine) uses type 16 from build 26100 on.
  - The names `…GET_ADVANCED_COLOR_INFO_2`, `…SET_HDR_STATE` and `…SET_WCG_STATE` are on Microsoft's official
    `DISPLAYCONFIG_DEVICE_INFO_TYPE` page. Their struct pages are missing (404), so the layouts come from the Windows SDK
    10.0.26100 `wingdi.h`:
    ```c
    // _2: header; union{ struct{ advancedColorSupported:1, advancedColorActive:1, reserved1:1,
    //   advancedColorLimitedByPolicy:1, highDynamicRangeSupported:1, highDynamicRangeUserEnabled:1,
    //   wideColorSupported:1, wideColorUserEnabled:1, reserved:24 }; UINT32 value; };
    //   DISPLAYCONFIG_COLOR_ENCODING colorEncoding; UINT32 bitsPerColorChannel; DISPLAYCONFIG_ADVANCED_COLOR_MODE activeColorMode;
    // SET_HDR_STATE: header; union{ struct{ enableHdr:1, reserved:31 }; UINT32 value; };
    ```
- **Persistence and revert:** Windows persists the state per monitor, exactly like the Settings toggle. Undo = set the
  recorded original `enableHdr`. Wrap the switch in the §2 confirm flow (journal first, 15 s, revert on timeout): an HDR
  switch is a modeset and can fail to sync at the bandwidth limit.
- **Admin:** no (Windows).
- **Recommended:**
  - **Self-emissive or local-dimming HDR displays** (OLED/QD-OLED with DisplayHDR True Black, or LCD with FALD and
    DisplayHDR ≥ 600): HDR is worth using for HDR games and video.
    - Simplest everyday pattern: keep the desktop in SDR and toggle with **Win+Alt+B**, which Microsoft lists as the
      Game Bar shortcut to turn HDR on or off.
    - Or leave HDR on and run the Windows HDR Calibration app.
  - **Edge-lit LCDs with HDR10 / DisplayHDR 400** (no local dimming required by the tier): keep HDR **off**. The gain
    over SDR is small, and SDR content can look dim or washed out (RTINGS, TFTCentral).
  - MONOLITH can't read the certification. Windows Settings shows certification for known displays, but there is no API
    for it.
    - So the recommendation is a hint built from the EDID HDR static metadata (max luminance), the panel name and the
      user's answer. It is never applied automatically.
    - EDID minimum luminance was 0 on both monitors here, so it's not a reliable OLED signal.
  - This PC: X27U W3 (QD-OLED, PQ, ~456 nits in EDID) → «HDR имеет смысл для HDR-игр; на рабочем столе — по желанию
    (Win+Alt+B)». Odyssey G5 (~400 nits, HDR10) → «оставить HDR выключенным».
- **Risk:**
  - Washed-out SDR when the SDR brightness slider is wrong.
  - Screenshots and recordings look different.
  - If the NVIDIA Control Panel colour override is set to a non-default format, the Windows HDR toggle can snap back to
    Off (Microsoft Q&A). Detect this by re-reading after the write and explain it.
  - `advancedColorLimitedByPolicy` = 1 means a policy blocks it, so say that and don't write.

### `display-auto-hdr`: Auto HDR для игр → **SAFE-OPTIONAL (only when HDR is on for at least one display)**

- **What:** Windows tone-maps SDR DirectX 11/12 games to HDR (Microsoft: "some older games are SDR only, such as games
  that use DirectX 11 or DirectX 12"). Per-game intensity lives in Game Bar (Win+G → Settings → Gaming features →
  "Adjust HDR intensity", stored per game; DirectX blog, 2022).
- **Mechanism:** HKCU `Software\Microsoft\DirectX\UserGpuPreferences` → REG_SZ `DirectXUserGlobalSettings`, token
  `AutoHDREnable=1;`.
  - Use the existing `DxTokenTweak`, which already parses and merges tokens, cleans up keys, writes to the interactive
    user's hive and has a journalled undo.
  - Microsoft documents only the Settings toggle. The token is community-documented (ElevenForum), so detect the state
    from the token and fall back to "unknown". Per-app numeric values (4147/48) are unverified → don't use them.
  - Auto HDR also forces the flip-model upgrade (`SwapEffectUpgradeEnable`), which is already on here ✔.
- **Default / revert:** token absent = off. Revert = restore the exact previous string (DxTokenTweak).
- **Admin:** no. Applies at the next game start.
- **Recommended:** on for users who keep HDR on and play SDR games. Off (no change) otherwise. On HDR-off systems, show it
  greyed out with the reason.
- **Risk:**
  - Some games show wrong colours or oversaturation. Opt out per game in Game Bar.
  - Windows 11 24H2 had a documented Auto HDR bug (games froze or showed incorrect colours; safeguard hold 55382406).
    It was fixed by the February 11, 2025 update KB5051987. Require build ≥ 26100.3194 on 24H2, or simply build > 26100
    (26300 here).

### `display-hdr-calibration`: Калибровка HDR (приложение Microsoft) → **INFO-ONLY**

- **What:** the Microsoft "Windows HDR Calibration" app (Store `9N7F2SM5D1LR`).
  - Requirements: Windows 11, HDR on, the app running full screen, WDDM 2.7+, and NVIDIA GTX 10xx+ / AMD RX 400+ /
    Intel 11th gen+.
  - It creates a colour profile for the display. Undo: Color Management → All Profiles → Remove (Microsoft).
- **MONOLITH:**
  - Detect it with `Get-AppxPackage Microsoft.Windows.HDRCalibration` (not installed here ✔).
  - After HDR is turned on, show one muted line recommending it.
  - Installing belongs to the Apps page (msstore source via winget), per the one-place rule. Launch it with
    `UnelevatedShell`.
- **Never** write the profile association ourselves.

### `display-sdr-white-level`: Яркость SDR-контента в режиме HDR → **INFO-ONLY**

- **Read:** `DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL` (11) → `SDRWhiteLevel`. Nits = value / 1000 × 80. Verified:
  1000 = 80 nits nominal while HDR is off.
- **Write:** there is no documented setter. The slider lives in Settings → Display → HDR (`ms-settings:display`).
  Microsoft notes that on desktop HDR monitors "SDR reference white levels are typically set to around 200 nits".
- Show it only when HDR is on: «SDR-контент: ~N нит».

### `display-acm`: Автоматическое управление цветом (ACM / WCG) → **INFO-ONLY**

- **What:** Windows colour-manages sRGB content on wide-gamut SDR displays (Advanced Color for SDR, Windows 11 22H2+;
  `AdvancedColorKind` WCG = "SDR display with high bit-depth and auto color management").
  - Reports say 24H2 widened it to all SDR displays.
  - Here `MonitorDataStore\…\AutoColorManagementSupported` = 1 and `wideColorSupported` = 1 on both monitors ✔.
- **Why INFO-ONLY:**
  - `DISPLAYCONFIG_SET_WCG_STATE` (17) exists, but no Microsoft page confirms that it is exactly the Settings
    "Automatically manage color for apps" toggle.
  - Forum reports say 24H2 can switch ACM back on by itself when the bit depth changes, and that it clamps apps to
    sRGB. Those are user reports.
  - Show the state (`wideColorUserEnabled`) and explain it: «на широкоохватных мониторах (QD-OLED) цвета sRGB перестают
    быть перенасыщенными». Link to `ms-settings:display`.
  - Revisit after verifying type 17 on a test machine.

### `display-scaling`: Масштаб (DPI) → **INFO-ONLY**

- **Read:**
  - `GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI)` from a per-monitor-v2-aware thread (`SetThreadDpiAwarenessContext(-4)`).
    Percent = DPI × 100 / 96.
  - Recommended vs custom: `HKLM\…\GraphicsDrivers\ScaleFactors\<monitor>\DpiValue`, a relative step where 0 =
    recommended (an OS internal, read only). Here both monitors are at 96 DPI = 100 % = recommended ✔.
- **Recommended:** the value Windows marks "(Recommended)" (Microsoft: "Typically, it's best to choose the one that's
  marked (Recommended)"). Scaling has no FPS effect.
- **Why not writable:**
  - There is no documented API to set per-monitor scaling.
  - Tools that do it call `DisplayConfigSetDeviceInfo` with undocumented negative type values, or write
    `HKCU\Control Panel\Desktop\PerMonitorSettings\<id>\DpiValue` and then need a sign-out.
  - Both are DO-NOT-INCLUDE (`display-dpi-registry`). Link to `ms-settings:display`.

### `display-vrr`: G-SYNC / FreeSync / VRR → **INFO-ONLY** (the Windows-side `vrr-optimization` tweak stays in Optimize)

- **Read (capability hint):** from the EDID:
  - the range-limits descriptor (`0xFD`, min–max V rate; 48–240 Hz on the X27U, 50–180 Hz on the G5 ✔);
  - the AMD FreeSync VSDB (OUI `00-00-1A`, range 48–240 on the X27U ✔);
  - Adaptive-Sync data in DisplayID.

  Windows shows "Variable refresh rate: Supported" in Advanced display. Whether G-SYNC Compatible is *enabled* is
  driver-private (NVAPI) and not readable through Windows.
- **Recommended (evidence: Blur Busters G-SYNC 101):**
  - G-SYNC (or FreeSync) on in the GPU panel.
  - V-SYNC on in the NVIDIA profile, off in-game.
  - FPS cap at least 3 below max refresh: ≤ 237 FPS @ 240 Hz, ≤ 177 @ 180 Hz. NVIDIA Reflex caps automatically at
    about 224 FPS @ 240 Hz.
- **MONOLITH:** shows the VRR range and these steps, with a button that opens the NVIDIA App or AMD Adrenalin as the
  user (`UnelevatedShell.StartAsUserAsync`). It writes nothing to driver profiles.

### `display-color-depth`: Глубина цвета → **INFO-ONLY**

- **Read:** `bitsPerColorChannel` from type 9/15. It is 8 on both monitors here ✔ in SDR.
- **Explain:**
  - SDR desktop and SDR games are 8-bit content, so 8 bpc is correct.
  - HDR uses 10 bpc signalling (Microsoft).
  - At 2560×1440@240 Hz the X27U needs 25.36 Gbit/s at 8 bpc but 31.7 Gbit/s at 10 bpc. That is more than DP 1.4 HBR3
    carries (25.92), so 10-bit at 240 Hz over DP 1.4 needs DSC or HDMI 2.1, and the driver or monitor negotiates it.
- **Write:** only in the GPU driver panel ("Output colour depth"). There is no Windows API.

### `display-link-bandwidth`: Кабель и порт (DP/HDMI) → **INFO-ONLY**

The fixed bandwidth limits of each link standard explain most "my 240 Hz option is missing" reports.

| Link | Effective data rate | 1440p @ 240 Hz 8 bpc (this X27U: 25.36 Gbit/s) | 1440p @ 180 Hz 8 bpc (this G5: 16.95) |
|---|---|---|---|
| DP 1.2 HBR2 | 17.28 Gbit/s | no | yes (tight) |
| DP 1.4 HBR3 | 25.92 Gbit/s | **yes (tight)**; 10 bpc needs DSC | yes, also 10 bpc (21.2) |
| DP 2.1 UHBR10/13.5/20 | 38.69 / 52.22 / 77.37 Gbit/s | yes | yes |
| HDMI 2.0 | 14.4 Gbit/s (~144 Hz max at 1440p 8-bit) | **no** | **no** |
| HDMI 2.1 FRL | ~42 Gbit/s | yes | yes |

Sources: Wikipedia DisplayPort and HDMI articles, which reproduce the VESA and HDMI rates. Requirement = pixel clock
(from `DISPLAYCONFIG_VIDEO_SIGNAL_INFO.pixelRate` or the EDID timing) × 24 (8 bpc) or × 30 (10 bpc).

**Detection rule:** EDID max refresh at native (range limits / highest DTD or DisplayID timing) **>** the max that
`EnumDisplaySettingsEx` offers at native. The link or path is the bottleneck. Then say, in this order:
1. The monitor is on the motherboard (iGPU) output → move the cable to the graphics card. The `monitor-gpu` check
   already exists.
2. `outputTechnology` = 5 (HDMI) and the requirement exceeds 14.4 Gbit/s → use DisplayPort or an HDMI 2.1 port and
   cable.
3. DP: use a VESA-certified DP 1.4 / DP80 cable. Some monitors also have an OSD "DisplayPort version" switch (1.2/1.4).
4. Docks, KVMs and adapters often cap the bandwidth.

The negotiated DP link rate itself is not exposed by Windows. Verified here: both monitors are DP (tech 10), EDID max =
offered max (240/240, 180/180), so there's no bottleneck ✔.

### `display-mixed-refresh`: Мониторы с разной частотой → **INFO-ONLY**

- **Facts:**
  - Since Windows 10 2004 the compositor runs each monitor at its own rate, which reduced the old "60 Hz second
    monitor drags the 144 Hz one" stutter. That fix was partial, especially for windowed apps (PCGH; Blur Busters).
  - Windows 11 build 25915 improved the refresh-rate logic to "allow different refresh rates on different monitors"
    depending on content.
  - This PC runs 240 + 180 Hz on one GPU.
- **Recommendations:**
  - Keep each monitor at its own max.
  - Play in exclusive fullscreen, or flip-model borderless with `windowed-games-optimization` already on, on the
    primary monitor.
  - If a video on the second monitor makes the game stutter or flicker, pause it, or use the existing ADVANCED
    `mpo-disable` fix (Optimize).
  - Trade-off: several high-refresh monitors can keep NVIDIA memory clocks and fans up at idle (reported for RTX 30/40).
    If idle power or noise matters, the user may lower the **secondary** monitor manually (for example 180 → 120 Hz)
    through `display-refresh-rate`. That's a manual choice, never a default.

### `display-primary`: Основной монитор → **INFO-ONLY**

- **Read:** `DISPLAY_DEVICE_PRIMARY_DEVICE` (0x4). Here the primary is the 240 Hz X27U ✔.
- **Recommendation:** games open on the primary monitor by default, so the fastest monitor should be primary. Warn only
  when a slower external monitor is primary.
- **Write:** not offered. Changing the primary needs `CDS_SET_PRIMARY` plus moving every monitor so the new primary sits
  at (0,0), which is easy to get wrong. Link to `ms-settings:display` («Сделать основным дисплеем»).

### `display-ddc-brightness`: Яркость и контраст монитора (DDC/CI) → **SAFE-OPTIONAL (manual sliders, no recommended value)**

- **Read:**
  - `EnumDisplayMonitors` → `GetNumberOfPhysicalMonitorsFromHMONITOR` → `GetPhysicalMonitorsFromHMONITOR` →
    `GetMonitorBrightness(h, out min, out cur, out max)` / `GetMonitorContrast` (dxva2.dll).
  - Free the handles with `DestroyPhysicalMonitors`.
  - Verified ✔: X27U 100 (0..100), G5 30 (0..50), contrast 50 on both.
  - `GetMonitorCapabilities` failed on the X27U although the getters work, so gate on the **getter succeeding**, not on
    caps.
- **Write:** `SetMonitorBrightness(h, value)` / `SetMonitorContrast`. Each call takes about 50 ms (Microsoft).
  - Write only on slider release (debounce ≥ 300 ms), never per tick.
  - Read back and compare. If it doesn't match, restore the old value and disable the slider for that monitor.
  - Internal laptop panels use WMI `root\wmi:WmiMonitorBrightnessMethods.WmiSetBrightness` instead. That is
    documented, but out of scope for desktops.
- **Revert:** journal the original value (record kind `ddc`, keyed by `monitorDevicePath`). Undo = set it back.
  Restore points don't cover monitor NVRAM.
- **Admin:** no.
- **Recommended:** none automatic. This is room- and taste-dependent. Show the two sliders on the display card.
- **Risk:**
  - Microsoft warns: "Many monitors don't fully implement that standard; so your use of these commands might result in
    undefined monitor behavior. We don't recommend using these functions for arbitrary monitors without physically
    validating that they work as intended."
  - Hence: brightness and contrast only, the read-back check, no batch writes, and the monitor's own OSD always
    overrides.
  - Some modes (HDR, sRGB lock, OLED) disable the setting → `ERROR_DISABLED_MONITOR_SETTING`; show «недоступно в
    текущем режиме монитора».
  - Never call `SaveCurrentMonitorSettings`, `RestoreMonitorFactory*Defaults` or any low-level `SetVCPFeature`.

### `display-drr`: Динамическая частота обновления (DRR) → **INFO-ONLY**

- Windows 11 only. Microsoft: it "requires that a display supports VRR and a refresh rate of at least 120 Hz". It
  "should not affect games designed for VRR, but it can limit the maximum refresh rate of other games", and "the best
  mitigation is to disable DRR" if a game seems slower.
- It is offered on laptops with qualifying internal panels; not applicable on this desktop.
- MONOLITH only explains it and links to Settings.

### Orientation and Night light → **INFO-ONLY**

- **Orientation:** the portrait G5 (`dmDisplayOrientation` 3 / `DISPLAYCONFIG_ROTATION_ROTATE270` = 4) must be
  **preserved** by every mode change: don't set `DM_DISPLAYORIENTATION`, and swap width/height when matching portrait
  modes. MONOLITH doesn't offer rotation.
- **Night light:** stored as an undocumented CloudStore blob → no writes. Link to `ms-settings:nightlight`.

---

## 4. DO-NOT-INCLUDE (common "display tweaks" online)

| ID | "Tweak" | Why not |
|---|---|---|
| `display-overclock-edid` | Monitor overclocking, custom resolutions/timings, CRU, `EDID_OVERRIDE` under `Enum\DISPLAY\…\Device Parameters` | Out-of-spec timings can give black screens, frame skipping or panel stress. An EDID override survives reboots and can leave no picture, so recovery needs Safe Mode. MONOLITH only uses modes the driver offers. |
| `display-tdr` | `TdrDelay`, `TdrLevel=0`, `TdrDdiDelay` "to stop driver crashes" | Microsoft: "End users shouldn't manipulate these registry keys. Applications shouldn't manipulate these registry keys outside of targeted testing or debugging during driver development." `TdrLevel=0` turns a recoverable GPU hang into a frozen PC. |
| `display-dpi-registry` | Setting scaling through `PerMonitorSettings\DpiValue`, `LogPixels`, or undocumented `DisplayConfigSetDeviceInfo` types | Undocumented, needs sign-out, breaks per-monitor DPI and the recommended baseline. No FPS effect. |
| `display-dwm-disable` | "Disable DWM / desktop composition / transparency for FPS" by killing or suspending dwm.exe or via IFEO | Composition can't be disabled on Windows 8+. Killing DWM gives a black screen and breaks HDR, VRR, flip model and Auto HDR. The modern path (flip model, `windowed-games-optimization`) is already in the catalog. |
| `display-force-max-bpc` | "Always force 10/12-bit for better image" | SDR content is 8-bit. At high refresh, forcing higher bpc pushes the link into DSC or chroma subsampling, or lowers the max refresh. It's a driver setting anyway. |
| `display-ddc-vendor` | Changing input (VCP 0x60), power mode (0xD6), factory reset (0x04), colour preset (0x14) or vendor codes E0–FF over DDC/CI | Can leave the user with no picture (wrong input, monitor off) or wipe calibration and OSD settings. Vendor codes are undocumented (Microsoft warns about undefined behaviour). |
| `display-hdr-registry` | Toggling HDR by editing `MonitorDataStore` or other `GraphicsDrivers` values | Undocumented OS internals. The documented API exists (`display-hdr`). |
| `display-fso-global` | Global FSO disable | Already DO-NOT-INCLUDE in gaming_tweaks.md (removes the flip/HDR/VRR path, no measured gain). |
| `display-239-vs-240` | "Your monitor runs at 239 Hz, force true 240" (CRU) | The 239.97 Hz timing *is* the monitor's 240 Hz mode (EDID DisplayID, verified). There is no gain. Forcing a custom 240.000 timing is `display-overclock-edid`. |
| `display-60hz-desktop` | "Set the desktop to 60 Hz to save GPU" as a default | The cost is moving every frame and the cursor at 60 Hz. Only a manual choice for secondary monitors (`display-mixed-refresh`). |

---

## 5. Detection recipes (read-only)

1. **Monitors and modes:** `DisplayProbe.Read()` (existing). Add:
   - `DISPLAYCONFIG_TARGET_PREFERRED_MODE` (type 3) for the native size;
   - `ENUM_REGISTRY_SETTINGS` (−2) to tell a temporary mode from a saved one;
   - the exact rational refresh and `outputTechnology` from the path;
   - `monitorDevicePath` and the EDID ids from `DISPLAYCONFIG_TARGET_DEVICE_NAME`.
2. **EDID:**
   - Map `monitorDevicePath` `\\?\DISPLAY#ACR1132#5&30ca09c6&0&UID4355#{…}` → `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\ACR1132\5&30ca09c6&0&UID4355\Device Parameters\EDID`.
     It is user-readable ✔; WMI `WmiMonitorDescriptorMethods` is the alternative.
   - Read the bytes as `int[]`. PowerShell `-shl` on `byte` silently truncates.
   - Parse:
     - byte 20: digital, bpc, interface 5 = DP;
     - descriptor `0xFD`: V/H range plus max pixel clock (+255 offset flags in EDID 1.4);
     - first DTD = the preferred timing (**can be 60 Hz**);
     - CTA-861 extension (tag 0x02): HDR static metadata (extended tag 6; EOTF bit 2 = PQ; max luminance = 50·2^(CV/32)),
       colorimetry (ext tag 5, BT.2020), AMD VSDB OUI `00001A` (FreeSync range), HDMI Forum VSDB OUI `C45DD8`;
     - DisplayID extension (tag 0x70): type-I timings in block 0x03/0x22. **High-refresh timings are often only here** ✔.
   - Several stale `Enum\DISPLAY\SAM74A7\…` instances exist (old ports). Use only the instance from the active path.
3. **HDR/ACM/bpc:** type 15 on build ≥ 26100, else type 9. SDR white level: type 11.
4. **DPI:** `GetDpiForMonitor` from a thread with `SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`.
5. **DDC/CI:** dxva2 getters. They take ~40–50 ms each, so run on `Task.Run` and never on the UI thread.
6. **State to refuse writes:** `SM_REMOTESESSION`, `SHQueryUserNotificationState`, Game Boost active,
   `advancedColorLimitedByPolicy`, not elevated (read-only mode).

---

## 6. What MONOLITH would show on this PC

| Monitor | Mode | Recommendation |
|---|---|---|
| X27U W3 (primary, DP, RTX 5070 Ti) | 2560×1440 @ 239,97 Гц, 8 bpc, 100 % | ✔ Max refresh at native. HDR supported and off: «для HDR-игр — Win+Alt+B; при постоянном HDR пройдите калибровку HDR». VRR 48–240 Гц: turn on G-SYNC Compatible in the NVIDIA App; cap ≤ 237 FPS or use Reflex. The 240 Hz link is near the DP 1.4 limit: if the screen blinks, check or replace the DP cable. |
| Odyssey G5 (portrait, DP, RTX 5070 Ti) | 1440×2560 @ 179,95 Гц, 8 bpc, 100 % | ✔ Max refresh at native. HDR: keep off (≈400 nits HDR10). Optional: 120 Hz to lower idle GPU power (manual). |
| Both | — | Scaling = recommended. Primary = fastest. Both on the discrete GPU. DDC/CI sliders available (X27U 100/100, G5 30/50). |

---

## Sources

- Microsoft, ChangeDisplaySettingsExW (flags, `CDS_TEST`, `CDS_UPDATEREGISTRY`, NULL = return to registry mode, multi-display batching): https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-changedisplaysettingsexw
- Microsoft, SetDisplayConfig (temporary vs saved, `SDC_USE_DATABASE_CURRENT`, `ERROR_ACCESS_DENIED` for remote sessions): https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig
- Microsoft, SetDisplayConfig Summary and Scenarios (the display control panel uses it for resolution and refresh): https://learn.microsoft.com/en-us/windows-hardware/drivers/display/setdisplayconfig-summary-and-scenarios
- Microsoft, Implementing display connection and configuration (CCD can't enumerate source modes): https://learn.microsoft.com/en-us/windows-hardware/drivers/display/connecting-and-configuring-displays
- Microsoft, DISPLAYCONFIG_DEVICE_INFO_TYPE (types 1–11 documented; GET_ADVANCED_COLOR_INFO_2 / SET_HDR_STATE / SET_WCG_STATE listed): https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ne-wingdi-displayconfig_device_info_type
- Microsoft, DISPLAYCONFIG_TARGET_PREFERRED_MODE: https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_preferred_mode
- Microsoft, DisplayConfigSetDeviceInfo: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-displayconfigsetdeviceinfo
- LizardByte libdisplaydevice (uses `GET_ADVANCED_COLOR_INFO_2` + `SET_HDR_STATE` type 16 on build ≥ 26100): https://github.com/LizardByte/libdisplaydevice/blob/master/src/windows/win_api_layer.cpp
- Microsoft, Use DirectX with Advanced Color (HDR/WCG/ACM, SDR white ~200 nits, DisplayHDR recommendation, GPU/WDDM requirements): https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range
- Microsoft Support, HDR settings in Windows: https://support.microsoft.com/en-us/windows/hdr-settings-in-windows-2d767185-38ec-7fdc-6f97-bbc6c5ef24e6
- Microsoft Support, Use Auto HDR for better gaming: https://support.microsoft.com/en-us/article/0cce8402-3de5-4512-a742-e027ca7aa79c
- Microsoft DirectX blog, Auto HDR intensity slider (2022): https://devblogs.microsoft.com/directx/exciting-additions-to-your-auto-hdr-experience-on-pc/
- ElevenForum, Auto HDR registry token (community): https://www.elevenforum.com/t/turn-on-or-off-auto-hdr-in-windows-11.891/
- Windows 11 24H2 Auto HDR issue fixed by KB5051987 (safeguard 55382406): https://www.windowslatest.com/2025/02/12/microsoft-unlocks-windows-11-24h2-for-more-gaming-pcs-with-feb-11-2025-update/ and https://www.thefpsreview.com/2024/12/19/windows-11s-auto-hdr-feature-may-cause-games-to-stop-responding-or-display-incorrect-colors-microsoft-reveals/
- Microsoft Support, Calibrate your HDR display using the Windows HDR Calibration app: https://support.microsoft.com/en-gb/windows/hardware/display-graphics/calibrate-your-hdr-display-using-the-windows-hdr-calibration-app
- Microsoft Support, Game Bar keyboard shortcuts (Win+Alt+B = HDR on/off): https://support.microsoft.com/en-us/help/4026387 ; PC Gamer: https://www.pcgamer.com/windows-11-shortcut-hdr/
- Microsoft Support, Change your display refresh rate in Windows (DRR, VRR notes): https://support.microsoft.com/en-us/windows/change-your-display-refresh-rate-in-windows-c8ea729e-0678-015c-c415-f806f04aae5a
- Microsoft Support, Change your screen resolution and layout (native / recommended scale): https://support.microsoft.com/en-us/windows/change-your-screen-resolution-and-layout-in-windows-5effefe3-2eac-e306-0b5d-2073b765876b
- Microsoft Learn, ms-settings URIs (`display-advanced` opens System > Display on Win11; `display`, `display-advancedgraphics`, `nightlight`): https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app
- "Keep these display settings" 15 s revert (observed): https://support.ksu.edu/TDClient/30/Portal/KB/Article/267/Changing-screen-resolution-in-Windows-11 ; https://helpdeskgeek.com/change-refresh-rate-windows-11/
- PCWorld, high-refresh monitors left at 60 Hz: https://www.pcworld.com/article/3620954/how-to-change-your-monitors-refresh-rate-and-why-gamers-should-check-pronto.html
- Wikipedia, DisplayPort (HBR2/HBR3/UHBR effective rates; 1440p240 = 24.62 Gbit/s at 8 bpc, 30.77 at 10 bpc): https://en.wikipedia.org/wiki/DisplayPort
- Wikipedia, HDMI (2.0 = 14.4 Gbit/s, 2.1 FRL ≈ 42 Gbit/s effective): https://en.wikipedia.org/wiki/HDMI
- Blur Busters, G-SYNC 101 optimal settings: https://blurbusters.com/gsync/gsync101-input-lag-tests-and-settings/14/
- Mixed refresh rates: PCGH on Windows 10 2004: https://www.pcgameshardware.de/Monitor-Display-Hardware-154105/News/Windows-10-Build-2004-loest-Problem-mit-Mischbetrieb-von-Monitoren-bei-144-Hertz-teilweise-1343758/ ; Blur Busters: https://blurbusters.com/new-windows-update-fixes-mixed-hz-multimonitor/ ; Windows 11 build 25915: https://blogs.windows.com/windows-insider/2023/07/27/announcing-windows-11-insider-preview-build-25915 ; https://mobilesyrup.com/2023/07/31/windows-11-multiple-high-refresh-rate-monitors-improvement/
- RTINGS / TFTCentral on DisplayHDR 400 (no local dimming required, little real HDR): https://www.rtings.com/monitor/learn/display-hdr ; https://tftcentral.co.uk/articles/why-your-hdr-monitor-is-probably-not-hdr-at-all-and-why-displayhdr-400-needs-to-go
- Microsoft Q&A, HDR toggle snapping back due to NVIDIA colour override: https://learn.microsoft.com/en-us/answers/questions/2796233/hdr-and-advanced-color-setting-automatically-switc
- Auto colour management on SDR / 24H2 behaviour: https://pureinfotech.com/enable-new-auto-color-management-acm-windows-11/ ; https://www.elevenforum.com/t/how-to-stop-windows-11-24h2-from-forcing-automatically-manage-color-for-apps-to-on-when-switching-bpc-mode.31905/
- Microsoft, SetMonitorBrightness (MCCS warning, ~50 ms): https://learn.microsoft.com/en-us/windows/win32/api/highlevelmonitorconfigurationapi/nf-highlevelmonitorconfigurationapi-setmonitorbrightness ; Using the high-level monitor configuration functions: https://learn.microsoft.com/en-us/windows/win32/monitor/using-the-high-level-monitor-configuration-functions
- Microsoft, TDR registry keys (debugging only): https://learn.microsoft.com/en-us/windows-hardware/drivers/display/tdr-registry-keys
- Acer Predator X27U W3 (QD-OLED 1440p 240 Hz, DP 1.4): https://www.acer.com/gb-en/predator/monitors/x27u-qd-oled/pdp/UM.GXXEE.504 ; https://www.mydealz.de/deals/acer-predator-x27uw3bmiiprx-265-qhd-qd-oled-gaming-monitor-240-hz-003-ms-hdr400-2806297
