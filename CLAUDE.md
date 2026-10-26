# MONOLITH — project guide

MONOLITH is a Windows 10/11 PC optimizer and game booster (C# / WPF / .NET 10, Russian UI).
Owner: GitHub `bil-xvii`, private repo `bil-xvii/MONOLITH`. Everything is auto-committed and pushed
by `tools/autosave.ps1` (Claude Code Stop hook in `.claude/settings.json`).

## Prime directive: never damage the system

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

Pages (key → folder): dashboard, optimize, games, cleaner, drivers, services, startup, apps, tools, restore, settings.
Navigation: `Ui.Navigate("optimize")`.

## Conventions

- **MVVM** with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`, `partial` classes).
  Each page VM derives from `Shell/PageViewModel`: override `LoadAsync` (first visit) / `OnReturnAsync`,
  `Deactivate` (stop timers). Long work: `await RunBusyAsync("Текст…", async () => …)` — shows the global
  progress bar, logs and toasts errors.
- **Threading:** never block the UI thread. WMI, registry scans, `ChangeSession` calls, file IO → `Task.Run`.
  Update bound collections on the UI thread (`Ui.Post` or await back on the dispatcher).
- **Text:** all UI text in Russian. Headings/labels/buttons in UPPER CASE; body text in sentence case.
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
- Inputs: CheckBox (square), `Toggle` (CheckBox style, rectangular switch), `Segment` (RadioButton tabs with
  underline), `Tile` (RadioButton card), TextBox (`Tag` = placeholder), ComboBox, Slider, ProgressBar (thin),
  TabControl/TabItem (underline), Expander.
- Surfaces: `Card`, `Card.Flush`, `Card.Hover`, `Card.Inverted`, `Callout`, `Row` (list row with hover + bottom
  hairline), `Divider`, `DividerV`, tags `Tag`, `Tag.Solid`, `Tag.Hatch` (risk/expert), `Tag.Muted`.
- Lists: `List.Plain` (ListBox without selection chrome).
- Converters: `BoolToVis`, `InverseBoolToVis`, `NullToVis` (param "invert"), `InverseBool`, `Bytes`, `Equals`,
  `EqualsToVis`, `Upper`, `Percent`.
- Controls (`xmlns:c="clr-namespace:Monolith.UI.Controls"`): `c:PageHeader` (Index/Title/Subtitle/Actions),
  `c:Meter` (segmented bar 0–100), `c:Sparkline` (Values = ObservableCollection<double>), `c:ArcGauge` (0–100).
- Square corners everywhere (no CornerRadius). Generous spacing (cards 20 px padding, 16–24 px gaps).
  Risk shown as tags: БЕЗОПАСНО (`Tag`), ПО ЖЕЛАНИЮ (`Tag.Muted`), ЭКСПЕРТ (`Tag.Hatch`).
- Page root: `Grid` with `c:PageHeader` on top (bind Index/Title/Subtitle from the VM), content below with
  its own `ScrollViewer` when needed. The shell already adds 40/30 px margins.

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
Unit tests include `TweakApplyRevertTests`: every RegistryTweak is applied and reverted against a redirected
registry (`RegistryHelper.Redirect`, tests only) and must restore every value exactly.

Running the app normally triggers a UAC prompt; use `--no-elevate` to test without admin.
Never apply real tweaks on the dev machine during automated testing — use read-only paths and unit tests
(registry tests use `HKCU\Software\MONOLITH-Tests`).

## Status

- Done: core (journal/undo, restore points, services, power plans, tasks, registry, process runner, winget),
  theme, shell, page stubs, research.
- In progress: module implementations (one folder each), full tweak catalog.
