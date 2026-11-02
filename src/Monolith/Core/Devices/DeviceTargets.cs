using System.Globalization;
using System.Text.RegularExpressions;

namespace Monolith.Core.Devices;

/// <summary>
/// Journal targets of device changes and the validators the journal check (JournalTrust) uses for them. A target names
/// one setting of one device: a later change of it becomes a new layer of the same target.
/// </summary>
public static class DeviceTargets
{
    /// <summary>Journal source of every change made on the «Устройства и сеть» page.</summary>
    public const string Source = "devices";

    public static string Spi(string id) => "spi:" + id;
    public static string DisplayMode(string monitorPath) => "display:mode:" + monitorPath;
    public static string DisplayHdr(string monitorPath) => "display:hdr:" + monitorPath;
    public static string Monitor(string monitorPath, MonitorVcp code) => $"ddc:{(byte)code:X2}:{monitorPath}";
    public static string AudioLevel(string endpointId) => "audio:level:" + endpointId;
    public static string AudioMute(string endpointId) => "audio:mute:" + endpointId;
    public static string Audio(string endpointId, AudioComponent component) =>
        component == AudioComponent.Mute ? AudioMute(endpointId) : AudioLevel(endpointId);
    public static string NicProperty(Guid interfaceGuid, string keyword) => $"nicprop:{interfaceGuid:B}:{keyword}";

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>A CCD monitor device path: \\?\DISPLAY#&lt;pnp id&gt;#&lt;instance&gt;#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}.</summary>
    private static readonly Regex MonitorPath = new(
        @"^\\\\\?\\DISPLAY#[A-Za-z0-9]{3,8}#[^\x00-\x1F""<>|*?]{1,200}#\{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7\}$", Options);

    /// <summary>An MMDevice endpoint id: {0.0.0.00000000}.{guid} (render) or {0.0.1.00000000}.{guid} (capture).</summary>
    private static readonly Regex EndpointId = new(@"^\{0\.0\.[01]\.00000000\}\.\{[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}$", Options);
    private static readonly Regex CaptureEndpointId = new(@"^\{0\.0\.1\.00000000\}\.\{[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}$", Options);

    public static bool IsMonitorPath(string? path) => path is not null && MonitorPath.IsMatch(path);
    public static bool IsEndpointId(string? id) => id is not null && EndpointId.IsMatch(id);

    /// <summary>
    /// Microphones and other capture endpoints. MONOLITH writes levels only there: it never raises an output volume,
    /// so a render endpoint in a level record is refused (a forged journal could otherwise blast a headset).
    /// </summary>
    public static bool IsCaptureEndpointId(string? id) => id is not null && CaptureEndpointId.IsMatch(id);

    /// <summary>The communications-ducking preference (read only here; changing it is the Optimize tweak «audio-ducking»).</summary>
    public const string DuckingPath = @"Software\Microsoft\Multimedia\Audio";
    public const string DuckingName = "UserDuckingPreference";

    // ───────────────────────────── Network adapters ─────────────────────────────

    /// <summary>
    /// The only NDIS advanced properties MONOLITH may write (research network_adapter.md): speed/duplex back to auto
    /// negotiation, and Energy-Efficient Ethernet (Microsoft *EEE, Realtek EEE / AdvancedEEE). Never offloads, RSS,
    /// buffers, interrupt moderation, flow control, jumbo frames or vendor Wi-Fi knobs (DO-NOT-INCLUDE).
    /// </summary>
    public static readonly IReadOnlyList<string> NicKeywords = ["*SpeedDuplex", "*EEE", "EEE", "AdvancedEEE"];

