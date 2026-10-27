# MONOLITH: «Устройства» — feature design (architecture, journal integration, work packages)

Owner request (27.10.2026): «сделай так, чтобы через это приложение можно было настраивать свои девайсы, и чтобы само
приложение предлагало наилучшие варианты настройки, а также вариант ручной настройки».

Inputs: `devices_mouse-keyboard.md`, `devices_displays.md`, `devices_audio.md`, `devices_vendors.md` (their
classifications are binding, CLAUDE.md rule 2), CLAUDE.md (prime directive, journal rules, Minimal UI, design system),
the current code (ChangeSession/ChangeJournal/JournalTrust, TweakCatalog, Dashboard checks, Drivers hardware analysis,
UnelevatedShell, Restore/LiveState/DriftDetector).

Test PC: Win 11 26H2 (26300), Ryzen 7 9800X3D, RTX 5070 Ti + AMD iGPU, Logitech LIGHTSPEED receiver 046D:C54D (mouse
behind it was last wired as PRO X2 SUPERSTRIKE 046D:C0A8), PRO Gaming Keyboard 046D:C339, G HUB virtual HID 046D:C231/C232,
Sony INZONE E9 054C:0F80, Razer Seiren V3 Mini 1532:056A, MSI Mystic Light 0DB0:0076, X27U W3 2560×1440 239.97 Hz (DP,
QD-OLED, HDR capable/off), Odyssey G5 1440×2560 portrait 179.95 Hz (DP, HDR-400 capable/off).

---

## 1. Decisions in one screen

| Question | Decision |
|---|---|
| Where | New sidebar page **«УСТРОЙСТВА»** (key `devices`, glyph `Glyphs.Mouse` U+E962), between «Игры» and «Автозапуск и службы». |
| What it owns (single place) | Parametric Windows input settings (pointer speed, wheel, double-click, button swap, trails, Ctrl-locate, hide-while-typing, keyboard repeat delay/rate); display mode (resolution + refresh) and HDR per monitor; monitor brightness/contrast over DDC/CI; microphone level; call ducking; the measurement tools (mouse polling rate, mouse DPI, mic level); launching vendor apps (G HUB, Synapse, INZONE Hub); the device inventory (what is plugged in, which model). |
| What stays where it is | Binary Windows-setting tweaks stay in **Optimize**: `mouse-acceleration-off`, `accessibility-hotkeys-off`, `vrr-optimization`, `windowed-games-optimization`, new `auto-hdr`. The Devices page shows their live state as a read-only row with «В ОПТИМИЗАЦИИ» (`Ui.RequestFocus("optimize", id)`), never a second toggle. Driver / vendor-software **installation** stays in **Drivers → ПОДБОР ДЛЯ ВАШЕГО ПК**; Devices links there («УСТАНОВИТЬ В «ДРАЙВЕРАХ»»). Bulk undo + restore points stay in **Restore**. |
| What moves / becomes a link | Dashboard check «МОНИТОР НЕ НА МАКСИМАЛЬНОЙ ЧАСТОТЕ»: button «ОТКРЫТЬ ПАРАМЕТРЫ» (ms-settings) → «К МОНИТОРАМ» (focus Devices, kind:monitor). Drivers → МОНИТОРЫ «Включить N Гц» (ms-settings) → «В «УСТРОЙСТВА»». No other page loses or duplicates a function. |
| Two modes | Page-header segment **РЕКОМЕНДУЕМОЕ \| ВРУЧНУЮ**. Recommended = a diff plan (сейчас → станет, one-line reason, tag) with checkboxes and one «ПРИМЕНИТЬ ВЫБРАННОЕ». Manual = every setting with its control, current value, «рек.» / «по умолч.» hints, per-row ПРИМЕНИТЬ / ОТКАТИТЬ. |
| Writes | Every write goes through `ChangeSession` (new typed primitives, §4). New `ChangeKind`s: `UserParameter`, `DisplayMode`, `DisplayHdr`, `MonitorSetting`, `AudioLevel`; plus allow-listed HKCU `SetRegistry` for ducking. Source of all Devices records: `"devices"`. |
| Admin rights | Consistent with the codebase: **all writes need elevation** (the journal lives in the admin-only `%ProgramData%\MONOLITH`). Without admin the page is fully readable; measurements, deep links and vendor-app launch work (no system change). Apply/undo buttons go through `PageViewModel.RequireAdmin`. |
| Per-user | SPI settings and HKCU ducking are per user (`UserSid` recorded). They are written **only when MONOLITH runs as the interactive user** (`Elevation.IsInteractiveUser`): SPI changes the live session but `SPIF_UPDATEINIFILE` persists into the *calling* account, so over-the-shoulder UAC would split live and persisted state. Otherwise those rows are read-only with one muted line. |
| Display safety | Windows' pattern rebuilt (Windows gives no safety net to API callers): journal-first record `PendingConfirm` → out-of-process guard armed → temporary mode (`ChangeDisplaySettingsEx` flags 0, not persisted) → topmost countdown «СОХРАНИТЬ ЭТИ ПАРАМЕТРЫ ЭКРАНА?» 15 s, default ВЕРНУТЬ, `System.Threading.Timer` deadline → keep = `CDS_UPDATEREGISTRY` / anything else = revert. Guard process `MONOLITH.exe --display-guard` reverts if MONOLITH dies or the deadline passes; startup reconciles leftover `PendingConfirm`. Only modes the driver lists for that monitor and that pass `CDS_TEST` are ever applied (also on undo). |
| Vendor-only settings | DPI, polling rate, RGB, EQ/spatial are never written (DO-NOT-INCLUDE `vendor-protocol-writes`). Shown as measured values + recommendation + «ОТКРЫТЬ G HUB» (signed exe, started unelevated) or «УСТАНОВИТЬ В «ДРАЙВЕРАХ»». |
| Parallel build | 5 packages with disjoint files: **core** (journal, session, trust, page shell, generic UI) first; then **input**, **displays**, **audio**, **vendors** in parallel. Feature code plugs in through `partial` hook methods (`DeviceProviders.*`, `DeviceWriters.*`), so core compiles and tests alone and no two packages touch one file. |

