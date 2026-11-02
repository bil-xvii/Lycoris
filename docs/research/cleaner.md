# Lycoris: safe junk cleaner design

## 0. Facts checked on the dev machine (build 26300, read-only)

- **cleanmgr is present** (`%SystemRoot%\System32\cleanmgr.exe`). The scheduled task `\Microsoft\Windows\DiskCleanup\SilentCleanup` runs `cleanmgr.exe /autocleanstoragesense /d %systemdrive%`. The `\Microsoft\Windows\Servicing\StartComponentCleanup` and `\Microsoft\Windows\DiskFootprint\StorageSense` tasks both exist.
- **The built-in `Temporary Files` handler** (`VolumeCaches\Temporary Files`, CLSID `{0B2C35D2-C8BC-4470-BFC0-D6BB1235E5CE}`) has `LastAccess=7` and `Folder=` `%TEMP%`, `C:\WINDOWS\Temp`, `C:\WINDOWS\Logs`, `C:\WINDOWS\System32\LogFiles`, `C:\WINDOWS\SystemTemp`, `%LOCALAPPDATA%\Microsoft\Windows\TaskManager\LiveKernelDumps` and `C:\ProgramData\Microsoft\WDF`. Windows' own default age for temp files is therefore **7 days**.
- **There are 31 VolumeCaches handlers.** The names used below are verified: `Delivery Optimization Files`, `D3D Shader Cache` `{D8D133CD-3F26-402F-86DA-90B710751C2C}`, `Thumbnail Cache` `{889900c3-59f3-4c2f-ae21-a409ea01e605}`, `Previous Installations` and `Windows ESD installation files` (both setup handler `{84e04a55-2d42-4909-86e3-62fd11483e8b}`), `Update Cleanup` `{606B3777-…}`, `Windows Error Reporting Files`, `System error memory dump files`, `System error minidump files`, `Setup Log Files` (`setup*.log|setup*.old|setuplog.txt|winnt32.log`, LastAccess 30), `Device Driver Packages`, `User file versions`, `DownloadsFolder`, `Recycle Bin`.
- **Delivery Optimization:**
  - `Get-DOConfig` returns `WorkingDirectory = C:\WINDOWS\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache`. A non-admin `Test-Path` on that folder fails with **Access denied**.
  - `Get-DeliveryOptimizationPerfSnap` returns `CacheSizeBytes = 3 051 105 143`, without admin.
  - Cmdlet syntax: `Delete-DeliveryOptimizationCache [[-FileId] <String>] [-IncludePinnedFiles] [-Force]`.
- **Sizes and states on this machine:**
  - `SoftwareDistribution\Download` = **1.89 GB**; `wuauserv` is Stopped (Manual).
  - Prefetch is `EnablePrefetcher=3` under `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters`.
  - CrashControl: `CrashDumpEnabled=3` (small dump), `DumpFile=%SystemRoot%\MEMORY.DMP`, `MinidumpDir=%SystemRoot%\Minidump`.
- **GPU caches that exist here:**
  - `%LOCALAPPDATA%\D3DSCache` (hashed per-app subfolders)
  - `%LOCALAPPDATA%\NVIDIA\DXCache\*.nvph`
  - `%USERPROFILE%\AppData\LocalLow\NVIDIA\DXCache`
  - `%LOCALAPPDATA%\AMD\DxCache` (iGPU)
- **Edge profile layout:**
  - Edge `User Data` has `Local State` → `profile.info_cache` = {`Default`}.
  - At the root: `ShaderCache`, `GrShaderCache`, `GPUPersistentCache`.
  - In the profile: `Cache\Cache_Data`, `Code Cache`, `GPUCache`, `DawnGraphiteCache`, `DawnWebGPUCache`, `Service Worker\{CacheStorage,ScriptCache,Database}`.
  - While Edge runs (14 processes), `User Data\lockfile` exists and is held.
- **Explorer folder:** `%LOCALAPPDATA%\Microsoft\Windows\Explorer` holds `thumbcache_*.db` and `iconcache_*.db`, 9.5 MB in total, so the gain is negligible.
- **SetupAPI:** `setupapi.offline.20240401_072605.log` is a rotated 4.9 MB archive. The active logs are `setupapi.dev.log`, `setupapi.setup.log` and `setupapi.offline.log`.

## 1. Safety tiers

| Tier | Meaning | UI |
|---|---|---|
| **T0 ✔** | Data that rebuilds itself, with no functional side effect. Cleaned only when not locked and old enough. | Checked by default in "Обычный" (Normal) and "Рекомендуемый" (Recommended) |
| **T1 ☐** | Data that rebuilds, but deleting it costs performance (shader recompiles, slower first page loads) or loses diagnostics. | Shown with its size, unchecked by default |
| **T2 ⚠** | Advanced or repair actions. They touch services, are irreversible (no rollback or uninstall afterwards), or touch user data. | Separate "Расширенное/Ремонт" (Advanced/Repair) tab. Requires an explicit confirmation dialog that states the consequence. |
| **T3 ⛔** | Never cleaned. Hard-blocked, with an explanation tooltip. | Shown greyed out as "почему нет" (why not) |

