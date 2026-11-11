# Lycoris

**Безопасная оптимизация Windows для работы и игр.**
Чёрно-белый, строгий интерфейс. Каждое изменение записывается в журнал и откатывается в один клик.

## Что умеет

| Раздел | Что делает |
|---|---|
| **01 Главная** | С чего начать: три главные проблемы ПК с кнопкой исправления, «Исправить всё безопасное», цели в один клик (игры, автозапуск, очистка, драйверы), последние изменения с откатом. |
| **02 Панель** | Нагрузка ЦП / видеокарты / памяти / диска / сети в реальном времени, температура GPU, оценка состояния системы, проверки с исправлением в один клик (XMP выключен, монитор работает не на своей частоте, нет точек восстановления, мало места и т. д.). |
| **03 Оптимизация** | Профили **Обычный**, **Игровой**, **Рекомендуемый** и **Свой набор**. Перед применением — список всех изменений, точка восстановления Windows, после — откат любого пункта. |
| **04 Игры** | Библиотека игр (Steam, Epic, GOG и др.), запуск с ускорением: план питания, приоритет игры, закрытие выбранных фоновых программ. |
| **05 Службы** | Рекомендации по службам (только «Вручную», а не «Отключить»), полный список, задачи телеметрии. Критичные службы защищены. |
| **05 Службы → Автозапуск** | Программы и задачи при входе в систему — отключение так же, как в Диспетчере задач. |
| **06 Обслуживание → Очистка** | Временные файлы, кэши Windows, браузеров (только кэш — без паролей и истории), лаунчеров. Сначала анализ, потом удаление. |
| **06 Обслуживание → Инструменты** | SFC, DISM, проверка и оптимизация дисков, сброс DNS / сети / кэша Store, быстрые ссылки на системные утилиты. |
| **07 Драйверы** | Обновления драйверов через Windows Update, официальные утилиты NVIDIA / AMD / Intel, проблемные устройства, резервная копия драйверов. |
| **08 Устройства** | Настройки мыши, клавиатуры, звука, мониторов и сети: автонастройка или вручную, замеры (частота опроса и DPI мыши, уровень микрофона, пинг), каждое изменение — через журнал с откатом. |
| **09 Приложения** | Лаунчеры, библиотеки для игр (Visual C++, DirectX, .NET), программы — установка и обновление через winget в один клик. |
| **10 Восстановление** | Журнал всех изменений, откат по одному / пакетом / всего сразу, точки восстановления Windows. |
| **11 Настройки** | Поведение программы, папки с данными, сведения. |

## Как Lycoris защищает систему

1. Перед каждым изменением исходное значение сохраняется в журнал (`%ProgramData%\Lycoris\journal.json`), а затрагиваемые ключи реестра — в `.reg`-копии.
2. Перед пакетом изменений создаётся точка восстановления Windows.
3. Только документированные настройки, проверенные по исследованиям (см. `docs/research`). Никаких «чисток реестра», отключения обновлений, защитника, файла подкачки и прочих мифов.
4. Критичные службы Windows изменять нельзя — они заблокированы в коде.
5. Без прав администратора программа работает в режиме просмотра.

## Скачать

**[Последняя версия → Releases](../../releases/latest)** — скачайте `Lycoris.exe` и запустите. Один файл, ничего устанавливать не нужно (.NET внутри).

- Windows 10 (2004+) / Windows 11, 64-bit.
- Программа открывается **без запроса прав администратора** в режиме просмотра: смотреть и проверять можно сразу. Когда вы нажимаете кнопку, которая что-то меняет, Lycoris сам перезапускается с правами администратора и продолжает то же действие.
- Язык интерфейса: русский / English (Настройки → «Язык / Language»; по умолчанию — язык Windows).
- Рядом с файлом в релизе лежит `Lycoris.exe.sha256` — сверьте контрольную сумму: `Get-FileHash Lycoris.exe -Algorithm SHA256`.
- Файл пока **не подписан** цифровой подписью, поэтому Windows SmartScreen / окно UAC может писать «Издатель: неизвестно» (см. `docs/signing.md`). Это не означает, что файл опасен; исходный код открыт в этом репозитории.

## Командная строка

