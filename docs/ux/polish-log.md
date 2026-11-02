# UX polish log (02.11.2026)

Method: `--snapshot` of all pages (ru, `--lang en`, `--full`, `--demo-update`, `--demo-shell`) at 1440x900 and at the window minimum
(new QA flag `--size 1120x720`). Contact sheet before | after: `polish-preview.png` (ru 1440, ru Settings, en at 1120x720).

| Was wrong | Now |
|---|---|
| Update status/buttons sat at the very bottom of Settings (About) | «Обновления» block is the first thing in Settings: installed/available version, last-check result text (`StatusText`), ПРОВЕРИТЬ ОБНОВЛЕНИЯ / ОБНОВИТЬ СЕЙЧАС / ПОЗЖЕ / ПРОПУСТИТЬ. «О программе» keeps version/build. Toast text updated |
| Release notes showed «ЧТО НОВОГО» twice (label + first note line) | `ReleaseInfo.NotesPreview` drops a leading identical heading |
| Page subtitles were cut with «…» at the minimum width | `PageHeader` subtitle wraps (2 lines) |
| Truncated paths/titles without full text anywhere | `UI/UiA11y`: any TextBlock cut by TextTrimming gets its full text as tooltip on hover (app-wide) |
| Buttons exposed no names to UI Automation (icon + text in a StackPanel) | `UiA11y` derives `AutomationProperties.Name` from the visible text (icons skipped), refreshed on rebinding; switches/boxes without text take their row title; XAML-set names win. Test `UiA11yTests` |
| Optimize profile tiles: description cut («Безопасный баланс для люб…») | wraps to 2 lines (+ tooltip) |
| Dashboard tiles: detail cut at 2 lines, «CHECK VERSION» button clipped at 1120 px | detail up to 3 lines; tile buttons wrap inside the tile (DockPanel, no fixed MaxWidth) |
| Games «Закрытие программ»: unframed segment, first letter clipped (asymmetric padding) | framed `SegmentBar`, symmetric padding |
| Games toolbar at minimum width: platform chips wrapped into a column | toolbar is a WrapPanel — whole groups wrap to the next row |
| Apps cards: advice line duplicated the description and was cut | wraps (full text) |
| Drivers tab badge showed «…» (against rule 7) | badge empty while searching |

Left for the owner / other areas (not UI-presentation):
- Restore shows a journal note stored in English («Windows did not create a new point…») — it was saved localized by an earlier `--lang en` run; storing neutral text is a Core/journal change.
- `--snapshot --demo-shell --demo-tasks` together hangs after the task-list shot (pre-existing, QA flow only).
- 125/150 % DPI was not rendered (no DPI flag; layout is DIP-based, the 1120x720 minimum is the stricter test).
- Dashboard «Считаем…» tile text is a transient state while the cleaner analysis runs.
- UIA names for lists/ComboBox/TextBox are derived heuristically (placeholder / row title); named lists are not set.
