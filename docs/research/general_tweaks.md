# Lycoris: Everyday and Recommended Windows optimizations catalog

## 0. Conventions used in this catalog

**How each change is undone:** before any write, save `{hive, path, valueName, kind, rawBytes | ABSENT}`. To revert, write the raw original back. If the value was absent before, delete it. If Lycoris created the key, delete the key. Windows is inconsistent about defaults, so never "revert to the documented default". Restore what was actually there. On this machine `TailoredExperiencesWithDiagnosticDataEnabled = 2`, which is not a boolean.

**Classes**
- **SAFE-DEFAULT**: applied by default in the profile marked.
- **SAFE-OPTIONAL**: offered, unchecked by default.
- **ADVANCED**: behind a warning screen.
- **DO-NOT-INCLUDE**: not offered.

**Profiles:** N = Normal, R = Recommended. "D" in a profile column means the item is on by default there.

**Detecting the OS**
- Use `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion` → `CurrentBuild` (Windows 11 when ≥ 22000), plus `UBR`, `DisplayVersion` and `EditionID`.
- Do **not** use `ProductName`. On this Windows 11 machine it reads "Windows 10 Pro" (verified: CurrentBuild 26300, UBR 9457, DisplayVersion 26H2).

**What takes effect when**
- **Ex**: restart Explorer, or broadcast `WM_SETTINGCHANGE`.
- **SO**: sign out.
- **RB**: reboot.
- **—**: takes effect immediately.

---

## 1. Startup apps (`startup.*`)

**`startup.toggle`: «Автозагрузка приложений» / Startup apps. SAFE-DEFAULT as a UI tool. Nothing is disabled automatically.**

