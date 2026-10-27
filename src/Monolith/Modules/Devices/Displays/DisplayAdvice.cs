using System.Globalization;
using Monolith.Core.Devices;
using Monolith.Modules.Dashboard.Checks;

namespace Monolith.Modules.Devices.Displays;

/// <summary>Where the recommendation is made: laptop panel, battery, Windows' own dynamic refresh rate.</summary>
public sealed record ModeContext(bool IsInternal = false, bool OnBattery = false, bool DrrLikely = false);

/// <summary>The recommended mode of a monitor and how it enters the plan.</summary>
public sealed record ModeAdvice(OfferedMode Target, RecommendPolicy Policy, string Reason);

/// <summary>A bottleneck between the monitor and the PC (EDID lists more than Windows offers).</summary>
public sealed record BottleneckAdvice(string Text, bool OnMotherboard);

/// <summary>
/// The recommendations for monitors (docs/research/devices_displays.md §3, with the review fixes): native size at the highest
/// refresh, never a downgrade, laptop panels and battery left to Windows, honest link-bottleneck notes. Pure.
/// </summary>
public static class DisplayAdvice
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Effective link rates, Gbit/s (Wikipedia DisplayPort / HDMI, research §3 display-link-bandwidth).</summary>
    public const double Hdmi20 = 14.4, Hdmi21 = 42.0, DpHbr2 = 17.28, DpHbr3 = 25.92;

    // ───────────────────────────── Native size and mode ─────────────────────────────

    /// <summary>
    /// The monitor's native size among the offered ones: the preferred mode (CCD type 3), else the EDID's first timing, else
    /// the largest offered size. Certain only when the preferred size, the EDID size and the largest offered size agree
    /// (many TVs list 1080p first although they are 4K).
    /// </summary>
    public static (DisplaySize Size, bool Certain)? NativeSize(IReadOnlyList<OfferedMode> modes, DisplaySize? preferred, DisplaySize? edidFirst)
    {
        if (modes.Count == 0) return null;
        var sizes = modes.Select(m => m.Size).Distinct().ToList();
        var largest = sizes.OrderByDescending(s => s.Pixels).ThenByDescending(s => s.Width).First();
        var p = preferred is { } pp && sizes.Contains(pp) ? pp : (DisplaySize?)null;
        var e = edidFirst is { } ee && sizes.Contains(ee) ? ee : (DisplaySize?)null;
        var native = p ?? e ?? largest;
        var certain = p is { } ps && ps == largest && (edidFirst is null || edidFirst == ps);
        return (native, certain);
    }

    /// <summary>
    /// The native size at the highest progressive refresh, keeping the current orientation (the offered modes already
    /// have it). Null when nothing should change. Rules: never a smaller size than the current one; a size change is
    /// pre-checked (Include) only when every source agrees on the native size; the refresh is raised only when it is more
    /// than 1 Hz below the maximum (239,97 vs 240 is the same timing), never on battery, and not on a laptop panel that has
    /// Windows' dynamic refresh rate; a laptop panel on AC power gets it unchecked (Suggest).
    /// </summary>
    public static ModeAdvice? RecommendedMode(IReadOnlyList<OfferedMode> modes, DisplaySize? preferred, DisplaySize? edidFirst,
        DisplayModeSpec? current, ModeContext? context = null)
    {
        var ctx = context ?? new ModeContext();
        if (current is null || modes.Count == 0) return null;
        if (NativeSize(modes, preferred, edidFirst) is not { } native) return null;

        var currentSize = new DisplaySize(current.Width, current.Height);
        var target = currentSize;
        var sizeChange = false;
        if (native.Size != currentSize && native.Size.Pixels > currentSize.Pixels)
        {
            target = native.Size;
            sizeChange = true;
        }

        var rates = modes.Where(m => m.Size == target).Select(m => m.Hz).Distinct().OrderByDescending(h => h).ToList();
        if (rates.Count == 0) return null;
        var max = rates[0];
        var refreshAllowed = !ctx.OnBattery && !(ctx.IsInternal && ctx.DrrLikely);

        int hz;
        bool refreshChange;
        if (sizeChange)
        {
            // A new size needs a rate of its own: the best one when allowed, otherwise the closest to the current rate.
            hz = refreshAllowed ? max : rates.Where(r => r <= Math.Max(current.Frequency, rates[^1])).DefaultIfEmpty(rates[^1]).Max();
            refreshChange = refreshAllowed && HealthRules.IsBelowMaxRefresh(current.Frequency, hz);
        }
        else
        {
            if (!refreshAllowed || !HealthRules.IsBelowMaxRefresh(current.Frequency, max)) return null;
            hz = max;
            refreshChange = true;
        }

        var policy = (sizeChange && !native.Certain) || (refreshChange && ctx.IsInternal) ? RecommendPolicy.Suggest : RecommendPolicy.Include;
        var reason = sizeChange && refreshChange ? $"Родное разрешение и {hz} Гц — самое чёткое изображение, плавнее игры и интерфейс"
            : sizeChange ? "Родное разрешение монитора — самое чёткое изображение"
            : ctx.IsInternal ? $"Экран умеет {hz} Гц — плавнее игры и интерфейс; от батареи расход чуть выше"
            : $"Монитор умеет {hz} Гц — плавнее игры и интерфейс";
        return new ModeAdvice(new OfferedMode(target.Width, target.Height, hz), policy, reason);
    }

    /// <summary>
    /// Windows 11 offers «Динамическая частота обновления» on laptop panels with VRR and at least 120 Hz: such a panel is
    /// left to Windows (research display-drr).
    /// </summary>
    public static bool DrrLikely(bool isWindows11, bool isInternal, EdidInfo? edid) =>
        isWindows11 && isInternal && edid?.VrrRange is { Max: >= 120 };

    // ───────────────────────────── Link bandwidth ─────────────────────────────

    /// <summary>Uncompressed RGB data rate, Gbit/s: pixel clock × 3 channels × bits per channel.</summary>
    public static double Bandwidth(long pixelClockHz, int bpc) => pixelClockHz * 3.0 * bpc / 1e9;

    /// <summary>
    /// A note only when the EDID lists a faster timing at the native size than Windows offers there (with 1 Hz of slack):
    /// the cable, port or adapter is the bottleneck. Advice in order: the motherboard output (only when the monitor really
    /// is on the integrated GPU), the HDMI 2.0 limit, a certified DisplayPort cable / the monitor's DP version, docks.
    /// </summary>
    public static BottleneckAdvice? Bottleneck(EdidInfo? edid, DisplaySize native, int offeredMaxAtNative, int outputTechnology, bool onIntegratedGpu)
    {
        if (edid?.MaxRefreshAt(native.Width, native.Height) is not { } edidMax) return null;
        if (offeredMaxAtNative <= 0 || edidMax <= offeredMaxAtNative + 1) return null;

        var hz = (int)Math.Round(edidMax);
        var parts = new List<string>();
        if (onIntegratedGpu) parts.Add("монитор подключён к материнской плате — переключите кабель в разъём видеокарты");
        var clock = edid.PixelClockAt(native.Width, native.Height, edidMax);
        var need = clock is { } c ? Bandwidth(c, 8) : (double?)null;
        switch (outputTechnology)
        {
            case DisplayNative.OUTPUT_HDMI:
                if (need is null || need > Hdmi20)
                    parts.Add(need is { } n
                        ? $"HDMI 2.0 пропускает до 14,4 Гбит/с, а для {hz} Гц нужно ≈ {n.ToString("0.0", Ru)} — нужен DisplayPort или HDMI 2.1 (порт и кабель)"
                        : "HDMI 2.0 часто не тянет высокую частоту — нужен DisplayPort или HDMI 2.1 (порт и кабель)");
                break;
            case DisplayNative.OUTPUT_DISPLAYPORT_EXTERNAL or DisplayNative.OUTPUT_DISPLAYPORT_USB_TUNNEL:
                parts.Add("возьмите сертифицированный VESA кабель DisplayPort 1.4 и проверьте версию DisplayPort в меню монитора");
                break;
        }
        parts.Add("переходники, доки и KVM часто ограничивают частоту");
        return new BottleneckAdvice($"Монитор умеет {hz} Гц, а Windows предлагает максимум {offeredMaxAtNative} Гц: {string.Join("; ", parts)}.", onIntegratedGpu);
    }

    // ───────────────────────────── Texts ─────────────────────────────

    /// <summary>«ограничьте FPS до 237» — at least 3 below the refresh rate keeps G-SYNC/FreeSync engaged (Blur Busters).</summary>
    public static string FpsCapText(int maxHz) => $"ограничьте FPS до {Math.Max(1, maxHz - 3).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The VRR row: «48–240 Гц» and what to do in the driver panel.</summary>
    public static string VrrRangeText((int Min, int Max) range) =>
        $"{range.Min.ToString(CultureInfo.InvariantCulture)}–{range.Max.ToString(CultureInfo.InvariantCulture)} Гц";

    public static string VrrHint(int maxHz) => $"Включите G-SYNC/FreeSync в панели видеокарты и {FpsCapText(maxHz)} — плавно и без лишней задержки";

    /// <summary>
    /// A neutral HDR hint (MONOLITH cannot read the DisplayHDR tier, so it never recommends a value), with the peak
    /// luminance from the EDID when known. At most 120 characters.
    /// </summary>
    public static string HdrHint(EdidInfo? edid)
    {
        const string text = "Для HDR-игр и видео. На OLED и с локальным затемнением — лучше всего, на HDR400 без него — лучше выкл.";
        return edid?.MaxLuminanceNits is { } nits and > 0 ? $"{text} Пик ≈ {Math.Round(nits).ToString("0", Ru)} нит" : text;
    }

    /// <summary>«~200 нит» of SDR content while HDR is on.</summary>
    public static string SdrWhiteText(double nits) => $"SDR-контент ≈ {Math.Round(nits).ToString("0", Ru)} нит";

    /// <summary>The monitor names a self-emissive panel (VRR in DirectX 11 games can flicker on OLED loading screens).</summary>
    public static bool LooksLikeOled(string? name) => name?.Contains("OLED", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Games open on the primary monitor: when a slower external monitor is primary, the fastest one gets a note (no write:
    /// changing the primary is left to Windows Settings). Returns the monitor path that should be primary, or null.
    /// </summary>
    public static string? FastestShouldBePrimary(IReadOnlyList<(string Path, bool IsPrimary, bool IsInternal, int MaxHz)> monitors)
    {
        if (monitors.Count < 2) return null;
        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        if (primary.Path is null || primary.IsInternal) return null;
        var fastest = monitors.Where(m => !m.IsInternal).OrderByDescending(m => m.MaxHz).FirstOrDefault();
        if (fastest.Path is null || fastest.IsPrimary || fastest.MaxHz <= primary.MaxHz + 1) return null;
        return fastest.Path;
    }
}
