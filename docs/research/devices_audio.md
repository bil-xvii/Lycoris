# MONOLITH: audio devices (headsets, headphones, microphones, speakers, monitor HDMI/DP audio)

What MONOLITH can **read**, **recommend** and **change** for audio devices on Windows 10/11 through documented mechanisms, what it must leave to Windows' own UI or to the vendor app, and which popular "audio tweaks" are placebo or harmful.

## 0. Summary

**Headline findings**
- **Reading is easy and documented.** Every device fact MONOLITH needs can be read without admin rights: the endpoint list, the default device for each role, the shared-mode format, volume and mute, spatial sound state and the topology controls. The APIs are the MMDevice API, `IAudioEndpointVolume`, `IDeviceTopology` and WinRT `SpatialAudioDeviceConfiguration`.
  - The "Audio enhancements" state and the communications-ducking preference come from plain registry reads.
- **Writing is mostly not documented.** Windows' own Sound control panel (mmsys.cpl) and Settings change the default device, the format, exclusive mode, enhancements and endpoint visibility through the undocumented `IPolicyConfig` COM interface. A Microsoft engineer states that the effects (enhancements) store is restricted to the driver, the audio engine and the Sound control panel.
  - CLAUDE.md allows only documented, reversible changes. MONOLITH therefore **reads, recommends and deep-links to the exact Windows page**, and does not write these values itself.
- **Documented writes MONOLITH can own:**
  1. Microphone level and mute (`IAudioEndpointVolume`).
  2. Microphone boost and AGC, when the driver exposes them (`IDeviceTopology` → `IAudioVolumeLevel` / `IAudioAutoGainControl`).
  3. The per-user communications-ducking preference (HKCU registry value).
  4. ADVANCED: disabling the GPU's HDMI/DP audio function with `pnputil /disable-device`.
- **Exact deep links exist** for the "manual" part of the owner's request:
  - `ms-settings:sound-properties?endpointId=<IMMDevice id>` opens that device's own Settings page: format, audio enhancements, spatial sound, volume, and Allow/Don't allow. Documented in the ms-settings URI list.
  - `ms-settings:sound`, `ms-settings:sound-devices` and `ms-settings:apps-volume` (per-app output/input).
  - `control mmsys.cpl,,0|1|3` opens the Playback, Recording or Communications tab.
- **Vendor DSP/firmware settings are out of reach.** These include INZONE EQ, 360 Spatial Sound, DRC and firmware, and the Razer sample rate, mixer and lighting. MONOLITH detects the vendor app and launches it unelevated.
- **No audio setting improves FPS or input latency.** Shared-mode games already run on the Windows 10+ engine (~1.3 ms engine latency, 10 ms default buffer, set by the driver). Every "audio latency" registry tweak found online is placebo or harmful (§4).

**Classes** (binding, as in the other research files): SAFE-DEFAULT = applied by default; SAFE-OPTIONAL = offered, unchecked; ADVANCED = behind a warning; DO-NOT-INCLUDE = never implemented; **INFO-ONLY** = read, explain, recommend, and open the right Windows/vendor page. MONOLITH writes nothing for INFO-ONLY items.

| id | Title (RU UI) | Class | Recommended | MONOLITH writes via | Admin | Undo |
|---|---|---|---|---|---|---|
| `audio-default-devices` | Устройства по умолчанию | INFO-ONLY | One headset for all 3 render roles; the mic you speak into for all 3 capture roles | — (deep link) | — | — |
| `audio-shared-format` | Формат по умолчанию (частота, разрядность) | INFO-ONLY | 48 000 Hz; 24-bit if offered, 16-bit is fine | — (deep link) | — | — |
| `audio-enhancements` | Улучшения звука | INFO-ONLY | Default; Off when vendor DSP is used or the voice sounds muffled/processed | — (deep link) | — | — |
| `audio-spatial-sound` | Пространственный звук | INFO-ONLY | Off with vendor spatial (INZONE Hub) or in-game HRTF; Windows Sonic optional | — (deep link) | — | — |
| `audio-ducking` | Приглушение звука во время звонков | SAFE-OPTIONAL | Gaming with voice chat: "Do nothing" (3); everyday: default (80 %) | HKCU `UserDuckingPreference` | no* | restore DWORD / delete |
| `audio-exclusive-mode` | Монопольный режим | INFO-ONLY | Windows default (allowed + priority); turn off only to fix an app grabbing the device | — (mmsys.cpl) | — | — |
| `audio-mic-level` | Уровень микрофона | SAFE-OPTIONAL | Speech peaks −12…−6 dBFS, never above −1 dBFS (measured) | `IAudioEndpointVolume` | no* | restore scalar + mute |
| `audio-mic-boost-agc` | Усиление микрофона и АРУ | SAFE-OPTIONAL | Boost 0…+10 dB; AGC off for gaming/streaming | `IAudioVolumeLevel`, `IAudioAutoGainControl` | no* | restore dB + AGC |
| `audio-monitor-endpoint-hide` | Звук монитора: скрыть выход | INFO-ONLY | Optional decluttering via Windows "Don't allow" | — (deep link) | — | — |
| `audio-gpu-hdmi-audio-disable` | Отключить звук видеокарты (HDMI/DP) | ADVANCED | Only if no monitor/TV audio is ever used | `pnputil /disable-device` | yes | `pnputil /enable-device` |
| `audio-vendor-apps` | Приложение производителя | INFO-ONLY | Use INZONE Hub / Razer Synapse for DSP and firmware | — (launch app) | — | — |
| `audio-mono-check` | Монофонический звук | INFO-ONLY | Off for games (unless needed for hearing) | — (deep link) | — | — |
| `audio-mic-privacy-check` | Доступ к микрофону | INFO-ONLY | Allow for desktop apps if voice chat is used | — (deep link) | — | — |
| `audio-ipolicyconfig-writes` | Запись через IPolicyConfig | DO-NOT-INCLUDE | — | — | — | — |
| `audio-fxproperties-registry-edits` | Правка FxProperties / удаление APO | DO-NOT-INCLUDE | — | — | — | — |
| `mmcss-audio-tasks` | MMCSS Audio / Pro Audio | DO-NOT-INCLUDE | — | — | — | — |
| `audio-latency-tweaks` | «Снижение задержки звука» | DO-NOT-INCLUDE | — | — | — | — |
| `audio-services-disable` | Отключение служб звука | DO-NOT-INCLUDE | — | — | — | — |
| `audiodg-priority-affinity` | Приоритет / ядра audiodg.exe | DO-NOT-INCLUDE | — | — | — | — |
| `audio-high-sample-rate` | 96/192 кГц «для позиционирования» | DO-NOT-INCLUDE | — | — | — | — |
| `audio-ghost-endpoint-cleanup` | Чистка «призрачных» аудиоустройств | DO-NOT-INCLUDE | — | — | — | — |

