using System.Globalization;

namespace Monolith.Modules.Dashboard;

/// <summary>Number / size / rate / duration formatting for the dashboard in the interface language (pure, unit-tested).</summary>
public static class DashFormat
{
    public const string Missing = "—";

    private static CultureInfo Ru => Loc.Culture;
    private const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024, Tb = Gb * 1024;

    /// <summary>Whole-number percentage ("37") or "—".</summary>
    public static string Percent(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Round(Math.Clamp(v, 0, 100)).ToString("0", Ru) : Missing;

    /// <summary>Transfer rate: "0 Б/с", "850 Б/с", "12,4 КБ/с", "1,2 МБ/с", "124 МБ/с".</summary>
    public static string Rate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } b || !double.IsFinite(b) || b < 0) return Missing;
        return b switch
        {
            < Kb => Loc.Tf("{0} Б/с", b.ToString("0", Ru)),
            < Mb => Loc.Tf("{0} КБ/с", Scaled(b / Kb)),
            < Gb => Loc.Tf("{0} МБ/с", Scaled(b / Mb)),
            _ => Loc.Tf("{0} ГБ/с", Scaled(b / Gb)),
        };
    }

    /// <summary>Size: "512 МБ", "12,4 ГБ", "476 ГБ", "1,8 ТБ".</summary>
    public static string Size(double? bytes)
    {
        if (bytes is not { } b || !double.IsFinite(b) || b < 0) return Missing;
        return b switch
        {
            < Mb => Loc.Tf("{0} КБ", (b / Kb).ToString("0", Ru)),
            < Gb => Loc.Tf("{0} МБ", (b / Mb).ToString("0", Ru)),
            < Tb => Loc.Tf("{0} ГБ", Scaled(b / Gb)),
            _ => Loc.Tf("{0} ТБ", (b / Tb).ToString("0.0", Ru)),
        };
    }

    /// <summary>"12,4 / 32 ГБ" — both numbers in gigabytes (the unit of the total).</summary>
    public static string UsedOfTotalGb(double? used, double? total)
    {
        if (used is not { } u || total is not { } t || t <= 0) return Missing;
        return Loc.Tf("{0} / {1} ГБ", (u / Gb).ToString("0.0", Ru), Whole(t / Gb));
    }

    /// <summary>VRAM: "0,9 / 16 ГБ" — the total rounded to the card's nominal size (16 GB cards report ≈15,6 GiB).</summary>
    public static string UsedOfCapacityGb(double? used, double? total)
    {
        if (used is not { } u || total is not { } t || t <= 0) return Missing;
        return Loc.Tf("{0} / {1} ГБ", (u / Gb).ToString("0.0", Ru), Math.Max(1, Math.Round(t / Gb)).ToString("0", Ru));
    }

    /// <summary>Gigabytes rounded the way the OS shows installed RAM / VRAM: "32 ГБ", "15,9 ГБ" → "16 ГБ".</summary>
    public static string WholeGb(double? bytes) =>
        bytes is { } b && b > 0 ? Loc.Tf("{0} ГБ", Math.Round(b / Gb).ToString("0", Ru)) : Missing;

    /// <summary>Clock: "5,08 ГГц" (≥ 1000 MHz) or "800 МГц".</summary>
    public static string Clock(double? mhz)
    {
        if (mhz is not { } m || !double.IsFinite(m) || m <= 0) return Missing;
        return m >= 1000 ? Loc.Tf("{0} ГГц", (m / 1000).ToString("0.00", Ru)) : Loc.Tf("{0} МГц", m.ToString("0", Ru));
    }

    public static string Temperature(double? celsius) =>
        celsius is { } c && double.IsFinite(c) ? Loc.Tf("{0} °C", c.ToString("0", Ru)) : Missing;

    /// <summary>Uptime: "3 д 04 ч 12 мин", "4 ч 07 мин", "12 мин".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalDays >= 1) return Loc.Tf("{0} д {1} ч {2} мин", (int)span.TotalDays, span.Hours.ToString("00"), span.Minutes.ToString("00"));
        if (span.TotalHours >= 1) return Loc.Tf("{0} ч {1} мин", span.Hours, span.Minutes.ToString("00"));
        return Loc.Tf("{0} мин", span.Minutes);
    }

    /// <summary>Russian plural: Plural(5, "день", "дня", "дней") → "дней". Pure grammar helper (use <see cref="Loc.N"/> for UI text).</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        n = Math.Abs(n);
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod10 == 1 && mod100 != 11) return one;
        if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return few;
        return many;
    }

    /// <summary>Age of a date in words: "3 дня", "2 месяца", "1 год".</summary>
    public static string Age(DateTime date, DateTime now)
    {
        var days = Math.Max(0, (int)(now.Date - date.Date).TotalDays);
        if (days < 31) return Loc.N(days, "{0} день", "{0} дня", "{0} дней");
        var months = (int)Math.Round(days / 30.44);
        if (months < 12) return Loc.N(months, "{0} месяц", "{0} месяца", "{0} месяцев");
        var years = (int)Math.Floor(days / 365.25);
        return Loc.N(Math.Max(1, years), "{0} год", "{0} года", "{0} лет");
    }

    public static string Date(DateTime date) => Loc.DateText(date);

    /// <summary>"7 %" for shares (free space etc.).</summary>
    public static string Share(double fraction) => Loc.Tf("{0} %", Math.Round(fraction * 100).ToString("0", Ru));

    private static string Scaled(double v) => v >= 100 ? v.ToString("0", Ru) : v.ToString("0.0", Ru);

    private static string Whole(double v) => Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0", Ru) : v.ToString("0.0", Ru);
}