**Where startup entries come from**
| Source | Location | Approval key (under `…\CurrentVersion\Explorer\StartupApproved\`) |
|---|---|---|
| HKCU Run | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | HKCU `…\Run` |
| HKLM Run (64-bit) | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` | HKLM `…\Run` |
| HKLM Run (32-bit) | `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run` | HKLM `…\Run32` |
| User Startup folder | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup` | HKCU `…\StartupFolder` (value name = `.lnk` file name) |
| Common Startup folder | `%ProgramData%\Microsoft\Windows\Start Menu\Programs\StartUp` | HKLM `…\StartupFolder` |
| Packaged (MSIX) apps | `HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\<PFN>\<TaskId>` → `State` (DWORD) | Uses the `StartupTaskState` enum: 0 Disabled, 1 DisabledByUser, 2 Enabled, 3 DisabledByPolicy, 4 EnabledByPolicy. Prefer the WinRT `StartupTask` API. |

**Byte format (REG_BINARY, 12 bytes).** This is what Task Manager and Settings › Apps › Startup write.
- Bytes 0–3 are a little-endian DWORD flag.
- Bytes 4–11 are a FILETIME in UTC: the moment the entry was disabled, or zero.
- Enabled: `02 00 00 00 00 00 00 00 00 00 00 00`.
- Disabled: `03 00 00 00` followed by the current UTC FILETIME (`DateTime.UtcNow.ToFileTimeUtc()`).
- Other values occur in the wild. This machine has HKLM `SecurityHealth = 04 00…`, which Task Manager shows as enabled.
- **Reading rule:** treat the entry as disabled when `(byte0 & 1) == 1`. A missing value means enabled.
- **Writing rule:** write 02 or 03 exactly as Task Manager does.
- **Caveat:** Microsoft does not document this format. The 02/03 meaning is well established empirically. The parity rule is my inference from the observed values, so test it against Task Manager.

**Behaviour and side effects**
- Disabling never deletes the Run value or the `.lnk`. Revert by restoring the original bytes, or deleting the value if it was absent.
- Writes to HKLM need elevation.
- Takes effect at the next sign-in. Task Manager must be reopened to show the change.
- **Benefit:** real. Fewer tray apps means a faster sign-in and less background RAM/CPU.
- **Risk:** low. Disabling security agents, audio, touchpad or GPU-panel helpers can lose functionality, so flag Microsoft- and driver-signed entries.
- Self-healing apps (Discord, Steam, Teams) may rewrite their Run value. The StartupApproved state normally persists anyway.
- Task Manager does not show scheduled tasks with logon triggers or services. List those read-only, like Autoruns does.

---

## 2. Background apps (`bg.*`)

The mechanism applies **only to packaged (UWP/MSIX) apps**. Win32 apps are unaffected.

| ID | RU / EN | Mechanism | Default and revert | Apply | Class |
|---|---|---|---|---|---|
| `bg.perapp` | «Фоновая работа приложения» / Per-app background permission | Settings › Apps › Installed apps › … › Advanced options: **Always / Power optimized (default) / Never**. "Never" = `HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\<PackageFamilyName>`: `Disabled`=1 and `DisabledByUser`=1 (DWORD). | Values absent = Power optimized. Revert = delete. | — (app-level) | SAFE-OPTIONAL. Offer "Never" for inbox consumer apps (BingNews, BingWeather, Clipchamp, GetHelp, Solitaire). |
| `bg.global.legacy` | «Глобально запретить фон (Win10)» / Global background off | `HKCU\…\BackgroundAccessApplications\GlobalUserDisabled`=1 plus `HKCU\…\Search\BackgroundAppGlobalToggle`=0 | Absent | SO | Windows 10 only: SAFE-OPTIONAL. Windows 11: the UI was removed and current builds are not confirmed to honour it, so don't offer it. |
| `bg.policy` | «Политика: запрет фоновых приложений» / LetAppsRunInBackground | `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy\LetAppsRunInBackground` DWORD: 0 = user in control, 1 = Force Allow, **2 = Force Deny**. Optional lists: `…_ForceAllowTheseApps` / `…_ForceDenyTheseApps` (MULTI_SZ of PFNs). | Absent | RB/SO | **ADVANCED.** Breaks notifications, alarms and sync for Store apps (Phone Link, new Outlook, WhatsApp, Xbox). |

**Benefit:** small. The OS already suspends these apps and power-optimizes them.

---

## 3. Tips, suggestions and ads (`ads.*`)

All values below are HKCU DWORDs. Set 0 to turn the feature off. Most `SubscribedContent-*` values are **absent by default**, so revert = delete. Changes apply immediately, or after Ex for Start/Explorer.

The base path for the `ContentDeliveryManager` values is `HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager`.

| ID | Value (path) | Controls | N | R |
|---|---|---|---|---|
| `ads.tips` | CDM `SubscribedContent-338389Enabled` | "Get tips and suggestions when using Windows" | D | D |
| `ads.welcome` | CDM `SubscribedContent-310093Enabled` | Welcome experience after updates | D | D |
| `ads.settings` | CDM `SubscribedContent-338393Enabled`, `-353694Enabled`, `-353696Enabled` | Suggested content in Settings | D | D |
| `ads.lockscreen` | CDM `SubscribedContent-338387Enabled` + `RotatingLockScreenOverlayEnabled` | "Fun facts, tips" on the lock screen. Leave `RotatingLockScreenEnabled` alone: it is the Spotlight wallpaper itself. | D | D |
| `ads.start.win10` | CDM `SubscribedContent-338388Enabled` + `SystemPaneSuggestionsEnabled` | Start suggestions (Windows 10; legacy on Windows 11). Already 0 on this machine. | D | D |
| `ads.silentinstall` | CDM `SilentInstalledAppsEnabled`, `PreInstalledAppsEnabled`, `OemPreInstalledAppsEnabled`, `PreInstalledAppsEverEnabled` | Auto-provisioning of promoted apps. Prevents future installs; does not uninstall anything. Default 1 (verified 1 here). | D | D |
| `ads.softlanding` | CDM `SoftLandingEnabled` | "Tips about Windows" toasts | D | D |
| `ads.scoobe` | `HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement\ScoobeSystemSettingEnabled` | "Suggest ways to finish setting up my device" | D | D |
| `ads.start.reco` | `HKCU\…\Explorer\Advanced\Start_IrisRecommendations`=0, `Start_AccountNotifications`=0 | Windows 11 Start: "recommendations for tips, shortcuts, new apps" and account nags. Ex. | D | D |
| `ads.explorer` | `HKCU\…\Explorer\Advanced\ShowSyncProviderNotifications`=0 | OneDrive/M365 promos in Explorer. Ex. | D | D |
| `ads.start.hiderec` | `HKLM\SOFTWARE\Policies\Microsoft\Windows\Explorer\HideRecommendedSection`=1 | Hides the whole Recommended section. **Edition-gated** (officially SE/Education); verify on Pro. | – | OPT |
| `ads.lang` | `HKCU\Control Panel\International\User Profile\HttpAcceptLanguageOptOut`=1 | Websites read the language list | – | D |
| `ads.start.websites` | `HKCU\Software\Policies\Microsoft\Windows\Explorer\HideRecommendedPersonalizedSites`=1 | Website recommendations (from browser history) in Start «Рекомендуем». Policy CSP Start, User + Device scope, Pro/Ent/Edu, Windows 11 22H2 with KB5027303 (22621.1928) and later. Absent = shown. Greys out the Settings toggle «Показывать веб-сайты из журнала браузера» with the "managed" note. The effect is narrow: the recommendations appear only when browsing history is shared and long enough. Ex. | – | OPT |
| `ads.start.phone` | `HKCU\Software\Microsoft\Windows\CurrentVersion\Start\Companions\Microsoft.YourPhone_8wekyb3d8bbwe\IsEnabled`=0 | Phone Link companion panel in the new Start. This is the Settings toggle «Показывать мобильное устройство в меню «Пуск»», not a policy (no UI lock). Absent/1 = shown. Exists only with Phone Link ≥ 1.25032.82.0, so offer it only where the `Companions\Microsoft.YourPhone_8wekyb3d8bbwe` key exists (otherwise the write would create an orphan key that does nothing). Ex. | – | OPT |
| `ads.spotlight.thirdparty` | `HKCU\Software\Policies\Microsoft\Windows\CloudContent\DisableThirdPartySuggestions`=1 | Third-party app/content suggestions in Spotlight and Windows tips (Policy CSP Experience `AllowThirdPartySuggestionsInWindowsSpotlight`, User scope, **Pro supported**). Mostly overlaps the user values above; the remaining real effect is third-party promotion. **Not offered on Home** (edition-blind placebo). The Spotlight pictures stay. SO. Own row, not hidden inside a Start item. | – | OPT |

**Leave `ContentDeliveryAllowed` alone:** setting it to 0 also kills Spotlight.

**Policy route:** the `…\Policies\Microsoft\Windows\CloudContent` policies (e.g. `DisableWindowsConsumerFeatures`) are largely Enterprise/Education-only. On Home/Pro the user values above are the effective mechanism.

**Spotlight / consumer-content policies: DO-NOT-INCLUDE (placebo on Home/Pro).** Policy CSP Experience marks these ❌ for Pro: `DisableWindowsSpotlightFeatures` (AllowWindowsSpotlight), `DisableSpotlightCollectionOnDesktop`, `ConfigureWindowsSpotlight` ("Enterprise SKUs only"), `DisableWindowsSpotlightOnSettings`, `DisableSoftLanding` (AllowWindowsTips), `DisableWindowsConsumerFeatures`, `DisableCloudOptimizedContent`. Lock-screen tips are already covered by `ads.lockscreen`; the Spotlight wallpaper itself stays. The only Pro-supported CloudContent value worth offering is `DisableThirdPartySuggestions` (above).

**Benefit / risk:** removes nags and promo-app installs. No performance effect, no functional risk. SAFE-DEFAULT.

---

## 4. Search, Widgets, Copilot, Recall

**`search.web`: «Убрать Bing из поиска Пуск» / Web results in Start. SAFE-DEFAULT (R).**
- Set `HKCU\Software\Policies\Microsoft\Windows\Explorer\DisableSearchBoxSuggestions`=1 (DWORD). Absent by default. Ex/SO.
- Also write the legacy `HKCU\Software\Microsoft\Windows\CurrentVersion\Search\BingSearchEnabled`=0. It is effective on Windows 10, unreliable on Windows 11, and already 0 here.
- **Side effect:** recent searches disappear from the Explorer search box.
- Insider build 26300 contains a hidden native toggle under Settings › Privacy › Search (not shipped as of Jul 2026). Prefer it once it exists.
- In the EEA the web provider is a removable package, `Microsoft.BingSearch_8wekyb3d8bbwe`. Verified registered here.

**`search.highlights`: «Подсветка поиска» / Search highlights. SAFE-DEFAULT (R).**
- `HKCU\Software\Microsoft\Windows\CurrentVersion\SearchSettings\IsDynamicSearchBoxEnabled`=0.
- Policy alternative: `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\EnableDynamicContentInWSB`=0.

**`widgets.off`: «Отключить Виджеты» / Widgets. SAFE-DEFAULT (R).**
- Set `HKLM\SOFTWARE\Policies\Microsoft\Dsh\AllowNewsAndInterests`=0 (DWORD). Absent by default. Ex/SO.
- This removes the board, the button and its WebView2 processes. Microsoft (Windows IT Pro blog, Mar 2024): disabling it "turns off weather and more on the lock screen in addition to widgets on the taskbar" — say so in Details.
- **Do not write `HKCU\…\Explorer\Advanced\TaskbarDa` directly.** On 24H2+ the User Choice Protection Driver (UCPD, running here) blocks it with "access denied". Never disable UCPD.
- To hide only the button, open `ms-settings:taskbar`.
- Uninstalling `MicrosoftWindows.Client.WebExperience`: DO-NOT-INCLUDE.

**`news.off`: «Отключить «Новости и интересы»» / News and interests (Windows 10). SAFE-DEFAULT (R).** Catalog id `news-interests-off`.
- `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Feeds\EnableFeeds`=0 (DWORD). Feeds.admx «Enable news and interests on the taskbar», Policy CSP Feeds/FeedsEnabled. Absent by default (feed on). Ex.
- Windows 10 counterpart of `widgets.off`: offer only on builds 19041–19045, so the two rows never appear together.
- Edition table: Windows 10 Pro/Business/Enterprise/Education = yes, Home not listed (the key is reported to be honoured there, undocumented) → Details say «официально: Pro и выше».
- Microsoft: on Windows 10 it also turns off "weather and more" on the lock screen, and the taskbar context-menu entry disappears.
- Revert = delete the value; the empty `Windows Feeds` key is removed (Lycoris created it). The user's own HKCU feed mode comes back untouched.
- **Rejected:** writing `HKCU\…\Feeds\ShellFeedsTaskbarViewMode`. Since 2022 the value is protected (writes are reverted or fail with access denied, Microsoft Q&A, TenForums): a placebo. DO-NOT-INCLUDE.

**`ai.paint` / `ai.notepad`: AI features in Paint and Notepad. SAFE-OPTIONAL.** Catalog ids `paint-ai-off`, `notepad-ai-off`.
- Paint: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint` → `DisableCocreator`, `DisableGenerativeFill`, `DisableImageCreator` = 1 (DWORD). Policy CSP WindowsAI, device scope, Pro/Ent/Edu, Windows 11 22H2 10.0.22621.4870+ / 24H2 10.0.26100.3360+, ADMX WindowsCopilot.admx. Absent = available. Restart Paint.
- Notepad: `HKLM\SOFTWARE\Policies\WindowsNotepad\DisableAIFeatures`=1 (Microsoft Learn «Manage Notepad»: Windows 11 22H2+, Notepad 11.2503.16.0+, machine-wide). Restart Notepad.
- Home is not in the CSP edition table; the apps read the keys, but say «официально: Pro и выше» for Paint. Gate both on Windows 11 (22621+).
- Revert = delete the values. `…\Policies\Paint` is removed when Lycoris created it. **Known gap:** `SOFTWARE\Policies\WindowsNotepad` is only two levels deep and `RegistryHelper.TryDeleteEmptyKeyChain` never deletes keys above three segments, so the empty key stays after undo unless the helper allows `SOFTWARE\Policies\<Vendor>` (requested from the core owner).
- **Rejected:** `HideAIActionsMenu` (HKLM `…\Policies\Microsoft\Windows\Explorer`) appears only on blogs, no Microsoft documentation → DO-NOT-INCLUDE; the supported route is Settings → Apps → Actions, and since build 26220.7344 the «AI actions» menu disappears when all actions are off. `DisableSettingsAgent`, `ConfigureAgentConnectors`, `DisableRecallDataProviders` are Enterprise/Education-only or Insider-only → placebo on Home/Pro, DO-NOT-INCLUDE. `DisableWidgetsOnLockScreen` is Insider-only: wait.

**`copilot.*`: «Copilot»**
- **Legacy pane:** `HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot\TurnOffWindowsCopilot`=1. It is **deprecated** and does not affect the new Copilot app. Apply it only on 22H2/23H2 builds. Also set `HKCU\…\Explorer\Advanced\ShowCopilotButton`=0 there (0 here).
- **24H2+ Copilot is a Store app:** `winget uninstall --id 9NHT9RB2F4HD -s msstore`. The ID was verified via `winget show`. Revert with `winget install --id 9NHT9RB2F4HD -s msstore`. SAFE-OPTIONAL; never default. It is not installed on this machine.
- **`RemoveMicrosoftCopilotApp`** (WindowsAI policy): Enterprise/Education. It only fires if M365 Copilot is also installed, the user didn't install Copilot, and Copilot wasn't launched in 28 days. It is one-shot, and the user can reinstall. DO-NOT-INCLUDE because it is unreliable for consumers.
- **Copilot key:** `HKCU\SOFTWARE\Policies\Microsoft\Windows\CopilotKey` holds an AUMID (policy `SetCopilotHardwareKey`). SAFE-OPTIONAL.

**`recall.*`: «Recall»**
- Recall exists only on Copilot+ PCs (NPU ≥ 40 TOPS). This desktop does not qualify, so the policy is a harmless no-op.
- `SOFTWARE\Policies\Microsoft\Windows\WindowsAI\DisableAIDataAnalysis`=1 in HKCU or HKLM, Pro+, 24H2 KB5055627+. Stops snapshot saving and **deletes existing snapshots**. SAFE-DEFAULT (R).
- `HKLM\…\WindowsAI\AllowRecallEnablement`=0 removes the Recall component (RB). Revert: set 1, then `DISM /Online /Enable-Feature /FeatureName:Recall`. ADVANCED.
- `DisableClickToDo`=1 (same key): SAFE-OPTIONAL.

---

## 5. Privacy and telemetry (`priv.*`)

| ID | RU / EN | Mechanism | Default | Apply | Class |
|---|---|---|---|---|---|
| `priv.telemetry` | «Только обязательные диагностические данные» / Required diagnostic data | Settings-backed value: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection\AllowTelemetry`=1 (1 here; `MaxTelemetryAllowed` also present). The policy route `HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry`=1 adds a "managed by your organization" banner. **0 (Diagnostic data off) is honoured only on Enterprise, Education, IoT and Server.** On Home/Pro the minimum is 1 (Required), so never offer 0 unless `EditionID` qualifies. Values: 0 off, 1 Required, 3 Optional; 2 (Enhanced) does not exist on Windows 11. | 1 or 3, chosen at OOBE | — | SAFE-DEFAULT (R) via the Settings value; policy route = ADVANCED |
| `priv.limits` | Limit dumps and logs | Same policy key: `LimitDiagnosticLogCollection`=1, `LimitDumpCollection`=1 (Windows 11) | Absent | — | SAFE-OPTIONAL |
| `priv.adid` | «Рекламный идентификатор» / Advertising ID | `HKCU\…\CurrentVersion\AdvertisingInfo\Enabled`=0. Policy: `HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo\DisabledByGroupPolicy`=1. | 1 if accepted at OOBE (0 here) | — | SAFE-DEFAULT (N, R) |
| `priv.tailored` | «Персонализированные возможности» / Tailored experiences | `HKCU\…\CurrentVersion\Privacy\TailoredExperiencesWithDiagnosticDataEnabled`=0. Policy: `HKCU\Software\Policies\Microsoft\Windows\CloudContent\DisableTailoredExperiencesWithDiagnosticData`=1. | Varies (2 here) | — | SAFE-DEFAULT (R) |
| `priv.activity` | «Журнал действий» / Activity history | `HKLM\SOFTWARE\Policies\Microsoft\Windows\System`: `PublishUserActivities`=0, `UploadUserActivities`=0, `EnableActivityFeed`=0 | Absent | SO | SAFE-DEFAULT (R) |
| `priv.feedback` | «Частота отзывов: никогда» / Feedback frequency | `HKCU\Software\Microsoft\Siuf\Rules\NumberOfSIUFInPeriod`=0 and delete `PeriodInNanoSeconds` (what Settings "Never" does). Policy: `HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\DoNotShowFeedbackNotifications`=1. | Key absent (Automatic) | — | SAFE-DEFAULT (R) |
| `priv.inking` | «Персонализация рукописного ввода» / Inking & typing | `HKCU\Software\Microsoft\InputPersonalization`: `RestrictImplicitInkCollection`=1, `RestrictImplicitTextCollection`=1. `…\TrainedDataStore\HarvestContacts`=0. `HKCU\Software\Microsoft\Input\TIPC\Enabled`=0. | 0/0/1/1 | SO | SAFE-OPTIONAL |
| `priv.location` | «Расположение» / Location | Device: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location\Value` = "Deny"/"Allow" (REG_SZ; "Deny" here). Per-user: same path under HKCU. | "Allow" | — | SAFE-OPTIONAL. Breaks Find My Device, automatic time zone, weather and maps. **Never default on laptops.** |
| `priv.remoteassist` | «Отключить удалённый помощник» / Remote Assistance off | `HKLM\SYSTEM\CurrentControlSet\Control\Remote Assistance\fAllowToGetHelp`=0 (verified 1 here). Also disable firewall group `@FirewallAPI.dll,-33002` (resource ID; display names are localized). Verify that ID with `Get-NetFirewallRule -Group`. | 1 on Pro | — | SAFE-DEFAULT (R). Quick Assist is unaffected. |

---

## 6. Storage and power (`sys.*`)

**`sys.storagesense`: «Контроль памяти» / Storage Sense. SAFE-DEFAULT (N, R).**
- Path: `HKCU\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy`, all DWORD.
- Values:
  - `01` = on (1) / off (0).
  - `2048` = cadence: 0 = when free space is low, 1 = daily, 7 = weekly, 30 = monthly.
  - `04` = delete temporary files (1).
  - `08` = Recycle Bin cleanup on.
  - `256` = Recycle Bin days (0, 1, 14, 30, 60).
  - `32` = Downloads cleanup on.
  - `512` = Downloads days.
- Current state here: `01=1`, `2048=0`; everything else absent (revert = delete).
- **Lycoris preset:** `01=1`, `2048=0` or `30`, `04=1`, `08=1`, `256=30`, `32=0`, `512=0`. **Never auto-delete Downloads** because of data-loss risk.
- `HKLM\SOFTWARE\Policies\Microsoft\Windows\StorageSense` (`AllowStorageSenseGlobal`, `ConfigStorageSenseGlobalCadence` …) locks the UI. Not used.

**`sys.faststartup`: «Отключить быстрый запуск» / Fast Startup off.**
- `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power\HiberbootEnabled`=0 (DWORD). The default is 1 when hibernation is available; it is already 0 on this machine. Takes effect at the next shutdown.
- **Pros:** a real cold boot (fresh driver and kernel state, consistent uptime), safe dual-boot and NTFS access from other operating systems, fewer "my GPU/USB is weird after power-on" issues.
- **Cons:** a few seconds slower cold boot (negligible on NVMe).
- Desktops: SAFE-DEFAULT (R). Laptops: SAFE-OPTIONAL.

**`sys.hibernate`: «Отключить гибернацию» / `powercfg /h off`.** Catalog id `hibernation-off`.
- Deletes `hiberfil.sys` (about 40% of RAM; ~13 GB at 32 GB) and implicitly disables Fast Startup.
- Revert: `powercfg /h on`, then restore `HiberbootEnabled`.
- **Hybrid sleep (Microsoft KB 920730, verbatim):** «You may lose data if you make hibernation unavailable and a power loss occurs while the hybrid sleep setting is turned on. When you make hibernation unavailable, hybrid sleep does not work.» Hybrid sleep is on by default on desktops, so the Details and the Warning must say that plain sleep keeps working but hybrid sleep stops, and that a power loss during sleep then loses unsaved work. Whether it applies on this PC is read-only: `SYSTEM_POWER_CAPABILITIES.FastSystemS4` («supports the hybrid sleep state»; false e.g. with an active hypervisor, as on the dev machine) and the active plan's `HYBRIDSLEEP` AC value (`SUB_SLEEP` 238c9fa8-… / 94ac6d29-73ce-41a6-809f-6363ba21b47e, 1 = on). Lycoris records the fact in the journal record's old value («гибернация включена, гибридный сон: включён в текущей схеме / выключен / недоступен»); the texts stay generic.
- `powercfg /h /type reduced` (Fast Startup only, half-size file) is a **manual alternative only** — hibernation and hybrid sleep are unavailable with it too, so it is mentioned in the Details and not offered as a second item: `hibernation-off` stays the single item. The revert still restores a reduced file when that was the original type.
- Desktops: ADVANCED.
- **Laptops: DO-NOT-INCLUDE.** The critical-battery action and Modern Standby both fall back to hibernation, so data would be lost.

**`sys.timeouts`: «Экран и сон по рекомендации Microsoft» — screen-off / sleep idle timeouts, only shortened. SAFE-OPTIONAL, never in a profile.** Catalog ids `battery-screen-off-3min`, `battery-sleep-10min` (DC, laptops) and `screen-off-ac-10m`, `sleep-ac-30m` (AC, every PC incl. desktops — owner 01.11: «правка не только для ноутбука, но и для настольного компьютера»).
- **Settings:** `SUB_VIDEO` 7516b95f-f776-4464-8c53-06167f40cc99 / `VIDEOIDLE` 3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e (seconds, 0 = never) and `SUB_SLEEP` 238c9fa8-0aad-41ed-83f4-97be242c8f20 / `STANDBYIDLE` 29f6c1db-86da-48c5-9fdb-f2b67b1f44da (seconds, 0 = never), always of the ACTIVE plan, one journal record per setting.
- **Evidence:** Microsoft's «Learn more about energy recommendations» (Settings → System → Power → Energy recommendations) lists «Turn off my screen after 3 (or 10) minutes» and «Put my device to sleep after 3 (or 10) minutes» — Microsoft gives both values for both settings «depending on the system». **Lycoris's own choice**, stated as such in the Details: battery — screen 180 s, sleep 600 s; mains — screen 600 s, sleep 1800 s (the stock Balanced AC values on Windows 10/11 desktops; most «never»-set PCs are gaming desktops that then run and light the room all night). Screen and idle time are the largest power drains Windows itself controls.
- **Only shortens (`onlyShorten`):** a value is written only when the current one is 0 (never) or longer than the target; shorter values count as applied and are never lengthened. Preview shows current → new from the record texts («от батареи: через 15 мин» → «через 3 мин»; «от сети: никогда» → «через 30 мин»).
- **DC:** `powercfg /setdcvalueindex {scheme} {sub} {setting} {v} && powercfg /setactive SCHEME_CURRENT` with the same self-rollback as the AC command; read with `PowerReadDCValueIndex`; target `powercfg:{scheme}:{setting}:dc`; a dedicated journal regex (`/setdcvalueindex`) as strict as the AC one. The AC values are never touched by the DC items and vice versa. Refused during a Game Boost session (its temporary plan), as for every PowerSettingTweak.
- **Undo:** the original index written back explicitly (same `PowerSettingUndoNote` caveat as the other powercfg items: an override where the plan default was used). Not a data-loss risk: apps that present or play video hold power requests that block idle sleep; a desktop with hybrid sleep saves the session to disk.
- **Laptop rule (01.11):** DC values may be changed only in the saving direction (shorter timeouts, Best efficiency mode) — see §11 and gaming_tweaks.md §2 C.

**`sys.lastaccess`: «Не обновлять время последнего доступа NTFS» / NTFS last access.**
- `fsutil behavior set disablelastaccess {0|1|2|3}`: 0 = user-managed on, 1 = user-managed off, 2 = system-managed on, 3 = system-managed off.
- The underlying value is `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\NtfsDisableLastAccessUpdate`. It is 0x80000002 here (System Managed, enabled). Store the raw DWORD; revert by rewriting it (or `fsutil … 2`). RB.
- **Benefit:** negligible on SSD/NVMe. NTFS already batches the update for up to 1 hour.
- **Risk:** Microsoft notes it affects backup and remote-storage programs. It can also confuse age-based cleanup ("not opened for X days").
- ADVANCED.

**`sys.trim`: «TRIM для SSD включён» / repair NTFS delete notifications. SAFE-DEFAULT (R), repair only.** Catalog id `trim-on`.
- `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\DisableDeleteNotification` DWORD: 0 = TRIM on (Windows default for NTFS), 1 = off. This is what `fsutil behavior set DisableDeleteNotify 0` changes; Microsoft: "Enabling or disabling doesn't require a restart. Trim is effective when the next unmap command is issued."
- Write only when the value is non-zero and an SSD is present; on a healthy PC nothing is written. Revert restores the exact previous DWORD (i.e. switches TRIM off again — a faithful revert of a repair).
- **Never touch `RefsDisableDeleteNotification`:** ReFS v2 (including Windows 11 Dev Drive) ships with trim off on purpose; writing 0 there would change a default, not repair one.
- **Group Policy:** ADMX_FileSys «Disable delete notifications on all volumes» = `HKLM\SYSTEM\CurrentControlSet\Policies\DisableDeleteNotification`. If it is set, the PC is managed: not offered, nothing reported as fixed.
- Microsoft also notes "some devices may experience performance degradation when the delete notification feature is turned on" — Details say so; undo is one click.
- Dashboard «trim» check should get a shortcut into this item instead of telling the user to type fsutil.

**`sys.optimize.task`: «Оптимизация дисков по расписанию». SAFE-OPTIONAL repair, never in a profile.** Catalog id `drive-optimize-schedule-on`.
- Scheduled task `\Microsoft\Windows\Defrag\ScheduledDefrag`, enabled by default (weekly; retrims SSDs, defragments HDDs; per the `defrag` Microsoft Learn page SSDs may also get a traditional defragmentation at most once a month when needed — the Microsoft storage team explains this happens when volume snapshots / System Restore are on (Hanselman, 2014)). `services.md` rates disabling it "never".
- Enable only when disabled (ScheduledTaskTweak); the journal keeps the previous task state, so undo disables it again.
- Not in profiles: turning the schedule off is a legitimate choice in the official «Оптимизация дисков» UI.
- `defragsvc` is in `ServiceControl.Protected` and is not touched.

**`sys.edge.background`: «Microsoft Edge без фонового запуска». SAFE-OPTIONAL, never default.** Catalog id `edge-background-off`.
- `HKLM\SOFTWARE\Policies\Microsoft\Edge` → `StartupBoostEnabled`=0 (Edge ≥ 88) and `BackgroundModeEnabled`=0 (Edge ≥ 77), REG_DWORD, absent by default. Documented Edge policies, mandatory path, dynamic policy refresh, no domain-join restriction (unlike e.g. HomepageLocation), so they work on unmanaged Home/Pro.
- The Recommended path (`…\Edge\Recommended`) is ignored once the user touched the toggle, so detection could not confirm the effect: use the mandatory path.
- Startup boost does write `HKCU\…\CurrentVersion\Run\MicrosoftEdgeAutoLaunch_<hash>` (`--no-startup-window --win-session-start`), which the Startup tab already shows; with the policy Edge removes it itself and does not relaunch after the last window closes. Lycoris never writes the Run value (IsForbiddenRestoreTarget); Edge recreates it after revert because its own preference was never touched.
- **Honest effect:** Microsoft calls the resource impact of startup boost "limited" and enables it automatically only for people who use Edge every few days (> 4 GB RAM, or > 1 GB with a modern disk). Background mode is off by default, so that half only locks it. No FPS claim, no RAM promise.
- **Side effects (Details):** the first Edge launch after sign-in is slower; sites and installed web apps cannot notify while Edge is closed; edge://settings/system shows the toggles locked «Управляется вашей организацией».
- Never touch EdgeUpdate or WebView2 policies (`services.md`: never disable edgeupdate). Offer only when msedge.exe exists.
- Revert = delete both values; the empty `Policies\Microsoft\Edge` key is removed when Lycoris created it (TryDeleteEmptyKeyChain skips it when WebView2/Recommended subkeys exist).

---

## 7. UI and quality of life (`ui.*`)

**`ui.vfx`: «Визуальные эффекты: производительность (с сглаживанием шрифтов и эскизами)». SAFE-OPTIONAL.**

The registry pieces:
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects\VisualFXSetting` DWORD: 0 = Let Windows choose, 1 = Appearance, 2 = Performance, **3 = Custom** (use 3).
- The effective state lives in `HKCU\Control Panel\Desktop\UserPreferencesMask` (REG_BINARY, 8 bytes). The default here is `9E 1E 07 80 12 00 00 00`; the commonly quoted "best performance" is `90 12 03 80 10 00 00 00`. Its bits map to the `SPI_*` UI-effect flags. **Do not hand-write it.** It only takes effect after sign-out, and the layout varies by build.

Apply through `SystemParametersInfo(…, SPIF_UPDATEINIFILE|SPIF_SENDCHANGE)`, which updates the mask live:
- Turn **off**: `SPI_SETMENUANIMATION`, `SPI_SETCOMBOBOXANIMATION`, `SPI_SETLISTBOXSMOOTHSCROLLING`, `SPI_SETTOOLTIPANIMATION`, `SPI_SETSELECTIONFADE`, `SPI_SETCURSORSHADOW`, `SPI_SETDROPSHADOW`, `SPI_SETCLIENTAREAANIMATION`, and `SPI_SETANIMATION` (ANIMATIONINFO `iMinAnimate`=0, i.e. `WindowMetrics\MinAnimate`="0").
- **Keep on:** `SPI_SETFONTSMOOTHING` (`FontSmoothing`="2", `FontSmoothingType`=2 for ClearType) and `SPI_SETDRAGFULLWINDOWS`.

Explorer and DWM values (Ex):
- `Explorer\Advanced`: `TaskbarAnimations`=0, `ListviewAlphaSelect`=0, `ListviewShadow`=0, **`IconsOnly`=0 (keeps thumbnails)**.
- `HKCU\Software\Microsoft\Windows\DWM`: `EnableAeroPeek`=0, `AlwaysHibernateThumbnails`=0.

**Benefit:** perceived snappiness only, no FPS gain. **Risk:** none functional.
Optional extra: transparency off, `HKCU\…\Themes\Personalize\EnableTransparency`=0. Small help on iGPU laptops.

**Other UI items**
- **`ui.menudelay`** «Задержка меню». `HKCU\Control Panel\Desktop\MenuShowDelay` (REG_SZ), default "400", set "150". Apply via `SPI_SETMENUSHOWDELAY` (live). Avoid 0, which makes menus jittery. SAFE-OPTIONAL.
- **`ui.ext`** «Показывать расширения файлов». `Explorer\Advanced\HideFileExt`=0 (default 1). Has a security benefit: it exposes `invoice.pdf.exe`. Ex/F5. SAFE-DEFAULT (N, R).
- **`ui.hidden`** Show hidden files. `Hidden`=1 (default 2). SAFE-OPTIONAL. `ShowSuperHidden`=1 (protected OS files): DO-NOT-INCLUDE.
- **`ui.launchto`** «Открывать "Этот компьютер"». `Explorer\Advanced\LaunchTo` DWORD: 1 = This PC, 2 = Home (default), 3 = Downloads, 4 = OneDrive/sync root. Absent by default, so revert = delete. SAFE-OPTIONAL.
- **`ui.ctxmenu`** «Классическое контекстное меню». Create `HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32` with (Default) = **empty REG_SZ** (it must exist and be empty, not missing). Revert = delete the `{86ca…}` key. Ex. Still the standard method as of May 2026; Microsoft could break it, so detect after Explorer restart. SAFE-OPTIONAL.
- **`ui.endtask`** «Завершить задачу на панели задач». `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings\TaskbarEndTask`=1 (DWORD). Absent by default. Windows 11 22H2 Moment 4 (KB5030310) and later. Ex. Kills the process tree, so the user can lose unsaved work. SAFE-OPTIONAL (D in R).
- **`ui.explorer.home`** «"Главная" Проводника без недавних файлов» (catalog id `explorer-home-private`). SAFE-OPTIONAL.
  - `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer`: `ShowRecent`=0 (Folder Options → Privacy «Показывать недавно использовавшиеся файлы»), `ShowCloudFilesInQuickAccess`=0 («Показывать файлы с Office.com» / account-based insights), `ShowRecommendations`=0 («Показать раздел рекомендаций»; ships for everyone from 24H2 26100.7309 / 25H2 26200.7309, earlier only for Entra ID accounts — elsewhere the value is simply not read). Absent = 1. Ex.
  - Settings-backed user values: no UI lock, all editions, Windows 10 22H2 and 11. Benefit: privacy when screen-sharing; Explorer stops its cloud recommendation requests on Home. Pinned Quick access folders stay; history and jump lists are not deleted.
  - **Leave `ShowFrequent` on:** with recent files, frequent folders and cloud files all off, Home renders blank (ElevenForum 21120; note that thread's bullet list swaps ShowRecent/ShowFrequent).
  - The machine policy `HKLM\Software\Policies\Microsoft\Windows\Explorer\DisableGraphRecentItems`=1 (FileExplorer CSP, Pro+, 22621+) blocks the cloud requests completely but locks the checkbox: not used.

---

## 8. Network (`net.*`)

**`net.dns`: «DNS: Cloudflare / Google». SAFE-OPTIONAL, never default.**

1. **Pick adapters:** `Get-NetAdapter -Physical | ? Status -eq 'Up'`. Skip Hyper-V vEthernet, WSL, VPN/TAP/WireGuard and virtual adapters.
2. **Snapshot per `InterfaceGuid`:**
   - IPv4 under `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{GUID}`: `NameServer` (REG_SZ, static list; **empty = DHCP**) and `DhcpNameServer` (what DHCP handed out).
   - IPv6 under `…\Services\Tcpip6\Parameters\Interfaces\{GUID}`: `NameServer`.
   - Also record `Get-DnsClientServerAddress -InterfaceIndex n` and `Get-DnsClientDohServerAddress`.
3. **Apply:** `Set-DnsClientServerAddress -InterfaceIndex n -ServerAddresses 1.1.1.1,1.0.0.1,2606:4700:4700::1111,2606:4700:4700::1001`. Google: `8.8.8.8, 8.8.4.4, 2001:4860:4860::8888, 2001:4860:4860::8844`. Then `Clear-DnsClientCache`.
   - **Always set IPv6 too.** Otherwise the router-advertised IPv6 DNS (RDNSS) keeps being used.
4. **Revert:**
   - If it was DHCP: `Set-DnsClientServerAddress -InterfaceIndex n -ResetServerAddresses`.
   - Otherwise: re-apply the saved static list.
   - Then flush the cache.

- DoH (`netsh dns add encryption …` or Settings): ADVANCED.
- **Risk:** breaks corporate/VPN split DNS, local `.lan`/`.home` names, Pi-hole/AdGuard and router parental filters.
- **Benefit:** helps only with a poor ISP resolver. No effect on in-game ping.
- **Dynamic DNS registration must survive:** `netsh … set dnsservers … register=none|primary|both` (and `source=dhcp`, whose default is primary) rewrites the adapter's «Зарегистрировать адреса этого подключения в DNS» / «Использовать DNS-суффикс подключения при регистрации» flags. The DNS script reads `Get-DnsClient -InterfaceIndex` → `RegisterThisConnectionsAddress` / `UseSuffixWhenRegistering` before netsh and puts them back with `Set-DnsClient` after it (also in the rollback). Registration is per interface, not per address family, so each family's script restores it immediately. Mapping the flags to netsh's none/primary/both would lose the "suffix checked but greyed" state, so it is not used.

**`net.tcp-autotuning`: «Автонастройка окна TCP — стандартная» / repair receive-window auto-tuning. SAFE-DEFAULT (R), repair only.** Catalog id `tcp-autotuning-normal`.
- Many "internet optimizers" set `netsh interface tcp set global autotuninglevel=disabled` (or experimental). Microsoft (Performance tuning network adapters): "The default level is Normal" and with auto-tuning disabled "for a 1Gbps connection that has a latency of 10 ms, the total achievable throughput is only 51 Mbps". The troubleshooting article gives `netsh int tcp set global autotuninglevel=normal` as the fix. Levels: disabled, highlyrestricted, restricted, normal, experimental.
- **Read** locale-independently from WMI `root\StandardCimv2:MSFT_NetTCPSetting` → `AutoTuningLevelLocal` (uint8 0–4 = Disabled…Experimental; the Automatic template has none) and `AutoTuningLevelEffective` (0 Local, 1 GroupPolicy). Never parse `netsh … show global` (localized). System.Management, not a PowerShell launch, so detection stays cheap.
- **Apply** only when the level is not normal: `netsh.exe interface tcp set global autotuninglevel=normal` through `ChangeSession.RunCommandAsync`; undo = the same line with the original level (journal check: System32 netsh.exe, exactly that argument line, one of the five names).
- netsh "global" sets every template, so the repair runs only when all templates hold the **same** level; mixed templates are refused (one undo line could not restore each of them). If Group Policy decides the level (QoS «inbound TCP throughput level»), nothing is changed.
- State is reported through Detect (like `win32-priority-programs`), not AppliesTo, so the row does not turn "not applicable" after a successful repair.
- **Risk / Details:** some users disabled it on purpose for old routers or firewalls that mishandle RFC 1323 window scaling; Details say so, undo is one click. No effect on game ping; applies to new connections immediately.

**Network myths, explicitly rejected (no catalog item):**
- QoS «Limit reservable bandwidth» / `NonBestEffortLimit`=0: Raymond Chen calls it a "placebo setting"; the 20 % is a maximum reservation for QoS applications, not a cut of normal traffic. DO-NOT-INCLUDE.
- DSCP / QoS policies for games on home internet: ISPs ignore or bleach the marks. DO-NOT-INCLUDE.
- `TCPNoDelay` / `TcpAckFrequency`: UDP games are unaffected; already ADVANCED (`tcp-ack-frequency`).
- `NetworkThrottlingIndex`: already ADVANCED placebo.
- NIC offload / RSS / RSC / interrupt-moderation toggles: DO-NOT-INCLUDE (gaming_tweaks.md §3).

**`net.do`: «Отключить P2P доставку обновлений» / Delivery Optimization HTTP-only. SAFE-DEFAULT (R).**
- Set `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization\DODownloadMode`=0 (DWORD).
- Modes: 0 HTTP only; **1 LAN (default)**; 2 Group; 3 Internet; 99 Simple; **100 Bypass, deprecated on Windows 11 and causes download failures, so never use it.**
- The Settings-backed alternative is `HKU\S-1-5-20\Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Settings\DownloadMode` (undocumented). There is no policy or value on this machine now. Revert = delete. Restart DoSvc or reboot.
- **Benefit:** no LAN upload or peer traffic. Downloads still go through DO/CDN.

**`net.wifipower`: «Wi-Fi без энергосбережения».**
- **Power-plan setting.** Subgroup `19cbb8fa-5279-450e-9fac-8a3d5fedd0c1`, setting `12bbebe6-58d6-4636-95bb-3217ef867c1a`. Values: 0 Max Performance, 1 Low, 2 Medium, 3 Max Saving.
  - Command: `powercfg /setacvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 0` followed by `powercfg /setactive SCHEME_CURRENT`.
  - Read the original with `powercfg /q` or `PowerReadACValueIndex`. This machine (High Performance plan) has AC 0 / DC 0.
  - SAFE-DEFAULT for AC. **Laptops: touch AC only, never DC** (for Wi-Fi; the catalog-wide DC rule — saving direction only — is in §11).
- **Device setting.** `Set-NetAdapterPowerManagement -Name <wifi> -AllowComputerToTurnOffDevice Disabled`. Read first with `Get-NetAdapterPowerManagement`.
  - Desktop only: SAFE-OPTIONAL.
  - Skip on Modern Standby (S0ix) devices.
- Vendor driver properties (MIMO power save, roaming aggressiveness): DO-NOT-INCLUDE.

---

## 9. Windows Update (`wu.*`): pause or defer, never disable

**`wu.pause`: «Приостановить обновления» (max 35 days).**
- Preferred: open `ms-settings:windowsupdate`.
- Programmatic and documented (Pro+): `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate`, `PauseQualityUpdatesStartTime` / `PauseFeatureUpdatesStartTime` (REG_SZ `yyyy-MM-dd`). A pause runs for 35 days.
- The Settings UI itself stores `HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings\PauseUpdatesExpiryTime` (ISO-8601 UTC string) plus `Pause*StartTime/EndTime`. Undocumented, so read only.
- SAFE-OPTIONAL.

**`wu.defer`: «Отложить» (Pro+ only; Home ignores it).**
- Same policy key: `DeferFeatureUpdates`=1 with `DeferFeatureUpdatesPeriodInDays` (0–365), and `DeferQualityUpdates`=1 with `DeferQualityUpdatesPeriodInDays` (0–30).
- Version pin: `TargetReleaseVersion`=1, `ProductVersion`="Windows 11", `TargetReleaseVersionInfo`="25H2". Catalog id `wu-target-version` (SAFE-OPTIONAL, never in a profile; ExclusiveGroup `wu-feature-updates` with `wu-defer-feature-updates` — one of the two).
  - **Pro and higher only** (Home ignores the policy; `IsHomeEdition` → not offered). **GA versions only:** the pin value is the installed `DisplayVersion`, offered only when it is in the catalog's end-of-servicing table (Home/Pro: 23H2 → 2025-11-11, 24H2 → 2026-10-13, 25H2 → 2027-10-12, 26H1 → 2028-03-14 (GA, new devices only), 26H2 → 2028-10-10 — verified 01.11.2026 on the release-health page; a later version is added only after learn.microsoft.com/windows/release-health/windows11-release-information lists its date). Insider/preview builds are never pinned.
  - **60-day post-EOS behaviour (Microsoft, Windows Update for Business docs):** a device pinned to a version that reached end of servicing keeps the pin for about 60 days and is then automatically moved to a supported version; until then it gets **no** security updates. The item therefore says so in the Details, and the Dashboard warns 90 days before the end-of-servicing date so the user un-pins in time.
  - Settings shows «Некоторыми параметрами управляет ваша организация» while the pin is active. Revert = delete the three values (the empty policy key Lycoris created is removed with them).
- Settings will show "managed by your organization". Revert = delete the values. SAFE-OPTIONAL.
- `ExcludeWUDriversInQualityUpdate`=1 stops Windows Update from replacing GPU drivers but also blocks useful drivers. ADVANCED.

**DO-NOT-INCLUDE:** disabling `wuauserv`, `UsoSvc` or `WaaSMedicSvc`, or hosts-file blocking. These break security patching, Store and Defender updates.

---

## 10. Services and the "never do this" list

| ID | Item | Verdict | Why |
|---|---|---|---|
| `svc.diagtrack` | DiagTrack (Connected User Experiences and Telemetry): `HKLM\SYSTEM\CurrentControlSet\Services\DiagTrack\Start`, default 2 = Automatic (verified, running). Change with `sc.exe config DiagTrack start= demand` or `disabled`; save `Start` and `DelayedAutostart`. | **ADVANCED** | Stops telemetry upload. Breaks the Insider program and parts of Feedback Hub and diagnostics. `priv.telemetry`=1 is the supported route. |
| `svc.dmwappush` | dmwappushservice (Start 3 = Manual, trigger-started; verified) | **DO-NOT-INCLUDE** | Needed for MDM/Intune enrollment and WAP push. Already idle when unused. |
| `svc.sysmain` | SysMain / Prefetch (`…\Memory Management\PrefetchParameters\EnablePrefetcher`=3, verified) | **DO-NOT-INCLUDE**. At most an ADVANCED troubleshooting toggle for HDD systems at 100% disk. | On SSDs Windows already skips boot defrag. Prefetch traces still speed up app and game launch, and standby-list preloading uses free RAM that is reclaimable instantly. **Stopping SysMain also disables memory compression.** |
| `svc.wsearch` | Windows Search (WSearch, Automatic) | Disable = **ADVANCED**. Tuning = SAFE-OPTIONAL: Classic mode, exclude game libraries, and on laptops `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search\PreventIndexOnBattery`=1. | Disabling breaks classic Outlook search, makes Explorer content search a slow scan and reduces Start file results. The indexer already backs off while the user is active, so the gain is minimal after the initial index. |
| `mem.pagefile` | Page file (`PagingFiles` = `?:\pagefile.sys`, system-managed; verified) | **DO-NOT-INCLUDE** disabling or shrinking. Offer only a "reset to System managed" repair: `Win32_ComputerSystem.AutomaticManagedPagefile=$true`. | It sets the commit limit (games and browsers reserve commit and crash with "out of memory" despite free RAM) and is needed for crash dumps. Microsoft sizes it automatically. |
| `reg.clean` | Registry cleaning | **DO-NOT-INCLUDE** | Microsoft does not support registry cleaners (KB2563254 support policy). No measurable benefit; a real risk of unbootable or broken apps. Offer SFC/DISM repair instead. |
| `ui.taskbarda.bypass` | Disabling UCPD to write `TaskbarDa` or file associations | **DO-NOT-INCLUDE** | It is a security control. |

---

## 11. Profile matrix and laptop vs desktop

**Normal (D):** `ads.*` (except `ads.start.hiderec` and `ads.lang`), `priv.adid`, `sys.storagesense` (safe preset), `ui.ext`.

**Recommended (D):** everything in Normal, plus:
- `ads.lang`, `search.web`, `search.highlights`, `widgets.off` (Windows 11) / `news.off` (Windows 10), `recall` (DisableAIDataAnalysis)
- `priv.telemetry`=1 (Settings value), `priv.tailored`, `priv.activity`, `priv.feedback`, `priv.remoteassist`
- `net.do`=0, `net.wifipower` (AC only), `net.tcp-autotuning` (repair, writes only when broken), `ui.endtask`
- `sys.trim` (repair, writes only when broken and an SSD is present)
- `sys.faststartup` off, **desktops only**

**Scenario matrix (01.11, wave 3 — binding for `Tweak.DefaultIn`).** The Optimize tiles are persistent pre-selections on the same
catalog; apply is one `TweakEngine.ApplyAsync` batch (one restore point, batch undo in «Восстановление»). Advanced items are never
members. `ProfileSet.Normal` *is* «Работа и учёба» (the former «Обычный»); `Streaming` and `Efficiency` are new flags; `All` keeps
the three original sets because `ServiceCatalog` pre-selects by them (Services follows the scenario: Streaming → Gaming,
Efficiency / Custom → Recommended).

| Catalog id | Work («Работа и учёба») | Streaming («Стриминг и запись») | Efficiency («Тихий ПК» / «Ноутбук: автономность») |
|---|---|---|---|
| `ads-*`, `advertising-id-off`, `file-extensions-show`, `storage-sense-on` | ✓ (the old Normal set) | — | — |
| `bing-start-search-off`, `search-highlights-off`, `widgets-off` / `news-interests-off`, `feedback-requests-off` | ✓ | — | — |
| `delivery-optimization-local`, `tcp-autotuning-normal`, `trim-on` (repairs) | ✓ | — (review #41: no uplink gain by default) | — |
| `power-plan-balanced` / `power-plan-balanced-x3d` | ✓ | X3D only (steering) | ✓ (vendor Balanced-derived plan counts as applied, see below) |
| `fast-startup-off` (desktop) | ✓ | — | — |
| `wu-active-hours` (SAFE-OPTIONAL pre-check: no restart 08:00–02:00) | ✓ | — | — |
| `game-mode`, `gamedvr-off`, `windowed-games-optimization`, `wifi-power-saving-off` (AC) | — | ✓ | — |
| `gamedvr-capture-off` (SAFE-OPTIONAL pre-check: no second encoder, no Win+Alt+R conflicts — OBS KB) | — | ✓ | — |
| `mouse-acceleration-off`, `accessibility-hotkeys-off`, `audio-ducking` («ничего не делать») | — | ✓ | — |
| `explorer-home-private` (SAFE-OPTIONAL pre-check: recent files never go on air) | **—** (productivity feature) | ✓ | — |
| `hags` | — | **—** (OBS KB /kb/hags: capture problems; `hags-off` is a troubleshooting row, named in the tooltip) | — |
| `power-mode-best-efficiency-ac` (Windows 11, Balanced personality) | — | — | ✓ desktop only (laptop: by choice, named in the tooltip) |
| `power-mode-best-efficiency-dc`, `battery-screen-off-3min`, `battery-sleep-10min`, `search-index-on-battery-off` (DC items, SAFE-OPTIONAL pre-checks) | — | — | ✓ laptops only (AppliesTo) |

Rules:
- **Work ⊆ Recommended ∪ {`wu-active-hours`}** (the former «Normal ⊆ Recommended» invariant, changed on purpose). Work never
  pre-checks gaming items, `location-off` or `explorer-home-private`.
- **Streaming never pre-checks `hags` or `delivery-optimization-local`** and switches no plan except the X3D steering.
  `usb-selective-suspend-off` and `hags-off` are named in the tooltip only (no new UI).
- **Efficiency never pre-checks `cpu-boost-off-ac`** (tooltip suggestion only), `hibernation-off`, any DC Wi-Fi / USB / ASPM
  change, `background-apps-*` or `location-off`; every member is a power item. On a laptop or desktop whose active plan is
  Balanced-derived («AMD Ryzen Balanced», OEM Recommended) `power-plan-balanced` detects as **applied** — the OEM plan is never
  replaced (Microsoft: the power-mode slider exists on «Balanced or any plan derived from it»; replacing an OEM plan can cost
  battery life). The tile is hidden while the hardware facts are unknown or when its only applicable member is a plan switch
  (Windows 10, dual-CCD X3D): a «Тихий ПК» that makes nothing quieter is not offered. The desktop effect («тише») is unmeasured
  by Lycoris — the tooltip says «эффект зависит от производителя, измерьте».
- **Manual follow-ups** (text only, in the apply-result dialog, only for the scenario applied): Streaming → Do not disturb «во время
  игры» (CloudStore blobs are never written); Quiet PC → FPS cap in the GPU panel, fan curves in BIOS / vendor utility; Laptop →
  vendor charge limit, keep dynamic refresh rate on. No vendor names, no page references.

Everything else is opt-in. Create one restore point per batch: `Checkpoint-Computer`, after setting `SystemRestorePointCreationFrequency`=0 under `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore`; that frequency value is itself saved and reverted. Restore points are throttled to one per 24 hours otherwise.

**Detecting a laptop**
- `IsPortable` is true when either:
  - `Win32_SystemEnclosure.ChassisTypes` ∩ {8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32} is non-empty, or
  - `CallNtPowerInformation(SystemPowerCapabilities)` reports `SystemBatteriesPresent && !BatteriesAreShortTerm`.
- `Win32_Battery` alone is unreliable: a USB UPS can show up there. **Implemented (01.11):** `SystemProfile.HasBattery` = `GetPwrCapabilities` → `SystemBatteriesPresent && !BatteriesAreShortTerm` (`PowerSchemes.ReadCapabilities`, byte offsets 30/31); `Win32_Battery` is only the fallback when the call fails. `SystemProfile.IsModernStandby` comes from the same structure (`AoAc`, offset 20).
- Modern Standby: `SYSTEM_POWER_CAPABILITIES.AoAc`.
- On AC vs battery: `GetSystemPowerStatus`.

**What changes on laptops**
| Item | Desktop | Laptop |
|---|---|---|
| Fast Startup off | R-default | Optional |
| `powercfg /h off` | ADVANCED | **Blocked** |
| Wi-Fi power | AC and device setting | AC index only, device setting untouched |
| Location | Optional | Not offered by default (Find My Device, time zone) |
| Search indexing | Tuning optional | Add `PreventIndexOnBattery`=1 |
| Background apps | Unchanged | Keep "Power optimized" |
| Transparency off | Cosmetic | Mild help on iGPU |
| Power plans | Ultimate/High allowed | Not offered; keep Balanced |
| Power mode (Windows 11, Balanced personality) | AC: Best performance / Best efficiency optional | AC the same; **DC: Best efficiency optional** (`power-mode-best-efficiency-dc`), never DC toward performance |
| Screen-off / sleep timeouts | AC `screen-off-ac-10m` / `sleep-ac-30m`, only shorten | AC the same; **DC: only shorten** (`battery-screen-off-3min` / `battery-sleep-10min`) |
| USB selective suspend, PCIe ASPM | Untouched | Untouched |

**DC rule (amended 01.11, binding):** on laptops the DC (battery) side may be changed only in the saving direction — Best efficiency power mode and shorter screen/sleep timeouts — never toward performance; AC items never call the DC setters. Evidence: `PowerSetUserConfiguredDCPowerMode` is documented with the same three GUIDs as the AC function; Microsoft Support: «Best Power Efficiency to reduce power consumption by limiting background activity». Every DC item names its desktop counterpart in its NotApplicableReason (owner 01.11: every power feature must serve desktops too).

**This dev machine (read-only checks)**
- No `Win32_Battery`; `ChassisTypes` = 3 (Desktop). S0 low-power idle not supported. Hybrid sleep unavailable (hypervisor active).
- UCPD running. `AllowTelemetry`=1. Location "Deny". `HiberbootEnabled`=0. Last access = 2.
- DiagTrack Auto/Running; SysMain and WSearch Auto/Running; DoSvc running with no DO policy.
- Remote Assistance `fAllowToGetHelp`=1. Copilot app not installed.

---

## Sources

- https://support.microsoft.com/en-us/windows/experience/power-battery/learn-more-about-energy-recommendations (screen 3 or 10 min, sleep 3 or 10 min «depending on the system»)
- https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powersetuserconfigureddcpowermode
- https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/configure-power-settings (powercfg /setacvalueindex, /setdcvalueindex)
- https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information (end-of-servicing dates)

- https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai
- https://learn.microsoft.com/en-us/windows/privacy/configure-windows-diagnostic-data-in-your-organization
- https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference
- https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-behavior
- https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows
- https://support.microsoft.com/en-us/topic/microsoft-support-policy-for-the-use-of-registry-cleaning-utilities-0485f4df-9520-3691-2461-7b0fd54e8b3a
- https://oofhours.com/2025/05/02/what-is-windows-11s-new-ucpd-feature/
- https://www.elevenforum.com/t/add-or-remove-widgets-button-on-taskbar-in-windows-11.32/
- https://www.elevenforum.com/t/enable-or-disable-bing-web-suggested-search-results-in-windows-search-on-windows-11.5309/
- https://windowslatest.com/2026/06/21/tested-microsoft-just-debloated-windows-11-search-without-bing-and-its-crazy-fast
- https://petri.com/windows-11-admins-remove-copilot-policy/
- https://stealthpuppy.com/windows-10-storage-sense-intune
- https://www.elevenforum.com/t/change-folder-to-open-file-explorer-to-by-default-in-windows-11.675/
- https://pureinfotech.com/bring-back-classic-context-menu-windows-11/
- https://cloudinfra.net/enable-disable-end-task-option-in-taskbar-on-windows-11/
- https://www.majorgeeks.com/content/page/windows_booting_too_slowly_disable_startup_apps_with_these_5_methods.html
- https://www.hexacorn.com/blog/?p=5992
- https://woshub.com/memory-compression-process-high-usage-windows-10/
- https://winaero.com/windows-11-disable-background-apps
- https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-start (HideRecommendedPersonalizedSites)
- https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-experience (AllowThirdPartySuggestionsInWindowsSpotlight; Spotlight policies ❌ Pro)
- https://support.microsoft.com/en-us/windows/mobile-device-in-start-menu-21676d6a-3bc3-439a-aaa3-7463b91cda79
- https://www.elevenforum.com/t/add-or-remove-phone-link-mobile-device-on-start-menu-in-windows-11.26919/
- https://techcommunity.microsoft.com/blog/windows-itpro-blog/configuring-weather-and-more-on-the-lock-screen-in-windows/4101024
- https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-feeds
- https://www.tenforums.com/threads/tweaking-question-shellfeedstaskbarviewmode-access-denied.215088/
- https://learn.microsoft.com/en-us/answers/questions/5638954/protected-registry-keys
- https://learn.microsoft.com/en-us/windows/client-management/manage-notepad
- https://www.windowslatest.com/2025/12/07/microsoft-confirms-itll-let-you-remove-ai-actions-from-file-explorer-menu-in-windows-11/
- https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-fileexplorer (DisableGraphRecentItems)
- https://support.microsoft.com/en-us/windows/experience/fileexplorer/file-explorer-in-windows
- https://www.thewindowsclub.com/disable-recommended-sectionexplorer-home-windows
- https://www.elevenforum.com/t/file-explorer-displaying-quick-access-within-home-via-registry.21120/
- https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-filesys (DisableDeleteNotification policy)
- https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/defrag
- https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/defragment-optimize-your-data-drives-in-windows
- https://infohub.delltechnologies.com/en-au/l/dell-powerstore-microsoft-hyper-v-best-practices/trim-unmap-and-disk-space-recovery-2/
- https://learn.microsoft.com/en-us/deployedge/microsoft-edge-browser-policies/startupboostenabled
- https://learn.microsoft.com/en-us/deployedge/microsoft-edge-browser-policies/backgroundmodeenabled
- https://support.microsoft.com/en-us/edge/get-help-with-startup-boost
- https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics
- https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/tcpip-performance-known-issues
- https://learn.microsoft.com/en-us/windows/win32/fwp/wmi/nettcpipprov/msft-nettcpsetting
- https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-interface
- https://devblogs.microsoft.com/oldnewthing/20061123-14/?p=28923
- https://www.howtogeek.com/730147/windows-10-myth-dont-touch-limit-reservable-bandwidth/
- https://learn.microsoft.com/en-us/powershell/module/dnsclient/set-dnsclient