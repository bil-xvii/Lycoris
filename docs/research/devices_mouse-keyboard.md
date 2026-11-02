# Lycoris: devices — mouse and keyboard (Win11 23H2–26H2, Win10 22H2)

Scope: what a «УСТРОЙСТВА» page can **read, measure, recommend and change** for mice and keyboards through documented
Windows mechanisms, without new NuGet packages. Everything that lives in device firmware (DPI, report/polling rate,
lift-off distance, debounce, onboard profiles, RGB effects) is **not writable by Windows**: Lycoris measures it where
possible, gives evidence-based advice and hands off to the vendor app (G HUB here). Classifications are binding
(CLAUDE.md): SAFE-DEFAULT / SAFE-OPTIONAL / ADVANCED / DO-NOT-INCLUDE, plus INFO-ONLY = Lycoris reads/measures/advises,
never writes.

## 0. Conventions and this PC (read-only, 2026-10-27)

**Labels**
- **SPI** = `SystemParametersInfoW` (user32). `fWinIni = 3` = `SPIF_UPDATEINIFILE (0x1) | SPIF_SENDCHANGE (0x2)`:
  persist to the user profile **and** broadcast `WM_SETTINGCHANGE`, so the change is live at once — no sign-out
  (unlike the registry-only tweaks `mouse-acceleration-off` / `accessibility-hotkeys-off`, which need a sign-out).
- **Live vs pending:** SPI_GET* returns the live session value; `HKCU\Control Panel\…` holds the persisted one. When they
  differ, a registry-only change is waiting for sign-out — show «вступит в силу после выхода из системы».
- **Per-user:** every writable setting here is per-user (HKCU). Windows does not require admin for them; Lycoris's
  `ChangeSession` does (journal in `%ProgramData%\MONOLITH`). Measurements and reads work in read-only mode.
- **Never verified by writing on this PC** (prime directive): SPI→registry mappings below come from Microsoft docs and
  the values Windows already wrote here; confirm them in unit tests against a redirected/test profile or a VM.

**Values read on this machine**

| Item | Live (SPI / API) | Registry (HKCU) |
|---|---|---|
| Pointer speed | `SPI_GETMOUSESPEED` = **10** (1:1) | `Control Panel\Mouse\MouseSensitivity` = "10" (REG_SZ) |
| Enhance pointer precision | `SPI_GETMOUSE` = {0,0,0} → **off** | MouseSpeed/Threshold1/Threshold2 = "0"/"0"/"0" (the `mouse-acceleration-off` tweak or the user). `HKU\.DEFAULT` (logon screen) still "1"/"6"/"10" — correctly untouched |
| SmoothMouseX/YCurve | — | Windows defaults (`…15,6e…`, `…fd,11,01…`): **no MarkC fix** |
| Wheel | lines **3**, chars **3**, routing **2** (scroll inactive windows on) | `Control Panel\Desktop\WheelScrollLines`/`WheelScrollChars` REG_SZ "3"; `MouseWheelRouting` REG_DWORD 2 |
| Double-click | `GetDoubleClickTime` = **500** ms, rect 4×4 | `DoubleClickSpeed` "500" |
| Buttons | `SM_SWAPBUTTON` = 0, `SM_CMOUSEBUTTONS` = 16, wheel yes, **horizontal wheel no** (`SM_MOUSEHORIZONTALWHEELPRESENT` = 0) | `SwapMouseButtons` "0" |
| Trails / Sonar / Vanish | 0 / **off** / **on** | `MouseTrails` "0"; `UserPreferencesMask` = 9E 1E 07 80 12 00 00 00 (byte 2 bit 0x01 = vanish on; byte 1 bit 0x40 = sonar off; byte 1 0x20 cursor shadow off — visual-effects tweak) |
| Keyboard repeat | delay **1** (~500 ms), speed **31** (~30/s) | `Control Panel\Keyboard\KeyboardDelay` "1", `KeyboardSpeed` "31" (REG_SZ) |
| Sticky / Filter / Toggle Keys | dwFlags 506 / 122 / 58 → all features **off**, all **shortcut bits cleared** (tweak applied) | `Accessibility\StickyKeys\Flags` "506", `Keyboard Response\Flags` "122" (AutoRepeatDelay 1000, AutoRepeatRate 500, DelayBeforeAcceptance 1000, BounceTime 0), `ToggleKeys\Flags` "58" |
| Class drivers | — | `kbdclass\Parameters\KeyboardDataQueueSize` = 100 (default, present); `mouclass\Parameters` has **no** MouseDataQueueSize (default 100) |
| HID power | — | Receiver interface `USB\VID_046D&PID_C54D&MI_00\…\Device Parameters`: `SelectiveSuspendEnabled` = 0 (binary), EnhancedPowerManagementEnabled 1, AllowIdleIrpInD3 1 → **selective suspend already off** (Windows HID default) |
| Dynamic Lighting | — | `HKCU\Software\Microsoft\Lighting\AmbientLightingEnabled` = 1; service `logi_lamparray_service` running |

**Devices (PnP + Raw Input enumeration, read-only)**

| Device | IDs | Notes |
|---|---|---|
| Logitech LIGHTSPEED receiver (PRO X SUPERLIGHT 2 family) | USB `VID_046D&PID_C54D` (serial 375C32603335), parent `USB\ROOT_HUB30` (directly on a root port, no hub) | Raw mouse `\\?\HID#VID_046D&PID_C54D&MI_00#…`; also a keyboard TLC (MI_01 Col01) and vendor TLCs (MI_02 = HID++). 7 LampArray slots, 8 pairing slots. Logitech spec: up to **8000 Hz**, 100–44 000 DPI, receiver = USB High Speed, wired = Full Speed |
| G HUB virtual mouse | `HID\VID_046D&PID_C231`, **parent `LGHUBDevice\…`** | Second raw mouse; emits only when G HUB injects (macros/remaps). Must be excluded from measurements |
| Logitech PRO keyboard | USB `VID_046D&PID_C339`, LampArray | Raw keyboards MI_00 and MI_01 Col01 |
| G HUB virtual keyboard | `HID\VID_046D&PID_C232`, parent `LGHUBDevice\…` | Exclude |
| `RID_DEVICE_INFO_MOUSE.dwSampleRate` | **0** for both mice | Windows does not know the USB polling rate → it must be **measured** (§5) |
| Vendor apps | Logitech G HUB 2026.7.992920 (`C:\Program Files\LGHUB\lghub.exe`, `lghub_agent` running), Razer Synapse 4.0.503, INZONE Hub 1.0.20 | Hand-off targets |

---

## 1. Summary

