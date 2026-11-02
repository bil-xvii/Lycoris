using System.Globalization;

namespace Monolith.Modules.Devices.Vendors;

/// <summary>
/// Wireless receivers report themselves («USB Receiver»), never the device paired behind them — Windows has no documented
/// way to ask (Logitech's HID++ would need output reports: DO-NOT-INCLUDE). These hints name the receiver from its PID and
/// offer, honestly worded, what was plugged in by cable before (devices_vendors.md §2.5). Pure.
/// </summary>
internal static class ReceiverHints
{
    private const string Logitech = "046D";

    // Solaar base_usb.py / Linux hid-ids.h (devices_vendors.md §3); C54D verified on the dev PC.
    private static readonly HashSet<string> LightspeedPids = new(StringComparer.OrdinalIgnoreCase)
        { "C539", "C53A", "C53D", "C53F", "C541", "C545", "C547", "C54D" };
    private static readonly HashSet<string> BoltPids = new(StringComparer.OrdinalIgnoreCase) { "C548" };
    private static readonly HashSet<string> UnifyingPids = new(StringComparer.OrdinalIgnoreCase) { "C52B", "C532" };

    /// <summary>Product strings that say nothing about the model (they fall through to the receiver table or vendor + kind).</summary>
    private static readonly HashSet<string> GenericProductNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "USB Receiver", "Wireless Receiver", "2.4G Receiver", "USB Audio Device", "USB Composite Device", "Gaming Mouse",
        "Gaming Keyboard", "Wireless Controller", "USB Device", "HID Device", "USB Input Device", "USB Keyboard", "USB Mouse",
    };

    /// <summary>«LIGHTSPEED-приёмник Logitech», «Приёмник Logi Bolt», «Приёмник Unifying»; null for anything else.</summary>
    public static string? Name(string? vid, string? pid)
    {
        if (!string.Equals(vid, Logitech, StringComparison.OrdinalIgnoreCase) || pid is null) return null;
        if (LightspeedPids.Contains(pid)) return Loc.T("LIGHTSPEED-приёмник Logitech");
        if (BoltPids.Contains(pid)) return Loc.T("Приёмник Logi Bolt");
        if (UnifyingPids.Contains(pid)) return Loc.T("Приёмник Unifying");
        return null;
    }

    public static bool IsReceiver(string? vid, string? pid) => Name(vid, pid) is not null;

    public static bool IsLightspeed(string? vid, string? pid) =>
        string.Equals(vid, Logitech, StringComparison.OrdinalIgnoreCase) && pid is not null && LightspeedPids.Contains(pid);

    /// <summary>
    /// A Logitech G mouse: a LIGHTSPEED receiver, or a wired HID++ 2.0 gaming mouse (PID C07D–C0FF: G502, G Pro, PRO X2…).
    /// Office receivers (Unifying, Bolt) and keyboards (C3xx) are not.
    /// </summary>
    public static bool IsLogitechGamingMouse(string? vid, string? pid)
    {
        if (!string.Equals(vid, Logitech, StringComparison.OrdinalIgnoreCase) || pid is null) return false;
        if (LightspeedPids.Contains(pid)) return true;
        return int.TryParse(pid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) && n is >= 0xC07D and <= 0xC0FF;
    }

    /// <summary>«раньше по кабелю подключалась: PRO X2 SUPERSTRIKE» — a hint only, never «this is the mouse behind the receiver».</summary>
    public static string HistoryText(string name) => Loc.Tf("раньше по кабелю подключалась: {0}", name.Trim());

    /// <summary>«USB Receiver», «Wireless Controller»… (exact strings, ignoring case and spaces around).</summary>
    public static bool IsGenericProductName(string? name) =>
        string.IsNullOrWhiteSpace(name) || GenericProductNames.Contains(name.Trim());
}
