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
        if (m < 1) return Loc.T("меньше месяца");
        if (m < 12) return Loc.Tf("{0} мес.", m);
        var years = m / 12;
        var rest = m % 12;
        var y = Loc.N(years, "{0} год", "{0} года", "{0} лет");
        return rest == 0 ? y : Loc.Tf("{0} {1} мес.", y, rest);
    }

    public static string VerdictText(DriverAgeVerdict v) => v switch
    {
        DriverAgeVerdict.Fresh => Loc.T("СВЕЖИЙ"),
        DriverAgeVerdict.Aging => Loc.T("СТАРШЕ 6 МЕС."),
        DriverAgeVerdict.Old => Loc.T("СТАРШЕ ГОДА"),
        _ => Loc.T("ДАТА НЕИЗВЕСТНА"),
    };
}