Сценарии раздела «Оптимизация» можно применить из консоли — тем же механизмом, что и кнопка (журнал, точка восстановления, откат в «Восстановлении»):

```
start /wait "" Lycoris.exe --apply-profile gaming            :: только план: каждый пункт «сейчас → станет», ничего не меняется
start /wait "" Lycoris.exe --apply-profile gaming --yes      :: применить (консоль от имени администратора, окно Lycoris закрыто)
echo %errorlevel%
```

Сценарии: `normal` («Работа и учёба»), `gaming` («Игры»), `recommended` («Рекомендуемый»). `--dry-run` всегда только показывает план. В PowerShell: `Start-Process .\Lycoris.exe '--apply-profile gaming' -NoNewWindow -Wait -PassThru`. Коды выхода: 0 — готово (или менять нечего), 1 — ошибки при применении, 2 — неверные параметры, 3 — нужны права администратора, 4 — Lycoris уже открыт, 5 — применено не всё, 6 — сбой.

## Сборка из исходников (нужен .NET 10 SDK)

```
dotnet build src/Lycoris/Lycoris.csproj -c Debug
dotnet test tests/Lycoris.Tests/Lycoris.Tests.csproj
powershell -ExecutionPolicy Bypass -File tools\publish.ps1     # Release-сборка в publish\Lycoris.exe
```

---

# Lycoris (English)

**Safe Windows optimization for work and gaming.** Strict black-and-white interface. Every change is written to a journal and can be undone in one click; a Windows restore point is created before each batch; only documented, evidence-backed settings; critical services are protected.

## What it does

| Section | What it does |
|---|---|
| **01 Home** | Where to start: the top three PC issues with a fix button, "Fix all safe", one-click goals (games, startup, cleanup, drivers), recent changes with undo. |
| **02 Dashboard** | Real-time CPU / GPU / memory / disk / network load, GPU temperature, system health score, one-click checks with fixes (XMP off, monitor not on its own frequency, no restore points, low space, etc.). |
| **03 Optimization** | Profiles **Standard**, **Gaming**, **Recommended** and **Custom**. Before applying — list of all changes, Windows restore point, after — undo any item. |
| **04 Games** | Game library (Steam, Epic, GOG etc.), launch with boost: power plan, game priority, closing selected background programs. |
| **05 Services** | Service recommendations (only "Manual", not "Disabled"), full list, telemetry tasks. Critical services are protected. |
| **05 Services → Auto-start** | Programs and tasks at login — disabling the same way as Task Manager. |
| **06 Maintenance → Cleanup** | Temporary files, caches of Windows, browsers (cache only — no passwords or history), launchers. Analysis first, then deletion. |
| **06 Maintenance → Tools** | SFC, DISM, disk check and optimization, DNS / network / Store cache reset, quick links to system utilities. |
| **07 Drivers** | Driver updates via Windows Update, official NVIDIA / AMD / Intel utilities, problematic devices, driver backup. |
| **08 Devices** | Mouse, keyboard, audio, display and network settings: auto-tune or manual, measurements (mouse polling rate and DPI, mic level, ping), every change goes through the journal with undo. |
| **09 Applications** | Launchers, game libraries (Visual C++, DirectX, .NET), programs — install and update via winget in one click. |
| **10 Recovery** | Journal of all changes, undo one / batch / all at once, Windows restore points. |
| **11 Settings** | Program behavior, data folders, information. |

**Download:** grab `Lycoris.exe` from the [latest release](../../releases/latest) — a single self-contained file, no installer, no .NET needed (Windows 10 2004+/11, 64-bit). It opens read-only without a UAC prompt and asks for administrator rights only when you press a button that changes something, then continues the same action. Interface language: Russian / English (Settings → Language; default follows Windows). Verify the download with `Lycoris.exe.sha256`. The file is not code-signed yet, so Windows may show "Unknown publisher" (see `docs/signing.md`). **Command line:** `Lycoris.exe --apply-profile normal|gaming|recommended [--dry-run] [--yes]` prints the scenario's plan (every change, current → new); only `--yes` in an administrator console applies it through the same journal/restore-point/undo engine (run it with `start /wait` in cmd; exit codes as in the Russian section above).