| id | RU title | Class | Recommended | Manual range | Read / write | Admin |
|---|---|---|---|---|---|---|
| `mouse-pointer-speed` | Скорость указателя | SAFE-OPTIONAL | **10** (1:1) for everyone; change DPI in G HUB instead | 1–20 | SPI 0x70 / 0x71 | no |
| `enhance-pointer-precision` | Повышенная точность указателя | INFO-ONLY here (tweak = SAFE-OPTIONAL) | off | — (Optimize tweak) | SPI_GETMOUSE + registry; write via existing tweak | no |
| `wheel-scroll-lines` | Строк за один шаг колёсика | SAFE-OPTIONAL | 3 (Windows default) | 1–100, «один экран» | SPI 0x68 / 0x69 | no |
| `wheel-scroll-chars` | Символов за шаг горизонтальной прокрутки | SAFE-OPTIONAL | 3 | 1–100 (only if tilt wheel) | SPI 0x6C / 0x6D | no |
| `scroll-inactive-windows` | Прокручивать неактивные окна при наведении | SAFE-OPTIONAL | on (default) | on / off | SPI 0x201C / 0x201D | no |
| `double-click-time` | Скорость двойного щелчка | SAFE-OPTIONAL | 500 ms (default) | 200–900 ms | GetDoubleClickTime / SPI 0x20 | no |
| `swap-mouse-buttons` | Поменять кнопки мыши местами | SAFE-OPTIONAL (manual only, auto-revert) | off unless left-handed | on / off | SM_SWAPBUTTON / SPI 0x21 | no |
| `mouse-trails` | Шлейф указателя | SAFE-OPTIONAL | off | off, 2–7 | SPI 0x5E / 0x5D | no |
| `mouse-sonar` | Показывать указатель по нажатию Ctrl | SAFE-OPTIONAL | off (Ctrl is a game bind) | on / off | SPI 0x101C / 0x101D | no |
| `mouse-vanish` | Скрывать указатель при вводе текста | SAFE-OPTIONAL | on (Windows out-of-box) | on / off | SPI 0x1020 / 0x1021 | no |
| `keyboard-repeat-delay` | Задержка перед повтором клавиши | SAFE-OPTIONAL | 1 (~500 ms); 0 for fast typists | 0–3 | SPI 0x16 / 0x17 | no |
| `keyboard-repeat-rate` | Скорость повтора клавиши | SAFE-OPTIONAL | 31 (fastest, default) | 0–31 | SPI 0x0A / 0x0B | no |
| `accessibility-hotkeys` | Сочетания клавиш специальных возможностей | INFO-ONLY here (tweak = SAFE-OPTIONAL) | shortcuts off (gamers) | — (Optimize tweak) | SPI_GET*KEYS bit 0x4 | no |
| `accessibility-features-state` | Залипание клавиш и фильтрация ввода включены | SAFE-OPTIONAL (manual, only when found ON) | off unless the user relies on them | off | SPI 0x3A/0x3B, 0x32/0x33 | no |
| `toggle-keys-sound` | Звук при нажатии Caps Lock, Num Lock, Scroll Lock | SAFE-OPTIONAL | off (default) | on / off | SPI 0x34 / 0x35 bit 0x1 | no |
| `mouse-polling-measure` | Частота опроса мыши (измерение) | INFO-ONLY | — (measurement) | — | Raw Input, timeboxed | no |
| `mouse-dpi-measure` | Проверка DPI мыши | INFO-ONLY | — (measurement) | — | Raw Input, user moves a known distance | no |
| `mouse-dpi-advice` | DPI мыши: рекомендации | INFO-ONLY | 800–1600 DPI at pointer speed 10 on 1440p | set in G HUB | — | no |
| `mouse-polling-advice` | Частота опроса: рекомендации | INFO-ONLY | 1000 Hz; 2000–4000 Hz optional on this CPU/240 Hz; 8000 Hz only after testing | set in G HUB | — | no |
| `keyboard-polling` | Частота опроса клавиатуры | INFO-ONLY | vendor spec (not measurable) | set in vendor app | — | no |
| `vendor-app-handoff` | Настройки в приложении производителя | INFO-ONLY | open G HUB / Synapse unelevated | — | Uninstall keys, processes | no |
| `dynamic-lighting` | Подсветка (динамическое освещение Windows) | INFO-ONLY | pick one controller (G HUB **or** Windows) | — | ms-settings link | no |
| `scroll-direction` | Направление прокрутки колёсика | INFO-ONLY | default | — | ms-settings link | no |
| `markc-mouse-fix` | «MarkC fix» (кривые ускорения) | DO-NOT-INCLUDE | — | — | — | — |
| `input-data-queue-size` | MouseDataQueueSize / KeyboardDataQueueSize | DO-NOT-INCLUDE | — | — | — | — |
| `usb-polling-overclock` | «Разгон» USB-опроса драйвером (hidusbf) | DO-NOT-INCLUDE | — | — | — | — |
| `hid-power-management-off` | Отключение энергосбережения HID/USB устройств ввода | DO-NOT-INCLUDE | — | — | — | — |
| `raw-input-buffer-registry` | «Raw input buffer» в реестре | DO-NOT-INCLUDE | — | — | — | — |
| `filterkeys-fast-repeat` | Ускорение повтора через «Фильтрацию ввода» | DO-NOT-INCLUDE | — | — | — | — |
| `vendor-protocol-writes` | Запись DPI/опроса/RGB по протоколу производителя | DO-NOT-INCLUDE | — | — | — | — |

---

## 2. How the Devices page should do it

### 2.1 Reading
- Read every value **live** with SPI_GET*/`GetDoubleClickTime`/`GetSystemMetrics` and **persisted** from HKCU; show
  «ожидает выхода из системы» when they differ (happens after the registry-only Optimize tweaks).
- Read through `InteractiveUser` (`Modules/Games/InteractiveUser.cs`) for the registry side. **Write only when
  `UserScope.CurrentSid` == the interactive user's SID** (owner of explorer.exe). SPI changes the live state of the
  session but `SPIF_UPDATEINIFILE` persists into the profile of the *calling* account: under over-the-shoulder UAC
  (different admin account) the change would land in the admin's HKCU and vanish at the next logon. In that case the
  page is read-only for these rows, with one muted line «Запустите Lycoris под своей учётной записью».
- SPI calls with `SPIF_SENDCHANGE` broadcast `WM_SETTINGCHANGE` and can block on hung windows → always off the UI
  thread (`Task.Run`), per CLAUDE.md.

### 2.2 Writing through the journal (proposal)
`ChangeSession` has no SPI method today; the visual-effects tweak runs SPI through a journaled PowerShell script
(`spi:visual-effects`, validated by `ChangeJournal.RebuildSpi`). For a page with ~12 parametric settings a typed path is
safer and simpler:

