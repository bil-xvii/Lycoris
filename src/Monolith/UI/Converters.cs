using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Monolith.Core;

namespace Monolith.UI;

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>null / empty string / empty collection / 0 → Collapsed, otherwise Visible. Parameter "invert" flips it.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            ICollection c => c.Count == 0,
            int i => i == 0,
            _ => false,
        };
        if (parameter is "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool → !bool.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Bytes (long/ulong/double) → "1.4 ГБ" / "1.4 GB" (units through <see cref="SystemProfile.FormatBytes"/>).</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? "—" : SystemProfile.FormatBytes(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Value equals parameter (string compare) → true. Useful for RadioButton ↔ enum.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
        {
            if (targetType.IsEnum) return Enum.Parse(targetType, parameter.ToString()!, true);
            return parameter;
        }
        return Binding.DoNothing;
    }
}

/// <summary>Value equals parameter → Visible.</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var options = parameter?.ToString()?.Split('|') ?? [];
        return options.Any(o => string.Equals(value?.ToString(), o, StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>String → UPPER CASE (WPF has no text-transform).</summary>
public sealed class UpperConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.ToUpper(CultureInfo.CurrentCulture) ?? "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Percent number → "42%" with no decimals.</summary>
/// <summary>
/// Headings written in capitals → sentence case: «ВРЕМЕННЫЕ ФАЙЛЫ WINDOWS» → «Временные файлы Windows».
/// Text that already has lower-case letters is left as is. Acronyms (ПК, GPU, DNS…) and brand spellings
/// (DirectX, NVIDIA, GeForce…) are kept; other Latin words become «Steam», «Edge».
/// </summary>
public sealed class SentenceCaseConverter : IValueConverter
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal)
    {
        "ПК", "ЦП", "ОС", "ИИ", "ОЗУ", "ГБ", "МБ", "КБ", "ТБ", "ГЦ", "МГЦ", "ГГЦ", "СМИ", "ЕС", // loc:ignore
        "GPU", "CPU", "RAM", "VRAM", "SSD", "HDD", "NVME", "SATA", "USB", "DNS", "IP", "TCP", "UDP", "VPN", "LAN", "WI-FI",
        "SFC", "DISM", "CBS", "TEMP", "ID", "PC", "OS", "AI", "UAC", "TPM", "VBS", "HVCI", "BIOS", "UEFI", "XMP", "EXPO",
        "FPS", "HDR", "VRR", "HAGS", "DLSS", "RTX", "GTX", "AMD", "NVIDIA", "DDU", "OEM", "MSI", "OBS", "MDM", "CCD", "X3D",
        "IFEO", "MMCSS", "SMB", "LLMNR", "QOS", "NTFS", "WSL", "DX", "DXGI", "BAR", "ACPI", "WU", "UWP", "MS", "EA", "GOG",
        "CPU-Z", "GPU-Z", "HWINFO", "MONOLITH", "VR", "4K", "HTML", "JSON", "WMI", "COM", "PDF", "LTSC", "KB",
    };

    private static readonly Dictionary<string, string> Brands = new(StringComparer.Ordinal)
    {
        ["DIRECTX"] = "DirectX", ["GEFORCE"] = "GeForce", ["ONEDRIVE"] = "OneDrive", ["WININET"] = "WinINet", ["WINGET"] = "winget",
        ["IPV6"] = "IPv6", ["IPV4"] = "IPv4", ["G-SYNC"] = "G-SYNC", ["FREESYNC"] = "FreeSync", ["BATTLE.NET"] = "Battle.net",
        ["NETBIOS"] = "NetBIOS", ["POWERSHELL"] = "PowerShell", ["SYSMAIN"] = "SysMain", ["DIAGTRACK"] = "DiagTrack",
        ["XBOX"] = "Xbox", ["PCIE"] = "PCIe", ["YOUTUBE"] = "YouTube", ["MACOS"] = "macOS", ["IOS"] = "iOS",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Apply(value as string);

    public static string Apply(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Any(char.IsLower)) return s ?? "";
        var sb = new System.Text.StringBuilder(s.Length);
        var first = true;
        var i = 0;
        while (i < s.Length)
        {
            if (!char.IsLetterOrDigit(s[i])) { sb.Append(s[i]); i++; continue; }
            var j = i;
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || ((s[j] is '-' or '.') && j + 1 < s.Length && char.IsLetterOrDigit(s[j + 1])))) j++;
            sb.Append(Word(s[i..j], first));
            first = false;
            i = j;
        }
        return sb.ToString();
    }

    // English proper nouns that keep a capital inside a sentence-case title (English mode only).
    private static readonly Dictionary<string, string> EnglishProper = new(StringComparer.Ordinal)
    {
        ["WINDOWS"] = "Windows", ["MICROSOFT"] = "Microsoft", ["STEAM"] = "Steam", ["DISCORD"] = "Discord", ["DEFENDER"] = "Defender",
        ["EDGE"] = "Edge", ["CHROME"] = "Chrome", ["FIREFOX"] = "Firefox", ["BLUETOOTH"] = "Bluetooth", ["GITHUB"] = "GitHub",
        ["EPIC"] = "Epic", ["INTEL"] = "Intel", ["STORE"] = "Store", ["OBS"] = "OBS", ["ONEDRIVE"] = "OneDrive", ["TEAMS"] = "Teams",
        ["SPOTIFY"] = "Spotify", ["REALTEK"] = "Realtek", ["RAZER"] = "Razer", ["LOGITECH"] = "Logitech", ["EXPLORER"] = "Explorer",
    };

    private static string Word(string w, bool first)
    {
        if (Acronyms.Contains(w)) return w;
        if (Brands.TryGetValue(w, out var brand)) return brand;
        if (w.Any(char.IsDigit)) return w;                      // X3D, 10/11, RTX40
        if (Loc.IsEnglish)
        {
            // English sentence case: only the first word (and proper nouns) get a capital.
            if (EnglishProper.TryGetValue(w, out var proper)) return proper;
            var lowerEn = w.ToLowerInvariant();
            return first ? char.ToUpperInvariant(lowerEn[0]) + lowerEn[1..] : lowerEn;
        }
        var latin = w.All(c => c < 128);
        var lower = w.ToLower(Ru);
        if (latin || first) return char.ToUpper(lower[0], Ru) + lower[1..];
        return lower;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? "—" : $"{System.Convert.ToDouble(value, CultureInfo.InvariantCulture):0}%";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
