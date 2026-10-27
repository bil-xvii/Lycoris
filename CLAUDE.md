# MONOLITH — project guide

MONOLITH is a Windows 10/11 PC optimizer and game booster (C# / WPF / .NET 10, Russian UI).
Owner: GitHub `bil-xvii`, private repo `bil-xvii/MONOLITH`. Everything is auto-committed and pushed
by `tools/autosave.ps1` (Claude Code Stop hook in `.claude/settings.json`).

## Prime directive: never damage the system

Goal (owner): MONOLITH must be the best PC-optimizer on the market — more effective, more honest and better
designed than every competitor — while never breaking or damaging the system. When effectiveness and safety
conflict, safety wins; the advantage comes from evidence-backed tweaks, exact undo and honest explanations,
not from riskier changes.

1. **Every system change goes through `Core/ChangeSession`** (`SetRegistry`, `SetServiceStart`, `SetPowerScheme`,
   `SetTaskEnabled`, `RunCommandAsync`). It records the original value in the undo journal
   (`%ProgramData%\MONOLITH\journal.json`) *before* changing anything, exports touched registry keys to `.reg`
   backups, and creates a System Restore point per batch. Never write to the registry / services / power plans
   directly from a module (reading is fine).
2. Only documented, reversible, evidence-backed changes. Research lives in `docs/research/*.md` — its
   classifications (SAFE-DEFAULT / SAFE-OPTIONAL / ADVANCED / DO-NOT-INCLUDE) are binding.
   DO-NOT-INCLUDE items must not be implemented, not even hidden.
3. Never: disable Windows Update / Defender / firewall / UAC / SmartScreen, delete system files, clean the
   registry, disable the page file, SysMain on SSD "fixes", core-parking hacks, bcdedit tweaks, remove Store
   apps silently, pass `--ignore-security-hash`/`--force`/`--allow-reboot` to winget, delete user data
   (Downloads, Documents, browser cookies/passwords/history) by default.
4. `Core/ServiceControl.Protected` services are never modified. Cleaner never follows reparse points.
5. Destructive or irreversible actions (deleting files, uninstalling, driver installs) always show a confirmation
   dialog (`Ui.ConfirmAsync`) listing exactly what will happen.
6. Without admin rights the app runs read-only: check `Elevation.IsElevated` / `PageViewModel.RequireAdmin`.
7. `%ProgramData%\MONOLITH` is locked to Administrators/SYSTEM (`AppPaths.SecureMachineDir`); a non-elevated
   process never writes there. The journal is treated as untrusted input: undo commands are limited to System32
   tools and autostart/hijack registry locations are refused (`ChangeJournal.IsForbiddenRestoreTarget`).
8. Browsers, games and launchers are never started with MONOLITH's admin token: use
   `UnelevatedShell.OpenAsync` (web pages) / `UnelevatedShell.StartAsUserAsync` (programs, launcher URIs).
9. Downloads of driver installers only via `Modules/Drivers/DriverDownloads` (vendor HTTPS hosts allow-list +
   valid vendor Authenticode signature, files kept in the admin-only machine folder).

## Layout

```
src/Monolith/
  App.xaml(.cs)          startup, UAC relaunch, single instance, --snapshot QA mode
  Core/                  system layer (no UI): AppPaths, Log, Elevation, ProcessRunner, RegistryHelper,
                         ChangeJournal, ChangeSession, ServiceControl, PowerSchemes, ScheduledTasks,
                         RestorePointService, SystemProfile, AppSettings, WingetClient
  Tweaks/                Tweak model (RegistryTweak, ServiceTweak, ScheduledTaskTweak, CustomTweak),
                         TweakEngine (apply/revert via journal), TweakCatalog (+ partial files with items)
  Shell/                 MainWindow (custom chrome, sidebar, console, toasts, dialogs), MainViewModel, PageViewModel
  UI/                    Ui facade (ConfirmAsync, AlertAsync, Toast, Navigate, Post), Glyphs, Converters,
                         Controls/ (PageHeader, Meter, Sparkline, ArcGauge)
  Theme/                 Colors.xaml, Typography.xaml, Controls.xaml, Converters.xaml
  Modules/<Name>/        one folder per page: <Name>View.xaml(.cs) + <Name>ViewModel.cs + module services
tests/Monolith.Tests/    xUnit
docs/research/           research reports (competitors, tweaks, cleaner, drivers, services, apps catalog, tech)
tools/                   autosave.ps1, make-icon.ps1, extract-research.ps1
```

Pages (sidebar order): dashboard, optimize, games, services («АВТОЗАПУСК И СЛУЖБЫ»: tabs АВТОЗАПУСК | СЛУЖБЫ |
ВСЕ СЛУЖБЫ | ЗАДАЧИ; hosts Modules/Startup), maintenance («ОБСЛУЖИВАНИЕ»: tabs ОЧИСТКА | РЕМОНТ И ДИСКИ; hosts
Modules/Cleaner + Modules/Tools), drivers, apps, restore; settings opens from the gear in the sidebar footer.
Navigation: `Ui.Navigate("optimize")`. Aliases: "startup" → services tab, "cleaner"/"tools" → maintenance tab.
Deep links: `Ui.RequestFocus(pageKey, id)` (optimize: tweak id; services: "tab:…" or service name). Ctrl+K = command palette.

## Minimal UI (owner rule)

The UI is minimal but functional: every function lives in exactly ONE place; no duplicates across pages.
- Bulk undo and restore points (incl. the auto-point toggle) → Restore only; per-row ОТКАТИТЬ stays on its row.
- Game-boost options → Games; gaming readiness checks → Dashboard; Windows settings as tweaks → Optimize only
  (Dashboard fix buttons are shortcuts into the same tweaks); DiagTrack → Optimize; vendor driver tools → Drivers;
  hardware facts → Dashboard; services expert mode → ВСЕ СЛУЖБЫ toolbar.
- Read-only mode: one global strip under the title bar — no per-page read-only callouts.
- No explanatory hero blocks, numbered section captions or "how it works" panels on pages; one muted line or a
  tooltip instead. The full safety explanation lives once in Settings → О ПРОГРАММЕ.
Before adding UI, check whether the function already exists elsewhere and extend that place instead.

## Conventions

- **MVVM** with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`, `partial` classes).
  Each page VM derives from `Shell/PageViewModel`: override `LoadAsync` (first visit) / `OnReturnAsync`,
  `Deactivate` (stop timers). Long work: `await RunBusyAsync("Текст…", async () => …)` — shows the global
  progress bar, logs and toasts errors.
- **Threading:** never block the UI thread. WMI, registry scans, `ChangeSession` calls, file IO → `Task.Run`.
  Update bound collections on the UI thread (`Ui.Post` or await back on the dispatcher).
- **Text:** all UI text in Russian. Page titles, section headings and item titles in sentence case («Временные файлы
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

## Design system (black & white, adult, aggressive, modern)

Strict monochrome — no hue anywhere. Hierarchy via luminance, weight, inversion (white blocks), hatching.
- Brushes: `B.Base` (window), `B.Surface/2/3/4` (raised layers), `B.Line/2/3` (hairlines),
  `B.Text/2/3/4` (text levels), `B.White`, `B.Ink` (text on white), `B.Hatch`, `B.HatchStrong`, `B.DotGrid`, `B.ChartFill`.
- Fonts: `F.Display` (Bahnschrift — use Bold + Condensed for headings), `F.Body` (Segoe UI Variable),
  `F.Mono` (Cascadia Mono / Consolas — numbers, ids, logs), `F.Icons` (Segoe Fluent Icons; codes in `UI/Glyphs.cs`).
- Text styles: `T.Display`, `T.H1`, `T.H2`, `T.H3`, `T.Label` (small caps-like labels), `T.Body`, `T.BodyStrong`,
  `T.Caption`, `T.Mono`, `T.Stat` + `T.StatUnit` (big numbers), `T.Icon`.
- Buttons: default outline `Btn`, `Btn.Primary` (white block), `Btn.Hero` (big primary), `Btn.Ghost`,
  `Btn.Danger` (hatched on hover), `Btn.Icon` (content = glyph), `Btn.Link`.
- Inputs: CheckBox (R.Xs box), `Toggle` (CheckBox style, R.Md rounded-rect switch with R.Xs knob), `Segment` (RadioButton
  tabs with underline), `Tile` (RadioButton card), TextBox (`Tag` = placeholder), ComboBox, Slider, ProgressBar (thin),
  TabControl/TabItem (underline), Expander.
- Surfaces: `Card`, `Card.Flush`, `Card.Hover`, `Card.Inverted`, `Callout`, `Row` (list row with hover + bottom
  hairline), `Divider`, `DividerV`, tags `Tag`, `Tag.Solid`, `Tag.Hatch` (risk/expert), `Tag.Muted`.
- Lists: `List.Plain` (ListBox without selection chrome).
- Converters: `BoolToVis`, `InverseBoolToVis`, `NullToVis` (param "invert"), `InverseBool`, `Bytes`, `Equals`,
  `EqualsToVis`, `Upper`, `Sentence` (all-caps → sentence case, keeps acronyms/brands), `Percent`.
- Controls (`xmlns:c="clr-namespace:Monolith.UI.Controls"`): `c:PageHeader` (Index/Title/Subtitle/Actions),
  `c:Meter` (segmented bar 0–100), `c:Sparkline` (Values = ObservableCollection<double>), `c:ArcGauge` (0–100).
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
- Page root: `Grid` with `c:PageHeader` on top (bind Title/Subtitle; Index is no longer shown), content below with
  its own `ScrollViewer` when needed. The shell already adds 24/16 px margins.

## Build, run, verify

```
dotnet build src/Monolith/Monolith.csproj -c Debug
dotnet test tests/Monolith.Tests/Monolith.Tests.csproj
# QA screenshots of pages (no admin, no UAC): writes PNGs into the folder
src/Monolith/bin/Debug/net10.0-windows10.0.19041.0/win-x64/MONOLITH.exe --snapshot snapshots/run1 --pages dashboard,optimize --wait 2500
# Release single-file exe
dotnet publish src/Monolith/Monolith.csproj -c Release -r win-x64 --self-contained true -o publish
```
```
# Full UI self-test (no admin, all system writes locked, dialogs auto-cancelled): report.md + PNG per page,
# exit code = number of failed pages. Run after every change set.
MONOLITH.exe --selftest snapshots/selftest
```
On Linux (cloud sessions): build with `-p:EnableWindowsTargeting=true`; `tools/test-on-linux.sh` runs the unit tests
(~170 Windows-only tests fail there by design — judge only failures that don't involve registry/P/Invoke/paths/processes).
Unit tests include `TweakApplyRevertTests`: every RegistryTweak is applied and reverted against a redirected
registry (`RegistryHelper.Redirect`, tests only) and must restore every value exactly.

Screenshots on Linux: publish win-x64 self-contained and run `MONOLITH.exe --snapshot` under Wine (wine64 + xvfb-run) with
Liberation/DejaVu fonts renamed to Segoe UI/Bahnschrift/Consolas and registered in the prefix (WPF needs them in
HKLM\...\Fonts, else it FailFasts in text shaping). Icons render as boxes there (no Segoe Fluent Icons).
Running the app normally triggers a UAC prompt; use `--no-elevate` to test without admin.
Never apply real tweaks on the dev machine during automated testing — use read-only paths and unit tests
(registry tests use `HKCU\Software\MONOLITH-Tests`).

## Status

- Done: core (journal/undo with record validation, restore points, services, power plans, tasks, registry, process
  runner, winget), theme, shell, all modules, tweak catalog, review findings in docs/review-findings.md,
  minimal-UI redesign, command palette, profile export/import, drift detection, Drivers «ПОДБОР ДЛЯ ВАШЕГО ПК»
  hardware analysis (Modules/Drivers/Hardware, default Drivers tab), direct NVIDIA/AMD installer downloads on the GPU cards.
- Verified on Windows 11 (27.10.2026): build 0 warnings, all unit tests pass, `--selftest` all pages OK.
- Two Claude sessions once developed in parallel (a cloud one on `main`, a local one); the local line is kept on branch
  `local/windows-line` for reference only. Before starting work: `git fetch` and build on top of `origin/main`.
- WPF rule learned the hard way: never call ScrollIntoView/UpdateLayout inside a CollectionChanged handler of the bound
  collection — defer it with `Dispatcher.BeginInvoke(..., DispatcherPriority.Background)` (crash: "ItemsControl is
  inconsistent with its items source").
