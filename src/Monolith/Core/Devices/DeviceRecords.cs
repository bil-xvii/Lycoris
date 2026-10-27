using System.Globalization;
using System.Text.Json.Serialization;

namespace Monolith.Core.Devices;

// Journal payloads of the «Устройства и сеть» page. Every class round-trips through System.Text.Json (public setters /
// init, positional records), because the journal file is JSON. They describe the state BEFORE a change (the original)
// plus what is needed to find the thing again (monitor device path, endpoint id, adapter GUID).

/// <summary>A per-user Windows parameter (SystemParametersInfo): the original live value and its persisted HKCU value.</summary>
public sealed class SpiSnapshot
{
    /// <summary><see cref="SpiCatalog"/> id ("mouse-speed").</summary>
    public string Id { get; init; } = "";
    /// <summary>The live value before the change. WHEEL_PAGESCROLL («экран целиком») is stored as -1.</summary>
    public int Value { get; init; }
    /// <summary>The persisted HKCU value before the change; null for parameters without one (UserPreferencesMask bits).</summary>
    public RegistryValueSnapshot? Persisted { get; init; }
}

/// <summary>A display mode as GDI/CCD report it. The exact refresh is the rational <see cref="RefreshNumerator"/>/<see cref="RefreshDenominator"/>.</summary>
public sealed record DisplayModeSpec(int Width, int Height, int Frequency, uint RefreshNumerator, uint RefreshDenominator,
    int BitsPerPel, int Orientation, int PositionX, int PositionY)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>The exact refresh rate (239,97 Гц), or the GDI integer when no rational one is known.</summary>
    [JsonIgnore]
    public double ExactHz => RefreshDenominator > 0 ? (double)RefreshNumerator / RefreshDenominator : Frequency;

    /// <summary>Same resolution and refresh: the exact rates within 0,5 Гц, or the same GDI integer (240 vs 239,97).</summary>
    public bool SameMode(DisplayModeSpec? o) =>
        o is not null && Width == o.Width && Height == o.Height
        && (Math.Abs(ExactHz - o.ExactHz) < 0.5 || (Frequency > 1 && Frequency == o.Frequency));

    /// <summary>
    /// Plausible values only (the journal is untrusted input). GDI reports 0 or 1 Hz for «hardware default», so an
    /// original may carry 0; MONOLITH never sets such a mode (<see cref="IsSaneTarget"/>).
    /// </summary>
    [JsonIgnore]
    public bool IsSane =>
        Width is >= 320 and <= 16384 && Height is >= 320 and <= 16384
        && Frequency is >= 0 and <= 1000
        && RefreshNumerator <= 1_000_000_000 && RefreshDenominator <= 1_000_000_000
        && (RefreshNumerator == 0 || RefreshDenominator > 0)
        && BitsPerPel is 0 or 8 or 16 or 24 or 32
        && Orientation is >= 0 and <= 3
        && Math.Abs((long)PositionX) <= 65536 && Math.Abs((long)PositionY) <= 65536;

    /// <summary>A mode MONOLITH may set: sane and with a real refresh rate.</summary>
    [JsonIgnore]
    public bool IsSaneTarget => IsSane && Frequency >= 1 && ExactHz >= 1;

    /// <summary>«2560×1440, 239,97 Гц» (an integer when the rate is exact).</summary>
    public string Describe() => $"{Width}×{Height}, {HzText()}";

    /// <summary>«239,97 Гц» / «240 Гц» / «частота по умолчанию».</summary>
    public string HzText()
    {
        var hz = ExactHz;
        if (hz < 1) return "частота по умолчанию";
        var rounded = Math.Round(hz);
        return Math.Abs(hz - rounded) < 0.005
            ? $"{rounded.ToString("0", Ru)} Гц"
            : $"{hz.ToString("0.00", Ru)} Гц";
    }
}

/// <summary>Resolution + refresh of one monitor. <see cref="PendingConfirm"/>: applied temporarily, not confirmed yet.</summary>
public sealed class DisplayModeSnapshot
{
    /// <summary>The CCD monitor device path (\\?\DISPLAY#…#{e6f07b5f-…}) — stable across reboots and ports.</summary>
    public string MonitorPath { get; init; } = "";
    public string MonitorName { get; init; } = "";
    public DisplayModeSpec Original { get; init; } = null!;
    public DisplayModeSpec Target { get; init; } = null!;
    public bool PendingConfirm { get; set; }
}

