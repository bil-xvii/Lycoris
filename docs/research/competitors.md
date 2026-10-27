# MONOLITH: competitor and user-needs research

Research covers 11 products (~40 sources) plus read-only checks on the dev machine, run on 2026‑10‑26.

## 0. Summary

- **The market has three groups:**
  - **Commercial suites** (IObit, CCleaner, Wise, Ashampoo, Razer): broad feature sets, opaque about what they change, built around upsells. Several have had PUA flags or security incidents.
  - **Power-user and script tools** (WinUtil, hellzerg Optimizer, O&O ShutUp10++, BoosterX): more transparent, but aggressive, and they measure almost nothing.
  - **Specialists** (Process Lasso): narrow scope.
- **The gap.** No product combines all of these:
  1. a preview of each change with its risk class,
  2. a revert that is confirmed to work,
  3. detection of settings Windows has reset ("drift"),
  4. a before/after frametime benchmark,
  5. a read-only finder for hardware misconfiguration.
- **BoosterX exists and is aimed at the Russian market** (boosterx.org, prices in ₽). It is MONOLITH's most direct competitor.
- **Positioning:** «Измеряет. Объясняет. Откатывает.» ("Measures. Explains. Reverts.") The product should be anti-placebo by design.

---

## 1. Feature matrix

Legend: ● full · ◐ partial or limited · ○ none · ? not verified

| Feature | Razer Cortex | IObit ASC 20 + Driver Booster | CCleaner 7 | Wise Care 365 + Game Booster | WinUtil | hellzerg Optimizer | Hone | Process Lasso | Ashampoo WinOptimizer 29 | O&O ShutUp10++ | BoosterX |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Game session boost (auto on game launch, auto-restore after) | ● | ◐ | ◐ (app "sleep") | ● | ○ | ○ | ● | ● (Performance Mode) | ● | ○ | ● (GameModeX) |
| Persistent OS/performance tweaks | ◐ | ● | ○ | ● | ● | ● | ● | ○ | ● | ○ | ● (deep) |
| Privacy/telemetry settings | ○ | ◐ (browser) | ◐ (traces) | ◐ (traces) | ● | ● | ○ | ○ | ● | ● | ● |
| Removing built-in Store (APPX) apps | ○ | ? | ◐ | ○ | ● | ● | ○ | ○ | ○ | ◐ (AI apps) | ● |
| Junk cleaner | ● | ● | ● | ● | ◐ | ● | ○ | ○ | ● | ○ | ? |
| Registry cleaner | ○ | ● | ● | ● (+ defrag) | ○ | ◐ | ○ | ○ | ● | ○ | ○ |
| Driver updater | ○ | ● (third-party database) | ● (Pro) | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ◐ (MSI mode, devices) |
| App installer/updater | ○ | ● | ● | ○ | ● (winget/choco) | ● | ○ | ○ | ○ | ○ | ◐ |
| Startup manager | ◐ | ● | ● | ● | ○ | ● | ○ | ○ | ● | ○ | ● |
| Services manager | ◐ | ◐ | ○ | ● | ◐ (preset) | ◐ | ◐ (silent) | ○ | ● | ○ | ● (PRO) |
| Persistent per-process priority/affinity rules | ◐ (core isolation) | ○ | ○ | ○ | ○ | ○ | ? | ● | ◐ (Live-Tuner) | ○ | ● (ProcessX, IFEO) |
| Repair tools (SFC/DISM/Windows Update reset/network) | ○ | ◐ | ○ | ○ | ● | ◐ | ○ | ○ | ◐ | ○ | ● ("fixes") |
| Windows Update control | ○ | ○ | ○ | ○ | ● (security-only / pause) | ● (disable) | ○ | ○ | ○ | ◐ | ? |
| Restore point before changes | n/a | ● | ○ | ● | ● | ○ | ◐ | n/a | ● | ● (prompt) | ◐ (advises one) |
| Per-change undo | ● (session) | ◐ | ○ | ◐ | ● | ◐ | ◐ (complaints) | ● | ● | ● (last 10 sessions) | ● (profiles) |
| Risk label on each item | ○ | ○ | ○ | ○ | ◐ (an "Advanced" section) | ○ | ○ | ○ | ○ | ● (3 levels) | ◐ |
| Preview/diff before applying | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ○ | ◐ | ◐ | ● |
| FPS/latency measurement | ◐ (FPS counter) | ○ | ○ | ○ | ○ | ○ | ◐ (claims only) | ○ | ○ | ○ | ● (latency test, bottleneck checker) |
| Live monitoring | ◐ | ● | ○ | ● | ○ | ◐ | ○ | ● | ● | ○ | ? |
| Profile export/import, CLI | ○ | ○ | ○ | ○ | ● (`-Preset`) | ● (templates) | ○ | ● | ○ | ● (.cfg) | ● (cloud) |
| Open source | ○ | ○ | ○ | ○ | ● (MIT) | ● (GPL‑3.0, archived) | ○ | ○ | ○ | ○ | ○ |
| Business model | Free, ads + Silver rewards | Subscription, upsells | Subscription | Freemium with ads | Free | Free | $2.99/mo, ads in free tier | Free / lifetime licence | From $14 | Free + Premium | 250 ₽/mo · 750 ₽/yr · 950 ₽ lifetime |

