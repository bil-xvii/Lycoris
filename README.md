<p align="center">
  <img src="docs/branding/lycoris-github.svg" width="850" alt="LYCORIS — Диагностика. Обслуживание. Контроль.">
</p>

**Lycoris помогает понять состояние Windows, найти проблемы и обслуживать ПК.** Нагрузка и состояние системы, понятные рекомендации, инструменты для работы и игр — в одном приложении.

**[Скачать установщик или portable](https://github.com/bil-xvii/Lycoris-releases/releases/latest)** · [Portable одним файлом — Lycoris.exe](https://github.com/bil-xvii/Lycoris-releases/releases/latest/download/Lycoris.exe)

Windows 10 2004+ / Windows 11, x64. .NET включён в загрузку. Интерфейс на русском и английском.

## Возможности

- **Состояние ПК.** Мониторинг CPU, GPU, RAM, дисков и сети; диагностика проблем с оборудованием и настройками Windows.
- **Помощник.** Объясняет найденные проблемы, открывает нужный раздел и может применить поддерживаемые исправления. Базовые подсказки работают без локальной AI-модели; для свободных вопросов можно подключить локальный чат через Ollama.
- **Игры.** Библиотека и избранное, запуск с GameBoost и профили оптимизации под разные задачи.
- **Автозапуск и службы.** Просмотр фоновых программ и управление поддерживаемыми настройками.
- **Обслуживание.** Анализ и очистка временных файлов, системные инструменты и проверка драйверов.
- **Контроль изменений.** Предпросмотр, подтверждение действий с правами администратора, журнал и откат поддерживаемых настроек.

## Как выглядит

**Главная — состояние системы и ближайшие действия.**

<img src="docs/branding/screenshots/home.png" width="100%" alt="Lycoris 1.0.25: Главная со сводкой состояния ПК и рекомендуемыми действиями">

<details>
<summary>Помощник и игровая библиотека</summary>

**Помощник — объяснения проблем и доступные действия.**

<img src="docs/branding/screenshots/assistant.png" width="100%" alt="Lycoris 1.0.25: Помощник с объяснением найденных проблем и доступными действиями">

**Игры — библиотека, избранное и GameBoost.**

<img src="docs/branding/screenshots/games.png" width="100%" alt="Lycoris 1.0.25: игровая библиотека и управление GameBoost">

</details>

Скриншоты версии **1.0.25**. Показатели и рекомендации зависят от компьютера.

## Перед запуском

Простой режим доступен всем. Экспертный режим пока доступен только автору на его ПК. Просмотр и диагностика работают без прав администратора; действия, требующие повышения прав, подтверждаются.

Откат предусмотрен для поддерживаемых настроек. Очистка файлов необратима; не каждое изменение можно отменить. Создание точки восстановления зависит от доступности и включённой защиты системы Windows. Результат оптимизации зависит от ПК и задачи — гарантий прироста FPS нет.

У приложения пока нет сертификата Authenticode: Windows может показывать неизвестного издателя. Сверяйте SHA-256 с файлом из релиза. Проверка подписи обновлений ECDSA — отдельный механизм и не заменяет подпись издателя Windows.

<details>
<summary>Для разработчиков: командная строка и сборка</summary>

## Командная строка

Сценарии раздела «Оптимизация» можно применить из консоли — с предпросмотром, журналом и откатом поддерживаемых настроек, как в интерфейсе. Точка восстановления зависит от настроек защиты Windows:

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

</details>

## English

**Lycoris brings Windows diagnostics, maintenance and gaming tools into one app.** Monitor CPU, GPU, RAM, disks and network; understand detected issues with the Assistant; manage games, GameBoost, optimization profiles, startup, services, cleanup and drivers. Basic guidance works without a local AI model; optional Ollama chat supports open-ended questions.

**[Download installer or portable](https://github.com/bil-xvii/Lycoris-releases/releases/latest)** · [Portable EXE](https://github.com/bil-xvii/Lycoris-releases/releases/latest/download/Lycoris.exe)

Windows 10 2004+ / Windows 11 x64; .NET included; RU/EN interface. Simple mode is available to everyone; Expert mode is currently limited to the author's PC. Review and confirm changes requiring administrator rights. Only supported settings can be rolled back; cleanup is irreversible. Restore points require available and enabled Windows System Protection. Performance gains are not guaranteed.

Screenshots show the real **1.0.25** release on a specific PC. The app has no Authenticode certificate yet, so Windows may show “Unknown publisher”. Verify the release SHA-256; ECDSA update verification is separate from Windows publisher signing.

[Лицензия / License](LICENSE.md)