---

## 2. Placement under the Minimal UI rule — justification

1. *Every function in exactly one place.* Today none of the parametric device settings exist anywhere in MONOLITH, so the
   Devices page is their only home. Display-mode changes also exist nowhere (Dashboard/Drivers only open ms-settings) —
   once Devices can change refresh rate safely, the two ms-settings buttons become shortcuts into Devices, exactly like
   «Dashboard fix buttons are shortcuts into the same tweaks».
2. *Windows settings as tweaks → Optimize only.* `mouse-acceleration-off` / `accessibility-hotkeys-off` are binary,
   profile-driven, drift-re-applicable tweaks (Gaming profile pre-checks them; journal sources `tweak:<id>`, profile
   export, drift fix `DriftFix.Tweak` all depend on the ids). The research classifies them INFO-ONLY on the Devices page
   (binding). Moving them would orphan existing journal records and the Gaming profile; duplicating a toggle would create
   two writers of one registry target. → They stay; Devices shows «Повышенная точность указателя: выкл · пункт
   «Отключить ускорение мыши» [В ОПТИМИЗАЦИИ]». In the recommended plan they appear as **advisory** rows (no checkbox,
   link only).
3. *Vendor driver tools → Drivers.* Installation of G HUB/Synapse/MSI Center stays on the Drivers hardware analysis
   (winget/Store/official page, verified ids). Drivers never *launches* a vendor app today, so «ОТКРЫТЬ G HUB» lives only
   on Devices.
4. *Hardware facts → Dashboard.* Devices shows only facts needed to configure a device (model, connection, current
   mode, measured rates) on the device's card; it does not duplicate CPU/RAM/disk facts.
5. *Bulk undo → Restore only.* Devices has per-row ОТКАТИТЬ (like Optimize rows); no «откатить всё» on Devices.
6. *No hero blocks.* Plan card on top (only when something is recommended; otherwise one muted line), then compact device
   cards. One muted line per explanation; details in tooltips.

---

## 3. Per device type: what is detected, recommended, manual, vendor-only

Legend for the plan policy: **Include** = pre-checked (SAFE-DEFAULT), **Suggest** = shown unchecked (SAFE-OPTIONAL),
**Advice** = non-selectable row with a link (Optimize tweak or vendor app), **—** = never in the plan (manual only).

### 3.1 Mouse card (key `mouse`) — package *input* (+ vendor enrichment)

