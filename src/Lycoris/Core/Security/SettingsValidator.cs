using System.IO;

namespace Lycoris.Core.Security;

/// <summary>
/// settings.json lives in the user's folder, so any program running as the user can rewrite it — and an elevated Lycoris
/// reads it. Whatever it says is clamped to what the app itself could have written: known values only, bounded lists,
/// plain text without control characters, absolute local folders without «..». Applied right after loading.
/// </summary>
public static class SettingsValidator
{
    public const int MaxListItems = 500;
    public const int MaxItemLength = 260;

    /// <summary>Fixes <paramref name="s"/> in place and returns how many values had to be corrected or dropped (0 = the file was clean).</summary>
    public static int Sanitize(AppSettings s)
    {
        var fixes = 0;

        var interval = Math.Clamp(s.MonitorIntervalMs, 250, 60_000);
        if (interval != s.MonitorIntervalMs) { s.MonitorIntervalMs = interval; fixes++; }

        fixes += OneOf(s.Language, ["auto", "ru", "en"], "auto", v => s.Language = v);
        fixes += OneOf(s.DevicesTab, ["auto", "manual"], "auto", v => s.DevicesTab = v);
        fixes += OneOf(s.GameBoostCloseMode, [AppSettings.CloseModeClose, AppSettings.CloseModeEcoQos], AppSettings.CloseModeClose, v => s.GameBoostCloseMode = v);

        if (!SafeIo.IsPlainText(s.LastProfile, 40) || s.LastProfile.Length == 0) { s.LastProfile = "Recommended"; fixes++; }
        if (!SafeIo.IsPlainText(s.LastWindowsBuild, 40)) { s.LastWindowsBuild = ""; fixes++; }

        var close = CleanList(s.GameBoostCloseList, name => SafeIo.IsPlainText(name, 100) && name.Trim().Length > 0, ref fixes);
        if (close is not null) s.GameBoostCloseList = close;

        var folders = CleanList(s.CustomGameFolders, IsLocalAbsoluteFolder, ref fixes);
        if (folders is not null) s.CustomGameFolders = folders;

        var drift = CleanList(s.DriftAccepted, item => SafeIo.IsPlainText(item, 700), ref fixes, maxItems: 4000);
        if (drift is not null) s.DriftAccepted = drift;

        return fixes;
    }

    /// <summary>A folder a user could have added: fully qualified, local drive (no \\server\share, no \\?\ device path), no «..», no control characters.</summary>
    public static bool IsLocalAbsoluteFolder(string? path)
    {
        if (path is null || path.Length is 0 or > MaxItemLength || !SafeIo.IsPlainText(path, MaxItemLength)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        if (!Path.IsPathFullyQualified(path)) return false;
        if (path.Split('\\', '/').Any(seg => seg == "..")) return false;
        try
        {
            var full = Path.GetFullPath(path);
            return full.Length > 3 && char.IsAsciiLetter(full[0]) && full[1] == ':'; // not a bare drive root
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static int OneOf(string? value, string[] allowed, string fallback, Action<string> set)
    {
        if (value is not null && allowed.Contains(value, StringComparer.Ordinal)) return 0;
        set(fallback);
        return 1;
    }

    /// <summary>Null when the list is fine as it is; otherwise the cleaned copy (invalid items dropped, duplicates removed, length capped).</summary>
    private static List<string>? CleanList(List<string>? list, Func<string, bool> valid, ref int fixes, int maxItems = MaxListItems)
    {
        if (list is null) { fixes++; return []; }
        var clean = new List<string>(Math.Min(list.Count, maxItems));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            if (clean.Count >= maxItems) break;
            if (item is null || !valid(item) || !seen.Add(item)) continue;
            clean.Add(item);
        }
        if (clean.Count == list.Count) return null;
        fixes += list.Count - clean.Count;
        return clean;
    }
}