Gaming-profile rule: the "Игровой" (Gaming) profile **must never** add shader caches. A cleaner that wipes them causes exactly the stutter a game booster is supposed to prevent.

Reversibility: deleting cache files can't be undone, and System Restore does **not** cover caches, temp folders or user files. Only T0 runs without a prompt. For T1 diagnostic items (dumps, old logs), offer **quarantine**: rename the files on the same volume to `%SystemDrive%\MONOLITH\Quarantine\<runId>\…` with an admin-only ACL, purge after 7 days, and allow "Восстановить" (Restore). Dumps contain memory secrets, so the ACL matters.

## 2. Global rules for every location

1. **Age filter.** Use `max(LastWriteTimeUtc, CreationTimeUtc) < now − MinAge`. Never use LastAccess: NTFS last-access updates are often disabled or lazy.
2. **In-use files.** If a file is locked (sharing violation `0x80070020`, lock violation `0x80070021`), skip it and count it. Never kill processes. Never schedule delete-on-reboot by default: `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` writes `PendingFileRenameOperations`, which makes installers and Windows Update see a "reboot pending" state.
3. **App-owned caches** need the owner closed. Check with a lock probe, not a process name (see §4).
4. **Privilege split.** User-scope locations are cleaned by the **unelevated** UI process. Machine-scope locations are cleaned by an elevated helper.
   - When a standard user elevates with another account's credentials, an elevated process's `%LOCALAPPDATA%` points at the wrong profile.
   - Elevated deletes inside user-writable folders are a classic junction or symlink privilege-escalation vector.
5. **Process priority.** The cleaning worker calls `SetPriorityClass(hProc, PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000)` for low I/O and CPU priority. Cleaning never runs during an active Game Boost session.
6. **Postpone the whole system group** when any servicing indicator is present:
   - Key `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired`
   - Key `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending`
   - `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations` (REG_MULTI_SZ)
   - The `Global\_MSIExecute` mutex (an MSI install is running)
   - A running `TiWorker.exe` or `TrustedInstaller`

## 3. Catalog

### A. Temp

| ID | RU / EN | Path · pattern | Min age | Locks / prerequisites | Admin | Side effects | Tier |
|---|---|---|---|---|---|---|---|
| `temp.user` | Временные файлы пользователя / User Temp | `GetTempPath2W()` (normally `%LOCALAPPDATA%\Temp`) `\**\*` | 24 h (7 d in "conservative" mode, same as Windows) | Skip locked files. Skip any file referenced in `HKCU\…\RunOnce` or in `PendingFileRenameOperations`. Postpone if `_MSIExecute` is held. | No (must run unelevated) | Installer resume data is lost | **T0 ✔** |
| `temp.windows` | Временные файлы Windows / Windows Temp | `%SystemRoot%\Temp\**\*` | 24 h | Same as above, plus servicing indicators (§2.6). Users can create files here, so use handle-based deletion (§5). | Yes | None | **T0 ✔** |
| `temp.systemtemp` | Временная папка SYSTEM / SystemTemp | `%SystemRoot%\SystemTemp\**\*`, which is SYSTEM's temp from `GetTempPath2` on Win11 and updated Win10 | 7 d | ACL is SYSTEM + Administrators only. Skip "access denied"; **never take ownership**. Never delete the folder itself. | Yes | None | T0 ✔ |