/// <summary>HDR (advanced colour) of one monitor, plus what an HDR switch may change as a side effect.</summary>
public sealed class DisplayHdrSnapshot
{
    public string MonitorPath { get; init; } = "";
    public string MonitorName { get; init; } = "";
    public bool OriginalEnabled { get; init; }
    public bool TargetEnabled { get; init; }
    public bool PendingConfirm { get; set; }
    /// <summary>The mode before the switch: the driver may lower the refresh or bit depth when HDR turns on.</summary>
    public DisplayModeSpec? ModeBefore { get; init; }
    /// <summary>Automatic colour management before the switch (24H2 may turn it on with a bit-depth change). Null = unknown.</summary>
    public bool? AcmBefore { get; init; }
}

/// <summary>The two MCCS VCP codes MONOLITH reads and writes over DDC/CI (nothing else, never vendor codes).</summary>
public enum MonitorVcp : byte
{
    Brightness = 0x10,
    Contrast = 0x12,
}

/// <summary>Monitor brightness / contrast over DDC/CI: the original value and the monitor's range at that time.</summary>
public sealed class MonitorVcpSnapshot
{
    public string MonitorPath { get; init; } = "";
    public string MonitorName { get; init; } = "";
    public MonitorVcp Code { get; init; }
    public uint Value { get; init; }
    public uint Max { get; init; }
    public uint Min { get; init; }
}

/// <summary>Which part of an endpoint's volume a record changed: the level and the mute switch are separate changes.</summary>
public enum AudioComponent
{
    Level,
    Mute,
}

/// <summary>
/// A capture endpoint's level or mute. Only the changed <see cref="Component"/> is ever put back: reverting a level never
/// unmutes a microphone the user muted later, and reverting a mute never resets a level adjusted since.
/// </summary>
public sealed class AudioLevelSnapshot
{
    /// <summary>MMDevice endpoint id ({0.0.1.00000000}.{guid} — capture only).</summary>
    public string EndpointId { get; init; } = "";
    public string EndpointName { get; init; } = "";
    public AudioComponent Component { get; init; }
    /// <summary>BitConverter.SingleToUInt32Bits of the original scalar (0..1): restored bit-exactly.</summary>
    public uint ScalarBits { get; init; }
    /// <summary>The original mute state.</summary>
    public bool Mute { get; init; }

    [JsonIgnore] public float Scalar => BitConverter.UInt32BitsToSingle(ScalarBits);
}

/// <summary>
/// One advanced property of a network adapter (NDIS keyword, e.g. *SpeedDuplex): the original RegistryValue, found again
/// by the adapter's InterfaceGuid. Setting it restarts the adapter: the link drops for about 1–6 s.
/// </summary>
public sealed class NicPropertySnapshot
{
    public Guid InterfaceGuid { get; init; }
    public string AdapterName { get; init; } = "";
    public string Keyword { get; init; } = "";
    /// <summary>The original RegistryValue; null when the driver had no value (its default applied).</summary>
    public string? Value { get; init; }
    /// <summary>The driver's DefaultRegistryValue at apply time: written back explicitly when <see cref="Value"/> was absent.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>What an undo writes: the original value, or the driver default when there was none.</summary>
    [JsonIgnore] public string? RestoreValue => Value ?? DefaultValue;
}

/// <summary>How a change that needs an on-screen confirmation ended.</summary>
public enum DisplayApplyResult
{
    /// <summary>Applied and kept (confirmed when a confirmation was needed).</summary>
    Kept,
    /// <summary>Already set: nothing changed, no record.</summary>
    Unchanged,
    /// <summary>Applied, then put back (declined, timed out, monitor gone): no record.</summary>
    Reverted,
    /// <summary>Not allowed now (game session, remote desktop, full-screen app): nothing changed.</summary>
    Blocked,
    /// <summary>Not applied, or applied and put back after an error; the message says which.</summary>
    Failed,
}

public sealed record DisplayApplyOutcome(DisplayApplyResult Result, string? Message = null)
{
    public static readonly DisplayApplyOutcome Unchanged = new(DisplayApplyResult.Unchanged);
    public bool Changed => Result == DisplayApplyResult.Kept;
}