Detected: physical mice from `GetRawInputDeviceList` (RIM_TYPEMOUSE) mapped to device instance ids, virtual ones dropped
(ancestor chain not reaching `USB\`/`BTHENUM\`/`BTHLE\`/`BTHLEDevice\`, e.g. `LGHUBDevice\…` for 046D:C231); name =
bus-reported product string (PnpTree); receivers named from the PID table + history hint «раньше по кабелю: PRO X2
SUPERSTRIKE» (vendors). Live SPI values + persisted HKCU values («ОЖИДАЕТ ВЫХОДА ИЗ СИСТЕМЫ» tag when they differ).

| Setting (SpiCatalog id) | Control | Recommended (policy) | Notes |
|---|---|---|---|
| Скорость указателя (`mouse-speed`) | slider 1–20 | 10 (Suggest; only when DPI was measured → reason shows «чтобы скорость курсора не изменилась, поставьте в G HUB ≈ N DPI»; without DPI: Advice text) | multiplier table 1/32…3.5 |
| Повышенная точность указателя | info | off → Advice «В ОПТИМИЗАЦИИ» (`mouse-acceleration-off`) | live `SPI_GETMOUSE`, pending registry |
| Строк за шаг колёсика (`wheel-lines`) | slider 1–100 + «экран целиком» | — (taste) | default 3 |
| Символов за шаг гориз. прокрутки (`wheel-chars`) | slider 1–100 | — | only if `SM_MOUSEHORIZONTALWHEELPRESENT` ≠ 0 (hidden here) |
| Прокручивать неактивные окна (`wheel-routing`) | toggle (2/0) | on (Suggest when off) | read-back verify |
| Скорость двойного щелчка (`double-click`) | slider 200–900 ms + test target | — | |
| Поменять кнопки местами (`swap-buttons`) | toggle | — (never in plan) | 15-s countdown, confirm with the new primary button, else auto-revert |
| Шлейф указателя (`mouse-trails`) | choice off/2–7 | off (Suggest when on) | |
| Показывать указатель по Ctrl (`mouse-sonar`) | toggle | off for games (Suggest when on) | UPM bit: SPI-only undo |
| Скрывать указатель при вводе (`mouse-vanish`) | toggle | — | UPM bit: SPI-only undo |
| DPI (vendor-only) | info | «800–1600 на 1440p» + measured value; Advice «ОТКРЫТЬ G HUB» when outside | tool «Проверка DPI» |
| Частота опроса (vendor-only) | info | 1000 Гц; 2000–4000 по желанию; 8000 only after testing games | tool «Частота опроса» |
| Встроенная память (Logitech) | note | one line «Сохраните профиль в память мыши в G HUB…» | vendors |

Tools: **Частота опроса** (≤ 5 s raw-input session, foreground-only) and **Проверка DPI** (drag 10 cm / card 85.6 mm,
three runs, median ± 5 %). See §6.

### 3.2 Keyboard card (key `keyboard`) — *input*

| Setting | Control | Recommended | Notes |
|---|---|---|---|
| Задержка перед повтором (`keyboard-delay`) | slider 0–3 (≈250/500/750/1000 ms) + test box | — (1 default; 0 for fast typists in hint) | FilterKeys overrides it — shown |
| Скорость повтора (`keyboard-speed`) | slider 0–31 | 31 (Suggest when lower) | |
| Окна «Залипание клавиш» в играх | info | Advice «В ОПТИМИЗАЦИИ» (`accessibility-hotkeys-off`) | |
| Залипание клавиш / Фильтрация ввода **включены** | note (only when on) | link `ms-settings:easeofaccess-keyboard` | v1 does not write the structs (avoids a second writer of the Flags values the tweak journals) |
| Частота опроса клавиатуры | note | «настраивается в G HUB» | not measurable from user mode |
| Подсветка | note | «Dynamic Lighting и G HUB — выберите одно» + `ms-settings:personalization-lighting` | vendors (LampArray child detected) |

### 3.3 Monitor cards (key `display:<monitorDevicePath>`) — *displays*

Detected per active CCD path: friendly name, GDI name (`\\.\DISPLAYn`, hint only), `monitorDevicePath` (the key),
output technology (DP/HDMI/internal), current mode with exact rational refresh, GDI mode list (progressive, 32 bpp, same
orientation), preferred/native size (`GET_TARGET_PREFERRED_MODE` cross-checked with EDID DTD), EDID (range limits,
FreeSync VSDB, DisplayID timings, HDR static metadata, max luminance), advanced colour (type 15 on ≥ 26100 else 9:
HDR supported/enabled, WCG, bpc, limited by policy), SDR white level (type 11), effective DPI, DDC/CI brightness and
contrast (getter succeeds → supported).

| Setting | Control | Recommended | Write |
|---|---|---|---|
| Режим (разрешение + частота) | dual choice: resolution → refresh list | native size at max progressive refresh; flag only when max > current + 1 Hz (Include) | `SetDisplayModeAsync` (§5) |
| HDR | toggle (only when supported and not limited by policy) | none automatic; hint per panel class (OLED / local-dimming 600+ → «для HDR-игр»; HDR400 edge-lit → «лучше выкл.») — | `SetDisplayHdrAsync` (§5) |
| Яркость / Контраст (DDC/CI) | sliders min–max reported by the monitor | — (taste) | `SetMonitorSetting` on ПРИМЕНИТЬ only, read-back verified |
| Auto HDR | info (only when HDR on for a monitor) | Advice «В ОПТИМИЗАЦИИ» (`auto-hdr`, new DxTokenTweak) | Optimize |
| VRR | info | «Диапазон 48–240 Гц (FreeSync/G-SYNC Compatible) — включите в панели NVIDIA; ограничьте FPS ≤ 237» + Advice `vrr-optimization` | Optimize |
| Facts line | text | «2560×1440 · 239,97 Гц · DisplayPort · 8 бит · HDR: поддерживается, выкл · масштаб 100 % (рекомендуемый)» | — |
| Notes (only when actionable) | note | link bottleneck (EDID max > offered max: cable/port advice), HDR on → «Откалибруйте HDR» (Store app), not-primary fastest monitor → `ms-settings:display` | — |

### 3.4 Audio output cards (key `audio:<endpointId>`) — *audio*

Detected (MMDevice API, read-only): active render endpoints, roles (console/multimedia/communications), friendly name,
form factor, shared format (`PKEY_AudioEngine_DeviceFormat`), USB VID/PID + container (vendor app), enhancements
(registry FxProperties `{1da5d803…},5`), spatial sound (WinRT `SpatialAudioDeviceConfiguration`), exclusive-mode flags
(unofficial PKEYs, shown as such). Monitor HDMI/DP endpoints (form factor 9) are not cards: one muted line on the
default output card «Выходы мониторов X27U W3, Odyssey G5 не используются» + link to Windows sound settings.

| Setting | Control | Recommended | Write |
|---|---|---|---|
| Устройство по умолчанию | info | all 3 render roles on the headset; flag split roles / HDMI default / BT hands-free | link `mmsys.cpl,,0` |
| Формат | info | 48 kHz (never 96/192 as "boost") | link `ms-settings:sound-properties?endpointId=…` |
| Улучшения звука | info | off when the vendor app does the DSP (INZONE Hub) | link |
| Пространственный звук | info | off when INZONE 360 / in-game HRTF is used | link |
| Приглушение во время звонков (default render card) | choice 0–3 | 3 «ничего не делать» for gaming with voice chat (Suggest) | `SetRegistry` HKCU `Software\Microsoft\Multimedia\Audio\UserDuckingPreference` (allow-listed); «вступит в силу после выхода из системы» + link `mmsys.cpl,,3` |
| Монофонический звук | note (only when on) | off for games | link `ms-settings:easeofaccess-audio` |
| EQ / 360 Spatial Sound / DRC (INZONE) | note | vendor-only → «ОТКРЫТЬ INZONE HUB» | vendors |

### 3.5 Audio input cards — *audio*

| Setting | Control | Recommended | Write |
|---|---|---|---|
| Уровень микрофона | slider 0–100 % (+ dB range in hint) | only after the **Проверка уровня** tool: speech peaks −12…−6 dBFS, nothing above −1 dBFS (Suggest, target in dB) | `SetAudioLevel` |
| Без звука | toggle | — | `SetAudioLevel(mute)` |
| Доступ приложений к микрофону | note (only when Deny) | link `ms-settings:privacy-microphone` | — |
| Усиление / АРУ | v1: not implemented (driver exposes none on this PC; deferred to v2, see §9) | | |
| Частота дискретизации / микшер (Razer) | note | vendor-only → «ОТКРЫТЬ RAZER SYNAPSE» | vendors |

### 3.6 Other peripherals (key `periph:<containerId>`) — *vendors*

Inventory = present `USB`/`HID`/`BTHENUM`/`BTHLE`/`BTHLEDevice` devnodes (CfgMgr32), grouped by ContainerId (local-machine
container or none → nearest `USB\VID_…&PID_…\` ancestor), virtual subtrees, LampArray children, hubs, storage and radios
dropped. Groups already claimed by another card (VID:PID or container match) are not repeated. Remaining groups of a known
vendor or of gamepad kind become cards, e.g. **MYSTIC LIGHT (MSI)**: «Подсветка платы MSI настраивается в MSI Center — не
установлен» + «УСТАНОВИТЬ В «ДРАЙВЕРАХ»»; note when OpenRGB and a vendor RGB app are both installed. Serial numbers /
instance-id tails are never shown, logged or exported.

---

## 4. Journal integration (core)

### 4.1 Mechanisms → ChangeSession

| Mechanism | ChangeSession method | ChangeKind / target | Per-user | Restore point | Undo (WindowsJournalSystem) |
|---|---|---|---|---|---|
| SPI parameter | `bool SetUserParameter(string spiId, int value, string source, string title)` | `UserParameter` / `spi:<id>` | yes (UserSid) + interactive-user gate | plan: yes; single: no | `SPI_SET(original, SPIF_UPDATEINIFILE\|SPIF_SENDCHANGE)` → read back → restore persisted value exactly (delete when it was absent); refuses unless interactive user |
| Display mode | `Task<DisplayApplyOutcome> SetDisplayModeAsync(string monitorPath, string monitorName, DisplayModeSpec target, string source, string title, CancellationToken ct = default)` | `DisplayMode` / `display:mode:<path>` | no | plan: yes; single: no | same safe-apply flow back to `Original` (mode must be offered + `CDS_TEST`); asks first if the live mode ≠ recorded target; monitor missing → error, record stays |
| HDR | `Task<DisplayApplyOutcome> SetDisplayHdrAsync(string monitorPath, string monitorName, bool enable, …)` | `DisplayHdr` / `display:hdr:<path>` | no | no | `SET_HDR_STATE` (type 16, ≥ 26100) / `SET_ADVANCED_COLOR_STATE` (type 10) back to original, with countdown |
| DDC/CI | `bool SetMonitorSetting(string monitorPath, string monitorName, MonitorVcp code, uint value, …)` | `MonitorSetting` / `ddc:<10\|12>:<path>` | no | no | `SetMonitorBrightness/Contrast(original)` + read-back |
| Endpoint level | `bool SetAudioLevel(string endpointId, string endpointName, float? scalar, float? db, bool? mute, …)` | `AudioLevel` / `audio:level:<endpointId>` | no | no | `SetMasterVolumeLevelScalar(original bits)` + `SetMute(original)` |
| Ducking | existing `SetRegistry(HKCU, Software\Microsoft\Multimedia\Audio, UserDuckingPreference, DWord, 0–3)` | `Registry` (source `devices`) | yes | single: no | existing registry restore |

All new Set* methods follow the existing pattern: hold the journal gate (`_journal.Exclusive`/`ExclusiveAsync`), read the
original, `Journal.Add` (throws if not written to disk → no change), write, read back, on failure put the original back
and `Remove` the new record, `Applied++`, log. No-op (live == target) creates no record.

### 4.2 Record payloads (new nullable properties on `ChangeRecord`, JSON-serialised)

```
Spi:         SpiSnapshot        { Id, Value (original, int; WHEEL_PAGESCROLL = -1), Persisted (RegistryValueSnapshot?) }
DisplayMode: DisplayModeSnapshot{ MonitorPath, MonitorName, Original: DisplayModeSpec, Target: DisplayModeSpec, PendingConfirm }
DisplayHdr:  DisplayHdrSnapshot { MonitorPath, MonitorName, OriginalEnabled, TargetEnabled, PendingConfirm }
Monitor:     MonitorVcpSnapshot { MonitorPath, MonitorName, Code (0x10|0x12), Value (original), Max }
Audio:       AudioLevelSnapshot { EndpointId, EndpointName, ScalarBits (BitConverter.SingleToUInt32Bits of original), Mute }
DisplayModeSpec(Width, Height, Frequency, RefreshNumerator, RefreshDenominator, BitsPerPel, Orientation, PositionX, PositionY)
```

Layering: `HandOver` copies the *original* part to the newer layer (Spi → whole snapshot; DisplayMode/Hdr → Original /
OriginalEnabled only, keeping the newer Target; Monitor/Audio → whole snapshot). `RestoreInputs` includes the five new
references. `UserScope.IsPerUser`: `UserParameter` → true.

### 4.3 JournalTrust (journal = untrusted input, CLAUDE.md §7)

- `"devices"` added to the module sources. It may write only: the five new kinds, and `Registry` for the allow-list
  `DeviceRegistry` = { HKCU `Software\Microsoft\Multimedia\Audio\UserDuckingPreference` (DWORD 0–3 or absent) }.
- The five new kinds are accepted **only** from source `devices` (tweaks may not write them).
- `UserParameter`: target == `spi:` + id; id in the compiled `SpiCatalog`; value inside the entry's *valid* range;
  `Persisted` is null exactly when the catalog entry has no persisted value, else Hive HKCU + the catalog's exact
  path/name (ordinal-ignore-case) + kind String/DWord as catalogued + no CreatedKeyRoot; `UserSid` must be present.
- `DisplayMode`/`DisplayHdr`: target == prefix + `MonitorPath`; `MonitorPath` matches
  `^\\\\\?\\DISPLAY#[A-Za-z0-9]{3,8}#[^\x00-\x1F"<>|*?]{1,200}#\{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7\}$`; specs sane
  (320 ≤ w,h ≤ 16384; 1 ≤ Frequency ≤ 1000; numerator/denominator 0 or ≤ 10^9 with denominator > 0 when numerator > 0;
  BitsPerPel ∈ {0, 8, 16, 24, 32}; orientation 0–3; |position| ≤ 65536); no UserSid.
- `MonitorSetting`: code ∈ {0x10, 0x12}; 1 ≤ Max ≤ 10000; Value ≤ Max; path valid; target matches; no UserSid.
- `AudioLevel`: endpoint id matches `^\{0\.0\.[01]\.00000000\}\.\{[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}$`;
  scalar (from bits) finite, 0–1; target matches; no UserSid.
- Restore additionally re-validates live: display modes must be offered by the driver and pass `CDS_TEST`; DDC value ≤ the
  monitor's live max; SPI value via catalog.

### 4.4 Restore page, drift, labels

- `RestoreFormat.KindLabel`: ПАРАМЕТР / ЭКРАН / HDR / МОНИТОР / ЗВУК. `DescribeTarget` for `spi:`, `display:`, `ddc:`,
  `audio:` prefixes (monitor/endpoint names come from the record payload).
- `LiveState.Read` / `VerifyRevert` for the new kinds through the writer interfaces (null writer → «—», not checked).
- `DriftDetector.IsCandidate`: `UserParameter` yes; `DisplayMode`, `DisplayHdr`, `MonitorSetting`, `AudioLevel` no
  (users change brightness/mic level/mode legitimately and often — no drift noise). `OwnerPage("devices") = "devices"`;
  devices records have `DriftFix.None` (only «ПОКАЗАТЬ»).
- `OptimizeLogic.SourceLabel("devices") = "Устройства"`.

### 4.5 Admin, account, journal availability

- `ChangeSession.BeginAsync` already demands elevation. Devices calls `RequireAdmin` before any write.
- `Elevation.IsInteractiveUser` (new, cached, `!IsRunningAsDifferentUser()`, test override) gates per-user writes and
  SPI undo.
- `ChangeJournal.IsUnavailable` → Add throws → nothing changes (existing behaviour).
- Self-test (`ChangeSession.Locked`, `Ui.AutoAnswer = false`) → every write refused, countdown auto-answers «ВЕРНУТЬ».

---

## 5. Display safe-apply protocol (core `DisplaySafeApply`, native side in *displays*)

```
Preconditions  GameBoostState.IsActive → Blocked «Во время игрового сеанса MONOLITH не меняет параметры экрана».
               writer.BlockReason(): RDP session (SM_REMOTESESSION), fullscreen D3D app/presentation mode
               (SHQueryUserNotificationState ∈ {QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE}).
               original = writer.ReadCurrentMode(path) ?? Failed «монитор не найден».
               original.SameMode(target) → Unchanged (no record).
               !writer.IsModeOffered(path, target) → Failed «режим не поддерживается монитором».
               writer.TestMode(path, target) != OK → Failed.
Journal first  record { Kind DisplayMode, PendingConfirm = true } → Add (saved, or throws → nothing changed).
Guard          guard = writer.StartGuard(record.Id, deadline = now + 35 s, action) — spawns
               MONOLITH.exe --display-guard …, waits ≤ 5 s for its «armed» event. null → Remove record, Failed
               «не удалось запустить защитный процесс — параметры не изменены».
Temporary      writer.ApplyTemporary(path, target) (ChangeDisplaySettingsEx flags 0; waits ≤ 5 s until CCD shows it).
               failure → writer.RevertTemporary(path, original); guard.Resolve(); Remove; Failed.
Countdown      DeviceConfirm.CountdownAsync(request, 15 s): System.Threading.Timer deadline (works with a hung UI),
               UI prompt = topmost CountdownWindow on a monitor that was not changed, default/focused ВЕРНУТЬ,
               Esc/close = revert. A 1-s poll cancels the prompt (→ revert) when the target monitor disappears.
Keep           writer.Persist(path, target) (CDS_UPDATEREGISTRY) → verify ReadCurrentMode ≈ target → guard.Resolve()
               → Journal.ConfirmPending(record, exact NewValue «2560×1440, 239,97 Гц») → Kept.
               Persist fails / not in temp mode any more → revert branch.
Revert         writer.RevertTemporary(path, original) (CDS(NULL,0) → verify → SDC_APPLY|SDC_USE_DATABASE_CURRENT →
               last resort SDC_USE_SUPPLIED_DISPLAY_CONFIG snapshot) → guard.Resolve() → Remove record → Reverted.
Guard process  --display-guard <recordIdN> <deadlineUtcTicks> <parentPid> mode
               --display-guard <recordIdN> <deadlineUtcTicks> <parentPid> hdr <0|1> <base64url(monitorPath)>
               Opens Local\MONOLITH-DisplayKeep-<id> (resolved) and the parent process; sets
               Local\MONOLITH-DisplayArmed-<id>; WaitAny(resolved, parent) until deadline. Resolved → exit 0.
               Parent died or deadline → mode: CDS(NULL,0) then SDC_USE_DATABASE_CURRENT; hdr: set original. Never
               touches the journal. Runs before single-instance/elevation logic in App.OnStartup; no window.
Startup        MainViewModel (elevated, before drift check): for each active PendingConfirm record: live ≈ original →
               close as reverted («не подтверждено — прежние параметры на месте»); live ≈ target → keep, clear
               PendingConfirm (the keep was persisted, the flag write was lost); neither/unknown → leave, log warning.
Undo           WindowsJournalSystem.RestoreAsync(DisplayMode): monitor missing → error «подключите монитор и повторите»;
               live ≈ original → done; live ≠ recorded target → DeviceConfirm.Ask (no → error, record stays); original
               must be offered + CDS_TEST; then the same Temporary/Countdown/Keep/Revert with the record already present
               (declined → error «откат отменён — оставлены текущие параметры», record stays active).
HDR            Same skeleton: SET_HDR_STATE is persisted by Windows at once, so Keep = guard.Resolve() + ConfirmPending,
               Revert = SetHdr(original) + Resolve + Remove.
Batching       One monitor per countdown. Plan applies display items last, one after another.
```

Pre-ship manual verification (test machine, or the owner's PC only with his explicit consent; never in unit tests or
`--selftest`): (a) flag-0 change is not in `ENUM_REGISTRY_SETTINGS` and a reboot restores the old mode; (b) CDS(NULL)
restores; (c) `SDC_USE_DATABASE_CURRENT` restores; (d) killing MONOLITH during the countdown → guard reverts; (e) a mode
the cable cannot carry reverts by itself; (f) HDR keep/revert/guard; (g) swap-buttons countdown with a real mouse.

---

## 6. Measurement tools (no system change, work without admin)

- **Частота опроса (input):** button «ИЗМЕРИТЬ» → «Водите мышью быстрыми кругами… 5 с» with progress. Dedicated thread,
  `HwndSource(HWND_MESSAGE)`; `GetRegisteredRawInputDevices` first (refuse if mouse class already registered);
  `RegisterRawInputDevices({0x01,0x02,RIDEV_INPUTSINK,hwnd})`; WM_INPUT → `Stopwatch.GetTimestamp` + `GetRawInputData`
  into a preallocated buffer; keep RIM_TYPEMOUSE, relative, non-zero motion; drop `RIM_INPUTSINK` (background) events; group
  by hDevice → device name → instance id → drop virtual. Unregister (`RIDEV_REMOVE`, hwnd NULL) on stop / 5 s after first
  motion / 10 s without motion / page leave / app deactivated / exit — always in `finally`. Estimate: segments split at
  gaps > 30 ms; 50-ms bins inside segments; rate = p90 of bin rates (≥ 20 bins, else «недостаточно движения»); snap
  within ±12 % to 125/250/500/1000/2000/4000/8000; stability = share of bins within ±10 %; jitter = p99/median interval;
  saturation hint when most reports have |dx|+|dy| ≤ 1. Result «≈ 1000 Гц · стабильность 98 % · разброс ×1,3».
- **Проверка DPI (input):** option «10 см по линейке» / «банковская карта 85,6 мм»; hold left button, drag, release;
  three runs; DPI = √(ΣdX²+ΣdY²)/(cm/2.54); median ± 5 %. Feeds pointer-speed compensation and the desktop-travel text.
- **Проверка уровня (audio):** «Говорите обычным голосом 5 секунд…», live meter (`c:Meter`), temporary shared-mode
  capture stream (`IAudioClient` + `IAudioCaptureClient`, released in `finally`); peaks/RMS in dBFS; result + a Suggest
  recommendation for the mic level (target −9 dBFS peak p95, clamp to the endpoint dB range). Mentions the Windows
  microphone indicator; refuses with a link when mic privacy is Deny.

Tools never start by themselves (self-test never presses buttons); results are cached per card for the app session
(`DeviceResultStore`) so a re-scan keeps them.

---

## 7. Page shell, model and UI (core)

### 7.1 Model (namespace `Monolith.Modules.Devices`, plain ObservableObjects, built off the UI thread)

```
enum DeviceKind { Mouse, Keyboard, Monitor, AudioOutput, AudioInput, Lighting, Controller, Peripheral }
enum RecommendPolicy { None, Include, Suggest, Advice }
enum SettingControl { Slider, Choice, DualChoice, Toggle, Info }
enum SettingTest { None, DoubleClick, KeyRepeat }
enum ApplyOutcome { Applied, NoChange, Declined, Blocked, Failed }
readonly record struct DeviceValue(long Number, string? Text = null)          // Of(long) / Of(bool) / Of(string)
record DeviceOption(DeviceValue Value, string Label)
record Recommendation(DeviceValue Value, string Reason, RecommendPolicy Policy, DeviceLink? Link = null)
enum DeviceLinkKind { Settings, SoundPanel, FocusTweak, Navigate }
record DeviceLink(DeviceLinkKind Kind, string Label, string Target, string? FocusId = null)
record DeviceNote(string Text, DeviceLink? Link = null)
record DeviceHardwareRef(string? Vid, string? Pid, string? ContainerId, IReadOnlyList<string> InstanceIds)
class DeviceSetting : ObservableObject   Id, Title, Hint, Control, Min/Max/Step, Options, SecondaryOptions(Func),
      Default, Format(Func), [obs] Current, [obs] Staged, [obs] Recommended, [obs] StateTag, [obs] CanRevert,
      JournalTarget, PerUser, NeedsCountdown, AppliesWhen, Link, Test, DisabledReason,
      Apply: Func<ChangeSession, DeviceValue, CancellationToken, Task<ApplyOutcome>>? (null = read-only)
class DeviceTool : ObservableObject      Id, Title, Description, ButtonText, Options, [obs] SelectedOption, IsRunning,
      Progress, Level, StatusText, ResultText, ResultDetail, Run: Func<DeviceToolRun, CancellationToken, Task>, Cancel()
class VendorAppLink : ObservableObject   AppName, IsInstalled, Note, Launch: Func<Task<bool>>?, OfferInstall
class DeviceCard : ObservableObject      Key, Kind, Title, Subtitle, Facts, Notes, Settings, Tools, VendorApps,
      Hardware (list of DeviceHardwareRef), Order, Glyph (from Kind), [obs] IsHighlighted
interface IDeviceProvider { string Name; Task<IReadOnlyList<DeviceCard>> DetectAsync(DeviceScanContext, CancellationToken); }
interface IDeviceEnricher { string Name; Task EnrichAsync(List<DeviceCard>, DeviceScanContext, CancellationToken); }
class DeviceScanContext { SystemProfile Profile; bool IsElevated; bool IsInteractiveUser; DeviceResultStore Results; }
static partial class DeviceProviders { Providers; Enrichers; partial void AddInput/AddDisplays/AddAudio(List<IDeviceProvider>);
                                       partial void AddVendors(List<IDeviceEnricher>); }
```

Card keys / focus ids: `mouse`, `keyboard`, `display:<monitorPath>`, `audio:<endpointId>`, `periph:<group key>`;
`kind:<kind>` focuses the first card of a kind. Order: mouse 10, keyboard 20, monitors 30+, outputs 50+, inputs 60+,
peripherals 80+.

### 7.2 Behaviour

- `LoadAsync`: `RunBusyAsync("Поиск устройств…")` → `DeviceScan.RunAsync` (providers in parallel on `Task.Run`, 10 s
  timeout each, a failing provider logs `Log.Warn` and is skipped; then enrichers in order) → cards on the UI thread →
  plan rebuilt → journal-based `CanRevert`. `OnReturnAsync`: quiet re-scan (no busy bar) so Optimize changes show.
  `Deactivate`: cancel running tools. `ChangeJournal.Changed` → recompute `CanRevert`.
- Plan (`DevicePlan.Build`, pure): every setting with a recommendation whose policy ≠ None and whose current value differs
  (or Advice rows); Include pre-checked, Suggest unchecked, Advice not selectable; per-user rows disabled when not the
  interactive user; display rows marked «С ПОДТВЕРЖДЕНИЕМ» and sorted last.
- Apply plan: `RequireAdmin` → `Ui.ConfirmAsync("ПРИМЕНИТЬ НАСТРОЙКИ УСТРОЙСТВ?", …, details «Мышь · Скорость указателя: 14
  → 10», footnote about journal + on-screen confirmation)` → one `ChangeSession` («Устройства: рекомендуемые настройки»,
  restore point on) → non-display items, then display items one by one → `Complete` → one toast with counts (applied /
  declined / failed) → re-scan.
- Manual: ПРИМЕНИТЬ (enabled when Staged ≠ Current) → `RequireAdmin` → session «Устройства: <title>» (no restore point)
  → `Apply` → toast → re-scan. ОТКАТИТЬ (when the top active layer of `JournalTarget` has source `devices` and is mine)
  → `ChangeJournal.Instance.RevertAsync([record], LiveState.VerifyRevert)` → toast with verification text.
- Links: Settings (target must start with `ms-settings:`) → `UnelevatedShell.StartAsUserAsync`; SoundPanel (0–3) →
  `StartAsUserAsync(ProcessRunner.Sys("control.exe"), "mmsys.cpl,,N")`; FocusTweak → `Ui.RequestFocus("optimize", id)` +
  `Ui.Navigate("optimize")`; Navigate → optional `RequestFocus` + `Navigate`. Vendor: Launch (toast on false) or
  OfferInstall → `Ui.Navigate("drivers")`.
- Focus: `Ui.FocusRequested` + `TakeFocusRequest("devices")` like Optimize; highlight 2 s; scroll via deferred
  `BringIntoView` (`DispatcherPriority.Background`, never inside CollectionChanged).

### 7.3 Layout (design system: monochrome, compact, R tokens only)

```
c:PageHeader  Title «УСТРОЙСТВА»  Subtitle «Мышь, клавиатура, мониторы и звук: рекомендуемые и ручные настройки.»
              Actions: [Segment РЕКОМЕНДУЕМОЕ | ВРУЧНУЮ] [Btn.Icon Refresh]
ScrollViewer
  (Recommended) Card «РЕКОМЕНДУЕТСЯ ИЗМЕНИТЬ · N»: Row per item:
      [CheckBox] T.Label device · T.BodyStrong setting   F.Mono «14 → 10»   T.Caption reason   Tag/Tag.Muted   [link]
      bottom bar inside the card: T.Caption «Выбрано K из N» · Btn.Primary «ПРИМЕНИТЬ ВЫБРАННОЕ»
    or one Card line with a check glyph «Все настройки устройств совпадают с рекомендуемыми.»
  Device cards (Card, 14 px padding, 8 px gap): header = T.Icon glyph · T.Label KIND · T.H3 title · T.Caption subtitle,
      right: vendor buttons (Btn «ОТКРЫТЬ G HUB» / Btn.Link «УСТАНОВИТЬ В «ДРАЙВЕРАХ»»); T.Caption facts (F.Mono numbers);
      setting rows (Row): Recommended mode → title · current (F.Mono) · «рек. X» · status tag;
                          Manual mode → title · control (Slider/ComboBox/Toggle/two ComboBoxes) · current · hints ·
                          Btn «ПРИМЕНИТЬ» · Btn.Ghost «ОТКАТИТЬ» · optional test area (double-click target / TextBox);
      tool rows: title · description · [ComboBox options] · Btn «ИЗМЕРИТЬ»/«ОТМЕНА» · thin ProgressBar / c:Meter · result;
      notes: T.Caption B.Text3, optional Btn.Link.
CountdownWindow (topmost, DWMWCP_ROUND, R.Xl surface): T.H2 title · message · F.Mono «Возврат к прежним параметрам через 15 с»
      · Btn «СОХРАНИТЬ» (or «ОСТАВИТЬ») · Btn.Primary «ВЕРНУТЬ» (IsDefault, IsCancel, focused).
```

Tags: БЕЗОПАСНО (`Tag`, Include), ПО ЖЕЛАНИЮ (`Tag.Muted`, Suggest), В ОПТИМИЗАЦИИ (`Tag.Muted` + link, Advice),
С ПОДТВЕРЖДЕНИЕМ (`Tag.Muted`), ОЖИДАЕТ ВЫХОДА ИЗ СИСТЕМЫ (`Tag.Hatch`). Read-only mode: the global strip only.

---

## 8. Work packages (disjoint files; core first, the four others in parallel from core's commit)

| Key | Owns (create/modify) | Depends on |
|---|---|---|
| `core` | Core/ChangeJournal.cs, Core/ChangeSession.cs, Core/Elevation.cs, Core/Devices/* (DeviceRecords, DeviceTargets, SpiCatalog, DeviceWriters, DisplaySafeApply, DeviceConfirm, PnpTree), App.xaml.cs, Shell/MainViewModel.cs, UI/Ui.cs, UI/Glyphs.cs, UI/CountdownWindow.xaml(.cs), Modules/Restore/{LiveState,RestoreFormat,DriftDetector}.cs, Modules/Optimize/OptimizeLogic.cs, Modules/Devices/{DevicesViewModel.cs, DevicesView.xaml(.cs), DeviceModel.cs, DeviceProviders.cs, DevicePlan.cs, DeviceApply.cs}, tests/Core/{DeviceJournalTests, DeviceTrustTests, DisplaySafeApplyTests}.cs, tests/Devices/DevicePageTests.cs | — |
| `input` | Modules/Devices/Input/* (SpiWriter, InputDevices, InputProvider, InputAdvice, RawInputSession, PollingRateEstimator, DpiEstimator, DeviceWriters.Input.cs, DeviceProviders.Input.cs), tests/Devices/{InputAdvice,PollingRateEstimator,DpiEstimator,InputProvider}Tests.cs | core |
| `displays` | Modules/Devices/Displays/* (DisplayNative, DisplayWriter, DdcMonitorWriter, DisplayInventory, EdidParser, DisplayAdvice, DisplayProvider, DeviceWriters.Displays.cs, DeviceProviders.Displays.cs), Tweaks/TweakCatalog.Gaming.cs (auto-hdr), Modules/Dashboard/Checks/{HealthCheck,HealthRules}.cs, Modules/Dashboard/DashboardViewModel.cs, tests/DashboardTests.cs, tests/TweakCatalogTests.cs, tests/Devices/{EdidParser,DisplayAdvice,DisplayGuardArgs,AutoHdrTweak}Tests.cs | core |
| `audio` | Modules/Devices/Audio/* (CoreAudioInterop, AudioWriter, AudioInventory, MicLevelMeter, AudioAdvice, AudioProvider, DeviceWriters.Audio.cs, DeviceProviders.Audio.cs), tests/Devices/{AudioAdvice,MicLevelAnalysis,AudioParsing,DuckingJournal}Tests.cs | core |
| `vendors` | Modules/Devices/Vendors/* (DeviceInventory, VendorApps, ReceiverHints, VendorEnricher, DeviceProviders.Vendors.cs), Modules/Drivers/Hardware/{HardwareCatalog,HardwareRules,HardwareModel,HardwareAdvisor,HardwareScanner,HardwareItems}.cs, Modules/Drivers/DriversViewModel.Hardware.cs, tests/HardwareTests.cs, tests/Devices/{DeviceInventory,VendorApps,ReceiverHints}Tests.cs | core |

The partial hooks (`DeviceProviders.AddInput` …, `DeviceWriters.InitInput` …) are declared once in core and implemented
once in each feature package's own file (same namespace as the core declaration). Missing implementation = that section
or writer is absent: the page shows less, and undo of that kind fails with «эта версия MONOLITH не умеет откатывать такое
изменение» (record stays active) — never a silent no-op.

Merge order: core → input, displays, audio, vendors (any order; no shared files). After the merge: build (0 warnings),
`dotnet test`, `MONOLITH.exe --selftest snapshots/selftest` (Devices page must be OK), then the §5 manual verification.

Suggested CLAUDE.md delta (to be applied by the orchestrator/owner, not by a package): Pages list gains `devices`
(«УСТРОЙСТВА») after games; rule 1 lists the new ChangeSession primitives; Minimal UI gains «device settings,
measurements and vendor-app launch → Devices; Windows-setting tweaks stay in Optimize (Devices shows state + link);
vendor software install → Drivers»; command line gains `--display-guard` (internal).

---

## 9. Research items: v1 status

| Research id | v1 |
|---|---|
| mouse-pointer-speed, wheel-scroll-lines, wheel-scroll-chars (conditional), scroll-inactive-windows, double-click-time, swap-mouse-buttons, mouse-trails, mouse-sonar, mouse-vanish, keyboard-repeat-delay, keyboard-repeat-rate | implemented (UserParameter) |
| enhance-pointer-precision, accessibility-hotkeys | INFO + link to Optimize |
| mouse-polling-measure, mouse-dpi-measure, mouse-dpi-advice, mouse-polling-advice, keyboard-polling, vendor-app-handoff, dynamic-lighting, scroll-direction | INFO / tools / links |
| accessibility-features-state | INFO + Settings link only (no struct write in v1) |
| toggle-keys-sound | deferred (would be a second writer of the ToggleKeys Flags value the tweak journals) |
| display-refresh-rate, display-resolution | implemented (DisplayMode, safe-apply) |
| display-hdr | implemented (DisplayHdr, countdown) |
| display-ddc-brightness | implemented (MonitorSetting) |
| display-auto-hdr | new Optimize tweak `auto-hdr` (DxTokenTweak) + INFO link |
| display-mode-safe-apply | implemented (core) |
| display-hdr-calibration, sdr-white-level, acm, scaling, vrr, color-depth, link-bandwidth, mixed-refresh, primary, drr | INFO (only when actionable) |
| audio-ducking | implemented (allow-listed SetRegistry) |
| audio-mic-level | implemented (AudioLevel + level tool) |
| audio-mic-boost-agc | deferred to v2 (no part on this PC, cannot be verified; needs IDeviceTopology writer) |
| audio-default-devices, shared-format, enhancements, spatial-sound, exclusive-mode, monitor-endpoint-hide, vendor-apps, mono-check, mic-privacy-check | INFO + links |
| audio-gpu-hdmi-audio-disable (ADVANCED) | not in v1 (no benefit; minimal UI) |
| device-inventory, product-name, grouping, virtual-filter, kind, receiver-paired-hint, device-serial-privacy, vid-vendor-map, vendor-app-detect, vendor-app-launch, onboard-memory-advice, dynamic-lighting-detect, rgb-unifier-apps (info line only) | implemented (vendors) |
| vendor-app-install | existing Drivers flow + MSI Center (link-first) |
| vendor-autostart-advice | deferred (Startup page; not part of this request) |
| every DO-NOT-INCLUDE item (MarkC curves, queue sizes, hidusbf, HID power, raw-input registry, FilterKeys repeat hack, vendor protocol reads/writes, firmware, WinRing0/PawnIO, vendor debloat, ghost removal, IPolicyConfig, FxProperties edits, MMCSS, latency tweaks, audio services, audiodg, 96/192 kHz, overclock/CRU, TDR, DPI registry, DWM disable, force max bpc, DDC vendor codes) | absent, not even hidden |

---

## 10. Risks

See the structured result of the architecture step (also summarised here): display black-out (mitigated by §5 + manual
verification), journal downgrade incompatibility (older builds cannot parse the new `ChangeKind` strings and treat the
journal as damaged), SPI per-user split under another admin account (gated), `SPIF_SENDCHANGE` broadcast blocking on
hung windows while holding the journal gate, raw-input listener cost if left registered (timeboxed, `finally`),
microphone privacy indicator during the level test, DDC/CI monitors with incomplete MCCS (read-back, two VCP codes only,
explicit apply), NVIDIA overriding the HDR toggle, COM interop mistakes without `[PreserveSig]`, vendor-app path
hijacking (fixed table + Program Files + Authenticode signer), partial-hook omissions at merge time, self-test log
errors from expected detection failures (use Warn/Info), localized endpoint/part names (never matched for logic).
