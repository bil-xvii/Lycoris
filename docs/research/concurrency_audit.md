# Concurrency audit — many tasks at once (02.11.2026)

Owner request: «Надо сделать так, чтобы при выполнении той или иной задачи, если, к примеру, выполняется много задач одновременно
и сразу, чтобы это не рушило систему и продолжала работать.»

Scope: every long-running or system-changing operation of MONOLITH at commit `9fc8042` (before this work), what it locks, what
happened when it met another one, and what was changed. Everything below was verified by reading the code at that commit; "today"
means before the change, "now" means after it.

## 1. What existed before

| Mechanism | Where | What it protects | What it does not |
|---|---|---|---|
| `IsBusy` + `RunBusyAsync` | `Shell/PageViewModel` | one operation per page (a second click is ignored silently) | anything across pages; the ignored click gave no reason |
| Journal gate (`SemaphoreSlim(1)` + `AsyncLocal`) | `Core/ChangeJournal.Exclusive/ExclusiveAsync`, `RevertAsync` | each journaled change / revert is atomic against the others (verified by the stress tests below) | batches of two pages interleave change by change; installers, repairs and file deletes are not journaled and not covered |
| `RestorePointService.Gate` + `_inFlight` | `Core/RestorePointService` | two WMI `CreateRestorePoint` calls never overlap; a timed-out call is not re-entered | the second caller still creates a *second* restore point right after the first (24 h limit is lifted again) |
| `WingetClient.Gate` | `Core/WingetClient` | winget processes of MONOLITH are serialized | driver installers, vendor installers, Windows Update, DISM/SFC |
| `Ui.DialogGate` | `UI/Ui` | one dialog at a time | a dialog opened *inside* an operation keeps that operation alive (and its lock) until the user answers |
| `GameBoostState` | Cleaner, Tools (energy/DPC), NIC writer | Cleaner, DPC/energy report and NIC changes refuse during a game session | SFC/DISM/chkdsk/defrag, installs |
| Exit watchdog (20 s) + `App.OnExit` (journal save, restore-point limit put back) + `GameBoostService.StopOnExit` | `App.xaml.cs`, `GamesViewModel` | a half-shut-down process cannot hold the instance lock | **no** check that something is still running: the window just closed |
| Display guard child process | `Core/Devices/DisplaySafeApply` | display mode/HDR comes back if MONOLITH dies | — |

## 2. Inventory of long-running / system-changing operations

Thread = where the work runs. Lock = what it takes today (before) → the coordinator class it has now.

