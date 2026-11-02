# Localization (Russian + English)

Lycoris speaks two languages: **Russian is the source language** (every string in the code is Russian and stays exactly as it is),
**English is a lookup** on top of it. The language is chosen once at startup (Settings → «Язык / Language»: АВТО | РУССКИЙ | ENGLISH;
АВТО = the Windows display language, `ru*` → Russian, anything else → English) and is fixed for the whole run. Changing it asks
«Перезапустить сейчас? / Restart now?» and relaunches Lycoris with the same rights on the same page, so there are never half-translated screens.

```
Russian text in code  ──Loc.T("Применить")──►  ru: «Применить»   en: table["Применить"] = «Apply»   (missing → Russian + remembered)
```

No NuGet packages, no reflection at runtime, nothing generated: `Core/Localization/Loc.cs` + one embedded JSON file per area.

## 1. The API (10 lines)

```csharp
Title = Loc.T("ПАНЕЛЬ");                                          // a constant text
Text  = Loc.Tf("Выбрано {0} из {1}", n, total);                   // a formatted text; the RUSSIAN FORMAT is the key
Text  = Loc.N(count, "{0} файл", "{0} файла", "{0} файлов");      // a counted phrase: «5 файлов» / «1 file», «5 files»
Word  = Loc.Plural(count, "файл", "файла", "файлов");             // only the word form (no number)
Ui.Toast("Настройки сброшены.", ToastKind.Success);               // sinks (Ui.*, Log.*) translate by themselves
Log.Warn("Не удалось сохранить: {0}", ex.Message);                // Log: Russian format for the file, translated line for the console
static readonly Item Row = new(Loc.Key("Игровой режим"));         // Loc.Key = «this literal is a key, it is translated where shown» (tables, catalogs)
var s = date.ToString("g", Loc.Culture); // or Loc.DateText / Loc.DateTimeText / Loc.TimeText / Loc.SecondsText — never a private ru-RU culture
Alias = Loc.Keep("мышь клавиатура");                              // stays Russian on purpose (search aliases, Windows output patterns)
```

XAML (declare `xmlns:loc="clr-namespace:Lycoris.UI"` once per file):

```xml
<TextBlock Text="{loc:T Применить}" ToolTip="{loc:T 'Открыть, закрыть'}" />        <!-- quote a text with a comma or {…} -->
<TextBlock Text="{Binding N, StringFormat={loc:T 'Выбрано: {0}'}}" />               <!-- translated format -->
<TextBlock Text="{Binding Title, Converter={x:Static loc:LocConverter.Instance}}" /> <!-- a bound Russian string -->
```

`Loc` is available in every file of `src/Lycoris` without a `using` (global using in the csproj). Tests use
`using var _ = Loc.UseLanguage("en");` (per async flow: parallel tests never disturb each other); the test host stays Russian.

### Which call for which situation