```
ChangeSession.SetUserParameter(UserParameter p, int value, string source, string title)
  target   = "spi:" + p.Id                     // per-user → UserScope.IsPerUserCommand already matches "spi:"
  record   = { Kind = UserParameter, OldValue = live value (SPI_GET), NewValue = value,
               Registry = snapshot(s) of the value(s) this action persists, UserSid = CurrentSid }
  steps    = export the HKCU key to .reg → journal.Add → SPI_SET(value, fWinIni = 3) → SPI_GET and compare
             → mismatch: SPI_SET(old, 3), remove record, report error
  revert   = SPI_SET(OldValue, 3) → if the registry snapshot said "absent", delete the value Windows just wrote
             → SPI_GET verify
```
- `UserParameter` is a **closed whitelist** compiled into Lycoris: (GET action, SET action, how the value is passed —
  uiParam or pvParam, documented range, registry value(s) it persists). The journal is untrusted input
  (`ChangeJournal.IsForbiddenRestoreTarget` spirit): revert accepts only a known id and a value inside its range.
- Bit-type settings stored in `UserPreferencesMask` (sonar, vanish): never restore the whole binary mask — that would
  roll back unrelated bits (e.g. the visual-effects tweak). Restore the one setting via its SPI_SET.
- Struct settings (Sticky/Filter/Toggle Keys): read the full struct with SPI_GET, change one bit, write the full struct;
  journal the full original struct **and** every registry value the write rewrites (gaming_tweaks.md §D:
  `SPI_SETFILTERKEYS` + `SPIF_UPDATEINIFILE` rewrites all of `Keyboard Response`).
- Unit tests: `TweakApplyRevertTests`-style test that applies and reverts each `UserParameter` against a redirected
  registry is not possible for live SPI; instead test the whitelist/range validation and the record round-trip, and
  run a manual QA pass on a VM.

### 2.3 Relation to the Optimize tweaks (Minimal-UI rule)
"Windows settings as tweaks → Optimize only; every function in exactly one place." Therefore:
- `mouse-acceleration-off` and `accessibility-hotkeys-off` stay **only** in Optimize. The Devices page shows their state
  as a read-only line with a link: `Ui.RequestFocus("optimize", "mouse-acceleration-off")`. It must **not** add a second
  toggle or write MouseSpeed/Threshold or the hotkey bit itself (two writers on one target = layered journal records
  and confusing undo).
- Parametric device settings (sliders/values: pointer speed, wheel, double-click, keyboard repeat, trails…) have no home
  today → the Devices page owns them.
- Optional improvement for the existing tweak (owner decision, not required): apply EPP live with
  `SPI_SETMOUSE(0, int[3]{0,0,0}, 3)` through the new journaled path so no sign-out is needed (gaming_tweaks.md already
  documents this).

### 2.4 "Recommended" vs manual
- Each row: current value · recommended value · tag (БЕЗОПАСНО / ПО ЖЕЛАНИЮ) · manual control (Slider/ComboBox/Toggle)
  · per-row ОТКАТИТЬ when Lycoris changed it.
- One button «Рекомендуемые» applies only rows whose value differs from the recommendation, after `Ui.ConfirmAsync`
  listing every change («Скорость указателя: 14 → 10» …). Pointer speed is **excluded** from the bulk action unless DPI
  compensation is known (§3.1) — it changes how the mouse feels.
- One button «Как в Windows» for the documented defaults (§7 table), also with the confirmation list.
- Built-in test zones instead of explanations: a double-click target, a text box for key-repeat, and the polling/DPI
  measurement panels.
- Bulk undo stays in Restore (owner rule).

---

## 3. Mouse settings

#### `mouse-pointer-speed`: Скорость указателя / Pointer speed → **SAFE-OPTIONAL**
- **What it is:** the multiplier Windows applies to mouse counts for the desktop cursor (`WM_MOUSEMOVE`). Games that read
  Raw Input (`WM_INPUT`) ignore it: Microsoft — WM_MOUSEMOVE has pointer ballistics applied, WM_INPUT is read directly
  from the HID stack without them.
- **Values:** 1–20, default 10 (Microsoft SPI docs). Multiplier with Enhance pointer precision **off**
  (Liquipedia table, reposted by yuuza.net): 1→1/32, 2→1/16, 3→1/8, 4→2/8, 5→3/8, 6→4/8, 7→5/8, 8→6/8, 9→7/8,
  **10→1.0**, 11→1.25, 12→1.5 … 20→3.5 (+0.25 per step). The old 11-notch Control Panel slider = 1, 2, 4, 6 … 20; the
  6th notch («6/11») = 10.