    public static bool IsNicKeyword(string? keyword) =>
        keyword is not null && NicKeywords.Any(k => string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>Enumeration keywords hold small decimal numbers ("0", "6", "2500").</summary>
    public static bool IsNicValue(string? value) =>
        value is { Length: >= 1 and <= 6 } && value.All(char.IsAsciiDigit);

    /// <summary>Shown with every adapter property change: the driver restarts the adapter.</summary>
    public static string NicRestartNote => Loc.T("адаптер перезапустится — связь пропадёт на 1–6 с");

    // ───────────────────────────── DDC/CI safety ─────────────────────────────

    /// <summary>
    /// Brightness under 10 % or contrast under 25 % of the monitor's range can leave a black or grey picture, so such a
    /// value is applied only with the 15-second confirmation (default: back to the previous value).
    /// </summary>
    public static bool NeedsMonitorConfirm(MonitorVcp code, uint value, uint min, uint max)
    {
        if (max <= min) return false;
        var share = code == MonitorVcp.Contrast ? 0.25 : 0.10;
        return value < min + (max - min) * share;
    }

    // ───────────────────────────── Restore page ─────────────────────────────

    private static readonly Regex MonitorModel = new(@"^\\\\\?\\DISPLAY#([A-Za-z0-9]{3,8})#", Options);

    /// <summary>"DELA1E8" from a monitor path (the EDID manufacturer + product code), or the path itself.</summary>
    public static string MonitorShort(string path) => MonitorModel.Match(path) is { Success: true } m ? m.Groups[1].Value : path;

    /// <summary>Readable form of a device target for the Restore page, or null when it is not a device target.</summary>
    public static string? DescribeTarget(string target)
    {
        if (target.StartsWith("spi:", StringComparison.Ordinal))
            return SpiCatalog.Find(target[4..]) is { } e ? Loc.Tf("Параметр Windows: {0}", e.Title) : target[4..];
        if (target.StartsWith("display:mode:", StringComparison.Ordinal))
            return Loc.Tf("Монитор {0}: разрешение и частота", MonitorShort(target["display:mode:".Length..]));
        if (target.StartsWith("display:hdr:", StringComparison.Ordinal))
            return Loc.Tf("Монитор {0}: HDR", MonitorShort(target["display:hdr:".Length..]));
        if (target.StartsWith("ddc:", StringComparison.Ordinal) && target.Length > 7)
        {
            var what = target[4..6] == "12" ? Loc.T("контраст") : Loc.T("яркость");
            return Loc.Tf("Монитор {0}: {1}", MonitorShort(target[7..]), what);
        }
        if (target.StartsWith("audio:level:", StringComparison.Ordinal)) return Loc.Tf("Уровень записи {0}", ShortEndpoint(target["audio:level:".Length..]));
        if (target.StartsWith("audio:mute:", StringComparison.Ordinal)) return Loc.Tf("Отключение звука {0}", ShortEndpoint(target["audio:mute:".Length..]));
        if (target.StartsWith("nicprop:", StringComparison.Ordinal))
        {
            var rest = target["nicprop:".Length..];
            var colon = rest.LastIndexOf(':');
            return colon > 0 ? Loc.Tf("Сетевой адаптер {0}: {1}", rest[..colon], rest[(colon + 1)..]) : rest;
        }
        return null;
    }

    private static string ShortEndpoint(string id)
    {
        var dot = id.IndexOf("}.{", StringComparison.Ordinal);
        return dot > 0 && id.Length > dot + 11 ? id[(dot + 2)..(dot + 11)] + "…}" : id;
    }

    /// <summary>«яркость 40 из 100».</summary>
    public static string DescribeVcp(MonitorVcp code, uint value, uint max) =>
        Loc.Tf("{0} {1} из {2}", code == MonitorVcp.Contrast ? Loc.T("контраст") : Loc.T("яркость"), value.ToString(CultureInfo.InvariantCulture), max.ToString(CultureInfo.InvariantCulture));

    /// <summary>«уровень 82 %».</summary>
    public static string DescribeLevel(float scalar) =>
        Loc.Tf("уровень {0} %", Math.Round(Math.Clamp(scalar, 0f, 1f) * 100).ToString("0", CultureInfo.InvariantCulture));

    public static string DescribeMute(bool mute) => mute ? Loc.T("без звука") : Loc.T("звук включён");

    public static string DescribeHdr(bool enabled) => enabled ? Loc.T("HDR включён") : Loc.T("HDR выключен");

    public static string DescribeNic(string? value) => value is null ? Loc.T("по умолчанию драйвера") : value;
}
