# Lycoris — project guide

Lycoris is a Windows 10/11 PC optimizer and game booster (C# / WPF / .NET 10, Russian UI + English via `Loc`, see «Localization»).
Owner: GitHub `bil-xvii`, private repo `bil-xvii/Lycoris`. Everything is auto-committed and pushed
by `tools/autosave.ps1` (Claude Code Stop hook in `.claude/settings.json`).

## AI coding rules (owner, 04.11.2026)

Every task (own or delegated to Codex/Gemini/Hermes/local models) starts with the checklist `C:\Users\Lycoris\agent-tools\task-preamble.md`;
the full rulebook with official sources (Anthropic, OpenAI, Google, GitHub) is `docs/ai-coding-rules.md`. Delegated task prompts get the checklist first.
No «impossible» (owner, 04.11.2026): there is only missing data — research online (official sources, cross-check), verify, and if nothing exists, build it.
A real limit is stated only with numbers, what was tried and the next step.

## Prime directive: never damage the system

Goal (owner): Lycoris must be the best PC-optimizer on the market — more effective, more honest and better
designed than every competitor — while never breaking or damaging the system. When effectiveness and safety
conflict, safety wins; the advantage comes from evidence-backed tweaks, exact undo and honest explanations,
not from riskier changes.

1. **Every system change goes through `Core/ChangeSession`** (`SetRegistry`, `SetServiceStart`, `SetPowerScheme`,
   `SetTaskEnabled`, `RunCommandAsync`; device settings: `SetUserParameter` / `SetUserParameterConfirmedAsync` (SPI mouse
   and keyboard), `SetDisplayModeAsync`, `SetDisplayHdrAsync`, `SetMonitorSettingAsync` (DDC/CI), `SetAudioLevel` (capture
   endpoints only), `SetNicAdvancedProperty`). It records the original value in the undo journal
   (`%ProgramData%\Lycoris\journal.json`) *before* changing anything, exports touched registry keys to `.reg`
   backups, and creates a System Restore point per batch. Never write to the registry / services / power plans
   directly from a module (reading is fine). The device writers (`Core/Devices/DeviceWriters`, implemented in
   `Modules/Devices/*`) are called only by ChangeSession and `DeviceRestore`, and every mutating call is SafetyLock-guarded.
2. Only documented, reversible, evidence-backed changes. Research lives in `docs/research/*.md` — its
   classifications (SAFE-DEFAULT / SAFE-OPTIONAL / ADVANCED / DO-NOT-INCLUDE) are binding.
   DO-NOT-INCLUDE items must not be implemented, not even hidden.
   Exception (owner, 10.11.2026): a DO-NOT-INCLUDE item that is merely useless or risky — NOT protection, NOT irreversible —
   may be offered as an expert-only setting in `Tweaks/TweakCatalog.Expert.cs` (id `expert-…`, ADVANCED, honest «Lycoris это не
   рекомендует» warning, exact journal rollback, evidence record). It stays in `RejectedTweaks` with the reason.
3. Never: disable Windows Update / Defender / firewall / UAC / SmartScreen, delete system files, clean the
   registry, disable the page file, SysMain on SSD "fixes"; core-parking hacks are prohibited except as warned,
   exactly reversible expert-only settings (`expert-…`); bcdedit tweaks, remove Store
   apps silently, pass `--ignore-security-hash`/`--force`/`--allow-reboot` to winget, delete user data
   (Downloads, Documents, browser cookies/passwords/history) by default.
4. `Core/ServiceControl.Protected` services are never modified. Cleaner never follows reparse points.
5. Destructive or irreversible actions (deleting files, uninstalling, driver installs) always show a confirmation
   dialog (`Ui.ConfirmAsync`) listing exactly what will happen.
6. Without admin rights the app runs read-only: check `Elevation.IsElevated` / `PageViewModel.RequireAdmin` (it offers
   the relaunch with admin rights and, given a command name, replays that command once in the elevated copy through its
   usual confirmation; the action never runs unelevated).
7. `%ProgramData%\Lycoris` is locked to Administrators/SYSTEM (`AppPaths.SecureMachineDir`); a non-elevated
   process never writes there. The journal is treated as untrusted input: undo commands are limited to System32
   tools and autostart/hijack registry locations are refused (`ChangeJournal.IsForbiddenRestoreTarget`).
8. Browsers, games and launchers are never started with Lycoris's admin token: use
   `UnelevatedShell.OpenAsync` (web pages) / `UnelevatedShell.StartAsUserAsync` (programs, launcher URIs).
9. Installers are downloaded only through the shared `Core/VendorDownloads` mechanism (per-request vendor HTTPS hosts
   allow-list, also after redirects + valid Authenticode signature of an expected vendor signer, files kept in the
   admin-only `%ProgramData%\Lycoris\Downloads`; refused without admin rights). Drivers use it via
   `Modules/Drivers/DriverDownloads`; the Apps page uses it when winget reports an installer hash mismatch
   (`CatalogEntry.VendorInstaller`) — a confirmation lists host/signer/file, and the installer is then started with
   `UnelevatedShell.StartAsUserAsync`. The winget hash is never bypassed.
10. Devices («Устройства и сеть»): per-user changes (SPI, microphone level/mute) only when Lycoris runs as the desktop
   user (`Elevation.InteractiveUserState`; unknown = refused). Display mode and HDR, very low brightness/contrast, very low
   pointer speed and the button swap apply only with an on-screen countdown (`DeviceConfirm`, auto-revert after 15 s); a
   display change is also watched by the `--display-guard` child process (Lycoris.exe itself, marked so it is never
   ended as a hung copy), which puts the previous mode/HDR back if Lycoris dies or nobody confirms. Network adapter
   properties: only `DeviceTargets.NicKeywords` (speed/duplex, EEE), only values the adapter lists; a change and its
   undo restart the adapter, so both are refused during Game Boost or while a library game runs
   (`INicWriter.RunningGame`); an undo whose adapter or keyword is gone is skipped, not failed. Measurements (raw-input
   polling/DPI, mic level, ping/load test/DNS/Valve) never change anything; the load test refuses on a metered
   connection or during a game, and no result leaves the PC.

## Layout

```
src/Lycoris/
  App.xaml(.cs)          startup, optional UAC relaunch, single instance, --snapshot QA mode
  Core/Localization/     Loc (ru text = key → English table), en/<Area>.json translation files (see «Localization»)
  Core/                  system layer (no UI): AppPaths, Log, Elevation, ProcessRunner, RegistryHelper,
                         ChangeJournal, ChangeSession, OperationCoordinator, ServiceControl, PowerSchemes, ScheduledTasks,
                         RestorePointService, SystemProfile, AppSettings, WingetClient, VendorDownloads;
                         Core/Devices: device journal targets, writer interfaces, safe display apply, undo of device records
                         Core/Maintenance: «Обслуживание по расписанию» — the weekly task «Lycoris.exe --maintain» (always normal rights,
                         journaled through ChangeSession.SetMaintenanceTask; the run: Modules/Cleaner/ScheduledMaintenanceRun.cs)
  Tweaks/                Tweak model (RegistryTweak, ServiceTweak, ScheduledTaskTweak, CustomTweak),
                         TweakEngine (apply/revert via journal), TweakCatalog (+ partial files with items)
  Shell/                 MainWindow (custom chrome, sidebar, console, toasts, dialogs), MainViewModel, PageViewModel
  UI/                    Ui facade (ConfirmAsync, ConfirmOptionAsync, AlertAsync, Toast, Navigate, RequestFocus, Post),
                         Glyphs, Converters, Controls/ (PageHeader, StickyBar, Meter, Sparkline, ArcGauge)
  Theme/                 Colors.xaml, Typography.xaml, Controls.xaml, Converters.xaml
  Modules/<Name>/        one folder per page: <Name>View.xaml(.cs) + <Name>ViewModel.cs + module services
tests/Lycoris.Tests/    xUnit
docs/research/           research reports (competitors, tweaks, cleaner, drivers, services, apps catalog, tech)
tools/                   loc-scan.ps1 + LocScan/ (localization progress and helper), autosave.ps1, make-icon.ps1, extract-research.ps1, publish.ps1 (build + auto-sign),
                         sign.ps1 / signing-cert.ps1 (Authenticode signing; owner guide in docs/signing.md)
```

Pages (sidebar order): dashboard, optimize, games, services («АВТОЗАПУСК И СЛУЖБЫ»: tabs АВТОЗАПУСК | СЛУЖБЫ |
ВСЕ СЛУЖБЫ | ЗАДАЧИ; hosts Modules/Startup), maintenance («ОБСЛУЖИВАНИЕ»: tabs ОЧИСТКА | РЕМОНТ И ДИСКИ; hosts
Modules/Cleaner + Modules/Tools), drivers, devices («УСТРОЙСТВА И СЕТЬ»: tabs АВТОНАСТРОЙКА | ВРУЧНУЮ; hosts
Modules/Devices with Input, Displays, Audio, Network, Vendors — each package adds its provider/writer through the partial
hooks `DeviceProviders.<Name>.cs` / `DeviceWriters.<Name>.cs`), apps, restore; settings opens from the gear in the sidebar footer.
Navigation: `Ui.Navigate("optimize")`. Aliases: "startup" → services tab, "cleaner"/"tools" → maintenance tab.
Deep links: `Ui.RequestFocus(pageKey, id)` (optimize: tweak id; services: "tab:…", a service name or "startup:<entry key>";
devices: "tab:auto|manual", "kind:monitor", a card key, a setting id, a journal target such as "spi:mouse-speed",
"tool:<id>"; drivers: "tab:<DriversTab>", "periph:<VID>"; apps: "app:<winget id>"; cleaner: "clean:<item id>"; settings:
"myth:<id>", "setting:<row>"; restore: "section:last-batch|journal|restore-points|drift" — prefixes in
`CommandPaletteIndex`). Ctrl+K or the title-bar «ПОИСК» = command palette.
`Shell/`: also `AdminIntent` (replay of an action after the relaunch with admin rights), `CommandPaletteIndex`.

## Obvious UI (owner rule, 01.11.2026)

Owner: «Всё слишком разбросано… Чтобы было понятно, куда нажимать. Чтобы я видел ярлыки, а не чтобы они скрывались
где-то в фоне.» The old «Minimal UI» rule (one place, no duplicates, tooltips instead of text) produced hidden actions and
is replaced by this one. The audit with evidence: `docs/ux/ux-audit.md`; the approved layout: `docs/ux/option-a.html`.

1. **Logic lives in ONE place, shortcuts live wherever the user needs them.** The change itself is one command on one
   page (bulk undo → Restore, tweaks → Optimize, game-boost options → Games, device settings → Devices, services expert
   mode → ВСЕ СЛУЖБЫ…). Shortcuts to it (Dashboard tiles, the title-bar «ОТКАТ · N», toasts, «ОТКРЫТЬ В …» buttons) call
   that same command with the same confirmation — never a second implementation, never a second toggle.
2. **Every action is a visible, labelled button**: icon + text, ≥ 30 px, outlined with `B.Outline` (`Btn`), or the ONE
   white main button of the page (`Btn.Primary` / `Btn.Hero`). Never icon-only (`Btn.Icon` is for the window caption and
   nothing else), never hover-only, never only in a ContextMenu, Ctrl+K or a tooltip. `Btn.Ghost` only next to a labelled
   main button; `Btn.Link` = an outlined jump to another page («ОТКРЫТЬ В ОПТИМИЗАЦИИ →»); `Btn.Danger` = red outline + red text, lightly hatched at rest.
3. **White fill = clickable, and only that.** Tags are status, never white and never an outlined capsule (`Tag`,
   `Tag.Solid`, `Tag.Muted`, `Tag.Hatch`/`Tag.Risk` (red) — filled R.Xs blocks). One state vocabulary everywhere: ПРИМЕНЕНО · НЕ ПРИМЕНЕНО ·
   ИЗМЕНЕНО Lycoris (+ ОТКАТИТЬ) · НЕПРИМЕНИМО; risk БЕЗОПАСНО / ПО ЖЕЛАНИЮ / ЭКСПЕРТ. No local tag styles in modules.
4. **Contrast and sizes.** Text ≥ 4.5:1 (`B.Text`, `B.Text2`, `B.Text3` #8A8A8A for captions), `B.Text4` #6B6B6B only
   for disabled; outlines ≥ 3:1 (`B.Outline` #606060 for Btn / CheckBox / Toggle / Chip / TextBox borders, `Line*` only
   decorative). `T.Label` and `T.Caption` 12 px, tags 11, mono 11.5–12; no FontSize 9–10.5 anywhere; glyphs in buttons 12–13.
5. **The reason for anything grey is text next to it** (`T.Caption`), never only a tooltip. A tooltip may repeat, never
   replace. Read-only mode: buttons are NOT disabled — a click calls `RequireAdmin(action, command, parameter)`, Lycoris
   relaunches with rights, reopens the page and replays that command once (see «Admin intent replay» below). The one
   global read-only strip under the title bar is the only read-only explanation; no per-page callouts.
6. **Expanders hide only secondary text** — never an action and never «что изменится / риск / как откатить». The
   Expander header is an outlined «ПОКАЗАТЬ N ▾» / «ПОДРОБНЕЕ ▾» button; «сейчас X → станет Y» is always visible.
7. **Tabs and filters look different.** Page tabs = a framed segmented control (`SegmentBar` + `Segment`, or TabControl;
   selected = white block). Filters = `Chip` / `Chip.Toggle` in one row with the search box. Counters = the one `TabBadge`
   style (`TabBadge.Alert` for attention), no Opacity, no «…».
8. **One page anatomy** (`c:PageHeader` → tabs → optional summary strip → list panel → list → `c:StickyBar`):
   header = title, subtitle, «проверено HH:MM» (`Checked`), the one word «ОБНОВИТЬ» (`RefreshCommand`), secondary
   `Actions`, ONE `Primary` button; the list is the page's only ScrollViewer; the sticky bar sits OUTSIDE the scroll in
   the last grid row with «ВЫБРАНО N · ПРИМЕНИТЬ · N». Navigation between pages is `Ui.Navigate` + `Ui.RequestFocus` to
   the exact row, never «see page X».
9. **Search is visible**: the title-bar «ПОИСК Ctrl+K» button opens the command palette; the index
   (`Shell/CommandPaletteIndex`) covers pages, tweaks, tools, services, device settings, rejected tweaks, the Apps catalog,
   cleanup items, autostart entries (live scan), Settings rows, Drivers tabs and Restore sections. The palette only opens.
10. **Sidebar** groups ОБЗОР · УСКОРЕНИЕ · ОБСЛУЖИВАНИЕ · ЗАЩИТА (`MainViewModel.NavGroupOf`) with a badge from
    `PageViewModel.BadgeText` — set from values the page already has (last analysis, journal count); never start work
    only for a badge.
11. **Compact stays** (cards 14 px, rows 12/8, buttons 30 px, title 24 px) — but never by hiding actions.
Before adding UI: the logic goes to its one page; the shortcut goes where the user looks for it.

### Shared shell pieces (use them, do not re-implement)

- `c:PageHeader` (`UI/Controls/PageHeader`): `Title`, `Subtitle`, `Checked` («проверено 15:40» — bind
  `PageViewModel.CheckedText`, set with `MarkChecked()`), `RefreshCommand` (+ `RefreshText`, default «ОБНОВИТЬ»),
  `Actions` (secondary buttons), `Primary` (the ONE `Btn.Primary`). Embedded in a host page it shows only the right side.
- `c:StickyBar` (`UI/Controls/StickyBar`): `Count`, `CountLabel` («ВЫБРАНО»), `Detail` («БУДЕТ ИЗМЕНЕНО 2»), `Text` (one
  muted line), `Actions` (last one = `Btn.Hero`). Place it in the last grid row, outside the page's ScrollViewer.
- Theme: `Btn*`, `SegmentBar` + `Segment`, `Chip` (RadioButton) / `Chip.Toggle` (ToggleButton), `TabBadge(.Alert)`,
  `Row` (hover = Surface3 + left bar), Expander (outlined header), `Tag*` (status only).
- `PageViewModel`: `BadgeText`, `CheckedAt`/`CheckedText`/`MarkChecked()`, `EnsureLoadedAsync()` (await the first load),
  `IsLoaded`, `RequireAdmin(action)` and `RequireAdmin(action, command, parameter)`.
- Shell: `MainViewModel.OpenSearchCommand` (palette), `RevertLastBatchCommand` / `OpenRestoreCenterCommand` (the title-bar
  «ОТКАТ · N ▾» menu; `LastBatchTitle` from the journal), toasts with labelled «ОТКРЫТЬ» / «ЗАКРЫТЬ», the status bar
  «ЖУРНАЛ ДЕЙСТВИЙ · N» button (the console), `DialogViewModel.OptionText/OptionChecked` (`Ui.ConfirmOptionAsync`).

### Admin intent replay (read-only mode)

`PageViewModel.RequireAdmin(action, command, parameter)` builds a `Shell/AdminIntent` {page key, command name without
the «Command» suffix, text parameter}. The relaunch carries it as `--replay-page … --replay-command … [--replay-param …]`
(`Elevation.RelaunchArguments` strips this run's `--page/--tab/--replay-*` first). The elevated copy (App → `PendingIntent`)
runs `MainViewModel.ReplayIntentAsync` after the startup checks: opens the page, awaits `EnsureLoadedAsync`, finds the
public ICommand property «{Command}Command» on the page (else on the shell) and executes it once with the parameter — so
the command's own checks and confirmation run as if the user had clicked. Nothing else is remembered or applied; a
command that cannot run gives a toast «повторите действие». The «Нужны права» dialog has the checkbox
«Всегда запускать Lycoris с правами администратора» (`AppSettings.ElevateAtStartup`). Tests: `AdminIntentTests`.
Pages: pass a command name for every action that needs rights (`RequireAdmin("Применение оптимизаций", "Apply")`,
`RequireAdmin("Откат пакета", "RevertBatch", batch.Id.ToString())` with a text-parameter-taking command).

## Concurrency (many tasks at once must never break the system)

Owner: «чтобы при выполнении много задач одновременно это не рушило систему и продолжала работать». Audit with evidence:
`docs/research/concurrency_audit.md`. **Every long or system-changing operation goes through `Core/OperationCoordinator`**
— never a private flag only. It decides what may run together and tells the user plainly what waits.

- **Classes** (`OperationClass`): `Install` (winget / vendor / driver / WU installers, driver backup+restore) and `Cleanup` (delete
  files) and `JournalChange` (apply/revert tweaks, services, tasks, startup, SPI…) → conflicting second operation **waits in the
  visible FIFO queue** («в очереди: …», cancellable). `SystemRepair` (DISM/SFC/chkdsk/defrag/network reset) and `Disruptive` (NIC
  restart, display, Game Boost start/stop, restore point, load test, Explorer restart) → **refused at once** with «Сейчас идёт
  установка Steam — дождитесь окончания» (`QueueIfBusy = true` for actions that must not be dropped: stopping a game session).
  `Measure` (read-only; `Quiet` for ping/DPC/energy: not beside install/repair/disruptive) and `Background` never block anything and
  run at most 3 at a time. Matrix: `OperationCoordinator.ClassesConflict` (tests: `OperationCoordinatorTests`).
- **Register a new long operation:** page work → `await RunBusyAsync(OperationClass.X, "Текст…", async () => …)` (or the overload
  that also gets the `OperationLease`: `lease.SetCancelHandler(...)` only for a SAFE stop, `lease.Report(...)`); call
  `CanStartNow(kind, name)` BEFORE the confirmation dialog for refusable classes. Own progress UI (queue panel): `AcquireLeaseAsync`
  + `using lease` + `lease.Scope()`. A service called from several places wraps itself with `OperationCoordinator.Instance.RunAsync`
  (nested calls of the same flow pass without waiting). Children started below a lease are nested automatically (async-local).
- **Rules:** never hold a lease across a dialog — ask with `AskWithoutLeaseAsync` / `lease.WhileUserDecidesAsync` (only before
  anything changed); never kill an installer or a repair tool (cancel only through a safe handler; otherwise the list says «прервать
  нельзя»); leases are released in `finally` (use `using` / `RunAsync`); a refusal is shown as text (`PageViewModel.OperationNote` →
  the strip under the title bar, plus a toast), never only a tooltip; short child processes (timeout ≤ 90 s) are capped at
  `ProcessRunner.MaxParallelProbes`; never add a second `RunBusyAsync` overload without a class.
- **Shell:** status-bar «ЗАДАЧИ · N» → list with ОТМЕНИТЬ; closing with operations running → `MainWindow.OnClosing` →
  `MainViewModel.ConfirmExitAsync` (ЖДАТЬ / ОТМЕНИТЬ БЕЗОПАСНЫЕ И ВЫЙТИ / ВЫЙТИ КАК ЕСТЬ; the exit watchdog and the journal save in
  `App.OnExit` stay). Admin-intent replay aborts when the page already has an operation (`UserOperationRunningFor`).
- **Checklist for a new long operation:** (1) class chosen and documented in the audit table; (2) goes through `RunBusyAsync` /
  `RunAsync`; (3) refusal/queue text is in Russian and names the blocker; (4) cancel only where safe; (5) no dialog under the lease;
  (6) a unit test with a fake clock or `PageOperationTests`-style fake page; (7) `--selftest` stays all OK (it fires read-only commands
  from every page at once in the stage «ОДНОВРЕМЕННЫЕ ЗАДАЧИ»); (8) `--snapshot … --demo-tasks` to look at the «ЗАДАЧИ» list.

## Conventions

## Простои и экспертный режимы

- Источник режима — `AppSettings.Current.ExpertMode`: «Простой» применяет только SAFE и OPTIONAL, «Эксперт» открывает все настройки.
- Переход в «Эксперт» проходит через единый `MainViewModel.RequestExpertModeAsync` с подтверждением; отказ оставляет простой режим, возврат не спрашивает.
- В «Простом» запрет настроек ADVANCED проверяется в логике Optimize при выборе и непосредственно перед применением; импорт профиля пропускает их.
- `ServicesExpertMode` действует только вместе с `ExpertMode`; сохранённое значение не стирается. Откат журнала разрешён в обоих режимах.
- Защитные настройки Windows из списка DO-NOT-INCLUDE остаются недоступны в обоих режимах.
- Экспертные настройки (`expert-…`, `TweakCatalog.Expert.cs`) — то, что Lycoris не рекомендует, но разрешает эксперту: только ADVANCED, с предупреждением и точным откатом; в профили не входят.

- **MVVM** with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`, `partial` classes).
  Each page VM derives from `Shell/PageViewModel`: override `LoadAsync` (first visit) / `OnReturnAsync`,
  `Deactivate` (stop timers). Long work: `await RunBusyAsync(OperationClass.X, "Текст…", async () => …)` — the class is
  mandatory (see «Concurrency»); shows the global progress bar, logs and toasts errors.
- **Threading:** never block the UI thread. WMI, registry scans, `ChangeSession` calls, file IO → `Task.Run`.
  Update bound collections on the UI thread (`Ui.Post` or await back on the dispatcher).
- **Text:** all UI text is written in Russian in the code and goes through `Loc` (see «Localization»). Page titles, section headings and item titles in sentence case («Временные файлы
  Windows»; bound all-caps titles go through the `Sentence` converter). UPPER CASE only for small labels (`T.Label`),
  tags, tabs and buttons. Body text in sentence case.
  Explain every action in plain language (what it does, why, risk, how to undo).
- **Logging:** `Log.Info/Success/Warn/Error` (shows in the in-app console + daily file).
- **External tools:** `ProcessRunner.RunAsync(ProcessRunner.Sys("tool.exe"), …)` with the right
  `OutputEncoding` (OEM for console tools on Russian Windows, Unicode for sfc, UTF-8 for winget/PowerShell).
  PowerShell snippets: `ProcessRunner.RunPowerShellAsync`.
- **Performance counters:** names are localized on Russian Windows — use `PdhAddEnglishCounter`, never
  `PerformanceCounter` with English names.
- No new NuGet packages without a very good reason (current: CommunityToolkit.Mvvm, System.Management,
  System.ServiceProcess.ServiceController). Data catalogs are C# code (single-file publish).

## Localization (Russian source, English lookup)

Owner: «Ещё нужно добавить английский язык». Full guide, glossary and style: `docs/localization.md`. **Every new or changed user-visible string
goes through `Loc`; the Russian original is the key** (`Loc.T("Применить")`, formatted `Loc.Tf("Выбрано {0}", n)`, plural `Loc.N(n, "{0} файл", "{0} файла", "{0} файлов")`,
XAML `{loc:T Применить}` with `xmlns:loc="clr-namespace:Lycoris.UI"`). Never `$"…"` with Russian text (use `Loc.Tf` / `Log.Info("… {0}", x)`), never a private
`ru-RU` culture (use `Loc.Culture`, `Loc.DateText/DateTimeText/TimeText/SecondsText`), never concatenate translated fragments.

- **Language:** `AppSettings.Language` = auto (Windows language: ru* → ru, else en) | ru | en; read once at startup (`Loc.Initialize`), changed in Settings → «Язык / Language»
  (restart with the same rights, `Ui.RestartApp`). `--lang ru|en` overrides it for one run (QA/screenshots/selftest, nothing saved).
- **Tables:** one embedded JSON file per area, `Core/Localization/en/<Area>.json` (`Common` = shared vocabulary and the glossary; `Shell`, `Settings` done as the reference).
  A new string = a new entry in the file of your area. A missing entry shows Russian (never crashes). `"translated": true` + `"sources"` make the coverage test
  (`LocalizationCoverageTests`) demand that every Cyrillic literal of those sources is wrapped and has an English entry.
- **Tools:** `tools\loc-scan.ps1` (report per module; `-Check <Area>`, `-Missing <Area>` numbered keys, `-Fill <Area> -File x.json`, `-New <Area> -Sources …`).
  Render English: `Lycoris.exe --snapshot snapshots\en --pages … --lang en [--demo-shell]` and LOOK at the PNGs; long English must not break the layout.
- **Rules:** ALL-CAPS Russian label ⇒ ALL-CAPS English label, sentences stay sentences; one term = one translation (glossary in the doc, `Common.json`);
  journal / settings / backups stay language-neutral (on-disk schema unchanged, texts are localized at display time); log FILES stay Russian, the console line is translated;
  tests keep Russian by default, English only inside `using var _ = Loc.UseLanguage("en");`.

## Design system (black & white + red accent + three muted semantic hues)

Black and white structure, adult, aggressive, modern. Hierarchy via luminance, weight, inversion (white blocks), hatching.
Colour only MARKS MEANING (owner, 02.11.2026: «немножечко добавить цвета, но не терять профессионализм»; research and sources:
`docs/ux/color-research.md`). Four hues, nothing else: **red** = brand, danger, failure, live (owner: «Красный акцент Dark»);
**green (Success)** = applied / OK / done / safe / recommended / toggle ON; **amber (Warning)** = needs attention, partial, pending, busy
(NOT danger); **steel blue (Info)** = information, optional, neutral progress and charts, measuring. Rules: colour is never the only carrier
(a label, glyph or shape always sits next to it); at most ONE hue per component; hue lives in small things (dot, 1 px outline, text, thin top
line, progress/meter fill) and in very dark tints (`*Deep`) with a hue outline — never a saturated large fill, never body text, never a theme
recolour, never a gradient, no neon, no rainbow; white fill still = clickable only (primary button stays white, ordinary buttons neutral).
Never put red, amber and green side by side at the same weight (traffic-light look): the risk vocabulary is БЕЗОПАСНО green · ПО ЖЕЛАНИЮ steel ·
ЭКСПЕРТ red. `ThemeContrastTests` enforces ≥ 4.5:1 for every hue's text on Base/Surface/Surface2/Surface3/its Deep, ≥ 3:1 for fills, hue families,
very dark Deep tints, muted (not neon) saturation, and pure-grey neutrals.
- Semantic tokens (`Theme/Colors.xaml`, `C.*` + `B.*`), each hue = Fill / Text / Deep: `Success` #2F8F5B / `SuccessText` #58BE86 / `SuccessDeep` #0B2016;
  `Warning` #B8893A / `WarningText` #E0AA4A / `WarningDeep` #2A1E08; `Info` #4F7FB0 / `InfoText` #78A6D6 / `InfoDeep` #0C1926. Styles: `Tag.Ok` / `Tag.Warn` /
  `Tag.Info` (Deep + 1 px Fill outline + Text), `Callout.Ok/Warn/Info`, `Dot.Green/Amber/Info`, `TabBadge.Alert` (amber block, ink text; neutral at 0), `Toggle` ON = green
  track + white knob, `ProgressBar` = steel blue, `c:Meter` = steel blue → amber (`WarnThreshold`) → red (`HotThreshold`; the value is printed beside it), toasts: Success green /
  Info blue / Warning amber / Error red edge + glyph. Applied: Dashboard (score number + 10-segment bar by tier ≥90 green / ≥70 amber / <50 red, thin top lines on the six tiles:
  game red, optimize+undo green, others blue; severity tags ВНИМАНИЕ amber / СОВЕТ blue / В НОРМЕ green), Optimize (state mark+text ПРИМЕНЕНО green, partial amber, ИЗМЕНИТСЯ blue,
  profile tiles' top line green for «Рекомендуемый» / blue for scenarios), Services/Autostart ПРИМЕНЕНО, Cleaner risk tags and the servicing band, Restore (drift band amber, protection
  on = green), Drivers status tags/glyphs, Devices safe tag. Deliberately NEUTRAL: ordinary buttons, segmented tabs (selected = white), nav (red marker only), chips, counters at 0,
  НЕ ПРИМЕНЕНО / НЕПРИМЕНИМО text, hatched bands, body text, tags `Tag.Muted/Solid`.
- Red tokens (`Theme/Colors.xaml`, `C.*` + `B.*`): `Red` #B3121F (borders, bars, dots, logo mark, small badge fill — white on it 6:1),
  `RedHover` #D11A29 (hover edge of a danger outline), `RedText` #EA5B63 (red text/glyphs; ≥ 4.5:1 on Base, Surface, Surface2, Surface3 and RedDeep),
  `RedDeep` #45080E (tint behind a risk tag/strip, always with a `Red` outline).
- Where red goes: the logo mark and the selected sidebar marker; `Tag.Risk` (= `Tag.Hatch`: ЭКСПЕРТ / problem / ВНИМАНИЕ — RedDeep + Red outline + RedText);
  `Callout.Risk`; `Btn.Danger` (red outline + RedText: ОТКАТИТЬ ВСЁ, СТОП, and the confirm button of
  `Ui.ConfirmAsync(danger: true)` instead of the white main button); `Dot.Red` (LIVE, update button, active game session); warning bands in `B.Red`
  (not the hatched band) only where the text is really bad (failed, blocked, risk); error toasts (red edge + glyph). Red is no longer used for progress bars,
  «needs attention» counters or drift/outdated notices — those are amber/blue.
- Brushes: `B.Base` (window), `B.Surface/2/3/4` (raised layers), `B.Line/2/3` (hairlines),
  `B.Text/2/3/4` (text levels), `B.White`, `B.Ink` (text on white), `B.Hatch`, `B.HatchStrong`, `B.DotGrid`, `B.ChartFill`,
  `B.Red`, `B.RedHover`, `B.RedText`, `B.RedDeep`, `B.Success/SuccessText/SuccessDeep`, `B.Warning/WarningText/WarningDeep`, `B.Info/InfoText/InfoDeep`.
- Fonts: `F.Display` (Bahnschrift — use Bold + Condensed for headings), `F.Body` (Segoe UI Variable),
  `F.Mono` (Cascadia Mono / Consolas — numbers, ids, logs), `F.Icons` (Segoe Fluent Icons; codes in `UI/Glyphs.cs`).
- Text styles: `T.Display`, `T.H1`, `T.H2`, `T.H3`, `T.Label` (small caps-like labels), `T.Body`, `T.BodyStrong`,
  `T.Caption`, `T.Mono`, `T.Stat` + `T.StatUnit` (big numbers), `T.Icon`.
- Buttons (all outlined `B.Outline`, 30 px, icon + text): default `Btn`, `Btn.Primary` (white block — the ONE main
  action), `Btn.Hero` (big primary, sticky bar), `Btn.Ghost` (outlined, muted text; secondary only), `Btn.Danger` (red outline, hatched
  at rest), `Btn.Link` (outlined jump to another page), `Btn.Icon` (glyph only — window caption ONLY), `Btn.Caption`.
- Inputs: CheckBox (R.Xs box, Outline border), `Toggle` (CheckBox style, R.Md rounded-rect switch with R.Xs knob),
  `SegmentBar` + `Segment` (framed segmented tabs, selected = white block), `Chip` / `Chip.Toggle` (filters next to the
  search box), `TabBadge` / `TabBadge.Alert` (counters), `Tile` (RadioButton card), TextBox (`Tag` = placeholder),
  ComboBox, Slider, ProgressBar (thin), TabControl/TabItem (segmented), Expander (outlined «ПОКАЗАТЬ ▾» header).
- Surfaces: `Card`, `Card.Flush`, `Card.Hover`, `Card.Inverted`, `Callout`, `Row` (list row with Surface3 hover + white
  left bar + bottom hairline), `Divider`, `DividerV`, tags `Tag`, `Tag.Solid` (Surface4, never white), `Tag.Hatch`
  (risk/expert), `Tag.Muted` — status only, never an action.
- Lists: `List.Plain` (ListBox without selection chrome).
- Converters: `BoolToVis`, `InverseBoolToVis`, `NullToVis` (param "invert"), `InverseBool`, `Bytes`, `Equals`,
  `EqualsToVis`, `Upper`, `Sentence` (all-caps → sentence case, keeps acronyms/brands), `Percent`.
- Controls (`xmlns:c="clr-namespace:Lycoris.UI.Controls"`): `c:PageHeader` (Title/Subtitle/Checked/RefreshCommand/
  Actions/Primary), `c:StickyBar` (Count/CountLabel/Detail/Text/Actions), `c:Meter` (segmented bar 0–100), `c:Sparkline`
  (Values = ObservableCollection<double>), `c:ArcGauge` (0–100).
- Clearly rounded corners, still strict (owner, 27.10: «вообще не округлен… сделай ещё более круглым, не такой
  заостренный, но строгим, агрессивным и профессиональным»). Only the shape tokens from Theme/Colors.xaml, via
  `{StaticResource R.*}` — never a literal radius, never change the values:
  `R.Xs` 4 — checkboxes, progress bars, scroll thumbs, slider track/thumb, meter segments, tiny badges, selection markers;
  `R.Sm` 6 — tags/chips (`Tag*`), small icon buttons (`Btn.Icon`);
  `R.Md` 8 — buttons, inputs, combo boxes, toggles, nav items, row/menu/palette highlights, tooltips, focus ring;
  `R.Lg` 12 — cards (`Card*`), callouts, tiles, list panels, stat strips, sticky bars, the read-only strip, the console panel;
  `R.Xl` 16 — popups, dialogs, toasts, menus, the command palette, the ComboBox dropdown.
  `*.D` doubles (`R.Xs.D` …) for Rectangle.RadiusX/RadiusY and drawn shapes (Meter cells: R.Xs, capped at ¼ of the cell).
  The window uses the Windows 11 rounded frame (`DWMWCP_ROUND` in MainWindow.ApplyDwmStyle).
  Highlights inside a rounded container are inset (nav items 10/8 px, palette rows and menu/dropdown items 4–8 px) and
  carry the selection marker as a short rounded bar on their left edge. No pills except toggles/badges.
  Hairlines stay straight: single-edge borders (`Row` bottom line, underline tabs, dividers, title/status bar lines) are
  never rounded; rows stacked in a card stay square — the card rounds and clips them.
  Children painted to the edge of a rounded surface (bars, inverted headers, hatched bands, first/last rows) are clipped
  with `ui:RoundClip.Content="True"` on the Border (UI/RoundClip.cs; `Card`, `Card.*` and `Callout` already set it). The clip
  is the border's inner area, so padded content is never cut. A filled rounded box with a same-colour outline must not be
  one Border with both (it shows a seam in the corners): CheckBox/Toggle draw fill and outline as two layers, Btn.Primary
  turns its border width into padding.
- COMPACT spacing (owner: «слишком разбросано»): cards 14 px padding,
  rows 12/8, 8–16 px gaps, buttons/inputs 30 px high, page title 24 px. The main list of a page must get most of the
  screen — no big hero blocks above it.
  Risk shown as tags: БЕЗОПАСНО (`Tag`), ПО ЖЕЛАНИЮ (`Tag.Muted`), ЭКСПЕРТ (`Tag.Hatch`).
- Page root: `Grid` rows = `c:PageHeader` (Title/Subtitle/Checked/RefreshCommand/Primary; Index is no longer shown) →
  tabs (`SegmentBar`) → summary strip → list panel (search + `Chip` filters) → the list, the page's ONLY `ScrollViewer`
  (`*` row) → `c:StickyBar` (Auto row, outside the scroll). The shell already adds 24/16 px margins.

## Updates (owner, 02.11.2026: «показывало, что есть обновление, нажать "обновить" в самой программе»)

`Core/Updates/*` + `Shell/UpdateController`. Owner flow and failure modes: `docs/releasing.md`; antivirus notes: `docs/antivirus.md`.
- The version has ONE source: `<Version>` in Lycoris.csproj (`AppVersion.Current`). Release = `tools/release.ps1` (publishes
  Lycoris.exe + Lycoris.exe.sha256 to the PUBLIC repo `bil-xvii/Lycoris-releases` and the private one; `-WhatIf` = dry run).
- The app asks ONLY `api.github.com/repos/bil-xvii/Lycoris-releases/releases/latest`, anonymously, ≤ 1 request / 12 h (ETag,
  cache in AppSettings), as an `OperationClass.Background` operation; a background failure is a log line, never a toast.
  `--demo-update` (with `--snapshot`) renders the «ОБНОВЛЕНИЕ x.y.z» button and the Settings block without network.
- Download = `UpdateDownloader` (HTTPS, host allow-list checked BEFORE every redirect hop, size 40–300 MB, SHA-256 from the
  .sha256 asset AND GitHub's digest, PE x64, signer must equal the running exe's signer). Streams through
  `VendorDownloads.CopyResponseToFileAsync` (shared with driver/installer downloads — extend it, never fork).
- **Never overwrite the running exe.** The old copy stages the verified file (`Lycoris.exe.new`, or runs it from
  `%LocalAppData%\Lycoris\Updates` elevated when the folder is protected), starts it as `--apply-update <old exe> <pid>`
  (headless, `UpdateApplier`), which waits for the old process, renames old → `.old`, COPIES itself to the old name, starts it,
  watches 10 s and rolls back on a crash. `UpdateCleanup` removes `.old/.new` one minute after a good start.
  Refused while any listed operation runs or a game session is active (`UpdateController.BlockerText`); a network/read-only
  location or a multi-file dev build is told plainly (`UpdateApplier.Plan`). Tests: `tests/Lycoris.Tests/Updates/*` (fake
  HTTP, fake file system, tiny real exes in a temp folder; no network, Lycoris.exe is never touched).

## Security (owner, 02.11.2026: «добавь защиту от взлома»)

Full audit, threat model, findings table and the owner's to-do list: `docs/security-audit.md`. Code: `Core/Security/*`; tests:
`tests/Lycoris.Tests/Security/*`; self-test stage «БЕЗОПАСНОСТЬ» runs `SecurityCorpus` (hostile inputs against every parser).
Attacker model: any program running as the same user (it writes Desktop, Downloads, `%LocalAppData%`, `%TEMP%`, HKCU, the user's Documents and
environment, and can start Lycoris.exe with any arguments). The elevated Lycoris must never trust anything such a program can write.

1. **No user-writable input is trusted by an elevated process without validation.** Settings, caches, markers, manifests, imported files: read with
   `SafeIo.ReadText/ReadBytes` (size limit, no links, `FileShare.Read`), parse with `System.Text.Json` into fixed types (never type names), XML only through
   `SafeXml` (no DTD), then clamp/validate (`SettingsValidator`, id regexes). A new parser gets a hostile case in `SecurityCorpus`.
2. **Never run, load or open a file from a user-writable place with the admin token.** Ask `PathTrust.WhyUserWritable(path)` (ACL walk: only SYSTEM/Administrators/
   TrustedInstaller may write) before an elevated process executes a tool found on PATH or in a user folder; `WingetClient` accepts only the app-execution alias;
   browsers/games/launchers stay de-elevated (rule 8). Temp files that an elevated process reads go to `SafeIo.NewPrivateTempFile` (admin-only folder when elevated), not `%TEMP%`.
3. **Child processes**: always through `ProcessRunner` (it removes DOTNET_STARTUP_HOOKS/COR_PROFILER/… and gives PowerShell a system-only `PSModulePath`; output is capped).
   Full System32 paths only. PowerShell scripts are constants or built from whitelisted GUIDs/numbers; a dynamic value goes through `PsText.Literal` (handles the
   typographic quotes U+2018/2019/201A/201B) or is refused. Never `cmd /c` with a user string.
4. **Starting Lycoris.exe again with rights** (`Elevation.TryRelaunchElevated/SameRights`, display guard, updater) must pass `SelfIntegrity.CheckBeforeElevation()`
   (hash re-check, hostile location, signature, unpacked natives). UAC binds to the exe path, not to its bytes. New self-starts: same gate.
5. **Launch arguments are attacker input.** `--replay-*` only for allow-listed commands (`AdminIntent.Replayable`) with a plain page key and a ≤ 4096-char printable
   parameter; a new replayable command must have its own confirmation dialog. `--e2e` only in Debug or with `Lycoris_E2E=1`. `--apply-update` validates its
   target/pid/hash (exit 13). Add no argument that changes the system without a confirmation.
6. **Process hardening** is applied by `Hardening` (module initializer, before any other code): DLL search = System32 + the runtime's native folders, image-load policy.
   Every `[DllImport]` names a System32 library (`DefaultDllImportSearchPaths(System32)` is assembly-wide); load anything else by full path after `PathTrust`/signature checks.
   Do not enable dynamic-code prohibition or signature-only policies (JIT, WPF, GPU drivers).
7. **Update channel** (`Core/Updates`): hash = integrity only; authenticity = the owner's ECDSA signature (`ReleaseSignature`, `tools/release-key.ps1`, key pinned in
   `PinnedPublicKey`); version inside the file must equal the release and be newer; the updater never touches a file that is not a Lycoris program outside `%WINDIR%`.
8. **Never add**: packers/obfuscators/anti-debug (antivirus false positives, no protection), network listeners/pipes without an ACL and a design review, credentials or
   keys in the repo (the release private key lives only in `%USERPROFILE%\.lycoris-release`), `HttpClient` certificate-validation callbacks, `http://` URLs.

Checklist for new code that reads a file/registry value/command line/network answer, starts a process or loads a library: (1) who can write this input? (2) size and
shape limit? (3) is the result used as a path, command, URL or registry key — validated against an allow-list? (4) does an elevated process use it without the user seeing it?
(5) hostile test in `SecurityCorpus` + a unit test; (6) `--selftest` stage «БЕЗОПАСНОСТЬ» still all OK.

## Build, run, verify

```
dotnet build src/Lycoris/Lycoris.csproj -c Debug
dotnet test tests/Lycoris.Tests/Lycoris.Tests.csproj
# QA screenshots of pages (no admin, no UAC): writes PNGs into the folder
src/Lycoris/bin/Debug/net10.0-windows10.0.19041.0/win-x64/Lycoris.exe --snapshot snapshots/run1 --pages dashboard,optimize --wait 2500
# tabbed pages are shot once per tab (07-devices-auto.png, 07-devices-manual.png; "--pages devices-manual" = that tab only);
# --full also saves the page's whole scrolled list (…-full.png). --page devices --tab manual opens a page on a tab.
# Release single-file exe
dotnet publish src/Lycoris/Lycoris.csproj -c Release -r win-x64 --self-contained true -o publish
```
```
# Full UI self-test (no admin, all system writes locked, dialogs auto-cancelled): report.md + PNG per page,
# exit code = number of failed pages. Run after every change set.
Lycoris.exe --selftest snapshots/selftest
```
On Linux (cloud sessions): build with `-p:EnableWindowsTargeting=true`; `tools/test-on-linux.sh` runs the unit tests
(~170 Windows-only tests fail there by design — judge only failures that don't involve registry/P/Invoke/paths/processes).
Unit tests include `TweakApplyRevertTests`: every RegistryTweak is applied and reverted against a redirected
registry (`RegistryHelper.Redirect`, tests only) and must restore every value exactly.

Screenshots on Linux: publish win-x64 self-contained and run `Lycoris.exe --snapshot` under Wine (wine64 + xvfb-run) with
Liberation/DejaVu fonts renamed to Segoe UI/Bahnschrift/Consolas and registered in the prefix (WPF needs them in
HKLM\...\Fonts, else it FailFasts in text shaping). Icons render as boxes there (no Segoe Fluent Icons).
By default the app opens without a UAC prompt (read-only) and `PageViewModel.RequireAdmin` offers a relaunch with
admin rights on the same page when a change needs them; Settings → «Запрашивать права администратора при запуске»
(`AppSettings.ElevateAtStartup`) restores the prompt at startup. `--no-elevate` always stays read-only.
Never apply real tweaks on the dev machine during automated testing — use read-only paths and unit tests
(registry tests use `HKCU\Software\Lycoris-Tests`). The same for devices: no display mode/HDR/DDC/SPI/audio level/NIC
writes on the dev PC — tests use the fakes (`DeviceWriters.OverrideForTests`), the redirected registry and
`ChangeJournal.ForTests`; read-only probing (enumeration, WMI, EDID) is fine. `--display-guard …` is an internal switch
(Lycoris starts it for a display change); never run it by hand against real displays.

## Status

- Done: core (journal/undo with record validation, restore points, services, power plans, tasks, registry, process
  runner, winget), theme, shell, all modules, tweak catalog, review findings in docs/review-findings.md,
  minimal-UI redesign, command palette, profile export/import, drift detection, Drivers «ПОДБОР ДЛЯ ВАШЕГО ПК»
  hardware analysis (Modules/Drivers/Hardware, default Drivers tab), direct NVIDIA/AMD installer downloads on the GPU cards,
  «Устройства и сеть» (Modules/Devices: core + input, displays, audio, vendors, network; research docs/research/devices_*.md,
  network_*.md, design docs/research/devices_design.md).
- Verified on Windows 11 (28.10.2026): build 0 warnings, all unit tests pass, `--selftest` all pages OK (incl.
  devices/auto and devices/manual). Device writes (SPI, display mode/HDR + guard, DDC, mic level, NIC) and the live
  measurements are unit-tested with fakes; on 02.11.2026 the owner authorised real-hardware checks: opt-in tests `tests/Lycoris.Tests/Hardware` (`Lycoris_HW_TESTS=1`), results in `docs/hardware-verification-2026-11.md` (§5 b–d, f done; a, e, g open).
- Two Claude sessions once developed in parallel (a cloud one on `main`, a local one); the local line is kept on branch
  `local/windows-line` for reference only. Before starting work: `git fetch` and build on top of `origin/main`.
- Never rename, overwrite or delete Lycoris.exe while it runs (single-file app: it loads parts of itself from the
  file lazily — after a swap its shutdown crashed and left a windowless process holding the instance lock, so the app
  "did not open"). Ask the owner to close it first. Since 27.10: an exit watchdog ends the process 20 s after the main
  window closes, and a new start waits for / offers to end a hung copy instead of exiting silently.
- WPF rule learned the hard way: never call ScrollIntoView/UpdateLayout inside a CollectionChanged handler of the bound
  collection — defer it with `Dispatcher.BeginInvoke(..., DispatcherPriority.Background)` (crash: "ItemsControl is
  inconsistent with its items source").
