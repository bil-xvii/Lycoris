using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Monolith.Modules.Drivers;

internal enum BackupKind { Full, BeforeUpdate }

/// <summary>
/// Driver backup folders: %ProgramData%\MONOLITH\DriverBackups\yyyyMMdd-HHmm[-wu][-N].
/// "-wu" marks the automatic copy of the drivers that a Windows Update install replaces;
/// "-N" disambiguates two backups made in the same minute.
/// </summary>
internal static partial class BackupFolders
{
    public const string ManifestFile = "monolith-backup.json";
    public const string BeforeUpdateTag = "wu";
    private const string StampFormat = "yyyyMMdd-HHmm";

    [GeneratedRegex(@"^(?<ts>\d{8}-\d{4})(?:-(?<tag>[a-z]+))?(?:-(?<n>\d{1,3}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    /// <summary>New folder name; <paramref name="exists"/> lets the caller avoid collisions within the same minute.</summary>
    public static string NewName(DateTime now, BackupKind kind, Func<string, bool> exists)
    {
        var baseName = now.ToString(StampFormat, CultureInfo.InvariantCulture);
        if (kind == BackupKind.BeforeUpdate) baseName += "-" + BeforeUpdateTag;
        if (!exists(baseName)) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}-{i}";
            if (!exists(candidate)) return candidate;
        }
        throw new IOException(Loc.T("Слишком много резервных копий за одну минуту."));
    }

    /// <summary>Parses a backup folder name. Returns false for folders MONOLITH did not create.</summary>
    public static bool TryParse(string? name, out DateTime created, out BackupKind kind)
    {
        created = default;
        kind = BackupKind.Full;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var m = NameRegex().Match(name.Trim());
        if (!m.Success) return false;
        if (!DateTime.TryParseExact(m.Groups["ts"].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out created))
            return false;
        if (m.Groups["tag"].Success)
        {
            if (!string.Equals(m.Groups["tag"].Value, BeforeUpdateTag, StringComparison.OrdinalIgnoreCase)) return false;
            kind = BackupKind.BeforeUpdate;
        }
        return true;
    }

    /// <summary>
    /// Path guard: true only when <paramref name="candidate"/> is a direct sub-folder of <paramref name="root"/>
    /// (no "..", no other drives, not the root itself). Used before restoring from or deleting a backup.
    /// </summary>
    public static bool IsDirectChild(string root, string candidate)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate)) return false;
            var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            if (c.Length <= r.Length + 1) return false;
            var parent = Path.GetDirectoryName(c);
            return parent is not null && string.Equals(parent, r, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>The [Version] section of an INF: who made the driver, its class, date and version.</summary>
internal sealed record InfVersionInfo(string? Provider, string? Class, DateTime? Date, string? Version);

/// <summary>Minimal INF reader for the [Version] section (with %token% resolution from [Strings]).</summary>
internal static class InfParser
{
    public static InfVersionInfo Parse(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                var name = (end > 1 ? line[1..end] : line[1..]).Trim();
                if (!sections.TryGetValue(name, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[name] = current;
                }
                continue;
            }
            if (current is null) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            current.TryAdd(key, value);
        }

        sections.TryGetValue("Version", out var version);
        var strings = sections.TryGetValue("Strings", out var s) ? s
            : sections.FirstOrDefault(kv => kv.Key.StartsWith("Strings.", StringComparison.OrdinalIgnoreCase)).Value;

        string? Get(string key) => version is not null && version.TryGetValue(key, out var v) ? Resolve(v, strings) : null;

        DateTime? date = null;
        string? ver = null;
        if (version is not null && version.TryGetValue("DriverVer", out var dv))
        {
            var parts = dv.Split(',', 2);
            if (DateTime.TryParseExact(parts[0].Trim(), ["MM/dd/yyyy", "M/d/yyyy", "MM-dd-yyyy", "M-d-yyyy"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                date = d;
            if (parts.Length > 1 && parts[1].Trim().Length > 0) ver = parts[1].Trim();
        }

        return new InfVersionInfo(Get("Provider"), Get("Class"), date, ver);
    }

    private static string Resolve(string value, Dictionary<string, string>? strings)
    {
        var v = Unquote(value);
        if (v.Length > 2 && v[0] == '%' && v[^1] == '%' && strings is not null &&
            strings.TryGetValue(v[1..^1], out var resolved))
            return Unquote(resolved);
        return v;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }

    /// <summary>Removes a ";" comment that is not inside quotes.</summary>
    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == ';' && !inQuotes) return line[..i];
        }
        return line.TrimEnd('\r');
    }
}
