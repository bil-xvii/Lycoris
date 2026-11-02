# Конфиденциальность / Privacy

## Русский

Lycoris хранит данные локально. Настройки (`settings.json`), ежедневные логи (`Logs\lycoris-ГГГГММДД.log`) и кэш находятся в `%LocalAppData%\Lycoris`. Журнал изменений и резервные копии для отката находятся в `%ProgramData%\Lycoris` и защищены разрешениями Windows. Старые данные могут оставаться в `%LocalAppData%\MONOLITH` или `%ProgramData%\MONOLITH` после неудачной миграции.

Телеметрии нет. Приложение не отправляет настройки, журнал, логи, сведения о компьютере или действия пользователя.

Сетевые обращения:

- Проверка обновления обращается к `api.github.com/repos/bil-xvii/Lycoris-releases/releases/latest` анонимно не чаще одного раза за 12 часов.
- winget и загрузка вендорных установщиков обращаются к соответствующим источникам только при действии пользователя. Запросы winget выполняет сам Windows Package Manager.
- Проверки ping, DNS и доступности игровых серверов выполняются по запросу пользователя; отдельные сетевые тесты могут обращаться к Steam API или проверяемому адресу.

Чтобы удалить настройки, логи и кэш, закройте приложение и удалите `%LocalAppData%\Lycoris` (а при наличии старых файлов — `%LocalAppData%\MONOLITH`). Журнал и резервные копии в `%ProgramData%\Lycoris` защищены и требуют отдельного администрирования; обычное удаление приложения их не стирает. Сначала используйте журнал для отката нужных изменений. Удаление файлов журнала само по себе изменения Windows не отменяет.

## English

Lycoris stores data locally. Preferences (`settings.json`), daily logs (`Logs\lycoris-YYYYMMDD.log`), and cache are in `%LocalAppData%\Lycoris`. The change and rollback journal and backups are in `%ProgramData%\Lycoris`, protected by Windows permissions. Older data may remain in `%LocalAppData%\MONOLITH` or `%ProgramData%\MONOLITH` after a failed migration.

There is no telemetry. The app does not send settings, journal entries, logs, computer details, or user activity.

Network requests:

- Update checks anonymously query `api.github.com/repos/bil-xvii/Lycoris-releases/releases/latest`, at most once every 12 hours.
- winget and vendor installer downloads contact their respective sources only after a user action. Windows Package Manager performs winget requests.
- Ping, DNS, and game-server availability checks run when requested by the user; specific network tests may contact the Steam API or the address being tested.

To remove preferences, logs, and cache, close the app and delete `%LocalAppData%\Lycoris` (and `%LocalAppData%\MONOLITH` if old files remain). The journal and backups in `%ProgramData%\Lycoris` are protected and require separate administration; normal app uninstall leaves them in place. Use the journal to undo desired changes first. Deleting journal files does not itself undo Windows changes.
