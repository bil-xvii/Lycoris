namespace Monolith.Modules.Drivers;

internal enum DriverAgeVerdict { Unknown, Fresh, Aging, Old }

/// <summary>
/// Age of a vendor driver (used for GPU drivers only). Research §2: a date alone never makes a driver
/// "outdated" — inbox drivers are deliberately dated 2006/2009 — so this is shown only for real vendor
/// drivers, and only as a hint ("older than 6 months"), never as an alarm.
/// </summary>
internal static class DriverAge
{
    public const int AgingMonths = 6;
    public const int OldMonths = 12;

    /// <summary>Whole months between the driver date and now; null when unknown. Future dates count as 0.</summary>
    public static int? Months(DateTime? date, DateTime now)
    {
        if (date is not { } d || d.Year < 1990) return null;
        var months = (now.Year - d.Year) * 12 + now.Month - d.Month;
        if (now.Day < d.Day) months--;
        return Math.Max(0, months);
    }

    public static DriverAgeVerdict Verdict(int? months) => months switch
    {
        null => DriverAgeVerdict.Unknown,
        >= OldMonths => DriverAgeVerdict.Old,
        >= AgingMonths => DriverAgeVerdict.Aging,
        _ => DriverAgeVerdict.Fresh,
    };

    /// <summary>"меньше месяца", "3 мес.", "1 год", "1 год 4 мес.", "2 года", "5 лет".</summary>
    public static string Format(int? months)
    {
        if (months is not { } m) return "—";
        if (m < 1) return "меньше месяца";
        if (m < 12) return $"{m} мес.";
        var years = m / 12;
        var rest = m % 12;
        var y = $"{years} {YearWord(years)}";
        return rest == 0 ? y : $"{y} {rest} мес.";
    }

    public static string YearWord(int n)
    {
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 14) return "лет";
        return (n % 10) switch
        {
            1 => "год",
            2 or 3 or 4 => "года",
            _ => "лет",
        };
    }

    public static string VerdictText(DriverAgeVerdict v) => v switch
    {
        DriverAgeVerdict.Fresh => "СВЕЖИЙ",
        DriverAgeVerdict.Aging => "СТАРШЕ 6 МЕС.",
        DriverAgeVerdict.Old => "СТАРШЕ ГОДА",
        _ => "ДАТА НЕИЗВЕСТНА",
    };
}