| Situation | Use |
|---|---|
| A text shown in the UI (property, field initializer, view-model, converter) | `Loc.T("…")` |
| Text with numbers/names inside | `Loc.Tf("… {0} …", args)` — never `$"…"` |
| A number + a noun | `Loc.N` / `Loc.Plural` (replace the module's own `Plural(n, "файл", "файла", "файлов")`) |
| Static tables / catalogs / records filled once (tweaks, services, apps…) | `Loc.T("…")` at construction (the language never changes during a run) or `Loc.Key("…")` + `Loc.T(item.Title)` where shown |
| Dialog / toast / log line | pass the Russian literal directly to `Ui.ConfirmAsync/AlertAsync/ChooseAsync/ConfirmOptionAsync/Toast` or `Log.*` — they translate by themselves (exact match); with a format use `Log.Info("… {0}", x)` |
| A text that must stay Russian (a pattern for Windows output on a Russian OS, a search alias, the names «РУССКИЙ»/«ENGLISH») | `Loc.Keep("…")` or `// loc:ignore` on the same line |
| A text built from pieces that grammar cannot split (word order, case, gender) | one `Loc.Tf` with the whole sentence — never concatenate translated words |
| Text that arrives from outside (Windows, winget, exception messages of the BCL) | leave it; show it as is |

Names that the tables do not know are **never lost**: they fall back to the Russian text (and `Loc.MissingKeys` remembers them).

## 2. Files and areas

```
src/Lycoris/Core/Localization/
  Loc.cs                 the API (Loc, LocArea, LocAreas)
  en/Common.json         shared vocabulary: states, risks, units, page titles, glossary terms
  en/Shell.json          the shell: Shell/**, UI/**, App, Elevation, OperationCoordinator…  (translated)
  en/Settings.json       Modules/Settings/**                                              (translated)
  en/<Area>.json         one file per area — each agent owns exactly one file, so parallel work never conflicts
src/Lycoris/UI/LocXaml.cs   {loc:T}, LocConverter, LocFormatConverter
tools/LocScan/               the scanner / helper (LocScanner.cs is also compiled into the tests); wrapper tools/loc-scan.ps1
tests/Lycoris.Tests/Localization/   LocTests, LocalizationCoverageTests
```

A file (embedded as `Lycoris.Loc.en.<name>.json`, picked up automatically by the wildcard in the csproj — no registration code):

```json
{
  "area": "Optimize",
  "translated": false,
  "sources": ["Modules/Optimize/**", "Tweaks/*"],
  "ignore": ["a Russian literal that must stay Russian"],
  "en": {
    "ПРИМЕНИТЬ": "APPLY",
    "Выбрано {0} из {1}": "{1} items, {0} selected",
    "{0} файл": "{0} file|{0} files"
  }
}
```

* **key** = the exact Russian text (after C# unescaping; `\n` is a real newline; concatenated literals are one key). A formatted text's key is
  the Russian *format* with `{0}` placeholders. English may reorder placeholders but must keep the same set.
* **plural** keys are the first (singular) form of `Loc.N` / `Loc.Plural`; the value is `"one|other"`.
* **ALL-CAPS Russian ⇒ ALL-CAPS English** (labels, tags, tabs, buttons: `ПРИМЕНИТЬ` → `APPLY`); a sentence stays a sentence. A test enforces it
  (brand spellings such as `winget`, `GeForce` are exempt). Page titles are written in caps in code and shown through the `Sentence`
  converter, which in English makes `STARTUP & SERVICES` → «Startup & services».
* The same Russian text has **one** translation in the whole app: the first definition wins and a different second one fails the test
  (`Common` is loaded first). Put words used on several pages into `Common.json`.
* `translated: true` + `sources` are what the coverage test checks (see §4). Set `translated` only when the area is finished.
* The file is plain UTF-8 JSON (comments and trailing commas are tolerated). Keep it sorted the way the tool writes it — don't reformat.

### Workflow for one area (what a page agent does)

1. `tools\loc-scan.ps1 -New Optimize -Sources "Modules/Optimize/**,Tweaks/*"` (or edit `sources` of your file).
2. In the sources wrap every Cyrillic literal (`Loc.T`, `Loc.Tf`, `Loc.N`, `Loc.Key`, `{loc:T …}`); replace `$"…{x}…"` with `Loc.Tf`/`Log.*("… {0}", x)`;
   replace private `Plural` helpers and `ru-RU` cultures with `Loc.Plural`/`Loc.N` and `Loc.Culture`/`Loc.*Text`.
3. `tools\loc-scan.ps1 -Check Optimize` lists what is still bare or has no English entry; `-Missing Optimize` prints the missing keys **numbered**.
4. Write the English as `{"1": "…", "2": "…"}` and merge it: `tools\loc-scan.ps1 -Fill Optimize -File fill.json` (the numbers belong to the last `-Missing`; you never retype Russian).
5. Repeat until `-Check Optimize` says `OK`, set `"translated": true`, run the tests, render the page: `Lycoris.exe --snapshot snapshots\x --pages optimize --lang en` and **look at the PNGs**
   (long English must not break the layout: trim/wrap, never shrink fonts below the design minimum).
6. Progress of everyone: `tools\loc-scan.ps1` (per module: *to do* = bare literals, *keyed*, *no EN*).

## 3. Scanner rules (what the tests accept)

In the sources of a translated area every **Cyrillic string literal** must be one of:

* the first argument (or `+`-joined literals) of `Loc.T`, `Loc.Tf`, `Loc.Key`, `Loc.Keep`, the 2nd–4th of `Loc.Plural`/`Loc.N`;
* an argument of a sink: `Ui.Toast`, `Ui.ConfirmAsync`, `Ui.AlertAsync`, `Ui.ChooseAsync`, `Ui.ConfirmOptionAsync`, or the first argument of `Log.Info/Success/Warn/Error/Command`;
* in XAML: `{loc:T …}` (alone or inside a binding's `StringFormat`);
* on a line with `// loc:ignore` (XAML: `<!-- loc:ignore -->` on the same line), or listed in the file's `"ignore"`.

Not accepted (reported with a reason): bare literals, **interpolated strings with Cyrillic** (`$"Выбрано {n}"`), a ternary inside the call
(`Loc.T(a ? "А" : "Б")` → write `a ? Loc.T("А") : Loc.T("Б")`), Cyrillic text in XAML element content. Every accepted key must exist in some `en/*.json`.
Comments and `///` docs are ignored.

## 4. Journal, logs, data stay language-neutral

* **Data on disk never depends on the language.** `journal.json` keeps ids, registry paths, original values and the Russian titles that were written when
  the record was made; **the schema is not changed**. Old records with Russian titles are shown as they are (or through `Loc.Sink`, which translates
  a Russian title the tables know — the table is the single place for such terms). New code stores ids/neutral data and localizes **when it displays**.
* Texts shown from the journal (Restore page, toasts, undo dialogs) are built at display time with `Loc`.
* **Log files** (`%LocalAppData%\Lycoris\Logs\lycoris-*.log`) are written in Russian (the Russian text of the call), so reports from users are uniform.
  The in-app console shows the same line in the interface language (`LogEntry.Display`; `LogEntry.Message` stays Russian). Dynamic data inside a line
  (a page title, an operation name) may already be English.
* Settings files, `.reg` backups, profile exports: no UI text.

## 5. English style

* Natural, short, gamer-friendly English — **not a calque**. «Отключить лишнее» = «Disable the extras», not «Switch off superfluous».
* Sentence case for titles, headings and body text. UPPER CASE only for labels/tags/tabs/buttons (as in Russian).
* Buttons are verbs: `APPLY`, `UNDO`, `REFRESH`; counters keep the middle dot: `UNDO · 71`, `SELECTED 26`.
* Keep brand and tech names: G HUB, NVIDIA, GeForce, winget, DirectX, UAC, SSD, DNS, DDC/CI. Windows terms use the English Windows names (Task Manager, Startup apps, Restore point).
* «Ты/вы» → no pronoun games: imperative or neutral («Repeat the action», «Wait until it finishes»).
* Quotes: “…” for UI names and quoted words. Em dash with spaces ` — `.
* Apostrophes are straight (`'`: `Couldn't`, `Lycoris's`), never `’`; double quotes are curly “…”.
* On/off vocabulary (one for the whole app): `ВКЛЮЧИТЬ`/`ВЫКЛЮЧИТЬ`/`ОТКЛЮЧИТЬ` = ENABLE / DISABLE / DISABLE; states `Включена/Включено/Включён` = Enabled, `Выключена/Выключено/Отключена` = Disabled. Short inline words in running text may stay on/off.
* Other fixed words: `ЗАПУСТИТЬ` = START, `ПРОЦЕССОР` = CPU, `ЗВУК` = AUDIO, `НАСТРОИТЬ` = SET UP, `ПРЕРВАНО` = INTERRUPTED, `нужна перезагрузка` = restart required, `Не удалось …` = Couldn't …, the change log is never called «journal» in English.
* Terms shared by several areas live in `Common.json` (the conflict test fails otherwise); if one context needs different wording give it a distinct Russian text, not a second translation of the same key.
* QA: `Lycoris.exe --selftest <dir> --lang en` also writes `<dir>/missing-keys.txt` (Russian texts that were requested but have no English entry).
* Numbers/dates: `Loc.Culture` (en-US: `1,234.5`, `Nov 2, 2026`, 24-hour `15:40`). Bytes: `SystemProfile.FormatBytes` → `MB/GB`.

### Glossary (fixed translations — use exactly these)

| Russian | English | Russian | English |
|---|---|---|---|
| Панель | Dashboard | Службы | Services |
| Оптимизация | Optimize | Автозапуск | Startup |
| Игры | Games | Драйверы | Drivers |
| Игровой сеанс | Game session | Приложения | Apps |
| Устройства и сеть | Devices & network | Восстановление | Restore |
| Обслуживание | Maintenance | Очистка | Cleanup |
| Журнал изменений | Change log | Точка восстановления | Restore point |
| Откат | Undo | Откатить | Undo (revert for the verb in a sentence) |
| Применить / Применено | Apply / Applied | Не применено | Not applied |
| Изменено Lycoris | Changed by Lycoris | Неприменимо | Not applicable |
| Безопасно | Safe | По желанию | Optional |
| Эксперт | Expert | Режим просмотра | View-only mode |
| Задачи | Tasks | Журнал действий | Action log |
| Профиль | Profile | Твик (настройка) | Tweak |
| Перезагрузка | Restart (the PC: «reboot» only in technical text) | Права администратора | Administrator rights |

## 6. Language switch and QA

* `AppSettings.Language` = `"auto" | "ru" | "en"` (default `auto`). `App.OnStartup` calls `Loc.Initialize(--lang ?? settings)` right after `AppSettings.Load()`,
  before any window, dialog or view-model exists (also before the «previous copy is hung» dialog).
* Settings page: segmented АВТО | РУССКИЙ | ENGLISH. The choice is saved at once; a bilingual dialog offers the restart; until it is accepted the row says
  «Язык изменится после перезапуска» and shows a labelled «ПЕРЕЗАПУСТИТЬ СЕЙЧАС» button. The restart (`Ui.RestartApp` → `MainViewModel.RestartSameRights` →
  `Elevation.TryRelaunchSameRights`) keeps the admin state (an elevated copy starts an elevated child without a new UAC prompt) and the current page and is refused
  while operations run.
* `--lang ru|en` works with every mode and affects only that run (nothing is saved): `Lycoris.exe --snapshot snapshots\en --pages dashboard,settings --lang en`,
  `--selftest snapshots\en --lang en`. `--snapshot … --demo-shell` additionally renders toasts, the command palette, dialogs and the task list (`shell-*.png`).
* Tests: Russian is the default in the test host; English is `using var _ = Loc.UseLanguage("en");`. `LocalizationCoverageTests` checks every area with
  `"translated": true`; `LocTests` the mechanism. Catalogs built once at static initialization keep the language of that moment — in tests construct them inside a `UseLanguage` scope
  or test the formatter, not the cached list.