\* The API itself needs no elevation. MONOLITH still writes only when elevated, because the undo journal lives in the admin-only `%ProgramData%\MONOLITH` (CLAUDE.md rule 7). Without admin rights the page is read-only.

---

## 1. Dev machine snapshot (read-only, 2026-10-27, build 26300.9711)

All values below were read with Get-*/registry reads and a COM probe that calls only `Get*`, `Enum*` and `Query*` methods.

**Active endpoints** (`EnumAudioEndpoints(eAll, DEVICE_STATE_ACTIVE)`)

| Flow | Endpoint (FriendlyName) | Endpoint id | Hardware | Format (`PKEY_AudioEngine_DeviceFormat`) | Volume | HW support |
|---|---|---|---|---|---|---|
| Render | Наушники (INZONE E9) | `{0.0.0.00000000}.{13115c35-2308-4ab1-8fa0-2a1a69dff0a8}` | USB VID_054C PID_0F80, `wdma_usb.inf` (USB Audio 1.0 class driver) | 48 kHz, 16-bit PCM, stereo. OEM default: 48 kHz 32-bit float | 0.30 (−18 dB; range −74…0) | 0x3 (volume + mute in HW) |
| Render | X27U W3 (NVIDIA High Definition Audio) | `{0.0.0.00000000}.{264c7e39-05c5-49e2-9ece-8b68055b6bad}` | HDAUDIO VEN_10DE DEV_00AB (RTX 5070 Ti HDA function) | 48 kHz 16-bit stereo | 1.00 | 0x2 (mute only) |
| Render | Odyssey G5 (NVIDIA High Definition Audio) | `{0.0.0.00000000}.{285d25ab-8011-44b3-b184-736e54321c5e}` | same NVIDIA HDA function | 48 kHz 16-bit stereo | 1.00 | 0x2 |
| Capture | Микрофон (Razer Seiren V3 Mini) | `{0.0.1.00000000}.{8baa6d9f-01c8-4bfe-b9e5-758c42eb3681}` | USB VID_1532 PID_056A, `wdma_usb.inf` | 48 kHz, 24-bit PCM, mono. OEM default: 48 kHz 32-bit float | 0.75 (+5.6 dB; range −16…+15 dB) | 0x3 |

**Other endpoints**
- 21 render and 3 capture endpoints are `DEVICE_STATE_NOTPRESENT` (0x4). They are invisible in the UI already. They come from:
  - unplugged Realtek ALC897 jacks (generic HDA driver);
  - the AMD iGPU HDMI function, with nothing connected;
  - old NVIDIA HDMI ports.
- `NVIDIA Virtual Audio Device (Wave Extensible)` (ROOT\UNNAMED_DEVICE\0000) is present but exposes no endpoint. It belongs to the NVIDIA App capture/streaming stack.

**Defaults:** render eConsole, eMultimedia and eCommunications are all INZONE E9; capture is all three roles on the Razer Seiren V3 Mini. Correct, nothing to recommend.

**Spatial sound** (WinRT `SpatialAudioDeviceConfiguration`):
- INZONE E9 and X27U: `IsSpatialAudioSupported=True`, Windows Sonic supported, `DefaultSpatialAudioFormat = ActiveSpatialAudioFormat = {00000000-…}`, so **Off**.
- Offered formats listed in the endpoint store: Windows Sonic, Dolby Atmos for Headphones/Speakers, DTS Headphone:X, DTS Virtual:X.

**Effects (APOs)**
- INZONE and both NVIDIA endpoints carry "Microsoft Audio Home Theater Effects" (SFX/MFX).
- The Razer mic carries the **Voice Clarity** effect pack: FxProperties `{9e6136e0-57ab-4949-b57a-3627be142855},100` → `SWD\DRIVERENUM\{96bedf2c-…}#VocaEffectPack…`, PnP device "Voice Clarity", class AudioProcessingObject.

**Vendor apps**

| App | Version | Executable | Autostart |
|---|---|---|---|
| INZONE Hub | 1.0.20.0 | `C:\Program Files\Sony\INZONE Hub\INZONEHub.exe` | Startup-folder `.lnk` with `/startup` |
| Razer Synapse | 4.0.503 | `C:\Program Files\Razer\RazerAppEngine\RazerAppEngine.exe --url-params=apps=synapse` | — |

The Razer "Seiren V3 Mini – LWI Wizard" SoftwareComponent (Windows Update–delivered Synapse installer trigger) is present.

**Other per-user state:** mic privacy is `Allow` (user, NonPackaged, machine). Mono audio (`AccessibilityMonoMixState`) is absent, so stereo.

**MMCSS tasks (defaults, untouched):**
- Audio: Priority 6, Medium, Clock Rate 10000, GPU 8, Background Only True.
- Pro Audio: Priority 1, High.
- audiodg.exe is running; its priority class can't be read, because it is a protected service process.

