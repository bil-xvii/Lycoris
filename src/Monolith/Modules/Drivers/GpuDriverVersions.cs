using System.Globalization;

namespace Monolith.Modules.Drivers;

/// <summary>Converts Windows driver versions into the numbers GPU vendors publish.</summary>
internal static class GpuDriverVersions
{
    /// <summary>
    /// NVIDIA: Windows version "32.0.16.1088" → "610.88" (drop the dots, take the last 5 digits; research §3).
    /// Returns null when the input is not a 4-part NVIDIA-style version.
    /// </summary>
    public static string? NvidiaFromWindows(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion)) return null;
        var parts = windowsVersion.Trim().Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return null;
        var digits = string.Concat(parts);
        if (digits.Length < 5) return null;
        var last5 = digits[^5..];
        return $"{int.Parse(last5[..3], CultureInfo.InvariantCulture)}.{last5[3..]}";
    }

    /// <summary>
    /// Compares two NVIDIA release numbers ("621.01" vs "610.88"). Returns null when either is unparsable.
    /// </summary>
    public static int? CompareNvidia(string? a, string? b)
    {
        if (!TryParseRelease(a, out var x) || !TryParseRelease(b, out var y)) return null;
        return x.CompareTo(y);
    }

    /// <summary>
    /// Compares two AMD Software (Adrenalin) versions ("26.10.1" vs "25.9.1"; year.month.revision).
    /// Returns null when either is not such a version.
    /// </summary>
    public static int? CompareAmd(string? a, string? b)
    {
        if (!TryParseAmd(a, out var x) || !TryParseAmd(b, out var y)) return null;
        return x.CompareTo(y);
    }

    private static bool TryParseAmd(string? s, out Version value)
    {
        value = new Version();
        // year.month[.revision] only — a 4-part Windows driver version ("32.0.21001.9") is a different numbering.
        if (string.IsNullOrWhiteSpace(s) || !Version.TryParse(s.Trim(), out var v) || v.Revision >= 0) return false;
        if (v.Major is < 10 or > 99 || v.Minor is < 1 or > 12) return false;
        value = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        return true;
    }

    private static bool TryParseRelease(string? s, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return decimal.TryParse(s.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Display version for the GPU card: vendor number when known, otherwise the Windows version.</summary>
    public static string DisplayVersion(Core.GpuVendor vendor, string windowsVersion, string? radeonSoftwareVersion, string? nvidiaPackageVersion)
    {
        switch (vendor)
        {
            case Core.GpuVendor.Nvidia:
                if (!string.IsNullOrWhiteSpace(nvidiaPackageVersion)) return nvidiaPackageVersion.Trim();
                return NvidiaFromWindows(windowsVersion) ?? (string.IsNullOrWhiteSpace(windowsVersion) ? "—" : windowsVersion);
            case Core.GpuVendor.Amd when !string.IsNullOrWhiteSpace(radeonSoftwareVersion):
                return radeonSoftwareVersion.Trim();
            default:
                return string.IsNullOrWhiteSpace(windowsVersion) ? "—" : windowsVersion;
        }
    }
}
