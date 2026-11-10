# Lycoris: rejected tweaks («Что Lycoris не делает и почему»)

> **Режим «Эксперт» (10.11.2026).** Часть пунктов ниже доступна эксперту как настройки `expert-…` (с предупреждением и откатом):
> power-throttling-off, global-timer-resolution, s3-override, bg-apps-global-win11, streaming-network-priority (только резерв QoS),
> global-notifications-off. Оценка «не рекомендуем» не меняется. Отключение защиты Windows и необратимые действия закрыты в обоих режимах.

Single source for the list in `src/Monolith/Tweaks/RejectedTweaks.cs`: every entry there must have a section here, and every
section here names the registry values the catalog must never write (enforced by `RejectedTweaksTests` and
`TweakCatalogTests.Rejected_research_items_are_not_in_the_catalog`). Competitor and product names are allowed in this file only;
the UI says «некоторые оптимизаторы». Classification REJECTED here means the same as DO-NOT-INCLUDE in the other research
files: not implemented, not even hidden. Where another research file already classifies the item, this file only cites that
section and adds the evidence that was checked for the explainer (01.11.2026; `learn.microsoft.com` / `support.microsoft.com`
were verified through search-engine excerpts because the cloud session cannot open those hosts).

Positioning (competitors.md §0, §2.3): «Измеряет. Объясняет. Откатывает.» Users coming from WinUtil, Hone, Razer Cortex or
Process Lasso should find their favourite tweak by name in the command palette (Ctrl+K) and read why it is missing, instead of
finding nothing.

---

## `reserved-storage-off` — Disable reserved storage → REJECTED
- **Who:** WinUtil «Disable Reserved Storage» (`config/tweaks.json`, section *Advanced Tweaks – CAUTION*, not Essential; its own
  text tells the user to re-enable it before feature updates): `DISM /Online /Set-ReservedStorageState /State:Disabled`.
- **Claim:** «frees about 7 GB».
- **Evidence:** Microsoft Support (Storage settings): «When your device is low on space, Windows will clear reserved storage so it can
  be used for other processes, like a Windows update.» DISM docs: reserved storage «increases the likelihood that Windows updates can
  be downloaded and installed». The space is therefore already given back when needed; disabling only raises the chance that a
  feature or monthly update fails for lack of space. (Verifier: no «2 % / 3 GB» figure — it is in none of the sources.)
