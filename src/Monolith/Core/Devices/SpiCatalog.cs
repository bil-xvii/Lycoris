using System.Globalization;
using Microsoft.Win32;

namespace Monolith.Core.Devices;

/// <summary>How the current value is read.</summary>
public enum SpiGet
{
    /// <summary>SystemParametersInfo(GET, 0, &amp;int).</summary>
    PvInt,
    /// <summary>SystemParametersInfo(GET, 0, &amp;BOOL).</summary>
    PvBool,
    /// <summary>GetDoubleClickTime().</summary>
    DoubleClickTime,
    /// <summary>GetSystemMetrics(SM_SWAPBUTTON = 23).</summary>
    SwapMetric,
}

/// <summary>How the new value is passed to SystemParametersInfo(SET, …).</summary>
public enum SpiSet
{
    /// <summary>The value goes in uiParam (pvParam = null).</summary>
    UiParam,
    /// <summary>The value goes in pvParam itself (cast to a pointer), uiParam = 0.</summary>
    PvParam,
}

/// <summary>Where Windows persists the parameter for the account (SPIF_UPDATEINIFILE writes it there).</summary>
public sealed record SpiPersisted(string Path, string Name, RegistryValueKind Kind);

/// <summary>
/// One per-user Windows input parameter Lycoris may change (research devices_mouse-keyboard.md). The valid range is
/// what an original may be (the journal check); the UI range — or <see cref="AllowedTargets"/> — is what Lycoris sets.
/// </summary>
public sealed record SpiEntry(string Id, string Title, uint GetAction, uint SetAction, SpiGet GetShape, SpiSet SetShape,
    int ValidMin, int ValidMax, int UiMin, int UiMax, bool AllowPageScroll, SpiPersisted? Persisted, Func<int, string> Describe)
{
    /// <summary>When set, only these targets are allowed (wheel routing: 0 or 2, never the undocumented hybrid 1).</summary>
    public IReadOnlySet<int>? AllowedTargets { get; init; }

    /// <summary>An original value Lycoris can put back.</summary>
    public bool IsValidOriginal(int v) => (v >= ValidMin && v <= ValidMax) || (AllowPageScroll && v == SpiCatalog.PageScroll);

    /// <summary>A value Lycoris may set.</summary>
    public bool IsAllowedTarget(int v)
    {
        if (AllowPageScroll && v == SpiCatalog.PageScroll) return true;
        if (AllowedTargets is { } set) return set.Contains(v);
        return v >= UiMin && v <= UiMax;
    }
}

/// <summary>The eleven SystemParametersInfo parameters of the Devices page (compiled in: the journal check uses this list).</summary>
public static class SpiCatalog
{
    /// <summary>WHEEL_PAGESCROLL (UINT_MAX) as stored in the journal: «экран целиком».</summary>
    public const int PageScroll = -1;

    private const string Mouse = @"Control Panel\Mouse";
    private const string Desktop = @"Control Panel\Desktop";
    private const string Keyboard = @"Control Panel\Keyboard";

    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static IReadOnlyList<SpiEntry> All { get; } =
    [
        new("mouse-speed", Loc.T("Скорость указателя"), 0x70, 0x71, SpiGet.PvInt, SpiSet.PvParam, 1, 20, 1, 20, false,
            new(Mouse, "MouseSensitivity", RegistryValueKind.String), v => N(v)),
        new("wheel-lines", Loc.T("Строк за шаг колёсика"), 0x68, 0x69, SpiGet.PvInt, SpiSet.UiParam, 0, 10000, 1, 100, true,
            new(Desktop, "WheelScrollLines", RegistryValueKind.String), v => v == PageScroll ? Loc.T("экран целиком") : N(v)),
        new("wheel-chars", Loc.T("Горизонтальная прокрутка колёсиком"), 0x6C, 0x6D, SpiGet.PvInt, SpiSet.UiParam, 0, 10000, 1, 100, false,
            new(Desktop, "WheelScrollChars", RegistryValueKind.String), v => Loc.Tf("{0} симв.", N(v))),
        new("wheel-routing", Loc.T("Прокручивать неактивные окна"), 0x201C, 0x201D, SpiGet.PvInt, SpiSet.PvParam, 0, 2, 0, 2, false,
            new(Desktop, "MouseWheelRouting", RegistryValueKind.DWord), v => v switch
            {
                0 => Loc.T("выкл"),
                2 => Loc.T("вкл"),
                _ => Loc.T("смешанный режим"),
            })
        {
            AllowedTargets = new HashSet<int> { 0, 2 },
        },
        new("double-click", Loc.T("Скорость двойного щелчка"), 0, 0x20, SpiGet.DoubleClickTime, SpiSet.UiParam, 1, 5000, 200, 900, false,
            new(Mouse, "DoubleClickSpeed", RegistryValueKind.String), v => Loc.Tf("{0} мс", N(v))),
        new("swap-buttons", Loc.T("Поменять кнопки мыши местами"), 0, 0x21, SpiGet.SwapMetric, SpiSet.UiParam, 0, 1, 0, 1, false,
            new(Mouse, "SwapMouseButtons", RegistryValueKind.String), v => v == 1 ? Loc.T("вкл") : Loc.T("выкл")),
        new("mouse-trails", Loc.T("Шлейф указателя"), 0x5E, 0x5D, SpiGet.PvInt, SpiSet.UiParam, 0, 100, 0, 7, false,
            new(Mouse, "MouseTrails", RegistryValueKind.String), v => v <= 1 ? Loc.T("выкл") : N(v)),
        new("mouse-sonar", Loc.T("Показывать указатель по Ctrl"), 0x101C, 0x101D, SpiGet.PvBool, SpiSet.PvParam, 0, 1, 0, 1, false,
            null, v => v == 1 ? Loc.T("вкл") : Loc.T("выкл")),
        new("mouse-vanish", Loc.T("Скрывать указатель при вводе"), 0x1020, 0x1021, SpiGet.PvBool, SpiSet.PvParam, 0, 1, 0, 1, false,
            null, v => v == 1 ? Loc.T("вкл") : Loc.T("выкл")),
        new("keyboard-delay", Loc.T("Задержка перед повтором"), 0x16, 0x17, SpiGet.PvInt, SpiSet.UiParam, 0, 3, 0, 3, false,
            new(Keyboard, "KeyboardDelay", RegistryValueKind.String), v => Loc.Tf("≈{0} мс", N((v + 1) * 250))),
        new("keyboard-speed", Loc.T("Скорость повтора"), 0x0A, 0x0B, SpiGet.PvInt, SpiSet.UiParam, 0, 31, 0, 31, false,
            new(Keyboard, "KeyboardSpeed", RegistryValueKind.String), v => N(v)),
    ];

    private static readonly Dictionary<string, SpiEntry> ById = All.ToDictionary(e => e.Id, StringComparer.Ordinal);

    public static SpiEntry? Find(string? id) => id is not null && ById.TryGetValue(id, out var e) ? e : null;
}
