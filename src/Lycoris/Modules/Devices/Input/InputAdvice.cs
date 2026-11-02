using System.Globalization;

namespace Lycoris.Modules.Devices.Input;

/// <summary>
/// Pure advice math and Russian texts for the mouse and keyboard (research devices_mouse-keyboard.md §3, §4, §7).
/// Nothing here reads or changes the system.
/// </summary>
internal static class InputAdvice
{
    /// <summary>Culture of the interface language (ru-RU or en-US) for shown numbers.</summary>
    public static CultureInfo Ru => Loc.Culture;

    /// <summary>Pointer speed 10: one sensor count = one pixel (Windows default, EPP off).</summary>
    public const int NeutralSpeed = 10;

    /// <summary>Pointer speeds this low make the cursor nearly unusable: applied only with the 15-second confirmation.</summary>
    public const int LowSpeedMax = 3;

    /// <summary>The sensible DPI range at pointer speed 10 on a 1440p desktop (research mouse-dpi-advice).</summary>
    public const int DpiLow = 800;
    public const int DpiHigh = 1600;

    /// <summary>A measured DPI within this share of a range edge counts as inside (the test itself is ±a few %).</summary>
    public const double DpiTolerance = 0.05;

    // ───────────────────────────── Pointer speed ─────────────────────────────

    /// <summary>
    /// The multiplier Windows applies to mouse counts at a pointer speed (EPP off; Liquipedia table): 1→1/32, 2→1/16,
    /// 3→1/8, 4→2/8 … 10→1, then +0,25 per step up to 20→3,5.
    /// </summary>
    public static double PointerMultiplier(int speed) => speed switch
    {
        <= 1 => 1.0 / 32,
        2 => 1.0 / 16,
        3 => 1.0 / 8,
        <= NeutralSpeed => (speed - 2) / 8.0,
        _ => 1 + (Math.Min(speed, 20) - NeutralSpeed) * 0.25,
    };

    /// <summary>«1/32», «0,5», «1», «2,25».</summary>
    public static string MultiplierText(int speed) => speed switch
    {
        <= 1 => "1/32",
        2 => "1/16",
        3 => "1/8",
        _ => PointerMultiplier(speed).ToString("0.###", Ru),
    };

    /// <summary>«14 (×2)» — the slider value with its multiplier.</summary>
    public static string SpeedText(long speed) => $"{speed.ToString(CultureInfo.InvariantCulture)} (×{MultiplierText((int)speed)})";

    /// <summary>
    /// The DPI that keeps today's cursor speed after switching to pointer speed 10: round(dpi × multiplier / 50) × 50
    /// (mice set DPI in steps of 50).
    /// </summary>
    public static int CompensatedDpi(int dpi, int currentSpeed) =>
        (int)(Math.Round(dpi * PointerMultiplier(currentSpeed) / 50.0, MidpointRounding.AwayFromZero) * 50);

    /// <summary>How far the mouse travels (cm) to cross <paramref name="widthPx"/> pixels: widthPx / (dpi × multiplier) × 2,54.</summary>
    public static double DesktopTravelCm(int widthPx, double dpi, double multiplier) =>
        widthPx > 0 && dpi > 0 && multiplier > 0 ? widthPx / (dpi * multiplier) * 2.54 : 0;

    /// <summary>«через экран 2560 px ≈ 4 см» (null without a screen width).</summary>
    public static string? TravelText(int widthPx, double dpi, double multiplier)
    {
        var cm = DesktopTravelCm(widthPx, dpi, multiplier);
        if (cm <= 0) return null;
        var text = cm < 10 ? cm.ToString("0.#", Ru) : cm.ToString("0", Ru);
        return Loc.Tf("через экран {0} px ≈ {1} см", widthPx.ToString(CultureInfo.InvariantCulture), text);
    }

    /// <summary>Below, inside or above 800–1600 DPI (with the measurement tolerance at both edges).</summary>
    public static int DpiRangeSide(double dpi) =>
        dpi < DpiLow * (1 - DpiTolerance) ? -1 : dpi > DpiHigh * (1 + DpiTolerance) ? 1 : 0;

    /// <summary>«≈ 1600 DPI» — rounded to the nearest 50 like the settings of mice.</summary>
    public static string DpiText(double dpi) =>
        $"≈ {(Math.Round(dpi / 50, MidpointRounding.AwayFromZero) * 50).ToString("0", CultureInfo.InvariantCulture)} DPI";

    /// <summary>
    /// What the measured DPI means at pointer speed 10 on this screen. <c>OutsideRange</c> when it is clearly outside
    /// 800–1600 (research: 400 is too slow for the desktop at 1:1, above ~3200 the cursor gets twitchy).
    /// </summary>
    public static (string Text, bool OutsideRange) DpiAdvice(double? dpi, int widthPx)
    {
        if (dpi is not { } d || d <= 0)
            return (Loc.T("DPI ещё не измерено — нужна линейка или банковская карта."), false);
        var travel = TravelText(widthPx, d, 1);
        var head = travel is null ? DpiText(d) : $"{DpiText(d)}: {travel}";
        return DpiRangeSide(d) switch
        {
            < 0 => (Loc.Tf("{0} — для рабочего стола медленно. Удобнее {1}–{2} DPI в программе мыши.", head, DpiLow, DpiHigh), true),
            > 0 => (Loc.Tf("{0} — курсор дёрганый. Удобнее {1}–{2} DPI в программе мыши.", head, DpiLow, DpiHigh), true),
            _ => (Loc.Tf("{0} — удобный диапазон для рабочего стола и игр.", head), false),
        };
    }