**Prefetch — T3 ⛔.**
- Path: `%SystemRoot%\Prefetch\*.pf`, `Layout.ini`, `ReadyBoot\`.
- The folder maintains itself and caps its own entry count.
- Emptying it makes boot and app launches slower until the traces are relearned. The space gained is only tens of MB.
- It is also forensic evidence. "Clean Prefetch" is a debunked placebo that has real side effects.

### B. Windows Update / servicing

| ID | RU / EN | Path · method | Age | Prerequisites | Admin | Side effects | Tier |
|---|---|---|---|---|---|---|---|
| `do.cache` | Кэш оптимизации доставки / Delivery Optimization cache | `Delete-DeliveryOptimizationCache -Force`. Without `-IncludePinnedFiles`, pinned files are kept. Get the size from `Get-DeliveryOptimizationPerfSnap`.`CacheSizeBytes`. **Do not delete files directly**: the folder is owned by the service (NetworkService profile). | n/a | None. Alternative: cleanmgr handler `Delivery Optimization Files`. | Yes | LAN peers can't fetch these files from this PC | **T0 ✔** |
| `wu.download` | Загрузки Центра обновления / WU download cache | `%SystemRoot%\SoftwareDistribution\Download\**\*` | 10 d | **Only if** `wuauserv` is already stopped and no servicing indicator (§2.6) is present. Otherwise show "WU активен — позже" (WU is active, try later). **Do not stop wuauserv or BITS in normal cleaning.** BITS carries other jobs, wuauserv is trigger-start and restarts itself, and deleting a staged payload breaks a pending install, which then downloads again. Stopping `bits`, `wuauserv` and `cryptsvc` belongs only to the Repair tool "Сброс компонентов WU" (Reset WU components), following Microsoft's documented reset sequence: rename `Download` and `DataStore` to `.bak` (reversible), then restart the services. | Yes | Updates download again | T1 ☐ |
| `wu.cbs` | Очистка хранилища компонентов / Component store cleanup | Gentlest: `schtasks /Run /TN "\Microsoft\Windows\Servicing\StartComponentCleanup"` (30-day grace period, 1 h timeout). Manual: `Dism.exe /Online /English /Cleanup-Image /AnalyzeComponentStore`, then, only if it reports `Component Store Cleanup Recommended : Yes`, run `Dism.exe /Online /English /Cleanup-Image /StartComponentCleanup`. | n/a | Use `/English` so the output can be parsed. Takes minutes to hours of heavy disk I/O, so never during a game. | Yes | **Safe but irreversible**: superseded component versions are removed at once, without the 30-day grace period | T2 ⚠ |
| `wu.resetbase` | ResetBase | `… /StartComponentCleanup /ResetBase` | n/a | Expert mode, double confirmation | Yes | **Irreversible.** "All existing update packages can't be uninstalled" afterwards. Never part of any profile. | T2 ⚠ (expert) |
| `wu.cleanup` | Очистка обновлений Windows / Windows Update Cleanup | cleanmgr handler `Update Cleanup` | n/a | 24H2 has a known reporting bug that shows about 8.63 GB that can't be reclaimed (checkpoint cumulative updates). **Never promise that number in the UI.** | Yes | Same as StartComponentCleanup | T2 ⚠ |

**Never delete WinSxS, `Windows\Installer` or the DriverStore manually (T3 ⛔).** Microsoft warns that deleting WinSxS files "may severely damage your system".

### C. Logs

| ID | RU / EN | Path · pattern | Age | Notes | Admin | Tier |
|---|---|---|---|---|---|---|
| `logs.cbs` | Архивы логов CBS/DISM / CBS/DISM log archives | `%SystemRoot%\Logs\CBS\CbsPersist_*.{log,cab}` and `%SystemRoot%\Logs\DISM\dism.log*` (only when larger than 50 MB) | 30 d | **Never `CBS.log`**: TrustedInstaller has it open. Losing these logs loses SFC/DISM history. | Yes | T1 ☐ |
| `logs.windows` | Журналы Windows / Windows\Logs | **Allow-list only:** `NetSetup`, `MoSetup`, `waasmedic`, `WindowsUpdate\*.etl` (older than 30 d) | 30 d | **Exclude `MeasuredBoot`** (TCG logs used for attestation), `WinREAgent` and `CBS\CBS.log`. Windows' own handler already sweeps `Windows\Logs` after 7 days. | Yes | T1 ☐ |
| `logs.setupapi` | Архивы SetupAPI / SetupAPI archives | `%SystemRoot%\INF\setupapi.*.????????_??????.log` (rotated archives only) | 30 d | Never the active `setupapi.dev.log`, `.setup.log` or `.app.log`: they are needed to troubleshoot drivers | Yes | T1 ☐ |
| `logs.panther` | Журналы обновления ОС / Upgrade logs (Panther) | `%SystemRoot%\Panther`, `$Windows.~BT\Sources\Panther` | n/a | **Only through the cleanmgr handler `Windows Upgrade Log Files`.** These logs are needed to diagnose a failed feature upgrade. | Yes | T2 ⚠ |
| `logs.setup` | Логи установки / Setup log files | cleanmgr handler `Setup Log Files` | 30 d (handler) | Small gain | Yes | T1 ☐ |

### D. Diagnostics

| ID | RU / EN | Path · pattern | Age | Notes | Admin | Tier |
|---|---|---|---|---|---|---|
| `wer.machine` | Отчёты об ошибках (система) / WER (machine) | `%ProgramData%\Microsoft\Windows\WER\{ReportArchive,ReportQueue,Temp}\**\*` | 7 d | Archive = reports already sent. Queue = unsent reports, which Windows drops anyway. | Yes | **T0 ✔** |
| `wer.user` | Отчёты об ошибках (пользователь) / WER (user) | `%LOCALAPPDATA%\Microsoft\Windows\WER\{ReportArchive,ReportQueue}` | 7 d | Reliability Monitor loses its "technical details" | No | **T0 ✔** |
| `dump.memory` | Полный дамп памяти / MEMORY.DMP | Value of `HKLM\SYSTEM\CurrentControlSet\Control\CrashControl\DumpFile` (REG_EXPAND_SZ, default `%SystemRoot%\MEMORY.DMP`) | 14 d | Needed to analyse BSODs. Can be several GB and holds RAM contents. Windows client already discards it under low disk space unless `AlwaysKeepMemoryDump=1`. Offer quarantine. | Yes | T1 ☐ |
| `dump.mini` | Мини-дампы / Minidumps | `MinidumpDir` (default `%SystemRoot%\Minidump\*.dmp`) | 30 d; always keep the newest 5 | Small. These are BSOD evidence. | Yes | T1 ☐ |
| `dump.livekernel` | Live kernel reports | `%SystemRoot%\LiveKernelReports\**\*.dmp` (`WATCHDOG\` = GPU TDRs, `USBHUB3\`, `NDIS\`) and `%LOCALAPPDATA%\Microsoft\Windows\TaskManager\LiveKernelDumps` | 30 d | Often GBs. **This is the evidence for RTX 50-series TDR problems**, so warn explicitly. | Yes | T1 ☐ |
| `dump.user` | Дампы приложений / App crash dumps | `%LOCALAPPDATA%\CrashDumps\*.dmp` (WER LocalDumps default) | 30 d | n/a | No | T1 ☐ |
| `chkdsk.found` | Фрагменты CHKDSK / Old chkdsk files | handler `Old ChkDsk Files` (`?:\FOUND.000–009\*.CHK`) | n/a | May contain fragments of recovered user data | Yes | T2 ⚠ |

### E. Shell caches

| ID | RU / EN | Path | Method | Side effects | Tier |
|---|---|---|---|---|---|
| `shell.inetcache` | Кэш WinINet / Internet cache | `%LOCALAPPDATA%\Microsoft\Windows\INetCache\**\*`, **excluding `Content.Outlook`** (opened Outlook attachments, possibly with unsaved edits) | Own engine, 7 d, or the cleanmgr handler `Internet Cache Files` | Legacy WebBrowser/IE-mode content loads again | **T0 ✔** |
| `shell.thumbcache` | Кэш эскизов / Thumbnail cache | `%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache_*.db` | Only through the cleanmgr handler `Thumbnail Cache`. Explorer, dllhost and the indexer lock these files, and **never kill explorer**. | CPU and disk burst while thumbnails regenerate. Gain is tiny (9.5 MB here). | T1 ☐ |
| `shell.iconcache` | Кэш значков / Icon cache | `%LOCALAPPDATA%\IconCache.db` and `…\Explorer\iconcache_*.db` | **Repair tool only** ("Исправить значки", Fix icons): first `ie4uinit.exe -show`; if that isn't enough, restart Explorer, delete the files and sign out | Blank icons until the cache rebuilds | T2 ⚠ |
| `shell.fontcache` | Кэш шрифтов / Font cache | `%SystemRoot%\ServiceProfiles\LocalService\AppData\Local\FontCache\*.dat` and `%SystemRoot%\System32\FNTCACHE.DAT` | **Repair tool only.** Stop `FontCache` (and `FontCache3.0.0.0` if present), delete, start again, recommend a reboot. Store the services' original states. | Slower font enumeration after the next boot | T2 ⚠ |

### F. GPU shader caches (never T0, never in the Gaming profile)

| ID | Path | Official alternative | Side effects | Tier |
|---|---|---|---|---|
| `gpu.d3d` | `%LOCALAPPDATA%\D3DSCache\*` (DirectX Shader Cache) | cleanmgr handler `D3D Shader Cache` | Every game recompiles its shaders, so the first launch and first hours stutter (DX12 and UE titles are worst) | T1 ☐ (offer only after a driver update or when artifacts appear) |
| `gpu.nvidia` | `%LOCALAPPDATA%\NVIDIA\{DXCache,GLCache}`, `%LOCALAPPDATA%Low\NVIDIA\DXCache`, `%LOCALAPPDATA%Low\NVIDIA\PerDriverVersion\{DXCache,GLCache}` (driver 545+ era, layout varies by driver), `%APPDATA%\NVIDIA\ComputeCache` (CUDA JIT), legacy `%ProgramData%\NVIDIA Corporation\NV_Cache`. Resolve LocalLow with `SHGetKnownFolderPath(FOLDERID_LocalAppDataLow {A520A1A4-1780-4FF6-BD18-167343C5AF16})`. | NVCP / NVIDIA App "Shader Cache Size". A clean driver install also wipes it. | Same as `gpu.d3d` | T1 ☐ |
| `gpu.amd` | `%LOCALAPPDATA%\AMD\{DxCache,DxcCache,GLCache,VkCache}` | Adrenalin → Settings → Graphics → Advanced → **Reset Shader Cache** (preferred) | Same as `gpu.d3d` | T1 ☐ |

Skip locked files; a running game holds them. The drivers already cap these caches' size, so they are not junk.

### G. Apps

| ID | Path | Owner must be closed (probe) | Notes | Tier |
|---|---|---|---|---|
| `steam.htmlcache` | `%LOCALAPPDATA%\Steam\htmlcache` (current client) and legacy `<SteamPath>\config\htmlcache`. Get `SteamPath` from `HKCU\Software\Valve\Steam`. | `steam.exe`, `steamwebhelper.exe` | Equivalent to Steam's own "Delete web browser data" | T1 ☐ |
| `steam.misc` | `<SteamPath>\appcache\httpcache`, `<SteamPath>\logs\*.txt` (30 d), `<SteamPath>\dumps\*.dmp` (30 d) | steam | Never `appcache\librarycache` (library artwork downloads again) | T1 ☐ |
| `steam.shader.orphan` | `<lib>\steamapps\shadercache\<appid>` **only when `appmanifest_<appid>.acf` does not exist** (game uninstalled). Read the libraries from `<SteamPath>\steamapps\libraryfolders.vdf`. | steam | Real disk reclaim with no side effect | T1 ☐ |
| `steam.shader` | Shader cache of installed games | n/a | Steam Shader Pre-Caching manages it. Deleting it means recompiling or downloading again, and stutter. | **T3 ⛔** |
| `steam.downloading` | `steamapps\downloading`, `steamapps\temp` | steam, and no queued download | Partial downloads are lost | T2 ⚠ |
| `discord.cache` | `%APPDATA%\{discord,discordptb,discordcanary}\{Cache\Cache_Data,Code Cache,GPUCache,DawnGraphiteCache,DawnWebGPUCache}` | `Discord*.exe` | **Never `Local Storage`** (it holds the login token), `IndexedDB` or `settings.json` | **T0 ✔** (if Discord is closed; otherwise skip) |
| `teams.cache` | New Teams: `%LOCALAPPDATA%\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\*\{Cache,Code Cache,GPUCache}`. Classic Teams: `%APPDATA%\Microsoft\Teams\{Cache,GPUCache,Code Cache}`. | `ms-teams.exe` / `Teams.exe` | Microsoft's own procedure wipes the whole folder, which resets settings. We clear only the cache subfolders. | T1 ☐ |
| `office.wef` | `%LOCALAPPDATA%\Microsoft\Office\16.0\Wef\` (add-in cache) | Office apps | **⛔ Never `OfficeFileCache`**: it can hold unsynced edits. Use Office's own "Delete cached files" instead. | T1 ☐ |

### H. Browsers (cache only; data stores are hard-blocked)

**Chromium-family roots ("User Data"):**

| Browser | Root | Process |
|---|---|---|
| Chrome | `%LOCALAPPDATA%\Google\Chrome\User Data` | `chrome.exe` |
| Edge (+ Beta/Dev/SxS) | `%LOCALAPPDATA%\Microsoft\Edge\User Data` (`Edge Beta`, …) | `msedge.exe` |
| Brave | `%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data` | `brave.exe` |
| Yandex | `%LOCALAPPDATA%\Yandex\YandexBrowser\User Data` | `browser.exe` (generic name, so match on path) |
| Vivaldi | `%LOCALAPPDATA%\Vivaldi\User Data` | `vivaldi.exe` |
| Opera / Opera GX | Data in `%APPDATA%\Opera Software\Opera Stable` (or `Opera GX Stable`); cache in `%LOCALAPPDATA%\Opera Software\Opera Stable\Cache`. Newer builds may add a `Default\` subfolder. | `opera.exe` |

**Discovering profiles:**
- Parse `<root>\Local State` (JSON) → `profile.info_cache` keys (`Default`, `Profile 1`, `Profile 2`, …).
- Fallback: any subfolder containing a `Preferences` file.
- Skip `System Profile`. For Opera, the root itself is a profile if it contains `Preferences`.

**"Browser running" probe:** open `<root>\lockfile` with `FileShare.None`. A sharing violation means the browser is running. This is verified on Edge.
- Edge often keeps background processes (Startup boost, background extensions). Show "Закройте Edge" (Close Edge); never kill it.
- `msedgewebview2.exe` does not lock Edge profiles.

**Allow-list (T0 ✔ when the browser is closed, no age filter):**
- Per profile: `Cache\Cache_Data`, `Code Cache`, `GPUCache`, `DawnGraphiteCache`, `DawnWebGPUCache`, `Media Cache`.
- At the root: `ShaderCache`, `GrShaderCache`, `GraphiteDawnCache`, `GPUPersistentCache`.

**T1 ☐:**
- `Service Worker\CacheStorage` and `Service Worker\ScriptCache`: PWAs and offline sites break or log out.
- `Crashpad\reports`.

**⛔ Never by default:** `Cookies`, `Network\Cookies`, `History`, `Login Data*`, `Web Data`, `Local Storage`, `IndexedDB`, `Session Storage`, `Sessions`, `Preferences`, `Secure Preferences`, `Bookmarks`, `Extensions`, `Local State`. A future "privacy" mode must be a separate, explicit, per-item opt-in.

**Firefox:**
- Read `%APPDATA%\Mozilla\Firefox\profiles.ini`: `[ProfileN] Path=`, `IsRelative=1`, and `[Install…] Default=`.
- The cache lives in the **local** mirror: `%LOCALAPPDATA%\Mozilla\Firefox\Profiles\<dir>\{cache2,startupCache,thumbnails}` → T0.
- Running probe: `parent.lock` in the roaming profile directory is held exclusively.
- ⛔ `cookies.sqlite`, `places.sqlite`, `logins.json`, `key4.db`, `sessionstore*`, `storage\`.

### I. Developer caches (optional group, hidden unless the tool exists)

| ID | Command (prefer the tool's own command over deleting files) | Tier |
|---|---|---|
| `dev.nuget.http` | `dotnet nuget locals http-cache --clear` (`%LOCALAPPDATA%\NuGet\v3-cache`) and `… temp --clear` (NuGetScratch). List paths with `dotnet nuget locals all --list`. | T1 ☐ |
| `dev.nuget.global` | `… global-packages --clear` (`%USERPROFILE%\.nuget\packages`). Every restore downloads again and offline builds break. | T2 ⚠ |
| `dev.npm` | `npm cache verify` (garbage-collects); `npm cache clean --force` only as T2. Location: `%LOCALAPPDATA%\npm-cache\_cacache`. | T1 ☐ |
| `dev.other` | `pip cache purge` (`%LOCALAPPDATA%\pip\Cache`), `yarn cache clean` | T1 ☐ |

Never run these while a build is running (`dotnet`, `MSBuild`, `node` processes).

### J. User data and OS rollback

- **`recyclebin`: Корзина / Recycle Bin — T1 ☐.**
  - Use `SHQueryRecycleBinW(null, ref info)` to show the item count and size, then `SHEmptyRecycleBinW(IntPtr.Zero, null, SHERB_NOCONFIRMATION|SHERB_NOPROGRESSUI|SHERB_NOSOUND)`.
  - A null root empties every drive. Bins are per user (`X:\$Recycle.Bin\<SID>`), so call from the unelevated process.
  - Skip the call when `i64NumItems == 0`; some builds return `E_UNEXPECTED` on an empty bin.
  - Always confirm with the count and size.
  - Optional "только старше 30 дней" (only older than 30 days): enumerate the bin through the Shell, or read the `$I` metadata, then delete through `IFileOperation`.
- **`downloads`: Загрузки / Downloads — never automatic.**
  - Resolve with `SHGetKnownFolderPath(FOLDERID_Downloads {374DE290-123F-4565-9164-39C4925E467B})`, because users relocate it.
  - Show only the size and the largest or oldest files for manual selection.
  - Delete **to the Recycle Bin** (`FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)` or `IFileOperation` + `FOFX_RECYCLEONDELETE`), which makes it reversible.
  - Skip cloud placeholders (attributes `0x00400000` RECALL_ON_DATA_ACCESS or `0x00040000` RECALL_ON_OPEN).
- **`windows.old`: Предыдущая установка Windows / Previous installation — T2 ⚠, irreversible.**
  - Covers `%SystemDrive%\Windows.old`, `$Windows.~BT` and `$Windows.~WS`.
  - **Only** through Settings (`ms-settings:storagesense` → Temporary files → "Previous Windows installation(s)") or the cleanmgr handler `Previous Installations`.
  - Do not delete it yourself, because:
    - the files are owned by TrustedInstaller with special ACLs;
    - it contains legacy junctions (e.g. `Users\*\Application Data`) that a naive recursive delete can follow out of the folder;
    - rollback metadata must be removed consistently.
  - Show the rollback window with `DISM /Online /Get-OSUninstallWindow` (default 10 days). Removing the folder disables "Вернуться к предыдущей версии" (Go back).
- **`setup.esd`** (handler `Windows ESD installation files`): T2 ⚠. Once deleted, "Reset this PC" may need installation media.
- **`driver.packages`** (handler `Device Driver Packages`): T2 ⚠, and it belongs in Driver Tools. Driver rollback is lost.
- **`user.versions`** (handler `User file versions`, i.e. File History): ⛔, it is user data.

### K. Hard-blocked list (T3 ⛔)

- **`%SystemRoot%\Installer\*.msi|*.msp` and `$PatchCache$`.**
  - The cached packages are needed to repair, patch and uninstall MSI products. Losing them can force a reinstall.
  - "Orphan" detection (PatchCleaner-style) is heuristic.
  - `$PatchCache$` is the baseline cache that lets a patch be removed without source media.
  - The only supported way to limit it is the `MaxPatchCacheSize` policy (`HKLM\Software\Policies\Microsoft\Windows\Installer`, DWORD %, default 10). Lycoris doesn't touch it.
- **System areas:** `%ProgramData%\Package Cache` (Burn/VS/VC++ bundles, needed for uninstall), WinSxS, DriverStore, `System Volume Information`, `pagefile.sys`, `hiberfil.sys`, `Windows\Logs\MeasuredBoot`.
- **App data:** `%LOCALAPPDATA%\Packages\*\LocalState`, `OfficeFileCache`, `INetCache\Content.Outlook`.
- **Already listed above:** Prefetch, browser data stores, shader caches of installed games.

## 4. Pre-flight checks (per location)

```text
Analyze(location):
  resolve path (Known Folder API / registry value / profile parse)  → not found ⇒ hide
  root sanity: absolute, exists, not a drive root, not in ForbiddenRoots
               (%SystemRoot%, %SystemRoot%\System32, %ProgramFiles*, %USERPROFILE%, %ProgramData% …),
               root itself not a reparse point
  owner probe: lockfile/parent.lock/process path; servicing indicators (§2.6) for system group
  enumerate → candidates (path, size, alloc size, mtime, fileId) ; total ; count
