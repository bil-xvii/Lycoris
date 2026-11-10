using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Core.Maintenance;
using Lycoris.UI;

namespace Lycoris.Modules.Cleaner;

/// <summary>A day in the schedule's day list.</summary>
public sealed record ScheduleDayOption(DayOfWeek Day, string Name)
{
    /// <summary>The closed ComboBox shows the item's text.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// «Обслуживание по расписанию» on the ОЧИСТКА tab: one card with the state, the day and time, and labelled buttons
/// (ВКЛЮЧИТЬ / СОХРАНИТЬ ВРЕМЯ / ИСПРАВИТЬ / ВЫКЛЮЧИТЬ). Every change goes through <see cref="ChangeSession.SetMaintenanceTask"/>
/// (journal + undo in «Восстановлении»); reading the state never changes anything.
/// </summary>
public sealed partial class CleanerViewModel
{
    /// <summary>Replay parameters of <see cref="ScheduleMaintenanceCommand"/>: "on:&lt;day 0-6&gt;:&lt;HH:mm&gt;", "fix", "off".</summary>
    public const string ScheduleOff = "off", ScheduleFix = "fix";

    public IReadOnlyList<ScheduleDayOption> ScheduleDays { get; } =
        Enum.GetValues<DayOfWeek>().OrderBy(d => ((int)d + 6) % 7).Select(d => new ScheduleDayOption(d, MaintenanceSchedule.DayName(d))).ToList();

    /// <summary>Every half hour of the day.</summary>
    public IReadOnlyList<string> ScheduleTimes { get; } =
        Enumerable.Range(0, 48).Select(i => string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", i / 2, i % 2 * 30)).ToList();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ScheduleChanged))] private ScheduleDayOption? _scheduleDay;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ScheduleChanged))] private string _scheduleTime = MaintenanceSchedule.Default.TimeText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleIsOn), nameof(ScheduleIsOff), nameof(ScheduleNeedsFix), nameof(ScheduleCanDisable), nameof(ScheduleChanged),
        nameof(ScheduleStateText), nameof(ScheduleTagMuted), nameof(ScheduleShowPicker))]
    private MaintenanceTaskStatus _scheduleStatus = MaintenanceTaskStatus.Off;

    /// <summary>The schedule the task has now (null when there is no task of Lycoris).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ScheduleChanged))] private MaintenanceSchedule? _scheduleCurrent;

    [ObservableProperty] private string _scheduleDetail = "";
    [ObservableProperty] private string _scheduleLastText = "";

    public bool ScheduleIsOn => ScheduleStatus == MaintenanceTaskStatus.On;
    public bool ScheduleIsOff => ScheduleStatus == MaintenanceTaskStatus.Off;
    public bool ScheduleNeedsFix => ScheduleStatus is MaintenanceTaskStatus.ExeMissing or MaintenanceTaskStatus.OtherCopy;
    public bool ScheduleCanDisable => ScheduleIsOn || ScheduleNeedsFix;
    /// <summary>The state tag is neutral (off, foreign task, scheduler silent).</summary>
    public bool ScheduleTagMuted => !ScheduleIsOn && !ScheduleNeedsFix;
    /// <summary>Day and time pickers: shown while the task is off (to pick) or on (to move it).</summary>
    public bool ScheduleShowPicker => ScheduleIsOn || ScheduleIsOff;

    /// <summary>The task runs, and the day or time picked differs from it: «СОХРАНИТЬ ВРЕМЯ» appears.</summary>
    public bool ScheduleChanged => ScheduleIsOn && ScheduleCurrent is { } now && SelectedSchedule() is { } picked && picked != now;

    public string ScheduleStateText => ScheduleStatus switch
    {
        MaintenanceTaskStatus.On => Loc.T("ВКЛЮЧЕНО"),
        MaintenanceTaskStatus.Off => Loc.T("ВЫКЛЮЧЕНО"),
        MaintenanceTaskStatus.ExeMissing or MaintenanceTaskStatus.OtherCopy => Loc.T("НУЖНО ИСПРАВИТЬ"),
        MaintenanceTaskStatus.Foreign => Loc.T("ЧУЖАЯ ЗАДАЧА"),
        _ => Loc.T("НЕИЗВЕСТНО"),
    };

    /// <summary>The names of the items a scheduled run cleans (the БЕЗОПАСНО per-user part of the catalog).</summary>
    public static IReadOnlyList<string> ScheduledItemNames() =>
        ScheduledMaintenanceRunner.SafeItems(CleanCatalog.All).Select(d => d.Name).Distinct().ToList();

    internal MaintenanceSchedule? SelectedSchedule()
    {
        if (ScheduleDay is not { } day) return null;
        var parts = ScheduleTime.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        var s = new MaintenanceSchedule(day.Day, h, m);
        return s.IsValid ? s : null;
    }