**Notes on each product**

- **Razer Cortex**
  - Game Booster switches on when a game launches and restores settings when it closes.
  - Two modes: "disable CPU sleep" and "CPU core prioritisation". Version 9 added core isolation (non-game processes on one core) and a System Cleaner that also targets launcher junk.
  - Current extras: Booster Prime (AI per-game settings), Game Clips, deals, "Play to Earn".
  - Complaints on Razer's own forum: lower FPS, freezes, high CPU use, auto-start.
- **IObit**
  - Advanced SystemCare 20 PRO: "AI deep clean", registry cleaner, CPU saver, browser anti-tracking, software updater, Safe Folder.
  - Driver Booster ships separately. Malwarebytes detects it as `PUP.Optional.iObitDriverBooster`, citing exaggerated scan results and bundling.
- **CCleaner 7**
  - Health Check, Custom Clean, Registry, Performance Optimizer, Driver Updater (scan free, install requires Pro), Software Updater.
  - Defender flagged its free and trial installers as PUA in 2020 because they bundled Chrome, Avast and AVG.
- **Wise**
  - Care 365: PC Checkup with a 0–10 score, cleaner, registry cleaner and defrag, tune-up, privacy eraser, monitor. The UI shows ads.
  - Wise Game Booster 1.5.7: closes processes, stops services and can restore them, "system optimizer".
- **WinUtil**
  - Install (winget/choco), Tweaks (Minimal / Standard / Advanced presets), Config, Updates (default / security-only / paused), Fixes, Win11 Creator.
  - Undo works from the same panel. Launched with `irm https://christitus.com/win | iex`.
  - Its "Set Services to Manual" tweak sets some services (e.g. DiagTrack, RemoteRegistry, SharedAccess) to **Disabled**, not Manual.
- **hellzerg Optimizer**
  - 16.7 is the last release (2024‑08‑18). The repo was **archived 2026‑01‑20** and replaced by OptimizerNXT, a CLI driven by signed YAML files.
  - Includes `/disabledefender`, HPET and Windows Update toggles, and editors for hosts, DNS and startup.
- **Hone**
  - Free tier: 10 optimisations. Premium: unlimited, plus "Performance Game Mode".
  - Trustpilot complaints include boot failures and driver conflicts. One review says power settings were **not reverted after uninstall** ("took me basically an entire day to undo"), and the vendor reply confirmed it.
- **Process Lasso**
  - ProBalance, Performance Mode (Bitsum Highest Performance power plan), IdleSaver, persistent affinity and priority rules, CPU Sets, Instance Balancer, power-plan automation per process.
- **Ashampoo WinOptimizer 29**
  - One-Click Optimizer (drive, internet and registry cleaners), Live-Tuner, Game Booster (turns off services), Privacy Manager, Service and Startup Managers, Crash Analyzer.
  - Backs up automatically before registry changes.
- **O&O ShutUp10++** (3.6.1135)
  - Each setting is labelled Recommended, Limited or Not recommended.
  - Prompts for a restore point. Undo keeps the last 10 sessions. "Restore initial settings" and "factory settings" are available. `.cfg` export/import.
  - Admits that feature updates reset settings. Premium has a service that re-applies them and handles "ping-pong" conflicts.
- **BoosterX** (Russian site)
  - Lets you see current state before applying, offers a questionnaire, and uses recovery profiles; the site says profiles do not replace a backup.
  - PRO adds a "hardcore method", interrupt routing, IFEO control, deep APPX removal, cloud backups and the TweaX AI tweak generator.
  - Free tier includes MSI mode, a bufferbloat/UDP test, a PC latency test, a bottleneck checker and an NVIDIA panel.
  - Marketing claims "6.6× lower CPU load in CS2" from its own research.

