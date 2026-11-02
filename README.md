# MONOLITH

**Безопасная оптимизация Windows для работы и игр.**
Чёрно-белый, строгий интерфейс. Каждое изменение записывается в журнал и откатывается в один клик.

## Что умеет

| Раздел | Что делает |
|---|---|
| **01 Панель** | Нагрузка ЦП / видеокарты / памяти / диска / сети в реальном времени, температура GPU, оценка состояния системы, проверки с исправлением в один клик (XMP выключен, монитор работает не на своей частоте, нет точек восстановления, мало места и т. д.). |
| **02 Оптимизация** | Профили **Обычный**, **Игровой**, **Рекомендуемый** и **Свой набор**. Перед применением — список всех изменений, точка восстановления Windows, после — откат любого пункта. |
| **03 Игры** | Библиотека игр (Steam, Epic, GOG и др.), запуск с ускорением: план питания, приоритет игры, закрытие выбранных фоновых программ — всё возвращается после выхода из игры. |
| **04 Очистка** | Временные файлы, кэши Windows, браузеров (только кэш — без паролей и истории), лаунчеров. Сначала анализ, потом удаление. |
| **05 Драйверы** | Обновления драйверов через Windows Update, официальные утилиты NVIDIA / AMD / Intel, проблемные устройства, резервная копия драйверов. |
| **06 Службы** | Рекомендации по службам (только «Вручную», а не «Отключить»), полный список, задачи телеметрии. Критичные службы защищены. |
| **07 Автозапуск** | Программы и задачи при входе в систему — отключение так же, как в Диспетчере задач. |
| **08 Приложения** | Лаунчеры, библиотеки для игр (Visual C++, DirectX, .NET), программы — установка и обновление через winget в один клик. |
| **09 Инструменты** | SFC, DISM, проверка и оптимизация дисков, сброс DNS / сети / кэша Store, быстрые ссылки на системные утилиты. |
| **10 Восстановление** | Журнал всех изменений, откат по одному / пакетом / всего сразу, точки восстановления Windows. |
| **11 Настройки** | Поведение программы, папки с данными, сведения. |

## Как MONOLITH защищает систему

1. Перед каждым изменением исходное значение сохраняется в журнал (`%ProgramData%\MONOLITH\journal.json`), а затрагиваемые ключи реестра — в `.reg`-копии.
2. Перед пакетом изменений создаётся точка восстановления Windows.
3. Только документированные настройки, проверенные по исследованиям (см. `docs/research`). Никаких «чисток реестра», отключения обновлений, защитника, файла подкачки и прочих мифов.
4. Критичные службы Windows изменять нельзя — они заблокированы в коде.
5. Без прав администратора программа работает в режиме просмотра.

## Скачать

**[Последняя версия → Releases](../../releases/latest)** — скачайте `MONOLITH.exe` и запустите. Один файл, ничего устанавливать не нужно (.NET внутри).

- Windows 10 (2004+) / Windows 11, 64-bit.
- Программа открывается **без запроса прав администратора** в режиме просмотра: смотреть и проверять можно сразу. Когда вы нажимаете кнопку, которая что-то меняет, MONOLITH сам перезапускается с правами администратора и продолжает то же действие.
- Язык интерфейса: русский / English (Настройки → «Язык / Language»; по умолчанию — язык Windows).
- Рядом с файлом в релизе лежит `MONOLITH.exe.sha256` — сверьте контрольную сумму: `Get-FileHash MONOLITH.exe -Algorithm SHA256`.
- Файл пока **не подписан** цифровой подписью, поэтому Windows SmartScreen / окно UAC может писать «Издатель: неизвестно» (см. `docs/signing.md`). Это не означает, что файл опасен; исходный код открыт в этом репозитории.

## Сборка из исходников (нужен .NET 10 SDK)

```
dotnet build src/Monolith/Monolith.csproj -c Debug
dotnet test tests/Monolith.Tests/Monolith.Tests.csproj
powershell -ExecutionPolicy Bypass -File tools\publish.ps1     # Release-сборка в publish\MONOLITH.exe
```

---

# MONOLITH (English)

**Safe Windows optimization for work and gaming.** Strict black-and-white interface. Every change is written to a journal and can be undone in one click; a Windows restore point is created before each batch; only documented, evidence-backed settings; critical services are protected.

**Download:** grab `MONOLITH.exe` from the [latest release](../../releases/latest) — a single self-contained file, no installer, no .NET needed (Windows 10 2004+/11, 64-bit). It opens read-only without a UAC prompt and asks for administrator rights only when you press a button that changes something, then continues the same action. Interface language: Russian / English (Settings → Language; default follows Windows). Verify the download with `MONOLITH.exe.sha256`. The file is not code-signed yet, so Windows may show "Unknown publisher" (see `docs/signing.md`).