- **Recommended:** **10 for everyone** (with EPP off): one sensor count = one pixel, no skipped pixels (above 10 each
  count moves >1 px, so some pixels can't be reached) and no dropped fractions. If the cursor is too slow or fast at 10,
  change DPI in G HUB, not this slider. This PC: already 10.
- **Suggestion logic:** if current ≠ 10, show «Поставьте 10 и измените DPI в G HUB в ×k раз», k = multiplier(current).
  With a measured DPI (§6) give the exact target: `newDPI = DPI × multiplier(current)` (e.g. 800 DPI at 14 (2.0×) →
  1600 DPI at 10). Never change it in a bulk action without this explanation.
- **Read:** `SystemParametersInfoW(SPI_GETMOUSESPEED = 0x0070, 0, out int, 0)`.
- **Write:** `SystemParametersInfoW(SPI_SETMOUSESPEED = 0x0071, 0, (IntPtr)value, 3)` — the value goes **in pvParam**
  (not a pointer). Persists `HKCU\Control Panel\Mouse\MouseSensitivity` (REG_SZ "1".."20"). Verify with SPI_GET.
- **Revert:** `SPI_SETMOUSESPEED(original, 3)`; delete `MouseSensitivity` if it was absent. Admin: no.
- **Risk:** low (feel only). Evidence: Microsoft SPI docs; Microsoft "Taking advantage of high-definition mouse
  movement"; Liquipedia/yuuza multiplier table; Logitech and Razer both tell users to turn EPP off and set DPI in the
  vendor app.

#### `enhance-pointer-precision`: Повышенная точность указателя / Enhance pointer precision → **INFO-ONLY on this page** (the setting = Optimize tweak `mouse-acceleration-off`, SAFE-OPTIONAL, gaming_tweaks.md §D)
- **Show:** live state `SPI_GETMOUSE (0x0003)` → int[3] {threshold1, threshold2, acceleration}; acceleration 0 = off.
  Pending state from `HKCU\Control Panel\Mouse\MouseSpeed`. Link «Изменить в Оптимизации».
- **Recommended:** off. Logitech support recommends turning it off when DPI behaves inconsistently after changing the
  wireless report rate in G HUB and when tracking differs between performance/endurance modes; Razer lists it as a
  HyperPolling prerequisite. Raw-input games are unaffected; the desktop and system-cursor games are.
- This PC: off live and in the registry. The logon screen (`HKU\.DEFAULT`) keeps 1/6/10 — correct, not touched.

#### `wheel-scroll-lines`: Строк за один шаг колёсика / Wheel scroll lines → **SAFE-OPTIONAL**
- Default 3 (Microsoft). Settings range 1–100 plus «один экран» = `WHEEL_PAGESCROLL` (UINT_MAX; persisted as "-1" —
  verify). 0 disables scrolling → Lycoris must not offer 0.
- **Recommended:** 3. On the portrait Odyssey G5 (2560 px tall) 4–6 may feel better for long pages — personal choice,
  no effect in games (games use wheel deltas).
- **Read:** `SPI_GETWHEELSCROLLLINES (0x0068)`, pvParam → UINT. **Write:** `SPI_SETWHEELSCROLLLINES (0x0069)`,
  **uiParam** = lines, pvParam = NULL, fWinIni 3 → `HKCU\Control Panel\Desktop\WheelScrollLines` (REG_SZ).
- **Revert:** SPI_SET(original, 3). Risk: none.

#### `wheel-scroll-chars`: Символов за шаг горизонтальной прокрутки / Horizontal scroll chars → **SAFE-OPTIONAL**
- Default 3. **Read:** `SPI_GETWHEELSCROLLCHARS (0x006C)`; **write:** `SPI_SETWHEELSCROLLCHARS (0x006D)`, uiParam,
  fWinIni 3 → `Control Panel\Desktop\WheelScrollChars` (REG_SZ). Revert as above.
- Show the row only when `GetSystemMetrics(SM_MOUSEHORIZONTALWHEELPRESENT = 91)` ≠ 0 — **0 on this PC**, so hidden here.

#### `scroll-inactive-windows`: Прокручивать неактивные окна при наведении / Scroll inactive windows → **SAFE-OPTIONAL**
- **Read:** `SPI_GETMOUSEWHEELROUTING (0x201C)` → DWORD: 0 = `MOUSEWHEEL_ROUTING_FOCUS`, 1 = `HYBRID`, 2 =
  `MOUSEWHEEL_ROUTING_MOUSE_POS` (window under the cursor; Windows 10+). This PC: **2**, registry
  `Control Panel\Desktop\MouseWheelRouting` REG_DWORD 2 (the Windows 10/11 Settings default "on").
- **Write:** `SPI_SETMOUSEWHEELROUTING (0x201D)`, uiParam 0, value in pvParam, fWinIni 3. Microsoft's page is ambiguous
  about pvParam (value vs pointer) → always read back and roll back on mismatch. Toggle maps on = 2 / off = 0
  (verify on a VM).
- **Recommended:** on (default). Revert: SPI_SET(original, 3). Risk: none.

#### `double-click-time`: Скорость двойного щелчка / Double-click speed → **SAFE-OPTIONAL**
- **Read:** `GetDoubleClickTime()` (500 here). **Write:** `SPI_SETDOUBLECLICKTIME (0x0020)`, **uiParam** = ms (Windows
  clamps >5000 to 5000), fWinIni 3 → `Control Panel\Mouse\DoubleClickSpeed` (REG_SZ). Don't use `SetDoubleClickTime`
  alone — it doesn't persist.
- **Manual range:** 200–900 ms (the Control Panel slider span); never below 200 — double-clicks become impossible for
  many people. **Recommended:** 500 (default). No gaming effect.
- **Revert:** SPI_SET(original, 3). UI: a double-click test target next to the slider. Risk: low.

#### `swap-mouse-buttons`: Поменять кнопки мыши местами / Swap primary button → **SAFE-OPTIONAL (manual only, never in presets)**
- **Read:** `GetSystemMetrics(SM_SWAPBUTTON = 23)`. **Write:** `SPI_SETMOUSEBUTTONSWAP (0x0021)`, uiParam TRUE/FALSE,
  fWinIni 3 → `Control Panel\Mouse\SwapMouseButtons` "0"/"1".
- **Safety:** after applying, show a 15-second «Оставить?» dialog that must be confirmed with the **new** primary button;
  no answer → automatic revert (same pattern as Windows' display-resolution confirmation). Prevents a user who clicked
  it by mistake from being stuck.
- Raw-input games see physical buttons (the swap applies to legacy mouse messages) and have their own option; G HUB can
  also remap buttons — warn about double swapping if G HUB has a left/right remap. **Recommended:** off unless
  left-handed. Revert: SPI_SET(original, 3).

#### `mouse-trails`: Шлейф указателя / Pointer trails → **SAFE-OPTIONAL (recommend off)**
- **Read:** `SPI_GETMOUSETRAILS (0x005E)` → int (0/1 = off, >1 = number of cursors). **Write:**
  `SPI_SETMOUSETRAILS (0x005D)`, **uiParam** = 0 or 2–7 (Control Panel range), fWinIni 3 →
  `Control Panel\Mouse\MouseTrails` (REG_SZ).
- **Recommended:** off (default); it is a visibility aid, not a performance feature — if found on, suggest turning it
  off (visual clutter on a 240 Hz screen), never claim FPS. Revert: SPI_SET(original, 3).

#### `mouse-sonar`: Показывать указатель по нажатию Ctrl / Pointer location (Sonar) → **SAFE-OPTIONAL**
- **Read:** `SPI_GETMOUSESONAR (0x101C)` → BOOL. **Write:** `SPI_SETMOUSESONAR (0x101D)`, uiParam 0,
  pvParam = (IntPtr)TRUE/FALSE, fWinIni 3. Persisted as a bit of `Control Panel\Desktop\UserPreferencesMask`
  (bit (0x101C−0x1000)/2 = 14 → byte 1, 0x40). Default off (Microsoft).
- **Recommended:** off for gamers — it fires when Ctrl is pressed and released alone, and Ctrl is a common game bind;
  on is reasonable for finding the cursor across two 1440p monitors. Revert: SPI_SET(original, 3) — never restore the
  whole mask.

#### `mouse-vanish`: Скрывать указатель при вводе текста / Hide pointer while typing → **SAFE-OPTIONAL**
- **Read:** `SPI_GETMOUSEVANISH (0x1020)`. **Write:** `SPI_SETMOUSEVANISH (0x1021)`, pvParam = (IntPtr)BOOL, fWinIni 3.
  UPM bit 16 (byte 2, 0x01). Microsoft's page says "default off", but out-of-box Windows profiles have the checkbox on
  and this PC reads **on** (UPM byte 2 = 0x07).
- **Recommended:** keep on. No gaming effect. Revert: SPI_SET(original, 3).

Not covered on purpose: ClickLock, Snap To default button (`SPI_SETSNAPTODEFBUTTON` can make an accidental click
confirm a dialog — no benefit worth that), hover time, cursor schemes.

---

## 4. Keyboard settings

#### `keyboard-repeat-delay`: Задержка перед повтором / Repeat delay → **SAFE-OPTIONAL**
- **Values:** 0–3 ≈ 250 / 500 / 750 / 1000 ms, hardware dependent (Microsoft). Default 1. This PC: 1.
- **Read:** `SPI_GETKEYBOARDDELAY (0x0016)` → int. **Write:** `SPI_SETKEYBOARDDELAY (0x0017)`, **uiParam** 0–3,
  fWinIni 3 → `HKCU\Control Panel\Keyboard\KeyboardDelay` (REG_SZ).
- **Recommended:** 1 (default) for everyday; 0 for fast typists/coders (quicker held-key navigation) — offered as a
  manual choice, not a preset. Games read key-down/key-up state, so repeat settings don't change movement — only
  chat/menus. When FilterKeys is **on**, its own AutoRepeatDelay overrides this (FILTERKEYS docs) → show that.
- **Revert:** SPI_SET(original, 3). Risk: very short delay causes accidental repeats for slow typists. Test text box.

#### `keyboard-repeat-rate`: Скорость повтора / Repeat rate → **SAFE-OPTIONAL**
- **Values:** 0–31 ≈ 2.5–30 repeats/s, up to ±20 % hardware variation; >31 is clamped (Microsoft). Default 31. This PC: 31.
- **Read:** `SPI_GETKEYBOARDSPEED (0x000A)` → DWORD. **Write:** `SPI_SETKEYBOARDSPEED (0x000B)`, **uiParam** 0–31,
  fWinIni 3 → `Control Panel\Keyboard\KeyboardSpeed` (REG_SZ).
- **Recommended:** 31. Faster than 31 is only possible through the FilterKeys hack → DO-NOT-INCLUDE (§10).
  Revert: SPI_SET(original, 3).

#### `accessibility-hotkeys`: Сочетания клавиш специальных возможностей → **INFO-ONLY on this page** (Optimize tweak `accessibility-hotkeys-off`)
- Show the three shortcut bits (0x4 of STICKYKEYS/FILTERKEYS/TOGGLEKEYS dwFlags, live via SPI_GET*KEYS and pending via
  registry `Flags`) and link to the tweak. This PC: all three cleared (506/122/58).

#### `accessibility-features-state`: Залипание клавиш / фильтрация ввода включены → **SAFE-OPTIONAL (manual turn-off only when found ON; never automatic)**
- **Why:** an accidentally enabled FilterKeys makes keystrokes count only after `iWaitMSec` (SlowKeys) or ignores quick
  repeats (BounceKeys) — keyboard "lags" in games; StickyKeys latches modifiers. People who rely on them must keep them,
  so Lycoris only **detects and offers**: «Фильтрация ввода включена: нажатия принимаются с задержкой N мс. Выключить?»
- **Read:** `SPI_GETSTICKYKEYS (0x003A)` (cbSize 8), `SPI_GETFILTERKEYS (0x0032)` (cbSize 24) → bit 0x1
  (`SKF_STICKYKEYSON` / `FKF_FILTERKEYSON`).
- **Write:** SPI_SET with the **full struct read just before**, clearing only bit 0x1 (`SPI_SETSTICKYKEYS 0x003B`,
  `SPI_SETFILTERKEYS 0x0033`, uiParam = cbSize, fWinIni 3). Keep the shortcut bit as it is (owned by the tweak).
- **Revert:** SPI_SET with the original full struct, then restore the byte-for-byte registry snapshot of every value
  under `Control Panel\Accessibility\StickyKeys` / `Keyboard Response` (FilterKeys writes rewrite all of them).
- Simpler alternative: link `ms-settings:easeofaccess-keyboard`. This PC: both off → row hidden.

#### `toggle-keys-sound`: Звук при нажатии Caps Lock, Num Lock, Scroll Lock / Toggle Keys → **SAFE-OPTIONAL**
- ToggleKeys = a high tone when Caps/Num/Scroll Lock turns on, low tone when off (TOGGLEKEYS docs). Default off.
  This PC: off (58).
- **Read:** `SPI_GETTOGGLEKEYS (0x0034)`, cbSize 8 → bit 0x1 `TKF_TOGGLEKEYSON`. **Write:** `SPI_SETTOGGLEKEYS (0x0035)`,
  uiParam 8, full struct with only bit 0x1 changed, fWinIni 3 → `Control Panel\Accessibility\ToggleKeys\Flags` (REG_SZ).
- **Recommended:** off; on for keyboards without lock LEDs or users who hit Caps Lock by accident. No gaming effect.
- **Journal interplay:** this and `accessibility-hotkeys-off` persist the **same** registry value. The tweak's undo
  restores the whole string, which would also undo a later sound change (drift detection shows it). Keep the record
  bit-scoped (`spi:togglekeys-sound`, revert via SPI bit 0x1) so reverting the sound never touches bit 0x4.
- **Revert:** SPI_SET(original struct, 3).

---

## 5. Measuring the real mouse polling rate (`mouse-polling-measure`, INFO-ONLY)

**Why measure:** Windows doesn't expose it — `RID_DEVICE_INFO_MOUSE.dwSampleRate` is 0 for both USB mice here (it's a
PS/2-era field), and the USB descriptor's bInterval says nothing about the rate the LIGHTSPEED firmware actually sends.
The only honest number is the arrival rate of HID reports while the mouse moves.

**Design (WPF, no packages)**
1. **User-initiated, timeboxed (≤ 5 s), foreground only.** Button «Измерить»; text «Быстро двигайте мышь кругами».
   Lycoris must **never keep a raw-input listener registered**: Microsoft found that many background raw-mouse
   subscribers caused game stutter with 1000 Hz mice and throttles/coalesces them since Windows 11 KB5028185
   (July 2023). Unregister on stop, timeout, page leave, window deactivation and app exit.
2. **Dedicated thread** with its own message loop so 8 kHz never touches the UI thread: `new Thread(...)` →
   `new HwndSource(new HwndSourceParameters("MonolithRawInput") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */,
   Width = 0, Height = 0, WindowStyle = 0 })`, `AddHook(WndProc)`, `Dispatcher.Run()`; shut down with
   `Dispatcher.InvokeShutdown()`.
3. **Don't clobber:** "only one window per raw input device class may be registered … within a process (the last
   call wins)" (Microsoft). Call `GetRegisteredRawInputDevices` first; if the process already has a mouse (0x01/0x02)
   registration, refuse the measurement instead of overriding it.