| # | Operation | Entry point | Thread | Locks (before) | Duration | Changes | Class now |
|---|---|---|---|---|---|---|---|
| 1 | Apply tweaks (Optimize, Dashboard «ИСПРАВИТЬ», profiles) | `OptimizeViewModel`, `DashboardViewModel.Actions` → `TweakEngine.ApplyAsync` | pool | page `IsBusy`, journal gate per change, RP gate | seconds–1 min | registry/services/tasks/power | JournalChange |
| 2 | Revert tweak / record / batch / all | `OptimizeViewModel`, `RestoreViewModel` → `ChangeJournal.RevertAsync` | pool | page `IsBusy`, journal gate **for the whole revert** | seconds–minutes (revert-all) | undo of the above | JournalChange |
| 3 | Services / tasks / startup changes and undo | `ServicesViewModel.*`, `StartupViewModel` → `ChangeSession` | pool | `IsBusy` (+ host mirroring), journal gate | seconds | services, tasks, Run keys | JournalChange |
| 4 | Drift re-apply | `RestoreViewModel.ReapplyAsync` → `DriftReapply` | pool | `IsBusy`, gate | seconds | registry | JournalChange |
| 5 | GPU preference per game | `GamesViewModel` → `GpuPreferenceStore.SetAsync` | pool | none beyond the gate (not in `RunBusyAsync`) | < 1 s | HKCU | JournalChange (inside the service) |
| 6 | winget install / upgrade queue (+ vendor installer download) | `AppsViewModel.RunQueueAsync`, `DownloadFromVendorAsync` | pool + UI | own flags (`IsQueueRunning`), `WingetClient.Gate` | minutes–hour | programs; restore point first | Install |
| 7 | Driver install from Windows Update / pnputil / vendor download / tool install / backup+restore | `DriversViewModel.*`, `DriverDownloads`, `WindowsUpdateDrivers` | pool | `IsBusy`+`IsInstalling`… (page only) | minutes–hour | drivers | Install |
| 8 | DISM CheckHealth/ScanHealth/RestoreHealth, SFC, chkdsk, defrag/TRIM, store/icon-cache reset, DNS flush | `ToolsViewModel.ExecuteAsync` → `ProcessRunner` | pool + child | `IsRunning` (Tools page only) | 1 min – 8 h | component store, system files, disk | SystemRepair |
| 9 | Network reset (`netsh winsock/int ip reset`) | `ToolsViewModel.RunNetworkResetAsync` | pool + child | same | seconds (+ restore point) | TCP/IP stack | SystemRepair |
| 10 | DPC latency (ETW), energy / sleep reports, chkntfs | `ToolsViewModel` | pool + child | `IsRunning` | 1–2 min | nothing | Measure (DPC/energy: `Quiet`) |
| 11 | Cleaner analysis | `CleanerViewModel.RunWorkAsync` | pool | `IsWorking` | seconds–min | nothing | Measure |
| 12 | Cleaner delete (+ DISM component cleanup, Delivery Optimization) | `CleanerViewModel.CleanAsync` | pool + child | `IsWorking`, `GameBoostState` | seconds–1 h | user/system caches | Cleanup |
| 13 | NIC advanced property write/undo (adapter restart), display mode/HDR, DDC, SPI, mic level | `DeviceApply.ApplyOneAsync/ApplyPlanAsync/RevertOneAsync` | pool + UI countdown | `IsApplying` per row, journal gate, `INicWriter.RunningGame` | seconds (+15 s countdown) | adapter, display, input | Disruptive |
| 14 | Network load test, ping, DNS speed, Valve regions | `DeviceTool.StartAsync` | pool | `IsRunning` per tool | 5–60 s | nothing (heavy traffic) | load test: Disruptive; others: Measure `Quiet` |
| 15 | Game Boost start / stop (power plan, closing apps, EcoQoS, standby purge) | `GameBoostService.StartAsync/StopAsync` (also called by the game watcher) | pool + UI | `IsTransitioning` (service only) | seconds | power plan, processes | Disruptive (stop never refused) |
| 16 | Create restore point / enable protection | `RestorePointService.CreateAsync`, `RestoreViewModel` | pool (WMI) | RP gate | up to 5 min | VSS snapshot | Disruptive (nested inside the operation that asked for it) |
| 17 | Explorer restart | `OptimizeViewModel` → `SessionActions` | pool | `IsBusy` | 2 s | shell | Disruptive |
| 18 | Enable a device (pnputil) | `DriversViewModel.Devices` | pool + child | `IsBusy` | seconds | device state | Disruptive |
| 19 | Startup recovery: stale game session revert, display reconcile | `MainViewModel.RunStartupChecksAsync` | pool | journal gate | seconds | HKLM/HKCU, display | JournalChange / Disruptive |
| 20 | Admin-intent replay | `MainViewModel.ReplayIntentAsync` | UI | polls `CanExecute` up to 10 s | — | whatever the command does | (goes through the page command) |
| 21 | Page loads / refreshes / Dashboard checks / hardware analysis / warm-ups | every `LoadAsync` / `Refresh` | pool + WMI + PowerShell | page `IsBusy` | 1–10 s each | nothing | Measure |

## 3. Matrix of dangerous combinations

Severity: **H** = can leave the system half-changed or two tools corrupting each other; **M** = wrong/misleading result, wasted
work, system slowed; **L** = cosmetic.