---

## 2. What users want and what they complain about

### 2.1 Top complaints, with evidence

1. **Placebo.** Independent tests find average-FPS differences within noise: gHacks 2015, and GameStar's test of Razer Game Booster titled "Placebo-Effekt mit Cloud-Zwang". Forum consensus is that these tools are "snake oil on mid/high-end PCs" but can help on bloated systems. Users increasingly ask about frametime and 1% lows rather than average FPS.
2. **Broken Windows Update, Store or Defender.** Aggressive debloating causes failures that show up 3–6 months later, when a feature update fails. Removing packages that other parts of the OS expect is the usual cause. O&O's "Not recommended" class exists precisely for settings touching Update delivery, Defender and activation.
3. **Registry cleaners.** Microsoft does not support them (KB2563254) and warns damage may need an OS reinstall. Since 2016 Microsoft requires cleaners and optimizers to give detailed information about what they claim to fix. Since 2018-03-01, alarming or coercive messaging gets software classified as unwanted (the examples include exaggerated error reports, time limits, and "only our product can fix it"). Microsoft has also cited optimizers deleting Prefetch, which hurts performance.
4. **Bundling and adware.** CCleaner was flagged as PUA in 2020. Malwarebytes flags Driver Booster as PUP. Driver updaters are commonly classed as PUP because of "exaggerated scan results".
5. **Trust and security incidents.**
   - CCleaner 5.33 (2017) shipped a signed backdoor; Avast estimated 2.27 million machines affected.
   - IObit's forum was compromised in 2021 and used to send DeroHE ransomware alongside IObit-signed binaries.
   - Avast, then CCleaner's owner, paid a $16.5M FTC fine for selling browsing data.