- **Registry values:** `ShippedWithReserves` (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager`).
- **Lycoris instead:** the disk-space facts on «Панель» and the cleaner («Обслуживание → Очистка»).
- **Sources:** https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-storage-reserve?view=windows-11 ;
  https://techcommunity.microsoft.com/blog/windows-itpro-blog/managing-reserved-storage-in-windows-10-environments/1297070 ;
  https://support.microsoft.com/en-us/windows/experience/storage-filemanagement/storage-settings-in-windows ;
  https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/config/tweaks.json

## `memory-compression-off` — Disable memory compression → REJECTED (services.md §1A: SysMain DO-NOT-INCLUDE)
- **Who:** many «RAM optimizer» guides: `Disable-MMAgent -MemoryCompression`, or stopping SysMain (which also turns compression off).
- **Evidence:** Microsoft's build 10525 announcement: compressed pages take about 40 % of their size and Windows writes to the page
  file about half as often, with better responsiveness. A large «Сжатая память» / «System» working set is memory being kept
  available for other apps, not waste. Turning it off means more hard faults and disk I/O under memory pressure.
- **Registry values:** none (cmdlet / service). SysMain is not in the services catalog and `ServiceGuard` blocks it on «ВСЕ СЛУЖБЫ» («Спорная настройка»).
- **Lycoris instead:** the memory facts on «Панель» (used / available, the live meter).
- **Sources:** https://blogs.windows.com/windows-insider/2015/08/18/announcing-windows-10-insider-preview-build-10525/ ;
  https://riverar.github.io/insiderhubcontent/memory_compression.html ;
  https://learn.microsoft.com/en-us/powershell/module/mmagent/disable-mmagent ; services.md §1A (SysMain)

## `native-nvme-flags` — «native» NVMe driver (Windows Server path) on Windows 11 → REJECTED
- **Who:** March 2026 «free SSD boost» guides: FeatureManagement overrides `735209102` / `1853569164` / `156965516` under
  `HKLM\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides`, or ViVeTool ids 60786016 / 48433719, which
  load `nvmedisk.sys` on a client build.
- **Evidence:** Microsoft supports the driver on Server 2025 only and blocked the registry route in 2026 (Tom's Hardware, Neowin,
  TechPowerUp, WinBuzzer); heise reports the driver was quietly switched back. Reported breakage: Samsung Magician and WD Dashboard
  stop seeing the drive, CrystalDiskInfo loses SMART, BitLocker recovery prompts, changed disk identity for backup/imaging tools.
  HotHardware measured «basically no change» on a consumer SSD. Undocumented and unsupported on clients, so it violates «only
  documented, reversible». (Verifier: INACCESSIBLE_BOOT_DEVICE is not in the sources — not claimed.)
- **Registry values:** `735209102`, `1853569164`, `156965516`.
- **Lycoris instead:** the disk facts on «Панель» (bus, health) and vendor tools via «Драйверы».
- **Sources:** https://www.tomshardware.com/software/windows/microsoft-blocks-the-registry-hack-trick-that-unlocked-native-nvme-performance-on-windows-11 ;
  https://windowsforum.com/windows-news.4/windows-native-nvme-path-big-server-gains-with-cautious-client-hack.394885/ ;
  https://www.neowin.net/news/report-microsoft-quietly-blocks-windows-11-25h2-24h2-performance-boost-hack-for-ssds/

## `cstates-idle-disable` — Disable C-states / «processor idle» → REJECTED (gaming_tweaks.md §2 C `cpu-min-state`)
- **Who:** Razer Cortex Game Booster «Disable CPU sleep mode», BIOS «Global C-state Control = Disabled» guides, the hidden power
  setting «Processor idle disable» (`5d76a2ca-e8c0-402f-a133-2158492d58ad`).
- **Evidence:** waking from a C-state takes microseconds while a frame takes milliseconds. Boost algorithms spend the power budget of
  idle cores: AMD's PB2 FAQ lists active core count and socket power among the boost inputs; SkatterBencher measured lower
  frequency on Ryzen 7000 with Global C-state Control disabled and recommends keeping C-states on; Level1Techs and Tom's forum
  reports show lower single-core boost on Ryzen and Intel. Result: more heat and idle power, equal or lower performance.
- **Registry values:** none (power setting / BIOS).
- **Lycoris instead:** the honest ADVANCED `cpu-min-state-100` text and the power-mode items on «Оптимизация».
- **Sources:** https://www.amd.com/en/resources/support-articles/faqs/cpu-pb2.html ; https://skatterbencher.com/2022/10/26/update-on-ryzen-7000-c-state-boost-limit/ ;
  https://forum.level1techs.com/t/ryzen-and-disabling-c6-c-state-the-curse-of-c6/153167 ;
  https://forums.tomshardware.com/threads/i5-6600-disabling-c-states-in-bios-lowers-boost-clock-but-improves-performance.3501006/ ;
  https://www.razer.com/cortex/booster ; gaming_tweaks.md §2 C

## `global-timer-resolution` — permanent 0.5 ms system timer → DO-NOT-INCLUDE (gaming_tweaks.md §2 E)
- **Who:** `GlobalTimerResolutionRequests=1`; Process Lasso «System Timer Resolution»; ISLC / TimerResolution services.
- **Evidence:** since Windows 10 2004 the timer resolution is per process (randomascii «The great rule change»); Windows 11 ignores
  requests from minimized or occluded windows; games call `timeBeginPeriod(1)` themselves. Microsoft's timeBeginPeriod docs advise
  against system-wide high resolution: it raises power use.
- **Registry values:** `GlobalTimerResolutionRequests`.
- **Lycoris instead:** «Обслуживание → Ремонт и диски → Отчёт об энергоэффективности» (`powercfg /energy`) shows which process holds
  the timer.
- **Sources:** https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod ;
  https://randomascii.wordpress.com/2020/10/04/windows-timer-resolution-the-great-rule-change/ ;
  https://bitsum.com/apps/process-lasso/docs/rules/ ; gaming_tweaks.md §2 E

## `core-parking-off` — Disable core parking → DO-NOT-INCLUDE (gaming_tweaks.md §1, §2 C `core-parking-off`, §3)
- **Who:** CPMINCORES `0cc5b647-c1df-4637-891a-dec35c318583` = 100 %, «unpark» tools, Hone «Core Parking» game enhancer.
- **Evidence:** on the dev machine Balanced AC is already 100 % — but that is the AMD PPM provisioning, not a universal default.
  Forcing all cores awake breaks dual-CCD X3D cache steering (Bitsum's own X3D article) and Intel Thread Director scheduling.
- **Registry values:** none (power setting).
- **Lycoris instead:** the Dashboard game-mode check (explains why a dual-CCD X3D needs Game Mode), `power-plan-balanced-x3d` and the honest power items.
- **Sources:** https://bitsum.com/amd-x3d-ccd-affinity/ ; https://hone.gg/research/how-game-mode-improves-performance ; gaming_tweaks.md §1, §2 C

## `power-throttling-off` — Turn off Power Throttling → DO-NOT-INCLUDE (gaming_tweaks.md §2 C `power-throttling-off`, §3)
- **Who:** `HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling\PowerThrottlingOff=1`; `DisableUserPresenceQos`.
- **Evidence:** Microsoft's Quality of Service table: Low and Utility QoS (invisible windows, background services) throttle «on
  battery»; Eco QoS is an explicit tag an app sets on itself (`SetProcessInformation`, `PROCESS_POWER_THROTTLING_EXECUTION_SPEED`) and
  «always selects the most efficient CPU frequency»; a focused game window is High QoS. So the key gives the game nothing: on a desktop
  it only lets Eco-tagged background apps (browser background tabs, messengers) spend more, on a laptop it costs battery.
- **Registry values:** `PowerThrottlingOff`, `DisableUserPresenceQos`.
- **Lycoris instead:** Game Boost (session-scoped: the High performance scheme on desktops, the «Максимальная производительность» power mode on laptops / Modern Standby) and the power-mode items.
- **Sources:** https://learn.microsoft.com/en-us/windows/win32/procthread/quality-of-service ; gaming_tweaks.md §2 C, §3

## `mpo-disableoverlays` — Disable MPO via `DisableOverlays` (24H2+) → REJECTED (gaming_tweaks.md §2 A `mpo-disable`)
- **Who:** `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\DisableOverlays=1`, passed around as the 24H2 replacement for
  `OverlayTestMode=5`.
- **Evidence:** backed by a single Guru3D report; undocumented by Microsoft and NVIDIA; reported to break VRR (G-SYNC / FreeSync).
- **Registry values:** `DisableOverlays`.
- **Lycoris instead:** the existing ADVANCED `mpo-disable` keeps NVIDIA's documented key and says honestly that 24H2+ may ignore it.
- **Sources:** https://forums.guru3d.com/goto/post?id=6305178 ;
  https://learn.microsoft.com/en-us/answers/questions/3881462/how-can-i-disable-windows-mpo-in-2025-(24h2-window ; gaming_tweaks.md §2 A

## `hibernate-timer-modern-standby` — fixed hibernate timer on Modern Standby laptops → REJECTED
- **Who:** «stop the battery draining in the bag» guides: SUB_SLEEP «Hibernate after» (DC) set to 30–60 min on Modern Standby.
- **Evidence:** Microsoft Learn (display, sleep and hibernate idle timers): the hibernate timeout «is not recommended for modern
  standby systems». Adaptive hibernate already hibernates by drain budget (defaults 5 % per 12 h, 4 refreshes); a fixed timer «can
  result in the system fully draining the battery … or cut short a low-drain Modern Standby experience».
- **Registry values:** none (power setting).
- **Lycoris instead:** «Обслуживание → Ремонт и диски → Отчёты о питании и сне» (SleepStudy / battery report) shows the real drain.
- **Sources:** https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/display--sleep--and-hibernate-idle-timers ;
  https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/adaptive-hibernate

## `hibernation-off-default` — disable hibernation by default / on laptops → REJECTED (general_tweaks.md §6 `sys.hibernate`)
- **Who:** WinUtil «Hibernation – Disable» under Essential Tweaks (also on laptops; hides the menu option).
- **Evidence:** on laptops the critical-battery action and Modern Standby fall back to hibernation, so unsaved work is lost
  (DO-NOT-INCLUDE on laptops). Desktops lose hybrid sleep: Microsoft KB 920730 «You may lose data if you make hibernation unavailable
  and a power loss occurs while the hybrid sleep setting is turned on.»
- **Registry values:** none (`powercfg /h`).
- **Lycoris instead:** `hibernation-off` stays ADVANCED on desktops only, with the hybrid-sleep warning and the disk-space facts.
- **Sources:** https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/config/tweaks.json ;
  https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/disable-and-re-enable-hibernation ; general_tweaks.md §6

## `s3-override` — bring back S3 sleep instead of Modern Standby → DO-NOT-INCLUDE (gaming_tweaks.md §2 C, §3)
- **Who:** WinUtil «S3 Sleep» toggle: `HKLM\SYSTEM\CurrentControlSet\Control\Power\PlatformAoAcOverride=0`.
- **Evidence:** disables Modern Standby; firmware on Modern Standby devices often does not implement S3 properly, so sleep or resume
  fails and work is lost.
- **Registry values:** `PlatformAoAcOverride`.
- **Lycoris instead:** the «Сон и пробуждение» card (what keeps the PC awake) and «Отчёты о питании и сне» (drain per session).
- **Sources:** https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/config/tweaks.json ; gaming_tweaks.md §2 C, §3

## `svchost-split` — disable svchost splitting → DO-NOT-INCLUDE (gaming_tweaks.md §3)
- **Who:** WinUtil Advanced: `SvcHostSplitThresholdInKB` = installed RAM.
- **Evidence:** Microsoft splits services for reliability, security and diagnosability (one crash no longer takes down several
  services). Saves a few tens of MB of RAM; no measurable gaming effect. WinUtil itself warns it «can interfere with Xbox services».
- **Registry values:** `SvcHostSplitThresholdInKB`.
- **Lycoris instead:** nothing is needed; the services page explains what each service does and why some are never offered.
- **Sources:** https://learn.microsoft.com/en-us/windows/application-management/svchost-service-refactoring ;
  https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/config/tweaks.json ; gaming_tweaks.md §3

## `standby-purge-timer` — periodic standby-list purge / RAM «cleaners» → REJECTED (tech.md §5; gaming_tweaks.md §3)
- **Who:** ISLC-style timers, «RAM booster» buttons (`EmptyWorkingSet`), Razer Cortex «free up RAM».
- **Evidence:** standby pages already count as available and are reused instantly (RAMMap docs); purging forces data to be read
  from disk again, so loads slow down and can hitch. `EmptyWorkingSet` moves pages out of working sets and causes immediate page
  faults and stutter («COUNTERPRODUCTIVE», tech.md §5).
- **Registry values:** none.
- **Lycoris instead:** the one-shot, opt-in purge in a Game Boost session, with its honest text.
- **Sources:** https://learn.microsoft.com/en-us/sysinternals/downloads/rammap ; tech.md §5 ; gaming_tweaks.md §3

## `bg-apps-global-win11` — global background-app ban on Windows 11 → DO-NOT-INCLUDE (gaming_tweaks.md §2 H; general_tweaks.md §2)
- **Who:** WinUtil «Background Apps – Disable» (`GlobalUserDisabled=1`, Advanced-CAUTION); the policy `LetAppsRunInBackground=2`.
- **Evidence:** Windows 11 removed the UI for the global switch and current builds may not honour the value; the policy breaks
  notifications, alarms, Phone Link and the Xbox app. Store apps are already suspended and power-optimized. general_tweaks.md
  rates the policy ADVANCED, gaming_tweaks.md rates the global switch DO-NOT-INCLUDE — the stricter rating wins.
- **Registry values:** `LetAppsRunInBackground`. (`GlobalUserDisabled` stays in the catalog as the Windows 10-only toggle.)
- **Lycoris instead:** the per-app item `background-apps-consumer-off` and the Windows 10 toggle.
- **Sources:** gaming_tweaks.md §2 H ; general_tweaks.md §2 ; https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/config/tweaks.json

## `mitigations-off` — disable Spectre/Meltdown mitigations → DO-NOT-INCLUDE (gaming_tweaks.md §3)
- **Who:** Hone Premium «Disable Mitigations»: `FeatureSettingsOverride` / `FeatureSettingsOverrideMask`; «gaming» playbooks.
- **Evidence:** removes CPU vulnerability mitigations — a security weakening for a few percent in some CPU-bound cases. Forbidden by
  CLAUDE.md rule 3.
- **Registry values:** `FeatureSettingsOverride`, `FeatureSettingsOverrideMask`.
- **Lycoris instead:** nothing; `hvci-off` is the only security trade-off, ADVANCED with a red warning.
- **Sources:** https://support.hone.gg/hc/en-gb/articles/4758238924959-Recommended-Optimizations-to-Use ; gaming_tweaks.md §3

## `auto-scenario-switching` — auto-switching persistent settings by foreground app → REJECTED
- **Who:** Armoury Crate «Scenario Profiles», similar launcher rules: switch the power plan / power mode / Windows settings
  whenever the active app changes.
- **Evidence:** persistent changes flipping in the background create endless journal layers and drift, and break «undo one =
  exactly one». Notebookcheck documents Armoury Crate modes silently rewriting the Windows power plan.
- **Registry values:** none.
- **Lycoris instead:** Game Boost: starts on game launch, reverts on exit, one journaled power change.
- **Sources:** https://rog.asus.com/articles/guides/guide-scenario-profiles-make-games-and-apps-run-with-your-preferred-system-settings-every-time/ ;
  https://www.notebookcheck.net/These-Asus-Armoury-Crate-power-profiles-don-t-make-much-sense.427047.0.html

## `cpu-sleep-unpark` — «Disable CPU sleep» / «Enable CPU core» boosters → REJECTED (gaming_tweaks.md §1, §2 C, §3)
- **Who:** Razer Cortex «Disable CPU sleep mode» / «Enable CPU core»; unparking (CPMINCORES); Ultimate Performance or
  cpu-min-state 100 % as a default; High performance outside the games scenario; Process Lasso «Bitsum Highest Performance».
- **Evidence:** about 0–2 % average FPS on CPPC/EPP CPUs; Razer support itself tells users to deselect «Enable CPU Core» when FPS
  drops («not the recommended setting»); users report Cortex overwriting their CPU min/max. Core parking is DO-NOT-INCLUDE (breaks
  X3D CCD steering and Thread Director). (Verifier: High performance stays SAFE-OPTIONAL inside the «Игры» tile on desktops.)
- **Registry values:** none (power settings).
- **Lycoris instead:** Game Boost with one journaled plan change; `power-plan-performance` only in «Игры» on desktops.
- **Sources:** https://insider.razer.com/razer-cortex-39/razer-cortex-boost-tanks-frame-rate-52359 ;
  https://insider.razer.com/razer-cortex-39/razer-cortex-power-plan-boost-default-settings-50206 ; https://bitsum.com/amd-x3d-ccd-affinity/ ;
  gaming_tweaks.md §1, §2 C, §3

## `stripped-windows-modes` — «gaming» ISOs and playbooks without Windows protection → REJECTED (gaming_tweaks.md §2 G)
- **Who:** Ghost Spectre / «Superlite» ISOs, AtlasOS-style playbooks: removing or disabling Defender, SmartScreen, Windows Update,
  CPU mitigations, VBS/HVCI, BitLocker.
- **Evidence:** forbidden by CLAUDE.md rule 3. Unpatched systems; anti-cheat (Vanguard, FACEIT) requires VBS, TPM and Secure Boot;
  modified ISOs can carry malware (Chris Titus' AtlasOS review, Gridinsoft, Tom's Guide). VBS/HVCI costs 0–10 % and is offered only
  as ADVANCED `hvci-off`, never in a scenario.
- **Registry values:** `DisableAntiSpyware`, `DisableRealtimeMonitoring`, `EnableSmartScreen`.
- **Lycoris instead:** everything else in the catalog, with undo.
- **Sources:** https://christitus.com/atlasos-review ; https://blog.gridinsoft.com/atlasos-safety/ ;
  https://www.tomsguide.fr/windows-10-11-pourquoi-vous-devriez-absolument-eviter-linstallation-diso-personnalises/ ; gaming_tweaks.md §2 G

## `energy-saver-always-quiet` — Energy Saver «always on» as the desktop quiet mode → REJECTED
- **Who:** «Quiet PC» guides: Energy Saver threshold 100 % / plugged-in policy.
- **Evidence:** Microsoft's Battery Saver / Energy Saver design doc: it pauses Mail/Calendar sync, blocks background apps and dims the
  screen. Most of the saving comes from blocking background activity, not from lower clocks — a functional loss, not quiet.
- **Registry values:** none.
- **Lycoris instead:** `power-mode-best-efficiency-ac` (+ optional `cpu-boost-off-ac`): the documented power-mode overlay.
- **Sources:** https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/battery-saver ;
  https://www.windowslatest.com/2024/08/02/microsoft-details-windows-11-24h2s-new-energy-saver-which-replaces-battery-saver/amp/

## `battery-boosters` — background-app bans and «battery boosters» → REJECTED (general_tweaks.md §2, §11; gaming_tweaks.md §2 C, §3; tech.md §5)
- **Who:** battery «modes» in optimizers: `LetAppsRunInBackground=2`, process killers, RAM cleaners, standby purge,
  `PowerThrottlingOff`, `DisableUserPresenceQos`.
- **Evidence:** Windows already applies EcoQoS to background work on battery and suspends Store apps. The policy breaks
  notifications, alarms, Phone Link and Xbox; `EmptyWorkingSet` causes stutter; the laptop table in general_tweaks.md §11 says
  «Background apps: keep Power optimized»; `PowerThrottlingOff` only costs battery.
- **Registry values:** covered by `bg-apps-global-win11` and `power-throttling-off`.
- **Lycoris instead:** the battery report («Обслуживание → Ремонт и диски → Отчёты о питании и сне») and the per-app background item.
- **Sources:** general_tweaks.md §2, §11 ; gaming_tweaks.md §2 C, §3 ; tech.md §5

## `streaming-network-priority` — DSCP priority and «network boosters» for streaming → REJECTED (network_stack.md #4, #6, #7, #9–12)
- **Who:** QoS/DSCP policies for `obs64.exe`, `NetworkThrottlingIndex`, «Limit reservable bandwidth», NIC offload toggles.
- **Evidence:** home ISPs ignore or bleach DSCP marks; the «20 % reservation» is a placebo (Raymond Chen); offloads only raise CPU
  load; NIC changes can drop the link mid-stream. The real upload problem is bufferbloat (Gettys & Nichols, ACM Queue).
- **Registry values:** `NonBestEffortLimit`. (`NetworkThrottlingIndex` is ADVANCED in the catalog, never in a profile.)
- **Lycoris instead:** the load test on «Устройства и сеть» (`NetProbes`: latency under load) and its bufferbloat finding with the SQM-on-the-router hint (`NetFindings` F10).
- **Sources:** network_stack.md #4, #6, #7 ; network_measure.md ; https://queue.acm.org/detail.cfm?id=2071893 ;
  https://devblogs.microsoft.com/oldnewthing/20061123-14/?p=28923

## `turbo-99-percent` — turbo off via «99 % maximum processor state» → REJECTED
- **Who:** «quiet laptop» guides: PROCTHROTTLEMAX = 99 %.
- **Evidence:** turbo dropping at 99 % is an undocumented side effect on many Intel CPUs; on CPPC/AMD the result is unpredictable
  and may cap frequency in ways the user does not see. Microsoft documents PERFBOOSTMODE 0 = Disabled for every P-state model.
- **Registry values:** none (power setting).
- **Lycoris instead:** `cpu-boost-off-ac` (documented PERFBOOSTMODE).
- **Sources:** https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-perfboostmode ;
  https://www.elevenforum.com/t/add-or-remove-processor-performance-boost-mode-in-power-options-on-windows-11.47581/

## `vendor-fan-charge-writes` — fan curves, power limits, charge limit through EC/WMI → DO-NOT-INCLUDE (devices_design.md §1)
- **Who:** laptop «control centers» and some optimizers writing OEM thermal modes, PL1/PL2, fan curves or the battery charge
  limit through ACPI/WMI/EC.
- **Evidence:** vendor-specific, undocumented interfaces; no exact undo; possible thermal or firmware issues. devices_design.md
  classifies vendor-protocol writes as DO-NOT-INCLUDE.
- **Registry values:** none.
- **Lycoris instead:** links to the signed vendor app («Устройства»; install via «Драйверы»).
- **Sources:** devices_design.md §1 ; devices_vendors.md ;
  https://rog.asus.com/us/articles/guides/armoury-crate-performance-modes-explained-silent-vs-performance-vs-turbo-vs-windows/

## `dark-mode-auto` — dark theme as part of a battery scenario → REJECTED
- **Who:** «battery» presets that switch the Windows/app theme to dark (it is on Microsoft's energy-recommendation list).
- **Evidence:** measurable only on OLED / emissive panels, none on LCD. A visual preference changed as a side effect surprises users.
- **Registry values:** `AppsUseLightTheme`, `SystemUsesLightTheme`.
- **Lycoris instead:** nothing; Lycoris never changes the theme. Monitor brightness / HDR live on «Устройства и сеть».
- **Sources:** https://elevenforum.com/t/review-and-apply-energy-recommendations-in-windows-11.10598/ ;
  https://pureinfotech.com/enable-new-energy-recommendations-settings-windows-11/

## `global-notifications-off` — global notification kill switch for streaming → REJECTED (gaming_tweaks.md §2 H `dnd-in-games`)
- **Who:** «streaming mode» presets: `HKCU\…\PushNotifications\ToastEnabled=0` as a persistent change; writing the Do Not
  Disturb CloudStore blobs.
- **Evidence:** Windows already turns on Do Not Disturb «when playing a game» and in full screen (on by default). The global switch is
  too broad (ADVANCED, only for a boost session with restore); the CloudStore blobs are undocumented and must never be written.
- **Registry values:** `ToastEnabled`.
- **Lycoris instead:** nothing: Windows' own «Do not disturb when playing a game» rule is on by default (Параметры → Система → Уведомления); Game Boost does not touch notifications. (No `dnd-in-games` check exists.)
- **Sources:** gaming_tweaks.md §2 H

## `perf-plans-in-work-scenarios` — High performance / memory purge in work, streaming and battery scenarios → REJECTED
- **Who:** «work mode» / «stream mode» presets that add High performance / Ultimate, cpu-min-state 100 % or a standby purge.
- **Evidence:** 0–2 % on desktops, nothing for office work or GPU encoders; higher idle power and heat; the purge is mostly placebo
  and can slow loads. Ultimate and cpu-min-state are ADVANCED (never in profiles); laptop plans stay Balanced (general_tweaks.md §11).
- **Registry values:** none.
- **Lycoris instead:** the power-mode items; the purge stays an opt-in Game Boost action.
- **Sources:** gaming_tweaks.md §2 C ; tech.md §5 ; general_tweaks.md §11

## `do-background-bandwidth` — Delivery Optimization bandwidth policies as a «speed-up» → REJECTED (gaming_tweaks.md §2 F; network_stack.md #21)
- **Who:** «internet booster» presets: `DOPercentageMaxBackgroundBandwidth` / `DOMaxBackgroundDownloadBandwidth` policies, or
  turning Delivery Optimization off entirely.
- **Evidence:** Microsoft's DO FAQ: by default Delivery Optimization «measures and targets to use no more than 45 % of the available
  bandwidth during a background download or 90 % for an interactive, foreground download»; `DOPercentageMaxBackgroundBandwidth`
  default 0 «dynamically adjusts». Since Windows 11 22H2 DO supports receiver-side LEDBAT (rLEDBAT), which yields to interactive
  traffic. The policies lock the Settings UI (ADVANCED per gaming_tweaks.md §2 F).
- **Registry values:** `DOPercentageMaxBackgroundBandwidth`, `DOMaxBackgroundDownloadBandwidth`.
- **Lycoris instead:** `delivery-optimization-local` («Обновления без P2P-раздачи», SAFE-DEFAULT, `DownloadMode=0` — no peer upload, no bandwidth limit; the limit stays in Windows Settings → Delivery Optimization → Advanced) and the bufferbloat measurement.
- **Sources:** https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-faq ;
  https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference ;
  https://learn.microsoft.com/en-us/windows/deployment/do/whats-new-do ; gaming_tweaks.md §2 F ; network_stack.md #21

## `search-exclude-game-libraries` — excluding game libraries from the indexer «for FPS» → REJECTED (services.md §1A WSearch)
- **Who:** «game mode» presets that exclude Steam/Epic folders or stop WSearch during play.
- **Evidence:** since Windows 10 2004 (build 19025) the indexer stops or throttles itself in game mode, power saver / low power mode,
  when CPU use is above 80 % or disk use above 70 % (Insider blog, Thurrott, TechSpot). Excluding libraries saves nothing during
  play and only makes the files unsearchable; stopping WSearch breaks Start and Explorer search.
- **Registry values:** none (crawl-scope API / service).
- **Lycoris instead:** the services page explains WSearch; Game Boost does not touch it.
- **Sources:** https://blogs.windows.com/windows-insider/2019/11/15/announcing-windows-10-insider-preview-build-19025/ ;
  https://www.thurrott.com/?p=222164 ; https://www.techspot.com/news/83454-windows-10-next-major-update-could-improve-system.html ; services.md §1A

## `hpet-bcdedit` — HPET / `bcdedit` timer tweaks → DO-NOT-INCLUDE (gaming_tweaks.md §3)
- **Who:** `bcdedit /set useplatformclock true|false`, `disabledynamictick yes`, `tscsyncpolicy enhanced`, disabling HPET in
  Device Manager (WinUtil-style scripts, many «latency» guides).
- **Evidence:** Microsoft (Acquiring high-resolution time stamps): since Windows 7/8 QPC uses the invariant TSC, which is cheap to
  read; HPET/ACPI PM timers «are slower to access» and shared across processors. `useplatformclock` forces the slow platform timer —
  more overhead, no gain; `disabledynamictick` raises idle power. Forum «results» (Guru3D, Blur Busters) are subjective and
  contradictory. bcdedit changes are forbidden by CLAUDE.md rule 3.
- **Registry values:** none (BCD store).
- **Lycoris instead:** nothing is needed; the «Отчёт об энергоэффективности» shows real timer holders.
- **Sources:** https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps ;
  https://forums.guru3d.com/goto/post?id=6015918 ; https://forums.blurbusters.com/viewtopic.php?p=109996 ; gaming_tweaks.md §3

## `registry-cleaning` — registry «cleaning» → DO-NOT-INCLUDE (general_tweaks.md §10 `reg.clean`; competitors.md §2.1)
- **Who:** CCleaner, Advanced SystemCare, Glary, Wise Registry Cleaner, Care 365.
- **Evidence:** Microsoft KB 2563254: «Microsoft does not support the use of registry cleaners», «serious issues can occur when you
  modify the registry incorrectly using these types of utilities. These issues might require users to reinstall the operating
  system». No measurable benefit; registry hives are memory-mapped and a few orphaned keys cost nothing.
- **Registry values:** none (the whole class of action).
- **Lycoris instead:** SFC / DISM repair in «Обслуживание → Ремонт и диски».
- **Sources:** https://support.microsoft.com/en-us/topic/2563254 ; general_tweaks.md §10 ; competitors.md §2.1

## `pagefile-off` — disabling or shrinking the page file → DO-NOT-INCLUDE (gaming_tweaks.md §3; general_tweaks.md §10 `mem.pagefile`)
- **Who:** «32 GB is enough» guides; `ClearPageFileAtShutdown`, `DisablePagingExecutive`, `LargeSystemCache`.
- **Evidence:** Russinovich (Pushing the Limits of Windows: Virtual Memory): the commit limit is RAM + page file; without a page file
  commit-heavy games and apps fail with «out of memory» while physical RAM is still free. `DisablePagingExecutive` and
  `LargeSystemCache` are server-era knobs with no gaming effect; `ClearPageFileAtShutdown` only slows shutdown.
- **Registry values:** `ClearPageFileAtShutdown`, `DisablePagingExecutive`, `LargeSystemCache`. (`PagingFiles` is written only by the
  «back to system-managed» repair.)
- **Lycoris instead:** `pagefile-system-managed` («Файл подкачки: автоматический размер», «Оптимизация») and the memory facts on «Панель».
- **Sources:** https://learn.microsoft.com/en-us/archive/blogs/markrussinovich/pushing-the-limits-of-windows-virtual-memory ;
  https://www.windowslatest.com/2026/10/23/should-you-turn-off-the-page-file-on-a-32gb-pc-heres-what-microsoft-actually-recommends-k4l9/ ;
  gaming_tweaks.md §3 ; general_tweaks.md §10

## `realtime-priority` — Realtime priority for a game → DO-NOT-INCLUDE (gaming_tweaks.md §3; tech.md §6)
- **Who:** Razer Cortex / Process Lasso / «game booster» presets setting the game process to Realtime.
- **Evidence:** Microsoft (Scheduling Priorities, SetThreadPriority): «You should almost never use REALTIME_PRIORITY_CLASS, because this
  interrupts system threads that manage mouse input, keyboard input, and background disk flushing»; it «may cause disk caches to not
  flush, cause the mouse to stop responding». Anti-cheat may also flag it.
- **Registry values:** none (`PerfOptions\CpuPriorityClass` is not written).
- **Lycoris instead:** Game Boost raises the game to High only (never Realtime), for the session.
- **Sources:** https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities ;
  https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriority ; gaming_tweaks.md §3 ; tech.md §6

## `nic-offloads` — disabling NIC offloads, RSS, interrupt moderation → DO-NOT-INCLUDE (network_adapter.md `net-offloads-off`; network_stack.md #9–12; gaming_tweaks.md §3)
- **Who:** «ping» guides and some optimizers: `*LsoV2IPv4/6`, `*TCPChecksumOffloadIPv4/6`, `*RSS`, RSC, interrupt moderation off.
- **Evidence:** Microsoft's network adapter tuning guide: «Enabling network adapter offload features is usually beneficial»; disabling
  checksum offload also disables RSS, RSC and LSO. The result is higher CPU load on the game's cores, not lower ping; changing
  adapter keywords resets the link (drops the connection mid-game).
- **Registry values:** `*LsoV2IPv4`, `*LsoV2IPv6`, `*TCPChecksumOffloadIPv4`, `*TCPChecksumOffloadIPv6`, `*RSS`, `*InterruptModeration`.
- **Lycoris instead:** the adapter facts and measurements on «Устройства и сеть».
- **Sources:** https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics ;
  network_adapter.md ; network_stack.md #9–12 ; gaming_tweaks.md §3
