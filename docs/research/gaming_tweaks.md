# MONOLITH: catalog of Windows gaming tweaks (Win11 23H2–26H2, Win10 22H2)

## 0. Conventions and the dev machine's current state (read-only)

**Labels**
- **absent→delete**: the value doesn't exist by default, so reverting means deleting the value, not writing a "default" into it.
- **HKCU writes from an elevated process**: the app may run under a different admin account through UAC. In that case HKCU points at the admin's hive, not the logged-in user's. Find the interactive user's SID (the owner of explorer.exe, or WTSQueryUserToken) and write to `HKU\<SID>\…`.
- **Restore points**: `SystemRestore.CreateRestorePoint` / `Checkpoint-Computer` silently skips creation if a restore point was made in the last 24 h (the documented Win8+ behaviour). Check that a new point actually appeared by comparing the SequenceNumber. If it didn't, rely on MONOLITH's own undo journal. Don't silently change `SystemRestorePointCreationFrequency`, because that is itself a system change.

**Values read on this machine (2026-10-26)**

| Item | Value |
|---|---|
| OS | Win11 Pro, DisplayVersion **26H2**, build 26300; S3 + Hibernate, **no Modern Standby** |
| Power plan | **High performance** (`8c5e7fda-…`) is active. Only the 3 stock plans exist: no Ultimate, no "AMD Ryzen Balanced" |
| CPU | 9800X3D (8C/16T, **single CCD**). PnP devices present: `AMD 3D V-Cache Performance Optimizer` (ACPI\AMDI0101) and `AMD PPM Provisioning File` (ACPI\AMDI0052). Service `amd3dvcacheSvc` is Running/Auto |
| Balanced plan, AC (DC) | PROCTHROTTLEMIN 0 % (5 %), CPMINCORES 100 % (10 %), ASPM 1 Moderate (2), USB selective suspend 1 (1) |
| High performance, AC | PROCTHROTTLEMIN 100 %, CPMINCORES 100 %, ASPM 0 Off, USB selective suspend 1 |
| Game Bar | `Microsoft.XboxGamingOverlay` 7.325.7221.0. `HKCU\Software\Microsoft\GameBar` has no values, i.e. defaults |
| GameConfigStore | GameDVR_Enabled=1, FSEBehaviorMode=2, HonorUserFSEBehaviorMode=0, DXGIHonorFSEWindowsCompatible=0, EFSEFeatureFlags=0 |
| GameDVR | AppCaptureEnabled and HistoricalCaptureEnabled are **absent**. The `AllowGameDVR` policy is absent |
| HAGS / DirectX | `HwSchMode` **absent**. `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` **absent** |
| Mouse | MouseSpeed "1", Threshold1 "6", Threshold2 "10" (pointer acceleration **on**) |
| MMCSS | SystemResponsiveness 0x14, NetworkThrottlingIndex 0xA. Games task: GPU Priority 8, Priority 2, "Medium", SFIO "Normal", Clock Rate 0x2710 |
| Scheduler | Win32PrioritySeparation 0x2. `GlobalTimerResolutionRequests` absent. Dwm `OverlayTestMode` absent |
| VBS/HVCI | VBS **running** (status 2), HVCI configured and running {2}. HVCI `Enabled`=1, `Locked` absent. KeyGuard running |
| UI | EnableTransparency=0 (already changed by the user). Explorer\Serialize absent |
| Delivery Optimization | DownloadMode=Lan, MaxUploadRatePct 50, UploadLimitMonthlyGB 5120 (all DefaultProvider). The S-1-5-20 settings key is absent |
| RAM | 2× G.Skill F5-6000J3636F16G (DDR5, SMBIOS 34), Speed 6000 / ConfiguredClockSpeed 6000, so **EXPO is active** |
| GPU | RTX 5070 Ti drives the monitor at 239 Hz; AMD iGPU is active too (hybrid). `nvidia-smi` BAR1 = 16384 MiB ≥ FB 16303 MiB, so **ReBAR is on** |

---

## 1. Ryzen X3D: what the Gaming profile must not break

**How it works.** The AMD chipset driver installs two components:
- The **3D V-Cache Performance Optimizer**: kernel driver `amd3dvcache` plus service `amd3dvcacheSvc`.
- The **PPM Provisioning File**: core-parking parameters for the **Balanced** plan.

On **dual-CCD** X3D chips (7900X3D, 7950X3D, 9900X3D, 9950X3D) the driver parks the frequency CCD when Windows flags a running game. Windows flags it through Game Bar detection, then Game Mode, then Effective Power Mode = GameMode. Sources:
- Bitsum and the VRChat wiki: Game Bar must be present and up to date, Game Mode must be on, and AMD recommends the Balanced plan. Users report no parking under High performance or Ultimate, or under Process Lasso's Bitsum plan.
- This machine's registry shows the driver keys off the Windows Effective Power Mode directly:
  `HKLM\SYSTEM\CurrentControlSet\Services\amd3dvcache\Preferences\EffectivePowerMode\GameMode` = {Mode=5 (EffectivePowerModeGameMode), Type=1}.
  - `DefaultType`=0.
  - `…\Preferences\App\League of Legends` = {EndsWith="League of Legends.exe", Type=0}.
  - My interpretation of Type (0 = prefer frequency CCD, 1 = prefer cache CCD) is **inferred, not documented by AMD**.
- SteamVR silently switches the plan to High performance, which turns the optimisation off (VRChat wiki).

**Rules for MONOLITH**
1. **Detecting an X3D chip:** `Win32_Processor.Name` contains `X3D`.
   - Dual-CCD = 12 or 16 cores.
   - 9950X3D2: cache on both CCDs, so steering helps only partly.
   - Also check that the `ACPI\AMDI0101` device and the `amd3dvcacheSvc` service exist.