Clean(location, manifest): re-validate each candidate, delete, tally
```

## 5. C# (.NET 10) deletion engine

```csharp
static readonly EnumerationOptions Opt = new() {
    RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0,
    ReturnSpecialDirectories = false, MatchType = MatchType.Win32, MatchCasing = MatchCasing.CaseInsensitive };
const FileAttributes Placeholder = (FileAttributes)0x00440000; // RECALL_ON_OPEN | RECALL_ON_DATA_ACCESS

IEnumerable<Candidate> Analyze(CleanTarget t, CancellationToken ct) {
    string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(t.Root));
    var r = new DirectoryInfo(root);
    if (!r.Exists || r.Attributes.HasFlag(FileAttributes.ReparsePoint) || IsForbiddenRoot(root)) yield break;
    DateTime cutoff = DateTime.UtcNow - t.MinAge;
    var stack = new Stack<DirectoryInfo>(); stack.Push(r);
    while (stack.TryPop(out var dir)) {
        ct.ThrowIfCancellationRequested();
        foreach (var e in SafeEnum(dir)) {                       // catches IO/UnauthorizedAccess per MoveNext
            if ((e.Attributes & (FileAttributes.ReparsePoint | Placeholder)) != 0) continue; // never follow, never delete
            if (e is DirectoryInfo d) { if (t.Recurse && !t.IsExcluded(d.Name)) stack.Push(d); continue; }
            var f = (FileInfo)e;
            if (!t.Matches(f.Name)) continue;
            if (Max(f.LastWriteTimeUtc, f.CreationTimeUtc) > cutoff) continue;
            if (!f.FullName.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase)) continue;
            yield return new Candidate(f.FullName, f.Length, f.LastWriteTimeUtc);
        }
    }
}
```

**Deleting a file safely (Win32 by handle).** This is required in elevated mode and recommended everywhere. It defeats the race where a directory is swapped for a junction between enumeration and deletion.

1. Open the file with `CreateFileW(path, DELETE | FILE_READ_ATTRIBUTES, FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS)`. A sharing violation means the file is in use, so skip it.
2. Check `GetFileInformationByHandleEx(FileAttributeTagInfo)`: no reparse tag allowed.
3. Check the `FileStandardInfo` link count. If `NumberOfLinks > 1`, the delete frees 0 bytes; count it as 0.
4. Call `GetFinalPathNameByHandleW` and check that the result is still under the root (case-insensitive, `\\?\` normalised). This is the real path you are about to delete.
5. Re-check the mtime and size against the manifest.
6. Delete with `SetFileInformationByHandle(h, FileDispositionInfoEx /*21*/, flags = FILE_DISPOSITION_FLAG_DELETE 0x1 | POSIX_SEMANTICS 0x2 | IGNORE_READONLY_ATTRIBUTE 0x10)`. This removes the need to clear attributes first and works on Windows 10 1809 and later. Fallback: clear `ReadOnly` + `File.Delete`.
7. Map the error codes:

   | Code | Count as |
   |---|---|
   | 32 / 33 | `InUse` |
   | 5 | `Denied` |
   | 2 / 3 | `Gone` |
   | anything else | `Error` (logged) |

8. For the UI, the Restart Manager (`RmStartSession`, `RmRegisterResources`, `RmGetList`) can name the process holding a lock ("Закройте Discord", close Discord). It is read-only: never call `RmShutdown`.

**Directories.**
- Post-order pass: remove a subdirectory only if it is now empty, is not a reparse point, is not excluded, and is older than MinAge.
- Use `Directory.Delete(p, recursive: false)`.
- **Never delete the root.** ACLs on `Windows\Temp` and `SystemTemp` are special, and apps expect the folders to exist.
- Never call `Directory.Delete(..., true)` on anything you haven't walked yourself.

**Size and reporting.**
- `Analyze` returns per-location `{count, logicalBytes, allocatedBytes}`. Get allocated size from `GetCompressedFileSizeW` or `FILE_STANDARD_INFO.AllocationSize`; it differs for compressed and sparse files.
- `Clean` returns `{freedBytes (allocated, excluding files with links > 1), deleted, inUse, denied, changed, errors, duration}`.
- As a sanity check, compare `DriveInfo.AvailableFreeSpace` before and after.
- Write a JSON log to `%ProgramData%\MONOLITH\Logs\clean-<utc>.json`. Store paths and sizes only, never file contents.

**Flow:** Analyze (dry run, a manifest that shows nothing is deleted yet) → user reviews the tiers → Clean re-validates every manifest entry. Never "analyze-and-delete" in a single pass.

## 6. Delegating to Windows' own cleaners

**cleanmgr profiles.** This is a documented mechanism, and the only correct path for Windows.old, ESD files, thumbnails and Update Cleanup.

1. Pick a private profile number, e.g. `4242` (documented range 0–9999).
2. For each chosen handler under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\<Handler>`:
   - write `StateFlags4242` (REG_DWORD) = `2` (selected);
   - write `0` to the others, or leave the value absent;
   - first record any pre-existing value in the undo journal.
