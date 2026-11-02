# winget: сборка и отправка манифестов

Версия берётся из `src/Lycoris/Lycoris.csproj`. После публикации `publish\Lycoris.exe` и `publish\Lycoris.exe.sha256` в корне этого репозитория запустите:

```powershell
powershell -ExecutionPolicy Bypass -File tools\make-winget-manifest.ps1
winget validate --manifest packaging\winget\generated\1.0.7
```

Скрипт создаёт version, defaultLocale и installer манифесты формата community repository. Проверьте идентификатор `bil-xvii.Lycoris`, версию, URL и хеш перед отправкой. Требуется Windows Package Manager CLI с поддержкой `winget validate --manifest` (например, версия 1.29.380).

Для PR: сделайте fork `microsoft/winget-pkgs`, создайте ветку с изменениями, скопируйте три сгенерированных YAML в `manifests/b/bil-xvii/Lycoris/<version>/`, затем откройте PR из своей ветки в `microsoft/winget-pkgs:master`. Перед PR проверьте актуальные правила и путь средствами репозитория по [README winget-pkgs](https://github.com/microsoft/winget-pkgs/blob/master/README.md) и [документации манифестов](https://learn.microsoft.com/windows/package-manager/package/manifest). Отправка PR этим проектом не выполняется.