    /// <summary>"on:0:19:00" → Sunday 19:00; null when the text is not a schedule.</summary>
    internal static MaintenanceSchedule? ParseOn(string? parameter)
    {
        if (parameter is not { Length: <= 16 } p || !p.StartsWith("on:", StringComparison.Ordinal)) return null;
        var parts = p[3..].Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var m) || d is < 0 or > 6) return null;
        var s = new MaintenanceSchedule((DayOfWeek)d, h, m);
        return s.IsValid ? s : null;
    }

    internal static string OnParameter(MaintenanceSchedule s) =>
        string.Format(CultureInfo.InvariantCulture, "on:{0}:{1:00}:{2:00}", (int)s.Day, s.Hour, s.Minute);

    // ───────────────────────────── State ─────────────────────────────

    /// <summary>Reads the task and the last result (read-only, off the UI thread) and fills the card.</summary>
    public async Task RefreshScheduleAsync()
    {
        var exe = AppPaths.ExecutablePath;
        var (info, last) = await Task.Run(() => (ScheduledMaintenanceTask.Inspect(exe), MaintenanceResultStore.Read()));
        ApplyScheduleState(info, last);
    }

    internal void ApplyScheduleState(MaintenanceTaskInfo info, MaintenanceRunResult? last)
    {
        ScheduleCurrent = info.Status == MaintenanceTaskStatus.On ? info.Snapshot?.Schedule : null;
        var shown = info.Snapshot?.Schedule ?? SelectedSchedule() ?? MaintenanceSchedule.Default;
        ScheduleDay = ScheduleDays.FirstOrDefault(d => d.Day == shown.Day) ?? ScheduleDays[^1];
        ScheduleTime = ScheduleTimes.Contains(shown.TimeText) ? shown.TimeText : MaintenanceSchedule.Default.TimeText;
        ScheduleStatus = info.Status;
        ScheduleDetail = info.Status switch
        {
            MaintenanceTaskStatus.On => Loc.Tf("Каждую неделю: {0}. Если ПК был выключен — при следующем включении. Только безопасные пункты с обычными правами — без прав администратора.",
                info.Snapshot!.Schedule!.Describe()),
            MaintenanceTaskStatus.Off => Loc.Tf("Lycoris сам раз в неделю очистит временные файлы и кэши ({0}) и проверит состояние ПК. Без прав администратора, ваши файлы не трогаются.",
                Loc.N(ScheduledItemNames().Count, "{0} пункт", "{0} пункта", "{0} пунктов")),
            MaintenanceTaskStatus.ExeMissing => Loc.Tf("Задача запускает {0}, а этого файла больше нет: обслуживание не выполняется (Windows только отметит ошибку). Исправьте задачу или выключите её.",
                info.Snapshot!.ExePath!),
            MaintenanceTaskStatus.OtherCopy => Loc.Tf("Задача запускает другую копию Lycoris: {0}. Если программа перенесена, исправьте задачу — она будет запускать эту копию.",
                info.Snapshot!.ExePath!),
            MaintenanceTaskStatus.Foreign => Loc.Tf("В планировщике есть задача «{0}», но Lycoris её не создавал в таком виде (другая программа, другая учётная запись или права администратора). Lycoris её не трогает — удалите её в планировщике заданий Windows.",
                ScheduledMaintenanceTask.TaskName),
            _ => Loc.T("Планировщик заданий не ответил — состояние неизвестно. Нажмите «ОБНОВИТЬ» позже."),
        };
        ScheduleLastText = last is { FinishedAt: { } at }
            ? Loc.Tf("Последний запуск: {0} — {1}", Loc.DateTimeText(at.ToLocalTime()), last.Summary())
            : Loc.T("Последний запуск: ещё не было");
    }

    // ───────────────────────────── Commands ─────────────────────────────

    [RelayCommand]
    private Task EnableSchedule() => ScheduleMaintenanceAsync(SelectedSchedule() is { } s ? OnParameter(s) : null);

    [RelayCommand]
    private Task FixSchedule() => ScheduleMaintenanceAsync(ScheduleFix);

    [RelayCommand]
    private Task DisableSchedule() => ScheduleMaintenanceAsync(ScheduleOff);

    /// <summary>
    /// The one implementation behind the card's buttons (and the admin-intent replay): "on:…" turns it on or moves it to that
    /// day and time, "fix" points the task at this Lycoris.exe, "off" removes the task. Always with a confirmation.
    /// </summary>
    [RelayCommand]
    private async Task ScheduleMaintenanceAsync(string? parameter)
    {
        var on = ParseOn(parameter);
        if (on is null && parameter is not (ScheduleOff or ScheduleFix)) return;
        if (!RequireAdmin(Loc.T("обслуживание по расписанию"), "ScheduleMaintenance", parameter)) return;
        if (!CanStartNow(OperationClass.JournalChange, Loc.T("Обслуживание по расписанию"))) return;
        if (Elevation.InteractiveUserState != true)
        {
            await Ui.AlertAsync(Loc.T("ДРУГАЯ УЧЁТНАЯ ЗАПИСЬ"), Elevation.InteractiveUserProblem ?? Elevation.UnknownAccountText);
            return;
        }

        var exe = AppPaths.ExecutablePath;
        var sid = ScheduledMaintenanceTask.CurrentUserSid();
        var info = await Task.Run(() => ScheduledMaintenanceTask.Inspect(exe));
        if (info.Status is MaintenanceTaskStatus.Foreign or MaintenanceTaskStatus.Unknown || sid is null)
        {
            await RefreshScheduleAsync();
            await Ui.AlertAsync(Loc.T("ОБСЛУЖИВАНИЕ ПО РАСПИСАНИЮ"), ScheduleDetail);
            return;
        }

        MaintenanceTaskSnapshot desired;
        string title, question, confirm;
        List<string> lines;
        if (parameter == ScheduleOff)
        {
            if (info.Status == MaintenanceTaskStatus.Off) { await RefreshScheduleAsync(); return; }
            desired = MaintenanceTaskSnapshot.Absent;
            title = Loc.T("Обслуживание по расписанию: выключено");
            question = Loc.T("ВЫКЛЮЧИТЬ ОБСЛУЖИВАНИЕ ПО РАСПИСАНИЮ?");
            confirm = Loc.T("ВЫКЛЮЧИТЬ");
            lines =
            [
                Loc.Tf("Задача «{0}» будет удалена из планировщика заданий Windows.", ScheduledMaintenanceTask.TaskName),
                Loc.T("Очистка останется доступной вручную на этой вкладке."),
                Loc.T("Вернуть можно здесь же или откатом в «Восстановлении»."),
            ];
        }
        else
        {
            var schedule = on ?? info.Snapshot?.Schedule ?? MaintenanceSchedule.Default;
            desired = MaintenanceTaskSnapshot.For(schedule, exe, sid);
            var change = info.Status != MaintenanceTaskStatus.Off;
            title = change ? Loc.T("Обслуживание по расписанию: изменено") : Loc.T("Обслуживание по расписанию: включено");
            question = parameter == ScheduleFix ? Loc.T("ИСПРАВИТЬ ЗАДАЧУ ОБСЛУЖИВАНИЯ?")
                : change ? Loc.T("ИЗМЕНИТЬ РАСПИСАНИЕ?") : Loc.T("ВКЛЮЧИТЬ ОБСЛУЖИВАНИЕ ПО РАСПИСАНИЮ?");
            confirm = parameter == ScheduleFix ? Loc.T("ИСПРАВИТЬ") : change ? Loc.T("СОХРАНИТЬ") : Loc.T("ВКЛЮЧИТЬ");
            lines =
            [
                Loc.Tf("Когда: каждую неделю, {0}; если ПК был выключен — при следующем включении (не от батареи).", schedule.Describe()),
                Loc.Tf("Очищается: {0}.", string.Join(", ", ScheduledItemNames())),
                Loc.T("Как на этой вкладке: только старые файлы, кэш запущенных программ пропускается, защита путей действует."),
                Loc.T("Не трогается: загрузки, документы, рабочий стол, пароли, cookies и история браузеров, корзина."),
                Loc.T("Затем — проверка состояния ПК (только чтение). Итог Lycoris покажет при следующем запуске."),
                Loc.T("Права: обычные права вашей учётной записи — прав администратора у задачи нет."),
                Loc.Tf("Задача планировщика: «{0}» → {1} {2}", ScheduledMaintenanceTask.TaskName, exe, ScheduledMaintenanceTask.Argument),
                Loc.T("Выключить можно здесь же или откатом в «Восстановлении»."),
            ];
        }

        if (!await Ui.ConfirmAsync(question, Loc.T("Изменение записывается в журнал Lycoris и откатывается одной кнопкой."), lines, confirmText: confirm)) return;

        await RunBusyAsync(OperationClass.JournalChange, Loc.T("Обслуживание по расписанию…"), async () =>
        {
            var session = await Task.Run(() => ChangeSession.BeginAsync(title, createRestorePoint: false));
            var ok = await Task.Run(() => session.SetMaintenanceTask(desired, title));
            session.Complete();
            if (ok)
            {
                AppSettings.Current.ScheduledMaintenance = desired.Exists;
                Ui.Toast(desired.Exists
                    ? Loc.Tf("Обслуживание по расписанию включено: {0}.", desired.Schedule!.Describe())
                    : Loc.T("Обслуживание по расписанию выключено."), ToastKind.Success);
            }
            else
            {
                Ui.Toast(session.Errors.FirstOrDefault() ?? Loc.T("Не удалось изменить задачу обслуживания."), ToastKind.Error);
            }
            await RefreshScheduleAsync();
        });
    }
}