3. Run `cleanmgr.exe /sagerun:4242` elevated and wait for it to exit. It enumerates all drives (`/d` is ignored) and always shows a progress window.
4. **Delete every `StateFlags4242` value afterwards**, which reverts the registry change.

Handler key names are English on every UI language. Do not use `/verylowdisk` or `/autoclean`, because we can't control what they include.

**Storage Sense.**
- Policies live in `HKLM\Software\Policies\Microsoft\Windows\StorageSense`:
  - `AllowStorageSenseGlobal`
  - `AllowStorageSenseTemporaryFilesCleanup`
  - `ConfigStorageSenseGlobalCadence` (0 = low disk, 1, 7, 30)
  - `ConfigStorageSenseRecycleBinCleanupThreshold` (0–365, default 30)
  - `ConfigStorageSenseDownloadsCleanupThreshold` (0–365, default 0 = never)
  - `ConfigStorageSenseCloudContentDehydrationThreshold`
- Writing these **locks the user's UI** (policy semantics), so **Lycoris must not write them**.
- Instead, deep-link to `ms-settings:storagepolicies` (Storage Sense), `ms-settings:storagesense` (Storage) and `ms-settings:storagerecommendations`.
- To display the current state, read `HKCU\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy`. Here `"01"=1` means on, and `"2048"=0` is observed to be the cadence. The numbered value names are **undocumented**: read only.
- Note: the SilentCleanup and StorageSense tasks may already clear temp files and the DirectX shader cache on their own schedule.