6. **Nagging, ads and subscriptions.** Ads in Wise, Hone's free tier and Cortex. Upsell pop-ups in IObit.
7. **Resident bloat.** Cortex users report auto-start, roughly 75% CPU use and freezes, and uninstalling restored their FPS.
8. **Weakened security.**
   - Tools that turn off Defender, updates or Hyper-V Code Integrity (Microsoft's VBS memory protection, shown as Memory Integrity, HVCI).
   - Microsoft's own gaming article says Memory Integrity and Virtual Machine Platform can be turned off for gaming, but warns the device "may be vulnerable".
   - Anti-cheat now requires Secure Boot and TPM 2.0 (Battlefield 6 with Javelin; Call of Duty: Black Ops 7 and Call of Duty: Warzone with Ricochet — no other current CoD title, per support.activision.com/tpm), so "security off" tweaks can lock players out of games.
9. **Edition-blind placebo.** `HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent\DisableWindowsConsumerFeatures` (DWORD 1) only works on Enterprise and Education since 1607. `AllowTelemetry=0` is treated as 1 (Required) on Home and Pro. Tools apply both anyway.
10. **Debloat breaking AMD X3D core parking.** On dual-CCD X3D CPUs (7950X3D, 9950X3D), core parking relies on Game Bar game detection plus the 3D V‑Cache Performance Optimizer chipset driver. Removing Game Bar breaks it. The 9800X3D on the dev machine has a single CCD and is not affected.
11. **Settings drift.** Feature updates reset privacy settings, which O&O's FAQ acknowledges.
12. **Monitoring drivers.** Since 2025 Defender quarantines `WinRing0x64.sys` as `HackTool:Win32/Winring0`. The ecosystem (LibreHardwareMonitor, Fan Control, OpenRGB) is moving to the PawnIO driver, and FACEIT reportedly blocks PawnIO.
13. **Hosts-file blocking.** Hosts files that block Microsoft telemetry domains are flagged as `SettingsModifier:Win32/HostsFileHijack`.

### 2.2 What users actually ask for

- Proof that a change helped: frametime, 1% and 0.1% lows, before/after on the same scene.
- An exact list of what changes, why, and how to undo it. Open source, or at least auditable.
- Undo that works, including after uninstalling the app.
- Removing the Windows 11 annoyances: Copilot, Recall, Start menu recommendations and ads, Edge prompts, Widgets, OneDrive nags. This is the strongest real demand.
- One-click install of apps and launchers without bundled offers (Ninite/WinUtil style).
- No resident service, no account, no ads, no telemetry; a portable option.
- Settings re-applied after feature updates, with a notice.
- Compatibility with anti-cheat (Vanguard, FACEIT, Javelin, Ricochet), Game Pass and the Xbox app.

### 2.3 Hard rules derived from this

R1. Make no FPS claims without a measurement on the user's own PC.
R2. No registry "cleaning". Junk cleaning only for documented locations.
R3. Never disable Defender, Tamper Protection, Windows Update, SmartScreen, Secure Boot or Memory Integrity by default.
R4. Services: allow Manual (start on demand) only. Disabled is offered as an expert option, never for the protected list.
R5. Every tweak checks that it applies to the edition, build and hardware.
R6. No coercive UI: no scary error counts, no countdowns, no red "critical" for cosmetic issues.
R7. No bundling, no ads, no resident process unless the user opts in. Ship code-signed builds.

---

## 3. What makes MONOLITH better: prioritised features

### Must-have (v1)

**1. Declarative change engine.** Every action is a record like this:

```
id: gpu.hags | titleRu, whyRu, sources[]
risk: Safe | Low | Medium | High | Blocked
evidence: Documented | Vendor-documented | Community-tested | Debated | Placebo
appliesTo: build ≥ 22621, editions[], hardware predicate (e.g. dual-CCD X3D, hybrid GPU)
detect() → current value, including the state "absent"
apply() / revert() / verify()
reboot: none | signout | restart
reversible: true | partial | false   (APPX removal = partial: reinstall from the Store)
```

- Revert must restore an "absent" value by deleting it. It must not write a "default" value.
- For services, revert restores both `Start` and `DelayedAutostart`.
- The journal lives in `%ProgramData%\MONOLITH\journal\*.json` (ACL: Administrators and SYSTEM only). Each session also exports a `.reg` file.

**2. Plan → Preview → Apply → Verify flow.**
- Show a diff for every row, e.g. `HKCU\…\GameBar\AutoGameModeEnabled: (absent) → 1`.
- After applying, read every value back and show the result as "applied / failed / not applicable".

**3. Restore points done correctly.**
- Check that System Protection is on for C:. If it is off, ask the user before running `Enable-ComputerRestore`.
- Use `SRSetRestorePoint` with `BEGIN_SYSTEM_CHANGE`/`END_SYSTEM_CHANGE` and type `MODIFY_SETTINGS`.
- **Watch for the 24-hour throttle.** If a restore point already exists from the last 24 hours, Windows *silently* skips the new one: the call returns TRUE with the old sequence number.
  - Detect this by comparing sequence numbers.
  - Optionally set `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore\SystemRestorePointCreationFrequency` (DWORD, minutes, default 1440 when absent) to 0 for the batch, then restore the original value.
  - On the dev machine this value is absent, so the throttle is active.

**4. Undo centre.**
- A timeline of sessions. Revert one change, one session, or everything.
- Shows "partial" and "irreversible" items honestly.
- **The uninstaller offers "revert all".** This answers the Hone complaint.

**5. Drift detection.**
- A scheduled task at logon (not a service) compares the journal with live state after an OS build change.
- Shows "Windows reset 4 settings" and lets the user re-apply them.

**6. Readiness check.** Read-only, zero risk, and the highest real-world value. Each check gives pass, fail or info plus how to fix it:

| Check | Source | On the dev machine |
|---|---|---|
| Monitor refresh rate is at its maximum | `Win32_VideoController.CurrentRefreshRate`, compared with display modes | 239 Hz at 2560×1440, on the dGPU |
| RAM runs at its rated profile (XMP/EXPO) | `Win32_PhysicalMemory.ConfiguredClockSpeed` vs rating in part number | 6000 MT/s, dual channel (A2/B2) |
| Resizable BAR | `nvidia-smi -q -d MEMORY`: BAR1 total ≈ VRAM | 16384 MiB → enabled |
| PCIe link | `nvidia-smi --query-gpu=pcie.link.gen.current,pcie.link.width.current` | Gen5 x16 |
| Hybrid GPU: games run on the discrete GPU | `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`, value = exe path, REG_SZ `GpuPreference=2;` (written by Settings → Graphics; not formally documented) | Relevant: RTX 5070 Ti + AMD iGPU |
| Game Mode | `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled` (absent = on) | Absent (on) |
| Hardware-accelerated GPU scheduling (HAGS) | `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` (2 = on, 1 = off, absent = OS default) | Absent |
| VBS/HVCI status | `root\Microsoft\Windows\DeviceGuard` → `Win32_DeviceGuard` | VBS running, HVCI running. Show info only; never "fix". |
| Secure Boot and TPM for anti-cheat | `Confirm-SecureBootUEFI`, `Get-Tpm` (need admin) | — |
| Game installed on an HDD, free space, GPU driver age, power plan | `Get-PhysicalDisk`, `powercfg /getactivescheme` | 2× NVMe; "High performance" plan |
| X3D dual-CCD: Game Bar and chipset driver present | CPU model, AppX package, driver | 9800X3D is single CCD → N/A |

**7. Profiles.**
- **Обычный / Рекомендуемый / Игровой** (Normal / Recommended / Gaming), built only from Safe/Low changes with Documented or Vendor-documented evidence.
- The Gaming profile prefers **temporary per-session** changes, which revert automatically when the game exits.
- **Crash safety:** if MONOLITH crashes during a session, a stale-session marker triggers the revert at the next launch or logon.

**8. Junk cleaner limited to documented locations.**
- **Allowed:** `%TEMP%` (files older than N days), Delivery Optimization cache, Windows Update download cache (via the service-safe path), crash dumps, thumbnail cache, Recycle Bin (only with confirmation), and DISM `/StartComponentCleanup`.
- `/ResetBase` makes installed updates impossible to uninstall, so it is classed High.
- **Never cleaned by default:** Prefetch (Microsoft explicitly calls this out) and GPU/DirectX shader caches. Deleting shader caches makes games recompile shaders, which causes stutter.

**9. Startup manager compatible with Task Manager.**
- Disable entries through `…\Explorer\StartupApproved\Run` (binary value; first byte 0x02 = enabled, 0x03 = disabled, followed by a FILETIME), the same way Task Manager does. Never delete Run entries.

**10. winget catalogue.** Curated, with publisher shown and no bundling. IDs verified on the dev machine (winget v1.11.510):

| Package | winget ID |
|---|---|
| Steam | `Valve.Steam` |
| Epic Games Launcher | `EpicGames.EpicGamesLauncher` |
| EA app | `ElectronicArts.EADesktop` |
| Ubisoft Connect | `Ubisoft.Connect` |
| GOG Galaxy | `GOG.Galaxy` |
| Battle.net | `Blizzard.BattleNet` |
| Discord | `Discord.Discord` |
| MSI Afterburner | `Guru3D.Afterburner` |
| O&O ShutUp10 | `OO-Software.ShutUp10` |

**11. Repair tools** (documented Microsoft procedures):
- `sfc /scannow`
- `DISM /Online /Cleanup-Image /RestoreHealth`
- Windows Update component reset: stop `wuauserv`, `bits`, `cryptsvc`; rename `SoftwareDistribution` and `catroot2`
- `netsh winsock reset` and `netsh int ip reset`
- `wsreset`
- Re-registering the WinGet/App Installer package

**12. Services view.**
- Read-only by default, with an explanation for each service. Only "Manual" is offered.
- **Hard-blocked list:** `wuauserv`, `BITS`, `WaaSMedicSvc`, `TrustedInstaller`, `WinDefend`, `mpssvc`, `BFE`, `CryptSvc`, `RpcSs`, `DcomLaunch`, `Winmgmt`, `AppXSvc`, `ClipSVC`, `wlidsvc`, `GamingServices`, `XblAuthManager`, `vgc`/`vgk` (Riot Vanguard), `EasyAntiCheat*`, `BEService`.

**13. Trust basics.**
- Code-signed binaries. No telemetry by default; crash reports opt-in and stored locally. No account, no ads.

### Should-have (v1.x)

- **Built-in benchmark** using PresentMon (Intel, MIT licence) for frame capture.
  - Run A (baseline) and run B (after changes) on the same scene, 3 runs or more each.
  - Show average FPS, 1% and 0.1% lows, and frametime standard deviation.
  - Show **"difference within noise"** honestly when it is.
- **Process rules (Process Lasso-style)** stored as IFEO entries: `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<exe>\PerfOptions`.
  - `CpuPriorityClass` (DWORD): 1 Idle, 2 Normal, 3 High, 5 BelowNormal, 6 AboveNormal.
  - `IoPriority` (DWORD): 0–2.
  - Cap at AboveNormal; never Realtime. Risk Medium because of anti-cheat.
- **Privacy pack** that maps to O&O's "Recommended" class and uses ADMX/CSP policies where they exist. Each item shows **"no effect on your edition"** where it applies.
- **Driver centre.**
  - Lists installed versions and dates. Links only to official vendor pages (NVIDIA app, AMD Adrenalin, chipset drivers).
  - Launches DDU in Safe Mode as an external tool; never bundled.
  - Cleans old driver-store packages via `pnputil /enum-drivers`, classed Medium.
  - No third-party driver databases.
- **Monitoring dashboard without kernel drivers by default.**
  - PDH counters such as `\GPU Engine(*)\Utilization Percentage` and `\GPU Adapter Memory(*)\Dedicated Usage`.
  - NVML (`nvml.dll`) for NVIDIA temperature, clocks and power; ADLX for AMD.
  - CPU temperature via LibreHardwareMonitor + PawnIO only as an opt-in, with a FACEIT warning. **Never WinRing0.**
- Profile export/import (`.json`), plus a CLI: `monolith.exe apply profile.json --dry-run`.
- Portable mode.

### Nice-to-have

- Community profiles that are signed and reviewed (like OptimizerNXT's Hub).
- Network diagnostics, read-only: bufferbloat/latency test, DNS benchmark.
- An "explain this setting" assistant that works from the local tweak database (BoosterX's TweaX shows the demand). It must not generate new tweaks.
- Per-game launch profiles, plus an optional overlay showing FPS and frametime.

### Opt-in only, behind a High-risk dialog (or never)

| Item | Status |
|---|---|
| Turning off Memory Integrity or Virtual Machine Platform | Debated. Microsoft documents it but warns. Offer as a per-session toggle with an anti-cheat warning, never in profiles. |
| HPET or `bcdedit` timer tweaks; interrupt affinity; MSI mode | Debated, can cause boot problems. Show information only, or High risk. |
| Removing APPX packages | Partially reversible. Never Game Bar, Store, Xbox, App Installer or Gaming Services. |
| Disabling Windows Update; turning off Defender; hosts-file telemetry blocking | Blocked |
| Registry cleaning; "RAM boosters"; deleting Prefetch | Blocked (placebo or harmful) |

### UX ideas

- **Every change row shows:** title, one-sentence "why", risk bars, evidence class, a "current → new" diff in monospace, reboot icon, source links, and a revert button.
- **Status is a checklist, not a scary score:** "Готовность 7/9" (readiness 7/9) with each check listed. This complies with Microsoft's criteria against coercive messaging. No inflated "1 284 проблемы" ("1,284 problems").
- **Command palette (Ctrl+K)** to search any setting or tweak.
- **"Режим эксперта"** (expert mode) reveals Medium/High items. Default view shows Safe/Low only.
- **Before/after report card** after a benchmark, exportable as PNG or Markdown.

---

## 4. UI and branding: black and white, adult, aggressive but professional

### Principles

- Strict monochrome, square geometry, 1px hairlines, data-dense, no gradients, no "gamer" RGB.
- **References:**
  - Vercel Geist: grey scale, near-black backgrounds.
  - IBM Carbon g100: dark theme; status indicator pattern combining shape and colour.
  - Linear and Raycast: density, command palette.
  - Teenage Engineering and Nothing OS: utilitarian monospace capitals, dot-matrix numerals.
  - Dieter Rams' "as little design as possible".
- **Brand mark:** a vertical slab in the 1:4:9 ratio of the monolith from *2001: A Space Odyssey*.

### Typography (all need Cyrillic, because the UI is Russian)

| Role | Font | Licence | Cyrillic | Notes |
|---|---|---|---|---|
| UI text | **Inter** (or **Geist**) | SIL OFL 1.1 | Yes (Geist 1.7.0 redesigned its Cyrillic) | Use `Typography.NumeralAlignment="Tabular"` for numbers |
| Numbers, paths, diffs | **JetBrains Mono** / **Geist Mono** | OFL 1.1 | Yes | `Typography.SlashedZero="True"` |
| Display (logo, large metrics) | **Unbounded** (wide, aggressive) or **Martian Mono** (wide monospace; Cyrillic added 2023) | OFL 1.1 | Yes | Use only at 24px and above |
| Native Russian alternative | **Golos Text** / **Onest** | OFL 1.1 | Yes | Tuned for Cyrillic |
| Icons | **Lucide** (ISC) / **Phosphor** (MIT), embedded as paths | — | — | Segoe Fluent Icons is missing on Windows 10 |

**WPF notes:**
- Ship **static TTFs**, not variable fonts; WPF has no API for variable font axes.
- Embed fonts as `Resource` and reference them as `pack://application:,,,/Assets/Fonts/#Inter`.
- **WPF has no letter-spacing property**, so labels in capitals need fonts that are already wide (mono or Unbounded). Russian text runs about 15–25% longer than English: design buttons with flexible widths.

### Colour tokens (dark mode is the default; light mode inverts)

| Token | Dark | Light | Contrast on background |
|---|---|---|---|
| bg | `#0A0A0A` | `#FAFAFA` | — |
| surface / surface‑2 | `#111111` / `#171717` | `#FFFFFF` / `#F2F2F2` | — |
| hairline (decorative) | `#262626` | `#E5E5E5` | 1.3:1 (decoration only) |
| control outline (meaningful) | `#6B6B6B` | `#8F8F8F` | 3.7:1 (passes WCAG 1.4.11; minimum `#5E5E5E`) |
| text‑muted | `#8A8A8A` | `#666666` | 5.7:1 / 5.5:1 |
| text‑secondary | `#A1A1A1` | `#4D4D4D` | 7.7:1 |
| text | `#EDEDED` | `#0A0A0A` | 16.9:1 / 19:1 |
| primary action | white fill, black text (inverted) | black fill, white text | — |

### Spacing and type scale

- Base unit 4px. Spacing scale: 4 / 8 / 12 / 16 / 24 / 32 / 48 / 64. Panel padding 24px.
- Row height 40px (32px in dense mode). Corner radius 0–2px.
- Type sizes: 11 (monospace capital labels) / 12 / 14 (body) / 16 / 20 / 24 / 32 / 48 / 72 (main metric).
- Line height 1.4 for body text, 1.1 for display text.

### Showing status without colour (WCAG 1.4.1: use two visual channels)

| State | Shape | Text | Extra channel |
|---|---|---|---|
| Applied | ■ filled square | ПРИМЕНЕНО (applied) | — |
| Not applied | □ outline | — | — |
| Pending (preview) | dashed outline | БУДЕТ ИЗМЕНЕНО (will change) | — |
| Drifted (Windows reset it) | ◧ half-filled / diagonal hatching | СБРОШЕНО WINDOWS (reset by Windows) | — |
| Risk | ▮▯▯ / ▮▮▯ / ▮▮▮ | НИЗКИЙ / СРЕДНИЙ / ВЫСОКИЙ (low / medium / high) | High risk = inverted (white) block |
| Error | ✕ | — | Inverted row |
| Warning | △ outline | — | — |
| OK | ✓ | — | — |
| Charts | — | — | Series told apart by line style (solid / dashed / dotted) and hatching; thresholds as dashed lines |

- No blinking (WCAG 2.3).
- An optional single accent colour (e.g. signal red) used *only* for destructive confirmations; strict monochrome stays the default.

### Layout

- **Navigation rail** on the left (64px collapsed, 220px expanded): Состояние / Профили / Твики / Очистка / Службы / Драйверы / Установка / Игры / Мониторинг / Откат (Status / Profiles / Tweaks / Cleanup / Services / Drivers / Install / Games / Monitoring / Undo).
- **Main pane** shows the change list.
- **Inspector on the right** for the selected change: why it matters, risk, diff, registry path, sources, revert.
- **Bottom action bar:** "N изменений · M требуют перезагрузки · [Предпросмотр] [Применить]" ("N changes · M need a restart · [Preview] [Apply]").
- **Dashboard:** large tabular numbers plus 1px sparklines. No speedometers.

### Window chrome (dwmapi)

| Attribute | ID | Use |
|---|---|---|
| `DWMWA_USE_IMMERSIVE_DARK_MODE` | 20 | Dark title bar |
| `DWMWA_WINDOW_CORNER_PREFERENCE` | 33 | `DWMWCP_DONOTROUND` = 1 for a square window |
| `DWMWA_BORDER_COLOR` | 34 | `#262626` |
| `DWMWA_CAPTION_COLOR` | 35 | Caption background |

Also:
- `UseLayoutRounding="True"` and `SnapsToDevicePixels` for crisp 1px lines.
- `TextOptions.TextFormattingMode="Display"` for text at 14px and below.
- Per-monitor DPI awareness (PerMonitorV2) in `app.manifest`.

### Tone of voice

Short, imperative Russian. No exclamation marks, no fear wording. Use «» quotes and a non-breaking space before units ("16 ГБ"). Format numbers with `ru-RU` culture.

---

## Sources

- BoosterX: https://boosterx.org/ · https://boosterx.org/pro
- Hone: https://hone.gg/premium · https://www.trustpilot.com/review/hone.gg?page=6
- Razer Cortex: https://www.razer.com/cortex/boost · https://www.razer.com/newsroom/product-news/media-alert-razer-improves-leading-game-booster-with-release-of-razer-cortex-9-0 · https://insider.razer.com/razer-support-45/razer-cortex-causes-pc-to-freeze-slow-down-22074 · https://www.gamestar.de/artikel/razer-game-booster-im-praxis-test-placebo-effekt-mit-cloud-zwang,3008650,seite2.html
- hellzerg Optimizer: https://github.com/hellzerg/optimizer · https://github.com/hellzerg/optimizerNXT
- WinUtil: https://github.com/ChrisTitusTech/winutil · https://winutil.christitus.com/
- Process Lasso: https://bitsum.com/processlasso/
- O&O ShutUp10: https://www.oo-software.com/en/shutup10 · https://manuals.oo-software.com/ooshutup10/en/docs/faq
- Wise: https://www.wisecleaner.com/wise-game-booster.html · https://www.techradar.com/reviews/wise-care-365
- IObit: https://www.iobit.com/en/advancedsystemcarepro.php · https://www.malwarebytes.com/blog/detections/pup-optional-iobitdriverbooster · https://www.bleepingcomputer.com/news/security/iobit-forums-hacked-in-widespread-derohe-ransomware-attack/
- Ashampoo: https://www.ashampoo.com/en-us/winoptimizer
- CCleaner: https://www.ghacks.net/?p=163505 · https://cyberscoop.com/ccleaner-malware-avast-cisco-talos/
- Avast FTC fine: https://therecord.media/avast-fine-ftc-alleged-browser-data-sales
- Microsoft registry-cleaner policy: https://support.microsoft.com/kb/2563254
- Microsoft unwanted-software criteria: https://liliputing.com/2018/01/windows-defender-will-start-treating-scareware-cleaner-optimizer-apps-malware.html · https://windowscentral.com/windows-defender-will-soon-set-its-sights-coercive-cleaner-software
- SRSetRestorePoint: https://learn.microsoft.com/en-us/windows/win32/api/srrestoreptapi/nf-srrestoreptapi-srsetrestorepointw
- Microsoft gaming performance options: https://www.elevenforum.com/t/options-to-optimize-gaming-performance-in-windows-11.9492/
- Debloat risks: https://maketecheasier.com/why-windows-debloat-scripts-do-more-harm-than-good · https://github.com/Raphire/Win11Debloat/wiki
- Consumer-features policy (edition limits): https://www.ghacks.net/?p=123597 · https://learn.microsoft.com/en-us/archive/technet-wiki/51837.windows-10-how-to-turn-off-microsoft-consumer-experiences
- Hosts-file detection: https://www.deskmodder.de/blog/2020/08/04/hosts-datei-mit-blockierten-microsoft-links-werden-vom-defender-als-win32-hostsfilehijack-angezeigt
- Anti-cheat requirements: https://www.heise.de/en/news/Anti-Cheat-Battlefield-6-and-Call-of-Duty-Black-Ops-7-need-Secure-Boot-10512517.html
- WinRing0 / PawnIO: https://www.neowin.net/news/windows-1110-is-flagging-winring0-on-your-pc-monitoring-fan-control-apps-heres-why/ · https://gitlab.com/CalcProgrammer1/OpenRGB/-/merge_requests/2885
- AMD X3D and Game Bar: https://www.notebookcheck.net/AMD-Ryzen-9-9950X3D-Review-Ryzen-7-9800X3D-levels-of-gaming-performance-and-power-efficiency-for-50-markup-over-the-Ryzen-9-9950X.976049.0.html
- Older booster tests: https://www.ghacks.net/2015/05/23/do-game-boosters-improve-pc-gaming-performance/ · https://steamcommunity.com/discussions/forum/0/1732089092456394212/?l=english
- Carbon status pattern: https://www.carbondesignsystem.com/patterns/status-indicator-pattern
- Fonts: https://github.com/vercel/geist-font/releases · https://fonts.google.com/specimen/Martian+Mono/about