using System.Globalization;
using System.Reflection;
using Monolith.Core;

namespace Monolith.Modules.Settings;

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
        target.ShowConsole = d.ShowConsole;
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
            $"Точка восстановления перед изменениями — {OnOff(d.CreateRestorePoints)}",
            $"Подтверждение перед применением — {OnOff(d.ConfirmBeforeApply)}",
            $"Консоль операций — {(d.ShowConsole ? "показана" : "скрыта")}",
            $"Интервал обновления мониторинга — {FormatInterval(d.MonitorIntervalMs)}",
            $"Игровой режим: план питания — {OnOff(d.GameBoostPowerPlan)}, приоритет игры — {OnOff(d.GameBoostPriority)}, очистка памяти ожидания — {OnOff(d.GameBoostPurgeStandby)}",
            "Последний выбранный профиль оптимизации — «Рекомендуемый»",
            "Сохраняются: добавленные папки с играми и список программ для игрового режима",
            "Изменения системы и журнал отката не затрагиваются",
        ];
    }

    /// <summary>Snaps any stored interval to the nearest offered choice (e.g. a hand-edited 750 → 500).</summary>
    public static int NearestInterval(int ms) =>
        IntervalChoices.OrderBy(c => Math.Abs(c - ms)).ThenBy(c => c).First();

    public static string FormatInterval(int ms) =>
        (ms / 1000d).ToString("0.#", CultureInfo.GetCultureInfo("ru-RU")) + " с";

    private static string OnOff(bool v) => v ? "включено" : "выключено";

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