2. **On dual-CCD X3D, the Gaming profile must not:**
   - change the plan away from Balanced;
   - turn off Game Mode;
   - remove or disable Game Bar;
   - touch CPMINCORES or PROCTHROTTLEMIN;
   - stop `amd3dvcacheSvc`.

   Instead it should *ensure* all of these and recommend the BIOS setting `CPPC Dynamic Preferred Cores = Auto/Driver`. Also tell the user about Win+G → "Remember this is a game" for games that aren't recognised.
3. **Single-CCD X3D (this 9800X3D, plus 5800X3D/7800X3D):** there is no CCD to choose, so the optimizer has no effect. The plan choice barely changes FPS. The recommendation is Balanced (AMD's guidance, lower idle power). High performance does no harm. Don't stop the service.
4. Game Boost sessions that switch plans must check for X3D.

---

## 2. Catalog

### A. Graphics and presentation

#### `game-mode`: Игровой режим / Game Mode → **SAFE-DEFAULT (ensure ON)**
- **What it does:** suppresses Windows Update driver installs and restart notifications during play and gives the game priority in the scheduler. On dual-CCD X3D it is also the trigger for CCD steering.
- **Mechanism:** HKCU `Software\Microsoft\GameBar` → `AutoGameModeEnabled` DWORD **1**. Legacy value: `AllowAutoGameMode`.
- **Default / revert:** absent→delete (absent means ON). Takes effect at the next game launch; no reboot.
- **Detection:** absent or 1 = ON.
- **Evidence:** Microsoft promises only "less FPS variability depending on game and system". Independent tests show roughly 0 % change in average FPS. The 2017 stutter bugs are history.
- **Risk:** minimal. Turning it OFF is allowed only as a troubleshooting option (ADVANCED), never on X3D.

#### `hags`: Аппаратное планирование GPU / Hardware-accelerated GPU scheduling → **SAFE-DEFAULT only for RTX 40/50 when HwSchSupported, otherwise SAFE-OPTIONAL**
- **Mechanism:** HKLM `SYSTEM\CurrentControlSet\Control\GraphicsDrivers` → `HwSchMode` DWORD: **2** = on, 1 = off.
- **Default / revert:** absent means the driver/OS default; absent→delete. **Reboot required.**
- **Detection:** `D3DKMTQueryAdapterInfo(KMTQAITYPE_WDDM_2_7_CAPS)` → `HwSchSupported` / `HwSchEnabled` / `HwSchEnabledByDefault` for each adapter. Use the registry only as a fallback. On this machine the value is absent and I couldn't check the actual state: dxdiag didn't run in the sandbox.
- **Evidence:** FPS change is within ±5 % noise. HAGS **is required for DLSS Frame Generation / Multi Frame Generation** (relevant for the RTX 5070 Ti).
- **Risk:** isolated reports of stutter and VR/audio problems. Never force it on an adapter where `HwSchSupported`=0.
- **Windows:** Win10 2004+ / Win11.

#### `windowed-optimizations`: Оптимизация для оконных игр / Optimizations for windowed games → **SAFE-DEFAULT**
- **What it does:** moves DX10/11 games in windowed and borderless mode from the blt model to the flip model. This gives lower latency, VRR and Auto HDR (Microsoft).
- **Mechanism:** HKCU `Software\Microsoft\DirectX\UserGpuPreferences` → REG_SZ `DirectXUserGlobalSettings`, token `SwapEffectUpgradeEnable=1;`.
  - **Parse and merge tokens** (`VRROptimizeEnable`, `AutoHDREnable`, …) separated by `;`. Never overwrite the whole string.
  - Per-game opt-out: a value named after the full exe path with `SwapEffectUpgradeEnable=0;`.
- **Default / revert:** the string is absent, which means the OS default (the toggle is off by default; turning on Auto HDR forces it on). Revert = restore the exact previous string, or delete it if it was absent.
- **Key cleanup on revert:** when `UserGpuPreferences` (and `Software\Microsoft\DirectX`) did not exist, the first token's apply creates them (`New-Item -Force`). The undo script then carries `$mk=$true;$mp=…;` and, once the last token is gone, removes the key and that parent again — only while they hold no values and no subkeys (per-app `GpuPreference` values live in the same key). A second token applied later inherits the cleanup duty from the active record, so any revert order leaves nothing behind. Without the flags the script text is byte-identical to older builds' records.
- **Applies:** after restarting the game.
- **Risk:** rare incompatibilities or tearing in individual games. **Win11 only.**

#### `vrr-windows`: Переменная частота обновления / Variable refresh rate → **SAFE-OPTIONAL**
- **What it does:** VRR for DX11 games without native VRR support. It does not replace G-SYNC/FreeSync in the GPU driver.
- **Mechanism:** the same string, token `VRROptimizeEnable=1;`.
- **Default:** off (community-documented). The toggle only appears when the display and driver support VRR.
- **Risk:** flicker on some VA/OLED panels in loading screens. Show it only when the display is VRR-capable.
- **Windows:** Win10 1903+ / Win11.

#### `gpu-preference-per-app`: GPU для игры (гибрид) / per-app GPU preference → **SAFE-DEFAULT on hybrid systems (only for exes detected as games)**
- **Mechanism:** HKCU `…\DirectX\UserGpuPreferences` → value name = full exe path, REG_SZ `GpuPreference=2;`. The values are 0 = auto, 1 = power saving, 2 = high performance.
- **Default / revert:** absent→delete.
- **Relevance:** this machine has an AMD iGPU and an RTX dGPU.
- **Detection:** read the value and enumerate adapters with DXGI `EnumAdapterByGpuPreference`.

#### `fso-disable-per-app`: Отключить оптимизацию во весь экран / Disable fullscreen optimizations → **per game: SAFE-OPTIONAL (troubleshooting); global: DO-NOT-INCLUDE**
- **Per game:** HKCU `Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers` → value = exe path, REG_SZ `~ DISABLEDXMAXIMIZEDWINDOWEDMODE`. Merge with existing flags such as `HIGHDPIAWARE`. Default absent→delete.
- **Global** (HKCU `System\GameConfigStore`):
  - Tweak: `GameDVR_FSEBehaviorMode`=2, `GameDVR_HonorUserFSEBehaviorMode`=1, `GameDVR_DXGIHonorFSEWindowsCompatible`=1, `GameDVR_FSEBehavior`=2.
  - Defaults here: 2, 0, 0, FSEBehavior absent.
- **Evidence:** with the flip model, FSO is effectively exclusive fullscreen plus fast Alt-Tab. Turning FSO off helps individual old DX9/11 titles (stutter, wrong refresh rate). The global version has no measured gain and removes the overlay/HDR path.

#### `mpo-disable`: Отключить MPO / Disable Multiplane Overlay → **ADVANCED (this is a fix, not a boost)**
- **Mechanism:** HKLM `SOFTWARE\Microsoft\Windows\Dwm` → `OverlayTestMode` DWORD **5**. Default absent→delete. Reboot.
- **Purpose:** fixes flicker, black screens and browser/video stutter with multiple monitors at different refresh rates (NVIDIA support article 5157, `mpo_disable.reg`).
- **Caveats:**
  - Many reports say **24H2+ ignores the key**.
  - Microsoft documents `OverlayMinFPS` DWORD **0** (same key). It reduces how often MPO engages but is "may be removed in future releases".
  - Turning MPO off **raises GPU load and latency** in borderless/windowed modes.
- **Detection:** read the value. Per-display MPO capabilities ("MPO MaxPlanes") are in `dxdiag /t`.

### B. Xbox Game Bar and Game DVR

#### `gamedvr-background-recording-off`: Фоновая запись / "Record what happened" → **SAFE-DEFAULT (ensure OFF)**
- **Mechanism:** HKCU `Software\Microsoft\Windows\CurrentVersion\GameDVR` → `HistoricalCaptureEnabled` DWORD **0**.
- **Default / revert:** absent means OFF on Win11, so this is usually a no-op. Absent→delete.
- **Evidence:** constant NVENC/AMF encoding costs roughly 1–5 % FPS plus VRAM.

#### `gamedvr-capture-off`: Отключить захват Game Bar / Disable Game Bar capture → **SAFE-OPTIONAL**
- **Mechanism:**
  - HKCU `…\CurrentVersion\GameDVR` → `AppCaptureEnabled` DWORD 0. Default absent, which means enabled; absent→delete.
  - HKCU `System\GameConfigStore` → `GameDVR_Enabled` DWORD 0. Default **1**.
- **Effect:** turns off Win+Alt+R and screenshots. The overlay, Game Mode and game detection keep working. There is no benefit when nothing is being recorded.

#### `gamedvr-policy`: Политика AllowGameDVR → **DO-NOT-INCLUDE (in Gaming)**
- **Mechanism:** HKLM `SOFTWARE\Policies\Microsoft\Windows\GameDVR` → `AllowGameDVR` DWORD 0 (absent by default).
- **Reason:** it is a machine-wide policy that makes Settings show "managed by your organization", and it adds no speed over the user-level setting. It belongs in a "privacy/hardening" section at most (it is a DISA STIG item).

#### `gamebar-remove`: Удаление/отключение Game Bar → **DO-NOT-INCLUDE**
- **Reason:** removing `Microsoft.XboxGamingOverlay` breaks game detection, which Game Mode and the X3D driver rely on, and causes `ms-gamebar:` pop-ups.

### C. Power

#### `power-plan-high`: Высокая производительность / High performance → **SAFE-OPTIONAL (desktop, not dual-CCD X3D); DO-NOT-INCLUDE on dual-CCD X3D**
- **Mechanism:** `powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` or `PowerSetActiveScheme`.
- **Default / revert:** the default is Balanced `381b4222-f694-41f0-9685-ff5bb260df2e`. Save the previous GUID from `PowerGetActiveScheme`. No reboot.
- **Modern Standby (S0):** only Balanced exists. Detect with `CallNtPowerInformation(SystemPowerCapabilities).AoAc` or `powercfg /a`.
- **Evidence:** on CPPC2/EPP CPUs the average FPS gain is about 0–2 %, at the cost of higher idle power.
- **Alternative:** on Balanced, use Settings → Power mode "Best performance" (catalog item `power-mode-best-performance`, below). It is the official route and works with Modern Standby. Its compatibility with X3D steering is unverified.

#### `power-mode-best-performance`: Режим питания «Максимальная производительность» / Windows 11 power mode → **SAFE-OPTIONAL (Windows 11, Balanced plan, AC only; not on dual-CCD X3D)**
- **What it is:** the Settings → System → Power → «Режим питания» choice. Microsoft calls each mode a "lite power plan" (CPU PPM settings + power throttling) on top of Balanced or plans derived from it; the Balanced plan stays active. The honest alternative to switching plans on laptops.
- **Mechanism (documented since Feb 2025, powrprof.dll, Windows 11 desktop apps):** `PowerGetUserConfiguredACPowerMode` reads, `PowerSetUserConfiguredACPowerMode(GUID)` sets it and "immediately updates" the mode. The only supported values: `GUID_POWER_MODE_NONE` 00000000-0000-0000-0000-000000000000 (Balanced), `GUID_POWER_MODE_BEST_EFFICIENCY` 961cc777-2547-4f9d-8174-7d86181b8a7a, `GUID_POWER_MODE_BEST_PERFORMANCE` ded574b5-45a0-4f42-8737-46345c09c238. Earlier notes ("setter undocumented, open ms-settings:powersleep") are obsolete.
- **AC only.** The DC setter is never called (laptops: touch AC only).
- **Never** write `ActiveOverlayAcPowerScheme` under `…\Control\Power\User\PowerSchemes` directly: undocumented and not applied live.
- **Apply / undo:** a journaled PowerShell snippet (P/Invoke, like the DirectX script) through `ChangeSession.RunCommandAsync`, target `powermode:ac`. The undo carries the original GUID read before the change; the journal check accepts only the three documented GUIDs, rebuilt by the catalog code. If Windows ever reports another GUID, MONOLITH refuses to change the mode (it could not journal an exact undo). Older Windows 11 builds without the export → not applicable.
- **Applies to:** Windows 11, active scheme = Balanced (the mode exists only there), not dual-CCD X3D (interplay with the AMD driver's EffectivePowerMode steering is undocumented).
- **Evidence / effect:** measurable mainly on laptops and OEM-tuned systems; often small on desktops. Slightly higher idle power. Not pre-selected in any profile; the Games boost stays as it is.

#### `power-throttling-off`: «Turn off Power Throttling» → **DO-NOT-INCLUDE on desktops (placebo); laptops at most ADVANCED, not offered**
- **Mechanism:** `HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling\PowerThrottlingOff` DWORD 1 (ADMX Power, Pro+; a plain value, so it also works on Home). Absent by default.
- **Reason:** per Microsoft's Quality of Service table, Low/Utility QoS throttling (EcoQoS) applies only on battery, and a focused game window is already High QoS. On desktops it changes nothing; on laptops it only costs battery.
- `DisableUserPresenceQos` (24H2 2025.05+) matters only for unattended benchmarks on battery: not offered.

#### `power-plan-ultimate`: Максимальная производительность / Ultimate Performance → **ADVANCED (desktop only, not dual-CCD X3D)**
- **Mechanism:** `powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61` returns a **new GUID**. Then run `/setactive <new>`.
- **Revert:** `/setactive <previous>` followed by `powercfg -delete <new>`. The plan doesn't exist by default (it's absent here).
- **Evidence:** about the same as High performance for games; more heat and power.
- **Do not use** the `PlatformAoAcOverride`=0 hack. It disables Modern Standby and is DO-NOT-INCLUDE.

#### `power-plan-balanced`: Сбалансированная (X3D) / Balanced → **SAFE-DEFAULT on dual-CCD X3D; Recommended profile everywhere**
- Same mechanism as above.
- On this 9800X3D it is a recommendation only: it doesn't affect FPS and lowers idle power.

#### `cpu-min-state`: Мин. состояние процессора / Minimum processor state → **ADVANCED (not on X3D)**
- **Mechanism:** `powercfg /setacvalueindex SCHEME_CURRENT 54533251-82be-4824-96c1-47b60b740d00 893dee8e-2bef-41e0-89c6-b55d0929964c 100`, then `powercfg /setactive SCHEME_CURRENT`.
- **Defaults:** Balanced AC 5 % in stock Windows (**0 % here**, set by the AMD PPM provisioning), DC 5 %. High performance 100 %. Save the AC and DC indexes for revert.
- **Evidence:** C-states and CPPC still apply, so this doesn't pin clocks. The effect is about zero.
- **Undo precision (all powercfg AC items: cpu-min-state, usb-selective-suspend, pcie-aspm, Wi-Fi power):** Windows stores only modified values under `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{scheme}\{subgroup}\{setting}\ACSettingIndex`; stock plans take the rest from `PowerSettings\…\DefaultPowerSchemeValues`. `PowerReadACValueIndex` returns the effective value either way, and powercfg has no per-setting "back to default". The undo therefore writes the original number explicitly: same effective value, but an override where there was none. Do **not** delete the override from the registry (the keys belong to the Power service; Administrators can only read them, so a journaled delete would fail at revert time) and do not call `PowerRestoreIndividualDefaultPowerScheme` (it resets every setting of the plan). MONOLITH reads (read-only) whether an override existed, says so in the journal record («по умолчанию схемы; при откате будет записано явно»), and the Details text tells the user that «Восстановить для схемы параметры по умолчанию» in Control Panel → Power Options restores the defaults (for the whole plan).

#### `core-parking-off`: Отключение парковки ядер / Disable core parking → **DO-NOT-INCLUDE**
- **Mechanism:** setting `CPMINCORES` `0cc5b647-c1df-4637-891a-dec35c318583`, hidden; read it with `powercfg /qh`. Here Balanced AC is already 100 % and DC 10 %.
- **Reason:** a myth on modern desktops. It **breaks dual-CCD X3D steering** and interferes with Intel Thread Director on hybrid CPUs.

#### `usb-selective-suspend-off`: Временное отключение USB / USB selective suspend → **SAFE-OPTIONAL (AC only)**
- **Mechanism:** subgroup `2a737441-1930-4402-8d77-b2bebba308a3`, setting `48e6b7a6-50f5-4782-a5d4-53bb8f07e226`: 0 = disabled.
- **Default:** 1 in every stock plan (checked here).
- **Effect:** no FPS gain. It fixes dropouts and wake delays on USB DACs, wireless dongles and VR. Slightly more idle power.

#### `pcie-aspm-off`: Управление питанием PCIe / Link State Power Management → **SAFE-OPTIONAL (AC, desktop)**
- **Mechanism:** subgroup `501a4d13-42af-4429-9fd1-a8218c268e20`, setting `ee12f906-d277-404b-b6da-e5fa1a576df5`: 0 = Off, 1 = Moderate, 2 = Maximum.
- **Defaults:** Balanced AC **1** / DC 2; High performance 0/0.
- **Notes:** the BIOS may keep ASPM control to itself (via _OSC), in which case the OS value is ignored. There are no consistent FPS measurements; the possible upside is fewer micro-stutters when the link wakes up.

### D. Input

#### `mouse-accel-off`: Повышенная точность указателя / Enhance pointer precision → **SAFE-OPTIONAL ("recommended for shooters")**
- **Mechanism:** HKCU `Control Panel\Mouse` → REG_SZ `MouseSpeed`="0", `MouseThreshold1`="0", `MouseThreshold2`="0".
- **Default:** "1"/"6"/"10" (checked here).
- **Apply live:** `SystemParametersInfo(SPI_SETMOUSE, 0, int[3]{0,0,0}, SPIF_UPDATEINIFILE|SPIF_SENDCHANGE)`. Writing only the registry needs a sign-out.
- **Leave alone:** `MouseSensitivity` and `SmoothMouseX/YCurve` (the "MarkC fix" is not needed).
- **Evidence:** most games use Raw Input and are unaffected. It matters on the desktop and in games that use the system cursor.

#### `accessibility-hotkeys-off`: Сочетания клавиш специальных возможностей / Sticky, Filter and Toggle Keys shortcuts → **SAFE-OPTIONAL (pre-checked in the Gaming profile, like `mouse-accel-off`)**
- **Problem:** Shift ×5 (Sticky Keys), holding right Shift ~8 s (Filter Keys) or holding Num Lock (Toggle Keys) opens a modal dialog; in exclusive fullscreen it minimizes the game.
- **Mechanism:** HKCU `Control Panel\Accessibility\StickyKeys`, `…\Keyboard Response`, `…\ToggleKeys` → REG_SZ `Flags`. Windows defaults "510" / "126" / "62". Clear **only bit 0x4** (`SKF_HOTKEYACTIVE` / `FKF_HOTKEYACTIVE` / `TKF_HOTKEYACTIVE`, Microsoft Learn STICKYKEYS/FILTERKEYS/TOGGLEKEYS) from the current value, keeping every other bit → "506" / "122" / "58" from the defaults; never hard-code. Keep REG_SZ (WinUtil writes a DWORD here: wrong type). An absent value is read as the Windows default.
- **Feature in use:** if bit 0x1 (`*_STICKYKEYSON` / `FILTERKEYSON` / `TOGGLEKEYSON`) is set, the user relies on the feature: that key is left alone. The logon screen (`HKU\.DEFAULT`) is not touched.
- Same as the Settings → Accessibility → Keyboard toggles «Сочетание клавиш для …» (Win11 Settings writes exactly 506; community tests on several PCs). Microsoft documents the struct bits, not the registry path; the registry side rests on community sources plus that Settings test.
- **Applies:** after sign-out. **Not** applied live through `SystemParametersInfo(SPI_SET*KEYS)`: that would bypass ChangeSession, and `SPI_SETFILTERKEYS` with `SPIF_UPDATEINIFILE` rewrites every Keyboard Response value (DelayBeforeAcceptance, AutoRepeatDelay, AutoRepeatRate, BounceTime) without journaling them.
- **Undo:** each Flags value is journaled with its original REG_SZ and restored byte for byte, or deleted if it was absent (and the key removed if MONOLITH created it). Undo restores the whole original string, so a later change of another bit (e.g. turning Sticky Keys on in Settings) is rolled back too — the drift detector shows that beforehand, and Details say so. A CustomTweak: `TweakApplyRevertTests` does not cover it, so it has its own redirected-registry test.
- **Effect:** no FPS gain; removes a focus-stealing dialog. Do not claim more.

### E. Scheduler, MMCSS and timer

#### `mmcss-system-responsiveness`: SystemResponsiveness → **DO-NOT-INCLUDE**
- **Mechanism:** HKLM `SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile` → `SystemResponsiveness` DWORD. Default **20** (0x14). Read when MMCSS starts, so a reboot is needed.
- **Reason:** Microsoft documents that **values below 10 and above 100 are clamped to 20**, so the popular "0" does nothing. 10 is the effective minimum, and it only affects threads registered with MMCSS. It is a myth, with a risk of audio crackle under load.

#### `network-throttling-index`: NetworkThrottlingIndex → **ADVANCED (placebo for ping)**
- **Mechanism:** same key, DWORD. Default **10** (0xA); the tweak value is `0xFFFFFFFF`.
- **What it affects:** network processing throughput (Vista-era mechanism), and only while MMCSS media is playing. It does not change game latency. Reboot.

#### `mmcss-games-task`: Задача Games (GPU Priority / Priority / Scheduling Category) → **DO-NOT-INCLUDE (myth)**
- **Mechanism:** `…\SystemProfile\Tasks\Games`.
  - Defaults: GPU Priority 8, Priority 2, Scheduling Category "Medium", SFIO Priority "Normal", Affinity 0, Background Only "False", Clock Rate 10000.
  - Typical tweak: Priority 6, "High", SFIO "High".
- **Reason:** Microsoft says GPU Priority is "not yet used", SFIO is "not used", and with "High" the Priority is always treated as 2. It only applies to threads that call `AvSetMmThreadCharacteristics("Games")`, which almost no games do. "High" (priority 23–26) can starve system threads.

#### `win32-priority-separation`: Win32PrioritySeparation → **SAFE-OPTIONAL only as a fix 0x18→0x26; other values DO-NOT-INCLUDE**
- **Mechanism:** HKLM `SYSTEM\CurrentControlSet\Control\PriorityControl` → DWORD. Default **0x2**.
  - Bit fields AABBCC: quantum length, variable/fixed, foreground boost (0 = 1:1, 1 = 2:1, 2/3 = 3:1).
  - The UI ("Programs" / "Background services") writes **0x26** / **0x18**. On client Windows, 0x2 ≈ 0x26 (short, variable, 3:1).
  - Applies without a reboot (the System Properties UI changes it live).
- **Evidence:** claims for 0x28/0x2A/0x16 are anecdotal only.
- **Detection:** if the value is 0x18, offer to restore 0x26 or 0x2.

#### `global-timer-resolution`: GlobalTimerResolutionRequests → **DO-NOT-INCLUDE**
- **Mechanism:** HKLM `SYSTEM\CurrentControlSet\Control\Session Manager\kernel` → DWORD 1. Absent by default (absent here). Win11 / Server 2022+ only. Reboot.
- **What it does:** brings back the global `timeBeginPeriod` behaviour. Since Win10 2004 the timer resolution is per-process, and on Win11 minimized or occluded windows lose their raised resolution.
- **Reason:** games request 1 ms themselves. It only matters together with a third-party "timer" service, and it increases power use. Microsoft explicitly advises against system-wide high resolution.

### F. Network and updates

#### `nagle-off`: Nagle / Delayed ACK → **ADVANCED (only for legacy TCP MMOs)**
- **Mechanism:** HKLM `SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{InterfaceGuid}` (GUID from `Get-NetAdapter`).
  - `TcpAckFrequency` DWORD **1**. Absent by default, meaning 2; range 0–255. KB 328890: don't change it "without careful study".
  - `TCPNoDelay` DWORD 1 is **not documented for the Windows TCP stack** (only for MSMQ); its effect is unproven.
- **Default / revert:** absent→delete. Takes effect after restarting the adapter or rebooting.
- **Reason for ADVANCED:** most games use UDP or set `TCP_NODELAY` themselves. The tweak adds more ACK packets.

#### `delivery-optimization-limit`: Оптимизация доставки / Delivery Optimization → **SAFE-OPTIONAL**
- **Read:** `Get-DOConfig` (gives DownloadMode, MaxUploadRatePct, UploadLimitMonthlyGB, DownBackLimitPct).
- **Settings store (user level):** `HKU\S-1-5-20\Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Settings` → `DownloadMode` DWORD. Absent here; absent→delete.
- **Option:** `DownloadMode` = **0** (HTTP only, no P2P uploads).
  - Default is LAN (1), so uploads go only to the local network. Uploads to internet peers happen only in mode 3.
  - The cmdlets `Set-DODownloadMode` and `Set-DOPercentageMaxBackgroundBandwidth` exist. **Check which store they write to (Settings or policy) before release.**
- **Policies:** `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization` (`DODownloadMode`, `DOPercentageMaxBackgroundBandwidth`, `DOMonthlyUploadDataCap` default 20 GB) count as ADVANCED because they lock the UI.
- **DO-NOT-INCLUDE:**
  - `DownloadMode` 100: deprecated on Win11 and breaks downloads.
  - Disabling `DoSvc`: breaks Windows Update and the Store.

#### `wu-active-hours`: Период активности / Windows Update active hours → **SAFE-OPTIONAL**
- **Mechanism:** HKLM `SOFTWARE\Microsoft\WindowsUpdate\UX\Settings`:
  - `SmartActiveHoursState` DWORD 0 (turns off automatic adjustment);
  - `ActiveHoursStart` and `ActiveHoursEnd` DWORD 0–23, with a window of at most 18 h.
- **Default / revert:** the values are absent here (automatic mode), so absent→delete. No reboot.
- **Don't use:** the policy route `…\Policies\Microsoft\Windows\WindowsUpdate` (`SetActiveHours`=1, `ActiveHoursStart`/`ActiveHoursEnd`) locks the UI. Pausing or disabling Windows Update is **DO-NOT-INCLUDE** for security reasons. Game Mode already blocks restarts and driver installs while a game is running.

### G. Security trade-off

#### `hvci-off`: Целостность памяти / Memory Integrity (HVCI) → **ADVANCED, red warning, never in a profile**
- **Mechanism:** HKLM `SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity` → `Enabled` DWORD **0**. Currently 1, with `WasEnabledBy`=1. **Reboot.**
- **Leave VBS itself alone:** `EnableVirtualizationBasedSecurity`=1, and KeyGuard is running.
- **Don't touch it if** `Locked`=1 (UEFI lock) or the policy `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeviceGuard` is set.
- **Default:** ON on new Win11 installs on supported hardware.
- **Detection:** CIM `root\Microsoft\Windows\DeviceGuard:Win32_DeviceGuard`.
  - `VirtualizationBasedSecurityStatus`: 2 = running.
  - `SecurityServicesRunning` contains 2 = HVCI is running.
- **Evidence:** Microsoft's own article says HVCI and the Virtual Machine Platform can reduce gaming performance "in some scenarios". Measured losses are about 0–10 % (~5 % on average) and smaller on CPUs with MBEC/GMET.
- **Risk:**
  - Loss of protection against kernel exploits, and loss of the vulnerable-driver blocklist.
  - **Anti-cheat:** Riot Vanguard's On-Demand mode (June 2026) requires VBS + HVCI + IOMMU + Secure Boot + TPM. FACEIT has VBS/IOMMU checks.
- **VMP:** disabling it breaks WSL2. Leave it as a recommendation only.

### H. Interface and background

#### `visual-effects`: Визуальные эффекты / Visual effects → **SAFE-OPTIONAL (Normal profile / weak iGPUs only)**
- **Mechanism:** HKCU `…\Explorer\VisualEffects` → `VisualFXSetting` DWORD (0 = auto, absent by default; 1 = appearance; 2 = performance; 3 = custom).
  - The real switches are `HKCU\Control Panel\Desktop\UserPreferencesMask` (REG_BINARY; here `9E1E078012000000`) and `…\WindowMetrics\MinAnimate`, plus the `SystemParametersInfo` calls (SPI_SETCLIENTAREAANIMATION, SPI_SETMENUANIMATION, SPI_SETDROPSHADOW, …).
  - Save the mask and each SPI value for revert.
- **Evidence:** no effect in games, because full-screen and independent flip bypass DWM composition.

#### `transparency-off`: Прозрачность / Transparency effects → **SAFE-OPTIONAL**
- **Mechanism:** HKCU `…\Themes\Personalize` → `EnableTransparency` DWORD 0. Default 1 (already 0 here).
- **Apply live:** broadcast `WM_SETTINGCHANGE("ImmersiveColorSet")`.
- **Effect:** negligible on a discrete GPU.

#### `background-apps`: Фоновые приложения / Background apps → **SAFE-OPTIONAL per app; global policy DO-NOT-INCLUDE**
- **Per app:** HKCU `…\BackgroundAccessApplications\<PackageFamilyName>` → `Disabled`=1 and `DisabledByUser`=1 (DWORD). Absent by default; absent→delete. On Win10 the global switch is `GlobalUserDisabled`=1 under the same key.
- **Policy (do not use):** `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy` → `LetAppsRunInBackground`=2. It breaks notifications, Phone Link, alarms and the Xbox app.
- **Scope:** affects packaged (UWP) apps only. Steam, Discord and launchers belong to the startup manager.
- **Default selection:** don't preselect the Xbox, Game Bar or SecHealthUI packages.

#### `startup-delay-off`: Задержка автозапуска / Startup delay → **SAFE-OPTIONAL (Normal profile, not Gaming)**
- **Mechanism:** HKCU `Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize` → `StartupDelayInMSec` DWORD 0. On Win11 also `WaitForIdleState` DWORD 0. The key doesn't exist by default (absent here), so revert = delete the key.
- **Notes:** undocumented. It doesn't help games and can make the desktop sluggish right after logon.

#### `dnd-in-games`: Не беспокоить во время игр / Do not disturb during games → **SAFE-DEFAULT as a check and recommendation, no registry write**
- **Setting:** Win11 → Notifications → "Turn on do not disturb automatically" → "When playing a game" (ON by default) and "…full-screen app". On Win10 this is Focus assist "When I'm playing a game".
- **Why no write:** the setting is stored in undocumented CloudStore blobs (`…\CloudStore\Store\DefaultAccount\Current\*windows.data.donotdisturb*` / `*quiethours*`). **Do not write them.**
- **Action:** a deep link to `ms-settings:notifications` (Win10: `ms-settings:quiethours`).
- **Global kill switch:** `HKCU\…\PushNotifications\ToastEnabled`=0 is too broad: ADVANCED, and only for the duration of a boost session with a restore afterwards.

### I. Recommendations only (MONOLITH reads, never writes)

#### `gpu-panel-reco`: NVIDIA / AMD settings
- **NVIDIA:** reading via NVAPI DRS (`NvAPI_DRS_GetSetting`) is fine; don't write.
  - G-SYNC on for fullscreen and windowed.
  - V-Sync ON in NVCP and off in the game, with an FPS cap about 3–4 below the refresh rate (≤236 at 240 Hz). Reflex caps automatically.
  - Reflex On (Boost optional).
  - Low Latency Mode On/Ultra for DX11 games without Reflex.
  - Power management mode "Normal" ("Prefer max" only raises idle power).
  - Shader Cache Size: driver default or ≥10 GB.
- **AMD:** FreeSync on, Anti-Lag 2 where supported, Chill/FRTC as an FPS cap, Enhanced Sync off when using VRR, SAM on.
- **Display connection:** check that the monitor is plugged into the discrete GPU (true here: 239 Hz on the 5070 Ti).

#### `rebar-detect`: Resizable BAR / SAM
- **NVIDIA:** `nvidia-smi -q -d MEMORY`. BAR1 Total ≥ FB Total means ON (here 16384 ≥ 16303). NVAPI also works.
- **Generic:** `ID3D12Device::CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS16).GPUUploadHeapSupported` (Win11 24H2+ / Agility SDK).
- **`Win32_DeviceMemoryAddress` is unreliable:** here it showed only the 32/65 MB ranges for the 5070 Ti.
- **Recommendation (BIOS):** Above 4G Decoding + Re-Size BAR, CSM off.

#### `xmp-expo-detect`: RAM running below its rated speed
- **Fields:** `Win32_PhysicalMemory` → `Speed` (SMBIOS max, **often JEDEC**), `ConfiguredClockSpeed`, `PartNumber`, `SMBIOSMemoryType` (26 = DDR4, 34 = DDR5).
- **Comparing Speed with ConfiguredClockSpeed is not enough:** with XMP off, both often read 4800/4800.
- **Logic:**
  - rated = max(Speed, speed parsed from PartNumber). Example regexes: G.Skill `F5-(\d{4})`, Corsair `…B(\d{4})C`, Kingston `KF(\d{2})\dC` ×100, Crucial `…(\d{2})C\d\dU` ×100.
  - Flag if Configured < 0.95 × rated and Configured is a JEDEC speed {2133, 2400, 2666, 2933, 3200, 4800, 5200, 5600}.
  - Normalize DDR4 readings below 1700 (some firmware reports MHz rather than MT/s, so ×2).
  - Mixed modules: use the minimum.
- **Here:** 6000/6000, so OK.

---

## 3. Other things to refuse (DO-NOT-INCLUDE)

| Tweak | Reason |
|---|---|
| `bcdedit useplatformclock / disabledynamictick / tscsyncpolicy`, disabling HPET in Device Manager | Myth; more timer overhead or instability |
| `FeatureSettingsOverride/Mask` (Spectre/Meltdown off) | Security |
| Defender off, whole-drive exclusions | Security |
| Pagefile off, `LargeSystemCache`, `DisablePagingExecutive`, `ClearPageFileAtShutdown` | Crashes or slowdowns, no gain |
| `IRQ8Priority` / IRQ priorities | Don't exist in the NT scheduler; placebo |
| `SvcHostSplitThresholdInKB`, disabling SysMain/Search | No measurable gaming effect |
| RAM "cleaners" (`EmptyWorkingSet`) | Cause page faults and stutter |
| Realtime priority for a game | Starves input and audio threads |
| Disabling NIC offloads, interrupt moderation, RSS | NIC-dependent; raises CPU load |
| Core-unparking tools, `PlatformAoAcOverride` | Break X3D/hybrid scheduling and Modern Standby |
| `PowerThrottlingOff` on desktops, `DisableUserPresenceQos` | Placebo: EcoQoS throttling applies only on battery and a focused game is High QoS (see §2 C) |
| Writing `ActiveOverlayAcPowerScheme` directly | Undocumented and not applied live; use `PowerSetUserConfiguredACPowerMode` (§2 C) |
| Live `SPI_SETFILTERKEYS` for the accessibility-shortcut item | Rewrites unjournaled Keyboard Response values (§2 D) |

## 4. Where each tweak is stored (HKCU vs HKLM)

- **HKCU (per user):** game-mode, windowed-optimizations, VRR, GPU preference, FSO per game, GameDVR/AppCapture/Historical, mouse, accessibility shortcuts, visual effects, transparency, background apps, startup delay, DND.
- **HKLM (machine, needs admin):** HAGS, MPO, AllowGameDVR, MMCSS (SystemResponsiveness, NetworkThrottlingIndex, Games task), Win32PrioritySeparation, GlobalTimerResolutionRequests, Tcpip Interfaces, HVCI, WU active hours, DO policies.
- **NETWORK SERVICE hive (`HKU\S-1-5-20`):** Delivery Optimization user settings.
- **Power plans (powercfg/PowrProf API, per machine):** active plan, PROCTHROTTLEMIN, CPMINCORES, USB selective suspend, ASPM, Windows 11 power mode (PowerSetUserConfiguredACPowerMode).

## Sources
- https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service
- https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952
- https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference
- https://learn.microsoft.com/en-ie/Windows/Win32/delivery_optimization/downloadmode
- https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod
- https://support.microsoft.com/kb/328890
- https://learn.microsoft.com/id-id/archive/blogs/nettracer/tcp-delayed-ack-combined-with-nagle-algorithm-can-badly-impact-communication-performance
- https://technet.microsoft.com/library/Cc976120
- https://bitsum.com/amd-x3d-ccd-affinity/
- https://wiki.vrchat.com/wiki/Guides:AMD_X3D_Series_Processors
- https://www.techpowerup.com/review/amd-ryzen-9-7950x3d/5.html
- https://hothardware.com/news/amds-chipset-driver-for-v-cache-cpus
- https://www.elevenforum.com/t/options-to-optimize-gaming-performance-in-windows-11.9492/
- https://www.ghacks.net/?p=181143
- https://www.tomshardware.com/video-games/pc-gaming/riot-vanguard-adds-an-on-demand-mode-that-stops-anti-cheat-loading-at-boot-on-secured-windows-11-pcs
- https://www.stigviewer.com/stigs/microsoft_windows_11_v2r7/2026-02-12/finding/V-253399
- https://www.elevenforum.com/t/enable-or-disable-variable-refresh-rate-for-games-in-windows-11.12052
- https://www.elevenforum.com/t/turn-on-or-off-optimizations-for-windowed-games-in-windows-11.4943/
- https://learn.microsoft.com/en-us/answers/questions/3881462/how-can-i-disable-windows-mpo-in-2025-(24h2-window
- https://www.guru3d.com/publish/comments/amd-radeon-software-adrenalin-2562-driver-download/page-8
- https://www.winhelponline.com/blog/power-options-only-balanced-plan/
- https://helpdeskgeek.com/how-to-enable-and-use-windows-ultimate-performance-power-plan/
- https://news.xbox.com/en-us/2018/10/02/latest-october-2018-windows-update-gaming-features-3/
- https://community.acer.com/en/discussion/670776
- https://devblogs.microsoft.com/directx/hardware-accelerated-gpu-scheduling/
- https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powersetuserconfiguredacpowermode
- https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powergetuserconfiguredacpowermode
- https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/customize-power-slider
- https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service
- https://gpsearch.extrememanual.net/en/computer/System/PowerManagementCat/PowerThrottlingSettingsCat/PowerThrottlingTurnOff
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-stickykeys
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-filterkeys
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-togglekeys
- https://winutil.christitus.com/code-reference/tweaks/customize-preferences/stickykeys/
- https://www.pcrisk.com/blog/windows/12938-cant-turn-off-sticky-keys