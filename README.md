<p align="center">
  <img src="docs/branding/lycoris-github.svg" width="850" alt="LYCORIS — Диагностика. Обслуживание. Контроль.">
</p>

**Lycoris — диагностика, обслуживание и настройка Windows под игры, работу и другие задачи.** Определяет тип ПК, объясняет найденные проблемы и предлагает изменения по подтверждённым данным.

**[Скачать установщик или portable](https://github.com/bil-xvii/Lycoris-releases/releases/latest)** · [Portable одним файлом — Lycoris.exe](https://github.com/bil-xvii/Lycoris-releases/releases/latest/download/Lycoris.exe)

Windows 10 2004+ / Windows 11, x64. .NET включён в загрузку. Интерфейс на русском и английском.

## Возможности

- **Состояние и тип ПК.** Мониторинг CPU, GPU, RAM, дисков и сети. Тип компьютера определяется по SMBIOS и данным корпуса; неизвестные и противоречивые сведения указаны явно.
- **Помощник.** Объясняет найденные проблемы и применяет поддерживаемые исправления. Без Ollama понимает «включи», «отключи», «запусти» и «останови службу…»: показывает точную цель и последствия, проверяет разрешения и результат. Для свободных вопросов доступен локальный чат через Ollama.
- **Программы и драйверы.** Рекомендации по подтверждённой потребности: например, игровые службы для обнаруженного Xbox. Ошибка сканирования или старый кэш не считаются отсутствием программы; ручной выбор сохранён.
- **Игры и профили.** Библиотека, избранное и GameBoost. Профили для игр, работы, стриминга и свой набор учитывают подтверждённую применимость аппаратных настроек.
- **Обслуживание.** Автозапуск и службы, анализ и очистка временных файлов, системные инструменты.
- **Разбор служб.** Возможные будущие функции сохраняются через обратимую настройку запуска. Удаление остатков ограничено проверенными сценариями: сейчас только простой остаток Everything после подтверждения удаления программы; неизвестные службы не удаляются автоматически.
- **Контроль изменений.** Проверка ПК без изменений системы, предпросмотр и повторная проверка плана перед установкой. Подтверждение действий с правами администратора, журнал и откат поддерживаемых настроек.

## Как выглядит

**Главная — состояние системы и ближайшие действия.**

<img src="docs/branding/screenshots/home.png" width="100%" alt="Lycoris 1.0.26: Главная со сводкой состояния ПК и рекомендуемыми действиями">

<details>
<summary>Помощник, приложения и игровая библиотека</summary>

**Помощник — объяснения проблем и доступные действия.**

<img src="docs/branding/screenshots/assistant.png" width="100%" alt="Lycoris 1.0.26: Помощник с объяснением найденных проблем и доступными действиями">

**Приложения — рекомендации по подтверждённой потребности.**

<img src="docs/branding/screenshots/apps.png" width="100%" alt="Lycoris 1.0.26: Приложения с результатом проверки рекомендаций для этого ПК">

**Игры — библиотека, избранное и GameBoost.**

<img src="docs/branding/screenshots/games.png" width="100%" alt="Lycoris 1.0.26: игровая библиотека и управление GameBoost">

</details>

Скриншоты версии **1.0.26**. Показатели и рекомендации зависят от компьютера.

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

**Lycoris brings Windows diagnostics, maintenance and settings for gaming, work and other tasks into one app.** Monitor hardware and identify the PC type from SMBIOS and chassis data, with unknown or conflicting facts stated explicitly. App recommendations require a confirmed need; failed scans and cached data do not prove a program is missing. Gaming, work, streaming and custom profiles respect verified hardware applicability. Review the plan before changes; installation plans are checked again. Manage games, GameBoost, startup, services and cleanup. Basic Assistant guidance works without a local AI model; Ollama chat is optional.

**[Download installer or portable](https://github.com/bil-xvii/Lycoris-releases/releases/latest)** · [Portable EXE](https://github.com/bil-xvii/Lycoris-releases/releases/latest/download/Lycoris.exe)

The Assistant also handles explicit enable, disable, start and stop service requests without Ollama, with an exact plan and verified outcome. Start policy and current running state are separate. Service residue removal currently supports only a constrained Everything remnant after you confirm the program was uninstalled; unknown services are not deleted. Supported removal records can be restored disabled, including from Simple mode.

Windows 10 2004+ / Windows 11 x64; .NET included; RU/EN interface. Simple mode is available to everyone; Expert mode is currently limited to the author's PC. Review and confirm changes requiring administrator rights. Only supported settings can be rolled back; file cleanup is irreversible. Restore points require available and enabled Windows System Protection. Performance gains are not guaranteed.

Screenshots show the real **1.0.26** release on a specific PC. The app has no Authenticode certificate yet, so Windows may show “Unknown publisher”. Verify the release SHA-256; ECDSA update verification is separate from Windows publisher signing.

[Лицензия / License](LICENSE.md)