**State that changed during this session** (registry key last-write times; none of this session's commands write):

| Time | Change |
|---|---|
| 21:07:50 | INZONE/Razer `Properties` got `{b3f8fa53-0004-438e-9003-51a46e139bfc},3 = 0` and `,4 = 0` (exclusive-mode checkboxes cleared) |
| 21:07:56 / 21:08:09 | INZONE / Razer `FxProperties` got `{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5 = 1` (Audio enhancements **Off**) |
| 21:08:15 | HKCU `UserDuckingPreference = 3` ("Do nothing") |

What this tells us:
- The sequence matches someone clicking through mmsys.cpl.
- The documented `IMMDevice::OpenPropertyStore` view showed the new `,3/,4` values **before** the registry flush. AudioEndpointBuilder caches the store and writes the registry lazily.
- That view **never** shows `Disable_SysFx`. The FX store is not merged into the endpoint property store. The detection rules in §3 follow from these observations.

---

## 2. Implementation notes (shared by all items)

**COM interop (.NET 10, no new packages)**
- Use `[ComImport]` interfaces with **`[PreserveSig]` on every method** (or `[GeneratedComInterface]`).
  - Without PreserveSig, the CLR treats an `int` return as an extra `[out, retval]` parameter.
  - On x64 the call then "works" by accident, and HRESULT checks read garbage. This was observed while probing.
- Interfaces and IDs:
  - `MMDeviceEnumerator` CLSID `BCDE0395-E52F-467C-8E3D-C4579291692E`; `IMMDeviceEnumerator` `A95664D2-…`; `IMMDevice` `D666063F-…`; `IPropertyStore` `886D8EEB-…`.
  - `IAudioEndpointVolume` `5CDF2C82-841E-4546-9722-0CF74078229A`; `IAudioMeterInformation` `C02216F6-8C67-4B5B-9D00-D008E73E0064`.
  - `IDeviceTopology` `2A07407E-…`; `IPart` `AE2DE0E4-…`; `IAudioVolumeLevel` `7FB7B48F-…`; `IAudioAutoGainControl` `85401FD4-…`.
- Run Core Audio calls on a worker (MTA) thread (`Task.Run`). The enumerator is free-threaded.
- Register `IMMNotificationClient` (`OnDefaultDeviceChanged`, `OnDeviceStateChanged`, `OnPropertyValueChanged`) so the page refreshes live. Post to the UI with `Ui.Post`.
- Pass a fixed MONOLITH `eventContext` GUID to every `Set*` call. MONOLITH's own `IAudioEndpointVolumeCallback` can then ignore its own changes, and drift detection can tell user changes from MONOLITH's.

**Endpoint id forms**
- `IMMDevice::GetId()` → `{0.0.0.00000000}.{guid}` (render) / `{0.0.1.00000000}.{guid}` (capture). Use it for `ms-settings:sound-properties?endpointId=` and as the journal key.
- WinRT / `Windows.Devices.Enumeration` id → `\\?\SWD#MMDEVAPI#<endpoint id>#{e6327cad-dcec-4949-ae8a-991e976a79d2}` (render) or `#{2eef81be-33fa-4800-9670-1cd474972c3f}` (capture). Use this one for `SpatialAudioDeviceConfiguration.GetForDeviceId`.
- Endpoint GUIDs change when the driver is reinstalled. Treat a missing endpoint at undo time as "nothing to restore", not as an error.

**Reading the registry (allowed, read-only)**
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{Render|Capture}\{guid}`:
  - `DeviceState` (1 active, 2 disabled, 4 not present, 8 unplugged).
  - `Properties` (endpoint store).
  - `FxProperties` (effects store).
- The ACL grants Administrators `SetValue`, but **MONOLITH never writes here** (§4).

**Elevated process vs. interactive user**
- HKCU values (ducking, mono) must be written to the interactive user's hive `HKU\<SID>` (same rule as gaming_tweaks.md §0).
- Core Audio COM calls made from an elevated process under a *different* admin account see that account's view. In that case perform them via an unelevated helper, or show read-only.

**Launching Windows pages and vendor apps**
- Use `UnelevatedShell.StartAsUserAsync` for `ms-settings:` URIs and `control.exe mmsys.cpl,,N`, and for vendor apps (CLAUDE.md rule 8).
- `mmsys.cpl` tab indices: 0 Playback, 1 Recording, 2 Sounds, 3 Communications.

**Never restart AudioSrv / AudioEndpointBuilder.** Both are in `ServiceControl.Protected`. Settings that would need a restart are applied at sign-in/reboot, or by Windows' own UI.

**New ChangeSession primitives needed** (not registry, so they need their own journal record types and validation in `ChangeJournal`):
- `SetEndpointVolume(endpointId, scalar?, mute?)` — record `{endpointId, prevScalar (exact float bits), prevMute}`.
- `SetTopologyLevel(endpointId, partGlobalId, perChannelDb[] | agc bool)` — record previous values.
- Validate `endpointId` against `^\{0\.0\.[01]\.00000000\}\.\{[0-9a-fA-F-]{36}\}$` before acting on a journal record (the journal is untrusted input).

**Placement (minimal-UI rule)**
- Windows settings implemented as tweaks live on Optimize only. `audio-ducking` is a RegistryTweak there.
- A device page/tab can show the audio cards: state, recommendation and deep-link buttons, plus a mic-level wizard. It links into Optimize with `Ui.RequestFocus("optimize", "audio-ducking")` instead of duplicating the toggle.

---

## 3. Settings catalog

### `audio-default-devices`: Устройства по умолчанию (воспроизведение и запись) → **INFO-ONLY**
- **What it is:**
  - Windows keeps one default render and one default capture endpoint per role: `eConsole` (games, system sounds), `eMultimedia` (music, movies) and `eCommunications` (chat/VoIP).
  - Apps that follow "Default" open the eConsole/eMultimedia device. Voice apps that follow "Default communication device" open eCommunications.
- **Read (documented):**
  - `IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender|eCapture, eConsole|eMultimedia|eCommunications)` → `GetId()`.
  - `PKEY_Device_FriendlyName` ({a45c254e-df1c-4efd-8020-67d146a850e0},14) and `PKEY_AudioEndpoint_FormFactor` ({1da5d803-…},0: 1 Speakers, 2 LineLevel, 3 Headphones, 4 Microphone, 5 Headset, 9 DigitalAudioDisplayDevice) through `OpenPropertyStore(STGM_READ)`.
  - Live changes: `IMMNotificationClient::OnDefaultDeviceChanged`.
- **Write:** none by MONOLITH. The only way is `IPolicyConfig::SetDefaultEndpoint`, which is DO-NOT-INCLUDE (§4).
- **Manual:**
  - `ms-settings:sound` (output/input pickers).
  - `control mmsys.cpl,,0` / `,,1` (right-click → "Set as Default Device" / "Set as Default Communication Device").
  - `ms-settings:apps-volume` (per-app device). Windows 11 Quick Settings also switches output in one click.
- **Recommendation logic** (flag, don't force):
  - **All three render roles on the same headset/headphones** when one is active. A split comm role is the classic reason why "Discord plays from the monitor".
  - **All three capture roles on the mic the user speaks into.**
  - **Warn when** the default render is a `DigitalAudioDisplayDevice` (monitor HDMI/DP) while a headset/headphones endpoint is active. It may be intentional, so ask.
  - **Warn when** a Bluetooth hands-free ("Headset", BTHHFENUM) endpoint is the default console device. Opening its mic drops audio to narrow-band hands-free quality.
  - Gaming and everyday rules are the same.
- **Range:** any active endpoint per role.
- **Risk:** none (read-only).
- **This PC:** correct (INZONE E9 ×3, Razer ×3).
- **Evidence:** Microsoft "Device Roles" (role table); `GetDefaultAudioEndpoint` reference; ms-settings URI list (Sound section). The fallback ranking Windows uses when the default disappears is in `HKLM\…\MMDevices\DefaultDeviceHeuristics` (read here: Headphones/Headset 11, Speakers 10, LineLevel 9, DigitalAudioDisplayDevice 8 for Role_0). It is undocumented, so only explain it, never write it.

### `audio-shared-format`: Формат по умолчанию (частота дискретизации и разрядность) → **INFO-ONLY**
- **What it is:**
  - The PCM format the shared-mode engine uses toward the device (mmsys.cpl → Properties → Advanced → Default Format, or Settings → device → Format).
  - The engine mixes in 32-bit float and converts to this format. App streams at another rate are resampled.
- **Read (documented):**
  - `PKEY_AudioEngine_DeviceFormat` ({f19f064d-082c-4e27-bc73-6882a1bb8e4c},0, VT_BLOB = WAVEFORMATEX/EXTENSIBLE) via `OpenPropertyStore(STGM_READ)`.
  - The OEM default is in `{e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04},0`; it is undocumented, so display it only.
  - Candidate formats the device accepts: `IAudioClient::IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, fmt)` for 44.1/48/96 kHz × 16/24 bit. This opens no stream.
- **Write:** only `IPolicyConfig::SetDeviceFormat` (DO-NOT-INCLUDE). Microsoft documents the format as user-selected in mmsys.cpl.
- **Manual:** `ms-settings:sound-properties?endpointId=<id>` → Format, or mmsys.cpl Advanced tab.
- **Recommendation:**
  - **48 000 Hz** for headsets, headphones and microphones. Game engines and voice chat run at 48 kHz (Discord requires Opus at 48 kHz), so 48 kHz avoids one resampling step.
  - **Bit depth: no action.** 16-bit (96 dB dynamic range) is transparent for listening; choose 24-bit if offered for a little headroom.
  - Do **not** pick 96/192 kHz for gaming (see `audio-high-sample-rate`).
  - Gaming and everyday rules are the same.
- **Range:** whatever the device lists (commonly 44.1/48/96/192 kHz × 16/24/32 bit).
- **Risk:** none (read-only).
- **This PC:** every endpoint is at 48 kHz, so nothing to suggest. INZONE is 16-bit, which is fine (hardware volume, hw=0x3); its OEM default is 32-bit float.
- **Evidence:** Microsoft "Device Formats" (float mixing; user selects in mmsys.cpl); `PKEY_AudioEngine_DeviceFormat` reference; Discord voice docs (Opus, stereo, 48 kHz); Xiph "24/192 Music Downloads… make no sense" (no audible benefit; ultrasonics can cause audible intermodulation).

### `audio-enhancements`: Улучшения звука (эффекты драйвера и Windows) → **INFO-ONLY**
- **What it is:**
  - System-effect APOs (SFX/MFX/EFX) in the shared-mode path: Microsoft Home Theater Effects (bass boost, virtual surround, loudness equalization, room correction), vendor effects, and on 24H2+ Microsoft **Voice Clarity** for microphones.
  - Windows 11 Settings shows "Audio enhancements: Device Default Effects / Off". mmsys.cpl shows the "Enable audio enhancements" checkbox.
- **Read:**
  - Registry `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{Render|Capture}\{guid}\FxProperties`, value `{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5` (= `PKEY_AudioEndpoint_Disable_SysFx`). REG_DWORD **1 = Off**; absent or 0 = On (Device Default Effects).
  - Installed APOs: `{b725f130-47ef-101a-a5f1-02608c9eebac},10` (friendly name), `{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5/6/7` (SFX/MFX/EFX CLSIDs), `{9e6136e0-57ab-4949-b57a-3627be142855},100` (effects pack, for example Voice Clarity).
  - **The documented `IMMDevice::OpenPropertyStore` does not return this value. It reported VT_EMPTY while FxProperties held 1 on this PC**, so the registry read is required.
- **Write:** **none by MONOLITH.**
  - Microsoft staff on Q&A: "effects property store" access is restricted to the driver, audio engine and Sound control panel, so there is no supported API.
  - The workarounds are `IPolicyConfig::SetPropertyValue(bFxStore=TRUE)` or writing the registry directly. They are reported unreliable on Windows 11: Settings deletes the value for "Device Default Effects", and the workaround cannot recreate it.
  - Both are DO-NOT-INCLUDE.
- **Manual:** `ms-settings:sound-properties?endpointId=<id>` → Audio enhancements, or mmsys.cpl → Properties → Enhancements/Advanced.
- **Recommendation:**
  - *Gaming headset with vendor DSP (INZONE Hub EQ/spatial):* **Off**, to avoid stacking Windows effects on vendor processing.
  - *Microphone:* **Off** if the voice sounds muffled, pumping or "underwater" in Discord. Discord's own noise suppression on top of Voice Clarity on top of vendor processing means triple processing.
  - *Everyday speakers/laptops:* leave the default. OEM speaker tuning or protection may live there.
  - There is no FPS or latency benefit either way.
- **Range:** On (Device Default Effects) / Off; effect choice in the vendor or Windows UI.
- **Risk:** none (read-only). Turning effects off on laptop speakers can remove OEM tuning, so the deep link carries that warning.
- **This PC:** INZONE Off, Razer Off (since 21:07–21:08 today); monitors On (irrelevant).
- **Evidence:** `PKEY_AudioEndpoint_Disable_SysFx` reference; Microsoft Q&A "how to control enable audio enhancements with code" (MS staff answer + Windows 11 behaviour); dechamps/APO notes (FxProperties layout, consumed by audiosrv, overwritten on driver reinstall); APO architecture doc; Voice Clarity press coverage (TechRadar, Pureinfotech).

### `audio-spatial-sound`: Пространственный звук (Windows Sonic, Dolby Atmos, DTS) → **INFO-ONLY**
- **What it is:** the Windows spatial renderer per endpoint. It virtualises 7.1 or object audio to binaural for headphones (Windows Sonic), or uses a licensed Dolby/DTS renderer.
- **Read (documented WinRT, 1809+):**
  - `Windows.Media.Audio.SpatialAudioDeviceConfiguration.GetForDeviceId(interfaceId)` → `IsSpatialAudioSupported`, `DefaultSpatialAudioFormat` (user choice), `ActiveSpatialAudioFormat`.
  - `IsSpatialAudioFormatSupported(SpatialAudioFormatSubtype.WindowsSonic / DolbyAtmosForHeadphones / …)`.
  - `ConfigurationChanged` event.
  - `Guid.Empty` (string `{00000000-…}`) = Off.
  - Available through the existing `net10.0-windows10.0.19041.0` TFM; no NuGet.
- **Write:** `SetDefaultSpatialAudioFormatAsync` is documented, but "the caller must be the app that owns this spatial audio format". MONOLITH owns none, so it **cannot** set it. There is no other documented path.
- **Manual:** `ms-settings:sound-properties?endpointId=<id>` → Spatial sound; the taskbar volume menu; Quick Settings.
- **Recommendation:**
  - **Off** when the headset's own spatial engine is in use (INZONE Hub "360 Spatial Sound for Gaming" on the E9's USB box). Two virtualisers in series apply HRTF twice; this is our inference, as no vendor states it.
  - **Off** for competitive games that ship their own HRTF.
  - *Optional:* Windows Sonic for plain stereo headphones in games and films that output 7.1/objects without their own HRTF. This is a preference, not a performance setting.
  - Spatial sound is greyed out while mono audio is on.
- **Range:** Off / Windows Sonic / installed Dolby/DTS formats.
- **Risk:** none (read-only).
- **This PC:** Off on all endpoints; Windows Sonic is available.
- **Evidence:** `SpatialAudioDeviceConfiguration` reference (owner-only setter); Microsoft Support "How to turn on spatial sound in Windows"; Sony INZONE Hub setup article (spatial/EQ/DRC configured in INZONE Hub, effective only through the USB connection).

### `audio-ducking`: Приглушение звука во время звонков (связь) → **SAFE-OPTIONAL**
- **What it is:**
  - When a communications stream opens on the default communications device, Windows attenuates all other streams: 80 % by default, or mute, or 50 %, or nothing.
  - The streams are restored about 2 s after the call ends.
  - Microsoft documents the feature and the Communications tab choices (default 80 %).
- **Read/Write:**
  - `HKCU\Software\Microsoft\Multimedia\Audio`, `UserDuckingPreference` REG_DWORD: **0** mute all other sounds; **1** reduce by 80 % (default when absent); **2** reduce by 50 %; **3** do nothing.
  - Write via `ChangeSession.SetRegistry` into the interactive user's hive. Implement as a `RegistryTweak` on Optimize. The value name appears in a Microsoft (archived) blog; the 0–3 mapping follows the dialog's order (community-documented).
- **Undo:** restore the exact previous DWORD, or delete the value if it was absent (absent→delete).
- **Applies:**
  - Changing it in the Communications tab takes effect at once.
  - A registry-only change is picked up at the next sign-in or reboot. Guides that suggest `net stop audiosrv` are not an option: the service is protected.
  - Offer the deep link `control mmsys.cpl,,3` as the "apply now" path.
- **Recommendation:**
  - *Gaming with voice chat:* **3 (Do nothing).** Losing 80 % of game audio whenever someone speaks hurts positional awareness. Discord has its own optional attenuation setting.
  - *Everyday/office:* default (absent = 80 %).
- **Range:** 0–3.
- **Admin:** not for HKCU. MONOLITH writes only when elevated (journal).
- **Risk:** minimal; worst case, the ducking behaviour stays as before. One Microsoft Q&A report on LTSC 2019 says the GUI choice did not survive a reboot.
- **This PC:** 3 (set at 21:08:15 today).
- **Evidence:** Microsoft "Default Ducking Experience" (stream-attenuation); Microsoft archived TechNet blog (value name, HKCU path, 2 s release, default communication device trigger); WiseChecker (0–3 mapping); Microsoft Q&A thread 286017 (persistence caveat).

### `audio-exclusive-mode`: Монопольный режим (эксклюзивный доступ приложений) → **INFO-ONLY**
- **What it is:**
  - Two checkboxes per device (mmsys.cpl → Properties → Advanced): "Allow applications to take exclusive control of this device" and "Give exclusive mode applications priority".
  - In exclusive mode one app owns the endpoint; no other app can render or capture on it until the app releases it (Microsoft low-latency doc).
- **Read:**
  - `IMMDevice::OpenPropertyStore(STGM_READ)` → `{b3f8fa53-0004-438e-9003-51a46e139bfc},3` (allow) and `,4` (priority). VT_UI4 **0 = off**; VT_EMPTY/absent = Windows default (on).
  - These PKEYs are not in the SDK headers. The meaning is consistent with the Microsoft Tech Community script that sets both to 0 to disable exclusive mode, and with this PC, where both appeared as 0 when the owner's mmsys.cpl changes were flushed.
  - Show the state with an "unofficial reading" note.
- **Write:** `IPolicyConfig::SetShareMode` or a direct registry write. Both are DO-NOT-INCLUDE: undocumented; AEB caches the store; reported reverted by Windows updates; needs ACL changes.
- **Manual:** `control mmsys.cpl,,0` (playback) / `,,1` (recording) → device → Properties → Advanced.
- **Recommendation:**
  - **Leave the Windows default** (both on). Shared-mode games gain nothing from changing it.
  - Turn "Allow exclusive control" **off** only to fix a conflict, for example "the mic or headset goes silent in Discord when a DAW, game or player starts".
  - Keep it **on** for hi-fi players that use WASAPI exclusive for bit-perfect output.
- **Range:** on/off × 2.
- **Risk:** none (read-only).
- **This PC:** both off on INZONE and Razer (owner's choice, fine for a voice-chat setup); HDMI endpoints at default.
- **Evidence:** Microsoft "Low Latency Audio" (exclusive-mode semantics); Microsoft Tech Community thread "Disable exclusive mode for all audio devices via PS" (values, ACL problem, "often reverted by Windows updates"); `OpenPropertyStore` reference (store is read-only for non-admin clients).

### `audio-mic-level`: Уровень микрофона (громкость записи) → **SAFE-OPTIONAL**
- **What it is:** the endpoint volume of a capture device. On USB mics with hw=0x3 (this Razer: −16…+15 dB) it is the device's own gain, so it is the real input gain, not a software multiplier.
- **Read (documented):**
  - `IMMDevice::Activate(IID_IAudioEndpointVolume)` → `GetMasterVolumeLevelScalar`, `GetMasterVolumeLevel` (dB), `GetVolumeRange`, `GetVolumeStepInfo`, `GetMute`, `QueryHardwareSupport`, `GetChannelCount`.
- **Measure (documented, temporary, no persistent change):**
  - Optional button «Проверить микрофон»: open a shared-mode capture stream (`IAudioClient::Initialize(AUDCLNT_SHAREMODE_SHARED…)` + `Start`) for about 5 s while the user reads a test sentence.
  - Compute peak and RMS from the `IAudioCaptureClient` buffers, then `Stop`/`Release`.
  - Don't rely on `IAudioMeterInformation` alone. On a device without a hardware meter it reads 0 without an active stream, and for capture it measures **before** endpoint attenuation.
  - The Windows mic-in-use indicator appears during the test, and the test requires mic privacy access (`audio-mic-privacy-check`).
- **Write (documented):**
  - `SetMasterVolumeLevelScalar(level, &MonolithCtx)` / `SetMute(false, &MonolithCtx)` through a new `ChangeSession.SetEndpointVolume` primitive (§2).
  - Suggest the level that puts measured speech peaks at **−12…−6 dBFS** with no sample above −1 dBFS, then re-measure.
  - Output (render) volume gets a manual slider only. There is no recommendation (hearing safety; MONOLITH must not raise output volume by itself).
- **Undo:** restore the exact previous scalar (store the float bits) and mute state. If the endpoint no longer exists, skip.
- **Recommendation:** gaming and everyday alike: speech peaks −12…−6 dBFS; no clipping (OBS treats > −0.5 dBFS as clipping); prefer lowering gain over noise suppression.
- **Range:** 0.0–1.0 scalar (device dB range shown alongside).
- **Admin:** no (API). MONOLITH policy: elevated only.
- **Risk:** low. Too high clips, and the change is immediately audible to call partners. Discord/Teams "automatically adjust" and vendor apps can override it (Razer Insider thread: Synapse auto-lowering Seiren gain). Detect and explain, don't fight it.
- **This PC:** Razer 0.75 (+5.6 dB), not muted. A measurement is needed before suggesting anything.
- **Evidence:** `IAudioEndpointVolume` reference; Peak Meters doc (pre-attenuation, needs stream); OBS KB "Audio Mixer Technical Details" (clip indicator > −0.5 dBFS); Saramonic level guide (peaks around −12…−6 dBFS); Razer Insider thread.

### `audio-mic-boost-agc`: Усиление микрофона (Boost) и автоматическая регулировка (АРУ) → **SAFE-OPTIONAL (shown only when the driver exposes it)**
- **What it is:** extra pre-amp stages ("Microphone Boost", typically +10/+20/+30 dB on HD Audio jacks) and AGC. They appear as sliders on mmsys.cpl → Recording → Properties → Levels, and on some drivers' Custom tab.
- **Read/Write (documented Device Topology API):**
  1. `IMMDevice::Activate(IID_IDeviceTopology)` → `GetConnector(0)` → `GetConnectedTo` → QI `IPart`.
  2. For capture, walk `EnumPartsIncoming` recursively, the same signal path mmsys "Levels" shows.
  3. For each part, enumerate `GetControlInterface(i)`. Where the IID is `IAudioVolumeLevel`: `Activate` → `GetLevelRange(ch)` / `GetLevel(ch)` / `SetLevel(ch, dB, &ctx)`. Where it is `IAudioAutoGainControl`: `GetEnabled` / `SetEnabled(bool, &ctx)`.
  4. Show each extra volume part under its driver-provided name (`IPart::GetName`, localized). Skip the part already controlled by `IAudioEndpointVolume`.
- **Undo:** `ChangeSession.SetTopologyLevel` records `{endpointId, IPart::GetGlobalId, previous dB per channel | previous AGC}` and restores exactly.
- **Recommendation:**
  - Boost **0 dB, at most +10 dB**. Raise the mic level first, because boost lifts the noise floor with the voice.
  - **AGC off** for gaming and streaming (pumping, background noise swelling between phrases); on only for far-field laptop/conference use.
- **Range:** the driver's range and steps (from `GetLevelRange`); AGC on/off.
- **Admin:** no (API). MONOLITH policy: elevated only.
- **Risk:** low; too much boost causes hiss or clipping.
- **This PC:** the Razer Seiren V3 Mini topology exposes only the connector (with jack-description interfaces) — **no boost and no AGC part**. The section is hidden. USB class mics rarely have them; Realtek HDA mic jacks usually do.
- **Evidence:** Microsoft Device Topologies / `IAudioVolumeLevel` / `IAudioAutoGainControl` references; probe output on this PC.

### `audio-monitor-endpoint-hide`: Звук через монитор — скрыть неиспользуемый выход → **INFO-ONLY**
- **What it is:** each monitor with speakers or a headphone jack (or a TV) on HDMI/DP adds an active render endpoint (form factor 9 `DigitalAudioDisplayDevice`). With a USB headset unplugged, Windows can only fall back to such an endpoint.
- **Read:** `EnumAudioEndpoints(eRender, ACTIVE)` + FormFactor 9 + whether the endpoint is the default for any role.
- **Write:** none by MONOLITH. Per-endpoint disable is `IPolicyConfig::SetEndpointVisibility` (DO-NOT-INCLUDE).
- **Manual:** `ms-settings:sound-properties?endpointId=<id>` → "Audio: Don't allow" (re-enable with "Allow"), or mmsys.cpl → right-click → Disable / Show disabled devices.
- **Recommendation:**
  - Optional decluttering only. Idle endpoints cost nothing, so there is **no performance gain**.
  - Suggest it only when the endpoint is not the default for any role.
  - It is the per-monitor alternative to `audio-gpu-hdmi-audio-disable`.
- **Range:** Allow / Don't allow per endpoint.
- **Risk:** none (read-only); the user can re-allow anytime.
- **This PC:** X27U W3 and Odyssey G5 are active and not default. A candidate if the owner never uses monitor audio.
- **Evidence:** ms-settings URI list (`sound-properties?endpointId`); DefaultDeviceHeuristics read on this PC; EarTrumpet source (`SetEndpointVisibility` is IPolicyConfig).

### `audio-gpu-hdmi-audio-disable`: Отключить звуковое устройство видеокарты (HDMI/DP) → **ADVANCED**
- **What it is:** disables the GPU's HD Audio *function* device, for example `HDAUDIO\FUNC_01&VEN_10DE&DEV_00AB&SUBSYS_10DE0000&REV_1001\5&2BB51DC2&0&0001`. This removes **all** HDMI/DP audio endpoints of that GPU at once, for both monitors here.
- **Read:**
  - `Get-PnpDevice` / `Win32_PnPEntity` (class MEDIA) whose instance id starts with `HDAUDIO\FUNC_01&VEN_10DE` (NVIDIA), `VEN_1002` (AMD) or `VEN_8086 … DEV_28xx` (Intel display audio).
  - Status/`ConfigManagerErrorCode` (22 = disabled).
  - Its endpoints from the MMDevice store, via `{b3f8fa53-0004-438e-9003-51a46e139bfc},2` = `{1}.<instance id>`.
- **Write (documented):** `ChangeSession.RunCommandAsync(ProcessRunner.Sys("pnputil.exe"), "/disable-device \"<instance id>\"")`. Windows 10 2004+. Never pass `/force`.
- **Undo:** `pnputil.exe /enable-device "<instance id>"`. It is a System32 tool, so it passes `ChangeJournal` policy. Skip the device if it was already disabled before.
- **Preconditions, all required:**
  1. None of the function's endpoints is a default for any role.
  2. Another active render endpoint exists.
  3. A confirmation dialog states: "monitor/TV speakers on this GPU will stop working until you undo".
  4. Coordinate with Drivers: a disabled device must not be reported as a driver problem there (code 22 = disabled by the user).
- **Recommendation:** none by default. Only for users who never use monitor or TV audio and want it gone from the list without per-monitor clicks. Gaming = everyday.
- **Range:** enabled/disabled.
- **Admin:** yes.
- **Risk:**
  - Forgotten state ("my TV has no sound").
  - NVIDIA clean installs may re-enable or reinstall the function.
  - Affects every HDMI/DP output of that GPU.
  - No performance benefit.
- **This PC:** both monitor endpoints come from one NVIDIA HDA function. The AMD iGPU HDA function has no present endpoints, so there is nothing to gain from disabling it.
- **Evidence:** PnPUtil command syntax (`/disable-device`, `/enable-device`, Windows 10 2004+, `/force` only for critical devices); device ids read on this PC.

### `audio-vendor-apps`: Приложение производителя (гарнитура, микрофон) → **INFO-ONLY**
- **What Windows cannot reach:**
  - Settings that live in the device firmware or the vendor's PC-side DSP. These include INZONE EQ, 360 Spatial Sound for Gaming, Dynamic Range Control and firmware updates (Sony says these work only through the USB connection/transceiver).
  - Razer Seiren V3 Mini sample/bit rate, onboard mixer and lighting (Razer FAQ: the mic works without Synapse; Synapse adds these).
- **Read/detect:** map USB VID/PID of active endpoints (`{b3f8fa53-…},2` / `PKEY_Device_InstanceId`) to a vendor table:
  - `054C` Sony → INZONE Hub;
  - `1532` Razer → Razer Synapse;
  - `046D` Logitech → G HUB;
  - `1038` SteelSeries → GG;
  - `0951`/`03F0` HyperX → NGENUITY;
  - `1B1C` Corsair → iCUE.
  Then check the Uninstall keys and Start-menu shortcuts for the installed app.
- **Act:**
  - Button «ОТКРЫТЬ INZONE HUB» / «ОТКРЫТЬ RAZER SYNAPSE» via `UnelevatedShell.StartAsUserAsync`.
  - If the app is missing, link to the vendor download page in the browser (unelevated). MONOLITH does not install it; Apps/VendorDownloads rules apply if added later.
  - MONOLITH never writes vendor app files or settings.
- **Recommendation:**
  - Use the vendor app for EQ, spatial and firmware.
  - Don't disable its autostart if its PC-side DSP is relied on. Check first whether settings persist on the device.
  - After INZONE Hub installs or updates, re-check default devices. Sony notes the output can switch to the "Chat" device on dual-endpoint models.
  - Razer's Windows Update "LWI Wizard" auto-installer is how Synapse self-installs on plug-in. A 2021 privilege-escalation bug in that flow was fixed; just inform the user, change nothing.
- **Risk:** none.
- **This PC:** INZONE Hub 1.0.20.0 (autostart) and Razer Synapse 4.0.503 are installed; G HUB is installed (mouse/keyboard, not audio).
- **Evidence:** Sony INZONE Hub setup article 00279035; Razer Seiren V3 Mini product page/FAQ; BleepingComputer (Razer Synapse PnP installer LPE, Aug 2021); Microsoft APO architecture doc (custom APO settings belong in a Hardware Support App).

### `audio-mono-check`: Монофонический звук → **INFO-ONLY**
- **Read:** `HKCU\Software\Microsoft\Multimedia\Audio`, `AccessibilityMonoMixState` (DWORD 1 = mono; absent/0 = stereo). The storage is community-documented; the setting lives in Settings → Accessibility → Audio.
- **Write:** none (accessibility setting; registry-only writes need audiosrv/Explorer restarts per guides).
- **Manual:** `ms-settings:easeofaccess-audio`.
- **Recommendation:** Off for gaming (positional cues are lost, and spatial sound is greyed out); On only when needed for single-sided hearing. Show a warning only when it is 1.
- **Risk:** none.
- **This PC:** absent (stereo).
- **Evidence:** ElevenForum mono-audio tutorial; ms-settings URI list.

### `audio-mic-privacy-check`: Доступ приложений к микрофону → **INFO-ONLY**
- **Read:** `HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone` `Value` (Allow/Deny), its `NonPackaged` subkey (desktop apps such as Discord and games), the HKLM counterpart, and the policy `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy\LetAppsAccessMicrophone`.
- **Write:** none. This is privacy territory, owned by the privacy catalog.
- **Manual:** `ms-settings:privacy-microphone`.
- **Recommendation:** if voice chat is used and desktop-app access is Deny, explain that this is why the mic "doesn't work" and open the page.
- **Risk:** none.
- **This PC:** Allow everywhere; no policy.
- **Evidence:** ms-settings URI list (Privacy → Microphone).

---

## 4. DO-NOT-INCLUDE

### `audio-ipolicyconfig-writes`: недокументированный IPolicyConfig (по умолчанию, формат, монопольный режим, эффекты, скрытие)
- **Mechanism:**
  - COM class `CPolicyConfigClient`; interface `IPolicyConfig` IID `F8679F50-850A-41CF-9C72-430F290290C8` (Windows 10 RS1+). Earlier IIDs: `CA286FC3-91FD-42C3-8E9B-CAAFA66242E3` (TH1) and `6BE54BE8-A068-4875-A49D-0C2966473B11` (TH2).
  - Methods: `GetMixFormat`, `Get/Reset/SetDeviceFormat`, `Get/SetProcessingPeriod`, `Get/SetShareMode`, `Get/SetPropertyValue(id, bFxStore, key, pv)`, `SetDefaultEndpoint(id, role)`, `SetEndpointVisibility(id, visible)`.
  - Per-app defaults use the equally undocumented `IAudioPolicyConfigFactory` (`SetPersistedDefaultAudioEndpoint`).
- **Why not:**
  - It is not documented by Microsoft; CLAUDE.md rule 2 allows documented changes only.
  - The IID changed three times during Windows 10. A 2016 AutoIt report describes `SetDefaultEndpoint` silently doing nothing after the Anniversary Update.
  - Microsoft staff say the effects store is not available to applications, and Windows 11 Settings deletes values the workaround cannot recreate.
  - Windows already offers every one of these choices in one or two clicks, and MONOLITH deep-links to the exact page.
  - (For reference only) an undo would be the same call with the previous value per role. It would still be undocumented.
- **Evidence:** EarTrumpet source (`IPolicyConfig.cs`, `IAudioPolicyConfigFactory.cs`); AutoIt forum topic 183977; Microsoft Q&A 669471.

### `audio-fxproperties-registry-edits`: прямая правка FxProperties / удаление APO
- Online guides write `{1da5d803-…},5`, or zero the `{d04e05a6-…},*` CLSIDs, under `HKLM\…\MMDevices\Audio\…\FxProperties`, sometimes after taking ownership of the key with SetACL. Some even invent property IDs.
- **Why not:**
  - The store is private to audiosrv and the Sound control panel (Microsoft staff).
  - Values are consumed by audiosrv, so they need a service restart (protected service).
  - Values are overwritten on driver reinstall (dechamps/APO).
  - Zeroed CLSIDs break the Enhancements UI, and users report apps losing sound.
  - Taking ownership of system keys violates the prime directive.

### `mmcss-audio-tasks`: MMCSS «Audio» / «Pro Audio» (Priority, Scheduling Category, SFIO, Clock Rate, GPU Priority)
- **Mechanism (for recognition):** `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Audio|Pro Audio`. This PC has the defaults: Audio = Priority 6/Medium, Pro Audio = Priority 1/High, Clock Rate 10000, GPU Priority 8, SFIO Normal.
- **Why not (Microsoft MMCSS doc):**
  - `GPU Priority` is "not yet used". `SFIO Priority` is "not used".
  - For `Scheduling Category` High, `Priority` is always treated as 2.
  - The `Clock Rate` guarantee was removed in Windows 7.
  - These keys affect only threads that join the task via `AvSetMmThreadCharacteristics`. audiodg and well-written audio apps already do.
  - Raising categories to High (priority 23–26) can starve other threads.
- Same verdict as gaming_tweaks.md §E (`mmcss-games-task`, `mmcss-system-responsiveness`).

### `audio-latency-tweaks`: «снижение задержки звука»
Covers: buffer-size registry values, HDAudio `PowerSettings` (ConservationIdleTime/IdlePowerState), timer-resolution or HPET tricks for audio, and resident "low-latency" helpers that hold an `IAudioClient3` stream open at the minimum period.

**Why not**
- **The latency is already low.** On Windows 10+ the engine adds about 1.3 ms. The default 10 ms buffer is chosen by the driver, and only apps that ask through `IAudioClient3`/AudioGraph get smaller buffers.
- **Microsoft lists the trade-offs.** Small buffers mean more CPU wake-ups and power, and audio effects add latency for quality reasons. Microsoft advises apps that don't need low latency not to request it.
- **Forcing a small period globally is unsafe and often ineffective.**
  - One app requesting a small period moves every stream on that endpoint and mode to it, which raises the glitch risk.
  - Many drivers, including USB Audio 1.0 class devices like both USB devices here, don't advertise small buffers at all.
- **The HDA idle-power values** only fix pops on HD Audio codecs; they don't change latency.
- **Timer and HPET tweaks** are already DO-NOT-INCLUDE in gaming_tweaks.md.

### `audio-services-disable`: отключение Audiosrv / AudioEndpointBuilder
- **Why not:** this removes all sound, the volume UI and voice chat. Both services are in `ServiceControl.Protected` and `ServiceGuard`'s media group. They cost almost nothing at idle.

### `audiodg-priority-affinity`: приоритет или привязка audiodg.exe к ядрам (IFEO PerfOptions, Process Lasso-style rules)
- **Why not:**
  - audiodg is a protected service process; its priority couldn't even be read here.
  - Its streaming threads are already MMCSS-scheduled.
  - IFEO entries for system processes are hijack-class registry locations (`ChangeJournal.IsForbiddenRestoreTarget`).
  - Pinning it to one core causes crackles under load.

### `audio-high-sample-rate`: 96/192 кГц «для лучшего позиционирования / качества»
- **Why not:**
  - Games and voice apps produce 48 kHz, so higher device rates only add resampling and CPU.
  - Xiph shows no audible benefit, and possible audible intermodulation from ultrasonic content.
  - Some USB devices behave worse at their maximum rate.
- Recommendation stays 48 kHz (`audio-shared-format`).

### `audio-ghost-endpoint-cleanup`: удаление «призрачных» аудиоустройств (ключи MMDevices, «очистка реестра»)
- **Why not:**
  - NOTPRESENT endpoints (24 on this PC) are already hidden and inert.
  - Deleting `MMDevices\Audio\*` keys corrupts AudioEndpointBuilder's store. They are recreated on the next plug-in anyway.
  - CLAUDE.md forbids registry cleaning.
- `pnputil /remove-device` on disconnected devices is also out of scope (no benefit).

---

## 5. Profile matrix

| Item | Gaming (voice chat) | Everyday |
|---|---|---|
| Default devices | Headset on all 3 render roles; the mic you speak into on all 3 capture roles (flag mismatches) | Same; speakers allowed as default |
| Format | 48 kHz (bit depth: no action) | 48 kHz |
| Audio enhancements | Off on headset with vendor DSP; Off on mic if voice sounds processed | Default |
| Spatial sound | Off if INZONE Hub spatial or in-game HRTF is used; else optional Windows Sonic | Optional |
| Ducking | **3 = Do nothing** (SAFE-OPTIONAL, offered pre-explained) | Default (80 %) |
| Exclusive mode | Default (off only to fix grabbing) | Default |
| Mic level | Measure → peaks −12…−6 dBFS | Same |
| Boost / AGC | ≤ +10 dB / AGC off (when present) | Boost ≤ +10 dB / AGC as preferred |
| Monitor audio | Optional "Don't allow" per monitor; ADVANCED whole-GPU disable | Leave |

---

## Sources
- https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-roles
- https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-getdefaultaudioendpoint
- https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-openpropertystore
- https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-disable-sysfx
- https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioengine-deviceformat
- https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-formats
- https://learn.microsoft.com/en-us/windows/win32/coreaudio/stream-attenuation
- https://learn.microsoft.com/en-us/archive/blogs/toml/ducking-music-volume-experience-with-incoming-lync-call-using-zune-player
- https://wisechecker.com/disable-automatic-audio-ducking-communication-apps/
- https://learn.microsoft.com/en-us/answers/questions/286017/audio-ducking-stream-attenuation-setting-doesnt-ta
- https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolume
- https://learn.microsoft.com/en-ie/Windows/Win32/coreaudio/peak-meters
- https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-topologies
- https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nn-devicetopology-iaudiovolumelevel
- https://learn.microsoft.com/en-us/windows/win32/api/devicetopology/nn-devicetopology-iaudioautogaincontrol
- https://learn.microsoft.com/en-us/uwp/api/windows.media.audio.spatialaudiodeviceconfiguration
- https://support.microsoft.com/en-US/Windows/Hardware/Drivers/how-to-turn-on-spatial-sound-in-windows
- https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings
- https://learn.microsoft.com/en-us/answers/questions/669471/how-to-control-enable-audio-enhancements-with-code
- https://github.com/dechamps/APO
- https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-processing-object-architecture
- https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio
- https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax
- https://raw.githubusercontent.com/File-New-Project/EarTrumpet/master/EarTrumpet/Interop/MMDeviceAPI/IPolicyConfig.cs
- https://raw.githubusercontent.com/File-New-Project/EarTrumpet/master/EarTrumpet/Interop/MMDeviceAPI/IAudioPolicyConfigFactory.cs
- https://autoitscript.com/forum/topic/183977-after-win10-anniversary-update-setdefaultendpoint-change-default-sound-device-is-no-longer-working
- https://techcommunity.microsoft.com/discussions/windowspowershell/disable-exclusive-mode-for-all-audio-devices-via-ps/3653408/replies/3657632
- https://docs.discord.com/developers/topics/voice-connections
- https://people.xiph.org/~xiphmont/demo/neil-young.html
- https://obsproject.com/kb/audio-mixer-technical-details
- https://www.saramonic.com/blogs/microphone-level-guide
- https://www.sony.ca/en/electronics/support/articles/00279035
- https://www.razer.com/streaming-microphones/razer-seiren-v3-mini
- https://insider.razer.com/razer-support-45/razer-synapse-causes-auto-gain-on-razer-seiren-12883
- https://www.bleepingcomputer.com/news/security/razer-bug-lets-you-become-a-windows-10-admin-by-plugging-in-a-mouse/
- https://www.techradar.com/computing/windows/windows-11s-ai-powered-voice-clarity-feature-improves-your-video-chats-plus-setup-has-a-new-look-finally
- https://pureinfotech.com/whats-voice-clarity-windows-11-enable/
- https://www.elevenforum.com/t/turn-on-or-off-mono-audio-in-windows-11.5861/