    /// <summary>
    /// The pointer-speed suggestion: 10 for everyone, with the DPI to set in the mouse's program so the cursor keeps its
    /// speed. Null when the speed is already 10 or unknown. Without a measured DPI the suggestion waits for the DPI test.
    /// When the compensating DPI is outside 800–1600 the reason offers both ways, so it never contradicts the DPI advice.
    /// </summary>
    public static Recommendation? PointerSpeedRecommendation(int? currentSpeed, double? measuredDpi, DeviceLink? toolLink)
    {
        if (currentSpeed is not { } speed || speed == NeutralSpeed) return null;
        if (measuredDpi is not { } dpi || dpi <= 0)
            return new Recommendation(DeviceValue.Of(NeutralSpeed),
                Loc.T("Сначала проверьте DPI мыши — тогда Lycoris скажет, какое DPI поставить, чтобы курсор не стал медленнее или быстрее."),
                RecommendPolicy.Suggest, toolLink) { NeedsMeasurement = true };
        return new Recommendation(DeviceValue.Of(NeutralSpeed), PointerSpeedReason(speed, dpi), RecommendPolicy.Suggest);
    }

    /// <summary>«Каждое движение мыши = 1 пиксель; чтобы скорость не изменилась, поставьте в программе мыши ≈ 3200 DPI» (+ the alternative).</summary>
    public static string PointerSpeedReason(int currentSpeed, double dpi)
    {
        var target = CompensatedDpi((int)Math.Round(dpi), currentSpeed);
        return DpiRangeSide(target) switch
        {
            > 0 => Loc.Tf("Каждое движение мыши = 1 пиксель. В программе мыши: ≈ {0} DPI — скорость как сейчас, или {1} DPI — курсор станет медленнее.", target, DpiHigh),
            < 0 => Loc.Tf("Каждое движение мыши = 1 пиксель. В программе мыши: ≈ {0} DPI — скорость как сейчас, или {1} DPI — курсор станет быстрее.", target, DpiLow),
            _ => Loc.Tf("Каждое движение мыши = 1 пиксель; чтобы скорость не изменилась, поставьте в программе мыши ≈ {0} DPI.", target),
        };
    }

    // ───────────────────────────── Polling rate ─────────────────────────────

    /// <summary>Advice for a measured polling rate (research mouse-polling-advice).</summary>
    public static string PollingAdvice(int? hz) => hz switch
    {
        null or <= 0 => Loc.T("Обычно хватает 1000 Гц; 2000–4000 Гц — по желанию, 8000 Гц — только после проверки своих игр."),
        < 1000 => Loc.Tf("{0} Гц — меньше обычного: поставьте 1000 Гц в программе мыши, задержка станет меньше на {1} мс.",
                  hz, (1000.0 / hz.Value - 1).ToString("0.#", Ru)),
        1000 => Loc.T("1000 Гц — оптимально для любых игр. Многие беспроводные мыши по кабелю больше 1000 Гц не дают."),
        < 8000 => Loc.Tf("{0} Гц — по желанию: нужен сильный процессор и монитор от 240 Гц. Если игры подтормаживают, верните 1000 Гц.", hz),
        _ => Loc.Tf("{0} Гц — только если вы проверили свои игры: процессор нагружается сильнее, старые игры могут подтормаживать. Иначе 2000–4000 Гц.", hz),
    };

    /// <summary>A finding for the plan: below 1000 Hz or at 8000 Hz; null otherwise (1000–4000 Hz are fine, unknown is not advice).</summary>
    public static bool PollingNeedsAdvice(int? hz) => hz is > 0 and (< 1000 or >= 8000);

    // ───────────────────────────── Keyboard ─────────────────────────────

    /// <summary>0..3 → «≈ 250 мс» … «≈ 1000 мс» (hardware dependent).</summary>
    public static string KeyboardDelayText(long v) => Loc.Tf("≈ {0} мс", ((Math.Clamp(v, 0, 3) + 1) * 250).ToString(CultureInfo.InvariantCulture));

    /// <summary>Repeats per second of keyboard speed 0..31: 2,5 + 27,5 × v / 31.</summary>
    public static double RepeatRate(long v) => 2.5 + 27.5 * Math.Clamp(v, 0, 31) / 31.0;

    /// <summary>«≈ 30,0 в секунду».</summary>
    public static string RepeatRateText(long v) => Loc.Tf("≈ {0} в секунду", RepeatRate(v).ToString("0.0", Ru));

    /// <summary>«≈30/с», «≈20,2/с» — the effect of the 0–31 value, compact for the slider column and the plan.</summary>
    public static string RepeatSpeedText(long v) => Loc.Tf("≈{0}/с", RepeatRate(v).ToString("0.#", Ru));

    // ───────────────────────────── Russian formatting ─────────────────────────────

    /// <summary>1 строка, 2 строки, 5 строк.</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100;
        var b = a % 10;
        if (a is > 10 and < 20) return many;
        return b switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many,
        };
    }

    /// <summary>«3 строки» / «экран целиком».</summary>
    public static string WheelLinesText(long v) =>
        v == Lycoris.Core.Devices.SpiCatalog.PageScroll ? Loc.T("экран целиком") : Loc.N(v, "{0} строка", "{0} строки", "{0} строк");

    /// <summary>«3 символа».</summary>
    public static string WheelCharsText(long v) => Loc.N(v, "{0} символ", "{0} символа", "{0} символов");

    /// <summary>Trails 0 and 1 both mean «off» in Windows.</summary>
    public static long NormalizeTrails(long v) => v <= 1 ? 0 : v;

    public static string TrailsText(long v) => NormalizeTrails(v) == 0 ? Loc.T("выкл") : Loc.Tf("длина {0}", v.ToString(CultureInfo.InvariantCulture));
}