| # | Combination | Today (verified in code) | Sev. | Fix now |
|---|---|---|---|---|
| 1 | winget queue + driver installer + Windows Update at once | Each page only checks its own flags. `WingetClient.Gate` serializes winget, but a driver download (`DriversViewModel.DownloadDriverAsync`), WU install and vendor installer run beside it. Windows Installer is single-instance (error 1618), driver installs replace what winget may be installing. | H | Install class: one at a time app-wide, the second waits in the visible FIFO queue («в очереди: …», cancellable); the confirmation dialogs stay before the wait |
| 2 | DISM / SFC / chkdsk / defrag during an install (and the reverse) | No cross-page check. DISM/SFC need the servicing stack; an install running meanwhile can fail or corrupt the component store state. | H | SystemRepair is refused at once while an install/change/cleanup runs («Сейчас идёт установка Steam — дождитесь окончания»), before its confirmation dialog; an install arriving during a repair waits in the queue |
| 3 | DISM/SFC and Game Boost | Only the energy report and DPC refused during a game. | M | Tools repair is refused during a game session |
| 4 | Cleaner delete during an install | No check; the cleaner can delete the installer's temp files. | H | Cleanup × Install conflict (queued) |
| 5 | Cleaner delete during a game session | Already refused (`CleanAsync`). | — | kept |
| 6 | Two restore-point creations (two pages applying) | `RestorePointService.Gate` serializes them, but the second creates a second point immediately (limit lifted twice). | M | RP creation is Disruptive: never beside another disruptive change, an installer or a journal batch that is not its own flow; nested calls (inside an install/apply) pass |
| 7 | Revert-all during apply | The gate serializes per change; a revert holds it for its whole run, so an apply from another page waits blocking a pool thread (no queue, no message, no timeout). Batches interleave. | M | JournalChange × JournalChange conflict at batch level: FIFO queue with a visible state and a 30 min timeout |
| 8 | NIC restart / display mode change during a download, ping/load test or Game Boost | NIC refused during game (`INicWriter.RunningGame`). Nothing for downloads or tests: a restart kills the download and makes a measurement meaningless. | M | Disruptive × Install conflict; ping/DNS/Valve are `Quiet` measures (refused during Install/SystemRepair/Disruptive and vice versa); the load test is Disruptive |
| 9 | Two Dashboard tiles clicked back-to-back | Each tile calls another page's command; both start; two journal batches / two restore points / «ОПТИМИЗАЦИЯ» while «ИГРОВОЙ СЕАНС» starts. | M | the same page commands now go through the coordinator: the second waits (J) or is refused with the reason of the first |
| 10 | Intent replay racing with startup warm-ups | `ReplayIntentAsync` polls `CanExecute` for ~10 s and then executes — if the user pressed the same button meanwhile, the action ran twice (once after the other finished). | M | replay aborts with a toast when the page already has a running/queued operation; its command otherwise goes through the coordinator |
| 11 | Apply from two pages touching the same tweak/registry key | The journal gate makes each change atomic and layers handle the order. Verified by `JournalStressTests` (same target hammered by 12 sessions + interleaved reverts ends at the original value). | L | nothing needed; batch-level queue added anyway (#7) |
| 12 | App exit while operations run | `MainWindow` had no `Closing` handler: the window closed at once; the 20 s watchdog then ended the process. Journal saved in `OnExit`; children (winget, DISM) keep running un-supervised; a queued change simply vanished. | H | exit dialog listing the operations: ЖДАТЬ / ОТМЕНИТЬ БЕЗОПАСНЫЕ И ВЫЙТИ / ВЫЙТИ КАК ЕСТЬ; nothing is killed; new operations are refused while closing; watchdog and journal save unchanged |
| 13 | Handle / memory pressure from many parallel WMI / PowerShell / winget processes | `ProcessRunner` had no cap (Dashboard, Drivers, Devices, Cleaner warm-ups all spawn PowerShell). | M | short child processes (timeout ≤ 90 s) limited to 4 at once; Measure/Background operations limited to 3 in parallel; long tools never take a slot |
| 14 | A dialog (restore point failed — continue?) shown while holding the install/repair lock | Apps, Drivers, network reset ask in the middle of the operation. | M | `OperationLease.WhileUserDecidesAsync`: the lease is released while the dialog waits and taken back first in line (only before anything was changed). The display countdown (15 s, auto-revert) is the one bounded exception |
| 15 | Game session auto-stop while an installer runs | `StopAsync` (called by the watcher when the game exits) has no wait/queue notion. | M | Stop is Disruptive with `QueueIfBusy`: never refused, waits visibly (4 h limit) |
| 16 | An exception inside one operation | `RunBusyAsync` caught and toasted it; services returned results. Nothing released a lock because there was none beyond the gate (released in `finally`). | L | leases are released in `finally` (tests: lease freed when the body throws); an unhandled exception in one page operation fails that operation only |
| 17 | Two installers started from the same page by double click | page flags | L | kept; `_installWaiting` stops queuing the same click twice |
| 18 | Cancelling the wrong thing | Apps: «ПРЕРВАТЬ СЕЙЧАС» kills winget and the installer. | M | the «ЗАДАЧИ» list only offers the safe stop (finish the current package, drop the rest); DISM/SFC/network reset show «прервать нельзя»; nothing in the coordinator kills a process |

## 4. Design (as implemented)

`Core/OperationCoordinator.cs`:

* **Classes** — `Install`, `SystemRepair`, `JournalChange`, `Disruptive`, `Cleanup`, `Measure`, `Background`.
* **Matrix** (symmetric; `OperationCoordinator.ClassesConflict`): Install conflicts with everything but Measure/Background;
  SystemRepair likewise; JournalChange conflicts with JournalChange, Disruptive, Install, SystemRepair; Disruptive with
  Disruptive, JournalChange, Install, SystemRepair; Cleanup with Cleanup, Install, SystemRepair; Measure/Background with nothing.
  A `Quiet` measurement (ping, DNS, Valve, DPC, energy report) additionally conflicts with Install / SystemRepair / Disruptive.
* **Policy** — Install, JournalChange, Cleanup (and any request with `QueueIfBusy`) wait in a visible FIFO queue; SystemRepair,
  Disruptive and Measure are refused at once with «Сейчас идёт <имя> — дождитесь окончания.»; Background waits quietly. Measure /
  Background never block and never wait for user actions; at most 3 of them run in parallel (the cap never delays a user action).
  FIFO is strict among conflicting requests (a later request cannot overtake an earlier conflicting waiter).
* **Deadlock safety** — admission is all-or-nothing under one lock; one lease per operation (no hold-and-wait); a flow that
  already holds a lease starts nested operations without waiting (async-local, invalid once the lease is released — same pattern
  as the journal gate); queue waits have per-class timeouts with a clear message (`OperationTimeoutException`), are cancellable,
  and use a `TimeProvider` (tests use a fake clock); leases are released in `finally`; no lease is held across a dialog.
* **Visibility** — `OperationCoordinator.Snapshot()`; `MainViewModel.Operations` (rebuilt on the UI thread), status-bar button
  «ЗАДАЧИ · N», an in-window list with state, reason («ждёт: …»), «ОТМЕНИТЬ» (queued items; running ones only where a safe handler
  exists) or the text «прервать нельзя — дождитесь окончания»; the strip under the title bar shows why a click did not start and
  offers «ОТКРЫТЬ ЗАДАЧИ» / «ЗАКРЫТЬ» (text, never only a tooltip).
* **Exit** — `ExitPlan` + `MainWindow.OnClosing` + `Ui.ChooseAsync` (three buttons).

Wiring (smallest possible change): `PageViewModel.RunBusyAsync(OperationClass, text, work)` (the old two-argument overload is
gone, so a new long operation cannot forget its class — it does not compile), `CanStartNow` (reason before the confirmation),
`AcquireLeaseAsync` (pages with their own progress UI: Apps queue / vendor download, Drivers download),
`AskWithoutLeaseAsync`; services with several callers wrap themselves: `RestorePointService.CreateAsync`,
`GameBoostService.StartAsync/StopAsync`, `DeviceApply.*`, `DeviceTool.StartAsync`, `GpuPreferenceStore.SetAsync`, the startup
recovery in `MainViewModel`.

## 5. Verification

* `OperationCoordinatorTests` (matrix, FIFO, cancel of queued items, release on exception, re-entrancy, timeouts with a fake clock,
  no deadlock and no conflicting overlap under 200 randomized concurrent operations, exit plan).
* `JournalStressTests`: 16 parallel sessions on distinct and shared targets with reverts in between; 12 sessions on one hot target;
  revert-all running beside new sessions — against `RegistryHelper.Redirect` and `ChangeJournal.ForTests`: no lost record, no
  exception, journal file valid JSON and equal to memory, final revert-all restores every original value. **The journal itself
  needed no fix.**
* `PageOperationTests`: a second start while the first runs yields the queue / refuse state; a queued start taken out of the queue
  never runs; a crashing operation frees its lease; Measure never waits behind a change.
* `ProbeLimitTests`: 14 child processes at once never exceed the cap of 4.
* `--selftest`: new stage «ОДНОВРЕМЕННЫЕ ЗАДАЧИ» (read-only commands of every page fired together three times + 90 synthetic
  operations of every class): no error/binding error, task list ends empty, short processes ≤ cap, memory growth bounded, UI lag
  measured by the existing monitor.
* `--snapshot … --demo-tasks` (QA only) fills the list with pretend operations and opens it, to look at the button, the list and the strip.

## 6. Not covered / residual risks

* A vendor installer that MONOLITH has **started for the user** (`UnelevatedShell.StartAsUserAsync`) runs outside MONOLITH: its
  lease ends when it is launched. Windows Installer (msiexec) still protects itself with error 1618.
* Windows Update, other programs and the user's own actions are outside any in-process coordinator.
* WMI queries are not individually capped (only the Measure/Background parallelism and the short-process cap bound them).
* Page first loads and Dashboard warm-ups are `Measure`, not `Background`; they never block, and the cap of 3 applies.
* Exiting with «ВЫЙТИ КАК ЕСТЬ» leaves running children (winget, DISM) alone by design; their result no longer shows in MONOLITH.
* A queued operation that waits behind a very long install can reach its queue timeout (4 h for installs, 30 min for changes);
  the message names the blocker and nothing is half-done.
* The display-change countdown (≤ 15 s, auto-revert) holds its Disruptive lease across the modal countdown window by design.