**Delivery Optimization:** use the cmdlet as described in §B, `do.cache`.

**Component store:** use the scheduled task first, DISM second, ResetBase never by default (§B).

## 7. Admin summary

| Requirement | Locations |
|---|---|
| **No admin (run unelevated)** | `temp.user`, `wer.user`, `dump.user`, `shell.inetcache`, thumbnail cache through its handler (the handler itself works unelevated), all browsers, Discord, Teams, Office, Steam (if Steam sits under Program Files, the elevated helper is needed), developer caches, Recycle Bin, Downloads |
| **Admin (elevated helper)** | `temp.windows`, `temp.systemtemp`, `do.cache`, `wu.*`, `logs.*`, `wer.machine`, `dump.memory`, `dump.mini`, `dump.livekernel`, font cache, cleanmgr `/sagerun` with system handlers, DISM, Windows.old |

## Sources

- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder
- https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cleanmgr
- https://learn.microsoft.com/en-us/troubleshoot/windows-server/backup-and-storage/automating-disk-cleanup-tool
- https://learn.microsoft.com/en-us/windows/win32/lwef/disk-cleanup
- https://learn.microsoft.com/en-us/powershell/module/deliveryoptimization/delete-deliveryoptimizationcache
- https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-storage
- https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shemptyrecyclebinw
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-gettemppath2a
- https://learn.microsoft.com/en-us/windows/deployment/update/windows-update-resources
- https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app
- https://learn.microsoft.com/en-us/windows/win32/msi/maxpatchcachesize
- https://devblogs.microsoft.com/setup/?p=1873
- https://en.wikipedia.org/wiki/Prefetcher
- https://www.theregister.com/2024/10/15/windows_11_24h2_disk_space/
- https://windowslatest.com/2024/10/10/windows-11-24h2-issue-creates-undeletable-8-63-gb-windows-update-cache
- https://github.com/Kkthnx/NvidiaShaderCleanup
- https://forums.developer.nvidia.com/t/dxcache-issues-file-path/283472