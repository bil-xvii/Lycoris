using System.Globalization;
using System.Reflection;
using Lycoris.Core;

namespace Lycoris.Modules.Settings;

/// <summary>Resetting preferences to factory defaults (pure; the defaults come from a fresh <see cref="AppSettings"/>).</summary>
public static class SettingsDefaults
{
    /// <summary>Monitor refresh options offered in the UI, in milliseconds.</summary>
    public static IReadOnlyList<int> IntervalChoices { get; } = [500, 1000, 2000, 5000];

    /// <summary>
    /// Puts every preference back to its default. User data kept on purpose: added game folders, the Game Boost
    /// close-list and the first-run flag — they are not preferences, and losing them would surprise the user.
    /// </summary>
    public static void Apply(AppSettings target)
    {
        var d = new AppSettings();
        target.CreateRestorePoints = d.CreateRestorePoints;
        target.ConfirmBeforeApply = d.ConfirmBeforeApply;
        target.ServicesExpertMode = d.ServicesExpertMode;
        target.ExpertMode = d.ExpertMode;
        target.ShowConsole = d.ShowConsole;
        target.AutoCheckUpdates = d.AutoCheckUpdates;
        target.MonitorIntervalMs = d.MonitorIntervalMs;
        target.LastProfile = d.LastProfile;
        target.GameBoostPowerPlan = d.GameBoostPowerPlan;
        target.GameBoostPriority = d.GameBoostPriority;
        target.GameBoostPurgeStandby = d.GameBoostPurgeStandby;
    }

    /// <summary>The exact list shown in the reset confirmation.</summary>
    public static IReadOnlyList<string> Describe()
    {
        var d = new AppSettings();
        return
        [
            Loc.Tf("Точка восстановления перед изменениями — {0}", OnOff(d.CreateRestorePoints)),
            Loc.Tf("Подтверждение перед применением — {0}", OnOff(d.ConfirmBeforeApply)),
            Loc.Tf("Экспертный режим служб — {0}", OnOff(d.ServicesExpertMode)),
            Loc.Tf("Режим подробностей интерфейса — {0}", d.ExpertMode ? Loc.T("экспертный") : Loc.T("простой")),
            Loc.Tf("Консоль операций — {0}", d.ShowConsole ? Loc.T("показана") : Loc.T("скрыта")),
            Loc.Tf("Автоматическая проверка обновлений — {0}", OnOff(d.AutoCheckUpdates)),
            Loc.Tf("Интервал обновления мониторинга — {0}", FormatInterval(d.MonitorIntervalMs)),
            Loc.Tf("Игровой сеанс: план питания — {0}, приоритет игры — {1}, очистка памяти ожидания — {2}", OnOff(d.GameBoostPowerPlan), OnOff(d.GameBoostPriority), OnOff(d.GameBoostPurgeStandby)),
            Loc.T("Последний выбранный профиль оптимизации — «Рекомендуемый»"),
            Loc.T("Сохраняются: добавленные папки с играми и список программ для игрового режима"),
            Loc.T("Язык интерфейса не меняется"),
            Loc.T("Изменения системы и журнал отката не затрагиваются"),
        ];
    }

    /// <summary>Snaps any stored interval to the nearest offered choice (e.g. a hand-edited 750 → 500).</summary>
    public static int NearestInterval(int ms) =>
        IntervalChoices.OrderBy(c => Math.Abs(c - ms)).ThenBy(c => c).First();

    public static string FormatInterval(int ms) => Loc.SecondsText(ms / 1000d);

    private static string OnOff(bool v) => v ? Loc.T("включено") : Loc.T("выключено");

    /// <summary>"8.4.2+5b4c…" → "8.4.2", "8.4.2.1" → "8.4.2" (package versions have three parts).</summary>
    public static string CleanVersion(string? informational, Version? fallback)
    {
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var v = (plus > 0 ? informational[..plus] : informational).Trim();
            return Version.TryParse(v, out var parsed) && parsed.Revision >= 0 && !v.Contains('-') ? parsed.ToString(3) : v;
        }
        return fallback?.ToString(3) ?? "—";
    }

    public static string VersionOf(Assembly a) =>
        CleanVersion(a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, a.GetName().Version);
}