4. **Register:** `RegisterRawInputDevices([{ usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK (0x100),
   hwndTarget = hwnd }], 1, sizeof)`. No `RIDEV_NOLEGACY` (it would kill Lycoris's own mouse messages).
   **Unregister:** same entry with `dwFlags = RIDEV_REMOVE (0x1)` and `hwndTarget = NULL` (non-NULL makes it fail).
5. **Per message** (`WM_INPUT = 0x00FF`): `t = Stopwatch.GetTimestamp()` first; `GetRawInputData(lParam,
   RID_INPUT = 0x10000003, buf, ref size, sizeof(RAWINPUTHEADER))` into a preallocated buffer; keep only
   `header.dwType == RIM_TYPEMOUSE (0)`, `(usFlags & MOUSE_MOVE_ABSOLUTE 0x1) == 0`, `lLastX|lLastY != 0`.
   **Drop samples with `GET_RAWINPUT_CODE_WPARAM(wParam) == RIM_INPUTSINK (1)`** — input while Lycoris was not in the
   foreground, i.e. possibly throttled. Store `(t, hDevice, |dx|+|dy|)` in a preallocated ring buffer (no per-event
   allocations). Return without marking handled so `DefWindowProc` runs ("must call DefWindowProc so the system can
   perform cleanup" — WM_INPUT docs). Per-message `GetRawInputData` keeps a timestamp per report; `GetRawInputBuffer`
   (batch drain, 8-byte alignment rules on WOW64) is the fallback if the thread can't keep up, but then timestamps
   collapse per batch → use the count-based estimate only.
6. **Which device:** group by `hDevice`. Resolve each handle: `GetRawInputDeviceInfo(RIDI_DEVICENAME = 0x20000007)` →
   `\\?\HID#VID_046D&PID_C54D&MI_00#8&20b818ad&0&0000#{378de44c-…}` → instance ID (strip `\\?\` and `#{GUID}`, `#`→`\`)
   → walk parents with CfgMgr32 (`CM_Locate_DevNodeW`, `CM_Get_Parent`, `CM_Get_Device_IDW`). **Exclude virtual devices:**
   parent chain without a USB/BTHENUM/BTHLEDEVICE/I2C/ACPI bus node (here: `LGHUBDevice\…` for PID C231/C232).
   Exclude `hDevice == 0` (precision touchpad per Microsoft; SendInput-injected input observed with 0 as well). Show
   the device with the most samples as "ваша мышь", with its friendly name and VID/PID.
7. **Estimate (robust):**
   - Split into motion segments at gaps > 30 ms (mouse stopped; 125 Hz = 8 ms).
   - Count reports in 50 ms bins that lie fully inside segments → bin rate = count / 0.05 s. Require ≥ 20 bins (1 s of
     motion), else «Слишком мало движения».
   - **Rate = 90th percentile of bin rates.** Not the mean (slow parts of a circle send fewer reports: a mouse only
     reports when it has ≥1 count of motion), not the max (two queued messages delivered back-to-back inflate it).
   - Snap to {125, 250, 500, 1000, 2000, 4000, 8000} when within ±12 %; else show «≈ N Гц (нестабильно)».
   - Stability = share of bins within ±10 % of the snapped rate; jitter = p99/median inter-report interval inside segments.
   - **Saturation hint:** if most reports carry |dx|+|dy| ≤ 1, the sensor wasn't moving fast enough to fill every
     report: «Двигайте быстрее или временно поднимите DPI». Required speed ≈ rate / DPI inches per second — 8000 Hz at
     800 DPI needs ≥ 10 in/s (25 cm/s); 1000 Hz at 800 DPI needs ~3 cm/s.
   - If > 20 % of samples were dropped as RIM_INPUTSINK: «Окно Lycoris было неактивно — повторите».
8. **Logitech-specific caveats (this PC):**
   - LIGHTSPEED: the rate is set in G HUB (Performance → REPORT RATE) and stored on the mouse/receiver; the measurement
     includes radio-link quality. Gaps/jitter → receiver closer (extension adapter), away from USB 3 ports/devices
     (2.4 GHz interference — Razer's HyperPolling guidance). Here the receiver sits on `USB\ROOT_HUB30` (no hub) — fine;
     if the parent is an external hub, suggest a rear port.
   - Wired mode (charging cable): Logitech lists **Full Speed** USB for wired → max 1000 Hz (1 ms frames); 2K–8K only
     over the High-Speed receiver.
   - The G HUB virtual mouse is a second raw mouse — excluded by the parent check, not by VID (same VID 046D).
   - Low battery / power-saving modes on some Logitech mice change tracking behaviour (Logitech support); re-measure on
     a charged mouse.
9. **Keyboard polling (`keyboard-polling`, INFO-ONLY):** not measurable from user mode — a keyboard reports only on key
   state changes, so press timing says nothing reliable about the poll interval. Show the vendor's spec and the
   vendor app link.

## 6. Checking DPI (`mouse-dpi-measure`, INFO-ONLY)
- Same raw-input session; the user holds the left button at a start mark, moves the mouse in a straight line along a
  ruler (10 cm) or a bank card's long edge (85.60 mm, ISO/IEC 7810 ID-1), releases. Sum `lLastX` (and `lLastY`) from
  the chosen `hDevice` between `RI_MOUSE_LEFT_BUTTON_DOWN (0x0001)` and `_UP (0x0002)` in `usButtonFlags`.
- `DPI = sqrt((ΣdX)² + (ΣdY)²) / (distance_cm / 2.54)`; three runs, median; show «≈ 1600 DPI (±5 %)».
- Purpose: compute the pointer-speed compensation (§3.1), desktop travel (px per cm = DPI / 2.54 × multiplier) and eDPI.
- Works without admin; changes nothing.

## 7. Advice

#### `mouse-dpi-advice`: DPI мыши → **INFO-ONLY (Lycoris cannot set DPI)**
- DPI (CPI) is how many counts per inch the sensor reports; it is set in device firmware via G HUB (PRO X SUPERLIGHT 2:
  100–44 000). Windows then multiplies by pointer speed; a raw-input game multiplies by its own sensitivity:
  **eDPI = DPI × in-game sensitivity**; keep eDPI constant when changing DPI: `newSens = oldSens × oldDPI / newDPI`.
- **Sensible range: 800–1600 DPI** at pointer speed 10 on 1440p: 1600 DPI ≈ 630 px/cm → crossing the 2560-px X27U takes
  ~4 cm; 800 DPI ~8 cm; 400 DPI ~16 cm (too slow for the desktop at 1:1, and at high polling rates 400 DPI can't fill
  reports during slow movement).
- Above ~3200 DPI: fine for modern sensors, but the desktop cursor becomes twitchy at 1:1 and in-game values get tiny.
- Latency: higher DPI delivers the first count sooner at slow speeds; the effect is speed-dependent and small
  (RTINGS measures sensor latency at fixed speeds; Blur Busters discussions disagree) → don't advertise DPI as a
  latency fix.
- Context, not a target: CS2 pros — most common DPI 400, median eDPI 800 (csdb.gg, 1 007 players); Valorant pros —
  median eDPI ≈ 231 (ProSettings data via setup.gg).
- Page logic: with a measured DPI show «низкий / оптимальный / высокий для 1440p» and the compensation math; button
  «Открыть G HUB».

#### `mouse-polling-advice`: Частота опроса → **INFO-ONLY (set in G HUB)**
- **1000 Hz — recommended default:** 1 ms interval, every game handles it.
- **2000–4000 Hz — optional** on a strong CPU (9800X3D) with a 240 Hz monitor: smoother cursor sampling, ≤ 0.5 ms less
  delay; TechPowerUp found 4000 Hz "virtually on par" with 8000 Hz while lighter on the CPU.
- **8000 Hz — only after testing your games:** 0.125 ms interval; CPU cost reported from ~4 → 8 % (Hardware Canucks) up
  to +10–15 % while moving fast (Digital Citizen); Razer: some PCs aren't powerful enough and older engines handle it
  poorly — drop to 4000/2000 Hz; Unity developers reported frame drops at 2K–8K; wireless battery drains faster.
  Windows 11 (KB5028185) reduced the background-listener part of the cost.
- Lycoris measures (§5), explains, and links to G HUB → mouse → Производительность → частота опроса. It never changes
  the rate.

#### `vendor-app-handoff`: Настройки в приложении производителя → **INFO-ONLY**
- Detect vendor apps from Uninstall keys (`DisplayIcon` = `C:\Program Files\LGHUB\lghub.exe` here) and running
  processes (`lghub_agent`); map by VID: 046D → G HUB (gaming) / Logi Options+ (office), 1532 → Razer Synapse,
  054C → INZONE Hub. Start them with `UnelevatedShell.StartAsUserAsync` (never with Lycoris's admin token).
- Don't read or write vendor databases (`%LOCALAPPDATA%\LGHUB\settings.db`): undocumented, version-specific.

#### `dynamic-lighting`: Подсветка → **INFO-ONLY**
- Windows 11 Dynamic Lighting drives HID LampArray devices (here: the PRO keyboard and the LIGHTSPEED receiver slots;
  `logi_lamparray_service` running; `AmbientLightingEnabled` = 1). Microsoft: settings in Settings → Personalization →
  Dynamic Lighting, background controllers are ranked, and a higher-priority vendor app can override Windows' effects.
- Lycoris: show "управляет: Windows / G HUB" hint and link `ms-settings:personalization-lighting`. Don't write
  `HKCU\Software\Microsoft\Lighting` (undocumented). The `Windows.Devices.Lights.LampArray` WinRT API exists, but
  app-driven lighting is out of scope for an optimizer.

#### `scroll-direction`: Направление прокрутки → **INFO-ONLY**
- Windows 11 24H2+ has «Направление прокрутки» in Settings → Bluetooth & devices → Mouse. The registry value behind it
  (`ReverseMouseWheelDirection`, community-sourced; absent here) and the per-device `FlipFlopWheel` (Device Parameters,
  HKLM, needs re-plug) are undocumented → link `ms-settings:mousetouchpad` only.

**Windows defaults (for «Как в Windows»)**: pointer speed 10; EPP on (1/6/10 — but Lycoris recommends off and doesn't
touch it here); wheel 3/3; routing 2 (on); double-click 500; swap off; trails off; sonar off; vanish on; keyboard delay 1,
speed 31; Toggle/Sticky/Filter Keys off.

---

## 8. DO-NOT-INCLUDE (with reasons)

#### `markc-mouse-fix` → **DO-NOT-INCLUDE**
- Replaces `SmoothMouseXCurve/YCurve` so that *if* EPP is on, the curve is linear. Exists for old games (Quake/Half-Life
  era) that turn EPP on through the legacy API. The author: "If you don't know you need it, then you don't need it!"
  — with EPP off there is no curve to fix. Requires a different file per display scaling (100/125/150 %) and the
  6/11 pointer speed, so a DPI-scaling change silently breaks it. Undocumented binary format. Lycoris already offers
  EPP off. (gaming_tweaks.md agrees: leave the curves alone.)

#### `input-data-queue-size` (MouseDataQueueSize / KeyboardDataQueueSize) → **DO-NOT-INCLUDE**
- Microsoft: the number of input events the class driver **buffers**, default 100; increase only if the System log
  shows "the ring buffer that stores incoming mouse data has overflowed". A FIFO buffer is drained as fast as the
  reader reads it: a smaller buffer doesn't reduce latency, it only risks overflow = **dropped input** (lost key-ups =
  stuck keys); forum testers lowering it to 20 report "inconsistent" movement. HKLM, reboot. Read-only display of the
  value is fine (this PC: 100 / absent = default).

#### `usb-polling-overclock` (hidusbf and similar) → **DO-NOT-INCLUDE**
- Third-party filter/patched USB drivers that force a different interval. Its own README: patching versions require
  **disabling Memory Integrity** (HVCI is on here); removing the driver without unlinking can leave the mouse dead;
  some USB stacks unsupported. Driver installs are out of scope, conflict with anti-cheats, and modern gaming mice
  already offer 1000–8000 Hz natively (this one: 8000 Hz in G HUB).

#### `hid-power-management-off` → **DO-NOT-INCLUDE**
- "Disable *Allow the computer to turn off this device*" / `SelectiveSuspendEnabled`/`EnhancedPowerManagementEnabled`
  edits on mice and keyboards. Microsoft: "By default, USB selective suspend is disabled by Windows" for HID to avoid
  resume latency; only devices whose vendor opted in (OS descriptor / INF) use it, and they must resume "without any
  perceived delay". This PC: `SelectiveSuspendEnabled` = 0 on the receiver already → placebo. Writes are per-device
  HKLM under `Enum`, need admin and re-plug. Lycoris may **display** the state (read-only). The power-plan USB
  selective-suspend setting is a separate, existing item (gaming_tweaks.md §C).

#### `raw-input-buffer-registry` → **DO-NOT-INCLUDE**
- There is no Windows registry switch for a "raw input buffer". Valorant's «Raw Input Buffer» and Overwatch's
  «High Precision Mouse Input» are **in-game options** (the game calls `GetRawInputBuffer` / reads sub-frame input);
  Razer tells HyperPolling users to enable them in the game. Registry "raw input buffer" tweaks online are relabelled
  MouseDataQueueSize edits or invented value names → nothing to implement. Lycoris may mention the in-game options
  as advice for 4–8 kHz users.

#### `filterkeys-fast-repeat` → **DO-NOT-INCLUDE**
- Popular hack: turn FilterKeys on with tiny AutoRepeatDelay/AutoRepeatRate to beat the 31 (~30/s) limit. Through the
  documented API, with BounceKeys off, `iWaitMSec`, `iDelayMSec` and `iRepeatMSec` "must all be nonzero" (Microsoft) —
  i.e. a SlowKeys acceptance delay on **every** keystroke; registry variants with DelayBeforeAcceptance 0 rely on
  undocumented behaviour. It also turns an accessibility feature on behind the user's back.

#### `vendor-protocol-writes` → **DO-NOT-INCLUDE**
- Setting DPI / report rate / RGB by sending vendor packets (Logitech HID++ on the MI_02 vendor collections, Razer
  protocol) or editing G HUB's database. Proprietary, undocumented by Microsoft, firmware-version dependent, fights the
  running vendor app, and a bad write can corrupt onboard profiles — exactly the damage the prime directive forbids.

---

## 9. Detection checklist (read-only, for implementation)
- SPI_GET* for every row; `GetDoubleClickTime`; `GetSystemMetrics(SM_SWAPBUTTON 23, SM_CMOUSEBUTTONS 43,
  SM_MOUSEWHEELPRESENT 75, SM_MOUSEHORIZONTALWHEELPRESENT 91)`.
- HKCU (`Control Panel\Mouse`, `\Desktop`, `\Keyboard`, `\Accessibility\*`) for pending values (through `InteractiveUser`).
- `GetRawInputDeviceList` + `GetRawInputDeviceInfo(RIDI_DEVICENAME, RIDI_DEVICEINFO 0x2000000B)` for the device list
  (type, buttons, keyboard function keys) — needs no registration and no admin; parent walk (CfgMgr32) to flag virtual
  devices and hubs.
- `Get-PnpDevice`-equivalent via SetupAPI/WMI `Win32_PnPEntity` for friendly names; LampArray presence = child
  `USB\VID_…&LAMPARRAY\…` devnodes.
- Vendor apps: Uninstall keys (HKLM, WOW6432Node, HKCU), processes.

## Sources
- Microsoft, SystemParametersInfoW (actions, ranges, defaults, fWinIni): https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
- Microsoft, Taking Advantage of High-Definition Mouse Movement (WM_MOUSEMOVE ballistics vs WM_INPUT): https://learn.microsoft.com/en-us/windows/win32/dxtecharts/taking-advantage-of-high-dpi-mouse-movement
- Microsoft, STICKYKEYS: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-stickykeys
- Microsoft, FILTERKEYS (nonzero rule, 20 000 ms max): https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-filterkeys
- Microsoft, TOGGLEKEYS: https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-togglekeys
- Microsoft, RAWINPUTDEVICE (RIDEV_INPUTSINK/REMOVE): https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice
- Microsoft, RegisterRawInputDevices (one window per class per process): https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerrawinputdevices
- Microsoft, WM_INPUT (RIM_INPUT / RIM_INPUTSINK, DefWindowProc cleanup): https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-input
- Microsoft, GetRawInputBuffer: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getrawinputbuffer
- Microsoft, RAWINPUTHEADER (hDevice 0 for precision touchpad): https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputheader
- Microsoft, Using Raw Input: https://learn.microsoft.com/en-us/windows/win32/inputdev/using-raw-input
- Microsoft Windows Developer blog, 26 May 2023, "Reduced game stutter with high report rate mice": https://blogs.windows.com/windowsdeveloper/2023/05/26/delivering-delightful-performance-for-more-than-one-billion-users-worldwide/
- Windows Latest on KB5028185: https://www.windowslatest.com/2023/07/16/windows-11-july-2023-update-delivers-big-gaming-boost-with-reduced-stutter/
- Microsoft, Parameters\MouseDataQueueSize: https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2003/cc775532(v=ws.10)
- Microsoft, Selective Suspend for HID over USB devices: https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/selective-suspend-for-hid-over-usb-devices
- Microsoft Support, Control Dynamic Lighting devices in Windows: https://support.microsoft.com/windows/hardware/input-devices/control-dynamic-lighting-devices-in-windows
- Logitech, PRO X SUPERLIGHT 2 specification (8000 Hz, 100–44 000 DPI, receiver 046D:C54D, wired Full Speed): https://support.logi.com/hc/en-ca/articles/15235304069783-Specification-G-PRO-X-Superlight-2-Lightspeed-Gaming-Mouse
- Logitech, Inconsistent DPI when wireless report rate is changed and EPP is enabled: https://support.logi.com/hc/vi/articles/22766568765719
- Logitech, Inconsistent tracking between performance and endurance modes: https://support.logi.com/hc/en-ca/articles/360023361233
- Logitech, How to change the USB report rate (G HUB → Performance → REPORT RATE): https://support.logi.com/hc/en-au/articles/39579196659735-How-can-I-change-the-USB-report-rate
- Linux kernel patch adding 0xC54D (G Pro X Superlight 2 receiver): https://lkml.iu.edu/2511.1/00202.html
- Razer HyperPolling (CPU, older engines, in-game Raw Input Buffer, USB 3 interference, EPP off): https://www.razer.com/technology/razer-hyperpolling
- Pointer-speed multiplier table (Liquipedia "Mouse Settings", repost): https://www.yuuza.net/Posts/windows-pointer-speed
- MarkC mouse acceleration fix (author's notes): https://donewmouseaccel.blogspot.com/2010/03/markc-windows-7-mouse-acceleration-fix.html
- hidusbf README (Memory Integrity, removal warning): https://github.com/LordOfMice/hidusbf
- 8000 Hz CPU cost: Hardware Canucks https://hardwarecanucks.com/gaming-keyboards-mice/razer-viper-8k-review/ ; Digital Citizen https://www.digitalcitizen.life/razer-viper-8khz-review/ ; TechPowerUp https://www.techpowerup.com/review/razer-viper-8k/6.html
- Unity high-polling frame drops / RIDEV_NOLEGACY: https://discussions.unity.com/t/performance-degradation-while-using-mouse-with-high-polling-rate-any-known-engine-version-without-the-problem/1508487?page=2
- RTINGS sensor latency method: https://www.rtings.com/mouse/tests/control/sensor-latency
- Blur Busters, DPI vs latency discussion: https://forums.blurbusters.com/viewtopic.php?p=66263
- MouseDataQueueSize forum reports: https://forums.blurbusters.com/viewtopic.php?p=95690 , https://www.overclock.net/threads/mousedataqueuesize-registry-tweak-to-reduce-input-delay-fact-or-fiction.1794122/
- CS2 pro DPI/eDPI statistics: https://csdb.gg/data/pro-player-settings/
- Valorant pro eDPI (ProSettings data): https://www.setup.gg/game/valorant/best-sens/
- Windows 11 mouse scroll direction setting: https://www.windowslatest.com/2024/07/26/windows-11-24h2-lets-you-easily-adjust-the-mouse-scroll-wheel-direction/ ; registry (community): https://www.elevenforum.com/t/change-mouse-scrolling-direction-in-windows-11.21352/
